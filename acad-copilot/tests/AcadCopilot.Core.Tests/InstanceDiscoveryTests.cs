using AcadCopilot.Core.Ipc;
using AcadCopilot.Tests.Fakes;

namespace AcadCopilot.Core.Tests;

public sealed class InstanceDiscoveryTests
{
    private static AcadInstanceInfo Info(int pid, int minutesAgo) => new()
    {
        Pid = pid,
        Pipe = PipeNames.FullPath(PipeNames.ForProcess(pid)),
        Token = "t" + pid,
        AcadVersion = "2027",
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
    };

    [Fact]
    public void Writes_reads_and_picks_newest_live_instance()
    {
        using var dir = new TestDirectory();
        InstanceDiscovery.Write(Info(100, 30), dir.Path);
        InstanceDiscovery.Write(Info(200, 5), dir.Path);
        InstanceDiscovery.Write(Info(300, 1), dir.Path);
        File.WriteAllText(dir.File("999.json"), "{ not json");
        bool Alive(int pid) => pid != 300;

        var live = InstanceDiscovery.ReadLive(dir.Path, Alive);
        Assert.Equal([200, 100], live.Select(i => i.Pid));
        Assert.Equal(200, InstanceDiscovery.Find(null, dir.Path, Alive)!.Pid);
        Assert.Equal(100, InstanceDiscovery.Find(100, dir.Path, Alive)!.Pid);
        Assert.Null(InstanceDiscovery.Find(300, dir.Path, Alive));
        Assert.Equal("t200", InstanceDiscovery.TryRead(InstanceDiscovery.PathFor(200, dir.Path))!.Token);
    }

    [Fact]
    public void Remove_stale_deletes_dead_and_corrupt_files()
    {
        using var dir = new TestDirectory();
        InstanceDiscovery.Write(Info(100, 1), dir.Path);
        InstanceDiscovery.Write(Info(200, 1), dir.Path);
        File.WriteAllText(dir.File("broken.json"), "nope");

        var removed = InstanceDiscovery.RemoveStale(dir.Path, pid => pid == 100);
        Assert.Equal(2, removed);
        Assert.True(File.Exists(InstanceDiscovery.PathFor(100, dir.Path)));
        Assert.False(File.Exists(InstanceDiscovery.PathFor(200, dir.Path)));
    }

    [Fact]
    public void Missing_directory_means_no_instances()
    {
        var path = Path.Combine(Path.GetTempPath(), "AcadCopilotTests", "missing-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(InstanceDiscovery.ReadLive(path));
        Assert.Null(InstanceDiscovery.Find(null, path));
    }

    [Fact]
    public void Current_process_is_alive_and_names_are_consistent()
    {
        Assert.True(InstanceDiscovery.IsProcessAlive(Environment.ProcessId));
        Assert.Equal("AcadCopilot-42", PipeNames.ToShortName(@"\\.\pipe\AcadCopilot-42"));
        Assert.Equal("AcadCopilot-42", PipeNames.ToShortName("AcadCopilot-42"));
        Assert.NotEqual(PipeNames.NewToken(), PipeNames.NewToken());
        Assert.DoesNotContain('+', PipeNames.NewToken());
    }
}
