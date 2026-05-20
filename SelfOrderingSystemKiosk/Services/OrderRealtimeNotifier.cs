using Microsoft.AspNetCore.SignalR;
using SelfOrderingSystemKiosk.Areas.Customer.Models;
using SelfOrderingSystemKiosk.Hubs;

namespace SelfOrderingSystemKiosk.Services
{
    public class OrderRealtimeNotifier
    {
        private readonly IHubContext<OrderRealtimeHub> _hub;

        public OrderRealtimeNotifier(IHubContext<OrderRealtimeHub> hub)
        {
            _hub = hub;
        }

        public async Task NotifyKitchenChangedAsync(string? branchId, string eventType)
        {
            var payload = new
            {
                eventType,
                branchId = branchId ?? string.Empty,
                updatedAtUtc = DateTime.UtcNow
            };

            await _hub.Clients.Group(OrderRealtimeGroups.AllKitchen)
                .SendAsync("KitchenBoardChanged", payload);

            if (!string.IsNullOrWhiteSpace(branchId))
            {
                await _hub.Clients.Group(OrderRealtimeGroups.KitchenBranch(branchId))
                    .SendAsync("KitchenBoardChanged", payload);
            }
        }

        public async Task NotifyOrderChangedAsync(Order? order, string eventType)
        {
            if (order == null)
                return;

            var payload = new
            {
                eventType,
                orderId = order.Id ?? string.Empty,
                orderNumber = order.OrderNumber ?? string.Empty,
                status = order.Status ?? string.Empty,
                paymentStatus = order.PaymentStatus ?? string.Empty,
                branchId = order.BranchId ?? string.Empty,
                tableNumber = order.TableNumber ?? string.Empty,
                updatedAtUtc = DateTime.UtcNow
            };

            await NotifyKitchenChangedAsync(order.BranchId, eventType);

            if (!string.IsNullOrWhiteSpace(order.OrderNumber) &&
                !string.IsNullOrWhiteSpace(order.PublicAccessToken))
            {
                await _hub.Clients
                    .Group(OrderRealtimeGroups.Order(order.OrderNumber, order.PublicAccessToken))
                    .SendAsync("OrderChanged", payload);
            }
        }

        public async Task NotifyOrdersChangedAsync(IEnumerable<Order> orders, string eventType)
        {
            foreach (var order in orders)
                await NotifyOrderChangedAsync(order, eventType);
        }
    }
}
