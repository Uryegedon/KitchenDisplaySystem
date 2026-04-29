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
                await SeedMenuBoardItemsAsync(db, menuName, cancellationToken);
                await SeedIngredientsIfNeededAsync(db, ingName, cancellationToken);
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

        private async Task SeedMenuBoardItemsAsync(IMongoDatabase db, string menuCollection, CancellationToken ct)
        {
            var coll = db.GetCollection<MenuItem>(menuCollection);
            var boardItems = BuildMenuBoardSeed();

            if (boardItems.Count == 0)
                return;

            var seedNames = boardItems.Select(x => x.Item).ToList();
            var existingNames = await coll.Find(Builders<MenuItem>.Filter.In(x => x.Item, seedNames))
                .Project(x => x.Item)
                .ToListAsync(ct);
            var existing = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = boardItems.Where(x => !existing.Contains(x.Item)).ToList();

            if (missing.Count == 0)
            {
                _logger.LogDebug("Menu board seed skipped; all {Count} default items already exist in {Coll}.", boardItems.Count, menuCollection);
                return;
            }

            foreach (var item in missing)
                item.Id = MongoDB.Bson.ObjectId.GenerateNewId().ToString();

            await coll.InsertManyAsync(missing, cancellationToken: ct);
            _logger.LogInformation("Seeded {Count} missing menu board items into {Coll}.", missing.Count, menuCollection);
        }

        private static List<MenuItem> BuildMenuBoardSeed() =>
            new()
            {
                Menu("Unli Bisita Ni Kap", "Group Add-ons", 50m, 1000, "/images/wings.png", "service"),

                Menu("6 Piece Chicken Wings (2 Flavors Only)", "Wings Ala Carte", 197m, 990, "/images/wings.png", "set"),
                Menu("12 Piece Chicken Wings (3 Flavors Only)", "Wings Ala Carte", 337m, 989, "/images/wings.png", "set"),
                Menu("24 Piece Chicken Wings (6 Flavors Only)", "Wings Ala Carte", 607m, 988, "/images/wings.png", "set"),
                Menu("50 Piece Chicken Wings (6 Flavors Only)", "Wings Ala Carte", 1157m, 987, "/images/wings.png", "set"),
                Menu("100 Piece Chicken Wings (10 Flavors Only)", "Wings Ala Carte", 2107m, 986, "/images/wings.png", "set"),

                Menu("2 Pieces Chicken Wings With Rice - Ala Carte", "Sulit Kap Meals", 77m, 980, "/images/wings.png", "meal"),
                Menu("2 Pieces Chicken Wings With Rice - Unli Rice", "Sulit Kap Meals", 117m, 979, "/images/wings.png", "meal"),
                Menu("4 Pieces Chicken Wings With Rice - Ala Carte", "Sulit Kap Meals", 127m, 978, "/images/wings.png", "meal"),
                Menu("4 Pieces Chicken Wings With Rice - Unli Rice", "Sulit Kap Meals", 147m, 977, "/images/wings.png", "meal"),
                Menu("Mix and Match Ala Carte", "Sulit Kap Meals", 77m, 976, "/images/wings.png", "meal"),

                Menu("Regular Pasta Sardine - Solo", "Pasta", 127m, 970, "/images/Kp%20items/Kp%20pasta%20sardine.jpg", "pasta"),
                Menu("Regular Pasta Sardine - Group", "Pasta", 227m, 969, "/images/Kp%20items/Kp%20pasta%20sardine.jpg", "pasta"),
                Menu("Regular Pasta Sardine - Party", "Pasta", 957m, 968, "/images/Kp%20items/Kp%20pasta%20sardine.jpg", "pasta"),
                Menu("Regular Pasta Manzo - Solo", "Pasta", 127m, 967, "/images/Kp%20items/Kp%20pasta%20manzo.jpg", "pasta"),
                Menu("Regular Pasta Manzo - Group", "Pasta", 227m, 966, "/images/Kp%20items/Kp%20pasta%20manzo.jpg", "pasta"),
                Menu("Regular Pasta Manzo - Party", "Pasta", 957m, 965, "/images/Kp%20items/Kp%20pasta%20manzo.jpg", "pasta"),
                Menu("Regular Pasta Pomodoro - Solo", "Pasta", 127m, 964, "/images/Kp%20items/Kp%20pasta%20pomodoro.jpg", "pasta"),
                Menu("Regular Pasta Pomodoro - Group", "Pasta", 227m, 963, "/images/Kp%20items/Kp%20pasta%20pomodoro.jpg", "pasta"),
                Menu("Regular Pasta Pomodoro - Party", "Pasta", 957m, 962, "/images/Kp%20items/Kp%20pasta%20pomodoro.jpg", "pasta"),
                Menu("Regular Pasta Salsiccia - Solo", "Pasta", 127m, 961, "/images/Kp%20items/Kp%20pasta%20salsiccia.jpg", "pasta"),
                Menu("Regular Pasta Salsiccia - Group", "Pasta", 227m, 960, "/images/Kp%20items/Kp%20pasta%20salsiccia.jpg", "pasta"),
                Menu("Regular Pasta Salsiccia - Party", "Pasta", 957m, 959, "/images/Kp%20items/Kp%20pasta%20salsiccia.jpg", "pasta"),
                Menu("Premium Pasta Gamberetto - Solo", "Pasta", 157m, 958, "/images/wings.png", "pasta"),
                Menu("Premium Pasta Gamberetto - Group", "Pasta", 257m, 957, "/images/wings.png", "pasta"),
                Menu("Premium Pasta Gamberetto - Party", "Pasta", 957m, 956, "/images/wings.png", "pasta"),
                Menu("Premium Pasta Kapow - Solo", "Pasta", 157m, 955, "/images/wings.png", "pasta"),
                Menu("Premium Pasta Kapow - Group", "Pasta", 257m, 954, "/images/wings.png", "pasta"),
                Menu("Premium Pasta Kapow - Party", "Pasta", 957m, 953, "/images/wings.png", "pasta"),

                Menu("Potato Thins Original", "Appetizer", 77m, 950, "/images/Kp%20items/Kp%20potato%20thins.jpg", "snack"),
                Menu("Potato Thins Cheese", "Appetizer", 77m, 949, "/images/Kp%20items/Kp%20potato%20thins%20cheese.jpg", "snack"),
                Menu("Potato Thins Sour Cream", "Appetizer", 77m, 948, "/images/Kp%20items/Kp%20potato%20thins%20sour%20cream.jpg", "snack"),
                Menu("Potato Thins BBQ", "Appetizer", 77m, 947, "/images/Kp%20items/Kp%20potato%20thins%20bbq.jpg", "snack"),
                Menu("Chickings (Chicken Tenders)", "Appetizer", 77m, 946, "/images/wings.png", "chicken"),
                Menu("Nachos", "Appetizer", 177m, 945, "/images/Kp%20items/Kp%20nachos.jpg", "snack"),

                Menu("Extra Gravy", "Add Ons", 20m, 940, "/images/Kp%20items/Kp%20gravy.jpg", "add-on"),
                Menu("Extra Mayo Garlic", "Add Ons", 20m, 939, "/images/wings.png", "add-on"),
                Menu("Garlic Rice", "Add Ons", 30m, 938, "/images/Kp%20items/Kp%20rice.jpg", "rice"),
                Menu("Plain Rice", "Add Ons", 30m, 937, "/images/Kp%20items/Kp%20rice.jpg", "rice"),

                Menu("Kap's Burger Ala Carte", "Kap's Burger", 57m, 930, "/images/wings.png", "burger"),
                Menu("Kap's Burger Meal", "Kap's Burger", 87m, 929, "/images/wings.png", "burger"),

                Menu("Water", "Drinks", 0m, 925, "/images/wings.png", "drink"),
                Menu("Iced Tea", "Drinks", 0m, 924, "/images/wings.png", "drink"),
                Menu("Coffee", "Drinks", 0m, 923, "/images/wings.png", "drink"),
                Menu("Hot Tea", "Drinks", 0m, 922, "/images/wings.png", "drink"),
                Menu("Softdrinks", "Drinks", 0m, 921, "/images/wings.png", "drink"),

                Menu("Red Iced Tea", "Unlimited Inclusions", 0m, 920, "/images/wings.png", "drink"),
                Menu("Coffee", "Unlimited Inclusions", 0m, 919, "/images/wings.png", "drink"),
                Menu("Tea", "Unlimited Inclusions", 0m, 918, "/images/wings.png", "drink"),
                Menu("Unli Pasta Sardine", "Unlimited Inclusions", 0m, 917, "/images/Kp%20items/Kp%20pasta%20sardine.jpg", "pasta"),
                Menu("Unli Pasta Manzo", "Unlimited Inclusions", 0m, 916, "/images/Kp%20items/Kp%20pasta%20manzo.jpg", "pasta"),
                Menu("Unli Pasta Pomodoro", "Unlimited Inclusions", 0m, 915, "/images/Kp%20items/Kp%20pasta%20pomodoro.jpg", "pasta"),
                Menu("Unli Pasta Salsiccia", "Unlimited Inclusions", 0m, 914, "/images/Kp%20items/Kp%20pasta%20salsiccia.jpg", "pasta"),
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
            if (await coll.CountDocumentsAsync(FilterDefinition<IngredientItem>.Empty, cancellationToken: ct) > 0)
                return;

            var seed = BuildIngredientSeed();
            if (seed.Count > 0)
                await coll.InsertManyAsync(seed, cancellationToken: ct);

            _logger.LogInformation("Seeded {Count} ingredients into {Coll}.", seed.Count, ingCollection);
        }

        private static List<IngredientItem> BuildIngredientSeed() =>
            new()
            {
                // Produce & herbs
                Ing("Garlic", "Raw mats", "pcs"),
                Ing("Onion", "Raw mats", "pcs"),
                Ing("Tomatoes", "Raw mats", "g"),
                Ing("Fresh tomatoes", "Raw mats", "g"),
                Ing("Mushrooms", "Raw mats", "g"),
                Ing("Basil", "Raw mats", "g"),
                Ing("Chili / chili peppers", "Raw mats", "g"),
                Ing("Parmesan cheese", "Raw mats", "g"),
                Ing("Peanuts", "Raw mats", "g"),
                Ing("Sugar", "Raw mats", "g"),
                Ing("Salted egg yolk", "Raw mats", "pcs"),
                Ing("Cooking oil", "Misc", "ml"),
                Ing("Olive oil", "Misc", "ml"),
                Ing("Butter", "Raw mats", "g"),
                Ing("Lemon juice", "Sauce", "ml"),
                Ing("Lime juice", "Sauce", "ml"),
                Ing("Milk / cream", "Raw mats", "ml"),
                Ing("Mustard", "Sauce", "ml"),
                Ing("Soy sauce", "Sauce", "ml"),
                Ing("Oyster sauce", "Sauce", "ml"),
                Ing("Fish sauce", "Sauce", "ml"),
                Ing("Tomato sauce", "Sauce", "ml"),
            };

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
