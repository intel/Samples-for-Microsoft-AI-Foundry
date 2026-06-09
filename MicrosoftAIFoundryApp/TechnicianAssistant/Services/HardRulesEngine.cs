using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace TechnicianAssistant.Services;

/// <summary>
/// Applies a deterministic set of hard rules to decide whether a query should be
/// routed to the cloud or handled locally — bypassing the probabilistic judge model.
/// </summary>
public class HardRulesEngine
{
    private readonly string[] _safetyKeywords =
    {
        "emergency", "danger", "fire", "smoke", "sparks", "burning", "explosion",
        "gas leak", "smell gas", "electrical shock", "electrocuted", "toxic",
        "poisonous", "leak", "flooding", "overheating", "hot surface"
    };

    private readonly string[] _simpleKeywords =
    {
        "filter size", "part number", "model number", "serial number",
        "reset button", "power switch",
        "warranty", "contact", "phone number", "hours", "location"
    };

    private readonly string[] _cloudRequiredKeywords =
    {
        "building management", "BMS", "integration", "network", "protocol",
        "compliance", "regulation", "EPA", "code requirement", "certification"
    };

    /// <summary>
    /// Evaluates the hard rules in priority order and returns a routing decision.
    /// </summary>
    /// <param name="query">The raw user query.</param>
    /// <param name="context">Runtime context for the current conversation turn.</param>
    /// <returns>
    /// A <see cref="HardRuleDecision"/> whose <see cref="HardRuleDecision.IsDefinitive"/>
    /// flag indicates whether the result is conclusive. When <see langword="false"/> the
    /// caller should fall back to the judge model.
    /// </returns>
    public HardRuleDecision ApplyHardRules(string query, ConversationContext context)
    {
        var queryLower = query.ToLowerInvariant();

        // Rule 1: Safety-Critical (Always Cloud)
        if (IsSafetyCritical(queryLower))
        {
            return HardRuleDecision.CloudAdvanced(
                reason: "Safety-critical situation detected",
                priority: Priority.Emergency);
        }

        // Rule 2: Multi-Modal Content (Always Cloud)
        if (HasMultiModalContent(context))
        {
            return HardRuleDecision.CloudAdvanced(
                reason: "Multi-modal analysis required (image + text/audio)");
        }

        // Rule 3: Offline Mode (Force Local)
        if (context.IsOffline)
        {
            return context.HasComplexHistory()
                ? HardRuleDecision.LocalReasoning("Offline - using local reasoning")
                : HardRuleDecision.LocalStandard("Offline - using local standard");
        }

        // Rule 4: Simple Lookups (Local Standard) — always takes priority over history-based
        // rules; a factual spec lookup is still simple regardless of how many prior steps exist.
        if (IsSimpleLookup(queryLower))
        {
            return HardRuleDecision.LocalStandard(
                reason: "Simple information lookup detected");
        }

        // Rule 5: Deep-reasoning signal — context has accumulated enough failed steps that
        // a reasoning model is warranted.
        if (context.RequiresDeepReasoning())
        {
            return HardRuleDecision.LocalReasoning(
                reason: $"Multi-step troubleshooting history ({context.TroubleshootingSteps.Count} prior steps) — deep reasoning required");
        }

        // Rule 5b: Fewer than 3 steps but still building history — use local reasoning
        // without the full deep-reasoning escalation.
        if (context.TroubleshootingSteps.Count >= 2)
        {
            return HardRuleDecision.LocalReasoning(
                reason: $"Multi-step troubleshooting history ({context.TroubleshootingSteps.Count} prior steps)");
        }

        // Rule 6: Integration/Compliance (Cloud Required)
        if (RequiresCloudKnowledge(queryLower))
        {
            return HardRuleDecision.CloudAdvanced(
                reason: "Requires specialized knowledge beyond local manuals");
        }

        // Rule 7: Multi-Step Troubleshooting Pattern (keyword-based, catches turn 2 before history builds)
        if (IsMultiStepTroubleshooting(query, context))
        {
            return HardRuleDecision.LocalReasoning(
                reason: "Multi-step troubleshooting sequence detected");
        }

        // Rule 7: User Preference Override
        if (context.UserPreferences?.ForceLocal == true)
        {
            return HardRuleDecision.LocalReasoning(
                reason: "User preference: local processing only");
        }

        // No hard rule applies — delegate to the judge model
        return HardRuleDecision.NotDefinitive("No hard rules matched - use judge model");
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private bool IsSafetyCritical(string queryLower) =>
        _safetyKeywords.Any(kw => queryLower.Contains(kw)) ||
        ContainsEmergencyPhrases(queryLower);

    private static bool ContainsEmergencyPhrases(string queryLower)
    {
        var emergencyPhrases = new[]
        {
            "call 911", "need help immediately", "urgent", "critical failure",
            "system down", "not responding", "completely failed"
        };

        return emergencyPhrases.Any(phrase => queryLower.Contains(phrase));
    }

    private static bool HasMultiModalContent(ConversationContext context) =>
        context.HasImageAttachment ||
        (context.HasImageAttachment && context.HasVoiceTranscription);

    private bool IsSimpleLookup(string queryLower)
    {
        var simplePatterns = new[]
        {
            @"what is the .* for",
            @"where is the .*",
            @"how much .*",
            @"what size .*",
            @"which .* should I use"
        };

        return _simpleKeywords.Any(kw => queryLower.Contains(kw)) ||
               simplePatterns.Any(pattern => Regex.IsMatch(queryLower, pattern));
    }

    private bool RequiresCloudKnowledge(string queryLower) =>
        _cloudRequiredKeywords.Any(kw => queryLower.Contains(kw)) ||
        ContainsRegulatoryTerms(queryLower);

    private static bool ContainsRegulatoryTerms(string queryLower)
    {
        var regulatoryTerms = new[]
        {
            "regulation", "compliance", "code", "standard", "certification",
            "EPA", "OSHA", "building code", "local ordinance"
        };

        return regulatoryTerms.Any(term => queryLower.Contains(term));
    }

    private static bool IsMultiStepTroubleshooting(string query, ConversationContext context)
    {
        var indicators = new[]
        {
            "I tried", "I checked", "I already", "still not working",
            "next step", "what else", "also tried", "still having"
        };

        return indicators.Any(ind => query.Contains(ind, StringComparison.OrdinalIgnoreCase)) ||
               context.TroubleshootingSteps.Count >= 2;
    }

}

// ---------------------------------------------------------------------------
// Supporting types
// ---------------------------------------------------------------------------

/// <summary>The outcome of the hard-rules evaluation.</summary>
public sealed class HardRuleDecision
{
    /// <summary>The routing target, valid only when <see cref="IsDefinitive"/> is <see langword="true"/>.</summary>
    public RoutingTarget Target { get; private set; }

