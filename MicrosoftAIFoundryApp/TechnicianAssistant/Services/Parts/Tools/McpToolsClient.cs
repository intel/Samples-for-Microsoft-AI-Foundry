using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using TechnicianAssistant.Services.Interfaces;

namespace TechnicianAssistant.Services.Parts.Tools;

/// <summary>
/// Wraps the MCP SDK client (<see cref="McpClient"/>) and provides a strongly-typed
/// helper for calling tools on the TechnicianAssistant.McpServer.
///
/// Connection lifecycle:
///   - <see cref="CreateAsync"/> must be called once to perform the MCP initialise
///     handshake before any tool calls are made.
///   - The underlying <see cref="McpClient"/> is disposed when this instance is disposed.
/// </summary>
public sealed class McpToolsClient : IAsyncDisposable
{
    private readonly McpClient _mcpClient;

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private McpToolsClient(McpClient mcpClient)
    {
        _mcpClient = mcpClient;
    }

    /// <summary>
    /// Creates and initialises an MCP client connected to <paramref name="serverUrl"/>
    /// using the Streamable HTTP transport (MCP spec 2025-11-25).
    /// </summary>
    public static async Task<McpToolsClient> CreateAsync(string serverUrl)
    {
        var httpClient = new HttpClient();
        var options    = new HttpClientTransportOptions
        {
            Endpoint = new Uri(serverUrl.TrimEnd('/') + "/mcp")
        };
        var transport  = new HttpClientTransport(options, httpClient, loggerFactory: null, ownsHttpClient: true);
        var mcpClient  = await McpClient.CreateAsync(transport);
        LoggingService.Instance.Log($"[MCP] Connected to server: {serverUrl}");
        return new McpToolsClient(mcpClient);
    }

    /// <summary>
    /// Calls a tool on the MCP server and deserialises the structured result.
    /// Uses <see cref="CallToolResult.StructuredContent"/> when present,
    /// falling back to the first <c>text</c> content block.
    /// </summary>
    public async Task<T> CallToolAsync<T>(string toolName, object arguments)
    {
        // Serialise the arguments object to a dictionary of JsonElement values
        // as required by the MCP SDK's CallToolAsync overload.
        var argsJson  = JsonSerializer.SerializeToElement(arguments, _jsonOptions);
        var argsDict  = argsJson.EnumerateObject()
                                .ToDictionary(p => p.Name, p => (object)p.Value);

        var result = await _mcpClient.CallToolAsync(toolName, argsDict);

        if (result.IsError == true)
            throw new InvalidOperationException($"MCP tool '{toolName}' returned an error.");

        // Prefer StructuredContent (JsonNode returned directly from the tool)
        if (result.StructuredContent is System.Text.Json.Nodes.JsonNode node)
        {
            var element = JsonSerializer.Deserialize<T>(node.ToJsonString(), _jsonOptions);
            return element ?? throw new InvalidOperationException($"MCP tool '{toolName}': StructuredContent deserialization returned null.");
        }

        // Fallback: parse the first text content block as JSON
        var textBlock = result.Content.FirstOrDefault(c => c.Type == "text");
        if (textBlock is ModelContextProtocol.Protocol.TextContentBlock tb)
        {
            return JsonSerializer.Deserialize<T>(tb.Text, _jsonOptions)
                ?? throw new InvalidOperationException($"MCP tool '{toolName}': text content deserialization returned null.");
        }

        throw new InvalidOperationException($"MCP tool '{toolName}' returned no usable content.");
    }

    public async ValueTask DisposeAsync()
    {
        await _mcpClient.DisposeAsync();
    }
}


public sealed class McpWarrantyCheckTool(McpToolsClient client) : IWarrantyCheckTool
{
    public string Name => "WarrantyCheck";

    public async Task<WarrantyStatus> CheckWarrantyAsync(EquipmentInfo equipment, string failedComponent)
    {
        var dto = await client.CallToolAsync<WarrantyStatusDto>("check_warranty", new
        {
            serialNumber = equipment.SerialNumber ?? string.Empty,
            modelNumber  = equipment.ModelNumber  ?? string.Empty
        });

        return new WarrantyStatus
        {
            IsUnderWarranty = dto.IsUnderWarranty,
            WarrantyType    = dto.WarrantyType,
            ExpirationDate  = DateTime.TryParse(dto.ExpirationDate, out var d) ? d : DateTime.MinValue,
            ContactInfo     = dto.ContactInfo,
            ClaimProcess    = dto.ClaimProcess,
            Advice          = dto.Advice
        };
    }

