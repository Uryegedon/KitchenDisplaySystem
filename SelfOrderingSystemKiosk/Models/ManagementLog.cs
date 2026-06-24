using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace SelfOrderingSystemKiosk.Models
{
    /// <summary>Audit row for admin and management actions.</summary>
    public class ManagementLog
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = null!;

        [BsonElement("timestampUtc")]
        public DateTime TimestampUtc { get; set; }

        [BsonElement("category")]
        public string Category { get; set; } = "Management";

        [BsonElement("action")]
        public string Action { get; set; } = string.Empty;

        [BsonElement("entityType")]
        public string EntityType { get; set; } = string.Empty;

        [BsonElement("entityId")]
        public string? EntityId { get; set; }

        [BsonElement("entityName")]
        public string? EntityName { get; set; }

        [BsonElement("summary")]
        public string Summary { get; set; } = string.Empty;

        [BsonElement("details")]
        public string? Details { get; set; }

        [BsonElement("branchId")]
        public string? BranchId { get; set; }

        [BsonElement("performedBy")]
        public string? PerformedBy { get; set; }

        [BsonElement("severity")]
        public string Severity { get; set; } = "Info";
    }
}
