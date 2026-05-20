using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using SelfOrderingSystemKiosk.Areas.Customer.Models;
using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;
using System.Security.Cryptography;
using Order = SelfOrderingSystemKiosk.Areas.Customer.Models.Order;

namespace SelfOrderingSystemKiosk.Areas.Customer.Controllers
{
    public partial class KioskController
    {
        public async Task<IActionResult> Index(bool startNewSession = false, string? branchId = null, string? branchCode = null)
        {
            if (!HasRememberedQrTableContext())
                SetKioskChannelDefaults();

            await ApplyKioskBranchContextAsync(branchId, branchCode);
            if (startNewSession)
                _skipRememberedPersonCountRestore = true;

            RestoreOrderingCookiesToSession();
            var table = HttpContext.Session.GetString(SessionServiceTable);
            if (startNewSession)
                await ClearRememberedPersonCountAsync(table, HttpContext.Session.GetString(SessionServiceBranch));

            if (!string.IsNullOrWhiteSpace(table))
                await ResetEndedTableSessionPersonCountAsync(table, HttpContext.Session.GetString(SessionServiceBranch));

            return View();
        }

        private async Task ApplyKioskBranchContextAsync(string? branchId, string? branchCode)
        {
            var branch = !string.IsNullOrWhiteSpace(branchId)
                ? await _branches.GetByIdAsync(branchId.Trim())
                : null;

            if (branch == null && !string.IsNullOrWhiteSpace(branchCode))
                branch = await _branches.GetByCodeAsync(branchCode.Trim());

            if (branch?.IsActive != true)
                return;

            HttpContext.Session.SetString(SessionServiceBranch, branch.Id);
            SaveOrderingCookies(null, null, GetSessionInt(SessionPersonCount), branch.Id);
        }

        /// <summary>Table QR entry point. Example: /Customer/Kiosk/Qr?token=secure-random-token</summary>
        [HttpGet]
        public async Task<IActionResult> Qr(string? token = null, string? branchId = null)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                TempData["ErrorMessage"] = "Invalid table link. Please scan the QR code on your table.";
                return RedirectToAction("Index");
            }

            var registeredTable = await _tableRegistry.GetByQrTokenAsync(token);
            if (registeredTable == null || string.IsNullOrWhiteSpace(registeredTable.TableNumber))
            {
                TempData["ErrorMessage"] = "Invalid or expired table QR code. Please ask staff for help.";
                return RedirectToAction("Index");
            }

            var table = registeredTable.TableNumber.Trim();
            if (table.Length > 32)
                table = table[..32];
            var floor = string.IsNullOrWhiteSpace(registeredTable.Floor) ? null : registeredTable.Floor.Trim();
            if (floor != null && floor.Length > 32)
                floor = floor[..32];

            var effectiveBranchId = !string.IsNullOrWhiteSpace(registeredTable.BranchId)
                ? registeredTable.BranchId.Trim()
                : await ResolveQrBranchIdAsync(table, branchId);
            if (string.IsNullOrWhiteSpace(effectiveBranchId))
            {
                TempData["ErrorMessage"] = "This table QR code is not assigned to a branch. Please ask staff to regenerate the QR code for the correct branch.";
                return RedirectToAction("Index");
            }

            SetQrTableContext(table, floor, effectiveBranchId);
            SaveOrderingCookies(table, floor, GetSessionInt(SessionPersonCount), effectiveBranchId);

            HttpContext.Session.SetString(SessionDiningType, "DineIn");
            await ResetEndedTableSessionPersonCountAsync(table, effectiveBranchId);
            await RestoreSharedTablePersonCountAsync(table, effectiveBranchId);
            SaveOrderingCookies(table, floor, GetSessionInt(SessionPersonCount), effectiveBranchId);
            TempData["DiningType"] = "DineIn";
            return RedirectToAction("ChooseExperience");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult SelectDining(string diningType)
        {
            RestoreOrderingCookiesToSession();
            var hasQrTableContext = HasRememberedQrTableContext();
            if (!string.Equals(diningType, "DineIn", StringComparison.OrdinalIgnoreCase) || !hasQrTableContext)
                SetKioskChannelDefaults();
            else
                HttpContext.Session.SetString(SessionOrderChannel, OrderChannelQr);

            TempData["DiningType"] = diningType;
            HttpContext.Session.SetString(SessionDiningType, diningType);

            if (diningType == "TakeOut")
            {
                // Skip experience selection and go straight to Ala Carte menu
                return RedirectToAction("AlaCarteMenu");
            }

            // Dine In goes to experience selection
            return RedirectToAction("ChooseExperience");
        }