    private sealed class WarrantyStatusDto
    {
        public bool    IsUnderWarranty { get; set; }
        public string  WarrantyType    { get; set; } = string.Empty;
        public string? ExpirationDate  { get; set; }
        public int     DaysRemaining   { get; set; }
        public string? ContactInfo     { get; set; }
        public string? ClaimProcess    { get; set; }
        public string  Advice          { get; set; } = string.Empty;
    }
}

public sealed class McpInventoryCheckTool(McpToolsClient client) : IInventoryCheckTool
{
    public string Name => "InventoryCheck";

    public async Task<InventoryStatus> CheckStockAsync(string partNumber)
    {
        var dto = await client.CallToolAsync<InventoryDto>("check_inventory", new { partNumber });

        return new InventoryStatus
        {
            InStock           = dto.InStock,
            Quantity          = dto.TotalQuantity,
            Locations         = dto.Locations.Select(l => l.Supplier).ToArray(),
            EstimatedDelivery = dto.Locations.Count > 0
                ? ParseDelivery(dto.Locations[0].EstimatedDelivery)
                : TimeSpan.FromDays(2),
            LastUpdated = DateTime.UtcNow
        };
    }

    private static TimeSpan ParseDelivery(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "immediate" => TimeSpan.Zero,
            "same day"  => TimeSpan.FromHours(4),
            "next day"  => TimeSpan.FromDays(1),
            _           => TimeSpan.FromDays(2)
        };

    private sealed class InventoryDto
    {
        public bool   InStock       { get; set; }
        public int    TotalQuantity { get; set; }
        public List<LocationDto> Locations { get; set; } = [];
    }

    private sealed class LocationDto
    {
        public string Supplier          { get; set; } = string.Empty;
        public int    Quantity          { get; set; }
        public string EstimatedDelivery { get; set; } = string.Empty;
    }
}

public sealed class McpSupplierLookupTool(McpToolsClient client) : ISupplierLookupTool
{
    public string Name => "SupplierLookup";

    public async Task<List<SupplierInfo>> FindSuppliersAsync(string partNumber)
    {
        var dto = await client.CallToolAsync<SuppliersDto>("find_suppliers", new { partNumber });

        return dto.Suppliers.Select(s => new SupplierInfo
        {
            Name            = s.Name,
            Distance        = s.Distance,
            Phone           = s.Phone,
            Hours           = s.Hours,
            HasPart         = s.HasPart,
            DeliveryOptions = s.DeliveryOptions?.ToArray() ?? []
        }).ToList();
    }

    private sealed class SuppliersDto
    {
        public List<SupplierDto> Suppliers { get; set; } = [];
    }

    private sealed class SupplierDto
    {
        public string       Name            { get; set; } = string.Empty;
        public string       Distance        { get; set; } = string.Empty;
        public string       Phone           { get; set; } = string.Empty;
        public string       Hours           { get; set; } = string.Empty;
        public bool         HasPart         { get; set; }
        public List<string> DeliveryOptions { get; set; } = [];
    }
}

public sealed class McpPricingTool(McpToolsClient client) : IPricingTool
{
    public string Name => "PricingComparison";

    public async Task<PricingComparison> ComparePricesAsync(string partNumber)
    {
        var dto = await client.CallToolAsync<PricingDto>("compare_prices", new { partNumber });

        return new PricingComparison
        {
            PartNumber      = dto.PartNumber,
            Prices          = dto.Prices.Select(p => new PriceOption
            {
                Supplier     = p.Supplier,
                Price        = p.Price,
                DeliveryCost = p.DeliveryCost,
                DeliveryTime = p.DeliveryTime
            }).ToArray(),
            BestValue       = dto.BestValue       ?? string.Empty,
            FastestDelivery = dto.Fastest         ?? string.Empty,
            LastUpdated     = DateTime.UtcNow
        };
    }

    private sealed class PricingDto
    {
        public string         PartNumber { get; set; } = string.Empty;
        public List<PriceDto> Prices     { get; set; } = [];
        public string?        BestValue  { get; set; }
        public string?        Fastest    { get; set; }
    }

    private sealed class PriceDto
    {
        public string  Supplier     { get; set; } = string.Empty;
        public decimal Price        { get; set; }
        public decimal DeliveryCost { get; set; }
        public string  DeliveryTime { get; set; } = string.Empty;
    }
}

public sealed class McpCompatibilityTool(McpToolsClient client) : ICompatibilityTool
{
    public string Name => "CompatibilityCheck";

