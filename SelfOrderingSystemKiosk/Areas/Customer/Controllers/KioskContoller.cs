using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SelfOrderingSystemKiosk.Areas.Customer.Models;
using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;
using Order = SelfOrderingSystemKiosk.Areas.Customer.Models.Order;

namespace SelfOrderingSystemKiosk.Areas.Customer.Controllers
{
    [Area("Customer")]
    public class KioskController : Controller
    {
        private readonly OrderService _orderService;
        private readonly MenuItemService _menuItems;
        private readonly MenuCategoryRegistry _menuCategories;
        private readonly ILogger<KioskController> _logger;

        private const string SessionOrderChannel = "OrderChannel";
        private const string SessionServiceTable = "ServiceTableNumber";
        private const string SessionServiceFloor = "ServiceFloor";
        private const string SessionDiningType = "DiningType";
        private const string SessionPersonCount = "PersonCount";
        private const string CookieServiceTable = "KdsOrderTable";
        private const string CookieServiceFloor = "KdsOrderFloor";
        private const string CookiePersonCount = "KdsOrderPersonCount";
        private const string OrderChannelKiosk = "Kiosk";
        private const string OrderChannelQr = "Qr";
        private const string SessionFirstOrderTime = "FirstOrderTime";
        private static readonly TimeSpan OrderingSessionLength = TimeSpan.FromHours(2);

        public KioskController(OrderService orderService, MenuItemService menuItems, MenuCategoryRegistry menuCategories, ILogger<KioskController> logger)
        {
            _orderService = orderService;
            _menuItems = menuItems;
            _menuCategories = menuCategories;
            _logger = logger;
        }

        private void SetKioskChannelDefaults()
        {
            HttpContext.Session.SetString(SessionOrderChannel, OrderChannelKiosk);
            HttpContext.Session.Remove(SessionServiceTable);
            HttpContext.Session.Remove(SessionServiceFloor);
        }

        private void ApplyOrderingSessionToViewBag()
        {
            RestoreOrderingCookiesToSession();
            var channel = HttpContext.Session.GetString(SessionOrderChannel) ?? OrderChannelKiosk;
            ViewBag.OrderChannel = channel;
            ViewBag.IsQrFlow = channel == OrderChannelQr;
            ViewBag.PersonCount = GetSessionInt(SessionPersonCount);
            ApplyOrderingWindowToViewBag();
            if (channel == OrderChannelQr)
            {
                var table = HttpContext.Session.GetString(SessionServiceTable);
                var floor = HttpContext.Session.GetString(SessionServiceFloor);
                ViewBag.ServiceTable = table;
                ViewBag.ServiceFloor = floor;
                ViewBag.LocationLabel = BuildLocationLabel(floor, table);
            }
        }

        private static string BuildLocationLabel(string floor, string table)
        {
            if (string.IsNullOrEmpty(table)) return null;
            if (!string.IsNullOrEmpty(floor))
                return $"Floor {floor} · Table {table}";
            return $"Table {table}";
        }

        private void SaveOrderingCookies(string table, string floor, int? personCount)
        {
            var options = new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.Add(OrderingSessionLength)
            };

            if (!string.IsNullOrWhiteSpace(table))
                Response.Cookies.Append(CookieServiceTable, table, options);
            if (!string.IsNullOrWhiteSpace(floor))
                Response.Cookies.Append(CookieServiceFloor, floor, options);
            if (personCount.HasValue && personCount.Value > 0)
                Response.Cookies.Append(CookiePersonCount, personCount.Value.ToString(), options);
        }

        private void RestoreOrderingCookiesToSession()
        {
            if (string.IsNullOrEmpty(HttpContext.Session.GetString(SessionServiceTable)) &&
                Request.Cookies.TryGetValue(CookieServiceTable, out var table) &&
                !string.IsNullOrWhiteSpace(table))
            {
                HttpContext.Session.SetString(SessionOrderChannel, OrderChannelQr);
                HttpContext.Session.SetString(SessionServiceTable, table);
            }

            if (string.IsNullOrEmpty(HttpContext.Session.GetString(SessionServiceFloor)) &&
                Request.Cookies.TryGetValue(CookieServiceFloor, out var floor) &&
                !string.IsNullOrWhiteSpace(floor))
            {
                HttpContext.Session.SetString(SessionServiceFloor, floor);
            }

            if (!GetSessionInt(SessionPersonCount).HasValue &&
                Request.Cookies.TryGetValue(CookiePersonCount, out var rawCount) &&
                int.TryParse(rawCount, out var personCount) &&
                personCount > 0)
            {
                HttpContext.Session.SetInt32(SessionPersonCount, personCount);
            }
        }

