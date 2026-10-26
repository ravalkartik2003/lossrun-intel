using LossRun.Orchestration;
using Microsoft.DurableTask.Worker;

namespace LossRun.Functions;

/// <summary>
/// Brief 33 (ADR 0028): the properties an activity's failure details carry to the orchestrator's retry handler (the SQL error number). The
/// Functions Durable extension takes this provider from DI (DurableFunctionExecutor); tools/capture-failures shows the property arriving.
/// </summary>
public sealed class FailureProperties : IExceptionPropertiesProvider
{
    public IDictionary<string, object?>? GetExceptionProperties(Exception exception) => ExceptionProperties.Of(exception);
}
