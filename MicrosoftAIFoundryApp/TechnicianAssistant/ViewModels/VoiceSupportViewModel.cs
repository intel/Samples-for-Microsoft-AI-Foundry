using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using TechnicianAssistant.Services;
using TechnicianAssistant.Services.Interfaces;
using TechnicianAssistant.Services.Parts;

namespace TechnicianAssistant.ViewModels
{
    public class VoiceSupportViewModel : ViewModelBase
    {
        private readonly IAudioCaptureService _audioService;
        private readonly ITranscriptionService _transcriptionService;
        private readonly ILlmService _llmService;
        private readonly ICloudLlmService _cloudLlmService;
        private readonly VectorDatabaseService? _vectorDbService;
        private readonly HardRulesEngine _hardRulesEngine;
        private CancellationTokenSource? _recordingCts;
        private string _lastQuery = string.Empty;
        private string _lastRagContext = string.Empty;
        private string _lastContextInfo = string.Empty;
        private bool _isVoiceTurn = false;

        private bool _isRecording;
        private string _liveTranscription = string.Empty;
        private string _assistantResponse = string.Empty;
        private string _conversationHistory = string.Empty;
        // Structured history passed as context to the LLM on each request.
        private readonly List<ConversationTurn> _conversationTurns = [];
        private double _microphoneOpacity = 0.3;
        private double _backgroundOpacity = 0.0;
        private double _audioLevel = 0.0;
        private string _audioLevelText = "Audio level: --";
        private bool _isProcessing = false;
        private bool _hasLocalResponse = false;
        private string _logOutput = "System logs will appear here...\n";
        private string _textInput = string.Empty;
        private string _equipmentContextSummary = string.Empty;
        private bool _isModelsReady = false;
        private bool _separatorPrinted;
        private string _streamingPrefix = string.Empty;
        private string _thinkingBasePrefix = string.Empty;
        private readonly List<PromptAttachment> _attachments = [];
        private string _keySnippet = string.Empty;
        private string? _currentPdfPath = null;
        private int _currentPdfPage = 1;

        public string EquipmentContextSummary
        {
            get => _equipmentContextSummary;
            set
            {
                if (SetProperty(ref _equipmentContextSummary, value))
                    OnPropertyChanged(nameof(HasEquipmentContext));
            }
        }

        public bool HasEquipmentContext => !string.IsNullOrEmpty(_equipmentContextSummary);

        /// <summary>The verbatim snippet extracted from the manual for the current turn.</summary>
        public string KeySnippet
        {
            get => _keySnippet;
            private set
            {
                if (SetProperty(ref _keySnippet, value))
                    OnPropertyChanged(nameof(HasKeySnippet));
            }
        }

        /// <summary><see langword="true"/> when a manual snippet is available to highlight.</summary>
        public bool HasKeySnippet => !string.IsNullOrWhiteSpace(_keySnippet);

        /// <summary>Full path to the PDF manual file for the current turn, or <see langword="null"/>.</summary>
        public string? CurrentPdfPath
        {
            get => _currentPdfPath;
            private set
            {
                if (SetProperty(ref _currentPdfPath, value))
                    OnPropertyChanged(nameof(HasPdfSource));
            }
        }

        /// <summary>1-based page number to open in the PDF viewer.</summary>
        public int CurrentPdfPage
        {
            get => _currentPdfPage;
            private set
            {
                _currentPdfPage = value;
                // Always raise the event unconditionally so the PDF viewer refreshes even
                // when two consecutive queries resolve to the same page number.
                OnPropertyChanged(nameof(CurrentPdfPage));
            }
        }

        /// <summary><see langword="true"/> when a PDF file is available to display.</summary>
        public bool HasPdfSource => !string.IsNullOrEmpty(_currentPdfPath) && System.IO.File.Exists(_currentPdfPath);

        /// <summary>Opens the current PDF at the correct page in the default system viewer.</summary>
        public ICommand OpenPdfCommand { get; private set; } = new RelayCommand(() => { });

        public bool HasAttachments => _attachments.Count > 0;

        /// <summary>Display labels for all currently attached files joined for the UI strip.</summary>
        public string AttachmentSummary
            => string.Join("  �  ", _attachments.Select(a => a.DisplayLabel));

        public bool IsRecording
        {
            get => _isRecording;
            set
            {
                if (SetProperty(ref _isRecording, value))
                {
                    OnPropertyChanged(nameof(RecordingButtonText));
                    // Update microphone visual state when recording changes
                    MicrophoneOpacity = value ? 1.0 : 0.3;
                    BackgroundOpacity = value ? 0.2 : 0.0;
                    
                    if (!value)
                    {
                        // Reset audio level when stopping
                        AudioLevel = 0.0;
                        AudioLevelText = "Audio level: --";
                    }
                }
            }
        }

        public string RecordingButtonText => IsRecording ? "Stop Recording" : "Start Recording";

        public double MicrophoneOpacity
        {
            get => _microphoneOpacity;
            set => SetProperty(ref _microphoneOpacity, value);
        }

        public double BackgroundOpacity
        {
            get => _backgroundOpacity;
            set => SetProperty(ref _backgroundOpacity, value);
        }

        public double AudioLevel
        {
            get => _audioLevel;
            set => SetProperty(ref _audioLevel, value);
        }

