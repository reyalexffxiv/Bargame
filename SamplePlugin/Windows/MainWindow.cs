using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace SamplePlugin.Windows;

/// <summary>
/// Main Urban888 raffle control panel.
/// 
/// This class is responsible for UI only. Raffle rules and calculations stay
/// in RaffleSession so future developers can maintain the logic separately.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("Urban888 Raffle Counter###Urban888Main")
    {
        this.plugin = plugin;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(430, 420),
            MaximumSize = new System.Numerics.Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var config = this.plugin.Configuration;
        var session = this.plugin.RaffleSession;

        ImGui.TextUnformatted("Urban888 Raffle Counter");
        ImGui.Separator();

        this.DrawTextInput(
            "Target Player",
            config.TargetPlayerName,
            value =>
            {
                config.TargetPlayerName = value;
                config.Save();
            });

        this.DrawLongInput(
            "Base Pot",
            config.BasePot,
            value =>
            {
                config.BasePot = Math.Max(0, value);
                config.Save();
            });

        this.DrawLongInput(
            "Price Per Roll",
            config.PricePerRoll,
            value =>
            {
                config.PricePerRoll = Math.Max(0, value);
                config.Save();
            });

        this.DrawIntInput(
            "Winning Roll",
            config.TargetRoll,
            value =>
            {
                config.TargetRoll = Math.Max(1, value);
                config.Save();
            });

        var countOnlyTarget = config.CountOnlyTargetPlayer;
        if (ImGui.Checkbox("Only count target player's rolls", ref countOnlyTarget))
        {
            config.CountOnlyTargetPlayer = countOnlyTarget;
            config.Save();
        }

        var stopWhenWinnerFound = config.StopWhenWinnerFound;
        if (ImGui.Checkbox("Stop when winner is found", ref stopWhenWinnerFound))
        {
            config.StopWhenWinnerFound = stopWhenWinnerFound;
            config.Save();
        }

        ImGui.Separator();

        ImGui.TextUnformatted($"Status: {(session.IsRunning ? "Running" : "Stopped")}");
        ImGui.TextUnformatted($"Purchased Rolls: {session.PurchasedRolls}");
        ImGui.TextUnformatted($"Used Rolls: {session.UsedRolls}");
        ImGui.TextUnformatted($"Remaining Rolls: {session.RemainingRolls}");
        ImGui.TextUnformatted($"Incoming Sales: {session.IncomingSales:N0} gil");
        ImGui.TextUnformatted($"Total Pot: {session.TotalPot:N0} gil");

        if (session.LastRoll is not null)
            ImGui.TextUnformatted($"Last Roll: {session.LastRoll.PlayerName} rolled {session.LastRoll.Value}");

        if (session.WinningRoll is not null)
            ImGui.TextUnformatted($"Winner: {session.WinningRoll.PlayerName} rolled {session.WinningRoll.Value}");

        ImGui.Separator();

        if (ImGui.Button(session.IsRunning ? "Stop Counting" : "Start Counting"))
        {
            if (session.IsRunning)
                this.plugin.StopRaffle();
            else
                this.plugin.StartRaffle();
        }

        ImGui.SameLine();

        if (ImGui.Button("Reset Session"))
            this.plugin.ResetRaffle();

        ImGui.Separator();

        if (ImGui.Button("+1 Roll Sale"))
            session.AddPurchasedRolls(1);

        ImGui.SameLine();

        if (ImGui.Button("+5 Roll Sale"))
            session.AddPurchasedRolls(5);

        ImGui.SameLine();

        if (ImGui.Button("+10 Roll Sale"))
            session.AddPurchasedRolls(10);

        if (ImGui.Button("-1 Roll Correction"))
            session.RemovePurchasedRolls(1);

        ImGui.SameLine();

        if (ImGui.Button("-5 Roll Correction"))
            session.RemovePurchasedRolls(5);

        ImGui.Separator();

        if (ImGui.Button("Print Status To Chat"))
            this.plugin.PrintStatus();
    }

    private void DrawTextInput(string label, string currentValue, Action<string> onChanged)
    {
        var value = currentValue;

        if (ImGui.InputText(label, ref value, 128))
            onChanged(value);
    }

    private void DrawIntInput(string label, int currentValue, Action<int> onChanged)
    {
        var value = currentValue;

        if (ImGui.InputInt(label, ref value))
            onChanged(value);
    }

    private void DrawLongInput(string label, long currentValue, Action<long> onChanged)
    {
        // ImGuiNET is more comfortable with text input for long values.
        // This also lets users paste values like 30000000 without spinner friction.
        var valueText = currentValue.ToString();

        if (!ImGui.InputText(label, ref valueText, 32))
            return;

        if (long.TryParse(valueText, out var parsedValue))
            onChanged(parsedValue);
    }
}
