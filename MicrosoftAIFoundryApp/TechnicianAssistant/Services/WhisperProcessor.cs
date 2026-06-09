using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace TechnicianAssistant.Services;

/// <summary>
/// Handles the two CPU-bound preprocessing steps for Whisper large-v3:
///   1. Log-mel spectrogram computation (128 mels, 30-second window).
///   2. Token-ID → text decoding using the GPT-2 byte-level BPE vocabulary.
///
/// The FFT uses a 400-sample Hann window zero-padded to 512 points (next power of
/// two). The mel filterbank is built with Slaney mel scaling and area normalisation,
/// which closely matches the librosa filterbank that Whisper's training pipeline used.
/// </summary>
public sealed class WhisperProcessor
{
    // ── Audio / spectrogram constants ────────────────────────────────────────
    public const int SampleRate  = 16_000;
    public const int NMels       = 128;        // whisper-large-v3 uses 128 mel bins
    public const int NFrames     = 3_000;      // 30 s × 100 frames/s
    private const int WindowSize = 400;        // STFT Hann window length
    private const int FftSize    = 512;        // next power-of-2 → zero-pad from 400
    private const int FftHalf    = FftSize / 2 + 1;   // 257 positive-freq bins
    private const int HopLength  = 160;        // 10 ms hop at 16 kHz

    // ── Whisper-large-v3 special token IDs ───────────────────────────────────
    // (multilingual tokenizer, vocab size ≈ 51 866)
    public const long TokenStartOfTranscript = 50_258;
    public const long TokenEnglish           = 50_259;   // <|en|>
    public const long TokenTranscribe        = 50_360;
    public const long TokenNoTimestamps      = 50_364;
    public const long TokenEndOfText         = 50_256;

    private readonly float[]   _hannWindow;
    private readonly float[,]  _melFilterbank;          // [NMels, FftHalf]
    private readonly Dictionary<int, string> _idToToken;
    private readonly Dictionary<char, byte>  _byteDecoder;  // GPT-2 unicode→byte

    public WhisperProcessor(string modelDirectory)
    {
        _hannWindow    = BuildHannWindow();
        _melFilterbank = BuildMelFilterbank();
        _idToToken     = LoadVocabulary(modelDirectory);
        _byteDecoder   = BuildByteDecoder();
    }

