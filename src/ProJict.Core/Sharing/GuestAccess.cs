using System.Text.Json.Nodes;
using ProJict.Core.Model;
using ProJict.Core.Storage;

namespace ProJict.Core.Sharing;

/// <summary>What a guest may do in the host's projects; each role can do everything the ones before it can.</summary>
public enum GuestRole
{
    /// <summary>Sees the projects, changes nothing.</summary>
    Viewer,
    /// <summary>Also takes part in discussions.</summary>
    Commenter,
    /// <summary>Also adds new items (for testers or clients reporting bugs), but doesn't change existing ones.</summary>
    Reporter,
    /// <summary>Changes everything in the projects, but not their name, group, status or folder, and can't delete them.</summary>
    Editor,
}

/// <summary>
/// The host's check of every change a guest sends: whatever the guest's app claims, a viewer changes nothing, a commenter
/// only comments, a reporter only adds items and comments, and nobody signs in someone else's name.
/// </summary>
public static class GuestAccess
{
    public static bool CanComment(GuestRole role) => role >= GuestRole.Commenter;
    public static bool CanAddItems(GuestRole role) => role >= GuestRole.Reporter;
    public static bool CanEdit(GuestRole role) => role == GuestRole.Editor;

    public static string Describe(GuestRole role) => role switch
    {
        GuestRole.Viewer => "Viewer: sees the projects, changes nothing",
        GuestRole.Commenter => "Commenter: sees and discusses",
        GuestRole.Reporter => "Reporter: adds items and discusses, doesn't change existing ones",
        _ => "Editor: changes everything in the projects (not their name, group or folder)",
    };

    /// <summary>Only the host changes these; a guest's values are replaced by the host's.</summary>
    private static readonly string[] HostFields =
        ["id", "name", "key", "filePrefix", "group", "status", "isInbox", "repoPath", "gitHubUrl", "issues", "gitHubRelease", "archivedFiles", "createdAt", "updatedAt"];

    /// <summary>
    /// The guest's version of the project as the host will store it, or the reason it is refused. Comments, history
    /// entries, notes and releases the guest added are signed with the guest's name.
    /// </summary>
    public static (Project? Accepted, string? Refusal) Check(Project current, Project incoming, string guestName, GuestRole role)
    {
        var c = ProjectMerge.ToJson(current);
        var n = ProjectMerge.ToJson(incoming);
        foreach (var field in HostFields)
        {
            if (c[field] is { } value) n[field] = value.DeepClone();
            else n.Remove(field);
        }
        // Folding a category only changes your own view.
        var collapsed = Categories(c).ToDictionary(k => (string)k["id"]!, k => k["collapsed"]?.DeepClone());
        foreach (var category in Categories(n))
        {
            if (collapsed.TryGetValue((string)category["id"]!, out var value) && value != null) category["collapsed"] = value;
            else category.Remove("collapsed");
        }

        var before = AllComments(c);
        var after = AllComments(n);
        var commentsChanged = before.Count != after.Count;
        foreach (var (id, comment) in after)
        {
            if (!before.TryGetValue(id, out var old))
            {
                comment["by"] = guestName;
                comment.Remove("agent");
                commentsChanged = true;
                continue;
            }
            if (JsonNode.DeepEquals(Without(old, "replies"), Without(comment, "replies"))) continue;
            commentsChanged = true;
            if (!Mine(old, guestName)) return (null, "You can only change or remove your own comments.");
        }
        if (before.Keys.Except(after.Keys).Any(id => !Mine(before[id], guestName)))
            return (null, "You can only change or remove your own comments.");
        if (commentsChanged && !CanComment(role)) return (null, "As a viewer you can't change anything here.");

        if (!CanEdit(role))
        {
            var known = AllItems(c).Keys.ToHashSet();
            var stripped = Strip(n, newItemsOf: CanAddItems(role) ? known : null);
            if (!JsonNode.DeepEquals(Strip(c, newItemsOf: null), stripped))
                return (null, role switch
                {
                    GuestRole.Viewer => "As a viewer you can't change anything here.",
                    GuestRole.Commenter => "As a commenter you can only take part in discussions.",
                    _ => "As a reporter you can add items and comments, but not change existing items.",
                });
        }
        SignNewWork(c, n, guestName);
        return (Json.Deserialize<Project>(n.ToJsonString(Json.Options)), null);
    }

