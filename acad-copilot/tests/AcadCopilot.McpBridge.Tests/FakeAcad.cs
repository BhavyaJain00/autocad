using AcadCopilot.Core.Ipc;
using AcadCopilot.Tests.Fakes;

namespace AcadCopilot.McpBridge.Tests;

/// <summary>
/// A fake "AutoCAD with the add-in": a real PipeServer backed by <see cref="FakePipeHandler"/> (optionally wrapped,
/// for example by <see cref="ImageToolHandler"/>) plus a discovery file in a private instances folder, using this
/// test process's pid (alive) and a unique pipe name.
/// </summary>
public sealed class FakeAcad : IAsyncDisposable
{
    private readonly IPipeHandler _served;

    private FakeAcad(TestDirectory directory, PipeServer server, FakePipeHandler handler, IPipeHandler served, AcadInstanceInfo instance)
    {
        Directory = directory;
        Server = server;
        Handler = handler;
        _served = served;
        Instance = instance;
    }

    public TestDirectory Directory { get; }

    public PipeServer Server { get; private set; }

    /// <summary>The shared fake add-in (records calls, questions and plans).</summary>
    public FakePipeHandler Handler { get; }

    public AcadInstanceInfo Instance { get; private set; }

    public string InstancesDirectory => System.IO.Path.Combine(Directory.Path, "instances");

    /// <summary>Starts the fake; <paramref name="wrap"/> can put a decorator in front of the shared fake handler.</summary>
    public static FakeAcad Start(TestDirectory? directory = null, Func<FakePipeHandler, IPipeHandler>? wrap = null)
    {
        var dir = directory ?? new TestDirectory();
        var handler = new FakePipeHandler();
        var served = wrap?.Invoke(handler) ?? handler;
        var (server, instance) = Launch(served, System.IO.Path.Combine(dir.Path, "instances"));
        return new FakeAcad(dir, server, handler, served, instance);
    }

    /// <summary>Starts a fake whose tool list also has capture_view returning a PNG (see <see cref="ImageToolHandler"/>).</summary>
    public static FakeAcad StartWithImages() => Start(wrap: inner => new ImageToolHandler(inner));

    public BridgeOptions Options(bool panel = true) => new()
    {
        Pid = Environment.ProcessId,
        Panel = panel,
        InstancesDirectory = InstancesDirectory,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        LogFile = "none",
    };

    /// <summary>Simulates AutoCAD closing (pipe gone, discovery file removed).</summary>
    public async Task StopAsync()
    {
        await Server.DisposeAsync();
        InstanceDiscovery.Delete(Instance.Pid, InstancesDirectory);
    }

    /// <summary>Simulates AutoCAD starting again (new pipe name and token).</summary>
    public void Restart()
    {
        (Server, Instance) = Launch(_served, InstancesDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        Directory.Dispose();
    }

    private static (PipeServer, AcadInstanceInfo) Launch(IPipeHandler handler, string instancesDirectory)
    {
        var pipe = "AcadCopilotTest-" + Guid.NewGuid().ToString("N");
        var token = PipeNames.NewToken();
        var server = new PipeServer(pipe, token, handler);
        server.Start();
        var instance = new AcadInstanceInfo
        {
            Pid = Environment.ProcessId,
            Pipe = PipeNames.FullPath(pipe),
            Token = token,
            AcadVersion = "2027",
            DocumentTitle = "House.dwg",
        };
        InstanceDiscovery.Write(instance, instancesDirectory);
        return (server, instance);
    }
}
