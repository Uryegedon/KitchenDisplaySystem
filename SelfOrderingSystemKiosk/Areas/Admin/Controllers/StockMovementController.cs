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
    [Authorize(Roles = "Owner,BranchManager,Admin")]
    public class StockMovementController : Controller
    {
        private readonly StockMovementService _movementService;
        private readonly BranchService _branchService;

        public StockMovementController(StockMovementService movementService, BranchService branchService)
        {
            _movementService = movementService;
            _branchService = branchService;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? range = "week", string? startDate = null, string? endDate = null, string? branchFilter = null)
        {
            ViewData["Title"] = "Stock history";

            // Get user's branch context
            var userBranchId = User.GetBranchId();
            var isOwner = User.HasAllBranchAccess();
            if (!isOwner && string.IsNullOrWhiteSpace(userBranchId))
                return Forbid();

            var allBranches = isOwner ? await _branchService.GetAllAsync() : new List<Branch>();
            var effectiveBranchId = userBranchId;
            if (isOwner)
            {
                effectiveBranchId = string.Equals(branchFilter, "all", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : string.IsNullOrWhiteSpace(branchFilter) ? null : branchFilter.Trim();
                ViewBag.AllBranches = allBranches;
                ViewBag.BranchFilter = string.IsNullOrWhiteSpace(branchFilter) ? "all" : branchFilter;
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
            
            ViewBag.Range = range;
            ViewBag.StartDate = AppClock.ToLocal(start).ToString("yyyy-MM-dd");
            ViewBag.EndDate = AppClock.ToLocal(end).AddDays(-1).ToString("yyyy-MM-dd");
            ViewBag.IsOwner = isOwner;
            return View(movements);
        }
    }
}