        private int? GetSessionInt(string key)
        {
            return HttpContext.Session.GetInt32(key);
        }

        private DateTime? GetFirstOrderTimeUtc()
        {
            var firstOrderTimeStr = HttpContext.Session.GetString(SessionFirstOrderTime);
            if (string.IsNullOrEmpty(firstOrderTimeStr))
                return null;

            try
            {
                var firstOrderTime = DateTime.Parse(firstOrderTimeStr, null, System.Globalization.DateTimeStyles.RoundtripKind);
                if (firstOrderTime.Kind == DateTimeKind.Unspecified)
                    return DateTime.SpecifyKind(firstOrderTime, DateTimeKind.Utc);

                return firstOrderTime.ToUniversalTime();
            }
            catch
            {
                HttpContext.Session.Remove(SessionFirstOrderTime);
                return null;
            }
        }

        private void ApplyOrderingWindowToViewBag()
        {
            var firstOrderTime = GetFirstOrderTimeUtc();
            ViewBag.OrderingSessionHours = (int)OrderingSessionLength.TotalHours;
            ViewBag.HasOrderingSession = firstOrderTime.HasValue;

            if (!firstOrderTime.HasValue)
                return;

            var remaining = OrderingSessionLength - (DateTime.UtcNow - firstOrderTime.Value);
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;

            ViewBag.FirstOrderTime = firstOrderTime.Value;
            ViewBag.OrderingSessionEndsAt = firstOrderTime.Value.Add(OrderingSessionLength);
            ViewBag.OrderingSessionRemaining = remaining;
            ViewBag.OrderingSessionExpired = remaining == TimeSpan.Zero;
        }

        private async Task<TableOrderingGateResult> CheckTableOrderingGateAsync(string tableNumber)
        {
            if (string.IsNullOrWhiteSpace(tableNumber))
                return TableOrderingGateResult.Allowed();

            var tableOrders = await _orderService.GetOrdersByTableAsync(tableNumber);
            var latestSession = GetLatestTableSession(tableOrders);
            if (!latestSession.Any())
                return TableOrderingGateResult.Allowed();

            var sessionStart = latestSession.Min(o => o.OrderDate);
            var sessionEnd = sessionStart.Add(OrderingSessionLength);
            var isExpired = DateTime.UtcNow >= sessionEnd;
            var isPaid = latestSession.All(o => string.Equals(o.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase));

            if (!isExpired)
            {
                HttpContext.Session.SetString(SessionFirstOrderTime, sessionStart.ToString("O"));
                return TableOrderingGateResult.Allowed(sessionStart, sessionEnd, isPaid, false);
            }

            if (!isPaid)
            {
                return TableOrderingGateResult.Blocked(
                    $"The previous Table {tableNumber} session ended at {sessionEnd.ToLocalTime():h:mm tt} and the bill is still pending. Please ask staff to mark the bill paid before starting a new order.",
                    sessionStart,
                    sessionEnd);
            }

            HttpContext.Session.Remove(SessionFirstOrderTime);
            return TableOrderingGateResult.Allowed(sessionStart, sessionEnd, true, true);
        }

        private async Task ApplyConfirmationSessionAsync(Order order)
        {
            ViewBag.ConfirmationBillPaid = false;

            if (order == null)
                return;

            if (string.IsNullOrWhiteSpace(order.TableNumber) ||
                !string.Equals(order.DiningType, "DineIn", StringComparison.OrdinalIgnoreCase))
            {
                HttpContext.Session.SetString(SessionFirstOrderTime, order.OrderDate.ToUniversalTime().ToString("O"));
                ViewBag.ConfirmationBillPaid = string.Equals(order.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase);
                return;
            }

            var tableOrders = await _orderService.GetOrdersByTableAsync(order.TableNumber);
            var sessionOrders = GetOrdersInSameSession(tableOrders, order);
            if (!sessionOrders.Any())
                sessionOrders = new List<Order> { order };

            var sessionStart = sessionOrders.Min(o => o.OrderDate);
            HttpContext.Session.SetString(SessionFirstOrderTime, sessionStart.ToUniversalTime().ToString("O"));
            ViewBag.ConfirmationBillPaid = sessionOrders.All(o => string.Equals(o.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase));
        }

