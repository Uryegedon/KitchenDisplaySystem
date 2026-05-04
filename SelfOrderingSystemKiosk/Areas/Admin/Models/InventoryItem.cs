using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace SelfOrderingSystemKiosk.Models
{
    public class InventoryItem
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; }  // MongoDB ObjectId

        [BsonElement("Item")]
        public string Item { get; set; }

        [BsonElement("Category")]
        public string Category { get; set; }

        /// <summary>Higher values sort first on kiosk and admin menu list; ties use item name.</summary>
        [BsonElement("menuOrder")]
        public int MenuOrder { get; set; }

        [BsonElement("CurrentStock")]
        public int CurrentStock { get; set; }

        [BsonElement("Unit")]
        public string Unit { get; set; }

        [BsonElement("ReorderLevel")]
        public int ReorderLevel { get; set; }

        [BsonElement("Price")]
        public decimal Price { get; set; }

        [BsonElement("Status")]
        public string Status { get; set; }

        [BsonElement("Availability")]
        public string Availability { get; set; } = "Available";

        [BsonElement("Image")]
        public string Image { get; set; } = "/images/wings.png";

        [BsonElement("BranchId")]
        public string BranchId { get; set; } = string.Empty; // Empty = shared across all branches
    }
}
