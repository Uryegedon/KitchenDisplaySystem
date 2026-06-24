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
    [Authorize(Roles = "Owner,BranchManager")]
    public class OrdersController : Controller
    {
        private readonly OrderService _orderService;
        private readonly BranchService _branchService;
        private readonly ManagementLogService _managementLogs;

        public OrdersController(OrderService orderService, BranchService branchService, ManagementLogService managementLogs)
        {
            _orderService = orderService;
            _branchService = branchService;
            _managementLogs = managementLogs;
        }

        public async Task<IActionResult> Index(string filter = null, string? branchFilter = null)
        {
            ViewData["Title"] = "Orders Management";

            // Get user's branch context
            var userBranchId = User.GetBranchId();
            var isOwner = User.HasAllBranchAccess();
            if (!isOwner && string.IsNullOrWhiteSpace(userBranchId))
                return Forbid();

            List<Branch> allBranches = new();
            string? effectiveBranchId = userBranchId;
            if (isOwner)
            {
                allBranches = await _branchService.GetAllAsync();
                if (string.IsNullOrWhiteSpace(branchFilter) &&
                    Request.Cookies.TryGetValue("KdsAdminBranchFilter", out var savedBranchFilter))
                {
                    branchFilter = savedBranchFilter;
                }

                effectiveBranchId = string.Equals(branchFilter, "all", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : string.IsNullOrWhiteSpace(branchFilter) ? null : branchFilter.Trim();
                ViewBag.AllBranches = allBranches;
                ViewBag.BranchFilter = string.IsNullOrWhiteSpace(branchFilter) ? "all" : branchFilter;
                Response.Cookies.Append(
                    "KdsAdminBranchFilter",
                    ViewBag.BranchFilter,
                    new CookieOptions
                    {
                        HttpOnly = true,
                        IsEssential = true,
                        SameSite = SameSiteMode.Lax,
                        Secure = Request.IsHttps,
                        Expires = DateTimeOffset.UtcNow.AddDays(30)
                    });
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

            List<Order> orders;

            // Apply date filter if specified
            if (filter == "today")
            {
                var (todayStart, todayEnd) = AppClock.LocalDateRange(AppClock.LocalNow.Date);
                var todayOrders = await _orderService.GetByDateRangeHalfOpenAsync(todayStart, todayEnd, effectiveBranchId);
                orders = todayOrders ?? new List<Order>();
                ViewBag.FilterMessage = isOwner && effectiveBranchId == null ? "Showing today's orders (all branches)" : "Showing today's orders";
            }
            else
            {
                var historyStart = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var ordersInRange = await _orderService.GetByDateRangeHalfOpenAsync(historyStart, DateTime.UtcNow.AddDays(1), effectiveBranchId);
                orders = ordersInRange ?? new List<Order>();
                ViewBag.FilterMessage = isOwner && effectiveBranchId == null ? "Showing all orders (all branches)" : "Showing all orders";
            }

            ViewBag.IsOwner = isOwner;
            return View(orders);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStatus(string id, string status)
        {
            if (!OrderStatuses.TryNormalize(status, out var normalizedStatus))
            {
                TempData["Error"] = "Unsupported order status.";
                return RedirectToAction("Index");
            }

            var order = await _orderService.GetByIdAsync(id);
            
            // Branch managers can only update orders from their branch
            if (order != null)
            {
                var userBranchId = User.GetBranchId();
                var isOwner = User.HasAllBranchAccess();
                if (!isOwner && string.IsNullOrWhiteSpace(userBranchId))
                    return Forbid();

                if (!isOwner && !string.Equals(order.BranchId, userBranchId, StringComparison.OrdinalIgnoreCase))
                {
                    TempData["Error"] = "You can only update orders from your assigned branch.";
                    return RedirectToAction("Index");
                }

                var previousStatus = order.Status;
                await _orderService.UpdateStatusAsync(id, normalizedStatus);
                await _managementLogs.RecordAsync(
                    "Status changed",
                    "Order",
                    $"Order #{order.OrderNumber} status changed",
                    order.Id,
                    order.OrderNumber,
                    $"{previousStatus} -> {normalizedStatus}",
                    order.BranchId,
                    User.GetUsername(),
                    category: "Order");
                TempData["Message"] = $"Order #{order.OrderNumber} status updated to '{normalizedStatus}'.";
            }
            return RedirectToAction("Index");
        }
    }
}
