using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Urban888.Models;

namespace Urban888;

/// <summary>
/// Persistent plugin configuration.
/// 
/// Values stored here survive game/plugin restarts.
/// Runtime-only values should not live here unless we intentionally want
/// to restore them after a reload.
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>
    /// Player whose /random rolls are being counted.
    /// This must match the visible chat name as closely as possible.
    /// </summary>
    public string TargetPlayerName { get; set; } = string.Empty;

    /// <summary>
    /// Price for one random attempt.
    /// Default: 100,000 gil.
    /// </summary>
    public long PricePerRoll { get; set; } = 100_000;

    /// <summary>
    /// Starting pot before new sales are added.
    /// Example: 30,000,000 gil.
    /// </summary>
    public long BasePot { get; set; } = 30_000_000;

    /// <summary>
    /// The winning /random value.
    /// Default: 888.
    /// </summary>
    public int TargetRoll { get; set; } = 888;

    /// <summary>
    /// When true, the plugin stops counting as soon as the target roll is found.
    /// </summary>
    public bool StopWhenWinnerFound { get; set; } = true;

    /// <summary>
    /// When true, rolls from other players are ignored.
    /// Recommended for venue raffles to avoid chat noise.
    /// </summary>
    public bool CountOnlyTargetPlayer { get; set; } = true;

    /// <summary>
    /// When true, the plugin automatically sends a /say message for halfway
    /// and final notices.
    /// </summary>
    public bool AutoSayWhenRollsEnd { get; set; } = false;

    /// <summary>
    /// Persistent sales ledger.
    /// 
    /// Each purchase is stored separately, even when the same player buys again.
    /// </summary>
    public List<RaffleSaleRecord> SalesLedger { get; set; } = new();

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
    }

    /// <summary>
    /// Saves the current configuration to Dalamud's plugin config storage.
    /// </summary>
    public void Save()
    {
        this.pluginInterface?.SavePluginConfig(this);
    }
}
