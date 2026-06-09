using System;

namespace TechnicianAssistant.Services;

/// <summary>
/// Singleton that holds the currently identified equipment's model and serial number
/// so they are available across the whole application without copy/paste.
/// </summary>
public class EquipmentStateService
{
    private static readonly Lazy<EquipmentStateService> _instance =
        new(() => new EquipmentStateService());

    public static EquipmentStateService Instance => _instance.Value;

    private string? _modelNumber;
    private string? _serialNumber;

    public string? ModelNumber
    {
        get => _modelNumber;
        set { _modelNumber = value; StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    public string? SerialNumber
    {
        get => _serialNumber;
        set { _serialNumber = value; StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    public bool HasEquipmentInfo =>
        !string.IsNullOrWhiteSpace(ModelNumber) || !string.IsNullOrWhiteSpace(SerialNumber);

    /// <summary>Returns a compact summary string, e.g. "Model: XY-123 | Serial: SN9876".</summary>
    public string Summary
    {
        get
        {
            var model  = string.IsNullOrWhiteSpace(ModelNumber)  ? "—" : ModelNumber;
            var serial = string.IsNullOrWhiteSpace(SerialNumber) ? "—" : SerialNumber;
            return $"Model: {model}  |  Serial: {serial}";
        }
    }

    /// <summary>Fired on the calling thread whenever ModelNumber or SerialNumber changes.</summary>
    public event EventHandler? StateChanged;

    private EquipmentStateService() { }
}
