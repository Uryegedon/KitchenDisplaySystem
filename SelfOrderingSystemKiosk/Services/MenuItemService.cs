using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using SelfOrderingSystemKiosk.Models;

namespace SelfOrderingSystemKiosk.Services
{
    public class MenuItemService
    {
        private readonly IMongoCollection<MenuItem> _collection;
        private readonly StockMovementService _movements;
        private readonly ILogger<MenuItemService> _logger;

        public MenuItemService(IMongoClient mongoClient, IConfiguration config, StockMovementService movements, ILogger<MenuItemService> logger)
        {
            var dbName = config["KitchenDatabase:DatabaseName"] ?? "Kitchen";
            var collectionName = config["KitchenDatabase:MenuItemsCollectionName"] ?? "MenuItems";
            _collection = mongoClient.GetDatabase(dbName).GetCollection<MenuItem>(collectionName);
            _movements = movements;
            _logger = logger;
        }

        private static bool IsAvailableForCustomerMenu(string? availability)
        {
            if (string.IsNullOrEmpty(availability)) return true;
            return string.Equals(availability, "Available", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<List<MenuItem>> GetAllAsync()
        {
            var list = await _collection.Find(_ => true).ToListAsync();
            return list
                .OrderBy(i => IsAvailableForCustomerMenu(i.Availability) ? 0 : 1)
                .ThenByDescending(i => i.MenuOrder)
                .ThenBy(i => i.Item ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<List<MenuItem>> GetAvailableAsync()
        {
            var availableOrUnset = Builders<MenuItem>.Filter.Or(
                Builders<MenuItem>.Filter.Eq(x => x.Availability, (string)null!),
                Builders<MenuItem>.Filter.Eq(x => x.Availability, ""),
                Builders<MenuItem>.Filter.Eq(x => x.Availability, "Available"),
                Builders<MenuItem>.Filter.Not(Builders<MenuItem>.Filter.Exists(x => x.Availability)));

            var list = await _collection.Find(availableOrUnset).ToListAsync();
            return list
                .OrderByDescending(i => i.MenuOrder)
                .ThenBy(i => i.Item ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<MenuItem?> GetByIdAsync(string id) =>
            await _collection.Find(x => x.Id == id).FirstOrDefaultAsync();

        public async Task AddAsync(MenuItem item)
        {
            if (string.Equals(item.Category, "Unavailable", StringComparison.Ordinal))
                item.Availability = "Unavailable";
            else if (item.CurrentStock == 0)
                item.Availability = "Unavailable";
            else if (string.IsNullOrEmpty(item.Availability))
                item.Availability = "Available";

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
                    ReferenceType = "Menu",
                    ReferenceId = item.Id,
                    Note = "New menu item"
                });
            }
        }

        public async Task UpdateAsync(MenuItem item)
        {
            if (string.Equals(item.Category, "Unavailable", StringComparison.Ordinal))
                item.Availability = "Unavailable";
            else if (item.CurrentStock == 0)
                item.Availability = "Unavailable";
            else if (string.IsNullOrEmpty(item.Availability))
                item.Availability = "Available";

            await _collection.ReplaceOneAsync(x => x.Id == item.Id, item);
        }

        public async Task DeleteAsync(string id) =>
            await _collection.DeleteOneAsync(x => x.Id == id);

        public async Task ToggleAvailabilityAsync(string id, string availability)
        {
            var update = Builders<MenuItem>.Update.Set(x => x.Availability, availability);
            await _collection.UpdateOneAsync(x => x.Id == id, update);
        }

        public async Task<MenuItem?> GetByNameAsync(string itemName) =>
            await _collection.Find(x => x.Item == itemName).FirstOrDefaultAsync();

        public async Task<bool> DecrementStockAsync(string itemName, int quantity, string? reason = null, string? referenceType = null, string? referenceId = null)
        {
            var item = await GetByNameAsync(itemName);
            if (item == null)
            {
                _logger.LogWarning("DecrementStock (menu): item '{Item}' not found.", itemName);
                return false;
            }

            var oldStock = item.CurrentStock;
            var newStock = Math.Max(0, oldStock - quantity);
            var availability = newStock == 0 ? "Unavailable" : "Available";

            var update = Builders<MenuItem>.Update
                .Set(x => x.CurrentStock, newStock)
                .Set(x => x.Status, newStock <= item.ReorderLevel ? "Low Stock" : "In Stock")
                .Set(x => x.Availability, availability);

            var result = await _collection.UpdateOneAsync(x => x.Item == itemName, update);
            if (result.ModifiedCount == 0)
                return false;

            var delta = newStock - oldStock;
            if (delta != 0)
            {
                await _movements.InsertAsync(new StockMovement
                {
                    InventoryItemId = item.Id,
                    ItemName = item.Item ?? itemName,
                    QuantityDelta = delta,
                    StockBefore = oldStock,
                    StockAfter = newStock,
                    Reason = reason ?? "Sale",
                    ReferenceType = referenceType ?? "Order",
                    ReferenceId = referenceId,
                    Note = null
                });
            }

            return true;
        }

        public async Task<bool> IncreaseStockAsync(string menuItemId, int quantityAdded, string referenceType, string? referenceId, string? note = null)
        {
            if (quantityAdded <= 0)
                return false;

            var item = await GetByIdAsync(menuItemId);
            if (item == null)
                return false;

            var oldStock = item.CurrentStock;
            item.CurrentStock += quantityAdded;
            item.Status = item.CurrentStock <= item.ReorderLevel ? "Low Stock" : "In Stock";
            item.Availability = item.CurrentStock == 0 ? "Unavailable" : "Available";

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

        public async Task RecordAdjustmentAsync(string menuItemId, string itemName, int stockBefore, int stockAfter, string note)
        {
            await _movements.InsertAsync(new StockMovement
            {
                InventoryItemId = menuItemId,
                ItemName = itemName,
                QuantityDelta = stockAfter - stockBefore,
                StockBefore = stockBefore,
                StockAfter = stockAfter,
                Reason = "Adjustment",
                ReferenceType = "Menu",
                ReferenceId = menuItemId,
                Note = note
            });
        }
    }
}
