using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using SelfOrderingSystemKiosk.Models;
using CustomerOrderItem = SelfOrderingSystemKiosk.Areas.Customer.Models.OrderItem;

namespace SelfOrderingSystemKiosk.Services
{
    public partial class MenuItemService
    {
        private readonly IMongoCollection<MenuItem> _collection;
        private readonly IMongoCollection<BsonDocument> _rawCollection;
        private readonly IngredientStockService _ingredients;
        private readonly ILogger<MenuItemService> _logger;
        private readonly SemaphoreSlim _branchFieldNormalizeLock = new(1, 1);
        private bool _branchFieldsNormalized;

        private enum IngredientUsageSource
        {
            Recipe,
            Sauce
        }

        private sealed record IngredientUsageLine(
            string IngredientId,
            int Quantity,
            string MenuItemName,
            string SubmittedItemName,
            IngredientUsageSource Source);

        private sealed class IngredientUsagePlan
        {
            public List<IngredientUsageLine> Lines { get; } = new();
            public List<MenuItem> MenuItems { get; } = new();
            public List<string> MissingMenuItems { get; } = new();
        }

        public MenuItemService(
            IMongoClient mongoClient,
            IConfiguration config,
            IngredientStockService ingredients,
            ILogger<MenuItemService> logger)
        {
            var dbName = config["KitchenDatabase:DatabaseName"] ?? "Kitchen";
            var collectionName = config["KitchenDatabase:MenuItemsCollectionName"] ?? "MenuItems";
            var database = mongoClient.GetDatabase(dbName);
            _collection = database.GetCollection<MenuItem>(collectionName);
            _rawCollection = database.GetCollection<BsonDocument>(collectionName);
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
            await NormalizeLegacyBranchFieldAsync();

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
            await NormalizeLegacyBranchFieldAsync();

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
            list = await FilterCurrentlyStockedAsync(list);
            return list
                .OrderByDescending(i => i.MenuOrder)
                .ThenBy(i => i.Item ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<MenuItem?> GetByIdAsync(string id)
        {
            await NormalizeLegacyBranchFieldAsync();
            return await _collection.Find(x => x.Id == id).FirstOrDefaultAsync();
        }

        public async Task AddAsync(MenuItem item)
        {
            item.BranchId = item.BranchId?.Trim() ?? string.Empty;
            item.Recipe = await SanitizeRecipeLinesAsync(item.BranchId, item.Recipe);

            if (string.Equals(item.Category, "Unavailable", StringComparison.Ordinal))
                item.Availability = "Unavailable";
            else if (string.IsNullOrEmpty(item.Availability))
                item.Availability = "Available";

            await _collection.InsertOneAsync(item);
        }

        public async Task<bool> UpdateAsync(MenuItem item)
        {
            item.BranchId = item.BranchId?.Trim() ?? string.Empty;
            item.Recipe = await SanitizeRecipeLinesAsync(item.BranchId, item.Recipe);

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
                .Set(x => x.Recipe, item.Recipe)
                .Set(x => x.BranchId, item.BranchId)
                .Unset("branchId");

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

        public async Task<MenuItem?> GetByNameAsync(string itemName, string? branchId = null)
        {
            await NormalizeLegacyBranchFieldAsync();

            if (string.IsNullOrWhiteSpace(branchId))
                return await _collection.Find(x => x.Item == itemName).FirstOrDefaultAsync();

            var trimmedBranchId = branchId.Trim();
            var branchItem = await _collection
                .Find(Builders<MenuItem>.Filter.And(
                    Builders<MenuItem>.Filter.Eq(x => x.Item, itemName),
                    BranchRecordFilter(trimmedBranchId)))
                .FirstOrDefaultAsync();
            if (branchItem != null)
                return branchItem;

            return await _collection
                .Find(Builders<MenuItem>.Filter.And(
                    Builders<MenuItem>.Filter.Eq(x => x.Item, itemName),
                    SharedBranchFilter()))
                .FirstOrDefaultAsync();
        }

    }
}