    /// <summary>Human-readable explanation of the decision.</summary>
    public string Reason { get; private set; } = string.Empty;

    /// <summary>Emergency priority used for safety-critical escalations.</summary>
    public Priority Priority { get; private set; }

    /// <summary>
    /// <see langword="true"/> when the engine reached a conclusive decision;
    /// <see langword="false"/> when the caller should fall back to the judge model.
    /// </summary>
    public bool IsDefinitive { get; private set; }

    public static HardRuleDecision CloudAdvanced(string reason, Priority priority = Priority.Normal) =>
        new() { Target = RoutingTarget.CloudAdvanced, Reason = reason, Priority = priority, IsDefinitive = true };

    public static HardRuleDecision LocalReasoning(string reason) =>
        new() { Target = RoutingTarget.LocalReasoning, Reason = reason, IsDefinitive = true };

    public static HardRuleDecision LocalStandard(string reason) =>
        new() { Target = RoutingTarget.LocalStandard, Reason = reason, IsDefinitive = true };

    public static HardRuleDecision NotDefinitive(string reason) =>
        new() { Reason = reason, IsDefinitive = false };
}

/// <summary>Identifies which inference back-end should handle the request.</summary>
public enum RoutingTarget
{
    LocalStandard,
    LocalReasoning,
    CloudAdvanced
}

/// <summary>Urgency level attached to a routing decision.</summary>
public enum Priority
{
    Normal,
    High,
    Emergency
}
