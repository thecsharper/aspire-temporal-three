using Google.Protobuf;
using Temporalio.Api.Common.V1;
using Workflows.Encryption;

namespace Api.Endpoints;

public static class CodecEndpoints
{
	public static void MapCodecEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/encode", EncodeAsync).WithName("EncodePayloads").WithTags("Codec").WithOpenApi();
		app.MapPost("/decode", DecodeAsync).WithName("DecodePayloads").WithTags("Codec").WithOpenApi();
	}

	private static Task<IResult> EncodeAsync(HttpContext ctx, KeyVaultEncryptionCodec codec)
	{
		return ApplyAsync(ctx, codec.EncodeAsync);
	}

	private static Task<IResult> DecodeAsync(HttpContext ctx, KeyVaultEncryptionCodec codec)
	{
		return ApplyAsync(ctx, codec.DecodeAsync);
	}

	private static async Task<IResult> ApplyAsync(
		HttpContext ctx,
		Func<IReadOnlyCollection<Payload>, Task<IReadOnlyCollection<Payload>>> func)
	{
		if (ctx.Request.ContentType?.StartsWith("application/json") != true)
			return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

		Payloads inPayloads;
		using (var reader = new StreamReader(ctx.Request.Body))
		{
			inPayloads = JsonParser.Default.Parse<Payloads>(await reader.ReadToEndAsync());
		}

		var outPayloads = new Payloads { Payloads_ = { await func(inPayloads.Payloads_) } };
		return Results.Text(JsonFormatter.Default.Format(outPayloads), "application/json");
	}
}