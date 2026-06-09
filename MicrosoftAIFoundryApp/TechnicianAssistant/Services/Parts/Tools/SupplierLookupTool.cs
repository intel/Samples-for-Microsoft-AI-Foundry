using System.Collections.Generic;
using System.Threading.Tasks;

namespace TechnicianAssistant.Services.Parts.Tools;

/// <summary>
/// Returns supplier locations, contact details and available delivery options.
/// Production implementation should call a distributor directory API.
/// </summary>
public sealed class SupplierLookupTool : ISupplierLookupTool
{
    public string Name => "SupplierLookup";

    public async Task<List<SupplierInfo>> FindSuppliersAsync(string partNumber)
    {
        await Task.Delay(300); // Simulate lookup latency

        return
        [
            new SupplierInfo
            {
                Name            = "Local HVAC Supply",
                Distance        = "2.3 miles",
                Phone           = "(555) 123-4567",
                Hours           = "7 AM - 5 PM Mon-Fri, 8 AM - 2 PM Sat",
                HasPart         = true,
                DeliveryOptions = ["Pickup", "Same-day delivery ($25)"]
            },
            new SupplierInfo
            {
                Name            = "Johnstone Supply",
                Distance        = "4.1 miles",
                Phone           = "(555) 234-5678",
                Hours           = "6 AM - 5 PM Mon-Fri, 7 AM - 12 PM Sat",
                HasPart         = true,
                DeliveryOptions = ["Pickup", "4-hour delivery ($35)", "Next-day free"]
            },
            new SupplierInfo
            {
                Name            = "Amazon Business",
                Distance        = "Online",
                Phone           = "Online ordering",
                Hours           = "24/7 online",
                HasPart         = true,
                DeliveryOptions = ["Next-day delivery", "2-day free shipping"]
            }
        ];
    }
}
