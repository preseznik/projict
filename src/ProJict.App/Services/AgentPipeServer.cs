using System.IO;
using System.IO.Pipes;
using ProJict.Core.Agents;

namespace ProJict.App.Services;

/// <summary>
/// Accepts AI agents (through <see cref="McpBridge"/>) on a named pipe that only the current Windows user can open,
/// one MCP session per connection. Nothing listens on the network.
/// </summary>
public sealed class AgentPipeServer(string pipeName, IMcpToolHost host, string version) : IDisposable
{
    private CancellationTokenSource? _stop;

    public static string PipeName(string dataRoot) => $"ProJict-{SingleInstance.IdFor(dataRoot)}-agents";

    public bool IsRunning => _stop != null;

    public void Start()
    {
        if (_stop != null) return;
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        Task.Run(() => AcceptAsync(token));
    }

    /// <summary>Stops accepting agents and ends the connected ones.</summary>
    public void Stop()
    {
        _stop?.Cancel();
        _stop = null;
    }

    private async Task AcceptAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception)
            {
                // The name is briefly unavailable (e.g. many agents at once); try again shortly.
                await Delay(stop);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(stop);
            }
            catch (Exception)
            {
                await pipe.DisposeAsync();
                if (!stop.IsCancellationRequested) await Delay(stop);
                continue;
            }
            _ = ServeAsync(pipe, stop);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        await using (pipe)
        {
            try
            {
                await McpServer.RunAsync(pipe, pipe, host, new McpSession(), version, stop);
            }
            catch (Exception)
            {
                // The agent went away mid-message, or ProJict is stopping.
            }
        }
    }

    private static async Task Delay(CancellationToken stop)
    {
        try { await Task.Delay(500, stop); }
        catch (OperationCanceledException) { }
    }

    public void Dispose() => Stop();
}
