using System;
using System.Threading;
using System.Threading.Tasks;

namespace TechnicianAssistant.Services.Interfaces
{
    public interface IAudioCaptureService
    {
        bool IsRecording { get; }
        event EventHandler<float>? LevelChanged;
        Task<float[]> RecordAsync(CancellationToken cancellationToken = default);
        void StopRecording();
    }
}
