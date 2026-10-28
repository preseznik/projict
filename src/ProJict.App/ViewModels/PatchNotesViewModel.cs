using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

public sealed partial class PatchNoteCandidate : ObservableObject
{
    private readonly Action _changed;

    public PatchNoteCandidate(PatchNoteEntry entry, Action changed)
    {
        Entry = entry;
        _changed = changed;
    }

    public PatchNoteEntry Entry { get; }
    public string CategoryName => Entry.Category.Name;
    public string Text => Entry.Item.DisplayText().Replace("\n", " ");
    public string DoneText => Entry.Item.Imported ? "imported" : Entry.Item.DoneAt?.ToString("d MMM", CultureInfo.CurrentCulture) ?? "";

    [ObservableProperty] private bool _include = true;

    partial void OnIncludeChanged(bool value) => _changed();
}

public sealed partial class PatchNotesViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly ProjectViewModel _owner;

    public PatchNotesViewModel(AppState state, ProjectViewModel owner)
    {
        _state = state;
        _owner = owner;
        Candidates = PatchNotesBuilder.Candidates(owner.Project).Select(e => new PatchNoteCandidate(e, Regenerate)).ToList();
        CandidatesView = CollectionViewSource.GetDefaultView(Candidates);
        CandidatesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PatchNoteCandidate.CategoryName)));
        _version = PatchNotesBuilder.SuggestNextVersion(owner.Project);
        var last = owner.Project.Releases.Where(r => !r.IsBaseline).OrderBy(r => r.Date).LastOrDefault();
        LastReleaseText = last == null ? "No releases yet." : $"Last release: {last.Version} on {last.Date:d MMM yyyy}.";
        Regenerate();
    }

    public string ProjectName => _owner.Project.Name;
    public List<PatchNoteCandidate> Candidates { get; }
    public ICollectionView CandidatesView { get; }
    public bool HasCandidates => Candidates.Count > 0;
    public string LastReleaseText { get; }

    [ObservableProperty] private string _version;
    [ObservableProperty] private string? _title;
    [ObservableProperty] private DateTime _date = DateTime.Today;
    [ObservableProperty] private bool _markdown = true;
    [ObservableProperty] private string _preview = "";

    partial void OnVersionChanged(string value) => Regenerate();
    partial void OnTitleChanged(string? value) => Regenerate();
    partial void OnDateChanged(DateTime value) => Regenerate();
    partial void OnMarkdownChanged(bool value) => Regenerate();

    private IEnumerable<PatchNoteEntry> Included => Candidates.Where(c => c.Include).Select(c => c.Entry);

    private void Regenerate() =>
        Preview = PatchNotesBuilder.Build(_owner.Project, Version ?? "", Date, Title, Included, Markdown);

    [RelayCommand] private void BumpPatch() => Version = PatchNotesBuilder.SuggestNextVersion(_owner.Project, VersionBump.Patch);
    [RelayCommand] private void BumpMinor() => Version = PatchNotesBuilder.SuggestNextVersion(_owner.Project, VersionBump.Minor);
    [RelayCommand] private void BumpMajor() => Version = PatchNotesBuilder.SuggestNextVersion(_owner.Project, VersionBump.Major);

    [RelayCommand]
    private void SelectAll(string? include)
    {
        var value = !string.Equals(include, "False", StringComparison.OrdinalIgnoreCase);
        foreach (var c in Candidates) c.Include = value;
    }

    [RelayCommand]
    private void Copy()
    {
        try
        {
            Clipboard.SetText(Preview);
            _state.StatusText = "Patch notes copied to the clipboard.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _state.StatusText = "Clipboard is busy, try again.";
        }
    }

    [RelayCommand]
    private void SaveAs()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{_owner.Project.Key}-{Version}.{(Markdown ? "md" : "txt")}",
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt|All files|*.*",
        };
        if (dialog.ShowDialog() == true) File.WriteAllText(dialog.FileName, Preview);
    }

    /// <summary>Records the release and links the included items to it. Returns false when there is nothing to record.</summary>
    public bool CreateRelease(out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(Version))
        {
            error = "Enter a version first.";
            return false;
        }
        var entries = Included.ToList();
        if (entries.Count == 0)
        {
            error = "Select at least one item.";
            return false;
        }
        _owner.Checkpoint();
        var markdown = PatchNotesBuilder.Build(_owner.Project, Version, Date, Title, entries, markdown: true);
        PatchNotesBuilder.CreateRelease(_owner.Project, Version, Date, Title, entries, markdown);
        _owner.Touch();
        _state.StatusText = $"Release {Version} recorded with {entries.Count} item(s).";
        return true;
    }
}
