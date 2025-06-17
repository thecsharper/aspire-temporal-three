using System.Diagnostics.Metrics;
using AzureKeyVaultEmulator.Aspire.Client;
using Temporalio.Converters;
using Temporalio.Extensions.DiagnosticSource;
using Temporalio.Extensions.Hosting;
using Temporalio.Extensions.OpenTelemetry;
using Temporalio.Runtime;
using Workflows;
using Workflows.Encryption;
using Workflows.Instrumentation;

var builder = Host.CreateApplicationBuilder(args);

// Configure OpenTelemetry BEFORE Temporal so meter/tracer are picked up correctly
builder.AddServiceDefaults(
	metrics =>
	{
		metrics.AddMeter("WorkflowMetrics");
		metrics.AddMeter("Temporal.Client");
	},
	tracing =>
	{
		tracing
			.AddSource(TracingInterceptor.ClientSource.Name) // "Temporal.Client"
			.AddSource(TracingInterceptor.WorkflowsSource.Name) // "Temporal.Workflow"
			.AddSource(TracingInterceptor.ActivitiesSource.Name); // "Temporal.Activity"
	});

// Configure Temporal runtime with OTEL metric support
var temporalMeter = new Meter("Temporal.Client");
var runtime = new TemporalRuntime(new TemporalRuntimeOptions
{
	Telemetry = new TelemetryOptions
	{
		Metrics = new MetricsOptions { CustomMetricMeter = new CustomMetricMeter(temporalMeter) }
	}
});

// Configure Key Vault emulator access
var vaultUri = builder.Configuration.GetConnectionString("keyvault") ?? string.Empty;
builder.Services.AddAzureKeyVaultEmulator(vaultUri, true, true);
builder.AddRedisClient("cache");
builder.Services.AddSingleton<KeyVaultKeyProvider>();
builder.Services.AddSingleton<KeyVaultEncryptionCodec>();

// Register Temporal client and worker
// Register the Temporal client separately so the worker can reuse it
builder.Services
	.AddTemporalClient(builder.Configuration.GetConnectionString("temporal"), Constants.Namespace)
	.Configure(options =>
	{
		options.Interceptors = new[] { new TracingInterceptor() };
		options.Runtime = runtime;
	})
	.Configure<IServiceProvider>((options, sp) =>
	{
		options.DataConverter = DataConverter.Default with
		{
			PayloadCodec = sp.GetRequiredService<KeyVaultEncryptionCodec>()
		};
	});

builder.Services
	.AddHostedTemporalWorker(Constants.TaskQueue)
	.AddWorkflow<SimpleWorkflow>()
	.AddWorkflow<KeyRollWorkflow>()
	.AddScopedActivities<Activities>();

// Register WorkflowMetrics as singleton
var workflowMeter = new Meter("WorkflowMetrics");
builder.Services.AddSingleton(workflowMeter);
builder.Services.AddSingleton<WorkflowMetrics>();

// Run the host
var host = builder.Build();
host.Run();