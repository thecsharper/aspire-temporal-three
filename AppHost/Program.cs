using AzureKeyVaultEmulator.Aspire.Client;
using AzureKeyVaultEmulator.Aspire.Hosting;
using InfinityFlow.Aspire.Temporal;
using Microsoft.Extensions.Hosting;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var temporal = await builder.AddTemporalServerContainer("temporal", b => b
	.WithPort(7233)
	.WithHttpPort(7234)
	.WithMetricsPort(7235)
	.WithUiPort(8233)
	.WithLogLevel(LogLevel.Info)
);
temporal.PublishAsConnectionString();

var cache = builder.AddRedis("cache").WithRedisCommander();
cache.PublishAsConnectionString();

// Key Vault emulator for storing encryption keys
var keyVault = builder
	.AddAzureKeyVault("keyvault")
	.RunAsEmulator();
keyVault.PublishAsConnectionString();

var vaultUri = keyVault.Resource.VaultUri.Value;
builder.Services.AddAzureKeyVaultEmulator(vaultUri, true, true, false);

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