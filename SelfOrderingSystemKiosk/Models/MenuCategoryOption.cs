namespace SelfOrderingSystemKiosk.Models
{
    /// <summary>Single menu / kiosk category from configuration (stable Key matches MenuItem.Category).</summary>
    public class MenuCategoryOption
    {
        public string Key { get; set; } = "";

        public string DisplayName { get; set; } = "";

        public string DefaultImage { get; set; } = "/images/wings.png";

        public bool ShowInKiosk { get; set; } = true;

        public int SortOrder { get; set; }

        /// <summary>Optional tab image under wwwroot, e.g. /images/wings.png</summary>
        public string? TabImageUrl { get; set; }

        /// <summary>Bootstrap Icons suffix only, e.g. basket → class bi bi-basket</summary>
        public string? TabIconClass { get; set; }
    }

    public class MenuCategoriesSettings
    {
        public List<MenuCategoryOption> Categories { get; set; } = new();
    }
}
