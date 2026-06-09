using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using TechnicianAssistant.McpServer.Data;

namespace TechnicianAssistant.McpServer.Tools;

[McpServerToolType]
public static class PricingTool
{
    [McpServerTool(Name = "compare_prices")]
    [System.ComponentModel.Description("Compares prices for a part number across all known suppliers. Returns a list of price options with delivery times and costs, plus the best-value and fastest-delivery recommendations.")]
    public static async Task<object> ComparePricesAsync(
        PartsDbContext db,
        string partNumber)
    {
        var prices = await db.SupplierPricing
            .Include(p => p.Supplier)
            .Where(p => p.PartNumber == partNumber)
            .ToListAsync();

        if (prices.Count == 0)
        {
            return new
            {
                partNumber,
                prices      = Array.Empty<object>(),
                bestValue   = (string?)null,
                fastest     = (string?)null,
                message     = $"No pricing data found for part number {partNumber}."
            };
        }

        var options = prices.Select(p => new
        {
            supplier     = p.Supplier.Name,
            price        = p.Price,
            deliveryCost = p.DeliveryCost,
            totalCost    = p.Price + p.DeliveryCost,
            deliveryTime = p.DeliveryTime,
            lastUpdated  = p.LastUpdated.ToString("yyyy-MM-dd HH:mm")
        }).ToList();

        var bestValue = options.OrderBy(o => o.totalCost).First().supplier;
        var fastest   = options.OrderBy(o => DeliverySpan(o.deliveryTime)).First().supplier;

        return new
        {
            partNumber,
            prices    = options,
            bestValue,
            fastest,
            lastUpdated = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")
        };
    }

    private static TimeSpan DeliverySpan(string deliveryTime) =>
        deliveryTime.ToLowerInvariant() switch
        {
            "immediate" => TimeSpan.Zero,
            "same day"  => TimeSpan.FromHours(4),
            "next day"  => TimeSpan.FromDays(1),
            _           => TimeSpan.FromDays(2)
        };
}
