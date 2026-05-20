using System.Security.Cryptography;
using System.Text;

namespace SelfOrderingSystemKiosk.Services
{
    public static class OrderRealtimeGroups
    {
        public const string AllKitchen = "kitchen:all";

        public static string KitchenBranch(string branchId)
            => $"kitchen:branch:{NormalizeGroupPart(branchId)}";

        public static string Order(string orderNumber, string publicAccessToken)
            => $"order:{NormalizeGroupPart(orderNumber)}:{Hash(publicAccessToken)}";

        private static string NormalizeGroupPart(string value)
            => (value ?? string.Empty).Trim().ToLowerInvariant();

        private static string Hash(string value)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
