using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ProJict.Core.Sharing;

namespace ProJict.App.Services.Sharing;

/// <summary>
/// This computer's sharing data (as host and as guest) with its secrets protected for the Windows user (DPAPI).
/// Without a path (tests) it lives in memory only.
/// </summary>
public sealed class SharingStore(string? path)
{
    private static readonly byte[] Entropy = "ProJict sharing"u8.ToArray();

    public SharingData Data { get; } = path != null ? SharingData.Load(path) : new SharingData();

    public void Save()
    {
        if (path == null) return;
        try { Data.Save(path); }
        catch (Exception) { /* kept in memory; tried again on the next change */ }
    }

    public static string Protect(byte[] data) => Convert.ToBase64String(ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser));

    public static byte[]? Unprotect(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try { return ProtectedData.Unprotect(Convert.FromBase64String(text), Entropy, DataProtectionScope.CurrentUser); }
        catch (Exception) { return null; }
    }

    public string? Token(KnownHost host) => Unprotect(host.ProtectedToken) is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null;

    public void SetToken(KnownHost host, string token) => host.ProtectedToken = Protect(System.Text.Encoding.UTF8.GetBytes(token));

    /// <summary>The host certificate, made (self-signed, for 20 years) the first time it's needed.</summary>
    public X509Certificate2 HostCertificate()
    {
        if (Unprotect(Data.ProtectedCertificate) is { } stored)
        {
            try { return X509CertificateLoader.LoadPkcs12(stored, null, X509KeyStorageFlags.Exportable); }
            catch (CryptographicException) { /* made again below */ }
        }
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=ProJict", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var made = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(20));
        var pfx = made.Export(X509ContentType.Pkcs12);
        Data.ProtectedCertificate = Protect(pfx);
        Save();
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
    }
}
