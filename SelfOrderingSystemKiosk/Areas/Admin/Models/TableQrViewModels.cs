using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace SelfOrderingSystemKiosk.Areas.Admin.Models
{
    public class TableQrIndexViewModel
    {
        [Display(Name = "Restaurant website address (only if your IT person asked you to fill this)")]
        public string? PublicSiteUrl { get; set; }

        [Display(Name = "Table numbers (one per line)")]
        public string TablesBulk { get; set; } = "";

        [Display(Name = "Floor or area (optional)")]
        public string? Floor { get; set; }

        [Display(Name = "Branch")]
        public string? BranchId { get; set; }

        /// <summary>Shown on GET; server-resolved URL used when the field above is empty.</summary>
        public string? ResolvedBaseUrlPreview { get; set; }
    }

    public class QrPrintItemViewModel
    {
        public string Table { get; set; } = "";
        public string? Floor { get; set; }
        public string DataUri { get; set; } = "";
        public string FullUrl { get; set; } = "";
        public string Label { get; set; } = "";
    }

    public class QrPrintPageViewModel
    {
        public string ResolvedBaseUrl { get; set; } = "";
        public List<QrPrintItemViewModel> Items { get; set; } = new();
    }
}
