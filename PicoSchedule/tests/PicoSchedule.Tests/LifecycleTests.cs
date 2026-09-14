namespace PicoSchedule.Tests;

public sealed class LifecycleTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Start_AfterStop_Throws_OneShotLifecycle()
    {
        var clock = new FakeClock(Start);
        var s = new Scheduler(
            new SchedulerOptions { Clock = clock, TickInterval = TimeSpan.FromHours(1) }
        );

        s.Start();
        await s.StopAsync();

        await Assert.That(() => s.Start()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Stop_BeforeStart_AlsoEndsTheLifecycle()
    {
        var clock = new FakeClock(Start);
        var s = new Scheduler(new SchedulerOptions { Clock = clock });

        await s.StopAsync(); // host may tear down before Start

        await Assert.That(() => s.Start()).Throws<InvalidOperationException>();
    }
}
