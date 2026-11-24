using System.Diagnostics;
using System.Runtime.Versioning;
using Knightfall.ScaleSet.Aws.RunnerBootstrap.Linux;
using static Knightfall.ScaleSet.Aws.RunnerBootstrap.Fargate.FargateMountLayout;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap.Fargate;

[SupportedOSPlatform("linux")]
internal sealed class FargateRunnerBoundary : ILinuxOneJobBoundary, IDisposable
{
    private const long MiB = 1024L * 1024;
    private static readonly string[] ManagedEntries = ["Runner.Listener.dll", "Runner.Worker.dll", "Runner.PluginHost.dll"];
    private readonly string _root;
    private readonly string _dotnet;

    private FargateRunnerBoundary(string root, string dotnet)
    {
        _root = root;
        _dotnet = dotnet;
    }

    internal static FargateRunnerBoundary Create(FargateBootstrapSettings settings)
    {
        var mounts = File.ReadAllLines("/proc/self/mountinfo");
        string[] membership = File.ReadAllLines("/proc/self/cgroup");
        string? swapLimit = FindCgroupSwapLimit(mounts, membership);
        bool swapDisabled = swapLimit is not null && File.Exists(swapLimit) && File.ReadAllText(swapLimit).Trim() == "0";
        string runnerBin = Path.Combine(settings.RunnerDistribution, "bin");
        string runnerExternals = Path.Combine(settings.RunnerDistribution, "externals");
        if (LinuxFiles.IsRoot() || !HasVerifiedMemoryMount(mounts, swapDisabled) ||
            new DriveInfo(MemoryMount).TotalSize is < (8 * MiB) or > (128 * MiB) ||
            !HasWritableWorkMount(settings.WorkScratch, mounts) ||
            !IsReadOnly(settings.RunnerDistribution, mounts) || !IsReadOnly(settings.RunnerDotnet, mounts) ||
            !IsReadOnly(settings.RunnerChild, mounts) || !LinuxFiles.IsTrustedFile(settings.RunnerDotnet, ownerOnly: false) ||
            !LinuxFiles.IsTrustedFile(settings.RunnerChild, ownerOnly: false) ||
            !LinuxFiles.HasTrustedParents(Path.Combine(runnerBin, "not-a-file")) || !Directory.Exists(runnerExternals) ||
            !ManagedEntries.All(name => LinuxFiles.IsTrustedFile(Path.Combine(runnerBin, name), ownerOnly: false)))
        {
            throw new IOException("The Fargate task's runner storage or identity boundary could not be verified.");
        }

        string instance = "scaleset-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(MemoryMount, instance);
        string work = Path.Combine(settings.WorkScratch, instance);
        var boundary = new FargateRunnerBoundary(root, settings.RunnerDotnet);
        try
        {
            Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(root, "externals"), runnerExternals);
            Directory.CreateSymbolicLink(Path.Combine(root, "_work"), work);
            foreach (string original in Directory.EnumerateFileSystemEntries(runnerBin))
            {
                string name = Path.GetFileName(original);
                string destination = Path.Combine(bin, name);
                if (ManagedEntries.Contains(name, StringComparer.Ordinal))
                {
                    // Assembly.Location must resolve to this actual RAM file, even when /dev/shm is mounted noexec.
                    File.Copy(original, destination);
                }
                else
                {
                    string source = name is "Runner.Worker" or "Runner.PluginHost" ? settings.RunnerChild : original;
                    File.CreateSymbolicLink(destination, source);
                }
            }
            return boundary;
        }
        catch
        {
            boundary.Dispose();
            throw;
        }
    }

    public void PrepareHome()
    {
        Directory.CreateDirectory(Path.Combine(_root, "home"));
        Directory.CreateDirectory(Path.Combine(_root, "tmp"));
    }

    public void WriteConfigFile(string name, ReadOnlySpan<byte> contents)
    {
        using var file = new FileStream(Path.Combine(_root, name), new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        file.Write(contents);
    }

    public Process? StartRunner()
    {
        var start = StartInfo();
        // The child inherits no-new-privileges from the thread that forks it.
        LinuxFiles.ProhibitPrivilegeGain();
        return Process.Start(start);
    }

    internal ProcessStartInfo StartInfo()
    {
        var start = new ProcessStartInfo(_dotnet)
        {
            WorkingDirectory = _root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(Path.Combine(_root, "bin", "Runner.Listener.dll"));
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--once");
        start.Environment.Clear();
        start.Environment["SCALESET_RUNNER_DOTNET"] = _dotnet;
        start.Environment["SCALESET_RUNNER_ROOT"] = _root;
        start.Environment["DOTNET_ReadyToRun"] = "0";
        start.Environment["HOME"] = Path.Combine(_root, "home");
        start.Environment["TMPDIR"] = Path.Combine(_root, "tmp");
        start.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        start.Environment["LANG"] = "C.UTF-8";
        return start;
    }

    public void RemoveConfigFiles()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    public void Dispose() => RemoveConfigFiles();
}

internal static class FargateMountLayout
{
    internal const string MemoryMount = "/dev/shm";

    internal static bool HasVerifiedMemoryMount(string[] mounts, bool cgroupSwapDisabled)
    {
        var memory = mounts.Select(ParseMount).Where(mount => mount?.Location == MemoryMount).ToArray();
        return memory is [{ Type: "tmpfs" } mount] && mount.Options.Contains("rw", StringComparer.Ordinal) &&
            mount.Options.Contains("nodev", StringComparer.Ordinal) && mount.Options.Contains("nosuid", StringComparer.Ordinal) &&
            (cgroupSwapDisabled || mount.FileSystemOptions.Contains("noswap", StringComparer.Ordinal)) &&
            !mounts.Select(ParseMount).Any(other => other?.Location.StartsWith(MemoryMount + "/", StringComparison.Ordinal) == true);
    }

    internal static string? FindCgroupSwapLimit(string[] mounts, string[] membership)
    {
        if (membership is not [var path] || !path.StartsWith("0::/", StringComparison.Ordinal))
        {
            return null;
        }

        var cgroupMounts = mounts.Select(ParseMount)
            .Where(item => item is { Type: "cgroup2", Location: "/sys/fs/cgroup" }).ToArray();
        if (cgroupMounts is not [{ } cgroup])
        {
            return null;
        }

        string group = path[3..];
        string relative;
        if (group == "/" || group == cgroup.Root)
        {
            relative = string.Empty;
        }
        else if (cgroup.Root == "/")
        {
            relative = group[1..];
        }
        else if (group.StartsWith(cgroup.Root + "/", StringComparison.Ordinal))
        {
            relative = group[(cgroup.Root.Length + 1)..];
        }
        else
        {
            return null;
        }

        return relative.Split('/').Any(segment => segment is "." or ".." ||
            segment.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')))
            ? null : Path.Combine(cgroup.Location, relative, "memory.swap.max");
    }

    internal static bool HasWritableWorkMount(string path, string[] lines)
    {
        var mounts = lines.Select(ParseMount).Where(item => item?.Location == path).ToArray();
        return mounts is [{ } mount] && mount.Options.Contains("rw", StringComparer.Ordinal) &&
            mount.Type is "tmpfs" or "ext4" or "xfs" or "virtiofs" && path is not ("/" or MemoryMount) &&
            !path.StartsWith(MemoryMount + "/", StringComparison.Ordinal) && new DirectoryInfo(path).LinkTarget is null;
    }

    internal static bool IsReadOnly(string path, string[] lines)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path)
        {
            return false;
        }
        var mount = lines.Select(ParseMount)
            .Where(item => item is not null && (item.Location == "/" || path == item.Location ||
                path.StartsWith(item.Location + "/", StringComparison.Ordinal)))
            .MaxBy(item => item!.Location.Length);
        return mount?.Options.Contains("ro", StringComparer.Ordinal) == true;
    }

    private static Mount? ParseMount(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int separator = Array.IndexOf(fields, "-");
        return fields.Length >= 10 && separator >= 6 && separator + 3 < fields.Length
            ? new Mount(fields[3], fields[4], fields[5].Split(','), fields[separator + 1], fields[separator + 3].Split(','))
            : null;
    }

    private sealed record Mount(string Root, string Location, string[] Options, string Type, string[] FileSystemOptions);
}
