using System.Collections.Generic;
using System.Threading.Tasks;
using TechnicianAssistant.Services;

namespace TechnicianAssistant.Services.Parts.Tools;

/// <summary>Marker interface for all parts-research tools.</summary>
public interface IPartsTool
{
    string Name { get; }
}

/// <summary>Identifies the exact replacement part for a described failure.</summary>
public interface IPartIdentificationTool : IPartsTool
{
    Task<PartDetails> IdentifyPartAsync(string componentDescription, EquipmentInfo equipment);
}

/// <summary>Checks real-time stock levels for a given part number.</summary>
public interface IInventoryCheckTool : IPartsTool
{
    Task<InventoryStatus> CheckStockAsync(string partNumber);
}

/// <summary>Finds supplier locations, contact details and delivery options.</summary>
public interface ISupplierLookupTool : IPartsTool
{
    Task<List<SupplierInfo>> FindSuppliersAsync(string partNumber);
}

/// <summary>Retrieves and compares prices across all known suppliers.</summary>
public interface IPricingTool : IPartsTool
{
    Task<PricingComparison> ComparePricesAsync(string partNumber);
}

/// <summary>Identifies compatible alternative parts for a given primary part.</summary>
public interface ICompatibilityTool : IPartsTool
{
    Task<List<PartDetails>> FindAlternativesAsync(PartDetails primaryPart, EquipmentInfo equipment);
}

/// <summary>Determines warranty status for a given piece of equipment and failed component.</summary>
public interface IWarrantyCheckTool : IPartsTool
{
    Task<WarrantyStatus> CheckWarrantyAsync(EquipmentInfo equipment, string failedComponent);
}

/// <summary>Estimates labor hours and total labor cost for fitting a replacement part.</summary>
public interface ILaborCostTool : IPartsTool
{
    Task<LaborCostEstimate> EstimateLaborAsync(string componentDescription, EquipmentInfo equipment);
}
