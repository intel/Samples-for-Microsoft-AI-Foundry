using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TechnicianAssistant.Services;
using TechnicianAssistant.ViewModels;
using EscalationRequestedEventArgs = TechnicianAssistant.ViewModels.EscalationRequestedEventArgs;

namespace TechnicianAssistant
{
    public sealed partial class MainPage : Page
    {
        public MainViewModel ViewModel { get; }

        public MainPage()
        {
            ViewModel = new MainViewModel();
            InitializeComponent();

            // Auto-scroll the conversation to the bottom whenever new content is appended.
            ViewModel.VoiceSupport.PropertyChanged += OnVoiceSupportPropertyChanged;

            // Show a confirmation dialog after OCR + LLM extraction completes.
            ViewModel.EquipmentDetails.ExtractionCompleted += OnExtractionCompleted;

            // Show error dialog when equipment cloud analysis credentials are missing or rejected.
            ViewModel.EquipmentDetails.CloudAuthenticationFailed += OnCloudAuthenticationFailed;

            // Ask the user before escalating low-confidence answers to the cloud.
            ViewModel.VoiceSupport.EscalationRequested += OnEscalationRequested;

            // Show cost confirmation before a manual cloud request.
            ViewModel.VoiceSupport.CloudConfirmationRequested += OnCloudConfirmationRequested;

            // Show error dialog when cloud credentials are missing or rejected.
            ViewModel.VoiceSupport.CloudAuthenticationFailed += OnCloudAuthenticationFailed;
        }

        private void OnVoiceSupportPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(VoiceSupportViewModel.ConversationHistory))
            {
                ConversationScrollViewer?.DispatcherQueue.TryEnqueue(() =>
                    ConversationScrollViewer.ChangeView(null, ConversationScrollViewer.ScrollableHeight, null));
            }

