using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MongoDB.Driver;
using SelfOrderingSystemKiosk.Models;

namespace SelfOrderingSystemKiosk.Services
{
    public class DeliveryImportService
    {
        private readonly IMongoCollection<DeliveryImportSession> _sessions;
        private readonly IngredientStockService _ingredients;

        public DeliveryImportService(IMongoClient mongoClient, IConfiguration config, IngredientStockService ingredients)
        {
            var dbName = config["KitchenDatabase:DatabaseName"] ?? "Kitchen";
            _sessions = mongoClient.GetDatabase(dbName).GetCollection<DeliveryImportSession>("DeliveryImportSessions");
            _ingredients = ingredients;
        }

        public async Task<DeliveryImportSession> CreateAsync(string branchId, string createdBy)
        {
            var session = new DeliveryImportSession
            {
                Token = CreateToken(),
                BranchId = branchId.Trim(),
                CreatedBy = createdBy,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10),
                Status = "Waiting"
            };

            await _sessions.InsertOneAsync(session);
            return session;
        }

        public async Task<DeliveryImportSession?> GetByTokenAsync(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return null;

            return await _sessions.Find(s => s.Token == token.Trim()).FirstOrDefaultAsync();
        }

        public async Task<DeliveryImportSession?> GetActiveByTokenAsync(string token)
        {
            var session = await GetByTokenAsync(token);
            if (session == null || session.ExpiresAtUtc < DateTime.UtcNow)
                return null;

            return session;
        }

        public async Task<(bool Success, string Message)> TrySaveUploadedTextAsync(
            string token,
            string rawText,
            string? contentType,
            string? userAgent,
            string? remoteIp)
        {
            var session = await GetActiveByTokenAsync(token);
            if (session == null)
                return (false, "This scan session is invalid or expired.");
            if (session.UploadedAtUtc.HasValue)
                return (false, "This scan session was already uploaded. Start a new scan if you need to replace it.");

            var rows = await ParseRowsAsync(rawText, session.BranchId);
            var update = Builders<DeliveryImportSession>.Update
                .Set(s => s.RawText, rawText ?? string.Empty)
                .Set(s => s.Rows, rows)
                .Set(s => s.UploadedAtUtc, DateTime.UtcNow)
                .Set(s => s.UploadContentType, contentType ?? string.Empty)
                .Set(s => s.UploadUserAgent, userAgent ?? string.Empty)
                .Set(s => s.UploadRemoteIp, remoteIp ?? string.Empty)
                .Set(s => s.Status, rows.Any() ? "Uploaded" : "NeedsReview");

            var filter = Builders<DeliveryImportSession>.Filter.And(
                Builders<DeliveryImportSession>.Filter.Eq(s => s.Id, session.Id),
                Builders<DeliveryImportSession>.Filter.Eq(s => s.UploadedAtUtc, null),
                Builders<DeliveryImportSession>.Filter.Gt(s => s.ExpiresAtUtc, DateTime.UtcNow));
            var result = await _sessions.UpdateOneAsync(filter, update);
            return result.ModifiedCount == 1
                ? (true, "Upload received. Return to the desktop to review.")
                : (false, "This scan session was already uploaded or expired.");
        }

        public async Task MarkConfirmedAsync(string token)
        {
            await _sessions.UpdateOneAsync(
                s => s.Token == token.Trim(),
                Builders<DeliveryImportSession>.Update
                    .Set(s => s.Status, "Confirmed")
                    .Set(s => s.ConfirmedAtUtc, DateTime.UtcNow));
        }

