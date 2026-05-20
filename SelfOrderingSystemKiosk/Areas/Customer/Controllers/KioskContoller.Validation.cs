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
    public partial class KioskController
    {
        private async Task<OrderItemValidationResult> ValidateSubmittedItemsAsync(List<OrderItem> submittedItems, bool isUnlimitedOrder)
        {
            var availableItems = await GetAvailableMenuForCurrentContextAsync() ?? new List<MenuItem>();
            var byName = availableItems
                .Where(i => !string.IsNullOrWhiteSpace(i.Item))
                .GroupBy(i => i.Item.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var validated = new List<OrderItem>();
            var submittedUnlimitedWingFlavors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var submitted in submittedItems)
            {
                var displayName = submitted.ItemName?.Trim();
                var lookupName = NormalizeSubmittedItemName(displayName);
                if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(lookupName))
                    return OrderItemValidationResult.Fail("One or more order items are invalid.");

                if (submitted.Quantity <= 0)
                    return OrderItemValidationResult.Fail("Item quantities must be greater than zero.");

                if (!byName.TryGetValue(lookupName, out var menuItem))
                    return OrderItemValidationResult.Fail($"'{lookupName}' is not currently available.");

                if (isUnlimitedOrder)
                {
                    var isIncluded = IsUnlimitedIncludedItem(menuItem);
                    if (!isIncluded && !IsUnlimitedMenuItem(menuItem))
                        return OrderItemValidationResult.Fail($"'{lookupName}' is not available in the unlimited menu.");
                    if (!isIncluded && submitted.Quantity > 4)
                        return OrderItemValidationResult.Fail("Maximum quantity of 4 per Ala Carte add-on allowed.");
                    if (isIncluded && string.Equals(menuItem.Category, "Wings", StringComparison.Ordinal))
                    {
                        if (submitted.Quantity > 4)
                            return OrderItemValidationResult.Fail("Maximum quantity of 4 pieces per wing flavor allowed.");
                        submittedUnlimitedWingFlavors.Add(menuItem.Item.Trim());
                        if (submittedUnlimitedWingFlavors.Count > 4)
                            return OrderItemValidationResult.Fail("You can only choose up to 4 wing flavors per unlimited order.");
                    }
                    else if (isIncluded && submitted.Quantity > 20)
                    {
                        return OrderItemValidationResult.Fail("One or more item quantities are too high.");
                    }
                }
                else
                {
                    if (string.Equals(menuItem.Category, "Unlimited Inclusions", StringComparison.Ordinal))
                        return OrderItemValidationResult.Fail($"'{lookupName}' is not available for ala carte orders.");
                    if (submitted.Quantity > 4)
                        return OrderItemValidationResult.Fail("Maximum quantity of 4 per item allowed.");
                }

                validated.Add(new OrderItem
                {
                    ItemName = displayName,
                    Quantity = submitted.Quantity,
                    Price = isUnlimitedOrder && IsUnlimitedIncludedItem(menuItem) ? 0m : menuItem.Price
                });
            }

            return OrderItemValidationResult.Ok(validated, submittedUnlimitedWingFlavors);
        }

        private async Task<List<ConfirmationQuickItem>> BuildConfirmationQuickItemsAsync(Order order)
        {
            var availableItems = await GetAvailableMenuForCurrentContextAsync() ?? new List<MenuItem>();
            var includedItems = availableItems
                .Where(IsUnlimitedIncludedItem)
                .Where(i => !string.IsNullOrWhiteSpace(i.Item))
                .ToList();

            var quickItems = new List<ConfirmationQuickItem>();
            var wingFlavorNames = (ViewBag.ConfirmationWingFlavors as IEnumerable<string> ?? Enumerable.Empty<string>())
                .Select(f => f.Trim())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!wingFlavorNames.Any() && order?.Items != null)
            {
                wingFlavorNames = (await ExtractUnlimitedWingFlavorsAsync(order.Items))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var wingItemsByName = includedItems
                .Where(i => string.Equals(i.Category, "Wings", StringComparison.Ordinal))
                .GroupBy(i => i.Item.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var flavor in wingFlavorNames.Take(4))
            {
                if (!wingItemsByName.ContainsKey(flavor))
                    continue;

                quickItems.Add(new ConfirmationQuickItem
                {
                    Name = flavor,
                    Label = flavor,
                    Group = "Current Flavors",
                    Quantity = 4,
                    IsWingFlavor = true
                });
            }

            var includedNames = new[]
            {
                "Plain Rice",
                "Garlic Rice",
                "Extra Gravy",
                "Nachos",
                "Potato Thins",
                "Regular Pasta",
                "Red Iced Tea",
                "Coffee",
                "Tea"
            };

            foreach (var desiredName in includedNames)
            {
                var menuItem = includedItems
                    .Where(i => i.Item.Contains(desiredName, StringComparison.OrdinalIgnoreCase)
                        || desiredName.Contains(i.Item, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(i => i.MenuOrder)
                    .FirstOrDefault();
                if (menuItem == null)
                    continue;

                var group = string.Equals(menuItem.Category, "Drinks", StringComparison.Ordinal)
                    || menuItem.Item.Contains("Tea", StringComparison.OrdinalIgnoreCase)
                    || menuItem.Item.Contains("Coffee", StringComparison.OrdinalIgnoreCase)
                        ? "Drinks"
                        : menuItem.Item.Contains("Rice", StringComparison.OrdinalIgnoreCase)
                            ? "Rice"
                            : menuItem.Item.Contains("Pasta", StringComparison.OrdinalIgnoreCase)
                                ? "Pasta"
                                : "Sides";

                if (quickItems.Any(i => string.Equals(i.Name, menuItem.Item, StringComparison.OrdinalIgnoreCase)))
                    continue;

                quickItems.Add(new ConfirmationQuickItem
                {
                    Name = menuItem.Item,
                    Label = menuItem.Item.StartsWith("Unli Pasta ", StringComparison.OrdinalIgnoreCase)
                        ? menuItem.Item["Unli Pasta ".Length..].Trim()
                        : menuItem.Item,
                    Group = group,
                    Quantity = 1,
                    IsWingFlavor = false
                });
            }

            return quickItems;
        }

        private async Task<HashSet<string>> ExtractUnlimitedWingFlavorsAsync(IEnumerable<OrderItem> orderItems)
        {
            var availableItems = await GetAvailableMenuForCurrentContextAsync() ?? new List<MenuItem>();
            var byName = availableItems
                .Where(i => !string.IsNullOrWhiteSpace(i.Item))
                .GroupBy(i => i.Item.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var wingFlavors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in orderItems ?? Enumerable.Empty<OrderItem>())
            {
                var lookupName = NormalizeSubmittedItemName(item.ItemName);
                if (string.IsNullOrWhiteSpace(lookupName))
                    continue;

                if (byName.TryGetValue(lookupName, out var menuItem) &&
                    string.Equals(menuItem.Category, "Wings", StringComparison.Ordinal))
                {
                    wingFlavors.Add(menuItem.Item.Trim());
                }
            }

            return wingFlavors;
        }

        private static string NormalizeSubmittedItemName(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName))
                return string.Empty;

            const string flavorMarker = " (Flavors:";
            var markerIndex = itemName.IndexOf(flavorMarker, StringComparison.OrdinalIgnoreCase);
            var normalized = markerIndex >= 0
                ? itemName[..markerIndex].Trim()
                : itemName.Trim();

            if (normalized.StartsWith("Coffee - ", StringComparison.OrdinalIgnoreCase))
                return "Coffee";

            return normalized;
        }
    }
}
