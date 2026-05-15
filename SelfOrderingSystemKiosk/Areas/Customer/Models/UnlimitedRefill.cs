using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System.Text.Json.Serialization;

namespace SelfOrderingSystemKiosk.Areas.Customer.Models
{
    public class UnlimitedRefill
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; }

        [BsonElement("anchorOrderId")]
        [JsonPropertyName("anchorOrderId")]
        public string AnchorOrderId { get; set; }

        [BsonElement("anchorOrderNumber")]
        [JsonPropertyName("anchorOrderNumber")]
        public string AnchorOrderNumber { get; set; }

        [BsonElement("requestedAtUtc")]
        [JsonPropertyName("requestedAtUtc")]
        public DateTime RequestedAtUtc { get; set; }

        [BsonElement("servedAtUtc")]
        [JsonPropertyName("servedAtUtc")]
        public DateTime? ServedAtUtc { get; set; }

        [BsonElement("status")]
        [JsonPropertyName("status")]
        public string Status { get; set; }

        [BsonElement("tableNumber")]
        [JsonPropertyName("tableNumber")]
        public string TableNumber { get; set; }

        [BsonElement("floor")]
        [JsonPropertyName("floor")]
        public string Floor { get; set; }

        [BsonElement("branchId")]
        [JsonPropertyName("branchId")]
        public string BranchId { get; set; }

        [BsonElement("items")]
        [JsonPropertyName("items")]
        public List<OrderItem> Items { get; set; }

        public UnlimitedRefill()
        {
            AnchorOrderId = string.Empty;
            AnchorOrderNumber = string.Empty;
            RequestedAtUtc = DateTime.UtcNow;
            Status = "New";
            TableNumber = string.Empty;
            Floor = string.Empty;
            BranchId = string.Empty;
            Items = new List<OrderItem>();
        }
    }
}
