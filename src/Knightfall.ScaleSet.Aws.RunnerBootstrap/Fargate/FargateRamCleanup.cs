using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap.Fargate;

[SupportedOSPlatform("linux")]
internal static partial class FargateRamCleanup
{
    private const int Missing = 2;
    private const int NotDirectory = 20;
    private const int Symlink = 40;
    private const int RemoveDirectory = 0x200;
    private const UnixFileMode OwnerAccess = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    internal static void Remove(string parent, string name)
    {
        const int currentWorkingDirectory = -100;
        int descriptor = OpenDirectory(currentWorkingDirectory, parent);
        if (descriptor < 0)
        {
            throw CleanupFailed(Marshal.GetLastPInvokeError());
        }
        using var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        RemoveEntry(descriptor, name);
    }

    private static void RemoveEntry(int parent, string name)
    {
        int descriptor = OpenDirectory(parent, name);
        if (descriptor < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is NotDirectory or Symlink)
            {
                Unlink(parent, name, flags: 0);
            }
            else if (error != Missing)
            {
                throw CleanupFailed(error);
            }
            return;
        }

        using var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        // Restore access on the pinned directory inode. Never chmod a job-provided pathname.
        string directory = "/proc/self/fd/" + descriptor.ToString(CultureInfo.InvariantCulture);
        File.SetUnixFileMode(directory, OwnerAccess);
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RemoveEntry(descriptor, Path.GetFileName(entry));
        }
        Unlink(parent, name, RemoveDirectory);
    }

    private static int OpenDirectory(int parent, string path)
    {
        const int pathOnly = 0x200000;
        const int closeOnExec = 0x80000;
        const int arm64DirectoryNoFollow = 0xc000;
        const int x64DirectoryNoFollow = 0x30000;
        int directoryNoFollow = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => arm64DirectoryNoFollow,
            Architecture.X64 => x64DirectoryNoFollow,
            _ => throw new InvalidOperationException("The architecture does not support Fargate bootstrap cleanup."),
        };
        return OpenAt(parent, path, pathOnly | closeOnExec | directoryNoFollow);
    }

    private static void Unlink(int parent, string name, int flags)
    {
        if (UnlinkAt(parent, name, flags) != 0 && Marshal.GetLastPInvokeError() is var error && error != Missing)
        {
            throw CleanupFailed(error);
        }
    }

    private static IOException CleanupFailed(int error) =>
        new(FormattableString.Invariant($"The Fargate RAM configuration root could not be removed (errno {error})."));

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAt(int parent, string path, int flags);

    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnlinkAt(int parent, string path, int flags);
}
