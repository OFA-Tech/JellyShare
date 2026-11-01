using JellyShare.Jellyfin.Provider;
using Xunit;

namespace JellyShare.JellyfinTests.Provider;

public class AdminLibrariesTests
{
    private readonly List<string> _calls = new();

    private void Run(Action? add = null, Action? queue = null)
        => JellyfinAdminLibraries.AddPathWithMonitorRecovery(
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

        Assert.Equal(["stop", "add", "queue"], _calls);
    }

    [Fact]
    public void AddFails_MonitorIsStartedAgain_AndTheErrorPropagates_NoScan()
    {
        var ex = Assert.Throws<ArgumentException>(() => Run(add: () => throw new ArgumentException("bad path")));

        Assert.Equal("bad path", ex.Message);
        Assert.Equal(["stop", "start"], _calls);
    }

    [Fact]
    public void QueueFails_MonitorIsStartedAgain_AndTheFailureIsLogged_NotThrown()
    {
        Run(queue: () => throw new InvalidOperationException("no scan"));

        Assert.Equal(["stop", "add", "log:no scan", "start"], _calls);
    }
}
