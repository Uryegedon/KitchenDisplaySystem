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
        public async Task<IActionResult> Index(string? range = "week", string? startDate = null, string? endDate = null)
        {
            ViewData["Title"] = "Stock history";

            // Get user's branch context
            var userBranchId = User.GetBranchId();
            var isOwner = User.HasAllBranchAccess();

            // Get branch info for display
            Branch? userBranch = null;
            if (!string.IsNullOrEmpty(userBranchId))
            {
                userBranch = await _branchService.GetByIdAsync(userBranchId);
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
                start = parsedStart.Date;
                end = parsedEnd.Date.AddDays(1);
                if (end <= start)
                    end = start.AddDays(1);
            }
            else
            {
                var now = DateTime.UtcNow;
                (start, end) = range switch
                {
                    "week" => (now.Date.AddDays(-(int)now.DayOfWeek + 1), now.Date.AddDays(8 - (int)now.DayOfWeek)),
                    "month" => (new DateTime(now.Year, now.Month, 1), new DateTime(now.Year, now.Month, 1).AddMonths(1)),
                    "year" => (new DateTime(now.Year, 1, 1), new DateTime(now.Year + 1, 1, 1)),
                    _ => (now.Date.AddDays(-(int)now.DayOfWeek + 1), now.Date.AddDays(8 - (int)now.DayOfWeek))
                };
            }
            var movements = await _movementService.GetRecentAsync(start, end, 1000);
            
            // Filter movements by branch if not owner
            if (!isOwner)
            {
                movements = string.IsNullOrWhiteSpace(userBranchId)
                    ? new List<StockMovement>()
                    : movements
                        .Where(m =>
                            string.Equals(m.BranchId, userBranchId, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(m.BranchId, string.Empty, StringComparison.Ordinal))
                        .ToList();
            }
            
            ViewBag.Range = range;
            ViewBag.StartDate = start.ToString("yyyy-MM-dd");
            ViewBag.EndDate = end.AddDays(-1).ToString("yyyy-MM-dd");
            ViewBag.IsOwner = isOwner;
            return View(movements);
        }
    }
}
