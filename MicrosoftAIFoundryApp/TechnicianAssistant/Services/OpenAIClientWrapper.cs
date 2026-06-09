using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.ML.Tokenizers;
using OpenAI;
using OpenAI.Chat;

namespace TechnicianAssistant.Services;

public class LlmToolCall
{
    public string Id { get; set; } = string.Empty;
    public string FunctionName { get; set; } = string.Empty;
    public Dictionary<string, object> Arguments { get; set; } = new();
}

public class LlmGenerationResult
{
    public string Answer { get; set; } = string.Empty;
    public string ThinkingContent { get; set; } = string.Empty;
    public List<LlmToolCall> ToolCalls { get; set; } = new();
    public double TimeToFirstToken { get; set; }
    public double TokensPerSecond { get; set; }
    public int TotalTokens { get; set; }
    public double TotalTime { get; set; }
    /// <summary>Model self-reported confidence 0–100, or -1 if not provided.</summary>
    public double Confidence { get; set; } = -1;
}

public class OpenAIClientWrapper
{
    private readonly OpenAIClient _client;
    private readonly string _modelId;
    private readonly string _reasoningModelId;
    private readonly string _endpoint;
    private Action<string>? _logger;

    public OpenAIClientWrapper(string endpoint, string modelId, string reasoningModelId = "", string apiKey = "not-needed-for-local")
    {
        _endpoint = endpoint;
        _modelId = modelId;
        _reasoningModelId = string.IsNullOrWhiteSpace(reasoningModelId) ? modelId : reasoningModelId;

        _client = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions
        {
            //Endpoint = new Uri("http://127.0.0.1:11434" + "/v1")
            //Endpoint = new Uri("http://127.0.0.1:8080" + "/v1")
              Endpoint = new Uri(endpoint + "/v1")
        });
        Log("✅ OpenAI client configured:");
        Log($"📡 Endpoint: {endpoint}");
        Log($"🤖 Model: {modelId}");
        Log($"🧠 Reasoning Model: {_reasoningModelId}");
    }

    public void SetLogger(Action<string> logger)
    {
        _logger = logger;
    }

    private void Log(string message)
    {
        Console.WriteLine(message);
        _logger?.Invoke(message + "\n");
    }

    
    private static Tokenizer? _tokenizer;
    private static readonly object _tokenizerLock = new();

    public Task<int> CountPromptTokensAsync(string prompt, IReadOnlyList<ConversationTurn>? history = null, string? systemPrompt = null)
    {
        try
        {
            if (_tokenizer == null)
            {
                lock (_tokenizerLock)
                {
                    _tokenizer ??= TiktokenTokenizer.CreateForModel("gpt-4");
                }
            }

            const string defaultSystemPrompt =
                "You are a helpful technician assistant. Answer clearly and concisely based on the conversation so far and any provided technical documentation." +
                "\n\nYou MUST respond with a single JSON object and nothing else. Use this exact schema:\n" +
                "{\"answer\": \"<your full answer here>\", \"confidence\": <integer 0-100>}\n" +
                "The \"confidence\" field is your confidence percentage (0-100) in the accuracy and completeness of your answer given the available information. " +
                "Do not include any text outside the JSON object.";

            var fullText = new StringBuilder();
            fullText.AppendLine(systemPrompt ?? defaultSystemPrompt);

            if (history != null)
            {
                foreach (var turn in history)
                {
                    fullText.AppendLine(turn.Question);
                    fullText.AppendLine(turn.Answer);
                }
            }

            fullText.AppendLine(prompt);

            return Task.FromResult(_tokenizer.CountTokens(fullText.ToString()));
        }
        catch (Exception ex)
        {
            Log($"⚠️ Token count unavailable ({ex.Message}), using character estimate.");
            return Task.FromResult(prompt.Length / 4);
        }
    }

    /// <summary>
    /// Asks the LLM to copy the single most relevant passage verbatim from the
    /// documentation. Makes a direct raw API call — bypassing the answer/confidence
    /// JSON wrapper used by <see cref="GenerateAsync"/> — so the model output is
    /// the snippet itself with no intermediate parsing layer.
    /// Returns an empty string when nothing relevant was found or on any error.
    /// </summary>
    public async Task<string> ExtractKeySnippetAsync(string question, string ragContext)
    {
        const string systemPrompt =
            "You are a precise retrieval tool. " +
            "Find the single sentence or short passage (at most 3 sentences) in the DOCUMENTATION block that is most directly relevant to the QUESTION. " +
            "If you find relevant text: copy it character-for-character from the documentation — do NOT paraphrase or add any words. " +
            "If the documentation contains nothing useful for the question: output exactly the word NONE. " +
            "Output ONLY the copied passage or the word NONE. No JSON, no explanation, no other text.";

        var userContent =
            $"QUESTION: {question}\n\n" +
            $"DOCUMENTATION:\n{ragContext}";

        try
        {
            var chatClient = _client.GetChatClient(_modelId);
            var messages = new List<ChatMessage>
            {
                ChatMessage.CreateSystemMessage(systemPrompt),
                ChatMessage.CreateUserMessage(userContent)
            };
            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = 600,  // reasoning model needs room for <think> tokens before the snippet
                Temperature = 0f,
            };

            // Collect the raw streamed text — no JSON parsing
            var raw = new StringBuilder();
            await foreach (var update in chatClient.CompleteChatStreamingAsync(messages, options))
            {
                foreach (var part in update.ContentUpdate)
                {
                    if (!string.IsNullOrEmpty(part.Text))
                        raw.Append(part.Text);
                }
            }

            // Strip <think>…</think> blocks produced by reasoning models before
            // evaluating the answer — the snippet itself follows the closing tag.
            var answer = System.Text.RegularExpressions.Regex
                .Replace(raw.ToString(), @"<think>.*?</think>",
                         string.Empty,
                         System.Text.RegularExpressions.RegexOptions.Singleline)
                .Trim();

            Log($"📖 Snippet raw output: \"{(answer.Length > 80 ? answer[..80] + "…" : answer)}\"");

            return string.Equals(answer, "NONE", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : answer;
        }
        catch (Exception ex)
        {
            Log($"⚠️ Key-snippet extraction failed: {ex.Message}");
            return string.Empty;
        }
    }

    public async Task<LlmGenerationResult> GenerateAsync(
        string prompt,
        IReadOnlyList<ConversationTurn>? history = null,
        int maxTokens = 10000,
        float temperature = 0f,
        IEnumerable<ChatTool>? tools = null,
        Action<string>? onToken = null,
        Action<string>? onThinkToken = null,
        bool useReasoning = false,
        string? systemPrompt = null)
    {
        var stopwatch = Stopwatch.StartNew();
        double? firstTokenTime = null;
        int tokenCount = 0;
        int thinkingTokenCount = 0;
        var fullResponse = new StringBuilder();
        var thinkingContent = new StringBuilder();

        // ── <think> / </think> tag state machine ─────────────────────────────
        const string openTag  = "<think>";
        const string closeTag = "</think>";
        bool inThink = false;
        var lookAhead = new StringBuilder();

        void FlushLookAhead(bool final = false)
        {
            while (lookAhead.Length > 0)
            {
                var buf       = lookAhead.ToString();
                var searchTag = inThink ? closeTag : openTag;
                int tagPos    = buf.IndexOf(searchTag, StringComparison.OrdinalIgnoreCase);

                if (tagPos >= 0)
                {
                    // Dispatch text before the tag
                    if (tagPos > 0)
                    {
                        var before = buf[..tagPos];
                    if (inThink)
                        {
                            thinkingContent.Append(before);
                            onThinkToken?.Invoke(before);
                            thinkingTokenCount++;
                            firstTokenTime ??= stopwatch.Elapsed.TotalSeconds;
                        }
                        else
                        {
                            fullResponse.Append(before);
                            tokenCount++;
                            firstTokenTime ??= stopwatch.Elapsed.TotalSeconds;
                        }
                    }
                    inThink = !inThink;
                    lookAhead.Clear();
                    lookAhead.Append(buf[(tagPos + searchTag.Length)..]);
                    // Continue loop with remaining text after the tag
                }
                else
                {
                    if (final)
                    {
                        if (inThink) { thinkingContent.Append(buf); onThinkToken?.Invoke(buf); thinkingTokenCount++; firstTokenTime ??= stopwatch.Elapsed.TotalSeconds; }
                        else { fullResponse.Append(buf); tokenCount++; firstTokenTime ??= stopwatch.Elapsed.TotalSeconds; }
                        lookAhead.Clear();
                        break;
                    }

                    // Hold back any suffix that could be a partial tag match
                    int maxPartial = Math.Min(buf.Length, searchTag.Length - 1);
                    int partialLen = 0;
                    for (int pl = maxPartial; pl >= 1; pl--)
                    {
                        if (searchTag.StartsWith(buf[^pl..], StringComparison.OrdinalIgnoreCase))
                        { partialLen = pl; break; }
                    }

                    int safeLen = buf.Length - partialLen;
                    if (safeLen > 0)
                    {
                        var safe = buf[..safeLen];
                    if (inThink) { thinkingContent.Append(safe); onThinkToken?.Invoke(safe); thinkingTokenCount++; firstTokenTime ??= stopwatch.Elapsed.TotalSeconds; }
                        else { fullResponse.Append(safe); tokenCount++; firstTokenTime ??= stopwatch.Elapsed.TotalSeconds; }
                        lookAhead.Clear();
                        lookAhead.Append(buf[safeLen..]);
                    }
                    break; // wait for next token
                }
            }
        }
        // ─────────────────────────────────────────────────────────────────────

        // Accumulators for streaming tool calls (keyed by index)
        var toolCallIds = new Dictionary<int, string>();
        var toolCallNames = new Dictionary<int, string>();
        var toolCallArgs = new Dictionary<int, StringBuilder>();

        try
        {
            var activeModelId = useReasoning ? _reasoningModelId : _modelId;
            var chatClient = _client.GetChatClient(activeModelId);

            // Build the message list: system + previous turns + current prompt
            var defaultSystemPrompt =
                "You are a helpful technician assistant. Answer clearly and concisely based on the conversation so far and any provided technical documentation." +
                "\n\nYou MUST respond with a single JSON object and nothing else. Use this exact schema:\n" +
                "{\"answer\": \"<your full answer here>\", \"confidence\": <integer 0-100>}\n" +
                "The \"confidence\" field is your confidence percentage (0-100) in the accuracy and completeness of your answer given the available information. " +
                "Do not include any text outside the JSON object.";

            var messages = new List<ChatMessage>
            {
                ChatMessage.CreateSystemMessage(systemPrompt ?? defaultSystemPrompt)
            };

            // Inject prior turns so the model has full conversation context
            if (history != null)
            {
                foreach (var turn in history)
                {
                    messages.Add(ChatMessage.CreateUserMessage(turn.Question));
                    messages.Add(ChatMessage.CreateAssistantMessage(turn.Answer));
                }
            }

            messages.Add(ChatMessage.CreateUserMessage(prompt));

            Log("=== MAIN PROMPT START ===");
            Log($"[System]: {systemPrompt ?? defaultSystemPrompt}");
            if (history != null)
            {
                foreach (var turn in history)
                {
                    Log($"[User (history)]: {turn.Question}");
                    Log($"[Assistant (history)]: {turn.Answer}");
                }
            }
            Log($"[User]: {prompt}");
            Log("=== MAIN PROMPT END ===");

            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = maxTokens,
                Temperature = temperature
            };

  
            await foreach (var update in chatClient.CompleteChatStreamingAsync(messages, options))
            {
                // Accumulate text content — route through the think state machine
                foreach (var contentPart in update.ContentUpdate)
                {
                    var content = contentPart.Text;
                    if (!string.IsNullOrEmpty(content))
                    {
                        lookAhead.Append(content);
                        FlushLookAhead();
                    }
                }

                // Accumulate tool call updates
                foreach (var toolCallUpdate in update.ToolCallUpdates)
                {
                    int idx = toolCallUpdate.Index;

                    if (!toolCallArgs.ContainsKey(idx))
                    {
                        toolCallArgs[idx] = new StringBuilder();
                        toolCallIds[idx] = toolCallUpdate.ToolCallId ?? string.Empty;
                        toolCallNames[idx] = toolCallUpdate.FunctionName ?? string.Empty;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(toolCallUpdate.ToolCallId))
                            toolCallIds[idx] = toolCallUpdate.ToolCallId;
                        if (!string.IsNullOrEmpty(toolCallUpdate.FunctionName))
                            toolCallNames[idx] = toolCallUpdate.FunctionName;
                    }

                    toolCallArgs[idx].Append(toolCallUpdate.FunctionArgumentsUpdate?.ToString() ?? string.Empty);

                    if (!firstTokenTime.HasValue)
                        firstTokenTime = stopwatch.Elapsed.TotalSeconds;

                    tokenCount++;
                }
            }

            // Flush any remaining buffered text
            FlushLookAhead(final: true);

            stopwatch.Stop();
            var ttft = firstTokenTime ?? 0;
            var generationTime = stopwatch.Elapsed.TotalSeconds - ttft;
            var tokensPerSec = generationTime > 0 ? tokenCount / generationTime : 0;

            // ── Post-process: recover thinking content the streaming state machine missed ──
            // If <think>…</think> blocks are still present in fullResponse it means the
            // state machine didn't detect them during streaming (e.g. tag split across chunks
            // in an unusual pattern). Extract and move them to thinkingContent now.
            var rawFull = fullResponse.ToString();
            var thinkPattern = new System.Text.RegularExpressions.Regex(
                @"<think>(.*?)</think>",
                System.Text.RegularExpressions.RegexOptions.Singleline |
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (thinkPattern.IsMatch(rawFull))
            {
                foreach (System.Text.RegularExpressions.Match m in thinkPattern.Matches(rawFull))
                {
                    var recovered = m.Groups[1].Value;
                    thinkingContent.Append(recovered);
                    thinkingTokenCount += Math.Max(1, recovered.Length / 4);
                    // Fire the callback so the UI can display the thinking content
                    onThinkToken?.Invoke(recovered);
                    Log($"🧠 Recovered {recovered.Length} chars of thinking content from answer stream");
                }
                // Strip all think blocks from the answer stream
                fullResponse.Clear();
                fullResponse.Append(thinkPattern.Replace(rawFull, string.Empty).Trim());
            }

            // Build structured tool call results
            var llmToolCalls = toolCallIds.Keys.OrderBy(i => i).Select(i => new LlmToolCall
            {
                Id = toolCallIds[i],
                FunctionName = toolCallNames.GetValueOrDefault(i, string.Empty),
                Arguments = JsonSerializer.Deserialize<Dictionary<string, object>>(
                    toolCallArgs[i].Length > 0 ? toolCallArgs[i].ToString() : "{}")
                    ?? new()
            }).ToList();

            // Parse the JSON response to extract the answer and confidence.
            var rawResponse = fullResponse.ToString().Trim();
            Log($"=== MODEL RAW RESPONSE START ===\n{rawResponse}\n=== MODEL RAW RESPONSE END ===");
            string answerText = rawResponse;
            double confidence = -1;
            try
            {
                // Strip markdown fences if the model wraps the JSON
                var jsonStr = rawResponse;
                if (jsonStr.StartsWith("```", StringComparison.Ordinal))
                {
                    var start = jsonStr.IndexOf('{');
                    var end   = jsonStr.LastIndexOf('}');
                    if (start >= 0 && end > start)
                        jsonStr = jsonStr[start..(end + 1)];
                }
                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;
                if (root.TryGetProperty("answer", out var ap))
                    answerText = ap.GetString() ?? rawResponse;
                if (root.TryGetProperty("confidence", out var cp))
                    confidence = cp.GetDouble();
            }
            catch
            {
                // If JSON parsing fails, use the raw response as-is
                answerText = rawResponse;
            }

            // If the model didn't return a usable confidence value, ask it for one now
            // using only the answer it already produced — cheap single-shot follow-up.
            if (confidence < 0 && !string.IsNullOrWhiteSpace(answerText))
            {
                try
                {
                    // Use the standard model (not the reasoning model) for the confidence
                    // follow-up — the reasoning model's <think> tokens consume the tight
                    // token budget before the actual integer is ever emitted.
                    var followUpClient = _client.GetChatClient(_modelId);
                    var followUpSystemPrompt =
                        "You are a self-evaluation tool. " +
                        "Given the ANSWER below, output ONLY a single integer between 0 and 100 representing " +
                        "your confidence that the answer is accurate and complete. No other text.";
                    var followUpUserPrompt = $"ANSWER:\n{answerText}";

                    Log("=== CONFIDENCE FOLLOW-UP PROMPT START ===");
                    Log($"[System]: {followUpSystemPrompt}");
                    Log($"[User]: {followUpUserPrompt}");
                    Log("=== CONFIDENCE FOLLOW-UP PROMPT END ===");

                    var followUpMessages = new List<ChatMessage>
                    {
                        ChatMessage.CreateSystemMessage(followUpSystemPrompt),
                        ChatMessage.CreateUserMessage(followUpUserPrompt)
                    };
                    var followUpOptions = new ChatCompletionOptions { MaxOutputTokenCount = 200, Temperature = 0f };

                    var followUpRaw = new StringBuilder();
                    await foreach (var u in followUpClient.CompleteChatStreamingAsync(followUpMessages, followUpOptions))
                        foreach (var p in u.ContentUpdate)
                            if (!string.IsNullOrEmpty(p.Text)) followUpRaw.Append(p.Text);

                    var followUpText = System.Text.RegularExpressions.Regex
                        .Replace(followUpRaw.ToString(), @"<think>.*?</think>", string.Empty,
                                 System.Text.RegularExpressions.RegexOptions.Singleline)
                        .Trim();

                    Log($"=== CONFIDENCE FOLLOW-UP RESPONSE: \"{followUpText}\" ===");

                    if (double.TryParse(followUpText, out var parsed) && parsed >= 0 && parsed <= 100)
                        confidence = parsed;
                }
                catch
                {
                    // Follow-up failed — confidence stays -1, no auto-escalation will trigger
                }
            }
            // Forward the clean answer to the streaming callback now that it is extracted
            if (!string.IsNullOrEmpty(answerText))
                onToken?.Invoke(answerText);

            var result = new LlmGenerationResult
            {
                Answer = answerText,
                ThinkingContent = thinkingContent.ToString(),
                ToolCalls = llmToolCalls,
                TimeToFirstToken = ttft,
                TokensPerSecond = tokensPerSec,
                TotalTokens = tokenCount,
                TotalTime = stopwatch.Elapsed.TotalSeconds,
                Confidence = confidence
            };

            if (result.Answer.Length == 0 && result.ThinkingContent.Length > 0)
                Log($"⚠️ Reasoning model produced {result.ThinkingContent.Length} chars of thinking but no final answer. " +
                    $"Consider increasing maxTokens (current: {maxTokens}) or simplifying the prompt.");

            var confidenceLabel = confidence >= 0 ? $" | 🎯 Confidence: {confidence:F0}%" : string.Empty;
            Log($"⏱️ LLM inference: {result.TotalTime:F2}s total | " +
                $"TTFT {result.TimeToFirstToken:F2}s | " +
                $"{result.TokensPerSecond:F1} tok/s | " +
                $"{result.TotalTokens} answer tokens | {thinkingTokenCount} thinking tokens{confidenceLabel}");

            return result;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"LLM API error ({_endpoint}): {ex.Message}", ex);
        }
    }
}
