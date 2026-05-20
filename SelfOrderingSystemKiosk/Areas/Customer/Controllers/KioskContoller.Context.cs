using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using SelfOrderingSystemKiosk.Areas.Customer.Models;
using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;
using System.Security.Cryptography;
using Order = SelfOrderingSystemKiosk.Areas.Customer.Models.Order;

namespace SelfOrderingSystemKiosk.Areas.Customer.Controllers
{
    public partial class KioskController
    {
        private async Task<string> ResolveOrderBranchIdAsync(string? tableNumber)
        {
            var sessionBranchId = HttpContext.Session.GetString(SessionServiceBranch);
            if (!string.IsNullOrWhiteSpace(sessionBranchId))
                return sessionBranchId;

            if (string.IsNullOrWhiteSpace(tableNumber) || IsDefaultServiceTable(tableNumber))
                return string.Empty;

            var registeredTable = await _tableRegistry.GetByTableNumberAsync(tableNumber);
            return registeredTable?.BranchId ?? string.Empty;
        }

        private async Task<string> ResolveTableBranchContextAsync(string? tableNumber, string? branchId = null)
        {
            if (!string.IsNullOrWhiteSpace(branchId))
                return branchId.Trim();

            return await ResolveOrderBranchIdAsync(tableNumber);
        }

        private async Task<string> ResolveQrBranchIdAsync(string tableNumber, string? branchId)
        {
            if (!string.IsNullOrWhiteSpace(branchId))
                return branchId.Trim();

            var registeredTable = await _tableRegistry.GetByTableNumberAsync(tableNumber);
            return registeredTable?.BranchId ?? string.Empty;
        }

        private async Task<List<MenuItem>> GetAvailableMenuForCurrentContextAsync()
        {
            var channel = HttpContext.Session.GetString(SessionOrderChannel) ?? OrderChannelKiosk;
            var branchId = string.Equals(channel, OrderChannelQr, StringComparison.OrdinalIgnoreCase)
                ? HttpContext.Session.GetString(SessionServiceBranch)
                : null;

            return !string.IsNullOrWhiteSpace(branchId)
                ? await _menuItems.GetAvailableByBranchAsync(branchId)
                : await _menuItems.GetAvailableAsync();
        }

        [HttpGet]
        public async Task<IActionResult> GetSessionInfo(string orderNumber = null, string accessToken = null)
        {
            RestoreOrderingCookiesToSession();

            if (!string.IsNullOrWhiteSpace(orderNumber))
            {
                var order = await _orderService.GetByOrderNumberAsync(orderNumber, accessToken: accessToken);
                if (order == null || !IsUnlimitedOrder(order))
                {
                    HttpContext.Session.Remove(SessionFirstOrderTime);
                    return Json(new { hasSession = false, sessionHours = (int)OrderingSessionLength.TotalHours });
                }

                if (order.BillArchived)
                {
                    HttpContext.Session.Remove(SessionFirstOrderTime);
                    await ClearRememberedPersonCountAsync(order.TableNumber, order.BranchId);
                    return Json(new
                    {
                        hasSession = false,
                        sessionEnded = true,
                        sessionHours = (int)OrderingSessionLength.TotalHours
                    });
                }

                if (HasPrivateOrderAccess(order, accessToken))
                    RememberOrderAccess(order);

                RestoreQrSessionFromOrder(order);
                var orderSessionStart = GetOrderSessionStartUtc(order);
                if (!string.IsNullOrWhiteSpace(order.TableNumber) &&
                    string.Equals(order.DiningType, "DineIn", StringComparison.OrdinalIgnoreCase))
                {
                    await CheckTableOrderingGateAsync(order.TableNumber, order.BranchId);
                }
                else if (orderSessionStart.HasValue)
                {
                    HttpContext.Session.SetString(SessionFirstOrderTime, orderSessionStart.Value.ToString("O"));
                }
                else
                {
                    HttpContext.Session.Remove(SessionFirstOrderTime);
                }
            }

            var channel = HttpContext.Session.GetString(SessionOrderChannel) ?? OrderChannelKiosk;
            if (string.Equals(channel, OrderChannelQr, StringComparison.OrdinalIgnoreCase))
            {
                var table = HttpContext.Session.GetString(SessionServiceTable);
                if (!string.IsNullOrWhiteSpace(table) &&
                    await HasEndedTableSessionReadyForNewSessionAsync(table, HttpContext.Session.GetString(SessionServiceBranch)))
                {
                    await ClearRememberedPersonCountAsync(table, HttpContext.Session.GetString(SessionServiceBranch));
                    return Json(new
                    {
                        hasSession = false,
                        sessionEnded = true,
                        resetPersonCount = true,
                        sessionHours = (int)OrderingSessionLength.TotalHours
                    });
                }

                if (!string.IsNullOrWhiteSpace(table))
                {
                    var branchId = HttpContext.Session.GetString(SessionServiceBranch);
                    await CheckTableOrderingGateAsync(table, branchId);
                    await RestoreSharedTablePersonCountAsync(table, branchId);
                }
            }

            var firstOrderTime = GetFirstOrderTimeUtc();
            if (!firstOrderTime.HasValue)
                return Json(new
                {
                    hasSession = false,
                    sessionHours = (int)OrderingSessionLength.TotalHours,
                    personCount = GetSessionInt(SessionPersonCount) ?? 0
                });

            var sessionLimit = OrderingSessionLength;
            var timeSinceFirstOrder = DateTime.UtcNow - firstOrderTime.Value;
            var timeRemaining = sessionLimit - timeSinceFirstOrder;
            var isExpired = timeRemaining <= TimeSpan.Zero;

            var maxSeconds = (int)sessionLimit.TotalSeconds;
            int timeRemainingSeconds = 0;
            if (!isExpired && timeRemaining.TotalSeconds > 0)
            {
                timeRemainingSeconds = Math.Max(0, Math.Min(maxSeconds, (int)timeRemaining.TotalSeconds));
            }

            // Calculate hours, minutes, and seconds for display.
            int hours = timeRemainingSeconds / 3600;
            int minutes = (timeRemainingSeconds % 3600) / 60;
            int seconds = timeRemainingSeconds % 60;

            return Json(new
            {
                hasSession = true,
                firstOrderTime = firstOrderTime,
                sessionHours = (int)OrderingSessionLength.TotalHours,
                sessionEndsAt = firstOrderTime.Value.Add(sessionLimit),
                timeRemainingSeconds = timeRemainingSeconds,
                timeRemainingHours = hours,
                timeRemainingMinutes = minutes,
                isExpired = isExpired,
                personCount = GetSessionInt(SessionPersonCount) ?? 0,
                timeRemainingFormatted = isExpired ? "00:00:00" : $"{hours:D2}:{minutes:D2}:{seconds:D2}"
            });
        }

