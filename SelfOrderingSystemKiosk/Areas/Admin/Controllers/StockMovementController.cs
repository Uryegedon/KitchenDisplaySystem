using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;
using SelfOrderingSystemKiosk.Areas.Admin.Models;

namespace SelfOrderingSystemKiosk.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Owner,BranchManager")]
    public class StockMovementController : Controller
    {
        private readonly StockMovementService _movementService;
        private readonly ManagementLogService _managementLogService;
        private readonly BranchService _branchService;

        public StockMovementController(
            StockMovementService movementService,
            ManagementLogService managementLogService,
            BranchService branchService)
        {
            _movementService = movementService;
            _managementLogService = managementLogService;
            _branchService = branchService;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? range = "week", string? startDate = null, string? endDate = null, string? branchFilter = null)
        {
            ViewData["Title"] = "All Logs";

            // Get user's branch context
            var userBranchId = User.GetBranchId();
            var isOwner = User.HasAllBranchAccess();
            if (!isOwner && string.IsNullOrWhiteSpace(userBranchId))
                return Forbid();

            var allBranches = isOwner ? await _branchService.GetAllAsync() : new List<Branch>();
            var effectiveBranchId = userBranchId;
            if (isOwner)
            {
                if (string.IsNullOrWhiteSpace(branchFilter) &&
                    Request.Cookies.TryGetValue("KdsAdminBranchFilter", out var savedBranchFilter))
                {
                    branchFilter = savedBranchFilter;
                }

                effectiveBranchId = string.Equals(branchFilter, "all", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : string.IsNullOrWhiteSpace(branchFilter) ? null : branchFilter.Trim();
                ViewBag.AllBranches = allBranches;
                ViewBag.BranchFilter = string.IsNullOrWhiteSpace(branchFilter) ? "all" : branchFilter;
                Response.Cookies.Append(
                    "KdsAdminBranchFilter",
                    ViewBag.BranchFilter,
                    new CookieOptions
                    {
                        HttpOnly = true,
                        IsEssential = true,
                        SameSite = SameSiteMode.Lax,
                        Secure = Request.IsHttps,
                        Expires = DateTimeOffset.UtcNow.AddDays(30)
                    });
            }

            // Get branch info for display
            Branch? userBranch = null;
            if (!string.IsNullOrEmpty(effectiveBranchId))
            {
                userBranch = await _branchService.GetByIdAsync(effectiveBranchId);
                ViewData["BranchName"] = userBranch?.BranchName ?? "Unknown Branch";
            }
            else
            {
                ViewData["BranchName"] = "All Branches";
            }

            DateTime start, end;
            if (range == "custom" &&
                DateTime.TryParse(startDate, out var parsedStart) &&
                DateTime.TryParse(endDate, out var parsedEnd))
            {
                (start, end) = AppClock.LocalDateRange(parsedStart, parsedEnd);
            }
            else
            {
                (start, end) = range switch
                {
                    "week" => AppClock.CurrentLocalWeekRange(),
                    "month" => AppClock.CurrentLocalMonthRange(),
                    "year" => AppClock.CurrentLocalYearRange(),
                    _ => AppClock.CurrentLocalWeekRange()
                };
            }
            var movements = await _movementService.GetRecentAsync(
                start,
                end,
                1000,
                effectiveBranchId);
            var managementLogs = await _managementLogService.GetRecentAsync(
                start,
                end,
                1000,
                effectiveBranchId);
            var branchNames = allBranches
                .Where(b => !string.IsNullOrWhiteSpace(b.Id))
                .ToDictionary(b => b.Id, b => b.BranchName, StringComparer.OrdinalIgnoreCase);
            var combined = movements
                .Select(AllLogEntry.FromStockMovement)
                .Concat(managementLogs.Select(AllLogEntry.FromManagementLog))
                .OrderByDescending(l => l.TimestampUtc)
                .Take(1000)
                .ToList();

            foreach (var entry in combined)
            {
                if (!string.IsNullOrWhiteSpace(entry.BranchId) &&
                    branchNames.TryGetValue(entry.BranchId, out var name))
                {
                    entry.BranchName = name;
                }
            }
            
            ViewBag.Range = range;
            ViewBag.StartDate = AppClock.ToLocal(start).ToString("yyyy-MM-dd");
            ViewBag.EndDate = AppClock.ToLocal(end).AddDays(-1).ToString("yyyy-MM-dd");
            ViewBag.IsOwner = isOwner;
            return View(combined);
        }
    }
}
