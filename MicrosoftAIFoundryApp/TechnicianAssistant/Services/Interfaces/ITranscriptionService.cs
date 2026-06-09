using System;
using System.Threading.Tasks;

namespace TechnicianAssistant.Services.Interfaces
{
    public interface ITranscriptionService
    {
        /// <summary>
        /// Transcribes audio samples to text.
        /// </summary>
        Task<string> TranscribeAsync(float[] audioSamples);
        
        /// <summary>
        /// Transcribes audio with progressive updates via callback.
        /// </summary>
        Task<string> TranscribeStreamingAsync(float[] audioSamples, Action<string> onPartialText);
        
        /// <summary>
        /// Sets a custom logger for debugging transcription operations.
        /// </summary>
        void SetLogger(Action<string> logger);

        /// <summary>
        /// Downloads the OpenVINO sample WAV and transcribes it as a dev/test smoke-test.
        /// </summary>
        Task<string> RunSampleAudioTestAsync();
    }
}

