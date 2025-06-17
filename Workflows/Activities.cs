using System.Diagnostics;
using System.Security.Cryptography;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Temporalio.Activities;
using Workflows.Instrumentation;

namespace Workflows;

public class Activities
{
	private const string SecretPrefix = "temporal-";
	private static readonly string CacheKey = $"temporal:{Constants.Namespace}:keys";

	private readonly WorkflowMetrics _metrics;
	private readonly IDatabase _redis;
	private readonly SecretClient _secrets;

	// ✅ DI is fine here
	public Activities(WorkflowMetrics metrics, SecretClient secrets, IConnectionMultiplexer redis)
	{
		_metrics = metrics;
		_secrets = secrets;
		_redis = redis.GetDatabase();
	}

	[Activity]
	public async Task<string> SimulateWork(string input)
	{
		ActivityExecutionContext.Current.Logger.LogInformation("Activity running with input: {input}", input);

		var sw = Stopwatch.StartNew();
		await Task.Delay(1000, ActivityExecutionContext.Current.CancellationToken);
		sw.Stop();

		_metrics.ActivityDurationMs.Record(sw.Elapsed.TotalMilliseconds);

		ActivityExecutionContext.Current.Logger.LogInformation("Activity completed.");

		return $"Processed: {input}";
	}

	[Activity]
	public async Task<string> FinalizeWork(string input)
	{
		ActivityExecutionContext.Current.Logger.LogInformation("Final activity running with input: {input}", input);

		var sw = Stopwatch.StartNew();
		await Task.Delay(1000, ActivityExecutionContext.Current.CancellationToken);
		sw.Stop();

		_metrics.ActivityDurationMs.Record(sw.Elapsed.TotalMilliseconds);

		ActivityExecutionContext.Current.Logger.LogInformation("Final activity completed.");

		return $"Finalized: {input}";
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
}