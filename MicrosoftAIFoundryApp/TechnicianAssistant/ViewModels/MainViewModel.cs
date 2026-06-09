using System.Windows.Input;
using TechnicianAssistant.Services;

namespace TechnicianAssistant.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private string _logOutput = string.Empty;

        public EquipmentDetailsViewModel EquipmentDetails { get; }
        public VoiceSupportViewModel VoiceSupport { get; }

        public string LogOutput
        {
            get => _logOutput;
            set => SetProperty(ref _logOutput, value);
        }

        public ICommand ClearLogsCommand { get; }

        public MainViewModel()
        {
            var services = ServiceContainer.Instance;

            EquipmentDetails = new EquipmentDetailsViewModel(services.OcrService, services.LlmService);
            VoiceSupport = new VoiceSupportViewModel(
                services.AudioCaptureService,
                services.TranscriptionService,
                services.LlmService
            );

            // Commands
            ClearLogsCommand = new RelayCommand(ClearLogs);

            // Subscribe to centralized logging
            LoggingService.Instance.LogAdded += OnLogAdded;
            
            // Initialize with existing logs
            LogOutput = LoggingService.Instance.GetFullLog();
        }

        private void ClearLogs()
        {
            LoggingService.Instance.Clear();
            LogOutput = string.Empty;
        }

        private void OnLogAdded(object? sender, string logEntry)
        {
            // Update on UI thread
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() =>
            {
                LogOutput = LoggingService.Instance.GetFullLog();
            });
        }
    }
}
