using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Azure.Security.KeyVault.Secrets;
using AzureKeyVaultEmulator.Aspire.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StackExchange.Redis;
using Workflows;
using Workflows.Encryption;
using Xunit;

namespace Tests;

public class KeyVaultKeyProviderTests
{
	private static IConfiguration BuildConfig()
	{
		return new ConfigurationBuilder().Build();
	}

	private static SecretClient CreateClient()
	{
		var hostBuilder = new HostBuilder();
		hostBuilder.ConfigureServices(s =>
			s.AddAzureKeyVaultEmulator("https://localhost:4997", true, true, false));
		var host = hostBuilder.Build();
		return host.Services.GetRequiredService<SecretClient>();
	}

	private static IConnectionMultiplexer CreateRedis()
	{
		var conn = Environment.GetEnvironmentVariable("REDIS_CONN");
		if (string.IsNullOrEmpty(conn))
			throw new InvalidOperationException("REDIS_CONN not set");
		return ConnectionMultiplexer.Connect(conn);
	}

	private static async Task ClearAsync(SecretClient client)
	{
		await foreach (var prop in client.GetPropertiesOfSecretsAsync())
		{
			await client.StartDeleteSecretAsync(prop.Name);
			await client.PurgeDeletedSecretAsync(prop.Name);
		}
	}

	[Fact]
	public async Task GetActiveKey_ReturnsCachedValue()
	{
		if (Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS") is null)
			return;
		var client = CreateClient();
		await ClearAsync(client);
		var id = $"ns-{Constants.Namespace}-0";
		await client.SetSecretAsync(new KeyVaultSecret($"temporal-{id}", Convert.ToBase64String(new byte[] { 1, 2, 3 }))
			{ Properties = { Tags = { ["namespace"] = Constants.Namespace } } });
		var redis = CreateRedis();
		var provider = new KeyVaultKeyProvider(client, redis, BuildConfig());

		var first = await provider.GetActiveKeyAsync();
		first.ShouldBe(new byte[] { 1, 2, 3 });
		await client.SetSecretAsync(new KeyVaultSecret($"temporal-{id}", Convert.ToBase64String(new byte[] { 9, 9, 9 }))
			{ Properties = { Tags = { ["namespace"] = Constants.Namespace } } });
		var second = await provider.GetActiveKeyAsync();
		second.ShouldBe(first); // cached value
	}

	[Fact]
	public async Task RefreshAsync_ReloadsKeys()
	{
		if (Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS") is null)
			return;
		var client = CreateClient();
		await ClearAsync(client);
		var id = $"ns-{Constants.Namespace}-1";
		await client.SetSecretAsync(new KeyVaultSecret($"temporal-{id}", Convert.ToBase64String(new byte[] { 1 }))
			{ Properties = { Tags = { ["namespace"] = Constants.Namespace } } });
		var redis = CreateRedis();
		var provider = new KeyVaultKeyProvider(client, redis, BuildConfig());
		await provider.GetActiveKeyAsync();
		await client.SetSecretAsync(new KeyVaultSecret($"temporal-{id}", Convert.ToBase64String(new byte[] { 2 }))
			{ Properties = { Tags = { ["namespace"] = Constants.Namespace } } });
		await provider.RefreshAsync();
		var val = await provider.GetActiveKeyAsync();
		val.ShouldBe(new byte[] { 2 });
	}

	[Fact]
	public async Task GetKeyAsync_ThrowsForMissing()
	{
		if (Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS") is null)
			return;
		var client = CreateClient();
		await ClearAsync(client);
		var redis = CreateRedis();
		var provider = new KeyVaultKeyProvider(client, redis, BuildConfig());
		await Should.ThrowAsync<KeyNotFoundException>(() => provider.GetKeyAsync("missing"));
	}
}