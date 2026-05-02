using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;

namespace SelfOrderingSystemKiosk.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Kitchen")]
    public class MenuController : Controller
    {
        private readonly MenuItemService _menuItems;
        private readonly IngredientStockService _ingredients;
        private readonly IWebHostEnvironment _environment;
        private readonly MenuCategoryRegistry _menuCategories;
        private const long MaxImageUploadBytes = 5 * 1024 * 1024;

        public MenuController(
            MenuItemService menuItems,
            IngredientStockService ingredients,
            IWebHostEnvironment environment,
            MenuCategoryRegistry menuCategories)
        {
            _menuItems = menuItems;
            _ingredients = ingredients;
            _environment = environment;
            _menuCategories = menuCategories;
        }

        public async Task<IActionResult> Index(string message = null, string categoryFilter = null)
        {
            ViewData["Title"] = "Menu (foods)";
            ViewBag.Message = message;
            ViewBag.MenuCategories = _menuCategories.All;
            var filter = string.IsNullOrWhiteSpace(categoryFilter) || string.Equals(categoryFilter, "all", StringComparison.OrdinalIgnoreCase)
                ? null
                : categoryFilter.Trim();
            ViewBag.CategoryFilter = filter ?? "all";

            var allItems = await _menuItems.GetAllAsync();
            ViewBag.MenuCategoryFormList = BuildEditCategoryOptions(allItems);
            ViewBag.Ingredients = await _ingredients.GetAllAsync();

            var items = allItems;
            if (filter != null && _menuCategories.IsValidKey(filter))
                items = allItems.Where(i => string.Equals(i.Category, filter, StringComparison.Ordinal)).ToList();

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
        [RequestSizeLimit(MaxImageUploadBytes)]
        public async Task<IActionResult> Add(string name, string category, decimal price, IFormFile imageFile)
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
                Recipe = ParseRecipeLines(Request.Form)
            };

            await _menuItems.AddAsync(newItem);
            return RedirectToAction("Index", new { message = "Menu item added successfully!" });
        }

        [HttpPost]
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
                existing.Recipe = ParseRecipeLines(Request.Form);

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

            await _menuItems.DeleteAsync(id);
            return RedirectToAction("Index", new { message = "Menu item deleted successfully!" });
        }

        [HttpGet]
        public async Task<IActionResult> RecipeJson(string id)
        {
            var m = await _menuItems.GetByIdAsync(id);
            if (m == null)
                return Json(Array.Empty<MenuRecipeLine>());
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
        public async Task<IActionResult> ToggleAvailability(string id, string availability)
        {
            try
            {
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
    }
}
