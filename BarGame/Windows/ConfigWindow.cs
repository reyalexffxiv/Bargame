using System;
using System.IO;
using System.Numerics;
using BarGame.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace BarGame.Windows;

/// <summary>
/// Settings window opened by the main-window cogwheel and Dalamud config button.
///
/// The main bar-game controls live in MainWindow. Slower configuration,
/// public announcements, and notice templates live here.
/// </summary>
public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;
    private int section;
    private Vector2 dragDelta;
    private FolderPicker? folderPicker;
    private string folderPickerError = string.Empty;
    private static readonly Vector2 SetupSize = new(760f, 520f);

    public ConfigWindow(Plugin plugin)
        : base("BarGame | Setup###BarGameConfig",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar)
    {
        this.plugin = plugin;
        this.Size = SetupSize;
        this.SizeCondition = ImGuiCond.Appearing;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(740, 500),
            MaximumSize = new Vector2(1100, 780)
        };
        this.AllowPinning = false;
        this.AllowClickthrough = false;
    }

    // Same native-window silhouette discipline as VenueHost's REY shell.
    public override void PreDraw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, ReyLayout.WindowCornerRadius);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, ReyLayout.Canvas);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        ImGui.SetNextWindowSize(SetupSize, ImGuiCond.Appearing);
        if (this.plugin.TryGetMainWindowBounds(out var ownerPos, out var ownerSize))
            ImGui.SetNextWindowPos(ReyLayout.CenteredWindowPosition(ownerPos, ownerSize,
                SetupSize), ImGuiCond.Appearing);
        this.Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar;
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
    }

    public override void Draw()
    {
        BarGameTheme.Push();
        try
        {
            this.dragDelta = Vector2.Zero;
            this.PollFolderPicker();
            var p = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            var draw = ImGui.GetWindowDrawList();
            ReyLayout.Fill(draw, p, size, ReyLayout.Canvas, ReyLayout.WindowCornerRadius);
            ReyLayout.Ambient(p, size);
            ReyLayout.Fill(draw, p, new Vector2(size.X, 68), ReyLayout.Rail, ReyLayout.WindowCornerRadius);
            ReyLayout.Fill(draw, p + new Vector2(0, 12), new Vector2(size.X, 56), ReyLayout.Rail);
            ReyLayout.Rule(draw, p + new Vector2(0, 67), p + new Vector2(size.X, 67), ReyLayout.SoftLine);
            var mascot = this.plugin.MascotTexture?.GetWrapOrDefault();
            if (mascot is not null)
                draw.AddImage(mascot.Handle, p + new Vector2(17, 15), p + new Vector2(67, 53));
            draw.AddText(p + new Vector2(78, 25), ReyLayout.Color(ReyLayout.Ivory), "BARGAME / SETUP");
            if (ReyLayout.FlatButton("##cfgGame", "GAME", p + new Vector2(size.X - 139, 18),
                    new Vector2(72, 32), ReyLayout.Gold)) { this.plugin.OpenMainUi(); this.IsOpen = false; }
            if (ReyLayout.FlatButton("##cfgClose", "X", p + new Vector2(size.X - 47, 18),
                    new Vector2(30, 32), ReyLayout.Rose)) this.IsOpen = false;
            ImGui.SetCursorScreenPos(p + new Vector2(238, 0));
            ImGui.InvisibleButton("##bgSetupDrag", new Vector2(MathF.Max(10, size.X - 393), 67));
            if (ImGui.IsItemActive()) this.dragDelta = ImGui.GetIO().MouseDelta;

            // VenueHost-style rounded selected states, not stock ImGui tabs.
            var nav = p + new Vector2(20, 86);
            ReyLayout.Fill(draw, nav + new Vector2(0, 0), new Vector2(size.X - 40, 52), ReyLayout.Surface, 11);
            string[] tabs = ["RULES", "GAME AD", "NOTICES", "OPTIONS"];
            for (var i = 0; i < tabs.Length; i++)
            {
                var j = i;
                if (ReyLayout.FlatButton($"##cfgTab{j}", tabs[j], nav + new Vector2(12 + j * 135, 9),
                        new Vector2(124, 34), ReyLayout.Gold, this.section == j))
                    this.section = j;
            }
            var pagePos = p + new Vector2(20, 152);
            // Keep the Setup window a stable size across tabs. Cap shorter
            // panels to their content; Notices scrolls within the panel.
            var availableHeight = MathF.Max(1f, size.Y - 172f);
            var preferredHeight = this.section switch
            {
                0 => 336f,
                1 => 304f,
                2 => availableHeight,
                _ => 145f,
            };
            var pageSize = new Vector2(size.X - 40, MathF.Min(availableHeight, preferredHeight));
            ImGui.SetCursorScreenPos(pagePos);
            // Rules, Game Ad and Options fit without a visible scrollbar at the
            // standard size. Keep mouse-wheel fallback when users increase UI scale.
            // Notices intentionally scrolls because its message editors are longer.
            var panelFlags = this.section == 2 ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoScrollbar;
            if (ReyLayout.BeginPanel($"##bgSettingsContent{this.section}", pageSize, 20, panelFlags))
            {
                switch (this.section)
                {
                    case 0: this.DrawGeneralSettings(); break;
                    case 1: this.DrawShoutGameAdEditor(); break;
                    case 2: this.DrawNoticeEditors(); break;
                    default: this.DrawOptions(); break;
                }
            }
            ReyLayout.EndPanel();
            if (this.dragDelta != Vector2.Zero)
                ImGui.SetWindowPos(p + this.dragDelta, ImGuiCond.Always);
        }
        finally { BarGameTheme.Pop(); }
    }

    private void DrawGeneralSettings()
    {
        var config = this.plugin.Configuration;
        // Keep standard control dimensions, but spend less height between rows.
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 4f));
        try
        {
            BarGameTheme.Heading("GAME RULES");

            // Two columns, two rows, with the same numeric input control for all
            // four settings. Text labels and fields therefore share row baselines.
            if (ImGui.BeginTable("##bgRulesGrid", 2, ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                this.DrawNumberInput("Base Pot", "##bgBasePot", config.BasePot, value =>
                {
                    config.BasePot = Math.Max(0, value);
                    config.Save();
                });
                ImGui.TableSetColumnIndex(1);
                this.DrawNumberInput("Price Per Random", "##bgPrice", config.PricePerRoll, value =>
                {
                    config.PricePerRoll = Math.Max(0, value);
                    config.Save();
                });

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                this.DrawNumberInput("Winning Random", "##bgWinNumber", config.TargetRoll, value =>
                {
                    config.TargetRoll = (int)Math.Clamp(value, 0L, 999L);
                    config.Save();
                });
                ImGui.TableSetColumnIndex(1);
                this.DrawNumberInput("Max Rolls / Player / Round", "##bgMaxRolls", config.MaxRollsPerPlayerPerRound, value =>
                {
                    config.MaxRollsPerPlayerPerRound = (int)Math.Clamp(value, 1L, 9999L);
                    config.Save();
                });
                ImGui.EndTable();
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            BarGameTheme.Heading("COUNTING BEHAVIOR");

            var autoStartCountingAfterAdd = config.AutoStartCountingAfterAdd;
            if (ImGui.Checkbox("Start counting automatically when Add is pressed", ref autoStartCountingAfterAdd))
            {
                config.AutoStartCountingAfterAdd = autoStartCountingAfterAdd;
                config.Save();
            }

            var autoResetSessionWhenRollsEnd = config.AutoResetSessionWhenRollsEnd;
            if (ImGui.Checkbox("Reset active session automatically after final random", ref autoResetSessionWhenRollsEnd))
            {
                config.AutoResetSessionWhenRollsEnd = autoResetSessionWhenRollsEnd;
                config.Save();
            }

        }
        finally { ImGui.PopStyleVar(); }
    }

    private void DrawWaitBetweenLinesControl()
    {
        var config = this.plugin.Configuration;
        var waitSeconds = Math.Clamp(config.ShoutGameAdWaitSeconds, 0.5f, 10.0f);

        ImGui.TextUnformatted("Line Delay (sec)");
        ImGui.SameLine(0, 12);
        ImGui.SetNextItemWidth(85);
        if (ImGui.InputFloat("##ShoutGameAdWaitSeconds", ref waitSeconds, 0.0f, 0.0f, "%.1f"))
        {
            config.ShoutGameAdWaitSeconds = Math.Clamp(waitSeconds, 0.5f, 10.0f);
            config.Save();
        }
        ImGui.SameLine(0, 5);
        if (ImGui.Button("-##ShoutWaitMinus"))
        {
            config.ShoutGameAdWaitSeconds = Math.Clamp(config.ShoutGameAdWaitSeconds - 0.5f, 0.5f, 10.0f);
            config.Save();
        }
        ImGui.SameLine(0, 5);
        if (ImGui.Button("+##ShoutWaitPlus"))
        {
            config.ShoutGameAdWaitSeconds = Math.Clamp(config.ShoutGameAdWaitSeconds + 0.5f, 0.5f, 10.0f);
            config.Save();
        }
    }

    private void DrawShoutGameAdEditor()
    {
        var config = this.plugin.Configuration;
        this.DrawWaitBetweenLinesControl();
        ImGui.Spacing();

        var template = string.IsNullOrWhiteSpace(config.ShoutGameAdTemplate)
            ? Configuration.DefaultShoutGameAdTemplate
            : config.ShoutGameAdTemplate;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextMultiline("##ShoutGameAdTemplate", ref template, 4096, new Vector2(-1, 123)))
        {
            config.ShoutGameAdTemplate = template;
            config.Save();
        }

        if (ReyLayout.Button("##cfgAdReset", "RESET DEFAULT", new Vector2(165, 32), ReyLayout.Muted))
        {
            config.ShoutGameAdTemplate = Configuration.DefaultShoutGameAdTemplate;
            config.Save();
        }
        ImGui.SameLine(0, 9);
        if (ReyLayout.Button("##cfgAdCopy", "COPY SHOUT MACRO", new Vector2(165, 32), ReyLayout.Mist))
            ImGui.SetClipboardText(this.plugin.BuildShoutGameAnnouncementMacroText());
    }

    private void DrawNoticeEditors()
    {
        var config = this.plugin.Configuration;
        BarGameTheme.Heading("NOTICE MESSAGES");

        ImGui.Spacing();

        var autoSayCountdownNotice = config.AutoSayCountdownNotice;
        if (ImGui.Checkbox("Auto /say countdown after every counted roll", ref autoSayCountdownNotice))
        {
            config.AutoSayCountdownNotice = autoSayCountdownNotice;
            config.Save();
        }

        var autoSayWhenRollsEnd = config.AutoSayWhenRollsEnd;
        if (ImGui.Checkbox("Auto /say halfway, final, and winner notices", ref autoSayWhenRollsEnd))
        {
            config.AutoSayWhenRollsEnd = autoSayWhenRollsEnd;
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        this.DrawNoticeTemplateEditor(
            "Countdown Message",
            "CountdownNoticeTemplate",
            config.CountdownNoticeTemplate,
            Configuration.DefaultCountdownNoticeTemplate,
            value =>
            {
                config.CountdownNoticeTemplate = value;
                config.Save();
            });

        this.DrawNoticeTemplateEditor(
            "Halfway Message",
            "HalfwayNoticeTemplate",
            config.HalfwayNoticeTemplate,
            Configuration.DefaultHalfwayNoticeTemplate,
            value =>
            {
                config.HalfwayNoticeTemplate = value;
                config.Save();
            });

        this.DrawNoticeTemplateEditor(
            "Final Message",
            "FinalNoticeTemplate",
            config.FinalNoticeTemplate,
            Configuration.DefaultFinalNoticeTemplate,
            value =>
            {
                config.FinalNoticeTemplate = value;
                config.Save();
            });

        this.DrawNoticeTemplateEditor(
            "Winner Message",
            "WinnerNoticeTemplate",
            config.WinnerNoticeTemplate,
            Configuration.DefaultWinnerNoticeTemplate,
            value =>
            {
                config.WinnerNoticeTemplate = value;
                config.Save();
            });
    }

    private void DrawNoticeTemplateEditor(
        string title,
        string id,
        string templateValue,
        string defaultTemplate,
        Action<string> saveTemplate)
    {
        ImGui.TextUnformatted(title);

        var template = string.IsNullOrWhiteSpace(templateValue)
            ? defaultTemplate
            : templateValue;

        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextMultiline($"##{id}", ref template, 2048, new Vector2(-1, 62)))
            saveTemplate(template);

        if (ReyLayout.Button($"##cfgReset{id}", "RESET DEFAULT", new Vector2(165, 32), ReyLayout.Muted))
            saveTemplate(defaultTemplate);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawOptions()
    {
        BarGameTheme.Heading("CSV EXPORT LOCATION");
        var config = this.plugin.Configuration;
        string current;
        try
        {
            current = string.IsNullOrWhiteSpace(config.CsvExportDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BarGame")
                : config.CsvExportDirectory;
        }
        catch (Exception ex)
        {
            ImGui.TextColored(ReyLayout.Rose, $"Cannot resolve export folder: {ex.Message}");
            return;
        }

        ImGui.TextWrapped(current);
        ImGui.Spacing();
        if (this.folderPicker is null)
        {
            const float folderActionWidth = 158f;
            const float folderActionHeight = 34f;
            if (ReyLayout.Button("##cfgBrowseFolder", "BROWSE FOLDER", new Vector2(folderActionWidth, folderActionHeight), ReyLayout.Mist, true))
                this.folderPicker = FolderPicker.Start(current);
            ImGui.SameLine(0, 9);
            if (ReyLayout.Button("##cfgDefaultFolder", "USE DEFAULT", new Vector2(folderActionWidth, folderActionHeight), ReyLayout.Muted))
            {
                config.CsvExportDirectory = string.Empty;
                config.Save();
                this.folderPickerError = string.Empty;
            }
        }
        else ImGui.TextDisabled("Choosing export folder...");

        if (!string.IsNullOrWhiteSpace(this.folderPickerError))
            ImGui.TextColored(ReyLayout.Rose, this.folderPickerError);
    }

    private void PollFolderPicker()
    {
        if (this.folderPicker is null || !this.folderPicker.TryGetResult(out var result, out var error))
            return;

        this.folderPicker = null;
        this.folderPickerError = error?.Message ?? string.Empty;
        if (string.IsNullOrWhiteSpace(result) || error is not null)
            return;

        if (!Directory.Exists(result))
        {
            this.folderPickerError = "The chosen folder does not exist.";
            return;
        }
        this.plugin.Configuration.CsvExportDirectory = result;
        this.plugin.Configuration.Save();
    }

    private void DrawNumberInput(string label, string id, long currentValue, Action<long> onChanged)
    {
        ImGui.TextDisabled(label);
        var valueText = currentValue.ToString();
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText(id, ref valueText, 32) && long.TryParse(valueText, out var parsedValue))
            onChanged(parsedValue);
    }
}
