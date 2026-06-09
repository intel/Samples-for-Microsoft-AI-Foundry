using System;
using System.Threading.Tasks;

namespace TechnicianAssistant.Services.Interfaces
{
    public interface IEmbeddingService
    {
        /// <summary>
        /// Generates a 384-dimensional embedding for the input text.
        /// </summary>
        float[] GetEmbedding(string text);
    }
}
