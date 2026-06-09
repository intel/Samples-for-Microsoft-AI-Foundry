using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;
using TechnicianAssistant.Services;
using TechnicianAssistant.Services.Interfaces;

namespace TechnicianAssistant.ViewModels
{
    public class EquipmentDetailsViewModel : ViewModelBase
    {
        private readonly IOcrService _ocrService;
        private readonly ILlmService _llmService;
        private readonly ICloudLlmService _cloudLlmService;
        private BitmapImage? _capturedImage;
        private string _modelNumber = string.Empty;
        private string _serialNumber = string.Empty;
        private bool _isProcessing;
        private bool _isExtractingModel;
        private bool _hasLocalExtractionResult;
        private string _extractionSource = string.Empty;
        private string? _currentImagePath;
        private byte[]? _currentImageBytes;

        /// <summary>
        /// Raised on the UI thread after LLM extraction completes.
        /// The code-behind should show a confirmation dialog and then call
        /// <see cref="ConfirmExtraction"/> or <see cref="CancelExtraction"/>.
        /// </summary>
        public event EventHandler<EquipmentExtractionEventArgs>? ExtractionCompleted;

        /// <summary>
        /// Raised when a cloud call fails due to missing or invalid AWS credentials.
        /// The UI should show an error dialog with setup instructions.
        /// </summary>
        public event EventHandler<string>? CloudAuthenticationFailed;

        // Holds extracted values while awaiting technician confirmation.
        private string? _pendingModel;
        private string? _pendingSerial;

        public BitmapImage? CapturedImage
        {
            get => _capturedImage;
            set => SetProperty(ref _capturedImage, value);
        }

        public string ModelNumber
        {
            get => _modelNumber;
            set => SetProperty(ref _modelNumber, value);
        }

        public string SerialNumber
        {
            get => _serialNumber;
            set => SetProperty(ref _serialNumber, value);
        }


