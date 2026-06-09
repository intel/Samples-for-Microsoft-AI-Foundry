namespace TechnicianAssistant.Services.Parts;

/// <summary>
/// Contextual factors that influence urgency assessment and ordering recommendations
/// when the parts ordering agent is running.
/// </summary>
public sealed class PartsConversationContext
{
    /// <summary>A customer is on-site waiting for the repair to be completed.</summary>
    public bool IsCustomerWaiting { get; init; }

    /// <summary>The failure is occurring during hot weather (affects urgency for cooling systems).</summary>
    public bool IsHotWeather { get; init; }

    /// <summary>The failure presents a safety risk that requires immediate resolution.</summary>
    public bool IsSafetyIssue { get; init; }

    /// <summary>The failed system is business-critical (e.g. server room, commercial refrigeration).</summary>
    public bool IsBusinessCritical { get; init; }

    /// <summary>Current ambient temperature in °F, if known.</summary>
    public int? CurrentTemperature { get; init; }
}
