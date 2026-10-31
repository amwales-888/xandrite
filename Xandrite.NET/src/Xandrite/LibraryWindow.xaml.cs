using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Xandrite.Presentation;

namespace Xandrite.App;

/// <summary>
/// The library window. The logic is in <see cref="LibraryViewModel"/>; this file turns mouse and keyboard input into
/// calls on it, builds the right-click menus, and keeps the grid's column count in step with the window width.
/// </summary>
public partial class LibraryWindow : Window
{
    private readonly LibraryViewModel _vm;

    public LibraryWindow(LibraryViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        _vm.ScrollRequested += ScrollIntoView;
    }

    /* ---------- the grid ---------- */

    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e) => UpdateViewportWidth();

    private void UpdateViewportWidth()
    {
        // The width tiles can use: the grid less its padding and the (always shown) scroll bar.
        double width = TileGrid.ActualWidth - TileGrid.Padding.Left - TileGrid.Padding.Right - SystemParameters.VerticalScrollBarWidth;
        _vm.SetViewportWidth(width);
    }

    private static TileViewModel? TileOf(object sender) => (sender as FrameworkElement)?.DataContext as TileViewModel;

    private async void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (TileOf(sender) is not { } tile) return;
        e.Handled = true;
        TileGrid.Focus();
        if (e.ClickCount == 2)
        {
            await _vm.OpenAsync(tile);
            return;
        }
        var modifiers = Keyboard.Modifiers;
        _vm.Click(tile, ctrl: modifiers.HasFlag(ModifierKeys.Control), shift: modifiers.HasFlag(ModifierKeys.Shift));
    }

    private void OnTickMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (TileOf(sender) is not { } tile) return;
        e.Handled = true;   // not also a click on the tile underneath
        TileGrid.Focus();
        _vm.ToggleTick(tile);
    }

    private void OnGridBackgroundClick(object sender, MouseButtonEventArgs e)
    {
        TileGrid.Focus();
        if (e.OriginalSource is ScrollViewer or Border or VirtualizingStackPanel) _vm.ClearSelection();
    }

    private void OnTileRightClick(object sender, MouseButtonEventArgs e)
    {
        if (TileOf(sender) is not { } tile) return;
        e.Handled = true;
        var selection = _vm.SelectionForMenu(tile);
        var menu = new ContextMenu();
        string many = selection.Count > 1 ? $" ({selection.Count:N0})" : "";

        menu.Items.Add(Item("Open", async () => await _vm.OpenAsync(tile), enabled: selection.Count == 1));
        menu.Items.Add(Item("Show in folder", () => _vm.Reveal(selection), enabled: LibraryViewModel.CommonFolder(selection) is not null));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Download…" + many, async () => await _vm.DownloadAsync(selection)));
        menu.Items.Add(Item("Copy path" + many, () => _vm.CopyPaths(selection)));
        menu.Items.Add(Item("Copy SHA-256" + many, () => _vm.CopyHashes(selection)));
        menu.Items.Add(new Separator());

        var rate = new MenuItem { Header = "Rate" + many };
        rate.Items.Add(Item("Unrated", () => _vm.Rate(selection, null)));
        for (int stars = 0; stars <= 5; stars++)
        {
            int value = stars;
            string label = stars == 0 ? "0 stars" : new string('★', stars) + new string('☆', 5 - stars);
            rate.Items.Add(Item(label, () => _vm.Rate(selection, value)));
        }
        menu.Items.Add(rate);
        menu.Items.Add(Item("Find other copies of this model", () => _vm.FindCopies(tile), enabled: selection.Count == 1));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Regenerate thumbnail" + many, () => _vm.Redraw(selection, revert: false)));
        menu.Items.Add(Item("Revert to automatic view" + many, () => _vm.Redraw(selection, revert: true), enabled: selection.Any(t => t.HasCustomView)));

        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private void ScrollIntoView(TileViewModel tile)
    {
        int row = tile.Index / Math.Max(1, _vm.Columns);
        var container = TileGrid.ItemContainerGenerator.ContainerFromIndex(row) as FrameworkElement;
        if (container is null)
        {
            // The row is not built (it is off screen): scroll near it first, using the height of a row that is.
            if (FindScrollViewer(TileGrid) is { } scroll)
            {
                double rowHeight = TileGrid.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement first && first.ActualHeight > 0
                    ? first.ActualHeight
                    : _vm.TileSize + 60;
                foreach (var item in TileGrid.Items)
                {
                    if (TileGrid.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement built && built.ActualHeight > 0)
                    {
                        rowHeight = built.ActualHeight;
                        break;
                    }
                }
                scroll.ScrollToVerticalOffset(row * rowHeight);
                TileGrid.UpdateLayout();
            }
            container = TileGrid.ItemContainerGenerator.ContainerFromIndex(row) as FrameworkElement;
        }
        container?.BringIntoView();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    /* ---------- keyboard ---------- */

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox)
        {
            if (e.Key == Key.Escape && _vm.SearchText.Length > 0)
            {
                _vm.SearchText = "";
                e.Handled = true;
            }
            return;   // typing in the search box
        }
        if (e.OriginalSource is ComboBox or Slider || !_vm.HasRoots) return;

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Left: _vm.Move(-1, 0); break;
            case Key.Right: _vm.Move(1, 0); break;
            case Key.Up: _vm.Move(0, -1); break;
            case Key.Down: _vm.Move(0, 1); break;
            case Key.Enter when _vm.Current is { } current: await _vm.OpenAsync(current); break;
            case Key.A when ctrl: _vm.SelectAll(); break;
            case Key.F when ctrl: SearchBox.Focus(); SearchBox.SelectAll(); break;
            case Key.Escape: _vm.ClearSelection(); break;
            case Key.F5: await _vm.RescanAsync(null); break;
            case >= Key.D0 and <= Key.D5 when !ctrl: _vm.Rate(e.Key == Key.D0 ? null : e.Key - Key.D0); break;
            case >= Key.NumPad0 and <= Key.NumPad5 when !ctrl: _vm.Rate(e.Key == Key.NumPad0 ? null : e.Key - Key.NumPad0); break;
            default: return;
        }
        e.Handled = true;
    }

    /* ---------- the folder tree ---------- */

    private void OnFolderRightClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not FolderNodeViewModel node) return;
        e.Handled = true;
        node.IsSelected = true;
        var menu = new ContextMenu();
        if (node.Path is null)
        {
            menu.Items.Add(Item("Rescan all folders", async () => await _vm.RescanAsync(null), enabled: !_vm.IsScanning));
        }
        else
        {
            string path = node.Path;
            menu.Items.Add(Item(node.IsRoot ? "Rescan this folder" : "Rescan the library folder that holds this",
                async () => await _vm.RescanAsync(path), enabled: !_vm.IsScanning));
            menu.Items.Add(Item("Show in Explorer", () => _vm.RevealFolder(path)));
            if (node.IsRoot)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Remove from library…", () => _vm.RemoveFolder(path)));
            }
        }
        menu.PlacementTarget = FolderTree;
        menu.IsOpen = true;
    }

    /* ---------- menus and dropping folders ---------- */

    private void OnManageFolders(object sender, RoutedEventArgs e)
    {
        var dialog = new ManageFoldersWindow(_vm) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Added.Count > 0) _ = _vm.AddFoldersAsync(dialog.Added);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Any(Directory.Exists)
            ? DragDropEffects.Link
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            await _vm.AddFoldersAsync(paths.Where(Directory.Exists));
    }
}
