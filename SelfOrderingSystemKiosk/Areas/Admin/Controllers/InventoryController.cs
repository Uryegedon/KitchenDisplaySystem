using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;

namespace SelfOrderingSystemKiosk.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Kitchen")]
    public class InventoryController : Controller
    {
        private readonly IngredientStockService _ingredients;
        private readonly IngredientCategoryRegistry _ingredientCategories;

        public InventoryController(IngredientStockService ingredients, IngredientCategoryRegistry ingredientCategories)
        {
            _ingredients = ingredients;
            _ingredientCategories = ingredientCategories;
        }

        public async Task<IActionResult> Index(string? categoryFilter = null)
        {
            ViewData["Title"] = "Ingredients inventory";
            var all = await _ingredients.GetAllAsync();
            ViewBag.CategoryFilter = string.IsNullOrWhiteSpace(categoryFilter) || categoryFilter == "all" ? "all" : categoryFilter;
            var items = all;
            if (ViewBag.CategoryFilter != "all")
                items = all.Where(i => string.Equals(i.IngredientCategory, categoryFilter, StringComparison.Ordinal)).ToList();

            ViewBag.ItemCount = items?.Count ?? 0;
            ViewBag.IngredientCategories = IngredientCategoryRegistry.All;
            return View(items);
        }

        [HttpPost]
        public async Task<IActionResult> Add(string item, string ingredientCategory, int stock, string unit, int reorderLevel)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                TempData["Message"] = "Ingredient name is required.";
                return RedirectToAction("Index");
            }

            var allItems = await _ingredients.GetAllAsync();
            if (allItems.Any(i => string.Equals(i.Item, item, StringComparison.OrdinalIgnoreCase)))
            {
                TempData["Message"] = $"An ingredient with the name '{item}' already exists.";
                return RedirectToAction("Index");
            }

            if (!_ingredientCategories.IsValid(ingredientCategory))
            {
                TempData["Message"] = "Invalid ingredient category.";
                return RedirectToAction("Index");
            }

            var newItem = new IngredientItem
            {
                Item = item.Trim(),
                IngredientCategory = ingredientCategory,
                CurrentStock = stock,
                Unit = unit ?? "g",
                ReorderLevel = reorderLevel
            };

            await _ingredients.AddAsync(newItem);
            TempData["Message"] = "Ingredient added!";
            return RedirectToAction("Index");
        }

        [HttpPost]
        public async Task<IActionResult> Edit(string id, string item, string ingredientCategory)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(item))
            {
                TempData["Message"] = "Invalid data.";
                return RedirectToAction("Index");
            }

            var existing = await _ingredients.GetByIdAsync(id);
            if (existing == null)
            {
                TempData["Message"] = "Ingredient not found.";
                return RedirectToAction("Index");
            }

            if (!_ingredientCategories.IsValid(ingredientCategory))
            {
                TempData["Message"] = "Invalid ingredient category.";
                return RedirectToAction("Index");
            }

            // Check for duplicate name if name changed
            if (!string.Equals(existing.Item, item.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var allItems = await _ingredients.GetAllAsync();
                if (allItems.Any(i => i.Id != id && string.Equals(i.Item, item.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    TempData["Message"] = $"An ingredient with the name '{item.Trim()}' already exists.";
                    return RedirectToAction("Index");
                }
            }

            existing.Item = item.Trim();
            existing.IngredientCategory = ingredientCategory;
            // Keep other fields unchanged

            await _ingredients.UpdateAsync(existing);
            TempData["Message"] = $"Ingredient '{existing.Item}' updated.";
            return RedirectToAction("Index");
        }

        [HttpPost]
        public async Task<IActionResult> Restock(string id, int amount)
        {
            var item = await _ingredients.GetByIdAsync(id);
            if (item == null)
            {
                TempData["Message"] = "Ingredient not found.";
                return RedirectToAction("Index");
            }

            if (amount <= 0)
            {
                TempData["Message"] = "Invalid restock amount.";
                return RedirectToAction("Index");
            }

            var previousStock = item.CurrentStock;
            item.CurrentStock += amount;
            
            // Update status based on new logic
            if (item.CurrentStock == 0)
            {
                item.Status = "No Stock";
            }
            else if (item.CurrentStock <= item.ReorderLevel)
            {
                item.Status = "Low Stock";
            }
            else
            {
                item.Status = "In Stock";
            }

            await _ingredients.UpdateAsync(item);
            await _ingredients.RecordAdjustmentAsync(
                item.Id,
                item.Item ?? "",
                previousStock,
                item.CurrentStock,
                $"Restock by {amount} units");

            TempData["Message"] = $"Restocked '{item.Item}' by {amount} units.";
            return RedirectToAction("Index");
        }

        [HttpGet]
        public async Task<IActionResult> GetCategory(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName)) return Json(new { category = "" });
            var item = (await _ingredients.GetAllAsync()).FirstOrDefault(i => string.Equals(i.Item, itemName.Trim(), StringComparison.OrdinalIgnoreCase));
            return Json(new { category = item?.IngredientCategory ?? "" });
        }

        [HttpGet]
        public async Task<IActionResult> CheckDuplicate(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName)) return Json(new { exists = false });
            var exists = (await _ingredients.GetAllAsync()).Any(i => string.Equals(i.Item, itemName.Trim(), StringComparison.OrdinalIgnoreCase));
            return Json(new { exists });
        }
    }
}
