namespace SelfOrderingSystemKiosk.Services
{
    public class IngredientCategoryRegistry
    {
        public static readonly string[] All =
        {
            "Raw mats",
            "Sauce",
            "Misc",
            "Drinks",
            "Ice cream"
        };

        public bool IsValid(string? value) =>
            !string.IsNullOrWhiteSpace(value) && All.Contains(value, StringComparer.Ordinal);
    }
}
