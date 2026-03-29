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
            ViewBag.TodaysRevenue = todayOrders.Where(o => o.Total > 0).Sum(o => o.Total);
            ViewBag.TodaysRevenueAlaCarte = todayOrders
                .Where(o => (o.OrderType ?? "AlaCarte") == "AlaCarte" && o.Total > 0)
                .Sum(o => o.Total);
            ViewBag.TodaysRevenueUnlimited = todayOrders
                .Where(o => o.OrderType == "Unlimited" && o.Total > 0)
                .Sum(o => o.Total);
            ViewBag.TodaysRevenueDineIn = todayOrders
                .Where(o => (o.DiningType ?? "DineIn") == "DineIn" && o.Total > 0)
                .Sum(o => o.Total);
            ViewBag.TodaysRevenueTakeOut = todayOrders
                .Where(o => o.DiningType == "TakeOut" && o.Total > 0)
                .Sum(o => o.Total);

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
