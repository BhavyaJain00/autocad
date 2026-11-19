using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Ipc;
using AcadCopilot.Tests.Fakes;

namespace AcadCopilot.Core.Tests;

public sealed class JsonRpcConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static async Task<(JsonRpcConnection A, JsonRpcConnection B)> ConnectedPairAsync()
    {
        var name = "AcadCopilotTest-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(5000));
        return (new JsonRpcConnection(server, "a"), new JsonRpcConnection(client, "b"));
    }

    [Fact]
    public async Task Request_response_in_both_directions()
    {
        var (a, b) = await ConnectedPairAsync();
        await using (a)
        await using (b)
        {
            a.RequestHandler = (request, _) => Task.FromResult<JsonNode?>(new JsonObject { ["echo"] = request.Params?["v"]?.DeepClone(), ["side"] = "a" });
            b.RequestHandler = (request, _) => Task.FromResult<JsonNode?>(JsonValue.Create(request.Method));
            a.Start();
            b.Start();

            var fromA = await b.SendRequestAsync("ping", new JsonObject { ["v"] = 42 }, CancellationToken.None).WaitAsync(Timeout);
            Assert.Equal(42, fromA!["echo"]!.GetValue<int>());
            var fromB = await a.SendRequestAsync("hello", null, CancellationToken.None).WaitAsync(Timeout);
            Assert.Equal("hello", fromB!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Errors_and_notifications_are_delivered()
    {
        var (a, b) = await ConnectedPairAsync();
        await using (a)
        await using (b)
        {
            var notified = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            a.RequestHandler = (_, _) => throw new JsonRpcException(RpcErrorCodes.InvalidParams, "bad params");
            a.NotificationHandler = (method, parameters) => notified.TrySetResult(method + ":" + parameters?["x"]);
            a.Start();
            b.Start();

            var error = await Assert.ThrowsAsync<JsonRpcException>(() => b.SendRequestAsync("x", null, CancellationToken.None).WaitAsync(Timeout));
            Assert.Equal(RpcErrorCodes.InvalidParams, error.Code);
            Assert.Equal("bad params", error.Message);

            await b.SendNotificationAsync("note", new JsonObject { ["x"] = 1 });
            Assert.Equal("note:1", await notified.Task.WaitAsync(Timeout));
        }
    }

    [Fact]
    public async Task Cancelling_a_request_cancels_the_remote_handler()
    {
        var (a, b) = await ConnectedPairAsync();
        await using (a)
        await using (b)
        {
            var handlerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            a.RequestHandler = async (_, ct) =>
            {
                handlerStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout, ct);
                }
                catch (OperationCanceledException)
                {
                    handlerCancelled.TrySetResult();
                    throw;
                }

                return null;
            };
            a.Start();
            b.Start();

            using var cts = new CancellationTokenSource();
            var pending = b.SendRequestAsync("slow", null, cts.Token);
            await handlerStarted.Task.WaitAsync(Timeout);
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await handlerCancelled.Task.WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task Closing_fails_pending_requests()
    {
        var (a, b) = await ConnectedPairAsync();
        await using (b)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            a.RequestHandler = async (_, ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout, ct);
                return null;
            };
            a.Start();
            b.Start();
            var pending = b.SendRequestAsync("slow", null, CancellationToken.None);
            await started.Task.WaitAsync(Timeout);
            await a.DisposeAsync();
            await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(Timeout));
            await b.Completion.WaitAsync(Timeout);
            Assert.False(b.IsOpen);
        }
    }

    [Fact]
    public async Task Invalid_json_line_gets_a_parse_error_and_the_connection_survives()
    {
        var name = "AcadCopilotTest-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var raw = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), raw.ConnectAsync(5000));
        await using var connection = new JsonRpcConnection(server, "server");
        connection.RequestHandler = (_, _) => Task.FromResult<JsonNode?>(JsonValue.Create("ok"));
        connection.Start();

        var reader = new StreamReader(raw, Encoding.UTF8);
        await raw.WriteAsync(Encoding.UTF8.GetBytes("this is not json\n"));
        var parseError = JsonNode.Parse((await reader.ReadLineAsync().WaitAsync(Timeout))!)!;
        Assert.Equal(RpcErrorCodes.ParseError, parseError["error"]!["code"]!.GetValue<int>());

        await raw.WriteAsync(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":\"abc\",\"method\":\"m\"}\n"));
        var response = JsonNode.Parse((await reader.ReadLineAsync().WaitAsync(Timeout))!)!;
        Assert.Equal("abc", response["id"]!.GetValue<string>());
        Assert.Equal("ok", response["result"]!.GetValue<string>());
    }
}

