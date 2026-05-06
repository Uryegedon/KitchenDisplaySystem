using MongoDB.Driver;
using SelfOrderingSystemKiosk.Areas.Customer.Models;


namespace SelfOrderingSystemKiosk.Services
{
    public class OrderService
    {
        private const string PendingStatus = "Pending";
        private const string CanceledStatus = "Canceled";
        private static readonly TimeSpan PendingOrderExpiration = TimeSpan.FromHours(24);
        private readonly IMongoCollection<Order> _orders;

        public OrderService(Models.KitchenDatabase db)
        {
            _orders = db.Database.GetCollection<Order>("Orders");
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

            var now = DateTime.UtcNow;
            var filter = string.IsNullOrEmpty(dateFilter) ? "all" : dateFilter.ToLowerInvariant();
            List<Order> orders = filter switch
            {
                "day" => await GetByDateRangeHalfOpenAsync(
                    new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc),
                    new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(1)),
                "week" =>
                    await GetByDateRangeHalfOpenAsync(
                        new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-(int)now.DayOfWeek),
                        new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-(int)now.DayOfWeek).AddDays(7)),
                "month" =>
                    await GetByDateRangeHalfOpenAsync(
                        new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc),
                        new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1)),
                _ => await GetAllAsync()
            };

            return orders;
        }

        /// <summary>Numeric order id (6 digits). Table orders replace the first digit with the table digit.</summary>
        public async Task<string> CreateUniqueOrderNumberAsync(string? tableNumber = null, CancellationToken cancellationToken = default)
        {
            for (var attempt = 0; attempt < 16; attempt++)
            {
                var candidate = ApplyTablePrefix(Random.Shared.Next(100000, 1000000).ToString(), tableNumber);
                var count = await _orders.CountDocumentsAsync(o => o.OrderNumber == candidate, cancellationToken: cancellationToken);
                if (count == 0)
                    return candidate;
                await Task.Delay(50, cancellationToken);
            }

            return ApplyTablePrefix(Random.Shared.Next(1000000, 10000000).ToString(), tableNumber);
        }

        private static string ApplyTablePrefix(string candidate, string? tableNumber)
        {
            var tableDigit = tableNumber?.FirstOrDefault(char.IsDigit);
            if (tableDigit is null or '\0' || string.IsNullOrEmpty(candidate))
                return candidate;

            return $"{tableDigit}{candidate[1..]}";
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
        }

        // Get order by ID
        public async Task<Order> GetByIdAsync(string id)
        {
            return await _orders.Find(o => o.Id == id).FirstOrDefaultAsync();
        }

        // Get order by order number
        public async Task<Order> GetByOrderNumberAsync(string orderNumber)
        {
            return await _orders.Find(o => o.OrderNumber == orderNumber).FirstOrDefaultAsync();
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
                var tableOrders = await GetOrdersByTableAsync(order.TableNumber);
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

        public async Task<long> UpdateOpenUnlimitedPersonCountForTableAsync(string tableNumber, int personCount)
        {
            if (string.IsNullOrWhiteSpace(tableNumber) || personCount <= 0)
                return 0;

            var filter = Builders<Order>.Filter.And(
                Builders<Order>.Filter.Eq(o => o.TableNumber, tableNumber),
                Builders<Order>.Filter.Eq(o => o.DiningType, "DineIn"),
                Builders<Order>.Filter.Eq(o => o.OrderType, "Unlimited"),
                Builders<Order>.Filter.Ne(o => o.Status, "Canceled"),
                Builders<Order>.Filter.Eq(o => o.BillArchived, false));
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
        public async Task<Order> GetFirstOrderByTableAsync(string tableNumber)
        {
            if (string.IsNullOrEmpty(tableNumber))
                return null;

            // Get the earliest order for this table that is not canceled
            var orders = await _orders
                .Find(o => o.TableNumber == tableNumber && 
                          o.Status != "Canceled" && 
                          o.DiningType == "DineIn")
                .SortBy(o => o.OrderDate)
                .Limit(1)
                .ToListAsync();

            return orders.FirstOrDefault();
        }

        // Get all orders for a table (for checking if table has any orders)
        public async Task<List<Order>> GetOrdersByTableAsync(string tableNumber)
        {
            if (string.IsNullOrEmpty(tableNumber))
                return new List<Order>();

            return await _orders
                .Find(o => o.TableNumber == tableNumber &&
                          o.Status != "Canceled" &&
                          o.DiningType == "DineIn")
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

            var now = DateTime.UtcNow;
            var filter = string.IsNullOrEmpty(dateFilter) ? "all" : dateFilter.ToLowerInvariant();
            
            if (!string.IsNullOrEmpty(branchId))
            {
                // Filter by branch
                return filter switch
                {
                    "day" => await _orders.Find(o => 
                        o.OrderDate >= new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc) &&
                        o.OrderDate < new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(1) &&
                        o.BranchId == branchId).ToListAsync(),
                    "week" => await _orders.Find(o =>
                        o.OrderDate >= new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-(int)now.DayOfWeek) &&
                        o.OrderDate < new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-(int)now.DayOfWeek).AddDays(7) &&
                        o.BranchId == branchId).ToListAsync(),
                    "month" => await _orders.Find(o =>
                        o.OrderDate >= new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc) &&
                        o.OrderDate < new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1) &&
                        o.BranchId == branchId).ToListAsync(),
                    _ => await GetAllByBranchAsync(branchId)
                };
            }
            else
            {
                // No branch filter - return all
                return filter switch
                {
                    "day" => await GetByDateRangeHalfOpenAsync(
                        new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc),
                        new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(1)),
                    "week" => await GetByDateRangeHalfOpenAsync(
                        new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-(int)now.DayOfWeek),
                        new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-(int)now.DayOfWeek).AddDays(7)),
                    "month" => await GetByDateRangeHalfOpenAsync(
                        new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc),
                        new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1)),
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
