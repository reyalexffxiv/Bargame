using System;
using System.Globalization;
using System.IO;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Urban888.Windows;

/// <summary>
/// Main Urban 888 raffle control panel.
///
/// Tabs:
/// - Main: live venue operation.
/// - Statistics: sales ledger.
/// - Config: slower settings.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private int randomsToAdd = 1;
    private int saleIndexPendingDelete = -1;
    private string exportStatus = string.Empty;

    private const string ClearStatisticsPopupName = "Clear Statistics?";
    private const string DeleteSalePopupName = "Delete Sale?";

    public MainWindow(Plugin plugin)
        : base("Urban 888###Urban888Main")
    {
        this.plugin = plugin;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(820, 390),
            MaximumSize = new System.Numerics.Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("Urban888Tabs"))
            return;

        if (ImGui.BeginTabItem("Main"))
        {
            this.DrawMainTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Statistics"))
        {
            this.DrawStatisticsTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Config"))
        {
            this.DrawConfigTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawMainTab()
    {
        var config = this.plugin.Configuration;
        var session = this.plugin.RaffleSession;

        ImGui.TextUnformatted("Urban 888");
        ImGui.Separator();

        this.DrawQuickSetupRow(config, session);
        ImGui.Separator();

        this.DrawSessionSummary(session);
        ImGui.Separator();

        this.DrawMainControls(session);
        ImGui.Separator();

        this.DrawAnnouncementSection(session);
    }

    private void DrawStatisticsTab()
    {
        var config = this.plugin.Configuration;
        var session = this.plugin.RaffleSession;

        ImGui.TextUnformatted("Sales Statistics");
        ImGui.Separator();

        ImGui.TextUnformatted($"Rows: {config.SalesLedger.Count}");
        ImGui.TextUnformatted($"Total Randoms: {session.SalesLedgerTotalRandoms}");
        ImGui.TextUnformatted($"Total Sales: {session.SalesLedgerTotalGil:N0} gil");
        ImGui.TextUnformatted($"Current Jackpot: {session.TotalPot:N0} gil");

        ImGui.Spacing();

        if (ImGui.Button("Remove Last Sale"))
            session.RemoveLastSale();

        ImGui.SameLine();

        if (ImGui.Button("Clear Statistics"))
            ImGui.OpenPopup(ClearStatisticsPopupName);

        ImGui.SameLine();

        if (ImGui.Button("Export CSV"))
            this.ExportSalesLedgerToCsv();

        this.DrawClearStatisticsConfirmationPopup(session);
        this.DrawDeleteSaleConfirmationPopup(session);

        if (!string.IsNullOrWhiteSpace(this.exportStatus))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(this.exportStatus);
        }

        ImGui.Spacing();
        ImGui.Separator();

        if (ImGui.BeginTable(
                "SalesLedgerTable",
                5,
                ImGuiTableFlags.Borders |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.Resizable |
                ImGuiTableFlags.ScrollY,
                new System.Numerics.Vector2(0, 220)))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("Player");
            ImGui.TableSetupColumn("Randoms", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Gil", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableHeadersRow();

            for (var i = 0; i < config.SalesLedger.Count; i++)
            {
                var sale = config.SalesLedger[i];

                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted((i + 1).ToString(CultureInfo.InvariantCulture));

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(sale.PlayerName);

                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(sale.Randoms.ToString(CultureInfo.InvariantCulture));

                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted($"{sale.Gil:N0}");

                ImGui.TableSetColumnIndex(4);
                if (ImGui.SmallButton($"Delete##DeleteSale{i}"))
                {
                    this.saleIndexPendingDelete = i;
                    ImGui.OpenPopup(DeleteSalePopupName);
                }
            }

            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted("");

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted("TOTAL");

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(session.SalesLedgerTotalRandoms.ToString(CultureInfo.InvariantCulture));

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted($"{session.SalesLedgerTotalGil:N0}");

            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted("");

            ImGui.EndTable();
        }
    }

    private void DrawClearStatisticsConfirmationPopup(Services.RaffleSession session)
    {
        var popupOpen = true;

        if (!ImGui.BeginPopupModal(ClearStatisticsPopupName, ref popupOpen, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.TextWrapped("This will permanently clear the saved sales ledger.");
        ImGui.TextWrapped("The active session counters are not reset by this action.");

        ImGui.Spacing();

        if (ImGui.Button("Yes, Clear Statistics", new System.Numerics.Vector2(170, 0)))
        {
            session.ClearSalesLedger();
            this.exportStatus = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();

        if (ImGui.Button("Cancel", new System.Numerics.Vector2(100, 0)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void DrawDeleteSaleConfirmationPopup(Services.RaffleSession session)
    {
        var popupOpen = true;

        if (!ImGui.BeginPopupModal(DeleteSalePopupName, ref popupOpen, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        var config = this.plugin.Configuration;

        if (this.saleIndexPendingDelete < 0 || this.saleIndexPendingDelete >= config.SalesLedger.Count)
        {
            ImGui.TextWrapped("This sale row no longer exists.");

            if (ImGui.Button("Close", new System.Numerics.Vector2(100, 0)))
            {
                this.saleIndexPendingDelete = -1;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
            return;
        }

        var sale = config.SalesLedger[this.saleIndexPendingDelete];

        ImGui.TextWrapped("Delete this sale row?");
        ImGui.Spacing();
        ImGui.TextUnformatted($"Player: {sale.PlayerName}");
        ImGui.TextUnformatted($"Randoms: {sale.Randoms}");
        ImGui.TextUnformatted($"Gil: {sale.Gil:N0}");

        ImGui.Spacing();

        if (ImGui.Button("Yes, Delete Sale", new System.Numerics.Vector2(150, 0)))
        {
            session.RemoveSaleAt(this.saleIndexPendingDelete);
            this.saleIndexPendingDelete = -1;
            this.exportStatus = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();

        if (ImGui.Button("Cancel", new System.Numerics.Vector2(100, 0)))
        {
            this.saleIndexPendingDelete = -1;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawConfigTab()
    {
        var config = this.plugin.Configuration;
        var session = this.plugin.RaffleSession;

        ImGui.TextUnformatted("Raffle Config");
        ImGui.Separator();

        this.DrawLongInput(
            "Base Pot",
            config.BasePot,
            value =>
            {
                config.BasePot = Math.Max(0, value);
                config.Save();
            });

        this.DrawLongInput(
            "Price Per Random",
            config.PricePerRoll,
            value =>
            {
                config.PricePerRoll = Math.Max(0, value);
                config.Save();
            });

        this.DrawIntInput(
            "Winning Random",
            config.TargetRoll,
            value =>
            {
                config.TargetRoll = Math.Clamp(value, 0, 999);
                config.Save();
            });

        ImGui.TextDisabled("Normal /random rolls are 0 to 999.");

        ImGui.Spacing();
        ImGui.Separator();

        var countOnlyTarget = config.CountOnlyTargetPlayer;
        if (ImGui.Checkbox("Only count target player's randoms", ref countOnlyTarget))
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

        var autoSayWhenRollsEnd = config.AutoSayWhenRollsEnd;
        if (ImGui.Checkbox("Auto /say halfway and final notices", ref autoSayWhenRollsEnd))
        {
            config.AutoSayWhenRollsEnd = autoSayWhenRollsEnd;
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();

        ImGui.TextUnformatted("Active Session Corrections");

        if (ImGui.Button("-1 Correction"))
            session.RemovePurchasedRolls(1);

        ImGui.SameLine();

        if (ImGui.Button("-5 Correction"))
            session.RemovePurchasedRolls(5);

        ImGui.Spacing();
        ImGui.Separator();

        ImGui.TextWrapped("Statistics are saved in the plugin configuration.");
        ImGui.TextWrapped("Reset Session does not clear Statistics.");
    }

    private void DrawQuickSetupRow(Configuration config, Services.RaffleSession session)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Target Player");

        ImGui.SameLine();

        ImGui.SetNextItemWidth(230);

        var targetName = config.TargetPlayerName;
        if (ImGui.InputText("##TargetPlayer", ref targetName, 128))
        {
            config.TargetPlayerName = targetName;
            config.Save();
        }

        ImGui.SameLine();

        if (ImGui.Button("Use Current Target"))
            this.plugin.SetTargetFromCurrentGameTarget();

        ImGui.SameLine();

        if (ImGui.Button("Clear"))
        {
            config.TargetPlayerName = string.Empty;
            config.Save();
        }

        ImGui.SameLine();

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Randoms");

        ImGui.SameLine();

        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("##Randoms", ref this.randomsToAdd);

        if (this.randomsToAdd < 1)
            this.randomsToAdd = 1;

        ImGui.SameLine();

        if (ImGui.Button("Add"))
            session.AddSale(config.TargetPlayerName, this.randomsToAdd);
    }

    private void DrawSessionSummary(Services.RaffleSession session)
    {
        ImGui.TextUnformatted("Session");

        ImGui.TextUnformatted($"Status: {(session.IsRunning ? "Running" : "Stopped")}");
        ImGui.TextUnformatted($"Randoms: {session.UsedRolls}/{session.PurchasedRolls}");
        ImGui.TextUnformatted($"Remaining: {session.RemainingRolls}");
        ImGui.TextUnformatted($"Pot: {session.TotalPot:N0} gil");

        if (session.LastRoll is not null)
            ImGui.TextUnformatted($"Last Random: {session.LastRoll.PlayerName} rolled {session.LastRoll.Value}");

        if (session.WinningRoll is not null)
            ImGui.TextUnformatted($"Winner: {session.WinningRoll.PlayerName} rolled {session.WinningRoll.Value}");

        if (!string.IsNullOrWhiteSpace(session.LastNotice))
        {
            ImGui.Spacing();
            ImGui.TextWrapped($"Notice: {session.LastNotice}");
        }

        ImGui.Spacing();
        ImGui.TextWrapped($"Chat Sender: {this.plugin.GameChatService.LastStatus}");
    }

    private void DrawMainControls(Services.RaffleSession session)
    {
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
    }

    private void DrawAnnouncementSection(Services.RaffleSession session)
    {
        ImGui.TextUnformatted("Announcements");

        if (ImGui.Button("Copy /say Notice"))
            ImGui.SetClipboardText(BuildSayCommand(session.LastNotice));

        ImGui.SameLine();

        if (ImGui.Button("Say Notice Now"))
            this.plugin.SendSayNotice(session.LastNotice);

        ImGui.SameLine();

        if (ImGui.Button("Shout Game Ad"))
            this.plugin.ShoutGameAnnouncement();

        ImGui.SameLine();

        if (ImGui.Button("Test /say"))
            this.plugin.SendSayNotice("Urban 888 test message.");
    }

    private void ExportSalesLedgerToCsv()
    {
        var config = this.plugin.Configuration;
        var session = this.plugin.RaffleSession;

        if (config.SalesLedger.Count == 0)
        {
            this.exportStatus = "Nothing to export. Sales ledger is empty.";
            return;
        }

        try
        {
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            if (string.IsNullOrWhiteSpace(documentsPath))
            {
                this.exportStatus = "CSV export failed: Documents folder could not be found.";
                return;
            }

            var exportDirectory = Path.Combine(documentsPath, "Urban888");
            Directory.CreateDirectory(exportDirectory);

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var filePath = Path.Combine(exportDirectory, $"Urban888-sales-{timestamp}.csv");

            var csv = new StringBuilder();

            csv.AppendLine("Row,CreatedAtLocal,Player,Randoms,Gil");

            for (var i = 0; i < config.SalesLedger.Count; i++)
            {
                var sale = config.SalesLedger[i];

                csv.AppendCsvField((i + 1).ToString(CultureInfo.InvariantCulture));
                csv.Append(',');
                csv.AppendCsvField(sale.CreatedAtLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                csv.Append(',');
                csv.AppendCsvField(sale.PlayerName);
                csv.Append(',');
                csv.AppendCsvField(sale.Randoms.ToString(CultureInfo.InvariantCulture));
                csv.Append(',');
                csv.AppendCsvField(sale.Gil.ToString(CultureInfo.InvariantCulture));
                csv.AppendLine();
            }

            csv.AppendCsvField(string.Empty);
            csv.Append(',');
            csv.AppendCsvField(string.Empty);
            csv.Append(',');
            csv.AppendCsvField("TOTAL");
            csv.Append(',');
            csv.AppendCsvField(session.SalesLedgerTotalRandoms.ToString(CultureInfo.InvariantCulture));
            csv.Append(',');
            csv.AppendCsvField(session.SalesLedgerTotalGil.ToString(CultureInfo.InvariantCulture));
            csv.AppendLine();

            File.WriteAllText(filePath, csv.ToString(), Encoding.UTF8);

            this.exportStatus = $"CSV exported to: {filePath}";
        }
        catch (Exception ex)
        {
            this.exportStatus = $"CSV export failed: {ex.Message}";
        }
    }

    private static string BuildSayCommand(string notice)
    {
        if (string.IsNullOrWhiteSpace(notice))
            return string.Empty;

        var cleanNotice = notice
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();

        return $"/s {cleanNotice}";
    }

    private void DrawIntInput(string label, int currentValue, Action<int> onChanged)
    {
        var value = currentValue;

        ImGui.SetNextItemWidth(160);

        if (ImGui.InputInt(label, ref value))
            onChanged(value);
    }

    private void DrawLongInput(string label, long currentValue, Action<long> onChanged)
    {
        var valueText = currentValue.ToString();

        ImGui.SetNextItemWidth(160);

        if (!ImGui.InputText(label, ref valueText, 32))
            return;

        if (long.TryParse(valueText, out var parsedValue))
            onChanged(parsedValue);
    }
}

internal static class CsvStringBuilderExtensions
{
    public static void AppendCsvField(this StringBuilder builder, string value)
    {
        builder.Append('"');

        foreach (var character in value)
        {
            if (character == '"')
                builder.Append("\"\"");
            else
                builder.Append(character);
        }

        builder.Append('"');
    }
}
