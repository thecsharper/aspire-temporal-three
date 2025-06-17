using Temporalio.Workflows;

namespace Workflows;

[Workflow]
public class KeyRollWorkflow
{
	[WorkflowRun]
	public async Task<string> RunAsync()
	{
		return await Workflow.ExecuteActivityAsync<Activities, string>(
			a => a.RollKey(),
			new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
	}
}