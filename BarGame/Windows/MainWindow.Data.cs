using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;

namespace BarGame.Windows;

/// <summary>Sales history, CSV export, and row actions; separate from shell layout.</summary>
public sealed partial class MainWindow
{
    private void DrawSalesHistory()
    {
        var config = this.plugin.Configuration;
        var session = this.plugin.RaffleSession;

        BarGameTheme.Heading("SALES HISTORY");
        var width = ImGui.GetContentRegionAvail().X;
        var card = (width - 27f) / 4f;
        Metric("SALES", config.SalesLedger.Count.ToString(CultureInfo.InvariantCulture), ReyLayout.Ivory, card);
        ImGui.SameLine(0, 9);
        Metric("TOTAL ROLLS", session.SalesLedgerTotalRandoms.ToString(CultureInfo.InvariantCulture), ReyLayout.Mist, card);
        ImGui.SameLine(0, 9);
        Metric("TOTAL SALES", $"{session.SalesLedgerTotalGil:N0} gil", ReyLayout.Gold, card);
        ImGui.SameLine(0, 9);
        Metric("CURRENT POT", $"{session.TotalPot:N0} gil", ReyLayout.Gold, card);
        ImGui.Dummy(new Vector2(0, 11));

        // Shared dimensions for all top-level Sales actions.
        const float salesActionWidth = 174f;
        const float salesActionHeight = 34f;
        if (ReyLayout.Button("##bgExport", "EXPORT CSV", new Vector2(salesActionWidth, salesActionHeight), ReyLayout.Mist, true))
            this.ExportSalesLedgerToCsv();
        ImGui.SameLine(0, 9);
        if (ReyLayout.Button("##bgOpenExports", "OPEN FOLDER", new Vector2(salesActionWidth, salesActionHeight), ReyLayout.Mist))
            this.OpenExportFolder();
        ImGui.SameLine(0, 9);
        if (ReyLayout.Button("##bgClearSales", "CLEAR SALES HISTORY", new Vector2(salesActionWidth, salesActionHeight), ReyLayout.Rose, true))
            this.RequestConfirmation(Confirmation.ClearSales);

        if (!string.IsNullOrWhiteSpace(this.exportStatus))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(this.exportStatus);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (config.SalesLedger.Count == 0)
        {
            ImGui.TextDisabled("No sales recorded yet.");
            return;
        }

        // The table alone scrolls; the summary and actions remain fixed.
        var tableHeight = MathF.Max(1f, ImGui.GetContentRegionAvail().Y - 3f);
        if (ImGui.BeginTable(
                "SalesLedgerTable",
                5,
                ImGuiTableFlags.BordersInnerH |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.Resizable |
                ImGuiTableFlags.ScrollY,
                new Vector2(0, tableHeight)))
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
                if (ReyLayout.Button($"##bgDeleteSale{i}", "DELETE", new Vector2(65, 27), ReyLayout.Rose, true))
                {
                    this.salePendingDelete = sale;
                    this.RequestConfirmation(Confirmation.DeleteSale);
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

    private void ExportSalesLedgerToCsv()
    {
        var config = this.plugin.Configuration;
        var session = this.plugin.RaffleSession;

        if (config.SalesLedger.Count == 0)
        {
            this.exportStatus = "Nothing to export. Sales history is empty.";
            return;
        }

        try
        {
            var exportDirectory = this.GetExportDirectory();
            Directory.CreateDirectory(exportDirectory);

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var filePath = Path.Combine(exportDirectory, $"BarGame-sales-{timestamp}.csv");

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

    private void OpenExportFolder()
    {
        try
        {
            var directory = this.GetExportDirectory();

            Directory.CreateDirectory(directory);

            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });

            // Explorer already gives visible feedback. Avoid a persistent path
            // message taking up precious space in the Sales panel.
            this.exportStatus = string.Empty;
        }
        catch (Exception ex)
        {
            this.exportStatus = $"Could not open export folder: {ex.Message}";
        }
    }

    private string GetExportDirectory()
    {
        if (!string.IsNullOrWhiteSpace(this.plugin.Configuration.CsvExportDirectory))
            return this.plugin.Configuration.CsvExportDirectory;

        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documentsPath))
            throw new InvalidOperationException("Documents folder could not be found.");
        return Path.Combine(documentsPath, "BarGame");
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
