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
    [Authorize(Roles = "Owner,BranchManager,Admin")]
    public class SalesController : Controller
    {
        private readonly OrderService _orderService;
        private readonly BranchService _branchService;
        private readonly MenuItemService _menuItemService;

        public SalesController(OrderService orderService, BranchService branchService, MenuItemService menuItemService)
        {
            _orderService = orderService;
            _branchService = branchService;
            _menuItemService = menuItemService;
        }

        public async Task<IActionResult> Index(string? startDate = null, string? endDate = null, string? branchFilter = null, string? reportBasis = null)
        {
            ViewData["Title"] = "Sales & reports";
            var selectedReportBasis = NormalizeReportBasis(reportBasis);

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
            var reportOrders = FilterOrdersForReport(rangeOrders, selectedReportBasis).ToList();
            var reportBillableOrders = reportOrders.Where(o => o.Total > 0).ToList();
            var nonCanceledBillableOrders = rangeOrders
                .Where(o => !IsCanceled(o) && o.Total > 0)
                .ToList();

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
            var grossBillableRevenue = nonCanceledBillableOrders.Sum(o => o.Total);
            var unpaidBillableOrders = rangeOrders
                .Where(o => !IsCanceled(o) && !IsPaid(o) && o.Total > 0)
                .ToList();
            var canceledOrders = rangeOrders.Where(IsCanceled).ToList();
            var completedOrders = rangeOrders.Where(IsCompleted).ToList();
            var paidOrders = rangeOrders.Where(o => !IsCanceled(o) && IsPaid(o)).ToList();
            var averageOrderValue = rangeOrderCount == 0 ? 0m : rangeRevenue / rangeOrderCount;
            var profitMarginPercent = rangeRevenue == 0 ? 0m : (rangeProfit / rangeRevenue) * 100m;
            var missingCostCount = reportBillableOrders.Count(o => o.OrderCost <= 0 && (o.Items?.Any(i => i.Quantity > 0) ?? false));

            var historyStart = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var allOrdersForBestSellers = await _orderService.GetByDateRangeHalfOpenAsync(historyStart, DateTime.UtcNow.AddDays(1), effectiveBranchId);
            var bestSellersAllTime = OrderSalesAnalytics.BuildBestSellers(FilterOrdersForReport(allOrdersForBestSellers, selectedReportBasis));
            var bestSellersToday = OrderSalesAnalytics.BuildBestSellers(FilterOrdersForReport(todayOrders, selectedReportBasis));

            var (monthStart, monthEnd) = AppClock.CurrentLocalMonthRange();
            var monthOrders = await _orderService.GetByDateRangeHalfOpenAsync(monthStart, monthEnd, effectiveBranchId);
            var bestSellersMonthly = OrderSalesAnalytics.BuildBestSellers(FilterOrdersForReport(monthOrders, selectedReportBasis));

            var menuItems = await _menuItemService.GetAllByBranchAsync(effectiveBranchId);
            var categoryStats = BuildCategoryStats(reportBillableOrders, menuItems);
            var topItemsByRevenue = OrderSalesAnalytics.BuildBestSellers(reportBillableOrders, take: 10)
                .OrderByDescending(i => i.Revenue)
                .ThenByDescending(i => i.Quantity)
                .ToList();
            var paymentBreakdown = BuildBreakdown(reportBillableOrders, o => string.IsNullOrWhiteSpace(o.PaymentMethod) ? "Unspecified" : o.PaymentMethod.Trim());
            var statusBreakdown = BuildBreakdown(rangeOrders, o => string.IsNullOrWhiteSpace(o.Status) ? "Unspecified" : o.Status.Trim());
            var hourlySales = reportBillableOrders
                .GroupBy(o => AppClock.ToLocal(o.OrderDate).Hour)
                .Select(g => new HourlySalesSummary
                {
                    Hour = g.Key,
                    OrderCount = g.Count(),
                    Revenue = g.Sum(o => o.Total)
                })
                .OrderByDescending(s => s.Revenue)
                .ThenBy(s => s.Hour)
                .Take(8)
                .ToList();

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
            ViewBag.GrossBillableRevenue = grossBillableRevenue;
            ViewBag.UnpaidOrderCount = unpaidBillableOrders.Count;
            ViewBag.UnpaidBillableRevenue = unpaidBillableOrders.Sum(o => o.Total);
            ViewBag.CanceledOrderCount = canceledOrders.Count;
            ViewBag.CanceledRevenue = canceledOrders.Where(o => o.Total > 0).Sum(o => o.Total);
            ViewBag.CompletedOrderCount = completedOrders.Count;
            ViewBag.PaidOrderCount = paidOrders.Count;
            ViewBag.AverageOrderValue = averageOrderValue;
            ViewBag.ProfitMarginPercent = profitMarginPercent;
            ViewBag.MissingCostCount = missingCostCount;
            ViewBag.ReportBasis = selectedReportBasis;
            ViewBag.PaymentBreakdown = paymentBreakdown;
            ViewBag.StatusBreakdown = statusBreakdown;
            ViewBag.HourlySales = hourlySales;
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

        private static string NormalizeReportBasis(string? reportBasis)
        {
            if (string.Equals(reportBasis, "completed", StringComparison.OrdinalIgnoreCase))
                return "completed";
            if (string.Equals(reportBasis, "allBillable", StringComparison.OrdinalIgnoreCase))
                return "allBillable";
            return "paid";
        }

        private static IEnumerable<CustomerOrder> FilterOrdersForReport(IEnumerable<CustomerOrder> orders, string reportBasis)
        {
            return reportBasis switch
            {
                "completed" => orders.Where(o => !IsCanceled(o) && IsCompleted(o)),
                "allBillable" => orders.Where(o => !IsCanceled(o) && o.Total > 0),
                _ => orders.Where(o => !IsCanceled(o) && IsPaid(o))
            };
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

    public class HourlySalesSummary
    {
        public int Hour { get; set; }
        public int OrderCount { get; set; }
        public decimal Revenue { get; set; }
        public string Label => $"{DateTime.Today.AddHours(Hour):h tt}";
    }

    public class CategorySalesSummary
    {
        public string Category { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Revenue { get; set; }
    }
}
