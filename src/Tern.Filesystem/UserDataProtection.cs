using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Tern.Filesystem;

/// <summary>Versioned CurrentUser DPAPI envelopes; legacy unprotected data remains readable.</summary>
public static class UserDataProtection
{
    private static ReadOnlySpan<byte> Magic => "TERNDP01"u8;

    public static byte[] Protect(byte[] plaintext)
    {
        var encrypted = Transform(plaintext, protect: true);
        var envelope = new byte[Magic.Length + encrypted.Length];
        Magic.CopyTo(envelope);
        encrypted.CopyTo(envelope, Magic.Length);
        return envelope;
    }

    public static byte[] Unprotect(byte[] envelope) => envelope.AsSpan().StartsWith(Magic)
        ? Transform(envelope.AsSpan(Magic.Length).ToArray(), protect: false)
        : envelope;

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows())
            throw new IOException("Windows current-user protection is unavailable. No unprotected copy was written.");
        var pinned = GCHandle.Alloc(input, GCHandleType.Pinned);
        var output = new Blob();
        try
        {
            var source = new Blob { Length = input.Length, Data = pinned.AddrOfPinnedObject() };
            var succeeded = protect
                ? CryptProtectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!succeeded)
                throw new IOException("Windows could not " + (protect ? "protect" : "unlock") +
                    " this secondary copy for the current Windows user.", new Win32Exception(Marshal.GetLastWin32Error()));
            if (output.Length < 0 || output.Length > 256 * 1024 * 1024)
                throw new IOException("The protected secondary copy exceeds its supported size.");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            pinned.Free();
            if (output.Data != IntPtr.Zero)
            {
                var zeros = new byte[4096];
                for (var offset = 0; offset < output.Length; offset += zeros.Length)
                    Marshal.Copy(zeros, 0, IntPtr.Add(output.Data, offset), Math.Min(zeros.Length, output.Length - offset));
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out Blob output);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
