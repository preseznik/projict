using System.Text;
using System.Text.Json.Nodes;

namespace ProJict.Core.Sharing;

/// <summary>
/// Messages between ProJict apps: one JSON object per line ({"type": …}), over TLS. Lines are capped so a bad peer
/// can't make the other side buffer without end.
/// </summary>
public sealed class Wire(Stream stream)
{
    /// <summary>2: items can be on hold (a 0.1.13 app couldn't read that priority).</summary>
    public const int Version = 2;
    public const int MaxLine = 24 * 1024 * 1024;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _start;
    private int _end;

    /// <summary>The next message, or null when the other side closed the connection.</summary>
    public async Task<JsonObject?> ReadAsync(CancellationToken ct = default)
    {
        using var line = new MemoryStream();
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
                if (_end == 0) return null;
            }
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            var stop = newline < 0 ? _end : newline;
            line.Write(_buffer, _start, stop - _start);
            if (line.Length > MaxLine) throw new InvalidDataException("A message was too large.");
            _start = newline < 0 ? _end : newline + 1;
            if (newline < 0) continue;
            if (line.Length == 0) continue;
            return JsonNode.Parse(line.ToArray()) as JsonObject ?? throw new InvalidDataException("A message wasn't a JSON object.");
        }
    }

    public async Task SendAsync(JsonObject message, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public static JsonObject Message(string type, params (string Name, JsonNode? Value)[] fields)
    {
        var message = new JsonObject { ["type"] = type };
        foreach (var (name, value) in fields) message[name] = value;
        return message;
    }
}
