using Knightfall.ScaleSet.Aws.Controller.Commands;

return args switch
{
    ["telemetry-smoke", ..] => await TelemetrySmokeCommand.RunAsync(args, Console.Out, Console.Error),
    ["preflight", "retire", ..] => await RetirementPreflightCommand.RunAsync(args, Console.Out, Console.Error),
    _ => await DryRunCommand.RunAsync(args, Console.Out, Console.Error),
};
