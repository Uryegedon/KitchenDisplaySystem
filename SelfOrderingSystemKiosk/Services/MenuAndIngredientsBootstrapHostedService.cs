using Microsoft.Extensions.Options;
using MongoDB.Driver;
using SelfOrderingSystemKiosk.Areas.Admin.Models;
using SelfOrderingSystemKiosk.Models;

namespace SelfOrderingSystemKiosk.Services
{
    /// <summary>Migrates legacy Stock → MenuItems when empty; seeds ingredient catalog when Ingredients is empty.</summary>
    public class MenuAndIngredientsBootstrapHostedService : IHostedService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<MenuAndIngredientsBootstrapHostedService> _logger;
        private const int UncookedRiceGramsPerCookedCup = 67;

        public MenuAndIngredientsBootstrapHostedService(IServiceProvider services, ILogger<MenuAndIngredientsBootstrapHostedService> logger)
        {
            _services = services;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = Task.Run(() => RunAsync(cancellationToken), cancellationToken);
            return Task.CompletedTask;
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(1500, cancellationToken);
                using var scope = _services.CreateScope();
                var client = scope.ServiceProvider.GetRequiredService<IMongoClient>();
                var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

                var dbName = config["KitchenDatabase:DatabaseName"] ?? "Kitchen";
                var db = client.GetDatabase(dbName);

                var legacyName = config["KitchenDatabase:LegacyStockCollectionName"]
                    ?? config["KitchenDatabase:InventoryItem"]
                    ?? "Stock";
                var menuName = config["KitchenDatabase:MenuItemsCollectionName"] ?? "MenuItems";
                var ingName = config["KitchenDatabase:IngredientsCollectionName"] ?? "Ingredients";

                await MigrateLegacyStockIfNeededAsync(db, legacyName, menuName, cancellationToken);
                await SeedIngredientsIfNeededAsync(db, ingName, cancellationToken);
                await SeedMenuBoardItemsAsync(db, menuName, ingName, cancellationToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Menu/ingredients bootstrap failed.");
            }
        }

        private async Task MigrateLegacyStockIfNeededAsync(IMongoDatabase db, string legacyCollection, string menuCollection, CancellationToken ct)
        {
            var menuColl = db.GetCollection<MenuItem>(menuCollection);
            if (await menuColl.CountDocumentsAsync(FilterDefinition<MenuItem>.Empty, cancellationToken: ct) > 0)
                return;

            var legacyColl = db.GetCollection<InventoryItem>(legacyCollection);
            if (await legacyColl.CountDocumentsAsync(FilterDefinition<InventoryItem>.Empty, cancellationToken: ct) == 0)
                return;

            var docs = await legacyColl.Find(_ => true).ToListAsync(ct);
            var mapped = docs.Select(MapLegacyToMenuItem).ToList();
            if (mapped.Count > 0)
                await menuColl.InsertManyAsync(mapped, cancellationToken: ct);

            _logger.LogInformation("Migrated {Count} documents from {Legacy} to {Menu}.", mapped.Count, legacyCollection, menuCollection);
        }

