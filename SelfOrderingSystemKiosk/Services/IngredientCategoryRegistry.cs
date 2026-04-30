namespace SelfOrderingSystemKiosk.Services
{
    public class IngredientCategoryRegistry
    {
        public static readonly string[] All =
        {
            "Sauces",
            "Raw Materials",
            "Drinks",
            "Ice Cream",
            "Miscellaneous",
            "Merchandise"
        };

        public bool IsValid(string? value) =>
            !string.IsNullOrWhiteSpace(value) && All.Contains(value, StringComparer.Ordinal);
    }
}
