using System.IO;
using System.Windows;
using System.Windows.Threading;
using ProJict.App.Services;
using ProJict.App.ViewModels;
using ProJict.App.Views;
using ProJict.Core.Export;
using ProJict.Core.Storage;

namespace ProJict.App;

public partial class App : Application
{
    private SingleInstance? _single;
    private TrayService? _tray;
    private HotkeyService? _hotkey;
    private QuickCaptureWindow? _capture;

    public static new App Current => (App)Application.Current;
    public AppState State { get; private set; } = null!;
    public MainViewModel MainVm { get; private set; } = null!;
    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var dataArg = ArgValue(e.Args, "--data");
        var appSettings = AppSettings.Load();
        if (dataArg == null && appSettings.DataRoot == null)
        {
            // First start: let the user pick the data folder, and remember it.
            ThemeMode = ThemeMode.System;
            appSettings.DataRoot = FirstRunWindow.Ask(AppSettings.DefaultDataRoot()) ?? AppSettings.DefaultDataRoot();
            try { appSettings.Save(); }
            catch (Exception) { /* asked again next time */ }
        }
        var dataRoot = Path.GetFullPath(dataArg ?? appSettings.DataRoot ?? AppSettings.DefaultDataRoot());

        _single = new SingleInstance(dataRoot);
        if (!_single.IsFirst)
        {
            _single.SignalFirst();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        try
        {
            State = new AppState(new ProjectStore(dataRoot), appSettings, persistAppSettings: dataArg == null);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the data folder:\n{dataRoot}\n\n{ex.Message}", "ProJict", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        ApplyTheme(State.Settings.Theme);
        // One snapshot per launch, before anything is written.
        State.BackupNow();

        MainVm = new MainViewModel(State);
        var main = new MainWindow(MainVm);
        MainWindow = main;
        _single.Activated += () => Dispatcher.BeginInvoke(ShowMainWindow);

        _tray = new TrayService(ShowMainWindow, ShowQuickCapture, ExportAllNow, ExitApp);
        _hotkey = new HotkeyService(main);
        _hotkey.Pressed += ShowQuickCapture;
        RegisterHotkey(State.Settings.Hotkey, out _);

        main.Show();

        if (State.Store.LoadErrors.Count > 0)
            MessageBox.Show(main, "Some files could not be loaded and were left untouched:\n\n" + string.Join("\n", State.Store.LoadErrors),
                "ProJict", MessageBoxButton.OK, MessageBoxImage.Warning);

        if (appSettings.CheckForUpdates)
            _ = MainVm.Updates.CheckAsync(manual: false);
        if (MainVm.GitHub.HasWork)
            _ = MainVm.GitHub.RefreshAsync(force: false);
    }

    public bool RegisterHotkey(string gesture, out string? error)
    {
        error = null;
        if (_hotkey == null) return false;
        var ok = _hotkey.Register(gesture, out error);
        _tray?.SetHotkeyHint(ok ? gesture : null);
        if (!ok) State.StatusText = "Quick capture hotkey not registered: " + error;
        return ok;
    }

    public static void ApplyTheme(string theme)
    {
        Current.ThemeMode = theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }

    public void ShowMainWindow()
    {
        if (MainWindow is not { } window) return;
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    public void ShowQuickCapture()
    {
        if (_capture is { IsLoaded: true })
        {
            _capture.Activate();
            return;
        }
        _capture = new QuickCaptureWindow(State, MainVm);
        _capture.Closed += (_, _) => _capture = null;
        _capture.Show();
        _capture.Activate();
    }

    private void ExportAllNow()
    {
        State.SaveNow();
        var folder = State.Settings.MirrorEnabled && !string.IsNullOrWhiteSpace(State.Settings.MirrorFolder)
            ? State.Settings.MirrorFolder
            : State.Settings.DefaultExportFolder(State.Store.DataRoot);
        try
        {
            var result = ExportService.Export(State.Projects, folder, State.Settings.Export, overwriteConflicts: false);
            var text = $"{result.Written.Count} written, {result.Unchanged.Count} unchanged";
            if (result.Conflicts.Count > 0) text += $", {result.Conflicts.Count} skipped (not written by ProJict)";
            _tray?.ShowBalloon($"Exported to {folder}", text);
        }
        catch (Exception ex)
        {
            _tray?.ShowBalloon("Export failed", ex.Message);
        }
    }

    public void ExitApp()
    {
        IsExiting = true;
        // Dialogs first (an update can be installed from Settings), then the main window.
        foreach (var window in Windows.OfType<Window>().Where(w => w != MainWindow).ToList()) window.Close();
        if (MainWindow != null) MainWindow.Close();
        else Shutdown();
    }

    public void OnMainWindowClosed()
    {
        IsExiting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { State?.SaveNow(); }
        catch (Exception) { }
        State?.SaveAppSettings();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try
        {
            var log = Path.Combine(State?.Store.DataRoot ?? Path.GetTempPath(), "error.log");
            File.AppendAllText(log, $"[{DateTime.Now:u}] {e.Exception}\r\n\r\n");
        }
        catch (Exception) { }
        MessageBox.Show(e.Exception.Message, "ProJict — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
