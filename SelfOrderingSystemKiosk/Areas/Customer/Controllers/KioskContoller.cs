using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SelfOrderingSystemKiosk.Areas.Customer.Models;
using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;
using System.Security.Cryptography;
using Order = SelfOrderingSystemKiosk.Areas.Customer.Models.Order;

namespace SelfOrderingSystemKiosk.Areas.Customer.Controllers
{
    [Area("Customer")]
    public class KioskController : Controller
    {
        private readonly OrderService _orderService;
        private readonly TableOrderingSessionService _tableOrderingSessions;
        private readonly MenuItemService _menuItems;
        private readonly MenuCategoryRegistry _menuCategories;
        private readonly ILogger<KioskController> _logger;
        private bool _skipRememberedPersonCountRestore;

        private const string SessionOrderChannel = "OrderChannel";
        private const string SessionServiceTable = "ServiceTableNumber";
        private const string SessionServiceFloor = "ServiceFloor";
        private const string SessionDiningType = "DiningType";
        private const string SessionPersonCount = "PersonCount";
        private const string SessionEndedTableReset = "EndedTableSessionReset";
        private const string CookieServiceTable = "KdsOrderTable";
        private const string CookieServiceFloor = "KdsOrderFloor";
        private const string CookiePersonCount = "KdsOrderPersonCount";
        private const string OrderChannelKiosk = "Kiosk";
        private const string OrderChannelQr = "Qr";
        private const string DefaultKioskTableNumber = "KIOSK";
        private const string DefaultTakeOutTableNumber = "TAKEOUT";
        private const string SessionFirstOrderTime = "FirstOrderTime";
        private const string OrderAccessSessionPrefix = "OrderAccess:";
        private static readonly TimeSpan OrderingSessionLength = TimeSpan.FromHours(2);
        private static readonly TimeSpan CustomerCancelWindow = TimeSpan.FromSeconds(5);

        public KioskController(
            OrderService orderService,
            TableOrderingSessionService tableOrderingSessions,
            MenuItemService menuItems,
            MenuCategoryRegistry menuCategories,
            ILogger<KioskController> logger)
        {
            _orderService = orderService;
            _tableOrderingSessions = tableOrderingSessions;
            _menuItems = menuItems;
            _menuCategories = menuCategories;
            _logger = logger;
        }

        private void SetKioskChannelDefaults()
        {
            HttpContext.Session.SetString(SessionOrderChannel, OrderChannelKiosk);
            HttpContext.Session.Remove(SessionServiceTable);
            HttpContext.Session.Remove(SessionServiceFloor);
            Response.Cookies.Delete(CookieServiceTable);
            Response.Cookies.Delete(CookieServiceFloor);
        }

        private async Task ApplyOrderingSessionToViewBagAsync()
        {
            RestoreOrderingCookiesToSession();
            var channel = HttpContext.Session.GetString(SessionOrderChannel) ?? OrderChannelKiosk;
            ViewBag.OrderChannel = channel;
            ViewBag.IsQrFlow = channel == OrderChannelQr;
            if (channel == OrderChannelQr)
            {
                var table = HttpContext.Session.GetString(SessionServiceTable);
                var floor = HttpContext.Session.GetString(SessionServiceFloor);
                if (!string.IsNullOrWhiteSpace(table))
                {
                    await ResetEndedTableSessionPersonCountAsync(table);
                    await CheckTableOrderingGateAsync(table);
                    await RestoreSharedTablePersonCountAsync(table);
                }

                var tableSession = !string.IsNullOrWhiteSpace(table)
                    ? await _tableOrderingSessions.GetAsync(table)
                    : null;
                ViewBag.SharedWingFlavors = tableSession?.WingFlavors ?? new List<string>();
                ViewBag.PersonCount = GetSessionInt(SessionPersonCount);
                ApplyOrderingWindowToViewBag();
                ViewBag.ServiceTable = table;
                ViewBag.ServiceFloor = floor;
                ViewBag.LocationLabel = BuildLocationLabel(floor, table);
                return;
            }

            ViewBag.SharedWingFlavors = new List<string>();
            ViewBag.PersonCount = GetSessionInt(SessionPersonCount);
            ApplyOrderingWindowToViewBag();
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
            else
                Response.Cookies.Delete(CookieServiceTable);

            if (!string.IsNullOrWhiteSpace(floor))
                Response.Cookies.Append(CookieServiceFloor, floor, options);
            else
                Response.Cookies.Delete(CookieServiceFloor);

            if (personCount.HasValue && personCount.Value > 0)
                Response.Cookies.Append(CookiePersonCount, personCount.Value.ToString(), options);
        }

        private async Task ClearRememberedPersonCountAsync(string tableNumber = null)
        {
            _skipRememberedPersonCountRestore = true;
            HttpContext.Session.Remove(SessionPersonCount);
            HttpContext.Session.Remove(SessionFirstOrderTime);
            Response.Cookies.Delete(CookiePersonCount);
            if (!string.IsNullOrWhiteSpace(tableNumber))
            {
                await _tableOrderingSessions.ClearAsync(tableNumber);
                HttpContext.Session.SetString(SessionEndedTableReset, tableNumber);
            }
        }

        private async Task ResetEndedTableSessionPersonCountAsync(string tableNumber)
        {
            if (string.IsNullOrWhiteSpace(tableNumber))
                return;

            if (await HasEndedTableSessionReadyForNewSessionAsync(tableNumber))
                await ClearRememberedPersonCountAsync(tableNumber);
        }

