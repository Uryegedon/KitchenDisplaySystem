using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SelfOrderingSystemKiosk.Services
{
    /// <summary>Creates MongoDB indexes for Orders on startup (non-blocking).</summary>
    public class OrderIndexesHostedService : IHostedService
    {
        private readonly OrderService _orderService;
        private readonly StockMovementService _stockMovementService;
        private readonly ManagementLogService _managementLogService;
        private readonly BranchService _branchService;
        private readonly UserService _userService;
        private readonly DeliveryImportService _deliveryImportService;
        private readonly IngredientStockService _ingredientStockService;
        private readonly ILogger<OrderIndexesHostedService> _logger;

        public OrderIndexesHostedService(
            OrderService orderService,
            StockMovementService stockMovementService,
            ManagementLogService managementLogService,
            BranchService branchService,
            UserService userService,
            DeliveryImportService deliveryImportService,
            IngredientStockService ingredientStockService,
            ILogger<OrderIndexesHostedService> logger)
        {
            _orderService = orderService;
            _stockMovementService = stockMovementService;
            _managementLogService = managementLogService;
            _branchService = branchService;
            _userService = userService;
            _deliveryImportService = deliveryImportService;
            _ingredientStockService = ingredientStockService;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _orderService.EnsureIndexesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Order collection index creation failed; queries may be slower until indexes exist.");
            }
            try
            {
                await _stockMovementService.EnsureIndexesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stock movement index creation failed.");
            }
            try
            {
                await _managementLogService.EnsureIndexesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Management log index creation failed.");
            }
            try
            {
                await _branchService.EnsureIndexesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Branch collection index creation failed.");
            }
            try
            {
                await _userService.EnsureIndexesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "User collection index creation failed.");
            }
            try
            {
                await _deliveryImportService.EnsureIndexesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Delivery import index creation failed.");
            }
            try
            {
                await _ingredientStockService.EnsureIndexesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ingredient index creation failed.");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
