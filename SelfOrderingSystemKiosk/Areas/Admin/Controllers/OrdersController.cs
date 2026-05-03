using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using SelfOrderingSystemKiosk.Services;
using SelfOrderingSystemKiosk.Areas.Customer.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SelfOrderingSystemKiosk.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Kitchen")]
    public class OrdersController : Controller
    {
        private readonly OrderService _orderService;

        public OrdersController(OrderService orderService)
        {
            _orderService = orderService;
        }

        public async Task<IActionResult> Index(string filter = null)
        {
            ViewData["Title"] = "Orders Management";
            
            List<Order> orders;

            // Apply date filter if specified
            if (filter == "today")
            {
                var todayStart = DateTime.UtcNow.Date;
                var todayOrders = await _orderService.GetByDateRangeHalfOpenAsync(todayStart, todayStart.AddDays(1));
                orders = todayOrders ?? new List<Order>();
                ViewBag.FilterMessage = "Showing today's orders";
            }
            else
            {
                var historyStart = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var ordersInRange = await _orderService.GetByDateRangeHalfOpenAsync(historyStart, DateTime.UtcNow.AddDays(1));
                orders = ordersInRange ?? new List<Order>();
            }

            return View(orders);
        }

        [HttpPost]
        public async Task<IActionResult> UpdateStatus(string id, string status)
        {
            var order = await _orderService.GetByIdAsync(id);
            if (order != null)
            {
                await _orderService.UpdateStatusAsync(id, status);
                TempData["Message"] = $"Order #{order.OrderNumber} status updated to '{status}'.";
            }
            return RedirectToAction("Index");
        }
    }
}
