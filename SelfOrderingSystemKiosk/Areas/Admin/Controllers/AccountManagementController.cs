using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;

namespace SelfOrderingSystemKiosk.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Owner")]
    public class AccountManagementController : Controller
    {
        private readonly UserService _userService;

        public AccountManagementController(UserService userService)
        {
            _userService = userService;
        }

        // GET: Admin/AccountManagement
        public async Task<IActionResult> Index()
        {
            ViewData["Title"] = "User Management";
            var users = await _userService.GetAllUsersAsync();
            return View(users);
        }

        // GET: Admin/AccountManagement/Create
        public IActionResult Create()
        {
            ViewData["Title"] = "Create User";
            ViewBag.Roles = GetUserRoles();
            ViewBag.Branches = new List<dynamic>(); // Will be populated if BranchService is injected
            return View();
        }

        // POST: Admin/AccountManagement/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(AdminUser user)
        {
            ViewData["Title"] = "Create User";
            ViewBag.Roles = GetUserRoles();

            // Validate username uniqueness
            if (!await _userService.IsUsernameUniqueAsync(user.Username))
            {
                ModelState.AddModelError("Username", "This username is already taken.");
            }

            // Validate email uniqueness
            if (!string.IsNullOrEmpty(user.Email) && !await _userService.IsEmailUniqueAsync(user.Email))
            {
                ModelState.AddModelError("Email", "This email is already in use.");
            }

            if (string.IsNullOrWhiteSpace(user.Username))
            {
                ModelState.AddModelError("Username", "Username is required.");
            }

            if (string.IsNullOrWhiteSpace(user.Password))
            {
                ModelState.AddModelError("Password", "Password is required.");
            }

            if (string.IsNullOrWhiteSpace(user.FullName))
            {
                ModelState.AddModelError("FullName", "Full name is required.");
            }

            if (!ModelState.IsValid)
            {
                return View(user);
            }

            // Hash the password before saving
            user.Password = BCrypt.Net.BCrypt.HashPassword(user.Password);
            
            await _userService.CreateUserAsync(user);

            TempData["Success"] = "User created successfully!";
            return RedirectToAction("Index");
        }

        // GET: Admin/AccountManagement/Edit/{id}
        public async Task<IActionResult> Edit(string id)
        {
            ViewData["Title"] = "Edit User";
            ViewBag.Roles = GetUserRoles();

            var user = await _userService.GetByIdAsync(id);
            if (user == null)
            {
                TempData["Error"] = "User not found.";
                return RedirectToAction("Index");
            }

            return View(user);
        }

        // POST: Admin/AccountManagement/Edit/{id}
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, AdminUser user)
        {
            ViewData["Title"] = "Edit User";
            ViewBag.Roles = GetUserRoles();

            if (id != user.Id)
            {
                TempData["Error"] = "Invalid user ID.";
                return RedirectToAction("Index");
            }

            var existingUser = await _userService.GetByIdAsync(id);
            if (existingUser == null)
            {
                TempData["Error"] = "User not found.";
                return RedirectToAction("Index");
            }

            // Validate username uniqueness
            if (!await _userService.IsUsernameUniqueAsync(user.Username, user.Id))
            {
                ModelState.AddModelError("Username", "This username is already taken.");
            }

            // Validate email uniqueness
            if (!string.IsNullOrEmpty(user.Email) && !await _userService.IsEmailUniqueAsync(user.Email, user.Id))
            {
                ModelState.AddModelError("Email", "This email is already in use.");
            }

            if (string.IsNullOrWhiteSpace(user.Username))
            {
                ModelState.AddModelError("Username", "Username is required.");
            }

            if (string.IsNullOrWhiteSpace(user.FullName))
            {
                ModelState.AddModelError("FullName", "Full name is required.");
            }

            if (!ModelState.IsValid)
            {
                return View(user);
            }

            // Preserve the existing password
            user.Password = existingUser.Password;
            
            await _userService.UpdateUserAsync(user);

            TempData["Success"] = "User updated successfully!";
            return RedirectToAction("Index");
        }

        // GET: Admin/AccountManagement/ChangePassword/{id}
        public async Task<IActionResult> ChangePassword(string id)
        {
            ViewData["Title"] = "Change Password";
            
            var user = await _userService.GetByIdAsync(id);
            if (user == null)
            {
                TempData["Error"] = "User not found.";
                return RedirectToAction("Index");
            }

            ViewBag.Username = user.Username;
            ViewBag.FullName = user.FullName;
            return View(new ChangePasswordViewModel { UserId = id });
        }

        // POST: Admin/AccountManagement/ChangePassword
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
        {
            ViewData["Title"] = "Change Password";

            var user = await _userService.GetByIdAsync(model.UserId);
            if (user == null)
            {
                TempData["Error"] = "User not found.";
                return RedirectToAction("Index");
            }

            ViewBag.Username = user.Username;
            ViewBag.FullName = user.FullName;

            if (string.IsNullOrWhiteSpace(model.NewPassword))
            {
                ModelState.AddModelError("NewPassword", "New password is required.");
            }
            else if (model.NewPassword.Length < 6)
            {
                ModelState.AddModelError("NewPassword", "Password must be at least 6 characters long.");
            }

            if (model.NewPassword != model.ConfirmPassword)
            {
                ModelState.AddModelError("ConfirmPassword", "Passwords do not match.");
            }

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            // Hash the new password
            var newPasswordHash = BCrypt.Net.BCrypt.HashPassword(model.NewPassword);
            
            await _userService.ChangePasswordAsync(model.UserId, newPasswordHash);

            TempData["Success"] = "Password changed successfully!";
            return RedirectToAction("Index");
        }

        // GET: Admin/AccountManagement/Delete/{id}
        public async Task<IActionResult> Delete(string id)
        {
            ViewData["Title"] = "Delete User";
            
            var user = await _userService.GetByIdAsync(id);
            if (user == null)
            {
                TempData["Error"] = "User not found.";
                return RedirectToAction("Index");
            }

            return View(user);
        }

        // POST: Admin/AccountManagement/DeleteConfirmed
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            var user = await _userService.GetByIdAsync(id);
            if (user == null)
            {
                TempData["Error"] = "User not found.";
                return RedirectToAction("Index");
            }

            // Prevent deleting the last Owner account
            if (user.Role == UserRoles.Owner)
            {
                var owners = await _userService.GetOwnersAsync();
                if (owners.Count <= 1)
                {
                    TempData["Error"] = "Cannot delete the last Owner account.";
                    return RedirectToAction("Index");
                }
            }

            await _userService.DeleteUserAsync(id);

            TempData["Success"] = "User deleted successfully!";
            return RedirectToAction("Index");
        }

        private List<string> GetUserRoles()
        {
            return new List<string>
            {
                UserRoles.Owner,
                UserRoles.BranchManager,
                UserRoles.Admin,
                UserRoles.Kitchen
            };
        }
    }

    public class ChangePasswordViewModel
    {
        public string UserId { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
