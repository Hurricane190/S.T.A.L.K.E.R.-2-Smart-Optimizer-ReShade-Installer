using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using ZoneTune.Core.Models;

namespace ZoneTune.Core.Services;

/// <summary>
/// Downloads are restricted to an allowlist of official/public hosts, require HTTPS,
/// enforce a size limit and are hashed. Authenticode verification is used for the
/// ReShade setup tool before it is ever launched.
/// </summary>
public static class HttpService
{
    public const long DefaultMaxBytes = 250L * 1024 * 1024;
    public const string UserAgent = "ZoneTune/1.0 (STALKER 2 optimization tool; read-only public downloads)";

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "reshade.me",
        "www.reshade.me",
        "github.com",
        "www.github.com",
        "api.github.com",
        "raw.githubusercontent.com",
        "codeload.github.com",
        "objects.githubusercontent.com",
        "github-releases.githubusercontent.com"
    };

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return client;
    }

    public static async Task<string> GetStringAsync(string url, int maxBytes = 8 * 1024 * 1024, CancellationToken ct = default)
    {
        ValidateUrl(url);
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        ValidateFinalHost(response.RequestMessage?.RequestUri);
        var length = response.Content.Headers.ContentLength;
        if (length > maxBytes) throw new InvalidOperationException($"Response is too large ({length} bytes).");
        var bytes = await ReadLimitedAsync(response, maxBytes, ct);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public static async Task<byte[]> GetBytesAsync(string url, long maxBytes = DefaultMaxBytes, CancellationToken ct = default)
    {
        ValidateUrl(url);
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        ValidateFinalHost(response.RequestMessage?.RequestUri);
        var length = response.Content.Headers.ContentLength;
        if (length > maxBytes) throw new InvalidOperationException($"Download is too large ({length} bytes).");
        return await ReadLimitedAsync(response, maxBytes, ct);
    }

    public static async Task<(string Path, string Sha256, long Bytes)> DownloadAsync(
        string url, string destination, IProgress<OperationProgress>? progress = null,
        long maxBytes = DefaultMaxBytes, CancellationToken ct = default)
    {
        ValidateUrl(url);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".part";
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        ValidateFinalHost(response.RequestMessage?.RequestUri);
        var total = response.Content.Headers.ContentLength ?? -1;
        if (total > maxBytes) throw new InvalidOperationException($"Download is too large ({total} bytes).");

        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                read += n;
                if (read > maxBytes) throw new InvalidOperationException("Download exceeded the size limit.");
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                if (total > 0)
                    progress?.Report(new OperationProgress("Downloading", $"{read / 1024 / 1024} / {total / 1024 / 1024} MB", (int)(read * 100 / total)));
            }
        }

        if (File.Exists(destination)) File.Delete(destination);
        File.Move(temp, destination);
        var hash = await ComputeSha256Async(destination, ct);
        Log.Info($"Downloaded {Path.GetFileName(destination)} from {new Uri(url).Host} ({new FileInfo(destination).Length} bytes, sha256 {hash})");
        return (destination, hash, new FileInfo(destination).Length);
    }

    public static async Task<(string Path, string Sha256, long Bytes)> FromStringAsync(
        string text, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, text, new System.Text.UTF8Encoding(false));
        var hash = await ComputeSha256Async(destination, CancellationToken.None);
        return (destination, hash, new FileInfo(destination).Length);
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, long maxBytes, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + n > maxBytes) throw new InvalidOperationException("Response exceeded the size limit.");
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    public static void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Invalid URL.");
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Only HTTPS downloads are allowed.");
        if (!AllowedHosts.Contains(uri.Host))
            throw new InvalidOperationException($"Host '{uri.Host}' is not on the download allowlist.");
    }

    private static void ValidateFinalHost(Uri? uri)
    {
        if (uri == null) throw new InvalidOperationException("Redirect target could not be verified.");
        if (uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host))
            throw new InvalidOperationException($"Redirected to a non-allowlisted host: {uri.Host}");
    }

    // ---------------- Authenticode ----------------

    private const string ReShadeThumbprint = "589690208A5E52FB96980C4A6698F50ACD47C49F";
    public const string ReShadeSetupSha256 = "207aea16205fbf952bc8fe1879966672454cf04002e7ad34237c7990a5b3c0b4";

    public sealed class SignatureInfo
    {
        public bool IsValid { get; set; }
        public bool IsOfficialReShade { get; set; }
        public string Subject { get; set; } = "";
        public string Thumbprint { get; set; } = "";
        public string Error { get; set; } = "";
        public string Warning { get; set; } = "";
        public uint TrustResult { get; set; }
    }

    public static SignatureInfo VerifyAuthenticode(string path, bool requireReShadeThumbprint = false)
    {
        var info = new SignatureInfo();
        try
        {
            X509Certificate2 cert;
#pragma warning disable SYSLIB0057
            try
            {
                cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            }
            catch
            {
                cert = X509CertificateLoader.LoadCertificateFromFile(path);
            }
#pragma warning restore SYSLIB0057
            info.Subject = cert.Subject;
            info.Thumbprint = cert.Thumbprint?.ToUpperInvariant() ?? "";
            var trusted = VerifyWinTrust(path, out var error, out var resultCode);
            info.TrustResult = resultCode;
            info.IsValid = trusted;
            info.Error = error;
            info.IsOfficialReShade = info.Thumbprint.Equals(ReShadeThumbprint, StringComparison.OrdinalIgnoreCase);
            if (requireReShadeThumbprint && !info.IsOfficialReShade)
            {
                info.IsValid = false;
                info.Error = $"Signer thumbprint does not match the official ReShade certificate ({info.Thumbprint}).";
            }
            else if (!trusted && info.IsOfficialReShade)
            {
                // ReShade's build certificate is published out-of-band on reshade.me and does not
                // chain to a public root on every system. Accept only if the digest itself is valid
                // (i.e. the failure is chain trust, not a corrupted/mutated binary) and the caller
                // pins the signer thumbprint. The caller also verifies the published SHA-256.
                var trustOnlyFailure = resultCode is 0x800B0109 or 0x800B010A or 0x800B010B or 0x800B0110;
                if (trustOnlyFailure)
                {
                    info.IsValid = true;
                    info.Warning = "Signature digest is valid and the signer matches the official ReShade thumbprint, but the local " +
                                   "certificate chain is not trusted (untrusted root). The pinned thumbprint and file hash are the trust anchor.";
                    Log.Warn($"ReShade certificate chain untrusted (0x{resultCode:X8}); accepting only because the published thumbprint matched.");
                }
                else
                {
                    info.IsValid = false;
                    info.Error = $"WinVerifyTrust failed with 0x{resultCode:X8} (not a chain-trust-only failure).";
                }
            }
            Log.Info($"Authenticode {Path.GetFileName(path)}: valid={info.IsValid}, officialReShade={info.IsOfficialReShade}, trust=0x{resultCode:X8}, subject='{info.Subject}'");
        }
        catch (Exception ex)
        {
            info.IsValid = false;
            info.Error = ex.Message;
            Log.Warn($"Authenticode check failed for {path}: {ex.Message}");
        }
        return info;
    }

    private static bool VerifyWinTrust(string path, out string error, out uint resultCode)
    {
        error = "";
        resultCode = 0;
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path
        };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        var dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2, // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE (offline-friendly)
                dwUnionChoice = 1, // WTD_CHOICE_FILE
                pFile = fileInfoPtr,
                dwStateAction = 0,
                dwProvFlags = 0x00000010 // WTD_REVOCATION_CHECK_NONE
            };
            Marshal.StructureToPtr(data, dataPtr, false);
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            var actionPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
            Marshal.StructureToPtr(action, actionPtr, false);
            resultCode = WinVerifyTrust(IntPtr.Zero, actionPtr, dataPtr);
            if (resultCode == 0) return true;
            error = $"WinVerifyTrust returned 0x{resultCode:X8}";
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPtr);
            Marshal.FreeHGlobal(dataPtr);
        }
    }

    [DllImport("wintrust.dll", PreserveSig = true, SetLastError = false)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, IntPtr pgActionID, IntPtr pWinTrustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
