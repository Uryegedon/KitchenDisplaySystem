using MongoDB.Driver;
using SelfOrderingSystemKiosk.Areas.Admin.Models;
using SelfOrderingSystemKiosk.Areas.Customer.Models;


namespace SelfOrderingSystemKiosk.Services
{
    public class OrderService
    {
        private const string PendingStatus = "Pending";
        private const string CanceledStatus = "Canceled";
        private static readonly TimeSpan PendingOrderExpiration = TimeSpan.FromHours(24);
        private readonly IMongoCollection<Order> _orders;
        private readonly IMongoCollection<Branch> _branches;

        public OrderService(Models.KitchenDatabase db)
        {
            _orders = db.Database.GetCollection<Order>("Orders");
            _branches = db.Database.GetCollection<Branch>("Branches");
        }

        public async Task<List<Order>> GetAllAsync() =>
            await _orders.Find(_ => true).ToListAsync();

        /// <summary>Start inclusive, end exclusive (typical for day/week/month ranges in UTC).</summary>
        public async Task<List<Order>> GetByDateRangeHalfOpenAsync(DateTime startUtcInclusive, DateTime endUtcExclusive)
        {
            return await _orders
                .Find(o => o.OrderDate >= startUtcInclusive && o.OrderDate < endUtcExclusive)
                .ToListAsync();
        }

        /// <summary>Kitchen board: filter in MongoDB by date preset instead of loading all orders.</summary>
        public async Task<List<Order>> GetOrdersForKitchenAsync(string? dateFilter)
        {
            await ExpirePendingOrdersAsync();

            var filter = string.IsNullOrEmpty(dateFilter) ? "all" : dateFilter.ToLowerInvariant();
            var (dayStart, dayEnd) = AppClock.LocalDateRange(AppClock.LocalNow.Date);
            var (weekStart, weekEnd) = AppClock.CurrentLocalWeekRange();
            var (monthStart, monthEnd) = AppClock.CurrentLocalMonthRange();
            List<Order> orders = filter switch
            {
                "day" => await GetByDateRangeHalfOpenAsync(dayStart, dayEnd),
                "week" => await GetByDateRangeHalfOpenAsync(weekStart, weekEnd),
                "month" => await GetByDateRangeHalfOpenAsync(monthStart, monthEnd),
                _ => await GetAllAsync()
            };

            return orders;
        }

        /// <summary>Six-digit order id: branch digit + table digit + four-digit branch/table sequence.</summary>
        public async Task<string> CreateUniqueOrderNumberAsync(string? tableNumber = null, string? branchId = null, CancellationToken cancellationToken = default)
        {
            var prefix = await BuildOrderNumberPrefixAsync(tableNumber, branchId, cancellationToken);
            var nextBase = await GetNextSequentialOrderNumberAsync(prefix, cancellationToken);
            for (var attempt = 0; attempt < 100 && nextBase <= 9999; attempt++)
            {
                var candidate = $"{prefix}{nextBase:D4}";
                var count = await CountOrderNumberAsync(candidate, cancellationToken);
                if (count == 0)
                    return candidate;

                nextBase++;
            }

            throw new InvalidOperationException($"Order number sequence is full for prefix {prefix}.");
        }

        private async Task<int> GetNextSequentialOrderNumberAsync(string prefix, CancellationToken cancellationToken)
        {
            var filter = Builders<Order>.Filter.And(
                Builders<Order>.Filter.Ne(o => o.OrderNumber, null),
                Builders<Order>.Filter.Ne(o => o.OrderNumber, ""),
                Builders<Order>.Filter.Regex(o => o.OrderNumber, new MongoDB.Bson.BsonRegularExpression($"^{prefix}\\d{{4}}$")));

            var latestOrder = await _orders
                .Find(filter)
                .SortByDescending(o => o.OrderNumber)
                .FirstOrDefaultAsync(cancellationToken);

            return Math.Clamp(GetSequenceSuffix(latestOrder?.OrderNumber) + 1, 1, 9999);
        }

        private async Task<string> BuildOrderNumberPrefixAsync(string? tableNumber, string? branchId, CancellationToken cancellationToken)
        {
            var branchDigit = await ResolveBranchDigitAsync(branchId, cancellationToken);
            var tableDigit = ResolveTableDigit(tableNumber);
            return $"{branchDigit}{tableDigit}";
        }

