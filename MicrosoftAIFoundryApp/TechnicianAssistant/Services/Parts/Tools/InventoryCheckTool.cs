using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TechnicianAssistant.Services.Parts.Tools;

/// <summary>
/// Checks stock levels for a part number.
/// In production this would call a live distributor API; the current implementation
/// returns mock data so the agent pipeline can be exercised end-to-end.
/// </summary>
public sealed class InventoryCheckTool : IInventoryCheckTool
{
    public string Name => "InventoryCheck";

    // Simulated inventory — replace with real API calls in production.
    private static readonly Dictionary<string, InventoryStatus> _mockInventory = new()
    {
        ["CAP-45-5-440"] = new InventoryStatus
        {
            InStock           = true,
            Quantity          = 12,
            Locations         = ["Local HVAC Supply", "Johnstone Supply", "Amazon Business"],
            EstimatedDelivery = TimeSpan.FromHours(2),
            LastUpdated       = DateTime.Now.AddMinutes(-15)
        },
        ["CONT-30A-24V"] = new InventoryStatus
        {
            InStock           = true,
            Quantity          = 8,
            Locations         = ["Johnstone Supply", "Ferguson HVAC"],
            EstimatedDelivery = TimeSpan.FromHours(4),
            LastUpdated       = DateTime.Now.AddMinutes(-30)
        }
    };

    public async Task<InventoryStatus> CheckStockAsync(string partNumber)
    {
        await Task.Delay(500); // Simulate network latency
        return _mockInventory.GetValueOrDefault(partNumber, new InventoryStatus { InStock = false });
    }
}
