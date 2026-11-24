namespace Knightfall.ScaleSet.Aws.Core.Configuration;

public sealed class RuntimeConfigurationException(string code, string path, string message) : Exception(message)
{
    public string Code { get; } = code;
    public string Path { get; } = path;
}
