using System.Collections.Concurrent;
using System.Globalization;
using Azure;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace Workflows.Encryption;

/// <summary>
///     Retrieves encryption keys from Azure Key Vault (or the emulator).
/// </summary>
public class KeyVaultKeyProvider
{
	private const string SecretPrefix = "temporal-";

	private const string CacheKeyPrefix = "temporal:";
	private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.OrdinalIgnoreCase);
	private readonly SecretClient _client;
	private readonly IDatabase _redis;
	private string? _activeKeyId;
	private DateTimeOffset _activeKeyTime;

	public KeyVaultKeyProvider(SecretClient client, IConnectionMultiplexer redis, IConfiguration configuration)
	{
		_client = client;
		_redis = redis.GetDatabase();
		_activeKeyId = configuration["Encryption:ActiveKeyId"];
	}

    /// <summary>
    ///     Gets the ID of the key used for new payloads. If no configuration value
    ///     is provided the newest key in the vault will be used.
    /// </summary>
    public string ActiveKeyId
	{
		get
		{
			EnsureCacheAsync().GetAwaiter().GetResult();
			CheckForNewerKeyAsync().GetAwaiter().GetResult();
			return _activeKeyId ?? string.Empty;
		}
	}

	private static string CacheKey => $"{CacheKeyPrefix}{Constants.Namespace}:keys";

    /// <summary>
    ///     Retrieve the key bytes for the active key.
    /// </summary>
    public async Task<byte[]> GetActiveKeyAsync(CancellationToken ct = default)
	{
		await EnsureCacheAsync(ct);
		await CheckForNewerKeyAsync(ct);
		return await GetKeyAsync(ActiveKeyId, ct);
	}

    /// <summary>
    ///     Retrieve the key bytes for a given key identifier.
    /// </summary>
    public async Task<byte[]> GetKeyAsync(string keyId, CancellationToken ct = default)
	{
		await EnsureCacheAsync(ct);
		if (_cache.TryGetValue(keyId, out var val))
			return val;

		// Attempt refresh in case a new key was added after startup
		await RefreshAsync(ct);
		if (_cache.TryGetValue(keyId, out val))
			return val;

		// Load directly from vault if entry exists
		try
		{
			var secret = await _client.GetSecretAsync(SecretPrefix + keyId, cancellationToken: ct);
			val = Convert.FromBase64String(secret.Value.Value);
			_cache[keyId] = val;
			return val;
		}
		catch (RequestFailedException)
		{
			throw new KeyNotFoundException($"Key '{keyId}' not found in Key Vault");
		}
	}

    /// <summary>
    ///     Reloads keys from Key Vault, clearing the current cache.
    /// </summary>
    public Task RefreshAsync(CancellationToken ct = default)
	{
		return LoadCacheAsync(true, ct);
	}

	private async Task EnsureCacheAsync(CancellationToken ct = default)
	{
		if (_cache.IsEmpty)
			await LoadCacheAsync(false, ct);
	}

	private async Task CheckForNewerKeyAsync(CancellationToken ct = default)
	{
		var latest = await _redis.SortedSetRangeByRankWithScoresAsync(CacheKey, -1);
		if (latest.Length == 0)
			return;

		var id = (string)latest[0].Element!;
		var updated = DateTimeOffset.FromUnixTimeMilliseconds((long)latest[0].Score);
		if (_activeKeyId is null || updated > _activeKeyTime)
		{
			_activeKeyId = id;
			_activeKeyTime = updated;
		}
	}

	private async Task LoadCacheAsync(bool clear, CancellationToken ct)
	{
		if (clear)
		{
			_cache.Clear();
			_activeKeyId = null;
			_activeKeyTime = default;
		}

		var entries = await _redis.SortedSetRangeByRankWithScoresAsync(CacheKey);
		if (entries.Length == 0)
		{
			await UpdateCacheFromVaultAsync(ct);
			entries = await _redis.SortedSetRangeByRankWithScoresAsync(CacheKey);
		}

		foreach (var entry in entries)
		{
			var id = (string)entry.Element!;
			var updated = DateTimeOffset.FromUnixTimeMilliseconds((long)entry.Score);
			if (_activeKeyId is null || updated > _activeKeyTime)
			{
				_activeKeyId = id;
				_activeKeyTime = updated;
			}
		}
	}

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

	private static DateTimeOffset ParseTimestamp(string id)
	{
		var parts = id.Split('-');
		var tsStr = parts.Length > 2 ? parts[^1] : string.Empty;
		if (DateTimeOffset.TryParseExact(tsStr, "yyyyMMddHHmmssfff", null, DateTimeStyles.AssumeUniversal, out var ts))
			return ts;
		return DateTimeOffset.MinValue;
	}
}