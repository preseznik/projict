namespace ProJict.Core.Model;

/// <summary>Someone who worked on an item: wrote in its history or its discussion.</summary>
public sealed record Contributor(string Name, bool Agent);

/// <summary>Threaded comments on items, categories and groups.</summary>
public static class Discussions
{
    /// <summary>Every comment with the one it replies to (null for a thread's first comment), in reading order.</summary>
    public static IEnumerable<(Comment Comment, Comment? Parent)> Walk(this List<Comment>? threads)
    {
        foreach (var comment in threads ?? [])
            foreach (var entry in Walk(comment, null))
                yield return entry;

        static IEnumerable<(Comment, Comment?)> Walk(Comment comment, Comment? parent)
        {
            yield return (comment, parent);
            foreach (var reply in comment.Replies ?? [])
                foreach (var entry in Walk(reply, comment))
                    yield return entry;
        }
    }

    /// <summary>The files posted in a discussion.</summary>
    public static IEnumerable<Attachment> Attachments(this List<Comment>? threads) =>
        threads.Walk().SelectMany(e => e.Comment.Attachments ?? []);

    /// <summary>Comments that weren't deleted.</summary>
    public static int CountComments(this List<Comment>? threads) => threads.Walk().Count(e => !e.Comment.Deleted);

    public static Comment? Find(this List<Comment>? threads, Guid id) =>
        threads.Walk().Select(e => e.Comment).FirstOrDefault(c => c.Id == id);

    /// <summary>Adds the comment as a new thread, or as a reply; returns the list to store (created when there was none).</summary>
    public static List<Comment> Post(List<Comment>? threads, Comment comment, Guid? replyTo)
    {
        threads ??= [];
        if (replyTo is not { } parentId)
        {
            threads.Add(comment);
            return threads;
        }
        var parent = threads.Find(parentId) ?? throw new ArgumentException("The comment to reply to isn't there any more.");
        (parent.Replies ??= []).Add(comment);
        return threads;
    }

    /// <summary>
    /// Removes a comment. One with replies stays as "deleted" so the thread below it still reads correctly; deleted
    /// comments left without replies go too. Returns the list to store (null once empty).
    /// </summary>
    public static List<Comment>? Remove(List<Comment>? threads, Guid id)
    {
        if (threads == null) return null;
        var entry = threads.Walk().FirstOrDefault(e => e.Comment.Id == id);
        if (entry.Comment == null) return threads;
        var (comment, parent) = entry;
        if (comment.Replies is { Count: > 0 })
        {
            comment.Deleted = true;
            comment.Text = "";
            comment.Attachments = null;
            return threads;
        }
        if (parent == null) threads.Remove(comment);
        else
        {
            parent.Replies!.Remove(comment);
            if (parent.Replies.Count == 0) parent.Replies = null;
            if (parent.Deleted && parent.Replies == null) return Remove(threads, parent.Id);
        }
        return threads.Count == 0 ? null : threads;
    }

    /// <summary>Everyone who wrote in the item's history or discussion, in the order they first did.</summary>
    public static IReadOnlyList<Contributor> Contributors(this TodoItem item)
    {
        var entries = (item.Activity ?? []).Select(a => (a.At, a.By, a.Agent))
            .Concat(item.Comments.Walk().Select(e => (e.Comment.At, e.Comment.By, e.Comment.Agent)))
            .Where(e => !string.IsNullOrWhiteSpace(e.By))
            .OrderBy(e => e.At);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return entries.Where(e => seen.Add(e.By)).Select(e => new Contributor(e.By, e.Agent)).ToList();
    }
}
