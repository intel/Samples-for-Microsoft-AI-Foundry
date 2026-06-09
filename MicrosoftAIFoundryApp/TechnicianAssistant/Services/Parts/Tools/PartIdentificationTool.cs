using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using TechnicianAssistant.Services.Interfaces;
using TechnicianAssistant.Services;

namespace TechnicianAssistant.Services.Parts.Tools;

/// <summary>
/// Uses the local LLM to identify the exact OEM part number and critical specifications
/// for a described component failure on a specific piece of equipment.
/// </summary>
public sealed class PartIdentificationTool : IPartIdentificationTool
{
    public string Name => "PartIdentification";

    private readonly ILlmService _llmService;

    public PartIdentificationTool(ILlmService llmService) => _llmService = llmService;

    public async Task<PartDetails> IdentifyPartAsync(string componentDescription, EquipmentInfo equipment)
    {
        var prompt = $$"""
            You are a parts identification expert for HVAC equipment.

            Failed Component Description: {{componentDescription}}
            Equipment Model: {{equipment.ModelNumber ?? "unknown"}}
            Manufacturer: {{equipment.Manufacturer ?? "unknown"}}

            Identify the specific replacement part needed. Consider:
            1. Exact part number (OEM preferred)
            2. Critical specifications (voltage, capacitance, size, etc.)
            3. Compatibility requirements
            4. Safety considerations

            Return ONLY a JSON object with this exact schema:
            {
                "partNumber": "CAP-45-5-440",
                "description": "Dual Run Capacitor 45/5 MFD 440V",
                "specifications": {"voltage": "440V", "capacitance": "45/5 MFD", "type": "Dual Run"},
                "isOEM": true,
                "criticalSpecs": ["Voltage must match exactly", "Capacitance tolerance ±6%"],
                "safetyNotes": ["Discharge capacitor before handling", "Use insulated tools"]
            }
            """;

        var (response, _) = await _llmService.GenerateResponseAsync(prompt, maxTokens: 500, temperature: 0f, useReasoning: true);
        var json = StripFences(response);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var part = new PartDetails
            {
                PartNumber  = root.TryGetProperty("partNumber",  out var pn) ? pn.GetString() ?? string.Empty : string.Empty,
                Description = root.TryGetProperty("description", out var d)  ? d.GetString()  ?? string.Empty : string.Empty,
                IsOEM       = root.TryGetProperty("isOEM",       out var oem) && oem.GetBoolean()
            };

            if (root.TryGetProperty("specifications", out var specs))
                foreach (var spec in specs.EnumerateObject())
                    part.Specifications[spec.Name] = spec.Value.GetString() ?? string.Empty;

            if (root.TryGetProperty("criticalSpecs", out var cs))
                foreach (var item in cs.EnumerateArray())
                    if (item.GetString() is { } s) part.CriticalSpecs.Add(s);

            if (root.TryGetProperty("safetyNotes", out var sn))
                foreach (var item in sn.EnumerateArray())
                    if (item.GetString() is { } s) part.SafetyNotes.Add(s);

            return part;
        }
        catch
        {
            // Fall back to a minimal result rather than throwing.
            return new PartDetails { Description = componentDescription };
        }
    }

    private static string StripFences(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```")) return t;
        var start = t.IndexOf('{');
        var end   = t.LastIndexOf('}');
        return start >= 0 && end > start ? t[start..(end + 1)] : t;
    }
}