        private async Task<char> ResolveBranchDigitAsync(string? branchId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(branchId))
                return '0';

            var branch = await _branches
                .Find(b => b.Id == branchId.Trim())
                .FirstOrDefaultAsync(cancellationToken);

            return BranchService.GetEffectiveReferenceDigit(branch) ?? '0';
        }

        private static char ResolveTableDigit(string? tableNumber)
        {
            if (string.IsNullOrWhiteSpace(tableNumber))
                return '0';

            var digit = tableNumber.Trim().FirstOrDefault(char.IsDigit);
            return digit is not '\0' ? digit : '0';
        }

        private static int GetSequenceSuffix(string? orderNumber)
        {
            if (string.IsNullOrWhiteSpace(orderNumber))
                return 0;

            var trimmed = orderNumber.Trim();
            if (trimmed.Length != 6 || !trimmed.All(char.IsDigit))
                return 0;

            return int.Parse(trimmed[2..]);
        }

        private async Task<long> CountOrderNumberAsync(string orderNumber, CancellationToken cancellationToken)
        {
            var filter = Builders<Order>.Filter.Eq(o => o.OrderNumber, orderNumber);
            return await _orders.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
        }

        public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
        {
            await _orders.Indexes.CreateOneAsync(
                new CreateIndexModel<Order>(
                    Builders<Order>.IndexKeys.Ascending(o => o.OrderNumber),
                    new CreateIndexOptions { Name = "ix_orders_orderNumber" }),
                cancellationToken: cancellationToken);

            await _orders.Indexes.CreateOneAsync(
                new CreateIndexModel<Order>(
                    Builders<Order>.IndexKeys.Ascending(o => o.OrderDate),
                    new CreateIndexOptions { Name = "ix_orders_orderDate" }),
                cancellationToken: cancellationToken);

            await _orders.Indexes.CreateOneAsync(
                new CreateIndexModel<Order>(
                    Builders<Order>.IndexKeys.Ascending(o => o.Status),
                    new CreateIndexOptions { Name = "ix_orders_status" }),
                cancellationToken: cancellationToken);

            await _orders.Indexes.CreateOneAsync(
                new CreateIndexModel<Order>(
                    Builders<Order>.IndexKeys
                        .Ascending(o => o.BranchId)
                        .Descending(o => o.OrderDate),
                    new CreateIndexOptions { Name = "ix_orders_branch_orderDate" }),
                cancellationToken: cancellationToken);

            await _orders.Indexes.CreateOneAsync(
                new CreateIndexModel<Order>(
                    Builders<Order>.IndexKeys
                        .Ascending(o => o.BranchId)
                        .Ascending(o => o.TableNumber)
                        .Ascending(o => o.DiningType)
                        .Ascending(o => o.BillArchived)
                        .Ascending(o => o.Status)
                        .Ascending(o => o.OrderDate),
                    new CreateIndexOptions { Name = "ix_orders_branch_table_session" }),
                cancellationToken: cancellationToken);

            await _orders.Indexes.CreateOneAsync(
                new CreateIndexModel<Order>(
                    Builders<Order>.IndexKeys
                        .Ascending(o => o.OrderNumber)
                        .Ascending(o => o.PublicAccessToken)
                        .Descending(o => o.OrderDate),
                    new CreateIndexOptions { Name = "ix_orders_number_accessToken_date" }),
                cancellationToken: cancellationToken);

            await _orders.Indexes.CreateOneAsync(
                new CreateIndexModel<Order>(
                    Builders<Order>.IndexKeys
                        .Ascending(o => o.Status)
                        .Ascending(o => o.OrderDate),
                    new CreateIndexOptions { Name = "ix_orders_status_orderDate" }),
                cancellationToken: cancellationToken);
        }

        // Get order by ID
        public async Task<Order> GetByIdAsync(string id)
        {
            return await _orders.Find(o => o.Id == id).FirstOrDefaultAsync();
        }

