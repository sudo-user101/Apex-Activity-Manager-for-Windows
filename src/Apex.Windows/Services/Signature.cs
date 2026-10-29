using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace Apex.Services;

/// Who signed a program, and whether Windows trusts that signature
/// (WinVerifyTrust, the check Windows itself makes), as the Mac's inspector
/// shows the code signature. Programs signed inside the file and Windows' own
/// programs, which are signed through the system's catalogs, are both checked.
/// Read-only; nothing is downloaded.
internal static class Signature
{
    public sealed record Result(bool Signed, bool Trusted, string Signer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO { public uint cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_CATALOG_INFO
    {
        public uint cbStruct, dwCatalogVersion; public string pcwszCatalogFilePath, pcwszMemberTag, pcwszMemberFilePath;
        public IntPtr hMemberFile, pbCalculatedFileHash; public uint cbCalculatedFileHash; public IntPtr pcCatalogContext, hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CATALOG_INFO { public uint cbStruct; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszCatalogFile; }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct; public IntPtr pPolicyCallbackData, pSIPClientData; public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pInfo; public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference; public uint dwProvFlags, dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
    [DllImport("wintrust.dll")] static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll")] static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provData, uint signer, bool counterSigner, uint counterSignerIndex);
    [DllImport("wintrust.dll")] static extern IntPtr WTHelperGetProvCertFromChain(IntPtr sgnr, uint cert);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CryptCATAdminAcquireContext2(out IntPtr catAdmin, IntPtr subsystem, string hashAlgorithm, IntPtr policy, uint flags);
    [DllImport("wintrust.dll", SetLastError = true)]
    static extern bool CryptCATAdminReleaseContext(IntPtr catAdmin, uint flags);
    [DllImport("wintrust.dll", SetLastError = true)]
    static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr catAdmin, SafeFileHandle file, ref uint cbHash, byte[]? hash, uint flags);
    [DllImport("wintrust.dll", SetLastError = true)]
    static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr catAdmin, byte[] hash, uint cbHash, uint flags, IntPtr prevCatInfo);
    [DllImport("wintrust.dll", SetLastError = true)]
    static extern bool CryptCATCatalogInfoFromContext(IntPtr catInfo, ref CATALOG_INFO info, uint flags);
    [DllImport("wintrust.dll", SetLastError = true)]
    static extern bool CryptCATAdminReleaseCatalogContext(IntPtr catAdmin, IntPtr catInfo, uint flags);

    static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    const uint UiNone = 2, ChoiceFile = 1, ChoiceCatalog = 2, StateVerify = 1, StateClose = 2, CacheOnlyUrl = 0x1000;

    public static Result Check(string path)
    {
        if (path.Length == 0 || !File.Exists(path)) return new(false, false, "");
        try
        {
            if (Embedded(path) is { } r) return r;
            return Catalog(path) ?? new(false, false, "");
        }
        catch { return new(false, false, ""); }
    }

    /// Signed inside the file. Null when it carries no signature.
    static Result? Embedded(string path)
    {
        var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, pFile, false);
            var (code, signer) = Verify(ChoiceFile, pFile);
            const int NoSignature = unchecked((int)0x800B0100);         // TRUST_E_NOSIGNATURE
            if (code == NoSignature || (code != 0 && signer.Length == 0 && !HasEmbeddedCertificate(path))) return null;
            return new(true, code == 0, signer);
        }
        finally { Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile); Marshal.FreeHGlobal(pFile); }
    }

    static bool HasEmbeddedCertificate(string path)
    {
        try { X509Certificate.CreateFromSignedFile(path); return true; } catch { return false; }
    }

    /// Signed through one of the system's catalogs (how Windows signs its own
    /// programs). Tried with SHA-256 and then SHA-1, which older catalogs use.
    static Result? Catalog(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        foreach (var algorithm in new[] { "SHA256", "SHA1" })
        {
            if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, algorithm, IntPtr.Zero, 0)) continue;
            IntPtr catInfo = IntPtr.Zero;
            try
            {
                uint cb = 0;
                CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref cb, null, 0);
                if (cb == 0) continue;
                var hash = new byte[cb];
                if (!CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref cb, hash, 0)) continue;
                catInfo = CryptCATAdminEnumCatalogFromHash(admin, hash, cb, 0, IntPtr.Zero);
                if (catInfo == IntPtr.Zero) continue;
                var info = new CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>() };
                if (!CryptCATCatalogInfoFromContext(catInfo, ref info, 0)) continue;
                var hashPin = GCHandle.Alloc(hash, GCHandleType.Pinned);
                var cat = new WINTRUST_CATALOG_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_CATALOG_INFO>(), pcwszCatalogFilePath = info.wszCatalogFile,
                    pcwszMemberTag = Convert.ToHexString(hash), pcwszMemberFilePath = path,
                    pbCalculatedFileHash = hashPin.AddrOfPinnedObject(), cbCalculatedFileHash = cb, hCatAdmin = admin,
                };
                var pCat = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_CATALOG_INFO>());
                try
                {
                    Marshal.StructureToPtr(cat, pCat, false);
                    var (code, signer) = Verify(ChoiceCatalog, pCat);
                    return new(true, code == 0, signer);
                }
                finally { Marshal.DestroyStructure<WINTRUST_CATALOG_INFO>(pCat); Marshal.FreeHGlobal(pCat); hashPin.Free(); }
            }
            finally
            {
                if (catInfo != IntPtr.Zero) CryptCATAdminReleaseCatalogContext(admin, catInfo, 0);
                CryptCATAdminReleaseContext(admin, 0);
            }
        }
        return null;
    }

    /// Runs Windows' check and reads the signer's name from what it verified.
    static (int Code, string Signer) Verify(uint choice, IntPtr pInfo)
    {
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(), dwUIChoice = UiNone, dwUnionChoice = choice, pInfo = pInfo,
            dwStateAction = StateVerify, dwProvFlags = CacheOnlyUrl,
        };
        var action = GenericVerifyV2;
        int code = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
        string signer = "";
        try
        {
            var prov = WTHelperProvDataFromStateData(data.hWVTStateData);
            var sgnr = prov == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(prov, 0, false, 0);
            var cert = sgnr == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvCertFromChain(sgnr, 0);
            // CRYPT_PROVIDER_CERT: a DWORD, then the certificate's context (pointer-aligned).
            var context = cert == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(cert, IntPtr.Size);
            if (context != IntPtr.Zero) signer = new X509Certificate2(context).GetNameInfo(X509NameType.SimpleName, false);
        }
        catch { }
        finally
        {
            data.dwStateAction = StateClose;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
        }
        return (code, signer);
    }
}
