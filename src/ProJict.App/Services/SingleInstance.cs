using System.Security.Cryptography;
using System.Text;

namespace ProJict.App.Services;

/// <summary>One process per data folder; a second launch asks the first one to show its window and exits.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private volatile bool _disposed;

    public SingleInstance(string dataRoot)
    {
        var id = IdFor(dataRoot);
        _mutex = new Mutex(true, $"ProJict-{id}-mutex", out var created);
        IsFirst = created;
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, $"ProJict-{id}-activate");
        if (IsFirst)
            new Thread(Listen) { IsBackground = true, Name = "ProJict activation" }.Start();
    }

    public bool IsFirst { get; }

    /// <summary>Names the per-data-folder objects (mutex, activation event, agent pipe).</summary>
    public static string IdFor(string dataRoot) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataRoot.ToLowerInvariant())))[..16];

    public static bool IsRunning(string dataRoot)
    {
        if (!Mutex.TryOpenExisting($"ProJict-{IdFor(dataRoot)}-mutex", out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    /// <summary>Raised on a background thread when another launch wants this instance in front.</summary>
    public event Action? Activated;

    public void SignalFirst() => _activate.Set();

    private void Listen()
    {
        while (true)
        {
            _activate.WaitOne();
            if (_disposed) return;
            Activated?.Invoke();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (IsFirst)
        {
            _activate.Set();
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _mutex.Dispose();
    }
}
