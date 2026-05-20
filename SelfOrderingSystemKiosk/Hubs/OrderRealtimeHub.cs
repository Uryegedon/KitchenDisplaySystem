using Microsoft.AspNetCore.SignalR;
using SelfOrderingSystemKiosk.Services;

namespace SelfOrderingSystemKiosk.Hubs
{
    public class OrderRealtimeHub : Hub
    {
        private readonly OrderService _orders;

        public OrderRealtimeHub(OrderService orders)
        {
            _orders = orders;
        }

        public async Task WatchKitchenBoard()
        {
            var user = Context.User;
            if (user?.Identity?.IsAuthenticated != true)
                return;

            if (user.HasAllBranchAccess())
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, OrderRealtimeGroups.AllKitchen);
                return;
            }

            var branchId = user.GetBranchId();
            if (!string.IsNullOrWhiteSpace(branchId))
                await Groups.AddToGroupAsync(Context.ConnectionId, OrderRealtimeGroups.KitchenBranch(branchId));
        }

        public async Task WatchOrder(string orderNumber, string accessToken)
        {
            var order = await _orders.GetByOrderNumberAsync(orderNumber, accessToken: accessToken);
            if (order == null ||
                string.IsNullOrWhiteSpace(order.OrderNumber) ||
                string.IsNullOrWhiteSpace(order.PublicAccessToken))
            {
                return;
            }

            await Groups.AddToGroupAsync(
                Context.ConnectionId,
                OrderRealtimeGroups.Order(order.OrderNumber, order.PublicAccessToken));
        }
    }
}
