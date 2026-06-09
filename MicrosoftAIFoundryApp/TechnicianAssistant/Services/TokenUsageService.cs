using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace TechnicianAssistant.Services;

/// <summary>
/// Accumulates local and cloud token usage across app launches and computes
/// both the actual cloud cost incurred and the equivalent cost saving from
/// running the local model.
///
/// Pricing (per million tokens, as of 2025 — Claude 3.5 Sonnet rates):
///   Input  : $3.00 / M tokens
///   Output : $15.00 / M tokens
///
/// Counters are persisted to <c>token_usage.json</c> in the app's local data
/// folder and survive app restarts.
/// </summary>
public sealed class TokenUsageService
{
    // ── Singleton ────────────────────────────────────────────────────────────
    private static TokenUsageService? _instance;
    private static readonly object _lock = new();

    public static TokenUsageService Instance
    {
        get { lock (_lock) { return _instance ??= new TokenUsageService(); } }
    }

    // ── Persistence path ─────────────────────────────────────────────────────
    private static readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TechnicianAssistant",
        "token_usage.json");

    // ── Pricing (configurable via appsettings.json) ───────────────────────────
    private double _inputPricePerMillion  = 3.00;
    private double _outputPricePerMillion = 15.00;

    public double InputPricePerMillion  => _inputPricePerMillion;
    public double OutputPricePerMillion => _outputPricePerMillion;

    /// <summary>
    /// Updates the per-million-token rates used for cost calculations.
    /// Call once at startup after reading appsettings.json.
    /// </summary>
    public void Configure(double inputPricePerMillion, double outputPricePerMillion)
    {
        _inputPricePerMillion  = inputPricePerMillion;
        _outputPricePerMillion = outputPricePerMillion;
        LoggingService.Instance.Log($"[TokenUsage] Pricing configured: ${_inputPricePerMillion:F2}/M input · ${_outputPricePerMillion:F2}/M output");
    }

    // ── Counters ──────────────────────────────────────────────────────────────
    private long _localInputTokens;
    private long _localOutputTokens;
    private long _cloudInputTokens;
    private long _cloudOutputTokens;
    private long _cloudInvocations;
    private string _trackingSince;

    // ── Derived ───────────────────────────────────────────────────────────────
    public long TotalLocalTokens  => _localInputTokens  + _localOutputTokens;
    public long TotalCloudTokens  => _cloudInputTokens  + _cloudOutputTokens;
    public long CloudInvocations  => Interlocked.Read(ref _cloudInvocations);
    public string TrackingSince   => _trackingSince;

    public double LocalEquivalentCostUsd =>
        (_localInputTokens  / 1_000_000.0 * _inputPricePerMillion) +
        (_localOutputTokens / 1_000_000.0 * _outputPricePerMillion);

    public double CloudActualCostUsd =>
        (_cloudInputTokens  / 1_000_000.0 * _inputPricePerMillion) +
        (_cloudOutputTokens / 1_000_000.0 * _outputPricePerMillion);

    // ── Constructor ───────────────────────────────────────────────────────────
    private TokenUsageService()
    {
        _trackingSince = DateTime.Today.ToString("yyyy-MM-dd");
        Load();
    }

    // ── Recording ─────────────────────────────────────────────────────────────

    public void RecordLocalUsage(int inputTokens, int outputTokens)
    {
        Interlocked.Add(ref _localInputTokens,  inputTokens);
        Interlocked.Add(ref _localOutputTokens, outputTokens);
        Save();
    }

    public void RecordCloudUsage(int inputTokens, int outputTokens)
    {
        Interlocked.Add(ref _cloudInputTokens,  inputTokens);
        Interlocked.Add(ref _cloudOutputTokens, outputTokens);
        Interlocked.Increment(ref _cloudInvocations);
        Save();
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _localInputTokens,  0);
        Interlocked.Exchange(ref _localOutputTokens, 0);
        Interlocked.Exchange(ref _cloudInputTokens,  0);
        Interlocked.Exchange(ref _cloudOutputTokens, 0);
        Interlocked.Exchange(ref _cloudInvocations,  0);
        _trackingSince = DateTime.Today.ToString("yyyy-MM-dd");
        Save();
    }

    // ── Display ───────────────────────────────────────────────────────────────

    public string BuildSummary()
    {
        var localCost = LocalEquivalentCostUsd;
        var cloudCost = CloudActualCostUsd;
        var saved     = localCost; // cost avoided by using local model

        return
            $"Tracking since : {_trackingSince}\n\n" +
            $"--- Local Model ----------------------------------------\n" +
            $"  Input tokens   : {_localInputTokens,12:N0}\n" +
            $"  Output tokens  : {_localOutputTokens,12:N0}\n" +
            $"  Total tokens   : {TotalLocalTokens,12:N0}\n" +
            $"  Equiv. cost    : {localCost,12:C4}  (what this would have cost in the cloud)\n\n" +
            $"--- Cloud Model ----------------------------------------\n" +
            $"  Invocations    : {CloudInvocations,12:N0}\n" +
            $"  Input tokens   : {_cloudInputTokens,12:N0}\n" +
            $"  Output tokens  : {_cloudOutputTokens,12:N0}\n" +
            $"  Total tokens   : {TotalCloudTokens,12:N0}\n" +
            $"  Actual cost    : {cloudCost,12:C4}\n\n" +
            $"--- Savings --------------------------------------------\n" +
            $"  Cost avoided   : {saved,12:C4}  (local model handled these for free)\n\n" +
            $"Rates: ${_inputPricePerMillion:F2}/M input · ${_outputPricePerMillion:F2}/M output";
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var data = new PersistedData
            {
                LocalInputTokens  = Interlocked.Read(ref _localInputTokens),
                LocalOutputTokens = Interlocked.Read(ref _localOutputTokens),
                CloudInputTokens  = Interlocked.Read(ref _cloudInputTokens),
                CloudOutputTokens = Interlocked.Read(ref _cloudOutputTokens),
                CloudInvocations  = Interlocked.Read(ref _cloudInvocations),
                TrackingSince     = _trackingSince
            };
            File.WriteAllText(_filePath,
                JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-critical — silently ignore I/O errors */ }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var data = JsonSerializer.Deserialize<PersistedData>(File.ReadAllText(_filePath));
            if (data is null) return;

            Interlocked.Exchange(ref _localInputTokens,  data.LocalInputTokens);
            Interlocked.Exchange(ref _localOutputTokens, data.LocalOutputTokens);
            Interlocked.Exchange(ref _cloudInputTokens,  data.CloudInputTokens);
            Interlocked.Exchange(ref _cloudOutputTokens, data.CloudOutputTokens);
            Interlocked.Exchange(ref _cloudInvocations,  data.CloudInvocations);
            if (!string.IsNullOrWhiteSpace(data.TrackingSince))
                _trackingSince = data.TrackingSince;
        }
        catch { /* corrupt file — start fresh */ }
    }

    private sealed class PersistedData
    {
        public long   LocalInputTokens  { get; set; }
        public long   LocalOutputTokens { get; set; }
        public long   CloudInputTokens  { get; set; }
        public long   CloudOutputTokens { get; set; }
        public long   CloudInvocations  { get; set; }
        public string TrackingSince     { get; set; } = string.Empty;
    }
}