        private async Task RestoreSharedTablePersonCountAsync(string tableNumber)
        {
            if (string.IsNullOrWhiteSpace(tableNumber))
                return;

            if (GetSessionInt(SessionPersonCount) is > 0)
                return;

            var tableSession = await _tableOrderingSessions.GetAsync(tableNumber);
            if (tableSession?.PersonCount > 0)
            {
                HttpContext.Session.SetInt32(SessionPersonCount, tableSession.PersonCount);
                return;
            }

            var tableOrders = await _orderService.GetOrdersByTableAsync(tableNumber);
            var sharedCount = tableOrders
                .Where(o => !o.BillArchived && IsUnlimitedOrder(o))
                .OrderByDescending(o => o.OrderDate)
                .Select(GetOrderPersonCount)
                .FirstOrDefault(count => count.HasValue && count.Value > 0);

            if (sharedCount.HasValue)
                HttpContext.Session.SetInt32(SessionPersonCount, sharedCount.Value);
        }

        private async Task<int> GetSharedTablePersonCountAsync(string tableNumber)
        {
            if (string.IsNullOrWhiteSpace(tableNumber))
                return 0;

            var tableSession = await _tableOrderingSessions.GetAsync(tableNumber);
            if (tableSession?.PersonCount > 0)
                return tableSession.PersonCount;

            var tableOrders = await _orderService.GetOrdersByTableAsync(tableNumber);
            return tableOrders
                .Where(o => !o.BillArchived && IsUnlimitedOrder(o))
                .Select(GetOrderPersonCount)
                .Where(count => count.HasValue && count.Value > 0)
                .Select(count => count!.Value)
                .DefaultIfEmpty(0)
                .Max();
        }

        private async Task SeedSharedTableSessionFromOrdersAsync(string tableNumber)
        {
            if (string.IsNullOrWhiteSpace(tableNumber))
                return;

            if (await _tableOrderingSessions.GetAsync(tableNumber) != null)
                return;

            var tableOrders = await _orderService.GetOrdersByTableAsync(tableNumber);
            var openUnlimitedOrders = tableOrders
                .Where(o => !o.BillArchived && IsUnlimitedOrder(o))
                .ToList();
            if (!openUnlimitedOrders.Any())
                return;

            var personCount = openUnlimitedOrders
                .Select(GetOrderPersonCount)
                .Where(count => count.HasValue && count.Value > 0)
                .Select(count => count!.Value)
                .DefaultIfEmpty(0)
                .Max();
            var wingFlavors = await ExtractUnlimitedWingFlavorsAsync(
                openUnlimitedOrders.SelectMany(o => o.Items ?? new List<OrderItem>()));

            await _tableOrderingSessions.SeedFromExistingOrdersAsync(tableNumber, personCount, wingFlavors);
        }

        private async Task RebuildSharedTableSessionFromOrdersAsync(string tableNumber)
        {
            if (string.IsNullOrWhiteSpace(tableNumber))
                return;

            var tableOrders = await _orderService.GetOrdersByTableAsync(tableNumber);
            var openUnlimitedOrders = tableOrders
                .Where(o => !o.BillArchived && IsUnlimitedOrder(o))
                .ToList();
            if (!openUnlimitedOrders.Any())
            {
                await _tableOrderingSessions.ClearAsync(tableNumber);
                return;
            }

            var personCount = openUnlimitedOrders
                .Select(GetOrderPersonCount)
                .Where(count => count.HasValue && count.Value > 0)
                .Select(count => count!.Value)
                .DefaultIfEmpty(0)
                .Max();
            var wingFlavors = await ExtractUnlimitedWingFlavorsAsync(
                openUnlimitedOrders.SelectMany(o => o.Items ?? new List<OrderItem>()));

            await _tableOrderingSessions.ReplaceFromExistingOrdersAsync(tableNumber, personCount, wingFlavors);
        }

        private static int? GetOrderPersonCount(Order order)
        {
            if (order?.PersonCount is > 0)
                return order.PersonCount.Value;

            const decimal pricePerHead = 477m;
            if (order != null && IsUnlimitedOrder(order) && order.Subtotal >= pricePerHead)
                return Math.Max(1, (int)Math.Floor(order.Subtotal / pricePerHead));

            return null;
        }

        private async Task<bool> HasEndedTableSessionReadyForNewSessionAsync(string tableNumber)
        {
            if (string.IsNullOrWhiteSpace(tableNumber))
                return false;

            var tableOrders = await _orderService.GetOrdersByTableAsync(tableNumber);
            if (!tableOrders.Any())
                return false;

            var openUnlimitedOrders = tableOrders
                .Where(o => !o.BillArchived && IsUnlimitedOrder(o))
                .ToList();
            var hasEndedSession = tableOrders.Any(o => o.BillArchived);
            if (hasEndedSession && !openUnlimitedOrders.Any())
                return true;

            var latestSession = GetLatestTableSession(tableOrders);
            if (!latestSession.Any())
                return false;

            var sessionStart = latestSession
                .Select(GetOrderSessionStartUtc)
                .Where(start => start.HasValue)
                .Select(start => start!.Value)
                .Min();
            var sessionEnd = sessionStart.Add(OrderingSessionLength);
            var hasNewPendingSessionOrder = openUnlimitedOrders.Any(o =>
                !GetOrderSessionStartUtc(o).HasValue &&
                ToUtc(o.OrderDate) >= sessionEnd);

            if (hasNewPendingSessionOrder)
                return false;

            var isExpired = DateTime.UtcNow >= sessionEnd;
            var isPaid = latestSession.All(o => string.Equals(o.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase));
            return isExpired && isPaid;
        }

