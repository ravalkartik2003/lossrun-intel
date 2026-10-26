using System.Reflection;
using System.Text.Json;
using Azure;
using Azure.Core;
using LossRun.Orchestration;
using Microsoft.Data.SqlClient;
using Microsoft.DurableTask.Worker;
using Microsoft.EntityFrameworkCore;

// Brief 33: TaskFailureDetails exactly as the orchestrator receives them, with no Azure call. Each exception is built by its SDK (or thrown by
// the runtime), converted by the Functions worker's own TaskFailureDetailsConverter with the app's exception-properties provider (what the worker
// sends to the host), and converted back by ProtoUtils (what the orchestrator's TaskFailedException carries). See CaptureFailures.csproj.

var cases = new List<(string Name, Exception Exception)>
{
    ("sql-40613", Thrown(Sql(40613))),
    ("efcore-dbupdate-over-sql-40613", Thrown(new Microsoft.EntityFrameworkCore.DbUpdateException(
        "An error occurred while saving the entity changes. See the inner exception for details.", Sql(40613)))),
    ("requestfailed-503", Thrown(new RequestFailedException(new FakeResponse(503, "Service Unavailable")))),
    ("requestfailed-429", Thrown(new RequestFailedException(new FakeResponse(429, "Too Many Requests")))),
    ("requestfailed-404", Thrown(new RequestFailedException(new FakeResponse(404, "Not Found")))),
    ("efcore-retry-limit-exceeded-over-sql-40613", EfRetriesExhausted(40613)),
    ("httpclient-timeout", await HttpClientTimeoutAsync()),
    ("http-connection-refused", await ConnectionRefusedAsync()),
    ("invalidoperation", Thrown(new InvalidOperationException("Graph doc-1 is missing."))),
};

var converter = Type.GetType("Microsoft.Azure.Functions.Worker.Extensions.DurableTask.TaskFailureDetailsConverter, Microsoft.Azure.Functions.Worker.Extensions.DurableTask")!;
var toWire = converter.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).First(m => m.Name == "TaskFailureFromException" && m.GetParameters().Length == 2);
var toOrchestrator = Type.GetType("Microsoft.DurableTask.ProtoUtils, Microsoft.DurableTask.Worker.Grpc")!.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
    .First(m => m.Name == "ToTaskFailureDetails" && m.GetParameters().Length == 1);
var provider = new AppExceptionProperties();
var output = new SortedDictionary<string, object>(StringComparer.Ordinal);
foreach (var (name, e) in cases)
{
    output[name] = new { Thrown = e.GetType().FullName, OrchestratorSees = Flatten(toOrchestrator.Invoke(null, [toWire.Invoke(null, [e, provider])])) };
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    description = "Brief 33: TaskFailureDetails as the orchestrator receives them, captured by tools/capture-failures (no Azure): real exceptions built by "
        + "the SDKs or thrown by the runtime, through the Functions worker's own conversion (TaskFailureDetailsConverter.TaskFailureFromException with the app's "
        + "IExceptionPropertiesProvider, ExceptionProperties.Of), then ProtoUtils back to Microsoft.DurableTask.TaskFailureDetails. The SqlException server "
        + "message is synthetic: shape only; no real capture yet (a server's text cannot be captured offline; brief 34's live resume never reached the "
        + "Durable layer, because EF's own retries absorb it). The classifier reads the sqlErrorNumber property, never the text.",
    packages = new { durableTaskWorker = "1.26.0", durableTaskAbstractions = "1.27.0", functionsWorkerExtensionsDurableTask = "1.20.0", azureCore = "1.62.0", sqlClient = "6.1.7", efCore = "10.0.13" },
    cases = output,
}, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
return;

static object? Flatten(object? f)
{
    if (f is null)
    {
        return null;
    }

    var t = f.GetType();
    object? Get(string n) => t.GetProperty(n)?.GetValue(f);
    var properties = Get("Properties") is System.Collections.IDictionary d ? d.Keys.Cast<object>().ToDictionary(k => k.ToString()!, k => d[k]) : null;
    return new { ErrorType = Get("ErrorType"), ErrorMessage = Get("ErrorMessage"), Properties = properties, InnerFailure = Flatten(Get("InnerFailure")) };
}

