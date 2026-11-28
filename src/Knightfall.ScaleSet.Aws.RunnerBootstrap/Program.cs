using Knightfall.ScaleSet.Aws.RunnerBootstrap;
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

return await BootstrapLog.ReportUsageAsync(Console.Error, TimeProvider.System);
