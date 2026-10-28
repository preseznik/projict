using System.Text;
using System.Text.RegularExpressions;
using ProJict.Core.Model;

namespace ProJict.Core.Import;

public sealed class ParsedDocument
{
    public List<Category> Categories { get; } = [];
    public List<NoteDoc> Notes { get; } = [];
    public int ItemCount => Categories.Sum(c => c.Walk().Count());
}

/// <summary>
/// Parses the hand-written TODO/DONE text format:
/// ALL-CAPS category headers, "- " open, "+ " done, "-+" in progress, tab-indented sub-items,
/// "TAG: text" prefixes, and free prose (kept as notes).
/// </summary>
public static partial class TxtListParser
{
    [GeneratedRegex(@"^(?<tag>[A-Z][A-Z0-9]*(?:[ /&][A-Z0-9]+){0,2})\s*:\s*(?<text>\S.*)$")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\t[\t ]*(?<m>-\+|\+-|\+\*|\*\+|[-+*])[ \t]*")]
    private static partial Regex InlineChildRegex();

    public static ParsedDocument Parse(string text, string defaultCategory = "GENERAL")
    {
        var doc = new ParsedDocument();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        Category? current = null;
        var stack = new List<(int Indent, TodoItem Item)>();
        List<string>? noteLines = null;
        string noteTitle = "";
        var prevBlank = true;
        var prevWasItemLine = false;
        var prevContent = "";

        void FinishNote()
        {
            if (noteLines == null) return;
            while (noteLines.Count > 0 && noteLines[^1].Trim().Length == 0) noteLines.RemoveAt(noteLines.Count - 1);
            if (noteLines.Count > 0)
                doc.Notes.Add(new NoteDoc { Title = noteTitle, Text = string.Join("\r\n", noteLines) });
            noteLines = null;
        }

        void SetCategory(string name, string? description)
        {
            current = GetOrAdd(doc, name);
            if (!string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(current.Description))
                current.Description = description;
            stack.Clear();
            prevWasItemLine = false;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd();
            if (raw.Trim().Length == 0)
            {
                noteLines?.Add("");
                prevBlank = true;
                prevWasItemLine = false;
                continue;
            }

            var (indent, content) = SplitIndent(raw);

            if (noteLines != null)
            {
                // Prose runs until the next unmistakable header.
                if (indent == 0 && TryHeader(content, strongOnly: true, out var noteEndName, out var noteEndDesc))
                {
                    FinishNote();
                    SetCategory(noteEndName, noteEndDesc);
                    prevBlank = false;
                }
                else
                {
                    noteLines.Add(raw);
                }
                continue;
            }

            if (IsSeparator(content))
            {
                prevBlank = true;
                prevWasItemLine = false;
                continue;
            }

            var hasMarker = TryMarker(content, out var state, out var rest);

            if (!hasMarker && indent == 0)
            {
                if (TryHeader(content, strongOnly: false, out var name, out var desc))
                {
                    SetCategory(name, desc);
                    prevBlank = false;
                    continue;
                }

                if (!TagRegex().IsMatch(content))
                {
                    var startsProse = content.StartsWith('|') || (prevBlank && content.EndsWith(':'));
                    if (startsProse)
                    {
                        if (content.EndsWith(':') && NextNonBlankIsItemOrHeader(lines, i))
                        {
                            // "look into:" followed by "- item" lines is a header, not prose.
                            SetCategory(content.TrimEnd(':').Trim(), null);
                            prevBlank = false;
                            continue;
                        }
                        noteTitle = content.EndsWith(':') ? Truncate(content.TrimEnd(':').Trim(), 60) : "Notes";
                        noteLines = [raw];
                        continue;
                    }

                    if (prevWasItemLine && stack.Count > 0 && EndsWithContinuation(prevContent))
                    {
                        AppendLine(stack[^1].Item, content);
                        prevContent = content;
                        prevBlank = false;
                        continue;
                    }
                }
            }
            else if (!hasMarker && stack.Count > 0)
            {
                // Unmarked indented text continues the deepest open item.
                AppendLine(stack[^1].Item, content);
                prevContent = content;
                prevBlank = false;
                prevWasItemLine = true;
                continue;
            }

            var item = BuildItem(hasMarker ? rest : content, hasMarker ? state : ItemState.Todo);
            prevBlank = false;
            if (item == null) continue;

            current ??= GetOrAdd(doc, defaultCategory);
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            if (stack.Count > 0) stack[^1].Item.Children.Add(item);
            else current.Items.Add(item);
            stack.Add((indent, item));
            prevWasItemLine = true;
            prevContent = content;
        }

        FinishNote();
        return doc;
    }

    /// <summary>Parses one typed line ("BUG: crash", "+ done thing") into an item, or null when empty.</summary>
    public static TodoItem? ParseLine(string line)
    {
        var content = line.Trim();
        var hasMarker = TryMarker(content, out var state, out var rest);
        return BuildItem(hasMarker ? rest : content, hasMarker ? state : ItemState.Todo);
    }

    /// <summary>Splits a leading "TAG: " off typed text; returns (null, text) when there is none.</summary>
    public static (string? Tag, string Text) SplitTag(string text)
    {
        var match = TagRegex().Match(text.Trim());
        return match.Success ? (match.Groups["tag"].Value, match.Groups["text"].Value.Trim()) : (null, text);
    }

    /// <summary>Builds an item, splitting "+ Level 1\t\t- fix lighting" into a parent with inline children.</summary>
    internal static TodoItem? BuildItem(string text, ItemState state)
    {
        text = text.Trim();
        if (text.Length == 0) return null;

        var matches = InlineChildRegex().Matches(text);
        var head = matches.Count > 0 ? text[..matches[0].Index] : text;
        var item = CreateItem(head, state);
        if (item == null)
        {
            if (matches.Count == 0) return null;
            // Text started with an inline marker; the first chunk becomes the item itself.
            item = new TodoItem();
        }

        for (var m = 0; m < matches.Count; m++)
        {
            var start = matches[m].Index + matches[m].Length;
            var end = m + 1 < matches.Count ? matches[m + 1].Index : text.Length;
            var child = CreateItem(text[start..end], MarkerState(matches[m].Groups["m"].Value));
            if (child == null) continue;
            if (item.Text.Length == 0) { item.Text = child.Text; item.Tag = child.Tag; item.State = child.State; }
            else item.Children.Add(child);
        }

        return item.Text.Length == 0 ? null : item;
    }

    private static TodoItem? CreateItem(string text, ItemState state)
    {
        text = Regex.Replace(text, @"\t+", " ").Trim();
        if (text.Length == 0) return null;
        string? tag = null;
        var tagMatch = TagRegex().Match(text);
        if (tagMatch.Success)
        {
            tag = tagMatch.Groups["tag"].Value;
            text = tagMatch.Groups["text"].Value.Trim();
            if (text.StartsWith("- ")) text = text[2..].Trim();
        }
        return new TodoItem { Text = text, Tag = tag, State = state };
    }

    private static void AppendLine(TodoItem item, string line) => item.Text += "\n" + line.Trim();

    internal static (int Indent, string Content) SplitIndent(string raw)
    {
        int tabs = 0, spaces = 0, pos = 0;
        for (; pos < raw.Length; pos++)
        {
            if (raw[pos] == '\t') tabs++;
            else if (raw[pos] == ' ') spaces++;
            else break;
        }
        return (tabs + spaces / 4, raw[pos..]);
    }

    internal static bool TryMarker(string content, out ItemState state, out string rest)
    {
        state = ItemState.Todo;
        rest = content;
        if (content.Length == 0) return false;
        var two = content.Length >= 2 ? content[..2] : "";
        if (two is "-+" or "+-")
        {
            state = ItemState.InProgress;
            rest = content[2..];
            return true;
        }
        if (two is "+*" or "*+")
        {
            state = ItemState.Done;
            rest = content[2..];
            return true;
        }
        switch (content[0])
        {
            case '+':
                state = ItemState.Done;
                rest = content[1..];
                return true;
            case '-' or '*' or '•':
                rest = content[1..];
                return true;
            default:
                return false;
        }
    }

    private static ItemState MarkerState(string marker) => marker switch
    {
        "-+" or "+-" => ItemState.InProgress,
        "+" or "+*" or "*+" => ItemState.Done,
        _ => ItemState.Todo,
    };

    /// <summary>
    /// Strong headers: mostly upper-case, short, optionally "## DECORATED ##" or "NAME - description".
    /// Weak headers (not used inside prose): "Short Title:" with nothing after the colon.
    /// </summary>
    internal static bool TryHeader(string content, bool strongOnly, out string name, out string? description)
    {
        name = "";
        description = null;
        var line = content.Trim();
        if (line.Length == 0 || line[0] is '-' or '+' or '*' or '|') return false;
        if (TagRegex().IsMatch(line)) return false; // "ADD: - allow player…" is a tagged item

        if (line[0] is '#' or '=')
        {
            // "### MAJOR FEATURES ###", "=- LAN -="
            var stripped = line.Trim('#', '=', '-', '~', '*', ' ', '\t').TrimEnd(':').Trim();
            if (stripped.Length == 0 || !stripped.Any(char.IsLetter)) return false;
            name = stripped;
            return true;
        }

        var head = line;
        var dash = line.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            head = line[..dash].Trim();
            description = line[(dash + 3)..].Trim();
        }

        var headName = head.TrimEnd(':').Trim();
        // "CHARACTERS - text" is a header; "PARTLY DONE NEW DAMAGE EFFECT - text" is an item.
        var headFitsDescription = dash <= 0 || headName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3;
        if (headFitsDescription && IsStrongHeader(headName))
        {
            name = headName;
            if (string.IsNullOrEmpty(description)) description = null;
            return true;
        }

        description = null;
        if (strongOnly) return false;
        if (line.EndsWith(':') && char.IsUpper(line[0]))
        {
            var weak = line.TrimEnd(':').Trim();
            if (weak.Length > 0 && !weak.Contains(':') && weak.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4)
            {
                name = weak;
                return true;
            }
        }
        return false;
    }

