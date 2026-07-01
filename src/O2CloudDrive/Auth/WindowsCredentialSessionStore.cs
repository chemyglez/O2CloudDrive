using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace O2CloudDrive.Auth;

public sealed class WindowsCredentialSessionStore : ISessionStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private readonly string _targetName;

    public WindowsCredentialSessionStore(string targetName)
    {
        _targetName = targetName;
    }

    public O2Session? TryRead()
    {
        if (!CredRead(_targetName, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            var protectedBytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, protectedBytes, 0, protectedBytes.Length);
            var jsonBytes = Decompress(protectedBytes);
            return JsonSerializer.Deserialize<O2Session>(jsonBytes);
        }
        catch
        {
            return null;
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void Save(O2Session session)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(session);
        var protectedBytes = Compress(jsonBytes);
        var blobPointer = Marshal.AllocHGlobal(protectedBytes.Length);

        try
        {
            Marshal.Copy(protectedBytes, 0, blobPointer, protectedBytes.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = _targetName,
                CredentialBlobSize = (uint)protectedBytes.Length,
                CredentialBlob = blobPointer,
                Persist = CredentialPersistLocalMachine,
                UserName = "O2 Cloud",
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException(
                    $"Windows Credential Manager no pudo guardar la sesion. Win32={Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blobPointer);
        }
    }

    public void Delete()
    {
        CredDelete(_targetName, CredentialTypeGeneric, 0);
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPointer);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }
}
