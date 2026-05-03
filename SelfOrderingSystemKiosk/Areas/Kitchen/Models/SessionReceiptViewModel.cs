using SelfOrderingSystemKiosk.Areas.Customer.Models;

namespace SelfOrderingSystemKiosk.Areas.Kitchen.Models
{
    public class SessionReceiptViewModel
    {
        public List<Order> Orders { get; set; } = new();
        public Order? AnchorOrder { get; set; }
        public DateTime SessionStartUtc { get; set; }
        public DateTime SessionEndUtc { get; set; }
        public bool HasSessionStarted { get; set; }
        public string TableNumber { get; set; } = string.Empty;
        public string Floor { get; set; } = string.Empty;
        public string LocationLabel { get; set; } = string.Empty;
        public bool IsTableSession { get; set; }

        public IEnumerable<Order> BillableOrders =>
            Orders.Where(o => !string.Equals(o.Status, "Canceled", StringComparison.OrdinalIgnoreCase));

        public decimal Subtotal => BillableOrders.Sum(o => o.Subtotal);
        public decimal Tax => BillableOrders.Sum(o => o.Tax);
        public decimal Total => Subtotal;
        public int TotalItems => BillableOrders.SelectMany(o => o.Items ?? new List<OrderItem>()).Sum(i => i.Quantity);
        public string ReceiptNumber => AnchorOrder?.OrderNumber ?? string.Empty;

        public IReadOnlyList<ReceiptLineItem> LineItems =>
            BillableOrders
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
        public string Floor { get; set; } = string.Empty;
        public string LocationLabel { get; set; } = string.Empty;
        public bool IsOccupied { get; set; }
        public TableOrderingSession? OrderingSession { get; set; }
        public SessionReceiptViewModel? Receipt { get; set; }
        public bool HasBill => Receipt?.Orders.Any() == true;
        public bool IsPaid => HasBill && Receipt!.Orders.All(o => string.Equals(o.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase));
        public decimal Total => Receipt?.Total ?? 0m;
        public int OrderCount => Receipt?.Orders.Count ?? 0;
        public int PersonCount => OrderingSession?.PersonCount ?? Receipt?.Orders.Select(o => o.PersonCount ?? 0).DefaultIfEmpty(0).Max() ?? 0;
        public DateTime? LastActivityUtc { get; set; }
    }
}
