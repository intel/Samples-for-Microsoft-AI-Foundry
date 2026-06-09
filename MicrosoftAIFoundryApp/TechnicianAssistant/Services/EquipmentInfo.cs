namespace TechnicianAssistant.Services;

public class EquipmentInfo
{
    public string? ModelNumber  { get; set; }
    public string? SerialNumber { get; set; }
    public string? Manufacturer { get; set; }

    /// <summary>True when both model and serial number were found.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ModelNumber) &&
        !string.IsNullOrWhiteSpace(SerialNumber);

    /// <summary>Human-readable label indicating how the values were obtained.</summary>
    public string ExtractionSource { get; set; } = string.Empty;
}
