namespace SelfOrderingSystemKiosk.Services
{
    /// <summary>Food types for menu items (proteins / mains).</summary>
    public class FoodCategoryRegistry
    {
        public static readonly string[] All =
        {
            "Chicken (wings, tenders)",
            "Shrimp",
            "Ground beef",
            "Spanish sardines",
            "Longaniza"
        };

        public bool IsValid(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            return All.Contains(value, StringComparer.Ordinal);
        }
    }
}
