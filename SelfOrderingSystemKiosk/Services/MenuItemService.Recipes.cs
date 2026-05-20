using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using SelfOrderingSystemKiosk.Models;
using CustomerOrderItem = SelfOrderingSystemKiosk.Areas.Customer.Models.OrderItem;

namespace SelfOrderingSystemKiosk.Services
{
    public partial class MenuItemService
    {
        public async Task<bool> DecrementStockAsync(string itemName, int quantity, string? reason = null, string? referenceType = null, string? referenceId = null, string? branchId = null)
        {
            var orderItem = new CustomerOrderItem
            {
                ItemName = itemName,
                Quantity = quantity,
                Price = 0
            };
            var plan = await BuildIngredientUsagePlanAsync(new[] { orderItem }, branchId);
            if (plan.MissingMenuItems.Any())
            {
                _logger.LogWarning("DecrementStock (menu): item '{Item}' not found.", itemName);
                return false;
            }

            var affectedIngredientIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var usage in plan.Lines)
            {
                try
                {
                    await _ingredients.DecrementForSaleAsync(
                        usage.IngredientId,
                        usage.Quantity,
                        usage.MenuItemName,
                        referenceType ?? "Order",
                        referenceId);
                    affectedIngredientIds.Add(usage.IngredientId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "{Source} decrement failed for menu {Menu} ingredient {IngredientId}",
                        usage.Source,
                        usage.SubmittedItemName,
                        usage.IngredientId);
                }
            }

            foreach (var ingredientId in affectedIngredientIds)
                await SyncAvailabilityForIngredientAsync(ingredientId);

            foreach (var item in plan.MenuItems
                .Where(i => !string.IsNullOrWhiteSpace(i.Id))
                .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()))
            {
                await SyncAvailabilityForMenuItemAsync(item);
            }

