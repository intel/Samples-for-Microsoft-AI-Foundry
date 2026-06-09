using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using TechnicianAssistant.Services.Interfaces;

namespace TechnicianAssistant.Services;

public class FoundryLocalService : IFoundryLocalService
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private FoundryLocalManager? _foundryManager;
    private IModel? _model;
    private IModel? _modelVariant;
    private string? _endpoint;
    private bool _isInitialized;
    private bool _isModelsReady;
    private Action<string>? _logger;

    public bool IsModelsReady => _isModelsReady;
    public event EventHandler? ModelsReady;
    private ILoggerFactory? _loggerFactory;
    private readonly string _modelId;
    private readonly string _reasoningModelId;
    private IModel? _reasoningModelVariant;

    public FoundryLocalService(string modelId = "Phi-4-mini-reasoning-openvino-gpu:2", string reasoningModelId = "")
    {
        _modelId = modelId;
        _reasoningModelId = reasoningModelId;
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

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        await _initLock.WaitAsync();
        try
        {
            // Double-check inside the lock — another caller may have finished while we waited.
            if (_isInitialized) return;

            try
            {
                Log("🚀 Initializing Foundry Local...");

                var config = new Configuration
                {
                    AppName = "Technician Assitant App",
                    LogLevel = Microsoft.AI.Foundry.Local.LogLevel.Debug,
                    Web = new Configuration.WebService
                    {
                        Urls = "http://127.0.0.1:5000"
                    }
                };

                _loggerFactory = LoggerFactory.Create(builder =>
                {
                    builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug);
                });

                var logger = _loggerFactory.CreateLogger("Technician Assitant");

                // CreateAsync throws if the SDK singleton already exists (e.g. a second hot-reload
                // cycle or concurrent caller). Fall back to the existing instance in that case.
                try
                {
                    await FoundryLocalManager.CreateAsync(config, logger);
                }
                catch (Exception ex) when (ex.Message.Contains("already been created") ||
                                           ex.Message.Contains("already created"))
                {
                    Log("ℹ️ FoundryLocalManager already exists — reusing existing instance");
                }

                _foundryManager = FoundryLocalManager.Instance;
                _endpoint = "http://127.0.0.1:5000";
                await LoadModelAsync(_modelId);

                if (!string.IsNullOrWhiteSpace(_reasoningModelId) &&
                    !string.Equals(_reasoningModelId, _modelId, StringComparison.OrdinalIgnoreCase))
                {
                    Log($"🧠 Loading reasoning model: {_reasoningModelId}...");
                    var reasoningId = await LoadModelAsync(_reasoningModelId);
                    if (_foundryManager != null && !string.IsNullOrEmpty(reasoningId))
                    {
                        var catalog = await _foundryManager.GetCatalogAsync();
                        _reasoningModelVariant = await catalog.GetModelVariantAsync(_reasoningModelId);
                    }
                }

                _isInitialized = true;
                _isModelsReady = true;
                Log($"✅ Foundry Local initialized successfully");
                Log($"📡 Endpoint: {_endpoint}");
                ModelsReady?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log($"⚠️ Error during Foundry Local initialization: {ex.Message}");
                Log("Using fallback endpoint: http://localhost:8080");
                _endpoint = "http://localhost:8080";
                _isInitialized = true;
                _isModelsReady = true;
                ModelsReady?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<string> GetEndpointAsync()
    {
        if (!_isInitialized)
        {
            await InitializeAsync();
        }

        return _endpoint ?? "http://127.0.0.1:5000";
    }

    public string Endpoint => _endpoint ?? "http://127.0.0.1:5000";

    public FoundryLocalManager? Manager => _foundryManager;

    public async Task<string> LoadModelAsync(string modelName)
    {
        try
        {
            Log($"📦 Loading model: {modelName}...");

            if (_foundryManager == null)
            {
                Log("⚠️ Foundry Manager not initialized. Call InitializeAsync first.");
                return string.Empty;
            }
            // Discover available execution providers and their registration status.
            var eps = _foundryManager.DiscoverEps();
            int maxNameLen = 30;
            LoggingService.Instance.Log("Available execution providers:");
            LoggingService.Instance.Log($"  {"Name".PadRight(maxNameLen)}  Registered");
            LoggingService.Instance.Log($"  {new string('─', maxNameLen)}  {"──────────"}");
            foreach (var ep in eps)
            {
                LoggingService.Instance.Log($"  {ep.Name.PadRight(maxNameLen)}  {ep.IsRegistered}");
            }

            // Download and register all execution providers with per-EP progress.
            // EP packages include dependencies and may be large.
            // Download is only required again if a new version of the EP is released.
            // For cross platform builds there is no dynamic EP download and this will return immediately.
            LoggingService.Instance.Log("\nDownloading execution providers:");
            if (eps.Length > 0)
            {
                string currentEp = "";
                await _foundryManager.DownloadAndRegisterEpsAsync((epName, percent) =>
                {
                    if (epName != currentEp)
                    {
                        if (currentEp != "")
                        {
                            LoggingService.Instance.Log(string.Empty);
                        }
                        currentEp = epName;
                    }
                    LoggingService.Instance.Log($"  {epName.PadRight(maxNameLen)}  {percent,6:F1}%");
                });
                LoggingService.Instance.Log(string.Empty);
            }
            else
            {
                LoggingService.Instance.Log("No execution providers to download.");
            }
            // Get the model catalog
            var catalog = await _foundryManager.GetCatalogAsync();

            // List available models
            //
            var models = await catalog.ListModelsAsync();
            //foreach (var availableModel in models)
            //{
            //    foreach (var variant in availableModel.Variants)
            //    {
            //        Log($"  - Alias:::: {variant.Alias} (Id: {string.Join(", ", variant.Id)})");
            //    }
            //}
            _modelVariant = await catalog.GetModelVariantAsync(modelName);

            // Download the model (skips if already cached)
            await _modelVariant.DownloadAsync(progress =>
            {
                if (progress % 10 == 0 || progress >= 100f)
                {
                    Log($"📥 Downloading model: {progress:F2}%");
                }
            });

            // Load the model
            await _modelVariant.LoadAsync();
            Log($"✅ Model '{_modelVariant.Id}' loaded successfully");
            await _foundryManager.StartWebServiceAsync();
            Log($"✅ Started web server successfully");
            Log($"✅ Model variant '{_modelVariant.Id}' loaded successfully");
            return _modelVariant.Id;
        }
        catch (Exception ex)
        {
            Log($"❌ Error loading model: {ex.Message}");
            throw;
        }
    }

    public async Task ShutdownAsync()
    {
        if (_foundryManager != null)
        {
            try
            {
                Log("🛑 Shutting down Foundry Local...");

                // Unload model if loaded
                if (_model != null)
                {
                    await _model.UnloadAsync();
                    Log("✅ Model unloaded");
                }

                // Unload reasoning model if loaded
                if (_reasoningModelVariant != null)
                {
                    await _reasoningModelVariant.UnloadAsync();
                    Log("✅ Reasoning model unloaded");
                }

                // Stop the web service
                await _foundryManager.StopWebServiceAsync();
                Log("✅ Foundry Local shutdown complete");
            }
            catch (Exception ex)
            {
                Log($"⚠️ Error during shutdown: {ex.Message}");
            }
        }

        _loggerFactory?.Dispose();
        await Task.CompletedTask;
    }
}
