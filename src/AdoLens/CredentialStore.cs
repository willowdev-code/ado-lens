using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AdoLens;

/// <summary>
/// PAT storage in Windows Credential Manager: a generic credential of the current user, kept on this machine only
/// (not roamed) and encrypted by Windows with the user's logon secret. It is visible and removable in
/// Control Panel → Credential Manager → Windows Credentials, as "ado-lens:&lt;connection&gt;".
/// </summary>
public static class CredentialStore
{
    const uint CRED_TYPE_GENERIC = 1;
    const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    const int ERROR_NOT_FOUND = 1168;

    public static string TargetFor(string connection) => $"ado-lens:{connection.ToLowerInvariant()}";

    public static string? Read(string connection)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!CredRead(TargetFor(connection), CRED_TYPE_GENERIC, 0, out var ptr))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ERROR_NOT_FOUND) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return null;
            return Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public static void Write(string connection, string organization, string pat)
    {
        var bytes = Encoding.Unicode.GetBytes(pat);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = TargetFor(connection),
                UserName = organization,
                Comment = $"Azure DevOps personal access token used by ADO Lens (https://dev.azure.com/{organization})",
                CredentialBlob = blob,
                CredentialBlobSize = (uint)bytes.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!CredWrite(ref cred, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            // Do not leave the token in unmanaged or managed buffers longer than needed.
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
            Array.Clear(bytes);
        }
    }

    /// <returns>false when there was nothing to delete.</returns>
    public static bool Delete(string connection)
    {
        if (CredDelete(TargetFor(connection), CRED_TYPE_GENERIC, 0)) return true;
        var error = Marshal.GetLastWin32Error();
        if (error == ERROR_NOT_FOUND) return false;
        throw new Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);
}
