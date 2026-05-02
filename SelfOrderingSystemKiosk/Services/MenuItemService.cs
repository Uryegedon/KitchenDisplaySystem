using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using SelfOrderingSystemKiosk.Models;

namespace SelfOrderingSystemKiosk.Services
{
    public class MenuItemService
    {
        private readonly IMongoCollection<MenuItem> _collection;
        private readonly IngredientStockService _ingredients;
        private readonly ILogger<MenuItemService> _logger;

        public MenuItemService(
            IMongoClient mongoClient,
            IConfiguration config,
            IngredientStockService ingredients,
            ILogger<MenuItemService> logger)
        {
            var dbName = config["KitchenDatabase:DatabaseName"] ?? "Kitchen";
            var collectionName = config["KitchenDatabase:MenuItemsCollectionName"] ?? "MenuItems";
            _collection = mongoClient.GetDatabase(dbName).GetCollection<MenuItem>(collectionName);
            _ingredients = ingredients;
            _logger = logger;
        }

        private static bool IsAvailableForCustomerMenu(string? availability)
        {
            if (string.IsNullOrEmpty(availability)) return true;
            return string.Equals(availability, "Available", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<List<MenuItem>> GetAllAsync()
        {
            var validItemFilter = Builders<MenuItem>.Filter.And(
                Builders<MenuItem>.Filter.Ne(x => x.Item, (string)null!),
                Builders<MenuItem>.Filter.Ne(x => x.Item, ""));

            var list = await _collection.Find(validItemFilter).ToListAsync();
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

            var validItemFilter = Builders<MenuItem>.Filter.And(
                Builders<MenuItem>.Filter.Ne(x => x.Item, (string)null!),
                Builders<MenuItem>.Filter.Ne(x => x.Item, ""));

            var filter = Builders<MenuItem>.Filter.And(validItemFilter, availableOrUnset);
            var list = await _collection.Find(filter).ToListAsync();
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
            else if (string.IsNullOrEmpty(item.Availability))
                item.Availability = "Available";

            await _collection.InsertOneAsync(item);
        }

        public async Task<bool> UpdateAsync(MenuItem item)
        {
            if (string.Equals(item.Category, "Unavailable", StringComparison.Ordinal))
                item.Availability = "Unavailable";
            else if (string.IsNullOrEmpty(item.Availability))
                item.Availability = "Available";

            var update = Builders<MenuItem>.Update
                .Set(x => x.Item, item.Item)
                .Set(x => x.Category, item.Category)
                .Set(x => x.FoodCategory, item.FoodCategory)
                .Set(x => x.MenuOrder, item.MenuOrder)
                .Set(x => x.CurrentStock, item.CurrentStock)
                .Set(x => x.Unit, item.Unit)
                .Set(x => x.ReorderLevel, item.ReorderLevel)
                .Set(x => x.Price, item.Price)
                .Set(x => x.Status, item.Status)
                .Set(x => x.Availability, item.Availability)
                .Set(x => x.Image, item.Image)
                .Set(x => x.Recipe, item.Recipe);

            var result = await _collection.UpdateOneAsync(x => x.Id == item.Id, update);
            return result.MatchedCount > 0;
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
            if (item == null && itemName.StartsWith("Coffee - ", StringComparison.OrdinalIgnoreCase))
            {
                item = await GetByNameAsync("Coffee");
            }

            if (item == null)
            {
                _logger.LogWarning("DecrementStock (menu): item '{Item}' not found.", itemName);
                return false;
            }

            if (item.Recipe is { Count: > 0 })
            {
                foreach (var line in item.Recipe)
                {
                    if (string.IsNullOrWhiteSpace(line.IngredientId) || line.QuantityPerUnit <= 0)
                        continue;
                    var total = (long)line.QuantityPerUnit * quantity;
                    var useQty = total > int.MaxValue ? int.MaxValue : (int)total;
                    if (useQty <= 0)
                        continue;
                    try
                    {
                        await _ingredients.DecrementForSaleAsync(
                            line.IngredientId.Trim(),
                            useQty,
                            item.Item ?? itemName,
                            referenceType ?? "Order",
                            referenceId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Recipe decrement failed for menu {Menu} ingredient {IngredientId}", itemName, line.IngredientId);
                    }
                }
            }

            return true;
        }

        public async Task<bool> IncreaseStockAsync(string menuItemId, int quantityAdded, string referenceType, string? referenceId, string? note = null)
        {
            _logger.LogWarning("Menu item stock adjustment ignored for {MenuItemId}; stock is tracked through ingredients.", menuItemId);
            await Task.CompletedTask;
            return false;
        }

        public async Task RecordAdjustmentAsync(string menuItemId, string itemName, int stockBefore, int stockAfter, string note)
        {
            _logger.LogWarning("Menu item stock adjustment ignored for {MenuItem}; stock is tracked through ingredients.", itemName);
            await Task.CompletedTask;
        }
    }
}
