using Microsoft.Extensions.Options;
using SelfOrderingSystemKiosk.Models;

namespace SelfOrderingSystemKiosk.Services
{
    /// <summary>Single source of truth for menu categories (from appsettings).</summary>
    public class MenuCategoryRegistry
    {
        private readonly IReadOnlyList<MenuCategoryOption> _all;

        public MenuCategoryRegistry(IOptions<MenuCategoriesSettings> options)
        {
            var raw = options.Value?.Categories?
                .Where(c => !string.IsNullOrWhiteSpace(c.Key))
                .ToList() ?? new List<MenuCategoryOption>();

            if (raw.Count == 0)
                raw = GetDefaultCategories();

            _all = raw.OrderBy(c => c.SortOrder).ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public IReadOnlyList<MenuCategoryOption> All => _all;

        public IReadOnlyList<MenuCategoryOption> KioskTabs =>
            _all.Where(c => c.ShowInKiosk).OrderBy(c => c.SortOrder).ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

        public bool IsValidKey(string? key) =>
            !string.IsNullOrWhiteSpace(key) &&
            _all.Any(c => c.Key.Equals(key.Trim(), StringComparison.Ordinal));

        public string GetDefaultImage(string? categoryKey)
        {
            if (string.IsNullOrWhiteSpace(categoryKey))
                return "/images/wings.png";
            var c = _all.FirstOrDefault(x => x.Key.Equals(categoryKey.Trim(), StringComparison.Ordinal));
            return string.IsNullOrEmpty(c?.DefaultImage) ? "/images/wings.png" : c!.DefaultImage;
        }

        private static List<MenuCategoryOption> GetDefaultCategories() =>
            new()
            {
                new MenuCategoryOption
                {
                    Key = "Wings",
                    DisplayName = "Wing Flavors",
                    DefaultImage = "/images/wings.png",
                    ShowInKiosk = true,
                    SortOrder = 1,
                    TabImageUrl = "/images/wings.png"
                },
                new MenuCategoryOption
                {
                    Key = "Wings Ala Carte",
                    DisplayName = "Wings Sets",
                    DefaultImage = "/images/wings.png",
                    ShowInKiosk = true,
                    SortOrder = 2,
                    TabImageUrl = "/images/wings.png"
                },
                new MenuCategoryOption
                {
                    Key = "Sulit Kap Meals",
                    DisplayName = "Sulit Kap Meals",
                    DefaultImage = "/images/wings.png",
                    ShowInKiosk = true,
                    SortOrder = 3,
                    TabIconClass = "egg-fried"
                },
                new MenuCategoryOption
                {
                    Key = "Pasta",
                    DisplayName = "Pasta",
                    DefaultImage = "/images/wings.png",
                    ShowInKiosk = true,
                    SortOrder = 4,
                    TabIconClass = "fork-knife"
                },
                new MenuCategoryOption
                {
                    Key = "Appetizer",
                    DisplayName = "Appetizers",
                    DefaultImage = "/images/appetize.png",
                    ShowInKiosk = true,
                    SortOrder = 5,
                    TabImageUrl = "/images/appetize.png"
                },
                new MenuCategoryOption
                {
                    Key = "Add Ons",
                    DisplayName = "Add-Ons",
                    DefaultImage = "/images/wings.png",
                    ShowInKiosk = true,
                    SortOrder = 6,
                    TabIconClass = "basket"
                },
                new MenuCategoryOption
                {
                    Key = "Kap's Burger",
                    DisplayName = "Kap's Burger",
                    DefaultImage = "/images/wings.png",
                    ShowInKiosk = true,
                    SortOrder = 7,
                    TabIconClass = "bag"
                },
                new MenuCategoryOption
                {
                    Key = "Drinks",
                    DisplayName = "Drinks",
                    DefaultImage = "/images/wings.png",
                    ShowInKiosk = true,
                    SortOrder = 8,
                    TabIconClass = "cup-straw"
                },
                new MenuCategoryOption
                {
                    Key = "Group Add-ons",
                    DisplayName = "Group Add-ons",
                    DefaultImage = "/images/wings.png",
                    ShowInKiosk = true,
                    SortOrder = 9,
                    TabIconClass = "people"
                },
                new MenuCategoryOption
                {
                    Key = "Unlimited Inclusions",
                    DisplayName = "Unlimited Inclusions",
                    DefaultImage = "/images/wings.png",
                    ShowInKiosk = false,
                    SortOrder = 90,
                    TabIconClass = "infinity"
                },
                new MenuCategoryOption
                {
                    Key = "Unavailable",
                    DisplayName = "Unavailable (hidden from kiosk)",
                    DefaultImage = "/images/wings.png",
                    ShowInKiosk = false,
                    SortOrder = 99
                }
            };
    }
}
