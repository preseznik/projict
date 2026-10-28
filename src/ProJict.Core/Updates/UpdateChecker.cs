using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ProJict.Core.Updates;

/// <summary>A newer release published on GitHub, with its installer download.</summary>
public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string ReleaseUrl,
    string? Notes,
    string AssetName,
    string AssetUrl,
    long AssetSize,
    string? Sha256);

/// <summary>Checks the GitHub releases of the project for a newer installer and downloads it.</summary>
public static class UpdateChecker
{
    public const string Owner = "preseznik";
    public const string Repository = "projict";
    public const string InstallerPrefix = "ProJict-Setup";

    public static string LatestReleaseApi => $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest";
    public static string ReleasesPage => $"https://github.com/{Owner}/{Repository}/releases";

    /// <summary>"v0.1.2", "0.1.2-beta" → 0.1.2; null when the tag is not a version.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var text = tag.Trim().TrimStart('v', 'V');
        var end = text.IndexOfAny(['-', '+', ' ']);
        if (end >= 0) text = text[..end];
        if (!Version.TryParse(text.Contains('.') ? text : text + ".0", out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    public static bool IsNewer(Version candidate, Version current) =>
        new Version(candidate.Major, candidate.Minor, Math.Max(candidate.Build, 0)) >
        new Version(current.Major, current.Minor, Math.Max(current.Build, 0));

    /// <summary>Reads a GitHub "release" JSON object; null when it has no installer asset or no version tag.</summary>
    public static UpdateInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
        var tag = Str(root, "tag_name");
        var version = ParseVersion(tag);
        if (version == null || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = Str(asset, "name") ?? "";
            if (!name.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            var url = Str(asset, "browser_download_url");
            if (url == null) continue;
            var digest = Str(asset, "digest");
            var sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;
            var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
            return new UpdateInfo(version, tag!, Str(root, "html_url") ?? ReleasesPage, Str(root, "body"), name, url, size, sha);
        }
        return null;
    }

    /// <summary>Returns the latest release when it is newer than <paramref name="current"/>, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, Version current, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ProJict", $"{current.Major}.{current.Minor}.{Math.Max(current.Build, 0)}"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null; // no release published yet
        response.EnsureSuccessStatusCode();
        var info = ParseRelease(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return info != null && IsNewer(info.Version, current) ? info : null;
    }

    /// <summary>Downloads the installer and verifies its size and, when GitHub provides it, its SHA-256.</summary>
    public static async Task DownloadAsync(HttpClient http, UpdateInfo info, string path, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var response = await http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? info.AssetSize;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written = 0;
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = File.Create(path))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                written += read;
                if (total > 0) progress?.Report((double)written / total);
            }
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset());
        var sizeOk = info.AssetSize <= 0 || written == info.AssetSize;
        var hashOk = info.Sha256 == null || string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase);
        if (sizeOk && hashOk) return;
        File.Delete(path);
        throw new InvalidDataException("The downloaded installer did not match the published file. Please try again.");
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