        private bool WasEndedTableSessionReset(string tableNumber)
        {
            return string.Equals(
                HttpContext.Session.GetString(SessionEndedTableReset),
                tableNumber,
                StringComparison.OrdinalIgnoreCase);
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

            if (!_skipRememberedPersonCountRestore &&
                !GetSessionInt(SessionPersonCount).HasValue &&
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
            {
                HttpContext.Session.Remove(SessionFirstOrderTime);
                return TableOrderingGateResult.Allowed();
            }

            var sessionStart = latestSession
                .Select(GetOrderSessionStartUtc)
                .Where(start => start.HasValue)
                .Select(start => start!.Value)
                .Min();
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
            ViewBag.ConfirmationPersonCount = 0;
            ViewBag.ConfirmationWingFlavors = new List<string>();

            if (order == null)
                return;

            if (!IsUnlimitedOrder(order))
            {
                ViewBag.HasOrderingSession = false;
                ViewBag.OrderingSessionExpired = false;
                return;
            }

            if (order.BillArchived)
            {
                HttpContext.Session.Remove(SessionFirstOrderTime);
                ViewBag.HasOrderingSession = false;
                ViewBag.OrderingSessionRemaining = TimeSpan.Zero;
                ViewBag.OrderingSessionExpired = false;
                ViewBag.ConfirmationBillPaid = string.Equals(order.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase);
                ViewBag.ConfirmationPersonCount = GetOrderPersonCount(order) ?? 0;
                return;
            }

            if (string.IsNullOrWhiteSpace(order.TableNumber) ||
                !string.Equals(order.DiningType, "DineIn", StringComparison.OrdinalIgnoreCase))
            {
                var orderSessionStart = GetOrderSessionStartUtc(order);
                if (orderSessionStart.HasValue)
                    HttpContext.Session.SetString(SessionFirstOrderTime, orderSessionStart.Value.ToString("O"));
                else
                    HttpContext.Session.Remove(SessionFirstOrderTime);
                ViewBag.ConfirmationBillPaid = string.Equals(order.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase);
                ViewBag.ConfirmationPersonCount = GetOrderPersonCount(order) ?? 0;
                return;
            }

            var tableOrders = await _orderService.GetOrdersByTableAsync(order.TableNumber);
            var sessionOrders = GetOrdersInSameSession(tableOrders, order);
            if (!sessionOrders.Any())
                sessionOrders = new List<Order> { order };

            var sessionStart = sessionOrders
                .Select(GetOrderSessionStartUtc)
                .Where(start => start.HasValue)
                .Select(start => start!.Value)
                .DefaultIfEmpty()
                .Min();
            if (sessionStart != default)
                HttpContext.Session.SetString(SessionFirstOrderTime, sessionStart.ToString("O"));
            else
                HttpContext.Session.Remove(SessionFirstOrderTime);
            ViewBag.ConfirmationBillPaid = sessionOrders.All(o => string.Equals(o.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase));
            ViewBag.ConfirmationPersonCount = sessionOrders
                .Select(GetOrderPersonCount)
                .Where(count => count.HasValue && count.Value > 0)
                .Select(count => count!.Value)
                .DefaultIfEmpty(GetOrderPersonCount(order) ?? 0)
                .Max();
            var tableSession = await _tableOrderingSessions.GetAsync(order.TableNumber);
            ViewBag.ConfirmationWingFlavors = tableSession?.WingFlavors?.Any() == true
                ? tableSession.WingFlavors
                : (await ExtractUnlimitedWingFlavorsAsync(sessionOrders.SelectMany(o => o.Items ?? new List<OrderItem>()))).ToList();
        }

        private static List<Order> GetLatestTableSession(List<Order> tableOrders)
        {
            var ordered = tableOrders
                .Where(o => !string.Equals(o.Status, "Canceled", StringComparison.OrdinalIgnoreCase)
                    && !o.BillArchived
                    && IsUnlimitedOrder(o)
                    && GetOrderSessionStartUtc(o).HasValue)
                .OrderBy(o => GetOrderSessionStartUtc(o))
                .ToList();

            if (!ordered.Any())
                return new List<Order>();

            var sessionStart = GetOrderSessionStartUtc(ordered.First())!.Value;
            var latestSession = new List<Order>();
            foreach (var order in ordered)
            {
                var orderSessionStart = GetOrderSessionStartUtc(order)!.Value;
                if (orderSessionStart >= sessionStart.Add(OrderingSessionLength))
                {
                    sessionStart = orderSessionStart;
                    latestSession.Clear();
                }

                latestSession.Add(order);
            }

            return latestSession;
        }

        private static List<Order> GetOrdersInSameSession(List<Order> tableOrders, Order anchorOrder)
        {
            var includeArchived = anchorOrder.BillArchived;
            var ordered = tableOrders
                .Where(o => !string.Equals(o.Status, "Canceled", StringComparison.OrdinalIgnoreCase)
                    && o.BillArchived == includeArchived
                    && IsUnlimitedOrder(o))
                .OrderBy(o => o.OrderDate)
                .ToList();

            if (!ordered.Any())
                return new List<Order> { anchorOrder };

            var startedSessions = ordered
                .Select(GetOrderSessionStartUtc)
                .Where(start => start.HasValue)
                .Select(start => start!.Value)
                .OrderBy(start => start)
                .ToList();
            var anchorSessionStart = GetOrderSessionStartUtc(anchorOrder);
            if (!anchorSessionStart.HasValue)
            {
                anchorSessionStart = startedSessions
                    .LastOrDefault(start => ToUtc(anchorOrder.OrderDate) >= start && ToUtc(anchorOrder.OrderDate) < start.Add(OrderingSessionLength));

                if (!anchorSessionStart.HasValue || anchorSessionStart.Value == default)
                {
                    anchorSessionStart = startedSessions
                        .FirstOrDefault(start => ToUtc(anchorOrder.OrderDate) <= start);
                }
            }

            if (!anchorSessionStart.HasValue || anchorSessionStart.Value == default)
                return ordered;

            var sessionStart = anchorSessionStart.Value;
            var sessionEnd = sessionStart.Add(OrderingSessionLength);
            var previousSessionEnd = startedSessions
                .Where(start => start < sessionStart)
                .Select(start => start.Add(OrderingSessionLength))
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            return ordered
                .Where(o =>
                {
                    var orderSessionStart = GetOrderSessionStartUtc(o);
                    if (orderSessionStart.HasValue)
                        return orderSessionStart.Value >= sessionStart && orderSessionStart.Value < sessionEnd;

                    var orderDate = ToUtc(o.OrderDate);
                    return orderDate > previousSessionEnd && orderDate < sessionEnd;
                })
                .ToList();
        }

        private static bool IsUnlimitedOrder(Order order)
        {
            return string.Equals(order.OrderType, "Unlimited", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDefaultServiceTable(string? tableNumber)
        {
            return string.IsNullOrWhiteSpace(tableNumber)
                || string.Equals(tableNumber, DefaultKioskTableNumber, StringComparison.OrdinalIgnoreCase)
                || string.Equals(tableNumber, DefaultTakeOutTableNumber, StringComparison.OrdinalIgnoreCase)
                || string.Equals(tableNumber, "0", StringComparison.OrdinalIgnoreCase);
        }

        private static DateTime ToUtc(DateTime value)
        {
            return value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                : value.ToUniversalTime();
        }

        private static DateTime? GetOrderSessionStartUtc(Order order)
        {
            if (order == null || string.Equals(order.Status, "Canceled", StringComparison.OrdinalIgnoreCase))
                return null;

            if (order.SessionStartedAtUtc.HasValue)
                return ToUtc(order.SessionStartedAtUtc.Value);

            return null;
        }

        private static bool IsOrderingSessionExpired(Order order)
        {
            var sessionStart = GetOrderSessionStartUtc(order);
            return sessionStart.HasValue && DateTime.UtcNow >= sessionStart.Value.Add(OrderingSessionLength);
        }

        private void RememberOrderAccess(Order order)
        {
            if (!string.IsNullOrWhiteSpace(order?.OrderNumber) && !string.IsNullOrWhiteSpace(order.PublicAccessToken))
                HttpContext.Session.SetString(OrderAccessSessionPrefix + order.OrderNumber, order.PublicAccessToken);
        }

        private bool HasPrivateOrderAccess(Order order, string accessToken)
        {
            if (order == null)
                return false;
            if (string.IsNullOrWhiteSpace(order.PublicAccessToken))
                return false;
            if (!string.IsNullOrWhiteSpace(accessToken) &&
                CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(order.PublicAccessToken),
                    System.Text.Encoding.UTF8.GetBytes(accessToken.Trim())))
                return true;

            var remembered = HttpContext.Session.GetString(OrderAccessSessionPrefix + order.OrderNumber);
            return !string.IsNullOrWhiteSpace(remembered) &&
                CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(order.PublicAccessToken),
                    System.Text.Encoding.UTF8.GetBytes(remembered));
        }

        private static string CreatePublicAccessToken()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
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

        public async Task<IActionResult> Index(bool startNewSession = false)
        {
            SetKioskChannelDefaults();
            if (startNewSession)
                _skipRememberedPersonCountRestore = true;

            RestoreOrderingCookiesToSession();
            var table = HttpContext.Session.GetString(SessionServiceTable);
            if (startNewSession)
                await ClearRememberedPersonCountAsync(table);

            if (!string.IsNullOrWhiteSpace(table))
                await ResetEndedTableSessionPersonCountAsync(table);

            return View();
        }

        /// <summary>Table QR entry point. Example: /Customer/Kiosk/Qr?table=12&amp;floor=2</summary>
        [HttpGet]
        public async Task<IActionResult> Qr(string table, string floor = null)
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

            if (!await _tableOrderingSessions.IsOrderingOpenAsync(table))
            {
                TempData["ErrorMessage"] = "Ordering for this table is not available yet. Please ask staff to seat/open your table.";
                return RedirectToAction("Index");
            }

            HttpContext.Session.SetString(SessionOrderChannel, OrderChannelQr);
            HttpContext.Session.SetString(SessionServiceTable, table);
            if (floor != null)
                HttpContext.Session.SetString(SessionServiceFloor, floor);
            else
                HttpContext.Session.Remove(SessionServiceFloor);

            HttpContext.Session.SetString(SessionDiningType, "DineIn");
            await ResetEndedTableSessionPersonCountAsync(table);
            await RestoreSharedTablePersonCountAsync(table);
            SaveOrderingCookies(table, floor, GetSessionInt(SessionPersonCount));
            TempData["DiningType"] = "DineIn";
            return RedirectToAction("ChooseExperience");
        }

