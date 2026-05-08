using MongoDB.Bson.Serialization.Attributes;

namespace SelfOrderingSystemKiosk.Areas.Customer.Models
{
    [BsonIgnoreExtraElements]
    public class TableOrderingSession
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;

        [BsonElement("tableNumber")]
        public string TableNumber { get; set; } = string.Empty;

        [BsonElement("branchId")]
        public string BranchId { get; set; } = string.Empty;

        [BsonElement("personCount")]
        public int PersonCount { get; set; }

        [BsonElement("billedPersonCount")]
        public int BilledPersonCount { get; set; }

        [BsonElement("wingFlavors")]
        public List<string> WingFlavors { get; set; } = new();

        [BsonElement("isOrderingOpen")]
        public bool IsOrderingOpen { get; set; }

        [BsonElement("orderingOpenedAtUtc")]
        public DateTime? OrderingOpenedAtUtc { get; set; }

        [BsonElement("orderingClosedAtUtc")]
        public DateTime? OrderingClosedAtUtc { get; set; }

        [BsonElement("createdAtUtc")]
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        [BsonElement("updatedAtUtc")]
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
