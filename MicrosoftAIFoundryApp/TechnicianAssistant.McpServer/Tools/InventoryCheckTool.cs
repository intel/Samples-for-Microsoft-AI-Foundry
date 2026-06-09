using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using TechnicianAssistant.McpServer.Data;

namespace TechnicianAssistant.McpServer.Tools;

[McpServerToolType]
public static class InventoryCheckTool
{
    [McpServerTool(Name = "check_inventory")]
    [System.ComponentModel.Description("Checks real-time stock levels for a part number across all suppliers. Returns quantity available, locations, and estimated delivery time.")]
    public static async Task<object> CheckInventoryAsync(
        PartsDbContext db,
        string partNumber)
    {
        var stockEntries = await db.SupplierStock
            .Include(s => s.Supplier)
            .Include(s => s.Part)
            .Where(s => s.Part.PartNumber == partNumber)
            .ToListAsync();

        var totalQty = stockEntries.Sum(s => s.Quantity);
        var inStock  = totalQty > 0;

        var locations = stockEntries
            .Where(s => s.Quantity > 0)
            .Select(s => new
            {
                supplier          = s.Supplier.Name,
                quantity          = s.Quantity,
                estimatedDelivery = s.EstimatedDelivery.TotalHours < 1
                    ? "Immediate"
                    : $"{s.EstimatedDelivery.TotalHours:F0} hours",
                lastUpdated = s.LastUpdated.ToString("yyyy-MM-dd HH:mm")
            }).ToList();

        return new
        {
            partNumber,
            inStock,
            totalQuantity     = totalQty,
            locations,
            fastestAvailable  = locations.Count > 0
                ? locations.OrderBy(l => l.estimatedDelivery).First().supplier
                : null as string
        };
    }
}
