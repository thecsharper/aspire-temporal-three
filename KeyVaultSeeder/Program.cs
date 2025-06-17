using System.Security.Cryptography;
using Azure.Security.KeyVault.Secrets;
using AzureKeyVaultEmulator.Aspire.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Workflows;

var builder = Host.CreateApplicationBuilder(args);

var vaultUri = builder.Configuration.GetConnectionString("keyvault") ?? string.Empty;
builder.Services.AddAzureKeyVaultEmulator(vaultUri, true, true, false);
builder.AddRedisClient("cache");

using var host = builder.Build();
var client = host.Services.GetRequiredService<SecretClient>();
var redis = host.Services.GetRequiredService<IConnectionMultiplexer>();
var command = args.FirstOrDefault() ?? "seed";

if (string.Equals(command, "seed", StringComparison.OrdinalIgnoreCase))
	await SeedAsync(client, redis);
else if (string.Equals(command, "roll", StringComparison.OrdinalIgnoreCase))
	await RollAsync(client, redis);
else
	Console.WriteLine("Unknown command. Use 'seed' or 'roll'.");

static async Task SeedAsync(SecretClient client, IConnectionMultiplexer redis)
{
	if (!await AnyKeyExistsAsync(client))
	{
		var id = CreateKeyId();
		await SetKeyAsync(client, id);
		Console.WriteLine($"Seeded {id}");
	}
	else
	{
		Console.WriteLine("Key vault already seeded");
	}

	await UpdateCacheAsync(client, redis);
}

static async Task RollAsync(SecretClient client, IConnectionMultiplexer redis)
{
	var id = CreateKeyId();
	await SetKeyAsync(client, id);
	Console.WriteLine($"Rolled new key {id}");
	await UpdateCacheAsync(client, redis);
}

static async Task<bool> AnyKeyExistsAsync(SecretClient client)
{
	await foreach (var prop in client.GetPropertiesOfSecretsAsync())
		if (prop.Tags.TryGetValue("namespace", out var ns) && ns == Constants.Namespace)
			return true;
	return false;
}

static string CreateKeyId()
{
	return $"ns-{Constants.Namespace}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
}

static async Task SetKeyAsync(SecretClient client, string id)
{
	var secret = new KeyVaultSecret($"temporal-{id}", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
	secret.Properties.Tags["namespace"] = Constants.Namespace;
	await client.SetSecretAsync(secret);
}

static async Task UpdateCacheAsync(SecretClient client, IConnectionMultiplexer redis)
{
	var db = redis.GetDatabase();
	var entries = new List<SortedSetEntry>();
	await foreach (var prop in client.GetPropertiesOfSecretsAsync())
	{
		if (!prop.Tags.TryGetValue("namespace", out var ns) || ns != Constants.Namespace)
			continue;

		var id = prop.Name.StartsWith("temporal-") ? prop.Name["temporal-".Length..] : prop.Name;
		var updated = prop.UpdatedOn ?? prop.CreatedOn ?? DateTimeOffset.UtcNow;
		entries.Add(new SortedSetEntry(id, updated.ToUnixTimeMilliseconds()));
	}

	await db.KeyDeleteAsync($"temporal:{Constants.Namespace}:keys");
	if (entries.Count > 0)
		await db.SortedSetAddAsync($"temporal:{Constants.Namespace}:keys", entries.ToArray());
}