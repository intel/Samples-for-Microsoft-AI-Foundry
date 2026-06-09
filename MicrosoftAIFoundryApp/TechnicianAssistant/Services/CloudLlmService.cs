using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using TechnicianAssistant.Services.Interfaces;

namespace TechnicianAssistant.Services;

/// <summary>
/// Thrown when AWS credential resolution or authorisation fails for a Bedrock request.
/// </summary>
public sealed class CloudAuthenticationException : Exception
{
    public CloudAuthenticationException(string message, Exception inner)
        : base(message, inner) { }
}

/// <summary>
/// Cloud LLM implementation targeting AWS Bedrock via the native Converse API.
/// Supports all Claude models regardless of generation.
///
/// Configure in appsettings.json:
///   "CloudProvider": "AWS"
///   "CloudModelId":  "&lt;bedrock-model-id&gt;"  e.g. "us.anthropic.claude-3-5-sonnet-20241022-v2:0"
///   "AwsRegion":     "us-east-1"
///
/// Credentials are resolved from the standard AWS chain:
///   1. Environment variables (AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY)
///   2. ~/.aws/credentials file
///   3. IAM role
/// </summary>
public class CloudLlmService : ICloudLlmService
{
    private readonly string _modelId;
    private readonly AmazonBedrockRuntimeClient _client;

    public CloudLlmService(string modelId, string awsRegion)
    {
        _modelId = modelId;
        _client  = new AmazonBedrockRuntimeClient(RegionEndpoint.GetBySystemName(awsRegion));
        LoggingService.Instance.Log($"[CloudLlmService] AWS Bedrock (Converse API): region={awsRegion}, model={modelId}");
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
            var messages = new List<Message>();

            if (history != null)
            {
                foreach (var turn in history)
                {
                    messages.Add(new Message { Role = ConversationRole.User,      Content = [new ContentBlock { Text = turn.Question }] });
                    messages.Add(new Message { Role = ConversationRole.Assistant, Content = [new ContentBlock { Text = turn.Answer }] });
                }
            }

            var currentContent = new List<ContentBlock>();

            if (attachments != null && attachments.Count > 0)
            {
                foreach (var att in attachments.Where(a => a.Kind == PromptAttachment.AttachmentKind.Image))
                {
                    currentContent.Add(new ContentBlock
                    {
                        Image = new ImageBlock
                        {
                            Format = GetImageFormat(att.FileName),
                            Source = new ImageSource { Bytes = new MemoryStream(att.Data) }
                        }
                    });
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

                currentContent.Add(new ContentBlock { Text = fullText });
            }
            else
            {
                currentContent.Add(new ContentBlock { Text = prompt });
            }

            messages.Add(new Message { Role = ConversationRole.User, Content = currentContent });

            var inferenceConfig = new InferenceConfiguration { MaxTokens = maxTokens };
            if (temperature > 0f)
                inferenceConfig.Temperature = temperature;

            var request = new ConverseRequest
            {
                ModelId         = _modelId,
                Messages        = messages,
                InferenceConfig = inferenceConfig
            };

            LoggingService.Instance.Log($"[CloudLlmService] POST bedrock-runtime / model={_modelId}");
            var response = await _client.ConverseAsync(request);
            var answer   = response.Output.Message.Content[0].Text ?? string.Empty;

            onToken?.Invoke(answer);

            return new CloudLlmResult(
                answer,
                response.Usage?.InputTokens  ?? 0,
                response.Usage?.OutputTokens ?? 0);
        }
        catch (Amazon.Runtime.AmazonClientException ex)
            when (ex.Message.Contains("credentials", StringComparison.OrdinalIgnoreCase) ||
                  ex.Message.Contains("credential",  StringComparison.OrdinalIgnoreCase) ||
                  ex.Message.Contains("Unable to find", StringComparison.OrdinalIgnoreCase))
        {
            throw new CloudAuthenticationException(
                "AWS credentials not found. Configure via environment variables " +
                "(AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY), the AWS CLI ('aws configure'), " +
                "or an IAM role.", ex);
        }
        catch (Amazon.Runtime.AmazonServiceException ex)
            when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                  ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new CloudAuthenticationException(
                $"AWS Bedrock returned {(int)ex.StatusCode} {ex.StatusCode}. " +
                "Verify your credentials have the 'bedrock:InvokeModel' permission.", ex);
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Log($"[CloudLlmService] Exception: {ex.GetType().FullName}: {ex.Message}");
            if (ex.InnerException != null)
                LoggingService.Instance.Log($"[CloudLlmService] Inner: {ex.InnerException.Message}");
            throw;
        }
    }

    public async Task<CloudLlmResult> AnalyzeImageAsync(byte[] imageBytes, string question)
    {
        try
        {
            var request = new ConverseRequest
            {
                ModelId  = _modelId,
                Messages =
                [
                    new Message
                    {
                        Role = ConversationRole.User,
                        Content =
                        [
                            new ContentBlock
                            {
                                Image = new ImageBlock
                                {
                                    Format = ImageFormat.Jpeg,
                                    Source = new ImageSource { Bytes = new MemoryStream(imageBytes) }
                                }
                            },
                            new ContentBlock { Text = question }
                        ]
                    }
                ]
            };

            LoggingService.Instance.Log($"[CloudLlmService] AnalyzeImage: model={_modelId}");
            var response = await _client.ConverseAsync(request);

            return new CloudLlmResult(
                response.Output.Message.Content[0].Text ?? string.Empty,
                response.Usage?.InputTokens  ?? 0,
                response.Usage?.OutputTokens ?? 0);
        }
        catch (Amazon.Runtime.AmazonClientException ex)
            when (ex.Message.Contains("credentials", StringComparison.OrdinalIgnoreCase) ||
                  ex.Message.Contains("credential",  StringComparison.OrdinalIgnoreCase) ||
                  ex.Message.Contains("Unable to find", StringComparison.OrdinalIgnoreCase))
        {
            throw new CloudAuthenticationException(
                "AWS credentials not found. Configure via environment variables " +
                "(AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY), the AWS CLI ('aws configure'), " +
                "or an IAM role.", ex);
        }
        catch (Amazon.Runtime.AmazonServiceException ex)
            when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                  ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new CloudAuthenticationException(
                $"AWS Bedrock returned {(int)ex.StatusCode} {ex.StatusCode}. " +
                "Verify your credentials have the 'bedrock:InvokeModel' permission.", ex);
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Log($"[CloudLlmService] Exception: {ex.GetType().FullName}: {ex.Message}");
            throw;
        }
    }

    private static ImageFormat GetImageFormat(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ImageFormat.Jpeg,
            ".png"            => ImageFormat.Png,
            ".gif"            => ImageFormat.Gif,
            ".webp"           => ImageFormat.Webp,
            _                 => ImageFormat.Jpeg
        };
}


