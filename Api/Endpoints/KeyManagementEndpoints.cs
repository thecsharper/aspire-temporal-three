using Api.Instrumentation;
using Temporalio.Client;
using Workflows;

namespace Api.Endpoints;

public static class KeyManagementEndpoints
{
	public static void MapKeyManagementEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/keys/roll", async (
				ITemporalClient client,
				WorkflowMetrics metrics) =>
			{
				metrics.StartedCount.Add(1);
				var id = $"key-roll-{Guid.NewGuid()}";
				await client.StartWorkflowAsync(
					(KeyRollWorkflow wf) => wf.RunAsync(),
					new WorkflowOptions(id, Constants.TaskQueue));
				return TypedResults.Accepted($"/keys/status/{id}", new KeyRollResponse(id));
			})
			.WithName("RollKey")
			.WithTags("Keys")
			.WithOpenApi();
	}

	public record KeyRollResponse(string WorkflowId);
}