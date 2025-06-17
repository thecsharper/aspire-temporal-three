using System;
using System.Diagnostics.Metrics;
using System.Threading.Tasks;
using Azure.Security.KeyVault.Secrets;
using AzureKeyVaultEmulator.Aspire.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StackExchange.Redis;
using Temporalio.Client;
using Temporalio.Worker;
using Workflows;
using Workflows.Instrumentation;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

public class SimpleWorkflowTests(ITestOutputHelper output, WorkflowEnvironment env)
	: WorkflowEnvironmentTestBase(output, env)
{
	private static IConnectionMultiplexer CreateRedis()
	{
		var conn = Environment.GetEnvironmentVariable("REDIS_CONN");
		if (string.IsNullOrEmpty(conn))
			throw new InvalidOperationException("REDIS_CONN not set");
		return ConnectionMultiplexer.Connect(conn);
	}

	[Fact]
	public async Task RunWorkflow_CompletesAfterSignal()
	{
		if (Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS") is null)
			return;
		var metrics = new WorkflowMetrics(new Meter("Test"));
		var hostBuilder = new HostBuilder();
		hostBuilder.ConfigureServices(s =>
			s.AddAzureKeyVaultEmulator("https://localhost:4997", true, true, false));
		using var host = hostBuilder.Build();
		var client = host.Services.GetRequiredService<SecretClient>();
		var redis = CreateRedis();
		var activities = new Activities(metrics, client, redis);

		using var worker = new TemporalWorker(
			Env.Client,
			new TemporalWorkerOptions(Constants.TaskQueue)
				.AddActivity(activities.SimulateWork)
				.AddActivity(activities.FinalizeWork)
				.AddWorkflow<SimpleWorkflow>());

		await worker.ExecuteAsync(async () =>
		{
			var workflowId = $"workflow-{Guid.NewGuid()}";
			var handle = await Client.StartWorkflowAsync(
				(SimpleWorkflow wf) => wf.RunAsync("hello"),
				new WorkflowOptions(workflowId, Constants.TaskQueue));

			await Task.Delay(1000);
			await handle.SignalAsync(wf => wf.Continue());

			var result = await handle.GetResultAsync();
			result.ShouldBe("Finalized: Processed: hello");
		});
	}
}