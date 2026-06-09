using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using TechnicianAssistant.McpServer.Data;
using TechnicianAssistant.McpServer.Models;

namespace TechnicianAssistant.McpServer.Tools;

[McpServerToolType]
public static class WarrantyCheckTool
{
    [McpServerTool(Name = "check_warranty")]
    [System.ComponentModel.Description("Checks warranty status for a piece of equipment by serial number and model number. Returns whether it is under warranty, the warranty type, expiration date, and claims process.")]
    public static async Task<object> CheckWarrantyAsync(
        PartsDbContext db,
        string serialNumber,
        string modelNumber)
    {
        // If a serial number is provided, match ONLY on serial number.
        // A serial number uniquely identifies a single unit — falling back to model number
        // when a serial was given would return another unit's warranty record, which is wrong.
        // Model number fallback is only used when no serial number is provided at all.
        WarrantyRecord? record;
        string matchedBy;

        if (!string.IsNullOrWhiteSpace(serialNumber))
        {
            record    = await db.WarrantyRecords.FirstOrDefaultAsync(w => w.SerialNumber == serialNumber);
            matchedBy = record != null ? "serial number" : "none (serial not found in database)";
        }
        else if (!string.IsNullOrWhiteSpace(modelNumber))
        {
            record    = await db.WarrantyRecords.FirstOrDefaultAsync(w => w.ModelNumber.ToLower() == modelNumber.ToLower());
            matchedBy = record != null ? "model number (no serial provided)" : "none";
        }
        else
        {
            record    = null;
            matchedBy = "none (no serial or model provided)";
        }

        if (record == null)
        {
            return new
            {
                isUnderWarranty = false,
                warrantyType    = "Unknown",
                matchedBy       = "none",
                advice          = $"No warranty record found for serial '{serialNumber}' or model '{modelNumber}'.",
                expirationDate  = (string?)null,
                contactInfo     = (string?)null,
                claimProcess    = (string?)null
            };
        }

        var expirationDate  = record.InstallDate.AddYears(record.WarrantyTermYears);
        var isUnderWarranty = DateTime.UtcNow < expirationDate;
        var daysRemaining   = (expirationDate - DateTime.UtcNow).Days;

        return new
        {
            isUnderWarranty,
            warrantyType   = record.WarrantyType,
            matchedBy,
            serialNumber   = record.SerialNumber,
            expirationDate = expirationDate.ToString("yyyy-MM-dd"),
            daysRemaining  = Math.Max(0, daysRemaining),
            contactInfo    = record.ContactInfo,
            claimProcess   = record.ClaimProcess,
            advice         = isUnderWarranty
                ? $"Equipment is under {record.WarrantyType} warranty until {expirationDate:yyyy-MM-dd}. Contact {record.ContactInfo} before purchasing parts."
                : $"Warranty expired on {expirationDate:yyyy-MM-dd}. Proceed with standard parts purchase."
        };
    }
}
