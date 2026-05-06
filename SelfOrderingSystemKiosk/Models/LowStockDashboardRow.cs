namespace SelfOrderingSystemKiosk.Models
{
    public class LowStockDashboardRow
    {
        public string Id { get; set; } = null!;
        public string Name { get; set; } = null!;
        /// <summary>Menu or Ingredient</summary>
        public string Kind { get; set; } = null!;
        public string? BranchId { get; set; }
        public int CurrentStock { get; set; }
        public int ReorderLevel { get; set; }
    }
}
