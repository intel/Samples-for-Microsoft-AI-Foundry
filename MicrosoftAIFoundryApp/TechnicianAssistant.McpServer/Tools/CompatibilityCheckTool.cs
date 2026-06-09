using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using TechnicianAssistant.McpServer.Data;

namespace TechnicianAssistant.McpServer.Tools;

[McpServerToolType]
public static class CompatibilityCheckTool
{
    [McpServerTool(Name = "check_compatibility")]
    [System.ComponentModel.Description("Checks whether a specific part number is compatible with a given equipment model number. Returns compatibility status, critical specifications, and safety notes.")]
    public static async Task<object> CheckCompatibilityAsync(
        PartsDbContext db,
        string partNumber,
        string modelNumber)
    {
        var part = await db.Parts
            .Include(p => p.CompatibleModels)
            .FirstOrDefaultAsync(p => p.PartNumber == partNumber);

        if (part == null)
        {
            return new
            {
                partNumber,
                modelNumber,
                isCompatible = false,
                message      = $"Part {partNumber} not found in database."
            };
        }

        var compatible = part.CompatibleModels
            .Any(c => c.ModelNumber.ToLower() == modelNumber.ToLower());

        // If no explicit compatibility record, return the part details for manual verification
        return new
        {
            partNumber,
            modelNumber,
            isCompatible        = compatible,
            partDescription     = part.Description,
            isOEM               = part.IsOEM,
            voltageRating       = part.VoltageRating,
            capacitanceRating   = part.CapacitanceRating,
            criticalSpecs       = part.CriticalSpecs,
            safetyNotes         = part.SafetyNotes,
            compatibilityNotes  = compatible
                ? $"Verified compatible with {modelNumber}."
                : $"No explicit compatibility record for {modelNumber}. Verify specifications manually.",
            verifiedModels      = part.CompatibleModels.Select(c => new { c.ModelNumber, c.Manufacturer }).ToList()
        };
    }
}
