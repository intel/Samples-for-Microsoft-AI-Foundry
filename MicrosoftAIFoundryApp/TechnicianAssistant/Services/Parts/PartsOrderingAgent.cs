using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TechnicianAssistant.Services.Interfaces;
using TechnicianAssistant.Services.Parts.Tools;

namespace TechnicianAssistant.Services.Parts;

/// <summary>
/// Autonomous agent that plans and executes parts research using a set of
/// specialised tools, then synthesises the findings into a ranked order plan.
/// </summary>
public sealed class PartsOrderingAgent : IPartsOrderingAgent
{
    private readonly ILlmService _llmService;
    private readonly IPartIdentificationTool _partIdentification;
    private readonly IInventoryCheckTool _inventoryCheck;
    private readonly ISupplierLookupTool _supplierLookup;
    private readonly IPricingTool _pricing;
    private readonly ICompatibilityTool _compatibility;
    private readonly IWarrantyCheckTool _warrantyCheck;
    private readonly ILaborCostTool _laborCost;

    public PartsOrderingAgent(
        ILlmService llmService,
        IPartIdentificationTool partIdentification,
        IInventoryCheckTool inventoryCheck,
        ISupplierLookupTool supplierLookup,
        IPricingTool pricing,
        ICompatibilityTool compatibility,
        IWarrantyCheckTool warrantyCheck,
        ILaborCostTool laborCost)
    {
        _llmService         = llmService;
        _partIdentification = partIdentification;
        _inventoryCheck     = inventoryCheck;
        _supplierLookup     = supplierLookup;
        _pricing            = pricing;
        _compatibility      = compatibility;
        _warrantyCheck      = warrantyCheck;
        _laborCost          = laborCost;
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public async Task<PartsOrderPlan> CreateOrderPlanAsync(
        string failedComponent,
        EquipmentInfo equipment,
        PartsConversationContext context,
        Action<string>? onProgress = null)
    {
        LoggingService.Instance.Log($"[PartsAgent] Starting analysis for: {failedComponent}");

        var findings  = await ExecuteResearchAsync(failedComponent, equipment, onProgress);
        var orderPlan = await SynthesiseOrderPlanAsync(findings, equipment, context);

        LoggingService.Instance.Log($"[PartsAgent] Order plan created with {orderPlan.Options.Count} option(s)");
        return orderPlan;
    }

    // -------------------------------------------------------------------------
    // Step 2 — ReAct loop: observe findings ? decide next tool ? execute ? repeat
    // -------------------------------------------------------------------------

    private const int MaxReActIterations = 8;

    private async Task<PartsResearchFindings> ExecuteResearchAsync(
        string failedComponent,
        EquipmentInfo equipment,
        Action<string>? onProgress)
    {
        var findings = new PartsResearchFindings();

        // PartIdentification is always the mandatory first step — every other tool
        // depends on findings.PrimaryPart being populated.
        LoggingService.Instance.Log("[PartsAgent] [1/ReAct] Executing mandatory first tool: PartIdentification");
        onProgress?.Invoke("🔍 Step 1 — Identifying the part...");
        findings.PrimaryPart = await _partIdentification.IdentifyPartAsync(failedComponent, equipment);
        if (findings.PrimaryPart is { } p)
            onProgress?.Invoke($"🔩 Part identified: {p.Description} ({p.PartNumber})");

        for (var iteration = 2; iteration <= MaxReActIterations; iteration++)
        {
            var nextTool = await DecideNextToolAsync(failedComponent, equipment, findings, iteration);

            if (nextTool is null or "Done")
            {
                LoggingService.Instance.Log($"[PartsAgent] [{iteration}/ReAct] Agent decided: Done");
                break;
            }

            LoggingService.Instance.Log($"[PartsAgent] [{iteration}/ReAct] Agent decided: {nextTool}");

            // Guard: if the model picks a tool that already produced results, skip it.
            var alreadyRunList = BuildAlreadyRunList(findings);
            if (alreadyRunList.Contains(nextTool))
            {
                LoggingService.Instance.Log($"[PartsAgent] [{iteration}/ReAct] Tool already completed — skipping duplicate: {nextTool}");
                continue;
            }

            onProgress?.Invoke($"⚙️ Step {iteration} — Running {nextTool}...");

            switch (nextTool)
            {
                case "InventoryCheck":
                    findings.Availability = await _inventoryCheck.CheckStockAsync(findings.PrimaryPart!.PartNumber);
                    if (findings.Availability is { } inv)
                        onProgress?.Invoke($"📦 Inventory: {(inv.InStock ? $"In stock ({inv.Quantity} units)" : "Out of stock")}");
                    break;

                case "SupplierLookup":
                    findings.Suppliers = await _supplierLookup.FindSuppliersAsync(findings.PrimaryPart!.PartNumber);
                    if (findings.Suppliers is { Count: > 0 } sups)
                        onProgress?.Invoke($"🏪 Found {sups.Count} supplier(s): {string.Join(", ", sups.Select(s => s.Name))}");
                    break;

                case "PricingComparison":
                    findings.Pricing = await _pricing.ComparePricesAsync(findings.PrimaryPart!.PartNumber);
                    if (findings.Pricing is { } pr)
                        onProgress?.Invoke($"💲 Pricing: {pr.Prices.Length} quote(s) — best value: {pr.BestValue}");
                    break;

                case "CompatibilityCheck":
                    findings.AlternativeParts = await _compatibility.FindAlternativesAsync(findings.PrimaryPart!, equipment);
                    if (findings.AlternativeParts is { Count: > 0 } alts)
                        onProgress?.Invoke($"🔄 Found {alts.Count} compatible alternative(s)");
                    break;

                case "WarrantyCheck":
                    findings.WarrantyStatus = await _warrantyCheck.CheckWarrantyAsync(equipment, failedComponent);
                    if (findings.WarrantyStatus is { } w)
                        onProgress?.Invoke($"🛡️ Warranty: {(w.IsUnderWarranty ? $"Covered — {w.WarrantyType}" : "Expired")}");
                    break;

                case "LaborCostEstimate":
                    findings.LaborCost = await _laborCost.EstimateLaborAsync(failedComponent, equipment);
                    if (findings.LaborCost is { } lc)
                        onProgress?.Invoke($"🔧 Labor estimate: {lc.EstimatedHours:F1} hrs × {lc.Currency} {lc.HourlyRate}/hr + {lc.Currency} {lc.TripCharge} trip = {lc.Currency} {lc.GrandTotal:F2} ({lc.DifficultyRating})");
                    break;

                default:
                    LoggingService.Instance.Log($"[PartsAgent] [{iteration}/ReAct] Unknown tool — stopping loop: {nextTool}");
                    goto done;
            }
        }

        done:

        // Post-loop mandatory sweep — run any required tools the ReAct loop didn't reach.
        // This guarantees LaborCostEstimate always runs when pricing is available and
        // warranty doesn't cover the part, regardless of how many iterations were consumed.
        if (findings.LaborCost is null &&
            findings.Pricing   is not null &&
            findings.WarrantyStatus?.IsUnderWarranty != true)
        {
            LoggingService.Instance.Log("[PartsAgent] [Post-loop] Running mandatory tool: LaborCostEstimate");
            onProgress?.Invoke("⚙️ Finalising — Running LaborCostEstimate...");
            findings.LaborCost = await _laborCost.EstimateLaborAsync(failedComponent, equipment);
            if (findings.LaborCost is { } lc)
                onProgress?.Invoke($"🔧 Labor estimate: {lc.EstimatedHours:F1} hrs × {lc.Currency} {lc.HourlyRate}/hr + {lc.Currency} {lc.TripCharge} trip = {lc.Currency} {lc.GrandTotal:F2} ({lc.DifficultyRating})");
        }

        return findings;
    }

    /// <summary>
    /// Shows the LLM the current findings and asks it which single tool to run next,
    /// or "Done" if enough information has been gathered.
    /// Returns <see langword="null"/> on parse failure, which the loop treats as Done.
    /// </summary>
    private async Task<string?> DecideNextToolAsync(
        string failedComponent,
        EquipmentInfo equipment,
        PartsResearchFindings findings,
        int iteration)
    {
        var alreadyRun    = BuildAlreadyRunList(findings);
        var summary       = SummariseFindings(findings);

        if ((alreadyRun.Contains("PricingComparison") && alreadyRun.Contains("LaborCostEstimate")) ||
            (findings.WarrantyStatus?.IsUnderWarranty == true))
        {
            LoggingService.Instance.Log($"[PartsAgent] [{iteration}/ReAct] Sufficient data gathered — skipping LLM decision");
            return "Done";
        }

        var prompt = $$"""
            You are an autonomous parts research agent deciding what to do next.

            Goal: Find the best way for a technician to order a replacement part.
            Failed component: {{failedComponent}}
            Equipment model:  {{equipment.ModelNumber ?? "unknown"}}
            Serial number:    {{(string.IsNullOrWhiteSpace(equipment.SerialNumber) ? "not provided" : equipment.SerialNumber)}}
            Manufacturer:     {{equipment.Manufacturer ?? "unknown"}}

            Tools already completed: {{alreadyRun}}

            Current findings:
            {{summary}}

            All available tools:
            - WarrantyCheck      : Check if the part is covered — if YES, technician should claim warranty instead of buying
            - InventoryCheck     : Check whether the part is in stock
            - PricingComparison  : Get prices from suppliers — REQUIRED to produce ordering options
            - SupplierLookup     : Get supplier contact details and delivery options
            - CompatibilityCheck : Find alternative parts (run if stock is low or part is expensive)
            - LaborCostEstimate  : Estimate labor hours and total repair cost — run after PricingComparison

            Rules:
            - Do NOT choose a tool that already appears in "Tools already completed".
            - If WarrantyCheck has NOT run yet, always run it next.
            - If WarrantyCheck findings show IsUnderWarranty=true, return Done — skip pricing/suppliers/labor.
            - PricingComparison MUST run before Done (unless warranty covers the part).
            - LaborCostEstimate MUST run after PricingComparison and before Done.
            - Return Done only when you have both Pricing and LaborCost data, or warranty is confirmed.

            Respond with ONLY this JSON — no explanation, no markdown:
            {"tool": "<ToolName or Done>", "reason": "<one sentence>"}
            """;

        var (response, _) = await _llmService.GenerateResponseAsync(
            prompt, maxTokens: 600, temperature: 0f, useReasoning: false);

        try
        {
            var json   = StripFences(response);
            using var doc = JsonDocument.Parse(json);
            var tool   = doc.RootElement.TryGetProperty("tool",   out var t) ? t.GetString() : null;
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : string.Empty;
            LoggingService.Instance.Log($"[PartsAgent] [{iteration}/ReAct] Reason: {reason}");
            return tool;
        }
        catch
        {
            LoggingService.Instance.Log($"[PartsAgent] [{iteration}/ReAct] Failed to parse LLM decision — stopping loop");
            return null;
        }
    }

    /// <summary>Builds a comma-separated list of tools that have already produced results.</summary>
    private static string BuildAlreadyRunList(PartsResearchFindings findings)
    {
        var run = new List<string> { "PartIdentification" };
        if (findings.WarrantyStatus   is not null)           run.Add("WarrantyCheck");
        if (findings.Availability     is not null)           run.Add("InventoryCheck");
        if (findings.Pricing          is not null)           run.Add("PricingComparison");
        if (findings.Suppliers        is { Count: > 0 })     run.Add("SupplierLookup");
        if (findings.AlternativeParts is { Count: > 0 })     run.Add("CompatibilityCheck");
        if (findings.LaborCost        is not null)           run.Add("LaborCostEstimate");
        return string.Join(", ", run);
    }

    /// <summary>
    /// Builds the prompt fragment listing only tools that have NOT yet run,
    /// so the LLM cannot choose a tool that has already produced results.
    /// </summary>
    private static string BuildRemainingToolsList(PartsResearchFindings findings)
    {
        var sb = new System.Text.StringBuilder();
        if (findings.WarrantyStatus   is null)               sb.AppendLine("- WarrantyCheck      : Check if the part is covered — if YES, technician claims warranty instead of buying");
        if (findings.Availability     is null)               sb.AppendLine("- InventoryCheck     : Check whether the part is in stock");
        if (findings.Pricing          is null)               sb.AppendLine("- PricingComparison  : Get prices from suppliers — REQUIRED to produce ordering options");
        if (findings.Suppliers        is not { Count: > 0 }) sb.AppendLine("- SupplierLookup     : Get supplier contact details and delivery options");
        if (findings.AlternativeParts is not { Count: > 0 }) sb.AppendLine("- CompatibilityCheck : Find alternative parts (run if stock is low or part is expensive)");
        if (findings.LaborCost        is null)               sb.AppendLine("- LaborCostEstimate  : Estimate labor hours and total repair cost — run after PricingComparison");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Builds a compact human-readable summary of findings so far for the LLM prompt.</summary>
    private static string SummariseFindings(PartsResearchFindings findings)
    {
        var sb = new System.Text.StringBuilder();

        if (findings.PrimaryPart is { } part)
            sb.AppendLine($"- Part identified: {part.Description} ({part.PartNumber}), OEM={part.IsOEM}, ~${part.EstimatedPrice}");

        if (findings.WarrantyStatus is { } w)
            sb.AppendLine($"- Warranty: IsUnderWarranty={w.IsUnderWarranty}, Type={w.WarrantyType}, Expires={w.ExpirationDate:yyyy-MM-dd}, Advice={w.Advice}");

        if (findings.Availability is { } inv)
            sb.AppendLine($"- Inventory: InStock={inv.InStock}, Qty={inv.Quantity}, Delivery={inv.EstimatedDelivery.TotalDays:F0} days");

        if (findings.Pricing is { } pricing)
            sb.AppendLine($"- Pricing: {pricing.Prices.Length} quote(s), best value={pricing.BestValue}, fastest={pricing.FastestDelivery}");

        if (findings.Suppliers is { Count: > 0 } suppliers)
            sb.AppendLine($"- Suppliers: {suppliers.Count} found ({string.Join(", ", suppliers.Select(s => s.Name))})");

        if (findings.AlternativeParts is { Count: > 0 } alts)
            sb.AppendLine($"- Alternatives: {alts.Count} compatible part(s) found");

        if (findings.LaborCost is { } lc)
            sb.AppendLine($"- Labor: {lc.EstimatedHours:F1} hrs, {lc.DifficultyRating}, total={lc.Currency} {lc.GrandTotal:F2}");

        return sb.Length > 0 ? sb.ToString().TrimEnd() : "No findings yet.";
    }

    // -------------------------------------------------------------------------
    // Step 3 — ask the LLM to synthesise a final order plan from findings
    // -------------------------------------------------------------------------

    private async Task<PartsOrderPlan> SynthesiseOrderPlanAsync(
        PartsResearchFindings findings,
        EquipmentInfo equipment,
        PartsConversationContext context)
    {
        var prompt = $"""
            You are a parts ordering strategist. Synthesise the research findings below into a
            clear ordering recommendation for a field technician.

            EQUIPMENT:
            Model:        {equipment.ModelNumber ?? "unknown"}
            Serial:       {equipment.SerialNumber ?? "not provided"}
            Manufacturer: {equipment.Manufacturer ?? "unknown"}

            RESEARCH FINDINGS:
            Primary Part    : {JsonSerializer.Serialize(findings.PrimaryPart)}
            Availability    : {JsonSerializer.Serialize(findings.Availability)}
            Pricing         : {JsonSerializer.Serialize(findings.Pricing)}
            Alternatives    : {JsonSerializer.Serialize(findings.AlternativeParts)}
            Warranty Status : {JsonSerializer.Serialize(findings.WarrantyStatus)}
            Labor Estimate  : {JsonSerializer.Serialize(findings.LaborCost)}
            Urgency Context : {BuildUrgencySummary(context)}

            Provide:
            1. Overall recommendation (2-3 sentences)
            2. Urgency assessment
            3. Proactive recommendations (other parts that commonly fail at the same time)

            Use plain prose. Begin proactive recommendations lines with a dash (-).
            """;

        var (response, _) = await _llmService.GenerateResponseAsync(prompt, maxTokens: 800, temperature: 0.2f, useReasoning: true);

        var bestPartsCost = findings.Pricing?.Prices.Length > 0
            ? findings.Pricing.Prices.Min(p => p.TotalCost)
            : (decimal?)null;
        var totalRepairCost = bestPartsCost.HasValue && findings.LaborCost is not null
            ? bestPartsCost.Value + findings.LaborCost.GrandTotal
            : (decimal?)null;

        return new PartsOrderPlan
        {
            PrimaryPart              = findings.PrimaryPart,
            Options                  = BuildOrderOptions(findings),
            Recommendations          = ExtractSection(response, "RECOMMENDATION"),
            WarrantyAdvice           = findings.WarrantyStatus?.Advice,
            UrgencyAssessment        = ExtractUrgency(response),
            ProactiveRecommendations = ExtractProactive(response),
            LaborCost                = findings.LaborCost,
            TotalRepairCost          = totalRepairCost
        };
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static List<OrderOption> BuildOrderOptions(PartsResearchFindings findings)
    {
        if (findings.Pricing?.Prices is not { Length: > 0 } prices)
            return [];

        var cheapest = prices.MinBy(p => p.TotalCost)!.Supplier;
        var fastest  = prices.MinBy(p => PricingTool.DeliverySpan(p.DeliveryTime))!.Supplier;

        var options = prices.Select((p, i) => new OrderOption
        {
            Supplier     = p.Supplier,
            Price        = p.Price,
            DeliveryTime = p.DeliveryTime,
            PartNumber   = findings.PrimaryPart?.PartNumber  ?? string.Empty,
            Description  = findings.PrimaryPart?.Description ?? string.Empty,
            Recommendation = DetermineRecommendation(p.Supplier, cheapest, fastest),
            Priority     = i
        }).ToList();

        return [.. options.OrderBy(o => o.Priority)];
    }

    private static string DetermineRecommendation(string supplier, string cheapest, string fastest)
    {
        var isCheapest = supplier == cheapest;
        var isFastest  = supplier == fastest;
        return (isCheapest, isFastest) switch
        {
            (true, true)  => "Best Overall",
            (true, false) => "Best Price",
            (false, true) => "Fastest Delivery",
            _             => "Alternative Option"
        };
    }

    private static string BuildUrgencySummary(PartsConversationContext ctx)
    {
        var factors = new List<string>();
        if (ctx.IsCustomerWaiting)  factors.Add("customer on-site");
        if (ctx.IsHotWeather)       factors.Add($"hot weather{(ctx.CurrentTemperature.HasValue ? $" ({ctx.CurrentTemperature}°F)" : "")}");
        if (ctx.IsSafetyIssue)      factors.Add("safety concern");
        if (ctx.IsBusinessCritical) factors.Add("business-critical system");
        return factors.Count > 0 ? string.Join(", ", factors) : "standard priority";
    }

    private static string ExtractSection(string response, string sectionKeyword)
    {
        var lines  = response.Split('\n');
        var result = lines.SkipWhile(l => !l.Contains(sectionKeyword, StringComparison.OrdinalIgnoreCase));
        return string.Join('\n', result).Trim();
    }

    private static string ExtractUrgency(string response)
    {
        var line = response.Split('\n')
            .FirstOrDefault(l => l.Contains("URGENCY", StringComparison.OrdinalIgnoreCase));
        return line?.Trim() ?? "Standard priority";
    }

    private static List<string> ExtractProactive(string response) =>
        response.Split('\n')
            .SkipWhile(l => !l.Contains("PROACTIVE", StringComparison.OrdinalIgnoreCase))
            .Where(l => l.TrimStart().StartsWith('-'))
            .Select(l => l.Trim())
            .ToList();

    private static string StripFences(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```")) return t;
        var start = t.IndexOf('{');
        var end   = t.LastIndexOf('}');
        return start >= 0 && end > start ? t[start..(end + 1)] : t;
    }

}

