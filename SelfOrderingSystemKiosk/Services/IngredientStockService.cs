using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using SelfOrderingSystemKiosk.Models;

namespace SelfOrderingSystemKiosk.Services
{
    /// <summary>Ingredient inventory only (not kiosk menu items).</summary>
    public class IngredientStockService
    {
        private readonly IMongoCollection<IngredientItem> _collection;
        private readonly StockMovementService _movements;
        private readonly ILogger<IngredientStockService> _logger;

        public IngredientStockService(
            IMongoClient mongoClient,
            IConfiguration config,
            StockMovementService movements,
            ILogger<IngredientStockService> logger)
        {
            var dbName = config["KitchenDatabase:DatabaseName"] ?? "Kitchen";
            var collectionName = config["KitchenDatabase:IngredientsCollectionName"] ?? "Ingredients";
            _collection = mongoClient.GetDatabase(dbName).GetCollection<IngredientItem>(collectionName);
            _movements = movements;
            _logger = logger;
        }

        /// <summary>Decreases ingredient stock when a menu item is sold (recipe consumption).</summary>
        public async Task<bool> DecrementForSaleAsync(
            string ingredientId,
            int quantity,
            string menuItemName,
            string? referenceType,
            string? referenceId)
        {
            if (quantity <= 0)
                return true;

            var item = await _collection.FindOneAndUpdateAsync(
                Builders<IngredientItem>.Filter.And(
                    Builders<IngredientItem>.Filter.Eq(x => x.Id, ingredientId),
                    Builders<IngredientItem>.Filter.Gte(x => x.CurrentStock, quantity)),
                Builders<IngredientItem>.Update.Inc(x => x.CurrentStock, -quantity),
                new FindOneAndUpdateOptions<IngredientItem> { ReturnDocument = ReturnDocument.Before });
            if (item == null)
            {
                _logger.LogWarning("Recipe: ingredient id {Id} missing or has insufficient stock (menu item {Menu}).", ingredientId, menuItemName);
                return false;
            }

            var oldStock = item.CurrentStock;
            var newStock = oldStock - quantity;
            await SetStatusAsync(item.Id, newStock, item.ReorderLevel);

            var delta = newStock - oldStock;
            if (delta != 0)
            {
                await _movements.InsertAsync(new StockMovement
                {
                    InventoryItemId = item.Id,
                    ItemName = item.Item ?? "",
                    QuantityDelta = delta,
                    StockBefore = oldStock,
                    StockAfter = newStock,
                    Reason = "Sale",
                    ReferenceType = referenceType ?? "Order",
                    ReferenceId = referenceId,
                    Note = $"Recipe: {menuItemName}",
                    BranchId = item.BranchId
                });
            }

            return true;
        }

        public async Task<decimal> EstimateCostAsync(string ingredientId, int quantity)
        {
            if (quantity <= 0 || string.IsNullOrWhiteSpace(ingredientId))
                return 0m;

            var item = await GetByIdAsync(ingredientId.Trim());
            return item == null ? 0m : Math.Max(0, quantity) * Math.Max(0m, item.CostPerUnit);
        }

