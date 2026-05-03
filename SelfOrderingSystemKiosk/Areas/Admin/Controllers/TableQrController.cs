using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SelfOrderingSystemKiosk.Areas.Admin.Models;
using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;

namespace SelfOrderingSystemKiosk.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Kitchen")]
    public class TableQrController : Controller
    {
        private readonly QrCodeService _qrCodeService;
        private readonly IOptions<QrOrderingSettings> _qrSettings;
        private readonly TableRegistryService _tableRegistry;

        public TableQrController(
            QrCodeService qrCodeService,
            IOptions<QrOrderingSettings> qrSettings,
            TableRegistryService tableRegistry)
        {
            _qrCodeService = qrCodeService;
            _qrSettings = qrSettings;
            _tableRegistry = tableRegistry;
        }

        [HttpGet]
        public IActionResult Index()
        {
            var vm = new TableQrIndexViewModel
            {
                PublicSiteUrl = _qrSettings.Value.PublicSiteUrl,
                TablesBulk = "1\n2\n3\n4\n5\n6\n7",
                Floor = ""
            };
            vm.ResolvedBaseUrlPreview = ResolvePublicBaseUrl(vm.PublicSiteUrl);
            return View(vm);
        }

        /// <summary>PNG for one table (for download or embedding).</summary>
        [HttpGet]
        public async Task<IActionResult> Download(string table, string? floor = null, string? publicSiteUrl = null)
        {
            if (string.IsNullOrWhiteSpace(table))
                return BadRequest("Table is required.");

            table = table.Trim();
            if (table.Length > 64)
                return BadRequest("Table value is too long.");

            await _tableRegistry.UpsertAsync(table, floor);

            var baseUrl = ResolvePublicBaseUrl(publicSiteUrl);
            var payload = BuildOrderUrl(baseUrl, table, floor);
            var png = _qrCodeService.GetPngBytes(payload);
            var safeName = SanitizeFileSegment(table);
            return File(png, "image/png", $"qr-table-{safeName}.png");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Print(TableQrIndexViewModel model)
        {
            var tables = ParseTableList(model.TablesBulk);
            if (tables.Count == 0)
            {
                ModelState.AddModelError(nameof(model.TablesBulk), "Please type at least one table number in the box (for example 1, 2, 3 on separate lines).");
                model.ResolvedBaseUrlPreview = ResolvePublicBaseUrl(model.PublicSiteUrl);
                return View("Index", model);
            }

            var baseUrl = ResolvePublicBaseUrl(model.PublicSiteUrl);
            var floor = string.IsNullOrWhiteSpace(model.Floor) ? null : model.Floor.Trim();
            await _tableRegistry.UpsertManyAsync(tables, floor);

            var items = new List<QrPrintItemViewModel>();
            foreach (var t in tables)
            {
                var fullUrl = BuildOrderUrl(baseUrl, t, floor);
                var png = _qrCodeService.GetPngBytes(fullUrl);
                var dataUri = "data:image/png;base64," + Convert.ToBase64String(png);
                var label = string.IsNullOrEmpty(floor) ? $"Table {t}" : $"Floor {floor} · Table {t}";
                items.Add(new QrPrintItemViewModel
                {
                    Table = t,
                    Floor = floor,
                    DataUri = dataUri,
                    FullUrl = fullUrl,
                    Label = label
                });
            }

            var page = new QrPrintPageViewModel
            {
                ResolvedBaseUrl = baseUrl,
                Items = items
            };

            return View("Print", page);
        }

        private string ResolvePublicBaseUrl(string? overrideUrl)
        {
            var o = overrideUrl?.Trim();
            if (!string.IsNullOrEmpty(o) &&
                Uri.TryCreate(o, UriKind.Absolute, out var abs) &&
                (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
            {
                return o.TrimEnd('/');
            }

            var configured = _qrSettings.Value.PublicSiteUrl?.Trim();
            if (!string.IsNullOrEmpty(configured) &&
                Uri.TryCreate(configured, UriKind.Absolute, out var cfg) &&
                (cfg.Scheme == Uri.UriSchemeHttp || cfg.Scheme == Uri.UriSchemeHttps))
            {
                return configured.TrimEnd('/');
            }

            var req = HttpContext.Request;
            return $"{req.Scheme}://{req.Host.Value}".TrimEnd('/');
        }

        private static string BuildOrderUrl(string baseUrl, string table, string? floor)
        {
            var qb = new QueryBuilder();
            qb.Add("table", table);
            if (!string.IsNullOrWhiteSpace(floor))
                qb.Add("floor", floor.Trim());
            return $"{baseUrl.TrimEnd('/')}/Customer/Kiosk/Qr{qb.ToQueryString()}";
        }

        private static List<string> ParseTableList(string? bulk)
        {
            if (string.IsNullOrWhiteSpace(bulk))
                return new List<string>();

            return bulk
                .Split(new[] { ',', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0 && s.Length <= 64)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string SanitizeFileSegment(string table)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                table = table.Replace(c, '-');
            return string.IsNullOrWhiteSpace(table) ? "table" : table;
        }
    }
}
