namespace SelfOrderingSystemKiosk.Services
{
    public class IngredientCategoryRegistry
    {
        public static readonly string[] All =
        {
            "Produce & herbs",
            "Dry goods & dairy",
            "Specialty",
            "Oils, fats & liquids",
            "Sauces & condiments"
        };

        public bool IsValid(string? value) =>
            !string.IsNullOrWhiteSpace(value) && All.Contains(value, StringComparer.Ordinal);
    }
}
