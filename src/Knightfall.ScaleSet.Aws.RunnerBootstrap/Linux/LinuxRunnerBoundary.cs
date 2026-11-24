using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap.Linux;

[SupportedOSPlatform("linux")]
internal sealed class LinuxRunnerBoundary : ILinuxOneJobBoundary
{
    private const string DropPrivileges = "/usr/bin/setpriv";
    private const string BootIdFile = "/proc/sys/kernel/random/boot_id";
    private const int MaximumProofBytes = 4096;

    private readonly LinuxRunnerSettings _settings;
    private readonly string _listener;

    private LinuxRunnerBoundary(LinuxRunnerSettings settings, string listener)
    {
        _settings = settings;
        _listener = listener;
    }

    internal static LinuxRunnerBoundary Verify(LinuxRunnerSettings settings)
    {
        if (!LinuxFiles.IsRoot() || settings.UserId < 1000 || settings.GroupId < 1000 ||
            !LinuxFiles.IsTrustedFile(DropPrivileges, ownerOnly: false) ||
            !LinuxFiles.IsPrivateRoot(settings.RuntimeRoot, settings.GroupId) ||
            settings.RuntimeRoot.Any(char.IsWhiteSpace) ||
            !VerifyVolatileRoot(settings.RuntimeRoot) ||
            !LinuxFiles.IsTrustedFile(settings.BoundaryProof, ownerOnly: true))
        {
            throw Unverified();
        }

        var bootId = File.ReadAllText(BootIdFile).Trim();
        using var proofFile = File.OpenRead(settings.BoundaryProof);
        if (proofFile.Length is <= 0 or > MaximumProofBytes)
        {
            throw Unverified();
        }
        var proof = JsonSerializer.Deserialize<LinuxRunnerBoundaryProof>(proofFile, BootstrapJson.Options);
        if (proof is null || proof.ProtocolVersion != 1 || proof.BootId != bootId || !Guid.TryParseExact(bootId, "D", out _) ||
            proof.RuntimeRoot != settings.RuntimeRoot || proof.UserId != settings.UserId || proof.GroupId != settings.GroupId ||
            proof.ListenerSha256 != settings.ListenerSha256 || !proof.ProcessIsolationVerified ||
            !proof.Ipv4MetadataDenied || !proof.Ipv6MetadataDenied || !proof.DockerBoundaryVerified)
        {
            throw Unverified();
        }

        string binary = Path.Combine(settings.RuntimeRoot, "bin", "Runner.Listener");
        if (!LinuxFiles.IsTrustedFile(binary, ownerOnly: false, privateRoot: settings.RuntimeRoot))
        {
            throw Unverified();
        }
        using var executable = File.OpenRead(binary);
        if (Convert.ToHexStringLower(SHA256.HashData(executable)) != settings.ListenerSha256)
        {
            throw Unverified();
        }

        return new LinuxRunnerBoundary(settings, binary);
    }

    private static bool VerifyVolatileRoot(string root)
    {
        string[] swaps = File.ReadAllLines("/proc/swaps");
        return LinuxMounts.IsVolatileRunnerRoot(root, File.ReadAllLines("/proc/self/mountinfo"), swaps,
            swaps.Length > 1 && HasNoSwapCgroup());
    }

    private static bool HasNoSwapCgroup()
    {
        string? line = File.ReadLines("/proc/self/cgroup").SingleOrDefault(static entry => entry.StartsWith("0::", StringComparison.Ordinal));
        if (line is null || line.Length < 4)
        {
            return false;
        }
        string group = line[3..];
        if (!Path.IsPathFullyQualified(group) || Path.GetFullPath(group) != group)
        {
            return false;
        }
        string file = Path.Join("/sys/fs/cgroup", group, "memory.swap.max");
        return File.Exists(file) && File.ReadAllText(file).Trim() == "0";
    }

    public void WriteConfigFile(string name, ReadOnlySpan<byte> contents)
    {
        string path = Path.Combine(_settings.RuntimeRoot, name);
        using var file = new FileStream(path, new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        file.Write(contents);
        LinuxFiles.SetOwner(path, _settings.UserId, _settings.GroupId);
    }

    private string Home => Path.Combine(_settings.RuntimeRoot, "_bootstrap-home");

    public void PrepareHome() => LinuxFiles.CreatePrivateDirectory(Home, _settings.UserId, _settings.GroupId);

    public ProcessStartInfo StartInfo()
    {
        var start = new ProcessStartInfo
        {
            FileName = DropPrivileges,
            WorkingDirectory = _settings.RuntimeRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        string[] arguments =
        [
            "--reuid=" + _settings.UserId.ToString(CultureInfo.InvariantCulture),
            "--regid=" + _settings.GroupId.ToString(CultureInfo.InvariantCulture),
            "--clear-groups", "--no-new-privs", "--bounding-set=-all", "--inh-caps=-all", "--ambient-caps=-all",
            "--", _listener, "run", "--once",
        ];
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Clear();
        start.Environment.Add("PATH", "/usr/local/bin:/usr/bin:/bin");
        start.Environment.Add("HOME", Home);
        start.Environment.Add("LANG", "C.UTF-8");
        start.Environment.Add("DOTNET_EnableDiagnostics", "0");
        start.Environment.Add("AWS_EC2_METADATA_DISABLED", "true");
        string userRuntime = "/run/user/" + _settings.UserId.ToString(CultureInfo.InvariantCulture);
        start.Environment.Add("XDG_RUNTIME_DIR", userRuntime);
        start.Environment.Add("DOCKER_HOST", "unix://" + userRuntime + "/docker.sock");
        return start;
    }

    public void RemoveConfigFiles()
    {
        foreach (string name in new[] { ".runner", ".runner_migrated", ".credentials", ".credentials_migrated", ".credentials_rsaparams" })
        {
            File.Delete(Path.Combine(_settings.RuntimeRoot, name));
        }
    }

    private static IOException Unverified() => new("The running EC2 image has no matching runner isolation proof.");
}

internal sealed record LinuxRunnerSettings(
    string RuntimeRoot, string BoundaryProof, string ListenerSha256, uint UserId, uint GroupId);

internal sealed record LinuxRunnerBoundaryProof(
    int ProtocolVersion, string BootId, string RuntimeRoot, string ListenerSha256, uint UserId, uint GroupId,
    bool ProcessIsolationVerified, bool Ipv4MetadataDenied, bool Ipv6MetadataDenied, bool DockerBoundaryVerified);
