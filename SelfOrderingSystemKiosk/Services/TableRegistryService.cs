using MongoDB.Driver;
using SelfOrderingSystemKiosk.Models;
using System.Security.Cryptography;

namespace SelfOrderingSystemKiosk.Services
{
    public class TableRegistryService
    {
        private readonly IMongoCollection<RestaurantTable> _tables;

        public TableRegistryService(Models.KitchenDatabase db)
        {
            _tables = db.Database.GetCollection<RestaurantTable>("RestaurantTables");
        }

        public async Task<List<RestaurantTable>> GetAllAsync()
        {
            return await _tables.Find(_ => true).ToListAsync();
        }

        public async Task<RestaurantTable?> GetByTableNumberAsync(string tableNumber, string? branchId = null)
        {
            if (string.IsNullOrWhiteSpace(tableNumber))
                return null;

            var id = BuildId(tableNumber, branchId);
            var table = await _tables.Find(t => t.Id == id).FirstOrDefaultAsync();
            if (table != null)
                return table;

            if (string.IsNullOrWhiteSpace(branchId))
            {
                var matches = await _tables
                    .Find(t => t.TableNumber == tableNumber.Trim())
                    .Limit(2)
                    .ToListAsync();

                return matches.Count == 1 ? matches[0] : null;
            }

            var legacyId = BuildId(tableNumber, null);
            return await _tables.Find(t => t.Id == legacyId).FirstOrDefaultAsync();
        }

        public async Task<RestaurantTable?> GetByQrTokenAsync(string qrToken)
        {
            if (string.IsNullOrWhiteSpace(qrToken))
                return null;

            return await _tables.Find(t => t.QrToken == qrToken.Trim()).FirstOrDefaultAsync();
        }

        public async Task<RestaurantTable?> UpsertAsync(string tableNumber, string? floor = null, string? branchId = null)
        {
            if (string.IsNullOrWhiteSpace(tableNumber))
                return null;

            var table = tableNumber.Trim();
            var id = BuildId(table, branchId);
            var now = DateTime.UtcNow;
            var existing = await _tables.Find(t => t.Id == id).FirstOrDefaultAsync();
            var qrToken = string.IsNullOrWhiteSpace(existing?.QrToken)
                ? await CreateUniqueQrTokenAsync()
                : existing.QrToken;

            var update = Builders<RestaurantTable>.Update
                .SetOnInsert(t => t.Id, id)
                .SetOnInsert(t => t.TableNumber, table)
                .SetOnInsert(t => t.CreatedAtUtc, now)
                .Set(t => t.QrToken, qrToken)
                .Set(t => t.UpdatedAtUtc, now);

            if (!string.IsNullOrWhiteSpace(floor))
                update = update.Set(t => t.Floor, floor.Trim());
            if (!string.IsNullOrWhiteSpace(branchId))
                update = update.Set(t => t.BranchId, branchId.Trim());

            await _tables.UpdateOneAsync(
                t => t.Id == id,
                update,
                new UpdateOptions { IsUpsert = true });

            return await _tables.Find(t => t.Id == id).FirstOrDefaultAsync();
        }

        public async Task UpsertManyAsync(IEnumerable<string> tableNumbers, string? floor = null, string? branchId = null)
        {
            foreach (var table in tableNumbers ?? Enumerable.Empty<string>())
                await UpsertAsync(table, floor, branchId);
        }

        private async Task<string> CreateUniqueQrTokenAsync()
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var token = CreateQrToken();
                var exists = await _tables.Find(t => t.QrToken == token).AnyAsync();
                if (!exists)
                    return token;
            }

            return CreateQrToken();
        }

        private static string CreateQrToken()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static string BuildId(string tableNumber, string? branchId = null)
        {
            var tableKey = tableNumber.Trim().ToUpperInvariant();
            return string.IsNullOrWhiteSpace(branchId)
                ? tableKey
                : $"{branchId.Trim().ToUpperInvariant()}:{tableKey}";
        }
    }
}
