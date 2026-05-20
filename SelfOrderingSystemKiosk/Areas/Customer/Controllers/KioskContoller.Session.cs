using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
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
        private void SetKioskChannelDefaults()
        {
            HttpContext.Session.SetString(SessionOrderChannel, OrderChannelKiosk);
            HttpContext.Session.Remove(SessionServiceTable);
            HttpContext.Session.Remove(SessionServiceFloor);
            Response.Cookies.Delete(CookieServiceTable);
            Response.Cookies.Delete(CookieServiceFloor);
        }

        private bool HasRememberedQrTableContext()
        {
            if (!string.IsNullOrWhiteSpace(HttpContext.Session.GetString(SessionServiceTable)))
                return true;

            return Request.Cookies.TryGetValue(CookieServiceTable, out var table) &&
                !string.IsNullOrWhiteSpace(table);
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
                var branchId = await ResolveOrderBranchIdAsync(table);
                if (!string.IsNullOrWhiteSpace(branchId))
                    HttpContext.Session.SetString(SessionServiceBranch, branchId);

                if (!string.IsNullOrWhiteSpace(table))
                {
                    await ResetEndedTableSessionPersonCountAsync(table, branchId);
                    await CheckTableOrderingGateAsync(table, branchId);
                    await RestoreSharedTablePersonCountAsync(table, branchId);
                }

                var tableSession = !string.IsNullOrWhiteSpace(table)
                    ? await _tableOrderingSessions.GetAsync(table, branchId)
                    : null;
                ViewBag.TableOrderingAvailable = true;
                ViewBag.SharedWingFlavors = tableSession?.WingFlavors ?? new List<string>();
                ViewBag.PersonCount = GetSessionInt(SessionPersonCount);
                ApplyOrderingWindowToViewBag();
                ViewBag.ServiceTable = table;
                ViewBag.ServiceFloor = floor;
                ViewBag.LocationLabel = BuildLocationLabel(floor, table);
                return;
            }

            ViewBag.SharedWingFlavors = new List<string>();
            ViewBag.TableOrderingAvailable = true;
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

        private void SaveOrderingCookies(string table, string floor, int? personCount, string? branchId = null)
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

            if (!string.IsNullOrWhiteSpace(branchId))
                Response.Cookies.Append(CookieServiceBranch, branchId, options);
            else if (string.IsNullOrWhiteSpace(table))
                Response.Cookies.Delete(CookieServiceBranch);

            if (personCount.HasValue && personCount.Value > 0)
                Response.Cookies.Append(CookiePersonCount, personCount.Value.ToString(), options);
        }

        private async Task ClearRememberedPersonCountAsync(string tableNumber = null, string? branchId = null)
        {
            _skipRememberedPersonCountRestore = true;
            HttpContext.Session.Remove(SessionPersonCount);
            HttpContext.Session.Remove(SessionFirstOrderTime);
            Response.Cookies.Delete(CookiePersonCount);
            if (!string.IsNullOrWhiteSpace(tableNumber))
            {
                await _tableOrderingSessions.ClearAsync(tableNumber, await ResolveTableBranchContextAsync(tableNumber, branchId));
                HttpContext.Session.SetString(SessionEndedTableReset, tableNumber);
            }
        }

        private async Task ResetEndedTableSessionPersonCountAsync(string tableNumber, string? branchId = null)
        {
            if (string.IsNullOrWhiteSpace(tableNumber))
                return;

            if (await HasEndedTableSessionReadyForNewSessionAsync(tableNumber, branchId))
                await ClearRememberedPersonCountAsync(tableNumber, branchId);
        }

        private async Task RestoreSharedTablePersonCountAsync(string tableNumber, string? branchId = null)
        {
            if (string.IsNullOrWhiteSpace(tableNumber))
                return;

            if (GetSessionInt(SessionPersonCount) is > 0)
                return;

            branchId = await ResolveTableBranchContextAsync(tableNumber, branchId);
            var tableSession = await _tableOrderingSessions.GetAsync(tableNumber, branchId);
            if (tableSession?.PersonCount > 0)
            {
                HttpContext.Session.SetInt32(SessionPersonCount, tableSession.PersonCount);
                return;
            }

            var tableOrders = await _orderService.GetOrdersByTableAsync(tableNumber, branchId);
            var sharedCount = tableOrders
                .Where(o => !o.BillArchived && IsUnlimitedOrder(o))
                .OrderByDescending(o => o.OrderDate)
                .Select(GetOrderPersonCount)
                .FirstOrDefault(count => count.HasValue && count.Value > 0);

            if (sharedCount.HasValue)
                HttpContext.Session.SetInt32(SessionPersonCount, sharedCount.Value);
        }

        private async Task<int> GetSharedTablePersonCountAsync(string tableNumber, string? branchId = null)
        {
            if (string.IsNullOrWhiteSpace(tableNumber))
                return 0;

            branchId = await ResolveTableBranchContextAsync(tableNumber, branchId);
            var tableSession = await _tableOrderingSessions.GetAsync(tableNumber, branchId);
            if (tableSession?.PersonCount > 0)
                return tableSession.PersonCount;

            var tableOrders = await _orderService.GetOrdersByTableAsync(tableNumber, branchId);
            return tableOrders
                .Where(o => !o.BillArchived && IsUnlimitedOrder(o))
                .Select(GetOrderPersonCount)
                .Where(count => count.HasValue && count.Value > 0)
                .Select(count => count!.Value)
                .DefaultIfEmpty(0)
                .Max();
        }

        private async Task SeedSharedTableSessionFromOrdersAsync(string tableNumber, string? branchId = null)
        {
            if (string.IsNullOrWhiteSpace(tableNumber))
                return;

            branchId = await ResolveTableBranchContextAsync(tableNumber, branchId);
            if (await _tableOrderingSessions.GetAsync(tableNumber, branchId) != null)
                return;

            var tableOrders = await _orderService.GetOrdersByTableAsync(tableNumber, branchId);
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

            await _tableOrderingSessions.SeedFromExistingOrdersAsync(tableNumber, personCount, wingFlavors, branchId);
        }

        private async Task RebuildSharedTableSessionFromOrdersAsync(string tableNumber, string? branchId = null)
        {
            if (string.IsNullOrWhiteSpace(tableNumber))
                return;

            branchId = await ResolveTableBranchContextAsync(tableNumber, branchId);
            var tableOrders = await _orderService.GetOrdersByTableAsync(tableNumber, branchId);
            var openUnlimitedOrders = tableOrders
                .Where(o => !o.BillArchived && IsUnlimitedOrder(o))
                .ToList();
            if (!openUnlimitedOrders.Any())
            {
                await _tableOrderingSessions.ClearAsync(tableNumber, branchId);
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

            await _tableOrderingSessions.ReplaceFromExistingOrdersAsync(
                tableNumber,
                personCount,
                wingFlavors,
                branchId);
        }

        private static int? GetOrderPersonCount(Order order)
        {
            if (order?.PersonCount is > 0)
                return order.PersonCount.Value;

            const decimal pricePerHead = RestaurantPricing.UnlimitedPricePerHead;
            if (order != null && IsUnlimitedOrder(order) && order.Subtotal >= pricePerHead)
                return Math.Max(1, (int)Math.Floor(order.Subtotal / pricePerHead));

            return null;
        }

        private async Task<bool> HasEndedTableSessionReadyForNewSessionAsync(string tableNumber, string? branchId = null)
        {
            if (string.IsNullOrWhiteSpace(tableNumber))
                return false;

            var tableOrders = await _orderService.GetOrdersByTableAsync(tableNumber, await ResolveTableBranchContextAsync(tableNumber, branchId));
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

            if (string.IsNullOrEmpty(HttpContext.Session.GetString(SessionServiceBranch)) &&
                Request.Cookies.TryGetValue(CookieServiceBranch, out var branchId) &&
                !string.IsNullOrWhiteSpace(branchId))
            {
                HttpContext.Session.SetString(SessionServiceBranch, branchId);
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

        private async Task<TableOrderingGateResult> CheckTableOrderingGateAsync(string tableNumber, string? branchId = null)
        {
            if (string.IsNullOrWhiteSpace(tableNumber))
                return TableOrderingGateResult.Allowed();

            var tableOrders = await _orderService.GetOrdersByTableAsync(tableNumber, await ResolveTableBranchContextAsync(tableNumber, branchId));
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
                    $"The previous Table {tableNumber} session ended at {AppClock.ToLocal(sessionEnd):h:mm tt} and the bill is still pending. Please ask staff to mark the bill paid before starting a new order.",
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

            var tableOrders = await _orderService.GetOrdersByTableAsync(order.TableNumber, order.BranchId);
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
            var tableSession = await _tableOrderingSessions.GetAsync(order.TableNumber, order.BranchId);
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
            return AppClock.ToUtc(value);
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
                SetQrTableContext(order.TableNumber, order.Floor, order.BranchId);
                HttpContext.Session.SetString(SessionDiningType, order.DiningType ?? "DineIn");
            }
        }

        private void SetQrTableContext(string table, string? floor, string? branchId = null)
        {
            var previousTable = HttpContext.Session.GetString(SessionServiceTable);
            var previousBranchId = HttpContext.Session.GetString(SessionServiceBranch);
            var normalizedTable = table.Trim();
            var normalizedBranchId = branchId?.Trim();
            var tableChanged = !string.IsNullOrWhiteSpace(previousTable) &&
                !string.Equals(previousTable, normalizedTable, StringComparison.OrdinalIgnoreCase);
            var branchChanged = !string.Equals(
                previousBranchId ?? string.Empty,
                normalizedBranchId ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
            var timerHasNoTableContext = string.IsNullOrWhiteSpace(previousTable) &&
                !string.IsNullOrWhiteSpace(HttpContext.Session.GetString(SessionFirstOrderTime));

            HttpContext.Session.SetString(SessionOrderChannel, OrderChannelQr);
            HttpContext.Session.SetString(SessionServiceTable, normalizedTable);
            if (!string.IsNullOrWhiteSpace(floor))
                HttpContext.Session.SetString(SessionServiceFloor, floor.Trim());
            else
                HttpContext.Session.Remove(SessionServiceFloor);

            if (!string.IsNullOrWhiteSpace(normalizedBranchId))
                HttpContext.Session.SetString(SessionServiceBranch, normalizedBranchId);

            if (tableChanged || branchChanged || timerHasNoTableContext)
            {
                HttpContext.Session.Remove(SessionFirstOrderTime);
                HttpContext.Session.Remove(SessionPersonCount);
                HttpContext.Session.Remove(SessionEndedTableReset);
                Response.Cookies.Delete(CookiePersonCount);
            }
        }
    }
}
