using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using TechnicianAssistant.Services;
using TechnicianAssistant.Services.Interfaces;
using TechnicianAssistant.Services.Parts;
using TechnicianAssistant.Services.Parts.Tools;

namespace TechnicianAssistant;

public class ServiceContainer
{
    private static ServiceContainer? _instance;
    private static readonly object _lock = new();

    public static ServiceContainer Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new ServiceContainer();
                }
            }
            return _instance;
        }
    }

    private ServiceContainer()
    {
        var (modelId, reasoningModelId, cloudModelId, cloudProvider, awsRegion, azureFoundryEndpoint, azureFoundryApiKey, whisperModelPath, manualsDirectory, embeddingModelDirectory, vectorDatabasePath) = ParseModelArguments();

        ManualsDirectory = manualsDirectory;
        if (!string.IsNullOrEmpty(modelId))
            LoggingService.Instance.Log($"[Config] Model override from config: {modelId}");
        if (!string.IsNullOrEmpty(reasoningModelId))
            LoggingService.Instance.Log($"[Config] Reasoning model from config: {reasoningModelId}");
        if (!string.IsNullOrEmpty(cloudModelId))
            LoggingService.Instance.Log($"[Config] Cloud model from config: {cloudModelId}");
        if (!string.IsNullOrEmpty(whisperModelPath))
            LoggingService.Instance.Log($"[Config] Whisper model path from config: {whisperModelPath}");

        // Initialize services
        FoundryLocalService = new FoundryLocalService(modelId ?? "Phi-4-mini-reasoning-openvino-gpu:2", reasoningModelId ?? "");
        AudioCaptureService = new AudioCaptureService();
        TranscriptionService = string.IsNullOrEmpty(whisperModelPath)
            ? new TranscriptionService()
            : new TranscriptionService(whisperModelPath);
        LlmService = new LlmService(FoundryLocalService, modelId ?? "Phi-4-mini-reasoning-openvino-gpu:2", reasoningModelId ?? "");
        CloudLlmService = CreateCloudLlmService(cloudProvider, cloudModelId, awsRegion, azureFoundryEndpoint, azureFoundryApiKey);
        CloudModelId    = cloudModelId ?? "us.anthropic.claude-3-5-sonnet-20241022-v2:0";

        var (inputPrice, outputPrice) = ParseCloudPricing();
        TokenUsageService.Instance.Configure(inputPrice, outputPrice);

        OcrService = new OcrService();

        // Always start with the mock tools so the agent is immediately usable.
        // If McpServerUrl is configured, the MCP-backed agent replaces it asynchronously
        // once the connection handshake completes.
        var (laborHourlyRate, laborTripCharge, laborCurrency) = ParseLaborRates();
        _partsOrderingAgent = new PartsOrderingAgent(
            LlmService,
            new PartIdentificationTool(LlmService),
            new InventoryCheckTool(),
            new SupplierLookupTool(),
            new PricingTool(),
            new CompatibilityTool(LlmService),
            new WarrantyCheckTool(),
            new LaborCostTool(LlmService, laborHourlyRate, laborTripCharge, laborCurrency));

        var mcpServerUrl = ParseMcpServerUrl();

        if (!string.IsNullOrWhiteSpace(mcpServerUrl))
        {
            LoggingService.Instance.Log($"[Config] MCP server URL configured: {mcpServerUrl} — connecting to live DB-backed tools");
            // Kick off async MCP client creation; tools will be available once the task completes.
            // ServiceContainer is constructed synchronously so we fire-and-forget the connection,
            // replacing PartsOrderingAgent with the MCP-backed version when ready.
            _ = Task.Run(async () =>
            {
                try
                {
                    var mcpClient = await McpToolsClient.CreateAsync(mcpServerUrl);
                    var mcpAgent = new PartsOrderingAgent(
                        LlmService,
                        new McpPartIdentificationTool(mcpClient, LlmService),
                        new McpInventoryCheckTool(mcpClient),
                        new McpSupplierLookupTool(mcpClient),
                        new McpPricingTool(mcpClient),
                        new McpCompatibilityTool(mcpClient),
                        new McpWarrantyCheckTool(mcpClient),
                        new McpLaborCostTool(mcpClient, laborHourlyRate, laborTripCharge, laborCurrency));

                    lock (_agentLock)
                        _partsOrderingAgent = mcpAgent;

                    LoggingService.Instance.Log("[MCP] Parts ordering agent switched to MCP server tools");
                }
                catch (Exception ex)
                {
                    LoggingService.Instance.Log($"[Config] MCP server connection failed: {ex.Message} — using mock tools");
                }
            });
        }
        else
        {
            LoggingService.Instance.Log("[Config] McpServerUrl not configured — using mock parts tools");
        }
        
        // Initialize embedding service if model is available
        try
        {
            var baseDir = embeddingModelDirectory
                ?? Path.Combine(AppContext.BaseDirectory, "all-MiniLM-L6-v2");
            var modelPath = Path.Combine(baseDir, "model.onnx");
            var vocabPath = Path.Combine(baseDir, "vocab.txt");

            if (File.Exists(modelPath))
            {
                EmbeddingService = new EmbeddingService(modelPath, vocabPath);

                var dbPath = vectorDatabasePath
                    ?? Path.Combine(AppContext.BaseDirectory, "manuals.db");
                if (File.Exists(dbPath))
                {
                    VectorDatabaseService = new VectorDatabaseService(EmbeddingService, dbPath);
                    LoggingService.Instance.Log($"[+] Vector database initialized: {dbPath}");
                    
                    // Log database stats
                    _ = Task.Run(async () =>
                    {
                        var chunkCount = await VectorDatabaseService.GetChunkCountAsync();
                        var manuals = await VectorDatabaseService.GetManualNamesAsync();
                        LoggingService.Instance.Log($"[DB] Database contains {chunkCount} chunks from {manuals.Length} manuals");
                        foreach (var manual in manuals)
                        {
                            LoggingService.Instance.Log($"   - {manual}");
                        }
                    });
                }
                else
                {
                    LoggingService.Instance.Log($"[!] Manual database not found: {dbPath}");
                }
            }
            else
            {
                LoggingService.Instance.Log("[!] Embedding model not found - RAG features disabled");
                LoggingService.Instance.Log($"   Expected: {modelPath}");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Log($"[x] Failed to initialize embedding/vector services: {ex.Message}");
        }
    }

    public IFoundryLocalService FoundryLocalService { get; }
    public IAudioCaptureService AudioCaptureService { get; }
    public ITranscriptionService TranscriptionService { get; }
    public ILlmService LlmService { get; }
    public ICloudLlmService CloudLlmService { get; }
    public IOcrService OcrService { get; }
    public IEmbeddingService? EmbeddingService { get; }
    public VectorDatabaseService? VectorDatabaseService { get; }
    public HardRulesEngine HardRulesEngine { get; } = new HardRulesEngine();

    private IPartsOrderingAgent _partsOrderingAgent = null!;
    private readonly object _agentLock = new();
    public IPartsOrderingAgent PartsOrderingAgent
    {
        get { lock (_agentLock) return _partsOrderingAgent; }
    }

    /// <summary>The cloud model ID resolved from configuration.</summary>
    public string CloudModelId { get; private set; }

    /// <summary>Directory containing the PDF manual files, or <see langword="null"/> if not configured.</summary>
    public string? ManualsDirectory { get; private set; }

    /// <summary>
    /// Instantiates the appropriate <see cref="ICloudLlmService"/> based on the
    /// <c>CloudProvider</c> setting. Defaults to AWS Bedrock when not specified.
    /// </summary>
    private static ICloudLlmService CreateCloudLlmService(
        string? cloudProvider,
        string? cloudModelId,
        string? awsRegion,
        string? azureFoundryEndpoint,
        string? azureFoundryApiKey)
    {
        var provider = (cloudProvider ?? "AWS").Trim();
        LoggingService.Instance.Log($"[Config] Cloud provider: {provider}");

        if (provider.Equals("AzureFoundry", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = azureFoundryEndpoint ?? string.Empty;
            var apiKey   = azureFoundryApiKey   ?? string.Empty;
            var model    = cloudModelId         ?? "gpt-4o";
            LoggingService.Instance.Log($"[Config] Azure Foundry endpoint: {endpoint}, model: {model}");
            return new AzureFoundryLlmService(model, endpoint, apiKey);
        }

        // Default: AWS Bedrock (native Converse API)
        return new CloudLlmService(
            cloudModelId ?? "us.anthropic.claude-3-5-sonnet-20241022-v2:0",
            awsRegion    ?? "us-east-1");
    }

    /// <summary>
    /// Resolves both ModelId and JudgeModelId from (in priority order):
    ///   1. appsettings.json  (C:\TechnicianAssistant\TechnicianAssistant\appsettings.json)
    ///   2. WinUI LaunchActivatedEventArgs.Arguments  (--model &lt;name&gt;)
    ///   3. Environment variable  TECHNICIAN_MODEL
    ///   4. Process command line  Environment.GetCommandLineArgs()
    /// Returns (null, null, null, null) if not found in any source.
    /// </summary>
    private static (string? modelId, string? reasoningModelId, string? cloudModelId, string? cloudProvider, string? awsRegion, string? azureFoundryEndpoint, string? azureFoundryApiKey, string? whisperModelPath, string? manualsDirectory, string? embeddingModelDirectory, string? vectorDatabasePath) ParseModelArguments()
    {
        string? modelId = null;
        string? reasoningModelId = null;
        string? cloudModelId = null;
        string? cloudProvider = null;
        string? awsRegion = null;
        string? azureFoundryEndpoint = null;
        string? azureFoundryApiKey = null;
        string? whisperModelPath = null;
        string? manualsDirectory = null;
        string? embeddingModelDirectory = null;
        string? vectorDatabasePath = null;

        // Locate appsettings.json: next to the exe (packaged or unpackaged), falling back to
        // the legacy hardcoded path so existing setups continue to work.
        var configPath = FindConfigPath();
        LoggingService.Instance.Log($"[Config] Looking for appsettings.json at: {configPath}");
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("ModelId", out var modelProp))
                {
                    var id = modelProp.GetString();
                    if (!string.IsNullOrWhiteSpace(id)) { modelId = id; LoggingService.Instance.Log($"[Config] Model resolved from appsettings.json: {modelId}"); }
                }
                if (doc.RootElement.TryGetProperty("ReasoningModelId", out var reasoningProp))
                {
                    var id = reasoningProp.GetString();
                    if (!string.IsNullOrWhiteSpace(id)) { reasoningModelId = id; LoggingService.Instance.Log($"[Config] Reasoning model resolved from appsettings.json: {reasoningModelId}"); }
                }
                if (doc.RootElement.TryGetProperty("CloudModelId", out var cloudProp))
                {
                    var id = cloudProp.GetString();
                    if (!string.IsNullOrWhiteSpace(id)) { cloudModelId = id; LoggingService.Instance.Log($"[Config] Cloud model resolved from appsettings.json: {cloudModelId}"); }
                }
                if (doc.RootElement.TryGetProperty("CloudProvider", out var providerProp))
                {
                    var id = providerProp.GetString();
                    if (!string.IsNullOrWhiteSpace(id)) { cloudProvider = id; LoggingService.Instance.Log($"[Config] Cloud provider resolved from appsettings.json: {cloudProvider}"); }
                }
                if (doc.RootElement.TryGetProperty("AwsRegion", out var regionProp))
                {
                    var id = regionProp.GetString();
                    if (!string.IsNullOrWhiteSpace(id)) { awsRegion = id; LoggingService.Instance.Log($"[Config] AWS region resolved from appsettings.json: {awsRegion}"); }
                }
                if (doc.RootElement.TryGetProperty("AzureFoundryEndpoint", out var afEndpointProp))
                {
                    var id = afEndpointProp.GetString();
                    if (!string.IsNullOrWhiteSpace(id)) { azureFoundryEndpoint = id; LoggingService.Instance.Log($"[Config] Azure Foundry endpoint resolved from appsettings.json: {azureFoundryEndpoint}"); }
                }
                if (doc.RootElement.TryGetProperty("AzureFoundryApiKey", out var afKeyProp))
                {
                    var id = afKeyProp.GetString();
                    if (!string.IsNullOrWhiteSpace(id)) { azureFoundryApiKey = id; }
                }
                if (doc.RootElement.TryGetProperty("WhisperModelPath", out var whisperProp))
                {
                    var raw = whisperProp.GetString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        // Relative paths are resolved next to appsettings.json itself,
                        // so "whisper-large-v3-onnx" becomes
                        // C:\TechnicianAssistant\TechnicianAssistant\whisper-large-v3-onnx
                        whisperModelPath = Path.IsPathRooted(raw)
                            ? raw
                            : Path.GetFullPath(Path.Combine(
                                Path.GetDirectoryName(configPath)!, raw));
                        Console.WriteLine($"[Config] Whisper model path resolved: {whisperModelPath}");
                    }
                }
                if (doc.RootElement.TryGetProperty("ManualsDirectory", out var manualsDirProp))
                {
                    var raw = manualsDirProp.GetString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        manualsDirectory = Path.IsPathRooted(raw)
                            ? raw
                            : Path.GetFullPath(Path.Combine(
                                Path.GetDirectoryName(configPath)!, raw));
                        LoggingService.Instance.Log($"[Config] Manuals directory resolved: {manualsDirectory}");
                    }
                }
                if (doc.RootElement.TryGetProperty("EmbeddingModelDirectory", out var embProp))
                {
                    var raw = embProp.GetString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        embeddingModelDirectory = Path.IsPathRooted(raw)
                            ? raw
                            : Path.GetFullPath(Path.Combine(
                                Path.GetDirectoryName(configPath)!, raw));
                        LoggingService.Instance.Log($"[Config] Embedding model directory resolved: {embeddingModelDirectory}");
                    }
                }
                if (doc.RootElement.TryGetProperty("VectorDatabasePath", out var dbProp))
                {
                    var raw = dbProp.GetString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        vectorDatabasePath = Path.IsPathRooted(raw)
                            ? raw
                            : Path.GetFullPath(Path.Combine(
                                Path.GetDirectoryName(configPath)!, raw));
                        LoggingService.Instance.Log($"[Config] Vector database path resolved: {vectorDatabasePath}");
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Log($"[Config] Failed to read appsettings.json: {ex.Message}");
            }
        }
        else
        {
            LoggingService.Instance.Log("[Config] appsettings.json not found.");
        }

        if (modelId != null)
            return (modelId, reasoningModelId, cloudModelId, cloudProvider, awsRegion, azureFoundryEndpoint, azureFoundryApiKey, whisperModelPath, manualsDirectory, embeddingModelDirectory, vectorDatabasePath);

        // 2. WinUI launch args
        var launchArgs = App.LaunchArguments;
        LoggingService.Instance.Log($"[Config] LaunchActivatedEventArgs.Arguments = \"{launchArgs}\"");
        if (!string.IsNullOrWhiteSpace(launchArgs))
        {
            var parsed = ParseModelFromString(launchArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (parsed != null)
            {
                LoggingService.Instance.Log($"[Config] Model resolved from launch args: {parsed}");
                return (parsed, reasoningModelId, cloudModelId, cloudProvider, awsRegion, azureFoundryEndpoint, azureFoundryApiKey, whisperModelPath, manualsDirectory, embeddingModelDirectory, vectorDatabasePath);
            }
        }

        // 3. Environment variable
        var envModel = Environment.GetEnvironmentVariable("TECHNICIAN_MODEL");
        LoggingService.Instance.Log($"[Config] TECHNICIAN_MODEL env var = \"{envModel ?? "(not set)"}\"");
        if (!string.IsNullOrWhiteSpace(envModel))
        {
            LoggingService.Instance.Log($"[Config] Model resolved from environment variable: {envModel}");
            return (envModel, reasoningModelId, cloudModelId, cloudProvider, awsRegion, azureFoundryEndpoint, azureFoundryApiKey, whisperModelPath, manualsDirectory, embeddingModelDirectory, vectorDatabasePath);
        }

        // 4. Process command line (works for unpackaged / direct exe launch)
        var clArgs = Environment.GetCommandLineArgs();
        LoggingService.Instance.Log($"[Config] Environment.GetCommandLineArgs() = [{string.Join(", ", clArgs)}]");
        var clParsed = ParseModelFromString(clArgs);
        if (clParsed != null)
        {
            LoggingService.Instance.Log($"[Config] Model resolved from process command line: {clParsed}");
            return (clParsed, reasoningModelId, cloudModelId, cloudProvider, awsRegion, azureFoundryEndpoint, azureFoundryApiKey, whisperModelPath, manualsDirectory, embeddingModelDirectory, vectorDatabasePath);
        }

        LoggingService.Instance.Log("[Config] No model found in any source; using default.");
        return (null, reasoningModelId, cloudModelId, cloudProvider, awsRegion, azureFoundryEndpoint, azureFoundryApiKey, whisperModelPath, manualsDirectory, embeddingModelDirectory, vectorDatabasePath);
    }

    private static string? ParseModelFromString(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--model", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    /// <summary>
    /// Locates appsettings.json by checking (in order):
    ///   1. Next to the running executable (<see cref="AppContext.BaseDirectory"/>)
    ///   2. Legacy absolute path used during initial development
    /// </summary>
    private static string FindConfigPath()
    {
        var nextToExe = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(nextToExe)) return nextToExe;

        // Legacy fallback — keeps existing dev machines working without any changes
        return @"C:\TechnicianAssistant\TechnicianAssistant\appsettings.json";
    }

    private static (decimal hourlyRate, decimal tripCharge, string currency) ParseLaborRates()
    {
        decimal hourlyRate = 125m;
        decimal tripCharge = 85m;
        string  currency   = "USD";

        var configPath = FindConfigPath();
        if (!File.Exists(configPath)) return (hourlyRate, tripCharge, currency);

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.TryGetProperty("LaborRates", out var lr))
            {
                if (lr.TryGetProperty("HourlyRate", out var hr) && hr.TryGetDecimal(out var hrv)) hourlyRate = hrv;
                if (lr.TryGetProperty("TripCharge", out var tc) && tc.TryGetDecimal(out var tcv)) tripCharge = tcv;
                if (lr.TryGetProperty("Currency",   out var cu) && cu.GetString() is { } cv)      currency   = cv;
            }
        }
        catch { /* use defaults */ }

        return (hourlyRate, tripCharge, currency);
    }

    private static (double inputPrice, double outputPrice) ParseCloudPricing()
    {
        double inputPrice  = 3.00;
        double outputPrice = 15.00;

        var configPath = FindConfigPath();
        if (!File.Exists(configPath)) return (inputPrice, outputPrice);

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.TryGetProperty("CloudPricing", out var cp))
            {
                if (cp.TryGetProperty("InputPricePerMillionTokens",  out var ip) && ip.TryGetDouble(out var ipv)) inputPrice  = ipv;
                if (cp.TryGetProperty("OutputPricePerMillionTokens", out var op) && op.TryGetDouble(out var opv)) outputPrice = opv;
            }
        }
        catch { /* use defaults */ }

        return (inputPrice, outputPrice);
    }

    private static string? ParseMcpServerUrl()
    {
        var configPath = FindConfigPath();
        if (!File.Exists(configPath)) return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.TryGetProperty("McpServerUrl", out var prop))
                return prop.GetString();
        }
        catch { /* ignore */ }

        return null;
    }
}
