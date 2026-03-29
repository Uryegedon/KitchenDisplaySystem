using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using SelfOrderingSystemKiosk.Services;
using System;
using System.Linq;

namespace SelfOrderingSystemKiosk.Areas.Kitchen.Controllers
{

    [Area("Kitchen")]
    [Authorize(Roles = "Kitchen,Admin")]
    public class KitchenController : Controller
    {
        private readonly OrderService _orderService;
        private readonly MenuItemService _menuItems;
        private readonly ILogger<KitchenController> _logger;

        public KitchenController(OrderService orderService, MenuItemService menuItems, ILogger<KitchenController> logger)
        {
            _orderService = orderService;
            _menuItems = menuItems;
            _logger = logger;
        }

        // GET: Kitchen/Kitchen/Index
        [HttpGet]
        public async Task<IActionResult> Index([FromQuery] string? dateFilter = "all")
        {
            var orders = await _orderService.GetOrdersForKitchenAsync(dateFilter);
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

            return View(order);
        }

        // Optional: update status
        [HttpPost]
        public async Task<IActionResult> UpdateStatus(string id, string status)
        {
            // Get the order to check current status
            var order = await _orderService.GetByIdAsync(id);
            
            if (order == null)
            {
                return RedirectToAction("Index");
            }

            // Prevent marking as "Completed" if order is still "Pending"
            if (status.Equals("Completed", StringComparison.OrdinalIgnoreCase) && 
                order.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase))
            {
                TempData["ErrorMessage"] = "Cannot mark order as done. Please start the order first.";
                return RedirectToAction("Index");
            }

            // If status is being changed to "Completed", decrement stock
            if (status.Equals("Completed", StringComparison.OrdinalIgnoreCase))
            {
                // Only decrement if order is not already completed (prevent double-decrementing)
                if (!order.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase) &&
                    order.Items != null && order.Items.Any())
                {
                    // Decrement stock for each item in the order
                    foreach (var orderItem in order.Items)
                    {
                        if (!string.IsNullOrEmpty(orderItem.ItemName) && orderItem.Quantity > 0)
                        {
                            try
                            {
                                await _menuItems.DecrementStockAsync(orderItem.ItemName, orderItem.Quantity, "Sale", "Order", order.Id);
                                _logger.LogInformation("Decremented stock for {Item} by {Qty}", orderItem.ItemName, orderItem.Quantity);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error decrementing stock for {Item}", orderItem.ItemName);
                            }
                        }
                    }
                }
            }

            // Update the order status
            await _orderService.UpdateStatusAsync(id, status);
            return RedirectToAction("Index");
        }
    }
}
