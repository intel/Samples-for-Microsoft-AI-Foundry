using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TechnicianAssistant.Services;

namespace TechnicianAssistant.Services.Parts.Tools;

/// <summary>
/// Determines warranty status based on the equipment's model number.
/// Production implementation should call a manufacturer warranty API.
/// </summary>
public sealed class WarrantyCheckTool : IWarrantyCheckTool
{
    public string Name => "WarrantyCheck";

    public async Task<WarrantyStatus> CheckWarrantyAsync(EquipmentInfo equipment, string failedComponent)
    {
        await Task.Delay(200); // Simulate lookup latency

        // Mock: always returns expired — use the MCP server for real warranty lookups.
        // The MCP server matches by serial number first, then model number as fallback.
        var installYear = DateTime.Now.Year - 10;

        return new WarrantyStatus
        {
            IsUnderWarranty = false,
            WarrantyType    = "Expired",
            ExpirationDate  = new DateTime(installYear + 5, 12, 31),
            Advice          = "No warranty record found (mock). Start the MCP server for live warranty lookups."
        };
    }

    private static int ExtractInstallYear(string? modelNumber)
    {
        if (modelNumber is null) return DateTime.Now.Year - 3;
        var match = Regex.Match(modelNumber, @"20\d{2}");
        return match.Success ? int.Parse(match.Value) : DateTime.Now.Year - 3;
    }
}
