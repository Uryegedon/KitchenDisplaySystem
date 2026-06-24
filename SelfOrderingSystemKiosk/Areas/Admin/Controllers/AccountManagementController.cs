using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SelfOrderingSystemKiosk.Areas.Admin.Models;
using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;

namespace SelfOrderingSystemKiosk.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Owner,BranchManager")]
    public class AccountManagementController : Controller
    {
        private const int MinimumPasswordLength = 10;
        private readonly UserService _userService;
        private readonly BranchService _branchService;
        private readonly ManagementLogService _managementLogs;

        public AccountManagementController(UserService userService, BranchService branchService, ManagementLogService managementLogs)
        {
            _userService = userService;
            _branchService = branchService;
            _managementLogs = managementLogs;
        }

        // GET: Admin/AccountManagement
        public async Task<IActionResult> Index()
        {
            ViewData["Title"] = "User Management";
            var isOwner = User.HasAllBranchAccess();
            ViewBag.CanCreateUsers = isOwner;
            ViewBag.CanEditUsers = isOwner;
            ViewBag.CanResetPasswords = isOwner || User.IsBranchManager();

            List<AdminUser> users;
            if (isOwner)
            {
                users = await _userService.GetAllUsersAsync();
            }
            else
            {
                var branchId = User.GetBranchId();
                if (string.IsNullOrWhiteSpace(branchId))
                    return Forbid();

                users = (await _userService.GetUsersByBranchAsync(branchId.Trim()))
                    .Where(u => !string.Equals(u.Role, UserRoles.Owner, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            await PopulateBranchesAsync();
            return View(users);
        }

        // GET: Admin/AccountManagement/Create
        [Authorize(Roles = "Owner")]
        public async Task<IActionResult> Create()
        {
            ViewData["Title"] = "Create User";
            ViewBag.Roles = GetUserRoles();
            await PopulateBranchesAsync();
            return View();
        }

        // POST: Admin/AccountManagement/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Owner")]
        public async Task<IActionResult> Create(AdminUser user)
        {
            ViewData["Title"] = "Create User";
            ViewBag.Roles = GetUserRoles();
            await PopulateBranchesAsync();
            ModelState.Remove(nameof(AdminUser.Id));
            NormalizeOptionalEmail(user);

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
            else if (user.Password.Length < MinimumPasswordLength)
            {
                ModelState.AddModelError("Password", $"Password must be at least {MinimumPasswordLength} characters long.");
            }

            if (string.IsNullOrWhiteSpace(user.FullName))
            {
                ModelState.AddModelError("FullName", "Full name is required.");
            }

            await ValidateBranchAssignmentAsync(user);

            if (!ModelState.IsValid)
            {
                return View(user);
            }

            // Hash the password before saving
            user.Password = BCrypt.Net.BCrypt.HashPassword(user.Password);
            
            await _userService.CreateUserAsync(user);
            await _managementLogs.RecordAsync(
                "Created",
                "User",
                $"Created user {user.Username}",
                user.Id,
                user.Username,
                $"Role: {user.Role}",
                user.BranchId,
                User.GetUsername(),
                category: "User");

            TempData["Success"] = "User created successfully!";
            return RedirectToAction("Index");
        }

        // GET: Admin/AccountManagement/Edit/{id}
        [Authorize(Roles = "Owner")]
        public async Task<IActionResult> Edit(string id)
        {
            ViewData["Title"] = "Edit User";
            ViewBag.Roles = GetUserRoles();
            await PopulateBranchesAsync();

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
        [Authorize(Roles = "Owner")]
        public async Task<IActionResult> Edit(string id, AdminUser user)
        {
            ViewData["Title"] = "Edit User";
            ViewBag.Roles = GetUserRoles();
            await PopulateBranchesAsync();
            id = string.IsNullOrWhiteSpace(id) ? user.Id : id.Trim();
            user.Id = id;

            ModelState.Remove(nameof(AdminUser.Id));
            ModelState.Remove(nameof(AdminUser.Password));
            NormalizeOptionalEmail(user);

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

            await ValidateBranchAssignmentAsync(user);

            if (!ModelState.IsValid)
            {
                return View(user);
            }

            // Preserve the existing password
            user.Password = existingUser.Password;
            
            var updated = await _userService.UpdateUserAsync(user);
            if (!updated)
            {
                TempData["Error"] = "User changes were not saved. Please refresh and try again.";
                return RedirectToAction("Edit", new { id });
            }
            await _managementLogs.RecordAsync(
                "Updated",
                "User",
                $"Updated user {user.Username}",
                user.Id,
                user.Username,
                $"Role: {existingUser.Role} -> {user.Role}",
                user.BranchId,
                User.GetUsername(),
                category: "User");

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
            if (!CanResetPasswordFor(user))
                return Forbid();

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
            if (!CanResetPasswordFor(user))
                return Forbid();

            if (string.IsNullOrWhiteSpace(model.NewPassword))
            {
                ModelState.AddModelError("NewPassword", "New password is required.");
            }
            else if (model.NewPassword.Length < MinimumPasswordLength)
            {
                ModelState.AddModelError("NewPassword", $"Password must be at least {MinimumPasswordLength} characters long.");
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
            await _managementLogs.RecordAsync(
                "Password changed",
                "User",
                $"Changed password for {user.Username}",
                user.Id,
                user.Username,
                "Password hash updated",
                user.BranchId,
                User.GetUsername(),
                category: "User",
                severity: "Warning");

            TempData["Success"] = "Password changed successfully!";
            return RedirectToAction("Index");
        }

        // GET: Admin/AccountManagement/Delete/{id}
        [Authorize(Roles = "Owner")]
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
        [Authorize(Roles = "Owner")]
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
            await _managementLogs.RecordAsync(
                "Deleted",
                "User",
                $"Deleted user {user.Username}",
                user.Id,
                user.Username,
                $"Role: {user.Role}",
                user.BranchId,
                User.GetUsername(),
                category: "User",
                severity: "Warning");

            TempData["Success"] = "User deleted successfully!";
            return RedirectToAction("Index");
        }

        private List<string> GetUserRoles()
        {
            return new List<string>
            {
                UserRoles.Owner,
                UserRoles.BranchManager,
                UserRoles.Kitchen
            };
        }

        private bool CanResetPasswordFor(AdminUser user)
        {
            if (User.HasAllBranchAccess())
                return true;

            if (!User.IsBranchManager())
                return false;

            if (string.Equals(user.Role, UserRoles.Owner, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(user.Role, UserRoles.BranchManager, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var managerBranchId = User.GetBranchId();
            return !string.IsNullOrWhiteSpace(managerBranchId) &&
                   !string.IsNullOrWhiteSpace(user.BranchId) &&
                   string.Equals(managerBranchId.Trim(), user.BranchId.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private async Task PopulateBranchesAsync()
        {
            List<Branch> branches;
            if (User.HasAllBranchAccess())
            {
                branches = await _branchService.GetActiveBranchesAsync();
            }
            else
            {
                var branchId = User.GetBranchId();
                var branch = string.IsNullOrWhiteSpace(branchId)
                    ? null
                    : await _branchService.GetByIdAsync(branchId.Trim());
                branches = branch?.IsActive == true
                    ? new List<Branch> { branch }
                    : new List<Branch>();
            }

            ViewBag.Branches = branches;
            ViewBag.BranchNames = branches.ToDictionary(
                b => b.Id,
                b => string.IsNullOrWhiteSpace(b.BranchCode)
                    ? b.BranchName
                    : $"{b.BranchName} ({b.BranchCode})",
                StringComparer.OrdinalIgnoreCase);
        }

        private async Task ValidateBranchAssignmentAsync(AdminUser user)
        {
            if (string.Equals(user.Role, UserRoles.Owner, StringComparison.OrdinalIgnoreCase))
            {
                user.BranchId = null;
                return;
            }

            if (string.IsNullOrWhiteSpace(user.BranchId))
            {
                ModelState.AddModelError("BranchId", "Choose a branch for this role.");
                return;
            }

            var branch = await _branchService.GetByIdAsync(user.BranchId.Trim());
            if (branch == null || !branch.IsActive)
            {
                ModelState.AddModelError("BranchId", "Choose an active branch.");
                return;
            }

            user.BranchId = branch.Id;
        }

        private void NormalizeOptionalEmail(AdminUser user)
        {
            if (!string.IsNullOrWhiteSpace(user.Email))
            {
                user.Email = user.Email.Trim();
                return;
            }

            user.Email = string.Empty;
            ModelState.Remove(nameof(AdminUser.Email));
        }
    }

    public class ChangePasswordViewModel
    {
        public string UserId { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
