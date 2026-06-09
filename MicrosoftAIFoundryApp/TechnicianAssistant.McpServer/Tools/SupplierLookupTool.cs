using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using TechnicianAssistant.McpServer.Data;

namespace TechnicianAssistant.McpServer.Tools;

[McpServerToolType]
public static class SupplierLookupTool
{
    [McpServerTool(Name = "find_suppliers")]
    [System.ComponentModel.Description("Finds suppliers that stock a given part number. Returns supplier contact details, hours, distance, delivery options, and current stock availability.")]
    public static async Task<object> FindSuppliersAsync(
        PartsDbContext db,
        string partNumber)
    {
        var stock = await db.SupplierStock
            .Include(s => s.Supplier)
            .Include(s => s.Part)
            .Where(s => s.Part.PartNumber == partNumber && s.Quantity > 0)
            .ToListAsync();

        var suppliers = stock.Select(s => new
        {
            name            = s.Supplier.Name,
            distance        = s.Supplier.Distance,
            phone           = s.Supplier.Phone,
            hours           = s.Supplier.Hours,
            hasPart         = true,
            quantity        = s.Quantity,
            estimatedDelivery = s.EstimatedDelivery.TotalHours < 1
                ? "Immediate"
                : s.EstimatedDelivery.TotalHours <= 4
                    ? "Same Day"
                    : s.EstimatedDelivery.TotalDays >= 1
                        ? "Next Day"
                        : $"{s.EstimatedDelivery.TotalHours:F0} hours",
            deliveryOptions = s.Supplier.DeliveryOptions,
            lastUpdated     = s.LastUpdated.ToString("yyyy-MM-dd HH:mm")
        }).ToList();

        return new
        {
            partNumber,
            suppliersFound = suppliers.Count,
            suppliers
        };
    }
}
