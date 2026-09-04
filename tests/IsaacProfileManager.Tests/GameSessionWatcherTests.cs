using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

public class GameSessionWatcherTests
{
    private sealed class FakeProcess : IGameProcessService
    {
        public bool Running { get; set; }
        public bool Throws { get; set; }
        public bool IsIsaacRunning() => Throws ? throw new InvalidOperationException("boom") : Running;
    }

    [Fact]
    public void FirstPoll_EstablishesABaseline_AndFiresNothing()
    {
        var process = new FakeProcess { Running = true };
        using var watcher = new GameSessionWatcher(process);
        var events = new List<string>();
        watcher.Started += () => events.Add("started");
        watcher.Exited += () => events.Add("exited");

        watcher.Poll();

        Assert.Empty(events);
        Assert.True(watcher.LastKnownRunning);
    }

    [Fact]
    public void RunningThenNot_FiresExitedOnce()
    {
        var process = new FakeProcess { Running = true };
        using var watcher = new GameSessionWatcher(process);
        var events = new List<string>();
        watcher.Started += () => events.Add("started");
        watcher.Exited += () => events.Add("exited");

        watcher.Poll();
        process.Running = false;
        watcher.Poll();
        watcher.Poll();

        Assert.Equal(new[] { "exited" }, events);
    }

    [Fact]
    public void NotRunningThenRunningThenNot_FiresBothInOrder()
    {
        var process = new FakeProcess();
        using var watcher = new GameSessionWatcher(process);
        var events = new List<string>();
        watcher.Started += () => events.Add("started");
        watcher.Exited += () => events.Add("exited");

        watcher.Poll();
        process.Running = true;
        watcher.Poll();
        process.Running = false;
        watcher.Poll();

        Assert.Equal(new[] { "started", "exited" }, events);
    }

    [Fact]
    public void AFailedObservation_ChangesNothing()
    {
        var process = new FakeProcess { Running = true };
        using var watcher = new GameSessionWatcher(process);
        var exited = 0;
        watcher.Exited += () => exited++;

        watcher.Poll();
        process.Throws = true;
        watcher.Poll();

        Assert.Equal(0, exited);
        Assert.True(watcher.LastKnownRunning);
    }
}
