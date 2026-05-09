using MongoDB.Driver;
using SelfOrderingSystemKiosk.Areas.Admin.Models;
using SelfOrderingSystemKiosk.Models;
using System.Text.RegularExpressions;

namespace SelfOrderingSystemKiosk.Services
{
    public class BranchService
    {
        private readonly IMongoCollection<Branch> _branches;

        public BranchService(KitchenDatabase db)
        {
            _branches = db.Database.GetCollection<Branch>("Branches");
        }

        public async Task<List<Branch>> GetAllAsync()
        {
            return await _branches.Find(_ => true).SortBy(b => b.BranchName).ToListAsync();
        }

        public async Task<Branch?> GetByIdAsync(string id)
        {
            return await _branches.Find(b => b.Id == id).FirstOrDefaultAsync();
        }

        public async Task<Branch?> GetByCodeAsync(string branchCode)
        {
            if (string.IsNullOrWhiteSpace(branchCode))
                return null;

            var code = branchCode.Trim();
            var regex = new MongoDB.Bson.BsonRegularExpression($"^{Regex.Escape(code)}$", "i");
            return await _branches
                .Find(Builders<Branch>.Filter.Regex(b => b.BranchCode, regex))
                .FirstOrDefaultAsync();
        }

        public async Task<List<Branch>> GetActiveBranchesAsync()
        {
            return await _branches.Find(b => b.IsActive).SortBy(b => b.BranchName).ToListAsync();
        }

        public async Task CreateAsync(Branch branch)
        {
            branch.CreatedAt = DateTime.UtcNow;
            await _branches.InsertOneAsync(branch);
        }

        public async Task UpdateAsync(Branch branch)
        {
            branch.UpdatedAt = DateTime.UtcNow;
            await _branches.ReplaceOneAsync(b => b.Id == branch.Id, branch);
        }

        public async Task DeleteAsync(string id)
        {
            await _branches.DeleteOneAsync(b => b.Id == id);
        }

        public async Task<bool> IsBranchCodeUniqueAsync(string branchCode, string? excludeId = null)
        {
            var filter = Builders<Branch>.Filter.Eq(b => b.BranchCode, branchCode);
            if (!string.IsNullOrEmpty(excludeId))
            {
                filter = filter & Builders<Branch>.Filter.Ne(b => b.Id, excludeId);
            }
            var count = await _branches.CountDocumentsAsync(filter);
            return count == 0;
        }

        public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
        {
            await _branches.Indexes.CreateOneAsync(
                new CreateIndexModel<Branch>(
                    Builders<Branch>.IndexKeys.Ascending(b => b.BranchCode),
                    new CreateIndexOptions { Name = "ix_branches_branchCode" }),
                cancellationToken: cancellationToken);

            await _branches.Indexes.CreateOneAsync(
                new CreateIndexModel<Branch>(
                    Builders<Branch>.IndexKeys.Ascending(b => b.IsActive),
                    new CreateIndexOptions { Name = "ix_branches_isActive" }),
                cancellationToken: cancellationToken);
        }
    }
}
