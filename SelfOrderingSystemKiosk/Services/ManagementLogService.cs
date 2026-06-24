using Microsoft.Extensions.Configuration;
using MongoDB.Driver;
using SelfOrderingSystemKiosk.Models;

namespace SelfOrderingSystemKiosk.Services
{
    public class ManagementLogService
    {
        private readonly IMongoCollection<ManagementLog> _logs;

        public ManagementLogService(IMongoClient mongoClient, IConfiguration config)
        {
            var dbName = config["KitchenDatabase:DatabaseName"] ?? "Kitchen";
            var collectionName = config["KitchenDatabase:ManagementLogsCollectionName"] ?? "ManagementLogs";
            _logs = mongoClient.GetDatabase(dbName).GetCollection<ManagementLog>(collectionName);
        }

        public async Task RecordAsync(
            string action,
            string entityType,
            string summary,
            string? entityId = null,
            string? entityName = null,
            string? details = null,
            string? branchId = null,
            string? performedBy = null,
            string category = "Management",
            string severity = "Info",
            CancellationToken cancellationToken = default)
        {
            var log = new ManagementLog
            {
                TimestampUtc = DateTime.UtcNow,
                Category = string.IsNullOrWhiteSpace(category) ? "Management" : category.Trim(),
                Action = action?.Trim() ?? string.Empty,
                EntityType = entityType?.Trim() ?? string.Empty,
                EntityId = string.IsNullOrWhiteSpace(entityId) ? null : entityId.Trim(),
                EntityName = string.IsNullOrWhiteSpace(entityName) ? null : entityName.Trim(),
                Summary = summary?.Trim() ?? string.Empty,
                Details = string.IsNullOrWhiteSpace(details) ? null : details.Trim(),
                BranchId = string.IsNullOrWhiteSpace(branchId) ? null : branchId.Trim(),
                PerformedBy = string.IsNullOrWhiteSpace(performedBy) ? null : performedBy.Trim(),
                Severity = string.IsNullOrWhiteSpace(severity) ? "Info" : severity.Trim()
            };

            await _logs.InsertOneAsync(log, cancellationToken: cancellationToken);
        }

        public async Task<List<ManagementLog>> GetRecentAsync(DateTime? startUtc, DateTime? endUtc, int limit = 500, string? branchId = null)
        {
            var filter = Builders<ManagementLog>.Filter.Empty;
            if (startUtc.HasValue)
                filter &= Builders<ManagementLog>.Filter.Gte(l => l.TimestampUtc, startUtc.Value);
            if (endUtc.HasValue)
                filter &= Builders<ManagementLog>.Filter.Lt(l => l.TimestampUtc, endUtc.Value);
            if (!string.IsNullOrWhiteSpace(branchId))
            {
                var trimmedBranchId = branchId.Trim();
                filter &= Builders<ManagementLog>.Filter.Or(
                    Builders<ManagementLog>.Filter.Eq(l => l.BranchId, trimmedBranchId),
                    Builders<ManagementLog>.Filter.Eq(l => l.BranchId, string.Empty),
                    Builders<ManagementLog>.Filter.Eq(l => l.BranchId, null));
            }

            return await _logs
                .Find(filter)
                .SortByDescending(l => l.TimestampUtc)
                .Limit(limit)
                .ToListAsync();
        }

        public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
        {
            await _logs.Indexes.CreateOneAsync(
                new CreateIndexModel<ManagementLog>(
                    Builders<ManagementLog>.IndexKeys.Descending(l => l.TimestampUtc),
                    new CreateIndexOptions { Name = "ix_management_logs_timestamp" }),
                cancellationToken: cancellationToken);

            await _logs.Indexes.CreateOneAsync(
                new CreateIndexModel<ManagementLog>(
                    Builders<ManagementLog>.IndexKeys
                        .Ascending(l => l.BranchId)
                        .Descending(l => l.TimestampUtc),
                    new CreateIndexOptions { Name = "ix_management_logs_branch_timestamp" }),
                cancellationToken: cancellationToken);

            await _logs.Indexes.CreateOneAsync(
                new CreateIndexModel<ManagementLog>(
                    Builders<ManagementLog>.IndexKeys
                        .Ascending(l => l.EntityType)
                        .Descending(l => l.TimestampUtc),
                    new CreateIndexOptions { Name = "ix_management_logs_entity_timestamp" }),
                cancellationToken: cancellationToken);
        }
    }
}
