using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using SelfOrderingSystemKiosk.Models;

namespace SelfOrderingSystemKiosk.Controllers
{
    public class HomeController : Controller
    {
        [Route("/Home/Error")]
        [AllowAnonymous]
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



