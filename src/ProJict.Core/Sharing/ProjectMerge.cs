using System.Text.Json.Nodes;
using ProJict.Core.Model;
using ProJict.Core.Storage;

namespace ProJict.Core.Sharing;

/// <summary>
/// Three-way merge of a project edited in two places at once (the host and a guest): what each side changed since the
/// version both started from is kept. Fields are merged one by one, so one side finishing an item while the other
/// edits its text keeps both; comments are merged as threads; items keep the place the side that moved them gave them.
/// Where both changed the same field, <c>mine</c> (the save being applied, the newer one) wins.
/// </summary>
public static class ProjectMerge
{
    public static Project Merge(Project @base, Project mine, Project theirs)
    {
        var b = ToJson(@base);
        var m = ToJson(mine);
        var t = ToJson(theirs);
        var result = MergeFields(b, m, t, except: ["categories", "notes", "releases"]);
        result["notes"] = MergeList(b["notes"], m["notes"], t["notes"], MergeEntity);
        result["releases"] = MergeList(b["releases"], m["releases"], t["releases"], MergeEntity);
        result["categories"] = MergeCategories(b, m, t);
        return Json.Deserialize<Project>(result.ToJsonString(Json.Options))!;
    }

    public static JsonObject ToJson(Project project) => (JsonObject)JsonNode.Parse(Json.Serialize(project))!;

    /// <summary>The project as JSON without what only matters on one computer: when it was touched, folded categories.</summary>
    public static string Normalized(Project project)
    {
        var json = ToJson(project);
        json.Remove("updatedAt");
        foreach (var category in (json["categories"] as JsonArray ?? []).OfType<JsonObject>()) category.Remove("collapsed");
        return json.ToJsonString();
    }

    // ───────────── Fields and lists ─────────────

    /// <summary>Each property from mine if mine changed it since base, else from theirs.</summary>
    private static JsonObject MergeFields(JsonObject? b, JsonObject m, JsonObject t, IReadOnlyCollection<string> except)
    {
        var result = new JsonObject();
        var names = t.Select(p => p.Key).Concat(m.Select(p => p.Key)).Concat(b?.Select(p => p.Key) ?? []).Distinct();
        foreach (var name in names.Where(n => !except.Contains(n)))
        {
            var mineChanged = !JsonNode.DeepEquals(b?[name], m[name]);
            var value = mineChanged ? m[name] : t[name];
            if (value != null) result[name] = value.DeepClone();
        }
        return result;
    }

    private static JsonObject MergeEntity(JsonObject? b, JsonObject m, JsonObject t) => MergeFields(b, m, t, except: []);

    /// <summary>
    /// A list of objects with ids: added on either side kept, removed on one side and untouched on the other removed,
    /// changed on both merged by <paramref name="merge"/>. Theirs' order, with mine's new ones after their predecessor.
    /// </summary>
    private static JsonArray MergeList(JsonNode? bNode, JsonNode? mNode, JsonNode? tNode, Func<JsonObject?, JsonObject, JsonObject, JsonObject> merge)
    {
        var b = ById(bNode);
        var m = ById(mNode);
        var t = ById(tNode);
        var order = Order(Ids(tNode), Ids(mNode));
        var result = new JsonArray();
        foreach (var id in order)
        {
            b.TryGetValue(id, out var bo);
            m.TryGetValue(id, out var mo);
            t.TryGetValue(id, out var to);
            var merged = Resolve(bo, mo, to, merge);
            if (merged != null) result.Add(merged);
        }
        return result;
    }

    /// <summary>One entity across the three versions; null when it should be gone.</summary>
    private static JsonObject? Resolve(JsonObject? b, JsonObject? m, JsonObject? t, Func<JsonObject?, JsonObject, JsonObject, JsonObject> merge)
    {
        if (m != null && t != null) return merge(b, m, t);
        if (b == null) return (m ?? t)?.DeepClone().AsObject(); // added on one side
        // Removed on one side: gone, unless the other side changed it meanwhile.
        var kept = m ?? t;
        return kept != null && !JsonNode.DeepEquals(b, kept) ? kept.DeepClone().AsObject() : null;
    }

    private static Dictionary<string, JsonObject> ById(JsonNode? list) =>
        (list as JsonArray ?? []).OfType<JsonObject>().Where(o => o["id"] != null)
            .GroupBy(o => (string)o["id"]!).ToDictionary(g => g.Key, g => g.First());

    private static List<string> Ids(JsonNode? list) =>
        (list as JsonArray ?? []).OfType<JsonObject>().Select(o => (string?)o["id"]).OfType<string>().ToList();

    /// <summary>Theirs' order; ids only mine has go right after the one before them in mine.</summary>
    private static List<string> Order(List<string> theirs, List<string> mine)
    {
        var result = new List<string>(theirs);
        var known = theirs.ToHashSet();
        string? previous = null;
        foreach (var id in mine)
        {
            if (known.Add(id))
            {
                var at = previous == null ? 0 : result.IndexOf(previous) + 1;
                result.Insert(at, id);
            }
            previous = id;
        }
        return result;
    }

