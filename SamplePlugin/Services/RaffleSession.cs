using System;
using SamplePlugin.Models;

namespace SamplePlugin.Services;

/// <summary>
/// Contains the raffle's business logic.
/// 
/// This class intentionally knows nothing about ImGui, Dalamud windows,
/// or chat subscriptions. That separation makes the raffle rules easier
/// for another developer to maintain later.
/// </summary>
public sealed class RaffleSession
{
    private readonly Configuration configuration;

    public RaffleSession(Configuration configuration)
    {
        this.configuration = configuration;
    }

    public bool IsRunning { get; private set; }

    public int PurchasedRolls { get; private set; }

    public int UsedRolls { get; private set; }

    public long IncomingSales { get; private set; }

    public RollResult? LastRoll { get; private set; }

    public RollResult? WinningRoll { get; private set; }

    public int RemainingRolls => Math.Max(0, this.PurchasedRolls - this.UsedRolls);

    public long TotalPot => this.configuration.BasePot + this.IncomingSales;

    public bool HasTargetPlayer => !string.IsNullOrWhiteSpace(this.configuration.TargetPlayerName);

    /// <summary>
    /// Starts counting valid /random rolls.
    /// </summary>
    public void Start()
    {
        this.IsRunning = true;
        this.WinningRoll = null;
    }

    /// <summary>
    /// Stops counting rolls without resetting the current session values.
    /// </summary>
    public void Stop()
    {
        this.IsRunning = false;
    }

    /// <summary>
    /// Resets the current raffle session.
    /// 
    /// This does not reset persistent settings like target player, base pot,
    /// price per roll, or target roll.
    /// </summary>
    public void Reset()
    {
        this.IsRunning = false;
        this.PurchasedRolls = 0;
        this.UsedRolls = 0;
        this.IncomingSales = 0;
        this.LastRoll = null;
        this.WinningRoll = null;
    }

    /// <summary>
    /// Adds paid roll attempts.
    /// 
    /// Sales increase the pot immediately. Used rolls only consume attempts.
    /// This keeps the pot correct if the player wins before using every
    /// purchased roll.
    /// </summary>
    public void AddPurchasedRolls(int amount)
    {
        if (amount <= 0)
            return;

        this.PurchasedRolls += amount;
        this.IncomingSales += this.configuration.PricePerRoll * amount;
    }

    /// <summary>
    /// Removes purchased rolls as a correction.
    /// 
    /// This also removes the matching sales amount from incoming sales.
    /// It never lets purchased rolls, used rolls, or incoming sales go negative.
    /// </summary>
    public void RemovePurchasedRolls(int amount)
    {
        if (amount <= 0)
            return;

        var removableRolls = Math.Min(amount, this.PurchasedRolls);
        this.PurchasedRolls -= removableRolls;

        if (this.UsedRolls > this.PurchasedRolls)
            this.UsedRolls = this.PurchasedRolls;

        var salesToRemove = this.configuration.PricePerRoll * removableRolls;
        this.IncomingSales = Math.Max(0, this.IncomingSales - salesToRemove);

        if (this.RemainingRolls <= 0 && this.PurchasedRolls > 0)
            this.Stop();
    }

    /// <summary>
    /// Checks whether a player name from chat matches the configured target.
    /// 
    /// FFXIV chat may display names as:
    /// "First Last"
    /// or
    /// "First Last @ Server"
    /// 
    /// We normalize both sides before comparison so users can type either form.
    /// </summary>
    public bool IsTargetPlayer(string playerName)
    {
        var normalizedRollPlayer = NormalizePlayerName(playerName);
        var normalizedTargetPlayer = NormalizePlayerName(this.configuration.TargetPlayerName);

        return string.Equals(
            normalizedRollPlayer,
            normalizedTargetPlayer,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Processes a parsed /random roll.
    /// 
    /// Returns a result object explaining what happened. The plugin layer can
    /// decide how much of that result to print to chat.
    /// </summary>
    public RaffleRollProcessResult ProcessRoll(RollResult roll)
    {
        if (!this.IsRunning)
            return RaffleRollProcessResult.Ignored("The raffle session is not running.");

        this.LastRoll = roll;

        var isTargetPlayer = this.IsTargetPlayer(roll.PlayerName);

        if (this.configuration.CountOnlyTargetPlayer && !isTargetPlayer)
            return RaffleRollProcessResult.Ignored("Roll ignored because it was not made by the target player.");

        if (isTargetPlayer)
        {
            if (this.RemainingRolls <= 0)
            {
                this.Stop();
                return RaffleRollProcessResult.RollLimitReached();
            }

            this.UsedRolls++;
        }

        var isWinner = roll.Value == this.configuration.TargetRoll;

        if (isWinner)
        {
            this.WinningRoll = roll;

            if (this.configuration.StopWhenWinnerFound)
                this.Stop();

            return RaffleRollProcessResult.Winner();
        }

        // If the target player just consumed their final paid roll, stop immediately.
        // This prevents extra /random rolls from being counted after the purchased
        // attempts are exhausted.
        if (isTargetPlayer && this.RemainingRolls <= 0)
        {
            this.Stop();
            return RaffleRollProcessResult.RollLimitReached();
        }

        return RaffleRollProcessResult.Counted();
    }

    /// <summary>
    /// Normalizes player names for comparison.
    /// 
    /// This strips the optional world/server suffix after "@":
    /// "Rey Alex @ Phantom" becomes "Rey Alex".
    /// </summary>
    private static string NormalizePlayerName(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            return string.Empty;

        var trimmed = playerName.Trim();

        var atIndex = trimmed.IndexOf('@');
        if (atIndex >= 0)
            trimmed = trimmed[..atIndex].Trim();

        return trimmed;
    }
}

/// <summary>
/// Result of trying to process a /random roll.
/// </summary>
public sealed class RaffleRollProcessResult
{
    private RaffleRollProcessResult(bool wasCounted, bool isWinner, bool rollsEnded, string? reason)
    {
        this.WasCounted = wasCounted;
        this.IsWinner = isWinner;
        this.RollsEnded = rollsEnded;
        this.Reason = reason;
    }

    public bool WasCounted { get; }

    public bool IsWinner { get; }

    public bool RollsEnded { get; }

    public string? Reason { get; }

    public static RaffleRollProcessResult Counted()
    {
        return new RaffleRollProcessResult(true, false, false, null);
    }

    public static RaffleRollProcessResult Winner()
    {
        return new RaffleRollProcessResult(true, true, false, null);
    }

    public static RaffleRollProcessResult RollLimitReached()
    {
        return new RaffleRollProcessResult(true, false, true, null);
    }

    public static RaffleRollProcessResult Ignored(string reason)
    {
        return new RaffleRollProcessResult(false, false, false, reason);
    }
}
