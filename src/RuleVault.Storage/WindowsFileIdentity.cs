using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RuleVault.Storage;

internal static class WindowsFileIdentity
{
    private const uint FileAttributeReparsePoint = 0x0400;

    public static SafePathResult? CheckExistingFile(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = Open(path, FileAccess.Read);
            return CheckHandle(stream.SafeFileHandle, path);
        }
        catch (UnauthorizedAccessException exception)
        {
            return SafePathResult.Unsupported("NATIVE_IDENTITY_UNAVAILABLE", exception.Message);
        }
        catch (IOException exception)
        {
            return SafePathResult.Unsupported("NATIVE_IDENTITY_UNAVAILABLE", exception.Message);
        }
    }

    public static void EnsureSafeHandle(SafeFileHandle handle, string relativePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = CheckHandle(handle, relativePath);
        if (result is not null)
        {
            throw new SafePathException(result.FailureCode!, result.FailureReason!);
        }
    }

    public static FileStream Open(string path, FileAccess access)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new FileStream(path, FileMode.Open, access, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
        }

        var desiredAccess = access == FileAccess.Read ? GenericRead : GenericRead | GenericWrite;
        var handle = CreateFile(
            path,
            desiredAccess,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics | FileFlagOverlapped,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Unable to open '{path}' without following the final reparse point.");
        }

        return new FileStream(handle, access, 64 * 1024, isAsync: true);
    }

    public static FileStream OpenBeneathRoot(string root, string relativePath, FileAccess access)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Open(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)), access);
        }

        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw new SafePathException("PATH_INVALID", "A relative file path is required.");
        }

        using var rootHandle = Open(root, FileAccess.Read).SafeFileHandle;
        SafeFileHandle current = rootHandle;
        var ownsCurrent = false;
        try
        {
            var rootCheck = CheckHandle(current, root);
            if (rootCheck is not null)
            {
                throw new SafePathException(rootCheck.FailureCode!, rootCheck.FailureReason!);
            }

            for (var index = 0; index < parts.Length; index++)
            {
                var last = index == parts.Length - 1;
                var child = OpenRelative(current, parts[index], access, last);
                var childCheck = CheckHandle(child, relativePath);
                if (childCheck is not null)
                {
                    child.Dispose();
                    throw new SafePathException(childCheck.FailureCode!, childCheck.FailureReason!);
                }

                if (!last && !IsDirectory(child))
                {
                    child.Dispose();
                    throw new SafePathException("PATH_TYPE", "An intermediate path component is not a directory.");
                }

                if (ownsCurrent)
                {
                    current.Dispose();
                }

                current = child;
                ownsCurrent = true;
            }

            var stream = new FileStream(current, access, 64 * 1024, isAsync: false);
            ownsCurrent = false;
            return stream;
        }
        finally
        {
            if (ownsCurrent)
            {
                current.Dispose();
            }
        }
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileReadData = 0x00000001;
    private const uint FileWriteData = 0x00000002;
    private const uint FileWriteAttributes = 0x00000100;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileTraverse = 0x00000020;
    private const uint Synchronize = 0x00100000;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileOpen = 0x00000001;
    private const uint ObjectAttributesCaseInsensitive = 0x00000040;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        IntPtr objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr extendedAttributes,
        uint extendedAttributesLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    private static bool IsDirectory(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to inspect opened path component.");
        }

        return (information.FileAttributes & FileAttributeDirectory) != 0;
    }

    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string name, FileAccess access, bool final)
    {
        var desiredAccess = final
            ? access switch
            {
                FileAccess.Read => FileReadData | FileReadAttributes | Synchronize,
                FileAccess.Write => FileWriteData | FileWriteAttributes | Synchronize,
                _ => FileReadData | FileWriteData | FileReadAttributes | FileWriteAttributes | Synchronize
            }
            : FileReadAttributes | FileTraverse | Synchronize;
        var options = (final ? FileNonDirectoryFile : FileDirectoryFile) | FileOpenReparsePoint | FileSynchronousIoNonAlert;
        var namePointer = Marshal.StringToHGlobalUni(name);
        var objectName = new UnicodeString
        {
            Length = checked((ushort)(name.Length * 2)),
            MaximumLength = checked((ushort)(name.Length * 2 + 2)),
            Buffer = namePointer
        };
        var objectNamePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var attributes = new ObjectAttributes
        {
            Length = Marshal.SizeOf<ObjectAttributes>(),
            RootDirectory = parent.DangerousGetHandle(),
            ObjectName = objectNamePointer,
            Attributes = ObjectAttributesCaseInsensitive
        };
        var attributesPointer = Marshal.AllocHGlobal(Marshal.SizeOf<ObjectAttributes>());
        Marshal.StructureToPtr(objectName, objectNamePointer, false);
        Marshal.StructureToPtr(attributes, attributesPointer, false);
        try
        {
            var status = NtCreateFile(
                out var handle,
                desiredAccess,
                attributesPointer,
                out _,
                IntPtr.Zero,
                0,
                FileShareRead | FileShareWrite | FileShareDelete,
                FileOpen,
                options,
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                handle?.Dispose();
                throw new Win32Exception(status, $"Unable to open '{name}' beneath the trusted root.");
            }

            return handle;
        }
        finally
        {
            Marshal.FreeHGlobal(attributesPointer);
            Marshal.FreeHGlobal(objectNamePointer);
            Marshal.FreeHGlobal(namePointer);
        }
    }

    private static SafePathResult? CheckHandle(SafeFileHandle handle, string displayPath)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Unable to inspect '{displayPath}'.");
        }

        if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            return SafePathResult.Unsupported("PATH_REPARSE", $"Reparse or link indirection is not allowed: {displayPath}.");
        }

        if (information.NumberOfLinks > 1)
        {
            return SafePathResult.Unsupported("PATH_HARDLINK", $"Multiple hard links are not allowed: {displayPath}.");
        }

        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
