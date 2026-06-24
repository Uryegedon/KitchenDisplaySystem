using SelfOrderingSystemKiosk.Models;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using MongoDB.Bson;
using System.Text.RegularExpressions;


namespace SelfOrderingSystemKiosk.Services
{
    public class AuthService
    {
        private readonly IMongoCollection<AdminUser> _users;
        private readonly ILogger<AuthService> _logger;

        public AuthService(IMongoDatabase authDatabase, ILogger<AuthService> logger)
        {
            _users = authDatabase.GetCollection<AdminUser>("Users");
            _logger = logger;
        }

        // Validate user credentials
        public async Task<AdminUser?> ValidateUserAsync(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                _logger.LogDebug("ValidateUserAsync called with empty username or password.");
                return null;
            }

            username = username.Trim();

            
            var safe = Regex.Escape(username);
            var regex = new BsonRegularExpression($"^{safe}$", "i");
            var filter = Builders<AdminUser>.Filter.Regex(u => u.Username, regex);

            var user = await _users.Find(filter).FirstOrDefaultAsync();
            if (user == null)
            {
                _logger.LogInformation("User not found for username: {Username}", username);
                return null;
            }

            var storedHash = user.Password;
            if (storedHash == null)
            {
                _logger.LogWarning("Stored password hash is null for user {Username}", username);
                return null;
            }

            storedHash = storedHash.Trim();

            bool isPasswordValid;
            try
            {
                isPasswordValid = BCrypt.Net.BCrypt.Verify(password, storedHash);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Password verification failed for user {Username}", username);
                return null;
            }

            if (!isPasswordValid)
            {
                _logger.LogInformation("Invalid password for user: {Username}", username);
                return null;
            }

            _logger.LogInformation("User {Username} authenticated successfully.", username);
            return user;
        }
    }
}
