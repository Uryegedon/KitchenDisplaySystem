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
        // GET: Kitchen/Kitchen/Index
        [HttpGet]
        public async Task<IActionResult> Index([FromQuery] string? dateFilter = "all")
        {
            if (!TryGetKitchenBranchFilter(out var kitchenBranchId))
                return Forbid();

            var orders = await _orderService.GetOrdersForKitchenAsync(dateFilter, kitchenBranchId);
            ViewBag.UnlimitedRefills = await _unlimitedRefills.GetActiveForKitchenAsync(kitchenBranchId);
            ViewBag.DateFilter = dateFilter;
            return View(orders.OrderByDescending(o => o.OrderDate).ToList());
        }

        // Optional: view single order
        public async Task<IActionResult> Details(string id)
        {
            if (string.IsNullOrEmpty(id))
                return RedirectToAction("Index");

            var order = await _orderService.GetByIdAsync(id);
            if (order == null)
                return RedirectToAction("Index");
            if (!CanAccessOrder(order))
                return Forbid();

            return View(order);
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> Receipt(string? id = null, string? orderNumber = null, string? accessToken = null, string? returnUrl = null)
        {
            await _orderService.ExpirePendingOrdersAsync();

            Order? anchorOrder = null;
            var isSignedIn = User?.Identity?.IsAuthenticated == true;

            if (isSignedIn && !string.IsNullOrWhiteSpace(id))
                anchorOrder = await _orderService.GetByIdAsync(id);
            else if (!string.IsNullOrWhiteSpace(orderNumber))
            {
                if (isSignedIn && !TryGetKitchenBranchFilter(out _))
                    return Forbid();

                anchorOrder = await _orderService.GetByOrderNumberAsync(
                    orderNumber,
                    isSignedIn ? GetKitchenBranchFilter() : null,
                    accessToken);
            }

            if (anchorOrder == null)
            {
                if (isSignedIn)
                    return RedirectToAction("Index");

                return RedirectToAction("Index", "Kiosk", new { area = "Customer" });
            }
            if (isSignedIn && !CanAccessOrder(anchorOrder))
            {
                return Forbid();
            }
            if (!isSignedIn && !HasPublicReceiptAccess(anchorOrder, accessToken))
                return RedirectToAction("Index", "Kiosk", new { area = "Customer" });

            ViewBag.ReturnUrl = GetSafeReturnUrl(returnUrl, anchorOrder, isSignedIn);
            ViewBag.CanManagePayment = isSignedIn
                && !string.IsNullOrWhiteSpace(id)
                && string.IsNullOrWhiteSpace(orderNumber);
            var canViewTableSession = isSignedIn
                && !string.IsNullOrWhiteSpace(id)
                && string.IsNullOrWhiteSpace(orderNumber);
            if (!isSignedIn && HasPublicReceiptAccess(anchorOrder, accessToken))
                canViewTableSession = true;
            return View(await BuildReceiptViewModelAsync(anchorOrder, canViewTableSession));
        }

        [HttpGet]
        public async Task<IActionResult> Receipts([FromQuery] string? dateFilter = "all", [FromQuery] bool showArchived = false)
        {
            if (!TryGetKitchenBranchFilter(out var kitchenBranchId))
                return Forbid();

            var orders = await _orderService.GetOrdersForKitchenAsync(dateFilter, kitchenBranchId);
            var receipts = await BuildReceiptsAsync(orders);
            var tableSessions = await _tableOrderingSessions.GetAllAsync();
            var knownTables = await _tableRegistry.GetAllAsync();
            if (!string.IsNullOrWhiteSpace(kitchenBranchId))
            {
                tableSessions = tableSessions
                    .Where(s => string.Equals(s.BranchId, kitchenBranchId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                knownTables = knownTables
                    .Where(t => string.Equals(t.BranchId, kitchenBranchId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            var tables = BuildTableOverviews(receipts, tableSessions, knownTables, showArchived);

            ViewBag.DateFilter = dateFilter;
            ViewBag.ShowArchived = showArchived;
            return View(tables);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ServeUnlimitedRefill(string id)
        {
            if (!TryGetKitchenBranchFilter(out _))
                return Forbid();

            var refill = await _unlimitedRefills.GetByIdAsync(id);
            if (refill == null)
            {
                TempData["ErrorMessage"] = "Refill alert was not found.";
                return RedirectToAction("Index");
            }

            if (!CanAccessRefill(refill))
                return Forbid();

            var markedServed = await _unlimitedRefills.MarkServedIfNewAsync(id);
            if (!markedServed)
            {
                TempData["ErrorMessage"] = "Refill was already served. Please refresh the kitchen board.";
                return RedirectToAction("Index");
            }

            foreach (var item in refill.Items ?? new List<OrderItem>())
            {
                if (string.IsNullOrWhiteSpace(item.ItemName) || item.Quantity <= 0)
                    continue;

                try
                {
                    await _menuItems.DecrementStockAsync(item.ItemName, item.Quantity, "Unlimited Refill", "UnlimitedRefill", refill.Id, refill.BranchId);
                    _logger.LogInformation("Deducted refill ingredients for {Item} by {Qty}", item.ItemName, item.Quantity);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error deducting refill ingredients for {Item}", item.ItemName);
                }
            }

            TempData["SuccessMessage"] = "Unlimited refill marked as served.";
            await _realtime.NotifyKitchenChangedAsync(refill.BranchId, "refill-served");
            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleTable(string table, string? branchId = null, bool occupied = false, string? dateFilter = "all", bool showArchived = false)
        {
            if (!TryGetKitchenBranchFilter(out _))
                return Forbid();

            if (string.IsNullOrWhiteSpace(table))
            {
                TempData["ErrorMessage"] = "Choose a table to update.";
                return RedirectToAction("Receipts", new { dateFilter, showArchived });
            }

            table = table.Trim();
            if (!IsDiningTable(table))
            {
                TempData["ErrorMessage"] = $"Table {table} is not part of the 7 dine-in tables.";
                return RedirectToAction("Receipts", new { dateFilter, showArchived });
            }

            if (table.Length > 32)
                table = table[..32];

            var effectiveBranchId = await GetEffectiveKitchenBranchIdAsync(table, branchId);
            if (string.IsNullOrWhiteSpace(effectiveBranchId))
            {
                TempData["ErrorMessage"] = "Choose a branch-specific table before changing availability.";
                return RedirectToAction("Receipts", new { dateFilter, showArchived });
            }

            if (occupied)
            {
                await _tableRegistry.UpsertAsync(table, branchId: effectiveBranchId);
                await _tableOrderingSessions.OpenOrderingAsync(table, effectiveBranchId);
                TempData["SuccessMessage"] = $"Table {table} is now occupied and QR ordering is enabled.";
            }
            else
            {
                await _tableOrderingSessions.CloseOrderingAsync(table, effectiveBranchId);
                TempData["SuccessMessage"] = $"Table {table} is now available and QR ordering is disabled.";
            }

            await _realtime.NotifyKitchenChangedAsync(effectiveBranchId, "table-status-changed");
            return RedirectToAction("Receipts", new { dateFilter, showArchived });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OpenTable(string table, string? branchId = null, string? dateFilter = "all", bool showArchived = false)
        {
            if (!TryGetKitchenBranchFilter(out _))
                return Forbid();

            if (string.IsNullOrWhiteSpace(table))
            {
                TempData["ErrorMessage"] = "Choose a table to seat/open.";
                return RedirectToAction("Receipts", new { dateFilter, showArchived });
            }

            table = table.Trim();
            if (!IsDiningTable(table))
            {
                TempData["ErrorMessage"] = $"Table {table} is not part of the 7 dine-in tables.";
                return RedirectToAction("Receipts", new { dateFilter, showArchived });
            }

            if (table.Length > 32)
                table = table[..32];

            var effectiveBranchId = await GetEffectiveKitchenBranchIdAsync(table, branchId);
            if (string.IsNullOrWhiteSpace(effectiveBranchId))
            {
                TempData["ErrorMessage"] = "Choose a branch-specific table before opening it.";
                return RedirectToAction("Receipts", new { dateFilter, showArchived });
            }

            await _tableRegistry.UpsertAsync(table, branchId: effectiveBranchId);
            await _tableOrderingSessions.OpenOrderingAsync(table, effectiveBranchId);
            var activeOrders = (await _orderService.GetOrdersByTableAsync(table, effectiveBranchId))
                .Where(o => !o.BillArchived)
                .ToList();
            var activeUnlimitedOrders = activeOrders
                .Where(o => string.Equals(o.OrderType, "Unlimited", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (activeUnlimitedOrders.Any())
            {
                var personCount = activeUnlimitedOrders
                    .Select(GetOrderPersonCount)
                    .DefaultIfEmpty(0)
                    .Max();
                var wingFlavors = await ExtractUnlimitedWingFlavorsAsync(
                    activeUnlimitedOrders.SelectMany(o => o.Items ?? new List<OrderItem>()));
                await _tableOrderingSessions.ReplaceFromExistingOrdersAsync(table, personCount, wingFlavors, effectiveBranchId);
            }

            TempData["SuccessMessage"] = $"Table {table} is now occupied and QR ordering is enabled.";
            await _realtime.NotifyKitchenChangedAsync(effectiveBranchId, "table-opened");
            return RedirectToAction("Receipts", new { dateFilter, showArchived });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CloseTable(string table, string? branchId = null, string? dateFilter = "all", bool showArchived = false)
        {
            if (!TryGetKitchenBranchFilter(out _))
                return Forbid();

            if (string.IsNullOrWhiteSpace(table))
            {
                TempData["ErrorMessage"] = "Choose a table to close.";
                return RedirectToAction("Receipts", new { dateFilter, showArchived });
            }

            table = table.Trim();
            if (!IsDiningTable(table))
            {
                TempData["ErrorMessage"] = $"Table {table} is not part of the 7 dine-in tables.";
                return RedirectToAction("Receipts", new { dateFilter, showArchived });
            }

            var effectiveBranchId = await GetEffectiveKitchenBranchIdAsync(table, branchId);
            if (string.IsNullOrWhiteSpace(effectiveBranchId))
            {
                TempData["ErrorMessage"] = "Choose a branch-specific table before closing it.";
                return RedirectToAction("Receipts", new { dateFilter, showArchived });
            }

            await _tableOrderingSessions.CloseOrderingAsync(table, effectiveBranchId);
            TempData["SuccessMessage"] = $"Table {table} is now available and QR ordering is disabled.";
            await _realtime.NotifyKitchenChangedAsync(effectiveBranchId, "table-closed");
            return RedirectToAction("Receipts", new { dateFilter, showArchived });
        }
    }
}
