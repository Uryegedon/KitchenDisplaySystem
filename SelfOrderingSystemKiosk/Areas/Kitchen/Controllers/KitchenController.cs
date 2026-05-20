using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using SelfOrderingSystemKiosk.Areas.Customer.Models;
using SelfOrderingSystemKiosk.Areas.Kitchen.Models;
using SelfOrderingSystemKiosk.Services;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SelfOrderingSystemKiosk.Areas.Kitchen.Controllers
{

    [Area("Kitchen")]
    [Authorize(Roles = "Owner,BranchManager,Kitchen")]
    public partial class KitchenController : Controller
    {
        private static readonly string[] DiningTableNumbers = { "1", "2", "3", "4", "5", "6", "7" };
        private const string DefaultKioskTableNumber = "KIOSK";
        private const string DefaultTakeOutTableNumber = "TAKEOUT";
        private readonly OrderService _orderService;
        private readonly TableOrderingSessionService _tableOrderingSessions;
        private readonly TableRegistryService _tableRegistry;
        private readonly MenuItemService _menuItems;
        private readonly UnlimitedRefillService _unlimitedRefills;
        private readonly OrderRealtimeNotifier _realtime;
        private readonly ILogger<KitchenController> _logger;

        public KitchenController(
            OrderService orderService,
            TableOrderingSessionService tableOrderingSessions,
            TableRegistryService tableRegistry,
            MenuItemService menuItems,
            UnlimitedRefillService unlimitedRefills,
            OrderRealtimeNotifier realtime,
            ILogger<KitchenController> logger)
        {
            _orderService = orderService;
            _tableOrderingSessions = tableOrderingSessions;
            _tableRegistry = tableRegistry;
            _menuItems = menuItems;
            _unlimitedRefills = unlimitedRefills;
            _realtime = realtime;
            _logger = logger;
        }
    }
}
