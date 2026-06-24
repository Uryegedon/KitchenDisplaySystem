using MongoDB.Driver;
using SelfOrderingSystemKiosk.Areas.Admin.Models;
using SelfOrderingSystemKiosk.Models;
using System.Text.RegularExpressions;

namespace SelfOrderingSystemKiosk.Services
{
    public class BranchService
    {
        private static readonly Dictionary<string, char> LegacyBranchDigitOverrides = new(StringComparer.OrdinalIgnoreCase)
        {
            ["69fd9dc484fd8e2bd6aba7a5"] = '9'
        };
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
            NormalizeBranch(branch);
            branch.CreatedAt = DateTime.UtcNow;
            await _branches.InsertOneAsync(branch);
        }

        public async Task UpdateAsync(Branch branch)
        {
            NormalizeBranch(branch);
            branch.UpdatedAt = DateTime.UtcNow;
            await _branches.ReplaceOneAsync(b => b.Id == branch.Id, branch);
        }

        public async Task DeleteAsync(string id)
        {
            await _branches.DeleteOneAsync(b => b.Id == id);
        }

        public async Task<bool> IsBranchCodeUniqueAsync(string branchCode, string? excludeId = null)
        {
            if (string.IsNullOrWhiteSpace(branchCode))
                return false;

            var code = branchCode.Trim();
            var regex = new MongoDB.Bson.BsonRegularExpression($"^{Regex.Escape(code)}$", "i");
            var filter = Builders<Branch>.Filter.Regex(b => b.BranchCode, regex);
            if (!string.IsNullOrEmpty(excludeId))
            {
                filter = filter & Builders<Branch>.Filter.Ne(b => b.Id, excludeId);
            }
            var count = await _branches.CountDocumentsAsync(filter);
            return count == 0;
        }

        public async Task<Branch?> GetBranchUsingReferenceDigitAsync(char referenceDigit, string? excludeId = null)
        {
            var branches = await GetAllAsync();
            return branches.FirstOrDefault(branch =>
                !string.Equals(branch.Id, excludeId, StringComparison.OrdinalIgnoreCase) &&
                GetEffectiveReferenceDigit(branch) == referenceDigit);
        }

        public static char? GetReferenceDigit(string? branchCode)
        {
            if (string.IsNullOrWhiteSpace(branchCode))
                return null;

            var digit = branchCode.Trim().LastOrDefault(char.IsDigit);
            return digit == '\0' ? null : digit;
        }

        public static char? GetEffectiveReferenceDigit(Branch? branch)
        {
            if (branch == null)
                return null;

            var branchCodeDigit = GetReferenceDigit(branch.BranchCode);
            if (branchCodeDigit.HasValue)
                return branchCodeDigit.Value;

            var branchNameDigit = branch.BranchName?.FirstOrDefault(char.IsDigit);
            if (branchNameDigit is not null and not '\0')
                return branchNameDigit.Value;

            if (!string.IsNullOrWhiteSpace(branch.Id) &&
                LegacyBranchDigitOverrides.TryGetValue(branch.Id.Trim(), out var overriddenDigit))
                return overriddenDigit;

            var name = $"{branch.BranchCode} {branch.BranchName}";
            if (name.Contains("main", StringComparison.OrdinalIgnoreCase))
                return '1';
            if (name.Contains("nova", StringComparison.OrdinalIgnoreCase))
                return '9';

            return null;
        }

        private static void NormalizeBranch(Branch branch)
        {
            branch.BranchCode = branch.BranchCode?.Trim() ?? string.Empty;
            branch.BranchName = branch.BranchName?.Trim() ?? string.Empty;
            branch.Address = branch.Address?.Trim() ?? string.Empty;
            branch.Phone = branch.Phone?.Trim() ?? string.Empty;
            branch.Email = branch.Email?.Trim() ?? string.Empty;
        }

        public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
        {
            await _branches.Indexes.CreateOneAsync(
                new CreateIndexModel<Branch>(
                    Builders<Branch>.IndexKeys.Ascending(b => b.BranchCode),
                    new CreateIndexOptions { Name = "ix_branches_branchCode" }),
                cancellationToken: cancellationToken);

            var populatedBranchCodeFilter = Builders<Branch>.Filter.And(
                Builders<Branch>.Filter.Exists(b => b.BranchCode),
                Builders<Branch>.Filter.Ne(b => b.BranchCode, null),
                Builders<Branch>.Filter.Ne(b => b.BranchCode, string.Empty));
            await _branches.Indexes.CreateOneAsync(
                new CreateIndexModel<Branch>(
                    Builders<Branch>.IndexKeys.Ascending(b => b.BranchCode),
                    new CreateIndexOptions<Branch>
                    {
                        Name = "ux_branches_branchCode_ci",
                        Unique = true,
                        Collation = new Collation("en", strength: CollationStrength.Secondary),
                        PartialFilterExpression = populatedBranchCodeFilter
                    }),
                cancellationToken: cancellationToken);

            await _branches.Indexes.CreateOneAsync(
                new CreateIndexModel<Branch>(
                    Builders<Branch>.IndexKeys.Ascending(b => b.IsActive),
                    new CreateIndexOptions { Name = "ix_branches_isActive" }),
                cancellationToken: cancellationToken);
        }
    }
}
