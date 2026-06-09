using System;
using System.Threading.Tasks;

namespace TechnicianAssistant.Services.Parts;

/// <summary>
/// Orchestrates autonomous research and produces a ranked, prioritised ordering
/// plan for a failed HVAC component.
/// </summary>
public interface IPartsOrderingAgent
{
    /// <summary>
    /// Analyses the failed component, consults all research tools, and returns a
    /// comprehensive <see cref="PartsOrderPlan"/> with ordering options, warranty
    /// advice, and proactive recommendations.
    /// </summary>
    /// <param name="failedComponent">
    /// Free-text description of what has failed (e.g. "Compressor capacitor is
    /// bulging and unit won't start").
    /// </param>
    /// <param name="equipment">Model, serial and manufacturer details.</param>
    /// <param name="context">Urgency and situational factors.</param>
    Task<PartsOrderPlan> CreateOrderPlanAsync(
        string failedComponent,
        EquipmentInfo equipment,
        PartsConversationContext context,
        Action<string>? onProgress = null);
}
