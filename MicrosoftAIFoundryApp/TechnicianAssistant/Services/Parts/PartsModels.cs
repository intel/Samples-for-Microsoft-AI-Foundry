using System.Collections.Generic;

namespace TechnicianAssistant.Services.Parts;

/// <summary>Identifies a specific replacement part with its specifications and safety notes.</summary>
public sealed class PartDetails
{
    public string PartNumber { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Dictionary<string, string> Specifications { get; set; } = new();
    public bool IsOEM { get; set; }
    public List<string> CriticalSpecs { get; set; } = new();
    public List<string> SafetyNotes { get; set; } = new();
    public string? CompatibilityNotes { get; set; }
    public decimal EstimatedPrice { get; set; }
}

/// <summary>Stock availability for a part across one or more supplier locations.</summary>
public sealed class InventoryStatus
{
    public bool InStock { get; set; }
    public int Quantity { get; set; }
    public string[] Locations { get; set; } = [];
    public System.TimeSpan EstimatedDelivery { get; set; }
    public System.DateTime LastUpdated { get; set; }
}

/// <summary>Contact and availability details for a single supplier.</summary>
public sealed class SupplierInfo
{
    public string Name { get; set; } = string.Empty;
    public string Distance { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Hours { get; set; } = string.Empty;
    public bool HasPart { get; set; }
    public string[] DeliveryOptions { get; set; } = [];
}

/// <summary>A single price quote from one supplier.</summary>
public sealed class PriceOption
{
    public string Supplier { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string DeliveryTime { get; set; } = string.Empty;
    public decimal DeliveryCost { get; set; }
    public decimal TotalCost => Price + DeliveryCost;
}

/// <summary>Aggregated pricing data across all suppliers for a given part.</summary>
public sealed class PricingComparison
{
    public string PartNumber { get; set; } = string.Empty;
    public PriceOption[] Prices { get; set; } = [];
    public string BestValue { get; set; } = string.Empty;
    public string FastestDelivery { get; set; } = string.Empty;
    public System.DateTime LastUpdated { get; set; }
}

/// <summary>Warranty coverage status for a piece of equipment.</summary>
public sealed class WarrantyStatus
{
    public bool IsUnderWarranty { get; set; }
    public string WarrantyType { get; set; } = string.Empty;
    public System.DateTime ExpirationDate { get; set; }
    public string? ContactInfo { get; set; }
    public string Advice { get; set; } = string.Empty;
    public string? ClaimProcess { get; set; }
}

/// <summary>Labor cost estimate for fitting the replacement part.</summary>
public sealed class LaborCostEstimate
{
    public double EstimatedHours { get; set; }
    public decimal HourlyRate { get; set; }
    public decimal TripCharge { get; set; }
    public decimal LaborTotal => (decimal)EstimatedHours * HourlyRate;
    public decimal GrandTotal => LaborTotal + TripCharge;
    public string Currency { get; set; } = "USD";
    public string DifficultyRating { get; set; } = string.Empty;
    public string Rationale { get; set; } = string.Empty;
}

/// <summary>A single ordering option presented to the technician.</summary>
public sealed class OrderOption
{
    public string Supplier { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string DeliveryTime { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    /// <summary>Human-readable label such as "Best Price" or "Fastest Delivery".</summary>
    public string Recommendation { get; set; } = string.Empty;
    /// <summary>Lower value = higher priority in the presented list.</summary>
    public int Priority { get; set; }
}

/// <summary>The fully resolved ordering plan returned to the caller.</summary>
public sealed class PartsOrderPlan
{
    public PartDetails? PrimaryPart { get; set; }
    public List<OrderOption> Options { get; set; } = new();
    public string Recommendations { get; set; } = string.Empty;
    public string? WarrantyAdvice { get; set; }
    public string UrgencyAssessment { get; set; } = string.Empty;
    public List<string> ProactiveRecommendations { get; set; } = new();
    public LaborCostEstimate? LaborCost { get; set; }
    /// <summary>Best parts price + labor grand total, or null if either is unavailable.</summary>
    public decimal? TotalRepairCost { get; set; }
}

/// <summary>Intermediate findings accumulated during autonomous research.</summary>
public sealed class PartsResearchFindings
{
    public PartDetails? PrimaryPart { get; set; }
    public List<PartDetails> AlternativeParts { get; set; } = new();
    public InventoryStatus? Availability { get; set; }
    public List<SupplierInfo> Suppliers { get; set; } = new();
    public PricingComparison? Pricing { get; set; }
    public WarrantyStatus? WarrantyStatus { get; set; }
    public LaborCostEstimate? LaborCost { get; set; }
}

/// <summary>An autonomous research plan produced by the LLM before tool execution begins.</summary>
public sealed class ResearchPlan
{
    public List<ResearchStep> Steps { get; set; } = new();
    public string UrgencyLevel { get; set; } = string.Empty;
    public List<string> CriticalFactors { get; set; } = new();
}

/// <summary>A single step within a <see cref="ResearchPlan"/>.</summary>
public sealed class ResearchStep
{
    public string Tool { get; set; } = string.Empty;
    public int Priority { get; set; }
    public string Reason { get; set; } = string.Empty;
}