        public async Task<IActionResult> ChooseExperience()
        {
            ViewBag.DiningType = TempData["DiningType"];
            await ApplyOrderingSessionToViewBagAsync();
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SelectExperience(string experienceType)
        {
            RestoreOrderingCookiesToSession();
            var channel = HttpContext.Session.GetString(SessionOrderChannel) ?? OrderChannelKiosk;
            var table = HttpContext.Session.GetString(SessionServiceTable);
            TempData["ExperienceType"] = experienceType;
            if (experienceType == "Unlimited") return RedirectToAction("UnlimitedMenu");
            if (experienceType == "AlaCarte") return RedirectToAction("AlaCarteMenu");
            return RedirectToAction("ChooseExperience");
        }

        public async Task<IActionResult> AlaCarteMenu(bool isReorder = false, string previousOrderNumber = null)
        {
            RestoreOrderingCookiesToSession();
            // Set experience type and keep it for the next request
            TempData["ExperienceType"] = "AlaCarte";
            TempData.Keep("ExperienceType");
            TempData.Keep("DiningType"); // Keep dining type if it exists
            ViewBag.ExperienceType = "AlaCarte";
            ViewBag.IsReorder = isReorder;
            // Only show available items from Stock collection
            var items = await GetAvailableMenuForCurrentContextAsync();
            items = items
                .Where(i => !string.Equals(i.Category, "Unlimited Inclusions", StringComparison.Ordinal))
                .ToList();
            ViewBag.MenuCategories = _menuCategories.KioskTabs
                .Where(c => !string.Equals(c.Key, "Wings", StringComparison.Ordinal))
                .ToList();
            ViewBag.DefaultMenuCategory = "Sulit Kap Meals";
            var channel = HttpContext.Session.GetString(SessionOrderChannel) ?? OrderChannelKiosk;
            ViewBag.OrderChannel = channel;
            ViewBag.IsQrFlow = channel == OrderChannelQr;
            if (channel == OrderChannelQr)
            {
                var table = HttpContext.Session.GetString(SessionServiceTable);
                var floor = HttpContext.Session.GetString(SessionServiceFloor);
                ViewBag.ServiceTable = table;
                ViewBag.ServiceFloor = floor;
                ViewBag.LocationLabel = BuildLocationLabel(floor, table);
            }
            return View(items);
        }

        public async Task<IActionResult> UnlimitedMenu(bool isReorder = false, string previousOrderNumber = null)
        {
            RestoreOrderingCookiesToSession();
            // Set experience type and keep it for the next request
            TempData["ExperienceType"] = "Unlimited";
            TempData.Keep("ExperienceType");
            TempData.Keep("DiningType"); // Keep dining type if it exists
            ViewBag.ExperienceType = "Unlimited";
            ViewBag.IsReorder = isReorder;
             
            // For reorders, calculate personCount from previous order
            int? personCount = null;
            if (isReorder && !string.IsNullOrEmpty(previousOrderNumber))
            {
                var previousOrder = await _orderService.GetByOrderNumberAsync(previousOrderNumber);
                if (previousOrder != null && previousOrder.OrderType == "Unlimited")
                {
                    if (previousOrder.BillArchived || IsOrderingSessionExpired(previousOrder))
                    {
                        isReorder = false;
                        ViewBag.IsReorder = false;
                        RestoreQrSessionFromOrder(previousOrder);
                        await ClearRememberedPersonCountAsync(previousOrder.TableNumber, previousOrder.BranchId);
                    }
                    else
                    {
                        personCount = GetOrderPersonCount(previousOrder);
                        ViewBag.PersonCount = personCount;
                        if (personCount.HasValue)
                            HttpContext.Session.SetInt32(SessionPersonCount, personCount.Value);
                    }
                }
            }
            
            // Unlimited orders show one included-selection board plus paid Ala Carte add-ons.
            var items = await GetAvailableMenuForCurrentContextAsync();
            items = items
                .Where(IsUnlimitedMenuItem)
                .Select(item =>
                {
                    if (IsUnlimitedIncludedItem(item))
                    {
                        item.Price = 0m;
                    }

                    return item;
                })
                .OrderByDescending(i => i.MenuOrder)
                .ThenBy(i => i.Item)
                .ToList();
            await ApplyOrderingSessionToViewBagAsync();
            return View(items);
        }

        private static bool IsUnlimitedMenuItem(MenuItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Item))
                return false;

