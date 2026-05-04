using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System.ComponentModel.DataAnnotations;

namespace SelfOrderingSystemKiosk.Models
{
    /// <summary>
    /// User role constants for authorization
    /// </summary>
    public static class UserRoles
    {
        public const string Owner = "Owner";
        public const string BranchManager = "BranchManager";
        public const string Admin = "Admin";
        public const string Kitchen = "Kitchen";
    }

    public class AdminUser
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; }

        [BsonElement("Username")]
        public string Username { get; set; }

        [BsonElement("Password")]
        public string Password { get; set; }

        [BsonElement("FullName")]
        public string FullName { get; set; }

        [BsonElement("Email")]
        public string Email { get; set; }

        [BsonElement("Role")]
        public string Role { get; set; } = UserRoles.Admin;

        [BsonElement("BranchId")]
        public string? BranchId { get; set; } // Null = Owner (access to all branches)
    }

    public class AdminSignUp
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [Required]
        public string FullName { get; set; }

        [Required]
        public string Username { get; set; }

        [Required, EmailAddress]
        public string Email { get; set; }

        [Required, MinLength(6)]
        public string Password { get; set; }

        public string Role { get; set; } = UserRoles.Admin;
        public string? BranchId { get; set; } // Null = Owner (access to all branches)
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

}

