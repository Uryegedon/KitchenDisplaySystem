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
            var lookupName = NormalizeSubmittedItemName(itemName);
            var item = await GetByNameAsync(lookupName);
            if (item == null && lookupName.StartsWith("Coffee - ", StringComparison.OrdinalIgnoreCase))
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

            var recipeIngredientIds = (item?.Recipe ?? new List<MenuRecipeLine>())
                .Select(r => r.IngredientId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var sauceUse in (await BuildSauceUsageAsync(itemName, item?.Item ?? lookupName, quantity))
                .Where(use => !recipeIngredientIds.Contains(use.IngredientId)))
            {
                try
                {
                    await _ingredients.DecrementForSaleAsync(
                        sauceUse.IngredientId,
                        sauceUse.Quantity,
                        item?.Item ?? itemName,
                        referenceType ?? "Order",
                        referenceId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Sauce decrement failed for menu {Menu} ingredient {IngredientId}", itemName, sauceUse.IngredientId);
                }
            }

            return true;
        }

        public async Task<decimal> CalculateOrderCostAsync(IEnumerable<SelfOrderingSystemKiosk.Areas.Customer.Models.OrderItem>? orderItems)
        {
            var total = 0m;
            foreach (var orderItem in orderItems ?? Enumerable.Empty<SelfOrderingSystemKiosk.Areas.Customer.Models.OrderItem>())
            {
                if (string.IsNullOrWhiteSpace(orderItem.ItemName) || orderItem.Quantity <= 0)
                    continue;

                var lookupName = NormalizeSubmittedItemName(orderItem.ItemName);
                var item = await GetByNameAsync(lookupName);
                if (item == null && lookupName.StartsWith("Coffee - ", StringComparison.OrdinalIgnoreCase))
                    item = await GetByNameAsync("Coffee");

                if (item?.Recipe is { Count: > 0 })
                {
                    foreach (var line in item.Recipe)
                    {
                        if (string.IsNullOrWhiteSpace(line.IngredientId) || line.QuantityPerUnit <= 0)
                            continue;

                        var totalQty = (long)line.QuantityPerUnit * orderItem.Quantity;
                        total += await _ingredients.EstimateCostAsync(line.IngredientId, totalQty > int.MaxValue ? int.MaxValue : (int)totalQty);
                    }
                }

                var recipeIngredientIds = (item?.Recipe ?? new List<MenuRecipeLine>())
                    .Select(r => r.IngredientId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var sauceUse in (await BuildSauceUsageAsync(orderItem.ItemName, item?.Item ?? lookupName, orderItem.Quantity))
                    .Where(use => !recipeIngredientIds.Contains(use.IngredientId)))
                    total += await _ingredients.EstimateCostAsync(sauceUse.IngredientId, sauceUse.Quantity);
            }

            return Math.Round(total, 2, MidpointRounding.AwayFromZero);
        }

        private static string NormalizeSubmittedItemName(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName))
                return string.Empty;

            const string flavorMarker = " (Flavors:";
            var markerIndex = itemName.IndexOf(flavorMarker, StringComparison.OrdinalIgnoreCase);
            var normalized = markerIndex >= 0
                ? itemName[..markerIndex].Trim()
                : itemName.Trim();

            return normalized.StartsWith("Coffee - ", StringComparison.OrdinalIgnoreCase)
                ? "Coffee"
                : normalized;
        }

        private async Task<List<(string IngredientId, int Quantity)>> BuildSauceUsageAsync(string submittedItemName, string menuItemName, int orderQuantity)
        {
            if (orderQuantity <= 0)
                return new List<(string IngredientId, int Quantity)>();

            var chickenPieces = ExtractChickenPieceCount(submittedItemName);
            if (chickenPieces <= 0 && IsChickenWingMenu(menuItemName))
                chickenPieces = ExtractChickenPieceCount(menuItemName);
            if (chickenPieces <= 0)
                return new List<(string IngredientId, int Quantity)>();

            var sauceNames = ExtractSubmittedFlavors(submittedItemName)
                .Select(MapFlavorToSauceName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!sauceNames.Any())
            {
                var inferred = MapFlavorToSauceName(menuItemName);
                if (!string.IsNullOrWhiteSpace(inferred))
                    sauceNames.Add(inferred);
            }

            if (!sauceNames.Any())
                return new List<(string IngredientId, int Quantity)>();

            var totalMl = chickenPieces * 4 * orderQuantity;
            var perSauce = Math.Max(1, (int)Math.Round(totalMl / (decimal)sauceNames.Count, MidpointRounding.AwayFromZero));
            var usage = new List<(string IngredientId, int Quantity)>();
            foreach (var sauceName in sauceNames)
            {
                var ingredient = await _ingredients.GetByNameAsync(sauceName);
                if (ingredient == null || !string.Equals(ingredient.Unit, "ml", StringComparison.OrdinalIgnoreCase))
                    continue;

                usage.Add((ingredient.Id, perSauce));
            }

            return usage;
        }

        private static bool IsChickenWingMenu(string? name)
        {
            return !string.IsNullOrWhiteSpace(name)
                && name.Contains("wing", StringComparison.OrdinalIgnoreCase);
        }

        private static int ExtractChickenPieceCount(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return 0;

            var normalized = name.Replace("-", " ", StringComparison.Ordinal);
            var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < tokens.Length; i++)
            {
                if (!int.TryParse(tokens[i], out var count) || count <= 0)
                    continue;

                var next = i + 1 < tokens.Length ? tokens[i + 1] : "";
                var afterNext = i + 2 < tokens.Length ? tokens[i + 2] : "";
                if (next.StartsWith("piece", StringComparison.OrdinalIgnoreCase)
                    || next.StartsWith("pc", StringComparison.OrdinalIgnoreCase)
                    || afterNext.Contains("wing", StringComparison.OrdinalIgnoreCase))
                    return count;
            }

            return 0;
        }

        private static IEnumerable<string> ExtractSubmittedFlavors(string? submittedItemName)
        {
            if (string.IsNullOrWhiteSpace(submittedItemName))
                yield break;

            const string marker = "(Flavors:";
            var start = submittedItemName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                yield break;

            start += marker.Length;
            var end = submittedItemName.IndexOf(')', start);
            var raw = end >= 0 ? submittedItemName[start..end] : submittedItemName[start..];
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return part;
        }

        private static string? MapFlavorToSauceName(string? flavor)
        {
            if (string.IsNullOrWhiteSpace(flavor))
                return null;

            var norm = flavor.Trim();
            if (norm.Contains("mayora sriracha original", StringComparison.OrdinalIgnoreCase))
                return "Mayora Sriracha Original";
            if (norm.Contains("sriracha mayo", StringComparison.OrdinalIgnoreCase) || norm.Contains("garlic mayo", StringComparison.OrdinalIgnoreCase))
                return "Mayo Garlic";
            if (norm.Contains("sriracha honey", StringComparison.OrdinalIgnoreCase))
                return "Honey";
            if (norm.Contains("chief parm", StringComparison.OrdinalIgnoreCase))
                return "Chief Parm Base";
            if (norm.Contains("colonel mustard", StringComparison.OrdinalIgnoreCase))
                return "Colonel Mustard";
            if (norm.Contains("konsi honey", StringComparison.OrdinalIgnoreCase))
                return "Konsi Honeysoy";
            if (norm.Contains("soy garlic", StringComparison.OrdinalIgnoreCase))
                return "Mayo Garlic";
            if (norm.Contains("soy spicy", StringComparison.OrdinalIgnoreCase))
                return "Hot Sauce";
            if (norm.Contains("vice thai", StringComparison.OrdinalIgnoreCase))
                return "Vice Thai";
            if (norm.Contains("teriyaki", StringComparison.OrdinalIgnoreCase))
                return "Teriyaki Sen";
            if (norm.Contains("salted egg", StringComparison.OrdinalIgnoreCase))
                return "Salted Egg";
            if (norm.Contains("buffalo", StringComparison.OrdinalIgnoreCase))
                return "Hot Sauce";
            return null;
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

        // ====================
        // Branch Filtering Methods
        // ====================

        /// <summary>
        /// Gets all menu items filtered by branch (empty branchId returns items with empty BranchId or matching branch)
        /// </summary>
        public async Task<List<MenuItem>> GetAllByBranchAsync(string? branchId)
        {
            var validItemFilter = Builders<MenuItem>.Filter.And(
                Builders<MenuItem>.Filter.Ne(x => x.Item, (string)null!),
                Builders<MenuItem>.Filter.Ne(x => x.Item, ""));

            if (string.IsNullOrEmpty(branchId))
            {
                var allItems = await _collection.Find(validItemFilter).ToListAsync();
                return allItems
                    .OrderBy(i => IsAvailableForCustomerMenu(i.Availability) ? 0 : 1)
                    .ThenByDescending(i => i.MenuOrder)
                    .ThenBy(i => i.Item ?? "", StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            // Return items for specific branch OR shared items (empty BranchId)
            var branchFilter = Builders<MenuItem>.Filter.And(
                validItemFilter,
                Builders<MenuItem>.Filter.Or(
                    Builders<MenuItem>.Filter.Eq(i => i.BranchId, branchId),
                    Builders<MenuItem>.Filter.Eq(i => i.BranchId, string.Empty)
                )
            );
            var branchItems = await _collection.Find(branchFilter).ToListAsync();
            return branchItems
                .OrderBy(i => IsAvailableForCustomerMenu(i.Availability) ? 0 : 1)
                .ThenByDescending(i => i.MenuOrder)
                .ThenBy(i => i.Item ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Gets available menu items filtered by branch
        /// </summary>
        public async Task<List<MenuItem>> GetAvailableByBranchAsync(string? branchId)
        {
            var availableOrUnset = Builders<MenuItem>.Filter.Or(
                Builders<MenuItem>.Filter.Eq(x => x.Availability, (string)null!),
                Builders<MenuItem>.Filter.Eq(x => x.Availability, ""),
                Builders<MenuItem>.Filter.Eq(x => x.Availability, "Available"),
                Builders<MenuItem>.Filter.Not(Builders<MenuItem>.Filter.Exists(x => x.Availability)));

            var validItemFilter = Builders<MenuItem>.Filter.And(
                Builders<MenuItem>.Filter.Ne(x => x.Item, (string)null!),
                Builders<MenuItem>.Filter.Ne(x => x.Item, ""));

            if (string.IsNullOrEmpty(branchId))
            {
                var allFilter = Builders<MenuItem>.Filter.And(validItemFilter, availableOrUnset);
                var allItems = await _collection.Find(allFilter).ToListAsync();
                return allItems
                    .OrderByDescending(i => i.MenuOrder)
                    .ThenBy(i => i.Item ?? "", StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            // Return items for specific branch OR shared items (empty BranchId)
            var branchFilter = Builders<MenuItem>.Filter.And(
                validItemFilter,
                availableOrUnset,
                Builders<MenuItem>.Filter.Or(
                    Builders<MenuItem>.Filter.Eq(i => i.BranchId, branchId),
                    Builders<MenuItem>.Filter.Eq(i => i.BranchId, string.Empty)
                )
            );
            var branchItems = await _collection.Find(branchFilter).ToListAsync();
            return branchItems
                .OrderByDescending(i => i.MenuOrder)
                .ThenBy(i => i.Item ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Gets menu item count by branch
        /// </summary>
        public async Task<long> GetCountByBranchAsync(string? branchId)
        {
            var validItemFilter = Builders<MenuItem>.Filter.And(
                Builders<MenuItem>.Filter.Ne(x => x.Item, (string)null!),
                Builders<MenuItem>.Filter.Ne(x => x.Item, ""));

            if (string.IsNullOrEmpty(branchId))
            {
                return await _collection.CountDocumentsAsync(validItemFilter);
            }

            var filter = Builders<MenuItem>.Filter.And(
                validItemFilter,
                Builders<MenuItem>.Filter.Or(
                    Builders<MenuItem>.Filter.Eq(i => i.BranchId, branchId),
                    Builders<MenuItem>.Filter.Eq(i => i.BranchId, string.Empty)
                )
            );
            return await _collection.CountDocumentsAsync(filter);
        }
    }
}