    // ────────────────────────────────────────────────────────────────────────
    // 1.  Log-mel spectrogram
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a flat float array of shape [NMels × NFrames] = [128 × 3000]
    /// ready to be wrapped in a DenseTensor of shape [1, 128, 3000].
    /// </summary>
    public float[] ComputeLogMelSpectrogram(float[] audio)
    {
        // Pad / trim to exactly 30 seconds
        int targetLen   = SampleRate * 30;
        var padded      = new float[targetLen];
        Array.Copy(audio, padded, Math.Min(audio.Length, targetLen));

        var melSpec      = new float[NMels * NFrames];
        var fftBuf       = new Complex[FftSize];
        var powerSpec    = new float[FftHalf];

        for (int frame = 0; frame < NFrames; frame++)
        {
            int start = frame * HopLength;

            // Apply 400-sample Hann window and zero-pad to 512
            for (int i = 0; i < FftSize; i++)
            {
                double s = (i < WindowSize && start + i < padded.Length)
                    ? padded[start + i] * _hannWindow[i]
                    : 0.0;
                fftBuf[i] = new Complex(s, 0.0);
            }

            Fft(fftBuf);

            // Power spectrum (positive frequencies only)
            for (int k = 0; k < FftHalf; k++)
                powerSpec[k] = (float)(fftBuf[k].Real * fftBuf[k].Real
                                     + fftBuf[k].Imaginary * fftBuf[k].Imaginary);

            // Apply mel filterbank
            for (int m = 0; m < NMels; m++)
            {
                float v = 0f;
                for (int k = 0; k < FftHalf; k++)
                    v += _melFilterbank[m, k] * powerSpec[k];

                melSpec[m * NFrames + frame] = Math.Max(v, 1e-10f);
            }
        }

        // Whisper normalisation: log10 → clamp to max-8 → scale to [-1, 1]
        float logMax = float.NegativeInfinity;
        for (int i = 0; i < melSpec.Length; i++)
        {
            melSpec[i] = (float)Math.Log10(melSpec[i]);
            if (melSpec[i] > logMax) logMax = melSpec[i];
        }
        for (int i = 0; i < melSpec.Length; i++)
            melSpec[i] = (Math.Max(melSpec[i], logMax - 8f) + 4f) / 4f;

        return melSpec;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 2.  Token decoding
    // ────────────────────────────────────────────────────────────────────────

    public string DecodeTokens(IEnumerable<long> tokenIds)
    {
        var sb = new StringBuilder();
        foreach (long id in tokenIds)
        {
            if (id == TokenEndOfText) break;
            if (id >= TokenStartOfTranscript) continue;   // skip all special tokens

            if (_idToToken.TryGetValue((int)id, out string? token))
                sb.Append(DecodeGpt2Token(token));
        }
        return sb.ToString().Trim();
    }

    // ────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ────────────────────────────────────────────────────────────────────────

    // Periodic Hann window (matches PyTorch's torch.hann_window default)
    private static float[] BuildHannWindow()
    {
        var w = new float[WindowSize];
        for (int i = 0; i < WindowSize; i++)
            w[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / WindowSize)));
        return w;
    }

    // Triangular mel filterbank with Slaney mel scale and area normalisation.
    // Designed for a 512-point FFT at 16 kHz → 257 bins at 31.25 Hz spacing.
    private static float[,] BuildMelFilterbank()
    {
        const double fMin = 0.0;
        const double fMax = 8_000.0;

        var fb = new float[NMels, FftHalf];

        double melMin = HzToMelSlaney(fMin);
        double melMax = HzToMelSlaney(fMax);

        // NMels + 2 equally-spaced mel points (includes lower and upper edge)
        var melPts = new double[NMels + 2];
        for (int i = 0; i <= NMels + 1; i++)
            melPts[i] = melMin + (melMax - melMin) * i / (NMels + 1);

        var hzPts = new double[NMels + 2];
        for (int i = 0; i <= NMels + 1; i++)
            hzPts[i] = MelToHzSlaney(melPts[i]);

        // Fractional FFT-bin positions (512-pt FFT, 16 kHz)
        var binPts = new double[NMels + 2];
        for (int i = 0; i <= NMels + 1; i++)
            binPts[i] = (FftSize + 1) * hzPts[i] / SampleRate;

        for (int m = 0; m < NMels; m++)
        {
            // Slaney area-normalisation factor
            double enorm = hzPts[m + 2] > hzPts[m]
                ? 2.0 / (hzPts[m + 2] - hzPts[m])
                : 1.0;

            for (int k = 0; k < FftHalf; k++)
            {
                double w = 0.0;
                if (k >= binPts[m] && binPts[m + 1] > binPts[m])
                    w = (k - binPts[m]) / (binPts[m + 1] - binPts[m]);
                if (k > binPts[m + 1] && binPts[m + 2] > binPts[m + 1])
                    w = (binPts[m + 2] - k) / (binPts[m + 2] - binPts[m + 1]);

                fb[m, k] = (float)Math.Max(0.0, w * enorm);
            }
        }
        return fb;
    }

    // Slaney mel scale: linear 0–1000 Hz, logarithmic above
    private const  double FSp      = 200.0 / 3.0;
    private const  double MinLogHz = 1_000.0;
    private const  double MinLogMel = MinLogHz / FSp;
    private static readonly double LogStep = Math.Log(6.4) / 27.0;

    private static double HzToMelSlaney(double hz)
    {
        return hz < MinLogHz
            ? hz / FSp
            : MinLogMel + Math.Log(hz / MinLogHz) / LogStep;
    }

    private static double MelToHzSlaney(double mel)
    {
        return mel < MinLogMel
            ? FSp * mel
            : MinLogHz * Math.Exp(LogStep * (mel - MinLogMel));
    }

    // In-place Cooley-Tukey FFT (power-of-2 size required)
    private static void Fft(Complex[] buf)
    {
        int n    = buf.Length;
        int bits = 0;
        for (int t = n; t > 1; t >>= 1) bits++;

        // Bit-reversal permutation
        for (int i = 0; i < n; i++)
        {
            int rev = BitReverse(i, bits);
            if (rev > i) (buf[i], buf[rev]) = (buf[rev], buf[i]);
        }

        // Butterfly stages
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2.0 * Math.PI / len;
            var wn = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                Complex w = Complex.One;
                for (int j = 0; j < len / 2; j++)
                {
                    Complex u = buf[i + j];
                    Complex v = buf[i + j + len / 2] * w;
                    buf[i + j]           = u + v;
                    buf[i + j + len / 2] = u - v;
                    w *= wn;
                }
            }
        }
    }

    private static int BitReverse(int x, int bits)
    {
        int r = 0;
        for (int i = 0; i < bits; i++) { r = (r << 1) | (x & 1); x >>= 1; }
        return r;
    }

    // ── Vocabulary ─────────────────────────────────────────────────────────

    private static Dictionary<int, string> LoadVocabulary(string modelDirectory)
    {
        string path = Path.Combine(modelDirectory, "vocab.json");
        if (!File.Exists(path)) return [];

        using var stream = File.OpenRead(path);
        var tokenToId = JsonSerializer.Deserialize<Dictionary<string, int>>(stream) ?? [];

        var idToToken = new Dictionary<int, string>(tokenToId.Count);
        foreach ((string token, int id) in tokenToId)
            idToToken[id] = token;

        return idToToken;
    }

    // ── GPT-2 byte-level BPE decode ──────────────────────────────────────────

    // Build the inverse of GPT-2's bytes_to_unicode() mapping: unicode char → byte value
    private static Dictionary<char, byte> BuildByteDecoder()
    {
        // Bytes that map to themselves (printable ASCII + Latin-1 supplement)
        var bs = new List<int>();
        for (int b = '!'; b <= '~'; b++) bs.Add(b);
        for (int b = '¡'; b <= '¬'; b++) bs.Add(b);
        for (int b = '®'; b <= 'ÿ'; b++) bs.Add(b);

        var cs = new List<int>(bs);
        int n = 0;
        for (int b = 0; b < 256; b++)
        {
            if (!bs.Contains(b)) { bs.Add(b); cs.Add(256 + n++); }
        }

        var decoder = new Dictionary<char, byte>(bs.Count);
        for (int i = 0; i < bs.Count; i++)
            decoder[(char)cs[i]] = (byte)bs[i];

        return decoder;
    }

    private string DecodeGpt2Token(string token)
    {
        // Each character in the BPE token is a unicode proxy for a single byte
        var bytes = new byte[token.Length];
        int count = 0;
        foreach (char c in token)
        {
            if (_byteDecoder.TryGetValue(c, out byte b))
                bytes[count++] = b;
        }
        return Encoding.UTF8.GetString(bytes, 0, count);
    }
}
