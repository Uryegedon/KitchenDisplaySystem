using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SelfOrderingSystemKiosk.Services;
using SelfOrderingSystemKiosk.Areas.Admin.Models;

namespace SelfOrderingSystemKiosk.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Owner,BranchManager,Admin")]
    public class SalesController : Controller
    {
        private readonly OrderService _orderService;
        private readonly BranchService _branchService;

        public SalesController(OrderService orderService, BranchService branchService)
        {
            _orderService = orderService;
            _branchService = branchService;
        }

        public async Task<IActionResult> Index(string? startDate = null, string? endDate = null, string? branchFilter = null)
        {
            ViewData["Title"] = "Sales & reports";

            // Get user's branch context
            var userBranchId = User.GetBranchId();
            var isOwner = User.HasAllBranchAccess();
            if (!isOwner && string.IsNullOrWhiteSpace(userBranchId))
                return Forbid();
            var allBranches = isOwner ? await _branchService.GetAllAsync() : new List<Branch>();
            var effectiveBranchId = userBranchId;

            if (isOwner)
            {
                effectiveBranchId = string.Equals(branchFilter, "all", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : string.IsNullOrWhiteSpace(branchFilter) ? null : branchFilter;
                ViewBag.AllBranches = allBranches;
                ViewBag.BranchFilter = string.IsNullOrWhiteSpace(branchFilter) ? "all" : branchFilter;
            }

            // Get branch info for display
            Branch? userBranch = null;
            if (!string.IsNullOrEmpty(effectiveBranchId))
            {
                userBranch = await _branchService.GetByIdAsync(effectiveBranchId);
                ViewData["BranchName"] = userBranch?.BranchName ?? "Unknown Branch";
            }
            else
            {
                ViewData["BranchName"] = "All Branches";
            }

            var (todayStart, todayEnd) = AppClock.LocalDateRange(AppClock.LocalNow.Date);
            var todayOrders = await _orderService.GetByDateRangeHalfOpenAsync(todayStart, todayEnd, effectiveBranchId);

            DateTime defaultRangeStart, defaultRangeEnd;
            if (string.IsNullOrEmpty(startDate) && string.IsNullOrEmpty(endDate))
            {
                (defaultRangeStart, defaultRangeEnd) = AppClock.CurrentLocalWeekRange();
            }
            else
            {
                defaultRangeStart = todayStart;
                defaultRangeEnd = todayEnd;
            }

            var rangeStart = defaultRangeStart;
            var rangeEnd = defaultRangeEnd;
            if (DateTime.TryParse(startDate, out var parsedStart) && DateTime.TryParse(endDate, out var parsedEnd))
            {
                (rangeStart, rangeEnd) = AppClock.LocalDateRange(parsedStart, parsedEnd);
            }

            var rangeOrders = await _orderService.GetByDateRangeHalfOpenAsync(rangeStart, rangeEnd, effectiveBranchId);

            var rangeRevenue = rangeOrders.Where(o => o.Total > 0).Sum(o => o.Total);
            var rangeCost = rangeOrders.Where(o => o.Total > 0).Sum(o => o.OrderCost);
            var rangeProfit = rangeOrders.Where(o => o.Total > 0).Sum(o => o.Profit);
            var rangeRevenueAlaCarte = rangeOrders
                .Where(o => (o.OrderType ?? "AlaCarte") == "AlaCarte" && o.Total > 0)
                .Sum(o => o.Total);
            var rangeRevenueUnlimited = rangeOrders
                .Where(o => o.OrderType == "Unlimited" && o.Total > 0)
                .Sum(o => o.Total);
            var rangeRevenueDineIn = rangeOrders
                .Where(o => (o.DiningType ?? "DineIn") == "DineIn" && o.Total > 0)
                .Sum(o => o.Total);
            var rangeRevenueTakeOut = rangeOrders
                .Where(o => o.DiningType == "TakeOut" && o.Total > 0)
                .Sum(o => o.Total);
            var rangeOrderCount = rangeOrders.Count;

            var historyStart = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var allOrdersForBestSellers = await _orderService.GetByDateRangeHalfOpenAsync(historyStart, DateTime.UtcNow.AddDays(1), effectiveBranchId);
            var bestSellersAllTime = OrderSalesAnalytics.BuildBestSellers(allOrdersForBestSellers);
            var bestSellersToday = OrderSalesAnalytics.BuildBestSellers(todayOrders);

            var (monthStart, monthEnd) = AppClock.CurrentLocalMonthRange();
            var monthOrders = await _orderService.GetByDateRangeHalfOpenAsync(monthStart, monthEnd, effectiveBranchId);
            var bestSellersMonthly = OrderSalesAnalytics.BuildBestSellers(monthOrders);

            var chartData = new Dictionary<string, decimal>();
            if (rangeOrders.Any())
            {
                var ordersByDay = rangeOrders
                    .Where(o => o.Total > 0)
                    .GroupBy(o => AppClock.ToLocal(o.OrderDate).Date.ToString("yyyy-MM-dd"))
                    .OrderBy(g => g.Key);
                foreach (var dayGroup in ordersByDay)
                    chartData[dayGroup.Key] = dayGroup.Sum(o => o.Total);
            }

            if (isOwner)
            {
                ViewBag.BranchRevenueStats = allBranches
                    .Select(branch =>
                    {
                        var branchOrders = rangeOrders
                            .Where(o => string.Equals(o.BranchId, branch.Id, StringComparison.OrdinalIgnoreCase) && o.Total > 0)
                            .ToList();
                        return new BranchRevenueSummary
                        {
                            BranchId = branch.Id,
                            BranchName = branch.BranchName,
                            BranchCode = branch.BranchCode,
                            Revenue = branchOrders.Sum(o => o.Total),
                            Cost = branchOrders.Sum(o => o.OrderCost),
                            Profit = branchOrders.Sum(o => o.Profit),
                            OrderCount = branchOrders.Count
                        };
                    })
                    .OrderByDescending(s => s.Revenue)
                    .ToList();
            }

            ViewBag.RangeStart = AppClock.ToLocal(rangeStart);
            ViewBag.RangeEnd = AppClock.ToLocal(rangeEnd).AddDays(-1);
            ViewBag.RangeRevenue = rangeRevenue;
            ViewBag.RangeCost = rangeCost;
            ViewBag.RangeProfit = rangeProfit;
            ViewBag.RangeRevenueAlaCarte = rangeRevenueAlaCarte;
            ViewBag.RangeRevenueUnlimited = rangeRevenueUnlimited;
            ViewBag.RangeRevenueDineIn = rangeRevenueDineIn;
            ViewBag.RangeRevenueTakeOut = rangeRevenueTakeOut;
            ViewBag.RangeOrderCount = rangeOrderCount;
            ViewBag.ChartData = chartData;
            ViewBag.BestSellersAllTime = bestSellersAllTime;
            ViewBag.BestSellersToday = bestSellersToday;
            ViewBag.BestSellersMonthly = bestSellersMonthly;
            ViewBag.HasCustomRange = !string.IsNullOrEmpty(startDate) && !string.IsNullOrEmpty(endDate);
            ViewBag.IsOwner = isOwner;

            return View();
        }
    }

    public class BranchRevenueSummary
    {
        public string BranchId { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty;
        public string BranchCode { get; set; } = string.Empty;
        public int OrderCount { get; set; }
        public decimal Revenue { get; set; }
        public decimal Cost { get; set; }
        public decimal Profit { get; set; }
    }
}
