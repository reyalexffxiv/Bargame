using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace BarGame.Windows;

/// <summary>
/// One owner-centered, non-modal confirmation surface, using REY UI/LAYOUT.
/// Unlike ImGui popups, it never dims or blocks the FFXIV viewport.
/// The owner controls are disabled while this window is shown.
/// </summary>
public sealed partial class MainWindow
{
    private void RequestConfirmation(Confirmation type)
    {
        if (this.confirmation != Confirmation.None)
            return;
        this.confirmation = type;
        this.confirmationFocusRequested = true;
    }

    private void DrawConfirmationWindow()
    {
        if (this.confirmation == Confirmation.None)
            return;

        var current = this.confirmation;
        const float width = 438f;
        const float height = 208f;
        var size = new Vector2(width, height);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowPos(ReyLayout.CenteredWindowPosition(this.lastWindowPosition, this.lastWindowSize, size), ImGuiCond.Always);
        if (this.confirmationFocusRequested)
        {
            ImGui.SetNextWindowFocus();
            this.confirmationFocusRequested = false;
        }

        ImGui.PushStyleColor(ImGuiCol.WindowBg, ReyLayout.Canvas);
        ImGui.PushStyleColor(ImGuiCol.Border, ReyLayout.SoftLine);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var open = true;
        if (ImGui.Begin("BarGame confirmation##bgConfirm", ref open,
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var p = ImGui.GetWindowPos();
            var draw = ImGui.GetWindowDrawList();
            ReyLayout.Fill(draw, p, new Vector2(width, 50), ReyLayout.Rail, 12);
            ReyLayout.Fill(draw, p + new Vector2(0, 12), new Vector2(width, 38), ReyLayout.Rail);
            ReyLayout.Rule(draw, p + new Vector2(0, 49), p + new Vector2(width, 49), ReyLayout.SoftLine);

            var heading = current switch
            {
                Confirmation.ResetSession => "RESET SESSION?",
                Confirmation.ClearSales => "CLEAR SALES HISTORY?",
                _ => "DELETE SALE?",
            };
            ImGui.SetCursorScreenPos(p + new Vector2(18, 16));
            ImGui.TextColored(ReyLayout.Gold, heading);
            if (ReyLayout.FlatButton("##bgConfirmClose", "X", p + new Vector2(width - 45, 10),
                    new Vector2(30, 30), ReyLayout.Rose)) open = false;

            if (current == Confirmation.ResetSession)
            {
                ImGui.SetCursorScreenPos(p + new Vector2(18, 66));
                ImGui.TextUnformatted("Clear the active round and counted rolls?");
                ImGui.SetCursorScreenPos(p + new Vector2(18, 91));
                ImGui.TextDisabled("Saved sales and jackpot will be kept.");
                ImGui.SetCursorScreenPos(p + new Vector2(18, 117));
                ImGui.TextDisabled($"Progress: {this.plugin.RaffleSession.UsedRolls}/{this.plugin.RaffleSession.PurchasedRolls}");
            }
            else if (current == Confirmation.ClearSales)
            {
                ImGui.SetCursorScreenPos(p + new Vector2(18, 66));
                ImGui.TextUnformatted("Permanently delete all saved sales?");
                ImGui.SetCursorScreenPos(p + new Vector2(18, 91));
                ImGui.TextDisabled("The jackpot returns to the base pot.");
                ImGui.SetCursorScreenPos(p + new Vector2(18, 117));
                ImGui.TextDisabled("Active rolls stay; quick-sale cooldown clears.");
            }
            else
            {
                var sale = this.salePendingDelete;
                ImGui.SetCursorScreenPos(p + new Vector2(18, 66));
                ImGui.TextUnformatted(sale is null ? "Sale no longer available."
                    : $"Delete {sale.Randoms} rolls for {sale.PlayerName}?");
                ImGui.SetCursorScreenPos(p + new Vector2(18, 91));
                ImGui.TextDisabled(sale is null ? "No changes will be made."
                    : $"{sale.Gil:N0} gil will be removed from the jackpot.");
                ImGui.SetCursorScreenPos(p + new Vector2(18, 117));
                ImGui.TextDisabled("Active round progress will not change.");
            }

            if (ReyLayout.FlatButton("##bgConfirmCancel", "CANCEL", p + new Vector2(18, 156),
                    new Vector2(120, 34), ReyLayout.Muted)) open = false;
            var canApply = current != Confirmation.DeleteSale ||
                (this.salePendingDelete is not null &&
                 this.plugin.Configuration.SalesLedger.Contains(this.salePendingDelete));
            if (canApply && ReyLayout.FlatButton("##bgConfirmApply",
                    current == Confirmation.ResetSession ? "RESET SESSION" :
                    current == Confirmation.ClearSales ? "CLEAR HISTORY" : "DELETE SALE",
                    p + new Vector2(width - 177, 156), new Vector2(159, 34), ReyLayout.Rose, true))
            {
                switch (current)
                {
                    case Confirmation.ResetSession:
                        this.plugin.ResetRaffle();
                        break;
                    case Confirmation.ClearSales:
                        this.plugin.RaffleSession.ClearSalesLedger();
                        this.exportStatus = string.Empty;
                        break;
                    case Confirmation.DeleteSale:
                        var index = this.plugin.Configuration.SalesLedger.IndexOf(this.salePendingDelete!);
                        if (index >= 0)
                            this.plugin.RaffleSession.RemoveSaleAt(index);
                        this.exportStatus = string.Empty;
                        break;
                }
                open = false;
            }
        }
        ImGui.End();
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);
        if (!open)
        {
            this.confirmation = Confirmation.None;
            this.salePendingDelete = null;
        }
    }
}
