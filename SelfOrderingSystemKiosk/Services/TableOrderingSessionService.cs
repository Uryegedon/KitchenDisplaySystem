using MongoDB.Bson;
using MongoDB.Driver;
using SelfOrderingSystemKiosk.Areas.Customer.Models;

namespace SelfOrderingSystemKiosk.Services
{
    public class TableOrderingSessionService
    {
        private const int MaxWingFlavors = 4;
        private readonly IMongoCollection<TableOrderingSession> _sessions;

        public TableOrderingSessionService(Models.KitchenDatabase db)
        {
            _sessions = db.Database.GetCollection<TableOrderingSession>("TableOrderingSessions");
        }

        public async Task<TableOrderingSession?> GetAsync(string tableNumber, string? branchId = null)
        {
            var id = BuildId(tableNumber, branchId);
            if (string.IsNullOrEmpty(id))
                return null;

            var session = await _sessions.Find(s => s.Id == id).FirstOrDefaultAsync();
            if (session != null || string.IsNullOrWhiteSpace(branchId))
                return session;

            var legacyId = BuildId(tableNumber, null);
            return await _sessions.Find(s => s.Id == legacyId).FirstOrDefaultAsync();
        }

        public async Task<List<TableOrderingSession>> GetAllAsync()
        {
            return await _sessions.Find(_ => true).ToListAsync();
        }

