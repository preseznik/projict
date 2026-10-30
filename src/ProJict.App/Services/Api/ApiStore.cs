using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ProJict.App.Services.Sharing;
using ProJict.Core.Api;

namespace ProJict.App.Services.Api;

/// <summary>
/// This computer's API settings and tokens (Settings › API). Without a path (tests) they live in memory only.
/// Lock <see cref="Sync"/> around reading or changing <see cref="Data"/>: requests use it from the server's threads.
/// </summary>
public sealed class ApiStore(string? path)
{
    public ApiData Data { get; } = path != null ? ApiData.Load(path) : new ApiData();
    public Lock Sync { get; } = new();

    public void Save()
    {
        if (path == null) return;
        try
        {
            lock (Sync) Data.Save(path);
        }
        catch (Exception) { /* kept in memory; tried again on the next change */ }
    }

    /// <summary>
    /// The HTTPS certificate, made the first time it's needed (self-signed, for 5 years) for this computer's name and its
    /// network addresses, so clients that trust it can also check they reached the right computer.
    /// </summary>
    public X509Certificate2 Certificate()
    {
        lock (Sync)
        {
            if (SharingStore.Unprotect(Data.ProtectedCertificate) is { } stored)
            {
                try { return X509CertificateLoader.LoadPkcs12(stored, null, X509KeyStorageFlags.Exportable); }
                catch (CryptographicException) { /* made again below */ }
            }
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest($"CN={Environment.MachineName}, O=ProJict", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(Environment.MachineName);
            names.AddDnsName(Environment.MachineName + ".local");
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            names.AddIpAddress(IPAddress.IPv6Loopback);
            foreach (var address in NetworkAddresses()) names.AddIpAddress(address);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // server authentication
            using var made = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));
            var pfx = made.Export(X509ContentType.Pkcs12);
            Data.ProtectedCertificate = SharingStore.Protect(pfx);
            Save();
            return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
        }
    }

    /// <summary>This computer's IPv4 addresses on the network.</summary>
    public static IReadOnlyList<IPAddress> NetworkAddresses()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).Distinct().ToList();
        }
        catch (SocketException)
        {
            return [];
        }
    }
}
