using System.Collections.Generic;

namespace TechnicianAssistant.Services;

/// <summary>
/// Captures the runtime context that the <see cref="HardRulesEngine"/> inspects
/// when deciding whether a query should be handled locally or escalated to the cloud.
/// </summary>
public sealed class ConversationContext
{
    /// <summary>Whether the device has no network connectivity.</summary>
    public bool IsOffline { get; init; }

    /// <summary>Whether the current turn has an image attachment.</summary>
    public bool HasImageAttachment { get; init; }

    /// <summary>Whether raw audio was captured for this turn.</summary>
    public bool HasAudioRecording { get; init; }

    /// <summary>Whether a voice transcription is present for this turn.</summary>
    public bool HasVoiceTranscription { get; init; }

    /// <summary>Troubleshooting steps already attempted in this conversation.</summary>
    public IReadOnlyList<string> TroubleshootingSteps { get; init; } = [];

    /// <summary>Optional user-defined preferences that can override routing.</summary>
    public UserPreferences? UserPreferences { get; init; }

    /// <summary>
    /// Returns <see langword="true"/> when the conversation has enough prior turns
    /// to be considered a complex, multi-step interaction.
    /// </summary>
    public bool HasComplexHistory() => TroubleshootingSteps.Count >= 3;

    /// <summary>
    /// Returns <see langword="true"/> when the current turn warrants the local reasoning
    /// model rather than the standard local chat model.
    /// <para>
    /// The reasoning model runs locally and is therefore available whether or not the
    /// device is online. It is appropriate when the conversation is at a dead end or
    /// diagnosis is ambiguous — specifically when multiple troubleshooting steps have
    /// already been attempted without resolution. It is <b>not</b> needed when the turn
    /// only contains an image or audio attachment (a vision/transcription model is the
    /// right tool), or when the user has forced local <em>standard</em> processing.
    /// </para>
    /// </summary>
    public bool RequiresDeepReasoning() =>
        UserPreferences?.ForceLocal != true &&
        !HasImageAttachment &&
        HasComplexHistory();

    /// <summary>
    /// Returns a compact, human-readable summary of the context for use in judge-model prompts.
    /// </summary>
    public string GetSummary()
    {
        var parts = new System.Collections.Generic.List<string>();
        if (IsOffline)             parts.Add("offline");
        if (HasImageAttachment)    parts.Add("has image");
        if (HasAudioRecording)     parts.Add("has audio");
        if (HasVoiceTranscription) parts.Add("has transcription");
        if (TroubleshootingSteps.Count > 0)
            parts.Add($"{TroubleshootingSteps.Count} prior troubleshooting step(s)");
        if (RequiresDeepReasoning())
            parts.Add("deep reasoning recommended");
        if (UserPreferences?.ForceLocal == true)
            parts.Add("user prefers local");
        return parts.Count > 0 ? string.Join(", ", parts) : "no additional context";
    }
}

/// <summary>User-configurable routing preferences.</summary>
public sealed class UserPreferences
{
    /// <summary>When <see langword="true"/> the engine will never escalate to the cloud.</summary>
    public bool ForceLocal { get; init; }
}
