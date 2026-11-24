namespace Knightfall.ScaleSet.Aws.RunnerBootstrap.Linux;

internal static class LinuxMounts
{
    internal static bool IsVolatileRunnerRoot(string root, IEnumerable<string> mountInfo, IReadOnlyList<string> swaps, bool cgroupSwapDisabled = false)
    {
        if (!Path.IsPathFullyQualified(root) || root == "/" || root != Path.GetFullPath(root) || root.EndsWith('/') ||
            swaps.Count == 0 || !swaps[0].StartsWith("Filename", StringComparison.Ordinal))
        {
            return false;
        }
        bool found = false;
        foreach (string line in mountInfo)
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10)
            {
                return false;
            }
            string mountpoint = fields[4];
            if (mountpoint.StartsWith(root + "/", StringComparison.Ordinal))
            {
                return false;
            }
            if (mountpoint != root)
            {
                continue;
            }
            int separator = Array.IndexOf(fields, "-");
            var options = fields[5].Split(',');
            if (found || separator < 6 || separator + 3 >= fields.Length || fields[separator + 1] != "tmpfs" ||
                !options.Contains("rw", StringComparer.Ordinal) || options.Contains("noexec", StringComparer.Ordinal) || !options.Contains("nosuid", StringComparer.Ordinal) ||
                !options.Contains("nodev", StringComparer.Ordinal) ||
                (swaps.Count > 1 && (!cgroupSwapDisabled || !fields[separator + 3].Split(',').Contains("noswap", StringComparer.Ordinal))))
            {
                return false;
            }
            found = true;
        }
        return found;
    }

    internal static bool IsPersistentFileSystem(string directory, IEnumerable<string> mountInfo)
    {
        int deepest = -1;
        string? kind = null;
        foreach (string entry in mountInfo)
        {
            var fields = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int separator = Array.IndexOf(fields, "-");
            if (fields.Length < 10 || separator < 6 || separator + 3 >= fields.Length)
            {
                return false;
            }
            string mount = fields[4];
            if ((mount == directory || mount == "/" || directory.StartsWith(mount + "/", StringComparison.Ordinal)) &&
                mount.Length >= deepest)
            {
                if (mount.Length == deepest)
                {
                    return false;
                }
                deepest = mount.Length;
                kind = fields[separator + 1];
            }
        }
        return kind is "xfs" or "ext4";
    }
}
