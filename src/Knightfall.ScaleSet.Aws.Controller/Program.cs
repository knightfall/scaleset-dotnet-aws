using Knightfall.ScaleSet.Aws.Controller.Commands;
using Knightfall.ScaleSet.Aws.Controller.Configuration;
using Knightfall.ScaleSet.Aws.Controller.Runtime;

return args switch
{
    ["config", "verify", ..] => await ConfigVerifyCommand.RunAsync(args, Console.Out, Console.Error),
    ["telemetry-smoke", ..] => await TelemetrySmokeCommand.RunAsync(args, Console.Out, Console.Error),
    ["run", ..] => await RunCommand.RunAsync(args, Console.Out, Console.Error, context => ActiveControllerRuntime.RunAsync(context)),
    ["healthcheck", ..] => await HealthcheckCommand.RunAsync(args, Console.Out, Console.Error),
    ["preflight", "clock", ..] => await ClockPreflightCommand.RunAsync(args, Console.Out, Console.Error),
    ["preflight", "table", ..] => await TablePreflightCommand.RunAsync(args, Console.Out, Console.Error),
    ["preflight", "retire", ..] => await RetirementPreflightCommand.RunAsync(args, Console.Out, Console.Error),
    _ => await DryRunCommand.RunAsync(args, Console.Out, Console.Error, ReadOnlyRunnerInventory.ReadAsync),
};
