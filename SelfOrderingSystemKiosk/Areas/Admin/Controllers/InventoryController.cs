using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;
using SelfOrderingSystemKiosk.Areas.Admin.Models;
using System.Text.Json.Serialization;

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
        private readonly DeliveryImportService _deliveryImports;
        private readonly QrCodeService _qrCodes;

        public InventoryController(IngredientStockService ingredients, MenuItemService menuItems, IngredientCategoryRegistry ingredientCategories, BranchService branchService, StockMovementService stockMovements, DeliveryImportService deliveryImports, QrCodeService qrCodes)
        {
            _ingredients = ingredients;
            _menuItems = menuItems;
            _ingredientCategories = ingredientCategories;
            _branchService = branchService;
            _stockMovements = stockMovements;
            _deliveryImports = deliveryImports;
            _qrCodes = qrCodes;
        }

        public async Task<IActionResult> Index(string? categoryFilter = null, string? branchFilter = null, string? expiryFilter = null, DateTime? expiryUntil = null, string? actionView = null, bool print = false)
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

            var menuItems = await _menuItems.GetAllByBranchAsync(effectiveBranchId);
            var usageByIngredient = BuildInventoryUsageImpact(all, menuItems);
            var items = all;
            if (ViewBag.CategoryFilter != "all")
                items = all.Where(i => string.Equals(i.IngredientCategory, categoryFilter, StringComparison.Ordinal)).ToList();
            var today = AppClock.LocalNow.Date;
            var expiryMaxDate = today.AddDays(14);
            var selectedExpiryFilter = string.IsNullOrWhiteSpace(expiryFilter)
                ? "all"
                : expiryFilter.Trim().ToLowerInvariant();
            if (selectedExpiryFilter == "near")
                selectedExpiryFilter = "7days";

            DateTime? expiryEndDate = selectedExpiryFilter switch
            {
                "3days" => today.AddDays(3),
                "7days" or "week" => today.AddDays(7),
                "14days" or "2weeks" => expiryMaxDate,
                "custom" => expiryUntil.HasValue
                    ? expiryUntil.Value.Date < today
                        ? today
                        : expiryUntil.Value.Date > expiryMaxDate
                            ? expiryMaxDate
                            : expiryUntil.Value.Date
                    : expiryMaxDate,
                _ => null
            };

            ViewBag.ExpiryFilter = selectedExpiryFilter;
            ViewBag.ExpiryUntil = expiryEndDate;
            ViewBag.ExpiryToday = today;
            ViewBag.ExpiryMaxDate = expiryMaxDate;

            if (selectedExpiryFilter == "expired")
                items = items.Where(i => i.ExpirationDate.HasValue && i.ExpirationDate.Value.Date < today).ToList();
            else if (expiryEndDate.HasValue)
                items = items.Where(i => i.ExpirationDate.HasValue && i.ExpirationDate.Value.Date >= today && i.ExpirationDate.Value.Date <= expiryEndDate.Value).ToList();

            var selectedActionView = NormalizeInventoryActionView(actionView);
            var actionCounts = BuildInventoryActionCounts(items, usageByIngredient, today);
            items = ApplyInventoryActionView(items, usageByIngredient, selectedActionView, today);

            var visibleItems = items ?? new List<IngredientItem>();
            ViewBag.ItemCount = visibleItems.Count;
            ViewBag.IngredientCategories = IngredientCategoryRegistry.All;
            ViewBag.IsOwner = isOwner;
            ViewBag.AllBranches = allBranches;
            ViewBag.AutoPrint = print;
            ViewBag.ActionView = selectedActionView;
            ViewBag.InventoryUsageByIngredient = usageByIngredient;
            ViewBag.InventoryActionCounts = actionCounts;
            ViewBag.ImportIngredientOptions = all;
            ViewBag.PrintStockStats = await BuildPrintStockStatsAsync(visibleItems, effectiveBranchId);
            return View(visibleItems);
        }

        private async Task<Dictionary<string, PrintStockSummary>> BuildPrintStockStatsAsync(List<IngredientItem> items, string? branchId)
        {
            var (startUtc, endUtc) = AppClock.LocalDateRange(AppClock.LocalNow.Date);
            var movements = await _stockMovements.GetForInventoryItemsAsync(items.Select(i => i.Id), startUtc, endUtc, branchId);
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

        private static Dictionary<string, InventoryUsageImpact> BuildInventoryUsageImpact(List<IngredientItem> ingredients, List<MenuItem> menuItems)
        {
            var impactByIngredient = ingredients
                .Where(i => !string.IsNullOrWhiteSpace(i.Id))
                .GroupBy(i => i.Id.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => new InventoryUsageImpact { IngredientId = g.Key },
                    StringComparer.OrdinalIgnoreCase);

            var ingredientById = ingredients
                .Where(i => !string.IsNullOrWhiteSpace(i.Id))
                .ToDictionary(i => i.Id.Trim(), StringComparer.OrdinalIgnoreCase);

            foreach (var menuItem in menuItems.Where(m => !string.Equals(m.Category, "Unavailable", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var line in menuItem.Recipe ?? new List<MenuRecipeLine>())
                {
                    if (string.IsNullOrWhiteSpace(line.IngredientId) || line.QuantityPerUnit <= 0)
                        continue;

                    var ingredientId = line.IngredientId.Trim();
                    if (!impactByIngredient.TryGetValue(ingredientId, out var impact))
                        continue;

                    impact.UsedBy.Add(new InventoryMenuUsage
                    {
                        MenuItemId = menuItem.Id ?? string.Empty,
                        MenuItemName = menuItem.Item ?? "Unnamed menu item",
                        MenuCategory = menuItem.Category ?? string.Empty,
                        QuantityPerUnit = line.QuantityPerUnit,
                        Unit = ingredientById.TryGetValue(ingredientId, out var ingredient) ? ingredient.Unit ?? string.Empty : string.Empty
                    });
                }
            }

            foreach (var impact in impactByIngredient.Values)
            {
                ingredientById.TryGetValue(impact.IngredientId, out var ingredient);
                if (ingredient?.CurrentStock <= 0 && impact.AffectedMenuItemCount > 0)
                {
                    impact.BlockingMenuItemCount = impact.AffectedMenuItemCount;
                    impact.ImpactLabel = $"Blocking {impact.BlockingMenuItemCount} item(s)";
                    impact.ImpactLevel = "blocked";
                    impact.ImpactSortValue = 3000 + impact.BlockingMenuItemCount;
                }
                else if (impact.AffectedMenuItemCount > 0)
                {
                    impact.ImpactLabel = $"Affects {impact.AffectedMenuItemCount} item(s)";
                    impact.ImpactLevel = "linked";
                    impact.ImpactSortValue = 2000 + impact.AffectedMenuItemCount;
                }
                else
                {
                    impact.ImpactLabel = "No recipe link";
                    impact.ImpactLevel = "none";
                    impact.ImpactSortValue = 1000;
                }
            }

            return impactByIngredient;
        }

        private static string NormalizeInventoryActionView(string? actionView)
        {
            var normalized = string.IsNullOrWhiteSpace(actionView)
                ? "needs"
                : actionView.Trim().ToLowerInvariant();

            return normalized is "needs" or "low" or "expiring" or "unused" or "recipe" or "all"
                ? normalized
                : "needs";
        }

        private static List<IngredientItem> ApplyInventoryActionView(List<IngredientItem> items, Dictionary<string, InventoryUsageImpact> usageByIngredient, string actionView, DateTime today)
        {
            return actionView switch
            {
                "needs" => items.Where(i => NeedsInventoryAction(i, usageByIngredient, today)).ToList(),
                "low" => items.Where(IsLowStock).ToList(),
                "expiring" => items.Where(i => IsExpiredOrExpiringSoon(i, today)).ToList(),
                "unused" => items.Where(i => GetUsage(i, usageByIngredient).AffectedMenuItemCount == 0).ToList(),
                "recipe" => items.Where(i => GetUsage(i, usageByIngredient).AffectedMenuItemCount > 0).ToList(),
                _ => items
            };
        }

        private static Dictionary<string, int> BuildInventoryActionCounts(List<IngredientItem> items, Dictionary<string, InventoryUsageImpact> usageByIngredient, DateTime today)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["needs"] = items.Count(i => NeedsInventoryAction(i, usageByIngredient, today)),
                ["low"] = items.Count(IsLowStock),
                ["expiring"] = items.Count(i => IsExpiredOrExpiringSoon(i, today)),
                ["unused"] = items.Count(i => GetUsage(i, usageByIngredient).AffectedMenuItemCount == 0),
                ["recipe"] = items.Count(i => GetUsage(i, usageByIngredient).AffectedMenuItemCount > 0),
                ["all"] = items.Count
            };
        }

        private static bool NeedsInventoryAction(IngredientItem item, Dictionary<string, InventoryUsageImpact> usageByIngredient, DateTime today)
        {
            var usage = GetUsage(item, usageByIngredient);
            return item.CurrentStock == 0
                || IsLowStock(item)
                || IsExpiredOrExpiringSoon(item, today)
                || usage.AffectedMenuItemCount == 0;
        }

        private static bool IsLowStock(IngredientItem item)
            => item.CurrentStock <= item.ReorderLevel;

        private static bool IsExpiredOrExpiringSoon(IngredientItem item, DateTime today)
            => item.ExpirationDate.HasValue && item.ExpirationDate.Value.Date <= today.AddDays(14);

        private static InventoryUsageImpact GetUsage(IngredientItem item, Dictionary<string, InventoryUsageImpact> usageByIngredient)
        {
            if (!string.IsNullOrWhiteSpace(item.Id) && usageByIngredient.TryGetValue(item.Id.Trim(), out var usage))
                return usage;

            return new InventoryUsageImpact { IngredientId = item.Id ?? string.Empty };
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> StartDeliveryImport(string? branchFilter = null)
        {
            var effectiveBranchId = await ResolveEffectiveInventoryBranchIdAsync(branchFilter);
            if (string.IsNullOrWhiteSpace(effectiveBranchId))
                return Json(new { success = false, message = "Choose a branch before starting phone scan." });

            var session = await _deliveryImports.CreateAsync(effectiveBranchId, User.Identity?.Name ?? "Admin");
            var scanUrl = Url.Action("ScanDeliveryImport", "Inventory", new { area = "Admin", token = session.Token }, Request.Scheme)
                ?? string.Empty;
            var qrBytes = _qrCodes.GetPngBytes(scanUrl, 8);
            var qrDataUrl = $"data:image/png;base64,{Convert.ToBase64String(qrBytes)}";

            return Json(new
            {
                success = true,
                token = session.Token,
                scanUrl,
                qrDataUrl,
                expiresAt = AppClock.ToLocal(session.ExpiresAtUtc).ToString("h:mm tt")
            });
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> ScanDeliveryImport(string token)
        {
            var session = await _deliveryImports.GetActiveByTokenAsync(token);
            if (session == null)
                return Content("This delivery scan link is invalid or expired.");

            ViewBag.Token = token;
            return View("ScanDeliveryImport");
        }

        [HttpPost]
        [AllowAnonymous]
        [IgnoreAntiforgeryToken]
        [EnableRateLimiting("delivery-import-upload")]
        [RequestSizeLimit(5 * 1024 * 1024)]
        [RequestFormLimits(MultipartBodyLengthLimit = 5 * 1024 * 1024, ValueLengthLimit = 64 * 1024)]
        public async Task<IActionResult> UploadDeliveryImport(string token, string? rawText, IFormFile? sheetImage)
        {
            var session = await _deliveryImports.GetActiveByTokenAsync(token);
            if (session == null)
                return Json(new { success = false, message = "This scan session is invalid or expired." });

            if (sheetImage == null && string.IsNullOrWhiteSpace(rawText))
                return Json(new { success = false, message = "Upload a sheet photo or recognized text." });
            if (sheetImage != null)
            {
                if (sheetImage.Length > 5 * 1024 * 1024)
                    return Json(new { success = false, message = "The sheet photo is too large. Use an image under 5 MB." });
                if (!IsAllowedDeliveryImageContentType(sheetImage.ContentType))
                    return Json(new { success = false, message = "Upload a JPG, PNG, HEIC, or WebP image." });
            }

            rawText = (rawText ?? string.Empty).Trim();
            if (rawText.Length > 64 * 1024)
                return Json(new { success = false, message = "The recognized text is too large. Shorten it and try again." });

            var result = await _deliveryImports.TrySaveUploadedTextAsync(
                token,
                rawText,
                sheetImage?.ContentType,
                Request.Headers["User-Agent"].ToString(),
                HttpContext.Connection.RemoteIpAddress?.ToString());
            return Json(new { success = result.Success, message = result.Message });
        }

        [HttpGet]
        public async Task<IActionResult> DeliveryImportStatus(string token)
        {
            var session = await _deliveryImports.GetByTokenAsync(token);
            if (session == null)
                return Json(new { success = false, message = "Import session was not found." });

            if (!CanAccessInventoryBranch(session.BranchId))
                return Forbid();

            return Json(new
            {
                success = true,
                status = session.ExpiresAtUtc < DateTime.UtcNow ? "Expired" : session.Status,
                uploaded = session.UploadedAtUtc.HasValue,
                rows = session.Rows.Select(r => new
                {
                    itemName = r.ItemName,
                    quantity = r.Quantity,
                    unit = r.Unit,
                    matchedIngredientId = r.MatchedIngredientId,
                    matchedIngredientName = r.MatchedIngredientName,
                    confidence = r.Confidence
                })
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmDeliveryImport([FromBody] ConfirmDeliveryImportRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Token))
                return Json(new { success = false, message = "Import session is required." });

            var session = await _deliveryImports.GetByTokenAsync(request.Token);
            if (session == null || session.ExpiresAtUtc < DateTime.UtcNow)
                return Json(new { success = false, message = "Import session is invalid or expired." });
            if (!CanAccessInventoryBranch(session.BranchId))
                return Forbid();

            var rows = request.Rows?
                .Where(r => !string.IsNullOrWhiteSpace(r.IngredientId) && r.Quantity > 0)
                .ToList() ?? new List<ConfirmDeliveryImportRow>();
            if (!rows.Any())
                return Json(new { success = false, message = "No valid rows to import." });

            var imported = 0;
            foreach (var row in rows)
            {
                var item = await _ingredients.GetByIdAsync(row.IngredientId);
                if (item == null || !CanAccessInventoryBranch(item.BranchId))
                    continue;

                var note = string.IsNullOrWhiteSpace(row.Note)
                    ? $"Delivery import {session.Id}"
                    : $"Delivery import {session.Id}: {row.Note.Trim()}";
                var ok = await _ingredients.IncreaseStockAsync(item.Id, row.Quantity, "DeliveryImport", session.Id, note);
                if (ok)
                {
                    imported++;
                    await _menuItems.SyncAvailabilityForIngredientAsync(item.Id);
                }
            }

            await _deliveryImports.MarkConfirmedAsync(session.Token);
            return Json(new { success = true, message = $"Imported {imported} delivery row(s)." });
        }

        private async Task<string?> ResolveEffectiveInventoryBranchIdAsync(string? branchFilter)
        {
            var userBranchId = User.GetBranchId();
            var isOwner = User.HasAllBranchAccess();
            if (!isOwner)
                return string.IsNullOrWhiteSpace(userBranchId) ? null : userBranchId.Trim();

            if (string.IsNullOrWhiteSpace(branchFilter) || branchFilter == "all")
                return null;

            var branch = await _branchService.GetByIdAsync(branchFilter.Trim());
            return branch?.Id;
        }

        private bool CanAccessInventoryBranch(string? branchId)
        {
            if (User.HasAllBranchAccess())
                return true;

            var userBranchId = User.GetBranchId();
            return !string.IsNullOrWhiteSpace(userBranchId)
                && !string.IsNullOrWhiteSpace(branchId)
                && string.Equals(userBranchId.Trim(), branchId.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAllowedDeliveryImageContentType(string? contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
                return false;

            return contentType.Trim().ToLowerInvariant() switch
            {
                "image/jpeg" or "image/jpg" or "image/png" or "image/webp" or "image/heic" or "image/heif" => true,
                _ => false
            };
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
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
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, string item, string ingredientCategory, string unit, decimal costPerUnit, DateTime? expirationDate, string? branchFilter = null, string? actionView = null)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(item))
            {
                TempData["Message"] = "Invalid data.";
                return RedirectToInventoryIndex(branchFilter, actionView);
            }

            var existing = await _ingredients.GetByIdAsync(id);
            if (existing == null)
            {
                TempData["Message"] = "Ingredient not found.";
                return RedirectToInventoryIndex(branchFilter, actionView);
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
                return RedirectToInventoryIndex(branchFilter, actionView);
            }

            if (!_ingredientCategories.IsValid(ingredientCategory))
            {
                TempData["Message"] = "Invalid ingredient category.";
                return RedirectToInventoryIndex(branchFilter, actionView);
            }

            // Check for duplicate name if name changed
            if (!string.Equals(existing.Item, item.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var allItems = await _ingredients.GetAllByBranchAsync(userBranchId);
                if (allItems.Any(i => i.Id != id && string.Equals(i.Item, item.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    TempData["Message"] = $"An ingredient with the name '{item.Trim()}' already exists.";
                    return RedirectToInventoryIndex(branchFilter, actionView);
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
            return RedirectToInventoryIndex(branchFilter, actionView);
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
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Restock(string id, int amount, string? batchNote = null, string? branchFilter = null, string? actionView = null)
        {
            var item = await _ingredients.GetByIdAsync(id);
            if (item == null)
            {
                TempData["Message"] = "Ingredient not found.";
                return RedirectToInventoryIndex(branchFilter, actionView);
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
                return RedirectToInventoryIndex(branchFilter, actionView);
            }

            if (amount <= 0)
            {
                TempData["Message"] = "Invalid restock amount.";
                return RedirectToInventoryIndex(branchFilter, actionView);
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
            return RedirectToInventoryIndex(branchFilter, actionView);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ClearStock(string id, string? branchFilter = null, string? actionView = null)
        {
            var item = await _ingredients.GetByIdAsync(id);
            if (item == null)
            {
                TempData["Message"] = "Ingredient not found.";
                return RedirectToInventoryIndex(branchFilter, actionView);
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
                return RedirectToInventoryIndex(branchFilter, actionView);
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
            return RedirectToInventoryIndex(branchFilter, actionView);
        }

        private RedirectToActionResult RedirectToInventoryIndex(string? branchFilter, string? actionView)
        {
            return RedirectToAction("Index", new
            {
                branchFilter,
                actionView = NormalizeInventoryActionView(actionView)
            });
        }

        [HttpGet]
        public async Task<IActionResult> GetCategory(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName)) return Json(new { category = "" });
            var userBranchId = User.GetBranchId();
            if (!User.HasAllBranchAccess() && string.IsNullOrWhiteSpace(userBranchId))
                return Json(new { category = "" });
            var effectiveBranchId = User.HasAllBranchAccess() ? null : userBranchId;
            var category = await _ingredients.GetCategoryByNameAsync(itemName, effectiveBranchId);
            return Json(new { category });
        }

        [HttpGet]
        public async Task<IActionResult> CheckDuplicate(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName)) return Json(new { exists = false });
            var userBranchId = User.GetBranchId();
            if (!User.HasAllBranchAccess() && string.IsNullOrWhiteSpace(userBranchId))
                return Json(new { exists = false });
            var effectiveBranchId = User.HasAllBranchAccess() ? null : userBranchId;
            var exists = await _ingredients.ExistsByNameAsync(itemName, effectiveBranchId);
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

    public class ConfirmDeliveryImportRequest
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = string.Empty;

        [JsonPropertyName("rows")]
        public List<ConfirmDeliveryImportRow> Rows { get; set; } = new();
    }

    public class ConfirmDeliveryImportRow
    {
        [JsonPropertyName("ingredientId")]
        public string IngredientId { get; set; } = string.Empty;

        [JsonPropertyName("quantity")]
        public int Quantity { get; set; }

        [JsonPropertyName("note")]
        public string? Note { get; set; }
    }
}
