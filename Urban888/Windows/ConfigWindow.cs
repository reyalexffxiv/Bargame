using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Urban888.Windows;

/// <summary>
/// Lightweight config window.
/// 
/// The main raffle controls live in MainWindow. This window exists so the
/// Dalamud plugin installer's config button has a clean destination.
/// </summary>
public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public ConfigWindow(Plugin plugin)
        : base("Urban888 Settings###Urban888Config")
    {
        this.plugin = plugin;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        ImGui.TextUnformatted("Urban888 settings are available in the main window.");

        if (ImGui.Button("Open Main Window"))
            this.plugin.ToggleMainUi();
    }
}
