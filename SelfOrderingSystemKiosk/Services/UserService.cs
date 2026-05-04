using BCrypt.Net;
using SelfOrderingSystemKiosk.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using SelfOrderingSystemKiosk.Areas.Admin.Models;

namespace SelfOrderingSystemKiosk.Services
{
    public class UserService
    {
        private readonly IMongoCollection<AdminUser> _users;

        public UserService(IMongoDatabase authDatabase)
        {
            _users = authDatabase.GetCollection<AdminUser>("Users");
        }

        public async Task<AdminUser?> ValidateUserAsync(string username, string password)
        {
            var user = await _users.Find(u => u.Username == username).FirstOrDefaultAsync();
            if (user == null) return null;

            bool isPasswordValid = BCrypt.Net.BCrypt.Verify(password, user.Password);
            return isPasswordValid ? user : null;
        }

        public async Task CreateAdminAsync(AdminUser newUser)
        {
            await _users.InsertOneAsync(newUser);
        }


        
        public async Task CreateUserAsync(AdminUser user)
        {
            await _users.InsertOneAsync(user);
        }

        
        public async Task<AdminUser?> GetUserByUsernameAsync(string username)
        {
            return await _users.Find(u => u.Username == username).FirstOrDefaultAsync();
        }

        
        public async Task<AdminUser?> GetUserByEmailAsync(string email)
        {
            return await _users.Find(u => u.Email == email).FirstOrDefaultAsync();
        }

        //  Validate login 
        public async Task<AdminUser?> ValidateLoginAsync(string username, string password)
        {
            var user = await GetUserByUsernameAsync(username);
            if (user == null)
                return null;

            bool passwordValid = BCrypt.Net.BCrypt.Verify(password, user.Password);
            return passwordValid ? user : null;
        }

       
        public async Task<long> GetUserCountAsync()
        {
            return await _users.CountDocumentsAsync(FilterDefinition<AdminUser>.Empty);
        }

        /// <summary>
        /// Gets all users (for Owner/Admin only)
        /// </summary>
        public async Task<List<AdminUser>> GetAllUsersAsync()
        {
            return await _users.Find(_ => true).ToListAsync();
        }

        /// <summary>
        /// Gets users filtered by branch (for branch-restricted views)
        /// </summary>
        public async Task<List<AdminUser>> GetUsersByBranchAsync(string? branchId)
        {
            if (string.IsNullOrEmpty(branchId))
            {
                // Return users with no branch assignment (Owners)
                return await _users.Find(u => u.BranchId == null).ToListAsync();
            }

            return await _users.Find(u => u.BranchId == branchId).ToListAsync();
        }

        /// <summary>
        /// Gets all branch managers (users with BranchManager role)
        /// </summary>
        public async Task<List<AdminUser>> GetBranchManagersAsync()
        {
            return await _users.Find(u => u.Role == UserRoles.BranchManager).ToListAsync();
        }

        /// <summary>
        /// Updates a user's branch assignment
        /// </summary>
        public async Task UpdateUserBranchAsync(string userId, string? branchId)
        {
            var filter = Builders<AdminUser>.Filter.Eq(u => u.Id, userId);
            var update = Builders<AdminUser>.Update.Set(u => u.BranchId, branchId);
            await _users.UpdateOneAsync(filter, update);
        }

        /// <summary>
        /// Gets all owners (users with Owner role)
        /// </summary>
        public async Task<List<AdminUser>> GetOwnersAsync()
        {
            return await _users.Find(u => u.Role == UserRoles.Owner).ToListAsync();
        }

        /// <summary>
        /// Gets a user by ID
        /// </summary>
        public async Task<AdminUser?> GetByIdAsync(string userId)
        {
            return await _users.Find(u => u.Id == userId).FirstOrDefaultAsync();
        }

        /// <summary>
        /// Updates a user's information
        /// </summary>
        public async Task UpdateUserAsync(AdminUser user)
        {
            var filter = Builders<AdminUser>.Filter.Eq(u => u.Id, user.Id);
            await _users.ReplaceOneAsync(filter, user);
        }

        /// <summary>
        /// Changes a user's password
        /// </summary>
        public async Task ChangePasswordAsync(string userId, string newPasswordHash)
        {
            var filter = Builders<AdminUser>.Filter.Eq(u => u.Id, userId);
            var update = Builders<AdminUser>.Update.Set(u => u.Password, newPasswordHash);
            await _users.UpdateOneAsync(filter, update);
        }

        /// <summary>
        /// Deletes a user
        /// </summary>
        public async Task DeleteUserAsync(string userId)
        {
            await _users.DeleteOneAsync(u => u.Id == userId);
        }

        /// <summary>
        /// Checks if a username is unique (excluding a specific user)
        /// </summary>
        public async Task<bool> IsUsernameUniqueAsync(string username, string? excludeUserId = null)
        {
            var filter = Builders<AdminUser>.Filter.Eq(u => u.Username, username);
            if (!string.IsNullOrEmpty(excludeUserId))
            {
                filter = Builders<AdminUser>.Filter.And(
                    filter,
                    Builders<AdminUser>.Filter.Ne(u => u.Id, excludeUserId)
                );
            }
            var count = await _users.CountDocumentsAsync(filter);
            return count == 0;
        }

        /// <summary>
        /// Checks if an email is unique (excluding a specific user)
        /// </summary>
        public async Task<bool> IsEmailUniqueAsync(string email, string? excludeUserId = null)
        {
            var filter = Builders<AdminUser>.Filter.Eq(u => u.Email, email);
            if (!string.IsNullOrEmpty(excludeUserId))
            {
                filter = Builders<AdminUser>.Filter.And(
                    filter,
                    Builders<AdminUser>.Filter.Ne(u => u.Id, excludeUserId)
                );
            }
            var count = await _users.CountDocumentsAsync(filter);
            return count == 0;
        }
    }
}
