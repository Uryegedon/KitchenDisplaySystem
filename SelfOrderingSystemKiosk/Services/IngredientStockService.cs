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

            var item = await GetByIdAsync(ingredientId);
            if (item == null)
            {
                _logger.LogWarning("Recipe: ingredient id {Id} not found (menu item {Menu}).", ingredientId, menuItemName);
                return false;
            }

            var oldStock = item.CurrentStock;
            var newStock = Math.Max(0, oldStock - quantity);
            var status = newStock == 0 ? "No Stock" : newStock <= item.ReorderLevel ? "Low Stock" : "In Stock";

            await _collection.UpdateOneAsync(
                x => x.Id == ingredientId,
                Builders<IngredientItem>.Update
                    .Set(x => x.CurrentStock, newStock)
                    .Set(x => x.Status, status));

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

        public async Task<IngredientItem?> GetByNameAsync(string itemName, string? branchId = null)
        {
            if (string.IsNullOrWhiteSpace(branchId))
                return await _collection.Find(x => x.Item == itemName).FirstOrDefaultAsync();

            var trimmedBranchId = branchId.Trim();
            return await _collection.Find(x =>
                    x.Item == itemName &&
                    (x.BranchId == trimmedBranchId || x.BranchId == string.Empty))
                .SortByDescending(x => x.BranchId == trimmedBranchId)
                .FirstOrDefaultAsync();
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

            var item = await GetByIdAsync(ingredientId);
            if (item == null)
                return false;

            var oldStock = item.CurrentStock;
            item.CurrentStock += quantityAdded;
            item.Status = item.CurrentStock == 0 ? "No Stock" : item.CurrentStock <= item.ReorderLevel ? "Low Stock" : "In Stock";

            await UpdateAsync(item);

            await _movements.InsertAsync(new StockMovement
            {
                InventoryItemId = item.Id,
                ItemName = item.Item ?? "",
                QuantityDelta = quantityAdded,
                StockBefore = oldStock,
                StockAfter = item.CurrentStock,
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
            var sourceBefore = source.CurrentStock;
            var destinationBefore = destination.CurrentStock;

            source.CurrentStock -= quantity;
            source.Status = source.CurrentStock == 0 ? "No Stock" : source.CurrentStock <= source.ReorderLevel ? "Low Stock" : "In Stock";
            destination.CurrentStock += quantity;
            destination.Status = destination.CurrentStock == 0 ? "No Stock" : destination.CurrentStock <= destination.ReorderLevel ? "Low Stock" : "In Stock";

            await _collection.ReplaceOneAsync(i => i.Id == source.Id, source);
            try
            {
                await _collection.ReplaceOneAsync(i => i.Id == destination.Id, destination);
            }
            catch
            {
                source.CurrentStock = sourceBefore;
                source.Status = source.CurrentStock == 0 ? "No Stock" : source.CurrentStock <= source.ReorderLevel ? "Low Stock" : "In Stock";
                await _collection.ReplaceOneAsync(i => i.Id == source.Id, source);
                throw;
            }

            var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            await _movements.InsertAsync(new StockMovement
            {
                InventoryItemId = source.Id,
                ItemName = source.Item ?? "",
                QuantityDelta = -quantity,
                StockBefore = sourceBefore,
                StockAfter = source.CurrentStock,
                Reason = "Transfer Out",
                ReferenceType = "Transfer",
                ReferenceId = transferGroupId,
                Note = trimmedNote,
                BranchId = source.BranchId,
                PerformedBy = performedBy,
                TransferGroupId = transferGroupId
            });
            await _movements.InsertAsync(new StockMovement
            {
                InventoryItemId = destination.Id,
                ItemName = destination.Item ?? "",
                QuantityDelta = quantity,
                StockBefore = destinationBefore,
                StockAfter = destination.CurrentStock,
                Reason = "Transfer In",
                ReferenceType = "Transfer",
                ReferenceId = transferGroupId,
                Note = trimmedNote,
                BranchId = destination.BranchId,
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

            // Return items for specific branch OR shared items (empty BranchId)
            var filter = Builders<IngredientItem>.Filter.Or(
                Builders<IngredientItem>.Filter.Eq(i => i.BranchId, branchId),
                Builders<IngredientItem>.Filter.Eq(i => i.BranchId, string.Empty)
            );
            var list = await _collection.Find(filter).ToListAsync();
            return list
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

            var filter = Builders<IngredientItem>.Filter.And(
                Builders<IngredientItem>.Filter.Or(
                    Builders<IngredientItem>.Filter.Eq(i => i.BranchId, branchId),
                    Builders<IngredientItem>.Filter.Eq(i => i.BranchId, string.Empty)
                ),
                Builders<IngredientItem>.Filter.Where(i => i.CurrentStock <= i.ReorderLevel)
            );
            var list = await _collection.Find(filter).ToListAsync();
            return list
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

            var filter = Builders<IngredientItem>.Filter.Or(
                Builders<IngredientItem>.Filter.Eq(i => i.BranchId, branchId),
                Builders<IngredientItem>.Filter.Eq(i => i.BranchId, string.Empty)
            );
            return await _collection.CountDocumentsAsync(filter);
        }
    }
}
