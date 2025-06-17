# aspire-temporal-three

Welcome to the **next step** in combining [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) and [Temporal](https://temporal.io/).

In our previous repo — [aspire-temporal-one](https://github.com/rebeccapowell/aspire-temporal-one) — we explored a minimal integration: run a Temporal workflow alongside a .NET Aspire app using the [`InfinityFlow.Aspire.Temporal`](https://www.nuget.org/packages/InfinityFlow.Aspire.Temporal) package.

This new repository, **aspire-temporal-three**, shows how to go further: secure your workflow data with encryption, manage rotating keys, and run everything — including a Key Vault emulator and Redis — entirely within Aspire. No cloud services. No infrastructure. Just code.

---

## Expanding our application

The application has been expanded:

- **AppHost** – boots the entire Aspire application, including Temporal, Key Vault Emulator, Redis, and orchestrates dependencies.
- **Api** – REST API that lets users start, signal, query workflows, test codecs, and trigger key rotations.
- **Worker** – executes the Temporal workflows, configured with encrypted payload codecs.
- **Workflows** – contains shared workflow and activity definitions, including encryption logic.
- **KeyVaultSeeder** - seeds initial encryption key(s) into Key Vault Emulator and syncs Redis with available key metadata.

A trimmed version of `AppHost/Program.cs` illustrates the basics of how this started:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var temporal = await builder.AddTemporalServerContainer("temporal", b => b
    .WithPort(7233)
    .WithHttpPort(7234)
    .WithMetricsPort(7235)
    .WithUiPort(8233)
    .WithLogLevel(LogLevel.Info));

temporal.PublishAsConnectionString();

builder.AddProject<Api>("api")
    .WithReference(temporal);
builder.AddProject<Worker>("worker")
    .WithReference(temporal);

builder.Build().Run();
```

At this point running `dotnet run --project AppHost` launched Temporal and we could POST to `/start/{message}` to kick off a workflow. The worker used `Temporalio.Extensions.Hosting` to register the `SimpleWorkflow` and its activities.

## Dependency Overview

Several NuGet packages have been added to make this sample tick:

- `InfinityFlow.Aspire.Temporal` spins up a Temporal dev server via Aspire so there is no separate install.
- `Temporalio` and the `Temporalio.Extensions.*` packages provide the .NET SDK and OpenTelemetry wiring.
- `Aspire.StackExchange.Redis` configures Redis and exposes a connection string for dependent projects.
- `AzureKeyVaultEmulator.Client` plus `AzureKeyVaultEmulator.Aspire.Hosting` stand up a local Key Vault.

These pieces let us run Temporal, Redis and the emulator entirely from the Aspire host, keeping setup simple.

---

## Encrypted Payloads via Key Vault

Next we tackled secure payload storage. Instead of persisting plaintext workflow data, we opted to encrypt everything using keys stored in an [Azure Key Vault Emulator](https://github.com/Azure/azure-key-vault-emulator). Two NuGet packages proved invaluable here:

- [`AzureKeyVaultEmulator.Aspire.Hosting`](https://www.nuget.org/packages/AzureKeyVaultEmulator.Aspire.Hosting)
- [`AzureKeyVaultEmulator.Client`](https://www.nuget.org/packages/AzureKeyVaultEmulator.Client)

We added the emulator as a service in `AppHost` along with a small seeder project that provisions a key when the app starts locally. The seeder runs as a one-shot container and exits after inserting the key. It also records the available key IDs in Redis so the apps don't fetch everything from Key Vault each time. Redis acts as a simple distributed cache that both the API and Worker can share. Redis itself is configured via the hosting API:

```csharp
IResourceBuilder<ProjectResource>? seeder = null;
if (!builder.ExecutionContext.IsPublishMode)
    seeder = builder.AddProject<KeyVaultSeeder>("keyvaultseeder")
        .WithReference(keyVault)
        .WithReference(cache)
        .WithArgs("seed");

var api = builder.AddProject<Api>("api")
    .WithReference(temporal)
    .WithReference(keyVault)
    .WithReference(cache);
var worker = builder.AddProject<Worker>("worker")
    .WithReference(temporal)
    .WithReference(keyVault)
    .WithReference(cache);

if (seeder is not null)
{
    api.WaitForCompletion(seeder);
    worker.WaitForCompletion(seeder);
}

var app = builder.Build();

await app.StartAsync();
await app.WaitForShutdownAsync();
```

With a key available we implemented `KeyVaultKeyProvider` and `KeyVaultEncryptionCodec` under `Workflows/Encryption` to load keys and wrap Temporal payloads. Here is the heart of the codec:

```csharp
public async Task<IReadOnlyCollection<Payload>> EncodeAsync(IReadOnlyCollection<Payload> payloads)
{
    var keyId = _provider.ActiveKeyId;
    var key = await _provider.GetActiveKeyAsync();
    var keyIdUtf8 = ByteString.CopyFromUtf8(keyId);
    return payloads.Select(p => new Payload
    {
        Metadata =
        {
            ["encoding"] = ByteString.CopyFromUtf8("binary/encrypted"),
            ["encryption-key-id"] = keyIdUtf8
        },
        Data = ByteString.CopyFrom(Encrypt(p.ToByteArray(), key))
    }).ToList();
}
```

Both the API and Worker projects configure their `DataConverter` to use this codec so that workflow inputs, results and signals are automatically encrypted and decrypted. The `KeyVaultKeyProvider` reads the list of key identifiers from Redis and fetches secret contents from Key Vault on demand. It checks Redis for the newest timestamp on each access so that the active key automatically updates after a rotation.

To demonstrate key rotation while the application runs we added a simple `KeyRollWorkflow` and exposed a `/keys/roll` endpoint in the API. Hitting this endpoint launches the workflow which generates a fresh secret in the Key Vault emulator.

---

## Codec Utilities

To help test the new codec we introduced two small endpoints. They simply accept Temporal payloads in JSON form and run them through the codec:

```csharp
public static class CodecEndpoints
{
    public static void MapCodecEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/encode", EncodeAsync).WithName("EncodePayloads");
        app.MapPost("/decode", DecodeAsync).WithName("DecodePayloads");
    }

    private static Task<IResult> EncodeAsync(HttpContext ctx, KeyVaultEncryptionCodec codec)
        => ApplyAsync(ctx, codec.EncodeAsync);

    private static Task<IResult> DecodeAsync(HttpContext ctx, KeyVaultEncryptionCodec codec)
        => ApplyAsync(ctx, codec.DecodeAsync);
}
```

These endpoints proved handy during development to verify that our codec works independently of a running workflow.

## Worker – executing the encrypted workflow

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults(
    metrics =>
    {
        metrics.AddMeter("WorkflowMetrics");
        metrics.AddMeter("Temporal.Client");
    },
    tracing =>
    {
        tracing
            .AddSource(TracingInterceptor.ClientSource.Name)
            .AddSource(TracingInterceptor.WorkflowsSource.Name)
            .AddSource(TracingInterceptor.ActivitiesSource.Name);
    });

// Configure Temporal runtime with OTEL metric support
var temporalMeter = new Meter("Temporal.Client");
var runtime = new TemporalRuntime(new TemporalRuntimeOptions
{
    Telemetry = new TelemetryOptions
    {
        Metrics = new MetricsOptions { CustomMetricMeter = new CustomMetricMeter(temporalMeter) }
    }
});

var vaultUri = builder.Configuration.GetConnectionString("keyvault") ?? string.Empty;
builder.Services.AddAzureKeyVaultEmulator(vaultUri, true, true);
builder.AddRedisClient("cache");
builder.Services.AddSingleton<KeyVaultKeyProvider>();
builder.Services.AddSingleton<KeyVaultEncryptionCodec>();

builder.Services
    .AddTemporalClient(builder.Configuration.GetConnectionString("temporal"), Constants.Namespace)
    .Configure(options =>
    {
        options.Interceptors = new[] { new TracingInterceptor() };
        options.Runtime = runtime;
    })
    .Configure<IServiceProvider>((options, sp) =>
    {
        options.DataConverter = DataConverter.Default with
        {
            PayloadCodec = sp.GetRequiredService<KeyVaultEncryptionCodec>()
        };
    });

builder.Services
    .AddHostedTemporalWorker(Constants.TaskQueue)
    .AddWorkflow<SimpleWorkflow>()
    .AddWorkflow<KeyRollWorkflow>()
    .AddScopedActivities<Activities>();

var workflowMeter = new Meter("WorkflowMetrics");
builder.Services.AddSingleton(workflowMeter);
builder.Services.AddSingleton<WorkflowMetrics>();

var host = builder.Build();
host.Run();
```

Services can depend on `IConnectionMultiplexer` which is automatically provided when `AddRedisClient` is used:

```csharp
public class ExampleService(IConnectionMultiplexer connectionMux)
{
    // Use connectionMux to access Redis...
}
```

## Example Workflow

```csharp
[Workflow]
public class SimpleWorkflow
{
    private bool _continueWorkflow;

    [WorkflowSignal]
    public Task Continue()
    {
        _continueWorkflow = true;
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public async Task<string> RunAsync(string input)
    {
        Workflow.Logger.LogInformation("Workflow started with input: {input}", input);

        var result = await Workflow.ExecuteActivityAsync<Activities, string>(
            a => a.SimulateWork(input),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(120) });

        Workflow.Logger.LogInformation("Waiting for continue signal...");
        await Workflow.WaitConditionAsync(() => _continueWorkflow);

        var final = await Workflow.ExecuteActivityAsync<Activities, string>(
            a => a.FinalizeWork(result),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(120) });

        Workflow.Logger.LogInformation("Workflow completed.");
        return final;
    }
}
```
## Rolling keys

We want to test whether you can version keys and switch to a new active key, whilst still supporting older keys that need to stay around for old active workflows that were encrypted with older keys.

So idea - why not use Temporal for this task? There's no rule that Temporal should only be used for business related code. In fact is perfect for TechOps, DevOps and DataOps as well. Admittedly you wouldn't want to have an unsecured endpoint for this, but for this demo it's ok.

```csharp
[Workflow]
public class KeyRollWorkflow
{
	[WorkflowRun]
	public async Task<string> RunAsync()
	{
		return await Workflow.ExecuteActivityAsync<Activities, string>(
			a => a.RollKey(),
			new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
	}
}

[Activity]
 public async Task<string> RollKey()
 {
     var id = $"ns-{Constants.Namespace}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
     var secret = new KeyVaultSecret($"temporal-{id}", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
     secret.Properties.Tags["namespace"] = Constants.Namespace;
     await _secrets.SetSecretAsync(secret, ActivityExecutionContext.Current.CancellationToken);
     await UpdateCacheAsync(ActivityExecutionContext.Current.CancellationToken);
     return id;
 }

 private async Task UpdateCacheAsync(CancellationToken ct)
 {
     var entries = new List<SortedSetEntry>();
     await foreach (var prop in _secrets.GetPropertiesOfSecretsAsync(ct))
     {
         if (!prop.Tags.TryGetValue("namespace", out var ns) || ns != Constants.Namespace)
             continue;

         var id = prop.Name.StartsWith(SecretPrefix) ? prop.Name[SecretPrefix.Length..] : prop.Name;
         var updated = prop.UpdatedOn ?? prop.CreatedOn ?? DateTimeOffset.UtcNow;
         entries.Add(new SortedSetEntry(id, updated.ToUnixTimeMilliseconds()));
     }

     await _redis.KeyDeleteAsync(CacheKey);
     if (entries.Count > 0)
         await _redis.SortedSetAddAsync(CacheKey, entries.ToArray());
 }
```

With this in place we are able to leverage Temporal to create a new version of the key and inject it into the Redis cache. Putting this in a Redis cache is important because the API and the Workers need to be in sync. Using a local memory cache would not offer that, and Redis is the perfect solution for this task.

---

## Observability and Testing

From day one we instrumented everything with OpenTelemetry. Counters and histograms defined in `WorkflowMetrics` are registered in both the API and Worker, and traces flow through the Temporal SDK via [`Temporalio.Extensions.DiagnosticSource`](https://www.nuget.org/packages/Temporalio.Extensions.DiagnosticSource).

Writing tests for the encryption layer was another milestone. The `Tests` project spins up the Key Vault emulator and validates round‑trip encryption with xUnit:

```csharp
[Fact]
public async Task EncodeDecode_RoundTripsPayload()
{
    var hostBuilder = new HostBuilder();
    
    hostBuilder.ConfigureServices(s =>
        s.AddAzureKeyVaultEmulator("https://localhost:4997", secrets: true, keys: true, certificates: false));
    using var host = hostBuilder.Build();
    
    var client = host.Services.GetRequiredService<SecretClient>();
    var id = $"ns-default-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
    await client.SetSecretAsync(new KeyVaultSecret($"temporal-{id}", Convert.ToBase64String(CreateKey()))
    { Properties = { Tags = { ["namespace"] = Constants.Namespace } } });
    
    var redis = ConnectionMultiplexer.Connect("localhost:6379");
    var provider = new KeyVaultKeyProvider(client, redis, new ConfigurationBuilder().Build());
    var codec = new KeyVaultEncryptionCodec(provider);

    var payload = new Payload { Data = ByteString.CopyFromUtf8("hello") };
    var encoded = await codec.EncodeAsync(new[] { payload });
    var decoded = await codec.DecodeAsync(encoded);

    decoded.Single().Data.ToStringUtf8().ShouldBe("hello");
}
```

---

## Running the Demo

1. Launch the Aspire app:
   ```bash
   dotnet run --project AppHost
   ```
   This boots the Temporal dev server, the Key Vault emulator and both the API and Worker projects.
2. Kick off a workflow:
   ```bash
   curl -X POST http://localhost:5228/start/hello
   ```
3. Continue it when you are ready:
   ```bash
   curl -X POST http://localhost:5228/signal/<workflowId>
   ```
4. Fetch the result:
   ```bash
   curl http://localhost:5228/result/<workflowId>
   ```
5. Try the codec utilities:
   ```bash
   curl -X POST http://localhost:5228/encode -H "Content-Type: application/json" -d '{"payloads":[]}'
   ```
6. Try rolling over the keys:
   ```bash
   curl -X 'POST' http://localhost:5228/keys/roll -H 'accept: application/json' -d ''
   ```

The Aspire dashboard is available at `http://localhost:18888` and the Temporal Web UI at `http://localhost:8233`.

## Continuous Integration

The `.github/workflows/ci.yml` file defines a small pipeline. Every pull request restores dependencies, builds, and executes the test suite while collecting coverage. Artifacts like the test results and coverage reports are uploaded for review.

---

## Side Notes

I've slightly misused Keyvault here by querying it for all keys with the same namespace tag, so it's a hacky workaround.

```csharp
private async Task UpdateCacheFromVaultAsync(CancellationToken ct)
	{
		var list = new List<SortedSetEntry>();
		await foreach (var prop in _client.GetPropertiesOfSecretsAsync(ct))
		{
			if (!prop.Tags.TryGetValue("namespace", out var ns) || ns != Constants.Namespace)
				continue;

			var id = prop.Name.StartsWith(SecretPrefix) ? prop.Name[SecretPrefix.Length..] : prop.Name;
			var updated = prop.UpdatedOn ?? prop.CreatedOn ?? ParseTimestamp(id);
			list.Add(new SortedSetEntry(id, updated.ToUnixTimeMilliseconds()));
		}

		if (list.Count > 0)
		{
			await _redis.KeyDeleteAsync(CacheKey);
			await _redis.SortedSetAddAsync(CacheKey, list.ToArray());
		}
	}
```
In a full production environment this would be better solved with an environment specific Azure Application Configuration Service, but there is no emulator for that to date, and I'm trying to keep everything local. If you wanted to, it would look something like this:

```csharp
using System;
using System.Threading.Tasks;
using Azure;
using Azure.Data.AppConfiguration;
using Azure.Identity;

class Program
{
    static async Task Main()
    {
        // Your App Configuration endpoint
        var endpoint = new Uri("https://<your-appconfig-name>.azconfig.io");

        // Authenticate using federated identity
        var client = new ConfigurationClient(endpoint, new DefaultAzureCredential());

        // Define label to filter by
        string label = "NS-SIMPLE-WF";

        // Set up selector to filter keys by label
        var selector = new SettingSelector
        {
            KeyFilter = "*",
            LabelFilter = label
        };

        // Enumerate all matching key-values
        await foreach (ConfigurationSetting setting in client.GetConfigurationSettingsAsync(selector))
        {
            Console.WriteLine($"Key: {setting.Key}");
            Console.WriteLine($"Value: {setting.Value}");
            Console.WriteLine($"Label: {setting.Label}");
            Console.WriteLine($"ContentType: {setting.ContentType}");
            Console.WriteLine();
        }
    }
}
```

## Wrapping Up

This evolved repository mirrors production features: encrypted payloads, repeatable tests and full telemetry. Along the way we leaned on packages like `InfinityFlow.Aspire.Temporal` to manage the Temporal server and the Key Vault emulator to avoid external dependencies.

Enjoy experimenting with .NET Aspire and Temporal!

