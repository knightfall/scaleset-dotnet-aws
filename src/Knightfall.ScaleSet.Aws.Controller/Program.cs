using Knightfall.ScaleSet.Aws.Controller.Commands;

return args is ["preflight", "retire", ..]
    ? await RetirementPreflightCommand.RunAsync(args, Console.Out, Console.Error)
    : await DryRunCommand.RunAsync(args, Console.Out, Console.Error);