        [HttpPost]
        public IActionResult SelectDining(string diningType)
        {
            SetKioskChannelDefaults();
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
            ViewBag.MenuCategories = _menuCategories.KioskTabs
                .Where(c => !string.Equals(c.Key, "Wings", StringComparison.Ordinal))
                .ToList();
            ViewBag.DefaultMenuCategory = "Sulit Kap Meals";
            RestoreOrderingCookiesToSession();
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
                        await ClearRememberedPersonCountAsync(previousOrder.TableNumber);
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
            var items = await _menuItems.GetAvailableAsync() ?? new List<MenuItem>();
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
        public async Task<IActionResult> ConfirmOrder([FromBody] List<OrderItem> Items, [FromQuery] string orderType, [FromQuery] int? personCount)
        {
            try
            {
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

                if (isRealQrTableOrder && !await _tableOrderingSessions.IsOrderingOpenAsync(tableNumber))
                {
                    return Json(new
                    {
                        success = false,
                        message = "Ordering for this table is not available. Please ask staff to seat/open your table."
                    });
                }

                if (isRealQrTableOrder &&
                    isUnlimitedOrder &&
                    await HasEndedTableSessionReadyForNewSessionAsync(tableNumber) &&
                    !WasEndedTableSessionReset(tableNumber))
                {
                    await ClearRememberedPersonCountAsync(tableNumber);
                    return Json(new
                    {
                        success = false,
                        resetPersonCount = true,
                        message = "The previous table session has ended. Please enter the number of persons for this new session."
                    });
                }

                SaveOrderingCookies(isRealQrTableOrder ? tableNumber : null, isRealQrTableOrder ? floor : null, personCount);

                var tableGate = isRealQrTableOrder && isUnlimitedOrder
                    ? await CheckTableOrderingGateAsync(tableNumber)
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
                    await SeedSharedTableSessionFromOrdersAsync(tableNumber);

                if (isUnlimitedOrder)
                {
                    const decimal pricePerHead = 477m;
                    var chargeablePersonCount = personCount!.Value;
                    if (isRealQrTableOrder)
                    {
                        var reservation = await _tableOrderingSessions.ReserveUnlimitedOrderAsync(
                            tableNumber,
                            personCount.Value,
                            validation.UnlimitedWingFlavors);
                        if (!reservation.Success)
                            return Json(new { success = false, message = reservation.Message });

                        personCount = reservation.PersonCount;
                        chargeablePersonCount = reservation.ChargeablePersonCount;
                        HttpContext.Session.SetInt32(SessionPersonCount, personCount.Value);
                        SaveOrderingCookies(tableNumber, floor, personCount);
                    }

                    subtotal = (chargeablePersonCount * pricePerHead) + alaCarteAddOnSubtotal;
                    total = subtotal;
                }

                var orderNumber = await _orderService.CreateUniqueOrderNumberAsync(tableNumber);

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
                                message = $"Time limit exceeded. Your {orderingSessionHours}-hour ordering window started at {firstOrderTime.Value.ToLocalTime():hh:mm tt} and has expired. Please start a new session."
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
                    Items = Items
                };

                await _orderService.CreateAsync(order);
                if (isRealQrTableOrder && isUnlimitedOrder)
                    await _orderService.UpdateOpenUnlimitedPersonCountForTableAsync(tableNumber, personCount!.Value);

                HttpContext.Session.Remove(SessionEndedTableReset);
                RememberOrderAccess(order);

                return Json(new { success = true, orderNumber = order.OrderNumber, accessToken = order.PublicAccessToken });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating order");
                return Json(new { success = false, message = "Error creating order. Please try again." });
            }
        }

