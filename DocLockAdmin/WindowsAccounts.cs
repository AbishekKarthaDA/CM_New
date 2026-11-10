using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// Windows Credential Manager (generic credentials) + "runas /netonly"-style logons, so one process can reach
// SQL Server as a different Windows account per environment. Passwords are only read at startup to create the
// logon token; they are not kept in memory afterwards.
public static class WindowsAccounts
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;   // survives logoff/reboot; still only readable by this Windows user
    private const int ERROR_NOT_FOUND = 1168;
    private const int LOGON32_LOGON_NEW_CREDENTIALS = 9; // same as runas /netonly: credentials used for network access only
    private const int LOGON32_PROVIDER_WINNT50 = 3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonUser(string user, string? domain, string password, int logonType, int provider, out SafeAccessTokenHandle token);

    public static (string User, string Password)? Read(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var ptr))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND) return null;
            throw new Win32Exception(err);
        }
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            var password = c.CredentialBlobSize > 0 ? Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2) : "";
            return (c.UserName ?? "", password);
        }
        finally { CredFree(ptr); }
    }

    public static void Write(string target, string user, string password)
    {
        var bytes = Encoding.Unicode.GetBytes(password);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var c = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC, TargetName = target, UserName = user, Comment = "Used by DocLockAdmin",
                CredentialBlob = blob, CredentialBlobSize = bytes.Length, Persist = CRED_PERSIST_LOCAL_MACHINE
            };
            if (!CredWrite(ref c, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Array.Clear(bytes);
            Marshal.FreeHGlobal(blob);
        }
    }

    public static void Delete(string target)
    {
        if (!CredDelete(target, CRED_TYPE_GENERIC, 0) && Marshal.GetLastWin32Error() != ERROR_NOT_FOUND)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    // Note: NEW_CREDENTIALS does not check the password here; a wrong password shows up as a SQL login failure.
    public static SafeAccessTokenHandle NetworkLogon(string account, string password)
    {
        string? domain = null, user = account;
        int slash = account.IndexOf('\\');
        if (slash > 0) { domain = account[..slash]; user = account[(slash + 1)..]; }
        if (!LogonUser(user, domain, password, LOGON32_LOGON_NEW_CREDENTIALS, LOGON32_PROVIDER_WINNT50, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return token;
    }
}
