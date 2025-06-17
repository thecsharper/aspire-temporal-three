using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Azure.Security.KeyVault.Secrets;
using AzureKeyVaultEmulator.Aspire.Client;
using Google.Protobuf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StackExchange.Redis;
using Temporalio.Api.Common.V1;
using Workflows;
using Workflows.Encryption;
using Xunit;

namespace Tests;

public class KeyVaultEncryptionCodecTests
{
	private static byte[] CreateKey()
	{
		var key = new byte[32];
		RandomNumberGenerator.Fill(key);
		return key;
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
	public async Task EncodeDecode_RoundTripsPayload()
	{
		if (Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS") is null)
			return;
		var client = CreateClient();
		await ClearAsync(client);
		var id = $"ns-{Constants.Namespace}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
		var key = CreateKey();
		await client.SetSecretAsync(new KeyVaultSecret($"temporal-{id}", Convert.ToBase64String(key))
			{ Properties = { Tags = { ["namespace"] = Constants.Namespace } } });
		var redis = CreateRedis();
		var provider = new KeyVaultKeyProvider(client, redis, new ConfigurationBuilder().Build());
		var codec = new KeyVaultEncryptionCodec(provider);
		var payload = new Payload { Data = ByteString.CopyFromUtf8("hello") };

		var encoded = await codec.EncodeAsync(new[] { payload });
		encoded.Count.ShouldBe(1);
		encoded.Single().Metadata["encryption-key-id"].ToStringUtf8().ShouldBe(id);

		var decoded = await codec.DecodeAsync(encoded);
		decoded.Single().Data.ToStringUtf8().ShouldBe("hello");
	}

	[Fact]
	public async Task Decode_ThrowsWithoutKeyId()
	{
		if (Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS") is null)
			return;
		var client = CreateClient();
		await ClearAsync(client);
		var id = $"ns-{Constants.Namespace}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
		var key = CreateKey();
		await client.SetSecretAsync(new KeyVaultSecret($"temporal-{id}", Convert.ToBase64String(key))
			{ Properties = { Tags = { ["namespace"] = Constants.Namespace } } });
		var redis = CreateRedis();
		var provider = new KeyVaultKeyProvider(client, redis, new ConfigurationBuilder().Build());
		var codec = new KeyVaultEncryptionCodec(provider);

		var payload = new Payload { Metadata = { ["encoding"] = ByteString.CopyFromUtf8("binary/encrypted") }, Data = ByteString.CopyFromUtf8("hi") };
		await Should.ThrowAsync<InvalidOperationException>(() => codec.DecodeAsync(new[] { payload }));
	}
}