        public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
        {
            await _sessions.Indexes.CreateOneAsync(
                new CreateIndexModel<DeliveryImportSession>(
                    Builders<DeliveryImportSession>.IndexKeys.Ascending(s => s.Token),
                    new CreateIndexOptions { Name = "ux_delivery_import_token", Unique = true }),
                cancellationToken: cancellationToken);

            await _sessions.Indexes.CreateOneAsync(
                new CreateIndexModel<DeliveryImportSession>(
                    Builders<DeliveryImportSession>.IndexKeys
                        .Ascending(s => s.BranchId)
                        .Descending(s => s.CreatedAtUtc),
                    new CreateIndexOptions { Name = "ix_delivery_import_branch_created" }),
                cancellationToken: cancellationToken);

            await _sessions.Indexes.CreateOneAsync(
                new CreateIndexModel<DeliveryImportSession>(
                    Builders<DeliveryImportSession>.IndexKeys.Ascending(s => s.ExpiresAtUtc),
                    new CreateIndexOptions { Name = "ix_delivery_import_expires" }),
                cancellationToken: cancellationToken);
        }

        private async Task<List<DeliveryImportRow>> ParseRowsAsync(string rawText, string branchId)
        {
            var ingredients = await _ingredients.GetAllByBranchAsync(branchId);
            var rows = new List<DeliveryImportRow>();
            foreach (var rawLine in (rawText ?? string.Empty).Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length < 3)
                    continue;
                if (line.Contains("item", StringComparison.OrdinalIgnoreCase) &&
                    line.Contains("qty", StringComparison.OrdinalIgnoreCase))
                    continue;

                var match = Regex.Match(line, @"^(?<name>.+?)[\s,|\t]+(?<qty>\d+)(?:\s*(?<unit>[A-Za-z]+))?$");
                if (!match.Success)
                    match = Regex.Match(line, @"^(?<qty>\d+)[\s,|\t]+(?<name>.+?)(?:\s+(?<unit>[A-Za-z]+))?$");
                if (!match.Success)
                    continue;

                var name = CleanupName(match.Groups["name"].Value);
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                if (!int.TryParse(match.Groups["qty"].Value, out var quantity) || quantity <= 0)
                    continue;

                var unit = match.Groups["unit"].Success ? match.Groups["unit"].Value.Trim() : string.Empty;
                var matched = FindBestIngredient(name, ingredients);
                rows.Add(new DeliveryImportRow
                {
                    ItemName = name,
                    Quantity = quantity,
                    Unit = unit,
                    MatchedIngredientId = matched.Item?.Id ?? string.Empty,
                    MatchedIngredientName = matched.Item?.Item ?? string.Empty,
                    Confidence = matched.Score
                });
            }

            return rows;
        }

        private static (IngredientItem? Item, int Score) FindBestIngredient(string name, List<IngredientItem> ingredients)
        {
            var normalized = Normalize(name);
            if (string.IsNullOrWhiteSpace(normalized))
                return (null, 0);

            IngredientItem? best = null;
            var bestScore = 0;
            foreach (var ingredient in ingredients)
            {
                var candidate = Normalize(ingredient.Item ?? "");
                if (candidate.Length == 0)
                    continue;

                var score = candidate == normalized
                    ? 100
                    : candidate.Contains(normalized) || normalized.Contains(candidate)
                        ? 85
                        : SimilarityScore(normalized, candidate);

                if (score > bestScore)
                {
                    best = ingredient;
                    bestScore = score;
                }
            }

            return bestScore >= 55 ? (best, bestScore) : (null, bestScore);
        }

        private static int SimilarityScore(string a, string b)
        {
            var distance = LevenshteinDistance(a, b);
            var max = Math.Max(a.Length, b.Length);
            if (max == 0)
                return 100;

            return Math.Max(0, (int)Math.Round((1 - (distance / (double)max)) * 100));
        }

        private static int LevenshteinDistance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (var j = 0; j <= b.Length; j++) d[0, j] = j;

            for (var i = 1; i <= a.Length; i++)
            {
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }

            return d[a.Length, b.Length];
        }

        private static string CleanupName(string value)
        {
            return Regex.Replace(value ?? string.Empty, @"\s+", " ")
                .Trim(' ', '-', ':', '|', ',');
        }

        private static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]+", "");
        }

        private static string CreateToken()
        {
            return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace("+", "-")
                .Replace("/", "_")
                .TrimEnd('=');
        }
    }
}
