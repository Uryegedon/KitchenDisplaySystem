using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;
using SelfOrderingSystemKiosk.Areas.Admin.Models;
using CustomerOrder = SelfOrderingSystemKiosk.Areas.Customer.Models.Order;
using CustomerOrderItem = SelfOrderingSystemKiosk.Areas.Customer.Models.OrderItem;

namespace SelfOrderingSystemKiosk.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Owner,BranchManager")]
    [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
    public class SalesController : Controller
    {
        private const int MaxReportRangeDays = 370;

        private readonly OrderService _orderService;
        private readonly BranchService _branchService;
        private readonly MenuItemService _menuItemService;

        public SalesController(OrderService orderService, BranchService branchService, MenuItemService menuItemService)
        {
            _orderService = orderService;
            _branchService = branchService;
            _menuItemService = menuItemService;
        }

        public async Task<IActionResult> Index(string? startDate = null, string? endDate = null, string? branchFilter = null)
        {
            Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
            Response.Headers.Pragma = "no-cache";
            Response.Headers["Referrer-Policy"] = "same-origin";

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
                var normalizedBranchFilter = NormalizeBranchFilter(branchFilter);
                if (!IsValidOwnerBranchFilter(normalizedBranchFilter, allBranches))
                    return BadRequest("Invalid branch filter.");

                effectiveBranchId = string.Equals(normalizedBranchFilter, "all", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : normalizedBranchFilter;
                ViewBag.AllBranches = allBranches;
                ViewBag.BranchFilter = normalizedBranchFilter;
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
            if (rangeEnd <= rangeStart)
                return BadRequest("End date must be after start date.");
            if ((rangeEnd - rangeStart).TotalDays > MaxReportRangeDays)
                return BadRequest($"Sales reports are limited to {MaxReportRangeDays} days per request.");

            var rangeOrders = await _orderService.GetByDateRangeHalfOpenAsync(rangeStart, rangeEnd, effectiveBranchId);
            var reportBillableOrders = FilterSalesOrders(rangeOrders).ToList();

            var rangeRevenue = reportBillableOrders.Sum(o => o.Total);
            var rangeCost = reportBillableOrders.Sum(o => o.OrderCost);
            var rangeProfit = reportBillableOrders.Sum(o => o.Profit);
            var rangeRevenueAlaCarte = reportBillableOrders
                .Where(o => (o.OrderType ?? "AlaCarte") == "AlaCarte" && o.Total > 0)
                .Sum(o => o.Total);
            var rangeRevenueUnlimited = reportBillableOrders
                .Where(o => o.OrderType == "Unlimited" && o.Total > 0)
                .Sum(o => o.Total);
            var rangeRevenueDineIn = reportBillableOrders
                .Where(o => (o.DiningType ?? "DineIn") == "DineIn" && o.Total > 0)
                .Sum(o => o.Total);
            var rangeRevenueTakeOut = reportBillableOrders
                .Where(o => o.DiningType == "TakeOut" && o.Total > 0)
                .Sum(o => o.Total);
            var rangeOrderCount = reportBillableOrders.Count;
            var unpaidBillableOrders = rangeOrders
                .Where(o => !IsCanceled(o) && !IsPaid(o) && o.Total > 0)
                .ToList();
            var canceledOrders = rangeOrders.Where(IsCanceled).ToList();
            var completedOrders = rangeOrders.Where(IsCompleted).ToList();
            var paidOrders = rangeOrders.Where(o => !IsCanceled(o) && IsPaid(o)).ToList();
            var averageOrderValue = rangeOrderCount == 0 ? 0m : rangeRevenue / rangeOrderCount;
            var profitMarginPercent = rangeRevenue == 0 ? 0m : (rangeProfit / rangeRevenue) * 100m;
            var missingCostCount = reportBillableOrders.Count(o => o.OrderCost <= 0 && (o.Items?.Any(i => i.Quantity > 0) ?? false));

            var bestSellersAllTime = await _orderService.GetBestSellersAsync(branchId: effectiveBranchId);
            var bestSellersToday = await _orderService.GetBestSellersAsync(todayStart, todayEnd, effectiveBranchId);

            var (monthStart, monthEnd) = AppClock.CurrentLocalMonthRange();
            var bestSellersMonthly = await _orderService.GetBestSellersAsync(monthStart, monthEnd, effectiveBranchId);

            var menuItems = await _menuItemService.GetAllByBranchAsync(effectiveBranchId);
            var categoryStats = BuildCategoryStats(reportBillableOrders, menuItems);
            var topItemsByRevenue = OrderSalesAnalytics.BuildBestSellers(reportBillableOrders, take: 10)
                .OrderByDescending(i => i.Revenue)
                .ThenByDescending(i => i.Quantity)
                .ToList();
            var paymentBreakdown = BuildBreakdown(reportBillableOrders, o => string.IsNullOrWhiteSpace(o.PaymentMethod) ? "Unspecified" : o.PaymentMethod.Trim());
            var statusBreakdown = BuildBreakdown(rangeOrders, o => string.IsNullOrWhiteSpace(o.Status) ? "Unspecified" : o.Status.Trim());
            var peakHourGroups = BuildPeakHourGroups(reportBillableOrders);

            var chartData = new Dictionary<string, decimal>();
            if (reportBillableOrders.Any())
            {
                var ordersByDay = reportBillableOrders
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
                        var branchOrders = reportBillableOrders
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
            ViewBag.UnpaidOrderCount = unpaidBillableOrders.Count;
            ViewBag.UnpaidBillableRevenue = unpaidBillableOrders.Sum(o => o.Total);
            ViewBag.CanceledOrderCount = canceledOrders.Count;
            ViewBag.CanceledRevenue = canceledOrders.Where(o => o.Total > 0).Sum(o => o.Total);
            ViewBag.CompletedOrderCount = completedOrders.Count;
            ViewBag.PaidOrderCount = paidOrders.Count;
            ViewBag.AverageOrderValue = averageOrderValue;
            ViewBag.ProfitMarginPercent = profitMarginPercent;
            ViewBag.MissingCostCount = missingCostCount;
            ViewBag.PaymentBreakdown = paymentBreakdown;
            ViewBag.StatusBreakdown = statusBreakdown;
            ViewBag.PeakHourGroups = peakHourGroups;
            ViewBag.CategoryStats = categoryStats;
            ViewBag.TopItemsByRevenue = topItemsByRevenue;
            ViewBag.ChartData = chartData;
            ViewBag.BestSellersAllTime = bestSellersAllTime;
            ViewBag.BestSellersToday = bestSellersToday;
            ViewBag.BestSellersMonthly = bestSellersMonthly;
            ViewBag.HasCustomRange = !string.IsNullOrEmpty(startDate) && !string.IsNullOrEmpty(endDate);
            ViewBag.IsOwner = isOwner;

            return View();
        }

        private static string NormalizeBranchFilter(string? branchFilter)
        {
            if (string.IsNullOrWhiteSpace(branchFilter))
                return "all";

            var trimmed = branchFilter.Trim();
            return string.Equals(trimmed, "all", StringComparison.OrdinalIgnoreCase)
                ? "all"
                : trimmed;
        }

        private static bool IsValidOwnerBranchFilter(string branchFilter, IEnumerable<Branch> branches)
        {
            return string.Equals(branchFilter, "all", StringComparison.OrdinalIgnoreCase)
                || branches.Any(branch => string.Equals(branch.Id, branchFilter, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<CustomerOrder> FilterSalesOrders(IEnumerable<CustomerOrder> orders) =>
            orders.Where(o => !IsCanceled(o) && o.Total > 0);

        private static List<PeakHourSummary> BuildPeakHourGroups(IEnumerable<CustomerOrder> orders)
        {
            return orders
                .GroupBy(order =>
                {
                    var hour = AppClock.ToLocal(order.OrderDate).Hour;
                    return hour == 0 ? 7 : (hour - 1) / 3;
                })
                .Select(group =>
                {
                    var startHour = (group.Key * 3) + 1;
                    var endHour = startHour + 2;
                    return new PeakHourSummary
                    {
                        StartHour = startHour % 24,
                        EndHour = endHour % 24,
                        OrderCount = group.Count(),
                        Revenue = group.Sum(o => o.Total)
                    };
                })
                .OrderByDescending(s => s.Revenue)
                .ThenBy(s => s.StartHour == 0 ? 24 : s.StartHour)
                .Take(8)
                .ToList();
        }

        private static bool IsCanceled(CustomerOrder order) =>
            string.Equals(order.Status, "Canceled", StringComparison.OrdinalIgnoreCase);

        private static bool IsCompleted(CustomerOrder order) =>
            string.Equals(order.Status, "Completed", StringComparison.OrdinalIgnoreCase);

        private static bool IsPaid(CustomerOrder order) =>
            string.Equals(order.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase);

        private static List<SalesBreakdownSummary> BuildBreakdown(IEnumerable<CustomerOrder> orders, Func<CustomerOrder, string> keySelector)
        {
            return orders
                .GroupBy(keySelector, StringComparer.OrdinalIgnoreCase)
                .Select(g => new SalesBreakdownSummary
                {
                    Label = g.Key,
                    OrderCount = g.Count(),
                    Revenue = g.Where(o => o.Total > 0).Sum(o => o.Total)
                })
                .OrderByDescending(s => s.Revenue)
                .ThenByDescending(s => s.OrderCount)
                .ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<CategorySalesSummary> BuildCategoryStats(IEnumerable<CustomerOrder> orders, IEnumerable<MenuItem> menuItems)
        {
            var categoryByName = menuItems
                .Where(i => !string.IsNullOrWhiteSpace(i.Item))
                .GroupBy(i => i.Item.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => string.IsNullOrWhiteSpace(g.First().Category) ? "Uncategorized" : g.First().Category.Trim(),
                    StringComparer.OrdinalIgnoreCase);

            return orders
                .SelectMany(o => o.Items ?? new List<CustomerOrderItem>())
                .Where(i => !string.IsNullOrWhiteSpace(i.ItemName) && i.Quantity > 0)
                .GroupBy(i =>
                {
                    var itemName = NormalizeItemName(i.ItemName);
                    return categoryByName.TryGetValue(itemName, out var category) ? category : "Uncategorized";
                }, StringComparer.OrdinalIgnoreCase)
                .Select(g => new CategorySalesSummary
                {
                    Category = g.Key,
                    Quantity = g.Sum(i => i.Quantity),
                    Revenue = g.Sum(i => i.Price * i.Quantity)
                })
                .OrderByDescending(s => s.Revenue)
                .ThenByDescending(s => s.Quantity)
                .ToList();
        }

        private static string NormalizeItemName(string itemName)
        {
            var trimmed = itemName.Trim();
            var flavorMarker = trimmed.IndexOf(" (Flavors:", StringComparison.OrdinalIgnoreCase);
            return flavorMarker > 0 ? trimmed[..flavorMarker].Trim() : trimmed;
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

    public class SalesBreakdownSummary
    {
        public string Label { get; set; } = string.Empty;
        public int OrderCount { get; set; }
        public decimal Revenue { get; set; }
    }

    public class PeakHourSummary
    {
        public int StartHour { get; set; }
        public int EndHour { get; set; }
        public int OrderCount { get; set; }
        public decimal Revenue { get; set; }
        public string Label => $"{DateTime.Today.AddHours(StartHour):h tt} - {DateTime.Today.AddHours(EndHour):h tt}";
    }

    public class CategorySalesSummary
    {
        public string Category { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Revenue { get; set; }
    }
}
