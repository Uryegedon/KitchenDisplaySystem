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
        private readonly UserService _userService;
        private readonly ManagementLogService _managementLogs;

        public BranchesController(
            BranchService branchService,
            OrderService orderService,
            TableOrderingSessionService tableOrderingSessions,
            TableRegistryService tableRegistry,
            UserService userService,
            ManagementLogService managementLogs)
        {
            _branchService = branchService;
            _orderService = orderService;
            _tableOrderingSessions = tableOrderingSessions;
            _tableRegistry = tableRegistry;
            _userService = userService;
            _managementLogs = managementLogs;
        }

        public async Task<IActionResult> Index(string? message = null)
        {
            ViewData["Title"] = "Branches";
            ViewBag.Message = message;
            var branches = await _branchService.GetAllAsync();
            return View(branches);
        }

        public async Task<IActionResult> Overview(string? branchId = null, string period = "today")
        {
            ViewData["Title"] = "Branch Overview";
            ViewBag.SelectedBranchId = branchId;
            ViewBag.SelectedPeriod = period;

            var allBranches = await _branchService.GetAllAsync();
            ViewBag.AllBranches = allBranches;
            var branchManagers = await _userService.GetBranchManagersAsync();
            var managerNamesByBranch = branchManagers
                .Where(manager => !string.IsNullOrWhiteSpace(manager.BranchId))
                .GroupBy(manager => manager.BranchId!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => string.Join(", ", group
                        .Select(manager => string.IsNullOrWhiteSpace(manager.FullName) ? manager.Username : manager.FullName)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)),
                    StringComparer.OrdinalIgnoreCase);
            ViewBag.ManagerNamesByBranch = managerNamesByBranch;

            var now = AppClock.LocalNow;
            DateTime startUtc, endUtc;
            string periodLabel;

            switch (period.ToLowerInvariant())
            {
                case "yesterday":
                    (startUtc, endUtc) = AppClock.LocalDateRange(now.Date.AddDays(-1));
                    periodLabel = "Yesterday";
                    break;
                case "week":
                    (startUtc, endUtc) = AppClock.CurrentLocalWeekRange();
                    periodLabel = "This Week";
                    break;
                case "month":
                    (startUtc, endUtc) = AppClock.CurrentLocalMonthRange();
                    periodLabel = "This Month";
                    break;
                case "today":
                default:
                    (startUtc, endUtc) = AppClock.LocalDateRange(now.Date);
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
                ViewBag.SelectedBranchManagers = managerNamesByBranch.TryGetValue(branchId.Trim(), out var managerNames) && !string.IsNullOrWhiteSpace(managerNames)
                    ? managerNames
                    : "Unassigned";
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

        public IActionResult Seats(string? branchId = null)
        {
            TempData["Message"] = "Seat availability is temporarily disabled.";
            return RedirectToAction(nameof(Overview), new { branchId });
        }

        private BranchOverviewStats CalculateBranchStats(string branchId, Branch? branch, List<Order> orders, int totalBranches)
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
                Cost = billableOrders.Sum(o => o.OrderCost),
                Profit = billableOrders.Sum(o => o.Profit),
                DineInOrders = orders.Count(o => IsNormalizedMatch(o.DiningType, "dinein")),
                TakeOutOrders = orders.Count(o => IsNormalizedMatch(o.DiningType, "takeout")),
                KioskOrders = orders.Count(o => IsNormalizedMatch(o.OrderChannel, "kiosk")),
                QrOrders = orders.Count(o => IsNormalizedMatch(o.OrderChannel, "qr")),
                AlaCarteOrders = orders.Count(o => IsNormalizedMatch(o.OrderType, "alacarte")),
                UnlimitedOrders = orders.Count(o => IsNormalizedMatch(o.OrderType, "unlimited"))
            };
            return stats;
        }

        private static bool IsNormalizedMatch(string? value, string expected)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var normalized = new string(value
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
            return normalized == expected;
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

            ModelState.Remove(nameof(Branch.Id));

            if (string.IsNullOrWhiteSpace(branch.BranchCode))
            {
                ModelState.AddModelError("BranchCode", "Branch code is required.");
            }
            else
            {
                branch.BranchCode = branch.BranchCode.Trim();
                await ValidateBranchReferenceDigitAsync(branch.BranchCode);

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
            else
            {
                branch.BranchName = branch.BranchName.Trim();
            }

            if (!ModelState.IsValid)
            {
                return View(branch);
            }

            await _branchService.CreateAsync(branch);
            await _managementLogs.RecordAsync(
                "Created",
                "Branch",
                $"Created branch {branch.BranchName}",
                branch.Id,
                branch.BranchName,
                $"Code: {branch.BranchCode}",
                branch.Id,
                User.GetUsername(),
                category: "Branch");
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
            else
            {
                branch.BranchCode = branch.BranchCode.Trim();
                await ValidateBranchReferenceDigitAsync(branch.BranchCode, id);

                if (!await _branchService.IsBranchCodeUniqueAsync(branch.BranchCode, id))
                {
                    ModelState.AddModelError("BranchCode", "A branch with this code already exists.");
                }
            }

            if (string.IsNullOrWhiteSpace(branch.BranchName))
            {
                ModelState.AddModelError("BranchName", "Branch name is required.");
            }
            else
            {
                branch.BranchName = branch.BranchName.Trim();
            }

            if (!ModelState.IsValid)
            {
                return View(branch);
            }

            branch.CreatedAt = existing.CreatedAt;
            await _branchService.UpdateAsync(branch);
            await _managementLogs.RecordAsync(
                "Updated",
                "Branch",
                $"Updated branch {branch.BranchName}",
                branch.Id,
                branch.BranchName,
                $"Code: {existing.BranchCode} -> {branch.BranchCode}",
                branch.Id,
                User.GetUsername(),
                category: "Branch");
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
            var branch = await _branchService.GetByIdAsync(id);
            if (branch == null)
            {
                return RedirectToAction("Index", new { message = "Branch not found." });
            }

            if (branch.IsActive)
            {
                branch.IsActive = false;
                await _branchService.UpdateAsync(branch);
            }

            await _managementLogs.RecordAsync(
                "Deactivated",
                "Branch",
                $"Deactivated branch {branch.BranchName}",
                id,
                branch.BranchName,
                $"Code: {branch.BranchCode}",
                id,
                User.GetUsername(),
                category: "Branch",
                severity: "Warning");
            return RedirectToAction("Index", new { message = "Branch deactivated. Historical data was kept." });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleStatus(string id)
        {
            var branch = await _branchService.GetByIdAsync(id);
            if (branch != null)
            {
                if (!branch.IsActive)
                {
                    var activationError = await GetReferenceDigitValidationErrorAsync(branch.BranchCode, id);
                    if (!string.IsNullOrWhiteSpace(activationError))
                    {
                        return Json(new { success = false, message = activationError });
                    }
                }

                branch.IsActive = !branch.IsActive;
                await _branchService.UpdateAsync(branch);
                await _managementLogs.RecordAsync(
                    branch.IsActive ? "Activated" : "Deactivated",
                    "Branch",
                    $"{(branch.IsActive ? "Activated" : "Deactivated")} branch {branch.BranchName}",
                    branch.Id,
                    branch.BranchName,
                    $"Active: {!branch.IsActive} -> {branch.IsActive}",
                    branch.Id,
                    User.GetUsername(),
                    category: "Branch",
                    severity: branch.IsActive ? "Info" : "Warning");
                return Json(new { success = true, isActive = branch.IsActive });
            }
            return Json(new { success = false, message = "Branch not found." });
        }

        private async Task ValidateBranchReferenceDigitAsync(string branchCode, string? excludeId = null)
        {
            var error = await GetReferenceDigitValidationErrorAsync(branchCode, excludeId);
            if (!string.IsNullOrWhiteSpace(error))
            {
                ModelState.AddModelError("BranchCode", error);
            }
        }

        private async Task<string?> GetReferenceDigitValidationErrorAsync(string branchCode, string? excludeId = null)
        {
            if (!branchCode.Any(char.IsDigit))
            {
                return "Branch code must include a reference digit, for example BR001, BR002, or NOVA9. The last digit is used for order references.";
            }

            var referenceDigit = BranchService.GetReferenceDigit(branchCode);
            if (!referenceDigit.HasValue)
            {
                return "Branch code must include a reference digit, for example BR001, BR002, or NOVA9. The last digit is used for order references.";
            }

            var existing = await _branchService.GetBranchUsingReferenceDigitAsync(referenceDigit.Value, excludeId);
            if (existing != null)
            {
                return $"Reference digit {referenceDigit.Value} is already used by {existing.BranchName} ({existing.BranchCode}). Choose another digit.";
            }

            return null;
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
        public decimal Cost { get; set; }
        public decimal Profit { get; set; }
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
