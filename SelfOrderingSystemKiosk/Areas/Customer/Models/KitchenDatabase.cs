using Microsoft.Extensions.Options;

using MongoDB.Driver;

namespace SelfOrderingSystemKiosk.Models
{
    public class KitchenDatabase
    {

        private readonly IMongoDatabase _database;

        public KitchenDatabase(IOptions<MongoDBSettings> settings)
        {
            var connectionString = settings.Value.ConnectionString?.Trim().Trim('"').Trim('\'');
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "MongoDB connection string is missing. Set DataCon__ConnectionString in Render environment variables.");
            }

            var databaseName = string.IsNullOrWhiteSpace(settings.Value.DatabaseName)
                ? "Kitchen"
                : settings.Value.DatabaseName.Trim();

            var client = new MongoClient(connectionString);
            _database = client.GetDatabase(databaseName);
        }

        // Expose it publicly
        public IMongoDatabase Database => _database;


        // 👇 Add this line — it defines your ChickenFlavors collection
        public IMongoCollection<ChickenFlavors> ChickenFlavors =>
            Database.GetCollection<ChickenFlavors>("ChickenWings_Flavor");
   
    }
}
