using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using SelfOrderingSystemKiosk.Models;

namespace SelfOrderingSystemKiosk.Services
{
    public class MenuItemService
    {
        private readonly IMongoCollection<MenuItem> _collection;
        private readonly IMongoCollection<BsonDocument> _rawCollection;
        private readonly IngredientStockService _ingredients;
        private readonly ILogger<MenuItemService> _logger;
        private readonly SemaphoreSlim _branchFieldNormalizeLock = new(1, 1);
        private bool _branchFieldsNormalized;

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

        public async Task<bool> DecrementStockAsync(string itemName, int quantity, string? reason = null, string? referenceType = null, string? referenceId = null, string? branchId = null)
        {
            var lookupName = NormalizeSubmittedItemName(itemName);
            var item = await GetByNameAsync(lookupName, branchId);
            if (item == null && lookupName.StartsWith("Coffee - ", StringComparison.OrdinalIgnoreCase))
            {
                item = await GetByNameAsync("Coffee", branchId);
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
                        await SyncAvailabilityForIngredientAsync(line.IngredientId.Trim());
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Recipe decrement failed for menu {Menu} ingredient {IngredientId}", itemName, line.IngredientId);
                    }
                }
            }

            var recipeIngredientIds = (item.Recipe ?? new List<MenuRecipeLine>())
                .Select(r => r.IngredientId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var sauceUse in (await BuildSauceUsageAsync(itemName, item, item.Item ?? lookupName, quantity, branchId))
                .Where(use => !recipeIngredientIds.Contains(use.IngredientId)))
            {
                try
                {
                    await _ingredients.DecrementForSaleAsync(
                        sauceUse.IngredientId,
                        sauceUse.Quantity,
                        item.Item ?? itemName,
                        referenceType ?? "Order",
                        referenceId);
                    await SyncAvailabilityForIngredientAsync(sauceUse.IngredientId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Sauce decrement failed for menu {Menu} ingredient {IngredientId}", itemName, sauceUse.IngredientId);
                }
            }

            await SyncAvailabilityForMenuItemAsync(item);
            return true;
        }

