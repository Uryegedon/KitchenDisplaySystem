using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

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
        public async Task<IActionResult> Login(bool logout = false)
        {
            if (logout && User.Identity?.IsAuthenticated == true)
            {
                HttpContext.Session.Clear();
                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return View();
            }

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
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("auth-login")]
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

            // Get user role (default to branch-restricted manager if not set)
            var userRole = existingUser.Role ?? UserRoles.BranchManager;

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
                // Owner goes to overview dashboard
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
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("auth-login")]
        public async Task<IActionResult> ForgotPassword(string account)
        {
            if (string.IsNullOrWhiteSpace(account))
            {
                ViewBag.Error = "Please enter your username or email address.";
                return View();
            }

            var user = await _userService.FindByUsernameOrEmailAsync(account);
            if (user != null)
            {
                var recipients = await _userService.GetPasswordResetRecipientsAsync(user);
                if (recipients.Any())
                {
                    ViewBag.ResetContacts = recipients
                        .Select(r => string.IsNullOrWhiteSpace(r.FullName) ? r.Email : $"{r.FullName} <{r.Email}>")
                        .ToList();
                    ViewBag.ResetMailto = BuildPasswordResetMailto(recipients, user);
                }
            }

            ViewBag.Message = "Ask an owner or your branch manager to reset your password from User Accounts.";
            return View();
        }

        private static string BuildPasswordResetMailto(List<AdminUser> recipients, AdminUser account)
        {
            var to = string.Join(",", recipients.Select(r => r.Email.Trim()));
            var subject = Uri.EscapeDataString("KDS password reset request");
            var body = Uri.EscapeDataString(
                $"Please reset the KDS password for {account.FullName} ({account.Username}).\n\nDo not send a password in plain text unless your store policy allows it. Reset it from Admin > User Accounts > Change Password.");
            return $"mailto:{to}?subject={subject}&body={body}";
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            HttpContext.Session.Clear();
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Login", "Account");
        }

    }
}