            return !string.Equals(item.Category, "Wings Ala Carte", StringComparison.Ordinal)
                && !string.Equals(item.Category, "Unavailable", StringComparison.Ordinal);
        }

        private static bool IsUnlimitedIncludedItem(MenuItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Item))
                return false;

            var name = item.Item.Trim();
            if (string.Equals(item.Category, "Unlimited Inclusions", StringComparison.Ordinal))
                return true;

            if (string.Equals(item.Category, "Wings", StringComparison.Ordinal))
                return true;

            if (string.Equals(item.Category, "Drinks", StringComparison.Ordinal))
                return true;

            if (string.Equals(item.Category, "Add Ons", StringComparison.Ordinal))
            {
                return name.Equals("Plain Rice", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Garlic Rice", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Extra Gravy", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(item.Category, "Appetizer", StringComparison.Ordinal))
            {
                return name.Equals("Nachos", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Potato Thins", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("customer-order-write")]
        public async Task<IActionResult> ConfirmOrder([FromBody] List<OrderItem> Items, [FromQuery] string orderType, [FromQuery] int? personCount)
        {
            try
            {
                RestoreOrderingCookiesToSession();

                if (Items == null || !Items.Any())
                    return Json(new { success = false, message = "No items in the order" });

                // Get orderType from TempData if not in query string
                string experienceType = orderType ?? TempData["ExperienceType"]?.ToString() ?? "AlaCarte";

                var isUnlimitedOrder = string.Equals(experienceType, "Unlimited", StringComparison.OrdinalIgnoreCase);
                var validation = await ValidateSubmittedItemsAsync(Items, isUnlimitedOrder);
                if (!validation.Success)
                    return Json(new { success = false, message = validation.Message });

                Items = validation.Items;

                decimal subtotal;
                decimal tax = 0m;
                decimal total;
                var alaCarteAddOnSubtotal = Items.Sum(i => i.Price * i.Quantity);

                if (isUnlimitedOrder && (!personCount.HasValue || personCount.Value <= 0 || personCount.Value > 50))
                    return Json(new { success = false, message = "Please enter a valid person count." });

                if (isUnlimitedOrder)
                {
                    HttpContext.Session.SetInt32(SessionPersonCount, personCount.Value);
                    subtotal = alaCarteAddOnSubtotal;
                    total = subtotal;
                }
                else
                {
                    // For Ala Carte orders, calculate based on item prices
                    subtotal = alaCarteAddOnSubtotal;
                    total = subtotal;
                }

                string diningType = TempData["DiningType"]?.ToString()
                    ?? HttpContext.Session.GetString(SessionDiningType)
                    ?? "DineIn";

                var channel = HttpContext.Session.GetString(SessionOrderChannel) ?? OrderChannelKiosk;
                string tableNumber = null;
                string floor = null;
                if (string.Equals(channel, OrderChannelQr, StringComparison.OrdinalIgnoreCase))
                {
                    tableNumber = HttpContext.Session.GetString(SessionServiceTable);
                    floor = HttpContext.Session.GetString(SessionServiceFloor);
                }
                if (string.Equals(diningType, "TakeOut", StringComparison.OrdinalIgnoreCase))
                {
                    tableNumber = DefaultTakeOutTableNumber;
                    floor = null;
                }
                else if (!string.Equals(channel, OrderChannelQr, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(tableNumber))
                {
                    tableNumber = DefaultKioskTableNumber;
                    floor = null;
                }

                var isRealQrTableOrder = string.Equals(channel, OrderChannelQr, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(diningType, "DineIn", StringComparison.OrdinalIgnoreCase)
                    && !IsDefaultServiceTable(tableNumber);

                var branchId = await ResolveOrderBranchIdAsync(tableNumber);
                if (string.IsNullOrWhiteSpace(branchId))
                {
                    return Json(new
                    {
                        success = false,
                        message = "Branch is not selected for this order. Please open the kiosk using the correct branch link or scan a branch table QR code."
                    });
                }

                if (isRealQrTableOrder &&
                    isUnlimitedOrder &&
                    await HasEndedTableSessionReadyForNewSessionAsync(tableNumber, branchId) &&
                    !WasEndedTableSessionReset(tableNumber))
                {
                    await ClearRememberedPersonCountAsync(tableNumber, branchId);
                    return Json(new
                    {
                        success = false,
                        resetPersonCount = true,
                        message = "The previous table session has ended. Please enter the number of persons for this new session."
                    });
                }

                SaveOrderingCookies(
                    isRealQrTableOrder ? tableNumber : null,
                    isRealQrTableOrder ? floor : null,
                    personCount,
                    branchId);

                var tableGate = isRealQrTableOrder && isUnlimitedOrder
                    ? await CheckTableOrderingGateAsync(tableNumber, branchId)
                    : TableOrderingGateResult.Allowed();
                if (!tableGate.CanOrder)
                {
                    return Json(new
                    {
                        success = false,
                        message = tableGate.Message
                    });
                }

                if (isRealQrTableOrder && isUnlimitedOrder)
                    await SeedSharedTableSessionFromOrdersAsync(tableNumber, branchId);

                if (isUnlimitedOrder)
                {
                    const decimal pricePerHead = RestaurantPricing.UnlimitedPricePerHead;
                    var chargeablePersonCount = personCount!.Value;
                    if (isRealQrTableOrder)
                    {
                    var reservation = await _tableOrderingSessions.ReserveUnlimitedOrderAsync(
                        tableNumber,
                        personCount.Value,
                        validation.UnlimitedWingFlavors,
                        branchId);
                        if (!reservation.Success)
                            return Json(new { success = false, message = reservation.Message });

                        personCount = reservation.PersonCount;
                        chargeablePersonCount = reservation.ChargeablePersonCount;
                        HttpContext.Session.SetInt32(SessionPersonCount, personCount.Value);
                        SaveOrderingCookies(tableNumber, floor, personCount, branchId);
                    }

                    subtotal = (chargeablePersonCount * pricePerHead) + alaCarteAddOnSubtotal;
                    total = subtotal;
                }

                var orderNumber = await _orderService.CreateUniqueOrderNumberAsync(tableNumber, branchId);

                if (isUnlimitedOrder)
                {
                    // Check the 2-hour time limit after staff starts the first Unlimited order.
                    const int orderingSessionHours = 2;
                    DateTime? firstOrderTime = GetFirstOrderTimeUtc();

                    if (firstOrderTime.HasValue)
                    {
                        var timeSinceFirstOrder = DateTime.UtcNow - firstOrderTime.Value;
                        var sessionLimit = TimeSpan.FromHours(orderingSessionHours);

                        if (timeSinceFirstOrder > sessionLimit)
                        {
                            return Json(new
                            {
                                success = false,
                                message = $"Time limit exceeded. Your {orderingSessionHours}-hour ordering window started at {AppClock.ToLocal(firstOrderTime.Value):hh:mm tt} and has expired. Please start a new session."
                            });
                        }
                    }
                }

                var order = new Order
                {
                    OrderNumber = orderNumber,
                    PublicAccessToken = CreatePublicAccessToken(),
                    OrderDate = DateTime.UtcNow,
                    Status = "Pending",
                    OrderType = experienceType,
                    DiningType = diningType,
                    OrderChannel = channel,
                    TableNumber = tableNumber,
                    Floor = floor,
                    PaymentMethod = "Cash",
                    PaymentStatus = "Pending",
                    PersonCount = isUnlimitedOrder ? personCount : null,
                    Subtotal = subtotal,
                    Tax = tax,
                    Total = total,
                    Items = Items,
                    BranchId = branchId
                };

                await _orderService.CreateAsync(order);
                if (isRealQrTableOrder && isUnlimitedOrder)
                    await _orderService.UpdateOpenUnlimitedPersonCountForTableAsync(tableNumber, personCount!.Value, branchId);

                HttpContext.Session.Remove(SessionEndedTableReset);
                RememberOrderAccess(order);
                await _realtime.NotifyOrderChangedAsync(order, "order-created");

                return Json(new { success = true, orderNumber = order.OrderNumber, accessToken = order.PublicAccessToken });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating order");
                return Json(new { success = false, message = "Error creating order. Please try again." });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("customer-order-write")]
        public async Task<IActionResult> SaveOrderingSession([FromQuery] int personCount)
        {
            if (personCount <= 0 || personCount > 50)
                return Json(new { success = false, message = "Person count must be greater than zero." });

            RestoreOrderingCookiesToSession();
            var table = HttpContext.Session.GetString(SessionServiceTable);
            var branchId = HttpContext.Session.GetString(SessionServiceBranch);
            if (!string.IsNullOrWhiteSpace(table) &&
                await HasEndedTableSessionReadyForNewSessionAsync(table, branchId) &&
                !WasEndedTableSessionReset(table))
            {
                await ClearRememberedPersonCountAsync(table, branchId);
                return Json(new
                {
                    success = false,
                    resetPersonCount = true,
                    message = "The previous table session has ended. Please enter the number of persons for this new session."
                });
            }

            if (!string.IsNullOrWhiteSpace(table))
            {
                var existingSession = await _tableOrderingSessions.GetAsync(table, branchId);
                var openUnlimitedOrders = (await _orderService.GetOrdersByTableAsync(table, branchId))
                    .Where(o => !o.BillArchived && IsUnlimitedOrder(o))
                    .ToList();
                var hasOpenUnlimitedOrders = openUnlimitedOrders.Any();
                var billedPersonCount = existingSession?.BilledPersonCount > 0
                    ? existingSession.BilledPersonCount
                    : openUnlimitedOrders
                        .Select(GetChargedUnlimitedPersonCount)
                        .DefaultIfEmpty(0)
                        .Max();
                if (hasOpenUnlimitedOrders && personCount > billedPersonCount)
                {
                    HttpContext.Session.SetInt32(SessionPersonCount, personCount);
                    return Json(new
                    {
                        success = true,
                        personCount,
                        requiresOrderToCharge = true,
                        message = "Additional guests will be added to the bill when the next Unlimited order is submitted."
                    });
                }

                var tableSession = await _tableOrderingSessions.SavePersonCountAsync(table, personCount, branchId);
                if (tableSession?.PersonCount > 0)
                    personCount = tableSession.PersonCount;

                await _orderService.UpdateOpenUnlimitedPersonCountForTableAsync(table, personCount, branchId);
            }

            HttpContext.Session.SetInt32(SessionPersonCount, personCount);

            SaveOrderingCookies(
                table,
                HttpContext.Session.GetString(SessionServiceFloor),
                personCount,
                HttpContext.Session.GetString(SessionServiceBranch));

            return Json(new { success = true, personCount });
        }

        private static int GetChargedUnlimitedPersonCount(Order order)
        {
            if (order == null || !IsUnlimitedOrder(order))
                return 0;

            const decimal pricePerHead = RestaurantPricing.UnlimitedPricePerHead;
            var addOnSubtotal = (order.Items ?? new List<OrderItem>())
                .Where(i => i.Price > 0)
                .Sum(i => i.Price * i.Quantity);
            var baseSubtotal = Math.Max(0m, order.Subtotal - addOnSubtotal);
            return baseSubtotal >= pricePerHead
                ? Math.Max(1, (int)Math.Floor(baseSubtotal / pricePerHead))
                : 0;
        }

        [HttpGet]
        public async Task<IActionResult> Confirmation(string orderNumber, string accessToken = null)
        {
            if (string.IsNullOrEmpty(orderNumber))
                return RedirectToAction("Index");

            var order = await _orderService.GetByOrderNumberAsync(orderNumber, accessToken: accessToken);
            if (order == null)
                return RedirectToAction("Index");
            var hasPrivateAccess = HasPrivateOrderAccess(order, accessToken);
            ViewBag.HasPrivateOrderAccess = hasPrivateAccess;
            ViewBag.PublicAccessToken = hasPrivateAccess ? order.PublicAccessToken : string.Empty;

            if (hasPrivateAccess)
                RememberOrderAccess(order);
            RestoreQrSessionFromOrder(order);
            await ApplyConfirmationSessionAsync(order);

            // Preserve order type and dining type for reordering
            if (!string.IsNullOrEmpty(order.OrderType))
            {
                TempData["ExperienceType"] = order.OrderType;
            }
            if (!string.IsNullOrEmpty(order.DiningType))
            {
                TempData["DiningType"] = order.DiningType;
            }

            if (IsUnlimitedOrder(order))
            {
                ApplyOrderingWindowToViewBag();
                if (hasPrivateAccess && !order.BillArchived)
                    ViewBag.ConfirmationQuickItems = await BuildConfirmationQuickItemsAsync(order);
            }
            else
            {
                ViewBag.OrderingSessionHours = (int)OrderingSessionLength.TotalHours;
                ViewBag.HasOrderingSession = false;
                ViewBag.OrderingSessionRemaining = TimeSpan.Zero;
                ViewBag.OrderingSessionExpired = false;
            }

            return View(order);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("customer-order-write")]
        public async Task<IActionResult> CreateUnlimitedRefill(
            [FromQuery] string orderNumber,
            [FromQuery] string accessToken,
            [FromBody] List<OrderItem> items)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(orderNumber))
                    return Json(new { success = false, message = "Order number is required." });
                if (items == null || !items.Any())
                    return Json(new { success = false, message = "Choose at least one refill item." });

                var anchorOrder = await _orderService.GetByOrderNumberAsync(orderNumber, accessToken: accessToken);
                if (anchorOrder == null || !HasPrivateOrderAccess(anchorOrder, accessToken))
                    return Json(new { success = false, message = "Unable to access this order." });
                if (!IsUnlimitedOrder(anchorOrder))
                    return Json(new { success = false, message = "Refills are only available for Unlimited orders." });
                if (anchorOrder.BillArchived || IsOrderingSessionExpired(anchorOrder))
                    return Json(new { success = false, sessionEnded = true, message = "This unlimited ordering session has ended." });

                var branchId = anchorOrder.BranchId?.Trim();
                if (string.IsNullOrWhiteSpace(branchId))
                    return Json(new { success = false, message = "Branch is not selected for this order." });

                var validation = await ValidateSubmittedItemsAsync(items, true);
                if (!validation.Success)
                    return Json(new { success = false, message = validation.Message });

                var refillItems = new List<OrderItem>();
                foreach (var item in validation.Items)
                {
                    var menuItem = await _menuItems.GetByNameAsync(NormalizeSubmittedItemName(item.ItemName), branchId);
                    if (menuItem == null || !IsUnlimitedIncludedItem(menuItem))
                        return Json(new { success = false, message = "Only Unlimited-included items can be sent as refills." });

                    refillItems.Add(new OrderItem
                    {
                        ItemName = item.ItemName,
                        Quantity = item.Quantity,
                        Price = 0m
                    });
                }

                var tableNumber = string.IsNullOrWhiteSpace(anchorOrder.TableNumber)
                    ? DefaultKioskTableNumber
                    : anchorOrder.TableNumber.Trim();
                var isRealQrTableOrder = !IsDefaultServiceTable(tableNumber)
                    && string.Equals(anchorOrder.DiningType, "DineIn", StringComparison.OrdinalIgnoreCase);
                var tableGate = isRealQrTableOrder
                    ? await CheckTableOrderingGateAsync(tableNumber, branchId)
                    : TableOrderingGateResult.Allowed();
                if (!tableGate.CanOrder)
                    return Json(new { success = false, message = tableGate.Message });

                await _unlimitedRefills.CreateAsync(new UnlimitedRefill
                {
                    AnchorOrderId = anchorOrder.Id,
                    AnchorOrderNumber = anchorOrder.OrderNumber,
                    TableNumber = tableNumber,
                    Floor = anchorOrder.Floor ?? string.Empty,
                    BranchId = branchId,
                    Items = refillItems
                });
                await _realtime.NotifyKitchenChangedAsync(branchId, "refill-created");

                return Json(new { success = true, message = "Refill sent to the kitchen." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating unlimited refill");
                return Json(new { success = false, message = "Unable to send refill. Please try again." });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("customer-order-write")]
        public async Task<IActionResult> QuickUnlimitedOrder(
            [FromQuery] string orderNumber,
            [FromQuery] string accessToken,
            [FromBody] List<OrderItem> items)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(orderNumber))
                    return Json(new { success = false, message = "Order number is required." });
                if (items == null || !items.Any())
                    return Json(new { success = false, message = "Choose at least one item to reorder." });

                var anchorOrder = await _orderService.GetByOrderNumberAsync(orderNumber, accessToken: accessToken);
                if (anchorOrder == null || !HasPrivateOrderAccess(anchorOrder, accessToken))
                    return Json(new { success = false, message = "Unable to access this order." });
                if (!IsUnlimitedOrder(anchorOrder))
                    return Json(new { success = false, message = "Quick reorder is only available for Unlimited orders." });
                if (anchorOrder.BillArchived || IsOrderingSessionExpired(anchorOrder))
                    return Json(new { success = false, sessionEnded = true, message = "This unlimited ordering session has ended." });
                RestoreQrSessionFromOrder(anchorOrder);

                var branchId = anchorOrder.BranchId?.Trim();
                if (string.IsNullOrWhiteSpace(branchId))
                {
                    return Json(new
                    {
                        success = false,
                        message = "Branch is not selected for this order. Please open the kiosk using the correct branch link or scan a branch table QR code."
                    });
                }

                var validation = await ValidateSubmittedItemsAsync(items, true);
                if (!validation.Success)
                    return Json(new { success = false, message = validation.Message });

                var tableNumber = anchorOrder.TableNumber;
                var floor = anchorOrder.Floor;
                var isRealQrTableOrder = !string.IsNullOrWhiteSpace(tableNumber)
                    && !IsDefaultServiceTable(tableNumber)
                    && string.Equals(anchorOrder.DiningType, "DineIn", StringComparison.OrdinalIgnoreCase);

                var personCount = isRealQrTableOrder
                    ? await GetSharedTablePersonCountAsync(tableNumber, branchId)
                    : GetOrderPersonCount(anchorOrder) ?? 0;
                if (personCount <= 0)
                    return Json(new { success = false, message = "Unable to find the person count for this unlimited session." });

                var tableGate = isRealQrTableOrder
                    ? await CheckTableOrderingGateAsync(tableNumber, branchId)
                    : TableOrderingGateResult.Allowed();
                if (!tableGate.CanOrder)
                    return Json(new { success = false, message = tableGate.Message });

                if (isRealQrTableOrder)
                    await SeedSharedTableSessionFromOrdersAsync(tableNumber, branchId);

                var chargeablePersonCount = 0;
                if (isRealQrTableOrder)
                {
                    var reservation = await _tableOrderingSessions.ReserveUnlimitedOrderAsync(
                        tableNumber,
                        personCount,
                        validation.UnlimitedWingFlavors,
                        branchId);
                    if (!reservation.Success)
                        return Json(new { success = false, message = reservation.Message });

                    personCount = reservation.PersonCount;
                    chargeablePersonCount = reservation.ChargeablePersonCount;
                }

                const decimal pricePerHead = RestaurantPricing.UnlimitedPricePerHead;
                var alaCarteAddOnSubtotal = validation.Items.Sum(i => i.Price * i.Quantity);
                var subtotal = (chargeablePersonCount * pricePerHead) + alaCarteAddOnSubtotal;

                var order = new Order
                {
                    OrderNumber = await _orderService.CreateUniqueOrderNumberAsync(tableNumber, branchId),
                    PublicAccessToken = CreatePublicAccessToken(),
                    OrderDate = DateTime.UtcNow,
                    Status = "Pending",
                    OrderType = "Unlimited",
                    DiningType = anchorOrder.DiningType,
                    OrderChannel = anchorOrder.OrderChannel,
                    TableNumber = string.IsNullOrWhiteSpace(tableNumber) ? DefaultKioskTableNumber : tableNumber,
                    Floor = floor,
                    PaymentMethod = "Cash",
                    PaymentStatus = "Pending",
                    PersonCount = personCount,
                    Subtotal = subtotal,
                    Tax = 0m,
                    Total = subtotal,
                    Items = validation.Items,
                    BranchId = branchId
                };

                await _orderService.CreateAsync(order);
                if (isRealQrTableOrder)
                    await _orderService.UpdateOpenUnlimitedPersonCountForTableAsync(tableNumber, personCount, branchId);

                RememberOrderAccess(order);
                await _realtime.NotifyOrderChangedAsync(order, "order-created");
                SaveOrderingCookies(
                    isRealQrTableOrder ? tableNumber : null,
                    isRealQrTableOrder ? floor : null,
                    personCount,
                    branchId);

                return Json(new
                {
                    success = true,
                    orderNumber = order.OrderNumber,
                    accessToken = order.PublicAccessToken,
                    message = "Quick order sent to the kitchen."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating quick unlimited order");
                return Json(new { success = false, message = "Unable to send quick order. Please try again." });
            }
        }
    }
}
