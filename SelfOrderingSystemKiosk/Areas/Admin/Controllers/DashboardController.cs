using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;
using SelfOrderingSystemKiosk.Areas.Admin.Models;
using SelfOrderingSystemKiosk.Areas.Customer.Models;
using Order = SelfOrderingSystemKiosk.Areas.Customer.Models.Order;

namespace SelfOrderingSystemKiosk.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Owner,BranchManager")]
    public class DashboardController : Controller
    {
        private readonly MenuItemService _menuItems;
        private readonly IngredientStockService _ingredients;
        private readonly OrderService _orderService;
        private readonly BranchService _branchService;
        private readonly TableOrderingSessionService _tableOrderingSessions;

        public DashboardController(
            MenuItemService menuItems,
            IngredientStockService ingredients,
            OrderService orderService,
            BranchService branchService,
            TableOrderingSessionService tableOrderingSessions)
        {
            _menuItems = menuItems;
            _ingredients = ingredients;
            _orderService = orderService;
            _branchService = branchService;
            _tableOrderingSessions = tableOrderingSessions;
        }

        public async Task<IActionResult> Index()
        {
            ViewData["Title"] = User.HasAllBranchAccess() ? "Owner Dashboard - All Branches" : "Branch Dashboard";

            // Get user's branch context
            var userBranchId = User.GetBranchId();
            var isOwner = User.HasAllBranchAccess();
            var isBranchManager = User.IsBranchManager();
            if (!isOwner && string.IsNullOrWhiteSpace(userBranchId))
                return Forbid();

            // Get branch info for display
            Branch? userBranch = null;
            if (!string.IsNullOrEmpty(userBranchId))
            {
                userBranch = await _branchService.GetByIdAsync(userBranchId);
                ViewData["BranchName"] = userBranch?.BranchName ?? "Unknown Branch";
            }
            else
            {
                ViewData["BranchName"] = "All Branches";
            }

            // Get all branches for owner overview
            List<Branch> allBranches = new();
            List<BranchSummary> branchSummaries = new();

            if (isOwner)
            {
                allBranches = await _branchService.GetAllAsync();

                // Build summary for each branch
                var (todayStart, todayEnd) = AppClock.LocalDateRange(AppClock.LocalNow.Date);
                var allTodayOrders = await _orderService.GetByDateRangeHalfOpenAsync(todayStart, todayEnd, null);
                var todayOrdersByBranch = allTodayOrders
                    .Where(o => !string.IsNullOrWhiteSpace(o.BranchId))
                    .GroupBy(o => o.BranchId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

                foreach (var branch in allBranches)
                {
                    todayOrdersByBranch.TryGetValue(branch.Id, out var branchOrders);
                    branchOrders ??= new List<Order>();
                    var billable = branchOrders.Where(o => o.Total > 0).ToList();

                    branchSummaries.Add(new BranchSummary
                    {
                        BranchId = branch.Id,
                        BranchName = branch.BranchName,
                        BranchCode = branch.BranchCode,
                        TodaysRevenue = billable.Sum(o => o.Total),
                        TodaysCost = billable.Sum(o => o.OrderCost),
                        TodaysProfit = billable.Sum(o => o.Profit),
                        TodaysOrders = branchOrders.Count,
                        TodaysBillableCount = billable.Count
                    });
                }

                ViewBag.AllBranches = allBranches;
                ViewBag.BranchSummaries = branchSummaries;

                // Aggregate totals for owner
                BuildDashboardMetrics(allTodayOrders, isOwner: true);
            }
            else
            {
                // Branch-restricted view
                var (todayStart, todayEnd) = AppClock.LocalDateRange(AppClock.LocalNow.Date);
                var todayOrders = await _orderService.GetByDateRangeHalfOpenAsync(todayStart, todayEnd, userBranchId);

                BuildDashboardMetrics(todayOrders, isOwner: false);
            }

            // Common metrics (inventory, menu items) - branch filtered
            var menuList = await _menuItems.GetAllByBranchAsync(userBranchId);
            var ingredientList = await _ingredients.GetAllByBranchAsync(userBranchId);

            ViewBag.TotalMenuItems = menuList?.Count ?? 0;
            ViewBag.TotalInventoryItems = ingredientList?.Count ?? 0;

            // Low stock items - branch filtered
            var lowStockItems = await _ingredients.GetLowStockByBranchAsync(userBranchId);
            var lowRows = lowStockItems
                .Select(g => new LowStockDashboardRow
                {
                    Id = g.Id,
                    Name = g.Item ?? "",
                    Kind = "Ingredient",
                    BranchId = g.BranchId,
                    CurrentStock = g.CurrentStock,
                    ReorderLevel = g.ReorderLevel
                })
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ViewBag.LowStockList = lowRows;
            ViewBag.LowStockItems = lowRows.Count;
            ViewBag.IsOwner = isOwner;
            var tableSessions = await _tableOrderingSessions.GetAllAsync();
            ViewBag.ActiveTables = tableSessions.Count(s =>
                s.IsOrderingOpen &&
                (isOwner || string.Equals(s.BranchId, userBranchId, StringComparison.OrdinalIgnoreCase)));
            var pendingOrderCount = ViewBag.PendingOrders != null ? (int)ViewBag.PendingOrders : 0;
            ViewBag.AttentionCount = pendingOrderCount + lowRows.Count;

            return View();
        }

        /// <summary>
        /// Builds common dashboard metrics from today's orders
        /// </summary>
        private void BuildDashboardMetrics(List<Order> todayOrders, bool isOwner)
        {
            ViewBag.TodaysSales = todayOrders.Count;
            var billable = todayOrders.Where(o => o.Total > 0).ToList();
            ViewBag.TodaysBillableCount = billable.Count;
            ViewBag.TodaysSubtotal = billable.Sum(o => o.Subtotal);
            ViewBag.TodaysTax = billable.Sum(o => o.Tax);
            ViewBag.TodaysRevenue = billable.Sum(o => o.Total);
            ViewBag.TodaysCost = billable.Sum(o => o.OrderCost);
            ViewBag.TodaysProfit = billable.Sum(o => o.Profit);
            ViewBag.TodaysRevenueAlaCarte = billable
                .Where(o => (o.OrderType ?? "AlaCarte") == "AlaCarte")
                .Sum(o => o.Total);
            ViewBag.TodaysRevenueUnlimited = billable
                .Where(o => o.OrderType == "Unlimited")
                .Sum(o => o.Total);
            ViewBag.TodaysRevenueDineIn = billable
                .Where(o => (o.DiningType ?? "DineIn") == "DineIn")
                .Sum(o => o.Total);
            ViewBag.TodaysRevenueTakeOut = billable
                .Where(o => o.DiningType == "TakeOut")
                .Sum(o => o.Total);

            ViewBag.TodaysCountAlaCarte = billable.Count(o => (o.OrderType ?? "AlaCarte") == "AlaCarte");
            ViewBag.TodaysCountUnlimited = billable.Count(o => o.OrderType == "Unlimited");
            ViewBag.TodaysCountDineIn = billable.Count(o => (o.DiningType ?? "DineIn") == "DineIn");
            ViewBag.TodaysCountTakeOut = billable.Count(o => o.DiningType == "TakeOut");

            ViewBag.TodaysRevAlaCarteDineIn = billable
                .Where(o => (o.OrderType ?? "AlaCarte") == "AlaCarte" && (o.DiningType ?? "DineIn") == "DineIn")
                .Sum(o => o.Total);
            ViewBag.TodaysRevAlaCarteTakeOut = billable
                .Where(o => (o.OrderType ?? "AlaCarte") == "AlaCarte" && o.DiningType == "TakeOut")
                .Sum(o => o.Total);
            ViewBag.TodaysRevUnlimitedDineIn = billable
                .Where(o => o.OrderType == "Unlimited" && (o.DiningType ?? "DineIn") == "DineIn")
                .Sum(o => o.Total);
            ViewBag.TodaysRevUnlimitedTakeOut = billable
                .Where(o => o.OrderType == "Unlimited" && o.DiningType == "TakeOut")
                .Sum(o => o.Total);

            ViewBag.TodaysCountKiosk = todayOrders.Count(o => string.Equals(o.OrderChannel, "Kiosk", StringComparison.OrdinalIgnoreCase));
            ViewBag.TodaysCountQr = todayOrders.Count(o => string.Equals(o.OrderChannel, "Qr", StringComparison.OrdinalIgnoreCase));
            ViewBag.PendingOrders = todayOrders.Count(o =>
                string.Equals(o.Status, "Pending", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(o.Status, "In Progress", StringComparison.OrdinalIgnoreCase));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RestockItem(string id, int quantity)
        {
            try
            {
                var ing = await _ingredients.GetByIdAsync(id);
                if (ing != null)
                {
                    var userBranchId = User.GetBranchId();
                    if (!User.HasAllBranchAccess() &&
                        (string.IsNullOrWhiteSpace(ing.BranchId) ||
                         string.IsNullOrWhiteSpace(userBranchId) ||
                         !string.Equals(ing.BranchId, userBranchId, StringComparison.OrdinalIgnoreCase)))
                    {
                        return Json(new { success = false, message = "You can only restock items from your assigned branch." });
                    }

                    var ok = await _ingredients.IncreaseStockAsync(id, quantity, "Dashboard", null, "Dashboard restock");
                    if (!ok)
                        return Json(new { success = false, message = "Could not restock ingredient." });
                    await _menuItems.SyncAvailabilityForIngredientAsync(id);
                    var updated = await _ingredients.GetByIdAsync(id);
                    return Json(new { success = true, message = $"Restocked {updated?.Item} by {quantity}. New stock: {updated?.CurrentStock}" });
                }

                return Json(new { success = false, message = "Item not found." });
            }
            catch
            {
                return Json(new { success = false, message = "Could not restock item. Please try again." });
            }
        }
    }

    /// <summary>
    /// View model for branch summary cards in owner dashboard
    /// </summary>
    public class BranchSummary
    {
        public string BranchId { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty;
        public string BranchCode { get; set; } = string.Empty;
        public decimal TodaysRevenue { get; set; }
        public int TodaysOrders { get; set; }
        public int TodaysBillableCount { get; set; }
        public decimal TodaysCost { get; set; }
        public decimal TodaysProfit { get; set; }
    }

}