        public async Task<TableOrderingSession?> OpenOrderingAsync(string tableNumber, string? branchId = null)
        {
            var id = BuildId(tableNumber, branchId);
            if (string.IsNullOrEmpty(id))
                return null;

            var now = DateTime.UtcNow;
            var session = new TableOrderingSession
            {
                Id = id,
                TableNumber = tableNumber.Trim(),
                BranchId = branchId?.Trim() ?? string.Empty,
                PersonCount = 0,
                BilledPersonCount = 0,
                WingFlavors = new List<string>(),
                IsOrderingOpen = true,
                OrderingOpenedAtUtc = now,
                OrderingClosedAtUtc = null,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            await _sessions.ReplaceOneAsync(
                s => s.Id == id,
                session,
                new ReplaceOptions { IsUpsert = true });

            return session;
        }

        public async Task CloseOrderingAsync(string tableNumber, string? branchId = null)
        {
            await ClearAsync(tableNumber, branchId);
        }

        public async Task<bool> IsOrderingOpenAsync(string tableNumber, string? branchId = null)
        {
            var session = await GetAsync(tableNumber, branchId);
            return session?.IsOrderingOpen == true;
        }

        public async Task<TableOrderingSession?> SavePersonCountAsync(string tableNumber, int personCount, string? branchId = null)
        {
            var id = BuildId(tableNumber, branchId);
            if (string.IsNullOrEmpty(id) || personCount <= 0)
                return null;

            var now = DateTime.UtcNow;
            var update = Builders<TableOrderingSession>.Update
                .SetOnInsert(s => s.Id, id)
                .SetOnInsert(s => s.TableNumber, tableNumber.Trim())
                .SetOnInsert(s => s.BranchId, branchId?.Trim() ?? string.Empty)
                .SetOnInsert(s => s.CreatedAtUtc, now)
                .SetOnInsert(s => s.BilledPersonCount, 0)
                .SetOnInsert(s => s.IsOrderingOpen, true)
                .Max(s => s.PersonCount, personCount)
                .Set(s => s.UpdatedAtUtc, now);

            return await _sessions.FindOneAndUpdateAsync(
                s => s.Id == id,
                update,
                new FindOneAndUpdateOptions<TableOrderingSession>
                {
                    IsUpsert = true,
                    ReturnDocument = ReturnDocument.After
                });
        }

        public async Task SeedFromExistingOrdersAsync(
            string tableNumber,
            int personCount,
            IEnumerable<string> wingFlavors,
            string? branchId = null)
        {
            var id = BuildId(tableNumber, branchId);
            if (string.IsNullOrEmpty(id))
                return;

            var now = DateTime.UtcNow;
            var normalizedFlavors = NormalizeFlavors(wingFlavors).Take(MaxWingFlavors).ToList();
            var update = Builders<TableOrderingSession>.Update
                .SetOnInsert(s => s.Id, id)
                .SetOnInsert(s => s.TableNumber, tableNumber.Trim())
                .SetOnInsert(s => s.BranchId, branchId?.Trim() ?? string.Empty)
                .SetOnInsert(s => s.PersonCount, Math.Max(0, personCount))
                .SetOnInsert(s => s.BilledPersonCount, Math.Max(0, personCount))
                .SetOnInsert(s => s.WingFlavors, normalizedFlavors)
                .SetOnInsert(s => s.IsOrderingOpen, true)
                .SetOnInsert(s => s.CreatedAtUtc, now)
                .SetOnInsert(s => s.UpdatedAtUtc, now);

            await _sessions.UpdateOneAsync(
                s => s.Id == id,
                update,
                new UpdateOptions { IsUpsert = true });
        }

        public async Task ReplaceFromExistingOrdersAsync(
            string tableNumber,
            int personCount,
            IEnumerable<string> wingFlavors,
            string? branchId = null)
        {
            var id = BuildId(tableNumber, branchId);
            if (string.IsNullOrEmpty(id))
                return;

            if (personCount <= 0)
            {
                await ClearAsync(tableNumber, branchId);
                return;
            }

            var now = DateTime.UtcNow;
            var normalizedFlavors = NormalizeFlavors(wingFlavors).Take(MaxWingFlavors).ToList();
            var replacement = new TableOrderingSession
            {
                Id = id,
                TableNumber = tableNumber.Trim(),
                BranchId = branchId?.Trim() ?? string.Empty,
                PersonCount = personCount,
                BilledPersonCount = personCount,
                WingFlavors = normalizedFlavors,
                IsOrderingOpen = true,
                OrderingOpenedAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            await _sessions.ReplaceOneAsync(
                s => s.Id == id,
                replacement,
                new ReplaceOptions { IsUpsert = true });
        }

        public async Task<TableOrderingSessionReserveResult> ReserveUnlimitedOrderAsync(
            string tableNumber,
            int personCount,
            IEnumerable<string> wingFlavors,
            string? branchId = null)
        {
            var id = BuildId(tableNumber, branchId);
            if (string.IsNullOrEmpty(id) || personCount <= 0)
                return TableOrderingSessionReserveResult.Fail("Please enter a valid person count.");

            var normalizedFlavors = NormalizeFlavors(wingFlavors).ToList();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var previous = await ReserveAsync(id, tableNumber.Trim(), personCount, normalizedFlavors, attempt == 0, branchId);
                    if (previous == null && attempt > 0)
                    {
                        var current = await GetAsync(tableNumber, branchId);
                        if (current != null)
                            return BuildFlavorLimitFailure(current);

                        continue;
                    }

                    var previousPersonCount = previous?.PersonCount ?? 0;
                    var previousBilledPersonCount = previous?.BilledPersonCount ?? 0;
                    var sessionPersonCount = Math.Max(personCount, previousPersonCount);
                    return TableOrderingSessionReserveResult.Ok(
                        sessionPersonCount,
                        Math.Max(0, sessionPersonCount - previousBilledPersonCount));
                }
                catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
                {
                    continue;
                }
                catch (MongoCommandException ex) when (ex.Code == 11000)
                {
                    continue;
                }
            }

            var existing = await GetAsync(tableNumber, branchId);
            return existing != null
                ? BuildFlavorLimitFailure(existing)
                : TableOrderingSessionReserveResult.Fail("Unable to reserve this table session. Please try again.");
        }

        public async Task ClearAsync(string tableNumber, string? branchId = null)
        {
            var id = BuildId(tableNumber, branchId);
            if (string.IsNullOrEmpty(id))
                return;

            await _sessions.DeleteOneAsync(s => s.Id == id);
            if (!string.IsNullOrWhiteSpace(branchId))
            {
                var legacyId = BuildId(tableNumber, null);
                if (!string.IsNullOrEmpty(legacyId))
                    await _sessions.DeleteOneAsync(s => s.Id == legacyId);
            }
        }

