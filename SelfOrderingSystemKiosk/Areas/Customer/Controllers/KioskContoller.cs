using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using SelfOrderingSystemKiosk.Areas.Customer.Models;
using SelfOrderingSystemKiosk.Models;
using SelfOrderingSystemKiosk.Services;
using System.Security.Cryptography;
using Order = SelfOrderingSystemKiosk.Areas.Customer.Models.Order;

namespace SelfOrderingSystemKiosk.Areas.Customer.Controllers
{
    [Area("Customer")]
    [AllowAnonymous]
    public partial class KioskController : Controller
    {
        private readonly OrderService _orderService;
        private readonly TableOrderingSessionService _tableOrderingSessions;
        private readonly TableRegistryService _tableRegistry;
        private readonly MenuItemService _menuItems;
        private readonly UnlimitedRefillService _unlimitedRefills;
        private readonly MenuCategoryRegistry _menuCategories;
        private readonly BranchService _branches;
        private readonly OrderRealtimeNotifier _realtime;
        private readonly ILogger<KioskController> _logger;
        private bool _skipRememberedPersonCountRestore;

        private const string SessionOrderChannel = "OrderChannel";
        private const string SessionServiceTable = "ServiceTableNumber";
        private const string SessionServiceFloor = "ServiceFloor";
        private const string SessionServiceBranch = "ServiceBranchId";
        private const string SessionDiningType = "DiningType";
        private const string SessionPersonCount = "PersonCount";
        private const string SessionEndedTableReset = "EndedTableSessionReset";
        private const string CookieServiceTable = "KdsOrderTable";
        private const string CookieServiceFloor = "KdsOrderFloor";
        private const string CookieServiceBranch = "KdsOrderBranch";
        private const string CookiePersonCount = "KdsOrderPersonCount";
        private const string OrderChannelKiosk = "Kiosk";
        private const string OrderChannelQr = "Qr";
        private const string DefaultKioskTableNumber = "KIOSK";
        private const string DefaultTakeOutTableNumber = "TAKEOUT";
        private const string SessionFirstOrderTime = "FirstOrderTime";
        private const string OrderAccessSessionPrefix = "OrderAccess:";
        private static readonly TimeSpan OrderingSessionLength = TimeSpan.FromHours(2);

        public KioskController(
            OrderService orderService,
            TableOrderingSessionService tableOrderingSessions,
            TableRegistryService tableRegistry,
            MenuItemService menuItems,
            UnlimitedRefillService unlimitedRefills,
            MenuCategoryRegistry menuCategories,
            BranchService branches,
            OrderRealtimeNotifier realtime,
            ILogger<KioskController> logger)
        {
            _orderService = orderService;
            _tableOrderingSessions = tableOrderingSessions;
            _tableRegistry = tableRegistry;
            _menuItems = menuItems;
            _unlimitedRefills = unlimitedRefills;
            _menuCategories = menuCategories;
            _branches = branches;
            _realtime = realtime;
            _logger = logger;
        }

    }

    internal class TableOrderingGateResult
    {
        public bool CanOrder { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime? SessionStartUtc { get; set; }
        public DateTime? SessionEndUtc { get; set; }
        public bool IsPaid { get; set; }
        public bool PreviousSessionExpired { get; set; }

        public static TableOrderingGateResult Allowed(DateTime? sessionStartUtc = null, DateTime? sessionEndUtc = null, bool isPaid = false, bool previousSessionExpired = false)
        {
            return new TableOrderingGateResult
            {
                CanOrder = true,
                SessionStartUtc = sessionStartUtc,
                SessionEndUtc = sessionEndUtc,
                IsPaid = isPaid,
                PreviousSessionExpired = previousSessionExpired
            };
        }

        public static TableOrderingGateResult Blocked(string message, DateTime sessionStartUtc, DateTime sessionEndUtc)
        {
            return new TableOrderingGateResult
            {
                CanOrder = false,
                Message = message,
                SessionStartUtc = sessionStartUtc,
                SessionEndUtc = sessionEndUtc
            };
        }
    }

    internal class OrderItemValidationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<OrderItem> Items { get; set; } = new();
        public HashSet<string> UnlimitedWingFlavors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public static OrderItemValidationResult Ok(List<OrderItem> items, HashSet<string> unlimitedWingFlavors) => new()
        {
            Success = true,
            Items = items,
            UnlimitedWingFlavors = unlimitedWingFlavors
        };

        public static OrderItemValidationResult Fail(string message) => new()
        {
            Success = false,
            Message = message
        };
    }

    public class ConfirmationQuickItem
    {
        public string Name { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public int Quantity { get; set; } = 1;
        public bool IsWingFlavor { get; set; }
    }
}
