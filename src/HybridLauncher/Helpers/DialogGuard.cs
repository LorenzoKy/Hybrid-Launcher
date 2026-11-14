using Microsoft.UI.Xaml.Controls;

namespace HybridLauncher;

/// <summary>
/// One dialog at a time, window-wide (2.1-rc.10 audit). WinUI throws (0x800F1000, "only a single ContentDialog can be open")
/// when a second one starts while the first is up: a double click on a menu entry, or two actions that each open one, ended
/// in an error. Every dialog of the app opens through <see cref="ShowOnceAsync"/>: while one is open, another is not shown
/// and reads as cancelled.
/// </summary>
public static class DialogGuard
{
    static ContentDialog? _open;

    /// <summary>Whether a dialog of the app is open now.</summary>
    public static bool Busy => _open is not null;

    public static async Task<ContentDialogResult> ShowOnceAsync(this ContentDialog dialog)
    {
        if (_open is not null)
        {
            Core.Log.Info($"Dialog \"{dialog.Title}\" not shown: another one is open");
            return ContentDialogResult.None;
        }
        _open = dialog;
        // Free as soon as it closes (a flow may open the next one before this await returns).
        void Closed(ContentDialog d, ContentDialogClosedEventArgs e) { if (ReferenceEquals(_open, d)) _open = null; }
        dialog.Closed += Closed;
        try { return await dialog.ShowAsync(); }
        finally
        {
            dialog.Closed -= Closed;
            if (ReferenceEquals(_open, dialog)) _open = null;
        }
    }
}
