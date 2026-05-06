using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SelfOrderingSystemKiosk.Areas.Admin.Models;
using SelfOrderingSystemKiosk.Areas.Customer.Models;
using SelfOrderingSystemKiosk.Services;

namespace SelfOrderingSystemKiosk.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Owner")]
    public class BranchesController : Controller
    {
        private readonly BranchService _branchService;
        private readonly OrderService _orderService;
        private readonly TableOrderingSessionService _tableOrderingSessions;
        private readonly TableRegistryService _tableRegistry;

        public BranchesController(
            BranchService branchService,
            OrderService orderService,
            TableOrderingSessionService tableOrderingSessions,
            TableRegistryService tableRegistry)
        {
            _branchService = branchService;
            _orderService = orderService;
            _tableOrderingSessions = tableOrderingSessions;
            _tableRegistry = tableRegistry;
        }

        public async Task<IActionResult> Index(string message = null)
        {
            ViewData["Title"] = "Branches";
            ViewBag.Message = message;
            var branches = await _branchService.GetAllAsync();
            return View(branches);
        }

        public async Task<IActionResult> Overview(string branchId = null, string period = "today")
        {
            ViewData["Title"] = "Branch Overview";
            ViewBag.SelectedBranchId = branchId;
            ViewBag.SelectedPeriod = period;

            var allBranches = await _branchService.GetAllAsync();
            ViewBag.AllBranches = allBranches;

            var now = DateTime.UtcNow;
            DateTime startUtc, endUtc;
            string periodLabel;

            switch (period.ToLowerInvariant())
            {
                case "yesterday":
                    startUtc = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-1);
                    endUtc = startUtc.AddDays(1);
                    periodLabel = "Yesterday";
                    break;
                case "week":
                    startUtc = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-(int)now.DayOfWeek);
                    endUtc = startUtc.AddDays(7);
                    periodLabel = "This Week";
                    break;
                case "month":
                    startUtc = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                    endUtc = startUtc.AddMonths(1);
                    periodLabel = "This Month";
                    break;
                case "today":
                default:
                    startUtc = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);
                    endUtc = startUtc.AddDays(1);
                    periodLabel = "Today";
                    break;
            }

            ViewBag.PeriodLabel = periodLabel;

            var allOrders = await _orderService.GetByDateRangeHalfOpenAsync(startUtc, endUtc);
            
            // Group orders by branch if branchId is specified, otherwise show all
            if (!string.IsNullOrEmpty(branchId))
            {
                var branch = await _branchService.GetByIdAsync(branchId);
                ViewBag.SelectedBranch = branch;
                var branchOrders = allOrders.Where(o => string.Equals(o.BranchId, branchId, StringComparison.OrdinalIgnoreCase)).ToList();
                ViewBag.BranchOrders = branchOrders;
                CalculateBranchStats(branchId, branch, branchOrders, allBranches.Count);
            }
            else
            {
                ViewBag.SelectedBranch = null;
                ViewBag.BranchOrders = allOrders;
                
                // Calculate stats for each branch
                var branchStats = new List<BranchOverviewStats>();
                foreach (var b in allBranches)
                {
                    var branchOrders = allOrders.Where(o => string.Equals(o.BranchId, b.Id, StringComparison.OrdinalIgnoreCase)).ToList();
                    var stats = CalculateBranchStats(b.Id, b, branchOrders, allBranches.Count);
                    branchStats.Add(stats);
                }
                ViewBag.BranchStats = branchStats
                    .OrderByDescending(s => s.TotalOrders)
                    .ThenBy(s => s.BranchName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return View();
        }

        public async Task<IActionResult> Seats(string? branchId = null)
        {
            ViewData["Title"] = "Branch Seat Availability";
            var allBranches = await _branchService.GetAllAsync();
            ViewBag.AllBranches = allBranches;
            ViewBag.SelectedBranchId = branchId;
            ViewBag.SelectedBranch = string.IsNullOrWhiteSpace(branchId)
                ? null
                : allBranches.FirstOrDefault(b => b.Id == branchId);

            var sessions = await _tableOrderingSessions.GetAllAsync();
            var registered = await _tableRegistry.GetAllAsync();
            var tableNumbers = registered.Select(t => t.TableNumber)
                .Concat(new[] { "1", "2", "3", "4", "5", "6", "7" })
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => int.TryParse(t, out var n) ? n : int.MaxValue)
                .ThenBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var rows = tableNumbers.Select(table =>
            {
                var session = sessions.FirstOrDefault(s => string.Equals(s.TableNumber, table, StringComparison.OrdinalIgnoreCase));
                var registeredTable = registered.FirstOrDefault(t => string.Equals(t.TableNumber, table, StringComparison.OrdinalIgnoreCase));
                return new SeatAvailabilityRow
                {
                    TableNumber = table,
                    Floor = registeredTable?.Floor ?? "",
                    IsUnavailable = session?.IsOrderingOpen == true,
                    UpdatedAtUtc = session?.UpdatedAtUtc ?? registeredTable?.UpdatedAtUtc
                };
            }).ToList();

            ViewBag.AvailableCount = rows.Count(r => !r.IsUnavailable);
            ViewBag.UnavailableCount = rows.Count(r => r.IsUnavailable);
            return View(rows);
        }

        private BranchOverviewStats CalculateBranchStats(string branchId, Branch branch, List<Order> orders, int totalBranches)
        {
            var billableOrders = orders.Where(o => o.Total > 0).ToList();
            var stats = new BranchOverviewStats
            {
                BranchId = branchId,
                BranchName = branch?.BranchName ?? "Unknown",
                BranchCode = branch?.BranchCode ?? "N/A",
                IsActive = branch?.IsActive ?? false,
                TotalOrders = orders.Count,
                BillableOrders = billableOrders.Count,
                TotalRevenue = billableOrders.Sum(o => o.Total),
                Subtotal = billableOrders.Sum(o => o.Subtotal),
                Tax = billableOrders.Sum(o => o.Tax),
                DineInOrders = orders.Count(o => string.Equals(o.DiningType, "DineIn", StringComparison.OrdinalIgnoreCase)),
                TakeOutOrders = orders.Count(o => string.Equals(o.DiningType, "TakeOut", StringComparison.OrdinalIgnoreCase)),
                KioskOrders = orders.Count(o => string.Equals(o.OrderChannel, "Kiosk", StringComparison.OrdinalIgnoreCase)),
                QrOrders = orders.Count(o => string.Equals(o.OrderChannel, "Qr", StringComparison.OrdinalIgnoreCase)),
                AlaCarteOrders = orders.Count(o => string.Equals(o.OrderType, "AlaCarte", StringComparison.OrdinalIgnoreCase)),
                UnlimitedOrders = orders.Count(o => string.Equals(o.OrderType, "Unlimited", StringComparison.OrdinalIgnoreCase))
            };
            return stats;
        }

        public IActionResult Create()
        {
            ViewData["Title"] = "Add Branch";
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Branch branch)
        {
            ViewData["Title"] = "Add Branch";

            if (string.IsNullOrWhiteSpace(branch.BranchCode))
            {
                ModelState.AddModelError("BranchCode", "Branch code is required.");
            }
            else
            {
                var isUnique = await _branchService.IsBranchCodeUniqueAsync(branch.BranchCode);
                if (!isUnique)
                {
                    ModelState.AddModelError("BranchCode", "A branch with this code already exists.");
                }
            }

            if (string.IsNullOrWhiteSpace(branch.BranchName))
            {
                ModelState.AddModelError("BranchName", "Branch name is required.");
            }

            if (!ModelState.IsValid)
            {
                return View(branch);
            }

            await _branchService.CreateAsync(branch);
            return RedirectToAction("Index", new { message = "Branch created successfully!" });
        }

        public async Task<IActionResult> Edit(string id)
        {
            ViewData["Title"] = "Edit Branch";
            var branch = await _branchService.GetByIdAsync(id);
            if (branch == null)
            {
                return RedirectToAction("Index", new { message = "Branch not found." });
            }
            return View(branch);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, Branch branch)
        {
            ViewData["Title"] = "Edit Branch";

            if (id != branch.Id)
            {
                return RedirectToAction("Index", new { message = "Invalid branch ID." });
            }

            var existing = await _branchService.GetByIdAsync(id);
            if (existing == null)
            {
                return RedirectToAction("Index", new { message = "Branch not found." });
            }

            if (string.IsNullOrWhiteSpace(branch.BranchCode))
            {
                ModelState.AddModelError("BranchCode", "Branch code is required.");
            }
            else if (!await _branchService.IsBranchCodeUniqueAsync(branch.BranchCode, id))
            {
                ModelState.AddModelError("BranchCode", "A branch with this code already exists.");
            }

            if (string.IsNullOrWhiteSpace(branch.BranchName))
            {
                ModelState.AddModelError("BranchName", "Branch name is required.");
            }

            if (!ModelState.IsValid)
            {
                return View(branch);
            }

            branch.CreatedAt = existing.CreatedAt;
            await _branchService.UpdateAsync(branch);
            return RedirectToAction("Index", new { message = "Branch updated successfully!" });
        }

        public async Task<IActionResult> Delete(string id)
        {
            ViewData["Title"] = "Delete Branch";
            var branch = await _branchService.GetByIdAsync(id);
            if (branch == null)
            {
                return RedirectToAction("Index", new { message = "Branch not found." });
            }
            return View(branch);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            await _branchService.DeleteAsync(id);
            return RedirectToAction("Index", new { message = "Branch deleted successfully!" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleStatus(string id)
        {
            var branch = await _branchService.GetByIdAsync(id);
            if (branch != null)
            {
                branch.IsActive = !branch.IsActive;
                await _branchService.UpdateAsync(branch);
                return Json(new { success = true, isActive = branch.IsActive });
            }
            return Json(new { success = false, message = "Branch not found." });
        }
    }

    public class BranchOverviewStats
    {
        public string BranchId { get; set; }
        public string BranchName { get; set; }
        public string BranchCode { get; set; }
        public bool IsActive { get; set; }
        public int TotalOrders { get; set; }
        public int BillableOrders { get; set; }
        public decimal TotalRevenue { get; set; }
        public decimal Subtotal { get; set; }
        public decimal Tax { get; set; }
        public int DineInOrders { get; set; }
        public int TakeOutOrders { get; set; }
        public int KioskOrders { get; set; }
        public int QrOrders { get; set; }
        public int AlaCarteOrders { get; set; }
        public int UnlimitedOrders { get; set; }
    }

    public class SeatAvailabilityRow
    {
        public string TableNumber { get; set; } = string.Empty;
        public string Floor { get; set; } = string.Empty;
        public bool IsUnavailable { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
    }
}
