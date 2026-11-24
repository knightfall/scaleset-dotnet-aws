using Knightfall.ScaleSet.Aws.Controller.Commands;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Core.Tests;

public sealed class RetirementPreflightCommandTests
{
    [Fact]
    public async Task RetirementRefusesAnUnrecognizedFixtureFlag()
    {
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        var result = await RetirementPreflightCommand.RunAsync(
            ["preflight", "retire", "--fixture", "verified", "--current-generation", "1", "--current-config", "current.json",
                "--config", "candidate.json", "--sha256", new string('1', 64), "--generation", "2"],
            output, error, TestContext.Current.CancellationToken);

        Assert.Equal(2, result);
        Assert.Empty(output.ToString());
        Assert.Contains("--current-sha256", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetirementRejectsADeployedArtifactThatDoesNotMatchItsDigest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("scaleset-retirement-");
        try
        {
            string file = Path.Combine(directory.FullName, "current.json");
            await File.WriteAllTextAsync(file, "{}", cancellationToken);
            await using var output = new StringWriter();
            await using var error = new StringWriter();
            var result = await RetirementPreflightCommand.RunAsync(
                ["preflight", "retire", "--current-config", file, "--current-sha256", new string('0', 64), "--current-generation", "1",
                    "--config", file, "--sha256", new string('1', 64), "--generation", "2"],
                output, error, cancellationToken);

            Assert.Equal(1, result);
            Assert.Empty(output.ToString());
            Assert.Contains("could not verify", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
