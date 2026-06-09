namespace TechnicianAssistant.Services;

/// <summary>
/// Represents an image or audio file that the user has attached to a prompt.
/// Image attachments are forwarded to the cloud model as vision blocks.
/// Audio attachments are transcribed locally and sent as text context.
/// </summary>
public sealed class PromptAttachment
{
    public enum AttachmentKind { Image, Audio }

    public AttachmentKind Kind { get; init; }
    public string FileName { get; init; } = string.Empty;
    public byte[] Data { get; init; } = [];

    /// <summary>Populated for audio attachments after Whisper transcription completes.</summary>
    public string? AudioTranscript { get; set; }

    public string DisplayLabel => Kind switch
    {
        AttachmentKind.Audio when AudioTranscript != null => $"?? {FileName} (transcribed)",
        AttachmentKind.Audio                              => $"?? {FileName}",
        _                                                 => $"??? {FileName}"
    };
}
