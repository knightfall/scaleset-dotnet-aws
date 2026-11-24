using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap.Linux;

[SupportedOSPlatform("linux")]
internal sealed class Ec2SpentMarker(string directory, string resourceArn)
{
    internal static void VerifyDirectory(string directory)
    {
        if (!LinuxFiles.IsPrivateStateDirectory(directory) || directory.Any(char.IsWhiteSpace) ||
            !LinuxMounts.IsPersistentFileSystem(directory, File.ReadAllLines("/proc/self/mountinfo")))
        {
            throw new IOException("The EC2 spent marker requires a private, persistent state directory.");
        }
    }

    internal bool TryReserve()
    {
        string id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(resourceArn)));
        string path = Path.Combine(directory, id + ".spent");
        try
        {
            using var marker = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                Share = FileShare.None,
            });
            marker.Write("v1\n"u8);
            marker.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
        LinuxFiles.SyncDirectory(directory);
        return true;
    }
}
