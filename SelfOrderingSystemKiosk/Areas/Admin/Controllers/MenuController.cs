using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;
using SelfOrderingSystemKiosk.Areas.Admin.Models;

namespace SelfOrderingSystemKiosk.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Owner,BranchManager,Admin")]
    public class MenuController : Controller
    {
        private readonly MenuItemService _menuItems;
        private readonly IngredientStockService _ingredients;
        private readonly BranchService _branchService;
        private readonly IWebHostEnvironment _environment;
        private readonly MenuCategoryRegistry _menuCategories;
        private const long MaxImageUploadBytes = 5 * 1024 * 1024;

        public MenuController(
            MenuItemService menuItems,
            IngredientStockService ingredients,
            BranchService branchService,
            IWebHostEnvironment environment,
            MenuCategoryRegistry menuCategories)
        {
            _menuItems = menuItems;
            _ingredients = ingredients;
            _branchService = branchService;
            _environment = environment;
            _menuCategories = menuCategories;
        }

        public async Task<IActionResult> Index(string message = null, string categoryFilter = null, string? branchFilter = null)
        {
            ViewData["Title"] = "Menu (foods)";
            ViewBag.Message = message;
            ViewBag.MenuCategories = _menuCategories.All;
            var filter = string.IsNullOrWhiteSpace(categoryFilter) || string.Equals(categoryFilter, "all", StringComparison.OrdinalIgnoreCase)
                ? null
                : categoryFilter.Trim();
            ViewBag.CategoryFilter = filter ?? "all";

            // Get user's branch context
            var userBranchId = User.GetBranchId();
            var isOwner = User.HasAllBranchAccess();
            if (!isOwner && string.IsNullOrWhiteSpace(userBranchId))
                return Forbid();

            // Get all branches for owner filter dropdown
            List<Branch> allBranches = new();
            if (isOwner)
            {
                allBranches = await _branchService.GetAllAsync();
                ViewBag.AllBranches = allBranches;

                // Owner must select a branch - default to first branch if none selected
                if (string.IsNullOrEmpty(branchFilter) || branchFilter == "all")
                {
                    branchFilter = allBranches.FirstOrDefault()?.Id;
                }
            }

            // Get branch info for display
            Branch? userBranch = null;
            string? effectiveBranchId = userBranchId;

            if (isOwner && !string.IsNullOrEmpty(branchFilter))
            {
                userBranch = allBranches.FirstOrDefault(b => b.Id == branchFilter);
                effectiveBranchId = branchFilter;
            }
            else if (!string.IsNullOrEmpty(userBranchId))
            {
                userBranch = await _branchService.GetByIdAsync(userBranchId);
            }

            ViewData["BranchName"] = userBranch?.BranchName ?? "Select Branch";
            ViewBag.BranchFilter = branchFilter;

            var cleanedRecipeItems = await _menuItems.CleanupInvalidRecipeLinesAsync(effectiveBranchId);
            if (cleanedRecipeItems > 0 && ViewBag.Message == null)
                ViewBag.Message = $"Removed invalid recipe ingredients from {cleanedRecipeItems} menu item(s).";

            // Get menu items filtered by branch
            var allItems = await _menuItems.GetAllByBranchAsync(effectiveBranchId);
            ViewBag.MenuCategoryFormList = BuildEditCategoryOptions(allItems);
            ViewBag.Ingredients = await _ingredients.GetAllByBranchAsync(effectiveBranchId);

            var items = allItems;
            if (filter != null && _menuCategories.IsValidKey(filter))
                items = allItems.Where(i => string.Equals(i.Category, filter, StringComparison.Ordinal)).ToList();

            ViewBag.IsOwner = isOwner;
            return View(items);
        }

        private List<MenuCategoryOption> BuildEditCategoryOptions(IEnumerable<MenuItem> items)
        {
            var list = _menuCategories.All.ToList();
            var keys = new HashSet<string>(list.Select(c => c.Key), StringComparer.Ordinal);
            foreach (var i in items)
            {
                if (string.IsNullOrWhiteSpace(i.Category)) continue;
                if (keys.Add(i.Category))
                    list.Add(new MenuCategoryOption { Key = i.Category, DisplayName = i.Category + " (legacy)" });
            }

            return list.OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(MaxImageUploadBytes)]
        public async Task<IActionResult> Add(string name, string category, decimal price, IFormFile imageFile, string? branchFilter = null)
        {
            if (string.IsNullOrEmpty(name))
                return RedirectToAction("Index", new { message = "Menu item name is required.", categoryFilter = "all" });

            if (!_menuCategories.IsValidKey(category))
                return RedirectToAction("Index", new { message = "Invalid kiosk category selected.", categoryFilter = "all" });

            string? imagePath = null;
            if (imageFile != null && imageFile.Length > 0)
                imagePath = await SaveImageFile(imageFile);

            if (string.IsNullOrEmpty(imagePath))
                imagePath = _menuCategories.GetDefaultImage(category);

            const int currentStock = 0;
            const int reorderLevel = 0;
            const int menuOrder = 0;
            var effectiveAvailability = string.Equals(category, "Unavailable", StringComparison.Ordinal)
                ? "Unavailable"
                : "Available";

            // Get user's branch context - managers create items for their branch only
            var userBranchId = User.GetBranchId();
            var isOwner = User.HasAllBranchAccess();
            if (!isOwner && string.IsNullOrWhiteSpace(userBranchId))
                return Forbid();
            var effectiveBranchId = isOwner && !string.IsNullOrWhiteSpace(branchFilter)
                ? branchFilter.Trim()
                : userBranchId;
            if (isOwner && string.IsNullOrWhiteSpace(effectiveBranchId))
                return RedirectToAction("Index", new { message = "Choose a branch before adding menu items.", categoryFilter = "all" });
            if (isOwner && await _branchService.GetByIdAsync(effectiveBranchId!) == null)
                return RedirectToAction("Index", new { message = "Choose a valid branch before adding menu items.", categoryFilter = "all" });

            var recipe = ParseRecipeLines(Request.Form);
            if (!await CanUseRecipeIngredientsAsync(recipe, effectiveBranchId))
                return RedirectToAction("Index", new { message = "Recipe contains ingredients outside your branch.", categoryFilter = "all" });

            var newItem = new MenuItem
            {
                Item = name,
                Category = category,
                FoodCategory = null,
                Price = price,
                Availability = effectiveAvailability,
                Image = imagePath,
                CurrentStock = currentStock,
                Unit = "pcs",
                ReorderLevel = reorderLevel,
                MenuOrder = menuOrder,
                Status = "Available",
                Recipe = recipe,
                BranchId = effectiveBranchId ?? string.Empty
            };

            await _menuItems.AddAsync(newItem);
            return RedirectToAction("Index", new { message = "Menu item added successfully!" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(MaxImageUploadBytes)]
        public async Task<IActionResult> Edit(
            string Id,
            string Item,
            string Category,
            decimal Price,
            string? Image,
            IFormFile imageFile,
            string? categoryFilter = null)
        {
            try
            {
                var existing = await _menuItems.GetByIdAsync(Id);
                if (existing == null)
                    return RedirectToAction("Index", new { message = "Item not found.", categoryFilter = categoryFilter ?? "all" });
                if (!CanManageBranchRecord(existing.BranchId))
                    return Forbid();

                var categoryOk = _menuCategories.IsValidKey(Category)
                    || string.Equals(Category, existing.Category, StringComparison.Ordinal);
                if (!categoryOk)
                    return RedirectToAction("Index", new { message = "Invalid kiosk category selected.", categoryFilter = categoryFilter ?? "all" });

                if (imageFile != null && imageFile.Length > 0)
                {
                    var newImagePath = await SaveImageFile(imageFile);
                    if (!string.IsNullOrEmpty(newImagePath))
                        existing.Image = newImagePath;
                }
                else if (!string.IsNullOrEmpty(Image))
                    existing.Image = Image;
                else if (string.IsNullOrEmpty(existing.Image))
                    existing.Image = _menuCategories.GetDefaultImage(Category);

                existing.Item = Item ?? existing.Item;
                existing.Category = Category ?? existing.Category;
                existing.Price = Price;
                var recipe = ParseRecipeLines(Request.Form);
                if (!await CanUseRecipeIngredientsAsync(recipe, existing.BranchId))
                    return RedirectToAction("Index", new { message = "Recipe contains ingredients outside your branch.", categoryFilter = categoryFilter ?? "all" });
                existing.Recipe = recipe;
                // Preserve BranchId from existing item - don't allow changing branch assignment

                if (string.Equals(existing.Category, "Unavailable", StringComparison.Ordinal))
                    existing.Availability = "Unavailable";
                // else: keep existing Availability/MenuOrder/FoodCategory

                var updated = await _menuItems.UpdateAsync(existing);
                if (!updated)
                    return RedirectToAction("Index", new { message = "Menu item was not updated because no database row matched it.", categoryFilter = categoryFilter ?? "all" });

                return RedirectToAction("Index", new { message = "Menu item updated successfully!", categoryFilter = string.IsNullOrWhiteSpace(categoryFilter) ? existing.Category : categoryFilter });
            }
            catch
            {
                return RedirectToAction("Index", new { message = "Error updating item. Please try again.", categoryFilter = categoryFilter ?? "all" });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return RedirectToAction("Index", new { message = "Menu item not found." });

            var existing = await _menuItems.GetByIdAsync(id);
            if (existing == null)
                return RedirectToAction("Index", new { message = "Menu item not found." });
            if (!CanManageBranchRecord(existing.BranchId))
                return Forbid();

            await _menuItems.DeleteAsync(id);
            return RedirectToAction("Index", new { message = "Menu item deleted successfully!" });
        }

        [HttpGet]
        public async Task<IActionResult> RecipeJson(string id)
        {
            var m = await _menuItems.GetByIdAsync(id);
            if (m == null)
                return Json(Array.Empty<MenuRecipeLine>());
            if (!CanViewBranchRecord(m.BranchId))
                return Forbid();
            return Json(m.Recipe ?? new List<MenuRecipeLine>());
        }

        private static List<MenuRecipeLine> ParseRecipeLines(IFormCollection form)
        {
            var ids = form["recipeIngredientId"];
            var qtys = form["recipeQtyPerUnit"];
            var list = new List<MenuRecipeLine>();
            var n = Math.Max(ids.Count, qtys.Count);
            for (var i = 0; i < n; i++)
            {
                var id = i < ids.Count ? ids[i]!.ToString() : string.Empty;
                var qs = i < qtys.Count ? qtys[i]!.ToString() : string.Empty;
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!int.TryParse(qs, out var q) || q <= 0) continue;
                list.Add(new MenuRecipeLine { IngredientId = id.Trim(), QuantityPerUnit = q });
            }
            return list;
        }

        private async Task<string?> SaveImageFile(IFormFile imageFile)
        {
            if (imageFile == null || imageFile.Length == 0)
                return null;
            if (imageFile.Length > MaxImageUploadBytes)
                return null;

            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
            var fileExtension = Path.GetExtension(imageFile.FileName).ToLowerInvariant();
            if (!allowedExtensions.Contains(fileExtension))
                return null;
            if (!await HasValidImageSignatureAsync(imageFile, fileExtension))
                return null;

            var itemsDirectory = Path.Combine(_environment.WebRootPath, "images", "items");
            if (!Directory.Exists(itemsDirectory))
                Directory.CreateDirectory(itemsDirectory);

            var fileName = $"{Guid.NewGuid()}{fileExtension}";
            var filePath = Path.Combine(itemsDirectory, fileName);

            await using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await imageFile.CopyToAsync(stream);
            }

            return $"/images/items/{fileName}";
        }

        private static async Task<bool> HasValidImageSignatureAsync(IFormFile imageFile, string extension)
        {
            var buffer = new byte[12];
            await using var stream = imageFile.OpenReadStream();
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
            if (read < 4)
                return false;

            return extension switch
            {
                ".jpg" or ".jpeg" => buffer[0] == 0xFF && buffer[1] == 0xD8 && buffer[2] == 0xFF,
                ".png" => read >= 8
                    && buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47
                    && buffer[4] == 0x0D && buffer[5] == 0x0A && buffer[6] == 0x1A && buffer[7] == 0x0A,
                ".gif" => read >= 6
                    && buffer[0] == 0x47 && buffer[1] == 0x49 && buffer[2] == 0x46
                    && buffer[3] == 0x38 && (buffer[4] == 0x37 || buffer[4] == 0x39) && buffer[5] == 0x61,
                ".webp" => read >= 12
                    && buffer[0] == 0x52 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x46
                    && buffer[8] == 0x57 && buffer[9] == 0x45 && buffer[10] == 0x42 && buffer[11] == 0x50,
                _ => false
            };
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleAvailability(string id, string availability)
        {
            try
            {
                var item = await _menuItems.GetByIdAsync(id);
                if (item == null)
                    return Json(new { success = false, message = "Item not found." });
                if (!CanManageBranchRecord(item.BranchId))
                    return Forbid();

                await _menuItems.ToggleAvailabilityAsync(id, availability);

                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest" ||
                    Request.Headers["Content-Type"].ToString().Contains("application/x-www-form-urlencoded"))
                    return Json(new { success = true, message = $"Item availability set to {availability}!" });

                return RedirectToAction("Index", new { message = $"Item availability set to {availability}!" });
            }
            catch (Exception)
            {
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest" ||
                    Request.Headers["Content-Type"].ToString().Contains("application/x-www-form-urlencoded"))
                    return Json(new { success = false, message = "Failed to update availability." });

                return RedirectToAction("Index", new { message = "Failed to update availability." });
            }
        }

        private bool CanManageBranchRecord(string? recordBranchId)
        {
            if (User.HasAllBranchAccess())
                return true;

            var userBranchId = User.GetBranchId();
            return !string.IsNullOrWhiteSpace(userBranchId) &&
                   !string.IsNullOrWhiteSpace(recordBranchId) &&
                   string.Equals(recordBranchId, userBranchId, StringComparison.OrdinalIgnoreCase);
        }

        private bool CanViewBranchRecord(string? recordBranchId)
        {
            if (User.HasAllBranchAccess())
                return true;

            var userBranchId = User.GetBranchId();
            return !string.IsNullOrWhiteSpace(userBranchId) &&
                   (string.IsNullOrWhiteSpace(recordBranchId) ||
                    string.Equals(recordBranchId, userBranchId, StringComparison.OrdinalIgnoreCase));
        }

        private async Task<bool> CanUseRecipeIngredientsAsync(IEnumerable<MenuRecipeLine> recipe, string? targetBranchId = null)
        {
            var branchId = User.HasAllBranchAccess()
                ? targetBranchId
                : User.GetBranchId();
            var sharedOnly = User.HasAllBranchAccess() && string.IsNullOrWhiteSpace(branchId);
            if (string.IsNullOrWhiteSpace(branchId) && !sharedOnly)
                return false;

            foreach (var line in recipe)
            {
                var ingredient = await _ingredients.GetByIdAsync(line.IngredientId);
                if (ingredient == null)
                    return false;
                if (IsUnknownIngredientName(ingredient.Item))
                    return false;
                if (sharedOnly && !string.IsNullOrWhiteSpace(ingredient.BranchId))
                    return false;
                if (!sharedOnly &&
                    !string.IsNullOrWhiteSpace(ingredient.BranchId) &&
                    !string.Equals(ingredient.BranchId, branchId, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private static bool IsUnknownIngredientName(string? name)
        {
            var normalized = (name ?? string.Empty).Trim();
            return normalized.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Unknown Ingredient", StringComparison.OrdinalIgnoreCase);
        }
    }
}
