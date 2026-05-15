using MongoDB.Driver;
using SelfOrderingSystemKiosk.Areas.Customer.Models;

namespace SelfOrderingSystemKiosk.Services
{
    public class UnlimitedRefillService
    {
        private const string NewStatus = "New";
        private const string ServedStatus = "Served";
        private readonly IMongoCollection<UnlimitedRefill> _refills;

        public UnlimitedRefillService(Models.KitchenDatabase db)
        {
            _refills = db.Database.GetCollection<UnlimitedRefill>("UnlimitedRefills");
        }

        public async Task CreateAsync(UnlimitedRefill refill)
        {
            refill.RequestedAtUtc = DateTime.UtcNow;
            refill.Status = NewStatus;
            await _refills.InsertOneAsync(refill);
        }

        public async Task<UnlimitedRefill?> GetByIdAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            return await _refills.Find(r => r.Id == id).FirstOrDefaultAsync();
        }

        public async Task<List<UnlimitedRefill>> GetActiveForKitchenAsync(string? branchId = null)
        {
            var filters = new List<FilterDefinition<UnlimitedRefill>>
            {
                Builders<UnlimitedRefill>.Filter.Eq(r => r.Status, NewStatus)
            };

            if (!string.IsNullOrWhiteSpace(branchId))
                filters.Add(Builders<UnlimitedRefill>.Filter.Eq(r => r.BranchId, branchId.Trim()));

            return await _refills
                .Find(Builders<UnlimitedRefill>.Filter.And(filters))
                .SortBy(r => r.RequestedAtUtc)
                .ToListAsync();
        }

        public async Task<bool> MarkServedIfNewAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return false;

            var filter = Builders<UnlimitedRefill>.Filter.And(
                Builders<UnlimitedRefill>.Filter.Eq(r => r.Id, id),
                Builders<UnlimitedRefill>.Filter.Eq(r => r.Status, NewStatus));
            var update = Builders<UnlimitedRefill>.Update
                .Set(r => r.Status, ServedStatus)
                .Set(r => r.ServedAtUtc, DateTime.UtcNow);

            var result = await _refills.UpdateOneAsync(filter, update);
            return result.ModifiedCount > 0;
        }
    }
}
