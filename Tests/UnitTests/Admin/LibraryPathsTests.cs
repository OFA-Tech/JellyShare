using JellyShare.Application.Features.Admin;

namespace JellyShare.UnitTests.Admin;

public sealed class LibraryPathsTests
{
    private readonly List<string> _calls = new();

    private void Run(Action? add = null, Action? queue = null)
        => LibraryPaths.AddWithMonitorRecovery(
            () => _calls.Add("stop"),
            () => _calls.Add("start"),
            add ?? (() => _calls.Add("add")),
            queue ?? (() => _calls.Add("queue")),
            work => work(),
            ex => _calls.Add("log:" + ex.Message));

    [Fact]
    public void Success_StopsMonitor_Adds_QueuesTheScan_AndLeavesRestartToTheScan()
    {
        Run();

        _calls.ShouldBe(["stop", "add", "queue"]);
    }

    [Fact]
    public void AddFails_MonitorIsStartedAgain_AndTheErrorPropagates_NoScan()
    {
        var ex = ShouldExactly.Throw<ArgumentException>(() => Run(add: () => throw new ArgumentException("bad path")));

        ex.Message.ShouldBe("bad path");
        _calls.ShouldBe(["stop", "start"]);
    }

    [Fact]
    public void QueueFails_MonitorIsStartedAgain_AndTheFailureIsLogged_NotThrown()
    {
        Run(queue: () => throw new InvalidOperationException("no scan"));

        _calls.ShouldBe(["stop", "add", "log:no scan", "start"]);
    }
}