            return true;
        }

        public async Task<decimal> CalculateOrderCostAsync(IEnumerable<CustomerOrderItem>? orderItems, string? branchId = null)
        {
            var plan = await BuildIngredientUsagePlanAsync(orderItems, branchId);
            var ingredientsById = (await _ingredients.GetByIdsAsync(plan.Lines.Select(line => line.IngredientId)))
                .Where(i => !string.IsNullOrWhiteSpace(i.Id))
                .ToDictionary(i => i.Id.Trim(), StringComparer.OrdinalIgnoreCase);

            var total = plan.Lines.Sum(line =>
                ingredientsById.TryGetValue(line.IngredientId, out var ingredient)
                    ? Math.Max(0, line.Quantity) * Math.Max(0m, ingredient.CostPerUnit)
                    : 0m);

            return Math.Round(total, 2, MidpointRounding.AwayFromZero);
        }

        private async Task<IngredientUsagePlan> BuildIngredientUsagePlanAsync(IEnumerable<CustomerOrderItem>? orderItems, string? branchId = null)
        {
            var plan = new IngredientUsagePlan();
            var menuCache = new Dictionary<string, MenuItem?>(StringComparer.OrdinalIgnoreCase);
            var ingredientNameCache = new Dictionary<string, IngredientItem?>(StringComparer.OrdinalIgnoreCase);
            var ingredientIdCache = new Dictionary<string, IngredientItem?>(StringComparer.OrdinalIgnoreCase);

            foreach (var orderItem in orderItems ?? Enumerable.Empty<CustomerOrderItem>())
            {
                if (string.IsNullOrWhiteSpace(orderItem.ItemName) || orderItem.Quantity <= 0)
                    continue;

                var submittedName = orderItem.ItemName;
                var lookupName = NormalizeSubmittedItemName(submittedName);
                var item = await ResolveMenuItemAsync(lookupName, branchId, menuCache);
                if (item == null)
                    plan.MissingMenuItems.Add(submittedName);
                else
                    plan.MenuItems.Add(item);

                var menuItemName = item?.Item ?? lookupName;
                var recipeIngredientIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (item?.Recipe is { Count: > 0 })
                {
                    foreach (var line in item.Recipe)
                    {
                        if (string.IsNullOrWhiteSpace(line.IngredientId) || line.QuantityPerUnit <= 0)
                            continue;

                        var total = (long)line.QuantityPerUnit * orderItem.Quantity;
                        var useQty = total > int.MaxValue ? int.MaxValue : (int)total;
                        if (useQty <= 0)
                            continue;

                        var ingredientId = line.IngredientId.Trim();
                        recipeIngredientIds.Add(ingredientId);
                        plan.Lines.Add(new IngredientUsageLine(
                            ingredientId,
                            useQty,
                            menuItemName,
                            submittedName,
                            IngredientUsageSource.Recipe));
                    }
                }

                var sauceUsage = await BuildSauceUsageAsync(
                    submittedName,
                    item,
                    menuItemName,
                    orderItem.Quantity,
                    branchId,
                    ingredientNameCache,
                    ingredientIdCache);
                foreach (var sauceUse in sauceUsage.Where(use => !recipeIngredientIds.Contains(use.IngredientId)))
                {
                    plan.Lines.Add(new IngredientUsageLine(
                        sauceUse.IngredientId,
                        sauceUse.Quantity,
                        menuItemName,
                        submittedName,
                        IngredientUsageSource.Sauce));
                }
            }

            return plan;
        }

        private async Task<MenuItem?> ResolveMenuItemAsync(
            string lookupName,
            string? branchId,
            Dictionary<string, MenuItem?> menuCache)
        {
            var cacheKey = $"{branchId?.Trim() ?? ""}|{lookupName}";
            if (menuCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var item = await GetByNameAsync(lookupName, branchId);
            if (item == null && lookupName.StartsWith("Coffee - ", StringComparison.OrdinalIgnoreCase))
                item = await GetByNameAsync("Coffee", branchId);

            menuCache[cacheKey] = item;
            return item;
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

        private async Task<List<(string IngredientId, int Quantity)>> BuildSauceUsageAsync(
            string submittedItemName,
            MenuItem? menuItem,
            string menuItemName,
            int orderQuantity,
            string? branchId,
            Dictionary<string, IngredientItem?> ingredientNameCache,
            Dictionary<string, IngredientItem?> ingredientIdCache)
        {
            if (orderQuantity <= 0)
                return new List<(string IngredientId, int Quantity)>();

            var chickenPieces = ExtractChickenPieceCount(submittedItemName);
            if (chickenPieces <= 0 && IsChickenWingMenu(menuItemName))
                chickenPieces = ExtractChickenPieceCount(menuItemName);
            if (chickenPieces <= 0 && IsChickenWingMenu(menuItemName))
                chickenPieces = await GetChickenPiecesFromRecipeAsync(menuItem, ingredientIdCache);
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
                var ingredient = await GetIngredientByNameAsync(sauceName, branchId, ingredientNameCache);
                if (ingredient == null)
                    continue;

                usage.Add((ingredient.Id, perSauce));
            }

            return usage;
        }

        private async Task<IngredientItem?> GetIngredientByNameAsync(
            string itemName,
            string? branchId,
            Dictionary<string, IngredientItem?> ingredientNameCache)
        {
            var cacheKey = $"{branchId?.Trim() ?? ""}|{itemName}";
            if (ingredientNameCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var ingredient = await _ingredients.GetByNameAsync(itemName, branchId);
            ingredientNameCache[cacheKey] = ingredient;
            return ingredient;
        }

        private async Task<IngredientItem?> GetIngredientByIdAsync(
            string ingredientId,
            Dictionary<string, IngredientItem?> ingredientIdCache)
        {
            var trimmedId = ingredientId.Trim();
            if (ingredientIdCache.TryGetValue(trimmedId, out var cached))
                return cached;

            var ingredient = await _ingredients.GetByIdAsync(trimmedId);
            ingredientIdCache[trimmedId] = ingredient;
            return ingredient;
        }

        private async Task<int> GetChickenPiecesFromRecipeAsync(
            MenuItem? menuItem,
            Dictionary<string, IngredientItem?> ingredientIdCache)
        {
            if (menuItem?.Recipe is not { Count: > 0 })
                return 0;

            foreach (var line in menuItem.Recipe)
            {
                if (string.IsNullOrWhiteSpace(line.IngredientId) || line.QuantityPerUnit <= 0)
                    continue;

                var ingredient = await GetIngredientByIdAsync(line.IngredientId, ingredientIdCache);
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

    }
}
