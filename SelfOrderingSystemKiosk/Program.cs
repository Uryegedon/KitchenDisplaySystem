using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using SelfOrderingSystemKiosk.Hubs;
using SelfOrderingSystemKiosk.Areas.Admin.Models;
using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    builder.Logging.AddDebug();
}

static string? CleanConfigValue(string? value) =>
    string.IsNullOrWhiteSpace(value)
        ? null
        : value.Trim().Trim('"').Trim('\'');

// Render sets PORT; Fly uses ASPNETCORE_URLS from fly.toml. Prefer PORT when present.
var portEnv = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(portEnv))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{portEnv}");
}

if (builder.Environment.IsDevelopment())
{
    var dataProtectionKeysPath = Path.Combine(builder.Environment.ContentRootPath, ".aspnet-data-protection-keys");
    Directory.CreateDirectory(dataProtectionKeysPath);
    builder.Services.AddDataProtection()
        .SetApplicationName("SelfOrderingSystemKiosk")
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}

// Add services to the container.
builder.Services.AddControllersWithViews(options =>
    {
        options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        options.JsonSerializerOptions.PropertyNamingPolicy = null; // Use exact property names
    });
builder.Services.AddAuthorization();
builder.Services.AddSignalR();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("auth-login", limiterOptions =>
    {
        limiterOptions.PermitLimit = 10;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
        limiterOptions.AutoReplenishment = true;
    });
    options.AddFixedWindowLimiter("customer-order-write", limiterOptions =>
    {
        limiterOptions.PermitLimit = 30;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
        limiterOptions.AutoReplenishment = true;
    });
    options.AddFixedWindowLimiter("delivery-import-upload", limiterOptions =>
    {
        limiterOptions.PermitLimit = 6;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
        limiterOptions.AutoReplenishment = true;
    });
});

// Bind all settings classes
builder.Services.Configure<DataConSettings>(
    builder.Configuration.GetSection("DataCon"));

builder.Services.Configure<MongoDBSettings>(options =>
{
    // Bind Kitchen settings 
    builder.Configuration.GetSection("KitchenDatabase").Bind(options);

    // Connection string with DataCon's value
    var connectionString = CleanConfigValue(builder.Configuration["DataCon:ConnectionString"]);
    options.ConnectionString = connectionString ?? string.Empty;
    options.DatabaseName = CleanConfigValue(options.DatabaseName) ?? "Kitchen";
    options.OrdersCollectionName = CleanConfigValue(options.OrdersCollectionName) ?? "Orders";
});

builder.Services.Configure<AuthenticationSettings>(
    builder.Configuration.GetSection("Authentication"));

builder.Services.Configure<QrOrderingSettings>(
    builder.Configuration.GetSection("QrOrdering"));

builder.Services.Configure<MenuCategoriesSettings>(
    builder.Configuration.GetSection("MenuCategories"));

// ------------------
// MongoDB DI Setup
// ------------------

// Register a single IMongoClient for the app
builder.Services.AddSingleton<IMongoClient>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var connectionString = CleanConfigValue(config["DataCon:ConnectionString"]);
    if (string.IsNullOrEmpty(connectionString))
    {
        throw new InvalidOperationException(
            "MongoDB connection string is missing. Set DataCon__ConnectionString in Render environment variables.");
    }

    return new MongoClient(connectionString);
});

// Register services that use IMongoClient instead of IMongoDatabase
// Each service gets the database itself internally
builder.Services.AddSingleton<StockMovementService>();
builder.Services.AddSingleton<MenuCategoryRegistry>();
builder.Services.AddSingleton<FoodCategoryRegistry>();
builder.Services.AddSingleton<IngredientCategoryRegistry>();
builder.Services.AddSingleton<MenuItemService>();
builder.Services.AddSingleton<DeliveryImportService>();
builder.Services.AddSingleton<KpItemsImageResolver>();
builder.Services.AddSingleton<IngredientStockService>();
builder.Services.AddSingleton<UserService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<BranchService>();

// Other services
builder.Services.AddSingleton<KitchenDatabase>();
builder.Services.AddSingleton<OrderService>();
builder.Services.AddSingleton<UnlimitedRefillService>();
builder.Services.AddSingleton<TableOrderingSessionService>();
builder.Services.AddSingleton<TableRegistryService>();
builder.Services.AddSingleton<QrCodeService>();
builder.Services.AddSingleton<OrderRealtimeNotifier>();
builder.Services.AddScoped<ChickenService>();

builder.Services.AddHostedService<OrderIndexesHostedService>();
builder.Services.AddHostedService<OrderExpirationHostedService>();
builder.Services.AddHostedService<MenuRecipeSeedHostedService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Admin/Account/Login";
        options.AccessDeniedPath = "/Admin/Account/AccessDenied";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });
var redisConnectionString = CleanConfigValue(builder.Configuration["DistributedCache:RedisConnectionString"])
    ?? CleanConfigValue(builder.Configuration["Redis:ConnectionString"])
    ?? CleanConfigValue(Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING"));
if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnectionString;
        options.InstanceName = "SelfOrderingSystemKiosk:";
    });
}
else
{
    if (!builder.Environment.IsDevelopment())
    {
        Console.WriteLine("Warning: using in-memory session cache. Set DistributedCache__RedisConnectionString for multi-instance cloud deployment.");
    }
    builder.Services.AddDistributedMemoryCache();
}
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(2);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 5 * 1024 * 1024;
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddSingleton<IMongoDatabase>(sp =>
{
    var client = sp.GetRequiredService<IMongoClient>();
    var config = sp.GetRequiredService<IConfiguration>();
    var authDbName = config["Authentication:DatabaseName"] ?? "Users";
    return client.GetDatabase(authDbName);
});

var app = builder.Build();


// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseForwardedHeaders();
app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// ✅ Enable session
app.UseSession();

// Login route for easy access
app.MapControllerRoute(
    name: "login",
    pattern: "login",
    defaults: new { area = "Admin", controller = "Account", action = "Login" });

app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller}/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}",
    defaults: new { area = "Admin" });

app.MapHub<OrderRealtimeHub>("/hubs/orders");

app.Run();
