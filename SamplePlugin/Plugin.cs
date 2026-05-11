using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SamplePlugin.Models;
using SamplePlugin.Services;
using SamplePlugin.Windows;

namespace SamplePlugin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/urban888";

    private static readonly Regex FullRandomRollRegex = new(
        @"^(?<name>.+?) rolls a (?<roll>\d+)\.",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MessageOnlyRandomRollRegex = new(
        @"^rolls a (?<roll>\d+)\.",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Configuration Configuration { get; }

    public RaffleSession RaffleSession { get; }

    public readonly WindowSystem WindowSystem = new("Urban888");

    private ConfigWindow ConfigWindow { get; }
    private MainWindow MainWindow { get; }

    public Plugin()
    {
        this.Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.Configuration.Initialize(PluginInterface);

        this.RaffleSession = new RaffleSession(this.Configuration);

        this.ConfigWindow = new ConfigWindow(this);
        this.MainWindow = new MainWindow(this);

        this.WindowSystem.AddWindow(this.ConfigWindow);
        this.WindowSystem.AddWindow(this.MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open the Urban 888 raffle counter."
        });

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

        CommandManager.RemoveHandler(CommandName);

        this.WindowSystem.RemoveAllWindows();

        this.ConfigWindow.Dispose();
        this.MainWindow.Dispose();
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
        {
            if (result.Reason == "The target player has no remaining rolls.")
                ChatGui.PrintError($"[Urban888] {roll.PlayerName} has no remaining rolls.");

            return;
        }

        if (result.IsWinner)
        {
            ChatGui.Print($"[Urban888] WINNER: {roll.PlayerName} rolled {roll.Value}!");
            ChatGui.Print($"[Urban888] Final pot: {this.RaffleSession.TotalPot:N0} gil.");
            ChatGui.Print($"[Urban888] Rolls used: {this.RaffleSession.UsedRolls}/{this.RaffleSession.PurchasedRolls}.");
            return;
        }

        ChatGui.Print(
            $"[Urban888] {roll.PlayerName} rolled {roll.Value}. " +
            $"Remaining: {this.RaffleSession.RemainingRolls}. " +
            $"Pot: {this.RaffleSession.TotalPot:N0} gil.");
    }

    private RollResult? TryParseRandomRoll(IHandleableChatMessage chatMessage)
    {
        var senderText = chatMessage.Sender.TextValue.Trim();
        var messageText = chatMessage.Message.TextValue.Trim();

        // Some chat messages contain the player name inside the message body:
        // "Player Name rolls a 123."
        var fullMatch = FullRandomRollRegex.Match(messageText);
        if (fullMatch.Success && int.TryParse(fullMatch.Groups["roll"].Value, out var fullRoll))
        {
            return new RollResult(
                fullMatch.Groups["name"].Value.Trim(),
                fullRoll);
        }

        // Other messages may expose the player name through Sender and only
        // contain "rolls a 123." in the message body. We support both formats.
        var messageOnlyMatch = MessageOnlyRandomRollRegex.Match(messageText);
        if (messageOnlyMatch.Success &&
            !string.IsNullOrWhiteSpace(senderText) &&
            int.TryParse(messageOnlyMatch.Groups["roll"].Value, out var messageOnlyRoll))
        {
            return new RollResult(senderText, messageOnlyRoll);
        }

        return null;
    }

    public void StartRaffle()
    {
        if (!this.RaffleSession.HasTargetPlayer)
        {
            ChatGui.PrintError("[Urban888] Set a target player before starting.");
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
        ChatGui.Print($"[Urban888] Rolls: {this.RaffleSession.UsedRolls}/{this.RaffleSession.PurchasedRolls}, Remaining: {this.RaffleSession.RemainingRolls}");
        ChatGui.Print($"[Urban888] Pot: {this.RaffleSession.TotalPot:N0} gil, Sales: {this.RaffleSession.IncomingSales:N0} gil.");
    }

    public void ToggleConfigUi() => this.ConfigWindow.Toggle();

    public void ToggleMainUi() => this.MainWindow.Toggle();
}