        [HttpPost]
        public async Task<IActionResult> SaveOrderingSession([FromQuery] int personCount)
        {
            if (personCount <= 0 || personCount > 50)
                return Json(new { success = false, message = "Person count must be greater than zero." });

            RestoreOrderingCookiesToSession();
            var table = HttpContext.Session.GetString(SessionServiceTable);
            if (!string.IsNullOrWhiteSpace(table) && !await _tableOrderingSessions.IsOrderingOpenAsync(table))
            {
                return Json(new
                {
                    success = false,
                    message = "Ordering for this table is not available. Please ask staff to seat/open your table."
                });
            }

            if (!string.IsNullOrWhiteSpace(table) &&
                await HasEndedTableSessionReadyForNewSessionAsync(table) &&
                !WasEndedTableSessionReset(table))
            {
                await ClearRememberedPersonCountAsync(table);
                return Json(new
                {
                    success = false,
                    resetPersonCount = true,
                    message = "The previous table session has ended. Please enter the number of persons for this new session."
                });
            }

            if (!string.IsNullOrWhiteSpace(table))
            {
                var existingSession = await _tableOrderingSessions.GetAsync(table);
                var openUnlimitedOrders = (await _orderService.GetOrdersByTableAsync(table))
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

                var tableSession = await _tableOrderingSessions.SavePersonCountAsync(table, personCount);
                if (tableSession?.PersonCount > 0)
                    personCount = tableSession.PersonCount;

                await _orderService.UpdateOpenUnlimitedPersonCountForTableAsync(table, personCount);
            }

            HttpContext.Session.SetInt32(SessionPersonCount, personCount);

            SaveOrderingCookies(
                table,
                HttpContext.Session.GetString(SessionServiceFloor),
                personCount);

            return Json(new { success = true, personCount });
        }

