using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Dalamud.Interface.Textures;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using BarGame.Models;
using BarGame.Services;
using BarGame.Windows;

namespace BarGame;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    private const string CommandName = "/bargame";
    private const string LegacyCommandName = "/urban888";
    private static readonly long QuickSaleRepeatCooldownTicks = TimeSpan.FromSeconds(30).Ticks;

    public Configuration Configuration { get; }

    public RaffleSession RaffleSession { get; }

    // The UI displays authentic observed rolls (including ignored ones), not just purchases spent.
    // Keep five in memory; the permanent counted-roll ledger remains in RaffleSession.
    private readonly object recentRollsLock = new();
    private readonly List<RecentRandomRoll> recentRolls = new(5);

    public RecentRandomRoll[] GetRecentRandomRolls()
    {
        lock (this.recentRollsLock)
            return this.recentRolls.ToArray();
    }

    public GameChatService GameChatService { get; }

    /// <summary>REY UI/LAYOUT mascot, owned by the plugin and shared across windows.</summary>
    public ISharedImmediateTexture? MascotTexture { get; }

    public readonly WindowSystem WindowSystem = new("BarGame");

    private ConfigWindow ConfigWindow { get; }

    private MainWindow MainWindow { get; }

    public Plugin()
    {
        this.Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.Configuration.Initialize(PluginInterface);

        try
        {
            this.MascotTexture = TextureProvider.GetFromManifestResource(
                Assembly.GetExecutingAssembly(), "BarGame.Assets.peepohappy.png");
        }
        catch (Exception ex) { Log.Warning(ex, "Could not load BarGame mascot."); }

        this.GameChatService = new GameChatService(Log);
        this.RaffleSession = new RaffleSession(this.Configuration);

        this.ConfigWindow = new ConfigWindow(this);
        this.MainWindow = new MainWindow(this);

        this.WindowSystem.AddWindow(this.ConfigWindow);
        this.WindowSystem.AddWindow(this.MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open the Bar Game counter."
        });

        CommandManager.AddHandler(LegacyCommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Legacy alias for /bargame."
        });

        Framework.Update += this.OnFrameworkUpdate;

        PluginInterface.UiBuilder.Draw += this.WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += this.ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += this.ToggleMainUi;

        ChatGui.ChatMessage += this.OnChatMessage;

        Log.Information("BarGame loaded.");
    }

    public void Dispose()
    {
        ChatGui.ChatMessage -= this.OnChatMessage;

        PluginInterface.UiBuilder.Draw -= this.WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= this.ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= this.ToggleMainUi;

        Framework.Update -= this.OnFrameworkUpdate;

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(LegacyCommandName);

        this.WindowSystem.RemoveAllWindows();

        if (this.MascotTexture is IDisposable texture) texture.Dispose();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        this.GameChatService.FlushOnePendingCommand();
    }

    private void OnCommand(string command, string args)
    {
        var trimmedArgs = args.Trim().ToLowerInvariant();

        switch (trimmedArgs)
        {
            case "start":
                this.StartRaffle();
                break;

            case "stop":
                this.StopRaffle();
                break;

            case "reset":
                this.ResetRaffle();
                break;

            case "status":
                this.PrintStatus();
                break;

            case "say":
                this.SendSayNotice(this.RaffleSession.LastNotice);
                break;

            case "countdown":
                this.SendCountdownNotice();
                break;

            case "announce":
                this.ShoutGameAnnouncement();
                break;

            case "target":
                this.SetTargetFromCurrentGameTarget();
                break;

            default:
                this.MainWindow.Toggle();
                break;
        }
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        var roll = this.TryParseRandomRoll(message);

        if (roll is null)
            return;

        // Do not let unrelated players crowd the live feed or affect the session.
        if (!this.RaffleSession.IsTargetPlayer(roll.PlayerName))
            return;

        var result = this.RaffleSession.ProcessRoll(roll);
        lock (this.recentRollsLock)
        {
            this.recentRolls.Insert(0, new RecentRandomRoll(
                roll.PlayerName, roll.Value, result.WasCounted, result.IsWinner));
            if (this.recentRolls.Count > 5)
                this.recentRolls.RemoveAt(5);
        }

        if (!result.WasCounted)
            return;

        if (result.IsWinner)
        {
            var winnerNotice = this.RaffleSession.LastNotice;

            ChatGui.Print($"[BarGame] {winnerNotice}");

            if (this.Configuration.AutoSayWhenRollsEnd)
                this.SendSayNotice(winnerNotice);

            // Winner always ends the round. Preserve saved sales and jackpot.
            // A winning final roll must not depend on the ordinary exhaustion reset setting.
            this.ResetRaffleAfterFinalRoll();

            return;
        }

        if (result.HalfwayReached)
        {
            ChatGui.Print($"[BarGame] {this.RaffleSession.LastNotice}");

            if (this.Configuration.AutoSayWhenRollsEnd)
                this.SendSayNotice(this.RaffleSession.LastNotice);

            return;
        }

        if (result.RollsEnded)
        {
            var finalNotice = this.RaffleSession.LastNotice;

            ChatGui.Print($"[BarGame] {finalNotice}");
            ChatGui.Print(
                $"[BarGame] Final pot: {this.RaffleSession.TotalPot:N0} gil. " +
                $"Saved sales: {this.RaffleSession.SalesLedgerTotalGil:N0} gil.");

            if (this.Configuration.AutoSayWhenRollsEnd)
                this.SendSayNotice(finalNotice);

            if (this.Configuration.AutoResetSessionWhenRollsEnd)
                this.ResetRaffleAfterFinalRoll();
            else
            {
                this.StartRepeatSaleCooldownAtRoundEnd();
                this.ClearFinishedRoundPlayer();
            }

            return;
        }

        ChatGui.Print(
            $"[BarGame] {this.RaffleSession.LastNotice} " +
            $"Pot: {this.RaffleSession.TotalPot:N0} gil.");

        if (this.Configuration.AutoSayCountdownNotice)
            this.SendSayNotice(this.RaffleSession.LastNotice);
    }

    private void ClearFinishedRoundPlayer()
    {
        // Active-session history remains available for win verification; this
        // clears only the selection and live feed, never the sales or jackpot.
        if (!string.IsNullOrEmpty(this.Configuration.TargetPlayerName))
        {
            this.Configuration.TargetPlayerName = string.Empty;
            this.Configuration.Save();
        }
        lock (this.recentRollsLock)
            this.recentRolls.Clear();
    }

    // The sale is recorded before players roll, but the queue's repeat-sale
    // cooldown needs to begin AFTER the round ends, not at purchase time.
    // Otherwise a 27-second round would leave only 3 seconds of protection.
    private void StartRepeatSaleCooldownAtRoundEnd()
    {
        if (this.RaffleSession.PurchasedRolls <= 0 ||
            string.IsNullOrWhiteSpace(this.Configuration.LastServedPlayerName))
            return;

        this.Configuration.LastServedSaleUtcTicks = DateTime.UtcNow.Ticks;
        this.Configuration.Save();
    }

    private void ResetRaffleAfterFinalRoll()
    {
        this.StartRepeatSaleCooldownAtRoundEnd();
        this.RaffleSession.Reset();
        this.ClearFinishedRoundPlayer();
        ChatGui.Print("[BarGame] Active session reset automatically. Statistics and jackpot sales were kept.");
    }

    private RollResult? TryParseRandomRoll(IHandleableChatMessage message)
    {
        // Other chat channels can contain text that imitates a random result.
        // Only FFXIV's dedicated random-number chat type is authoritative.
        if (message.LogKind != XivChatType.RandomNumber)
            return null;

        return RandomRollParser.Parse(
            message.Message.TextValue,
            message.Sender.TextValue,
            this.GetLocalPlayerName());
    }

    private string GetLocalPlayerName()
    {
        try
        {
            return ObjectTable.LocalPlayer?.Name.TextValue.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read local player name.");
            return string.Empty;
        }
    }

    /// <summary>
    /// Checks whether the currently selected FFXIV *player character* can be
    /// served through compact mode. Re-evaluated at click time: no cached
    /// target, remembered name or NPC can ever become a one-click purchase.
    /// </summary>
    public bool CanQuickSell(out string playerName, out uint homeWorldId, out string reason)
    {
        playerName = string.Empty;
        homeWorldId = 0;
        reason = string.Empty;

        try
        {
            if (TargetManager.Target is not IPlayerCharacter target)
            {
                reason = "Select a player in FFXIV first.";
                return false;
            }

            playerName = target.Name.TextValue.Trim();
            homeWorldId = target.HomeWorld.RowId;
            if (string.IsNullOrWhiteSpace(playerName))
            {
                reason = "The selected player has no valid name.";
                return false;
            }
        }
        catch (Exception)
        {
            // This check is also called every UI frame; avoid flooding the log.
            reason = "Cannot read the selected FFXIV player.";
            return false;
        }

        if (this.RaffleSession.PurchasedRolls > 0 || this.RaffleSession.IsRunning ||
            this.RaffleSession.WinningRoll is not null)
        {
            reason = "A round is active or unfinished. Use the expanded window.";
            return false;
        }

        var previous = this.Configuration.LastServedPlayerName?.Trim() ?? string.Empty;
        var at = previous.IndexOf('@');
        if (at >= 0) previous = previous[..at].Trim();
        var sameName = string.Equals(previous, playerName, StringComparison.OrdinalIgnoreCase);
        if (sameName && (this.Configuration.LastServedHomeWorldId == 0 || homeWorldId == 0 ||
                         this.Configuration.LastServedHomeWorldId == homeWorldId))
        {
            // Cooldown starts at round completion (or early manual reset), not
            // at purchase time. Legacy timestamps still expire normally.
            var soldAt = this.Configuration.LastServedSaleUtcTicks;
            var elapsedTicks = DateTime.UtcNow.Ticks - soldAt;
            if (soldAt > 0 && elapsedTicks >= 0 && elapsedTicks < QuickSaleRepeatCooldownTicks)
            {
                var secondsLeft = (QuickSaleRepeatCooldownTicks - elapsedTicks + TimeSpan.TicksPerSecond - 1)
                    / TimeSpan.TicksPerSecond;
                reason = $"Recently served this player. Try again in {secondsLeft}s, or use the expanded window.";
                return false;
            }
        }

        var max = Math.Clamp(this.Configuration.MaxRollsPerPlayerPerRound, 1, 9999);
        if (this.Configuration.PricePerRoll < 0 || this.Configuration.PricePerRoll > long.MaxValue / max)
        {
            reason = "Price configuration is invalid for this purchase.";
            return false;
        }

        return true;
    }

    /// <summary>One click = use the currently targeted player, sell max and start.</summary>
    public void QuickSellMax()
    {
        if (!this.CanQuickSell(out var name, out var worldId, out var reason))
        {
            ChatGui.PrintError($"[BarGame] {reason}");
            return;
        }

        var previousName = this.Configuration.TargetPlayerName;
        this.Configuration.TargetPlayerName = name;
        var max = Math.Clamp(this.Configuration.MaxRollsPerPlayerPerRound, 1, 9999);
        if (!this.RaffleSession.AddSale(name, max))
        {
            this.Configuration.TargetPlayerName = previousName;
            this.Configuration.Save();
            ChatGui.PrintError($"[BarGame] {this.RaffleSession.LastNotice}");
            return;
        }

        // AddSale has already persisted the sale, rolls and last name.
        // Store verified home-world identity too, to disambiguate cross-world names.
        this.Configuration.LastServedHomeWorldId = worldId;
        this.Configuration.Save();
        this.StartRaffle(); // Quick sales always start, regardless of manual Add settings.
        ChatGui.Print($"[BarGame] Quick sale: {max} rolls for {name}. Counting started.");
    }

    public void SetTargetFromCurrentGameTarget()
    {
        if (this.RaffleSession.PurchasedRolls > 0)
        {
            ChatGui.PrintError("[BarGame] The player is locked during an active round. Reset Session first.");
            return;
        }

        var targetName = GetCurrentGameTargetName();

        if (string.IsNullOrWhiteSpace(targetName))
        {
            ChatGui.PrintError("[BarGame] No valid target selected.");
            return;
        }

        this.Configuration.TargetPlayerName = targetName;
        this.Configuration.Save();

        ChatGui.Print($"[BarGame] Target player set to {targetName}.");
    }

    private static string GetCurrentGameTargetName()
    {
        try
        {
            return TargetManager.Target is IPlayerCharacter target
                ? target.Name.TextValue.Trim()
                : string.Empty;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read current game target name.");
            return string.Empty;
        }
    }

    public void SendSayNotice(string notice)
    {
        if (string.IsNullOrWhiteSpace(notice))
            return;

        this.GameChatService.QueueSay(notice);
    }

    public void SendCountdownNotice()
    {
    var notice = this.RaffleSession.BuildCountdownNotice();

    if (string.IsNullOrWhiteSpace(notice))
    {
        ChatGui.PrintError("[BarGame] Countdown notice is empty. Check Settings.");
        return;
    }

    this.SendSayNotice(notice);
    ChatGui.Print($"[BarGame] Queued countdown notice: {notice}");
    }

    /// <summary>
    /// Sends the public shout advertisement for the game.
    /// The message body comes from the editable template in Settings.
    /// </summary>
    public void ShoutGameAnnouncement()
    {
        var lines = this.BuildShoutGameAnnouncementMessages();

        if (lines.Count == 0)
        {
            ChatGui.PrintError("[BarGame] Shout Game Ad is empty. Check Settings.");
            return;
        }

        var waitSeconds = Math.Clamp(this.Configuration.ShoutGameAdWaitSeconds, 0.5f, 10.0f);
        this.GameChatService.QueueShoutSequence(lines, TimeSpan.FromSeconds(waitSeconds));
        ChatGui.Print($"[BarGame] Queued shout announcement with {lines.Count} /sh line(s), {waitSeconds:0.#}s between lines.");
    }

    /// <summary>
    /// Builds the shout ad as actual macro-style /sh commands for preview/copy.
    /// </summary>
    public string BuildShoutGameAnnouncementMacroText()
    {
        var lines = this.BuildShoutGameAnnouncementMessages();
        return string.Join(Environment.NewLine, lines.ConvertAll(line => $"/sh {line}"));
    }

    /// <summary>
    /// Builds the shout ad body lines from the editable template.
    /// One non-empty template line becomes one /sh message.
    /// </summary>
    public List<string> BuildShoutGameAnnouncementMessages()
    {
        var template = this.Configuration.ShoutGameAdTemplate;

        if (string.IsNullOrWhiteSpace(template))
            template = Configuration.DefaultShoutGameAdTemplate;

        var renderedTemplate = this.RenderMacroVariables(template);
        var lines = new List<string>();

        foreach (var rawLine in renderedTemplate.Replace("\r", string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            line = StripOptionalShoutPrefix(line);

            if (!string.IsNullOrWhiteSpace(line))
                lines.Add(line);
        }

        return lines;
    }

    /// <summary>
    /// Replaces editable macro variables with live game values.
    /// </summary>
    public string RenderMacroVariables(string template)
    {
        var targetPlayer = string.IsNullOrWhiteSpace(this.Configuration.TargetPlayerName)
            ? "target player"
            : this.Configuration.TargetPlayerName;

        var rollPlayer = string.IsNullOrWhiteSpace(this.RaffleSession.LastRoll?.PlayerName)
            ? targetPlayer
            : this.RaffleSession.LastRoll!.PlayerName;

        var lastRoll = this.RaffleSession.LastRoll is null
            ? "0"
            : this.RaffleSession.LastRoll.Value.ToString(CultureInfo.InvariantCulture);

        return template
            .Replace("{player}", rollPlayer, StringComparison.OrdinalIgnoreCase)
            .Replace("{price}", FormatGilShort(this.Configuration.PricePerRoll), StringComparison.OrdinalIgnoreCase)
            .Replace("{jackpot}", FormatGilShort(this.RaffleSession.TotalPot), StringComparison.OrdinalIgnoreCase)
            .Replace("{lastRoll}", lastRoll, StringComparison.OrdinalIgnoreCase)
            .Replace("{roll}", lastRoll, StringComparison.OrdinalIgnoreCase)
            .Replace("{targetRoll}", this.Configuration.TargetRoll.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{maxRolls}", this.Configuration.MaxRollsPerPlayerPerRound.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{targetPlayer}", targetPlayer, StringComparison.OrdinalIgnoreCase)
            .Replace("{purchasedRolls}", this.RaffleSession.PurchasedRolls.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{usedRolls}", this.RaffleSession.UsedRolls.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{remainingRolls}", this.RaffleSession.RemainingRolls.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{basePot}", FormatGilShort(this.Configuration.BasePot), StringComparison.OrdinalIgnoreCase);
    }

    private static string StripOptionalShoutPrefix(string line)
    {
        if (line.StartsWith("/shout ", StringComparison.OrdinalIgnoreCase))
            return line[7..].Trim();

        if (line.StartsWith("/sh ", StringComparison.OrdinalIgnoreCase))
            return line[4..].Trim();

        if (line.Equals("/sh", StringComparison.OrdinalIgnoreCase) || line.Equals("/shout", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        return line;
    }

    private static string FormatGilShort(long gil)
    {
        if (gil >= 1_000_000)
        {
            var millions = gil / 1_000_000d;
            return $"{millions.ToString("0.#", CultureInfo.InvariantCulture)}M";
        }

        if (gil >= 1_000)
        {
            var thousands = gil / 1_000d;
            return $"{thousands.ToString("0.#", CultureInfo.InvariantCulture)}K";
        }

        return gil.ToString("N0", CultureInfo.InvariantCulture);
    }

    public void StartRaffle()
    {
        if (this.RaffleSession.WinningRoll is not null && !this.RaffleSession.IsRunning)
        {
            ChatGui.PrintError("[BarGame] Round complete. Reset Session before starting another player.");
            this.MainWindow.IsOpen = true;
            return;
        }

        if (!this.RaffleSession.HasTargetPlayer)
        {
            ChatGui.PrintError("[BarGame] Set a target player before starting.");
            this.MainWindow.IsOpen = true;
            return;
        }

        if (this.RaffleSession.RemainingRolls <= 0)
        {
            ChatGui.PrintError("[BarGame] Add randoms before starting.");
            this.MainWindow.IsOpen = true;
            return;
        }

        this.RaffleSession.Start();
        ChatGui.Print($"[BarGame] Counting started for {this.Configuration.TargetPlayerName}.");
    }

    public void StopRaffle()
    {
        this.RaffleSession.Stop();
        ChatGui.Print("[BarGame] Counting stopped.");
    }

    public void ResetRaffle()
    {
        // An interrupted round counts as finished at manual reset. If the
        // final roll already ended it, don't restart its existing cooldown.
        if (this.RaffleSession.PurchasedRolls > 0 && this.RaffleSession.RemainingRolls > 0)
            this.StartRepeatSaleCooldownAtRoundEnd();

        this.RaffleSession.Reset();
        this.ClearFinishedRoundPlayer();
        ChatGui.Print("[BarGame] Session reset.");
    }

    public void PrintStatus()
    {
        ChatGui.Print($"[BarGame] Target: {this.Configuration.TargetPlayerName}");
        ChatGui.Print($"[BarGame] Randoms: {this.RaffleSession.UsedRolls}/{this.RaffleSession.PurchasedRolls}, Remaining: {this.RaffleSession.RemainingRolls}");
        ChatGui.Print($"[BarGame] Pot: {this.RaffleSession.TotalPot:N0} gil, Saved sales: {this.RaffleSession.SalesLedgerTotalGil:N0} gil.");
    }

    public bool TryGetMainWindowBounds(out System.Numerics.Vector2 position, out System.Numerics.Vector2 size)
        => this.MainWindow.TryGetBounds(out position, out size);

    public void OpenMainUi() => this.MainWindow.IsOpen = true;

    public void OpenConfigUi() => this.ConfigWindow.IsOpen = true;

    public void ToggleConfigUi() => this.ConfigWindow.Toggle();

    public void ToggleMainUi() => this.MainWindow.Toggle();
}
