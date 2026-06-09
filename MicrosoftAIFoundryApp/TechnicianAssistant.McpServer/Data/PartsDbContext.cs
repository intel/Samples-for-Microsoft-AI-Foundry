using System.Collections.Generic;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TechnicianAssistant.McpServer.Models;

namespace TechnicianAssistant.McpServer.Data;

public class PartsDbContext(DbContextOptions<PartsDbContext> options) : DbContext(options)
{
    public DbSet<Part> Parts => Set<Part>();
    public DbSet<PartCompatibility> PartCompatibilities => Set<PartCompatibility>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<SupplierStock> SupplierStock => Set<SupplierStock>();
    public DbSet<SupplierPricing> SupplierPricing => Set<SupplierPricing>();
    public DbSet<WarrantyRecord> WarrantyRecords => Set<WarrantyRecord>();
    public DbSet<LaborRate> LaborRates => Set<LaborRate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Serialise List<string> fields as JSON text columns
        var stringListConverter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<List<string>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new());

        modelBuilder.Entity<Part>(e =>
        {
            e.Property(p => p.CriticalSpecs).HasConversion(stringListConverter);
            e.Property(p => p.SafetyNotes).HasConversion(stringListConverter);
        });

        modelBuilder.Entity<Supplier>(e =>
        {
            e.Property(s => s.DeliveryOptions).HasConversion(stringListConverter);
        });
    }
}
