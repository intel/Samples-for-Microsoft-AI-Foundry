using Microsoft.ML.OnnxRuntimeGenAI;
using Microsoft.Windows.AI.MachineLearning;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;


/// <summary>
/// Transcribes float32 PCM audio (16 kHz, mono) to text using a Whisper model
/// loaded via OnnxRuntimeGenAI.
///
/// The model directory must contain the GenAI model artefacts produced by the
/// Olive / ONNX GenAI builder tool (genai_config.json + encoder/decoder ONNX files).
///
/// Usage:
///   var svc = new WhisperOnnxService(@"C:\models\whisper-large-v3");
///   string text = await svc.TranscribeAsync(pcmSamples);
/// </summary>
public sealed class WhisperOnnxService : IDisposable
{
    private readonly Model? _model;
    private readonly MultiModalProcessor? _processor;
    private bool _disposed;

    public WhisperOnnxService(string modelDirectory)
    {
        RegisterAvailableProviders();

        using var config = new Config(modelDirectory);
        _model = new Model(config);
        _processor = new MultiModalProcessor(_model);
    }

    

    /// <summary>
    /// Transcribes <paramref name="audioSamples"/> (float32, 16 kHz, mono) to text.
    /// Runs on a thread-pool thread so the UI stays responsive.
    /// </summary>
    public Task<string> TranscribeAsync(float[] audioSamples,
                                        CancellationToken ct = default)
        => Task.Run(() => Transcribe(audioSamples, ct), ct);

    /// <summary>
    /// Transcribes audio and fires <paramref name="onPartialText"/> after each decoded token,
    /// so callers can stream text progressively into the UI.
    /// </summary>
    public Task TranscribeStreamingAsync(float[] audioSamples,
                                         Action<string> onPartialText,
                                         CancellationToken ct = default)
        => Task.Run(() => TranscribeStreaming(audioSamples, onPartialText, ct), ct);

    /// <summary>
    /// Downloads the LibriSpeech sample WAV from OpenVINO storage and transcribes it,
    /// mirroring the Python test snippet. Returns the transcription result.
    /// </summary>
    public async Task<string> TestWithSampleAudioAsync(CancellationToken ct = default)
    {
        const string sampleUrl  = "https://storage.openvinotoolkit.org/models_contrib/speech/2021.2/librispeech_s5/how_are_you_doing_today.wav";
        const string sampleName = "how_are_you_doing_today.wav";

        string localPath = Path.Combine(Path.GetTempPath(), sampleName);

        using var http = new HttpClient();
        byte[] bytes = await http.GetByteArrayAsync(sampleUrl, ct);
        await File.WriteAllBytesAsync(localPath, bytes, ct);

        return await TranscribeFileAsync(localPath, ct);
    }

    /// <summary>
    /// Transcribes a WAV/audio file at <paramref name="audioFilePath"/> directly to text,
    /// without any intermediate PCM conversion.
    /// </summary>
    public Task<string> TranscribeFileAsync(string audioFilePath,
                                            CancellationToken ct = default)
        => Task.Run(() => TranscribeFile(audioFilePath, ct), ct);

    private string TranscribeFile(string audioFilePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!File.Exists(audioFilePath))
            throw new FileNotFoundException("Audio file not found.", audioFilePath);

        string[] decoderPromptTokens =
            ["<|startoftranscript|>", "<|en|>", "<|transcribe|>", "<|notimestamps|>"];
        string[] prompts = [string.Concat(decoderPromptTokens)];

        using var audios = Audios.Load(new[] { audioFilePath });
        using var inputs = _processor!.ProcessAudios(prompts, audios);
        using var genParams = new GeneratorParams(_model!);

        genParams.SetSearchOption("do_sample", false);
        genParams.SetSearchOption("num_beams", 1);
        genParams.SetSearchOption("num_return_sequences", 1);
        genParams.SetSearchOption("max_length", 448);
        genParams.SetSearchOption("batch_size", 1);

        using var generator = new Generator(_model!, genParams);
        generator.SetInputs(inputs);

        while (!generator.IsDone())
        {
            ct.ThrowIfCancellationRequested();
            generator.GenerateNextToken();
        }

