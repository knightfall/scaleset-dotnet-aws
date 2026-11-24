using System.Collections.Immutable;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Knightfall.ScaleSet.Aws.Controller.Health;

public sealed record ControllerProfileHealth(string Profile, bool Ready);

public sealed record ControllerHealthSnapshot(
    bool Live,
    bool ConfigurationVerified,
    bool OwnershipRequired,
    bool LeaseActive,
    ImmutableArray<ControllerProfileHealth> Profiles)
{
    public bool Ready => Live && ConfigurationVerified && (!OwnershipRequired || LeaseActive) &&
        !Profiles.IsDefault && Profiles.All(static profile => profile.Ready);
}

public static class ControllerHealthServer
{
    public static async Task<WebApplication> StartAsync(
        LoopbackHealthAddress address, Func<ControllerHealthSnapshot> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(read);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(ControllerHealthServer).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        // Health has one explicitly selected listener and no ambient endpoint configuration.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Logging.ClearProviders();
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true")
            .PreferHostingUrls(false).ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 0;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(2);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
            options.Listen(address.Address, address.Port, listener => listener.Protocols = HttpProtocols.Http1);
        });
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(2));
        builder.Services.AddSingleton<IHostLifetime, HealthHostLifetime>();

        var app = builder.Build();
        _ = app.MapGet("/healthz", () => Results.StatusCode(
            read().Live ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable));
        _ = app.MapGet("/readyz", () =>
        {
            var snapshot = read();
            return Results.Json(new
            {
                snapshot.Ready,
                snapshot.ConfigurationVerified,
                snapshot.OwnershipRequired,
                snapshot.LeaseActive,
                Profiles = snapshot.Profiles.IsDefault ? [] : snapshot.Profiles,
            }, statusCode: snapshot.Ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        });
        try
        {
            await app.StartAsync(cancellationToken);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    /// <summary>Leaves process signals and health listener shutdown with the controller.</summary>
    private sealed class HealthHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