        public string AudioLevelText
        {
            get => _audioLevelText;
            set => SetProperty(ref _audioLevelText, value);
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            set
            {
                if (SetProperty(ref _isProcessing, value))
                {
                    OnPropertyChanged(nameof(CanSubmitText));
                    RaiseSubmitCanExecuteChanged();
                    (AskCloudCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (ClearConversationCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (AttachFileCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>
        /// True once the local model has produced a response for the current turn.
        /// Controls visibility of the "Ask Cloud" button.
        /// </summary>
        public bool HasLocalResponse
        {
            get => _hasLocalResponse;
            private set
            {
                if (SetProperty(ref _hasLocalResponse, value))
                    (AskCloudCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public string LiveTranscription
        {
            get => _liveTranscription;
            set => SetProperty(ref _liveTranscription, value);
        }

        public string AssistantResponse
        {
            get => _assistantResponse;
            set => SetProperty(ref _assistantResponse, value);
        }

        // Accumulated chat transcript shown in the conversation area.
        public string ConversationHistory
        {
            get => _conversationHistory;
            set => SetProperty(ref _conversationHistory, value);
        }

        /// <summary>
        /// Prepares the conversation area for streaming by appending the turn header and a
        /// typing-cursor placeholder. Returns the answer prefix string.
        /// </summary>
        private string BeginStreamingTurn(string question)
        {
            var separator = string.IsNullOrEmpty(_conversationHistory) ? string.Empty : "\n\n---------------------\n\n";
            _thinkingBasePrefix = _conversationHistory + $"{separator}You:  {question}\n\n";
            _streamingPrefix = _thinkingBasePrefix + "Assistant:  "; // updated later if thinking occurs
            ConversationHistory = _thinkingBasePrefix + "..."; // typing indicator
            return _streamingPrefix;
        }

        private void FinalizeStreamingTurn(string question, string answer, string sourceInfo)
        {
            _conversationTurns.Add(new ConversationTurn(question, answer));
            ConversationHistory = _streamingPrefix + answer + sourceInfo;
        }

        public string LogOutput
        {
            get => _logOutput;
            set => SetProperty(ref _logOutput, value);
        }

        public string TextInput
        {
            get => _textInput;
            set
            {
                if (SetProperty(ref _textInput, value))
                {
                    OnPropertyChanged(nameof(CanSubmitText));
                    RaiseSubmitCanExecuteChanged();
                }
            }
        }

        public bool IsModelsReady
        {
            get => _isModelsReady;
            private set
            {
                if (SetProperty(ref _isModelsReady, value))
                {
                    OnPropertyChanged(nameof(CanSubmitText));
                    RaiseSubmitCanExecuteChanged();
                }
            }
        }

        public bool CanSubmitText => !string.IsNullOrWhiteSpace(_textInput) && !IsProcessing && _isModelsReady;

        public ICommand ToggleRecordingCommand { get; }
        public ICommand SubmitTextCommand { get; }
        public ICommand AskCloudCommand { get; }
        public ICommand AttachFileCommand { get; }
        public ICommand ClearAttachmentsCommand { get; }
        public ICommand ClearConversationCommand { get; }

        /// <summary>
        /// Raised when the local model's confidence is below the threshold and the UI
        /// should ask the user whether to escalate to the cloud model.
        /// The handler must complete <see cref="EscalationRequestedEventArgs.Decision"/>.
        /// </summary>
        public event EventHandler<EscalationRequestedEventArgs>? EscalationRequested;

        /// <summary>
        /// Raised when the user manually clicks "Ask Cloud" so the UI can show a
        /// cost-confirmation dialog before the API call is made.
        /// The handler must complete <see cref="CloudConfirmationEventArgs.Decision"/>.
        /// </summary>
        public event EventHandler<CloudConfirmationEventArgs>? CloudConfirmationRequested;

        /// <summary>
        /// Raised when a cloud call fails due to missing or invalid AWS credentials.
        /// The UI should show an error dialog with setup instructions.
        /// </summary>
        public event EventHandler<string>? CloudAuthenticationFailed;

        private void RaiseSubmitCanExecuteChanged() =>
            (SubmitTextCommand as RelayCommand)?.RaiseCanExecuteChanged();

        /// <summary>
        /// Returns the first <paramref name="count"/> sentences from <paramref name="text"/>,
        /// joined as a single string. Used to produce a concise log preview of RAG results.
        /// </summary>
        private static string GetFirstSentences(string text, int count)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            // Split on sentence-ending punctuation followed by whitespace or end-of-string.
            var sentences = System.Text.RegularExpressions.Regex
                .Split(text.Trim(), @"(?<=[.!?])\s+")
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Take(count)
                .ToList();

            return string.Join(" ", sentences);
        }

        /// <summary>
        /// Tries to locate the PDF file for <paramref name="manualName"/> inside the
        /// configured manuals directory. The manual name stored in the database is the
        /// bare filename without extension (e.g. "APX-36 Service Manual"), so we append
        /// ".pdf" and check that path first, then fall back to a case-insensitive scan.
        /// Returns <see langword="null"/> when the directory is not configured or the file
        /// is not found.
        /// </summary>
        private static string? ResolvePdfPath(string manualName)
        {
            var dir = ServiceContainer.Instance.ManualsDirectory;
            if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
                return null;

            // The DB may store the name with or without the .pdf extension — normalise to stem.
            var stem = manualName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                ? manualName[..^4]
                : manualName;

            // Fast path — stem + .pdf
            var candidate = System.IO.Path.Combine(dir, stem + ".pdf");
            if (System.IO.File.Exists(candidate))
                return candidate;

            // Slow path — case-insensitive scan
            foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*.pdf"))
            {
                var fileStem = System.IO.Path.GetFileNameWithoutExtension(file);
                if (fileStem.Equals(stem, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
            return null;
        }

        /// <summary>
        /// Updates <see cref="CurrentPdfPath"/> and <see cref="CurrentPdfPage"/> from the
        /// best RAG result so the PDF viewer panel stays in sync with the answer.
        /// Must be called on the UI thread.
        /// </summary>
        private void SetPdfSource(string manualName, int pageNumber)
        {
            var path = ResolvePdfPath(manualName);
            CurrentPdfPath  = path;
            CurrentPdfPage  = pageNumber;
            (OpenPdfCommand as RelayCommand)?.RaiseCanExecuteChanged();
            if (path != null)
                LoggingService.Instance.Log($"[PDF] Located: {System.IO.Path.GetFileName(path)}  p.{pageNumber}");
            else
                LoggingService.Instance.Log($"[PDF] File not found for manual \"{manualName}\" in manuals directory");
        }
        /// Returns an empty string when no equipment has been identified.
        /// </summary>
        private static string BuildEquipmentContext()
        {
            var eq = EquipmentStateService.Instance;
            if (!eq.HasEquipmentInfo) return string.Empty;

            var model  = string.IsNullOrWhiteSpace(eq.ModelNumber)  ? "unknown" : eq.ModelNumber;
            var serial = string.IsNullOrWhiteSpace(eq.SerialNumber) ? "unknown" : eq.SerialNumber;

            return
                $"Equipment context:\n" +
                $"  Model Number:  {model}\n" +
                $"  Serial Number: {serial}\n" +
                $"The technician is working on this specific unit. Use these details when providing guidance.\n\n";
        }

        public VoiceSupportViewModel(
            IAudioCaptureService audioService,
            ITranscriptionService transcriptionService,
            ILlmService llmService)
        {
            _audioService = audioService;
            _transcriptionService = transcriptionService;
            _llmService = llmService;
            _cloudLlmService = ServiceContainer.Instance.CloudLlmService;
            _vectorDbService = ServiceContainer.Instance.VectorDatabaseService;
            _hardRulesEngine = ServiceContainer.Instance.HardRulesEngine;

            // Subscribe to audio level changes
            _audioService.LevelChanged += OnAudioLevelChanged;

            // Subscribe to logging service
            LoggingService.Instance.LogAdded += OnLogAdded;
            
            // Get initial logs
            LogOutput = LoggingService.Instance.GetFullLog();
            if (string.IsNullOrEmpty(LogOutput))
            {
                LogOutput = "System logs will appear here...\n";
            }

            // Reflect current model-ready state (may already be ready if services initialized before this VM)
            var foundry = ServiceContainer.Instance.FoundryLocalService;
            _isModelsReady = foundry.IsModelsReady;
            foundry.ModelsReady += (_, _) =>
            {
                App.MainWindow?.DispatcherQueue.TryEnqueue(() => IsModelsReady = true);
            };

            ToggleRecordingCommand = new RelayCommand(async () => await ToggleRecordingAsync());
            SubmitTextCommand = new RelayCommand(async () => await SubmitTextAsync(), () => CanSubmitText);
            AskCloudCommand = new RelayCommand(async () => await AskCloudAsync(), () => HasLocalResponse && !IsProcessing);
            AttachFileCommand = new RelayCommand(async () => await AttachFileAsync(), () => !IsProcessing);
            OpenPdfCommand = new RelayCommand(() =>
            {
                if (!HasPdfSource) return;
                try
                {
                    // Open the PDF at the target page using the shell default viewer.
                    // The #page=N fragment is honoured by Adobe Reader and most PDF viewers.
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName        = $"{_currentPdfPath}",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    LoggingService.Instance.Log($"[!] Could not open PDF: {ex.Message}");
                }
            });
            ClearAttachmentsCommand = new RelayCommand(() =>
            {
                _attachments.Clear();
                OnPropertyChanged(nameof(HasAttachments));
                OnPropertyChanged(nameof(AttachmentSummary));
            });
            ClearConversationCommand = new RelayCommand(() =>
            {
                _conversationTurns.Clear();
                _lastQuery = string.Empty;
                _lastRagContext = string.Empty;
                _lastContextInfo = string.Empty;
                _streamingPrefix = string.Empty;
                _thinkingBasePrefix = string.Empty;
                KeySnippet = string.Empty;
                HasLocalResponse = false;
                ConversationHistory = string.Empty;
                LoggingService.Instance.Log("[*] Conversation cleared");
            }, () => !IsProcessing);

            // Subscribe to central equipment state so the banner stays current.
            EquipmentStateService.Instance.StateChanged += OnEquipmentStateChanged;
            RefreshEquipmentContext();
        }

        private void OnEquipmentStateChanged(object? sender, EventArgs e)
        {
            App.MainWindow?.DispatcherQueue.TryEnqueue(RefreshEquipmentContext);
        }

        private void RefreshEquipmentContext()
        {
            EquipmentContextSummary = EquipmentStateService.Instance.HasEquipmentInfo
                ? EquipmentStateService.Instance.Summary
                : string.Empty;
        }

        private void OnLogAdded(object? sender, string logEntry)
        {
            // Update UI with new log entry
            App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                // Append new log line
                LogOutput += logEntry + "\n";
                
                // Keep last 500 lines to prevent memory issues
                var lines = LogOutput.Split('\n');
                if (lines.Length > 500)
                {
                    LogOutput = string.Join("\n", lines.Skip(lines.Length - 500));
                }
            });
        }

        private void OnAudioLevelChanged(object? sender, float level)
        {
            if (!IsRecording) return;

            // Update audio level indicator in real-time
            // This proves audio is being captured!
            App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                AudioLevel = level;
                AudioLevelText = $"Audio level: {level:P0}";
                
                // Make microphone pulse with audio level
                // Base opacity 0.6, increases to 1.0 with sound
                var dynamicOpacity = 0.6 + (level * 0.4);
                MicrophoneOpacity = Math.Min(1.0, dynamicOpacity);
                
                // Make background glow pulse with audio
                var dynamicGlow = 0.1 + (level * 0.3);
                BackgroundOpacity = Math.Min(0.4, dynamicGlow);
            });
        }

        private async Task ToggleRecordingAsync()
        {
            if (IsRecording)
            {
                await StopRecordingAsync();
            }
            else
            {
                await StartRecordingAsync();
            }
        }

        private async Task StartRecordingAsync()
        {
            try
            {
                IsRecording = true;
                TextInput = string.Empty;
                AssistantResponse = string.Empty;

                _recordingCts = new CancellationTokenSource();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var audioSamples = await _audioService.RecordAsync(_recordingCts.Token);

                        App.MainWindow!.DispatcherQueue.TryEnqueue(async () =>
                        {
                            await ProcessRecordingAsync(audioSamples);
                        });
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        App.MainWindow!.DispatcherQueue.TryEnqueue(() =>
                        {
                            AssistantResponse = $"Recording error: {ex.Message}";
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                AssistantResponse = $"Error starting recording: {ex.Message}";
                IsRecording = false;
            }
        }

        private async Task StopRecordingAsync()
        {
            try
            {
                IsRecording = false;
                await Task.Yield(); // yield so the UI renders the state change before the blocking driver call

                var cts = _recordingCts;
                _recordingCts = null;
                await Task.Run(() => _audioService.StopRecording()); // stop audio driver off the UI thread
                cts?.Cancel();
            }
            catch (Exception ex)
            {
                AssistantResponse = $"Error stopping recording: {ex.Message}";
            }
        }

        private async Task SubmitTextAsync()
        {
            var query = TextInput.Trim();
            if (string.IsNullOrWhiteSpace(query)) return;

            LiveTranscription = query;
            LoggingService.Instance.Log("========================================");
            LoggingService.Instance.Log("=======================================");
            LoggingService.Instance.Log("|  NEW QUERY                          |");
            LoggingService.Instance.Log("=======================================");
            LoggingService.Instance.Log($"[>] Text query: \"{query}\"");
            _separatorPrinted = true;
            OnPropertyChanged(nameof(CanSubmitText));

            _isVoiceTurn = false;
            await QueryAssistantAsync(query);
        }

        private async Task ProcessRecordingAsync(float[] audioSamples)
        {
            try
            {
                IsProcessing = true;

                LoggingService.Instance.Log("========================================");
                LoggingService.Instance.Log("=======================================");
                LoggingService.Instance.Log("|  NEW QUERY (VOICE)                  |");
                LoggingService.Instance.Log("=======================================");
                LoggingService.Instance.Log("=======================================");
                LoggingService.Instance.Log("|  STEP 1 � SPEECH TRANSCRIPTION      ?");
                LoggingService.Instance.Log("=======================================");
                AssistantResponse = "Step 1 � Transcribing audio...";
                _separatorPrinted = true;

                string finalTranscription = string.Empty;

                var transcribeSw = System.Diagnostics.Stopwatch.StartNew();
                await Task.Run(async () =>
                {
                    await _transcriptionService.TranscribeStreamingAsync(
                        audioSamples,
                        partialText =>
                        {
                            finalTranscription = partialText;
                            App.MainWindow!.DispatcherQueue.TryEnqueue(() =>
                                TextInput = partialText);
                        }
                    );
                });

                if (string.IsNullOrEmpty(finalTranscription))
                {
                    finalTranscription = await Task.Run(() =>
                        _transcriptionService.TranscribeAsync(audioSamples));
                }
                transcribeSw.Stop();

                if (!string.IsNullOrEmpty(finalTranscription))
                    TextInput = finalTranscription;

                if (string.IsNullOrWhiteSpace(finalTranscription) ||
                    finalTranscription.Contains("No speech detected") ||
                    finalTranscription.Contains("Whisper model not configured"))
                {
                    LoggingService.Instance.Log("[!] Transcription: no speech detected or model unavailable.");
                    AssistantResponse = "No speech detected or transcription unavailable.\n\n" +
                                        "Please ensure:\n" +
                                        "- You spoke clearly into the microphone\n" +
                                        "- Microphone volume is adequate\n" +
                                        "- Whisper model is properly configured";
                    TextInput = string.Empty;
                    IsProcessing = false;
                    return;
                }

                LoggingService.Instance.Log($"[+] Transcription complete ({transcribeSw.Elapsed.TotalSeconds:F2}s): \"{finalTranscription}\"");

                _isVoiceTurn = true;
                await QueryAssistantAsync(finalTranscription);
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Log($"[x] Transcription error: {ex.Message}");
                AssistantResponse = $"Processing error: {ex.Message}\n\nStack trace:\n{ex.StackTrace}";
                IsProcessing = false;
            }
        }

        private async Task QueryAssistantAsync(string query)
        {
            try
            {
                IsProcessing = true;
                HasLocalResponse = false;
                _lastQuery = query;
                KeySnippet = string.Empty;
                // Only print the separator when this is the entry point (text submit);
                // voice path already printed it in ProcessRecordingAsync.
                if (!_separatorPrinted)
                    LoggingService.Instance.Log("========================================");
                _separatorPrinted = false;

                // ?? Step 1: Routing decision ?????????????????????????????????????????
                var stepBase = 1;
                LoggingService.Instance.Log($"=======================================");
                LoggingService.Instance.Log($"|  STEP {stepBase} � ROUTING DECISION             ?");
                LoggingService.Instance.Log($"=======================================");
                LoggingService.Instance.Log($"[*] Deciding response strategy...");
                AssistantResponse = $"Step {stepBase} � Deciding response strategy...";

                // ?? Hard rules engine ?????????????????????????????????????????????????
                var hardCtx = new ConversationContext
                {
                    IsOffline          = false,
                    HasImageAttachment = _attachments.Count > 0,
                    HasAudioRecording  = _isVoiceTurn,
                    HasVoiceTranscription = !string.IsNullOrEmpty(LiveTranscription),
                    TroubleshootingSteps = _conversationTurns
                        .Select(t => t.Question)
                        .ToList()
                };

                LoggingService.Instance.Log($"[*] TroubleshootingSteps.Count = {hardCtx.TroubleshootingSteps.Count}");

                var hardDecision = _hardRulesEngine.ApplyHardRules(query, hardCtx);
                LoggingService.Instance.Log($"?? Hard rules: {(hardDecision.IsDefinitive ? $"DEFINITIVE ? {hardDecision.Target}" : "not definitive")} — {hardDecision.Reason}");

                // If the hard rules say cloud, escalate immediately without going local.
                if (hardDecision.IsDefinitive && hardDecision.Target == RoutingTarget.CloudAdvanced)
                {
                    LoggingService.Instance.Log($"[!] Hard rule escalation ({hardDecision.Priority}): routing directly to cloud");
                    IsProcessing = false;
                    TextInput = string.Empty;
                    await AskCloudDirectAsync(query, reason: hardDecision.Reason);
                    return;
                }

                // ?? Parts ordering agent gate ???????????????????????????????????????????
                if (IsExplicitPartsRequest(query))
                {
                    LoggingService.Instance.Log("=======================================\n|  PARTS ORDERING AGENT               |\n=======================================" );
                    LoggingService.Instance.Log("[*] Parts request detected — delegating to PartsOrderingAgent");
                    AssistantResponse = "Researching parts…";

                    var partsEqState = EquipmentStateService.Instance;
                    var equipment = new global::TechnicianAssistant.Services.EquipmentInfo
                    {
                        ModelNumber  = partsEqState.ModelNumber,
                        SerialNumber = partsEqState.SerialNumber
                    };

                    var partsContext = new PartsConversationContext
                    {
                        IsHotWeather  = DateTime.Now.Month is >= 6 and <= 8,
                        IsSafetyIssue = hardDecision.Priority == Priority.Emergency
                    };

                    BeginStreamingTurn(query);
                    var plan = await ServiceContainer.Instance.PartsOrderingAgent
                        .CreateOrderPlanAsync(query, equipment, partsContext, onProgress: step =>
                        {
                            App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                                ConversationHistory = _streamingPrefix + step + "▌");
                        });

                    var planResponse = FormatPlanAsResponse(plan);
                    LoggingService.Instance.Log($"[+] Parts plan ready: {plan.Options.Count} option(s)");
                    FinalizeStreamingTurn(query, planResponse, string.Empty);
                    AssistantResponse = string.Empty;
                    HasLocalResponse  = true;
                    return;
                }

                // Always use RAG when the vector database is available.
                bool useRag = _vectorDbService != null;
                if (useRag)
                    LoggingService.Instance.Log("[*] Vector DB available � will search technical manuals");
                else
                    LoggingService.Instance.Log("[!] Vector DB unavailable � direct response");

                // Reasoning is only triggered by a definitive hard rule.
                var useReasoning = hardDecision.IsDefinitive && hardDecision.Target == RoutingTarget.LocalReasoning;
                if (useReasoning)
                    LoggingService.Instance.Log("[*] Hard rule: LOCAL_REASONING � reasoning model will be used");

                string ragContext = string.Empty;
                string contextInfo = string.Empty;

                if (useRag)
                {
                    stepBase++;
                    LoggingService.Instance.Log($"=======================================");
                    LoggingService.Instance.Log($"|  STEP {stepBase} � RAG � MANUAL SEARCH          ?");
                    LoggingService.Instance.Log($"=======================================");
                    LoggingService.Instance.Log($"[*] Searching technical manuals...");
                    AssistantResponse = $"Step {stepBase} � Searching technical manuals...";
                    var _ragResult = await PerformRagLookupAsync(query);
                    ragContext  = _ragResult.ragContext;
                    contextInfo = _ragResult.contextInfo;
                    var bestManual = _ragResult.bestManualName;
                    var bestPage   = _ragResult.bestPage;
                    if (bestManual != null)
                        App.MainWindow?.DispatcherQueue.TryEnqueue(() => SetPdfSource(bestManual, bestPage));
                }

                // ?? Step 3: LLM inference ????????????????????????????????????????????
                stepBase++;
                var eqState = EquipmentStateService.Instance;
                var hasEquip = eqState.HasEquipmentInfo;

                LoggingService.Instance.Log($"=======================================");
                LoggingService.Instance.Log($"|  STEP {stepBase} � LLM INFERENCE                ?");
                LoggingService.Instance.Log($"=======================================");
                LoggingService.Instance.Log($"[*] Generating response{(hasEquip ? $" (equipment: {eqState.Summary})" : string.Empty)}...");
                AssistantResponse = $"Step {stepBase} � Generating response...";

                var equipmentContext = BuildEquipmentContext();
                if (hasEquip)
                    LoggingService.Instance.Log($"[+] Equipment context injected � Model: {eqState.ModelNumber ?? "�"}  |  Serial: {eqState.SerialNumber ?? "�"}");
                else
                    LoggingService.Instance.Log("[!] No equipment context � confirm equipment in the Equipment Details tab to include model/serial in prompts");

                string prompt = !string.IsNullOrEmpty(ragContext)
                    ? $"{equipmentContext}Relevant technical documentation:\n{ragContext}\n\nUser question: {query}\n\n"
                      + "Using ONLY the documentation above, answer the question concisely in plain prose. "
                      + "Do NOT add a sources, references, or 'Manual says' section."
                    : !string.IsNullOrEmpty(contextInfo)
                        // No manual found � instruct the LLM to lead with a clear disclaimer.
                        ? $"{equipmentContext}IMPORTANT: No service manual for this specific equipment model is available in the database. " +
                          "You MUST begin your response by clearly stating that no documentation for this model is available and that " +
                          "the following guidance is based on general HVAC knowledge only � NOT the equipment's official service documentation. " +
                          "Encourage the technician to consult the official service manual.\n\n" +
                          $"User question: {query}"
                        : $"{equipmentContext}{query}";

                // ?? Prompt preview dialog ?????????????????????????????????????????????
                LoggingService.Instance.Log("=== FULL PROMPT ===");
                LoggingService.Instance.Log(prompt);
                LoggingService.Instance.Log("=== FULL PROMPT ===");

                // Show turn header with typing cursor immediately so the user knows the model is working.
                BeginStreamingTurn(query);
                AssistantResponse = "Generating response�";

                var thinkTokenCount = 0;
                var thinkingBuilder = new System.Text.StringBuilder();
                var answerBuilder = new System.Text.StringBuilder();

                // Stream thinking content into ConversationHistory so the chain of thought is visible live.
                Action<string> onThinkTokenCallback = chunk =>
                {
                    App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                    {
                        thinkTokenCount++;
                        thinkingBuilder.Append(chunk);
                        AssistantResponse = $"\U0001f9e0 Thinking\u2026 ({thinkTokenCount} token{(thinkTokenCount == 1 ? "" : "s")})";
                        ConversationHistory = _thinkingBasePrefix +
                            $"\U0001f9e0 Thinking\u2026\n{thinkingBuilder}\u25cc";
                    });
                };

                // Stream answer tokens into ConversationHistory immediately after thinking ends.
                Action<string> onAnswerTokenCallback = chunk =>
                {
                    App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                    {
                        answerBuilder.Append(chunk);
                        // On first answer token, lock in the thinking summary prefix.
                        if (answerBuilder.Length == chunk.Length && thinkTokenCount > 0)
                        {
                            _streamingPrefix = _thinkingBasePrefix +
                                $"\U0001f9e0 Thought for {thinkTokenCount} token{(thinkTokenCount == 1 ? "" : "s")} \u2713\n\nAssistant:  ";
                        }
                        ConversationHistory = _streamingPrefix + answerBuilder + "\u25cc";
                    });
                };

                var llmSw = System.Diagnostics.Stopwatch.StartNew();
                var inputTokens = await _llmService.CountPromptTokensAsync(prompt, _conversationTurns);
                var (response, confidence) = await _llmService.GenerateResponseAsync(
                    prompt, _conversationTurns,
                    onToken: onAnswerTokenCallback,
                    onThinkToken: onThinkTokenCallback,
                    useReasoning: useReasoning);
                llmSw.Stop();

                var confidenceLabel = confidence >= 0
                    ? $"[%] Confidence: {confidence:F0}%"
                    : "[%] Confidence: N/A";
                var confidenceBar = confidence >= 0
                    ? new string('#', (int)Math.Round(confidence / 10)) + new string('.', 10 - (int)Math.Round(confidence / 10))
                    : "..........";
                LoggingService.Instance.Log($"{confidenceLabel}  [{confidenceBar}]");

                // Finalise the prefix � add thinking badge if the model used a reasoning step.
                _streamingPrefix = thinkTokenCount > 0
                    ? _thinkingBasePrefix + $"\U0001f9e0 Thought for {thinkTokenCount} token{(thinkTokenCount == 1 ? "" : "s")} \u2713\n\nAssistant:  "
                    : _thinkingBasePrefix + "Assistant:  ";

                LoggingService.Instance.Log($"[+] LLM response ({llmSw.Elapsed.TotalSeconds:F2}s): {inputTokens} input tokens ? {response.Length} output chars");
                LoggingService.Instance.Log("========================================");

                if (!string.IsNullOrEmpty(ragContext))
                {
                    try
                    {
                        var keySnippet = await _llmService.ExtractKeySnippetAsync(query, ragContext);
                        if (!string.IsNullOrWhiteSpace(keySnippet))
                        {
                            contextInfo += $"\n\n\U0001f4d6 From the manual: \"{keySnippet.Trim()}\"";
                            App.MainWindow?.DispatcherQueue.TryEnqueue(() => KeySnippet = keySnippet.Trim());
                        }
                    }
                    catch (Exception snippetEx)
                    {
                        LoggingService.Instance.Log($"[!] Key snippet extraction failed: {snippetEx.Message}");
                    }
                }

                // Store after snippet is appended so AskCloudAsync inherits the full context.
                _lastRagContext = ragContext;
                _lastContextInfo = contextInfo;
                FinalizeStreamingTurn(query, response, contextInfo);
                AssistantResponse = string.Empty;
                HasLocalResponse = true;

                // If confidence is below threshold (or N/A), ask the user whether to escalate.
                bool shouldEscalate = false;
                if (confidence < 90)
                {
                    var label = confidence < 0 ? "N/A" : $"{confidence:F0}%";
                    LoggingService.Instance.Log($"[%] Confidence {label} — prompting user for escalation decision");

                    var args = new EscalationRequestedEventArgs(confidence);
                    EscalationRequested?.Invoke(this, args);
                    shouldEscalate = await args.Decision.Task;

                    if (shouldEscalate)
                        LoggingService.Instance.Log("[!] User chose to escalate to cloud for an expert opinion");
                    else
                        LoggingService.Instance.Log("[*] User chose to keep the local answer");
                }
                else
                {
                    LoggingService.Instance.Log($"[+] Local confidence {confidence:F0}% meets threshold — no escalation needed");
                }

                if (shouldEscalate)
                    await AskCloudAsync(isAutoEscalation: true);
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Log($"[x] Query error: {ex.Message}");
                AssistantResponse = $"Processing error: {ex.Message}\n\nStack trace:\n{ex.StackTrace}";
            }
            finally
            {
                IsProcessing = false;
                TextInput = string.Empty;
            }
        }

        /// <summary>
        /// Searches the vector database for manual content relevant to <paramref name="query"/>.
        /// Returns the concatenated RAG context, citation/disclaimer info string, and the
        /// best result's manual name + page number (for the PDF viewer).
        /// Returns empty strings and null when the vector database is unavailable or no content is found.
        /// </summary>
        // Minimum similarity for a RAG result to be cited as a source in the response footer.
        // Results below this are still passed to the LLM as context but are not labelled as
        // "Sources" because they are too weakly related to be considered authoritative.
        private const float CitationSimilarityThreshold = 0.3f;

        private async Task<(string ragContext, string contextInfo, string? bestManualName, int bestPage)> PerformRagLookupAsync(string query)
        {
            if (_vectorDbService == null)
                return (string.Empty, string.Empty, null, 1);

            string ragContext = string.Empty;
            string contextInfo = string.Empty;
            string? bestManualName = null;
            int bestPage = 1;
            var vectorSw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                string[]? manualFilter = null;
                bool skipRag = false;
                string? noManualLabel = null;
                var eqForRag = EquipmentStateService.Instance;

                if (!string.IsNullOrWhiteSpace(eqForRag.ModelNumber))
                {
                    // Strip leading non-alphanumeric characters that OCR sometimes prepends
                    // (e.g. "\]RA1430AJ1NA" → "RA1430AJ1NA").
                    var cleanModel = System.Text.RegularExpressions.Regex
                        .Replace(eqForRag.ModelNumber, @"^[^A-Za-z0-9]+", string.Empty);
                    var matchedManuals = await _vectorDbService.FindManualsContainingModelAsync(cleanModel);
                    if (matchedManuals.Length > 0)
                    {
                        manualFilter = matchedManuals;
                        LoggingService.Instance.Log($"[DB] Manual filter from content search for model \"{cleanModel}\" � {matchedManuals.Length} manual(s): {string.Join(", ", matchedManuals)}");
                    }
                    else
                    {
                        noManualLabel = cleanModel;
                        LoggingService.Instance.Log($"??  Model \"{cleanModel}\" not found in any manual content � skipping RAG");
                        skipRag = true;
                    }
                }
                else
                {
                    var matchedManuals = await _vectorDbService.GetMatchingManualNamesAsync(query);
                    if (matchedManuals.Length > 0)
                    {
                        manualFilter = matchedManuals;
                        LoggingService.Instance.Log($"[DB] Manual filter inferred from query � {matchedManuals.Length} manual(s): {string.Join(", ", matchedManuals)}");
                    }
                    else
                    {
                        noManualLabel = "this equipment";
                        LoggingService.Instance.Log("[!] No matching service manual found for this query � skipping RAG. Confirm equipment in Equipment Details tab for reliable manual lookup.");
                        skipRag = true;
                    }
                }

                if (noManualLabel != null)
                    contextInfo = $"\n\n[NOTE: No service manual for {noManualLabel} is available in the database � response based on general knowledge only]";

                if (!skipRag)
                {
                    var results = await _vectorDbService.SearchAsync(query, topK: 3, minSimilarity: 0.1f, manualNameFilter: manualFilter);
                    vectorSw.Stop();

                    if (results.Length > 0)
                    {
                        LoggingService.Instance.Log($"[DB] Manual search ({vectorSw.Elapsed.TotalSeconds:F2}s): {results.Length} result(s) found");
                        for (int i = 0; i < results.Length; i++)
                        {
                            var r = results[i];
                            var preview = GetFirstSentences(r.Content, 10);
                            LoggingService.Instance.Log(
                                $"   [{i + 1}] {r.ManualName}  �  Page {r.PageNumber}  �  Similarity {r.Similarity:F3}\n" +
                                $"       {preview}");
                        }

                        bestManualName = results[0].ManualName;
                        bestPage       = results[0].PageNumber;

                        ragContext = string.Join("\n\n---\n\n",
                            results.Select(r => $"[{r.ManualName}, Page {r.PageNumber}]\n{r.Content}"));

                        var citations = results
                            .Where(r => r.Similarity >= CitationSimilarityThreshold)
                            .Select(r => $"{r.ManualName} p.{r.PageNumber}")
                            .Distinct()
                            .ToList();
                        contextInfo = citations.Count > 0
                            ? $"\n\n[Sources: {string.Join(" | ", citations)}]"
                            : $"\n\n[Manuals consulted (low relevance): {string.Join(" | ", results.Select(r => $"{r.ManualName} p.{r.PageNumber}").Distinct())}]";
                    }
                    else
                    {
                        LoggingService.Instance.Log($"[DB] Manual search ({vectorSw.Elapsed.TotalSeconds:F2}s): No matching content � falling back to general knowledge");
                    }
                }
                else
                {
                    vectorSw.Stop();
                }
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Log($"[x] Manual search failed: {ex.Message} � falling back to general knowledge");
            }

            return (ragContext, contextInfo, bestManualName, bestPage);
        }

        /// <summary>
        /// Sends <paramref name="query"/> directly to the cloud model, bypassing local
        /// inference. Called when a hard rule definitively routes the request to the cloud.
        /// </summary>
        private async Task AskCloudDirectAsync(string query, string reason)
        {
            try
            {
                IsProcessing = true;
                HasLocalResponse = false;
                _lastQuery = query;
                KeySnippet = string.Empty;

                LoggingService.Instance.Log("========================================");
                LoggingService.Instance.Log("=======================================");
                LoggingService.Instance.Log("|  HARD RULE ? CLOUD ESCALATION      |");
                LoggingService.Instance.Log("=======================================");
                LoggingService.Instance.Log($"[!] Reason: {reason}");
                LoggingService.Instance.Log($"[>] Sending query to cloud model: \"{query}\"");
                AssistantResponse = "Contacting cloud model�";

                var equipmentContext = BuildEquipmentContext();
                var eqState = EquipmentStateService.Instance;
                if (eqState.HasEquipmentInfo)
                    LoggingService.Instance.Log($"[+] Equipment context injected � Model: {eqState.ModelNumber ?? "�"}  |  Serial: {eqState.SerialNumber ?? "�"}");
                else
                    LoggingService.Instance.Log("[!] No equipment context � confirm equipment in the Equipment Details tab to include model/serial in prompts");

                LoggingService.Instance.Log("=======================================");
                LoggingService.Instance.Log("|  STEP 2 - RAG - MANUAL SEARCH       |");
                LoggingService.Instance.Log("=======================================");
                LoggingService.Instance.Log("[*] Searching technical manuals before cloud call...");
                AssistantResponse = "Step 2 � Searching technical manuals...";
                var (ragContext, contextInfo, bestManualNameCloud, bestPageCloud) = await PerformRagLookupAsync(query);
                if (bestManualNameCloud != null)
                    App.MainWindow?.DispatcherQueue.TryEnqueue(() => SetPdfSource(bestManualNameCloud, bestPageCloud));

                var prompt = !string.IsNullOrEmpty(ragContext)
                    ? $"{equipmentContext}Relevant technical documentation:\n{ragContext}\n\nUser question: {query}\n\n"
                      + "Using the documentation above where applicable, provide a thorough answer. "
                      + "Where the documentation does not fully address the question, supplement with your expert knowledge and clearly indicate which parts of your answer go beyond the manual. "
                      + "Do NOT add a sources, references, or 'Manual says' section."
                    : !string.IsNullOrEmpty(contextInfo)
                        ? $"{equipmentContext}IMPORTANT: No service manual for this specific equipment model is available in the database. "
                          + "You MUST begin your response by clearly stating that no documentation for this model is available and that "
                          + "the following guidance is based on general HVAC knowledge only � NOT the equipment's official service documentation. "
                          + "Encourage the technician to consult the official service manual.\n\n"
                          + $"User question: {query}"
                        : $"{equipmentContext}{query}";

                LoggingService.Instance.Log("=== FULL PROMPT (HARD RULE CLOUD) ===");
                LoggingService.Instance.Log(prompt);
                LoggingService.Instance.Log("=== FULL PROMPT (HARD RULE CLOUD) ===");

                BeginStreamingTurn($"[Cloud] {query}");
                AssistantResponse = "Contacting cloud model�";

                var cloudSw = System.Diagnostics.Stopwatch.StartNew();
                var result = await _cloudLlmService.GenerateResponseAsync(
                    prompt,
                    _conversationTurns,
                    onToken: null,
                    attachments: _attachments.Count > 0 ? _attachments.ToList() : null);
                cloudSw.Stop();

                if (_attachments.Count > 0)
                    LoggingService.Instance.Log($"[+] {_attachments.Count} attachment(s) sent to cloud");

                _streamingPrefix = _thinkingBasePrefix + "Assistant (Cloud):  ";

                LoggingService.Instance.Log($"[+] Cloud response ({cloudSw.Elapsed.TotalSeconds:F2}s): {result.InputTokens} input tokens ? {result.OutputTokens} output tokens");
                LoggingService.Instance.Log("========================================");

                // Record cloud token usage for cost tracking
                TokenUsageService.Instance.RecordCloudUsage(result.InputTokens, result.OutputTokens);

                if (!string.IsNullOrEmpty(ragContext))
                {
                    try
                    {
                        var keySnippet = await _llmService.ExtractKeySnippetAsync(query, ragContext);
                        if (!string.IsNullOrWhiteSpace(keySnippet))
                        {
                            contextInfo += $"\n\n\U0001f4d6 From the manual: \"{keySnippet.Trim()}\"";
                            App.MainWindow?.DispatcherQueue.TryEnqueue(() => KeySnippet = keySnippet.Trim());
                        }
                    }
                    catch (Exception snippetEx)
                    {
                        LoggingService.Instance.Log($"[!] Key snippet extraction failed: {snippetEx.Message}");
                    }
                }

                _lastRagContext = ragContext;
                _lastContextInfo = contextInfo;
                FinalizeStreamingTurn($"[Cloud] {query}", result.Answer, contextInfo);
                AssistantResponse = string.Empty;
                HasLocalResponse = true;
            }
            catch (TechnicianAssistant.Services.CloudAuthenticationException ex)
            {
                LoggingService.Instance.Log($"[x] Cloud authentication failed: {ex.Message}");
                AssistantResponse = string.Empty;
                ConversationHistory = _conversationHistory; // restore conversation without error text
                HasLocalResponse = false;
                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                    CloudAuthenticationFailed?.Invoke(this, ex.Message));
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Log($"[x] Cloud query error: {ex.Message}");
                AssistantResponse = $"Cloud error: {ex.Message}";
                HasLocalResponse = true;
            }
            finally
            {
                IsProcessing = false;
                TextInput = string.Empty;
            }
        }

        private async Task AskCloudAsync(bool isAutoEscalation = false)
        {
            if (string.IsNullOrWhiteSpace(_lastQuery)) return;

            // Show cost-confirmation dialog for manual cloud requests (not auto-escalation,
            // which already went through the EscalationRequested confirmation dialog).
            if (!isAutoEscalation && CloudConfirmationRequested != null)
            {
                var estimatedInputTokens = await _llmService.CountPromptTokensAsync(_lastQuery, _conversationTurns);
                var confirmArgs = new CloudConfirmationEventArgs(estimatedInputTokens);
                CloudConfirmationRequested.Invoke(this, confirmArgs);
                var confirmed = await confirmArgs.Decision.Task;
                if (!confirmed)
                {
                    LoggingService.Instance.Log("[*] User cancelled manual cloud escalation.");
                    return;
                }
            }

            try
            {
                IsProcessing = true;
                HasLocalResponse = false;

                LoggingService.Instance.Log("========================================");
                LoggingService.Instance.Log("=======================================");
                LoggingService.Instance.Log(isAutoEscalation
                    ? "|  AUTO CLOUD ESCALATION (low confidence) |"
                    : "|  CLOUD ESCALATION                   |");
                LoggingService.Instance.Log("=======================================");
                LoggingService.Instance.Log($"[>] Sending query to cloud model: \"{_lastQuery}\"");
                AssistantResponse = "Contacting cloud model�";

                var equipmentContext = BuildEquipmentContext();
                var eqState = EquipmentStateService.Instance;
                if (eqState.HasEquipmentInfo)
                    LoggingService.Instance.Log($"[+] Equipment context injected � Model: {eqState.ModelNumber ?? "�"}  |  Serial: {eqState.SerialNumber ?? "�"}");
                else
                    LoggingService.Instance.Log("[!] No equipment context � confirm equipment in the Equipment Details tab to include model/serial in prompts");

                var prompt = !string.IsNullOrEmpty(_lastRagContext)
                    ? $"{equipmentContext}Relevant technical documentation:\n{_lastRagContext}\n\nUser question: {_lastQuery}\n\n"
                      + "Using the documentation above where applicable, provide a thorough answer. "
                      + "Where the documentation does not fully address the question, supplement with your expert knowledge and clearly indicate which parts of your answer go beyond the manual. "
                      + "Do NOT add a sources, references, or 'Manual says' section."
                    : $"{equipmentContext}{_lastQuery}";

                LoggingService.Instance.Log("=== FULL PROMPT (CLOUD) ===");
                LoggingService.Instance.Log(prompt);
                LoggingService.Instance.Log("=== FULL PROMPT (CLOUD) ===");

                BeginStreamingTurn($"[Cloud] {_lastQuery}");
                AssistantResponse = "Contacting cloud model�";

                var cloudSw = System.Diagnostics.Stopwatch.StartNew();
                var result = await _cloudLlmService.GenerateResponseAsync(
                    prompt,
                    _conversationTurns,
                    onToken: null,
                    attachments: _attachments.Count > 0 ? _attachments.ToList() : null);
                cloudSw.Stop();

                if (_attachments.Count > 0)
                    LoggingService.Instance.Log($"[+] {_attachments.Count} attachment(s) sent to cloud");

                // Set the cloud label prefix for the finalised turn.
                _streamingPrefix = _thinkingBasePrefix + (isAutoEscalation
                    ? "Assistant (Cloud � auto-escalated):  "
                    : "Assistant (Cloud):  ");

                LoggingService.Instance.Log($"[+] Cloud response ({cloudSw.Elapsed.TotalSeconds:F2}s): {result.InputTokens} input tokens ? {result.OutputTokens} output tokens");
                LoggingService.Instance.Log("========================================");

                // Record cloud token usage for cost tracking
                TokenUsageService.Instance.RecordCloudUsage(result.InputTokens, result.OutputTokens);

                // Re-extract the snippet for the cloud answer � the cloud model may surface
                // different content, and _lastContextInfo already has the sources line from
                // the previous local turn (snippet included), so rebuild cleanly from RAG.
                var cloudContextInfo = string.Empty;
                if (!string.IsNullOrEmpty(_lastRagContext))
                {
                    // Rebuild sources line
                    cloudContextInfo = _lastContextInfo;

                    // Re-run snippet extraction against the cloud answer
                    try
                    {
                        var keySnippet = await _llmService.ExtractKeySnippetAsync(_lastQuery, _lastRagContext);
                        if (!string.IsNullOrWhiteSpace(keySnippet))
                        {
                            // Strip any existing snippet from _lastContextInfo before appending new one
                            var sourcesOnly = _lastContextInfo.Contains("\n\n\U0001f4d6")
                                ? _lastContextInfo[.._lastContextInfo.IndexOf("\n\n\U0001f4d6", StringComparison.Ordinal)]
                                : _lastContextInfo;
                            cloudContextInfo = sourcesOnly + $"\n\n\U0001f4d6 From the manual: \"{keySnippet.Trim()}\"";
                            App.MainWindow?.DispatcherQueue.TryEnqueue(() => KeySnippet = keySnippet.Trim());
                        }
                    }
                    catch (Exception snippetEx)
                    {
                        LoggingService.Instance.Log($"[!] Key snippet extraction failed: {snippetEx.Message}");
                    }
                }

                FinalizeStreamingTurn($"[Cloud] {_lastQuery}", result.Answer, cloudContextInfo);
                AssistantResponse = string.Empty;
                HasLocalResponse = true;
            }
            catch (TechnicianAssistant.Services.CloudAuthenticationException ex)
            {
                LoggingService.Instance.Log($"[x] Cloud authentication failed: {ex.Message}");
                AssistantResponse = string.Empty;
                ConversationHistory = _conversationHistory; // restore conversation without error text
                HasLocalResponse = false;
                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                    CloudAuthenticationFailed?.Invoke(this, ex.Message));
            }
            catch (NotImplementedException)
            {
                LoggingService.Instance.Log("[!] Cloud LLM is not yet configured.");
                AssistantResponse = "Cloud LLM is not yet configured.";
                // Put the typing indicator back so the user can see the conversation
                ConversationHistory = _streamingPrefix;
                HasLocalResponse = true;
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Log($"[x] Cloud query error: {ex.Message}");
                AssistantResponse = $"Cloud error: {ex.Message}";
                HasLocalResponse = true;
            }
            finally
            {
                IsProcessing = false;
            }
        }

        /// <summary>
        /// Opens a file picker for images and audio files, reads the selected files into
        /// <see cref="_attachments"/>, and eagerly transcribes WAV audio so the transcript
        /// is ready when the user escalates to the cloud.
        /// </summary>
        private async Task AttachFileAsync()
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(
                    picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
                foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" })
                    picker.FileTypeFilter.Add(ext);

                var files = await picker.PickMultipleFilesAsync();
                if (files == null || files.Count == 0) return;

                foreach (var file in files)
                {
                    byte[] bytes;
                    var buffer = await Windows.Storage.FileIO.ReadBufferAsync(file);
                    bytes = new byte[buffer.Length];
                    Windows.Storage.Streams.DataReader.FromBuffer(buffer).ReadBytes(bytes);

                    var attachment = new PromptAttachment
                    {
                        Kind     = PromptAttachment.AttachmentKind.Image,
                        FileName = file.Name,
                        Data     = bytes
                    };

                    _attachments.Add(attachment);
                    LoggingService.Instance.Log($"[+] Attached: {attachment.DisplayLabel}");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Log($"[x] Attach file error: {ex.Message}");
            }
            finally
            {
                OnPropertyChanged(nameof(HasAttachments));
                OnPropertyChanged(nameof(AttachmentSummary));
            }
        }

        private static bool IsExplicitPartsRequest(string query)
        {
            var partsKeywords = new[]
            {
                "find parts", "order parts", "replacement part", "part number",
                "where to buy", "supplier", "price", "inventory", "stock"
            };
            return partsKeywords.Any(kw => query.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        private static string FormatPlanAsResponse(PartsOrderPlan plan)
        {
            var sb = new System.Text.StringBuilder();

            if (!string.IsNullOrWhiteSpace(plan.WarrantyAdvice))
                sb.AppendLine($"⚠️ **Warranty:** {plan.WarrantyAdvice}\n");

            if (plan.PrimaryPart is not null)
                sb.AppendLine($"🔩 **Part identified:** {plan.PrimaryPart.Description} — `{plan.PrimaryPart.PartNumber}`\n");

            if (!string.IsNullOrWhiteSpace(plan.UrgencyAssessment))
                sb.AppendLine($"🕐 **Urgency:** {plan.UrgencyAssessment}\n");

            if (plan.Options.Count > 0)
            {
                sb.AppendLine("**Ordering options:**");
                foreach (var opt in plan.Options)
                    sb.AppendLine($"  • {opt.Recommendation}: **{opt.Supplier}** — ${opt.Price:F2}, {opt.DeliveryTime}");
                sb.AppendLine();
            }

            if (plan.LaborCost is { } lc)
            {
                sb.AppendLine($"**Labor estimate:** {lc.EstimatedHours:F1} hrs × {lc.Currency} {lc.HourlyRate}/hr + {lc.Currency} {lc.TripCharge} trip = **{lc.Currency} {lc.GrandTotal:F2}** ({lc.DifficultyRating})");
                if (!string.IsNullOrWhiteSpace(lc.Rationale))
                    sb.AppendLine($"  _{lc.Rationale}_");
                sb.AppendLine();
            }

            if (plan.TotalRepairCost.HasValue)
                sb.AppendLine($"📊 **Total estimated repair cost: ${plan.TotalRepairCost.Value:F2}** (best parts price + labor)\n");

            if (!string.IsNullOrWhiteSpace(plan.Recommendations))
                sb.AppendLine($"**Recommendation:** {plan.Recommendations}\n");

            if (plan.ProactiveRecommendations.Count > 0)
            {
                sb.AppendLine("**Also consider checking:**");
                foreach (var rec in plan.ProactiveRecommendations)
                    sb.AppendLine(rec);
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Decodes a WAV byte array to 16 kHz mono PCM float samples and transcribes via Whisper.
        /// Returns <see langword="null"/> if decoding or transcription fails.
        /// </summary>
        private async Task<string?> TranscribeAudioBytesAsync(byte[] wavBytes)
        {
            try
            {
                using var ms = new System.IO.MemoryStream(wavBytes);
                using var reader = new NAudio.Wave.WaveFileReader(ms);

                NAudio.Wave.ISampleProvider provider =
                    new NAudio.Wave.SampleProviders.WaveToSampleProvider(reader);

                NAudio.Wave.ISampleProvider mono = provider.WaveFormat.Channels > 1
                    ? new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(provider)
                    : provider;

                NAudio.Wave.ISampleProvider resampled = mono.WaveFormat.SampleRate != 16_000
                    ? new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(mono, 16_000)
                    : mono;

                var samples = new List<float>();
                var buf = new float[4096];
                int read;
                while ((read = resampled.Read(buf, 0, buf.Length)) > 0)
                    for (int i = 0; i < read; i++) samples.Add(buf[i]);

                return await _transcriptionService.TranscribeAsync(samples.ToArray());
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Log($"[!] WAV decode/transcribe failed: {ex.Message}");
                return null;
            }
        }
    }
}

