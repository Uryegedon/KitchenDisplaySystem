using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace SelfOrderingSystemKiosk.Models
{
    /// <summary>Kitchen ingredient stock — stored in Ingredients collection (not on customer menu).</summary>
    public class IngredientItem
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = null!;

        [BsonElement("Item")]
        public string Item { get; set; } = null!;

        [BsonElement("IngredientCategory")]
        public string IngredientCategory { get; set; } = null!;

        [BsonElement("CurrentStock")]
        public int CurrentStock { get; set; }

        [BsonElement("Unit")]
        public string Unit { get; set; } = "g";

        [BsonElement("ReorderLevel")]
        public int ReorderLevel { get; set; }

        [BsonElement("Status")]
        public string Status { get; set; } = "In Stock";

        [BsonElement("BranchId")]
        public string BranchId { get; set; } = string.Empty; // Empty = shared across all branches
    }
}