        public async Task<decimal> CalculateOrderCostAsync(IEnumerable<SelfOrderingSystemKiosk.Areas.Customer.Models.OrderItem>? orderItems, string? branchId = null)
        {
            var total = 0m;
            foreach (var orderItem in orderItems ?? Enumerable.Empty<SelfOrderingSystemKiosk.Areas.Customer.Models.OrderItem>())
            {
                if (string.IsNullOrWhiteSpace(orderItem.ItemName) || orderItem.Quantity <= 0)
                    continue;

                var lookupName = NormalizeSubmittedItemName(orderItem.ItemName);
                var item = await GetByNameAsync(lookupName, branchId);
                if (item == null && lookupName.StartsWith("Coffee - ", StringComparison.OrdinalIgnoreCase))
                    item = await GetByNameAsync("Coffee", branchId);

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
                foreach (var sauceUse in (await BuildSauceUsageAsync(orderItem.ItemName, item, item?.Item ?? lookupName, orderItem.Quantity, branchId))
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

        private async Task<List<(string IngredientId, int Quantity)>> BuildSauceUsageAsync(string submittedItemName, MenuItem? menuItem, string menuItemName, int orderQuantity, string? branchId = null)
        {
            if (orderQuantity <= 0)
                return new List<(string IngredientId, int Quantity)>();

            var chickenPieces = ExtractChickenPieceCount(submittedItemName);
            if (chickenPieces <= 0 && IsChickenWingMenu(menuItemName))
                chickenPieces = ExtractChickenPieceCount(menuItemName);
            if (chickenPieces <= 0 && IsChickenWingMenu(menuItemName))
                chickenPieces = await GetChickenPiecesFromRecipeAsync(menuItem);
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
                var ingredient = await _ingredients.GetByNameAsync(sauceName, branchId);
                if (ingredient == null)
                    continue;

                usage.Add((ingredient.Id, perSauce));
            }

            return usage;
        }

        private async Task<int> GetChickenPiecesFromRecipeAsync(MenuItem? menuItem)
        {
            if (menuItem?.Recipe is not { Count: > 0 })
                return 0;

            foreach (var line in menuItem.Recipe)
            {
                if (string.IsNullOrWhiteSpace(line.IngredientId) || line.QuantityPerUnit <= 0)
                    continue;

                var ingredient = await _ingredients.GetByIdAsync(line.IngredientId.Trim());
                if (ingredient?.Item != null && ingredient.Item.Contains("wing", StringComparison.OrdinalIgnoreCase))
                    return line.QuantityPerUnit;
            }

            return 0;
        }

        public async Task SeedRecipesFromMenuItemNamesAsync()
        {
            var ingredients = await _ingredients.GetAllAsync();
            if (ingredients.Count == 0)
                return;

            var items = await _collection.Find(Builders<MenuItem>.Filter.And(
                    Builders<MenuItem>.Filter.Ne(x => x.Item, (string)null!),
                    Builders<MenuItem>.Filter.Ne(x => x.Item, "")))
                .ToListAsync();

            var updated = 0;
            foreach (var item in items)
            {
                var inferred = InferRecipeFromName(item, ingredients);
                if (inferred.Count == 0)
                    continue;

                var existing = SanitizeRecipeLines(item.BranchId, item.Recipe, ingredients);
                var merged = existing
                    .GroupBy(r => r.IngredientId.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                var existingIds = merged
                    .Select(r => r.IngredientId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var line in inferred)
                {
                    if (existingIds.Add(line.IngredientId))
                        merged.Add(line);
                }

                if (merged.Count == existing.Count)
                    continue;

                await _collection.UpdateOneAsync(
                    x => x.Id == item.Id,
                    Builders<MenuItem>.Update.Set(x => x.Recipe, merged));
                item.Recipe = merged;
                await SyncAvailabilityForMenuItemAsync(item);
                updated++;
            }

            if (updated > 0)
                _logger.LogInformation("Seeded inferred recipes for {Count} menu items.", updated);
        }

        public async Task SyncAvailabilityForIngredientAsync(string ingredientId)
        {
            if (string.IsNullOrWhiteSpace(ingredientId))
                return;

            var filter = Builders<MenuItem>.Filter.ElemMatch(
                x => x.Recipe,
                r => r.IngredientId == ingredientId);

            var items = await _collection.Find(filter).ToListAsync();
            foreach (var item in items)
                await SyncAvailabilityForMenuItemAsync(item);
        }

        public async Task<int> CleanupInvalidRecipeLinesAsync(string? branchId = null)
        {
            await NormalizeLegacyBranchFieldAsync();

            var ingredients = await _ingredients.GetAllAsync();
            if (ingredients.Count == 0)
                return 0;

            var validItemFilter = Builders<MenuItem>.Filter.And(
                Builders<MenuItem>.Filter.Ne(x => x.Item, (string)null!),
                Builders<MenuItem>.Filter.Ne(x => x.Item, ""));

            FilterDefinition<MenuItem> filter = validItemFilter;
            if (!string.IsNullOrWhiteSpace(branchId))
            {
                filter = Builders<MenuItem>.Filter.And(
                    validItemFilter,
                    Builders<MenuItem>.Filter.Or(
                        BranchRecordFilter(branchId.Trim()),
                        SharedBranchFilter()));
            }

            var items = await _collection.Find(filter).ToListAsync();
            var updated = 0;

            foreach (var item in items)
            {
                var existing = item.Recipe ?? new List<MenuRecipeLine>();
                if (existing.Count == 0)
                    continue;

                var sanitized = SanitizeRecipeLines(item.BranchId, existing, ingredients);
                if (RecipesEqual(existing, sanitized))
                    continue;

                await _collection.UpdateOneAsync(
                    x => x.Id == item.Id,
                    Builders<MenuItem>.Update.Set(x => x.Recipe, sanitized));
                item.Recipe = sanitized;
                await SyncAvailabilityForMenuItemAsync(item);
                updated++;
            }

            if (updated > 0)
                _logger.LogInformation("Removed invalid recipe ingredients from {Count} menu items.", updated);

            return updated;
        }

        private async Task<List<MenuItem>> FilterCurrentlyStockedAsync(List<MenuItem> items)
        {
            var ingredientIds = items
                .SelectMany(item => item.Recipe ?? new List<MenuRecipeLine>())
                .Where(line => !string.IsNullOrWhiteSpace(line.IngredientId) && line.QuantityPerUnit > 0)
                .Select(line => line.IngredientId.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var ingredientsById = (await _ingredients.GetByIdsAsync(ingredientIds))
                .Where(i => !string.IsNullOrWhiteSpace(i.Id))
                .ToDictionary(i => i.Id.Trim(), StringComparer.OrdinalIgnoreCase);

            return items
                .Where(item => IsCurrentlyStocked(item, ingredientsById))
                .ToList();
        }

        private static bool IsCurrentlyStocked(MenuItem item, IReadOnlyDictionary<string, IngredientItem> ingredientsById)
        {
            if (!IsAvailableForCustomerMenu(item.Availability))
                return false;

            if (item.Recipe is not { Count: > 0 })
                return true;

            foreach (var line in item.Recipe)
            {
                if (string.IsNullOrWhiteSpace(line.IngredientId) || line.QuantityPerUnit <= 0)
                    continue;

                if (ingredientsById.TryGetValue(line.IngredientId.Trim(), out var ingredient) &&
                    ingredient.CurrentStock <= 0)
                    return false;
            }

            return true;
        }

        private async Task<bool> SyncAvailabilityForMenuItemAsync(MenuItem item)
        {
            if (item.Recipe is not { Count: > 0 })
                return IsAvailableForCustomerMenu(item.Availability);

            var outOfStock = false;
            foreach (var line in item.Recipe)
            {
                if (string.IsNullOrWhiteSpace(line.IngredientId) || line.QuantityPerUnit <= 0)
                    continue;

                var ingredient = await _ingredients.GetByIdAsync(line.IngredientId.Trim());
                if (ingredient?.CurrentStock <= 0)
                {
                    outOfStock = true;
                    break;
                }
            }

            if (!outOfStock)
            {
                if (string.Equals(item.Status, "No Stock", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.Availability, "Unavailable", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(item.Category, "Unavailable", StringComparison.Ordinal))
                {
                    await _collection.UpdateOneAsync(
                        x => x.Id == item.Id,
                        Builders<MenuItem>.Update
                            .Set(x => x.Status, "Available")
                            .Set(x => x.Availability, "Available"));
                    item.Status = "Available";
                    item.Availability = "Available";
                }

                return IsAvailableForCustomerMenu(item.Availability);
            }

            if (!string.Equals(item.Availability, "Unavailable", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(item.Status, "No Stock", StringComparison.OrdinalIgnoreCase))
            {
                await _collection.UpdateOneAsync(
                    x => x.Id == item.Id,
                    Builders<MenuItem>.Update
                        .Set(x => x.Availability, "Unavailable")
                        .Set(x => x.Status, "No Stock"));
                item.Availability = "Unavailable";
                item.Status = "No Stock";
            }

            return false;
        }

        private async Task<List<MenuRecipeLine>> SanitizeRecipeLinesAsync(string? menuBranchId, List<MenuRecipeLine>? recipe)
        {
            var ingredients = await _ingredients.GetAllAsync();
            return SanitizeRecipeLines(menuBranchId, recipe, ingredients);
        }

        private static List<MenuRecipeLine> SanitizeRecipeLines(
            string? menuBranchId,
            IEnumerable<MenuRecipeLine>? recipe,
            IEnumerable<IngredientItem> ingredients)
        {
            if (recipe == null)
                return new List<MenuRecipeLine>();

            var ingredientsById = ingredients
                .Where(i => !string.IsNullOrWhiteSpace(i.Id))
                .GroupBy(i => i.Id.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            return recipe
                .Where(line => IsValidRecipeLine(menuBranchId, line, ingredientsById))
                .GroupBy(line => line.IngredientId.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => new MenuRecipeLine
                {
                    IngredientId = g.First().IngredientId.Trim(),
                    QuantityPerUnit = g.First().QuantityPerUnit
                })
                .ToList();
        }

        private static bool IsValidRecipeLine(
            string? menuBranchId,
            MenuRecipeLine? line,
            IReadOnlyDictionary<string, IngredientItem> ingredientsById)
        {
            if (line == null || string.IsNullOrWhiteSpace(line.IngredientId) || line.QuantityPerUnit <= 0)
                return false;

            if (!ingredientsById.TryGetValue(line.IngredientId.Trim(), out var ingredient))
                return false;

            if (!IsBranchCompatible(menuBranchId, ingredient.BranchId))
                return false;

            return !IsUnknownIngredientName(ingredient.Item);
        }

        private static bool IsUnknownIngredientName(string? name)
        {
            var normalized = (name ?? string.Empty).Trim();
            return normalized.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Unknown Ingredient", StringComparison.OrdinalIgnoreCase);
        }

        private static bool RecipesEqual(IReadOnlyList<MenuRecipeLine> left, IReadOnlyList<MenuRecipeLine> right)
        {
            if (left.Count != right.Count)
                return false;

            for (var i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i].IngredientId?.Trim(), right[i].IngredientId?.Trim(), StringComparison.OrdinalIgnoreCase))
                    return false;
                if (left[i].QuantityPerUnit != right[i].QuantityPerUnit)
                    return false;
            }

            return true;
        }

        private static List<MenuRecipeLine> InferRecipeFromName(MenuItem menuItem, List<IngredientItem> ingredients)
        {
            var name = menuItem.Item ?? "";
            var recipe = new List<MenuRecipeLine>();
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(IngredientItem? ingredient, int quantity)
            {
                if (ingredient == null || string.IsNullOrWhiteSpace(ingredient.Id) || quantity <= 0)
                    return;
                if (added.Add(ingredient.Id))
                    recipe.Add(new MenuRecipeLine { IngredientId = ingredient.Id, QuantityPerUnit = quantity });
            }

            var chickenPieces = ExtractChickenPieceCount(name);
            if (chickenPieces > 0 && IsChickenWingMenu(name))
            {
                Add(FindCompatibleIngredient(menuItem.BranchId, ingredients, "Chicken Wings", "Chicken Wing", "Wings"), chickenPieces);

                var sauceName = MapFlavorToSauceName(name);
                if (!string.IsNullOrWhiteSpace(sauceName))
                    Add(FindCompatibleIngredient(menuItem.BranchId, ingredients, sauceName), chickenPieces * 4);
            }

            var directMatches = ingredients
                .Where(i => IsBranchCompatible(menuItem.BranchId, i.BranchId))
                .Where(i => !string.IsNullOrWhiteSpace(i.Item))
                .Where(i => name.Contains(i.Item!, StringComparison.OrdinalIgnoreCase)
                    || i.Item!.Contains(name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(i => string.Equals(i.BranchId, menuItem.BranchId, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(i => i.Item!.Length)
                .Take(3);

            foreach (var ingredient in directMatches)
                Add(ingredient, 1);

            if (name.Contains("rice", StringComparison.OrdinalIgnoreCase))
                Add(FindCompatibleIngredient(menuItem.BranchId, ingredients, "Rice"), 1);
            if (name.Contains("pasta", StringComparison.OrdinalIgnoreCase))
                Add(FindCompatibleIngredient(menuItem.BranchId, ingredients, "Pasta"), 1);
            if (name.Contains("coffee", StringComparison.OrdinalIgnoreCase))
                Add(FindCompatibleIngredient(menuItem.BranchId, ingredients, "Coffee"), 1);
            if (name.Contains("tea", StringComparison.OrdinalIgnoreCase))
                Add(FindCompatibleIngredient(menuItem.BranchId, ingredients, "Tea"), 1);

            return recipe;
        }

        private static IngredientItem? FindCompatibleIngredient(string? menuBranchId, IEnumerable<IngredientItem> ingredients, params string[] names)
        {
            foreach (var name in names)
            {
                var match = ingredients
                    .Where(i => IsBranchCompatible(menuBranchId, i.BranchId))
                    .Where(i => string.Equals(i.Item, name, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(i => string.Equals(i.BranchId, menuBranchId, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
                if (match != null)
                    return match;
            }

            foreach (var name in names)
            {
                var match = ingredients
                    .Where(i => IsBranchCompatible(menuBranchId, i.BranchId))
                    .Where(i => !string.IsNullOrWhiteSpace(i.Item)
                        && (i.Item.Contains(name, StringComparison.OrdinalIgnoreCase)
                            || name.Contains(i.Item, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(i => string.Equals(i.BranchId, menuBranchId, StringComparison.OrdinalIgnoreCase))
                    .ThenBy(i => i.Item!.Length)
                    .FirstOrDefault();
                if (match != null)
                    return match;
            }

            return null;
        }

        private static bool IsBranchCompatible(string? menuBranchId, string? ingredientBranchId)
        {
            if (string.IsNullOrWhiteSpace(menuBranchId))
                return string.IsNullOrWhiteSpace(ingredientBranchId);

            return string.IsNullOrWhiteSpace(ingredientBranchId)
                || string.Equals(ingredientBranchId, menuBranchId, StringComparison.OrdinalIgnoreCase);
        }

        private static FilterDefinition<MenuItem> SharedBranchFilter()
        {
            var filter = Builders<MenuItem>.Filter;
            return filter.Or(
                filter.Eq("BranchId", BsonNull.Value),
                filter.Eq("BranchId", string.Empty),
                filter.Not(filter.Exists("BranchId")));
        }

        private static FilterDefinition<MenuItem> BranchRecordFilter(string branchId)
        {
            return Builders<MenuItem>.Filter.Eq("BranchId", branchId);
        }

        private async Task NormalizeLegacyBranchFieldAsync()
        {
            if (_branchFieldsNormalized)
                return;

            await _branchFieldNormalizeLock.WaitAsync();
            try
            {
                if (_branchFieldsNormalized)
                    return;

                var filter = Builders<BsonDocument>.Filter.Exists("branchId");
                var docs = await _rawCollection.Find(filter)
                    .Project(Builders<BsonDocument>.Projection
                        .Include("_id")
                        .Include("BranchId")
                        .Include("branchId"))
                    .ToListAsync();

                foreach (var doc in docs)
                {
                    var legacyBranchId = doc.GetValue("branchId", BsonNull.Value);
                    var hasCanonicalBranchId = doc.TryGetValue("BranchId", out var canonicalBranchId)
                        && !canonicalBranchId.IsBsonNull
                        && !string.IsNullOrWhiteSpace(canonicalBranchId.ToString());

                    var update = Builders<BsonDocument>.Update.Unset("branchId");
                    if (!hasCanonicalBranchId && !legacyBranchId.IsBsonNull)
                        update = update.Set("BranchId", legacyBranchId.IsString ? legacyBranchId.AsString.Trim() : legacyBranchId.ToString());

                    await _rawCollection.UpdateOneAsync(
                        Builders<BsonDocument>.Filter.Eq("_id", doc["_id"]),
                        update);
                }

                _branchFieldsNormalized = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to normalize legacy menu item branch fields.");
            }
            finally
            {
                _branchFieldNormalizeLock.Release();
            }
        }

        private static List<MenuItem> PreferBranchItems(IEnumerable<MenuItem> items, string? branchId)
        {
            var trimmedBranchId = branchId?.Trim();
            return items
                .Where(i => !string.IsNullOrWhiteSpace(i.Item))
                .GroupBy(i => i.Item.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g
                    .OrderByDescending(i => !string.IsNullOrWhiteSpace(trimmedBranchId) &&
                        string.Equals(i.BranchId, trimmedBranchId, StringComparison.OrdinalIgnoreCase))
                    .ThenBy(i => string.IsNullOrWhiteSpace(i.BranchId) ? 1 : 0)
                    .First())
                .ToList();
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
            await NormalizeLegacyBranchFieldAsync();

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
                    BranchRecordFilter(branchId),
                    SharedBranchFilter()
                )
            );
            var branchItems = await _collection.Find(branchFilter).ToListAsync();
            branchItems = PreferBranchItems(branchItems, branchId);
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
            await NormalizeLegacyBranchFieldAsync();

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
                allItems = await FilterCurrentlyStockedAsync(allItems);
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
                    BranchRecordFilter(branchId),
                    SharedBranchFilter()
                )
            );
            var branchItems = await _collection.Find(branchFilter).ToListAsync();
            branchItems = await FilterCurrentlyStockedAsync(branchItems);
            branchItems = PreferBranchItems(branchItems, branchId);
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
            await NormalizeLegacyBranchFieldAsync();

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
                    BranchRecordFilter(branchId),
                    SharedBranchFilter()
                )
            );
            return await _collection.CountDocumentsAsync(filter);
        }
    }
}