        private async Task SeedMenuBoardItemsAsync(IMongoDatabase db, string menuCollection, string ingCollection, CancellationToken ct)
        {
            var coll = db.GetCollection<MenuItem>(menuCollection);
            var ingredientByName = await LoadIngredientMapAsync(db, ingCollection, ct);
            var boardItems = BuildMenuBoardSeed();
            foreach (var item in boardItems)
                item.Recipe = BuildRecipeForMenuItem(item.Item, ingredientByName);

            if (boardItems.Count == 0)
                return;

            var seedByName = boardItems
                .GroupBy(x => x.Item, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var seedNames = boardItems.Select(x => x.Item).ToList();
            var existingSeedItems = await coll.Find(Builders<MenuItem>.Filter.In(x => x.Item, seedNames))
                .ToListAsync(ct);
            var existing = existingSeedItems.Select(x => x.Item).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = boardItems.Where(x => !existing.Contains(x.Item)).ToList();

            if (missing.Count == 0)
            {
                var existingItems = await coll.Find(_ => true).ToListAsync(ct);
                var recipeUpdates = await SeedMenuRecipesAsync(coll, existingItems, seedByName, ingredientByName, ct);
                await RetireMenuSeedItemsAsync(coll, ct);
                _logger.LogDebug("Menu board seed skipped; all {Count} default items already exist in {Coll}; filled {Recipes} blank recipes.", boardItems.Count, menuCollection, recipeUpdates);
                return;
            }

            foreach (var item in missing)
                item.Id = MongoDB.Bson.ObjectId.GenerateNewId().ToString();

            await coll.InsertManyAsync(missing, cancellationToken: ct);
            var allItems = await coll.Find(_ => true).ToListAsync(ct);
            var insertedRecipeUpdates = await SeedMenuRecipesAsync(coll, allItems, seedByName, ingredientByName, ct);
            await RetireMenuSeedItemsAsync(coll, ct);
            _logger.LogInformation("Seeded {Count} missing menu board items into {Coll}; filled {Recipes} blank recipes.", missing.Count, menuCollection, insertedRecipeUpdates);
        }

        private static async Task<Dictionary<string, IngredientItem>> LoadIngredientMapAsync(IMongoDatabase db, string ingCollection, CancellationToken ct)
        {
            var ingColl = db.GetCollection<IngredientItem>(ingCollection);
            var ingredients = await ingColl.Find(_ => true).ToListAsync(ct);
            return ingredients
                .Where(i => !string.IsNullOrWhiteSpace(i.Item) && !string.IsNullOrWhiteSpace(i.Id))
                .GroupBy(i => i.Item.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }

        private static async Task<int> SeedMenuRecipesAsync(
            IMongoCollection<MenuItem> coll,
            IEnumerable<MenuItem> existingItems,
            IReadOnlyDictionary<string, MenuItem> seedByName,
            IReadOnlyDictionary<string, IngredientItem> ingredientByName,
            CancellationToken ct)
        {
            var updated = 0;

            foreach (var existing in existingItems)
            {
                if (string.IsNullOrWhiteSpace(existing.Id))
                    continue;

                var inferredRecipe = seedByName.TryGetValue(existing.Item ?? string.Empty, out var seed)
                    ? seed.Recipe
                    : BuildRecipeForMenuItem(existing.Item, ingredientByName);
                if (inferredRecipe is not { Count: > 0 })
                    continue;

                var nextRecipe = BuildSeededRecipeUpdate(existing.Recipe, inferredRecipe);
                if (nextRecipe == null)
                    continue;

                var result = await coll.UpdateOneAsync(
                    x => x.Id == existing.Id,
                    Builders<MenuItem>.Update.Set(x => x.Recipe, nextRecipe),
                    cancellationToken: ct);

                updated += (int)result.ModifiedCount;
            }

            return updated;
        }

        private static List<MenuRecipeLine>? BuildSeededRecipeUpdate(List<MenuRecipeLine>? existingRecipe, List<MenuRecipeLine> seedRecipe)
        {
            if (existingRecipe is not { Count: > 0 })
                return seedRecipe;

            var next = existingRecipe
                .Select(x => new MenuRecipeLine { IngredientId = x.IngredientId, QuantityPerUnit = x.QuantityPerUnit })
                .ToList();
            var changed = false;

            foreach (var seedLine in seedRecipe)
            {
                var currentLine = next.FirstOrDefault(x => x.IngredientId == seedLine.IngredientId);
                if (currentLine == null)
                    continue;

                if (seedLine.QuantityPerUnit == UncookedRiceGramsPerCookedCup
                    && (currentLine.QuantityPerUnit == 50
                        || currentLine.QuantityPerUnit == 60
                        || currentLine.QuantityPerUnit == 150
                        || currentLine.QuantityPerUnit == 180))
                {
                    currentLine.QuantityPerUnit = seedLine.QuantityPerUnit;
                    changed = true;
                }
            }

            return changed ? next : null;
        }

        private static Task RetireMenuSeedItemsAsync(IMongoCollection<MenuItem> coll, CancellationToken ct)
        {
            var filter = Builders<MenuItem>.Filter.And(
                Builders<MenuItem>.Filter.Eq(x => x.Item, "Softdrinks"),
                Builders<MenuItem>.Filter.Eq(x => x.Category, "Drinks"));

            return coll.DeleteManyAsync(filter, ct);
        }

        private static List<MenuItem> BuildMenuBoardSeed() =>
            new()
            {
                Menu("Unli Bisita Ni Kap", "Group Add-ons", 50m, 1000, "/images/logopakpak.png", "service"),

                Menu("6 Piece Chicken Wings (2 Flavors Only)", "Wings Ala Carte", 197m, 990, "/images/menu-items/Kp%20box%20of%202%2C%204%2C%20and%208%20pcs.jpg", "set"),
                Menu("12 Piece Chicken Wings (3 Flavors Only)", "Wings Ala Carte", 337m, 989, "/images/menu-items/Kp%20box%20of%202%2C%204%2C%20and%208%20pcs.jpg", "set"),
                Menu("24 Piece Chicken Wings (6 Flavors Only)", "Wings Ala Carte", 607m, 988, "/images/menu-items/Kp%20box%20of%202%2C%204%2C%20and%208%20pcs.jpg", "set"),
                Menu("50 Piece Chicken Wings (6 Flavors Only)", "Wings Ala Carte", 1157m, 987, "/images/menu-items/Kp%20box%20of%202%2C%204%2C%20and%208%20pcs.jpg", "set"),
                Menu("100 Piece Chicken Wings (10 Flavors Only)", "Wings Ala Carte", 2107m, 986, "/images/menu-items/Kp%20box%20of%202%2C%204%2C%20and%208%20pcs.jpg", "set"),

                Menu("2 Pieces Chicken Wings With Rice - Ala Carte", "Sulit Kap Meals", 77m, 980, "/images/Kp%20items/Kp%20quarter%20chicken.jpg", "meal"),
                Menu("2 Pieces Chicken Wings With Rice - Unli Rice", "Sulit Kap Meals", 117m, 979, "/images/Kp%20items/Kp%20quarter%20chicken.jpg", "meal"),
                Menu("4 Pieces Chicken Wings With Rice - Ala Carte", "Sulit Kap Meals", 127m, 978, "/images/Kp%20items/Kp%20quarter%20chicken.jpg", "meal"),
                Menu("4 Pieces Chicken Wings With Rice - Unli Rice", "Sulit Kap Meals", 147m, 977, "/images/Kp%20items/Kp%20quarter%20chicken.jpg", "meal"),
                Menu("Mix and Match Ala Carte", "Sulit Kap Meals", 77m, 976, "/images/Kp%20items/Kp%20quarter%20chicken.jpg", "meal"),

                Menu("Regular Pasta Sardine - Solo", "Pasta", 127m, 970, "/images/Kp%20items/Spanish%20Spardines.jpeg", "pasta"),
                Menu("Regular Pasta Sardine - Group", "Pasta", 227m, 969, "/images/Kp%20items/Spanish%20Spardines.jpeg", "pasta"),
                Menu("Regular Pasta Sardine - Party", "Pasta", 957m, 968, "/images/Kp%20items/Spanish%20Spardines.jpeg", "pasta"),
                Menu("Regular Pasta Manzo - Solo", "Pasta", 127m, 967, "/images/Kp%20items/Manzo%28Beefy%20Spaghetti%29.jpg", "pasta"),
                Menu("Regular Pasta Manzo - Group", "Pasta", 227m, 966, "/images/Kp%20items/Manzo%28Beefy%20Spaghetti%29.jpg", "pasta"),
                Menu("Regular Pasta Manzo - Party", "Pasta", 957m, 965, "/images/Kp%20items/Manzo%28Beefy%20Spaghetti%29.jpg", "pasta"),
                Menu("Regular Pasta Pomodoro - Solo", "Pasta", 127m, 964, "/images/Kp%20items/Pomodoro%28Tomato%20Basil%29.jpg", "pasta"),
                Menu("Regular Pasta Pomodoro - Group", "Pasta", 227m, 963, "/images/Kp%20items/Pomodoro%28Tomato%20Basil%29.jpg", "pasta"),
                Menu("Regular Pasta Pomodoro - Party", "Pasta", 957m, 962, "/images/Kp%20items/Pomodoro%28Tomato%20Basil%29.jpg", "pasta"),
                Menu("Regular Pasta Salsiccia - Solo", "Pasta", 127m, 961, "/images/Kp%20items/Kp%20pasta%20salsiccia.jpg", "pasta"),
                Menu("Regular Pasta Salsiccia - Group", "Pasta", 227m, 960, "/images/Kp%20items/Kp%20pasta%20salsiccia.jpg", "pasta"),
                Menu("Regular Pasta Salsiccia - Party", "Pasta", 957m, 959, "/images/Kp%20items/Kp%20pasta%20salsiccia.jpg", "pasta"),
                Menu("Premium Pasta Gamberetto - Solo", "Pasta", 157m, 958, "/images/menu-items/Gambretto.jpg", "pasta"),
                Menu("Premium Pasta Gamberetto - Group", "Pasta", 257m, 957, "/images/menu-items/Gambretto.jpg", "pasta"),
                Menu("Premium Pasta Gamberetto - Party", "Pasta", 957m, 956, "/images/menu-items/Gambretto.jpg", "pasta"),
                Menu("Premium Pasta Kapow - Solo", "Pasta", 157m, 955, "/images/menu-items/Kapow.jpg", "pasta"),
                Menu("Premium Pasta Kapow - Group", "Pasta", 257m, 954, "/images/menu-items/Kapow.jpg", "pasta"),
                Menu("Premium Pasta Kapow - Party", "Pasta", 957m, 953, "/images/menu-items/Kapow.jpg", "pasta"),

                Menu("Potato Thins Original", "Appetizer", 77m, 950, "/images/Kp%20items/Kp%20potato%20thins.jpg", "snack"),
                Menu("Potato Thins Cheese", "Appetizer", 77m, 949, "/images/Kp%20items/Kp%20potato%20thins%20cheese.jpg", "snack"),
                Menu("Potato Thins Sour Cream", "Appetizer", 77m, 948, "/images/Kp%20items/Kp%20potato%20thins%20sour%20cream.jpg", "snack"),
                Menu("Potato Thins BBQ", "Appetizer", 77m, 947, "/images/Kp%20items/Kp%20potato%20thins%20bbq.jpg", "snack"),
                Menu("Chickings (Chicken Tenders)", "Appetizer", 77m, 946, "/images/Kp%20items/chicken%20tenders.jpg", "chicken"),
                Menu("Nachos", "Appetizer", 177m, 945, "/images/Kp%20items/Kp%20nachos.jpg", "snack"),

                Menu("Extra Gravy", "Add Ons", 20m, 940, "/images/Kp%20items/Kp%20gravy.jpg", "add-on"),
                Menu("Extra Mayo Garlic", "Add Ons", 20m, 939, "/images/menu-items/Garlic%20Mayo.png", "add-on"),
                Menu("Garlic Rice", "Add Ons", 30m, 938, "/images/menu-items/Garlic-Rice.png", "rice"),
                Menu("Plain Rice", "Add Ons", 30m, 937, "/images/Kp%20items/Kp%20rice.jpg", "rice"),

                Menu("Kap's Burger Ala Carte", "Kap's Burger", 57m, 930, "/images/wings.png", "burger"),
                Menu("Kap's Burger Meal", "Kap's Burger", 87m, 929, "/images/wings.png", "burger"),

                Menu("Water", "Drinks", 0m, 925, "/images/menu-items/Water.jpg", "drink"),
                Menu("Iced Tea", "Drinks", 0m, 924, "/images/menu-items/Red%20Iced%20Tea.png", "drink"),
                Menu("Coffee", "Drinks", 0m, 923, "/images/menu-items/Coffee%20and%20Tea.jpg", "drink"),
                Menu("Hot Tea", "Drinks", 0m, 922, "/images/menu-items/Coffee%20and%20Tea.jpg", "drink"),
                Menu("Softdrinks - Coke", "Drinks", 0m, 921, "/images/menu-items/Coke%20Regular.jpg", "drink"),
                Menu("Softdrinks - Sprite", "Drinks", 0m, 920, "/images/wings.png", "drink"),
                Menu("Softdrinks - Royal", "Drinks", 0m, 919, "/images/wings.png", "drink"),

                Menu("Red Iced Tea", "Unlimited Inclusions", 0m, 918, "/images/menu-items/Red%20Iced%20Tea.png", "drink"),
                Menu("Coffee", "Unlimited Inclusions", 0m, 917, "/images/menu-items/Coffee%20and%20Tea.jpg", "drink"),
                Menu("Tea", "Unlimited Inclusions", 0m, 916, "/images/menu-items/Coffee%20and%20Tea.jpg", "drink"),
                Menu("Unli Pasta Sardine", "Unlimited Inclusions", 0m, 915, "/images/Kp%20items/Spanish%20Spardines.jpeg", "pasta"),
                Menu("Unli Pasta Manzo", "Unlimited Inclusions", 0m, 914, "/images/Kp%20items/Manzo%28Beefy%20Spaghetti%29.jpg", "pasta"),
                Menu("Unli Pasta Pomodoro", "Unlimited Inclusions", 0m, 913, "/images/Kp%20items/Pomodoro%28Tomato%20Basil%29.jpg", "pasta"),
                Menu("Unli Pasta Salsiccia", "Unlimited Inclusions", 0m, 912, "/images/Kp%20items/Kp%20pasta%20salsiccia.jpg", "pasta"),
            };

        private static MenuItem Menu(string name, string category, decimal price, int order, string image, string? foodCategory = null) =>
            new()
            {
                Item = name,
                Category = category,
                FoodCategory = foodCategory,
                MenuOrder = order,
                CurrentStock = 999,
                Unit = "pcs",
                ReorderLevel = 10,
                Price = price,
                Status = "In Stock",
                Availability = "Available",
                Image = image,
                Recipe = null
            };

        private static MenuItem MapLegacyToMenuItem(InventoryItem d) => new()
        {
            Id = d.Id,
            Item = d.Item ?? "",
            Category = d.Category ?? "Wings",
            FoodCategory = null,
            MenuOrder = d.MenuOrder,
            CurrentStock = d.CurrentStock,
            Unit = d.Unit ?? "pcs",
            ReorderLevel = d.ReorderLevel,
            Price = d.Price,
            Status = d.Status ?? "In Stock",
            Availability = string.IsNullOrEmpty(d.Availability) ? "Available" : d.Availability,
            Image = string.IsNullOrEmpty(d.Image) ? "/images/wings.png" : d.Image,
            Recipe = null
        };

        private async Task SeedIngredientsIfNeededAsync(IMongoDatabase db, string ingCollection, CancellationToken ct)
        {
            var coll = db.GetCollection<IngredientItem>(ingCollection);
            var seed = BuildIngredientSeed();
            if (seed.Count == 0)
                return;

            var seedNames = seed.Select(x => x.Item).ToList();
            var existingItems = await coll.Find(Builders<IngredientItem>.Filter.In(x => x.Item, seedNames))
                .ToListAsync(ct);
            var existingByName = existingItems.ToDictionary(x => x.Item, StringComparer.OrdinalIgnoreCase);
            var missing = new List<IngredientItem>();
            var legacyDefaults = LegacyIngredientSeedNames.Except(seedNames, StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var item in seed)
            {
                if (!existingByName.TryGetValue(item.Item, out var existing))
                {
                    missing.Add(item);
                    continue;
                }

                if (existing.IngredientCategory == item.IngredientCategory && existing.Unit == item.Unit)
                    continue;

                await coll.UpdateOneAsync(
                    x => x.Id == existing.Id,
                    Builders<IngredientItem>.Update
                        .Set(x => x.IngredientCategory, item.IngredientCategory)
                        .Set(x => x.Unit, item.Unit),
                    cancellationToken: ct);
            }

            if (missing.Count > 0)
                await coll.InsertManyAsync(missing, cancellationToken: ct);

            if (legacyDefaults.Count > 0)
            {
                await coll.DeleteManyAsync(
                    Builders<IngredientItem>.Filter.In(x => x.Item, legacyDefaults),
                    ct);
            }

            _logger.LogInformation("Ingredient catalog synced with {Count} spreadsheet items in {Coll}; inserted {Missing}.", seed.Count, ingCollection, missing.Count);
        }

        private static List<MenuRecipeLine> BuildRecipeForMenuItem(string? itemName, IReadOnlyDictionary<string, IngredientItem> ingredients)
        {
            var name = itemName ?? "";
            var norm = Normalize(name);
            var lines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            void AddExisting(IngredientItem ingredient, int quantity)
            {
                if (quantity <= 0)
                    return;
                if (string.IsNullOrWhiteSpace(ingredient.Id))
                    return;

                if (lines.ContainsKey(ingredient.Id))
                    lines[ingredient.Id] += quantity;
                else
                    lines[ingredient.Id] = quantity;
            }

            void Add(string ingredientName, int quantity)
            {
                if (!ingredients.TryGetValue(ingredientName, out var ingredient))
                    return;

                AddExisting(ingredient, quantity);
            }

            var scale = RecipeScale(norm);
            var wingPieces = ExtractLeadingNumber(norm);

            if (norm.Contains("mix and match", StringComparison.OrdinalIgnoreCase))
            {
                Add("Chicken Wings", 2);
                Add("Rice", UncookedRiceGramsPerCookedCup);
            }

            if (norm.Contains("bisita ni kap", StringComparison.OrdinalIgnoreCase))
                Add("Bisita ni Kap", 30);

            if (norm.Contains("chicken wings", StringComparison.OrdinalIgnoreCase)
                || norm.Contains("wings", StringComparison.OrdinalIgnoreCase)
                || KnownWingFlavorIngredient(norm) != null)
            {
                Add("Chicken Wings", wingPieces > 0 ? wingPieces : 1);
                var sauce = KnownWingFlavorIngredient(norm);
                if (sauce != null)
                    Add(sauce, 15);
            }

            if (norm.Contains("with rice", StringComparison.OrdinalIgnoreCase)
                || norm.Contains("plain rice", StringComparison.OrdinalIgnoreCase)
                || norm.Contains("garlic rice", StringComparison.OrdinalIgnoreCase))
            {
                Add("Rice", UncookedRiceGramsPerCookedCup);
            }

            if (norm.Contains("garlic rice", StringComparison.OrdinalIgnoreCase))
            {
                Add("Garlic Rice Sauce", 20);
                Add("Garlic Bits", 5);
            }

            if (norm.Contains("pasta", StringComparison.OrdinalIgnoreCase))
            {
                Add("Pasta", 100 * scale);
                if (norm.Contains("sardine", StringComparison.OrdinalIgnoreCase) || norm.Contains("spardine", StringComparison.OrdinalIgnoreCase))
                    Add("Sardine", 60 * scale);
                if (norm.Contains("manzo", StringComparison.OrdinalIgnoreCase))
                {
                    Add("Manzo", 60 * scale);
                    Add("Beef", 50 * scale);
                }
                if (norm.Contains("pomodoro", StringComparison.OrdinalIgnoreCase))
                {
                    Add("Pomodoro", 60 * scale);
                    Add("Tomato", 40 * scale);
                }
                if (norm.Contains("salsiccia", StringComparison.OrdinalIgnoreCase) || norm.Contains("salsicca", StringComparison.OrdinalIgnoreCase))
                    Add("Salsicca", 60 * scale);
                if (norm.Contains("gamberetto", StringComparison.OrdinalIgnoreCase) || norm.Contains("gambretto", StringComparison.OrdinalIgnoreCase))
                {
                    Add("Shrimp", 60 * scale);
                    Add("White Wine", 10 * scale);
                    Add("Olive Oil", 10 * scale);
                }
                if (norm.Contains("kapow", StringComparison.OrdinalIgnoreCase))
                    Add("Kung Pao", 60 * scale);
            }

            if (norm.Contains("potato thins", StringComparison.OrdinalIgnoreCase))
            {
                Add("Potato", 80);
                if (norm.Contains("cheese", StringComparison.OrdinalIgnoreCase))
                    Add("Cheese Powder", 10);
                if (norm.Contains("sour cream", StringComparison.OrdinalIgnoreCase))
                    Add("Sour Cream", 10);
                if (norm.Contains("bbq", StringComparison.OrdinalIgnoreCase) || norm.Contains("barbeque", StringComparison.OrdinalIgnoreCase))
                    Add("Barbeque Powder", 10);
                if (norm.Contains("salt and pepper", StringComparison.OrdinalIgnoreCase))
                {
                    Add("Salt", 3);
                    Add("Pepper", 2);
                }
            }

            if (norm.Contains("nachos", StringComparison.OrdinalIgnoreCase))
            {
                Add("Nachos", 100);
                Add("Cheese Sauce", 30);
            }

            if (norm.Contains("chickings", StringComparison.OrdinalIgnoreCase)
                || norm.Contains("chicken tenders", StringComparison.OrdinalIgnoreCase))
                Add("Chicken Tenders", 1);

            if (norm.Contains("gravy", StringComparison.OrdinalIgnoreCase))
                Add("Gravy", 30);

            if (norm.Contains("mayo garlic", StringComparison.OrdinalIgnoreCase)
                || norm.Contains("garlic mayo", StringComparison.OrdinalIgnoreCase))
                Add("Mayo Garlic", 30);

            if (norm.Contains("water", StringComparison.OrdinalIgnoreCase))
                Add("Water", 1);

            if (norm.Contains("iced tea", StringComparison.OrdinalIgnoreCase)
                || norm.Contains("ice tea", StringComparison.OrdinalIgnoreCase))
            {
                Add("Ice Tea", 250);
                Add("Ice Tea Sugar", 15);
            }

            if (norm.Contains("coffee", StringComparison.OrdinalIgnoreCase))
                Add("Coffee", 10);

            if (norm.Equals("tea", StringComparison.OrdinalIgnoreCase) || norm.Contains("hot tea", StringComparison.OrdinalIgnoreCase))
                Add("Tea", 1);

            if (norm.Contains("coke zero", StringComparison.OrdinalIgnoreCase))
                Add("Coke Zero", 1);
            else if (norm.Contains("coke", StringComparison.OrdinalIgnoreCase))
                Add("Coke in a Can", 1);

            if (norm.Contains("sprite", StringComparison.OrdinalIgnoreCase))
                Add("Sprite in a Can", 1);

            if (norm.Contains("royal", StringComparison.OrdinalIgnoreCase))
                Add("Royal in a Can", 1);

            if (norm.Contains("kap s burger", StringComparison.OrdinalIgnoreCase)
                || norm.Contains("kaps burger", StringComparison.OrdinalIgnoreCase)
                || norm.Contains("kap's burger", StringComparison.OrdinalIgnoreCase))
            {
                Add("Burger Bun", 1);
                Add("Burger Patty", 1);
                Add("Mayo", 15);
                Add("Catsup", 15);
                if (norm.Contains("meal", StringComparison.OrdinalIgnoreCase))
                    Add("Potato", 80);
            }

            if (lines.Count == 0)
                AddFallbackIngredientFromName(norm, ingredients, AddExisting);

            return lines
                .Select(line => new MenuRecipeLine { IngredientId = line.Key, QuantityPerUnit = line.Value })
                .ToList();
        }

        private static void AddFallbackIngredientFromName(
            string norm,
            IReadOnlyDictionary<string, IngredientItem> ingredients,
            Action<IngredientItem, int> add)
        {
            var matched = false;
            foreach (var ingredient in ingredients.Values
                .Where(i => !string.IsNullOrWhiteSpace(i.Item))
                .OrderByDescending(i => i.Item.Length))
            {
                var ingredientNorm = Normalize(ingredient.Item);
                if (ingredientNorm.Length < 4)
                    continue;
                if (!norm.Contains(ingredientNorm, StringComparison.OrdinalIgnoreCase))
                    continue;

                add(ingredient, DefaultRecipeQuantity(ingredient));
                matched = true;
            }

            if (!matched && norm.Contains("rice", StringComparison.OrdinalIgnoreCase) && ingredients.TryGetValue("Rice", out var rice))
                add(rice, UncookedRiceGramsPerCookedCup);
        }

        private static int DefaultRecipeQuantity(IngredientItem ingredient)
        {
            if (string.Equals(ingredient.Unit, "pcs", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (string.Equals(ingredient.IngredientCategory, "Sauces", StringComparison.OrdinalIgnoreCase))
                return 30;
            if (string.Equals(ingredient.Unit, "ml", StringComparison.OrdinalIgnoreCase))
                return 30;
            return 30;
        }

        private static int RecipeScale(string norm)
        {
            if (norm.Contains("party", StringComparison.OrdinalIgnoreCase))
                return 8;
            if (norm.Contains("group", StringComparison.OrdinalIgnoreCase))
                return 2;
            return 1;
        }

        private static int ExtractLeadingNumber(string norm)
        {
            var first = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return int.TryParse(first, out var value) ? value : 0;
        }

        private static string? KnownWingFlavorIngredient(string norm)
        {
            if (norm.Contains("mayora sriracha original", StringComparison.OrdinalIgnoreCase))
                return "Mayora Sriracha Original";
            if (norm.Contains("mayora sriracha mayo", StringComparison.OrdinalIgnoreCase)
                || norm.Contains("moyora sriracha mayo", StringComparison.OrdinalIgnoreCase))
                return "Mayo Garlic";
            if (norm.Contains("sriracha honey", StringComparison.OrdinalIgnoreCase))
                return "Honey";
            if (norm.Contains("chief parm", StringComparison.OrdinalIgnoreCase))
                return "Chief Parm Base";
            if (norm.Contains("colonel mustard", StringComparison.OrdinalIgnoreCase))
                return "Colonel Mustard";
            if (norm.Contains("konsi honey soy", StringComparison.OrdinalIgnoreCase)
                || norm.Contains("konsi honeysoy", StringComparison.OrdinalIgnoreCase))
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
            if (norm.Contains("salt and pepper", StringComparison.OrdinalIgnoreCase))
                return "Salt";
            if (norm.Contains("buffalo", StringComparison.OrdinalIgnoreCase))
                return "Hot Sauce";
            return null;
        }

        private static readonly string[] LegacyIngredientSeedNames =
        {
            "Garlic",
            "Onion",
            "Tomatoes",
            "Fresh tomatoes",
            "Mushrooms",
            "Basil",
            "Chili / chili peppers",
            "Parmesan cheese",
            "Peanuts",
            "Sugar",
            "Salted egg yolk",
            "Cooking oil",
            "Olive oil",
            "Butter",
            "Lemon juice",
            "Lime juice",
            "Milk / cream",
            "Mustard",
            "Soy sauce",
            "Oyster sauce",
            "Fish sauce",
            "Tomato sauce",
        };

        private static List<IngredientItem> BuildIngredientSeed() =>
            new()
            {
                Ing("Mayora Sriracha Original", "Sauces", "ml"),
                Ing("Chief Parm Base", "Sauces", "ml"),
                Ing("Mayo Garlic", "Sauces", "ml"),
                Ing("Teriyaki Sen", "Sauces", "ml"),
                Ing("Konsi Honeysoy", "Sauces", "ml"),
                Ing("Colonel Mustard", "Sauces", "ml"),
                Ing("Vice Thai", "Sauces", "ml"),
                Ing("Bisita ni Kap", "Sauces", "ml"),
                Ing("Honey", "Sauces", "ml"),
                Ing("Cheese Sauce", "Sauces", "ml"),
                Ing("Kung Pao", "Sauces", "ml"),
                Ing("Salsicca", "Sauces", "ml"),
                Ing("Sardine", "Sauces", "ml"),
                Ing("Manzo", "Sauces", "ml"),
                Ing("Pomodoro", "Sauces", "ml"),
                Ing("Gravy", "Sauces", "ml"),
                Ing("Hot Sauce", "Sauces", "ml"),
                Ing("Garlic Rice Sauce", "Sauces", "ml"),

                Ing("Chicken Wings", "Raw Materials", "pcs"),
                Ing("Chicken Tenders", "Raw Materials", "pcs"),
                Ing("Whole Chicken", "Raw Materials", "pcs"),
                Ing("Beef", "Raw Materials", "g"),
                Ing("Shrimp", "Raw Materials", "g"),
                Ing("Potato", "Raw Materials", "g"),
                Ing("Tomato", "Raw Materials", "g"),
                Ing("Onion", "Raw Materials", "g"),
                Ing("Fresh Garlic", "Raw Materials", "g"),
                Ing("Cabbage", "Raw Materials", "g"),
                Ing("Cucumber", "Raw Materials", "g"),
                Ing("Salted Egg", "Raw Materials", "pcs"),
                Ing("Parmesan", "Raw Materials", "g"),
                Ing("Cheddar Cheese", "Raw Materials", "g"),
                Ing("Garlic Bits", "Raw Materials", "g"),
                Ing("Sesame Seed", "Raw Materials", "g"),
                Ing("Chili Flakes", "Raw Materials", "g"),
                Ing("Cheese Powder", "Raw Materials", "g"),
                Ing("Sour Cream", "Raw Materials", "g"),
                Ing("Barbeque Powder", "Raw Materials", "g"),
                Ing("Rice", "Raw Materials", "g"),
                Ing("Pasta", "Raw Materials", "g"),
                Ing("Nachos", "Raw Materials", "g"),
                Ing("Mayo", "Raw Materials", "ml"),
                Ing("Catsup", "Raw Materials", "ml"),
                Ing("White Wine", "Raw Materials", "ml"),
                Ing("Olive Oil", "Raw Materials", "ml"),
                Ing("Palm Oil", "Raw Materials", "ml"),
                Ing("Iodized Salt", "Raw Materials", "g"),
                Ing("Salt", "Raw Materials", "g"),
                Ing("Pepper", "Raw Materials", "g"),
                Ing("Condensed Milk", "Raw Materials", "ml"),
                Ing("Sugar", "Raw Materials", "g"),

                Ing("Bottled Water", "Drinks", "pcs"),
                Ing("Water", "Drinks", "pcs"),
                Ing("Mineral Water", "Drinks", "pcs"),
                Ing("Ice Tea", "Drinks", "ml"),
                Ing("Ice Tea Sugar", "Drinks", "g"),
                Ing("Coke in a Can", "Drinks", "pcs"),
                Ing("Coke Zero", "Drinks", "pcs"),
                Ing("Sprite in a Can", "Drinks", "pcs"),
                Ing("Royal in a Can", "Drinks", "pcs"),
                Ing("Tea", "Drinks", "pcs"),
                Ing("Coffee", "Drinks", "g"),

                Ing("Milky Melon", "Ice Cream", "pcs"),
                Ing("Choco Stick", "Ice Cream", "pcs"),
                Ing("Sundae Choco", "Ice Cream", "pcs"),
                Ing("Sundae Strawberry", "Ice Cream", "pcs"),
                Ing("Mochi Choco", "Ice Cream", "pcs"),
                Ing("Mochi Vanilla", "Ice Cream", "pcs"),
                Ing("Chocolate Crispy", "Ice Cream", "pcs"),
                Ing("Coffee Crispy", "Ice Cream", "pcs"),
                Ing("Taro Crispy", "Ice Cream", "pcs"),
                Ing("Strawberry Crispy", "Ice Cream", "pcs"),
                Ing("Semangka", "Ice Cream", "pcs"),
                Ing("Chocomelt", "Ice Cream", "pcs"),
                Ing("Strawberry Cone", "Ice Cream", "pcs"),

                Ing("Box Small", "Miscellaneous", "pcs"),
                Ing("Box Big", "Miscellaneous", "pcs"),
                Ing("Paper Bag #8", "Miscellaneous", "pcs"),
                Ing("Paper Bag #20", "Miscellaneous", "pcs"),
                Ing("Plastic Bag Small", "Miscellaneous", "pcs"),
                Ing("Plastic Bag Large", "Miscellaneous", "pcs"),
                Ing("Cling Wrap", "Miscellaneous", "pcs"),
                Ing("Hinge Cup Small", "Miscellaneous", "pcs"),
                Ing("Hinge Cup Big", "Miscellaneous", "pcs"),
                Ing("Bilao Small", "Miscellaneous", "pcs"),
                Ing("Bilao Big", "Miscellaneous", "pcs"),
                Ing("Party Tray", "Miscellaneous", "pcs"),
                Ing("Rice Wrap", "Miscellaneous", "pcs"),
                Ing("Grease Proof", "Miscellaneous", "pcs"),
                Ing("Plastic 8x11", "Miscellaneous", "pcs"),
                Ing("Disposable Spoon", "Miscellaneous", "pcs"),
                Ing("Disposable Fork", "Miscellaneous", "pcs"),
                Ing("Toothpick", "Miscellaneous", "pcs"),
                Ing("Zonrox", "Miscellaneous", "ml"),
                Ing("Degreaser", "Miscellaneous", "ml"),
                Ing("Glass Cleaner", "Miscellaneous", "ml"),
                Ing("Dish Washing", "Miscellaneous", "ml"),
                Ing("Hand Soap", "Miscellaneous", "ml"),
                Ing("Detergent Powder", "Miscellaneous", "g"),
                Ing("Alcohol", "Miscellaneous", "ml"),
                Ing("Baygon", "Miscellaneous", "pcs"),
                Ing("Garbage Bag Black", "Miscellaneous", "pcs"),
                Ing("Garbage Bag White", "Miscellaneous", "pcs"),
                Ing("Tissue Napkin", "Miscellaneous", "pcs"),
                Ing("Tissue Roll", "Miscellaneous", "pcs"),
                Ing("Sponge", "Miscellaneous", "pcs"),
                Ing("Scotch Tape", "Miscellaneous", "pcs"),
                Ing("Staple Wire", "Miscellaneous", "pcs"),
                Ing("Gloves", "Miscellaneous", "pcs"),
                Ing("Burger Bun", "Raw Materials", "pcs"),
                Ing("Burger Patty", "Raw Materials", "pcs"),

                Ing("Vinegar", "Merchandise", "ml"),
            };

        private static string Normalize(string s)
        {
            var lower = s.ToLowerInvariant().Trim();
            var chars = lower.Select(c => char.IsLetterOrDigit(c) || c == ' ' || c == '\'' ? c : ' ').ToArray();
            var collapsed = new string(chars);
            while (collapsed.Contains("  ", StringComparison.Ordinal))
                collapsed = collapsed.Replace("  ", " ", StringComparison.Ordinal);
            return collapsed.Trim();
        }

        private static IngredientItem Ing(string name, string category, string unit) => new()
        {
            Id = MongoDB.Bson.ObjectId.GenerateNewId().ToString(),
            Item = name,
            IngredientCategory = category,
            CurrentStock = 0,
            Unit = unit,
            ReorderLevel = 10,
            Status = "Low Stock"
        };

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
