using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Tern.Desktop.Services;

public interface IAICredentialStore
{
    void Save(string profileId, string secret);
    string? Read(string profileId);
    void Remove(string profileId);
}

/// <summary>Windows Credential Manager generic credentials, protected for the signed-in Windows user.</summary>
public sealed partial class WindowsAICredentialStore : IAICredentialStore
{
    private const uint Generic = 1;
    private const int NotFound = 1168;
    private static string Target(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid connection identity.", nameof(id));
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Saved AI credentials require Windows Credential Manager.");
        return "Tern.AI/" + id;
    }

    public void Save(string profileId, string secret)
    {
        var target = Target(profileId);
        var bytes = Encoding.Unicode.GetBytes(secret);
        if (bytes.Length > 2560) throw new ArgumentException("The credential exceeds the Windows storage limit.", nameof(secret));
        var targetPointer = Marshal.StringToCoTaskMemUni(target);
        var userPointer = Marshal.StringToCoTaskMemUni("Tern AI connection");
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        var structure = Marshal.AllocCoTaskMem(Marshal.SizeOf<Credential>());
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential { Type = Generic, TargetName = targetPointer, BlobSize = (uint)bytes.Length,
                Blob = blob, Persist = 2, UserName = userPointer };
            Marshal.StructureToPtr(credential, structure, false);
            if (!CredWrite(structure, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not securely save the AI credential.");
        }
        finally
        {
            Array.Clear(bytes);
            for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(blob, i, 0);
            Marshal.FreeCoTaskMem(structure); Marshal.FreeCoTaskMem(blob);
            Marshal.FreeCoTaskMem(targetPointer); Marshal.FreeCoTaskMem(userPointer);
        }
    }

    public string? Read(string profileId)
    {
        var target = Target(profileId);
        if (!CredRead(target, Generic, 0, out var pointer))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == NotFound) return null;
            throw new Win32Exception(error, "Could not read the saved AI credential.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.BlobSize > 2560 || credential.BlobSize % 2 != 0) throw new InvalidOperationException("The saved AI credential is invalid.");
            return Marshal.PtrToStringUni(credential.Blob, (int)credential.BlobSize / 2);
        }
        finally { CredFree(pointer); }
    }
    public void Remove(string profileId)
    {
        if (!CredDelete(Target(profileId), Generic, 0) && Marshal.GetLastPInvokeError() != NotFound)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not remove the saved AI credential.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags; public uint Type; public nint TargetName; public nint Comment; public long LastWritten;
        public uint BlobSize; public nint Blob; public uint Persist; public uint AttributeCount;
        public nint Attributes; public nint TargetAlias; public nint UserName;
    }
    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CredWrite(nint credential, uint flags);
    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CredRead(string target, uint type, uint flags, out nint credential);
    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CredDelete(string target, uint type, uint flags);
    [LibraryImport("advapi32.dll")] private static partial void CredFree(nint buffer);
}
