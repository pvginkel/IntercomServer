using IntercomServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Console().CreateLogger();

var builder = new HostBuilder().ConfigureServices(
    (context, services) =>
    {
        services.AddHostedService<Service>();
    }
);

await builder.RunConsoleAsync();