        private async Task<TableOrderingSession?> ReserveAsync(
            string id,
            string tableNumber,
            int personCount,
            List<string> wingFlavors,
            bool isUpsert,
            string? branchId)
        {
            var flavorArray = new BsonArray(wingFlavors);
            var existingFlavors = new BsonDocument("$ifNull", new BsonArray { "$wingFlavors", new BsonArray() });
            var filter = new BsonDocument
            {
                { "_id", id },
                {
                    "$expr",
                    new BsonDocument("$lte", new BsonArray
                    {
                        new BsonDocument("$size", new BsonDocument("$setUnion", new BsonArray
                        {
                            existingFlavors,
                            flavorArray
                        })),
                        MaxWingFlavors
                    })
                }
            };

            var now = DateTime.UtcNow;
            var pipeline = new EmptyPipelineDefinition<TableOrderingSession>()
                .AppendStage<TableOrderingSession, TableOrderingSession, TableOrderingSession>(
                    new BsonDocument("$set", new BsonDocument
                    {
                        { "tableNumber", new BsonDocument("$ifNull", new BsonArray { "$tableNumber", tableNumber }) },
                        { "branchId", branchId?.Trim() ?? string.Empty },
                        { "isOrderingOpen", true },
                        { "personCount", new BsonDocument("$max", new BsonArray
                            {
                                new BsonDocument("$ifNull", new BsonArray { "$personCount", 0 }),
                                personCount
                            })
                        },
                        { "billedPersonCount", new BsonDocument("$max", new BsonArray
                            {
                                new BsonDocument("$ifNull", new BsonArray { "$billedPersonCount", 0 }),
                                new BsonDocument("$ifNull", new BsonArray { "$personCount", 0 }),
                                personCount
                            })
                        },
                        { "wingFlavors", new BsonDocument("$setUnion", new BsonArray
                            {
                                existingFlavors,
                                flavorArray
                            })
                        },
                        { "createdAtUtc", new BsonDocument("$ifNull", new BsonArray { "$createdAtUtc", now }) },
                        { "updatedAtUtc", now }
                    }));

            return await _sessions.FindOneAndUpdateAsync(
                filter,
                Builders<TableOrderingSession>.Update.Pipeline(pipeline),
                new FindOneAndUpdateOptions<TableOrderingSession>
                {
                    IsUpsert = isUpsert,
                    ReturnDocument = ReturnDocument.Before
                });
        }

        private static TableOrderingSessionReserveResult BuildFlavorLimitFailure(TableOrderingSession session)
        {
            var flavors = NormalizeFlavors(session.WingFlavors).Take(MaxWingFlavors).ToList();
            var selected = flavors.Any()
                ? string.Join(", ", flavors)
                : "the current table flavors";
            return TableOrderingSessionReserveResult.Fail(
                $"This table can only have {MaxWingFlavors} wing flavors for the unlimited session. Current flavors: {selected}. Please choose from those flavors.");
        }

        private static IEnumerable<string> NormalizeFlavors(IEnumerable<string> wingFlavors)
        {
            return (wingFlavors ?? Enumerable.Empty<string>())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string BuildId(string tableNumber, string? branchId = null)
        {
            if (string.IsNullOrWhiteSpace(tableNumber))
                return string.Empty;

            var tableKey = tableNumber.Trim().ToUpperInvariant();
            return string.IsNullOrWhiteSpace(branchId)
                ? $"unlimited:{tableKey}"
                : $"unlimited:{branchId.Trim().ToUpperInvariant()}:{tableKey}";
        }
    }

    public class TableOrderingSessionReserveResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int PersonCount { get; set; }
        public int ChargeablePersonCount { get; set; }

        public static TableOrderingSessionReserveResult Ok(int personCount, int chargeablePersonCount) => new()
        {
            Success = true,
            PersonCount = personCount,
            ChargeablePersonCount = chargeablePersonCount
        };

        public static TableOrderingSessionReserveResult Fail(string message) => new()
        {
            Success = false,
            Message = message
        };
    }
}
