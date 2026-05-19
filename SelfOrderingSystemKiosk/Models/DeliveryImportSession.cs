using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace SelfOrderingSystemKiosk.Models
{
    public class DeliveryImportSession
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = null!;

        [BsonElement("token")]
        public string Token { get; set; } = string.Empty;

        [BsonElement("branchId")]
        public string BranchId { get; set; } = string.Empty;

        [BsonElement("createdBy")]
        public string CreatedBy { get; set; } = string.Empty;

        [BsonElement("createdAtUtc")]
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        [BsonElement("expiresAtUtc")]
        public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddMinutes(10);

        [BsonElement("uploadedAtUtc")]
        public DateTime? UploadedAtUtc { get; set; }

        [BsonElement("confirmedAtUtc")]
        public DateTime? ConfirmedAtUtc { get; set; }

        [BsonElement("status")]
        public string Status { get; set; } = "Waiting";

        [BsonElement("rawText")]
        public string RawText { get; set; } = string.Empty;

        [BsonElement("rows")]
        public List<DeliveryImportRow> Rows { get; set; } = new();

        [BsonElement("uploadContentType")]
        public string UploadContentType { get; set; } = string.Empty;

        [BsonElement("uploadUserAgent")]
        public string UploadUserAgent { get; set; } = string.Empty;

        [BsonElement("uploadRemoteIp")]
        public string UploadRemoteIp { get; set; } = string.Empty;
    }

    public class DeliveryImportRow
    {
        [BsonElement("itemName")]
        public string ItemName { get; set; } = string.Empty;

        [BsonElement("quantity")]
        public int Quantity { get; set; }

        [BsonElement("unit")]
        public string Unit { get; set; } = string.Empty;

        [BsonElement("matchedIngredientId")]
        public string MatchedIngredientId { get; set; } = string.Empty;

        [BsonElement("matchedIngredientName")]
        public string MatchedIngredientName { get; set; } = string.Empty;

        [BsonElement("confidence")]
        public int Confidence { get; set; }
    }
}
