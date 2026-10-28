using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Import;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

public sealed partial class ImportFileRow(ImportFile file) : ObservableObject
{
    public ImportFile File { get; } = file;
    public string FileName => File.FileName;
    public string SizeText => File.Size < 1024 ? $"{File.Size} B" : $"{File.Size / 1024.0:0.#} KB";
    public string ModifiedText => File.LastWriteTime.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    public bool IsUnknown => File.Prefix == null && File.Role == ImportRole.Skip;

    public ImportRole Role
    {
        get => File.Role;
        set
        {
            if (value == File.Role) return;
            File.Role = value;
            if (value != ImportRole.Skip && string.IsNullOrWhiteSpace(File.TargetProject)) TargetProject = File.Key;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsUnknown));
        }
    }

    public string TargetProject
    {
        get => File.TargetProject;
        set { File.TargetProject = value?.Trim() ?? ""; OnPropertyChanged(); }
    }

    public string Group
    {
        get => File.Group;
        set { File.Group = value?.Trim() ?? ""; OnPropertyChanged(); }
    }

    public string? CategoryPrefix
    {
        get => File.CategoryPrefix;
        set { File.CategoryPrefix = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); OnPropertyChanged(); }
    }
}

public sealed class ImportItemNode(TodoItem item)
{
    public string Text { get; } = item.DisplayText().Replace("\n", " ⏎ ");
    public string Glyph { get; } = item.State switch { ItemState.Done => "☑", ItemState.InProgress => "◐", _ => "☐" };
    public bool IsDone { get; } = item.IsDone;
    public List<ImportItemNode> Children { get; } = item.Children.Select(c => new ImportItemNode(c)).ToList();
}

public sealed partial class ImportCategoryNode(ImportDraft draft, Category category) : ObservableObject
{
    public string Name => category.Name;
    public string CountsText => $"{category.Items.Count(i => !i.IsDone)} open · {category.Items.Count(i => i.IsDone)} done";
    public List<ImportItemNode> Items { get; } = category.Items.Select(i => new ImportItemNode(i)).ToList();
    public static IReadOnlyList<CategoryImportMode> Modes { get; } = Enum.GetValues<CategoryImportMode>();

    public CategoryImportMode Mode
    {
        get => draft.CategoryModes.GetValueOrDefault(category.Id, CategoryImportMode.List);
        set { draft.CategoryModes[category.Id] = value; OnPropertyChanged(); }
    }
}

public sealed partial class ImportDraftViewModel(ImportDraft draft, Project? existing) : ObservableObject
{
    public ImportDraft Draft { get; } = draft;
    public Project? Existing { get; } = existing;
    public string Name => Draft.ProjectName;
    public string ActionText => Existing == null ? "new project" : "merge into existing";
    public string CountsText => $"{Draft.OpenCount} open · {Draft.DoneCount} done · {Draft.NoteCount} notes";
    public List<ImportCategoryNode> Categories { get; } = draft.Preview.Categories.Select(c => new ImportCategoryNode(draft, c)).ToList();
    public List<NoteDoc> Notes => Draft.Preview.Notes;
    public List<ArchivedFile> Files => Draft.Preview.ArchivedFiles;

    [ObservableProperty] private ArchivedFile? _selectedFile = draft.Preview.ArchivedFiles.FirstOrDefault();

    public override string ToString() => Name;
}

public sealed partial class ImportWizardViewModel : ObservableObject
{
    private readonly AppState _state;

