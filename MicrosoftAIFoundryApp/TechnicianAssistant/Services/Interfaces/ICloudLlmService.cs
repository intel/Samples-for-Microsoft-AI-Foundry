using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TechnicianAssistant.Services;

namespace TechnicianAssistant.Services.Interfaces;

/// <summary>
/// Cloud-based LLM fallback invoked when the technician is not satisfied
/// with the local model's response.
/// </summary>
public interface ICloudLlmService
{
    /// <summary>
    /// Sends <paramref name="prompt"/> to the cloud LLM and returns the answer
    /// together with Bedrock token-usage counters.
    /// Streaming tokens are forwarded via <paramref name="onToken"/> when provided.
    /// </summary>
    Task<CloudLlmResult> GenerateResponseAsync(
        string prompt,
        IReadOnlyList<ConversationTurn>? history = null,
        int maxTokens = 15000,
        float temperature = 0f,
        Action<string>? onToken = null,
        IReadOnlyList<PromptAttachment>? attachments = null);

    /// <summary>
    /// Sends an image together with <paramref name="question"/> to the cloud vision
    /// model and returns the answer together with Bedrock token-usage counters.
    /// Used to analyse equipment labels directly from a photo when local OCR + LLM
    /// extraction is insufficient.
    /// </summary>
    Task<CloudLlmResult> AnalyzeImageAsync(byte[] imageBytes, string question);
}