        // Get order by order number, optionally constrained by branch or the customer's public access token.
        public async Task<Order> GetByOrderNumberAsync(string orderNumber, string? branchId = null, string? accessToken = null)
        {
            if (string.IsNullOrWhiteSpace(orderNumber))
                return null;

            var filters = new List<FilterDefinition<Order>>
            {
                Builders<Order>.Filter.Eq(o => o.OrderNumber, orderNumber.Trim())
            };

            if (!string.IsNullOrWhiteSpace(branchId))
                filters.Add(Builders<Order>.Filter.Eq(o => o.BranchId, branchId.Trim()));

            if (!string.IsNullOrWhiteSpace(accessToken))
                filters.Add(Builders<Order>.Filter.Eq(o => o.PublicAccessToken, accessToken.Trim()));

            return await _orders
                .Find(Builders<Order>.Filter.And(filters))
                .SortByDescending(o => o.OrderDate)
                .FirstOrDefaultAsync();
        }

        // Create new order
        public async Task CreateAsync(Order order)
        {
            await _orders.InsertOneAsync(order);
        }

        // Update order
        public async Task UpdateAsync(string id, Order order)
        {
            await _orders.ReplaceOneAsync(o => o.Id == id, order);
        }

        // Update order status
        public async Task UpdateStatusAsync(string id, string status, DateTime? sessionStartedAtUtc = null)
        {
            var order = await GetByIdAsync(id);
            var update = Builders<Order>.Update.Set(o => o.Status, status);
            if (string.Equals(status, "In Progress", StringComparison.OrdinalIgnoreCase) &&
                order != null &&
                order.SessionStartedAtUtc == null &&
                string.Equals(order.OrderType, "Unlimited", StringComparison.OrdinalIgnoreCase))
            {
                var sessionStart = sessionStartedAtUtc ?? await GetSessionStartForStaffStartAsync(order);
                update = update.Set(o => o.SessionStartedAtUtc, sessionStart.ToUniversalTime());
            }

            await _orders.UpdateOneAsync(o => o.Id == id, update);
        }

        public async Task<bool> UpdateStatusIfCurrentAsync(string id, string expectedStatus, string newStatus)
        {
            if (string.IsNullOrWhiteSpace(id))
                return false;

            var filter = Builders<Order>.Filter.And(
                Builders<Order>.Filter.Eq(o => o.Id, id),
                Builders<Order>.Filter.Eq(o => o.Status, expectedStatus));
            var update = Builders<Order>.Update.Set(o => o.Status, newStatus);
            var result = await _orders.UpdateOneAsync(filter, update);
            return result.ModifiedCount > 0;
        }

        public async Task UpdateCompletionCostAsync(string id, decimal orderCost)
        {
            var roundedCost = Math.Round(Math.Max(0m, orderCost), 2, MidpointRounding.AwayFromZero);
            var order = await GetByIdAsync(id);
            if (order == null)
                return;

            var profit = Math.Round(order.Total - roundedCost, 2, MidpointRounding.AwayFromZero);
            var update = Builders<Order>.Update
                .Set(o => o.OrderCost, roundedCost)
                .Set(o => o.Profit, profit)
                .Set(o => o.CostedAtUtc, DateTime.UtcNow);
            await _orders.UpdateOneAsync(o => o.Id == id, update);
        }

        private async Task<DateTime> GetSessionStartForStaffStartAsync(Order order)
        {
            var existingSessionStart = GetSessionStartedAtUtc(order);
            if (existingSessionStart.HasValue)
                return existingSessionStart.Value;

            if (!string.IsNullOrWhiteSpace(order.TableNumber) &&
                string.Equals(order.DiningType, "DineIn", StringComparison.OrdinalIgnoreCase))
            {
                var tableOrders = await GetOrdersByTableAsync(order.TableNumber, order.BranchId);
                var now = DateTime.UtcNow;
                var activeSessionStart = tableOrders
                    .Where(o => !o.BillArchived
                        && string.Equals(o.OrderType, "Unlimited", StringComparison.OrdinalIgnoreCase))
                    .Select(GetSessionStartedAtUtc)
                    .Where(start => start.HasValue && now < start.Value.AddHours(2))
                    .Select(start => start!.Value)
                    .OrderByDescending(start => start)
                    .FirstOrDefault();

                if (activeSessionStart != default)
                    return activeSessionStart;
            }

            return DateTime.UtcNow;
        }

        private static DateTime? GetSessionStartedAtUtc(Order existingOrder)
        {
            if (existingOrder?.SessionStartedAtUtc == null)
                return null;

            var value = existingOrder.SessionStartedAtUtc.Value;
            return value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                : value.ToUniversalTime();
        }

