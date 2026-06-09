namespace TechnicianAssistant.Services;

/// <summary>
/// Represents a single completed question-and-answer exchange in the conversation history.
/// </summary>
public sealed record ConversationTurn(string Question, string Answer);
