using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ProJict.App.Services;
using ProJict.App.ViewModels;

namespace ProJict.App.Views;

/// <summary>Keyboard, focus and drag &amp; drop plumbing for the outline board; all edits go to <see cref="ProjectViewModel"/>.</summary>
public partial class ProjectView : UserControl
{
    private ProjectViewModel? _vm;
    private Point _dragStart;
    private object? _dragCandidate;

    public ProjectView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        // Row text boxes would otherwise swallow the mouse wheel.
        BoardScroll.PreviewMouseWheel += (_, e) =>
        {
            BoardScroll.ScrollToVerticalOffset(BoardScroll.VerticalOffset - e.Delta * 0.6);
            e.Handled = true;
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.FocusRequested -= OnFocusRequested;
            _vm.FocusAddItemRequested -= OnFocusAddItem;
        }
        _vm = DataContext as ProjectViewModel;
        if (_vm != null)
        {
            _vm.FocusRequested += OnFocusRequested;
            _vm.FocusAddItemRequested += OnFocusAddItem;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && _vm != null)
        {
            _vm.SelectedTab = 0;
            Dispatcher.BeginInvoke(() =>
            {
                FilterBox.Focus();
                FilterBox.SelectAll();
            }, DispatcherPriority.Loaded);
            e.Handled = true;
        }
    }

    private void FilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm != null)
        {
            _vm.FilterText = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Down && _vm?.VisibleRows().FirstOrDefault() is { } first)
        {
            OnFocusRequested(first.Item.Id, -1);
            e.Handled = true;
        }
    }

    // ───────────── Focus ─────────────

    private void OnFocusRequested(Guid itemId, int caret)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var box = FindRowTextBox(itemId);
            if (box == null)
            {
                UpdateLayout();
                box = FindRowTextBox(itemId);
            }
            if (box == null) return;
            box.Focus();
            box.CaretIndex = caret < 0 ? box.Text.Length : Math.Min(caret, box.Text.Length);
            box.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private void OnFocusAddItem(CategoryViewModel category)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var box = FindVisual<TextBox>(Board, t => t.Uid == "AddItem" && t.DataContext == category);
            if (box == null) return;
            box.Focus();
            box.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private TextBox? FindRowTextBox(Guid itemId) =>
        FindVisual<TextBox>(Board, t => t.Uid == "RowText" && t.DataContext is ItemRowViewModel r && r.Item.Id == itemId && t.IsVisible);

    private static T? FindVisual<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && predicate(match)) return match;
            var found = FindVisual(child, predicate);
            if (found != null) return found;
        }
        return null;
    }

    // ───────────── Row keyboard ─────────────

    private void RowText_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ItemRowViewModel row }) row.Owner.OnRowGotFocus(row);
    }

    private void RowText_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ItemRowViewModel row }) row.Owner.OnRowLostFocus(row);
    }

    private void Header_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => _vm?.OnHeaderGotFocus();

    private void RowText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not ItemRowViewModel row) return;
        var vm = row.Owner;
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var caret = box.CaretIndex;

        switch (key)
        {
            case Key.Enter when mods == ModifierKeys.Shift:
                var at = box.CaretIndex;
                box.Text = box.Text.Insert(at, "\n");
                box.CaretIndex = at + 1;
                break;
            case Key.Enter when mods == ModifierKeys.Control:
                vm.SetState(row, row.IsDone ? Core.Model.ItemState.Todo : Core.Model.ItemState.Done);
                break;
            case Key.Enter when mods == ModifierKeys.None:
                vm.InsertSibling(row, before: caret == 0 && box.Text.Length > 0);
                break;
            case Key.Tab when mods == ModifierKeys.None:
                vm.Indent(row, caret);
                break;
            case Key.Tab when mods == ModifierKeys.Shift:
                vm.Outdent(row, caret);
                break;
            case Key.Up when mods == ModifierKeys.Alt:
                vm.Move(row, -1, caret);
                break;
            case Key.Down when mods == ModifierKeys.Alt:
                vm.Move(row, +1, caret);
                break;
            case Key.Up when mods == ModifierKeys.None && box.GetLineIndexFromCharacterIndex(caret) <= 0:
                vm.FocusRelative(row, -1);
                break;
            case Key.Down when mods == ModifierKeys.None && box.GetLineIndexFromCharacterIndex(caret) >= box.LineCount - 1:
                vm.FocusRelative(row, +1);
                break;
            case Key.I when mods == ModifierKeys.Control:
                vm.SetState(row, row.IsInProgress ? Core.Model.ItemState.Todo : Core.Model.ItemState.InProgress);
                break;
            case Key.V when mods == ModifierKeys.Control && ClipboardAttachments.HasAttachment():
                vm.PasteAttachments(row);
                break;
            case Key.Space when mods == ModifierKeys.Control:
                row.IsSelected = !row.IsSelected;
                break;
            case Key.Back when mods == ModifierKeys.None && box.Text.Length == 0:
                vm.Delete(row, focusPrevious: true);
                break;
            case Key.Delete when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                vm.Delete(row, focusPrevious: false);
                break;
            case Key.Escape:
                if (vm.AllRows.Any(r => r.IsSelected)) vm.ClearSelectionCommand.Execute(null);
                else Keyboard.ClearFocus();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void RowText_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not ItemRowViewModel row) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = box };

        MenuItem Item(string header, ICommand command, object? parameter = null, string? gesture = null) =>
            new() { Header = header, Command = command, CommandParameter = parameter, InputGestureText = gesture ?? "" };

        menu.Items.Add(Item(row.IsDone ? "Mark not done" : "Mark done", row.ToggleDoneCommand, gesture: "Ctrl+Enter"));
        menu.Items.Add(Item(row.IsInProgress ? "Not in progress" : "In progress", row.ToggleInProgressCommand, gesture: "Ctrl+I"));
        menu.Items.Add(Item("Add sub-item", row.AddChildCommand));
        menu.Items.Add(Item("Attach files…", row.AttachCommand));
        if (ClipboardAttachments.HasAttachment()) menu.Items.Add(Item("Paste as attachment", row.PasteAttachmentsCommand, gesture: "Ctrl+V"));

        var tags = new MenuItem { Header = "Tag" };
        foreach (var tag in ProjectViewModel.TagChoices)
            tags.Items.Add(new MenuItem { Header = tag, Command = row.SetTagCommand, CommandParameter = tag, IsChecked = row.Tag == tag });
        tags.Items.Add(new Separator());
        tags.Items.Add(Item("No tag", row.SetTagCommand, null));
        menu.Items.Add(tags);

        var move = new MenuItem { Header = "Move to category" };
        foreach (var category in row.Owner.Categories)
            move.Items.Add(Item(category.Name, row.MoveToCategoryCommand, category));
        menu.Items.Add(move);

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Copy as prompt", row.CopyPromptCommand));
        menu.Items.Add(new MenuItem { Header = "Select for prompt", Command = row.ToggleSelectedCommand, IsChecked = row.IsSelected, InputGestureText = "Ctrl+Space" });
        menu.Items.Add(Item("Promote to project…", row.PromoteCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Cut", Command = ApplicationCommands.Cut, CommandTarget = box });
        menu.Items.Add(new MenuItem { Header = "Copy", Command = ApplicationCommands.Copy, CommandTarget = box });
        menu.Items.Add(new MenuItem { Header = "Paste", Command = ApplicationCommands.Paste, CommandTarget = box });
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete", row.DeleteCommand, gesture: "Ctrl+Shift+Del"));
        menu.IsOpen = true;
    }

    /// <summary>Right-click on a row's checkbox: the two states the item isn't in.</summary>
    private void StateBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not CheckBox box || box.DataContext is not ItemRowViewModel row) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = box };
        foreach (var state in row.OtherStates)
        {
            var (header, gesture) = state switch
            {
                Core.Model.ItemState.Done => ("Mark as done", "Ctrl+Enter"),
                Core.Model.ItemState.InProgress => ("Mark as in progress", "Ctrl+I"),
                _ => ("Mark as to do", row.IsDone ? "Ctrl+Enter" : "Ctrl+I"),
            };
            menu.Items.Add(new MenuItem { Header = header, InputGestureText = gesture, Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => row.Owner.SetState(row, state)) });
        }
        menu.IsOpen = true;
    }

    private void Attachment_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AttachmentViewModel attachment } element) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = element };
        menu.Items.Add(new MenuItem { Header = "Open", Command = attachment.OpenCommand });
        menu.Items.Add(new MenuItem { Header = "Show in folder", Command = attachment.ShowInFolderCommand });
        menu.Items.Add(new MenuItem { Header = "Copy path", Command = attachment.CopyPathCommand });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Remove", Command = attachment.RemoveCommand });
        menu.IsOpen = true;
    }

    private void AddItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CategoryViewModel category }) category.Owner.RequestAddItem(category);
    }

    private void CategoryMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CategoryViewModel category } button) return;
        var menu = new ContextMenu { PlacementTarget = button };
        menu.Items.Add(new MenuItem { Header = "Move up", Command = category.MoveUpCommand });
        menu.Items.Add(new MenuItem { Header = "Move down", Command = category.MoveDownCommand });
        menu.Items.Add(new MenuItem { Header = category.IsDoneExpanded ? "Hide done" : "Show done", Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => category.IsDoneExpanded = !category.IsDoneExpanded) });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Delete category…", Command = category.DeleteCommand });
        menu.IsOpen = true;
    }

    // ───────────── Drag & drop ─────────────

    private void Grip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ItemRowViewModel row }) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            row.IsSelected = !row.IsSelected;
            e.Handled = true;
            return;
        }
        _dragStart = e.GetPosition(this);
        _dragCandidate = row;
    }

    private void CategoryGrip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CategoryViewModel category }) return;
        _dragStart = e.GetPosition(this);
        _dragCandidate = category;
    }

    private void Grip_MouseMove(object sender, MouseEventArgs e) => TryStartDrag(sender, e);
    private void CategoryGrip_MouseMove(object sender, MouseEventArgs e) => TryStartDrag(sender, e);

    private void TryStartDrag(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate == null) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var payload = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(payload.GetType(), payload), DragDropEffects.Move);
        ClearDropTargets();
    }

    private static T? Payload<T>(DragEventArgs e) where T : class =>
        e.Data.GetDataPresent(typeof(T)) ? e.Data.GetData(typeof(T)) as T : null;

    private void Row_DragOver(object sender, DragEventArgs e)
    {
        var target = (sender as FrameworkElement)?.DataContext as ItemRowViewModel;
        var source = Payload<ItemRowViewModel>(e);
        e.Effects = source != null && target != null && source.Item != target.Item ? DragDropEffects.Move : DragDropEffects.None;
        if (target != null) target.IsDropTarget = e.Effects == DragDropEffects.Move;
        e.Handled = true;
    }

    private void Row_DragLeave(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ItemRowViewModel row) row.IsDropTarget = false;
    }

    private void Row_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ItemRowViewModel target } element) return;
        target.IsDropTarget = false;
        if (Payload<ItemRowViewModel>(e) is not { } source) return;
        var after = e.GetPosition(element).Y > element.ActualHeight / 2;
        e.Handled = true;
        target.Owner.MoveItem(source, target, after);
    }

    // Files from Explorer are taken in the preview, before the row's text box can claim them.
    private void Row_PreviewFileDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || (sender as FrameworkElement)?.DataContext is not ItemRowViewModel row) return;
        e.Effects = DragDropEffects.Copy;
        row.IsDropTarget = true;
        e.Handled = true;
    }

    private void Row_PreviewFileDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || (sender as FrameworkElement)?.DataContext is not ItemRowViewModel row) return;
        row.IsDropTarget = false;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = row.Owner.AttachFiles(row, paths);
    }

    private void Category_DragOver(object sender, DragEventArgs e)
    {
        var target = (sender as FrameworkElement)?.DataContext as CategoryViewModel;
        var ok = target != null && (Payload<ItemRowViewModel>(e) != null || Payload<CategoryViewModel>(e) is { } c && c != target);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (target != null) target.IsDropTarget = ok;
        e.Handled = true;
    }

    private void Category_DragLeave(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CategoryViewModel category) category.IsDropTarget = false;
    }

    private void Category_Drop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CategoryViewModel target) return;
        target.IsDropTarget = false;
        e.Handled = true;
        if (Payload<ItemRowViewModel>(e) is { } row) target.Owner.MoveToCategory(row, target);
        else if (Payload<CategoryViewModel>(e) is { } category) target.Owner.MoveCategoryTo(category, target);
    }

    private void ClearDropTargets()
    {
        if (_vm == null) return;
        foreach (var category in _vm.Categories)
        {
            category.IsDropTarget = false;
            foreach (var row in category.AllRows) row.IsDropTarget = false;
        }
    }
}
