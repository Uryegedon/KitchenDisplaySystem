using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using SelfOrderingSystemKiosk.Areas.Customer.Models;
using SelfOrderingSystemKiosk.Areas.Kitchen.Models;
using SelfOrderingSystemKiosk.Services;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

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
        public async Task<IActionResult> Receipt(string? id = null, string? orderNumber = null, string? accessToken = null, string? returnUrl = null)
        {
            await _orderService.ExpirePendingOrdersAsync();

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
            if (!isSignedIn && !HasPublicReceiptAccess(anchorOrder, accessToken))
                return RedirectToAction("Index", "Kiosk", new { area = "Customer" });

            ViewBag.ReturnUrl = GetSafeReturnUrl(returnUrl, anchorOrder, isSignedIn);
            ViewBag.CanManagePayment = isSignedIn
                && !string.IsNullOrWhiteSpace(id)
                && string.IsNullOrWhiteSpace(orderNumber);
            var canViewTableSession = isSignedIn
                && !string.IsNullOrWhiteSpace(id)
                && string.IsNullOrWhiteSpace(orderNumber);
            return View(await BuildReceiptViewModelAsync(anchorOrder, canViewTableSession));
        }

        [HttpGet]
        public async Task<IActionResult> Receipts([FromQuery] string? dateFilter = "day", [FromQuery] bool showArchived = false)
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
                foreach (var included in receipt.Orders)
                {
                    if (!string.IsNullOrEmpty(included.Id))
                        coveredOrderIds.Add(included.Id);
                }

                if (!showArchived && ShouldHideClosedReceipt(receipt))
                    continue;

                receipts.Add(receipt);
            }

            ViewBag.DateFilter = dateFilter;
            ViewBag.ShowArchived = showArchived;
            return View(receipts.OrderByDescending(r => r.SessionStartUtc).ToList());
        }

        [HttpPost]
        public async Task<IActionResult> UpdatePaymentStatus(string id, string paymentStatus, string? returnUrl = null)
        {
            if (string.IsNullOrWhiteSpace(id))
                return RedirectToAction("Receipts");

            await _orderService.ExpirePendingOrdersAsync();

            var anchorOrder = await _orderService.GetByIdAsync(id);
            if (anchorOrder == null)
                return RedirectToAction("Receipts");

            if (string.Equals(anchorOrder.Status, "Canceled", StringComparison.OrdinalIgnoreCase))
            {
                TempData["ErrorMessage"] = "This order was canceled because it was not started within 24 hours.";
                return RedirectToAction("Receipt", new { id, returnUrl = GetSafeReturnUrl(returnUrl, anchorOrder, true) });
            }

            if (!string.Equals(paymentStatus, "Paid", StringComparison.OrdinalIgnoreCase))
                return RedirectToAction("Receipt", new { id, returnUrl = GetSafeReturnUrl(returnUrl, anchorOrder, true) });

            var receipt = await BuildReceiptViewModelAsync(anchorOrder);
            await _orderService.UpdatePaymentStatusAsync(receipt.Orders.Select(o => o.Id), "Paid");

            if (ShouldHideClosedReceipt(receipt))
                return RedirectToAction("Receipts");

            return RedirectToAction("Receipt", new { id, returnUrl = GetSafeReturnUrl(returnUrl, anchorOrder, true) });
        }

        [HttpPost]
        public async Task<IActionResult> EndSession(string id, string? returnUrl = null)
        {
            if (string.IsNullOrWhiteSpace(id))
                return RedirectToAction("Receipts");

            await _orderService.ExpirePendingOrdersAsync();

            var anchorOrder = await _orderService.GetByIdAsync(id);
            if (anchorOrder == null)
                return RedirectToAction("Receipts");

            if (string.Equals(anchorOrder.Status, "Canceled", StringComparison.OrdinalIgnoreCase))
            {
                TempData["ErrorMessage"] = "This order was canceled because it was not started within 24 hours.";
                return RedirectToAction("Receipt", new { id, returnUrl = GetSafeReturnUrl(returnUrl, anchorOrder, true) });
            }

            var receipt = await BuildReceiptViewModelAsync(anchorOrder);
            if (!receipt.IsTableSession)
                return RedirectToAction("Receipt", new { id, returnUrl = GetSafeReturnUrl(returnUrl, anchorOrder, true) });

            var isPaid = receipt.Orders.All(o => string.Equals(o.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase));
            if (!isPaid)
            {
                TempData["ErrorMessage"] = "This table session can only be ended after the bill is marked as paid.";
                return RedirectToAction("Receipt", new { id, returnUrl = GetSafeReturnUrl(returnUrl, anchorOrder, true) });
            }

            await _orderService.ArchiveBillsAsync(receipt.Orders.Select(o => o.Id));
            return RedirectToAction("Receipts");
        }

        private async Task<SessionReceiptViewModel> BuildReceiptViewModelAsync(Order anchorOrder, bool includeTableSession = true)
        {
            var orders = new List<Order> { anchorOrder };
            var isAnchorCanceled = string.Equals(anchorOrder.Status, "Canceled", StringComparison.OrdinalIgnoreCase);
            var isTableSession = includeTableSession
                && !isAnchorCanceled
                && !string.IsNullOrWhiteSpace(anchorOrder.TableNumber)
                && string.Equals(anchorOrder.DiningType, "DineIn", StringComparison.OrdinalIgnoreCase);

            if (isTableSession)
            {
                var tableOrders = await _orderService.GetOrdersByTableAsync(anchorOrder.TableNumber);
                orders = GetOrdersInSameSession(tableOrders, anchorOrder);
            }

            var sessionStart = orders
                .Where(o => string.Equals(o.OrderType, "Unlimited", StringComparison.OrdinalIgnoreCase))
                .Select(o => (DateTime?)o.OrderDate)
                .Min() ?? orders.Min(o => o.OrderDate);
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
            var includeArchived = anchorOrder.BillArchived;
            var ordered = tableOrders
                .Where(o => !string.Equals(o.Status, "Canceled", StringComparison.OrdinalIgnoreCase)
                    && (includeArchived || !o.BillArchived))
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

        private static bool ShouldHideClosedReceipt(SessionReceiptViewModel receipt)
        {
            return receipt.Orders.Any()
                && (receipt.Orders.All(o => o.BillArchived)
                    || (receipt.SessionEndUtc <= DateTime.UtcNow
                        && receipt.Orders.All(o => string.Equals(o.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase))));
        }

        private static string BuildLocationLabel(string floor, string table)
        {
            if (string.IsNullOrWhiteSpace(table))
                return "Kiosk / Take out";

            return string.IsNullOrWhiteSpace(floor)
                ? $"Table {table}"
                : $"Floor {floor} - Table {table}";
        }

        private static bool HasPublicReceiptAccess(Order order, string? accessToken)
        {
            if (string.IsNullOrWhiteSpace(order.PublicAccessToken))
                return false;
            if (string.IsNullOrWhiteSpace(accessToken))
                return false;

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(order.PublicAccessToken),
                Encoding.UTF8.GetBytes(accessToken.Trim()));
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
            await _orderService.ExpirePendingOrdersAsync();

            // Get the order to check current status
            var order = await _orderService.GetByIdAsync(id);
            
            if (order == null)
            {
                return RedirectToAction("Index");
            }

            if (order.Status.Equals("Canceled", StringComparison.OrdinalIgnoreCase))
            {
                TempData["ErrorMessage"] = "This order was canceled because it was not started within 24 hours.";
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
