using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace BarGame.Windows;

/// <summary>
/// BarGame's REY UI/LAYOUT shell, based on VenueHost 1.0b. This is UI-only:
/// sales, jackpot, and random processing remain owned by RaffleSession.
/// </summary>
public sealed partial class MainWindow : Window
{
    private enum Page { Game, Sales }
    private Page page;
    private readonly Plugin plugin;
    private int randomsToAdd = 1;
    private string exportStatus = string.Empty;

    private enum Confirmation { None, ResetSession, ClearSales, DeleteSale }
    private Confirmation confirmation;
    private BarGame.Models.RaffleSaleRecord? salePendingDelete;
    private bool confirmationFocusRequested;
    private const float HeaderHeight = 68f;
    private const float RailWidth = 72f;
    private const float FooterHeight = 56f;
    private static readonly Vector2 ExpandedSize = new(990f, 570f);
    private const float CompactHeight = 60f;
    // REY UI/LAYOUT: VenueHost 1.0b compact-strip geometry and timing.
    private const float CompactWidth = 590f;
    private const float CompactDurationMs = 240f;
    private const ImGuiWindowFlags ShellFlags =
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
        ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoResize;

    private bool compactRequested;
    private float compactAmount;
    private float transitionFrom;
    private long transitionAt;
    private Vector2 dragDelta;
    private float animatedRailY = -1f;
    private Vector2 lastWindowPosition;
    private Vector2 lastWindowSize;

    public bool TryGetBounds(out Vector2 position, out Vector2 size)
    {
        position = this.lastWindowPosition;
        size = this.lastWindowSize;
        return this.lastWindowSize.X > 0f;
    }
    public MainWindow(Plugin plugin) : base("BarGame | REY###BarGameMain", ShellFlags)
    {
        this.plugin = plugin;
        this.Size = ExpandedSize;
        this.SizeCondition = ImGuiCond.Always;
        this.AllowPinning = false;
        this.AllowClickthrough = false;
        this.SizeConstraints = new WindowSizeConstraints { MinimumSize = ExpandedSize, MaximumSize = ExpandedSize };
    }

    private void ToggleCompactMode()
    {
        this.compactRequested = !this.compactRequested;
        this.transitionFrom = this.compactAmount;
        this.transitionAt = Environment.TickCount64;
    }

