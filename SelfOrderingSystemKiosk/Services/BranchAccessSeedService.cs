using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;
using SelfOrderingSystemKiosk.Areas.Admin.Models;

namespace SelfOrderingSystemKiosk.Services
{
    /// <summary>
    /// Seed data utility for creating initial branch and user data for testing
    /// </summary>
    public class BranchAccessSeedService
    {
        private readonly UserService _userService;
        private readonly BranchService _branchService;

        public BranchAccessSeedService(UserService userService, BranchService branchService)
        {
            _userService = userService;
            _branchService = branchService;
        }

        /// <summary>
        /// Creates seed data for testing branch access control:
        /// - Branch 1: "Main Branch" (code: BR001)
        /// - Branch 2: "Downtown Branch" (code: BR002)
        /// - Owner user: "owner" (access to all branches)
        /// - Branch 1 Manager: "manager1" (access to Main Branch only)
        /// - Branch 2 Manager: "manager2" (access to Downtown Branch only)
        /// </summary>
        public async Task SeedAsync()
        {
            // Check if already seeded
            var existingUsers = await _userService.GetAllUsersAsync();
            if (existingUsers.Any(u => u.Username == "owner"))
            {
                return; // Already seeded
            }

            // Create branches
            var branch1 = new Branch
            {
                BranchCode = "BR001",
                BranchName = "Main Branch",
                Address = "123 Main Street",
                Phone = "+1-555-0101",
                Email = "main@kds.com",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            var branch2 = new Branch
            {
                BranchCode = "BR002",
                BranchName = "Downtown Branch",
                Address = "456 Downtown Ave",
                Phone = "+1-555-0102",
                Email = "downtown@kds.com",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            await _branchService.CreateAsync(branch1);
            await _branchService.CreateAsync(branch2);

            // Create users
            var ownerUser = new AdminUser
            {
                Username = "owner",
                Password = BCrypt.Net.BCrypt.HashPassword("Password123!"),
                FullName = "System Owner",
                Email = "owner@kds.com",
                Role = UserRoles.Owner,
                BranchId = null // Owner has access to all branches
            };

            var manager1User = new AdminUser
            {
                Username = "manager1",
                Password = BCrypt.Net.BCrypt.HashPassword("Password123!"),
                FullName = "Main Branch Manager",
                Email = "manager1@kds.com",
                Role = UserRoles.BranchManager,
                BranchId = branch1.Id // Restricted to Main Branch
            };

            var manager2User = new AdminUser
            {
                Username = "manager2",
                Password = BCrypt.Net.BCrypt.HashPassword("Password123!"),
                FullName = "Downtown Branch Manager",
                Email = "manager2@kds.com",
                Role = UserRoles.BranchManager,
                BranchId = branch2.Id // Restricted to Downtown Branch
            };

            await _userService.CreateUserAsync(ownerUser);
            await _userService.CreateUserAsync(manager1User);
            await _userService.CreateUserAsync(manager2User);
        }

        /// <summary>
        /// Creates a branch manager for an existing branch
        /// </summary>
        public async Task CreateBranchManagerAsync(string username, string password, string fullName, string email, string branchId)
        {
            var user = new AdminUser
            {
                Username = username,
                Password = BCrypt.Net.BCrypt.HashPassword(password),
                FullName = fullName,
                Email = email,
                Role = UserRoles.BranchManager,
                BranchId = branchId
            };

            await _userService.CreateUserAsync(user);
        }

        /// <summary>
        /// Creates an owner user
        /// </summary>
        public async Task CreateOwnerAsync(string username, string password, string fullName, string email)
        {
            var user = new AdminUser
            {
                Username = username,
                Password = BCrypt.Net.BCrypt.HashPassword(password),
                FullName = fullName,
                Email = email,
                Role = UserRoles.Owner,
                BranchId = null
            };

            await _userService.CreateUserAsync(user);
        }
    }
}
