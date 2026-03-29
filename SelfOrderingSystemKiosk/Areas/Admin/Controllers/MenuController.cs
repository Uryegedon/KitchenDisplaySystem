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
        private readonly IWebHostEnvironment _environment;
        private readonly MenuCategoryRegistry _menuCategories;
        private readonly FoodCategoryRegistry _foodCategories;

        public MenuController(
            MenuItemService menuItems,
            IWebHostEnvironment environment,
            MenuCategoryRegistry menuCategories,
            FoodCategoryRegistry foodCategories)
        {
            _menuItems = menuItems;
            _environment = environment;
            _menuCategories = menuCategories;
            _foodCategories = foodCategories;
        }

        public async Task<IActionResult> Index(string message = null, string categoryFilter = null)
        {
            ViewData["Title"] = "Menu (foods)";
            ViewBag.Message = message;
            ViewBag.MenuCategories = _menuCategories.All;
            ViewBag.FoodCategories = FoodCategoryRegistry.All;
            var filter = string.IsNullOrWhiteSpace(categoryFilter) || string.Equals(categoryFilter, "all", StringComparison.OrdinalIgnoreCase)
                ? null
                : categoryFilter.Trim();
            ViewBag.CategoryFilter = filter ?? "all";

            var allItems = await _menuItems.GetAllAsync();
            ViewBag.MenuCategoryFormList = BuildEditCategoryOptions(allItems);

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
        public async Task<IActionResult> Add(
            string name,
            string category,
            string foodCategory,
            decimal price,
            string availability,
            int currentStock,
            string unit,
            int reorderLevel,
            int menuOrder,
            IFormFile imageFile)
        {
            if (!string.IsNullOrEmpty(name))
            {
                if (!_menuCategories.IsValidKey(category))
                    return RedirectToAction("Index", new { message = "Invalid kiosk category selected.", categoryFilter = "all" });

                if (!_foodCategories.IsValid(foodCategory))
                    return RedirectToAction("Index", new { message = "Invalid food type selected.", categoryFilter = "all" });

                string? imagePath = null;

                if (imageFile != null && imageFile.Length > 0)
                    imagePath = await SaveImageFile(imageFile);

                if (string.IsNullOrEmpty(imagePath))
                    imagePath = _menuCategories.GetDefaultImage(category);

                var effectiveAvailability = string.Equals(category, "Unavailable", StringComparison.Ordinal)
                    ? "Unavailable"
                    : (currentStock == 0 ? "Unavailable" : (availability ?? "Available"));

                var newItem = new MenuItem
                {
                    Item = name,
                    Category = category,
                    FoodCategory = string.IsNullOrWhiteSpace(foodCategory) ? null : foodCategory.Trim(),
                    Price = price,
                    Availability = effectiveAvailability,
                    Image = imagePath,
                    CurrentStock = currentStock,
                    Unit = unit ?? "pcs",
                    ReorderLevel = reorderLevel,
                    MenuOrder = menuOrder,
                    Status = currentStock <= reorderLevel ? "Low Stock" : "In Stock"
                };

                await _menuItems.AddAsync(newItem);
            }

            return RedirectToAction("Index", new { message = "Menu item added successfully!" });
        }

        [HttpPost]
        public async Task<IActionResult> Edit(MenuItem updated, IFormFile imageFile)
        {
            var existing = await _menuItems.GetByIdAsync(updated.Id);
            if (existing != null)
            {
                var categoryOk = _menuCategories.IsValidKey(updated.Category)
                    || string.Equals(updated.Category, existing.Category, StringComparison.Ordinal);
                if (!categoryOk)
                    return RedirectToAction("Index", new { message = "Invalid kiosk category selected." });

                if (!_foodCategories.IsValid(updated.FoodCategory))
                    return RedirectToAction("Index", new { message = "Invalid food type selected." });

                if (imageFile != null && imageFile.Length > 0)
                {
                    var newImagePath = await SaveImageFile(imageFile);
                    if (!string.IsNullOrEmpty(newImagePath))
                        updated.Image = newImagePath;
                }
                else
                    updated.Image = existing.Image ?? updated.Image;

                if (string.IsNullOrEmpty(updated.Image))
                    updated.Image = _menuCategories.GetDefaultImage(updated.Category);

                if (string.Equals(updated.Category, "Unavailable", StringComparison.Ordinal))
                    updated.Availability = "Unavailable";

                updated.Status = updated.CurrentStock <= updated.ReorderLevel ? "Low Stock" : "In Stock";
                await _menuItems.UpdateAsync(updated);
            }

            return RedirectToAction("Index", new { message = "Menu item updated successfully!" });
        }

        private async Task<string?> SaveImageFile(IFormFile imageFile)
        {
            if (imageFile == null || imageFile.Length == 0)
                return null;

            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
            var fileExtension = Path.GetExtension(imageFile.FileName).ToLowerInvariant();
            if (!allowedExtensions.Contains(fileExtension))
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
