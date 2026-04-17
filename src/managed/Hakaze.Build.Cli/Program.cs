using Microsoft.Extensions.Hosting;

namespace Hakaze.Build.Cli;

class Program
{
    static async Task Main(string[] args)
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
