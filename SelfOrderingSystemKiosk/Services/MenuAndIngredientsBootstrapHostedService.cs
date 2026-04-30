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
                await RetireMenuSeedItemsAsync(coll, ct);
                _logger.LogDebug("Menu board seed skipped; all {Count} default items already exist in {Coll}.", boardItems.Count, menuCollection);
                return;
            }

            foreach (var item in missing)
                item.Id = MongoDB.Bson.ObjectId.GenerateNewId().ToString();

            await coll.InsertManyAsync(missing, cancellationToken: ct);
            await RetireMenuSeedItemsAsync(coll, ct);
            _logger.LogInformation("Seeded {Count} missing menu board items into {Coll}.", missing.Count, menuCollection);
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
                Menu("Softdrinks - Coke", "Drinks", 0m, 921, "/images/wings.png", "drink"),
                Menu("Softdrinks - Sprite", "Drinks", 0m, 920, "/images/wings.png", "drink"),
                Menu("Softdrinks - Royal", "Drinks", 0m, 919, "/images/wings.png", "drink"),

                Menu("Red Iced Tea", "Unlimited Inclusions", 0m, 918, "/images/wings.png", "drink"),
                Menu("Coffee", "Unlimited Inclusions", 0m, 917, "/images/wings.png", "drink"),
                Menu("Tea", "Unlimited Inclusions", 0m, 916, "/images/wings.png", "drink"),
                Menu("Unli Pasta Sardine", "Unlimited Inclusions", 0m, 915, "/images/Kp%20items/Kp%20pasta%20sardine.jpg", "pasta"),
                Menu("Unli Pasta Manzo", "Unlimited Inclusions", 0m, 914, "/images/Kp%20items/Kp%20pasta%20manzo.jpg", "pasta"),
                Menu("Unli Pasta Pomodoro", "Unlimited Inclusions", 0m, 913, "/images/Kp%20items/Kp%20pasta%20pomodoro.jpg", "pasta"),
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
                Ing("Mineral Water", "Drinks", "pcs"),
                Ing("Ice Tea", "Drinks", "ml"),
                Ing("Ice Tea Sugar", "Drinks", "g"),
                Ing("Coke in a Can", "Drinks", "pcs"),
                Ing("Coke Zero", "Drinks", "pcs"),
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

                Ing("Vinegar", "Merchandise", "ml"),
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
