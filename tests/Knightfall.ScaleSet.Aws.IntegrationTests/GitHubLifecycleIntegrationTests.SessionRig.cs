using System.Text.Json;
using Amazon.SecretsManager;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.GitHub;
using Knightfall.ScaleSet.Aws.Lifecycle.Tests;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

public sealed partial class GitHubLifecycleIntegrationTests
{
    private const string SuccessorTask = "arn:aws:ecs:us-east-1:000000000000:task/controller/00000000000000000000000000000002";

    private sealed class SessionRig(
        MiniStackTable table, AmazonSecretsManagerClient secrets, GitHubProtocolHost host, HttpClient http,
        GitHubSimulator simulator, GitHubSecretLoader loader, GitHubScaleSetClientFactory factory,
        RuntimeConfiguration configuration, IntegrationClock clock, DynamoDbLifecycleStore store,
        ControllerLease lease, GitHubScaleSetBinding binding) : IAsyncDisposable
    {
        internal GitHubSimulator Simulator { get; } = simulator;
        internal DynamoDbLifecycleStore Store { get; } = store;
        internal ControllerLease Lease { get; } = lease;
        internal LifecycleProfile Profile { get; } = new(lease.Deployment, ProfileId);
        internal RuntimeConfiguration Configuration { get; } = configuration;
        internal IntegrationClock Clock { get; } = clock;
        internal MiniStackTable Table { get; } = table;

        internal static async Task<SessionRig> CreateAsync(CancellationToken token)
        {
            var table = await MiniStackTable.CreateAsync(token);
            var secrets = CreateSecretsClient();
            GitHubProtocolHost? host = null;
            HttpClient? http = null;
            try
            {
                var secret = await StoreSecretAsync(secrets, app: false, token);
                var configuration = await LoadConfigurationAsync(secret.ARN, "https://github.com/enterprises/engineering", app: false, token);
                var clock = new IntegrationClock();
                var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
                var lease = await StartOwnerAsync(store, configuration, clock, token);
                var simulator = new GitHubSimulator(Group);
                host = await GitHubProtocolHost.StartAsync(simulator, token);
                http = host.CreateClient();
                var factory = new GitHubScaleSetClientFactory(http, timeProvider: clock);
                var loader = new GitHubSecretLoader(secrets);
                using var connection = await GitHubLifecycleIntegrationTests.ConnectAsync(configuration, loader, factory, token);
                var binding = await RegisterAsync(store, clock, configuration, lease, connection, token);
                return new SessionRig(table, secrets, host, http, simulator, loader, factory, configuration, clock, store, lease, binding);
            }
            catch
            {
                http?.Dispose();
                if (host is not null)
                {
                    await host.DisposeAsync();
                }
                secrets.Dispose();
                table.Dispose();
                throw;
            }
        }

        internal Task<GitHubScaleSetConnection> ConnectAsync(CancellationToken token) =>
            GitHubLifecycleIntegrationTests.ConnectAsync(Configuration, loader, factory, token);

        internal async Task<GitHubListenerLane> OpenLaneAsync(ControllerLease owner, CancellationToken token, int capacity = 2)
        {
            var connection = await ConnectAsync(token);
            return new GitHubListenerSupervisor(Store, Clock, ListenerTiming)
                .Register(Definition(Configuration, owner, Clock, binding, connection, capacity));
        }

        internal Task<ControllerLease> TakeOverAsync(CancellationToken token)
        {
            Clock.Now = Lease.ValidUntil.AddSeconds(2);
            return StartOwnerAsync(Store, Configuration, Clock, token, SuccessorTask);
        }

        internal async Task<int> ReadHeldAsync(CancellationToken token)
        {
            var row = await Store.ReadRecordAsync(LifecycleKey.Admission(Profile), token);
            if (row is null)
            {
                return 0;
            }
            using var payload = JsonDocument.Parse(row.Payload ?? throw new InvalidDataException("The admission row has no payload."));
            return payload.RootElement.GetProperty("heldCount").GetInt32();
        }

        public async ValueTask DisposeAsync()
        {
            http.Dispose();
            await host.DisposeAsync();
            secrets.Dispose();
            Table.Dispose();
        }
    }
}
