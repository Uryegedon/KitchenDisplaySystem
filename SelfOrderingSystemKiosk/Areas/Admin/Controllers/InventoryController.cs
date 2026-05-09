using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;
using SelfOrderingSystemKiosk.Areas.Admin.Models;

namespace SelfOrderingSystemKiosk.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Owner,BranchManager,Admin")]
    public class InventoryController : Controller
    {
        private readonly IngredientStockService _ingredients;
        private readonly MenuItemService _menuItems;
        private readonly IngredientCategoryRegistry _ingredientCategories;
        private readonly BranchService _branchService;
        private readonly StockMovementService _stockMovements;

        public InventoryController(IngredientStockService ingredients, MenuItemService menuItems, IngredientCategoryRegistry ingredientCategories, BranchService branchService, StockMovementService stockMovements)
        {
            _ingredients = ingredients;
            _menuItems = menuItems;
            _ingredientCategories = ingredientCategories;
            _branchService = branchService;
            _stockMovements = stockMovements;
        }

        public async Task<IActionResult> Index(string? categoryFilter = null, string? branchFilter = null, string? expiryFilter = null, bool print = false)
        {
            ViewData["Title"] = "Kitchen/Supplies inventory";

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

            // Get ingredients filtered by branch
            var all = await _ingredients.GetAllByBranchAsync(effectiveBranchId);
            ViewBag.CategoryFilter = string.IsNullOrWhiteSpace(categoryFilter) || categoryFilter == "all" ? "all" : categoryFilter;
            ViewBag.BranchFilter = branchFilter;
            
            var items = all;
            if (ViewBag.CategoryFilter != "all")
                items = all.Where(i => string.Equals(i.IngredientCategory, categoryFilter, StringComparison.Ordinal)).ToList();
            ViewBag.ExpiryFilter = string.IsNullOrWhiteSpace(expiryFilter) ? "all" : expiryFilter;
            var today = DateTime.UtcNow.Date;
            if (ViewBag.ExpiryFilter == "expired")
                items = items.Where(i => i.ExpirationDate.HasValue && i.ExpirationDate.Value.Date < today).ToList();
            else if (ViewBag.ExpiryFilter == "near")
                items = items.Where(i => i.ExpirationDate.HasValue && i.ExpirationDate.Value.Date >= today && i.ExpirationDate.Value.Date <= today.AddDays(7)).ToList();

            var visibleItems = items ?? new List<IngredientItem>();
            ViewBag.ItemCount = visibleItems.Count;
            ViewBag.IngredientCategories = IngredientCategoryRegistry.All;
            ViewBag.IsOwner = isOwner;
            ViewBag.AllBranches = allBranches;
            ViewBag.AutoPrint = print;
            ViewBag.PrintStockStats = await BuildPrintStockStatsAsync(visibleItems);
            return View(visibleItems);
        }

        private async Task<Dictionary<string, PrintStockSummary>> BuildPrintStockStatsAsync(List<IngredientItem> items)
        {
            var todayLocal = DateTime.Today;
            var startUtc = todayLocal.ToUniversalTime();
            var endUtc = todayLocal.AddDays(1).ToUniversalTime();
            var movements = await _stockMovements.GetForInventoryItemsAsync(items.Select(i => i.Id), startUtc, endUtc, User.GetBranchId());
            var byItem = movements
                .GroupBy(m => m.InventoryItemId ?? "", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderBy(m => m.TimestampUtc).ToList(), StringComparer.OrdinalIgnoreCase);

            return items.ToDictionary(
                item => item.Id,
                item =>
                {
                    byItem.TryGetValue(item.Id, out var itemMovements);
                    var firstMovement = itemMovements?.FirstOrDefault();
                    var beginningStock = firstMovement?.StockBefore ?? item.CurrentStock;
                    var deliveredStock = itemMovements?
                        .Where(m => m.QuantityDelta > 0)
                        .Sum(m => m.QuantityDelta) ?? 0;
                    var endingStock = item.CurrentStock;
                    var rawUsedEstimate = beginningStock + deliveredStock - endingStock;
                    var usedEstimate = Math.Abs(rawUsedEstimate) <= 1 ? 0 : Math.Max(0, rawUsedEstimate);

                    return new PrintStockSummary
                    {
                        BeginningStock = beginningStock,
                        DeliveredStock = deliveredStock,
                        EndingStock = endingStock,
                        UsedStockEstimate = usedEstimate
                    };
                },
                StringComparer.OrdinalIgnoreCase);
        }

        [HttpPost]
        public async Task<IActionResult> Add(
            List<string> item,
            List<string> ingredientCategory,
            List<int> stock,
            List<string> unit,
            List<int> reorderLevel,
            List<decimal> costPerUnit,
            List<DateTime?> expirationDate,
            string? branchFilter = null)
        {
            var rows = item
                .Select((name, index) => new
                {
                    Name = name?.Trim() ?? "",
                    Category = index < ingredientCategory.Count ? ingredientCategory[index] : "",
                    Stock = index < stock.Count ? stock[index] : 0,
                    Unit = index < unit.Count ? unit[index] : "g",
                    ReorderLevel = index < reorderLevel.Count ? reorderLevel[index] : 10,
                    CostPerUnit = index < costPerUnit.Count ? costPerUnit[index] : 0m,
                    ExpirationDate = index < expirationDate.Count ? expirationDate[index] : null
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .ToList();

            if (rows.Count == 0)
            {
                TempData["Message"] = "At least one ingredient name is required.";
                return RedirectToAction("Index");
            }

            // Get user's branch context - managers create items for their branch only
            var userBranchId = User.GetBranchId();
            var isOwner = User.HasAllBranchAccess();
            if (!isOwner && string.IsNullOrWhiteSpace(userBranchId))
                return Forbid();

            var effectiveBranchId = isOwner && !string.IsNullOrWhiteSpace(branchFilter)
                ? branchFilter.Trim()
                : userBranchId;

            if (isOwner && string.IsNullOrWhiteSpace(effectiveBranchId))
            {
                TempData["Message"] = "Choose a branch before adding ingredients.";
                return RedirectToAction("Index");
            }
            if (isOwner && await _branchService.GetByIdAsync(effectiveBranchId!) == null)
            {
                TempData["Message"] = "Choose a valid branch before adding ingredients.";
                return RedirectToAction("Index");
            }

            var allItems = await _ingredients.GetAllByBranchAsync(effectiveBranchId);
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
                    ReorderLevel = Math.Max(0, row.ReorderLevel),
                    CostPerUnit = Math.Max(0m, row.CostPerUnit),
                    ExpirationDate = row.ExpirationDate,
                    BranchId = effectiveBranchId ?? string.Empty
                };

                await _ingredients.AddAsync(newItem);
                await _menuItems.SyncAvailabilityForIngredientAsync(newItem.Id);
            }
            await _menuItems.SeedRecipesFromMenuItemNamesAsync();

            TempData["Message"] = rows.Count == 1
                ? "Ingredient added!"
                : $"{rows.Count} ingredients added!";
            return RedirectToAction("Index");
        }

        [HttpPost]
        public async Task<IActionResult> Edit(string id, string item, string ingredientCategory, string unit, decimal costPerUnit, DateTime? expirationDate)
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

            // Branch managers can only edit items from their branch or shared items
            var userBranchId = User.GetBranchId();
            var isOwner = User.HasAllBranchAccess();
            if (!isOwner && string.IsNullOrWhiteSpace(userBranchId))
                return Forbid();
            if (!isOwner &&
                (string.IsNullOrWhiteSpace(userBranchId) ||
                 string.IsNullOrWhiteSpace(existing.BranchId) ||
                 !string.Equals(existing.BranchId, userBranchId, StringComparison.OrdinalIgnoreCase)))
            {
                TempData["Message"] = "You can only edit items from your assigned branch.";
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
                var allItems = await _ingredients.GetAllByBranchAsync(userBranchId);
                if (allItems.Any(i => i.Id != id && string.Equals(i.Item, item.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    TempData["Message"] = $"An ingredient with the name '{item.Trim()}' already exists.";
                    return RedirectToAction("Index");
                }
            }

            existing.Item = item.Trim();
            existing.IngredientCategory = ingredientCategory;
            existing.Unit = string.IsNullOrWhiteSpace(unit) ? existing.Unit : unit.Trim();
            existing.CostPerUnit = Math.Max(0m, costPerUnit);
            existing.ExpirationDate = expirationDate;

            await _ingredients.UpdateAsync(existing);
            await _menuItems.SeedRecipesFromMenuItemNamesAsync();
            await _menuItems.SyncAvailabilityForIngredientAsync(existing.Id);
            TempData["Message"] = $"Ingredient '{existing.Item}' updated.";
            return RedirectToAction("Index");
        }

        [HttpGet]
        [Authorize(Roles = "Owner")]
        public async Task<IActionResult> Transfer()
        {
            ViewData["Title"] = "Transfer supplies";
            ViewBag.AllBranches = await _branchService.GetAllAsync();
            return View(await _ingredients.GetAllAsync());
        }

        [HttpPost]
        [Authorize(Roles = "Owner")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Transfer(string sourceIngredientId, string destinationBranchId, int quantity, string? note)
        {
            var result = await _ingredients.TransferAsync(
                sourceIngredientId,
                destinationBranchId,
                quantity,
                note,
                User.Identity?.Name ?? "Owner");
            await _menuItems.SyncAvailabilityForIngredientAsync(sourceIngredientId);

            TempData["Message"] = result.Message;
            return RedirectToAction("Transfer");
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

            // Branch managers can only restock items from their branch or shared items
            var userBranchId = User.GetBranchId();
            var isOwner = User.HasAllBranchAccess();
            if (!isOwner && string.IsNullOrWhiteSpace(userBranchId))
                return Forbid();
            if (!isOwner &&
                (string.IsNullOrWhiteSpace(userBranchId) ||
                 string.IsNullOrWhiteSpace(item.BranchId) ||
                 !string.Equals(item.BranchId, userBranchId, StringComparison.OrdinalIgnoreCase)))
            {
                TempData["Message"] = "You can only restock items from your assigned branch.";
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
            await _menuItems.SyncAvailabilityForIngredientAsync(item.Id);

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

            // Branch managers can only clear items from their branch or shared items
            var userBranchId = User.GetBranchId();
            var isOwner = User.HasAllBranchAccess();
            if (!isOwner && string.IsNullOrWhiteSpace(userBranchId))
                return Forbid();
            if (!isOwner &&
                (string.IsNullOrWhiteSpace(userBranchId) ||
                 string.IsNullOrWhiteSpace(item.BranchId) ||
                 !string.Equals(item.BranchId, userBranchId, StringComparison.OrdinalIgnoreCase)))
            {
                TempData["Message"] = "You can only clear items from your assigned branch.";
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
            await _menuItems.SyncAvailabilityForIngredientAsync(item.Id);

            TempData["Message"] = $"Stock for '{item.Item}' was cleared to 0.";
            return RedirectToAction("Index");
        }

        [HttpGet]
        public async Task<IActionResult> GetCategory(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName)) return Json(new { category = "" });
            var userBranchId = User.GetBranchId();
            if (!User.HasAllBranchAccess() && string.IsNullOrWhiteSpace(userBranchId))
                return Json(new { category = "" });
            var effectiveBranchId = User.HasAllBranchAccess() ? null : userBranchId;
            var item = (await _ingredients.GetAllByBranchAsync(effectiveBranchId)).FirstOrDefault(i => string.Equals(i.Item, itemName.Trim(), StringComparison.OrdinalIgnoreCase));
            return Json(new { category = item?.IngredientCategory ?? "" });
        }

        [HttpGet]
        public async Task<IActionResult> CheckDuplicate(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName)) return Json(new { exists = false });
            var userBranchId = User.GetBranchId();
            if (!User.HasAllBranchAccess() && string.IsNullOrWhiteSpace(userBranchId))
                return Json(new { exists = false });
            var effectiveBranchId = User.HasAllBranchAccess() ? null : userBranchId;
            var exists = (await _ingredients.GetAllByBranchAsync(effectiveBranchId)).Any(i => string.Equals(i.Item, itemName.Trim(), StringComparison.OrdinalIgnoreCase));
            return Json(new { exists });
        }
    }

    public class PrintStockSummary
    {
        public int BeginningStock { get; set; }
        public int DeliveredStock { get; set; }
        public int EndingStock { get; set; }
        public int UsedStockEstimate { get; set; }
    }
}
