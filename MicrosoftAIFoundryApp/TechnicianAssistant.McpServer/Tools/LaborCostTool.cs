using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using TechnicianAssistant.McpServer.Data;

namespace TechnicianAssistant.McpServer.Tools;

[McpServerToolType]
public static class LaborCostTool
{
    [McpServerTool(Name = "estimate_labor")]
    [System.ComponentModel.Description("Estimates labor hours and total cost to replace a component. Looks up the component type in the labor rate database and applies the configured hourly rate and trip charge.")]
    public static async Task<object> EstimateLaborAsync(
        PartsDbContext db,
        string componentType,
        decimal hourlyRate = 125m,
        decimal tripCharge = 85m,
        string currency = "USD")
    {
        // Match on component type — fall back to "default" if not found
        var rate = await db.LaborRates
            .Where(r => r.Region == "default")
            .FirstOrDefaultAsync(r =>
                r.ComponentType.ToLower() == componentType.ToLower()) 
            ?? await db.LaborRates
                .FirstOrDefaultAsync(r => r.ComponentType == "default");

        var estimatedHours = rate?.EstimatedHours ?? 1.5;
        var difficulty     = rate?.DifficultyRating ?? "Moderate";
        var rationale      = rate?.Rationale ?? "Standard component replacement.";

        var laborTotal = (decimal)estimatedHours * hourlyRate;
        var grandTotal = laborTotal + tripCharge;

        return new
        {
            componentType,
            estimatedHours,
            difficultyRating = difficulty,
            rationale,
            hourlyRate,
            tripCharge,
            laborTotal,
            grandTotal,
            currency
        };
    }
}
