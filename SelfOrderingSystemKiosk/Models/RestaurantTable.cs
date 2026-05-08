using MongoDB.Bson.Serialization.Attributes;

namespace SelfOrderingSystemKiosk.Models
{
    public class RestaurantTable
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;

        [BsonElement("tableNumber")]
        public string TableNumber { get; set; } = string.Empty;

        [BsonElement("floor")]
        public string Floor { get; set; } = string.Empty;

        [BsonElement("branchId")]
        public string BranchId { get; set; } = string.Empty;

        [BsonElement("qrToken")]
        public string QrToken { get; set; } = string.Empty;

        [BsonElement("createdAtUtc")]
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        [BsonElement("updatedAtUtc")]
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