        private static List<Order> GetLatestTableSession(List<Order> tableOrders)
        {
            var ordered = tableOrders
                .Where(o => !string.Equals(o.Status, "Canceled", StringComparison.OrdinalIgnoreCase))
                .OrderBy(o => o.OrderDate)
                .ToList();

            if (!ordered.Any())
                return new List<Order>();

            var sessionStart = ordered.First().OrderDate;
            var latestSession = new List<Order>();
            foreach (var order in ordered)
            {
                if (order.OrderDate >= sessionStart.Add(OrderingSessionLength))
                {
                    sessionStart = order.OrderDate;
                    latestSession.Clear();
                }

                latestSession.Add(order);
            }

            return latestSession;
        }

        private static List<Order> GetOrdersInSameSession(List<Order> tableOrders, Order anchorOrder)
        {
            var ordered = tableOrders
                .Where(o => !string.Equals(o.Status, "Canceled", StringComparison.OrdinalIgnoreCase))
                .OrderBy(o => o.OrderDate)
                .ToList();

            if (!ordered.Any())
                return new List<Order> { anchorOrder };

            var sessionStart = ordered.First().OrderDate;
            foreach (var order in ordered)
            {
                if (order.OrderDate >= sessionStart.Add(OrderingSessionLength))
                    sessionStart = order.OrderDate;

                if (order.Id == anchorOrder.Id)
                    break;
            }

            var sessionEnd = sessionStart.Add(OrderingSessionLength);
            return ordered
                .Where(o => o.OrderDate >= sessionStart && o.OrderDate < sessionEnd)
                .ToList();
        }

