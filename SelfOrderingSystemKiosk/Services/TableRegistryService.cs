using MongoDB.Driver;
using SelfOrderingSystemKiosk.Models;

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

        public async Task UpsertAsync(string tableNumber, string? floor = null)
        {
            if (string.IsNullOrWhiteSpace(tableNumber))
                return;

            var table = tableNumber.Trim();
            var id = BuildId(table);
            var now = DateTime.UtcNow;
            var update = Builders<RestaurantTable>.Update
                .SetOnInsert(t => t.Id, id)
                .SetOnInsert(t => t.TableNumber, table)
                .SetOnInsert(t => t.CreatedAtUtc, now)
                .Set(t => t.UpdatedAtUtc, now);

            if (!string.IsNullOrWhiteSpace(floor))
                update = update.Set(t => t.Floor, floor.Trim());

            await _tables.UpdateOneAsync(
                t => t.Id == id,
                update,
                new UpdateOptions { IsUpsert = true });
        }

        public async Task UpsertManyAsync(IEnumerable<string> tableNumbers, string? floor = null)
        {
            foreach (var table in tableNumbers ?? Enumerable.Empty<string>())
                await UpsertAsync(table, floor);
        }

        private static string BuildId(string tableNumber)
        {
            return tableNumber.Trim().ToUpperInvariant();
        }
    }
}
