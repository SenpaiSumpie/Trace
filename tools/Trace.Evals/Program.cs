using Trace.Evals;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    // Stop the run in an orderly way: the result file is still written and the database dropped.
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

return await EvalCommand.RunAsync(args, Console.Out, Console.Error, cancellationToken: cancellation.Token);
