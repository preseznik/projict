using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

public enum DiscussionKind { Item, Category, Group }

/// <summary>What a discussion is about: an item or category (by id, so it survives undo) or a group (by name).</summary>
public sealed record DiscussionTarget(DiscussionKind Kind, Guid ProjectId, Guid TargetId, string? Group)
{
    public static DiscussionTarget InProject(Project project, Guid targetId) =>
        new(project.Find(targetId) != null ? DiscussionKind.Item : DiscussionKind.Category, project.Id, targetId, null);

    public static DiscussionTarget ForGroup(string group) => new(DiscussionKind.Group, Guid.Empty, Guid.Empty, group.Trim());
}

/// <summary>
/// The side panel with the threads of one item, category or group. It follows changes from anywhere (an agent's
/// reply, undo) by updating its comments in place, so a half-written reply keeps its text and focus.
/// </summary>
public sealed partial class DiscussionViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly AppState _state;

    public DiscussionViewModel(MainViewModel main, AppState state, DiscussionTarget target)
    {
        _main = main;
        _state = state;
        Target = target;
        Sync();
    }

    public DiscussionTarget Target { get; }
    public ObservableCollection<CommentViewModel> Threads { get; } = [];

    public string KindText => Target.Kind switch
    {
        DiscussionKind.Item => "Item",
        DiscussionKind.Category => "Category",
        _ => "Group",
    };

    [ObservableProperty] private string _subject = "";
    [ObservableProperty] private string _location = "";
    [ObservableProperty] private bool _isGone;
    [ObservableProperty] private int _count;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PostCommand))]
    private string _newText = "";

    public bool IsEmpty => Threads.Count == 0 && !IsGone;

    /// <summary>False in another ProJict's project where your role doesn't include discussions.</summary>
    public bool CanWrite => !IsGone && (Project?.Remote is not { } remote || (remote.Connected && ProJict.Core.Sharing.GuestAccess.CanComment(remote.Role)));
    public string SignedAs => $"Posting as {_state.UserName}";

    private Project? Project => Target.Kind == DiscussionKind.Group ? null : _state.Projects.FirstOrDefault(p => p.Id == Target.ProjectId);

    private List<Comment>? Comments()
    {
        if (Target.Kind == DiscussionKind.Group) return _state.Settings.CommentsFor(Target.Group!);
        var project = Project;
        return project?.Find(Target.TargetId)?.Item.Comments ?? project?.Categories.FirstOrDefault(c => c.Id == Target.TargetId)?.Comments;
    }

    /// <summary>Brings the panel up to date with the stored threads.</summary>
    public void Sync()
    {
        var project = Project;
        switch (Target.Kind)
        {
            case DiscussionKind.Group:
                Subject = Target.Group!;
                Location = "Projects in this group";
                IsGone = !_state.Settings.Groups.Contains(Target.Group!, StringComparer.OrdinalIgnoreCase);
                break;
            default:
                var loc = project?.Find(Target.TargetId);
                var category = loc?.Category ?? project?.Categories.FirstOrDefault(c => c.Id == Target.TargetId);
                IsGone = category == null;
                Subject = loc != null ? loc.Item.DisplayText().Replace("\r\n", " ").Replace('\n', ' ') : category?.Name ?? "(removed)";
                Location = project == null ? "" : loc != null ? $"{project.Name} › {loc.Category.Name}" : project.Name;
                break;
        }
        var comments = Comments();
        CommentViewModel.SyncList(Threads, comments, this, 0);
        Count = comments.CountComments();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanWrite));
        _state.MarkRead(Target.Kind == DiscussionKind.Group ? AppState.ReadKey(Target.Group!) : AppState.ReadKey(Target.TargetId), comments);
    }

    private bool CanPost() => NewText.Trim().Length > 0 && !IsGone;

    [RelayCommand(CanExecute = nameof(CanPost))]
    private void Post()
    {
        if (Post(NewText, null)) NewText = "";
    }

    [RelayCommand] private void Close() => _main.CloseDiscussion();

    /// <summary>Posts your comment as a new thread or a reply; false (with the reason in the status bar) when it couldn't.</summary>
    public bool Post(string text, Guid? replyTo)
    {
        if (text.Trim().Length == 0 || IsGone) return false;
        var comment = new Comment { By = _state.UserName, Text = text.Trim() };
        try
        {
            if (Target.Kind == DiscussionKind.Group) _state.PostGroupComment(Target.Group!, comment, replyTo);
            else _main.GetProjectViewModel(Project!).PostComment(Target.TargetId, comment, replyTo);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _state.StatusText = ex.Message;
            return false;
        }
        return true;
    }

    /// <summary>A whole comment to the clipboard (parts of one are selected and copied in the panel itself).</summary>
    public void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            _state.StatusText = "Comment copied.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _state.StatusText = "Clipboard is busy, try again.";
        }
    }

    public void Remove(CommentViewModel comment)
    {
        if (Target.Kind == DiscussionKind.Group)
        {
            // Group discussions live in the workspace settings, outside the projects' undo.
            if (MessageBox.Show(Application.Current.MainWindow!, "Delete this comment? This can't be undone.", "Delete comment",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _state.RemoveGroupComment(Target.Group!, comment.Id);
            return;
        }
        _main.GetProjectViewModel(Project!).RemoveComment(Target.TargetId, comment.Id);
        _state.StatusText = "Comment deleted (Ctrl+Z on the board brings it back).";
    }
}

/// <summary>One comment in the panel, with its replies below it.</summary>
public sealed partial class CommentViewModel : ObservableObject
{
    private readonly DiscussionViewModel _owner;

    public CommentViewModel(DiscussionViewModel owner, Comment model, int depth)
    {
        _owner = owner;
        Model = model;
        Depth = depth;
        SyncList(Replies, model.Replies, owner, depth + 1);
    }

    public Comment Model { get; private set; }
    public Guid Id => Model.Id;
    public int Depth { get; }
    public ObservableCollection<CommentViewModel> Replies { get; } = [];
    public bool HasReplies => Replies.Count > 0;

    public string By => Model.Deleted ? "" : Model.By;
    public bool IsAgent => Model.Agent;
    /// <summary>A robot for AI agents, a person otherwise.</summary>
    public string Glyph => Model.Agent ? "" : "";
    public string Text => Model.Deleted ? "(deleted)" : Model.Text;
    public bool IsDeleted => Model.Deleted;
    public string WhenText => Model.At.Date == DateTime.Today
        ? Model.At.ToString("HH:mm", CultureInfo.CurrentCulture)
        : Model.At.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
    public string WhenToolTip => Model.At.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture);

    [ObservableProperty] private bool _isReplying;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendReplyCommand))]
    private string _replyText = "";

    [RelayCommand] private void Reply() => IsReplying = true;

    [RelayCommand]
    private void CancelReply()
    {
        IsReplying = false;
        ReplyText = "";
    }

    private bool CanSendReply() => ReplyText.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSendReply))]
    private void SendReply()
    {
        if (!_owner.Post(ReplyText, Id)) return;
        ReplyText = "";
        IsReplying = false;
    }

    [RelayCommand] private void Delete() => _owner.Remove(this);

    [RelayCommand] private void Copy() => _owner.Copy(Model.Text);

    private void Update(Comment model)
    {
        var changed = model.Text != Model.Text || model.Deleted != Model.Deleted;
        Model = model;
        if (changed) OnPropertyChanged(string.Empty);
        SyncList(Replies, model.Replies, _owner, Depth + 1);
        OnPropertyChanged(nameof(HasReplies));
    }

    /// <summary>Makes <paramref name="items"/> match <paramref name="models"/> by id, keeping existing view models (and their drafts).</summary>
    public static void SyncList(ObservableCollection<CommentViewModel> items, List<Comment>? models, DiscussionViewModel owner, int depth)
    {
        var list = models ?? [];
        for (var i = items.Count - 1; i >= 0; i--)
            if (list.All(m => m.Id != items[i].Id)) items.RemoveAt(i);
        for (var i = 0; i < list.Count; i++)
        {
            var existing = items.FirstOrDefault(v => v.Id == list[i].Id);
            if (existing == null)
            {
                items.Insert(i, new CommentViewModel(owner, list[i], depth));
                continue;
            }
            var at = items.IndexOf(existing);
            if (at != i) items.Move(at, i);
            existing.Update(list[i]);
        }
    }
}
