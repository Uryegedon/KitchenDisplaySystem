using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace SelfOrderingSystemKiosk.Models
{
    /// <summary>Audit row for inventory changes (sales, restocks, manual edits).</summary>
    public class StockMovement
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = null!;

        [BsonElement("timestampUtc")]
        public DateTime TimestampUtc { get; set; }

        [BsonElement("inventoryItemId")]
        public string? InventoryItemId { get; set; }

        [BsonElement("itemName")]
        public string ItemName { get; set; } = "";

        /// <summary>Positive = stock in, negative = stock out.</summary>
        [BsonElement("quantityDelta")]
        public int QuantityDelta { get; set; }

        [BsonElement("stockBefore")]
        public int StockBefore { get; set; }

        [BsonElement("stockAfter")]
        public int StockAfter { get; set; }

        /// <summary>Sale, Restock, Adjustment, Initial, Waste.</summary>
        [BsonElement("reason")]
        public string Reason { get; set; } = "";

        /// <summary>Order, Dashboard, Inventory, System.</summary>
        [BsonElement("referenceType")]
        public string ReferenceType { get; set; } = "";

        [BsonElement("referenceId")]
        public string? ReferenceId { get; set; }

        [BsonElement("note")]
        public string? Note { get; set; }

        [BsonElement("branchId")]
        public string? BranchId { get; set; }

        [BsonElement("performedBy")]
        public string? PerformedBy { get; set; }

        [BsonElement("transferGroupId")]
        public string? TransferGroupId { get; set; }
    }
}
