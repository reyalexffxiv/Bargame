using System;
using System.Linq;
using System.Text.RegularExpressions;
using Urban888.Models;

namespace Urban888.Services;

/// <summary>
/// Contains the raffle's business logic.
/// 
/// This class intentionally knows nothing about ImGui, Dalamud windows,
/// or chat subscriptions. That separation makes the raffle rules easier
/// for another developer to maintain later.
/// </summary>
public sealed class RaffleSession
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex NonLetterOrDigitRegex = new(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled);

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

    public int SalesLedgerTotalRandoms => this.configuration.SalesLedger.Sum(sale => sale.Randoms);

    public long SalesLedgerTotalGil => this.configuration.SalesLedger.Sum(sale => sale.Gil);

    /// <summary>
    /// Starts counting valid /random rolls.
    /// </summary>
    public void Start()
    {
        this.IsRunning = true;
        this.WinningRoll = null;
        this.HalfwayNoticeSent = false;
        this.LastNotice = $"Counting started for {this.configuration.TargetPlayerName}.";
    }

    /// <summary>
    /// Stops counting rolls without resetting the current session values.
    /// </summary>
    public void Stop()
    {
        this.IsRunning = false;
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
        this.IncomingSales = 0;
        this.LastRoll = null;
        this.WinningRoll = null;
        this.HalfwayNoticeSent = false;
        this.LastNotice = "Session reset.";
    }

    /// <summary>
    /// Adds a sale for the current target player.
    /// Kept for compatibility with existing buttons/code.
    /// </summary>
    public void AddPurchasedRolls(int amount)
    {
        this.AddSale(this.configuration.TargetPlayerName, amount);
    }

    /// <summary>
    /// Adds paid random attempts and records them in the persistent sales ledger.
    /// 
    /// Each call creates a new row. Repeated purchases by the same player are
    /// intentionally not merged.
    /// </summary>
    public void AddSale(string playerName, int randoms)
    {
        if (randoms <= 0)
            return;

        var cleanPlayerName = string.IsNullOrWhiteSpace(playerName)
            ? "Unknown"
            : playerName.Trim();

        var gil = this.configuration.PricePerRoll * randoms;

        this.configuration.SalesLedger.Add(new RaffleSaleRecord
        {
            PlayerName = cleanPlayerName,
            Randoms = randoms,
            Gil = gil,
            CreatedAtLocal = DateTime.Now
        });

        this.configuration.Save();

        this.PurchasedRolls += randoms;
        this.IncomingSales += gil;

        if (this.UsedRolls < this.GetHalfwayThreshold())
            this.HalfwayNoticeSent = false;

        this.LastNotice = $"{randoms} random(s) added for {cleanPlayerName}.";
    }

    /// <summary>
    /// Removes purchased rolls as a correction.
    /// This affects the active session numbers only.
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

        if (this.UsedRolls < this.GetHalfwayThreshold())
            this.HalfwayNoticeSent = false;

        this.LastNotice = $"{removableRolls} random(s) removed from active session.";

        if (this.RemainingRolls <= 0 && this.PurchasedRolls > 0)
            this.Stop();
    }

    /// <summary>
    /// Removes the latest sale row from the statistics ledger and active session.
    /// Useful when a sale was added by mistake.
    /// </summary>
    public void RemoveLastSale()
    {
        if (this.configuration.SalesLedger.Count <= 0)
        {
            this.LastNotice = "No sales to remove.";
            return;
        }

        this.RemoveSaleAt(this.configuration.SalesLedger.Count - 1);
    }

    /// <summary>
    /// Removes one sale row from the statistics ledger and active session.
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
        this.configuration.Save();

        this.PurchasedRolls = Math.Max(0, this.PurchasedRolls - sale.Randoms);

        if (this.UsedRolls > this.PurchasedRolls)
            this.UsedRolls = this.PurchasedRolls;

        this.IncomingSales = Math.Max(0, this.IncomingSales - sale.Gil);

        if (this.UsedRolls < this.GetHalfwayThreshold())
            this.HalfwayNoticeSent = false;

        this.LastNotice = $"Removed sale: {sale.Randoms} random(s) for {sale.PlayerName}.";
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
        this.configuration.Save();

        this.LastNotice = "Sales statistics cleared.";
    }

    /// <summary>
    /// Checks whether a player name from chat matches the configured target.
    /// </summary>
    public bool IsTargetPlayer(string playerName)
    {
        var rollPlayer = NormalizePlayerName(playerName);
        var targetPlayer = NormalizePlayerName(this.configuration.TargetPlayerName);

        if (string.IsNullOrWhiteSpace(rollPlayer) || string.IsNullOrWhiteSpace(targetPlayer))
            return false;

        if (string.Equals(rollPlayer, targetPlayer, StringComparison.OrdinalIgnoreCase))
            return true;

        if (StartsWithWholeName(rollPlayer, targetPlayer) ||
            StartsWithWholeName(targetPlayer, rollPlayer))
            return true;

        var compactRollPlayer = CompactNameForComparison(rollPlayer);
        var compactTargetPlayer = CompactNameForComparison(targetPlayer);

        if (string.IsNullOrWhiteSpace(compactRollPlayer) || string.IsNullOrWhiteSpace(compactTargetPlayer))
            return false;

        return compactRollPlayer.StartsWith(compactTargetPlayer, StringComparison.OrdinalIgnoreCase) ||
               compactTargetPlayer.StartsWith(compactRollPlayer, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Processes a parsed /random roll.
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
                this.LastNotice = this.BuildFinalNotice();
                return RaffleRollProcessResult.RollLimitReached();
            }

            this.UsedRolls++;
        }

        var isWinner = roll.Value == this.configuration.TargetRoll;

        if (isWinner)
        {
            this.WinningRoll = roll;
            this.LastNotice = $"WINNER: {this.configuration.TargetPlayerName} rolled {roll.Value}! Final pot: {this.TotalPot:N0} gil.";

            if (this.configuration.StopWhenWinnerFound)
                this.Stop();

            return RaffleRollProcessResult.Winner();
        }

        if (isTargetPlayer && this.RemainingRolls <= 0)
        {
            this.Stop();
            this.LastNotice = this.BuildFinalNotice();
            return RaffleRollProcessResult.RollLimitReached();
        }

        if (isTargetPlayer && this.ShouldSendHalfwayNotice())
        {
            this.HalfwayNoticeSent = true;
            this.LastNotice = this.BuildHalfwayNotice();
            return RaffleRollProcessResult.HalfwayReachedResult();
        }

        if (isTargetPlayer)
            this.LastNotice = $"{this.configuration.TargetPlayerName} rolled {roll.Value}. Remaining: {this.RemainingRolls}.";
        else
            this.LastNotice = $"{roll.PlayerName} rolled {roll.Value}. Remaining: {this.RemainingRolls}.";

        return RaffleRollProcessResult.Counted();
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

    private string BuildHalfwayNotice()
    {
        return $"{this.UsedRolls}/{this.PurchasedRolls} randoms for {this.configuration.TargetPlayerName} are done, {this.RemainingRolls} remaining!";
    }

    private string BuildFinalNotice()
    {
        return $"{this.UsedRolls}/{this.PurchasedRolls} randoms for {this.configuration.TargetPlayerName} are done!";
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

        var atIndex = trimmed.IndexOf('@');
        if (atIndex >= 0)
            trimmed = trimmed[..atIndex].Trim();

        trimmed = WhitespaceRegex.Replace(trimmed, " ");

        return trimmed;
    }

    private static string CompactNameForComparison(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            return string.Empty;

        return NonLetterOrDigitRegex.Replace(playerName, string.Empty);
    }

    private static bool StartsWithWholeName(string longerName, string shorterName)
    {
        if (!longerName.StartsWith(shorterName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (longerName.Length == shorterName.Length)
            return true;

        return char.IsWhiteSpace(longerName[shorterName.Length]);
    }
}

/// <summary>
/// Result of trying to process a /random roll.
/// </summary>
public sealed class RaffleRollProcessResult
{
    private RaffleRollProcessResult(
        bool wasCounted,
        bool isWinner,
        bool rollsEnded,
        bool halfwayReached,
        string? reason)
    {
        this.WasCounted = wasCounted;
        this.IsWinner = isWinner;
        this.RollsEnded = rollsEnded;
        this.HalfwayReached = halfwayReached;
        this.Reason = reason;
    }

    public bool WasCounted { get; }

    public bool IsWinner { get; }

    public bool RollsEnded { get; }

    public bool HalfwayReached { get; }

    public string? Reason { get; }

    public static RaffleRollProcessResult Counted()
    {
        return new RaffleRollProcessResult(true, false, false, false, null);
    }

    public static RaffleRollProcessResult Winner()
    {
        return new RaffleRollProcessResult(true, true, false, false, null);
    }

    public static RaffleRollProcessResult RollLimitReached()
    {
        return new RaffleRollProcessResult(true, false, true, false, null);
    }

    public static RaffleRollProcessResult HalfwayReachedResult()
    {
        return new RaffleRollProcessResult(true, false, false, true, null);
    }

    public static RaffleRollProcessResult Ignored(string reason)
    {
        return new RaffleRollProcessResult(false, false, false, false, reason);
    }
}
