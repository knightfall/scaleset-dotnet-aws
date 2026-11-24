using Knightfall.ScaleSet.Aws.RunnerBootstrap.Ec2;
using Knightfall.ScaleSet.Aws.RunnerBootstrap.Fargate;

if (OperatingSystem.IsLinux() && args is [var mode, var configuration])
{
    if (mode == "ec2")
    {
        return await Ec2BootstrapCommand.RunAsync(configuration);
    }
    if (mode == "fargate")
    {
        return await FargateBootstrapCommand.RunAsync(configuration);
    }
}

Console.Error.WriteLine("Run RunnerBootstrap on Linux with ec2 or fargate and an absolute settings path.");
return 2;
