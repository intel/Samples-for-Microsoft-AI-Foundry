using System;
using System.Text.Json;
using System.Threading.Tasks;
using TechnicianAssistant.Services;
using TechnicianAssistant.Services.Interfaces;

namespace TechnicianAssistant.Services.Parts.Tools;

/// <summary>
/// Estimates labor hours using the local LLM and applies a configurable company
/// rate card (hourly rate + trip charge) to produce a total labor cost.
/// </summary>
public sealed class LaborCostTool : ILaborCostTool
{
    public string Name => "LaborCostEstimate";

    private readonly ILlmService _llmService;
    private readonly decimal _hourlyRate;
    private readonly decimal _tripCharge;
    private readonly string _currency;

    public LaborCostTool(ILlmService llmService, decimal hourlyRate = 125m, decimal tripCharge = 85m, string currency = "USD")
    {
        _llmService  = llmService;
        _hourlyRate  = hourlyRate;
        _tripCharge  = tripCharge;
        _currency    = currency;
    }

    public async Task<LaborCostEstimate> EstimateLaborAsync(string componentDescription, EquipmentInfo equipment)
    {
        var prompt = $$"""
            You are an HVAC labor estimator. Estimate the labor hours required to replace the failed component.

            Failed component: {{componentDescription}}
            Equipment model:  {{equipment.ModelNumber ?? "unknown"}}
            Manufacturer:     {{equipment.Manufacturer ?? "unknown"}}

            Consider:
            - Access difficulty (rooftop, attic, crawlspace vs ground-level)
            - Component complexity (capacitor swap vs compressor replacement)
            - Typical time including diagnosis, replacement, and system test

            Return ONLY a JSON object with this exact schema:
            {
                "estimatedHours": 1.5,
                "difficultyRating": "Easy|Moderate|Hard|Expert",
                "rationale": "Capacitor is accessible on the side panel; straightforward swap with system test."
            }
            """;

        var (response, _) = await _llmService.GenerateResponseAsync(
            prompt, maxTokens: 300, temperature: 0f, useReasoning: false);

        double hours = 1.0;
        string difficulty = "Moderate";
        string rationale  = string.Empty;

        try
        {
            var json = StripFences(response);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("estimatedHours",  out var h) && h.TryGetDouble(out var hv))  hours      = hv;
            if (root.TryGetProperty("difficultyRating", out var d) && d.GetString() is { } dv)    difficulty = dv;
            if (root.TryGetProperty("rationale",        out var r) && r.GetString() is { } rv)    rationale  = rv;
        }
        catch
        {
            LoggingService.Instance.Log("[LaborCostTool] Failed to parse LLM response — using default estimate");
        }

        return new LaborCostEstimate
        {
            EstimatedHours  = hours,
            HourlyRate      = _hourlyRate,
            TripCharge      = _tripCharge,
            Currency        = _currency,
            DifficultyRating = difficulty,
            Rationale       = rationale
        };
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