public sealed class PipeServerClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static AcadInstanceInfo Instance(string pipe, string token) => new()
    {
        Pid = Environment.ProcessId,
        Pipe = PipeNames.FullPath(pipe),
        Token = token,
        AcadVersion = "2027",
    };

    [Fact]
    public async Task Client_authenticates_lists_and_calls_tools()
    {
        var handler = new FakePipeHandler();
        var pipe = "AcadCopilotTest-" + Guid.NewGuid().ToString("N");
        var token = PipeNames.NewToken();
        await using var server = new PipeServer(pipe, token, handler);
        server.Start();

        await using var client = await PipeClient.ConnectAsync(Instance(pipe, token), "test", Timeout, CancellationToken.None);
        Assert.Equal("FakeAddin", client.Server.ServerName);
        Assert.Equal("House.dwg", client.Server.DocumentTitle);
        Assert.Equal(1, server.AuthenticatedClientCount);

        var tools = await client.ListToolsAsync(CancellationToken.None);
        Assert.Equal(["get_model_info", "create_wall"], tools.Select(t => t.Name));
        Assert.Equal("object", tools[1].InputSchema["type"]!.GetValue<string>());

        var result = await client.CallToolAsync("create_wall", new JsonObject { ["level"] = "L1" }, "c1", CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal([1001L], result.CreatedIds);
        Assert.True(handler.Calls.TryDequeue(out var call));
        Assert.Equal("c1", call.CallId);
        Assert.Equal("L1", call.Arguments!["level"]!.GetValue<string>());
    }

    [Fact]
    public async Task Wrong_token_is_rejected()
    {
        var pipe = "AcadCopilotTest-" + Guid.NewGuid().ToString("N");
        await using var server = new PipeServer(pipe, PipeNames.NewToken(), new FakePipeHandler());
        server.Start();
        var error = await Assert.ThrowsAsync<JsonRpcException>(() =>
            PipeClient.ConnectAsync(Instance(pipe, "wrong"), "test", Timeout, CancellationToken.None));
        Assert.Equal(RpcErrorCodes.Unauthorized, error.Code);
        Assert.Equal(0, server.AuthenticatedClientCount);
    }

    [Fact]
    public async Task Requests_before_auth_are_refused()
    {
        var pipe = "AcadCopilotTest-" + Guid.NewGuid().ToString("N");
        await using var server = new PipeServer(pipe, PipeNames.NewToken(), new FakePipeHandler());
        server.Start();
        var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await stream.ConnectAsync(5000);
        await using var connection = new JsonRpcConnection(stream, "raw");
        connection.Start();
        var error = await Assert.ThrowsAsync<JsonRpcException>(() => connection.SendRequestAsync(RpcMethods.ToolsList, null, CancellationToken.None).WaitAsync(Timeout));
        Assert.Equal(RpcErrorCodes.Unauthorized, error.Code);
    }

    [Fact]
    public async Task Ui_methods_round_trip_and_map_unavailable_ui()
    {
        var handler = new FakePipeHandler();
        var pipe = "AcadCopilotTest-" + Guid.NewGuid().ToString("N");
        var token = PipeNames.NewToken();
        await using var server = new PipeServer(pipe, token, handler);
        server.Start();
        await using var client = await PipeClient.ConnectAsync(Instance(pipe, token), "test", Timeout, CancellationToken.None);

        var request = new QuestionRequest
        {
            Questions = [new UserQuestion { Question = "How many bedrooms?", Options = [new QuestionOption { Label = "3" }, new QuestionOption { Label = "4" }] }],
        };
        var response = await client.AskUserAsync(request, CancellationToken.None);
        Assert.Equal("3", Assert.Single(Assert.Single(response.Answers).Selected));

        await client.UpdatePlanAsync(new PlanUpdate { Tasks = [new PlanTask { Title = "Walls", Status = PlanTaskStatus.InProgress }] }, CancellationToken.None);
        Assert.True(handler.Plans.TryDequeue(out var plan));
        Assert.Equal(PlanTaskStatus.InProgress, plan.Tasks[0].Status);

        handler.UiAvailable = false;
        var error = await Assert.ThrowsAsync<JsonRpcException>(() => client.AskUserAsync(request, CancellationToken.None));
        Assert.Equal(RpcErrorCodes.UiUnavailable, error.Code);
    }

    [Fact]
    public async Task Server_notifications_reach_authenticated_clients_and_dispose_disconnects()
    {
        var pipe = "AcadCopilotTest-" + Guid.NewGuid().ToString("N");
        var token = PipeNames.NewToken();
        var server = new PipeServer(pipe, token, new FakePipeHandler());
        server.Start();
        await using var first = await PipeClient.ConnectAsync(Instance(pipe, token), "one", Timeout, CancellationToken.None);
        await using var second = await PipeClient.ConnectAsync(Instance(pipe, token), "two", Timeout, CancellationToken.None);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        second.Notification += (method, _) => received.TrySetResult(method);

        await server.NotifyAllAsync(RpcMethods.ToolsChanged, null);
        Assert.Equal(RpcMethods.ToolsChanged, await received.Task.WaitAsync(Timeout));

        await server.DisposeAsync();
        await first.Completion.WaitAsync(Timeout);
        await second.Completion.WaitAsync(Timeout);
        Assert.False(first.IsConnected);
    }

    [Fact]
    public async Task Connect_to_missing_pipe_times_out()
    {
        var instance = Instance("AcadCopilotTest-missing-" + Guid.NewGuid().ToString("N"), "t");
        await Assert.ThrowsAsync<TimeoutException>(() => PipeClient.ConnectAsync(instance, "test", TimeSpan.FromMilliseconds(300), CancellationToken.None));
    }
}
