using Knightfall.ScaleSet.Aws.RunnerBootstrap.Ec2;

if (OperatingSystem.IsLinux() && args is ["ec2", var configuration])
{
    return await Ec2BootstrapCommand.RunAsync(configuration);
}

Console.Error.WriteLine("Usage on Linux: RunnerBootstrap ec2 /absolute/path/to/bootstrap.json");
return 2;