    private static bool Mine(JsonObject comment, string guestName) =>
        string.Equals((string?)comment["by"], guestName, StringComparison.OrdinalIgnoreCase) && comment["agent"] == null;

    /// <summary>New history entries, notes changes and releases carry the guest's name.</summary>
    private static void SignNewWork(JsonObject c, JsonObject n, string guestName)
    {
        var old = AllItems(c);
        foreach (var (id, item) in AllItems(n))
        {
            var known = old.TryGetValue(id, out var o) ? (o["activity"] as JsonArray)?.Count ?? 0 : 0;
            if (item["activity"] is not JsonArray log) continue;
            for (var i = known; i < log.Count; i++)
                if (log[i] is JsonObject entry)
                {
                    entry["by"] = guestName;
                    entry.Remove("agent");
                }
        }
        var notes = (c["notes"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(x => (string)x["id"]!);
        foreach (var note in (n["notes"] as JsonArray ?? []).OfType<JsonObject>())
            if (!notes.TryGetValue((string)note["id"]!, out var was) || !JsonNode.DeepEquals(was, note))
                note["updatedBy"] = guestName;
        var releases = (c["releases"] as JsonArray ?? []).OfType<JsonObject>().Select(x => (string)x["id"]!).ToHashSet();
        foreach (var release in (n["releases"] as JsonArray ?? []).OfType<JsonObject>())
            if (!releases.Contains((string)release["id"]!)) release["createdBy"] = guestName;
    }

    private static IEnumerable<JsonObject> Categories(JsonObject project) => (project["categories"] as JsonArray ?? []).OfType<JsonObject>();

    private static Dictionary<string, JsonObject> AllItems(JsonObject project)
    {
        var map = new Dictionary<string, JsonObject>();
        void Walk(JsonNode? list)
        {
            foreach (var item in (list as JsonArray ?? []).OfType<JsonObject>())
            {
                map[(string)item["id"]!] = item;
                Walk(item["children"]);
            }
        }
        foreach (var category in Categories(project)) Walk(category["items"]);
        return map;
    }

    private static Dictionary<string, JsonObject> AllComments(JsonObject project)
    {
        var map = new Dictionary<string, JsonObject>();
        void Walk(JsonNode? list)
        {
            foreach (var comment in (list as JsonArray ?? []).OfType<JsonObject>())
            {
                map[(string)comment["id"]!] = comment;
                Walk(comment["replies"]);
            }
        }
        foreach (var category in Categories(project)) Walk(category["comments"]);
        foreach (var item in AllItems(project).Values) Walk(item["comments"]);
        return map;
    }

    private static JsonObject Without(JsonObject node, string property)
    {
        var copy = node.DeepClone().AsObject();
        copy.Remove(property);
        return copy;
    }

    /// <summary>The project without its discussions and (for reporters) without the items they added.</summary>
    private static JsonObject Strip(JsonObject project, HashSet<string>? newItemsOf)
    {
        var copy = project.DeepClone().AsObject();
        void Walk(JsonArray? list)
        {
            if (list == null) return;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] is not JsonObject item) continue;
                if (newItemsOf != null && !newItemsOf.Contains((string)item["id"]!))
                {
                    list.RemoveAt(i);
                    continue;
                }
                item.Remove("comments");
                Walk(item["children"] as JsonArray);
            }
        }
        foreach (var category in Categories(copy))
        {
            category.Remove("comments");
            Walk(category["items"] as JsonArray);
        }
        return copy;
    }
}
