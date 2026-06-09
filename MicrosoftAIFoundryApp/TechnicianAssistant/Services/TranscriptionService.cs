using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TechnicianAssistant.Services.Interfaces;

namespace TechnicianAssistant.Services;

public class TranscriptionService : ITranscriptionService
{
    private WhisperOnnxService? _whisperService;
    private readonly string? _modelPath;
    private Action<string>? _logger;
    private bool _isInitialized;

    public TranscriptionService()
    {
        LoggingService.Instance.Log("=======================================");
        LoggingService.Instance.Log("[*] TranscriptionService: no model path configured");
        LoggingService.Instance.Log("[!] Set WhisperModelPath in appsettings.json to enable transcription");
        LoggingService.Instance.Log("=======================================");
    }

    public TranscriptionService(string modelPath)
    {
        _modelPath = modelPath;
        LoggingService.Instance.Log("=======================================");
        LoggingService.Instance.Log($"[*] TranscriptionService: model path set to {modelPath}");
        LoggingService.Instance.Log("=======================================");
    }

    public void SetLogger(Action<string> logger)
    {
        _logger = logger;
    }

    private void Log(string message)
    {
        LoggingService.Instance.Log(message);
        _logger?.Invoke(message + "\n");
    }

    public async Task<string> TranscribeAsync(float[] audioSamples)
    {
        if (audioSamples.Length == 0)
        {
            return "No audio detected.";
        }

        var duration = audioSamples.Length / 16000.0; // 16kHz sample rate
        Log($"Transcribing {duration:F2} seconds of audio ({audioSamples.Length:N0} samples)");

        try
        {
            // Initialize Whisper service if not already done
            if (!_isInitialized)
            {
                await InitializeWhisperAsync();
            }

            if (_whisperService == null)
            {
                Log("[!] Whisper model not available, using fallback");
                return $"[Audio Captured: {duration:F2}s]\n\nWhisper model not configured.\n" +
                       "To enable transcription:\n" +
                       "1. Download Whisper ONNX model\n" +
                       "2. Set model path in app settings\n" +
                       "3. Restart application";
            }

            // Transcribe using Whisper
            Log("[*] Running Whisper transcription...");
            var transcription = await _whisperService.TranscribeAsync(audioSamples, CancellationToken.None);
            
            Log($"[+] Transcription complete: {transcription.Length} characters");
            return string.IsNullOrWhiteSpace(transcription) 
                ? "No speech detected in audio." 
                : transcription;
        }
        catch (Exception ex)
        {
            Log($"[x] Transcription error: {ex.Message}");
            return $"[Audio Captured: {duration:F2}s]\n\n" +
                   $"Transcription failed: {ex.Message}\n\n" +
                   "Please check:\n" +
                   "- Whisper model is properly installed\n" +
                   "- Model path is correct\n" +
                   "- Audio quality is sufficient";
        }
    }

    public async Task<string> TranscribeStreamingAsync(float[] audioSamples, Action<string> onPartialText)
    {
        if (audioSamples.Length == 0)
        {
            return "No audio detected.";
        }

        try
        {
            if (!_isInitialized)
            {
                await InitializeWhisperAsync();
            }

            if (_whisperService == null)
            {
                return await TranscribeAsync(audioSamples);
            }

            Log("[*] Running streaming Whisper transcription...");
            
            // Use streaming API to update UI progressively
            await _whisperService.TranscribeStreamingAsync(
                audioSamples, 
                onPartialText, 
                CancellationToken.None
            );

            // The final text will be delivered via the callback
            return string.Empty;
        }
        catch (Exception ex)
        {
            Log($"[x] Streaming transcription error: {ex.Message}");
            throw;
        }
    }

    public async Task<string> RunSampleAudioTestAsync()
    {
        if (!_isInitialized)
            await InitializeWhisperAsync();

        if (_whisperService == null)
        {
            Log("[!] Whisper not initialized — cannot run sample audio test");
            return "[!] Whisper model not available";
        }

        return await _whisperService.TestWithSampleAudioAsync();
    }

    private async Task InitializeWhisperAsync()
    {
        if (_isInitialized)
            return;

        try
        {
            if (string.IsNullOrEmpty(_modelPath))
            {
                Log("[!] Whisper model path not configured");
                _isInitialized = true;
                return;
            }

            if (!Directory.Exists(_modelPath))
            {
                Log($"[!] Whisper model directory not found: {_modelPath}");
                _isInitialized = true;
                return;
            }

                Log($"[*] Loading Whisper model from: {_modelPath}");
            _whisperService = new WhisperOnnxService(_modelPath);
            Log("[+] Whisper model loaded successfully");
        }
        catch (Exception ex)
        {
            // More specific error handling for OrtEnv singleton conflicts
            if (ex.Message.Contains("OrtEnv singleton"))
            {
                Log($"[!] ONNX Runtime environment already exists (shared with EmbeddingService)");
                Log($"[!] But model initialization still failed: {ex.Message}");
            }
            else
            {
                Log($"[x] Failed to load Whisper model: {ex.Message}");
            }
        }
        finally
        {
            _isInitialized = true;
        }

        await Task.CompletedTask;
    }
}


