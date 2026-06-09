using System;
using System.Threading.Tasks;

namespace TechnicianAssistant.Services.Parts.Tools;

/// <summary>
/// Fetches and compares part prices across known suppliers.
/// Production implementation should call live pricing APIs.
/// </summary>
public sealed class PricingTool : IPricingTool
{
    public string Name => "PricingComparison";

    public async Task<PricingComparison> ComparePricesAsync(string partNumber)
    {
        await Task.Delay(400); // Simulate price-lookup latency

        var prices = new[]
        {
            new PriceOption { Supplier = "Local HVAC Supply", Price = 45.00m, DeliveryTime = "Immediate", DeliveryCost = 0m },
            new PriceOption { Supplier = "Amazon Business",   Price = 38.00m, DeliveryTime = "Next Day",  DeliveryCost = 0m },
            new PriceOption { Supplier = "Johnstone Supply",  Price = 42.00m, DeliveryTime = "Same Day",  DeliveryCost = 25.00m },
            new PriceOption { Supplier = "Ferguson HVAC",     Price = 47.00m, DeliveryTime = "Same Day",  DeliveryCost = 0m }
        };

        return new PricingComparison
        {
            PartNumber      = partNumber,
            Prices          = prices,
            BestValue       = BestByTotalCost(prices),
            FastestDelivery = BestByDeliveryTime(prices),
            LastUpdated     = DateTime.Now
        };
    }

    private static string BestByTotalCost(PriceOption[] prices)
    {
        var best = prices[0];
        foreach (var p in prices)
            if (p.TotalCost < best.TotalCost) best = p;
        return best.Supplier;
    }

    private static string BestByDeliveryTime(PriceOption[] prices)
    {
        var best = prices[0];
        foreach (var p in prices)
            if (DeliverySpan(p.DeliveryTime) < DeliverySpan(best.DeliveryTime)) best = p;
        return best.Supplier;
    }

    internal static TimeSpan DeliverySpan(string deliveryTime) =>
        deliveryTime.ToLowerInvariant() switch
        {
            "immediate" => TimeSpan.Zero,
            "same day"  => TimeSpan.FromHours(4),
            "next day"  => TimeSpan.FromDays(1),
            _           => TimeSpan.FromDays(2)
        };
}