        public async Task<List<IngredientItem>> GetAllAsync()
        {
            var list = await _collection.Find(_ => true).ToListAsync();
            return list
                .OrderBy(i => i.IngredientCategory, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Item ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<IngredientItem?> GetByIdAsync(string id) =>
            await _collection.Find(x => x.Id == id).FirstOrDefaultAsync();

        public async Task<List<IngredientItem>> GetByIdsAsync(IEnumerable<string> ids)
        {
            var validIds = ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (validIds.Count == 0)
                return new List<IngredientItem>();

            return await _collection
                .Find(Builders<IngredientItem>.Filter.In(x => x.Id, validIds))
                .ToListAsync();
        }

        public async Task<IngredientItem?> GetByNameAsync(string itemName, string? branchId = null)
        {
            if (string.IsNullOrWhiteSpace(branchId))
                return await _collection.Find(x => x.Item == itemName).FirstOrDefaultAsync();

            var trimmedBranchId = branchId.Trim();
            var branchItem = await _collection
                .Find(x => x.Item == itemName && x.BranchId == trimmedBranchId)
                .FirstOrDefaultAsync();
            if (branchItem != null)
                return branchItem;

            return await _collection
                .Find(Builders<IngredientItem>.Filter.And(
                    Builders<IngredientItem>.Filter.Eq(x => x.Item, itemName),
                    SharedBranchFilter()))
                .FirstOrDefaultAsync();
        }

        public async Task<string> GetCategoryByNameAsync(string itemName, string? branchId = null)
        {
            var item = await GetByNameAsync(itemName.Trim(), branchId);
            return item?.IngredientCategory ?? string.Empty;
        }

        public async Task<bool> ExistsByNameAsync(string itemName, string? branchId = null)
        {
            var item = await GetByNameAsync(itemName.Trim(), branchId);
            return item != null;
        }

        public async Task AddAsync(IngredientItem item)
        {
            item.Status = item.CurrentStock == 0 ? "No Stock" : item.CurrentStock <= item.ReorderLevel ? "Low Stock" : "In Stock";
            await _collection.InsertOneAsync(item);

            if (item.CurrentStock > 0)
            {
                await _movements.InsertAsync(new StockMovement
                {
                    InventoryItemId = item.Id,
                    ItemName = item.Item ?? "",
                    QuantityDelta = item.CurrentStock,
                    StockBefore = 0,
                    StockAfter = item.CurrentStock,
                    Reason = "Initial",
                    ReferenceType = "Ingredient",
                    ReferenceId = item.Id,
                    Note = "New ingredient",
                    BranchId = item.BranchId
                });
            }
        }

        public async Task UpdateAsync(IngredientItem item)
        {
            item.Status = item.CurrentStock == 0 ? "No Stock" : item.CurrentStock <= item.ReorderLevel ? "Low Stock" : "In Stock";
            item.CostPerUnit = Math.Max(0m, item.CostPerUnit);
            await _collection.ReplaceOneAsync(x => x.Id == item.Id, item);
        }

        public async Task DeleteAsync(string id) =>
            await _collection.DeleteOneAsync(x => x.Id == id);

        public async Task<bool> IncreaseStockAsync(string ingredientId, int quantityAdded, string referenceType, string? referenceId, string? note = null)
        {
            if (quantityAdded <= 0)
                return false;

            var item = await _collection.FindOneAndUpdateAsync(
                x => x.Id == ingredientId,
                Builders<IngredientItem>.Update.Inc(x => x.CurrentStock, quantityAdded),
                new FindOneAndUpdateOptions<IngredientItem> { ReturnDocument = ReturnDocument.Before });
            if (item == null)
                return false;

            var oldStock = item.CurrentStock;
            var newStock = oldStock + quantityAdded;
            await SetStatusAsync(item.Id, newStock, item.ReorderLevel);

            await _movements.InsertAsync(new StockMovement
            {
                InventoryItemId = item.Id,
                ItemName = item.Item ?? "",
                QuantityDelta = quantityAdded,
                StockBefore = oldStock,
                StockAfter = newStock,
                Reason = "Restock",
                ReferenceType = referenceType,
                ReferenceId = referenceId,
                Note = note,
                BranchId = item.BranchId
            });

            return true;
        }

        public async Task RecordAdjustmentAsync(string ingredientId, string itemName, int stockBefore, int stockAfter, string note)
        {
            var item = await GetByIdAsync(ingredientId);
            await _movements.InsertAsync(new StockMovement
            {
                InventoryItemId = ingredientId,
                ItemName = itemName,
                QuantityDelta = stockAfter - stockBefore,
                StockBefore = stockBefore,
                StockAfter = stockAfter,
                Reason = "Adjustment",
                ReferenceType = "Ingredient",
                ReferenceId = ingredientId,
                Note = note,
                BranchId = item?.BranchId
            });
        }

        public async Task<(bool Success, string Message)> TransferAsync(
            string sourceIngredientId,
            string destinationBranchId,
            int quantity,
            string? note,
            string? performedBy)
        {
            if (string.IsNullOrWhiteSpace(sourceIngredientId) || string.IsNullOrWhiteSpace(destinationBranchId))
                return (false, "Source ingredient and destination branch are required.");
            if (quantity <= 0)
                return (false, "Transfer quantity must be greater than zero.");

            var source = await GetByIdAsync(sourceIngredientId);
            if (source == null)
                return (false, "Source ingredient was not found.");
            if (string.Equals(source.BranchId, destinationBranchId, StringComparison.OrdinalIgnoreCase))
                return (false, "Choose a different destination branch.");
            if (source.CurrentStock < quantity)
                return (false, $"Not enough stock. Available: {source.CurrentStock} {source.Unit}.");

            var destination = await _collection.Find(i =>
                    i.BranchId == destinationBranchId &&
                    i.Item == source.Item &&
                    i.Unit == source.Unit)
                .FirstOrDefaultAsync();

            if (destination == null)
            {
                destination = new IngredientItem
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    Item = source.Item,
                    IngredientCategory = source.IngredientCategory,
                    CurrentStock = 0,
                    Unit = source.Unit,
                    CostPerUnit = source.CostPerUnit,
                    ExpirationDate = source.ExpirationDate,
                    ReorderLevel = source.ReorderLevel,
                    Status = "No Stock",
                    BranchId = destinationBranchId
                };
                await _collection.InsertOneAsync(destination);
            }

            var transferGroupId = ObjectId.GenerateNewId().ToString();
            var sourceBeforeItem = await _collection.FindOneAndUpdateAsync(
                Builders<IngredientItem>.Filter.And(
                    Builders<IngredientItem>.Filter.Eq(i => i.Id, source.Id),
                    Builders<IngredientItem>.Filter.Gte(i => i.CurrentStock, quantity)),
                Builders<IngredientItem>.Update.Inc(i => i.CurrentStock, -quantity),
                new FindOneAndUpdateOptions<IngredientItem> { ReturnDocument = ReturnDocument.Before });
            if (sourceBeforeItem == null)
                return (false, $"Not enough stock. Available stock changed before transfer could complete.");

            var sourceBefore = sourceBeforeItem.CurrentStock;
            var sourceAfter = sourceBefore - quantity;
            await SetStatusAsync(sourceBeforeItem.Id, sourceAfter, sourceBeforeItem.ReorderLevel);

            IngredientItem? destinationBeforeItem;
            try
            {
                destinationBeforeItem = await _collection.FindOneAndUpdateAsync(
                    i => i.Id == destination.Id,
                    Builders<IngredientItem>.Update.Inc(i => i.CurrentStock, quantity),
                    new FindOneAndUpdateOptions<IngredientItem> { ReturnDocument = ReturnDocument.Before });
                if (destinationBeforeItem == null)
                    throw new InvalidOperationException("Destination ingredient was not available for transfer.");
            }
            catch
            {
                await _collection.UpdateOneAsync(
                    i => i.Id == sourceBeforeItem.Id,
                    Builders<IngredientItem>.Update.Inc(i => i.CurrentStock, quantity));
                await SetStatusAsync(sourceBeforeItem.Id, sourceBefore, sourceBeforeItem.ReorderLevel);
                throw;
            }

            var destinationBefore = destinationBeforeItem.CurrentStock;
            var destinationAfter = destinationBefore + quantity;
            await SetStatusAsync(destinationBeforeItem.Id, destinationAfter, destinationBeforeItem.ReorderLevel);

            var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            await _movements.InsertAsync(new StockMovement
            {
                InventoryItemId = sourceBeforeItem.Id,
                ItemName = sourceBeforeItem.Item ?? "",
                QuantityDelta = -quantity,
                StockBefore = sourceBefore,
                StockAfter = sourceAfter,
                Reason = "Transfer Out",
                ReferenceType = "Transfer",
                ReferenceId = transferGroupId,
                Note = trimmedNote,
                BranchId = sourceBeforeItem.BranchId,
                PerformedBy = performedBy,
                TransferGroupId = transferGroupId
            });
            await _movements.InsertAsync(new StockMovement
            {
                InventoryItemId = destinationBeforeItem.Id,
                ItemName = destinationBeforeItem.Item ?? "",
                QuantityDelta = quantity,
                StockBefore = destinationBefore,
                StockAfter = destinationAfter,
                Reason = "Transfer In",
                ReferenceType = "Transfer",
                ReferenceId = transferGroupId,
                Note = trimmedNote,
                BranchId = destinationBeforeItem.BranchId,
                PerformedBy = performedBy,
                TransferGroupId = transferGroupId
            });

            return (true, $"Transferred {quantity} {source.Unit} of {source.Item}.");
        }

        // ====================
        // Branch Filtering Methods
        // ====================

        /// <summary>
        /// Gets all ingredients filtered by branch (empty branchId returns ingredients with empty BranchId or matching branch)
        /// </summary>
        public async Task<List<IngredientItem>> GetAllByBranchAsync(string? branchId)
        {
            if (string.IsNullOrEmpty(branchId))
            {
                return await GetAllAsync();
            }

            var trimmedBranchId = branchId.Trim();
            // Return items for specific branch OR shared items (empty BranchId)
            var filter = Builders<IngredientItem>.Filter.Or(
                Builders<IngredientItem>.Filter.Eq(i => i.BranchId, trimmedBranchId),
                SharedBranchFilter()
            );
            var list = await _collection.Find(filter).ToListAsync();
            return PreferBranchItemsOverShared(list, trimmedBranchId)
                .OrderBy(i => i.IngredientCategory, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Item ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Gets low stock items filtered by branch
        /// </summary>
        public async Task<List<IngredientItem>> GetLowStockByBranchAsync(string? branchId)
        {
            if (string.IsNullOrEmpty(branchId))
            {
                var all = await GetAllAsync();
                return all.Where(i => i.CurrentStock <= i.ReorderLevel).ToList();
            }

            var trimmedBranchId = branchId.Trim();
            var filter = Builders<IngredientItem>.Filter.Or(
                Builders<IngredientItem>.Filter.Eq(i => i.BranchId, trimmedBranchId),
                SharedBranchFilter()
            );
            var list = await _collection.Find(filter).ToListAsync();
            return PreferBranchItemsOverShared(list, trimmedBranchId)
                .Where(i => i.CurrentStock <= i.ReorderLevel)
                .OrderBy(i => i.IngredientCategory, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Item ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Gets ingredient count by branch
        /// </summary>
        public async Task<long> GetCountByBranchAsync(string? branchId)
        {
            if (string.IsNullOrEmpty(branchId))
            {
                return await _collection.CountDocumentsAsync(_ => true);
            }

            var trimmedBranchId = branchId.Trim();
            var filter = Builders<IngredientItem>.Filter.Or(
                Builders<IngredientItem>.Filter.Eq(i => i.BranchId, trimmedBranchId),
                SharedBranchFilter()
            );
            var list = await _collection.Find(filter).ToListAsync();
            return PreferBranchItemsOverShared(list, trimmedBranchId).LongCount();
        }

        public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
        {
            await _collection.Indexes.CreateOneAsync(
                new CreateIndexModel<IngredientItem>(
                    Builders<IngredientItem>.IndexKeys
                        .Ascending(i => i.BranchId)
                        .Ascending(i => i.Item)
                        .Ascending(i => i.Unit),
                    new CreateIndexOptions { Name = "ix_ingredients_branch_item_unit" }),
                cancellationToken: cancellationToken);

            await _collection.Indexes.CreateOneAsync(
                new CreateIndexModel<IngredientItem>(
                    Builders<IngredientItem>.IndexKeys
                        .Ascending(i => i.BranchId)
                        .Ascending(i => i.IngredientCategory)
                        .Ascending(i => i.Item),
                    new CreateIndexOptions { Name = "ix_ingredients_branch_category_item" }),
                cancellationToken: cancellationToken);
        }

        private async Task SetStatusAsync(string ingredientId, int currentStock, int reorderLevel)
        {
            var status = currentStock == 0 ? "No Stock" : currentStock <= reorderLevel ? "Low Stock" : "In Stock";
            await _collection.UpdateOneAsync(
                i => i.Id == ingredientId,
                Builders<IngredientItem>.Update.Set(i => i.Status, status));
        }

        private static FilterDefinition<IngredientItem> SharedBranchFilter()
        {
            return Builders<IngredientItem>.Filter.Or(
                Builders<IngredientItem>.Filter.Eq(i => i.BranchId, (string)null!),
                Builders<IngredientItem>.Filter.Eq(i => i.BranchId, string.Empty),
                Builders<IngredientItem>.Filter.Not(Builders<IngredientItem>.Filter.Exists(i => i.BranchId)));
        }

        private static IEnumerable<IngredientItem> PreferBranchItemsOverShared(IEnumerable<IngredientItem> items, string branchId)
        {
            return (items ?? Enumerable.Empty<IngredientItem>())
                .Where(i => !string.IsNullOrWhiteSpace(i.Item))
                .GroupBy(i => i.Item!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g
                    .OrderByDescending(i => string.Equals(i.BranchId, branchId, StringComparison.OrdinalIgnoreCase))
                    .ThenBy(i => string.IsNullOrWhiteSpace(i.BranchId) ? 1 : 0)
                    .ThenBy(i => i.Item, StringComparer.OrdinalIgnoreCase)
                    .First());
        }
    }
}
