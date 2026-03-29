using MongoDB.Bson.Serialization.Attributes;
using System.Text.Json.Serialization;

namespace SelfOrderingSystemKiosk.Models
{
    /// <summary>One ingredient and how much is consumed per single menu unit sold (order uses ingredient stock units).</summary>
    public class MenuRecipeLine
    {
        [BsonElement("ingredientId")]
        [JsonPropertyName("ingredientId")]
        public string IngredientId { get; set; } = null!;

        /// <summary>Amount deducted from ingredient stock for each 1× this menu item (e.g. 150 g, 5 ml).</summary>
        [BsonElement("quantityPerUnit")]
        [JsonPropertyName("quantityPerUnit")]
        public int QuantityPerUnit { get; set; }
    }
}
