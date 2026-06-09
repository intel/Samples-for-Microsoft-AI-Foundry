using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using TechnicianAssistant.Services.Interfaces;

namespace TechnicianAssistant.Services;

public class LlmService : ILlmService
{
    private OpenAIClientWrapper? _clientWrapper;
    private readonly IFoundryLocalService _foundryService;
    private readonly string _modelId;
    private readonly string _reasoningModelId;

    public LlmService(IFoundryLocalService foundryService, string modelId = "Phi-4-mini-reasoning-openvino-gpu:2", string reasoningModelId = "")
    {
        _foundryService = foundryService;
        _modelId = modelId;
        _reasoningModelId = reasoningModelId;
    }

    public async Task InitializeAsync()
    {
        var endpoint = await _foundryService.GetEndpointAsync();
        _clientWrapper = new OpenAIClientWrapper(endpoint, _modelId, _reasoningModelId);
    }

    public async Task<(string answer, double confidence)> GenerateResponseAsync(
        string prompt,
        IReadOnlyList<ConversationTurn>? history = null,
        int maxTokens = 8000,  // Safe limit: leaves room for ~8K input tokens within 16384 total
        float temperature = 0f,
        Action<string>? onToken = null,
        Action<string>? onThinkToken = null,
        bool useReasoning = false)
    {
        if (_clientWrapper == null)
            await InitializeAsync();

        var inputTokens = await _clientWrapper!.CountPromptTokensAsync(prompt, history);
        var result = await _clientWrapper!.GenerateAsync(prompt, history, maxTokens, temperature, onToken: onToken, onThinkToken: onThinkToken, useReasoning: useReasoning);
        TokenUsageService.Instance.RecordLocalUsage(inputTokens, result.TotalTokens);
        return (result.Answer, result.Confidence);
    }

    public async Task<int> CountPromptTokensAsync(string prompt, IReadOnlyList<ConversationTurn>? history = null, string? systemPrompt = null)
    {
        if (_clientWrapper == null)
            await InitializeAsync();

        return await _clientWrapper!.CountPromptTokensAsync(prompt, history, systemPrompt);
    }

    public async Task<string> ExtractKeySnippetAsync(string question, string ragContext)
    {
        if (_clientWrapper == null)
            await InitializeAsync();

        return await _clientWrapper!.ExtractKeySnippetAsync(question, ragContext);
    }

    public async Task<EquipmentInfo> ExtractEquipmentInfoAsync(string ocrText, EquipmentInfo partialInfo)
    {
        if (_clientWrapper == null)
            await InitializeAsync();

        var prompt =
            "You are an HVAC equipment expert. Extract the model number and serial number from the OCR text below.\n" +
            "Note: 'Serial Number' and 'Product Identification Number' (also labelled as P/IN or PIN) are the same field — return whichever is present as the \"serial\" value.\n" +
            "Return ONLY a JSON object with these two keys: \"model\" and \"serial\".\n" +
            "Use null for any value you cannot find. Do not include any other text.\n" +
            "Example: {\"model\": \"XY-123-A\", \"serial\": \"SN9876543\"}\n\n" +
            "OCR Text:\n" + ocrText;

        const string extractionSystemPrompt =
            "You are an HVAC equipment expert. " +
            "Return ONLY a JSON object with exactly two keys: \"model\" and \"serial\". " +
            "Use null for any value you cannot find. Do not include any other text.";

        var result = await _clientWrapper!.GenerateAsync(
            prompt,
            maxTokens:    200,
            temperature:  0f,
            systemPrompt: extractionSystemPrompt);
        var json = result.Answer.Trim();

        // Strip markdown fences if the model wraps the output
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var start = json.IndexOf('{');
            var end   = json.LastIndexOf('}');
            if (start >= 0 && end > start)
                json = json[start..(end + 1)];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var llmModel  = root.TryGetProperty("model",  out var mp) ? mp.GetString() : null;
            var llmSerial = root.TryGetProperty("serial", out var sp) ? sp.GetString() : null;

            var merged = new EquipmentInfo
            {
                ModelNumber  = llmModel,
                SerialNumber = llmSerial,
                ExtractionSource = "LLM"
            };
            return merged;
        }
        catch
        {
            // If JSON parsing fails return whatever regex already found
            return partialInfo;
        }
    }
}

