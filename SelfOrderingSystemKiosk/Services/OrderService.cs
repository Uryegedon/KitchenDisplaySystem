using MongoDB.Driver;
using SelfOrderingSystemKiosk.Areas.Customer.Models;


namespace SelfOrderingSystemKiosk.Services
{
    public class OrderService
    {
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

        /// <summary>Numeric order id (10 digits): yyMMdd + 4 random digits. Table orders replace the first digit with the table digit.</summary>
        public async Task<string> CreateUniqueOrderNumberAsync(string? tableNumber = null, CancellationToken cancellationToken = default)
        {
            for (var attempt = 0; attempt < 16; attempt++)
            {
                var candidate = ApplyTablePrefix($"{DateTime.UtcNow:yyMMdd}{Random.Shared.Next(1000, 10000)}", tableNumber);
                var count = await _orders.CountDocumentsAsync(o => o.OrderNumber == candidate, cancellationToken: cancellationToken);
                if (count == 0)
                    return candidate;
                await Task.Delay(50, cancellationToken);
            }

            return ApplyTablePrefix($"{DateTime.UtcNow:yyMMdd}{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}", tableNumber);
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
        public async Task UpdateStatusAsync(string id, string status)
        {
            var update = Builders<Order>.Update.Set(o => o.Status, status);
            await _orders.UpdateOneAsync(o => o.Id == id, update);
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
    }
}