        public bool IsProcessing
        {
            get => _isProcessing;
            set
            {
                if (SetProperty(ref _isProcessing, value))
                    (AnalyzeWithCloudCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public bool IsExtractingModel
        {
            get => _isExtractingModel;
            set => SetProperty(ref _isExtractingModel, value);
        }

        /// <summary>
        /// True once local OCR + LLM extraction has completed for the current image.
        /// Controls visibility of the "Analyze with Cloud" button.
        /// </summary>
        public bool HasLocalExtractionResult
        {
            get => _hasLocalExtractionResult;
            private set
            {
                if (SetProperty(ref _hasLocalExtractionResult, value))
                    (AnalyzeWithCloudCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        /// <summary>
        /// Human-readable label indicating where the last extraction was performed,
        /// e.g. "??? Local (OCR + LLM)" or "?? Cloud (Claude)".
        /// Empty until the first extraction completes.
        /// </summary>
        public string ExtractionSource
        {
            get => _extractionSource;
            private set => SetProperty(ref _extractionSource, value);
        }

        public ICommand SelectImageCommand { get; }
        public ICommand AnalyzeWithCloudCommand { get; }

        public EquipmentDetailsViewModel(IOcrService ocrService, ILlmService llmService)
        {
            _ocrService = ocrService;
            _llmService = llmService;
            _cloudLlmService = ServiceContainer.Instance.CloudLlmService;
            SelectImageCommand = new RelayCommand(async () => await SelectImageAsync());
            AnalyzeWithCloudCommand = new RelayCommand(
                async () => await AnalyzeWithCloudAsync(),
                () => HasLocalExtractionResult && !IsProcessing);
        }

        private async Task SelectImageAsync()
        {
            try
            {
                IsProcessing = true;
                LoggingService.Instance.Log("?? Opening file picker...");

                var picker = new FileOpenPicker();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".bmp");

                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    _currentImagePath = file.Path;
                    HasLocalExtractionResult = false;
                    ExtractionSource = string.Empty;

                    // Read raw bytes for potential cloud vision analysis
                    using (var readStream = await file.OpenReadAsync())
                    using (var reader = new DataReader(readStream.GetInputStreamAt(0)))
                    {
                        await reader.LoadAsync((uint)readStream.Size);
                        _currentImageBytes = new byte[readStream.Size];
                        reader.ReadBytes(_currentImageBytes);
                    }

                    // Load and display the image
                    using (var stream = await file.OpenAsync(FileAccessMode.Read))
                    {
                        var bitmap = new BitmapImage();
                        await bitmap.SetSourceAsync(stream);
                        CapturedImage = bitmap;
                    }

                    LoggingService.Instance.Log($"??? Image loaded: {file.Name}");
                    LoggingService.Instance.Log("?? Processing OCR...");

                    try
                    {
                        var ocrSw = Stopwatch.StartNew();
                        var extractedText = await _ocrService.RecognizeTextFromImageAsync(file.Path);
                        ocrSw.Stop();

                        if (string.IsNullOrWhiteSpace(extractedText))
                        {
                            LoggingService.Instance.Log($"?? No text detected in the image ({ocrSw.Elapsed.TotalSeconds:F2}s).");
                        }
                        else
                        {
                            LoggingService.Instance.Log($"? OCR complete ({ocrSw.Elapsed.TotalSeconds:F2}s) — extracted text:\n{extractedText}");

                            IsExtractingModel = true;
                            ModelNumber  = "Extracting...";
                            SerialNumber = "Extracting...";
                            try
                            {
                                // ?? LLM extraction ???????????????????????????????????
                                LoggingService.Instance.Log("?? Equipment extraction: sending to LLM...");
                                var llmSw = Stopwatch.StartNew();
                                var llmInfo = await _llmService.ExtractEquipmentInfoAsync(extractedText, new EquipmentInfo());
                                llmSw.Stop();

                                IsExtractingModel = false;

                                LoggingService.Instance.Log($"?? LLM extraction complete ({llmSw.Elapsed.TotalSeconds:F2}s)");
                                LoggingService.Instance.Log(string.IsNullOrWhiteSpace(llmInfo.ModelNumber)
                                    ? "? Model No.  ? LLM: not found"
                                    : $"? Model No.  ? LLM extracted: \"{llmInfo.ModelNumber}\"");
                                LoggingService.Instance.Log(string.IsNullOrWhiteSpace(llmInfo.SerialNumber)
                                    ? "? Serial No. ? LLM: not found"
                                    : $"? Serial No. ? LLM extracted: \"{llmInfo.SerialNumber}\"");

                                // Store pending values and let the code-behind show the dialog.
                                _pendingModel  = llmInfo.ModelNumber;
                                _pendingSerial = llmInfo.SerialNumber;
                                ExtractionCompleted?.Invoke(this, new EquipmentExtractionEventArgs(
                                    llmInfo.ModelNumber, llmInfo.SerialNumber));

                                LoggingService.Instance.Log($"?? Awaiting technician confirmation — Model: \"{llmInfo.ModelNumber}\" | Serial: \"{llmInfo.SerialNumber}\" | Source: {llmInfo.ExtractionSource}");
                                ExtractionSource = $"??? Local (OCR {ocrSw.Elapsed.TotalSeconds:F2}s + LLM {llmSw.Elapsed.TotalSeconds:F2}s)";
                                HasLocalExtractionResult = true;
                            }
                            catch (Exception extractEx)
                            {
                                LoggingService.Instance.Log($"? Extraction error: {extractEx.Message}");
                                if (string.IsNullOrEmpty(ModelNumber)  || ModelNumber  == "Extracting...") ModelNumber  = "Error";
                                if (string.IsNullOrEmpty(SerialNumber) || SerialNumber == "Extracting...") SerialNumber = $"Error: {extractEx.Message}";
                            }
                            finally
                            {
                                IsExtractingModel = false;
                            }
                        }
                    }
                    catch (Exception ocrEx)
                    {
                        LoggingService.Instance.Log($"? OCR Error: {ocrEx.Message}");
                    }
                }
                else
                {
                    LoggingService.Instance.Log("?? Image selection cancelled.");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Log($"? Error loading image: {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        /// <summary>
        /// Called by the code-behind after the technician clicks Confirm.
        /// Commits the (possibly edited) values to the card and central state.
        /// </summary>
        public void ConfirmExtraction(string model, string serial)
        {
            ModelNumber  = string.IsNullOrWhiteSpace(model)  ? "NOT FOUND" : model;
            SerialNumber = string.IsNullOrWhiteSpace(serial) ? "NOT FOUND" : serial;

            EquipmentStateService.Instance.ModelNumber  = string.IsNullOrWhiteSpace(model)  ? null : model;
            EquipmentStateService.Instance.SerialNumber = string.IsNullOrWhiteSpace(serial) ? null : serial;

            LoggingService.Instance.Log($"? Confirmed ? Model: \"{ModelNumber}\" | Serial: \"{SerialNumber}\"");
        }

        /// <summary>
        /// Called by the code-behind when the technician cancels the confirmation dialog.
        /// Displays the raw extracted values without publishing them to the central state.
        /// </summary>
        public void CancelExtraction()
        {
            ModelNumber  = _pendingModel  ?? "NOT FOUND";
            SerialNumber = _pendingSerial ?? "NOT FOUND";
            LoggingService.Instance.Log("?? Equipment confirmation cancelled — values not saved to session.");
        }

        private async Task AnalyzeWithCloudAsync()
        {
            if (_currentImageBytes == null)
            {
                LoggingService.Instance.Log("?? No image available for cloud analysis.");
                return;
            }

            try
            {
                IsProcessing = true;
                HasLocalExtractionResult = false;
                IsExtractingModel = true;
                ModelNumber  = "Analyzing with Cloud…";
                SerialNumber = "Analyzing with Cloud…";

                LoggingService.Instance.Log("??  Cloud image analysis: sending photo to Claude…");

                const string question =
                    "You are an HVAC equipment expert. Look at this equipment label photo and extract " +
                    "the model number and serial number. " +
                    "Note: 'Serial Number' and 'Product Identification Number' (also labelled as P/IN or PIN) are the same field." +
                    "Return ONLY a JSON object with two keys: \"model\" and \"serial\". " +
                    "Use null for any value you cannot find. Do not include any other text. " +
                    "Example: {\"model\": \"XY-123-A\", \"serial\": \"SN9876543\"}";

                var cloudSw = System.Diagnostics.Stopwatch.StartNew();
                var cloudResult = await _cloudLlmService.AnalyzeImageAsync(_currentImageBytes, question);
                cloudSw.Stop();

                LoggingService.Instance.Log($"? Cloud image analysis ({cloudSw.Elapsed.TotalSeconds:F2}s): {cloudResult.InputTokens} input tokens ? {cloudResult.OutputTokens} output tokens");

                // Strip markdown fences if the model wraps the output
                var trimmed = cloudResult.Answer.Trim();
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    var start = trimmed.IndexOf('{');
                    var end   = trimmed.LastIndexOf('}');
                    if (start >= 0 && end > start)
                        trimmed = trimmed[start..(end + 1)];
                }

                string? model  = null;
                string? serial = null;
                try
                {
                    using var doc  = JsonDocument.Parse(trimmed);
                    var root = doc.RootElement;
                    model  = root.TryGetProperty("model",  out var mp) ? mp.GetString() : null;
                    serial = root.TryGetProperty("serial", out var sp) ? sp.GetString() : null;
                }
                catch
                {
                    LoggingService.Instance.Log("?? Cloud response was not valid JSON — treating as plain text answer.");
                    ModelNumber  = "See logs";
                    SerialNumber = cloudResult.Answer;
                    return;
                }

                LoggingService.Instance.Log(string.IsNullOrWhiteSpace(model)
                    ? "? Model No.  ? Cloud: not found"
                    : $"? Model No.  ? Cloud extracted: \"{model}\"");
                LoggingService.Instance.Log(string.IsNullOrWhiteSpace(serial)
                    ? "? Serial No. ? Cloud: not found"
                    : $"? Serial No. ? Cloud extracted: \"{serial}\"");

                // Reuse the existing confirmation dialog flow
                _pendingModel  = model;
                _pendingSerial = serial;
                ExtractionCompleted?.Invoke(this, new EquipmentExtractionEventArgs(model, serial));

                LoggingService.Instance.Log($"?? Awaiting technician confirmation (cloud) — Model: \"{model}\" | Serial: \"{serial}\"");
                ExtractionSource = $"?? Cloud — Claude ({cloudSw.Elapsed.TotalSeconds:F2}s)";
                HasLocalExtractionResult = true;
            }
            catch (TechnicianAssistant.Services.CloudAuthenticationException ex)
            {
                LoggingService.Instance.Log($"[x] Cloud authentication failed: {ex.Message}");
                ModelNumber  = string.Empty;
                SerialNumber = string.Empty;
                HasLocalExtractionResult = false;
                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                    CloudAuthenticationFailed?.Invoke(this, ex.Message));
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Log($"[x] Cloud image analysis error: {ex.Message}");
                ModelNumber  = "Cloud Error";
                SerialNumber = ex.Message;
            }
            finally
            {
                IsExtractingModel = false;
                IsProcessing = false;
            }
        }
    }
}