            // Only trigger on CurrentPdfPage: SetPdfSource always sets path first then page,
            // so by the time CurrentPdfPage fires both properties are already updated.
            // Triggering on both would cause a first navigation at page 1 (old value) followed
            // by a fragment-only change that the PDF renderer ignores.
            if (e.PropertyName == nameof(VoiceSupportViewModel.CurrentPdfPage))
            {
                DispatcherQueue.TryEnqueue(RefreshPdfViewer);
            }
        }

        /// <summary>
        /// Navigates <see cref="ManualWebView"/> to the current PDF file at the relevant page.
        /// Uses the browser's built-in PDF renderer — no third-party library needed.
        /// </summary>
        private async void RefreshPdfViewer()
        {
            var vm = ViewModel.VoiceSupport;
            if (!vm.HasPdfSource || ManualWebView == null) return;

            // Ensure the underlying Chromium engine is initialized before navigating.
            await ManualWebView.EnsureCoreWebView2Async();

            var url = $"file:///{vm.CurrentPdfPath!.Replace('\\', '/')}#page={vm.CurrentPdfPage}";

            // If the same PDF is already displayed the browser treats the #page=N change as a
            // fragment-only navigation and ignores it.  Work around this by navigating to
            // about:blank first and waiting for that navigation to complete before loading the
            // real URL, which forces a full reload at the correct page every time.
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnBlankNavigated(object? s, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs _)
            {
                ManualWebView.CoreWebView2.NavigationCompleted -= OnBlankNavigated;
                tcs.TrySetResult(true);
            }

            ManualWebView.CoreWebView2.NavigationCompleted += OnBlankNavigated;
            ManualWebView.CoreWebView2.Navigate("about:blank");

            // Wait for about:blank to finish (with a safety timeout) then load the PDF.
            await Task.WhenAny(tcs.Task, Task.Delay(2000));
            ManualWebView.CoreWebView2.Navigate(url);
        }

        private void OnEscalationRequested(object? sender, EscalationRequestedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() => ShowEscalationDialog(e));
        }

        private void OnCloudConfirmationRequested(object? sender, CloudConfirmationEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() => ShowCloudConfirmationDialog(e));
        }

        private void OnCloudAuthenticationFailed(object? sender, string message)
        {
            DispatcherQueue.TryEnqueue(() => ShowCloudAuthenticationErrorDialog(message));
        }

        private void ShowCloudAuthenticationErrorDialog(string message)
        {
            var panel = new StackPanel { Spacing = 12, MinWidth = 420 };

            panel.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"]
            });

            panel.Children.Add(new TextBlock
            {
                Text = "Check appsettings.json and ensure the correct CloudProvider, credentials, and endpoint are configured, then restart the application.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
            });

            var dialog = new ContentDialog
            {
                Title           = "\u26A0 Cloud Credentials Error",
                Content         = panel,
                CloseButtonText = "OK",
                DefaultButton   = ContentDialogButton.Close,
                XamlRoot        = XamlRoot
            };

            _ = dialog.ShowAsync();
        }

        private void ShowCloudConfirmationDialog(CloudConfirmationEventArgs e)
        {
            var costText     = e.EstimatedCostUsd < 0.0001 ? "< $0.0001" : $"~{e.EstimatedCostUsd:C4}";
            var modelId      = ServiceContainer.Instance.CloudModelId;
            var usage        = TechnicianAssistant.Services.TokenUsageService.Instance;
            var inputRate    = usage.InputPricePerMillion;
            var outputRate   = usage.OutputPricePerMillion;

            var messageBlock = new TextBlock
            {
                Text =
                    $"This will send your question to the cloud ({modelId}) and will incur API costs.\n\n" +
                    $"Estimated cost for this request: {costText}\n" +
                    $"(based on ~{e.EstimatedInputTokens:N0} input tokens at ${inputRate:F2}/M + estimated output at ${outputRate:F2}/M)\n\n" +
                    "Do you want to proceed?",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 400
            };

            var dialog = new ContentDialog
            {
                Title             = "Send to Cloud Model?",
                Content           = messageBlock,
                PrimaryButtonText = "Yes, Ask Cloud",
                CloseButtonText   = "Cancel",
                DefaultButton     = ContentDialogButton.Primary,
                XamlRoot          = XamlRoot
            };

            dialog.PrimaryButtonClick += (_, _) => e.Decision.TrySetResult(true);
            dialog.CloseButtonClick   += (_, _) => e.Decision.TrySetResult(false);
            dialog.Closed             += (_, _) => e.Decision.TrySetResult(false);

            _ = dialog.ShowAsync();
        }

        private void ShowEscalationDialog(EscalationRequestedEventArgs e)
        {
            var confidenceText = e.Confidence < 0
                ? "The local model was unable to score its own answer (confidence: N/A)."
                : $"The local model rated its own answer at {e.Confidence:F0}% confidence.";

            var messageBlock = new TextBlock
            {
                Text        = confidenceText +
                              "\n\nWould you like a second opinion from the cloud expert model?" +
                              "\n\nNote: this will incur additional cloud API usage costs.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth    = 380
            };

            var confidenceBadge = new Border
            {
                CornerRadius      = new CornerRadius(6),
                Background        = e.Confidence < 0
                    ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray)
                    : e.Confidence < 50
                        ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Firebrick)
                        : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DarkOrange),
                Padding           = new Thickness(10, 4, 10, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin            = new Thickness(0, 0, 0, 12),
                Child = new TextBlock
                {
                    Text       = e.Confidence < 0 ? "Confidence: N/A" : $"Confidence: {e.Confidence:F0}%",
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    FontSize   = 13
                }
            };

            var panel = new StackPanel { Spacing = 8, MinWidth = 360 };
            panel.Children.Add(confidenceBadge);
            panel.Children.Add(messageBlock);

            var dialog = new ContentDialog
            {
                Title               = "Low Confidence - Get Expert Opinion?",
                Content             = panel,
                PrimaryButtonText   = "Yes, Ask Cloud Expert",
                CloseButtonText     = "No, Keep Local Answer",
                DefaultButton       = ContentDialogButton.Primary,
                XamlRoot            = XamlRoot
            };

            dialog.PrimaryButtonClick += (_, _) => e.Decision.TrySetResult(true);
            dialog.CloseButtonClick   += (_, _) => e.Decision.TrySetResult(false);

            // Safety net — if dialog is dismissed without a button (e.g. light-dismiss),
            // default to not escalating so the ViewModel never hangs awaiting the TCS.
            dialog.Closed += (_, _) => e.Decision.TrySetResult(false);

            _ = dialog.ShowAsync();
        }

        private void OnExtractionCompleted(object? sender, EquipmentExtractionEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() => ShowEquipmentConfirmationDialog(e));
        }

        private void ShowEquipmentConfirmationDialog(EquipmentExtractionEventArgs e)
        {
            // ?? Build editable fields ????????????????????????????????????????????????
            var modelLabel = new TextBlock
            {
                Text   = "Model No.",
                Style  = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Margin = new Thickness(0, 0, 0, 4)
            };
            var modelBox = new TextBox
            {
                Text                = e.ModelNumber  ?? string.Empty,
                PlaceholderText     = "e.g. XY-123-A",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var serialLabel = new TextBlock
            {
                Text   = "Serial No.",
                Style  = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Margin = new Thickness(0, 16, 0, 4)
            };
            var serialBox = new TextBox
            {
                Text                = e.SerialNumber ?? string.Empty,
                PlaceholderText     = "e.g. SN9876543",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var panel = new StackPanel { MinWidth = 360 };
            panel.Children.Add(modelLabel);
            panel.Children.Add(modelBox);
            panel.Children.Add(serialLabel);
            panel.Children.Add(serialBox);

            // ?? Build the ContentDialog ???????????????????????????????????????????????
            var dialog = new ContentDialog
            {
                Title             = "Confirm Equipment Details",
                Content           = panel,
                PrimaryButtonText = "Confirm",
                CloseButtonText   = "Cancel",
                DefaultButton     = ContentDialogButton.Primary,
                XamlRoot          = XamlRoot
            };

            // Use button-click events instead of awaiting ShowAsync to work around
            // the WinRT async awaiter limitation in this SDK version.
            dialog.PrimaryButtonClick += (_, _) =>
                ViewModel.EquipmentDetails.ConfirmExtraction(modelBox.Text.Trim(), serialBox.Text.Trim());

            dialog.CloseButtonClick += (_, _) =>
                ViewModel.EquipmentDetails.CancelExtraction();

            // Fire and forget — the button handlers above carry the result.
            _ = dialog.ShowAsync();
        }

        private void SystemLogsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ShowLogsButton_Click(sender, e);
        }

        private void ClearLogsButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ClearLogsCommand.Execute(null);
        }

        private void ShowLogsButton_Click(object sender, RoutedEventArgs e)
        {
            var usage = ViewModel.VoiceSupport;

            var logText = new TextBlock
            {
                Text                   = LoggingService.Instance.GetFullLog(),
                FontFamily             = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize               = 12,
                TextWrapping           = TextWrapping.NoWrap,
                IsTextSelectionEnabled = true
            };

            var clearBtn = new Button
            {
                Content             = "Clear",
                Margin              = new Thickness(0, 8, 0, 0),
                CornerRadius        = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var scroll = new ScrollViewer
            {
                Content                    = logText,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight                  = 520,
                MinWidth                   = 700
            };

            var panel = new StackPanel { Spacing = 0 };
            panel.Children.Add(scroll);
            panel.Children.Add(clearBtn);

            var dialog = new ContentDialog
            {
                Title           = "System Logs",
                Content         = panel,
                CloseButtonText = "Close",
                DefaultButton   = ContentDialogButton.Close,
                XamlRoot        = XamlRoot
            };

            // Refresh log text live while the dialog is open
            void onLogAdded(object? s, string entry)
            {
                DispatcherQueue.TryEnqueue(() => logText.Text = LoggingService.Instance.GetFullLog());
            }
            LoggingService.Instance.LogAdded += onLogAdded;

            clearBtn.Click += (_, _) =>
            {
                ViewModel.ClearLogsCommand.Execute(null);
                logText.Text = string.Empty;
            };

            dialog.Closed += (_, _) => LoggingService.Instance.LogAdded -= onLogAdded;

            _ = dialog.ShowAsync();
        }

        private async void TokenUsageMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var usage = TechnicianAssistant.Services.TokenUsageService.Instance;

            var summaryText = new TextBlock
            {
                Text             = usage.BuildSummary(),
                FontFamily       = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize         = 13,
                TextWrapping     = TextWrapping.NoWrap,
                IsTextSelectionEnabled = true
            };

            var resetButton = new Button
            {
                Content             = "Reset Counters",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin              = new Thickness(0, 16, 0, 0),
                CornerRadius        = new CornerRadius(6)
            };

            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(summaryText);
            panel.Children.Add(resetButton);

            var dialog = new ContentDialog
            {
                Title           = "Token Usage & Cost Estimate",
                Content         = panel,
                CloseButtonText = "Close",
                DefaultButton   = ContentDialogButton.Close,
                XamlRoot        = XamlRoot
            };

            resetButton.Click += (_, _) =>
            {
                usage.Reset();
                summaryText.Text = usage.BuildSummary();
            };

            _ = dialog.ShowAsync();
        }

        private void QueryTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                var shiftHeld = Microsoft.UI.Input.InputKeyboardSource
                    .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

                if (!shiftHeld)
                {
                    // Plain Enter — submit if possible
                    e.Handled = true;
                    if (ViewModel.VoiceSupport.SubmitTextCommand.CanExecute(null))
                        ViewModel.VoiceSupport.SubmitTextCommand.Execute(null);
                }
                // Shift+Enter falls through so AcceptsReturn inserts the newline normally
            }
        }
    }
}


