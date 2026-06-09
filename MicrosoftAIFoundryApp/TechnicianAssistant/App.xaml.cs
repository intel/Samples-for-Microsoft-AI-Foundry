using Microsoft.UI.Xaml;
using System;
using System.Threading.Tasks;
using TechnicianAssistant.Services;

namespace TechnicianAssistant
{
    public partial class App : Application
    {
        public static Window? MainWindow { get; private set; }

        public App()
        {
            InitializeComponent();
            UnhandledException += App_UnhandledException;
            
            // Initialize logging service early to capture all console output
            _ = LoggingService.Instance;
            LoggingService.Instance.Log("═══════════════════════════════════════");
            LoggingService.Instance.Log("🚀 TechnicianAssistant Starting...");
            LoggingService.Instance.Log("═══════════════════════════════════════");
        }

        /// <summary>
        /// Raw arguments string from WinUI launch (e.g. "--model phi-4:2").
        /// Set before ServiceContainer is first accessed.
        /// </summary>
        public static string LaunchArguments { get; private set; } = string.Empty;

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // Capture WinUI launch args before ServiceContainer is constructed.
            // Packaged apps do NOT expose these via Environment.GetCommandLineArgs().
            LaunchArguments = args.Arguments ?? string.Empty;
            LoggingService.Instance.Log($"📋 Launch arguments: \"{LaunchArguments}\"");

            MainWindow = new MainWindow();
            MainWindow.Activate();

            // Initialize services in the background
            _ = InitializeServicesAsync();
        }

        private async Task InitializeServicesAsync()
        {
            try
            {
                LoggingService.Instance.Log("🔧 Initializing services...");
                var services = ServiceContainer.Instance;
                
                // Initialize Foundry Local service
                LoggingService.Instance.Log("   Initializing Foundry Local service...");
                await services.FoundryLocalService.InitializeAsync();
                
                // Initialize LLM service
                LoggingService.Instance.Log("   Initializing LLM service...");
                await services.LlmService.InitializeAsync();
                
                LoggingService.Instance.Log("✅ All services initialized successfully");
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Log($"❌ Service initialization error: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Service initialization error: {ex.Message}");
            }
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            var errorMsg = $"Unhandled exception: {e.Exception.Message}\n{e.Exception.StackTrace}";
            LoggingService.Instance.Log($"❌ {errorMsg}");
            System.Diagnostics.Debug.WriteLine(errorMsg);
            e.Handled = true;
        }
    }
}
