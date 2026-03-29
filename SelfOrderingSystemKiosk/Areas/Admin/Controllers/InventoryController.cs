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
            if (!_ingredientCategories.IsValid(ingredientCategory))
            {
                TempData["Message"] = "Invalid ingredient category.";
                return RedirectToAction("Index");
            }

            var newItem = new IngredientItem
            {
                Item = item,
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
        public async Task<IActionResult> Delete(string id)
        {
            await _ingredients.DeleteAsync(id);
            TempData["Message"] = "Ingredient deleted.";
            return RedirectToAction("Index");
        }

        public async Task<IActionResult> Edit(string id)
        {
            var item = await _ingredients.GetByIdAsync(id);
            if (item == null) return RedirectToAction("Index");
            ViewBag.IngredientCategories = IngredientCategoryRegistry.All;
            return View(item);
        }

        [HttpPost]
        public async Task<IActionResult> Edit(IngredientItem updatedItem)
        {
            if (!_ingredientCategories.IsValid(updatedItem.IngredientCategory))
            {
                TempData["Message"] = "Invalid ingredient category.";
                return RedirectToAction("Edit", new { id = updatedItem.Id });
            }

            var previous = await _ingredients.GetByIdAsync(updatedItem.Id);
            updatedItem.Status = updatedItem.CurrentStock <= updatedItem.ReorderLevel ? "Low Stock" : "In Stock";
            await _ingredients.UpdateAsync(updatedItem);
            if (previous != null && previous.CurrentStock != updatedItem.CurrentStock)
            {
                await _ingredients.RecordAdjustmentAsync(
                    updatedItem.Id,
                    updatedItem.Item ?? "",
                    previous.CurrentStock,
                    updatedItem.CurrentStock,
                    "Manual inventory edit");
            }

            TempData["Message"] = $"Ingredient '{updatedItem.Item}' updated.";
            return RedirectToAction("Index");
        }
    }
}
