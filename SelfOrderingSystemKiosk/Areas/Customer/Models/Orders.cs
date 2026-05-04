using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System.Text.Json.Serialization;

namespace SelfOrderingSystemKiosk.Areas.Customer.Models
{
    public class Order
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; }

        [BsonElement("orderNumber")]
        [JsonPropertyName("orderNumber")]
        public string OrderNumber { get; set; }

        [BsonElement("publicAccessToken")]
        [JsonPropertyName("publicAccessToken")]
        public string PublicAccessToken { get; set; }

        [BsonElement("orderDate")]
        [JsonPropertyName("orderDate")]
        public DateTime OrderDate { get; set; }

        [BsonElement("sessionStartedAtUtc")]
        [JsonPropertyName("sessionStartedAtUtc")]
        public DateTime? SessionStartedAtUtc { get; set; }

        [BsonElement("orderType")]
        [JsonPropertyName("orderType")]
        public string OrderType { get; set; }

        [BsonElement("diningType")]
        [JsonPropertyName("diningType")]
        public string DiningType { get; set; }

        [BsonElement("tableNumber")]
        [JsonPropertyName("tableNumber")]
        public string TableNumber { get; set; }

        [BsonElement("items")]
        [JsonPropertyName("items")]
        public List<OrderItem> Items { get; set; }

        [BsonElement("personCount")]
        [JsonPropertyName("personCount")]
        public int? PersonCount { get; set; }

        [BsonElement("subtotal")]
        [JsonPropertyName("subtotal")]
        public decimal Subtotal { get; set; }

        [BsonElement("tax")]
        [JsonPropertyName("tax")]
        public decimal Tax { get; set; }

        [BsonElement("total")]
        [JsonPropertyName("total")]
        public decimal Total { get; set; }

        [BsonElement("status")]
        [JsonPropertyName("status")]
        public string Status { get; set; }

        /// <summary>Kiosk (fixed terminal) or Qr (table scan).</summary>
        [BsonElement("orderChannel")]
        [JsonPropertyName("orderChannel")]
        public string OrderChannel { get; set; }

        /// <summary>Optional floor or zone label for dine-in routing (e.g. "2" or "Upstairs").</summary>
        [BsonElement("floor")]
        [JsonPropertyName("floor")]
        public string Floor { get; set; }

        /// <summary>Cash, GCash, Card — extend when integrating a PSP.</summary>
        [BsonElement("paymentMethod")]
        [JsonPropertyName("paymentMethod")]
        public string PaymentMethod { get; set; }

        /// <summary>Pending, Paid, Failed — cash often Pending until staff confirms.</summary>
        [BsonElement("paymentStatus")]
        [JsonPropertyName("paymentStatus")]
        public string PaymentStatus { get; set; }

        /// <summary>Hides paid, expired bills from the kitchen bills dashboard without deleting the order.</summary>
        [BsonElement("billArchived")]
        [JsonPropertyName("billArchived")]
        public bool BillArchived { get; set; }

        /// <summary>Optional branch identifier for multi-branch setups.</summary>
        [BsonElement("branchId")]
        [JsonPropertyName("branchId")]
        public string BranchId { get; set; }

        public Order()
        {
            Items = new List<OrderItem>();
            OrderDate = DateTime.UtcNow;
            Status = "Pending";
            OrderType = "AlaCarte";
            OrderChannel = "Kiosk";
            PaymentMethod = "Cash";
            PaymentStatus = "Pending";
            BillArchived = false;
            BranchId = string.Empty;
        }
    }

    public class OrderItem
    {
        [BsonElement("itemName")]
        [JsonPropertyName("ItemName")]
        public string ItemName { get; set; }

        [BsonElement("quantity")]
        [JsonPropertyName("Quantity")]
        public int Quantity { get; set; }

        [BsonElement("price")]
        [JsonPropertyName("Price")]
        public decimal Price { get; set; }
    }
}
