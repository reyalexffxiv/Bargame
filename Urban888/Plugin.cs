using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Urban888.Models;
using Urban888.Services;
using Urban888.Windows;

namespace Urban888;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/urban888";

    private static readonly Regex OwnRandomRollRegex = new(
        @"(?:Random!\s*)?You\s+roll\s+an?\D*(?<roll>\d+)\.",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FullRandomRollRegex = new(
        @"(?:Random!\s*)?(?<name>.+?)\s+rolls?\s+an?\D*(?<roll>\d+)\.",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MessageOnlyRandomRollRegex = new(
        @"(?:Random!\s*)?rolls?\s+an?\D*(?<roll>\d+)\.",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Configuration Configuration { get; }

    public RaffleSession RaffleSession { get; }

    public GameChatService GameChatService { get; }

    public readonly WindowSystem WindowSystem = new("Urban888");

    private ConfigWindow ConfigWindow { get; }

    private MainWindow MainWindow { get; }

    public Plugin()
    {
        this.Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.Configuration.Initialize(PluginInterface);

        this.GameChatService = new GameChatService(Log);
        this.RaffleSession = new RaffleSession(this.Configuration);

        this.ConfigWindow = new ConfigWindow(this);
        this.MainWindow = new MainWindow(this);

        this.WindowSystem.AddWindow(this.ConfigWindow);
        this.WindowSystem.AddWindow(this.MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open the Urban 888 raffle counter."
        });

        Framework.Update += this.OnFrameworkUpdate;

        PluginInterface.UiBuilder.Draw += this.WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += this.ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += this.ToggleMainUi;

        ChatGui.ChatMessage += this.OnChatMessage;

        Log.Information("Urban888 loaded.");
    }

    public void Dispose()
    {
        ChatGui.ChatMessage -= this.OnChatMessage;

        PluginInterface.UiBuilder.Draw -= this.WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= this.ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= this.ToggleMainUi;

        Framework.Update -= this.OnFrameworkUpdate;

        CommandManager.RemoveHandler(CommandName);

        this.WindowSystem.RemoveAllWindows();

        this.ConfigWindow.Dispose();
        this.MainWindow.Dispose();
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

            case "testsay":
                this.SendSayNotice("Urban 888 test message.");
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

        var result = this.RaffleSession.ProcessRoll(roll);

        if (!result.WasCounted)
            return;

        if (result.IsWinner)
        {
            ChatGui.Print($"[Urban888] {this.RaffleSession.LastNotice}");

            if (this.Configuration.AutoSayWhenRollsEnd)
                this.SendSayNotice(this.RaffleSession.LastNotice);

            return;
        }

        if (result.HalfwayReached)
        {
            ChatGui.Print($"[Urban888] {this.RaffleSession.LastNotice}");

            if (this.Configuration.AutoSayWhenRollsEnd)
                this.SendSayNotice(this.RaffleSession.LastNotice);

            return;
        }

        if (result.RollsEnded)
        {
            ChatGui.Print($"[Urban888] {this.RaffleSession.LastNotice}");
            ChatGui.Print(
                $"[Urban888] Final pot: {this.RaffleSession.TotalPot:N0} gil. " +
                $"Saved sales: {this.RaffleSession.SalesLedgerTotalGil:N0} gil.");

            if (this.Configuration.AutoSayWhenRollsEnd)
                this.SendSayNotice(this.RaffleSession.LastNotice);

            return;
        }

        ChatGui.Print(
            $"[Urban888] {roll.PlayerName} rolled {roll.Value}. " +
            $"Remaining: {this.RaffleSession.RemainingRolls}. " +
            $"Pot: {this.RaffleSession.TotalPot:N0} gil.");
    }

    private RollResult? TryParseRandomRoll(IHandleableChatMessage chatMessage)
    {
        var senderText = CleanChatText(chatMessage.Sender.TextValue);
        var messageText = CleanChatText(chatMessage.Message.TextValue);

        var ownRollMatch = OwnRandomRollRegex.Match(messageText);
        if (ownRollMatch.Success && int.TryParse(ownRollMatch.Groups["roll"].Value, out var ownRoll))
        {
            var localPlayerName = this.GetLocalPlayerName();

            if (string.IsNullOrWhiteSpace(localPlayerName))
                localPlayerName = this.Configuration.TargetPlayerName;

            return new RollResult(localPlayerName, ownRoll);
        }

        var fullMatch = FullRandomRollRegex.Match(messageText);
        if (fullMatch.Success && int.TryParse(fullMatch.Groups["roll"].Value, out var fullRoll))
        {
            var parsedName = CleanPlayerName(fullMatch.Groups["name"].Value);

            if (!string.IsNullOrWhiteSpace(parsedName))
                return new RollResult(parsedName, fullRoll);
        }

        var messageOnlyMatch = MessageOnlyRandomRollRegex.Match(messageText);
        if (messageOnlyMatch.Success && int.TryParse(messageOnlyMatch.Groups["roll"].Value, out var messageOnlyRoll))
        {
            if (!string.IsNullOrWhiteSpace(senderText))
                return new RollResult(CleanPlayerName(senderText), messageOnlyRoll);

            if (!string.IsNullOrWhiteSpace(this.Configuration.TargetPlayerName))
                return new RollResult(this.Configuration.TargetPlayerName, messageOnlyRoll);
        }

        return null;
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

    public void SetTargetFromCurrentGameTarget()
    {
        var targetName = GetCurrentGameTargetName();

        if (string.IsNullOrWhiteSpace(targetName))
        {
            ChatGui.PrintError("[Urban888] No valid target selected.");
            return;
        }

        this.Configuration.TargetPlayerName = targetName;
        this.Configuration.Save();

        ChatGui.Print($"[Urban888] Target player set to {targetName}.");
    }

    private static string GetCurrentGameTargetName()
    {
        try
        {
            return TargetManager.Target?.Name.TextValue.Trim() ?? string.Empty;
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

    /// <summary>
    /// Sends the public shout advertisement for the game.
    /// The jackpot amount is read live from the current session total pot.
    /// </summary>
    public void ShoutGameAnnouncement()
    {
        var jackpotText = FormatGilShort(this.RaffleSession.TotalPot);
        var priceText = FormatGilShort(this.Configuration.PricePerRoll);

        var lines = new List<string>
        {
            "Urban 888 Game is ON! Hit 888 and Win the Jackpot!",
            $"{priceText} per roll, max 20 each round",
            $"Jackpot right now is {jackpotText}",
            "All gil goes into the Jackpot Pool",
            "You can find me next to the bar!",
            "First to roll 888 wins it ALL!"
        };

        this.GameChatService.QueueShoutSequence(lines, TimeSpan.FromSeconds(2));
        ChatGui.Print($"[Urban888] Queued shout announcement. Price: {priceText} per roll. Jackpot: {jackpotText}.");
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
        if (!this.RaffleSession.HasTargetPlayer)
        {
            ChatGui.PrintError("[Urban888] Set a target player before starting.");
            this.MainWindow.IsOpen = true;
            return;
        }

        if (this.RaffleSession.RemainingRolls <= 0)
        {
            ChatGui.PrintError("[Urban888] Add randoms before starting.");
            this.MainWindow.IsOpen = true;
            return;
        }

        this.RaffleSession.Start();
        ChatGui.Print($"[Urban888] Counting started for {this.Configuration.TargetPlayerName}.");
    }

    public void StopRaffle()
    {
        this.RaffleSession.Stop();
        ChatGui.Print("[Urban888] Counting stopped.");
    }

    public void ResetRaffle()
    {
        this.RaffleSession.Reset();
        ChatGui.Print("[Urban888] Session reset.");
    }

    public void PrintStatus()
    {
        ChatGui.Print($"[Urban888] Target: {this.Configuration.TargetPlayerName}");
        ChatGui.Print($"[Urban888] Randoms: {this.RaffleSession.UsedRolls}/{this.RaffleSession.PurchasedRolls}, Remaining: {this.RaffleSession.RemainingRolls}");
        ChatGui.Print($"[Urban888] Pot: {this.RaffleSession.TotalPot:N0} gil, Saved sales: {this.RaffleSession.SalesLedgerTotalGil:N0} gil.");
    }

    private static string CleanChatText(string text)
    {
        return text
            .Replace('\u00A0', ' ')
            .Replace("", " ")
            .Replace("", " ")
            .Replace("", " ")
            .Replace("", " ")
            .Replace("", " ")
            .Replace("", " ")
            .Trim();
    }

    private static string CleanPlayerName(string playerName)
    {
        var cleaned = CleanChatText(playerName);

        if (cleaned.StartsWith("Random!", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned["Random!".Length..].Trim();

        return cleaned;
    }

    public void ToggleConfigUi() => this.ConfigWindow.Toggle();

    public void ToggleMainUi() => this.MainWindow.Toggle();
}
