using Microsoft.AspNetCore.Mvc;
using SelfOrderingSystemKiosk.Models;

namespace SelfOrderingSystemKiosk.Controllers
{
    public class HomeController : Controller
    {
        [Route("/Home/Error")]
        public IActionResult Error()
        {
            var errorViewModel = new ErrorViewModel
            {
                RequestId = HttpContext.TraceIdentifier
            };
            return View("~/Areas/Customer/Views/Shared/Error.cshtml", errorViewModel);
        }
    }
}



