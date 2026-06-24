using SelfOrderingSystemKiosk.Services;

namespace SelfOrderingSystemKiosk.Services
{
    /// <summary>
    /// Reconciles stock-driven availability after a restart. Recipes are maintained only
    /// through the menu editor; they must never be inferred from item or ingredient names.
    /// </summary>
    public class MenuAvailabilitySyncHostedService : IHostedService
    {
        private readonly MenuItemService _menuItems;
        private readonly ILogger<MenuAvailabilitySyncHostedService> _logger;

        public MenuAvailabilitySyncHostedService(
            MenuItemService menuItems,
            ILogger<MenuAvailabilitySyncHostedService> logger)
        {
            _menuItems = menuItems;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _menuItems.SyncAllAvailabilityAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to reconcile menu availability from ingredient stock.");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
