using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ProJict.App.ViewModels;

namespace ProJict.App.Views;

/// <summary>Keyboard handling for the discussion panel: Ctrl+Enter posts, Esc cancels a reply.</summary>
public partial class DiscussionPanel : UserControl
{
    public DiscussionPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Dispatcher.BeginInvoke(ScrollToHighlighted, DispatcherPriority.Loaded);
    }

    /// <summary>Scrolls to the comment the panel was opened for, if any.</summary>
    private void ScrollToHighlighted()
    {
        var body = Descendants(ThreadScroll).OfType<Border>().FirstOrDefault(b => b.Name == "Body" && b.DataContext is CommentViewModel { IsHighlighted: true });
        body?.BringIntoView();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private void NewThread_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control || DataContext is not DiscussionViewModel vm) return;
        if (vm.PostCommand.CanExecute(null)) vm.PostCommand.Execute(null);
        e.Handled = true;
    }

    private void ReplyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CommentViewModel comment }) return;
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (comment.SendReplyCommand.CanExecute(null)) comment.SendReplyCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            comment.CancelReplyCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Clicking Reply opens the reply box under the comment; put the cursor in it.</summary>
    private void ReplyBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox box && Window.GetWindow(box)?.IsActive == true)
            Dispatcher.BeginInvoke(() => box.Focus(), DispatcherPriority.Input);
    }
}