        public async Task<long> ExpirePendingOrdersAsync(CancellationToken cancellationToken = default)
        {
            var cutoffUtc = DateTime.UtcNow.Subtract(PendingOrderExpiration);
            var filter = Builders<Order>.Filter.And(
                Builders<Order>.Filter.Eq(o => o.Status, PendingStatus),
                Builders<Order>.Filter.Lte(o => o.OrderDate, cutoffUtc));
            var update = Builders<Order>.Update
                .Set(o => o.Status, CanceledStatus)
                .Set(o => o.BillArchived, true);
            var result = await _orders.UpdateManyAsync(filter, update, cancellationToken: cancellationToken);
            return result.ModifiedCount;
        }

        public async Task UpdatePaymentStatusAsync(IEnumerable<string> ids, string paymentStatus)
        {
            var validIds = ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToList();

            if (!validIds.Any())
                return;

            var update = Builders<Order>.Update.Set(o => o.PaymentStatus, paymentStatus);
            await _orders.UpdateManyAsync(o => validIds.Contains(o.Id), update);
        }

        public async Task ArchiveBillsAsync(IEnumerable<string> ids)
        {
            var validIds = ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToList();

            if (!validIds.Any())
                return;

            var update = Builders<Order>.Update.Set(o => o.BillArchived, true);
            await _orders.UpdateManyAsync(o => validIds.Contains(o.Id), update);
        }

        public async Task<long> UpdateOpenUnlimitedPersonCountForTableAsync(string tableNumber, int personCount, string? branchId = null)
        {
            if (string.IsNullOrWhiteSpace(tableNumber) || personCount <= 0)
                return 0;

            var filters = new List<FilterDefinition<Order>>
            {
                Builders<Order>.Filter.Eq(o => o.TableNumber, tableNumber),
                Builders<Order>.Filter.Eq(o => o.DiningType, "DineIn"),
                Builders<Order>.Filter.Eq(o => o.OrderType, "Unlimited"),
                Builders<Order>.Filter.Ne(o => o.Status, "Canceled"),
                Builders<Order>.Filter.Eq(o => o.BillArchived, false)
            };
            if (!string.IsNullOrWhiteSpace(branchId))
                filters.Add(Builders<Order>.Filter.Eq(o => o.BranchId, branchId.Trim()));

            var filter = Builders<Order>.Filter.And(filters);
            var update = Builders<Order>.Update.Set(o => o.PersonCount, personCount);
            var result = await _orders.UpdateManyAsync(filter, update);
            return result.ModifiedCount;
        }

        // Delete order
        public async Task DeleteAsync(string id)
        {
            await _orders.DeleteOneAsync(o => o.Id == id);
        }

        // Get orders by status
        public async Task<List<Order>> GetByStatusAsync(string status)
        {
            return await _orders.Find(o => o.Status == status).ToListAsync();
        }

        // Get orders by date range
        public async Task<List<Order>> GetByDateRangeAsync(DateTime startDate, DateTime endDate)
        {
            return await _orders.Find(o => o.OrderDate >= startDate && o.OrderDate <= endDate).ToListAsync();
        }

        // Cancel order by ID
        public async Task CancelOrderAsync(string id)
        {
            var update = Builders<Order>.Update.Set(o => o.Status, "Canceled");
            await _orders.UpdateOneAsync(o => o.Id == id, update);
        }

        // Cancel order by order number
        public async Task CancelOrderByOrderNumberAsync(string orderNumber)
        {
            var update = Builders<Order>.Update.Set(o => o.Status, "Canceled");
            await _orders.UpdateOneAsync(o => o.OrderNumber == orderNumber, update);
        }

        // Get first order for a table (ordering session timer is enforced in kiosk session)
        public async Task<Order> GetFirstOrderByTableAsync(string tableNumber, string? branchId = null)
        {
            if (string.IsNullOrEmpty(tableNumber))
                return null;

            var filters = new List<FilterDefinition<Order>>
            {
                Builders<Order>.Filter.Eq(o => o.TableNumber, tableNumber),
                Builders<Order>.Filter.Ne(o => o.Status, "Canceled"),
                Builders<Order>.Filter.Eq(o => o.DiningType, "DineIn")
            };
            if (!string.IsNullOrWhiteSpace(branchId))
                filters.Add(Builders<Order>.Filter.Eq(o => o.BranchId, branchId.Trim()));

            // Get the earliest order for this table that is not canceled
            var orders = await _orders
                .Find(Builders<Order>.Filter.And(filters))
                .SortBy(o => o.OrderDate)
                .Limit(1)
                .ToListAsync();

            return orders.FirstOrDefault();
        }

