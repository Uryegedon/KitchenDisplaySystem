using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using System.Text.Json.Serialization;

namespace SelfOrderingSystemKiosk.Areas.Admin.Models
{
    public class Branch
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        [ValidateNever]
        public string Id { get; set; } = null!;

        [BsonElement("branchCode")]
        [JsonPropertyName("branchCode")]
        public string BranchCode { get; set; }

        [BsonElement("branchName")]
        [JsonPropertyName("branchName")]
        public string BranchName { get; set; }

        [BsonElement("address")]
        [JsonPropertyName("address")]
        public string Address { get; set; }

        [BsonElement("phone")]
        [JsonPropertyName("phone")]
        public string Phone { get; set; }

        [BsonElement("email")]
        [JsonPropertyName("email")]
        public string Email { get; set; }

        [BsonElement("isActive")]
        [JsonPropertyName("isActive")]
        public bool IsActive { get; set; }

        [BsonElement("createdAt")]
        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }

        [BsonElement("updatedAt")]
        [JsonPropertyName("updatedAt")]
        public DateTime? UpdatedAt { get; set; }

        public Branch()
        {
            BranchCode = string.Empty;
            BranchName = string.Empty;
            Address = string.Empty;
            Phone = string.Empty;
            Email = string.Empty;
            IsActive = true;
            CreatedAt = DateTime.UtcNow;
        }
    }
}
