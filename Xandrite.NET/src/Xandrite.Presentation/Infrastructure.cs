using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Xandrite.Presentation;

/// <summary>Base for view models: raises PropertyChanged when a value really changes.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>A command that runs a delegate.</summary>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Runs work on the UI thread. The WPF app passes its Dispatcher; tests run things straight away.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>What the person chose when removing a folder from the library.</summary>
public enum RemoveChoice { Cancel, Keep, Purge }

/// <summary>Everything the view models need from the window system: dialogs, the clipboard, Explorer, the viewer.</summary>
public interface ILibraryShell
{
    /// <summary>A folder picker. Null if cancelled.</summary>
    string? PickFolder(string title);

    RemoveChoice ConfirmRemoveFolder(string folder, long files);

    void ShowMessage(string title, string message);

    void CopyText(string text);

    /// <summary>Opens Explorer on a folder, or with a file selected.</summary>
    void Reveal(string path, bool selectFile);

    /// <summary>A file to save to. Null if cancelled.</summary>
    string? PickSaveFile(string title, string suggestedName, string filter);

    /// <summary>The About box.</summary>
    void ShowAbout(AboutInfo about);

    /// <summary>Opens a file in the program Windows uses for it (shows it in Explorer if there is none).</summary>
    void OpenFile(string path);

    /// <summary>Shows a model in the viewer.</summary>
    void OpenModel(ViewerDocument document);
}

/// <summary>What the About box says.</summary>
public sealed record AboutInfo(string Version, string Description, string Updates, string Link);

/// <summary>A model for the viewer: its bytes, and whether it was opened from the library (only then can it be rated).</summary>
public sealed record ViewerDocument(string Name, string Format, byte[] Bytes, bool FromLibrary);
