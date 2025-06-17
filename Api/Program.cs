using System.Diagnostics.Metrics;
using Api.Endpoints;
using Api.Instrumentation;
using AzureKeyVaultEmulator.Aspire.Client;
using Temporalio.Converters;
using Temporalio.Extensions.DiagnosticSource;
using Temporalio.Extensions.OpenTelemetry;
using Temporalio.Runtime;
using Workflows;
using Workflows.Encryption;

var builder = WebApplication.CreateBuilder(args);

// Register OTEL early to wire everything up
builder.AddServiceDefaults(
	metrics =>
	{
		metrics.AddMeter("WorkflowMetrics");
		metrics.AddMeter("Temporal.Client");
	},
	tracing =>
	{
		tracing
			.AddSource(TracingInterceptor.ClientSource.Name)
			.AddSource(TracingInterceptor.WorkflowsSource.Name)
			.AddSource(TracingInterceptor.ActivitiesSource.Name);
	});

// API-specific services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors();

// Register WorkflowMetrics once
var workflowMeter = new Meter("WorkflowMetrics");
builder.Services.AddSingleton(workflowMeter);
builder.Services.AddSingleton<WorkflowMetrics>();

// Create Temporal runtime with metrics support
var temporalMeter = new Meter("Temporal.Client");
var runtime = new TemporalRuntime(new TemporalRuntimeOptions
{
	Telemetry = new TelemetryOptions
	{
		Metrics = new MetricsOptions { CustomMetricMeter = new CustomMetricMeter(temporalMeter) }
	}
});

// Configure Key Vault clients via the emulator
var vaultUri = builder.Configuration.GetConnectionString("keyvault") ?? string.Empty;
builder.Services.AddAzureKeyVaultEmulator(vaultUri, true, true);
builder.AddRedisClient("cache");
builder.Services.AddSingleton<KeyVaultKeyProvider>();
builder.Services.AddSingleton<KeyVaultEncryptionCodec>();

// Temporal client setup
var conn = builder.Configuration.GetConnectionString("temporal");
builder.Services
	.AddTemporalClient(conn, Constants.Namespace)
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

// Build the app
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
	app.UseSwagger();
	app.UseSwaggerUI();
}

// We need CORS so that the browser can access this endpoint from a
// different origin
app.UseCors(
	builder => builder
		.WithHeaders("content-type", "x-namespace")
		.WithMethods("POST")
		.WithOrigins("http://localhost:8233", "https://cloud.temporal.io"));

//if (!app.Environment.IsDevelopment())
//{
//    app.UseHttpsRedirection();
//}
app.MapWorkflowEndpoints(); // Workflow triggering endpoint
app.MapKeyManagementEndpoints(); // Key management endpoints
app.MapCodecEndpoints(); // Payload codec encode/decode endpoints
app.MapHealthChecks("/health");

app.Run();