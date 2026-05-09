using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using System.Security.Claims;

namespace SelfOrderingSystemKiosk.Controllers
{

    [Area("Admin")]
    public class AccountController : Controller
    {
        private readonly AuthService _authService;
        private readonly UserService _userService;

        public AccountController(AuthService authService, UserService userService)
        {
            _authService = authService;
            _userService = userService;
        }



        [HttpGet]
        [AllowAnonymous]
        public IActionResult Login()
        {
            // If user is already authenticated, redirect to appropriate dashboard
            if (User.Identity?.IsAuthenticated == true)
            {
                var role = User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.Role)?.Value;
                if (role?.Equals("Kitchen", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return RedirectToAction("Index", "Kitchen", new { area = "Kitchen" });
                }
                else
                {
                    return RedirectToAction("Index", "Dashboard", new { area = "Admin" });
                }
            }
            return View();
        }

        [HttpPost]
        [AllowAnonymous]
        public async Task<IActionResult> Login(AdminUser user)
        {
            if (user == null || string.IsNullOrEmpty(user.Username) || string.IsNullOrEmpty(user.Password))
            {
                ViewBag.Error = "Please enter both username and password.";
                return View();
            }

            var existingUser = await _authService.ValidateUserAsync(user.Username, user.Password);

            if (existingUser == null)
            {
                ViewBag.Error = "Invalid username or password.";
                return View();
            }

            // Get user role (default to Admin if not set)
            var userRole = existingUser.Role ?? UserRoles.Admin;

            // Create claims with user information
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, existingUser.Username),
                new Claim(ClaimTypes.Role, userRole),
                new Claim("BranchId", existingUser.BranchId ?? "") // Empty string = Owner/All branches
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

            // Redirect based on user role
            if (userRole.Equals(UserRoles.Kitchen, StringComparison.OrdinalIgnoreCase))
            {
                return RedirectToAction("Index", "Kitchen", new { area = "Kitchen" });
            }
            else if (userRole.Equals(UserRoles.BranchManager, StringComparison.OrdinalIgnoreCase))
            {
                // Branch managers go to their branch-specific dashboard
                return RedirectToAction("Index", "Dashboard", new { area = "Admin" });
            }
            else
            {
                // Owner/Admin goes to overview dashboard
                return RedirectToAction("Index", "Dashboard", new { area = "Admin" });
            }
        }


        [HttpGet]
        [AllowAnonymous]
        public IActionResult AccessDenied()
        {
            return View();
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult ForgotPassword()
        {
            return View();
        }

        [HttpPost]
        [AllowAnonymous]
        public IActionResult ForgotPassword(string email)
        {
            if (string.IsNullOrEmpty(email))
            {
                ViewBag.Error = "Please enter your email address.";
                return View();
            }

            ViewBag.Message = $"A password reset link has been sent to {email}.";
            return View();
        }

        [Authorize]
        public async Task<IActionResult> Logout()
        {
            HttpContext.Session.Clear();
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Login", "Account");
        }

        [Authorize(Roles = "Owner")]
        [HttpGet]
        public IActionResult Signup()
        {
            return View();
        }

        [Authorize(Roles = "Owner")]
        [HttpPost]
        public async Task<IActionResult> Signup(AdminUser user)
        {
            if (!ModelState.IsValid)
                return View(user);

            // Hash password
            user.Password = BCrypt.Net.BCrypt.HashPassword(user.Password);
            await _userService.CreateUserAsync(user);

            TempData["Success"] = "New user registered successfully!";
            return RedirectToAction("Signup");
        }
    }
}
