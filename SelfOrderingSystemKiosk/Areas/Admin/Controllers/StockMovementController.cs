using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SelfOrderingSystemKiosk.Services;

namespace SelfOrderingSystemKiosk.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Kitchen")]
    public class StockMovementController : Controller
    {
        private readonly StockMovementService _movementService;

        public StockMovementController(StockMovementService movementService)
        {
            _movementService = movementService;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? range = "week", string? startDate = null, string? endDate = null)
        {
            ViewData["Title"] = "Stock history";
            DateTime start, end;
            if (range == "custom" && !string.IsNullOrEmpty(startDate) && !string.IsNullOrEmpty(endDate))
            {
                start = DateTime.Parse(startDate).Date;
                end = DateTime.Parse(endDate).Date.AddDays(1);
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
            ViewBag.Range = range;
            ViewBag.StartDate = start.ToString("yyyy-MM-dd");
            ViewBag.EndDate = end.AddDays(-1).ToString("yyyy-MM-dd");
            return View(movements);
        }
    }
}
