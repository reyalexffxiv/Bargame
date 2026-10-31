using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;
using BarGame.Models;

namespace BarGame;

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
    public int Version { get; set; } = 9;

    // One non-empty line of the game ad becomes one /sh message.
    public const string DefaultShoutGameAdTemplate =
        "Bar Game is OPEN! Hit {targetRoll} and win the jackpot!" + "\n" +
        "{price} per roll, max {maxRolls} each round" + "\n" +
        "Jackpot right now: {jackpot}" + "\n" +
        "All gil goes into the Jackpot Pool" + "\n" +
        "Find me next to the bar" + "\n" +
        "First to roll {targetRoll} wins it ALL!";

    public const string DefaultCountdownNoticeTemplate =
        "{player} rolled {lastRoll}. {remainingRolls}/{purchasedRolls} randoms left.";

    public const string DefaultHalfwayNoticeTemplate =
        "{usedRolls}/{purchasedRolls} randoms for {targetPlayer} are done, {remainingRolls} remaining!";

    public const string DefaultFinalNoticeTemplate =
        "{usedRolls}/{purchasedRolls} randoms for {targetPlayer} are done!";

    public const string DefaultWinnerNoticeTemplate =
        "WINNER: {targetPlayer} rolled {lastRoll}! Final pot: {jackpot}.";

    public int MaxRollsPerPlayerPerRound { get; set; } = 20;

    public string TargetPlayerName { get; set; } = string.Empty;

    // Quick-sale duplicate protection is independent of the sales history.
    public string LastServedPlayerName { get; set; } = string.Empty;

    public uint LastServedHomeWorldId { get; set; }

    public long LastServedSaleUtcTicks { get; set; }

    public long PricePerRoll { get; set; } = 100_000;

    public long BasePot { get; set; } = 30_000_000;

    public int TargetRoll { get; set; } = 888;

    public bool AutoSayWhenRollsEnd { get; set; } = false;

    public bool AutoSayCountdownNotice { get; set; } = false;

    public string CountdownNoticeTemplate { get; set; } = DefaultCountdownNoticeTemplate;

    public string HalfwayNoticeTemplate { get; set; } = DefaultHalfwayNoticeTemplate;

    public string FinalNoticeTemplate { get; set; } = DefaultFinalNoticeTemplate;

    public string WinnerNoticeTemplate { get; set; } = DefaultWinnerNoticeTemplate;

    public bool AutoStartCountingAfterAdd { get; set; } = true;

    public bool AutoResetSessionWhenRollsEnd { get; set; } = true;

    public string CsvExportDirectory { get; set; } = string.Empty;

    public string ShoutGameAdTemplate { get; set; } = DefaultShoutGameAdTemplate;

    public float ShoutGameAdWaitSeconds { get; set; } = 2.0f;

    public List<RaffleSaleRecord> SalesLedger { get; set; } = new();

    // Persist active-round progress independently of historical sales.
    public int ActiveSessionPurchasedRolls { get; set; }

    public int ActiveSessionUsedRolls { get; set; }

    public bool ActiveSessionHalfwayNoticeSent { get; set; }

    public bool ActiveSessionWasRunning { get; set; }

    public string ActiveSessionLastNotice { get; set; } = string.Empty;

    public List<RollHistoryRecord> ActiveSessionRollHistory { get; set; } = new();

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
        var oldVersion = this.Version;
        this.Version = 9;
        this.SalesLedger ??= new();
        this.ActiveSessionRollHistory ??= new();
        this.LastServedPlayerName ??= string.Empty;
        // Preserve the previous customer when migrating older saved configurations.
        if (oldVersion < 8 && string.IsNullOrWhiteSpace(this.LastServedPlayerName) && this.SalesLedger.Count > 0)
            this.LastServedPlayerName = this.SalesLedger[^1].PlayerName;

        this.MaxRollsPerPlayerPerRound = Math.Clamp(this.MaxRollsPerPlayerPerRound, 1, 9999);
        this.CsvExportDirectory ??= string.Empty;
        // Older saved templates included a literal roll cap. Only migrate that
        // exact wording; all other custom text remains the user's own.
        if (this.ShoutGameAdTemplate?.Contains("max 20 each round", StringComparison.OrdinalIgnoreCase) == true)
        {
            this.ShoutGameAdTemplate = this.ShoutGameAdTemplate.Replace(
                "max 20 each round", "max {maxRolls} each round", StringComparison.OrdinalIgnoreCase);
            this.Save();
        }
    }

    public void Save()
    {
        this.pluginInterface?.SavePluginConfig(this);
    }
}
