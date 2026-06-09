using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TechnicianAssistant.McpServer.Models;

namespace TechnicianAssistant.McpServer.Data;

/// <summary>Seeds the database with representative HVAC parts, suppliers, and warranty data.</summary>
public static class DbSeeder
{
    public static async Task SeedAsync(PartsDbContext db)
    {
        await db.Database.EnsureCreatedAsync();

        // ?? Incremental records — always run regardless of whether the DB was seeded ??
        await EnsureWarrantyRecordAsync(db,
            serialNumber      : "W581827366",
            modelNumber       : "RA1430AJ1NA",
            manufacturer      : "Arctic-Pro",
            installDate       : DateTime.UtcNow.AddYears(-2),
            warrantyTermYears : 10,
            warrantyType      : "Parts & Labor",
            contactInfo       : "1-800-432-8373",
            claimProcess      : "Call manufacturer with model and serial number");

        await EnsureWarrantyRecordAsync(db,
            serialNumber      : "W491827365",
            modelNumber       : "RA1430AJ1NA",
            manufacturer      : "Arctic-Pro",
            installDate       : DateTime.UtcNow.AddYears(-12),
            warrantyTermYears : 10,
            warrantyType      : "Parts & Labor",
            contactInfo       : "1-800-432-8373",
            claimProcess      : "Call manufacturer with model and serial number");

        await UpdateManufacturersAsync(db);

        if (await db.Parts.AnyAsync()) return; // Full seed already applied

        // ?? Suppliers ??????????????????????????????????????????????????????
        var localHvac = new Supplier
        {
            Name            = "Local HVAC Supply",
            Distance        = "2.3 miles",
            Phone           = "(555) 123-4567",
            Hours           = "7 AM - 5 PM Mon-Fri, 8 AM - 2 PM Sat",
            Region          = "local",
            DeliveryOptions = ["Pickup", "Same-day delivery ($25)"]
        };
        var johnstone = new Supplier
        {
            Name            = "Johnstone Supply",
            Distance        = "4.1 miles",
            Phone           = "(555) 234-5678",
            Hours           = "6 AM - 5 PM Mon-Fri, 7 AM - 12 PM Sat",
            Region          = "local",
            DeliveryOptions = ["Pickup", "4-hour delivery ($35)", "Next-day free"]
        };
        var amazon = new Supplier
        {
            Name            = "Amazon Business",
            Distance        = "Online",
            Phone           = "Online ordering",
            Hours           = "24/7 online",
            Region          = "online",
            DeliveryOptions = ["Next-day delivery", "2-day free shipping"]
        };
        var ferguson = new Supplier
        {
            Name            = "Ferguson HVAC",
            Distance        = "6.8 miles",
            Phone           = "(555) 345-6789",
            Hours           = "7 AM - 5 PM Mon-Fri",
            Region          = "local",
            DeliveryOptions = ["Pickup", "Same-day delivery"]
        };
        db.Suppliers.AddRange(localHvac, johnstone, amazon, ferguson);

        // ?? Parts ??????????????????????????????????????????????????????????
        var cap45 = new Part
        {
            PartNumber        = "CAP-45-5-440",
            Description       = "Dual Run Capacitor 45/5 MFD 440V",
            IsOEM             = true,
            BasePrice         = 18.00m,
            VoltageRating     = "440V",
            CapacitanceRating = "45/5 MFD",
            PartType          = "Capacitor",
            CriticalSpecs     = ["Voltage must match exactly", "Capacitance tolerance ±6%"],
            SafetyNotes       = ["Discharge capacitor before handling", "Use insulated tools"]
        };
        var contactor = new Part
        {
            PartNumber    = "CONT-30A-24V",
            Description   = "Single Pole Contactor 30A 24V Coil",
            IsOEM         = false,
            BasePrice     = 22.00m,
            PartType      = "Contactor",
            CriticalSpecs = ["Amperage rating must match", "Coil voltage must be 24V"],
            SafetyNotes   = ["Disconnect power before replacement", "Verify coil voltage"]
        };
        var txv = new Part
        {
            PartNumber    = "TXV-R410A-3T",
            Description   = "Thermostatic Expansion Valve R-410A 3 Ton",
            IsOEM         = true,
            BasePrice     = 85.00m,
            PartType      = "TXV",
            CriticalSpecs = ["Refrigerant type must match", "Tonnage rating critical"],
            SafetyNotes   = ["EPA 608 certification required", "Recover refrigerant before replacement"]
        };
        var blowerMotor = new Part
        {
            PartNumber    = "BM-1HP-208-230",
            Description   = "ECM Blower Motor 1HP 208/230V",
            IsOEM         = false,
            BasePrice     = 210.00m,
            PartType      = "Blower Motor",
            CriticalSpecs = ["Horsepower must match", "Voltage must match", "RPM range compatible"],
            SafetyNotes   = ["Disconnect power before replacement", "Verify rotation direction"]
        };
        db.Parts.AddRange(cap45, contactor, txv, blowerMotor);

        // ?? Compatibility ??????????????????????????????????????????????????
        db.PartCompatibilities.AddRange(
            new PartCompatibility { Part = cap45,      ModelNumber = "RA1430AJ1NA", Manufacturer = "Rheem" },
            new PartCompatibility { Part = cap45,      ModelNumber = "13AJA30A01",  Manufacturer = "Carrier" },
            new PartCompatibility { Part = cap45,      ModelNumber = "TSA130E3",    Manufacturer = "Trane" },
            new PartCompatibility { Part = contactor,  ModelNumber = "RA1430AJ1NA", Manufacturer = "Rheem" },
            new PartCompatibility { Part = contactor,  ModelNumber = "TSA130E3",    Manufacturer = "Trane" },
            new PartCompatibility { Part = txv,        ModelNumber = "4TTR3036",    Manufacturer = "Trane" },
            new PartCompatibility { Part = blowerMotor,ModelNumber = "RA1430AJ1NA", Manufacturer = "Rheem" }
        );

        // ?? Supplier Stock ?????????????????????????????????????????????????
        db.SupplierStock.AddRange(
            new SupplierStock { Supplier = localHvac, Part = cap45,      Quantity = 12, EstimatedDelivery = TimeSpan.FromHours(2),  LastUpdated = DateTime.UtcNow },
            new SupplierStock { Supplier = johnstone,  Part = cap45,      Quantity = 8,  EstimatedDelivery = TimeSpan.FromHours(4),  LastUpdated = DateTime.UtcNow },
            new SupplierStock { Supplier = amazon,     Part = cap45,      Quantity = 50, EstimatedDelivery = TimeSpan.FromDays(1),   LastUpdated = DateTime.UtcNow },
            new SupplierStock { Supplier = johnstone,  Part = contactor,  Quantity = 5,  EstimatedDelivery = TimeSpan.FromHours(4),  LastUpdated = DateTime.UtcNow },
            new SupplierStock { Supplier = ferguson,   Part = contactor,  Quantity = 3,  EstimatedDelivery = TimeSpan.FromHours(4),  LastUpdated = DateTime.UtcNow },
            new SupplierStock { Supplier = amazon,     Part = contactor,  Quantity = 30, EstimatedDelivery = TimeSpan.FromDays(1),   LastUpdated = DateTime.UtcNow },
            new SupplierStock { Supplier = ferguson,   Part = txv,        Quantity = 2,  EstimatedDelivery = TimeSpan.FromHours(4),  LastUpdated = DateTime.UtcNow },
            new SupplierStock { Supplier = localHvac,  Part = blowerMotor,Quantity = 1,  EstimatedDelivery = TimeSpan.FromHours(2),  LastUpdated = DateTime.UtcNow }
        );

        // ?? Supplier Pricing ???????????????????????????????????????????????
        db.SupplierPricing.AddRange(
            new SupplierPricing { Supplier = localHvac, PartNumber = "CAP-45-5-440", Price = 45.00m, DeliveryCost = 0m,    DeliveryTime = "Immediate", LastUpdated = DateTime.UtcNow },
            new SupplierPricing { Supplier = amazon,    PartNumber = "CAP-45-5-440", Price = 38.00m, DeliveryCost = 0m,    DeliveryTime = "Next Day",  LastUpdated = DateTime.UtcNow },
            new SupplierPricing { Supplier = johnstone, PartNumber = "CAP-45-5-440", Price = 42.00m, DeliveryCost = 25m,   DeliveryTime = "Same Day",  LastUpdated = DateTime.UtcNow },
            new SupplierPricing { Supplier = ferguson,  PartNumber = "CAP-45-5-440", Price = 47.00m, DeliveryCost = 0m,    DeliveryTime = "Same Day",  LastUpdated = DateTime.UtcNow },
            new SupplierPricing { Supplier = johnstone, PartNumber = "CONT-30A-24V", Price = 28.00m, DeliveryCost = 25m,   DeliveryTime = "Same Day",  LastUpdated = DateTime.UtcNow },
            new SupplierPricing { Supplier = amazon,    PartNumber = "CONT-30A-24V", Price = 22.00m, DeliveryCost = 0m,    DeliveryTime = "Next Day",  LastUpdated = DateTime.UtcNow },
            new SupplierPricing { Supplier = ferguson,  PartNumber = "CONT-30A-24V", Price = 30.00m, DeliveryCost = 0m,    DeliveryTime = "Same Day",  LastUpdated = DateTime.UtcNow },
            new SupplierPricing { Supplier = ferguson,  PartNumber = "TXV-R410A-3T", Price = 95.00m, DeliveryCost = 0m,    DeliveryTime = "Same Day",  LastUpdated = DateTime.UtcNow },
            new SupplierPricing { Supplier = localHvac, PartNumber = "BM-1HP-208-230",Price = 220.00m,DeliveryCost = 0m,   DeliveryTime = "Immediate", LastUpdated = DateTime.UtcNow }
        );

        // ?? Warranty Records ???????????????????????????????????????????????
        db.WarrantyRecords.AddRange(
            new WarrantyRecord
            {
                SerialNumber      = "SN9876543",
                ModelNumber       = "RA1430AJ1NA",
                Manufacturer      = "Arctic-Pro",
                InstallDate       = DateTime.UtcNow.AddYears(-3),
                WarrantyTermYears = 10,
                WarrantyType      = "Parts & Labor",
                ContactInfo       = "1-800-432-8373",
                ClaimProcess      = "Call manufacturer with model and serial number"
            },
            new WarrantyRecord
            {
                SerialNumber      = "W581827366",
                ModelNumber       = "RA1430AJ1NA",
                Manufacturer      = "Arctic-Pro",
                InstallDate       = DateTime.UtcNow.AddYears(-2),
                WarrantyTermYears = 10,
                WarrantyType      = "Parts & Labor",
                ContactInfo       = "1-800-432-8373",
                ClaimProcess      = "Call manufacturer with model and serial number"
            },
            new WarrantyRecord
            {
                SerialNumber      = "W491827365",
                ModelNumber       = "RA1430AJ1NA",
                Manufacturer      = "Arctic-Pro",
                InstallDate       = DateTime.UtcNow.AddYears(-12),
                WarrantyTermYears = 10,
                WarrantyType      = "Parts & Labor",
                ContactInfo       = "1-800-432-8373",
                ClaimProcess      = "Call manufacturer with model and serial number"
            },
            new WarrantyRecord
            {
                SerialNumber      = "SN1234567",
                ModelNumber       = "TSA130E3",
                Manufacturer      = "Arctic-Pro",
                InstallDate       = DateTime.UtcNow.AddYears(-7),
                WarrantyTermYears = 5,
                WarrantyType      = "Parts Only",
                ContactInfo       = "1-800-554-6413",
                ClaimProcess      = "Submit claim via Arctic-Pro dealer portal"
            }
        );

        // ?? Labor Rates ????????????????????????????????????????????????????
        db.LaborRates.AddRange(
            new LaborRate { Region = "default", ComponentType = "Capacitor",    EstimatedHours = 0.5,  DifficultyRating = "Easy",     Rationale = "Accessible side panel; straightforward swap with system test." },
            new LaborRate { Region = "default", ComponentType = "Contactor",    EstimatedHours = 1.0,  DifficultyRating = "Easy",     Rationale = "Standard access; disconnect and reconnect wiring." },
            new LaborRate { Region = "default", ComponentType = "TXV",          EstimatedHours = 3.0,  DifficultyRating = "Hard",     Rationale = "Refrigerant recovery required; brazing involved." },
            new LaborRate { Region = "default", ComponentType = "Blower Motor", EstimatedHours = 2.0,  DifficultyRating = "Moderate", Rationale = "Remove blower assembly; align new motor." },
            new LaborRate { Region = "default", ComponentType = "Compressor",   EstimatedHours = 6.0,  DifficultyRating = "Expert",   Rationale = "Full refrigerant recovery; electrical and refrigerant work." },
            new LaborRate { Region = "default", ComponentType = "default",      EstimatedHours = 1.5,  DifficultyRating = "Moderate", Rationale = "Standard component replacement." }
        );

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Updates all existing warranty records to use Arctic-Pro as the manufacturer.
    /// Safe to call repeatedly — only writes if records still have the old value.
    /// </summary>
    private static async Task UpdateManufacturersAsync(PartsDbContext db)
    {
        var records = await db.WarrantyRecords
            .Where(w => w.Manufacturer != "Arctic-Pro")
            .ToListAsync();

        if (records.Count == 0) return;

        foreach (var r in records)
            r.Manufacturer = "Arctic-Pro";

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Inserts a warranty record only if one with the same serial number does not already exist.
    /// Safe to call on both a fresh and a previously seeded database.
    /// </summary>
    private static async Task EnsureWarrantyRecordAsync(
        PartsDbContext db,
        string serialNumber,
        string modelNumber,
        string manufacturer,
        DateTime installDate,
        int warrantyTermYears,
        string warrantyType,
        string contactInfo,
        string claimProcess)
    {
        var exists = await db.WarrantyRecords.AnyAsync(w => w.SerialNumber == serialNumber);
        if (exists) return;

        db.WarrantyRecords.Add(new WarrantyRecord
        {
            SerialNumber      = serialNumber,
            ModelNumber       = modelNumber,
            Manufacturer      = manufacturer,
            InstallDate       = installDate,
            WarrantyTermYears = warrantyTermYears,
            WarrantyType      = warrantyType,
            ContactInfo       = contactInfo,
            ClaimProcess      = claimProcess
        });

        await db.SaveChangesAsync();
    }
}
