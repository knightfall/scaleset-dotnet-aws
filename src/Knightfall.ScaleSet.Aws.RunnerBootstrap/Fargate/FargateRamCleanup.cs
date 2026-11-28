using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Knightfall.ScaleSet.Aws.RunnerBootstrap.Linux;
using Microsoft.Win32.SafeHandles;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap.Fargate;

[SupportedOSPlatform("linux")]
internal sealed partial class FargateRamCleanup : IDisposable
{
    private const int Missing = 2;
    private const int IsDirectory = 21;
    private const int RemoveDirectory = 0x200;
    private const UnixFileMode OwnerAccess = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const int MaximumEntries = 65_536;
    private readonly SafeFileHandle _parent;
    private readonly SafeFileHandle _root;
    private readonly string _name;
    private readonly (ulong Mount, ulong Inode) _rootIdentity;
    private readonly int _maximumEntries;
    private readonly Lock _gate = new();
    private bool _attempted;
    private bool _completed;

    private FargateRamCleanup(SafeFileHandle parent, SafeFileHandle root, string name, int maximumEntries)
    {
        _parent = parent;
        _root = root;
        _name = name;
        _rootIdentity = LinuxFiles.GetEntryIdentity(Descriptor(root), string.Empty);
        _maximumEntries = maximumEntries;
    }

