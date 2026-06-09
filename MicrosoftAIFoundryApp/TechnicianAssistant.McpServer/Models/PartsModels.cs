using System;
using System.Collections.Generic;

namespace TechnicianAssistant.McpServer.Models;

public class Part
{
    public int Id { get; set; }
    public string PartNumber { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsOEM { get; set; }
    public string? CompatibilityNotes { get; set; }
    public decimal BasePrice { get; set; }
    public string? VoltageRating { get; set; }
    public string? CapacitanceRating { get; set; }
    public string? PartType { get; set; }
    public List<string> CriticalSpecs { get; set; } = [];
    public List<string> SafetyNotes { get; set; } = [];
    public List<PartCompatibility> CompatibleModels { get; set; } = [];
    public List<SupplierStock> SupplierStock { get; set; } = [];
}

public class PartCompatibility
{
    public int Id { get; set; }
    public int PartId { get; set; }
    public Part Part { get; set; } = null!;
    public string ModelNumber { get; set; } = string.Empty;
    public string? Manufacturer { get; set; }
}

public class Supplier
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Distance { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Hours { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public List<string> DeliveryOptions { get; set; } = [];
    public List<SupplierStock> Stock { get; set; } = [];
    public List<SupplierPricing> Pricing { get; set; } = [];
}

public class SupplierStock
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;
    public int PartId { get; set; }
    public Part Part { get; set; } = null!;
    public int Quantity { get; set; }
    public DateTime LastUpdated { get; set; }
    public TimeSpan EstimatedDelivery { get; set; }
}

public class SupplierPricing
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;
    public string PartNumber { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal DeliveryCost { get; set; }
    public string DeliveryTime { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; }
}

public class WarrantyRecord
{
    public int Id { get; set; }
    public string SerialNumber { get; set; } = string.Empty;
    public string ModelNumber { get; set; } = string.Empty;
    public string? Manufacturer { get; set; }
    public DateTime InstallDate { get; set; }
    public int WarrantyTermYears { get; set; }
    public string WarrantyType { get; set; } = string.Empty;
    public string? ContactInfo { get; set; }
    public string? ClaimProcess { get; set; }
}

public class LaborRate
{
    public int Id { get; set; }
    public string Region { get; set; } = string.Empty;
    public string ComponentType { get; set; } = string.Empty;
    public double EstimatedHours { get; set; }
    public string DifficultyRating { get; set; } = string.Empty;
    public string Rationale { get; set; } = string.Empty;
}
