using System;
using System.ClientModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using OpenAI;
using OpenAI.Chat;
using TechnicianAssistant.Services.Interfaces;

namespace TechnicianAssistant.Services;

/// <summary>
/// Cloud LLM implementation that targets Azure AI Foundry serverless inference endpoints.
/// Uses the OpenAI-compatible REST API exposed by Azure AI Foundry — no additional NuGet
/// packages are required beyond the <c>OpenAI</c> SDK that is already referenced.
///
/// Configure in appsettings.json:
///   "CloudProvider":        "AzureFoundry"
///   "AzureFoundryEndpoint": "https://&lt;your-project&gt;.inference.ai.azure.com"
///   "AzureFoundryApiKey":   "&lt;your-api-key&gt;"
///   "CloudModelId":         "&lt;deployment-name&gt;"  e.g. "gpt-4o"
/// </summary>
public class AzureFoundryLlmService : ICloudLlmService
{
    private readonly ChatClient _chatClient;
    private readonly string _requestUrl;

    public AzureFoundryLlmService(string modelId, string endpoint, string apiKey)
    {
        var baseEndpoint = endpoint.TrimEnd('/');
        _requestUrl = $"{baseEndpoint}/v1/chat/completions";

        var client = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(baseEndpoint + "/v1") });
        _chatClient = client.GetChatClient(modelId);
    }

    public async Task<CloudLlmResult> GenerateResponseAsync(
        string prompt,
        IReadOnlyList<ConversationTurn>? history = null,
        int maxTokens = 15000,
        float temperature = 0f,
        Action<string>? onToken = null,
        IReadOnlyList<PromptAttachment>? attachments = null)
    {
        try
        {
            var messages = new List<ChatMessage>();

            if (history != null)
            {
                foreach (var turn in history)
                {
                    messages.Add(ChatMessage.CreateUserMessage(turn.Question));
                    messages.Add(ChatMessage.CreateAssistantMessage(turn.Answer));
                }
            }

            var contentParts = new List<ChatMessageContentPart>();

            if (attachments != null && attachments.Count > 0)
            {
                foreach (var att in attachments.Where(a => a.Kind == PromptAttachment.AttachmentKind.Image))
                {
                    var mediaType = GetImageMediaType(att.FileName);
                    var base64 = Convert.ToBase64String(att.Data);
                    contentParts.Add(ChatMessageContentPart.CreateImagePart(
                        new Uri($"data:{mediaType};base64,{base64}")));
                }

                var audioNotes = attachments
                    .Where(a => a.Kind == PromptAttachment.AttachmentKind.Audio)
                    .Select(a => a.AudioTranscript != null
                        ? $"[Audio file: {a.FileName}]\nTranscript: {a.AudioTranscript}"
                        : $"[Audio file attached: {a.FileName} \u2013 transcript unavailable]")
                    .ToList();

                var fullText = audioNotes.Count > 0
                    ? string.Join("\n\n", audioNotes) + "\n\n" + prompt
                    : prompt;

                contentParts.Add(ChatMessageContentPart.CreateTextPart(fullText));
            }
            else
            {
                contentParts.Add(ChatMessageContentPart.CreateTextPart(prompt));
            }

            messages.Add(ChatMessage.CreateUserMessage(contentParts));

            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = maxTokens,
                Temperature = temperature
            };

            LoggingService.Instance.Log($"[AzureFoundryLlmService] POST {_requestUrl}");
            var response = await _chatClient.CompleteChatAsync(messages, options);
            var answer = response.Value?.Content is { Count: > 0 } c ? c[0].Text ?? string.Empty : string.Empty;

            onToken?.Invoke(answer);

            return new CloudLlmResult(
                answer,
                response.Value.Usage?.InputTokenCount  ?? 0,
                response.Value.Usage?.OutputTokenCount ?? 0);
        }
        catch (ClientResultException ex) when (
            ex.Status == (int)HttpStatusCode.Unauthorized ||
            ex.Status == (int)HttpStatusCode.Forbidden)
        {
            throw new CloudAuthenticationException(
                "Azure AI Foundry returned an authentication error. " +
                "Verify that AzureFoundryApiKey in appsettings.json is correct and that the " +
                "deployment endpoint is accessible from this machine.", ex);
        }
    }

    public async Task<CloudLlmResult> AnalyzeImageAsync(byte[] imageBytes, string question)
    {
        try
        {
            var base64 = Convert.ToBase64String(imageBytes);
            var messages = new List<ChatMessage>
            {
                ChatMessage.CreateUserMessage(
                    ChatMessageContentPart.CreateImagePart(new Uri($"data:image/jpeg;base64,{base64}")),
                    ChatMessageContentPart.CreateTextPart(question))
            };

            var options = new ChatCompletionOptions { Temperature = 0f };
            LoggingService.Instance.Log($"[AzureFoundryLlmService] POST {_requestUrl}");
            var response = await _chatClient.CompleteChatAsync(messages, options);

            return new CloudLlmResult(
                response.Value?.Content is { Count: > 0 } c ? c[0].Text ?? string.Empty : string.Empty,
                response.Value.Usage?.InputTokenCount  ?? 0,
                response.Value.Usage?.OutputTokenCount ?? 0);
        }
        catch (ClientResultException ex) when (
            ex.Status == (int)HttpStatusCode.Unauthorized ||
            ex.Status == (int)HttpStatusCode.Forbidden)
        {
            throw new CloudAuthenticationException(
                "Azure AI Foundry returned an authentication error. " +
                "Verify that AzureFoundryApiKey in appsettings.json is correct and that the " +
                "deployment endpoint is accessible from this machine.", ex);
        }
    }

    private static string GetImageMediaType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png"            => "image/png",
            ".gif"            => "image/gif",
            ".webp"           => "image/webp",
            _                 => "image/jpeg"
        };
}
