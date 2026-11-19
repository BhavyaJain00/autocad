using AcadCopilot.Core.Drafting;

namespace AcadCopilot.Core.Tests.Drafting;

// DraftingPace is process-wide state: every test that touches it lives in this one class (xUnit runs the tests of a
// class one after the other) and restores watch mode when it ends.
public sealed class DraftingPaceTests : IDisposable
{
    public DraftingPaceTests()
    {
        DraftingPace.Watch = true;
    }

    public void Dispose()
    {
        DraftingPace.Watch = true;
    }

    [Fact]
    public void Watch_mode_is_the_default_and_pauses()
    {
        Assert.True(DraftingPace.Watch);
        Assert.Equal("watch", DraftingPace.ModeName);
        Assert.Equal(DraftingPace.WatchInputDelay, DraftingPace.InputDelayMs);
        Assert.Equal(DraftingPace.WatchCommandDelay, DraftingPace.CommandDelayMs);
        Assert.InRange(DraftingPace.InputDelayMs, 300, 700);
        Assert.InRange(DraftingPace.CommandDelayMs, 600, 1300);
    }

    [Fact]
    public void Fast_mode_has_no_pauses()
    {
        DraftingPace.Watch = false;
        Assert.Equal("fast", DraftingPace.ModeName);
        Assert.Equal(0, DraftingPace.InputDelayMs);
        Assert.Equal(0, DraftingPace.CommandDelayMs);
    }

    [Fact]
    public void Changed_is_raised_only_on_a_real_change()
    {
        var raised = 0;
        void Handler(object? sender, EventArgs e)
        {
            Assert.Null(sender);
            raised++;
        }

        DraftingPace.Changed += Handler;
        try
        {
            DraftingPace.Watch = true;
            Assert.Equal(0, raised);
            DraftingPace.Watch = false;
            Assert.Equal(1, raised);
            Assert.False(DraftingPace.Watch);
            DraftingPace.Watch = false;
            Assert.Equal(1, raised);
            DraftingPace.Watch = true;
            Assert.Equal(2, raised);
        }
        finally
        {
            DraftingPace.Changed -= Handler;
        }
    }

    [Fact]
    public void A_failing_handler_does_not_break_the_setter_or_other_handlers()
    {
        var second = 0;
        EventHandler failing = (_, _) => throw new InvalidOperationException("boom");
        EventHandler counting = (_, _) => second++;
        DraftingPace.Changed += failing;
        DraftingPace.Changed += counting;
        try
        {
            DraftingPace.Watch = false;
            Assert.False(DraftingPace.Watch);
            Assert.Equal(1, second);
        }
        finally
        {
            DraftingPace.Changed -= failing;
            DraftingPace.Changed -= counting;
        }
    }

    [Fact]
    public void Concurrent_toggles_leave_a_consistent_state()
    {
        var changes = 0;
        EventHandler counting = (_, _) => Interlocked.Increment(ref changes);
        DraftingPace.Changed += counting;
        try
        {
            Parallel.For(0, 1000, i => DraftingPace.Watch = i % 2 == 0);
            DraftingPace.Watch = true;
            Assert.True(DraftingPace.Watch);
            Assert.Equal(DraftingPace.WatchInputDelay, DraftingPace.InputDelayMs);
            Assert.True(changes >= 1);
        }
        finally
        {
            DraftingPace.Changed -= counting;
        }
    }
}