        private static int GetChargedUnlimitedPersonCount(Order order)
        {
            if (order == null || !IsUnlimitedOrder(order))
                return 0;

            const decimal pricePerHead = 477m;
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

            var order = await _orderService.GetByOrderNumberAsync(orderNumber);
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

                var anchorOrder = await _orderService.GetByOrderNumberAsync(orderNumber);
                if (anchorOrder == null || !HasPrivateOrderAccess(anchorOrder, accessToken))
                    return Json(new { success = false, message = "Unable to access this order." });
                if (!IsUnlimitedOrder(anchorOrder))
                    return Json(new { success = false, message = "Quick reorder is only available for Unlimited orders." });
                if (anchorOrder.BillArchived || IsOrderingSessionExpired(anchorOrder))
                    return Json(new { success = false, sessionEnded = true, message = "This unlimited ordering session has ended." });

                var validation = await ValidateSubmittedItemsAsync(items, true);
                if (!validation.Success)
                    return Json(new { success = false, message = validation.Message });

                var tableNumber = anchorOrder.TableNumber;
                var floor = anchorOrder.Floor;
                var isRealQrTableOrder = !string.IsNullOrWhiteSpace(tableNumber)
                    && !IsDefaultServiceTable(tableNumber)
                    && string.Equals(anchorOrder.DiningType, "DineIn", StringComparison.OrdinalIgnoreCase);

                if (isRealQrTableOrder && !await _tableOrderingSessions.IsOrderingOpenAsync(tableNumber))
                    return Json(new { success = false, message = "Ordering for this table is currently closed. Please ask staff for help." });

                var personCount = isRealQrTableOrder
                    ? await GetSharedTablePersonCountAsync(tableNumber)
                    : GetOrderPersonCount(anchorOrder) ?? 0;
                if (personCount <= 0)
                    return Json(new { success = false, message = "Unable to find the person count for this unlimited session." });

                var tableGate = isRealQrTableOrder
                    ? await CheckTableOrderingGateAsync(tableNumber)
                    : TableOrderingGateResult.Allowed();
                if (!tableGate.CanOrder)
                    return Json(new { success = false, message = tableGate.Message });

                if (isRealQrTableOrder)
                    await SeedSharedTableSessionFromOrdersAsync(tableNumber);

                var chargeablePersonCount = 0;
                if (isRealQrTableOrder)
                {
                    var reservation = await _tableOrderingSessions.ReserveUnlimitedOrderAsync(
                        tableNumber,
                        personCount,
                        validation.UnlimitedWingFlavors);
                    if (!reservation.Success)
                        return Json(new { success = false, message = reservation.Message });

                    personCount = reservation.PersonCount;
                    chargeablePersonCount = reservation.ChargeablePersonCount;
                }

                const decimal pricePerHead = 477m;
                var alaCarteAddOnSubtotal = validation.Items.Sum(i => i.Price * i.Quantity);
                var subtotal = (chargeablePersonCount * pricePerHead) + alaCarteAddOnSubtotal;
                var order = new Order
                {
                    OrderNumber = await _orderService.CreateUniqueOrderNumberAsync(tableNumber),
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
                    Items = validation.Items
                };

                await _orderService.CreateAsync(order);
                if (isRealQrTableOrder)
                    await _orderService.UpdateOpenUnlimitedPersonCountForTableAsync(tableNumber, personCount);

                RememberOrderAccess(order);
                SaveOrderingCookies(isRealQrTableOrder ? tableNumber : null, isRealQrTableOrder ? floor : null, personCount);

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

        [HttpGet]
        public async Task<IActionResult> GetSessionInfo(string orderNumber = null, string accessToken = null)
        {
            RestoreOrderingCookiesToSession();

            if (!string.IsNullOrWhiteSpace(orderNumber))
            {
                var order = await _orderService.GetByOrderNumberAsync(orderNumber);
                if (order == null || !IsUnlimitedOrder(order))
                {
                    HttpContext.Session.Remove(SessionFirstOrderTime);
                    return Json(new { hasSession = false, sessionHours = (int)OrderingSessionLength.TotalHours });
                }

                if (order.BillArchived)
                {
                    HttpContext.Session.Remove(SessionFirstOrderTime);
                    await ClearRememberedPersonCountAsync(order.TableNumber);
                    return Json(new
                    {
                        hasSession = false,
                        sessionEnded = true,
                        sessionHours = (int)OrderingSessionLength.TotalHours
                    });
                }

                if (HasPrivateOrderAccess(order, accessToken))
                    RememberOrderAccess(order);

                RestoreQrSessionFromOrder(order);
                var orderSessionStart = GetOrderSessionStartUtc(order);
                if (!string.IsNullOrWhiteSpace(order.TableNumber) &&
                    string.Equals(order.DiningType, "DineIn", StringComparison.OrdinalIgnoreCase))
                {
                    await CheckTableOrderingGateAsync(order.TableNumber);
                }
                else if (orderSessionStart.HasValue)
                {
                    HttpContext.Session.SetString(SessionFirstOrderTime, orderSessionStart.Value.ToString("O"));
                }
                else
                {
                    HttpContext.Session.Remove(SessionFirstOrderTime);
                }
            }

            var channel = HttpContext.Session.GetString(SessionOrderChannel) ?? OrderChannelKiosk;
            if (string.Equals(channel, OrderChannelQr, StringComparison.OrdinalIgnoreCase))
            {
                var table = HttpContext.Session.GetString(SessionServiceTable);
                if (!string.IsNullOrWhiteSpace(table) &&
                    await HasEndedTableSessionReadyForNewSessionAsync(table))
                {
                    await ClearRememberedPersonCountAsync(table);
                    return Json(new
                    {
                        hasSession = false,
                        sessionEnded = true,
                        resetPersonCount = true,
                        sessionHours = (int)OrderingSessionLength.TotalHours
                    });
                }

                if (!string.IsNullOrWhiteSpace(table))
                {
                    await CheckTableOrderingGateAsync(table);
                    await RestoreSharedTablePersonCountAsync(table);
                }
            }

            var firstOrderTime = GetFirstOrderTimeUtc();
            if (!firstOrderTime.HasValue)
                return Json(new
                {
                    hasSession = false,
                    sessionHours = (int)OrderingSessionLength.TotalHours,
                    personCount = GetSessionInt(SessionPersonCount) ?? 0
                });

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

            // Calculate hours, minutes, and seconds for display.
            int hours = timeRemainingSeconds / 3600;
            int minutes = (timeRemainingSeconds % 3600) / 60;
            int seconds = timeRemainingSeconds % 60;

            return Json(new
            {
                hasSession = true,
                firstOrderTime = firstOrderTime,
                sessionHours = (int)OrderingSessionLength.TotalHours,
                sessionEndsAt = firstOrderTime.Value.Add(sessionLimit),
                timeRemainingSeconds = timeRemainingSeconds,
                timeRemainingHours = hours,
                timeRemainingMinutes = minutes,
                isExpired = isExpired,
                personCount = GetSessionInt(SessionPersonCount) ?? 0,
                timeRemainingFormatted = isExpired ? "00:00:00" : $"{hours:D2}:{minutes:D2}:{seconds:D2}"
            });
        }

        [HttpGet]
        public async Task<IActionResult> GetOrderStatus(string orderNumber, string accessToken = null)
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

            return RedirectToAction("Confirmation", new { orderNumber = orderNumber });
        }


