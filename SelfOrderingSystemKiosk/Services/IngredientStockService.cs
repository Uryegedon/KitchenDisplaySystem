using Microsoft.Extensions.Logging;
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
            var status = newStock <= item.ReorderLevel ? "Low Stock" : "In Stock";

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
                    Note = $"Recipe: {menuItemName}"
                });
            }

            return true;
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

        public async Task<IngredientItem?> GetByNameAsync(string itemName) =>
            await _collection.Find(x => x.Item == itemName).FirstOrDefaultAsync();

        public async Task AddAsync(IngredientItem item)
        {
            item.Status = item.CurrentStock <= item.ReorderLevel ? "Low Stock" : "In Stock";
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
                    Note = "New ingredient"
                });
            }
        }

        public async Task UpdateAsync(IngredientItem item)
        {
            item.Status = item.CurrentStock <= item.ReorderLevel ? "Low Stock" : "In Stock";
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
            item.Status = item.CurrentStock <= item.ReorderLevel ? "Low Stock" : "In Stock";

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
                Note = note
            });

            return true;
        }

        public async Task RecordAdjustmentAsync(string ingredientId, string itemName, int stockBefore, int stockAfter, string note)
        {
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
                Note = note
            });
        }
    }
}
