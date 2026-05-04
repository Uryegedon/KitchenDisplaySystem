using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using SelfOrderingSystemKiosk.Services;
using SelfOrderingSystemKiosk.Areas.Customer.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SelfOrderingSystemKiosk.Areas.Admin.Models;

namespace SelfOrderingSystemKiosk.Controllers
{
    [Area("Admin")]
    [Authorize]
    public class OrdersController : Controller
    {
        private readonly OrderService _orderService;
        private readonly BranchService _branchService;

        public OrdersController(OrderService orderService, BranchService branchService)
        {
            _orderService = orderService;
            _branchService = branchService;
        }

        public async Task<IActionResult> Index(string filter = null)
        {
            ViewData["Title"] = "Orders Management";

            // Get user's branch context
            var userBranchId = User.GetBranchId();
            var isOwner = User.IsOwner();

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

            List<Order> orders;

            // Apply date filter if specified
            if (filter == "today")
            {
                var todayStart = DateTime.UtcNow.Date;
                var todayOrders = await _orderService.GetByDateRangeHalfOpenAsync(todayStart, todayStart.AddDays(1), userBranchId);
                orders = todayOrders ?? new List<Order>();
                ViewBag.FilterMessage = isOwner ? "Showing today's orders (all branches)" : "Showing today's orders";
            }
            else
            {
                var historyStart = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var ordersInRange = await _orderService.GetByDateRangeHalfOpenAsync(historyStart, DateTime.UtcNow.AddDays(1), userBranchId);
                orders = ordersInRange ?? new List<Order>();
                ViewBag.FilterMessage = isOwner ? "Showing all orders (all branches)" : "Showing all orders";
            }

            return View(orders);
        }

        [HttpPost]
        public async Task<IActionResult> UpdateStatus(string id, string status)
        {
            var order = await _orderService.GetByIdAsync(id);
            
            // Branch managers can only update orders from their branch
            if (order != null)
            {
                var userBranchId = User.GetBranchId();
                var isOwner = User.IsOwner();

                if (!isOwner && !string.IsNullOrEmpty(userBranchId) && order.BranchId != userBranchId)
                {
                    TempData["Error"] = "You can only update orders from your assigned branch.";
                    return RedirectToAction("Index");
                }

                await _orderService.UpdateStatusAsync(id, status);
                TempData["Message"] = $"Order #{order.OrderNumber} status updated to '{status}'.";
            }
            return RedirectToAction("Index");
        }
    }
}
