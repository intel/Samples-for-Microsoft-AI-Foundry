using System.Threading.Tasks;
using TechnicianAssistant.Services;

namespace TechnicianAssistant.ViewModels;

/// <summary>
/// Raised when the user manually requests a cloud escalation so the UI can
/// show a cost-confirmation dialog before the API call is made.
/// The handler must complete <see cref="Decision"/> with <see langword="true"/>
/// to proceed or <see langword="false"/> to cancel.
/// </summary>
public sealed class CloudConfirmationEventArgs : System.EventArgs
{
    /// <summary>Estimated input tokens for the pending cloud request.</summary>
    public int EstimatedInputTokens { get; }

    /// <summary>
    /// Estimated cost in USD using the configured CloudPricing rates.
    /// Output is estimated at 20% of input tokens.
    /// </summary>
    public double EstimatedCostUsd { get; }

    /// <summary>
    /// Set to <see langword="true"/> to proceed with the cloud call,
    /// <see langword="false"/> to cancel.
    /// </summary>
    public TaskCompletionSource<bool> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CloudConfirmationEventArgs(int estimatedInputTokens)
    {
        EstimatedInputTokens = estimatedInputTokens;
        var usage            = TokenUsageService.Instance;
        var estimatedOutput  = estimatedInputTokens * 0.2;
        EstimatedCostUsd     = (estimatedInputTokens / 1_000_000.0 * usage.InputPricePerMillion)
                             + (estimatedOutput       / 1_000_000.0 * usage.OutputPricePerMillion);
    }
}
