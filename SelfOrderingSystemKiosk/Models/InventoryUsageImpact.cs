namespace SelfOrderingSystemKiosk.Models
{
    public class InventoryUsageImpact
    {
        public string IngredientId { get; set; } = string.Empty;
        public List<InventoryMenuUsage> UsedBy { get; set; } = new();
        public int AffectedMenuItemCount => UsedBy.Count;
        public int BlockingMenuItemCount { get; set; }
        public string ImpactLabel { get; set; } = "No recipe link";
        public string ImpactLevel { get; set; } = "none";
        public int ImpactSortValue { get; set; }
    }

    public class InventoryMenuUsage
    {
        public string MenuItemId { get; set; } = string.Empty;
        public string MenuItemName { get; set; } = string.Empty;
        public string MenuCategory { get; set; } = string.Empty;
        public decimal QuantityPerUnit { get; set; }
        public string Unit { get; set; } = string.Empty;
    }
}