        [HttpPost]
        public async Task<IActionResult> CancelOrder(string orderNumber, string accessToken = null)
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
                if (!HasPrivateOrderAccess(order, accessToken))
                {
                    return Json(new { success = false, message = "Please use the original confirmation link for this order." });
                }
                if (DateTime.UtcNow - order.OrderDate.ToUniversalTime() > CustomerCancelWindow)
                {
                    return Json(new { success = false, message = "The cancellation window has already closed." });
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
                if (IsUnlimitedOrder(order) &&
                    string.Equals(order.OrderChannel, OrderChannelQr, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(order.TableNumber))
                {
                    await RebuildSharedTableSessionFromOrdersAsync(order.TableNumber);
                }

                return Json(new { success = true, message = "Order cancelled successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cancelling order");
                return Json(new { success = false, message = "Error cancelling order. Please try again." });
            }
        }

        private async Task<OrderItemValidationResult> ValidateSubmittedItemsAsync(List<OrderItem> submittedItems, bool isUnlimitedOrder)
        {
            var availableItems = await _menuItems.GetAvailableAsync() ?? new List<MenuItem>();
            var byName = availableItems
                .Where(i => !string.IsNullOrWhiteSpace(i.Item))
                .GroupBy(i => i.Item.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var validated = new List<OrderItem>();
            var submittedUnlimitedWingFlavors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var submitted in submittedItems)
            {
                var displayName = submitted.ItemName?.Trim();
                var lookupName = NormalizeSubmittedItemName(displayName);
                if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(lookupName))
                    return OrderItemValidationResult.Fail("One or more order items are invalid.");

                if (submitted.Quantity <= 0)
                    return OrderItemValidationResult.Fail("Item quantities must be greater than zero.");

                if (!byName.TryGetValue(lookupName, out var menuItem))
                    return OrderItemValidationResult.Fail($"'{lookupName}' is not currently available.");

                if (isUnlimitedOrder)
                {
                    var isIncluded = IsUnlimitedIncludedItem(menuItem);
                    if (!isIncluded && !IsUnlimitedMenuItem(menuItem))
                        return OrderItemValidationResult.Fail($"'{lookupName}' is not available in the unlimited menu.");
                    if (!isIncluded && submitted.Quantity > 4)
                        return OrderItemValidationResult.Fail("Maximum quantity of 4 per Ala Carte add-on allowed.");
                    if (isIncluded && string.Equals(menuItem.Category, "Wings", StringComparison.Ordinal))
                    {
                        if (submitted.Quantity > 4)
                            return OrderItemValidationResult.Fail("Maximum quantity of 4 pieces per wing flavor allowed.");
                        submittedUnlimitedWingFlavors.Add(menuItem.Item.Trim());
                        if (submittedUnlimitedWingFlavors.Count > 4)
                            return OrderItemValidationResult.Fail("You can only choose up to 4 wing flavors per unlimited order.");
                    }
                    else if (isIncluded && submitted.Quantity > 20)
                    {
                        return OrderItemValidationResult.Fail("One or more item quantities are too high.");
                    }
                }
                else
                {
                    if (string.Equals(menuItem.Category, "Unlimited Inclusions", StringComparison.Ordinal))
                        return OrderItemValidationResult.Fail($"'{lookupName}' is not available for ala carte orders.");
                    if (submitted.Quantity > 4)
                        return OrderItemValidationResult.Fail("Maximum quantity of 4 per item allowed.");
                }

                validated.Add(new OrderItem
                {
                    ItemName = displayName,
                    Quantity = submitted.Quantity,
                    Price = isUnlimitedOrder && IsUnlimitedIncludedItem(menuItem) ? 0m : menuItem.Price
                });
            }

            return OrderItemValidationResult.Ok(validated, submittedUnlimitedWingFlavors);
        }

