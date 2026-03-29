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
                Ing("Garlic", "Produce & herbs", "pcs"),
                Ing("Onion", "Produce & herbs", "pcs"),
                Ing("Tomatoes", "Produce & herbs", "kg"),
                Ing("Fresh tomatoes", "Produce & herbs", "kg"),
                Ing("Mushrooms", "Produce & herbs", "g"),
                Ing("Basil", "Produce & herbs", "g"),
                Ing("Chili / chili peppers", "Produce & herbs", "g"),
                // Dry goods & dairy
                Ing("Parmesan cheese", "Dry goods & dairy", "g"),
                Ing("Peanuts", "Dry goods & dairy", "g"),
                Ing("Sugar", "Dry goods & dairy", "g"),
                // Specialty
                Ing("Salted egg yolk", "Specialty", "pcs"),
                // Oils, fats & liquids
                Ing("Cooking oil", "Oils, fats & liquids", "ml"),
                Ing("Olive oil", "Oils, fats & liquids", "ml"),
                Ing("Butter", "Oils, fats & liquids", "g"),
                Ing("Lemon juice", "Oils, fats & liquids", "ml"),
                Ing("Lime juice", "Oils, fats & liquids", "ml"),
                Ing("Milk / cream", "Oils, fats & liquids", "ml"),
                // Sauces & condiments
                Ing("Mustard", "Sauces & condiments", "ml"),
                Ing("Soy sauce", "Sauces & condiments", "ml"),
                Ing("Oyster sauce", "Sauces & condiments", "ml"),
                Ing("Fish sauce", "Sauces & condiments", "ml"),
                Ing("Tomato sauce", "Sauces & condiments", "ml"),
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
