using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProJict.Core.Storage;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

public static class AtomicFile
{
    /// <summary>Writes to a temp file first, then swaps it in, so a crash never leaves a half-written file.</summary>
    public static void WriteAllText(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new System.Text.UTF8Encoding(false));
        if (File.Exists(path))
        {
            try
            {
                File.Replace(tmp, path, null, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException) { }
            catch (PlatformNotSupportedException) { }
        }
        File.Move(tmp, path, overwrite: true);
    }
}
