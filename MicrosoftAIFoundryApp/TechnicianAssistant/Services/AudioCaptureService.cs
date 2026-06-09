using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TechnicianAssistant.Services.Interfaces;

namespace TechnicianAssistant.Services;

/// <summary>
/// Records from the default microphone at 16 kHz mono 16-bit PCM using NAudio WaveInEvent.
/// </summary>
public sealed class AudioCaptureService : IAudioCaptureService, IDisposable
{
    private const int SampleRate  = 16_000;
    private const int Channels    = 1;
    private const int BitsPerSample = 16;

    private WaveInEvent? _waveIn;
    private readonly List<float> _audioBuffer = new();
    private bool _isRecording;
    private bool _disposed;

    public bool IsRecording => _isRecording;

    /// <summary>Fires on each 100 ms buffer with the RMS amplitude (0-1).</summary>
    public event EventHandler<float>? LevelChanged;

    // ──────────────────────────────────────────────────────────────────────────
    // Public API
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts recording.  The returned Task completes when <see cref="StopRecording"/> is
    /// called or when <paramref name="cancellationToken"/> is cancelled.
    /// Returns the captured float32 mono 16 kHz PCM samples.
    /// </summary>
    public Task<float[]> RecordAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _audioBuffer.Clear();

        var tcs = new TaskCompletionSource<float[]>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _waveIn = new WaveInEvent
        {
            WaveFormat       = new WaveFormat(SampleRate, BitsPerSample, Channels),
            BufferMilliseconds = 100
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += (_, _) => tcs.TrySetResult([.. _audioBuffer]);

        _isRecording = true;
        _waveIn.StartRecording();

        cancellationToken.Register(StopRecording);

        return tcs.Task;
    }

    public void StopRecording()
    {
        if (!_isRecording) return;
        _isRecording = false;
        _waveIn?.StopRecording();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────────────────────────────────

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int sampleCount = e.BytesRecorded / 2;
        float sumSquares = 0f;

        for (int i = 0; i < e.BytesRecorded; i += 2)
        {
            float sample = BitConverter.ToInt16(e.Buffer, i) / 32768.0f;
            _audioBuffer.Add(sample);
            sumSquares += sample * sample;
        }

        float rms = (float)Math.Sqrt(sumSquares / sampleCount);
        LevelChanged?.Invoke(this, rms);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopRecording();
        _waveIn?.Dispose();
    }
}
