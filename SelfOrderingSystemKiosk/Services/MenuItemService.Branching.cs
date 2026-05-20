using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using SelfOrderingSystemKiosk.Models;
using CustomerOrderItem = SelfOrderingSystemKiosk.Areas.Customer.Models.OrderItem;

namespace SelfOrderingSystemKiosk.Services
{
    public partial class MenuItemService
    {
        private static FilterDefinition<MenuItem> SharedBranchFilter()
        {
            var filter = Builders<MenuItem>.Filter;
            return filter.Or(
                filter.Eq("BranchId", BsonNull.Value),
                filter.Eq("BranchId", string.Empty),
                filter.Not(filter.Exists("BranchId")));
        }

        private static FilterDefinition<MenuItem> BranchRecordFilter(string branchId)
        {
            return Builders<MenuItem>.Filter.Eq("BranchId", branchId);
        }

        private async Task NormalizeLegacyBranchFieldAsync()
        {
            if (_branchFieldsNormalized)
                return;

            await _branchFieldNormalizeLock.WaitAsync();
            try
            {
                if (_branchFieldsNormalized)
                    return;

                var filter = Builders<BsonDocument>.Filter.Exists("branchId");
                var docs = await _rawCollection.Find(filter)
                    .Project(Builders<BsonDocument>.Projection
                        .Include("_id")
                        .Include("BranchId")
                        .Include("branchId"))
                    .ToListAsync();

                foreach (var doc in docs)
                {
                    var legacyBranchId = doc.GetValue("branchId", BsonNull.Value);
                    var hasCanonicalBranchId = doc.TryGetValue("BranchId", out var canonicalBranchId)
                        && !canonicalBranchId.IsBsonNull
                        && !string.IsNullOrWhiteSpace(canonicalBranchId.ToString());

                    var update = Builders<BsonDocument>.Update.Unset("branchId");
                    if (!hasCanonicalBranchId && !legacyBranchId.IsBsonNull)
                        update = update.Set("BranchId", legacyBranchId.IsString ? legacyBranchId.AsString.Trim() : legacyBranchId.ToString());

                    await _rawCollection.UpdateOneAsync(
                        Builders<BsonDocument>.Filter.Eq("_id", doc["_id"]),
                        update);
                }

                _branchFieldsNormalized = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to normalize legacy menu item branch fields.");
            }
            finally
            {
                _branchFieldNormalizeLock.Release();
            }
        }

        private static List<MenuItem> PreferBranchItems(IEnumerable<MenuItem> items, string? branchId)
        {
            var trimmedBranchId = branchId?.Trim();
            return items
                .Where(i => !string.IsNullOrWhiteSpace(i.Item))
                .GroupBy(i => i.Item.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g
                    .OrderByDescending(i => !string.IsNullOrWhiteSpace(trimmedBranchId) &&
                        string.Equals(i.BranchId, trimmedBranchId, StringComparison.OrdinalIgnoreCase))
                    .ThenBy(i => string.IsNullOrWhiteSpace(i.BranchId) ? 1 : 0)
                    .First())
                .ToList();
        }

        private static bool IsChickenWingMenu(string? name)
        {
            return !string.IsNullOrWhiteSpace(name)
                && name.Contains("wing", StringComparison.OrdinalIgnoreCase);
        }

        private static int ExtractChickenPieceCount(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return 0;

            var normalized = name.Replace("-", " ", StringComparison.Ordinal);
            var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < tokens.Length; i++)
            {
                if (!int.TryParse(tokens[i], out var count) || count <= 0)
                    continue;

                var next = i + 1 < tokens.Length ? tokens[i + 1] : "";
                var afterNext = i + 2 < tokens.Length ? tokens[i + 2] : "";
                if (next.StartsWith("piece", StringComparison.OrdinalIgnoreCase)
                    || next.StartsWith("pc", StringComparison.OrdinalIgnoreCase)
                    || afterNext.Contains("wing", StringComparison.OrdinalIgnoreCase))
                    return count;
            }

            return 0;
        }

        private static IEnumerable<string> ExtractSubmittedFlavors(string? submittedItemName)
        {
            if (string.IsNullOrWhiteSpace(submittedItemName))
                yield break;

            const string marker = "(Flavors:";
            var start = submittedItemName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                yield break;

            start += marker.Length;
            var end = submittedItemName.IndexOf(')', start);
            var raw = end >= 0 ? submittedItemName[start..end] : submittedItemName[start..];
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return part;
        }

        private static string? MapFlavorToSauceName(string? flavor)
        {
            if (string.IsNullOrWhiteSpace(flavor))
                return null;

            var norm = flavor.Trim();
            if (norm.Contains("mayora sriracha original", StringComparison.OrdinalIgnoreCase))
                return "Mayora Sriracha Original";
            if (norm.Contains("sriracha mayo", StringComparison.OrdinalIgnoreCase) || norm.Contains("garlic mayo", StringComparison.OrdinalIgnoreCase))
                return "Mayo Garlic";
            if (norm.Contains("sriracha honey", StringComparison.OrdinalIgnoreCase))
                return "Honey";
            if (norm.Contains("chief parm", StringComparison.OrdinalIgnoreCase))
                return "Chief Parm Base";
            if (norm.Contains("colonel mustard", StringComparison.OrdinalIgnoreCase))
                return "Colonel Mustard";
            if (norm.Contains("konsi honey", StringComparison.OrdinalIgnoreCase))
                return "Konsi Honeysoy";
            if (norm.Contains("soy garlic", StringComparison.OrdinalIgnoreCase))
                return "Mayo Garlic";
            if (norm.Contains("soy spicy", StringComparison.OrdinalIgnoreCase))
                return "Hot Sauce";
            if (norm.Contains("vice thai", StringComparison.OrdinalIgnoreCase))
                return "Vice Thai";
            if (norm.Contains("teriyaki", StringComparison.OrdinalIgnoreCase))
                return "Teriyaki Sen";
            if (norm.Contains("salted egg", StringComparison.OrdinalIgnoreCase))
                return "Salted Egg";
            if (norm.Contains("buffalo", StringComparison.OrdinalIgnoreCase))
                return "Hot Sauce";
            return null;
        }