        [HttpGet]
        public async Task<IActionResult> GetOrderStatus(string orderNumber, string accessToken = null)
        {
            if (string.IsNullOrEmpty(orderNumber))
                return Json(new { status = "" });

            var order = await _orderService.GetByOrderNumberAsync(orderNumber, accessToken: accessToken);
            if (order == null)
                return Json(new { status = "" });

            return Json(new { status = order.Status });
        }

        [HttpGet]
        public async Task<IActionResult> LookupOrder(string orderNumber)
        {
            if (string.IsNullOrEmpty(orderNumber))
            {
                TempData["ErrorMessage"] = "Please enter an order number";
                return RedirectToAction("Index");
            }

            var order = await _orderService.GetByOrderNumberAsync(orderNumber);
            if (order == null)
            {
                TempData["ErrorMessage"] = $"Order #{orderNumber} not found. Please check your order number and try again.";
                return RedirectToAction("Index");
            }

            return RedirectToAction("Confirmation", new { orderNumber = orderNumber });
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("customer-order-write")]
        public async Task<IActionResult> CancelOrder(string orderNumber, string accessToken = null)
        {
            try
            {
                if (string.IsNullOrEmpty(orderNumber))
                {
                    return Json(new { success = false, message = "Order number is required" });
                }

                var order = await _orderService.GetByOrderNumberAsync(orderNumber, accessToken: accessToken);
                if (order == null)
                {
                    return Json(new { success = false, message = "Order not found" });
                }
                if (!HasPrivateOrderAccess(order, accessToken))
                {
                    return Json(new { success = false, message = "Please use the original confirmation link for this order." });
                }
                if (!string.Equals(order.Status, "Pending", StringComparison.OrdinalIgnoreCase))
                {
                    return Json(new { success = false, message = $"Cannot cancel order. Order status is: {order.Status}" });
                }

                // Cancel the order
                await _orderService.CancelOrderAsync(order.Id);
                order.Status = "Canceled";
                if (IsUnlimitedOrder(order) &&
                    string.Equals(order.OrderChannel, OrderChannelQr, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(order.TableNumber))
                {
                    await RebuildSharedTableSessionFromOrdersAsync(order.TableNumber, order.BranchId);
                }
                await _realtime.NotifyOrderChangedAsync(order, "order-canceled");

                return Json(new { success = true, message = "Order cancelled successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cancelling order");
                return Json(new { success = false, message = "Error cancelling order. Please try again." });
            }
        }
    }
}
