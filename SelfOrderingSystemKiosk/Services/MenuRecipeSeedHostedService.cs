using SelfOrderingSystemKiosk.Services;

namespace SelfOrderingSystemKiosk.Services
{
    public class MenuRecipeSeedHostedService : IHostedService
    {
        private readonly MenuItemService _menuItems;
        private readonly ILogger<MenuRecipeSeedHostedService> _logger;

        public MenuRecipeSeedHostedService(
            MenuItemService menuItems,
            ILogger<MenuRecipeSeedHostedService> logger)
        {
            _menuItems = menuItems;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _menuItems.SeedRecipesFromMenuItemNamesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to seed menu item recipes from names.");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