        public async Task<bool> IncreaseStockAsync(string menuItemId, int quantityAdded, string referenceType, string? referenceId, string? note = null)
        {
            _logger.LogWarning("Menu item stock adjustment ignored for {MenuItemId}; stock is tracked through ingredients.", menuItemId);
            await Task.CompletedTask;
            return false;
        }

        public async Task RecordAdjustmentAsync(string menuItemId, string itemName, int stockBefore, int stockAfter, string note)
        {
            _logger.LogWarning("Menu item stock adjustment ignored for {MenuItem}; stock is tracked through ingredients.", itemName);
            await Task.CompletedTask;
        }

        // ====================
        // Branch Filtering Methods
        // ====================

        /// <summary>
        /// Gets all menu items filtered by branch (empty branchId returns items with empty BranchId or matching branch)
        /// </summary>
        public async Task<List<MenuItem>> GetAllByBranchAsync(string? branchId)
        {
            await NormalizeLegacyBranchFieldAsync();

            var validItemFilter = Builders<MenuItem>.Filter.And(
                Builders<MenuItem>.Filter.Ne(x => x.Item, (string)null!),
                Builders<MenuItem>.Filter.Ne(x => x.Item, ""));

            if (string.IsNullOrEmpty(branchId))
            {
                var allItems = await _collection.Find(validItemFilter).ToListAsync();
                return allItems
                    .OrderBy(i => IsAvailableForCustomerMenu(i.Availability) ? 0 : 1)
                    .ThenByDescending(i => i.MenuOrder)
                    .ThenBy(i => i.Item ?? "", StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            // Return items for specific branch OR shared items (empty BranchId)
            var branchFilter = Builders<MenuItem>.Filter.And(
                validItemFilter,
                Builders<MenuItem>.Filter.Or(
                    BranchRecordFilter(branchId),
                    SharedBranchFilter()
                )
            );
            var branchItems = await _collection.Find(branchFilter).ToListAsync();
            branchItems = PreferBranchItems(branchItems, branchId);
            return branchItems
                .OrderBy(i => IsAvailableForCustomerMenu(i.Availability) ? 0 : 1)
                .ThenByDescending(i => i.MenuOrder)
                .ThenBy(i => i.Item ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Gets available menu items filtered by branch
        /// </summary>
        public async Task<List<MenuItem>> GetAvailableByBranchAsync(string? branchId)
        {
            await NormalizeLegacyBranchFieldAsync();

            var availableOrUnset = Builders<MenuItem>.Filter.Or(
                Builders<MenuItem>.Filter.Eq(x => x.Availability, (string)null!),
                Builders<MenuItem>.Filter.Eq(x => x.Availability, ""),
                Builders<MenuItem>.Filter.Eq(x => x.Availability, "Available"),
                Builders<MenuItem>.Filter.Not(Builders<MenuItem>.Filter.Exists(x => x.Availability)));

            var validItemFilter = Builders<MenuItem>.Filter.And(
                Builders<MenuItem>.Filter.Ne(x => x.Item, (string)null!),
                Builders<MenuItem>.Filter.Ne(x => x.Item, ""));

            if (string.IsNullOrEmpty(branchId))
            {
                var allFilter = Builders<MenuItem>.Filter.And(validItemFilter, availableOrUnset);
                var allItems = await _collection.Find(allFilter).ToListAsync();
                allItems = await FilterCurrentlyStockedAsync(allItems);
                return allItems
                    .OrderByDescending(i => i.MenuOrder)
                    .ThenBy(i => i.Item ?? "", StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            // Return items for specific branch OR shared items (empty BranchId)
            var branchFilter = Builders<MenuItem>.Filter.And(
                validItemFilter,
                availableOrUnset,
                Builders<MenuItem>.Filter.Or(
                    BranchRecordFilter(branchId),
                    SharedBranchFilter()
                )
            );
            var branchItems = await _collection.Find(branchFilter).ToListAsync();
            branchItems = await FilterCurrentlyStockedAsync(branchItems);
            branchItems = PreferBranchItems(branchItems, branchId);
            return branchItems
                .OrderByDescending(i => i.MenuOrder)
                .ThenBy(i => i.Item ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Gets menu item count by branch
        /// </summary>
        public async Task<long> GetCountByBranchAsync(string? branchId)
        {
            await NormalizeLegacyBranchFieldAsync();

            var validItemFilter = Builders<MenuItem>.Filter.And(
                Builders<MenuItem>.Filter.Ne(x => x.Item, (string)null!),
                Builders<MenuItem>.Filter.Ne(x => x.Item, ""));

            if (string.IsNullOrEmpty(branchId))
            {
                return await _collection.CountDocumentsAsync(validItemFilter);
            }

            var filter = Builders<MenuItem>.Filter.And(
                validItemFilter,
                Builders<MenuItem>.Filter.Or(
                    BranchRecordFilter(branchId),
                    SharedBranchFilter()
                )
            );
            return await _collection.CountDocumentsAsync(filter);
        }
    }
}
