using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using SelfOrderingSystemKiosk.Areas.Customer.Models;
using SelfOrderingSystemKiosk.Areas.Kitchen.Models;
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

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> Receipt(string? id = null, string? orderNumber = null, string? returnUrl = null)
        {
            Order? anchorOrder = null;
            var isSignedIn = User?.Identity?.IsAuthenticated == true;

            if (isSignedIn && !string.IsNullOrWhiteSpace(id))
                anchorOrder = await _orderService.GetByIdAsync(id);
            else if (!string.IsNullOrWhiteSpace(orderNumber))
                anchorOrder = await _orderService.GetByOrderNumberAsync(orderNumber);

            if (anchorOrder == null)
            {
                if (isSignedIn)
                    return RedirectToAction("Index");

                return RedirectToAction("Index", "Kiosk", new { area = "Customer" });
            }

            ViewBag.ReturnUrl = GetSafeReturnUrl(returnUrl, anchorOrder, isSignedIn);
            ViewBag.CanManagePayment = isSignedIn;
            return View(await BuildReceiptViewModelAsync(anchorOrder));
        }

        [HttpGet]
        public async Task<IActionResult> Receipts([FromQuery] string? dateFilter = "day")
        {
            var orders = await _orderService.GetOrdersForKitchenAsync(dateFilter);
            var receipts = new List<SessionReceiptViewModel>();
            var coveredOrderIds = new HashSet<string>();

            foreach (var order in orders
                .Where(o => !string.Equals(o.Status, "Canceled", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(o => o.OrderDate))
            {
                if (!string.IsNullOrEmpty(order.Id) && coveredOrderIds.Contains(order.Id))
                    continue;

                var receipt = await BuildReceiptViewModelAsync(order);
                receipts.Add(receipt);

                foreach (var included in receipt.Orders)
                {
                    if (!string.IsNullOrEmpty(included.Id))
                        coveredOrderIds.Add(included.Id);
                }
            }

            ViewBag.DateFilter = dateFilter;
            return View(receipts.OrderByDescending(r => r.SessionStartUtc).ToList());
        }

        [HttpPost]
        public async Task<IActionResult> UpdatePaymentStatus(string id, string paymentStatus, string? returnUrl = null)
        {
            if (string.IsNullOrWhiteSpace(id))
                return RedirectToAction("Receipts");

            var anchorOrder = await _orderService.GetByIdAsync(id);
            if (anchorOrder == null)
                return RedirectToAction("Receipts");

            var normalizedStatus = string.Equals(paymentStatus, "Paid", StringComparison.OrdinalIgnoreCase)
                ? "Paid"
                : "Pending";

            var receipt = await BuildReceiptViewModelAsync(anchorOrder);
            await _orderService.UpdatePaymentStatusAsync(receipt.Orders.Select(o => o.Id), normalizedStatus);

            return RedirectToAction("Receipt", new { id, returnUrl = GetSafeReturnUrl(returnUrl, anchorOrder, true) });
        }

        private async Task<SessionReceiptViewModel> BuildReceiptViewModelAsync(Order anchorOrder)
        {
            var orders = new List<Order> { anchorOrder };
            var isTableSession = !string.IsNullOrWhiteSpace(anchorOrder.TableNumber)
                && string.Equals(anchorOrder.DiningType, "DineIn", StringComparison.OrdinalIgnoreCase);

            if (isTableSession)
            {
                var tableOrders = await _orderService.GetOrdersByTableAsync(anchorOrder.TableNumber);
                orders = GetOrdersInSameSession(tableOrders, anchorOrder);
            }

            var sessionStart = orders.Min(o => o.OrderDate);
            var locationLabel = BuildLocationLabel(anchorOrder.Floor, anchorOrder.TableNumber);

            return new SessionReceiptViewModel
            {
                Orders = orders.OrderBy(o => o.OrderDate).ToList(),
                AnchorOrder = anchorOrder,
                SessionStartUtc = sessionStart,
                SessionEndUtc = sessionStart.AddHours(2),
                TableNumber = anchorOrder.TableNumber,
                Floor = anchorOrder.Floor,
                LocationLabel = locationLabel,
                IsTableSession = isTableSession
            };
        }

        private static List<Order> GetOrdersInSameSession(List<Order> tableOrders, Order anchorOrder)
        {
            var ordered = tableOrders
                .Where(o => !string.Equals(o.Status, "Canceled", StringComparison.OrdinalIgnoreCase))
                .OrderBy(o => o.OrderDate)
                .ToList();

            if (!ordered.Any())
                return new List<Order> { anchorOrder };

            DateTime sessionStart = ordered.First().OrderDate;
            foreach (var order in ordered)
            {
                if (order.OrderDate >= sessionStart.AddHours(2))
                    sessionStart = order.OrderDate;

                if (order.Id == anchorOrder.Id)
                    break;
            }

            var sessionEnd = sessionStart.AddHours(2);
            return ordered
                .Where(o => o.OrderDate >= sessionStart && o.OrderDate < sessionEnd)
                .ToList();
        }

        private static string BuildLocationLabel(string floor, string table)
        {
            if (string.IsNullOrWhiteSpace(table))
                return "Kiosk / Take out";

            return string.IsNullOrWhiteSpace(floor)
                ? $"Table {table}"
                : $"Floor {floor} - Table {table}";
        }

        private string GetSafeReturnUrl(string? returnUrl, Order? anchorOrder = null, bool isSignedIn = true)
        {
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                return returnUrl;

            if (!isSignedIn && !string.IsNullOrWhiteSpace(anchorOrder?.OrderNumber))
            {
                return Url.Action("Confirmation", "Kiosk", new { area = "Customer", orderNumber = anchorOrder.OrderNumber })
                    ?? "/Customer/Kiosk";
            }

            return Url.Action("Receipts", "Kitchen", new { area = "Kitchen" }) ?? "/Kitchen/Kitchen/Receipts";
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