    private static bool IsStrongHeader(string text)
    {
        if (text.Length == 0 || text.Length > 60 || text.Contains(':')) return false;
        if (text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 6) return false;
        var letters = text.Where(char.IsLetter).ToList();
        if (letters.Count == 0) return false;
        return letters.Count(char.IsUpper) >= letters.Count * 0.7;
    }

    private static bool IsSeparator(string content) =>
        content.Length >= 3 && content.All(c => c is '-' or '=' or '*' or '_' or '~' or '#');

    private static bool EndsWithContinuation(string previous)
    {
        var p = previous.TrimEnd();
        return p.EndsWith(',') || p.EndsWith(';') || p.EndsWith(':') || p.EndsWith('&') ||
               p.EndsWith(" and", StringComparison.OrdinalIgnoreCase) ||
               p.EndsWith(" or", StringComparison.OrdinalIgnoreCase) ||
               p.EndsWith(" with", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NextNonBlankIsItemOrHeader(string[] lines, int index)
    {
        for (var j = index + 1; j < lines.Length; j++)
        {
            var (indent, content) = SplitIndent(lines[j].TrimEnd());
            if (content.Length == 0) continue;
            return TryMarker(content, out _, out _) ||
                   (indent == 0 && TryHeader(content, strongOnly: true, out _, out _));
        }
        return false;
    }

    private static Category GetOrAdd(ParsedDocument doc, string name)
    {
        var existing = doc.Categories.FirstOrDefault(c => ProjectExtensions.CategoryNamesEqual(c.Name, name));
        if (existing != null) return existing;
        var created = new Category { Name = name };
        doc.Categories.Add(created);
        return created;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