    // ───────────── Comments ─────────────

    /// <summary>Threads merged comment by comment, so two people commenting at once both stay.</summary>
    private static JsonNode? MergeComments(JsonNode? b, JsonNode? m, JsonNode? t)
    {
        if (b == null && m == null && t == null) return null;
        var bf = Flatten(b);
        var mf = Flatten(m);
        var tf = Flatten(t);
        var merged = new Dictionary<string, (JsonObject Node, string? Parent)>();
        foreach (var id in tf.Keys.Concat(mf.Keys).Distinct())
        {
            bf.TryGetValue(id, out var bc);
            mf.TryGetValue(id, out var mc);
            tf.TryGetValue(id, out var tc);
            var node = Resolve(bc.Node, mc.Node, tc.Node, (bo, mo, to) => MergeFields(bo, mo, to, except: []));
            if (node != null) merged[id] = (node, (mc.Node != null ? mc.Parent : tc.Parent));
        }
        var roots = new JsonArray();
        var children = merged.Where(kv => kv.Value.Parent != null && merged.ContainsKey(kv.Value.Parent!))
            .GroupBy(kv => kv.Value.Parent!).ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());
        JsonObject Build(string id)
        {
            var node = merged[id].Node;
            if (children.TryGetValue(id, out var replies))
                node["replies"] = new JsonArray(replies.OrderBy(r => (string?)merged[r].Node["at"]).Select(r => (JsonNode)Build(r)).ToArray());
            else node.Remove("replies");
            return node;
        }
        // A reply whose parent is gone becomes a thread of its own rather than being lost.
        foreach (var id in merged.Keys.Where(id => merged[id].Parent == null || !merged.ContainsKey(merged[id].Parent!))
                     .OrderBy(id => (string?)merged[id].Node["at"]))
            roots.Add(Build(id));
        return roots.Count == 0 ? null : roots;
    }

    private static Dictionary<string, (JsonObject Node, string? Parent)> Flatten(JsonNode? comments)
    {
        var map = new Dictionary<string, (JsonObject, string?)>();
        void Walk(JsonNode? list, string? parent)
        {
            foreach (var c in (list as JsonArray ?? []).OfType<JsonObject>())
            {
                var id = (string?)c["id"];
                if (id == null) continue;
                var copy = c.DeepClone().AsObject();
                copy.Remove("replies");
                map[id] = (copy, parent);
                Walk(c["replies"], id);
            }
        }
        Walk(comments, null);
        return map;
    }

    // ───────────── Categories and items ─────────────

    private sealed record Placed(JsonObject Item, string Container, int Index);

    private static JsonArray MergeCategories(JsonObject b, JsonObject m, JsonObject t)
    {
        var categories = MergeList(b["categories"], m["categories"], t["categories"], (bo, mo, to) =>
        {
            var merged = MergeFields(bo, mo, to, except: ["items", "comments"]);
            if (MergeComments(bo?["comments"], mo["comments"], to["comments"]) is { } comments) merged["comments"] = comments;
            return merged;
        });

        // Items can move between categories and under other items, so they are merged across the whole project.
        var bi = FlattenItems(b);
        var mi = FlattenItems(m);
        var ti = FlattenItems(t);
        var movedInMine = Moved(mi, bi);
        var items = new Dictionary<string, JsonObject>();
        var container = new Dictionary<string, string>();
        var placedByMine = new HashSet<string>();
        foreach (var id in ti.Keys.Concat(mi.Keys).Distinct())
        {
            bi.TryGetValue(id, out var bp);
            mi.TryGetValue(id, out var mp);
            ti.TryGetValue(id, out var tp);
            var item = Resolve(bp?.Item, mp?.Item, tp?.Item, (bo, mo, to) =>
            {
                var merged = MergeFields(bo, mo, to, except: ["children", "comments"]);
                if (MergeComments(bo?["comments"], mo["comments"], to["comments"]) is { } comments) merged["comments"] = comments;
                return merged;
            });
            if (item == null) continue;
            items[id] = item;
            // Its place: where mine put it if mine added or moved it (or only mine still has it), else where theirs has it.
            var byMine = mp != null && (bp == null || movedInMine.Contains(id) || tp == null);
            if (byMine) placedByMine.Add(id);
            container[id] = byMine ? mp!.Container : tp!.Container;
        }

        var categoryIds = categories.OfType<JsonObject>().Select(c => (string)c["id"]!).ToHashSet();
        if (categoryIds.Count == 0) return categories;
        bool Exists(string c) => c.StartsWith("c:") ? categoryIds.Contains(c[2..]) : items.ContainsKey(c[2..]);
        // The category an item was in (theirs' side first), following its parents up.
        string HomeCategory(string id)
        {
            var seen = new HashSet<string>();
            var p = ti.GetValueOrDefault(id) ?? mi.GetValueOrDefault(id);
            while (p != null && p.Container.StartsWith("i:") && seen.Add(p.Container))
            {
                var parent = p.Container[2..];
                p = ti.GetValueOrDefault(parent) ?? mi.GetValueOrDefault(parent);
            }
            return p != null && p.Container.StartsWith("c:") && Exists(p.Container) ? p.Container : "c:" + categoryIds.First();
        }
        // A parent or category removed on the other side: the item's other place, else its category.
        foreach (var id in items.Keys)
        {
            if (Exists(container[id])) continue;
            var other = new[] { ti.GetValueOrDefault(id), mi.GetValueOrDefault(id) }.FirstOrDefault(p => p != null && Exists(p.Container));
            container[id] = other?.Container ?? HomeCategory(id);
        }
        // Moves on both sides can make a loop (A under B here, B under A there): such an item goes back to its category.
        foreach (var id in items.Keys)
        {
            var seen = new HashSet<string> { id };
            for (var c = container[id]; c.StartsWith("i:"); c = container[c[2..]])
                if (!seen.Add(c[2..]))
                {
                    container[id] = HomeCategory(id);
                    break;
                }
        }

        // Order in each list: theirs' order, then what mine placed goes after the item before it in mine.
        var lists = new Dictionary<string, List<string>>();
        List<string> ListOf(string c) => lists.TryGetValue(c, out var l) ? l : lists[c] = [];
        foreach (var (id, p) in ti.OrderBy(kv => kv.Value.Index))
            if (items.ContainsKey(id) && !placedByMine.Contains(id) && container[id] == p.Container) ListOf(p.Container).Add(id);
        foreach (var group in mi.GroupBy(kv => kv.Value.Container))
        {
            string? previous = null;
            foreach (var (id, _) in group.OrderBy(kv => kv.Value.Index))
            {
                if (!items.ContainsKey(id)) continue;
                var list = ListOf(group.Key);
                if (container[id] == group.Key && !list.Contains(id) && !lists.Values.Any(l => l.Contains(id)))
                    list.Insert(previous == null ? 0 : list.IndexOf(previous) + 1, id);
                if (list.Contains(id)) previous = id;
            }
        }
        foreach (var id in items.Keys.Where(id => !lists.Values.Any(l => l.Contains(id))))
            ListOf(container[id]).Add(id);

        JsonArray Children(string c) => new((lists.GetValueOrDefault(c) ?? []).Select(id =>
        {
            var node = items[id];
            node["children"] = Children("i:" + id);
            return (JsonNode)node;
        }).ToArray());
        foreach (var category in categories.OfType<JsonObject>())
            category["items"] = Children("c:" + (string)category["id"]!);
        return categories;
    }

    /// <summary>
    /// Items a side moved: into another list, or out of order with the rest of their list. Items that only shifted
    /// because others were added or removed around them don't count (the longest run still in order stays put).
    /// </summary>
    private static HashSet<string> Moved(Dictionary<string, Placed> side, Dictionary<string, Placed> @base)
    {
        var moved = new HashSet<string>();
        foreach (var group in side.GroupBy(kv => kv.Value.Container))
        {
            var stayed = new List<(string Id, int BaseIndex)>();
            foreach (var (id, _) in group.OrderBy(kv => kv.Value.Index))
            {
                if (!@base.TryGetValue(id, out var was)) continue; // new, not moved
                if (was.Container != group.Key) moved.Add(id);
                else stayed.Add((id, was.Index));
            }
            var inOrder = LongestIncreasing(stayed.Select(s => s.BaseIndex).ToList());
            for (var i = 0; i < stayed.Count; i++)
                if (!inOrder.Contains(i)) moved.Add(stayed[i].Id);
        }
        return moved;
    }

    /// <summary>Positions of a longest strictly increasing subsequence.</summary>
    private static HashSet<int> LongestIncreasing(List<int> values)
    {
        var length = new int[values.Count];
        var previous = new int[values.Count];
        var best = -1;
        for (var i = 0; i < values.Count; i++)
        {
            length[i] = 1;
            previous[i] = -1;
            for (var j = 0; j < i; j++)
                if (values[j] < values[i] && length[j] + 1 > length[i])
                {
                    length[i] = length[j] + 1;
                    previous[i] = j;
                }
            if (best < 0 || length[i] > length[best]) best = i;
        }
        var result = new HashSet<int>();
        for (var i = best; i >= 0; i = previous[i]) result.Add(i);
        return result;
    }

    private static Dictionary<string, Placed> FlattenItems(JsonObject project)
    {
        var map = new Dictionary<string, Placed>();
        void Walk(JsonNode? list, string container)
        {
            var index = 0;
            foreach (var item in (list as JsonArray ?? []).OfType<JsonObject>())
            {
                var id = (string?)item["id"];
                if (id == null) continue;
                var copy = item.DeepClone().AsObject();
                copy.Remove("children");
                map[id] = new Placed(copy, container, index++);
                Walk(item["children"], "i:" + id);
            }
        }
        foreach (var category in (project["categories"] as JsonArray ?? []).OfType<JsonObject>())
            Walk(category["items"], "c:" + (string?)category["id"]);
        return map;
    }
}
