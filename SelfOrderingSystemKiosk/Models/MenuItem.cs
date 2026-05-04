using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace SelfOrderingSystemKiosk.Models
{
    /// <summary>Sellable dish on the kiosk — stored in MenuItems collection.</summary>
    public class MenuItem
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = null!;

        [BsonElement("Item")]
        public string Item { get; set; } = null!;

        /// <summary>Kiosk tab category (Wings, Appetizer, …) — matches MenuCategoryRegistry.</summary>
        [BsonElement("Category")]
        public string Category { get; set; } = null!;

        /// <summary>Food type: chicken, shrimp, proteins, etc.</summary>
        [BsonElement("foodCategory")]
        public string? FoodCategory { get; set; }

        [BsonElement("menuOrder")]
        public int MenuOrder { get; set; }

        [BsonElement("CurrentStock")]
        public int CurrentStock { get; set; }

        [BsonElement("Unit")]
        public string Unit { get; set; } = "pcs";

        [BsonElement("ReorderLevel")]
        public int ReorderLevel { get; set; }

        [BsonElement("Price")]
        public decimal Price { get; set; }

        [BsonElement("Status")]
        public string Status { get; set; } = "In Stock";

        [BsonElement("Availability")]
        public string Availability { get; set; } = "Available";

        [BsonElement("Image")]
        public string Image { get; set; } = "/images/wings.png";

        /// <summary>Ingredients deducted when this dish is sold (kitchen stock).</summary>
        [BsonElement("recipe")]
        public List<MenuRecipeLine>? Recipe { get; set; }

        [BsonElement("BranchId")]
        public string BranchId { get; set; } = string.Empty; // Empty = shared across all branches
    }
}