        private void RestoreQrSessionFromOrder(Order order)
        {
            if (order == null) return;
            if (string.Equals(order.OrderChannel, OrderChannelQr, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(order.TableNumber))
            {
                HttpContext.Session.SetString(SessionOrderChannel, OrderChannelQr);
                HttpContext.Session.SetString(SessionServiceTable, order.TableNumber);
                if (!string.IsNullOrEmpty(order.Floor))
                    HttpContext.Session.SetString(SessionServiceFloor, order.Floor);
                else
                    HttpContext.Session.Remove(SessionServiceFloor);
                HttpContext.Session.SetString(SessionDiningType, order.DiningType ?? "DineIn");
            }
        }

        public IActionResult Index()
        {
            SetKioskChannelDefaults();
            RestoreOrderingCookiesToSession();
            return View();
        }

        /// <summary>Table QR entry point. Example: /Customer/Kiosk/Qr?table=12&amp;floor=2</summary>
        [HttpGet]
        public IActionResult Qr(string table, string floor = null)
        {
            if (string.IsNullOrWhiteSpace(table))
            {
                TempData["ErrorMessage"] = "Invalid table link. Please scan the QR code on your table.";
                return RedirectToAction("Index");
            }

            table = table.Trim();
            if (table.Length > 32)
                table = table[..32];
            floor = string.IsNullOrWhiteSpace(floor) ? null : floor.Trim();
            if (floor != null && floor.Length > 32)
                floor = floor[..32];

            HttpContext.Session.SetString(SessionOrderChannel, OrderChannelQr);
            HttpContext.Session.SetString(SessionServiceTable, table);
            if (floor != null)
                HttpContext.Session.SetString(SessionServiceFloor, floor);
            else
                HttpContext.Session.Remove(SessionServiceFloor);

            HttpContext.Session.SetString(SessionDiningType, "DineIn");
            SaveOrderingCookies(table, floor, GetSessionInt(SessionPersonCount));
            TempData["DiningType"] = "DineIn";
            return RedirectToAction("ChooseExperience");
        }

        [HttpPost]
        public IActionResult SelectDining(string diningType)
        {
            RestoreOrderingCookiesToSession();
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

        public IActionResult ChooseExperience()
        {
            ViewBag.DiningType = TempData["DiningType"];
            ApplyOrderingSessionToViewBag();
            return View();
        }

        [HttpPost]
        public IActionResult SelectExperience(string experienceType)
        {
            RestoreOrderingCookiesToSession();
            TempData["ExperienceType"] = experienceType;
            if (experienceType == "Unlimited") return RedirectToAction("UnlimitedMenu");
            if (experienceType == "AlaCarte") return RedirectToAction("AlaCarteMenu");
            return RedirectToAction("ChooseExperience");
        }

        public async Task<IActionResult> AlaCarteMenu(bool isReorder = false, string previousOrderNumber = null)
        {
            // Set experience type and keep it for the next request
            TempData["ExperienceType"] = "AlaCarte";
            TempData.Keep("ExperienceType");
            TempData.Keep("DiningType"); // Keep dining type if it exists
            ViewBag.ExperienceType = "AlaCarte";
            ViewBag.IsReorder = isReorder;
            // Only show available items from Stock collection
            var items = await _menuItems.GetAvailableAsync() ?? new List<MenuItem>();
            items = items
                .Where(i => !string.Equals(i.Category, "Unlimited Inclusions", StringComparison.Ordinal))
                .ToList();
            ViewBag.MenuCategories = _menuCategories.KioskTabs;
            ApplyOrderingSessionToViewBag();
            return View(items);
        }

        public async Task<IActionResult> UnlimitedMenu(bool isReorder = false, string previousOrderNumber = null)
        {
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
                    const decimal pricePerHead = 477m;
                    const decimal taxRate = 1.12m;
                    personCount = (int)Math.Round(previousOrder.Total / (pricePerHead * taxRate));
                    ViewBag.PersonCount = personCount;
                    HttpContext.Session.SetInt32(SessionPersonCount, personCount.Value);
                }
            }
            
            // Unlimited orders include the items listed on the unlimited dine-in board.
            var items = await _menuItems.GetAvailableAsync() ?? new List<MenuItem>();
            items = items
                .Where(IsUnlimitedIncludedItem)
                .OrderByDescending(i => i.MenuOrder)
                .ThenBy(i => i.Item)
                .ToList();
            var unlimitedTabKeys = new HashSet<string>(items.Select(i => i.Category), StringComparer.Ordinal);
            ViewBag.MenuCategories = _menuCategories.All
                .Where(c => unlimitedTabKeys.Contains(c.Key))
                .OrderBy(c => c.SortOrder)
                .ToList();
            ApplyOrderingSessionToViewBag();
            return View(items);
        }