    internal static FargateRamCleanup Open(string parent, string name, bool create, int maximumEntries = MaximumEntries)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumEntries);
        RequireExclusiveNamespace();
        const int currentWorkingDirectory = -100;
        var parentHandle = RequireDirectory(currentWorkingDirectory, parent);
        SafeFileHandle? root = null;
        try
        {
            if (create && MkdirAt(Descriptor(parentHandle), name, (uint)OwnerAccess) != 0)
            {
                throw CleanupFailed(Marshal.GetLastPInvokeError());
            }
            root = RequireDirectory(Descriptor(parentHandle), name);
            return new FargateRamCleanup(parentHandle, root, name, maximumEntries);
        }
        catch
        {
            root?.Dispose();
            parentHandle.Dispose();
            throw;
        }
    }

    internal void Remove()
    {
        lock (_gate) RemoveOnce();
    }

    private void RemoveOnce()
    {
        if (_attempted)
        {
            if (!_completed) throw new IOException("The previous Fargate RAM cleanup was incomplete.");
            return;
        }
        _attempted = true;
        try
        {
            RemoveKnownCredentials();
            RequireExclusiveNamespace();
            VerifyName(Descriptor(_parent), _name, _rootIdentity);
            RemoveContents();
            VerifyName(Descriptor(_parent), _name, _rootIdentity);
            RemoveEmptyDirectory(Descriptor(_parent), _name);
            _completed = true;
        }
        finally
        {
            _root.Dispose();
            _parent.Dispose();
        }
    }

    public void Dispose()
    {
        // The command also disposes the boundary after the runner's finally has attempted cleanup.
        // Preserve an in-flight failure and never spend a second cleanup budget.
        lock (_gate)
        {
            if (!_attempted) RemoveOnce();
        }
    }

    private static void RequireExclusiveNamespace()
    {
        // A private PID namespace without sidecars or ECS Exec is required. Signal zero only
        // checks for other process leaders (including unreaped ones). A pipe drain cannot prove this.
        const int noSuchProcess = 3;
        if (Environment.ProcessId != 1 || Kill(-1, 0) != -1 || Marshal.GetLastPInvokeError() != noSuchProcess)
        {
            throw new IOException("The Fargate bootstrap could not verify exclusive access to the RAM directory.");
        }
    }

    private void RemoveKnownCredentials()
    {
        RestoreAccess(_root);
        int firstError = 0;
        foreach (string credential in JitRunnerConfiguration.CleanupNames)
        {
            int error = UnlinkNonDirectory(Descriptor(_root), credential);
            if (firstError == 0 && error is not (Missing or IsDirectory))
            {
                firstError = error;
            }
        }
        if (firstError != 0) throw CleanupFailed(firstError);
    }

    private sealed record Frame(string Name, (ulong Mount, ulong Inode) Identity, Queue<string> Directories);

    private void RemoveContents()
    {
        int remaining = _maximumEntries;
        var frames = new Stack<Frame>();
        var current = RequireDirectory(Descriptor(_root), ".");
        try
        {
            frames.Push(new Frame(_name, _rootIdentity, RemoveFiles(current, ref remaining)));
            while (true)
            {
                var frame = frames.Peek();
                if (frame.Directories.TryDequeue(out string? child))
                {
                    var next = RequireDirectory(Descriptor(current), child);
                    current.Dispose();
                    current = next;
                    var identity = LinuxFiles.GetEntryIdentity(Descriptor(current), string.Empty);
                    RestoreAccess(current);
                    frames.Push(new Frame(child, identity, RemoveFiles(current, ref remaining)));
                }
                else
                {
                    frames.Pop();
                    if (frames.Count == 0) return;
                    // Verify the pinned child's parent against the identity recorded on descent
                    // before allowing any mutation through it. The pinned root stays open throughout.
                    var parent = RequireDirectory(Descriptor(current), "..");
                    current.Dispose();
                    current = parent;
                    VerifyName(Descriptor(current), string.Empty, frames.Peek().Identity);
                    VerifyName(Descriptor(current), frame.Name, frame.Identity);
                    RemoveEmptyDirectory(Descriptor(current), frame.Name);
                }
            }
        }
        finally
        {
            current.Dispose();
        }
    }

    private static Queue<string> RemoveFiles(SafeFileHandle directory, ref int remaining)
    {
        var directories = new Queue<string>();
        foreach (string entry in Directory.EnumerateFileSystemEntries(DescriptorPath(directory)))
        {
            if (remaining == 0) throw new IOException("The Fargate RAM cleanup entry limit was reached.");
            remaining--;
            string name = Path.GetFileName(entry);
            int error = UnlinkNonDirectory(Descriptor(directory), name);
            if (error == IsDirectory) directories.Enqueue(name);
            else if (error != 0) throw CleanupFailed(error);
        }
        return directories;
    }

    private static void VerifyName(int directory, string name, (ulong Mount, ulong Inode) expected)
    {
        if (LinuxFiles.GetEntryIdentity(directory, name) != expected)
        {
            throw new IOException("The Fargate RAM directory no longer has its original identity.");
        }
    }

    private static int UnlinkNonDirectory(int parent, string name)
    {
        if (UnlinkAt(parent, name, 0) == 0)
        {
            return 0;
        }
        return Marshal.GetLastPInvokeError();
    }

    private static SafeFileHandle RequireDirectory(int parent, string name)
    {
        int descriptor = OpenDirectory(parent, name);
        if (descriptor < 0)
        {
            throw CleanupFailed(Marshal.GetLastPInvokeError());
        }
        return new SafeFileHandle(descriptor, ownsHandle: true);
    }

    private static int Descriptor(SafeFileHandle handle) => (int)handle.DangerousGetHandle();

    private static string DescriptorPath(SafeFileHandle handle) =>
        "/proc/self/fd/" + Descriptor(handle).ToString(CultureInfo.InvariantCulture);

    private static void RestoreAccess(SafeFileHandle directory)
    {
        // chmod only the inode pinned by this descriptor, including for directories with mode 000.
        if (Chmod(DescriptorPath(directory), (uint)OwnerAccess) != 0)
        {
            throw CleanupFailed(Marshal.GetLastPInvokeError());
        }
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

    private static void RemoveEmptyDirectory(int parent, string name)
    {
        if (UnlinkAt(parent, name, RemoveDirectory) != 0)
        {
            throw CleanupFailed(Marshal.GetLastPInvokeError());
        }
    }

    private static IOException CleanupFailed(int error) =>
        new(FormattableString.Invariant($"The Fargate RAM configuration root could not be removed (errno {error})."));

    [LibraryImport("libc", EntryPoint = "mkdirat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MkdirAt(int parent, string name, uint mode);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);

    [LibraryImport("libc", EntryPoint = "chmod", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Chmod(string path, uint mode);

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAt(int parent, string path, int flags);

    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnlinkAt(int parent, string path, int flags);
}
