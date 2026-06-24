namespace SelfOrderingSystemKiosk.Models
{
    public class AllLogEntry
    {
        public DateTime TimestampUtc { get; set; }
        public string LogType { get; set; } = string.Empty;
        public string Area { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string? Change { get; set; }
        public string? ReferenceType { get; set; }
        public string? ReferenceId { get; set; }
        public string? BranchId { get; set; }
        public string? BranchName { get; set; }
        public string? PerformedBy { get; set; }
        public string? Note { get; set; }
        public string Severity { get; set; } = "Info";

        public static AllLogEntry FromStockMovement(StockMovement movement) => new()
        {
            TimestampUtc = movement.TimestampUtc,
            LogType = "Stock",
            Area = movement.Reason,
            Summary = movement.ItemName,
            Change = $"{movement.StockBefore} -> {movement.StockAfter} ({(movement.QuantityDelta > 0 ? "+" : string.Empty)}{movement.QuantityDelta})",
            ReferenceType = movement.ReferenceType,
            ReferenceId = movement.ReferenceId,
            BranchId = movement.BranchId,
            PerformedBy = movement.PerformedBy,
            Note = movement.Note,
            Severity = movement.QuantityDelta < 0 ? "Warning" : "Info"
        };

        public static AllLogEntry FromManagementLog(ManagementLog log) => new()
        {
            TimestampUtc = log.TimestampUtc,
            LogType = log.Category,
            Area = string.IsNullOrWhiteSpace(log.Action) ? log.EntityType : log.Action,
            Summary = log.Summary,
            Change = log.EntityName,
            ReferenceType = log.EntityType,
            ReferenceId = log.EntityId,
            BranchId = log.BranchId,
            PerformedBy = log.PerformedBy,
            Note = log.Details,
            Severity = log.Severity
        };
    }
}
