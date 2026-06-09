using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using TechnicianAssistant.McpServer.Data;

namespace TechnicianAssistant.McpServer.Tools;

[McpServerToolType]
public static class PartLookupTool
{
    [McpServerTool(Name = "lookup_part")]
    [System.ComponentModel.Description("Looks up a part by part number or by description and equipment model. Returns full part details including specifications, safety notes, and compatible equipment models.")]
    public static async Task<object> LookupPartAsync(
        PartsDbContext db,
        string? partNumber = null,
        string? modelNumber = null,
        string? componentType = null)
    {
        var query = db.Parts
            .Include(p => p.CompatibleModels)
            .AsQueryable();

        if (!string.IsNullOrEmpty(partNumber))
            query = query.Where(p => p.PartNumber == partNumber);

        if (!string.IsNullOrEmpty(componentType))
            query = query.Where(p => p.PartType != null &&
                p.PartType.ToLower().Contains(componentType.ToLower()));

        if (!string.IsNullOrEmpty(modelNumber))
            query = query.Where(p =>
                p.CompatibleModels.Any(c => c.ModelNumber.ToLower() == modelNumber.ToLower()));

        var parts = await query.Take(5).ToListAsync();

        return new
        {
            found = parts.Count,
            parts = parts.Select(p => new
            {
                p.PartNumber,
                p.Description,
                p.IsOEM,
                p.BasePrice,
                p.VoltageRating,
                p.CapacitanceRating,
                p.PartType,
                p.CriticalSpecs,
                p.SafetyNotes,
                CompatibleModels = p.CompatibleModels.Select(c => new { c.ModelNumber, c.Manufacturer }).ToList()
            })
        };
    }
}
