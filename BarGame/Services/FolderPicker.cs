using System;
using System.Threading;
using System.Windows.Forms;

namespace BarGame.Services;

/// <summary>
/// Native folder selection on an STA thread so the FFXIV render thread remains responsive.
/// Same non-blocking approach as VenueHost's file dialogs.
/// </summary>
internal sealed class FolderPicker
{
    private readonly object gate = new();
    private bool finished;
    private string? selectedDirectory;
    private Exception? failure;

    private FolderPicker() { }

    public static FolderPicker Start(string currentDirectory)
    {
        var pending = new FolderPicker();
        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = "Select BarGame CSV export folder",
                    UseDescriptionForTitle = true,
                    SelectedPath = currentDirectory,
                    ShowNewFolderButton = true,
                };
                var result = dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
                pending.Complete(result, null);
            }
            catch (Exception ex)
            {
                pending.Complete(null, ex);
            }
        }) { Name = "BarGameFolderPicker", IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return pending;
    }

    private void Complete(string? directory, Exception? error)
    {
        lock (this.gate)
        {
            this.selectedDirectory = directory;
            this.failure = error;
            this.finished = true;
        }
    }

    public bool TryGetResult(out string? directory, out Exception? error)
    {
        lock (this.gate)
        {
            directory = this.selectedDirectory;
            error = this.failure;
            return this.finished;
        }
    }
}
