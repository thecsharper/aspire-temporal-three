using System.Security.Cryptography;
using Google.Protobuf;
using Temporalio.Api.Common.V1;
using Temporalio.Converters;

namespace Workflows.Encryption;

/// <summary>
///     Payload codec that encrypts workflow payloads using keys from Key Vault.
///     The key identifier used for encryption is stored in payload metadata so
///     older payloads remain decryptable after rotation.
/// </summary>
public class KeyVaultEncryptionCodec : IPayloadCodec
{
	private const int IvSize = 12;
	private const int TagSize = 16;
	private static readonly ByteString EncodingByteString = ByteString.CopyFromUtf8("binary/encrypted");

	private readonly KeyVaultKeyProvider _provider;

	public KeyVaultEncryptionCodec(KeyVaultKeyProvider provider)
	{
		_provider = provider;
	}

	public async Task<IReadOnlyCollection<Payload>> EncodeAsync(IReadOnlyCollection<Payload> payloads)
	{
		var keyId = _provider.ActiveKeyId;
		var key = await _provider.GetActiveKeyAsync();
		var keyIdUtf8 = ByteString.CopyFromUtf8(keyId);
		return payloads.Select(p => new Payload
		{
			Metadata =
			{
				["encoding"] = EncodingByteString,
				["encryption-key-id"] = keyIdUtf8
			},
			Data = ByteString.CopyFrom(Encrypt(p.ToByteArray(), key))
		}).ToList();
	}

	public async Task<IReadOnlyCollection<Payload>> DecodeAsync(IReadOnlyCollection<Payload> payloads)
	{
		var result = new List<Payload>(payloads.Count);
		foreach (var p in payloads)
		{
			if (p.Metadata.GetValueOrDefault("encoding") != EncodingByteString)
			{
				result.Add(p);
				continue;
			}

			var keyId = p.Metadata.GetValueOrDefault("encryption-key-id")?.ToStringUtf8();
			if (string.IsNullOrEmpty(keyId))
				throw new InvalidOperationException("Missing key id for encrypted payload");
			var key = await _provider.GetKeyAsync(keyId);
			result.Add(Payload.Parser.ParseFrom(Decrypt(p.Data.ToByteArray(), key)));
		}

		return result;
	}

	private static byte[] Encrypt(byte[] data, byte[] key)
	{
		var bytes = new byte[IvSize + TagSize + data.Length];
		var ivSpan = bytes.AsSpan(0, IvSize);
		RandomNumberGenerator.Fill(ivSpan);
		using var aes = new AesGcm(key, TagSize);
		aes.Encrypt(ivSpan, data, bytes.AsSpan(IvSize, data.Length), bytes.AsSpan(IvSize + data.Length, TagSize));
		return bytes;
	}

	private static byte[] Decrypt(byte[] data, byte[] key)
	{
		var bytes = new byte[data.Length - IvSize - TagSize];
		using var aes = new AesGcm(key, TagSize);
		aes.Decrypt(data.AsSpan(0, IvSize), data.AsSpan(IvSize, bytes.Length), data.AsSpan(IvSize + bytes.Length, TagSize), bytes.AsSpan());
		return bytes;
	}
}