        private static bool IsUnlimitedIncludedItem(MenuItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Item))
                return false;

            var name = item.Item.Trim();
            if (string.Equals(item.Category, "Wings", StringComparison.Ordinal))
                return true;

            if (string.Equals(item.Category, "Unlimited Inclusions", StringComparison.Ordinal))
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
        [IgnoreAntiforgeryToken] // Allow API calls without CSRF token
        public async Task<IActionResult> ConfirmOrder([FromBody] List<OrderItem> Items, [FromQuery] string orderType, [FromQuery] int? personCount)
        {
            try
            {
                // Log incoming request for debugging
                Console.WriteLine($"ConfirmOrder called - orderType: {orderType}, personCount: {personCount}");
                Console.WriteLine($"Items count: {Items?.Count ?? 0}");
                
                if (Items == null || !Items.Any())
                    return Json(new { success = false, message = "No items in the order" });

                // Get orderType from TempData if not in query string
                string experienceType = orderType ?? TempData["ExperienceType"]?.ToString() ?? "AlaCarte";

                // Validate quantity limit for Ala Carte orders (max 5 per item)
                if (experienceType == "AlaCarte")
                {
                    var itemsExceedingLimit = Items.Where(i => i.Quantity > 5).ToList();
                    if (itemsExceedingLimit.Any())
                    {
                        var itemNames = string.Join(", ", itemsExceedingLimit.Select(i => i.ItemName));
                        return Json(new { success = false, message = $"Maximum quantity of 5 per item allowed. The following items exceed this limit: {itemNames}" });
                    }
                }

                decimal subtotal;
                decimal tax;
                decimal total;

                // For Unlimited orders, calculate based on personCount * pricePerHead
                if (experienceType == "Unlimited" && personCount.HasValue && personCount.Value > 0)
                {
                    const decimal pricePerHead = 477m;
                    HttpContext.Session.SetInt32(SessionPersonCount, personCount.Value);
                    subtotal = personCount.Value * pricePerHead;
                    tax = subtotal * 0.12m;
                    total = subtotal + tax;
                }
                else
                {
                    // For Ala Carte orders, calculate based on item prices
                    subtotal = Items.Sum(i => i.Price * i.Quantity);
                    tax = subtotal * 0.12m;
                    total = subtotal + tax;
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

                SaveOrderingCookies(tableNumber, floor, personCount);

                var tableGate = await CheckTableOrderingGateAsync(tableNumber);
                if (!tableGate.CanOrder)
                {
                    return Json(new
                    {
                        success = false,
                        message = tableGate.Message
                    });
                }

                var orderNumber = await _orderService.CreateUniqueOrderNumberAsync(tableNumber);

                // Check 2-hour time limit after first confirmed order (session)
                const int orderingSessionHours = 2;
                DateTime? firstOrderTime = GetFirstOrderTimeUtc();

                if (firstOrderTime.HasValue)
                {
                    var timeSinceFirstOrder = DateTime.UtcNow - firstOrderTime.Value;
                    var sessionLimit = TimeSpan.FromHours(orderingSessionHours);
                    
                    if (timeSinceFirstOrder > sessionLimit)
                    {
                        return Json(new { 
                            success = false, 
                            message = $"Time limit exceeded. Your {orderingSessionHours}-hour ordering window started at {firstOrderTime.Value.ToLocalTime():hh:mm tt} and has expired. Please start a new session." 
                        });
                    }
                }
                else
                {
                    // First order - store the timestamp in session
                    HttpContext.Session.SetString(SessionFirstOrderTime, DateTime.UtcNow.ToString("O"));
                }

                var order = new Order
                {
                    OrderNumber = orderNumber,
                    OrderDate = DateTime.UtcNow,
                    Status = "Pending",
                    OrderType = experienceType,
                    DiningType = diningType,
                    OrderChannel = channel,
                    TableNumber = tableNumber,
                    Floor = floor,
                    PaymentMethod = "Cash",
                    PaymentStatus = "Pending",
                    Subtotal = subtotal,
                    Tax = tax,
                    Total = total,
                    Items = Items
                };

                await _orderService.CreateAsync(order);

                return Json(new { success = true, orderNumber = order.OrderNumber });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating order");
                return Json(new { success = false, message = $"Error creating order: {ex.Message}" });
            }
        }

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public IActionResult SaveOrderingSession([FromQuery] int personCount)
        {
            if (personCount <= 0)
                return Json(new { success = false, message = "Person count must be greater than zero." });

            HttpContext.Session.SetInt32(SessionPersonCount, personCount);
            SaveOrderingCookies(
                HttpContext.Session.GetString(SessionServiceTable),
                HttpContext.Session.GetString(SessionServiceFloor),
                personCount);

            return Json(new { success = true });
        }

        [HttpGet]
        public async Task<IActionResult> Confirmation(string orderNumber)
        {
            if (string.IsNullOrEmpty(orderNumber))
                return RedirectToAction("Index");

            var order = await _orderService.GetByOrderNumberAsync(orderNumber);
            if (order == null)
                return RedirectToAction("Index");

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

            ApplyOrderingWindowToViewBag();

            return View(order);
        }

        [HttpGet]
        [IgnoreAntiforgeryToken]
        public IActionResult GetSessionInfo()
        {
            var firstOrderTime = GetFirstOrderTimeUtc();
            if (!firstOrderTime.HasValue)
                return Json(new { hasSession = false, sessionHours = (int)OrderingSessionLength.TotalHours });

            var sessionLimit = OrderingSessionLength;
            var timeSinceFirstOrder = DateTime.UtcNow - firstOrderTime.Value;
            var timeRemaining = sessionLimit - timeSinceFirstOrder;
            var isExpired = timeRemaining <= TimeSpan.Zero;

            var maxSeconds = (int)sessionLimit.TotalSeconds;
            int timeRemainingSeconds = 0;
            if (!isExpired && timeRemaining.TotalSeconds > 0)
            {
                timeRemainingSeconds = Math.Max(0, Math.Min(maxSeconds, (int)timeRemaining.TotalSeconds));
            }

            // Calculate minutes and seconds for display
            int minutes = timeRemainingSeconds / 60;
            int seconds = timeRemainingSeconds % 60;

            return Json(new
            {
                hasSession = true,
                firstOrderTime = firstOrderTime,
                sessionHours = (int)OrderingSessionLength.TotalHours,
                sessionEndsAt = firstOrderTime.Value.Add(sessionLimit),
                timeRemainingSeconds = timeRemainingSeconds,
                timeRemainingMinutes = minutes,
                isExpired = isExpired,
                timeRemainingFormatted = isExpired ? "00:00" : $"{minutes:D2}:{seconds:D2}"
            });
        }

        [HttpGet]
        public async Task<IActionResult> GetOrderStatus(string orderNumber)
        {
            if (string.IsNullOrEmpty(orderNumber))
                return Json(new { status = "" });

            var order = await _orderService.GetByOrderNumberAsync(orderNumber);
            if (order == null)
                return Json(new { status = "" });

            return Json(new { status = order.Status });
        }

        [HttpGet]
        public async Task<IActionResult> LookupOrder(string orderNumber)
        {
            if (string.IsNullOrEmpty(orderNumber))
            {
                TempData["ErrorMessage"] = "Please enter an order number";
                return RedirectToAction("Index");
            }

            var order = await _orderService.GetByOrderNumberAsync(orderNumber);
            if (order == null)
            {
                TempData["ErrorMessage"] = $"Order #{orderNumber} not found. Please check your order number and try again.";
                return RedirectToAction("Index");
            }

            // Order found, redirect to confirmation page
            return RedirectToAction("Confirmation", new { orderNumber = orderNumber });
        }


        [HttpPost]
        [IgnoreAntiforgeryToken] // Allow API calls without CSRF token
        public async Task<IActionResult> CancelOrder(string orderNumber)
        {
            try
            {
                if (string.IsNullOrEmpty(orderNumber))
                {
                    return Json(new { success = false, message = "Order number is required" });
                }

                var order = await _orderService.GetByOrderNumberAsync(orderNumber);
                if (order == null)
                {
                    return Json(new { success = false, message = "Order not found" });
                }

                // Check if order can be cancelled (not in progress, completed, or already cancelled)
                if (order.Status != null && 
                    (order.Status.Equals("In Progress", StringComparison.OrdinalIgnoreCase) ||
                     order.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase) ||
                     order.Status.Equals("Canceled", StringComparison.OrdinalIgnoreCase)))
                {
                    return Json(new { success = false, message = $"Cannot cancel order. Order status is: {order.Status}" });
                }

                // Cancel the order
                await _orderService.CancelOrderAsync(order.Id);

                return Json(new { success = true, message = "Order cancelled successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cancelling order");
                return Json(new { success = false, message = $"Error cancelling order: {ex.Message}" });
            }
        }
    }

    internal class TableOrderingGateResult
    {
        public bool CanOrder { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime? SessionStartUtc { get; set; }
        public DateTime? SessionEndUtc { get; set; }
        public bool IsPaid { get; set; }
        public bool PreviousSessionExpired { get; set; }

        public static TableOrderingGateResult Allowed(DateTime? sessionStartUtc = null, DateTime? sessionEndUtc = null, bool isPaid = false, bool previousSessionExpired = false)
        {
            return new TableOrderingGateResult
            {
                CanOrder = true,
                SessionStartUtc = sessionStartUtc,
                SessionEndUtc = sessionEndUtc,
                IsPaid = isPaid,
                PreviousSessionExpired = previousSessionExpired
            };
        }

        public static TableOrderingGateResult Blocked(string message, DateTime sessionStartUtc, DateTime sessionEndUtc)
        {
            return new TableOrderingGateResult
            {
                CanOrder = false,
                Message = message,
                SessionStartUtc = sessionStartUtc,
                SessionEndUtc = sessionEndUtc
            };
        }
    }
}
