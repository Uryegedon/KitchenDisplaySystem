using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;

namespace SelfOrderingSystemKiosk.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Kitchen")]
    public class DashboardController : Controller
    {
        private readonly MenuItemService _menuItems;
        private readonly IngredientStockService _ingredients;
        private readonly OrderService _orderService;

        public DashboardController(MenuItemService menuItems, IngredientStockService ingredients, OrderService orderService)
        {
            _menuItems = menuItems;
            _ingredients = ingredients;
            _orderService = orderService;
        }

        public async Task<IActionResult> Index()
        {
            ViewData["Title"] = "Admin dashboard";

            var menuList = await _menuItems.GetAllAsync();
            var ingredientList = await _ingredients.GetAllAsync();

            ViewBag.TotalMenuItems = menuList?.Count ?? 0;
            ViewBag.TotalInventoryItems = ingredientList?.Count ?? 0;

            var lowRows = new List<LowStockDashboardRow>();
            if (menuList != null)
            {
                lowRows.AddRange(menuList
                    .Where(i => i.CurrentStock <= i.ReorderLevel)
                    .Select(m => new LowStockDashboardRow
                    {
                        Id = m.Id,
                        Name = m.Item ?? "",
                        Kind = "Menu",
                        CurrentStock = m.CurrentStock,
                        ReorderLevel = m.ReorderLevel
                    }));
            }

            if (ingredientList != null)
            {
                lowRows.AddRange(ingredientList
                    .Where(i => i.CurrentStock <= i.ReorderLevel)
                    .Select(g => new LowStockDashboardRow
                    {
                        Id = g.Id,
                        Name = g.Item ?? "",
                        Kind = "Ingredient",
                        CurrentStock = g.CurrentStock,
                        ReorderLevel = g.ReorderLevel
                    }));
            }

            lowRows = lowRows.OrderBy(r => r.Kind).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            ViewBag.LowStockList = lowRows;
            ViewBag.LowStockItems = lowRows.Count;

            var todayStart = DateTime.UtcNow.Date;
            var todayEnd = todayStart.AddDays(1);
            var todayOrders = await _orderService.GetByDateRangeHalfOpenAsync(todayStart, todayEnd);

            ViewBag.TodaysSales = todayOrders.Count;
            var billable = todayOrders.Where(o => o.Total > 0).ToList();
            ViewBag.TodaysBillableCount = billable.Count;
            ViewBag.TodaysSubtotal = billable.Sum(o => o.Subtotal);
            ViewBag.TodaysTax = billable.Sum(o => o.Tax);
            ViewBag.TodaysRevenue = billable.Sum(o => o.Total);
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

            return View();
        }

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> RestockItem(string id, int quantity)
        {
            try
            {
                var menu = await _menuItems.GetByIdAsync(id);
                if (menu != null)
                {
                    var ok = await _menuItems.IncreaseStockAsync(id, quantity, "Dashboard", null, "Dashboard restock");
                    if (!ok)
                        return Json(new { success = false, message = "Could not restock menu item." });
                    var updated = await _menuItems.GetByIdAsync(id);
                    return Json(new { success = true, message = $"Restocked {updated?.Item} by {quantity}. New stock: {updated?.CurrentStock}" });
                }

                var ing = await _ingredients.GetByIdAsync(id);
                if (ing != null)
                {
                    var ok = await _ingredients.IncreaseStockAsync(id, quantity, "Dashboard", null, "Dashboard restock");
                    if (!ok)
                        return Json(new { success = false, message = "Could not restock ingredient." });
                    var updated = await _ingredients.GetByIdAsync(id);
                    return Json(new { success = true, message = $"Restocked {updated?.Item} by {quantity}. New stock: {updated?.CurrentStock}" });
                }

                return Json(new { success = false, message = "Item not found." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Error: {ex.Message}" });
            }
        }
    }
}
