using Microsoft.Extensions.Configuration;
using MongoDB.Driver;
using SelfOrderingSystemKiosk.Models;

namespace SelfOrderingSystemKiosk.Services
{
    public class StockMovementService
    {
        private readonly IMongoCollection<StockMovement> _movements;

        public StockMovementService(IMongoClient mongoClient, IConfiguration config)
        {
            var dbName = config["KitchenDatabase:DatabaseName"] ?? "Kitchen";
            var collectionName = config["KitchenDatabase:StockMovementsCollectionName"] ?? "StockMovements";
            _movements = mongoClient.GetDatabase(dbName).GetCollection<StockMovement>(collectionName);
        }

        public async Task InsertAsync(StockMovement movement, CancellationToken cancellationToken = default)
        {
            movement.TimestampUtc = DateTime.UtcNow;
            await _movements.InsertOneAsync(movement, cancellationToken: cancellationToken);
        }

        public async Task<List<StockMovement>> GetRecentAsync(DateTime? startUtc, DateTime? endUtc, int limit = 500, string? branchId = null)
        {
            var filter = Builders<StockMovement>.Filter.Empty;
            if (startUtc.HasValue)
                filter &= Builders<StockMovement>.Filter.Gte(m => m.TimestampUtc, startUtc.Value);
            if (endUtc.HasValue)
                filter &= Builders<StockMovement>.Filter.Lt(m => m.TimestampUtc, endUtc.Value);
            if (!string.IsNullOrWhiteSpace(branchId))
            {
                var trimmedBranchId = branchId.Trim();
                filter &= Builders<StockMovement>.Filter.Or(
                    Builders<StockMovement>.Filter.Eq(m => m.BranchId, trimmedBranchId),
                    Builders<StockMovement>.Filter.Eq(m => m.BranchId, string.Empty),
                    Builders<StockMovement>.Filter.Eq(m => m.BranchId, null));
            }

            return await _movements
                .Find(filter)
                .SortByDescending(m => m.TimestampUtc)
                .Limit(limit)
                .ToListAsync();
        }

        public async Task<List<StockMovement>> GetForInventoryItemsAsync(IEnumerable<string> inventoryItemIds, DateTime? startUtc, DateTime? endUtc, string? branchId = null)
        {
            var ids = inventoryItemIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ids.Count == 0)
                return new List<StockMovement>();

            var filter = Builders<StockMovement>.Filter.In(m => m.InventoryItemId, ids);
            if (startUtc.HasValue)
                filter &= Builders<StockMovement>.Filter.Gte(m => m.TimestampUtc, startUtc.Value);
            if (endUtc.HasValue)
                filter &= Builders<StockMovement>.Filter.Lt(m => m.TimestampUtc, endUtc.Value);
            if (!string.IsNullOrWhiteSpace(branchId))
            {
                var trimmedBranchId = branchId.Trim();
                filter &= Builders<StockMovement>.Filter.Or(
                    Builders<StockMovement>.Filter.Eq(m => m.BranchId, trimmedBranchId),
                    Builders<StockMovement>.Filter.Eq(m => m.BranchId, string.Empty),
                    Builders<StockMovement>.Filter.Eq(m => m.BranchId, null));
            }

            return await _movements
                .Find(filter)
                .SortBy(m => m.TimestampUtc)
                .ToListAsync();
        }

        public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
        {
            await _movements.Indexes.CreateOneAsync(
                new CreateIndexModel<StockMovement>(
                    Builders<StockMovement>.IndexKeys.Descending(m => m.TimestampUtc),
                    new CreateIndexOptions { Name = "ix_stock_movements_timestamp" }),
                cancellationToken: cancellationToken);

            await _movements.Indexes.CreateOneAsync(
                new CreateIndexModel<StockMovement>(
                    Builders<StockMovement>.IndexKeys.Ascending(m => m.ItemName),
                    new CreateIndexOptions { Name = "ix_stock_movements_itemName" }),
                cancellationToken: cancellationToken);

            await _movements.Indexes.CreateOneAsync(
                new CreateIndexModel<StockMovement>(
                    Builders<StockMovement>.IndexKeys
                        .Ascending(m => m.InventoryItemId)
                        .Ascending(m => m.TimestampUtc),
                    new CreateIndexOptions { Name = "ix_stock_movements_inventoryItem_timestamp" }),
                cancellationToken: cancellationToken);

            await _movements.Indexes.CreateOneAsync(
                new CreateIndexModel<StockMovement>(
                    Builders<StockMovement>.IndexKeys
                        .Ascending(m => m.BranchId)
                        .Descending(m => m.TimestampUtc),
                    new CreateIndexOptions { Name = "ix_stock_movements_branch_timestamp" }),
                cancellationToken: cancellationToken);
        }
    }
}
