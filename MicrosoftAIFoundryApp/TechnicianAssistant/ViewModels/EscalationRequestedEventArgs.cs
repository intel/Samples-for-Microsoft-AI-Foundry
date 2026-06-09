using System;
using System.Threading.Tasks;

namespace TechnicianAssistant.ViewModels
{
    /// <summary>
    /// Raised by <see cref="VoiceSupportViewModel"/> when the local model's confidence is
    /// below the escalation threshold and the user should be asked whether to consult the
    /// cloud model. Set <see cref="Decision"/> to <see langword="true"/> to escalate or
    /// <see langword="false"/> to keep the local answer.
    /// </summary>
    public sealed class EscalationRequestedEventArgs : EventArgs
    {
        /// <summary>
        /// Confidence score returned by the local model (0–100), or -1 when the model
        /// could not produce a score (N/A).
        /// </summary>
        public double Confidence { get; }

        /// <summary>
        /// Complete this with <see langword="true"/> to proceed with cloud escalation or
        /// <see langword="false"/> to keep the local answer as-is.
        /// </summary>
        public TaskCompletionSource<bool> Decision { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EscalationRequestedEventArgs(double confidence) => Confidence = confidence;
    }
}
