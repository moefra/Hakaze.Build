using Hakaze.Build.Abstractions;
using Hakaze.Build.Abstractions.Generator;
using Microsoft.Extensions.Hosting;

namespace Hakaze.Build.Cli;

public class Program
{
    public static void Help<T>()
    {

    }

    public static async Task Run<T>(string[] args, params TargetId[] defaultTargets)
        where T : IExportOptions<T>, IExportTargets
    {
        if (args.Any(arg => arg is "--help" or "-h" or "/?" or "help"))
        {

        }

        var builder = Host.CreateEmptyApplicationBuilder(null);

        var host = builder.Build();

        var cancellationTokenSource = new CancellationTokenSource();

        Console.CancelKeyPress += (sender, eventArgs) =>
        {
            cancellationTokenSource.Cancel();
        };

        var token = cancellationTokenSource.Token;

        await host.StartAsync(token);
    }
}
