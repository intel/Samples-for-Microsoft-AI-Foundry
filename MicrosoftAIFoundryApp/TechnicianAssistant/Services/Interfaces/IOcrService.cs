using System;
using System.Threading.Tasks;

namespace TechnicianAssistant.Services.Interfaces
{
    public interface IOcrService
    {
        Task<string> RecognizeTextFromImageAsync(string imagePath);
       
        
        /// <summary>
        /// Sets a custom logger for debugging OCR operations.
        /// </summary>
        void SetLogger(Action<string> logger);
    }
}

