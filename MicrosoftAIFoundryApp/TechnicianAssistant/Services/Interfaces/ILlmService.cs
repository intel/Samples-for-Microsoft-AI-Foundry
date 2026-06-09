using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TechnicianAssistant.Services;

namespace TechnicianAssistant.Services.Interfaces
{
    public interface ILlmService
    {
        Task InitializeAsync();
        Task<(string answer, double confidence)> GenerateResponseAsync(
            string prompt,
            IReadOnlyList<ConversationTurn>? history = null,
            int maxTokens = 8000,
            float temperature = 0f,
            Action<string>? onToken = null,
            Action<string>? onThinkToken = null,
            bool useReasoning = false);
        Task<int> CountPromptTokensAsync(string prompt, IReadOnlyList<ConversationTurn>? history = null, string? systemPrompt = null);
        /// <summary>
        /// Given the technician's question and the RAG context that was retrieved,
        /// asks the LLM to identify the single most relevant sentence or short passage
        /// that directly supports the answer.
        /// </summary>
        Task<string> ExtractKeySnippetAsync(string question, string ragContext);
        /// <summary>
        /// LLM fallback: extracts model and/or serial number from raw OCR text.
        /// Only fields that are null/empty in <paramref name="partialInfo"/> will
        /// be populated — pass a fresh <see cref="EquipmentInfo"/> to extract both.
        /// </summary>
        Task<EquipmentInfo> ExtractEquipmentInfoAsync(string ocrText, EquipmentInfo partialInfo);
    }
}

