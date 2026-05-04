using SelfOrderingSystemKiosk.Services;

namespace SelfOrderingSystemKiosk.Services
{
    /// <summary>
    /// Hosted service that seeds branch access control data on startup
    /// </summary>
    public class BranchAccessBootstrapHostedService : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<BranchAccessBootstrapHostedService> _logger;

        public BranchAccessBootstrapHostedService(
            IServiceProvider serviceProvider,
            ILogger<BranchAccessBootstrapHostedService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var branchSeedService = scope.ServiceProvider.GetRequiredService<BranchAccessSeedService>();
                var inventorySeedService = scope.ServiceProvider.GetRequiredService<BranchInventorySeedService>();

                _logger.LogInformation("Seeding branch access control data...");
                await branchSeedService.SeedAsync();
                _logger.LogInformation("Branch access control data seeded successfully.");

                _logger.LogInformation("Seeding branch inventory (ingredients and menu items)...");
                await inventorySeedService.SeedBranchInventoryAsync();
                _logger.LogInformation("Branch inventory seeded successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error seeding branch access data (may already be seeded or MongoDB not ready).");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