        var tokens = generator.GetSequence(0);
        return _processor.Decode(tokens).Trim();
    }


    private string Transcribe(float[] audioSamples, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // OnnxRuntimeGenAI's audio pipeline expects a file path, so write the
        // PCM samples to a temporary WAV file and delete it when done.
        string tempWav = Path.Combine(Path.GetTempPath(),
                                      $"whisper_{Guid.NewGuid():N}.wav");
        try
        {
            WritePcmAsWav(tempWav, audioSamples);

            string[] prompts =
                ["<|startoftranscript|><|en|><|transcribe|><|notimestamps|>"];

            using var audios = Audios.Load(new[] { tempWav });
            using var inputs = _processor!.ProcessAudios(prompts, audios);
            using var genParams = new GeneratorParams(_model!);

            genParams.SetSearchOption("do_sample", false);
            genParams.SetSearchOption("num_beams", 1);
            genParams.SetSearchOption("num_return_sequences", 1);
            genParams.SetSearchOption("max_length", 448);
            genParams.SetSearchOption("batch_size", 1);

            using var generator = new Generator(_model!, genParams);
            generator.SetInputs(inputs);

            while (!generator.IsDone())
            {
                ct.ThrowIfCancellationRequested();
                generator.GenerateNextToken();
            }

            var tokens = generator.GetSequence(0);
            return _processor.Decode(tokens).Trim();
        }
        finally
        {
            try { File.Delete(tempWav); } catch { /* best effort */ }
        }
    }

    private void TranscribeStreaming(float[] audioSamples,
                                     Action<string> onPartialText,
                                     CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string tempWav = Path.Combine(Path.GetTempPath(),
                                      $"whisper_{Guid.NewGuid():N}.wav");
        try
        {
            WritePcmAsWav(tempWav, audioSamples);

            string[] prompts =
                ["<|startoftranscript|><|en|><|transcribe|><|notimestamps|>"];

            using var audios = Audios.Load(new[] { tempWav });
            using var inputs = _processor!.ProcessAudios(prompts, audios);
            using var genParams = new GeneratorParams(_model!);

            genParams.SetSearchOption("do_sample", false);
            genParams.SetSearchOption("num_beams", 1);
            genParams.SetSearchOption("num_return_sequences", 1);
            genParams.SetSearchOption("max_length", 448);
            genParams.SetSearchOption("batch_size", 1);

            using var generator = new Generator(_model!, genParams);
            generator.SetInputs(inputs);

            string lastDelivered = string.Empty;
            while (!generator.IsDone())
            {
                ct.ThrowIfCancellationRequested();
                generator.GenerateNextToken();

                string current = _processor.Decode(generator.GetSequence(0)).Trim();
                if (current.Length > lastDelivered.Length)
                {
                    lastDelivered = current;
                    onPartialText(current);
                }
            }

            // Guarantee the final fully-decoded text is always delivered.
            string final = _processor.Decode(generator.GetSequence(0)).Trim();
            if (final != lastDelivered)
                onPartialText(final);
        }
        finally
        {
            try { File.Delete(tempWav); } catch { /* best effort */ }
        }
    }


    /// <summary>
    /// Attempts to register available execution providers (DirectML, CUDA, â€¦)
    /// for hardware-accelerated inference. Failures are non-fatal.
    /// </summary>
    private static void RegisterAvailableProviders()
    {
        try
        {
            var catalog = ExecutionProviderCatalog.GetDefault();
            foreach (var provider in catalog.FindAllProviders())
            {
                try { provider.TryRegister(); }
                catch { /* non-critical */ }
            }
        }
        catch { /* non-critical */ }
    }

    /// <summary>
    /// Serialises float32 PCM samples as a 16-bit mono WAV file that
    /// <see cref="Audios.Load"/> can consume.
    /// </summary>
    private static void WritePcmAsWav(string path, float[] samples,
                                      int sampleRate = 16_000)
    {
        int dataBytes = samples.Length * sizeof(short);
        using var bw = new BinaryWriter(File.Create(path));

        // RIFF/WAVE header
        bw.Write(0x46464952u);      // "RIFF"
        bw.Write(36 + dataBytes);
        bw.Write(0x45564157u);      // "WAVE"

        // fmt  chunk
        bw.Write(0x20746D66u);      // "fmt "
        bw.Write(16);               // chunk size
        bw.Write((ushort)1);        // PCM format
        bw.Write((ushort)1);        // mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);   // byte rate (sampleRate Ã— blockAlign)
        bw.Write((ushort)2);        // block align
        bw.Write((ushort)16);       // bits per sample

        // data chunk
        bw.Write(0x61746164u);      // "data"
        bw.Write(dataBytes);
        foreach (float s in samples)
            bw.Write((short)Math.Clamp(s * 32768f, -32768f, 32767f));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _processor?.Dispose();
        _model?.Dispose();
    }
}

