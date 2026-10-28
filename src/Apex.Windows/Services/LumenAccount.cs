using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using Apex.Core;

namespace Apex.Services;

/// The Lumen key and how it was bought. The key lives in Windows' Credential
/// Manager (encrypted for this Windows account), never in Apex's settings file.
internal static class LumenAccount
{
    const string Target = "Apex Activity Manager: Lumen AI";
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static string? Key => Environment.GetEnvironmentVariable("APEX_LUMEN_KEY") is { Length: > 0 } k ? k : Read();
    public static bool HasKey => Key != null;
    public static Lumen.Access? Access
    {
        get => Enum.TryParse<Lumen.Access>(Settings.Get<string?>("lumenAccess", null), out var a) ? a : null;
        set => Settings.Set<string?>("lumenAccess", value?.ToString());
    }

    /// Checks the key with the Lumen service, then keeps it. Returns a problem, or null.
    public static async Task<string?> Connect(string key, Lumen.Access access)
    {
        key = key.Trim();
        if (key.Length == 0) return "Paste your Lumen key first.";
        if (Lumen.Base is not { } b) return "Lumen AI isn’t open yet. CyberNet Labs will open it soon.";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, b.TrimEnd('/') + "/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var res = await http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return Lumen.Problem((int)res.StatusCode);
        }
        catch { return "Couldn’t reach Lumen AI. Check the internet connection."; }
        if (!Write(key)) return "Windows didn’t let Apex keep the key in Credential Manager.";
        Access = access;
        return null;
    }

    public static void SignOut() { Delete(); Access = null; }

    // ---- Credential Manager --------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public uint Flags, Type; public string TargetName; public string? Comment; public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist, AttributeCount; public IntPtr Attributes; public string? TargetAlias, UserName;
    }
    const uint CRED_TYPE_GENERIC = 1, CRED_PERSIST_LOCAL_MACHINE = 2;
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredWriteW(ref CREDENTIAL c, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredReadW(string target, uint type, uint flags, out IntPtr c);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr p);

    internal static bool Write(string secret, string target = Target)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var c = new CREDENTIAL { Type = CRED_TYPE_GENERIC, TargetName = target, CredentialBlobSize = (uint)bytes.Length, CredentialBlob = blob,
                                     Persist = CRED_PERSIST_LOCAL_MACHINE, UserName = Environment.UserName };
            return CredWriteW(ref c, 0);
        }
        finally { Marshal.FreeHGlobal(blob); }
    }

    internal static string? Read(string target = Target)
    {
        if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out var p)) return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(p);
            return c.CredentialBlobSize == 0 ? null : Marshal.PtrToStringUni(c.CredentialBlob, (int)c.CredentialBlobSize / 2);
        }
        finally { CredFree(p); }
    }

    internal static bool Delete(string target = Target) => CredDeleteW(target, CRED_TYPE_GENERIC, 0);
}