    public override void PreDraw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, ReyLayout.WindowCornerRadius);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, ReyLayout.Canvas);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, Vector4.Zero);

        // Same interruptible, eased 240 ms transition as VenueHost.
        var target = this.compactRequested ? 1f : 0f;
        var range = MathF.Abs(target - this.transitionFrom);
        if (Plugin.PluginInterface.UiBuilder.ShouldUseReducedMotion || range <= 0.0001f)
            this.compactAmount = target;
        else
        {
            var t = Math.Clamp((Environment.TickCount64 - this.transitionAt) /
                (CompactDurationMs * range), 0f, 1f);
            var eased = t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) * 0.5f;
            this.compactAmount = this.transitionFrom + (target - this.transitionFrom) * eased;
        }

        // Like VenueHost, 590 px is the minimum, not a mandatory fixed width.
        // Leave room for long live summaries without colliding with the buttons.
        var compactSummary = this.GetCompactSummary();
        var status = this.GetCompactStatus();
        // VenueHost's compact shell stays intact. Fit only plugin-specific
        // contents: summary, live roll, quick-sale control, restore and close.
        var summaryEnd = GetCompactSummaryX(status) + ImGui.CalcTextSize(compactSummary).X;
        var desired = summaryEnd + 10f + 46f + 8f + 92f + 14f + 86f;
        var compactWidth = Math.Clamp(desired, CompactWidth, 900f);
        var sizeDriven = this.compactRequested || this.compactAmount > 0.001f;
        this.Flags = ShellFlags;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = sizeDriven ? new Vector2(CompactWidth, CompactHeight) : ExpandedSize,
            MaximumSize = sizeDriven ? new Vector2(float.MaxValue, float.MaxValue) : ExpandedSize,
        };
        this.Size = Vector2.Lerp(ExpandedSize, new Vector2(compactWidth, CompactHeight), this.compactAmount);
        this.SizeCondition = ImGuiCond.Always;
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(5);
        ImGui.PopStyleVar(4);
    }

    public override void Draw()
    {
        BarGameTheme.Push();
        try
        {
            this.dragDelta = Vector2.Zero;
            this.lastWindowPosition = ImGui.GetWindowPos();
            this.lastWindowSize = ImGui.GetWindowSize();
            // Disable only this plugin's controls while a confirmation is open.
            ImGui.BeginDisabled(this.confirmation != Confirmation.None);
            if (this.compactAmount > 0.001f)
                this.DrawCompactBar();
            else
                this.DrawExpanded();
            ImGui.EndDisabled();
            this.DrawConfirmationWindow();
        }
        finally { BarGameTheme.Pop(); }
    }

    private void DrawExpanded()
    {
        var p = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var draw = ImGui.GetWindowDrawList();
        ReyLayout.Fill(draw, p, size, ReyLayout.Canvas, ReyLayout.WindowCornerRadius);
        ReyLayout.Ambient(p, size);
        this.DrawHeader(p, size.X);
        this.DrawRail(p, size.Y);
        this.DrawFooter(p, size);

        // Workspace scrolls independently of the header, navigation and footer.
        ImGui.SetCursorScreenPos(p + new Vector2(RailWidth, HeaderHeight));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(22f, 18f));
        var workspaceFlags = ImGuiWindowFlags.AlwaysUseWindowPadding;
        if (this.page == Page.Sales)
            workspaceFlags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (ImGui.BeginChild("##bgWorkspace", new Vector2(size.X - RailWidth, size.Y - HeaderHeight - FooterHeight),
                false, workspaceFlags))
        {
            if (this.page == Page.Game) this.DrawGamePage();
            else this.DrawSalesPage();
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
        if (this.dragDelta != Vector2.Zero)
            ImGui.SetWindowPos(p + this.dragDelta, ImGuiCond.Always);
    }

    private void DrawHeader(Vector2 p, float width)
    {
        var draw = ImGui.GetWindowDrawList();
        ReyLayout.Fill(draw, p, new Vector2(width, HeaderHeight), ReyLayout.Rail, ReyLayout.WindowCornerRadius);
        ReyLayout.Fill(draw, p + new Vector2(0, 12), new Vector2(width, HeaderHeight - 12), ReyLayout.Rail);
        ReyLayout.Rule(draw, p + new Vector2(0, 67), p + new Vector2(width, 67), ReyLayout.SoftLine);
        var mascot = this.plugin.MascotTexture?.GetWrapOrDefault();
        if (mascot is not null)
            draw.AddImage(mascot.Handle, p + new Vector2(19f, 17f), p + new Vector2(53f, 51f));
        else
            draw.AddText(p + new Vector2(28f, 25f), ReyLayout.Color(ReyLayout.Gold), "B");
        draw.AddText(p + new Vector2(67f, 25f), ReyLayout.Color(ReyLayout.Ivory), "BARGAME");
        var session = this.plugin.RaffleSession;
        var status = session.IsRunning ? "COUNTING" : session.WinningRoll is not null ? "WINNER" : "IDLE";
        var accent = session.IsRunning ? ReyLayout.Mint : session.WinningRoll is not null ? ReyLayout.Gold : ReyLayout.Muted;
        ReyLayout.StatusDot(p + new Vector2(width - 267, 34), accent, 3.5f);
        draw.AddText(p + new Vector2(width - 254, 25), ReyLayout.Color(accent), status);
        if (ReyLayout.FlatButton("##bgSetup", "SETUP", p + new Vector2(width - 177, 18),
                new Vector2(74, 32), ReyLayout.Gold)) this.plugin.OpenConfigUi();
        if (ReyLayout.ChevronButton("##bgCompact", p + new Vector2(width - 91, 18),
                new Vector2(34, 32), ReyLayout.Gold, up: false)) this.ToggleCompactMode();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Compact (or double-click header)");
        if (ReyLayout.FlatButton("##bgClose", "X", p + new Vector2(width - 47, 18),
                new Vector2(30, 32), ReyLayout.Rose)) this.IsOpen = false;
        ImGui.SetCursorScreenPos(p + new Vector2(186, 0));
        ImGui.InvisibleButton("##bgHeaderDrag", new Vector2(MathF.Max(10, width - 486), 67));
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) this.ToggleCompactMode();
        if (ImGui.IsItemActive()) this.dragDelta = ImGui.GetIO().MouseDelta;
    }

    private void DrawRail(Vector2 p, float height)
    {
        var draw = ImGui.GetWindowDrawList();
        var at = p + new Vector2(0, HeaderHeight);
        ReyLayout.Fill(draw, at, new Vector2(RailWidth, height - HeaderHeight - FooterHeight), ReyLayout.Rail);
        ReyLayout.Rule(draw, p + new Vector2(RailWidth - 1, HeaderHeight),
            p + new Vector2(RailWidth - 1, height - FooterHeight), ReyLayout.SoftLine);
        this.DrawRailItem(p, Page.Game, 142, "Game");
        this.DrawRailItem(p, Page.Sales, 194, "Sales");
        var target = this.page == Page.Game ? 152f : 204f;
        if (this.animatedRailY < 0 || Plugin.PluginInterface.UiBuilder.ShouldUseReducedMotion) this.animatedRailY = target;
        else this.animatedRailY += (target - this.animatedRailY) *
                (1f - MathF.Exp(-15f * MathF.Min(ImGui.GetIO().DeltaTime, 0.05f)));
        ReyLayout.Fill(draw, p + new Vector2(10, this.animatedRailY), new Vector2(3, 23), ReyLayout.Gold, 1.5f);
    }

    private void DrawRailItem(Vector2 p, Page item, float y, string title)
    {
        var pos = p + new Vector2(10, y);
        ImGui.SetCursorScreenPos(pos);
        var pressed = ImGui.InvisibleButton($"##bgRail{item}", new Vector2(52, 43));
        var hover = ImGui.IsItemHovered();
        var selected = this.page == item;
        var dl = ImGui.GetWindowDrawList();
        if (selected || hover)
            ReyLayout.Fill(dl, pos, new Vector2(52, 43), selected ? new Vector4(.25f,.205f,.195f,1f) : ReyLayout.Raised, 9);
        var ink = ReyLayout.Color(selected ? ReyLayout.Gold : hover ? ReyLayout.Ivory : ReyLayout.Muted);
        if (item == Page.Game)
        {
            dl.AddRect(pos + new Vector2(17, 12), pos + new Vector2(35, 30), ink, 4, ImDrawFlags.None, 1.5f);
            dl.AddCircleFilled(pos + new Vector2(23, 18), 1.5f, ink);
            dl.AddCircleFilled(pos + new Vector2(29, 24), 1.5f, ink);
        }
        else
        {
            for (var i = 0; i < 3; i++)
                dl.AddLine(pos + new Vector2(17, 14 + i * 7), pos + new Vector2(35, 14 + i * 7), ink, 1.7f);
        }
        if (hover) ImGui.SetTooltip(title);
        if (pressed) this.page = item;
    }

    private void DrawFooter(Vector2 p, Vector2 size)
    {
        var top = p + new Vector2(0, size.Y - FooterHeight);
        var draw = ImGui.GetWindowDrawList();
        ReyLayout.Rule(draw, top, top + new Vector2(size.X, 0), ReyLayout.SoftLine);
        var bar = top + new Vector2(12, 6);
        ReyLayout.SurfacePanel(bar, new Vector2(size.X - 24, 42), ReyLayout.Surface, 10);
        draw.AddText(bar + new Vector2(17, 13), ReyLayout.Color(ReyLayout.Gold),
            $"HIT {this.plugin.Configuration.TargetRoll} TO WIN");
        var player = this.plugin.Configuration.TargetPlayerName;
        if (!string.IsNullOrWhiteSpace(player))
        {
            const string label = "CURRENT PLAYER";
            var labelSize = ImGui.CalcTextSize(label);
            var playerSize = ImGui.CalcTextSize(player);
            var x = MathF.Max(210, size.X - 48 - labelSize.X - 12 - playerSize.X);
            draw.AddText(bar + new Vector2(x, 13), ReyLayout.Color(ReyLayout.Gold), label);
            draw.AddText(bar + new Vector2(x + labelSize.X + 12, 13), ReyLayout.Color(ReyLayout.Muted), player);
        }
    }

    // VenueHost compact-bar text placement: dynamic status and summary, not columns.
    private static float GetCompactStatusDotX() =>
        62f + ImGui.CalcTextSize("BARGAME").X + 23f;

    private static float GetCompactSummaryX(string status) =>
        GetCompactStatusDotX() + 10f + ImGui.CalcTextSize(status).X + 25f;

    private string GetCompactStatus()
    {
        var session = this.plugin.RaffleSession;
        return session.IsRunning ? "COUNTING" : session.WinningRoll is not null ? "WINNER" : "IDLE";
    }

    private string GetCompactSummary()
    {
        var session = this.plugin.RaffleSession;
        return $"{session.RemainingRolls} left  |  {ShortGil(session.TotalPot)} pot";
    }

    private void DrawCompactBar()
    {
        // VenueHost compact geometry; plugin-specific live-roll and sale controls
        // occupy the spare region ahead of the unchanged restore/close buttons.
        var p = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var centerY = p.Y + size.Y * .5f;
        var restorePos = p + new Vector2(size.X - 86f, centerY - p.Y - 16f);
        var closePos = p + new Vector2(size.X - 45f, centerY - p.Y - 16f);

        var status = this.GetCompactStatus();
        var summary = this.GetCompactSummary();
        var summaryX = GetCompactSummaryX(status);
        var rollPos = p + new Vector2(summaryX + ImGui.CalcTextSize(summary).X + 10f, centerY - p.Y - 16f);
        var sellPos = rollPos + new Vector2(54f, 0f);
        var canSell = this.plugin.CanQuickSell(out _, out _, out _);

        // A small dedicated drag area never overlaps the interactive sale button.
        ImGui.SetCursorScreenPos(p);
        ImGui.InvisibleButton("##bgCompactDrag", new Vector2(MathF.Max(1f, rollPos.X - p.X - 4f), size.Y));
        var restore = ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
        var move = ImGui.IsItemActive() ? ImGui.GetIO().MouseDelta : Vector2.Zero;

        ImGui.SetCursorScreenPos(sellPos);
        var sell = ImGui.InvisibleButton("##bgQuickSell", new Vector2(92f, 32f));
        var sellHover = ImGui.IsItemHovered();
        // Compact chrome is painted on the foreground layer. Native ImGui
        // tooltips are drawn beneath that layer, making their text appear
        // behind the bar. The action is self-labelled; invalid clicks report
        // the full reason in chat instead of showing obscured hover text.

        ImGui.SetCursorScreenPos(restorePos);
        restore |= ImGui.InvisibleButton("##bgRestore", new Vector2(34f, 32f));
        var restoreHover = ImGui.IsItemHovered();
        ImGui.SetCursorScreenPos(closePos);
        var close = ImGui.InvisibleButton("##bgCompactClose", new Vector2(30f, 32f));
        var closeHover = ImGui.IsItemHovered();

        var dl = ImGui.GetForegroundDrawList();
        dl.PushClipRect(p, p + size, true);
        ReyLayout.Fill(dl, p, size, ReyLayout.Rail, ReyLayout.WindowCornerRadius);
        var mascot = this.plugin.MascotTexture?.GetWrapOrDefault();

        // Interpolate VenueHost's expanded and compact brand geometry to avoid
        // the original jump when crossing from the expanded draw path.
        var x = 19f + (17f - 19f) * this.compactAmount;
        var y = 17f + (14f - 17f) * this.compactAmount;
        var mascotSize = 34f + (32f - 34f) * this.compactAmount;
        if (mascot is not null)
            dl.AddImage(mascot.Handle, p + new Vector2(x, y),
                p + new Vector2(x + mascotSize, y + mascotSize));
        else
            dl.AddText(p + new Vector2(x + 9f, y + 8f), ReyLayout.Color(ReyLayout.Gold), "B");
        var titleX = 67f + (62f - 67f) * this.compactAmount;
        var titleY = 25f + (21f - 25f) * this.compactAmount;
        dl.AddText(p + new Vector2(titleX, titleY), ReyLayout.Color(ReyLayout.Ivory), "BARGAME");

        var accent = status == "COUNTING" ? ReyLayout.Mint :
            status == "WINNER" ? ReyLayout.Gold : ReyLayout.Muted;
        var dotX = GetCompactStatusDotX();
        dl.AddCircleFilled(p + new Vector2(dotX, centerY - p.Y), 3f, ReyLayout.Color(accent), 12);
        dl.AddText(p + new Vector2(dotX + 10f, centerY - p.Y - 9f), ReyLayout.Color(accent), status);

        dl.AddText(p + new Vector2(summaryX, centerY - p.Y - 9f),
            ReyLayout.Color(ReyLayout.Gold), summary);

        // Most recent counted /random for the active session. Reset clears it.
        var last = this.plugin.RaffleSession.IsRunning ? this.plugin.RaffleSession.LastRoll : null;
        ReyLayout.Fill(dl, rollPos, new Vector2(46f, 32f), ReyLayout.Surface, 8);
        ReyLayout.Frame(dl, rollPos, new Vector2(46f, 32f), ReyLayout.SoftLine, 8);
        var lastText = last?.Value.ToString(CultureInfo.InvariantCulture) ?? "—";
        var lastSize = ImGui.CalcTextSize(lastText);
        dl.AddText(rollPos + new Vector2((46f - lastSize.X) / 2f, (32f - lastSize.Y) / 2f),
            ReyLayout.Color(last is null ? ReyLayout.Muted : ReyLayout.Mist), lastText);

        var sellBackground = canSell ? new Vector4(.31f, .23f, .14f, 1f) : ReyLayout.Surface;
        ReyLayout.Fill(dl, sellPos, new Vector2(92f, 32f),
            sellHover && canSell ? ReyLayout.Raised : sellBackground, 8);
        ReyLayout.Frame(dl, sellPos, new Vector2(92f, 32f),
            canSell ? ReyLayout.Gold : ReyLayout.SoftLine, 8);
        var sellText = "SELL MAX";
        var sellSize = ImGui.CalcTextSize(sellText);
        dl.AddText(sellPos + new Vector2((92f - sellSize.X) / 2f, (32f - sellSize.Y) / 2f),
            ReyLayout.Color(canSell ? ReyLayout.Ivory : ReyLayout.Dim), sellText);

        DrawCompactIcon(dl, restorePos, restoreHover, true);
        DrawCompactIcon(dl, closePos, closeHover, false);
        dl.PopClipRect();
        if (move != Vector2.Zero) ImGui.SetWindowPos(p + move, ImGuiCond.Always);
        if (restore) this.ToggleCompactMode();
        if (close) this.IsOpen = false;
        if (sell) this.plugin.QuickSellMax();
    }

    private static void DrawCompactIcon(ImDrawListPtr draw, Vector2 p, bool hover, bool restore)
    {
        ReyLayout.Fill(draw, p, new Vector2(restore ? 34 : 30, 32), hover ? ReyLayout.Raised : ReyLayout.Surface, 8);
        ReyLayout.Frame(draw, p, new Vector2(restore ? 34 : 30, 32), ReyLayout.SoftLine, 8);
        if (restore)
        {
            var center = p + new Vector2(17, 16);
            var ink = ReyLayout.Color(ReyLayout.Gold);
            draw.AddLine(center + new Vector2(-5, 2), center + new Vector2(0, -2), ink, 1.7f);
            draw.AddLine(center + new Vector2(0, -2), center + new Vector2(5, 2), ink, 1.7f);
        }
        else draw.AddText(p + new Vector2(9, 8), ReyLayout.Color(ReyLayout.Rose), "X");
    }

    private static string ShortGil(long value) => value >= 1000000
        ? (value / 1000000d).ToString("0.#", CultureInfo.InvariantCulture) + "M"
        : value >= 1000 ? (value / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "K"
        : value.ToString("N0", CultureInfo.InvariantCulture);

    private static void Metric(string title, string value, Vector4 accent, float width)
    {
        var p = ImGui.GetCursorScreenPos();
        var height = 80f;
        ReyLayout.SurfacePanel(p, new Vector2(width, height), ReyLayout.Surface, 11);
        var dl = ImGui.GetWindowDrawList();
        dl.AddText(p + new Vector2(15, 13), ReyLayout.Color(ReyLayout.Dim), title);
        dl.PushClipRect(p + new Vector2(12, 34), p + new Vector2(width - 9, height - 4), true);
        dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * 1.24f, p + new Vector2(15, 42),
            ReyLayout.Color(accent), value);
        dl.PopClipRect();
        ImGui.Dummy(new Vector2(width, height));
    }

    private void DrawGamePage()
    {
        var session = this.plugin.RaffleSession;
        var width = ImGui.GetContentRegionAvail().X;
        var jackpotWidth = MathF.Floor((width - 20f) * 0.44f);
        var otherWidth = MathF.Floor((width - 20f - jackpotWidth) / 2f);
        Metric("JACKPOT", $"{session.TotalPot:N0} gil", ReyLayout.Gold, jackpotWidth);
        ImGui.SameLine(0, 10);
        Metric("REMAINING", session.RemainingRolls.ToString(CultureInfo.InvariantCulture), ReyLayout.Mist, otherWidth);
        ImGui.SameLine(0, 10);
        Metric("USED / PAID", $"{session.UsedRolls} / {session.PurchasedRolls}", ReyLayout.Ivory,
            width - 20f - jackpotWidth - otherWidth);
        ImGui.Dummy(new Vector2(0, 10));

        var leftWidth = MathF.Floor((width - 12) * .53f);
        var rightWidth = width - 12 - leftWidth;
        if (ReyLayout.BeginPanel("##bgOperator", new Vector2(leftWidth, 290))) this.DrawOperatorPanel();
        ReyLayout.EndPanel();
        ImGui.SameLine(0, 12);
        if (ReyLayout.BeginPanel("##bgLive", new Vector2(rightWidth, 290))) this.DrawLivePanel();
        ReyLayout.EndPanel();
    }

    private void DrawOperatorPanel()
    {
        var config = this.plugin.Configuration;
        var session = this.plugin.RaffleSession;
        BarGameTheme.Heading("PLAYER & PURCHASE");
        ImGui.TextDisabled("TARGET PLAYER");
        var textWidth = MathF.Max(110, ImGui.GetContentRegionAvail().X - 160);
        ImGui.SetNextItemWidth(textWidth);
        var name = config.TargetPlayerName;
        // Lock the selected player after their first purchase; changing the name
        // mid-round could silently credit a different person's /random results.
        var playerLocked = session.PurchasedRolls > 0;
        ImGui.BeginDisabled(playerLocked);
        if (ImGui.InputText("##bgTarget", ref name, 128)) { config.TargetPlayerName = name; config.Save(); }
        ImGui.SameLine(0, 7);
        if (ReyLayout.Button("##bgUseTarget", "TARGET", new Vector2(78, 32), ReyLayout.Mint, true))
            this.plugin.SetTargetFromCurrentGameTarget();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Use your currently selected FFXIV target");
        ImGui.SameLine(0, 7);
        if (ReyLayout.Button("##bgClearTarget", "X", new Vector2(31, 32), ReyLayout.Rose))
        { config.TargetPlayerName = string.Empty; config.Save(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clear target player");
        ImGui.EndDisabled();

        var maximum = Math.Clamp(config.MaxRollsPerPlayerPerRound, 1, 9999);
        var capacity = Math.Max(0, maximum - session.PurchasedRolls);
        var roundFinished = (session.WinningRoll is not null && !session.IsRunning) ||
            (session.PurchasedRolls > 0 && session.RemainingRolls == 0);
        ImGui.TextDisabled($"{session.PurchasedRolls} / {maximum} ROLLS PURCHASED");
        if (roundFinished)
            ImGui.TextColored(ReyLayout.Gold, "Round complete — reset to start the next player");
        else if (capacity > 0)
        {
            ImGui.SetNextItemWidth(96);
            this.randomsToAdd = Math.Clamp(this.randomsToAdd, 1, capacity);
            ImGui.InputInt("##bgPurchaseCount", ref this.randomsToAdd);
            this.randomsToAdd = Math.Clamp(this.randomsToAdd, 1, capacity);
            ImGui.SameLine(0, 8);
            if (ReyLayout.Button("##bgAddPurchase", "ADD PURCHASE", new Vector2(154, 34), ReyLayout.Gold, true))
            {
                if (!session.AddSale(config.TargetPlayerName, this.randomsToAdd))
                    Plugin.ChatGui.PrintError($"[BarGame] {session.LastNotice}");
                else if (config.AutoStartCountingAfterAdd && !session.IsRunning)
                    this.plugin.StartRaffle();
            }
        }
        else ImGui.TextColored(ReyLayout.Muted, "Roll limit reached for this round");

        ImGui.Separator();
        BarGameTheme.Heading("GAME CONTROLS");
        // One action group = one button size (REY UI/LAYOUT).
        const float actionWidth = 132f;
        const float actionHeight = 34f;
        const float actionGap = 8f;
        // Start is a recovery control only; Add Purchase auto-starts by default.
        if (!session.IsRunning && session.RemainingRolls > 0 && session.WinningRoll is null)
        {
            if (ReyLayout.Button("##bgRun", "START COUNTING", new Vector2(actionWidth, actionHeight), ReyLayout.Mint, true))
                this.plugin.StartRaffle();
            ImGui.SameLine(0, actionGap);
        }
        if (ReyLayout.Button("##bgReset", "RESET SESSION", new Vector2(actionWidth, actionHeight), ReyLayout.Rose, true))
            this.RequestConfirmation(Confirmation.ResetSession);
        ImGui.SameLine(0, actionGap);
        if (ReyLayout.Button("##bgSendAd", "SHOUT GAME AD", new Vector2(actionWidth, actionHeight), ReyLayout.Mist, true))
            this.plugin.ShoutGameAnnouncement();
    }

    private void DrawLivePanel()
    {
        BarGameTheme.Heading("LIVE SESSION");
        var recent = this.plugin.GetRecentRandomRolls();
        if (recent.Length == 0)
        {
            ImGui.TextDisabled("Waiting for /random results");
            return;
        }

        ImGui.TextDisabled("LAST FIVE /RANDOMS");
        var width = ImGui.GetContentRegionAvail().X;
        for (var i = 0; i < recent.Length; i++)
        {
            var roll = recent[i];
            var pos = ImGui.GetCursorScreenPos();
            var draw = ImGui.GetWindowDrawList();
            ReyLayout.Fill(draw, pos, new Vector2(width, 39), i % 2 == 0 ? ReyLayout.Raised : ReyLayout.Surface, 8);
            draw.PushClipRect(pos + new Vector2(11, 0), pos + new Vector2(MathF.Max(12, width - 145), 39), true);
            draw.AddText(pos + new Vector2(12, 11), ReyLayout.Color(ReyLayout.Ivory), roll.PlayerName);
            draw.PopClipRect();
            draw.AddText(pos + new Vector2(width - 130, 11), ReyLayout.Color(roll.Winner ? ReyLayout.Gold : ReyLayout.Mist),
                roll.Value.ToString(CultureInfo.InvariantCulture));
            draw.AddText(pos + new Vector2(width - 78, 12), ReyLayout.Color(roll.Winner ? ReyLayout.Gold : roll.Counted ? ReyLayout.Mint : ReyLayout.Muted),
                roll.Winner ? "WINNER" : roll.Counted ? "COUNTED" : "SEEN");
            ImGui.Dummy(new Vector2(width, 37));
            if (ImGui.IsItemHovered() && !roll.Counted)
                ImGui.SetTooltip("Authentic /random observed; not counted toward purchased rolls");
        }
    }

    private void DrawSalesPage()
    {
        var size = ImGui.GetContentRegionAvail();
        // Keep summary/actions fixed, and allocate table height for actual sales rows.
        // Large histories use the available workspace and scroll only within the table.
        var salesCount = this.plugin.Configuration.SalesLedger.Count;
        var preferredHeight = salesCount == 0 ? 244f : 248f + (salesCount + 2) * 29f;
        var panelHeight = MathF.Min(MathF.Max(1f, size.Y - 1f), preferredHeight);
        if (ReyLayout.BeginPanel("##bgLedger", new Vector2(size.X, panelHeight),
                16f, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            this.DrawSalesHistory();
        ReyLayout.EndPanel();
    }
}
