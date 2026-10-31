using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BarGame.Models;

namespace BarGame.Services;

/// <summary>
/// Contains the raffle's business logic.
///
/// This class intentionally knows nothing about ImGui, Dalamud windows,
/// or chat subscriptions. That separation makes the game rules easier
/// for another developer to maintain later.
/// </summary>
public sealed class RaffleSession
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private readonly Configuration configuration;

    public RaffleSession(Configuration configuration)
    {
        this.configuration = configuration;
        this.RestoreActiveSessionState();
    }

    public bool IsRunning { get; private set; }

    public int PurchasedRolls { get; private set; }

    public int UsedRolls { get; private set; }

    public RollResult? LastRoll { get; private set; }

    public RollResult? WinningRoll { get; private set; }

    /// <summary>
    /// Prevents the halfway announcement from being sent more than once per run.
    /// </summary>
    private bool HalfwayNoticeSent { get; set; }

    /// <summary>
    /// Human-readable status message shown in the main UI.
    /// Useful when chat filters hide local plugin output.
    /// </summary>
    public string LastNotice { get; private set; } = string.Empty;

    public int RemainingRolls => Math.Max(0, this.PurchasedRolls - this.UsedRolls);

    public long TotalPot => this.configuration.BasePot + this.SalesLedgerTotalGil;

    public bool HasTargetPlayer => !string.IsNullOrWhiteSpace(this.configuration.TargetPlayerName);

    public long SalesLedgerTotalRandoms => this.configuration.SalesLedger.Sum(sale => (long)sale.Randoms);

    public long SalesLedgerTotalGil => this.configuration.SalesLedger.Sum(sale => sale.Gil);

    public IReadOnlyList<RollHistoryRecord> RollHistory => this.configuration.ActiveSessionRollHistory;

    /// <summary>
    /// Starts counting valid /random rolls.
    /// </summary>
    public void Start()
    {
        this.IsRunning = true;
        this.WinningRoll = null;
        this.HalfwayNoticeSent = this.UsedRolls >= this.GetHalfwayThreshold();
        this.LastNotice = $"Counting started for {this.configuration.TargetPlayerName}.";
        this.SaveActiveSessionState();
    }

    /// <summary>
    /// Stops counting rolls without resetting the current session values.
    /// </summary>
    public void Stop()
    {
        this.IsRunning = false;
        this.SaveActiveSessionState();
    }

    /// <summary>
    /// Resets the active counting session.
    ///
    /// This does not clear the sales ledger.
    /// Use ClearSalesLedger() if you want to wipe the statistics tab.
    /// </summary>
    public void Reset()
    {
        this.IsRunning = false;
        this.PurchasedRolls = 0;
        this.UsedRolls = 0;
        this.LastRoll = null;
        this.WinningRoll = null;
        this.HalfwayNoticeSent = false;
        this.configuration.ActiveSessionRollHistory.Clear();
        this.configuration.TargetPlayerName = string.Empty;
        this.LastNotice = "Session reset.";
        this.SaveActiveSessionState();
    }

    /// <summary>
    /// Adds paid random attempts and records them in the persistent sales ledger.
    ///
    /// Each call creates a new row. Repeated purchases by the same player are
    /// intentionally not merged.
    /// </summary>
    public bool AddSale(string playerName, int randoms)
    {
        // A completed round must be reset before another purchase is recorded.
        // Otherwise a purchase could be appended to an exhausted or winning round.
        if ((this.WinningRoll is not null && !this.IsRunning) || (this.PurchasedRolls > 0 && this.RemainingRolls == 0))
        {
            this.LastNotice = "This round has finished. Reset Session before the next purchase.";
            return false;
        }

        var maxRolls = Math.Clamp(this.configuration.MaxRollsPerPlayerPerRound, 1, 9999);
        if (randoms <= 0 || randoms > maxRolls - this.PurchasedRolls)
        {
            this.LastNotice = $"A player can buy at most {maxRolls} rolls in one round. " +
                              $"Available in this round: {Math.Max(0, maxRolls - this.PurchasedRolls)}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(playerName))
        {
            this.LastNotice = "Choose a player before recording a sale.";
            return false;
        }

        var cleanPlayerName = playerName.Trim();
        if (this.PurchasedRolls > 0 && !this.IsTargetPlayer(cleanPlayerName))
        {
            this.LastNotice = "Finish the current player's round before adding another player.";
            return false;
        }

        if (this.configuration.PricePerRoll < 0 || this.configuration.BasePot < 0 ||
            this.configuration.PricePerRoll > long.MaxValue / randoms)
        {
            this.LastNotice = "Price or base pot is invalid for this sale.";
            return false;
        }

        var gil = this.configuration.PricePerRoll * randoms;
        if (gil > long.MaxValue - this.configuration.BasePot ||
            this.SalesLedgerTotalGil > long.MaxValue - this.configuration.BasePot - gil)
        {
            this.LastNotice = "The sale would exceed the supported jackpot amount.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(this.configuration.TargetPlayerName))
            this.configuration.TargetPlayerName = cleanPlayerName;

        this.configuration.SalesLedger.Add(new RaffleSaleRecord
        {
            PlayerName = cleanPlayerName,
            Randoms = randoms,
            Gil = gil,
            CreatedAtLocal = DateTime.Now
        });

        this.PurchasedRolls += randoms;
        this.configuration.LastServedPlayerName = cleanPlayerName;
        this.configuration.LastServedHomeWorldId = 0; // Full UI has no verified world identity.
        this.configuration.LastServedSaleUtcTicks = DateTime.UtcNow.Ticks;

        if (this.UsedRolls < this.GetHalfwayThreshold())
            this.HalfwayNoticeSent = false;

        this.LastNotice = $"{randoms} random(s) added for {cleanPlayerName}.";

        this.SaveActiveSessionState();
        return true;
    }

    /// <summary>
    /// Historical sales and active session progress must never be coupled.
    /// Even removing a sale from the current game does not silently change
    /// counted or purchased rolls. Reset the round separately if necessary.
    /// </summary>
    public void RemoveSaleAt(int index)
    {
        if (index < 0 || index >= this.configuration.SalesLedger.Count)
        {
            this.LastNotice = "Sale could not be removed because the row no longer exists.";
            return;
        }

        var sale = this.configuration.SalesLedger[index];
        this.configuration.SalesLedger.RemoveAt(index);
        this.LastNotice = $"Deleted sale: {sale.Randoms} random(s) for {sale.PlayerName}. Session rolls unchanged.";
        this.configuration.Save();
    }

    /// <summary>
    /// Clears the persistent statistics ledger.
    ///
    /// This does not reset the active counting session.
    /// Use Reset() separately if needed.
    /// </summary>
    public void ClearSalesLedger()
    {
        this.configuration.SalesLedger.Clear();
        // Clearing Sales History marks the end of the game: allow the first
        // player of a new game to use compact SELL MAX immediately.
        this.configuration.LastServedPlayerName = string.Empty;
        this.configuration.LastServedHomeWorldId = 0;
        this.configuration.LastServedSaleUtcTicks = 0;
        this.configuration.Save();

        this.LastNotice = "Sales statistics and quick-sale cooldown cleared.";
    }

    /// <summary>
    /// Checks whether a player name from chat matches the configured target.
    /// </summary>
    public bool IsTargetPlayer(string playerName)
    {
        var rollPlayer = NormalizePlayerName(playerName);
        var targetPlayer = NormalizePlayerName(this.configuration.TargetPlayerName);

        if (string.IsNullOrEmpty(rollPlayer) || string.IsNullOrEmpty(targetPlayer))
            return false;

        // Compare complete player names, never prefixes or stripped punctuation.
        // A configured Name@World additionally requires that same world.
        if (targetPlayer.Contains('@'))
            return string.Equals(rollPlayer, targetPlayer, StringComparison.OrdinalIgnoreCase);

        var atIndex = rollPlayer.IndexOf('@');
        if (atIndex >= 0)
            rollPlayer = rollPlayer[..atIndex];

        return string.Equals(rollPlayer, targetPlayer, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Processes a parsed /random roll.
    /// </summary>
    public RaffleRollProcessResult ProcessRoll(RollResult roll)
    {
        if (!this.IsRunning)
            return RaffleRollProcessResult.Ignored();

        // Game invariant: only the current player can use attempts or win.
        if (!this.IsTargetPlayer(roll.PlayerName))
            return RaffleRollProcessResult.Ignored();

        if (this.RemainingRolls <= 0)
        {
            this.IsRunning = false;
            this.SaveActiveSessionState();
            return RaffleRollProcessResult.Ignored();
        }

        this.UsedRolls++;
        this.AddRollHistory(roll);

        if (roll.Value == this.configuration.TargetRoll)
        {
            this.WinningRoll = roll;
            this.LastNotice = this.BuildWinnerNotice(roll);

            // A winning roll always ends counting, before the plugin performs
            // its mandatory session reset and preserves the sales history.
            this.IsRunning = false;

            this.SaveActiveSessionState();
            return RaffleRollProcessResult.Winner();
        }

        if (this.RemainingRolls <= 0)
        {
            this.IsRunning = false;
            this.LastNotice = this.BuildFinalNotice();
            this.SaveActiveSessionState();
            return RaffleRollProcessResult.RollLimitReached();
        }

        if (this.ShouldSendHalfwayNotice())
        {
            this.HalfwayNoticeSent = true;
            this.LastNotice = this.BuildHalfwayNotice();
            this.SaveActiveSessionState();
            return RaffleRollProcessResult.HalfwayReachedResult();
        }

        this.LastNotice = this.BuildCountdownNotice(roll);
        this.SaveActiveSessionState();
        return RaffleRollProcessResult.Counted();
    }

    private void RestoreActiveSessionState()
    {
        this.PurchasedRolls = Math.Max(0, this.configuration.ActiveSessionPurchasedRolls);
        this.UsedRolls = Math.Clamp(this.configuration.ActiveSessionUsedRolls, 0, this.PurchasedRolls);
        this.HalfwayNoticeSent = this.configuration.ActiveSessionHalfwayNoticeSent;
        this.TrimRollHistoryToUsedRolls();
        this.RefreshRollPointersFromHistory();

        this.IsRunning = false;
        // Migrate stale selections from older versions: a player belongs to an
        // active round, not the next sale or an empty session after reloading.
        if ((this.PurchasedRolls == 0 || this.RemainingRolls == 0 ||
             this.WinningRoll is not null) &&
            !string.IsNullOrWhiteSpace(this.configuration.TargetPlayerName))
        {
            this.configuration.TargetPlayerName = string.Empty;
            this.configuration.Save();
        }

        if (this.PurchasedRolls > 0)
        {
            this.LastNotice = string.IsNullOrWhiteSpace(this.configuration.ActiveSessionLastNotice)
                ? "Previous session restored. Press Start Counting to continue."
                : $"{this.configuration.ActiveSessionLastNotice} Previous session restored stopped for safety.";
        }
        else
        {
            this.LastNotice = this.configuration.ActiveSessionLastNotice ?? string.Empty;
        }

        if (this.configuration.ActiveSessionWasRunning)
            this.SaveActiveSessionState();
    }

    private void SaveActiveSessionState()
    {
        this.TrimRollHistoryToUsedRolls();
        this.configuration.ActiveSessionPurchasedRolls = Math.Max(0, this.PurchasedRolls);
        this.configuration.ActiveSessionUsedRolls = Math.Clamp(this.UsedRolls, 0, this.PurchasedRolls);
        this.configuration.ActiveSessionHalfwayNoticeSent = this.HalfwayNoticeSent;
        this.configuration.ActiveSessionWasRunning = this.IsRunning;
        this.configuration.ActiveSessionLastNotice = this.LastNotice ?? string.Empty;
        this.configuration.Save();
    }

    private void AddRollHistory(RollResult roll)
    {
        this.configuration.ActiveSessionRollHistory.Add(new RollHistoryRecord
        {
            PlayerName = roll.PlayerName,
            Value = roll.Value,
            CreatedAtLocal = DateTime.Now
        });

        this.RefreshRollPointersFromHistory();
    }

    private void TrimRollHistoryToUsedRolls()
    {
        while (this.configuration.ActiveSessionRollHistory.Count > this.UsedRolls)
            this.configuration.ActiveSessionRollHistory.RemoveAt(this.configuration.ActiveSessionRollHistory.Count - 1);

        this.RefreshRollPointersFromHistory();
    }

    private void RefreshRollPointersFromHistory()
    {
        var history = this.configuration.ActiveSessionRollHistory;

        if (history.Count <= 0)
        {
            this.LastRoll = null;
            this.WinningRoll = null;
            return;
        }

        var latest = history[^1];
        this.LastRoll = new RollResult(latest.PlayerName, latest.Value);

        var winningRecord = history.FirstOrDefault(record => record.Value == this.configuration.TargetRoll);
        this.WinningRoll = winningRecord is null
            ? null
            : new RollResult(winningRecord.PlayerName, winningRecord.Value);
    }

    private bool ShouldSendHalfwayNotice()
    {
        if (this.HalfwayNoticeSent)
            return false;

        if (this.PurchasedRolls <= 1)
            return false;

        if (this.RemainingRolls <= 0)
            return false;

        return this.UsedRolls >= this.GetHalfwayThreshold();
    }

    private int GetHalfwayThreshold()
    {
        if (this.PurchasedRolls <= 0)
            return 0;

        return (this.PurchasedRolls + 1) / 2;
    }

    /// <summary>
    /// Builds the editable countdown notice using the latest roll if no roll is supplied.
    /// </summary>
    public string BuildCountdownNotice(RollResult? roll = null)
    {
        return this.RenderNoticeTemplate(
            this.configuration.CountdownNoticeTemplate,
            Configuration.DefaultCountdownNoticeTemplate,
            roll ?? this.LastRoll);
    }

    /// <summary>
    /// Builds the editable halfway notice.
    /// </summary>
    public string BuildHalfwayNotice()
    {
        return this.RenderNoticeTemplate(
            this.configuration.HalfwayNoticeTemplate,
            Configuration.DefaultHalfwayNoticeTemplate,
            this.LastRoll);
    }

    /// <summary>
    /// Builds the editable final notice.
    /// </summary>
    public string BuildFinalNotice()
    {
        return this.RenderNoticeTemplate(
            this.configuration.FinalNoticeTemplate,
            Configuration.DefaultFinalNoticeTemplate,
            this.LastRoll);
    }

    /// <summary>
    /// Builds the editable winner notice using the winning roll if no roll is supplied.
    /// </summary>
    public string BuildWinnerNotice(RollResult? roll = null)
    {
        return this.RenderNoticeTemplate(
            this.configuration.WinnerNoticeTemplate,
            Configuration.DefaultWinnerNoticeTemplate,
            roll ?? this.WinningRoll,
            winnerNotice: true);
    }

    private string RenderNoticeTemplate(string? template, string fallbackTemplate, RollResult? roll, bool winnerNotice = false)
    {
        var safeTemplate = string.IsNullOrWhiteSpace(template)
            ? fallbackTemplate
            : template;

        var targetPlayer = string.IsNullOrWhiteSpace(this.configuration.TargetPlayerName)
            ? "target player"
            : this.configuration.TargetPlayerName;

        if (winnerNotice && roll is not null)
            targetPlayer = roll.PlayerName;

        var rollPlayer = string.IsNullOrWhiteSpace(roll?.PlayerName)
            ? targetPlayer
            : roll!.PlayerName;

        var lastRoll = roll is null
            ? "0"
            : roll.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return safeTemplate
            .Replace("{player}", rollPlayer, StringComparison.OrdinalIgnoreCase)
            .Replace("{targetPlayer}", targetPlayer, StringComparison.OrdinalIgnoreCase)
            .Replace("{lastRoll}", lastRoll, StringComparison.OrdinalIgnoreCase)
            .Replace("{roll}", lastRoll, StringComparison.OrdinalIgnoreCase)
            .Replace("{targetRoll}", this.configuration.TargetRoll.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{price}", FormatGilShort(this.configuration.PricePerRoll), StringComparison.OrdinalIgnoreCase)
            .Replace("{jackpot}", FormatGilShort(this.TotalPot), StringComparison.OrdinalIgnoreCase)
            .Replace("{purchasedRolls}", this.PurchasedRolls.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{usedRolls}", this.UsedRolls.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{remainingRolls}", this.RemainingRolls.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{basePot}", FormatGilShort(this.configuration.BasePot), StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatGilShort(long gil)
    {
        if (gil >= 1_000_000)
        {
            var millions = gil / 1_000_000d;
            return $"{millions.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}M";
        }

        if (gil >= 1_000)
        {
            var thousands = gil / 1_000d;
            return $"{thousands.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}K";
        }

        return gil.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string NormalizePlayerName(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            return string.Empty;

        var trimmed = playerName
            .Replace('\u00A0', ' ')
            .Replace("", " ")
            .Replace("", " ")
            .Replace("", " ")
            .Replace("", " ")
            .Replace("", " ")
            .Replace("", " ")
            .Trim();

        trimmed = WhitespaceRegex.Replace(trimmed, " ");

        return trimmed;
    }
}

/// <summary>
/// Result of trying to process a /random roll.
/// </summary>
public sealed class RaffleRollProcessResult
{
    private RaffleRollProcessResult(bool wasCounted, bool isWinner, bool rollsEnded, bool halfwayReached)
    {
        this.WasCounted = wasCounted;
        this.IsWinner = isWinner;
        this.RollsEnded = rollsEnded;
        this.HalfwayReached = halfwayReached;
    }

    public bool WasCounted { get; }
    public bool IsWinner { get; }
    public bool RollsEnded { get; }
    public bool HalfwayReached { get; }

    public static RaffleRollProcessResult Counted() => new(true, false, false, false);
    public static RaffleRollProcessResult Winner() => new(true, true, false, false);
    public static RaffleRollProcessResult RollLimitReached() => new(true, false, true, false);
    public static RaffleRollProcessResult HalfwayReachedResult() => new(true, false, false, true);
    public static RaffleRollProcessResult Ignored() => new(false, false, false, false);
}
