using System.Reflection;
using System.Runtime.Versioning;
using ScaleSet.Client;
using ScaleSet.Listener;
using Xunit;

namespace Knightfall.ScaleSet.Aws.SdkContract.Tests;

public sealed class ReleasedSdkBuildTests
{
    [Theory]
    [InlineData(typeof(ScaleSetClient))]
    [InlineData(typeof(ScaleSetListenerPump))]
    public void ReleasedAssemblyTargetsNet10(Type sdkType)
    {
        var framework = sdkType.Assembly.GetCustomAttribute<TargetFrameworkAttribute>();

        Assert.NotNull(framework);
        Assert.Equal(".NETCoreApp,Version=v10.0", framework.FrameworkName);
    }
}
