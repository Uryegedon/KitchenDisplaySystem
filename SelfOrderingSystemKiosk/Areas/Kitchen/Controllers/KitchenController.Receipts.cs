using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using SelfOrderingSystemKiosk.Areas.Customer.Models;
using SelfOrderingSystemKiosk.Areas.Kitchen.Models;
using SelfOrderingSystemKiosk.Services;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SelfOrderingSystemKiosk.Areas.Kitchen.Controllers
{
    public partial class KitchenController
    {
        private async Task<List<SessionReceiptViewModel>> BuildReceiptsAsync(IEnumerable<Order> orders)
        {
            var receipts = new List<SessionReceiptViewModel>();
            var coveredOrderIds = new HashSet<string>();
            var tableOrdersCache = new Dictionary<string, List<Order>>(StringComparer.OrdinalIgnoreCase);

            foreach (var order in orders
                .Where(o => !string.Equals(o.Status, "Canceled", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(o => o.OrderDate))
            {
                if (!string.IsNullOrEmpty(order.Id) && coveredOrderIds.Contains(order.Id))
                    continue;

                List<Order>? tableOrders = null;
                if (IsDineInTableOrder(order))
                {
                    var tableKey = NormalizeTableKey(order.TableNumber, order.BranchId);
                    if (!tableOrdersCache.TryGetValue(tableKey, out tableOrders))
                    {
                        tableOrders = await _orderService.GetOrdersByTableAsync(order.TableNumber, order.BranchId);
                        tableOrdersCache[tableKey] = tableOrders;
                    }
                }

                var receipt = await BuildReceiptViewModelAsync(order, tableOrdersOverride: tableOrders);
                foreach (var included in receipt.Orders)
                {
                    if (!string.IsNullOrEmpty(included.Id))
                        coveredOrderIds.Add(included.Id);
                }

                receipts.Add(receipt);
            }

            return receipts.OrderByDescending(r => r.SessionStartUtc).ToList();
        }

        private List<TableOverviewViewModel> BuildTableOverviews(
            List<SessionReceiptViewModel> receipts,
            List<TableOrderingSession> tableSessions,
            List<SelfOrderingSystemKiosk.Models.RestaurantTable> knownTables,
            bool showArchived)
        {
            var byKey = new Dictionary<string, TableOverviewViewModel>(StringComparer.OrdinalIgnoreCase);

            foreach (var tableNumber in DiningTableNumbers)
            {
                var key = NormalizeTableKey(tableNumber);
                byKey[key] = new TableOverviewViewModel
                {
                    TableNumber = tableNumber,
                    LocationLabel = BuildLocationLabel(string.Empty, tableNumber),
                    CanManageOrdering = true
                };
            }

            foreach (var defaultTable in new[] { DefaultKioskTableNumber, DefaultTakeOutTableNumber })
            {
                var key = NormalizeTableKey(defaultTable);
                byKey[key] = new TableOverviewViewModel
                {
                    TableNumber = defaultTable,
                    LocationLabel = BuildLocationLabel(string.Empty, defaultTable),
                    CanManageOrdering = false
                };
            }

            foreach (var knownTable in knownTables)
            {
                if (string.IsNullOrWhiteSpace(knownTable.TableNumber))
                    continue;

                var key = NormalizeTableKey(knownTable.TableNumber, knownTable.BranchId);
                if (!byKey.TryGetValue(key, out var table))
                {
                    table = new TableOverviewViewModel
                    {
                        TableNumber = knownTable.TableNumber,
                        CanManageOrdering = IsDiningTable(knownTable.TableNumber)
                    };
                    byKey[key] = table;
                }

                table.TableNumber = knownTable.TableNumber;
                table.BranchId = knownTable.BranchId;
                table.Floor = knownTable.Floor;
                table.LocationLabel = BuildLocationLabel(knownTable.Floor, knownTable.TableNumber);
                table.LastActivityUtc = knownTable.UpdatedAtUtc;
            }

            foreach (var session in tableSessions)
            {
                if (string.IsNullOrWhiteSpace(session.TableNumber))
                    continue;

                var key = NormalizeTableKey(session.TableNumber, session.BranchId);
                if (!byKey.TryGetValue(key, out var table))
                {
                    table = new TableOverviewViewModel
                    {
                        TableNumber = session.TableNumber,
                        CanManageOrdering = IsDiningTable(session.TableNumber)
                    };
                    byKey[key] = table;
                }

                table.IsOccupied = session.IsOrderingOpen;
                table.BranchId = session.BranchId;
                table.OrderingSession = session;
                table.LastActivityUtc = session.UpdatedAtUtc;
                if (string.IsNullOrWhiteSpace(table.LocationLabel))
                {
                    table.LocationLabel = BuildLocationLabel(table.Floor, session.TableNumber);
                }
            }

            foreach (var receipt in receipts)
            {
                if (!showArchived && ShouldHideClosedReceipt(receipt))
                    continue;

            var key = GetReceiptServiceTableKey(receipt);
                if (!byKey.TryGetValue(key, out var table))
                {
                    table = new TableOverviewViewModel
                    {
                        TableNumber = receipt.TableNumber,
                        BranchId = receipt.AnchorOrder?.BranchId ?? string.Empty,
                        Floor = receipt.Floor,
                        LocationLabel = receipt.LocationLabel,
                        CanManageOrdering = IsDiningTable(receipt.TableNumber)
                    };
                    byKey[key] = table;
                }

                var nextReceiptDate = receipt.Orders.Select(o => o.OrderDate).DefaultIfEmpty().Max();
                table.Receipts.Add(receipt);

                var currentReceiptDate = table.Receipt?.Orders.Select(o => o.OrderDate).DefaultIfEmpty().Max() ?? DateTime.MinValue;
                if (table.Receipt == null || nextReceiptDate >= currentReceiptDate)
                    table.Receipt = receipt;

                if (string.IsNullOrWhiteSpace(table.Floor))
                    table.Floor = receipt.Floor;
                if (string.IsNullOrWhiteSpace(table.BranchId))
                    table.BranchId = receipt.AnchorOrder?.BranchId ?? string.Empty;
                if (string.IsNullOrWhiteSpace(table.LocationLabel))
                    table.LocationLabel = receipt.LocationLabel;
                table.LastActivityUtc = new[] { table.LastActivityUtc ?? DateTime.MinValue, nextReceiptDate }.Max();
            }

            foreach (var table in byKey.Values)
            {
                table.Receipts = table.Receipts
                    .OrderByDescending(r => r.Orders.Select(o => o.OrderDate).DefaultIfEmpty(r.SessionStartUtc).Max())
                    .ToList();
            }

            var hasBranchScopedTables = byKey.Values.Any(t => !string.IsNullOrWhiteSpace(t.BranchId));
            return byKey.Values
                .Where(t => (IsDiningTable(t.TableNumber) && (!hasBranchScopedTables || !string.IsNullOrWhiteSpace(t.BranchId) || t.IsOccupied || t.HasBill))
                    || IsDefaultServiceTable(t.TableNumber)
                    || t.IsOccupied
                    || t.HasBill
                    || t.OrderingSession != null)
                .OrderBy(t => GetTableSortValue(t.TableNumber))
                .ThenBy(t => t.LocationLabel, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdatePaymentStatus(string id, string paymentStatus, string? returnUrl = null)
        {
            if (string.IsNullOrWhiteSpace(id))
                return RedirectToAction("Receipts");

            await _orderService.ExpirePendingOrdersAsync();

            var anchorOrder = await _orderService.GetByIdAsync(id);
            if (anchorOrder == null)
                return RedirectToAction("Receipts");
            if (!CanAccessOrder(anchorOrder))
                return Forbid();

            if (string.Equals(anchorOrder.Status, "Canceled", StringComparison.OrdinalIgnoreCase))
            {
                TempData["ErrorMessage"] = "This order was canceled because it was not started within 24 hours.";
                return RedirectToAction("Receipt", new { id, returnUrl = GetSafeReturnUrl(returnUrl, anchorOrder, true) });
            }

            if (!string.Equals(paymentStatus, "Paid", StringComparison.OrdinalIgnoreCase))
                return RedirectToAction("Receipt", new { id, returnUrl = GetSafeReturnUrl(returnUrl, anchorOrder, true) });

            var receipt = await BuildReceiptViewModelAsync(anchorOrder);
            await _orderService.UpdatePaymentStatusAsync(receipt.Orders.Select(o => o.Id), "Paid");
            foreach (var order in receipt.Orders)
                order.PaymentStatus = "Paid";
            await _realtime.NotifyOrdersChangedAsync(receipt.Orders, "payment-status-changed");

            if (!receipt.IsTableSession)
            {
                await _orderService.ArchiveBillsAsync(receipt.Orders.Select(o => o.Id));
                await _realtime.NotifyOrdersChangedAsync(receipt.Orders, "bill-archived");
                return RedirectToAction("Receipts");
            }

            if (ShouldHideClosedReceipt(receipt))
                return RedirectToAction("Receipts");

            return RedirectToAction("Receipt", new { id, returnUrl = GetSafeReturnUrl(returnUrl, anchorOrder, true) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EndSession(string id, string? returnUrl = null)
        {
            if (string.IsNullOrWhiteSpace(id))
                return RedirectToAction("Receipts");

            await _orderService.ExpirePendingOrdersAsync();

            var anchorOrder = await _orderService.GetByIdAsync(id);
            if (anchorOrder == null)
                return RedirectToAction("Receipts");
            if (!CanAccessOrder(anchorOrder))
                return Forbid();

            if (string.Equals(anchorOrder.Status, "Canceled", StringComparison.OrdinalIgnoreCase))
            {
                TempData["ErrorMessage"] = "This order was canceled because it was not started within 24 hours.";
                return RedirectToAction("Receipt", new { id, returnUrl = GetSafeReturnUrl(returnUrl, anchorOrder, true) });
            }

            var receipt = await BuildReceiptViewModelAsync(anchorOrder);
            if (!receipt.IsTableSession)
                return RedirectToAction("Receipt", new { id, returnUrl = GetSafeReturnUrl(returnUrl, anchorOrder, true) });

            var isPaid = receipt.Orders.All(o => string.Equals(o.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase));
            if (!isPaid)
            {
                TempData["ErrorMessage"] = "This table session can only be ended after the bill is marked as paid.";
                return RedirectToAction("Receipt", new { id, returnUrl = GetSafeReturnUrl(returnUrl, anchorOrder, true) });
            }

            await _orderService.ArchiveBillsAsync(receipt.Orders.Select(o => o.Id));
            if (!string.IsNullOrWhiteSpace(anchorOrder.TableNumber))
                await _tableOrderingSessions.CloseOrderingAsync(
                    anchorOrder.TableNumber,
                    await ResolveTableSessionBranchIdAsync(anchorOrder));
            await _realtime.NotifyOrdersChangedAsync(receipt.Orders, "session-ended");

            return RedirectToAction("Receipts");
        }

        private async Task<SessionReceiptViewModel> BuildReceiptViewModelAsync(
            Order anchorOrder,
            bool includeTableSession = true,
            List<Order>? tableOrdersOverride = null)
        {
            var orders = new List<Order> { anchorOrder };
            var isAnchorCanceled = string.Equals(anchorOrder.Status, "Canceled", StringComparison.OrdinalIgnoreCase);
            var isTableSession = includeTableSession && !isAnchorCanceled && IsDineInTableOrder(anchorOrder);

            if (isTableSession)
            {
                var tableOrders = tableOrdersOverride
                    ?? await _orderService.GetOrdersByTableAsync(anchorOrder.TableNumber, anchorOrder.BranchId);
                orders = GetOrdersInSameSession(tableOrders, anchorOrder);
            }

            var sessionStart = orders
                .Where(o => string.Equals(o.OrderType, "Unlimited", StringComparison.OrdinalIgnoreCase))
                .Select(GetOrderSessionStartUtc)
                .Where(start => start.HasValue)
                .Select(start => start!.Value)
                .DefaultIfEmpty()
                .Min();
            var hasSessionStarted = sessionStart != default;
            var displayStart = hasSessionStarted ? sessionStart : orders.Min(o => o.OrderDate);
            var displayTableNumber = GetDisplayTableNumber(anchorOrder);
            var locationLabel = BuildLocationLabel(anchorOrder.Floor, displayTableNumber);

            return new SessionReceiptViewModel
            {
                Orders = orders.OrderBy(o => o.OrderDate).ToList(),
                AnchorOrder = anchorOrder,
                SessionStartUtc = displayStart,
                SessionEndUtc = hasSessionStarted ? sessionStart.AddHours(2) : displayStart,
                HasSessionStarted = hasSessionStarted,
                TableNumber = displayTableNumber,
                BranchId = anchorOrder.BranchId ?? string.Empty,
                Floor = anchorOrder.Floor,
                LocationLabel = locationLabel,
                IsTableSession = isTableSession
            };
        }

        private static bool IsDineInTableOrder(Order order)
        {
            return !string.IsNullOrWhiteSpace(order.TableNumber)
                && !IsDefaultServiceTable(order.TableNumber)
                && string.Equals(order.DiningType, "DineIn", StringComparison.OrdinalIgnoreCase);
        }

        private static List<Order> GetOrdersInSameSession(List<Order> tableOrders, Order anchorOrder)
        {
            var includeArchived = anchorOrder.BillArchived;
            var ordered = tableOrders
                .Where(o => !string.Equals(o.Status, "Canceled", StringComparison.OrdinalIgnoreCase)
                    && o.BillArchived == includeArchived)
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
                    .LastOrDefault(start => ToUtc(anchorOrder.OrderDate) >= start && ToUtc(anchorOrder.OrderDate) < start.AddHours(2));

                if (!anchorSessionStart.HasValue || anchorSessionStart.Value == default)
                {
                    anchorSessionStart = startedSessions
                        .FirstOrDefault(start => ToUtc(anchorOrder.OrderDate) <= start);
                }
            }

            if (!anchorSessionStart.HasValue || anchorSessionStart.Value == default)
                return ordered;

            var sessionStart = anchorSessionStart.Value;
            var sessionEnd = sessionStart.AddHours(2);
            var previousSessionEnd = startedSessions
                .Where(start => start < sessionStart)
                .Select(start => start.AddHours(2))
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

        private static bool ShouldHideClosedReceipt(SessionReceiptViewModel receipt)
        {
            var latestOrderUtc = receipt.Orders
                .Select(o => ToUtc(o.OrderDate))
                .DefaultIfEmpty(ToUtc(receipt.SessionStartUtc))
                .Max();
            var isStaleUnstartedBill = !receipt.HasSessionStarted
                && latestOrderUtc <= DateTime.UtcNow.Subtract(TimeSpan.FromHours(24));

            return receipt.Orders.Any()
                && (receipt.Orders.All(o => o.BillArchived)
                    || (!receipt.IsTableSession && receipt.Orders.All(o => string.Equals(o.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase)))
                    || IsReceiptExpired(receipt)
                    || isStaleUnstartedBill);
        }

        private static bool IsReceiptExpired(SessionReceiptViewModel receipt)
        {
            return receipt.HasSessionStarted && ToUtc(receipt.SessionEndUtc) <= DateTime.UtcNow;
        }

        private static string BuildLocationLabel(string floor, string table)
        {
            if (string.IsNullOrWhiteSpace(table) || string.Equals(table, DefaultKioskTableNumber, StringComparison.OrdinalIgnoreCase) || string.Equals(table, "0", StringComparison.OrdinalIgnoreCase))
                return "Kiosk Counter";

            if (string.Equals(table, DefaultTakeOutTableNumber, StringComparison.OrdinalIgnoreCase))
                return "Take Out";

            return string.IsNullOrWhiteSpace(floor)
                ? $"Table {table}"
                : $"Floor {floor} - Table {table}";
        }

        private static string GetDisplayTableNumber(Order order)
        {
            if (!string.IsNullOrWhiteSpace(order.TableNumber))
                return order.TableNumber;

            return string.Equals(order.DiningType, "TakeOut", StringComparison.OrdinalIgnoreCase)
                ? DefaultTakeOutTableNumber
                : DefaultKioskTableNumber;
        }

        private async Task<string?> ResolveTableSessionBranchIdAsync(Order order)
        {
            if (!string.IsNullOrWhiteSpace(order.BranchId))
                return order.BranchId;

            var kitchenBranchId = GetKitchenBranchFilter();
            if (!string.IsNullOrWhiteSpace(kitchenBranchId))
                return kitchenBranchId;

            if (string.IsNullOrWhiteSpace(order.TableNumber))
                return null;

            var registeredTable = await _tableRegistry.GetByTableNumberAsync(order.TableNumber);
            return registeredTable?.BranchId;
        }

        private static string GetReceiptServiceTableKey(SessionReceiptViewModel receipt)
        {
            if (!string.IsNullOrWhiteSpace(receipt.TableNumber))
                return NormalizeTableKey(receipt.TableNumber, receipt.AnchorOrder?.BranchId);

            var diningType = receipt.AnchorOrder?.DiningType;
            return string.Equals(diningType, "TakeOut", StringComparison.OrdinalIgnoreCase)
                ? NormalizeTableKey(DefaultTakeOutTableNumber, receipt.AnchorOrder?.BranchId)
                : NormalizeTableKey(DefaultKioskTableNumber, receipt.AnchorOrder?.BranchId);
        }

        private static string NormalizeTableKey(string table)
        {
            if (string.IsNullOrWhiteSpace(table))
                return string.Empty;

            var normalized = table.Trim().ToUpperInvariant();
            var tableKey = normalized == "0" ? DefaultKioskTableNumber : normalized;
            return tableKey;
        }

        private static string NormalizeTableKey(string table, string? branchId)
        {
            var tableKey = NormalizeTableKey(table);
            return string.IsNullOrWhiteSpace(branchId)
                ? tableKey
                : $"{branchId.Trim().ToUpperInvariant()}:{tableKey}";
        }

        private static bool IsDiningTable(string? table)
        {
            return !string.IsNullOrWhiteSpace(table)
                && DiningTableNumbers.Contains(table.Trim(), StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsDefaultServiceTable(string? table)
        {
            return !string.IsNullOrWhiteSpace(table)
                && (string.Equals(table.Trim(), DefaultKioskTableNumber, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(table.Trim(), DefaultTakeOutTableNumber, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(table.Trim(), "0", StringComparison.OrdinalIgnoreCase));
        }

        private static int GetTableSortValue(string? table)
        {
            if (string.Equals(table, DefaultKioskTableNumber, StringComparison.OrdinalIgnoreCase) || string.Equals(table, "0", StringComparison.OrdinalIgnoreCase))
                return 100;
            if (string.Equals(table, DefaultTakeOutTableNumber, StringComparison.OrdinalIgnoreCase))
                return 101;
            if (!string.IsNullOrWhiteSpace(table) && int.TryParse(table.Trim(), out var value))
                return value;

            return int.MaxValue;
        }

        private static int GetOrderPersonCount(Order order)
        {
            if (order?.PersonCount is > 0)
                return order.PersonCount.Value;

            const decimal pricePerHead = RestaurantPricing.UnlimitedPricePerHead;
            if (order != null &&
                string.Equals(order.OrderType, "Unlimited", StringComparison.OrdinalIgnoreCase) &&
                order.Subtotal >= pricePerHead)
            {
                return Math.Max(1, (int)Math.Floor(order.Subtotal / pricePerHead));
            }

            return 0;
        }

        private async Task RebuildSharedTableSessionFromOrdersAsync(string tableNumber, string? branchId = null)
        {
            if (string.IsNullOrWhiteSpace(tableNumber))
                return;

            var openUnlimitedOrders = (await _orderService.GetOrdersByTableAsync(tableNumber, branchId))
                .Where(o => !o.BillArchived
                    && !string.Equals(o.Status, "Canceled", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(o.OrderType, "Unlimited", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!openUnlimitedOrders.Any())
            {
                await _tableOrderingSessions.ClearAsync(tableNumber, branchId);
                return;
            }

            var personCount = openUnlimitedOrders
                .Select(GetOrderPersonCount)
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

        private async Task<HashSet<string>> ExtractUnlimitedWingFlavorsAsync(IEnumerable<OrderItem> orderItems)
        {
            var availableItems = await _menuItems.GetAvailableAsync() ?? new List<SelfOrderingSystemKiosk.Models.MenuItem>();
            var byName = availableItems
                .Where(i => !string.IsNullOrWhiteSpace(i.Item))
                .GroupBy(i => i.Item.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var wingFlavors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in orderItems ?? Enumerable.Empty<OrderItem>())
            {
                var lookupName = item.ItemName?.Trim();
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

        private static bool HasPublicReceiptAccess(Order order, string? accessToken)
        {
            if (string.IsNullOrWhiteSpace(order.PublicAccessToken))
                return false;
            if (string.IsNullOrWhiteSpace(accessToken))
                return false;

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(order.PublicAccessToken),
                Encoding.UTF8.GetBytes(accessToken.Trim()));
        }

        private string GetSafeReturnUrl(string? returnUrl, Order? anchorOrder = null, bool isSignedIn = true)
        {
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                return returnUrl;

            if (!isSignedIn && !string.IsNullOrWhiteSpace(anchorOrder?.OrderNumber))
            {
                return Url.Action("Confirmation", "Kiosk", new { area = "Customer", orderNumber = anchorOrder.OrderNumber })
                    ?? "/Customer/Kiosk";
            }

            return Url.Action("Receipts", "Kitchen", new { area = "Kitchen" }) ?? "/Kitchen/Kitchen/Receipts";
        }

        private async Task<DateTime?> GetSessionStartForStaffStartAsync(Order order)
        {
            if (order.SessionStartedAtUtc.HasValue)
                return ToUtc(order.SessionStartedAtUtc.Value);

            if (!string.Equals(order.OrderType, "Unlimited", StringComparison.OrdinalIgnoreCase))
                return null;

            if (!string.IsNullOrWhiteSpace(order.TableNumber) &&
                string.Equals(order.DiningType, "DineIn", StringComparison.OrdinalIgnoreCase))
            {
                var tableOrders = await _orderService.GetOrdersByTableAsync(order.TableNumber, order.BranchId);
                var activeSessionStart = tableOrders
                    .Where(o => !o.BillArchived)
                    .Select(GetOrderSessionStartUtc)
                    .Where(start => start.HasValue && DateTime.UtcNow < start.Value.AddHours(2))
                    .Select(start => start!.Value)
                    .OrderByDescending(start => start)
                    .FirstOrDefault();

                if (activeSessionStart != default)
                    return activeSessionStart;
            }

            return DateTime.UtcNow;
        }
    }
}
