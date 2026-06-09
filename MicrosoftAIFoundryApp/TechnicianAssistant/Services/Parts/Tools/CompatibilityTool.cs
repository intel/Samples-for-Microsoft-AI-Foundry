using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using TechnicianAssistant.Services.Interfaces;
using TechnicianAssistant.Services;

namespace TechnicianAssistant.Services.Parts.Tools;

/// <summary>
/// Uses the local LLM to find compatible aftermarket and OEM-equivalent alternatives
/// for a given primary part.
/// </summary>
public sealed class CompatibilityTool : ICompatibilityTool
{
    public string Name => "CompatibilityCheck";

    private readonly ILlmService _llmService;

    public CompatibilityTool(ILlmService llmService) => _llmService = llmService;

    public async Task<List<PartDetails>> FindAlternativesAsync(PartDetails primaryPart, EquipmentInfo equipment)
    {
        var prompt = $$"""
            Find compatible alternative parts for:

            Primary Part: {{JsonSerializer.Serialize(primaryPart)}}
            Equipment: {{equipment.ModelNumber ?? "unknown"}} ({{equipment.Manufacturer ?? "unknown"}})

            Return ONLY a JSON array of alternative part objects with this schema:
            [
              {
                "partNumber": "...",
                "description": "...",
                "isOEM": false,
                "compatibilityNotes": "Direct replacement, aftermarket quality",
                "estimatedPrice": 30.00
              }
            ]
            """;

        var (response, _) = await _llmService.GenerateResponseAsync(prompt, maxTokens: 600, temperature: 0f, useReasoning: true);

        try
        {
            var json = StripFences(response);
            using var doc = JsonDocument.Parse(json);
            var result = new List<PartDetails>();

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var part = new PartDetails
                {
                    PartNumber         = item.TryGetProperty("partNumber",         out var pn) ? pn.GetString() ?? string.Empty : string.Empty,
                    Description        = item.TryGetProperty("description",        out var d)  ? d.GetString()  ?? string.Empty : string.Empty,
                    IsOEM              = item.TryGetProperty("isOEM",              out var oem) && oem.GetBoolean(),
                    CompatibilityNotes = item.TryGetProperty("compatibilityNotes", out var cn) ? cn.GetString() : null,
                    EstimatedPrice     = item.TryGetProperty("estimatedPrice",     out var ep) && ep.TryGetDecimal(out var price) ? price : 0m
                };
                result.Add(part);
            }

            return result;
        }
        catch
        {
            // Return empty list rather than propagating a JSON parse failure.
            return [];
        }
    }

    private static string StripFences(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```")) return t;
        var start = t.IndexOf('[');
        var end   = t.LastIndexOf(']');
        return start >= 0 && end > start ? t[start..(end + 1)] : t;
    }
}
