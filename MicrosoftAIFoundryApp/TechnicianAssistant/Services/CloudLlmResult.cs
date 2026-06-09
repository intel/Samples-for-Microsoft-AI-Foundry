namespace TechnicianAssistant.Services;

/// <summary>
/// The answer and Bedrock token-usage counters returned by <see cref="Interfaces.ICloudLlmService"/>.
/// </summary>
/// <param name="Answer">The text produced by the model.</param>
/// <param name="InputTokens">Number of tokens consumed by the prompt (including history).</param>
/// <param name="OutputTokens">Number of tokens in the model's reply.</param>
public sealed record CloudLlmResult(string Answer, int InputTokens, int OutputTokens);
