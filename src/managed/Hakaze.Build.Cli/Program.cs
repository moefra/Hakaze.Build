using Hakaze.Build.Abstractions;
using Microsoft.Extensions.Hosting;

namespace Hakaze.Build.Cli;

public class Program
{
    public static async Task Main(string[] args, params TargetId[] defaultTargets)
    {
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
