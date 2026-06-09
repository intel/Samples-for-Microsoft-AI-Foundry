using System;

namespace TechnicianAssistant.ViewModels;

/// <summary>
/// Carries the model and serial number values extracted by OCR + LLM,
/// passed to the code-behind so it can display a confirmation dialog.
/// </summary>
public sealed class EquipmentExtractionEventArgs(string? modelNumber, string? serialNumber) : EventArgs
{
    public string? ModelNumber  { get; } = modelNumber;
    public string? SerialNumber { get; } = serialNumber;
}