    public async Task<List<PartDetails>> FindAlternativesAsync(PartDetails primaryPart, EquipmentInfo equipment)
    {
        var dto = await client.CallToolAsync<CompatibilityDto>("check_compatibility", new
        {
            partNumber  = primaryPart.PartNumber,
            modelNumber = equipment.ModelNumber ?? string.Empty
        });

        if (!dto.IsCompatible) return [];

        return
        [
            new PartDetails
            {
                PartNumber         = dto.PartNumber,
                Description        = dto.PartDescription ?? string.Empty,
                IsOEM              = dto.IsOEM,
                CriticalSpecs      = dto.CriticalSpecs ?? [],
                SafetyNotes        = dto.SafetyNotes   ?? [],
                CompatibilityNotes = dto.CompatibilityNotes
            }
        ];
    }

    private sealed class CompatibilityDto
    {
        public string       PartNumber         { get; set; } = string.Empty;
        public string?      PartDescription    { get; set; }
        public bool         IsCompatible       { get; set; }
        public bool         IsOEM              { get; set; }
        public List<string> CriticalSpecs      { get; set; } = [];
        public List<string> SafetyNotes        { get; set; } = [];
        public string?      CompatibilityNotes { get; set; }
    }
}

public sealed class McpLaborCostTool(McpToolsClient client, decimal hourlyRate, decimal tripCharge, string currency) : ILaborCostTool
{
    public string Name => "LaborCostEstimate";

    public async Task<LaborCostEstimate> EstimateLaborAsync(string componentDescription, EquipmentInfo equipment)
    {
        var dto = await client.CallToolAsync<LaborDto>("estimate_labor", new
        {
            componentType = ExtractComponentType(componentDescription),
            hourlyRate,
            tripCharge,
            currency
        });

        return new LaborCostEstimate
        {
            EstimatedHours   = dto.EstimatedHours,
            HourlyRate       = dto.HourlyRate,
            TripCharge       = dto.TripCharge,
            Currency         = dto.Currency,
            DifficultyRating = dto.DifficultyRating,
            Rationale        = dto.Rationale
        };
    }

    private static string ExtractComponentType(string description)
    {
        var lower = description.ToLowerInvariant();
        if (lower.Contains("capacitor"))                              return "Capacitor";
        if (lower.Contains("contactor"))                              return "Contactor";
        if (lower.Contains("txv") || lower.Contains("expansion valve")) return "TXV";
        if (lower.Contains("blower"))                                 return "Blower Motor";
        if (lower.Contains("compressor"))                             return "Compressor";
        return description;
    }

    private sealed class LaborDto
    {
        public double  EstimatedHours   { get; set; }
        public string  DifficultyRating { get; set; } = string.Empty;
        public string  Rationale        { get; set; } = string.Empty;
        public decimal HourlyRate       { get; set; }
        public decimal TripCharge       { get; set; }
        public string  Currency         { get; set; } = "USD";
    }
}

public sealed class McpPartIdentificationTool(McpToolsClient client, ILlmService llmService) : IPartIdentificationTool
{
    public string Name => "PartIdentification";

    public async Task<PartDetails> IdentifyPartAsync(string componentDescription, EquipmentInfo equipment)
    {
        // DB-first: look up by model number + component type — no LLM hallucination risk.
        var dto = await client.CallToolAsync<LookupResultDto>("lookup_part", new
        {
            modelNumber   = equipment.ModelNumber  ?? string.Empty,
            componentType = componentDescription
        });

        if (dto.Found > 0 && dto.Parts.Count > 0)
        {
            var p = dto.Parts[0];
            LoggingService.Instance.Log($"[McpPartIdentification] DB match: {p.PartNumber} — {p.Description}");
            return new PartDetails
            {
                PartNumber     = p.PartNumber,
                Description    = p.Description,
                IsOEM          = p.IsOEM,
                EstimatedPrice = p.BasePrice,
                CriticalSpecs  = p.CriticalSpecs ?? [],
                SafetyNotes    = p.SafetyNotes   ?? []
            };
        }

        // Fallback: LLM identification when part is not yet in the database.
        LoggingService.Instance.Log($"[McpPartIdentification] No DB match for '{componentDescription}' on model '{equipment.ModelNumber}' — falling back to LLM");
        return await new PartIdentificationTool(llmService).IdentifyPartAsync(componentDescription, equipment);
    }

    private sealed class LookupResultDto
    {
        public int           Found { get; set; }
        public List<PartDto> Parts { get; set; } = [];
    }

    private sealed class PartDto
    {
        public string       PartNumber    { get; set; } = string.Empty;
        public string       Description   { get; set; } = string.Empty;
        public bool         IsOEM         { get; set; }
        public decimal      BasePrice     { get; set; }
        public List<string> CriticalSpecs { get; set; } = [];
        public List<string> SafetyNotes   { get; set; } = [];
    }
}
