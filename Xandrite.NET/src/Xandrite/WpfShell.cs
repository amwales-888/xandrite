using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Xandrite.Presentation;

namespace Xandrite.App;

/// <summary>Runs view-model work on the WPF UI thread.</summary>
public sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action) => dispatcher.BeginInvoke(action);
}

/// <summary>Dialogs, the clipboard, Explorer and the preview window, for the view models.</summary>
public sealed class WpfShell : ILibraryShell
{
    private ViewerWindow? _viewerWindow;

    public Window? Owner { get; set; }

    /// <summary>The viewer's view model (one for the app); a window is made for it whenever a model is opened.</summary>
    public ViewerViewModel? Viewer { get; set; }

    /// <summary>The window a dialog belongs to: the active one (for example Manage folders), else the library window.</summary>
    private Window? Current => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Owner;

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        bool? picked = Current is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return picked == true ? dialog.FolderName : null;
    }

    public RemoveChoice ConfirmRemoveFolder(string folder, long files)
    {
        string text =
            $"Take this folder out of the library?\n\n{folder}\n({files:N0} files)\n\n" +
            "Your files are never touched.\n\n" +
            "Also delete the thumbnails, saved views and ratings of its models (unless they are found in another folder)?\n\n" +
            "Yes: remove the folder and delete them\n" +
            "No: remove the folder and keep them, so adding it again brings them back\n" +
            "Cancel: keep the folder";
        var answer = Current is { } owner
            ? MessageBox.Show(owner, text, "Remove folder", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(text, "Remove folder", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
        return answer switch
        {
            MessageBoxResult.Yes => RemoveChoice.Purge,
            MessageBoxResult.No => RemoveChoice.Keep,
            _ => RemoveChoice.Cancel,
        };
    }

    public void ShowMessage(string title, string message)
    {
        if (Current is { } owner) MessageBox.Show(owner, message, title);
        else MessageBox.Show(message, title);
    }

    public void CopyText(string text)
    {
        // The clipboard can be busy for a moment while another program holds it.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (COMException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
        }
    }

    public void Reveal(string path, bool selectFile)
    {
        // Explorer wants exactly: /select,"C:\folder\file.stl"
        var start = new ProcessStartInfo("explorer.exe", selectFile ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = false };
        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ShowMessage("Show in folder", ex.Message);
        }
    }

    public void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Reveal(path, selectFile: true);   // nothing is set up to open this kind of file
        }
    }

    public string? PickSaveFile(string title, string suggestedName, string filter)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = suggestedName, Filter = filter, OverwritePrompt = true };
        bool? picked = Current is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return picked == true ? dialog.FileName : null;
    }

    public void ShowAbout(AboutInfo about)
    {
        var window = new AboutWindow(about);
        if (Current is { } owner) window.Owner = owner;
        window.ShowDialog();
    }

    public void OpenModel(ViewerDocument document)
    {
        if (Viewer is null) return;
        if (_viewerWindow is null)
        {
            _viewerWindow = new ViewerWindow(Viewer);
            _viewerWindow.Closed += (_, _) => _viewerWindow = null;
            _viewerWindow.Show();
        }
        else
        {
            if (_viewerWindow.WindowState == WindowState.Minimized) _viewerWindow.WindowState = WindowState.Normal;
            _viewerWindow.Activate();
        }
        _ = Viewer.LoadAsync(document);
    }
}
