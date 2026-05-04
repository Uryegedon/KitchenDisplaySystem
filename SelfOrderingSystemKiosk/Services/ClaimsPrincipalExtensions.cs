using System.Security.Claims;
using SelfOrderingSystemKiosk.Models;

namespace SelfOrderingSystemKiosk.Services
{
    /// <summary>
    /// Extension methods for ClaimsPrincipal to simplify branch and role access
    /// </summary>
    public static class ClaimsPrincipalExtensions
    {
        /// <summary>
        /// Gets the BranchId claim from the user (null if not set or Owner)
        /// </summary>
        public static string? GetBranchId(this ClaimsPrincipal user)
            => user.FindFirst("BranchId")?.Value;

        /// <summary>
        /// Gets the username claim from the user
        /// </summary>
        public static string? GetUsername(this ClaimsPrincipal user)
            => user.FindFirst(ClaimTypes.Name)?.Value;

        /// <summary>
        /// Checks if user has Owner role (can access all branches)
        /// </summary>
        public static bool IsOwner(this ClaimsPrincipal user)
            => user.IsInRole(UserRoles.Owner);

        /// <summary>
        /// Checks if user has BranchManager role (restricted to single branch)
        /// </summary>
        public static bool IsBranchManager(this ClaimsPrincipal user)
            => user.IsInRole(UserRoles.BranchManager);

        /// <summary>
        /// Checks if user has Admin role (legacy, treats as Owner for backward compatibility)
        /// </summary>
        public static bool IsAdmin(this ClaimsPrincipal user)
            => user.IsInRole(UserRoles.Admin);

        /// <summary>
        /// Checks if user has Kitchen role
        /// </summary>
        public static bool IsKitchen(this ClaimsPrincipal user)
            => user.IsInRole(UserRoles.Kitchen);

        /// <summary>
        /// Returns true if user can access all branches (Owner or Admin)
        /// </summary>
        public static bool HasAllBranchAccess(this ClaimsPrincipal user)
            => user.IsOwner() || user.IsAdmin();

        /// <summary>
        /// Returns true if user should be restricted to a single branch
        /// </summary>
        public static bool IsBranchRestricted(this ClaimsPrincipal user)
            => user.IsBranchManager();
    }
}
