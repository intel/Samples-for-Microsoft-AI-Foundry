using Microsoft.Graphics.Imaging;
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Imaging;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using TechnicianAssistant.Services.Interfaces;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;


namespace TechnicianAssistant.Services;

public class OcrService : IOcrService
{
    private Action<string>? _logger;

    public void SetLogger(Action<string> logger)
    {
        _logger = logger;
    }

    private void Log(string message)
    {
        Console.WriteLine(message);
        _logger?.Invoke(message + "\n");
    }

    public async Task<string> RecognizeTextFromImageAsync(string imagePath)
    {
        try
        {
            Log($"Starting OCR for image: {imagePath}");
            SoftwareBitmap inputBitmap = await LoadImageBufferFromFileAsync(imagePath);
            SoftwareBitmap convertedImage = SoftwareBitmap.Convert(inputBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            using var imageBuffer = ImageBuffer.CreateForSoftwareBitmap(convertedImage);
            return await RecognizeTextFromSoftwareBitmap(imageBuffer);
        }
        catch (Exception ex)
        {
            Log($"OCR Error: {ex.Message}");
            throw;
        }
    }

    private async Task<string> RecognizeTextFromSoftwareBitmap(ImageBuffer imageBuffer)
    {
        Log($"Before Text Recognizer created");
        if (TextRecognizer.GetReadyState() == AIFeatureReadyState.NotReady)
        {
            await TextRecognizer.EnsureReadyAsync();
        }
        TextRecognizer textRecognizer = await TextRecognizer.CreateAsync();
  
        RecognizedText recognizedText = textRecognizer.RecognizeTextFromImage(imageBuffer);
        StringBuilder stringBuilder = new StringBuilder();

        foreach (var line in recognizedText.Lines)
        {
            stringBuilder.AppendLine(line.Text);
        }
        return stringBuilder.ToString();
    }

    private static async Task<SoftwareBitmap> LoadImageBufferFromFileAsync(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        StorageFile file = await StorageFile.GetFileFromPathAsync(fullPath);
        IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync();
    }

}
