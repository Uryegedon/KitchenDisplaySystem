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
            ViewData["Title"] = "Kitchen/Supplies inventory";
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
        public async Task<IActionResult> Add(
            List<string> item,
            List<string> ingredientCategory,
            List<int> stock,
            List<string> unit,
            List<int> reorderLevel)
        {
            var rows = item
                .Select((name, index) => new
                {
                    Name = name?.Trim() ?? "",
                    Category = index < ingredientCategory.Count ? ingredientCategory[index] : "",
                    Stock = index < stock.Count ? stock[index] : 0,
                    Unit = index < unit.Count ? unit[index] : "g",
                    ReorderLevel = index < reorderLevel.Count ? reorderLevel[index] : 10
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .ToList();

            if (rows.Count == 0)
            {
                TempData["Message"] = "At least one ingredient name is required.";
                return RedirectToAction("Index");
            }

            var allItems = await _ingredients.GetAllAsync();
            var existingNames = allItems
                .Select(i => i.Item ?? "")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var duplicateInForm = rows
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicateInForm != null)
            {
                TempData["Message"] = $"Ingredient '{duplicateInForm.Key}' is listed more than once.";
                return RedirectToAction("Index");
            }

            var existingDuplicate = rows.FirstOrDefault(x => existingNames.Contains(x.Name));
            if (existingDuplicate != null)
            {
                TempData["Message"] = $"An ingredient with the name '{existingDuplicate.Name}' already exists.";
                return RedirectToAction("Index");
            }

            var invalidCategory = rows.FirstOrDefault(x => !_ingredientCategories.IsValid(x.Category));
            if (invalidCategory != null)
            {
                TempData["Message"] = $"Invalid ingredient category for '{invalidCategory.Name}'.";
                return RedirectToAction("Index");
            }

            foreach (var row in rows)
            {
                var newItem = new IngredientItem
                {
                    Item = row.Name,
                    IngredientCategory = row.Category,
                    CurrentStock = Math.Max(0, row.Stock),
                    Unit = string.IsNullOrWhiteSpace(row.Unit) ? "g" : row.Unit,
                    ReorderLevel = Math.Max(0, row.ReorderLevel)
                };

                await _ingredients.AddAsync(newItem);
            }

            TempData["Message"] = rows.Count == 1
                ? "Ingredient added!"
                : $"{rows.Count} ingredients added!";
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
        public async Task<IActionResult> Restock(string id, int amount, string? batchNote = null)
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
                string.IsNullOrWhiteSpace(batchNote)
                    ? $"Restock by {amount} units"
                    : $"Restock by {amount} units - Batch/Delivery: {batchNote.Trim()}");

            TempData["Message"] = string.IsNullOrWhiteSpace(batchNote)
                ? $"Restocked '{item.Item}' by {amount} units."
                : $"Restocked '{item.Item}' by {amount} units. Batch/Delivery: {batchNote.Trim()}";
            return RedirectToAction("Index");
        }

        [HttpPost]
        public async Task<IActionResult> ClearStock(string id)
        {
            var item = await _ingredients.GetByIdAsync(id);
            if (item == null)
            {
                TempData["Message"] = "Ingredient not found.";
                return RedirectToAction("Index");
            }

            var previousStock = item.CurrentStock;
            item.CurrentStock = 0;
            item.Status = "No Stock";

            await _ingredients.UpdateAsync(item);
            await _ingredients.RecordAdjustmentAsync(
                item.Id,
                item.Item ?? "",
                previousStock,
                item.CurrentStock,
                "Stock cleared to 0");

            TempData["Message"] = $"Stock for '{item.Item}' was cleared to 0.";
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