        private async Task<List<ConfirmationQuickItem>> BuildConfirmationQuickItemsAsync(Order order)
        {
            var availableItems = await _menuItems.GetAvailableAsync() ?? new List<MenuItem>();
            var includedItems = availableItems
                .Where(IsUnlimitedIncludedItem)
                .Where(i => !string.IsNullOrWhiteSpace(i.Item))
                .ToList();

            var quickItems = new List<ConfirmationQuickItem>();
            var wingFlavorNames = (ViewBag.ConfirmationWingFlavors as IEnumerable<string> ?? Enumerable.Empty<string>())
                .Select(f => f.Trim())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!wingFlavorNames.Any() && order?.Items != null)
            {
                wingFlavorNames = (await ExtractUnlimitedWingFlavorsAsync(order.Items))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var wingItemsByName = includedItems
                .Where(i => string.Equals(i.Category, "Wings", StringComparison.Ordinal))
                .GroupBy(i => i.Item.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var flavor in wingFlavorNames.Take(4))
            {
                if (!wingItemsByName.ContainsKey(flavor))
                    continue;

                quickItems.Add(new ConfirmationQuickItem
                {
                    Name = flavor,
                    Label = flavor,
                    Group = "Current Flavors",
                    Quantity = 4,
                    IsWingFlavor = true
                });
            }

            var includedNames = new[]
            {
                "Plain Rice",
                "Garlic Rice",
                "Extra Gravy",
                "Nachos",
                "Potato Thins",
                "Regular Pasta",
                "Red Iced Tea",
                "Coffee",
                "Tea"
            };

            foreach (var desiredName in includedNames)
            {
                var menuItem = includedItems
                    .Where(i => i.Item.Contains(desiredName, StringComparison.OrdinalIgnoreCase)
                        || desiredName.Contains(i.Item, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(i => i.MenuOrder)
                    .FirstOrDefault();
                if (menuItem == null)
                    continue;

                var group = string.Equals(menuItem.Category, "Drinks", StringComparison.Ordinal)
                    || menuItem.Item.Contains("Tea", StringComparison.OrdinalIgnoreCase)
                    || menuItem.Item.Contains("Coffee", StringComparison.OrdinalIgnoreCase)
                        ? "Drinks"
                        : menuItem.Item.Contains("Rice", StringComparison.OrdinalIgnoreCase)
                            ? "Rice"
                            : menuItem.Item.Contains("Pasta", StringComparison.OrdinalIgnoreCase)
                                ? "Pasta"
                                : "Sides";

                if (quickItems.Any(i => string.Equals(i.Name, menuItem.Item, StringComparison.OrdinalIgnoreCase)))
                    continue;

                quickItems.Add(new ConfirmationQuickItem
                {
                    Name = menuItem.Item,
                    Label = menuItem.Item.StartsWith("Unli Pasta ", StringComparison.OrdinalIgnoreCase)
                        ? menuItem.Item["Unli Pasta ".Length..].Trim()
                        : menuItem.Item,
                    Group = group,
                    Quantity = 1,
                    IsWingFlavor = false
                });
            }

            return quickItems;
        }

        private async Task<HashSet<string>> ExtractUnlimitedWingFlavorsAsync(IEnumerable<OrderItem> orderItems)
        {
            var availableItems = await _menuItems.GetAvailableAsync() ?? new List<MenuItem>();
            var byName = availableItems
                .Where(i => !string.IsNullOrWhiteSpace(i.Item))
                .GroupBy(i => i.Item.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var wingFlavors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in orderItems ?? Enumerable.Empty<OrderItem>())
            {
                var lookupName = NormalizeSubmittedItemName(item.ItemName);
                if (string.IsNullOrWhiteSpace(lookupName))
                    continue;

                if (byName.TryGetValue(lookupName, out var menuItem) &&
                    string.Equals(menuItem.Category, "Wings", StringComparison.Ordinal))
                {
                    wingFlavors.Add(menuItem.Item.Trim());
                }
            }

            return wingFlavors;
        }

        private static string NormalizeSubmittedItemName(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName))
                return string.Empty;

            const string flavorMarker = " (Flavors:";
            var markerIndex = itemName.IndexOf(flavorMarker, StringComparison.OrdinalIgnoreCase);
            var normalized = markerIndex >= 0
                ? itemName[..markerIndex].Trim()
                : itemName.Trim();

            if (normalized.StartsWith("Coffee - ", StringComparison.OrdinalIgnoreCase))
                return "Coffee";

            return normalized;
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

    internal class OrderItemValidationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<OrderItem> Items { get; set; } = new();
        public HashSet<string> UnlimitedWingFlavors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public static OrderItemValidationResult Ok(List<OrderItem> items, HashSet<string> unlimitedWingFlavors) => new()
        {
            Success = true,
            Items = items,
            UnlimitedWingFlavors = unlimitedWingFlavors
        };

        public static OrderItemValidationResult Fail(string message) => new()
        {
            Success = false,
            Message = message
        };
    }

    public class ConfirmationQuickItem
    {
        public string Name { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public int Quantity { get; set; } = 1;
        public bool IsWingFlavor { get; set; }
    }
}