static Exception Thrown(Exception e)
{
    try
    {
        throw e;
    }
    catch (Exception caught)
    {
        return caught;
    }
}

// A SqlException through SqlClient's own factory: its message is the server's text; the number is on SqlError.
static SqlException Sql(int number)
{
    var ctor = typeof(SqlError).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).OrderByDescending(c => c.GetParameters().Length).First();
    var error = (SqlError)ctor.Invoke([.. ctor.GetParameters().Select(p => p.Name switch
    {
        "infoNumber" => (object?)number, "errorState" => (byte)1, "errorClass" => (byte)20, "server" => "example.database.windows.net",
        "errorMessage" => $"<synthetic: shape only; no real capture yet (server message for {number})>", "procedure" => "", "lineNumber" => 0,
        "win32ErrorCode" => p.ParameterType == typeof(uint) ? (object)0u : (object)0, "exception" => null, "batchIndex" => -1,
        _ => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null,
    })]);
    var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), true)!;
    typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(collection, [error]);
    var create = typeof(SqlException).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.Name == "CreateException")
        .OrderBy(m => m.GetParameters().Length).First(m => m.GetParameters()[0].ParameterType == typeof(SqlErrorCollection));
    return (SqlException)create.Invoke(null, [.. create.GetParameters().Select((p, i) => i == 0 ? collection
        : p.ParameterType == typeof(string) ? "12.0.2000.8" : p.ParameterType == typeof(Guid) ? Guid.Empty : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)])!;
}

// EF's own SQL Server execution strategy, retrying a 40613 until it gives up (no connection is opened: the operation throws before any I/O).
static Exception EfRetriesExhausted(int number)
{
    using var context = new Microsoft.EntityFrameworkCore.DbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder()
        .UseSqlServer("Server=example.invalid;Database=x;Encrypt=True", sql => sql.EnableRetryOnFailure(1, TimeSpan.FromMilliseconds(1), [number])).Options);
    try
    {
        context.Database.CreateExecutionStrategy().Execute(() => throw Sql(number));
    }
    catch (Exception e)
    {
        return e;
    }

    throw new InvalidOperationException("the strategy did not give up");
}

// The runtime's own HttpClient timeout (an in-process handler that never answers).
static async Task<Exception> HttpClientTimeoutAsync()
{
    using var http = new HttpClient(new NeverAnswers()) { Timeout = TimeSpan.FromMilliseconds(50) };
    try
    {
        await http.GetAsync(new Uri("http://example.invalid/"));
    }
    catch (Exception e)
    {
        return e;
    }

    throw new InvalidOperationException("no timeout");
}

// The runtime's own refused connection (a loopback port nothing listens on).
static async Task<Exception> ConnectionRefusedAsync()
{
    using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    using var http = new HttpClient();
    try
    {
        await http.GetAsync(new Uri($"http://127.0.0.1:{port}/"));
    }
    catch (Exception e)
    {
        return e;
    }

    throw new InvalidOperationException("no refusal");
}

sealed class AppExceptionProperties : IExceptionPropertiesProvider
{
    public IDictionary<string, object?>? GetExceptionProperties(Exception exception) => ExceptionProperties.Of(exception);
}

sealed class NeverAnswers : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return new HttpResponseMessage();
    }
}

sealed class FakeResponse(int status, string reason) : Response
{
    private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "application/json", ["Retry-After"] = "1" };
    public override int Status => status;
    public override string ReasonPhrase => reason;
    public override Stream? ContentStream { get; set; } = new MemoryStream("{\"error\":{\"code\":\"ServerBusy\",\"message\":\"The server is busy.\"}}"u8.ToArray());
    public override string ClientRequestId { get; set; } = "";
    public override void Dispose() { }
    protected override bool ContainsHeader(string name) => _headers.ContainsKey(name);
    protected override IEnumerable<HttpHeader> EnumerateHeaders() => _headers.Select(h => new HttpHeader(h.Key, h.Value));
    protected override bool TryGetHeader(string name, out string value) => _headers.TryGetValue(name, out value!);
    protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
    {
        var ok = _headers.TryGetValue(name, out var v);
        values = ok ? [v!] : [];
        return ok;
    }
}