        // Get all orders for a table (for checking if table has any orders)
        public async Task<List<Order>> GetOrdersByTableAsync(string tableNumber, string? branchId = null)
        {
            if (string.IsNullOrEmpty(tableNumber))
                return new List<Order>();

            var filters = new List<FilterDefinition<Order>>
            {
                Builders<Order>.Filter.Eq(o => o.TableNumber, tableNumber),
                Builders<Order>.Filter.Ne(o => o.Status, "Canceled"),
                Builders<Order>.Filter.Eq(o => o.DiningType, "DineIn")
            };
            if (!string.IsNullOrWhiteSpace(branchId))
                filters.Add(Builders<Order>.Filter.Eq(o => o.BranchId, branchId.Trim()));

            return await _orders
                .Find(Builders<Order>.Filter.And(filters))
                .SortBy(o => o.OrderDate)
                .ToListAsync();
        }

        // ====================
        // Branch Filtering Methods
        // ====================

        /// <summary>
        /// Gets all orders filtered by branch (empty branchId returns all orders)
        /// </summary>
        public async Task<List<Order>> GetAllByBranchAsync(string? branchId)
        {
            if (string.IsNullOrEmpty(branchId))
            {
                return await GetAllAsync();
            }

            return await _orders.Find(o => o.BranchId == branchId).ToListAsync();
        }

        /// <summary>
        /// Gets orders by date range and branch
        /// </summary>
        public async Task<List<Order>> GetByDateRangeHalfOpenAsync(DateTime startUtcInclusive, DateTime endUtcExclusive, string? branchId)
        {
            if (string.IsNullOrEmpty(branchId))
            {
                return await GetByDateRangeHalfOpenAsync(startUtcInclusive, endUtcExclusive);
            }

            return await _orders
                .Find(o => o.OrderDate >= startUtcInclusive && o.OrderDate < endUtcExclusive && o.BranchId == branchId)
                .ToListAsync();
        }

        /// <summary>
        /// Gets orders for kitchen filtered by branch
        /// </summary>
        public async Task<List<Order>> GetOrdersForKitchenAsync(string? dateFilter, string? branchId)
        {
            await ExpirePendingOrdersAsync();

            var filter = string.IsNullOrEmpty(dateFilter) ? "all" : dateFilter.ToLowerInvariant();
            var (dayStart, dayEnd) = AppClock.LocalDateRange(AppClock.LocalNow.Date);
            var (weekStart, weekEnd) = AppClock.CurrentLocalWeekRange();
            var (monthStart, monthEnd) = AppClock.CurrentLocalMonthRange();

            if (!string.IsNullOrEmpty(branchId))
            {
                // Filter by branch
                return filter switch
                {
                    "day" => await _orders.Find(o =>
                        o.OrderDate >= dayStart &&
                        o.OrderDate < dayEnd &&
                        o.BranchId == branchId).ToListAsync(),
                    "week" => await _orders.Find(o =>
                        o.OrderDate >= weekStart &&
                        o.OrderDate < weekEnd &&
                        o.BranchId == branchId).ToListAsync(),
                    "month" => await _orders.Find(o =>
                        o.OrderDate >= monthStart &&
                        o.OrderDate < monthEnd &&
                        o.BranchId == branchId).ToListAsync(),
                    _ => await GetAllByBranchAsync(branchId)
                };
            }
            else
            {
                // No branch filter - return all
                return filter switch
                {
                    "day" => await GetByDateRangeHalfOpenAsync(dayStart, dayEnd),
                    "week" => await GetByDateRangeHalfOpenAsync(weekStart, weekEnd),
                    "month" => await GetByDateRangeHalfOpenAsync(monthStart, monthEnd),
                    _ => await GetAllAsync()
                };
            }
        }

        /// <summary>
        /// Gets order count by branch
        /// </summary>
        public async Task<long> GetCountByBranchAsync(string? branchId)
        {
            if (string.IsNullOrEmpty(branchId))
            {
                return await _orders.CountDocumentsAsync(_ => true);
            }

            return await _orders.CountDocumentsAsync(o => o.BranchId == branchId);
        }
    }
}
