using SelfOrderingSystemKiosk.Areas.Customer.Models;

namespace SelfOrderingSystemKiosk.Areas.Kitchen.Models
{
    public class SessionReceiptViewModel
    {
        private const decimal UnlimitedPricePerHead = 477m;

        public List<Order> Orders { get; set; } = new();
        public Order? AnchorOrder { get; set; }
        public DateTime SessionStartUtc { get; set; }
        public DateTime SessionEndUtc { get; set; }
        public bool HasSessionStarted { get; set; }
        public string TableNumber { get; set; } = string.Empty;
        public string BranchId { get; set; } = string.Empty;
        public string Floor { get; set; } = string.Empty;
        public string LocationLabel { get; set; } = string.Empty;
        public bool IsTableSession { get; set; }

        public IEnumerable<Order> BillableOrders =>
            Orders.Where(o => !string.Equals(o.Status, "Canceled", StringComparison.OrdinalIgnoreCase));

        public decimal Subtotal => LineItems.Sum(i => i.LineTotal);
        public decimal Tax => BillableOrders.Sum(o => o.Tax);
        public decimal Total => Subtotal;
        public int TotalItems => BillableOrders.SelectMany(o => o.Items ?? new List<OrderItem>()).Sum(i => i.Quantity);
        public string ReceiptNumber => AnchorOrder?.OrderNumber ?? string.Empty;

        public IReadOnlyList<ReceiptLineItem> LineItems =>
            BuildLineItems();

        private IReadOnlyList<ReceiptLineItem> BuildLineItems()
        {
            var billableOrders = BillableOrders.ToList();
            var unlimitedPersonCount = billableOrders
                .Where(IsUnlimitedOrder)
                .Select(GetOrderPersonCount)
                .DefaultIfEmpty(0)
                .Max();

            var lineItems = billableOrders
                .SelectMany(o => o.Items ?? new List<OrderItem>())
                .GroupBy(i => new { i.ItemName, i.Price })
                .Select(g => new ReceiptLineItem
                {
                    ItemName = g.Key.ItemName,
                    Price = g.Key.Price,
                    Quantity = g.Sum(i => i.Quantity)
                })
                .OrderBy(i => i.ItemName)
                .ToList();

            if (unlimitedPersonCount > 0)
            {
                lineItems.Insert(0, new ReceiptLineItem
                {
                    ItemName = "Unlimited Dine-In",
                    Price = UnlimitedPricePerHead,
                    Quantity = unlimitedPersonCount
                });
            }

            return lineItems;
        }

        private static bool IsUnlimitedOrder(Order order) =>
            string.Equals(order.OrderType, "Unlimited", StringComparison.OrdinalIgnoreCase);

        private static int GetOrderPersonCount(Order order)
        {
            if (order?.PersonCount is > 0)
                return order.PersonCount.Value;

            if (order != null && IsUnlimitedOrder(order) && order.Subtotal >= UnlimitedPricePerHead)
                return Math.Max(1, (int)Math.Floor(order.Subtotal / UnlimitedPricePerHead));

            return 0;
        }
    }

    public class ReceiptLineItem
    {
        public string ItemName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public decimal LineTotal => Price * Quantity;
    }

    public class TableOverviewViewModel
    {
        public string TableNumber { get; set; } = string.Empty;
        public string BranchId { get; set; } = string.Empty;
        public string Floor { get; set; } = string.Empty;
        public string LocationLabel { get; set; } = string.Empty;
        public bool IsOccupied { get; set; }
        public bool CanManageOrdering { get; set; }
        public TableOrderingSession? OrderingSession { get; set; }
        public SessionReceiptViewModel? Receipt { get; set; }
        public List<SessionReceiptViewModel> Receipts { get; set; } = new();
        public bool HasBill => Receipts.Any(r => r.Orders.Any());
        public bool IsPaid => HasBill && Receipts.All(r => r.Orders.All(o => string.Equals(o.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase)));
        public decimal Total => Receipts.Sum(r => r.Total);
        public int OrderCount => Receipts.Sum(r => r.Orders.Count);
        public int PersonCount => OrderingSession?.PersonCount ?? Receipts.SelectMany(r => r.Orders).Select(o => o.PersonCount ?? 0).DefaultIfEmpty(0).Max();
        public DateTime? LastActivityUtc { get; set; }
    }
}
