using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using SelfOrderingSystemKiosk.Areas.Admin.Models;
using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<KpItemsImageResolver>();
builder.Services.AddSingleton<IngredientStockService>();
builder.Services.AddSingleton<UserService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<BranchService>();

// Other services
builder.Services.AddSingleton<KitchenDatabase>();
builder.Services.AddSingleton<OrderService>();
builder.Services.AddSingleton<TableOrderingSessionService>();
builder.Services.AddSingleton<TableRegistryService>();
builder.Services.AddSingleton<QrCodeService>();
builder.Services.AddScoped<ChickenService>();

builder.Services.AddHostedService<OrderIndexesHostedService>();
builder.Services.AddHostedService<OrderExpirationHostedService>();

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
builder.Services.AddDistributedMemoryCache();
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

app.Run();
