using System.ComponentModel;
using System.Runtime.InteropServices;
using RuleVault.Storage;
using Xunit;

namespace RuleVault.PlatformTests;

public sealed class PlatformInventoryTests
{
    [Fact]
    public void WindowsDevelopmentPlatformIsAvailable()
    {
        Assert.True(System.OperatingSystem.IsWindows());
    }

    [Fact]
    public void RealDirectorySymlinkIsRejectedBeforeRead()
    {
        var root = CreateTempDirectory();
        try
        {
            var target = Path.Combine(root, "target");
            var link = Path.Combine(root, "link");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "rule.md"), "data");
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (UnauthorizedAccessException)
            {
                Assert.Skip("Windows symbolic-link creation is unavailable without the required developer-mode or privilege capability.");
            }
            catch (PlatformNotSupportedException)
            {
                Assert.Skip("The development OS does not expose symbolic-link creation.");
            }
            catch (IOException exception)
            {
                Assert.Skip($"Windows symbolic-link creation is unavailable in this test environment: {exception.Message}");
            }

            var result = SafePath.ValidateRelative(root, "link/rule.md", SafePathProfile.PrivateConfig);
            Assert.False(result.Supported);
            Assert.Equal("PATH_REPARSE", result.FailureCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RealDirectoryJunctionIsRejectedBeforeRead()
    {
        var root = CreateTempDirectory();
        var link = Path.Combine(root, "junction");
        try
        {
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "rule.md"), "data");
            JunctionFixture.Create(link, target);

            var result = SafePath.ValidateRelative(root, "junction/rule.md", SafePathProfile.PrivateConfig);
            Assert.False(result.Supported);
            Assert.Equal("PATH_REPARSE", result.FailureCode);
        }
        finally
        {
            JunctionFixture.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RealMultipleHardLinkIsRejected()
    {
        var root = CreateTempDirectory();
        try
        {
            var original = Path.Combine(root, "original.md");
            var alias = Path.Combine(root, "alias.md");
            File.WriteAllText(original, "data");
            if (!CreateHardLink(alias, original, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateHardLink failed.");
            }

            var result = SafePath.ValidateRelative(root, "original.md", SafePathProfile.PrivateConfig);
            Assert.False(result.Supported);
            Assert.Equal("PATH_HARDLINK", result.FailureCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingHandleRelativeFileUsesManagedFileNotFoundException()
    {
        var root = CreateTempDirectory();
        try
        {
            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                TrustedFileSystem.ReadAllBytesAsync(root, "missing.md", SafePathProfile.PrivateConfig, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "RuleVaultCli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    private static class JunctionFixture
    {
        private const uint GenericWrite = 0x40000000;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FsctlSetReparsePoint = 0x000900A4;
        private const uint FsctlDeleteReparsePoint = 0x000900AC;
        private const uint IoReparseTagMountPoint = 0xA0000003;

        public static void Create(string link, string target)
        {
            Directory.CreateDirectory(link);
            var substitute = @"\??\" + target;
            var print = target;
            var substituteBytes = System.Text.Encoding.Unicode.GetBytes(substitute);
            var printBytes = System.Text.Encoding.Unicode.GetBytes(print);
            var dataLength = checked((ushort)(8 + substituteBytes.Length + 2 + printBytes.Length + 2));
            var buffer = new byte[8 + dataLength];
            BitConverter.GetBytes(IoReparseTagMountPoint).CopyTo(buffer, 0);
            BitConverter.GetBytes(dataLength).CopyTo(buffer, 4);
            BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);
            BitConverter.GetBytes((ushort)substituteBytes.Length).CopyTo(buffer, 10);
            BitConverter.GetBytes((ushort)(substituteBytes.Length + 2)).CopyTo(buffer, 12);
            BitConverter.GetBytes((ushort)printBytes.Length).CopyTo(buffer, 14);
            substituteBytes.CopyTo(buffer, 16);
            printBytes.CopyTo(buffer, 16 + substituteBytes.Length + 2);

            using var handle = OpenDirectory(link);
            if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create a synthetic directory junction.");
            }
        }

        public static void Delete(string link)
        {
            if (!Directory.Exists(link))
            {
                return;
            }

            try
            {
                using var handle = OpenDirectory(link);
                var buffer = new byte[8];
                BitConverter.GetBytes(IoReparseTagMountPoint).CopyTo(buffer, 0);
                if (!DeviceIoControl(handle, FsctlDeleteReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to remove the synthetic directory junction.");
                }
            }
            finally
            {
                Directory.Delete(link);
            }
        }

        private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenDirectory(string path)
        {
            var handle = CreateFile(
                path,
                GenericWrite,
                0,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "Unable to open synthetic junction directory.");
            }

            return handle;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            Microsoft.Win32.SafeHandles.SafeFileHandle device,
            uint controlCode,
            byte[] inputBuffer,
            int inputBufferSize,
            IntPtr outputBuffer,
            int outputBufferSize,
            out int bytesReturned,
            IntPtr overlapped);
    }
}
