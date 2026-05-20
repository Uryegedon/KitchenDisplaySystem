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
    public partial class KitchenController
    {
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

            if (!CanAccessOrder(order))
            {
                return Forbid();
            }

            if (string.IsNullOrWhiteSpace(status))
            {
                TempData["ErrorMessage"] = "Choose a valid order action.";
                return RedirectToAction("Index");
            }
            if (!string.Equals(status, "In Progress", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status, "Canceled", StringComparison.OrdinalIgnoreCase))
            {
                TempData["ErrorMessage"] = "Unsupported order action.";
                return RedirectToAction("Index");
            }

            if (string.Equals(order.Status, "Canceled", StringComparison.OrdinalIgnoreCase))
            {
                TempData["ErrorMessage"] = "This order was canceled because it was not started within 24 hours.";
                return RedirectToAction("Index");
            }

            if (string.Equals(status, "Canceled", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(order.Status, "Pending", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(order.Status, "In Progress", StringComparison.OrdinalIgnoreCase))
                {
                    TempData["ErrorMessage"] = "Only pending or in-progress orders can be canceled.";
                    return RedirectToAction("Index");
                }

                await _orderService.CancelOrderAsync(order.Id);
                order.Status = "Canceled";
                if (string.Equals(order.OrderType, "Unlimited", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(order.OrderChannel, "Qr", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(order.TableNumber))
                {
                    await RebuildSharedTableSessionFromOrdersAsync(order.TableNumber, order.BranchId);
                }

                await _realtime.NotifyOrderChangedAsync(order, "order-canceled");
                TempData["SuccessMessage"] = $"Order #{order.OrderNumber} has been canceled.";
                return RedirectToAction("Index");
            }

            // Prevent marking as "Completed" if order is still "Pending"
            if (string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(order.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                TempData["ErrorMessage"] = "Cannot mark order as done. Please start the order first.";
                return RedirectToAction("Index");
            }

            // If status is being changed to "Completed", claim the transition first so a retry cannot double-deduct stock.
            if (string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                var transitioned = await _orderService.UpdateStatusIfCurrentAsync(id, "In Progress", "Completed");
                if (!transitioned)
                {
                    TempData["ErrorMessage"] = "Order was already updated. Please refresh the kitchen board.";
                    return RedirectToAction("Index");
                }

                if (order.Items != null && order.Items.Any())
                {
                    // Deduct ingredient stock for each menu item recipe in the order.
                    var orderCost = await _menuItems.CalculateOrderCostAsync(order.Items, order.BranchId);
                    await _orderService.UpdateCompletionCostAsync(order.Id, orderCost);

                    foreach (var orderItem in order.Items)
                    {
                        if (!string.IsNullOrEmpty(orderItem.ItemName) && orderItem.Quantity > 0)
                        {
                            try
                            {
                                await _menuItems.DecrementStockAsync(orderItem.ItemName, orderItem.Quantity, "Sale", "Order", order.Id, order.BranchId);
                                _logger.LogInformation("Deducted recipe ingredients for {Item} by {Qty}", orderItem.ItemName, orderItem.Quantity);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error deducting recipe ingredients for {Item}", orderItem.ItemName);
                            }
                        }
                    }
                }

                var completedOrder = await _orderService.GetByIdAsync(id);
                await _realtime.NotifyOrderChangedAsync(completedOrder, "order-status-changed");
                return RedirectToAction("Index");
            }

            DateTime? sessionStartedAtUtc = null;
            if (string.Equals(status, "In Progress", StringComparison.OrdinalIgnoreCase))
                sessionStartedAtUtc = await GetSessionStartForStaffStartAsync(order);

            // Update the order status
            await _orderService.UpdateStatusAsync(id, status, sessionStartedAtUtc);
            var updatedOrder = await _orderService.GetByIdAsync(id);
            await _realtime.NotifyOrderChangedAsync(updatedOrder, "order-status-changed");
            return RedirectToAction("Index");
        }

        private string? GetKitchenBranchFilter()
        {
            return TryGetKitchenBranchFilter(out var branchId) ? branchId : null;
        }

        private bool TryGetKitchenBranchFilter(out string? branchId)
        {
            if (User.HasAllBranchAccess())
            {
                branchId = null;
                return true;
            }

            branchId = User.GetBranchId();
            if (string.IsNullOrWhiteSpace(branchId))
            {
                branchId = null;
                return false;
            }

            branchId = branchId.Trim();
            return true;
        }

        private bool CanAccessOrder(Order order)
        {
            if (!TryGetKitchenBranchFilter(out var branchId))
                return false;

            if (string.IsNullOrWhiteSpace(branchId))
                return true;

            return !string.IsNullOrWhiteSpace(order.BranchId)
                && string.Equals(order.BranchId, branchId, StringComparison.OrdinalIgnoreCase);
        }

        private bool CanAccessRefill(UnlimitedRefill refill)
        {
            if (!TryGetKitchenBranchFilter(out var branchId))
                return false;

            if (string.IsNullOrWhiteSpace(branchId))
                return true;

            return !string.IsNullOrWhiteSpace(refill.BranchId)
                && string.Equals(refill.BranchId, branchId, StringComparison.OrdinalIgnoreCase);
        }

        private string? GetEffectiveKitchenBranchId(string? requestedBranchId)
        {
            var kitchenBranchId = GetKitchenBranchFilter();
            if (!string.IsNullOrWhiteSpace(kitchenBranchId))
                return kitchenBranchId;

            return string.IsNullOrWhiteSpace(requestedBranchId)
                ? null
                : requestedBranchId.Trim();
        }

        private async Task<string?> GetEffectiveKitchenBranchIdAsync(string table, string? requestedBranchId)
        {
            var effectiveBranchId = GetEffectiveKitchenBranchId(requestedBranchId);
            if (!string.IsNullOrWhiteSpace(effectiveBranchId))
                return effectiveBranchId;

            return null;
        }
    }
}
