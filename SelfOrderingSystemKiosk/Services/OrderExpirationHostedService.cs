using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SelfOrderingSystemKiosk.Services
{
    public class OrderExpirationHostedService : BackgroundService
    {
        private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
        private readonly OrderService _orderService;
        private readonly ILogger<OrderExpirationHostedService> _logger;

        public OrderExpirationHostedService(OrderService orderService, ILogger<OrderExpirationHostedService> logger)
        {
            _orderService = orderService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var expiredCount = await _orderService.ExpirePendingOrdersAsync(stoppingToken);
                    if (expiredCount > 0)
                    {
                        _logger.LogInformation("Canceled {Count} pending orders older than 24 hours.", expiredCount);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to expire pending orders.");
                }

                try
                {
                    await Task.Delay(SweepInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
