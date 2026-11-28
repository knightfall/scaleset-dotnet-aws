using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap.Linux;

[SupportedOSPlatform("linux")]
internal static partial class LinuxFiles
{
    private const ushort DirectoryMode = 0x4000;
    private const ushort RegularMode = 0x8000;
    private const ushort TypeMask = 0xf000;
    private const ushort GroupOtherWrite = 0x0012;

    internal static bool IsRoot() => GetEffectiveUserId() == 0;

    internal static bool HasFargateRunnerIdentity() =>
        HasFargateRunnerIdentity(File.ReadAllLines("/proc/thread-self/status"));

    internal static bool HasFargateRunnerIdentity(string[] status)
    {
        // Uid and Gid include the real, effective, saved and filesystem IDs.
        return ValidIds("Uid:", primary: true) && ValidIds("Gid:", primary: true) && ValidIds("Groups:", primary: false) &&
            EmptyCapabilities("CapInh:") && EmptyCapabilities("CapPrm:") && EmptyCapabilities("CapEff:") &&
            EmptyCapabilities("CapBnd:") && EmptyCapabilities("CapAmb:");

        string? FieldValue(string field) =>
            Array.Find(status, entry => entry.StartsWith(field, StringComparison.Ordinal))?[field.Length..].Trim();

        bool ValidIds(string field, bool primary)
        {
            string? value = FieldValue(field);
            if (value is null) return false;
            string[] ids = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return (!primary || ids.Length == 4) && ids.All(text =>
                int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int id) && id >= 1000);
        }

        bool EmptyCapabilities(string field)
        {
            string? value = FieldValue(field);
            return value is { Length: > 0 } && !value.AsSpan().ContainsAnyExcept('0');
        }
    }

    internal static ulong? GetMountId(string path)
    {
        const int atCurrentDirectory = -100;
        const int noFollow = 0x100;
        const uint mountId = 0x1000;
        return Statx(atCurrentDirectory, path, noFollow, mountId, out var stat) == 0 && (stat.Mask & mountId) != 0
            ? stat.MountId : null;
    }

    internal static void DisableCoreDumps()
    {
        const int coreLimit = 4;
        const int setDumpable = 4;
        if (SetResourceLimit(coreLimit, new LinuxLimit(0, 0)) != 0 || ProcessControl(setDumpable, 0, 0, 0, 0) != 0)
        {
            throw new IOException("The bootstrap process could not disable memory dumps.");
        }
    }

    internal static void ProhibitPrivilegeGain()
    {
        const int noNewPrivileges = 38;
        if (ProcessControl(noNewPrivileges, 1, 0, 0, 0) != 0)
        {
            throw new IOException("The runner could not prohibit privilege gain.");
        }
    }

    internal static bool IsTrustedFile(string path, bool ownerOnly, string? privateRoot = null)
    {
        var file = Read(path);
        return (file.Mode & TypeMask) == RegularMode && file.User == 0 &&
            (file.Mode & (ownerOnly ? 0x003f : GroupOtherWrite)) == 0 &&
            HasTrustedParents(path, privateRoot);
    }

    internal static bool HasTrustedParents(string path, string? privateRoot = null)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path || path.Contains('\n') || path.Contains('\r'))
        {
            return false;
        }
        string? parent = Path.GetDirectoryName(path);
        while (parent is not null)
        {
            var state = Read(parent);
            if ((state.Mode & TypeMask) != DirectoryMode || state.User != 0 ||
                (parent != privateRoot && (state.Mode & GroupOtherWrite) != 0))
            {
                return false;
            }
            parent = Path.GetDirectoryName(parent);
        }
        return true;
    }

    internal static bool IsPrivateRoot(string path, uint group)
    {
        var directory = Read(path);
        return (directory.Mode & TypeMask) == DirectoryMode && directory.User == 0 && directory.Group == group &&
            (directory.Mode & 0x0fff) == 0x03f8 && HasTrustedParents(path);
    }

    internal static bool IsPrivateStateDirectory(string path)
    {
        var directory = Read(path);
        return (directory.Mode & TypeMask) == DirectoryMode && directory.User == 0 &&
            (directory.Mode & 0x003f) == 0 && HasTrustedParents(path);
    }

    internal static void CreatePrivateDirectory(string path, uint user, uint group)
    {
        const uint userOnlyDirectory = 0x1c0;
        if (Mkdir(path, userOnlyDirectory) != 0)
        {
            throw new IOException("The private runner home could not be created.");
        }
        SetOwner(path, user, group);
    }

    internal static void SetOwner(string path, uint user, uint group)
    {
        if (ChangeLinkOwner(path, user, group) != 0)
        {
            throw new IOException("The runner file ownership could not be set.");
        }
    }

    internal static void SyncDirectory(string path)
    {
        const int readOnly = 0;
        int descriptor = Open(path, readOnly);
        if (descriptor < 0)
        {
            throw new IOException("The spent-marker directory could not be opened.");
        }
        try
        {
            if (Fsync(descriptor) != 0)
            {
                throw new IOException("The spent-marker directory could not be flushed.");
            }
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    private static LinuxStatx Read(string path)
    {
        const int atCurrentDirectory = -100;
        const int noFollow = 0x100;
        const uint modeOwnerGroup = 0x1a;
        return Statx(atCurrentDirectory, path, noFollow, modeOwnerGroup, out var stat) == 0 &&
            (stat.Mask & modeOwnerGroup) == modeOwnerGroup ? stat : default;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint User;
        [FieldOffset(24)] public uint Group;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(144)] public ulong MountId;
    }

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int directory, string path, int flags, uint mask, out LinuxStatx stat);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct LinuxLimit(ulong soft, ulong hard)
    {
        private readonly ulong _soft = soft;
        private readonly ulong _hard = hard;
    }

    [LibraryImport("libc", EntryPoint = "setrlimit")]
    private static partial int SetResourceLimit(int resource, in LinuxLimit limits);

    [LibraryImport("libc", EntryPoint = "prctl")]
    private static partial int ProcessControl(int option, nuint first, nuint second, nuint third, nuint fourth);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    [LibraryImport("libc", EntryPoint = "lchown", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ChangeLinkOwner(string path, uint user, uint group);

    [LibraryImport("libc", EntryPoint = "mkdir", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Mkdir(string path, uint mode);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync")]
    private static partial int Fsync(int descriptor);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int descriptor);
}
