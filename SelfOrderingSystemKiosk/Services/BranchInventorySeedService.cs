using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;

namespace SelfOrderingSystemKiosk.Services
{
    /// <summary>
    /// Seeds existing shared ingredients and menu items to all branches for placeholder data
    /// </summary>
    public class BranchInventorySeedService
    {
        private readonly IngredientStockService _ingredients;
        private readonly MenuItemService _menuItems;
        private readonly BranchService _branchService;
        private readonly ILogger<BranchInventorySeedService> _logger;

        public BranchInventorySeedService(
            IngredientStockService ingredients,
            MenuItemService menuItems,
            BranchService branchService,
            ILogger<BranchInventorySeedService> logger)
        {
            _ingredients = ingredients;
            _menuItems = menuItems;
            _branchService = branchService;
            _logger = logger;
        }

        /// <summary>
        /// Copies all shared ingredients and menu items to each branch as branch-specific items
        /// </summary>
        public async Task SeedBranchInventoryAsync()
        {
            try
            {
                // Get all branches
                var branches = await _branchService.GetAllAsync();
                if (!branches.Any())
                {
                    _logger.LogInformation("No branches found. Skipping inventory seeding.");
                    return;
                }

                // Seed ingredients
                await SeedIngredientsToBranchesAsync(branches);
                
                // Seed menu items
                await SeedMenuItemsToBranchesAsync(branches);

                _logger.LogInformation("Branch inventory seeding completed successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error seeding branch inventory.");
                throw;
            }
        }

        /// <summary>
        /// Copies all shared ingredients to each branch
        /// </summary>
        private async Task SeedIngredientsToBranchesAsync(List<SelfOrderingSystemKiosk.Areas.Admin.Models.Branch> branches)
        {
            // Get all shared ingredients (BranchId is empty or null)
            var allIngredients = await _ingredients.GetAllAsync();
            var sharedIngredients = allIngredients
                .Where(i => string.IsNullOrEmpty(i.BranchId))
                .ToList();

            if (!sharedIngredients.Any())
            {
                _logger.LogInformation("No shared ingredients found. Skipping ingredient seeding.");
                return;
            }

            _logger.LogInformation("Found {BranchCount} branches and {IngredientCount} shared ingredients.", 
                branches.Count, sharedIngredients.Count);

            int totalCreated = 0;

            foreach (var branch in branches)
            {
                // Get existing ingredients for this branch
                var branchIngredients = await _ingredients.GetAllByBranchAsync(branch.Id);
                var branchSpecificIngredients = branchIngredients
                    .Where(i => !string.IsNullOrEmpty(i.BranchId) && i.BranchId == branch.Id)
                    .ToList();

                // Skip if branch already has specific ingredients
                if (branchSpecificIngredients.Any())
                {
                    _logger.LogInformation("Branch {BranchName} already has {Count} branch-specific ingredients. Skipping.", 
                        branch.BranchName, branchSpecificIngredients.Count);
                    continue;
                }

                // Copy shared ingredients to this branch
                foreach (var shared in sharedIngredients)
                {
                    var branchIngredient = new IngredientItem
                    {
                        Item = shared.Item,
                        IngredientCategory = shared.IngredientCategory,
                        CurrentStock = shared.CurrentStock,
                        Unit = shared.Unit,
                        ReorderLevel = shared.ReorderLevel,
                        Status = shared.Status,
                        BranchId = branch.Id
                    };

                    await _ingredients.AddAsync(branchIngredient);
                    totalCreated++;
                }

                _logger.LogInformation("Seeded {Count} ingredients to branch {BranchName}.", 
                    sharedIngredients.Count, branch.BranchName);
            }

            _logger.LogInformation("Created {TotalCreated} branch-specific ingredient copies.", totalCreated);
        }

        /// <summary>
        /// Copies all shared menu items to each branch
        /// </summary>
        private async Task SeedMenuItemsToBranchesAsync(List<SelfOrderingSystemKiosk.Areas.Admin.Models.Branch> branches)
        {
            // Get all shared menu items (BranchId is empty or null)
            var allMenuItems = await _menuItems.GetAllAsync();
            var sharedMenuItems = allMenuItems
                .Where(i => string.IsNullOrEmpty(i.BranchId))
                .ToList();

            if (!sharedMenuItems.Any())
            {
                _logger.LogInformation("No shared menu items found. Skipping menu item seeding.");
                return;
            }

            _logger.LogInformation("Found {BranchCount} branches and {MenuItemCount} shared menu items.", 
                branches.Count, sharedMenuItems.Count);

            int totalCreated = 0;

            foreach (var branch in branches)
            {
                // Get existing menu items for this branch
                var branchMenuItems = await _menuItems.GetAllByBranchAsync(branch.Id);
                var branchSpecificMenuItems = branchMenuItems
                    .Where(i => !string.IsNullOrEmpty(i.BranchId) && i.BranchId == branch.Id)
                    .ToList();

                // Skip if branch already has specific menu items
                if (branchSpecificMenuItems.Any())
                {
                    _logger.LogInformation("Branch {BranchName} already has {Count} branch-specific menu items. Skipping.", 
                        branch.BranchName, branchSpecificMenuItems.Count);
                    continue;
                }

                // Copy shared menu items to this branch
                foreach (var shared in sharedMenuItems)
                {
                    var branchMenuItem = new MenuItem
                    {
                        Item = shared.Item,
                        Category = shared.Category,
                        FoodCategory = shared.FoodCategory,
                        Price = shared.Price,
                        Availability = shared.Availability,
                        Image = shared.Image,
                        CurrentStock = shared.CurrentStock,
                        Unit = shared.Unit,
                        ReorderLevel = shared.ReorderLevel,
                        MenuOrder = shared.MenuOrder,
                        Status = shared.Status,
                        Recipe = shared.Recipe,
                        BranchId = branch.Id
                    };

                    await _menuItems.AddAsync(branchMenuItem);
                    totalCreated++;
                }

                _logger.LogInformation("Seeded {Count} menu items to branch {BranchName}.", 
                    sharedMenuItems.Count, branch.BranchName);
            }

            _logger.LogInformation("Created {TotalCreated} branch-specific menu item copies.", totalCreated);
        }
    }
}