    public ImportWizardViewModel(AppState state)
    {
        _state = state;
        _folder = state.Settings.ImportSourceFolders.LastOrDefault()
                  ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    public ObservableCollection<ImportFileRow> Files { get; } = [];
    public ObservableCollection<ImportDraftViewModel> Drafts { get; } = [];
    public static IReadOnlyList<ImportRole> Roles { get; } = Enum.GetValues<ImportRole>();
    public List<string> ProjectSuggestions { get; private set; } = [];
    public IReadOnlyList<string> Groups => _state.Settings.Groups;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextText), nameof(CanGoBack), nameof(StepTitle), nameof(IsPreviewStep))]
    private int _step;

    public bool IsPreviewStep => Step == 2;

    [ObservableProperty] private string _folder;
    [ObservableProperty] private ImportDraftViewModel? _selectedDraft;
    [ObservableProperty] private bool _createBaseline = true;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _error;

    public bool IsFinished => Step == 3;
    public bool CanGoBack => Step is 1 or 2;
    public string NextText => Step switch { 0 => "Scan folder", 1 => "Preview", 2 => "Import", _ => "Close" };
    public string StepTitle => Step switch
    {
        0 => "1 · Choose the folder with your TODO / DONE text files",
        1 => "2 · Check which project each file belongs to",
        2 => "3 · Preview what will be imported",
        _ => "Done",
    };

    public event Action? CloseRequested;

    [RelayCommand]
    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder with TODO/DONE text files" };
        if (Directory.Exists(Folder)) dialog.InitialDirectory = Folder;
        if (dialog.ShowDialog() == true) Folder = dialog.FolderName;
    }

    [RelayCommand]
    private void Back()
    {
        Error = null;
        if (CanGoBack) Step--;
    }

    [RelayCommand]
    private void Next()
    {
        Error = null;
        try
        {
            switch (Step)
            {
                case 0: Scan(); break;
                case 1: BuildPreview(); break;
                case 2: DoImport(); break;
                default: CloseRequested?.Invoke(); break;
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        OnPropertyChanged(nameof(IsFinished));
    }

    private void Scan()
    {
        if (!Directory.Exists(Folder))
        {
            Error = "That folder does not exist.";
            return;
        }
        Files.Clear();
        foreach (var file in ImportScanner.Scan(Folder)) Files.Add(new ImportFileRow(file));
        if (Files.Count == 0)
        {
            Error = "No .txt files found in that folder.";
            return;
        }
        ProjectSuggestions = _state.Projects.Where(p => !p.IsInbox).Select(p => p.Name)
            .Concat(Files.Select(f => f.TargetProject))
            .Append(ImportScanner.InboxTarget)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        OnPropertyChanged(nameof(ProjectSuggestions));
        Step = 1;
    }

    private void BuildPreview()
    {
        var drafts = ImportPlanner.BuildDrafts(Files.Select(f => f.File));
        if (drafts.Count == 0)
        {
            Error = "No files selected — set a role other than Skip for the files you want.";
            return;
        }
        Drafts.Clear();
        foreach (var draft in drafts) Drafts.Add(new ImportDraftViewModel(draft, FindExisting(draft)));
        SelectedDraft = Drafts.FirstOrDefault();
        Step = 2;
    }

    private Project? FindExisting(ImportDraft draft) => draft.IsInbox
        ? _state.Inbox
        : _state.Projects.FirstOrDefault(p => !p.IsInbox && string.Equals(p.Name, draft.ProjectName, StringComparison.OrdinalIgnoreCase));

    private void DoImport()
    {
        _state.SaveNow();
        _state.BackupNow();
        int created = 0, merged = 0, items = 0;
        foreach (var vm in Drafts)
        {
            var existing = FindExisting(vm.Draft);
            var result = ImportPlanner.Apply(vm.Draft, existing, CreateBaseline);
            items += vm.Draft.OpenCount + vm.Draft.DoneCount;
            if (existing == null)
            {
                _state.AddProject(result);
                created++;
            }
            else
            {
                _state.NotifyReplaced(existing);
                merged++;
            }
        }

        if (!_state.Settings.ImportSourceFolders.Contains(Folder, StringComparer.OrdinalIgnoreCase))
            _state.Settings.ImportSourceFolders.Add(Folder);
        _state.MarkSettingsDirty();
        _state.NotifyListChanged();
        _state.SaveNow();

        Summary = $"Imported {items} top-level items: {created} new project(s), {merged} merged.\n\n" +
                  "Your original files were only read, never changed. Their full text is kept in each project's Archive tab.";
        Step = 3;
    }
}
