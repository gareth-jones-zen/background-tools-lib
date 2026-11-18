using WinBackground;

namespace WinBackground.Tests;

/// <summary>v0.5: a job can also run a set time after each unlock (catching up after sleep).</summary>
public class V05Tests
{
    [Fact]
    public void After_unlock_is_a_session_unlock_trigger_with_its_delay()
    {
        var script = new ScheduledTask("x").EnableScript(@"C:\x\tool.exe", ["run"], @"C:\x",
            new TaskSchedule { Every = TimeSpan.FromMinutes(15), KeepRunning = false, AfterUnlock = TimeSpan.FromMinutes(1) });
        Assert.Contains("MSFT_TaskSessionStateChangeTrigger", script);
        Assert.Contains("$t.StateChange = 8", script);
        Assert.Contains("$t.Delay = 'PT1M'", script);
        Assert.Contains("-RepetitionInterval (New-TimeSpan -Seconds 900)", script);
        Assert.DoesNotContain("MSFT_TaskSessionStateChangeTrigger", new ScheduledTask("x").EnableScript("x", [], null));
    }

    [Fact]
    public void After_unlock_alone_is_a_trigger_and_cant_be_negative()
    {
        Assert.Contains("StateChange = 8", new ScheduledTask("x").EnableScript("x", [], null,
            new TaskSchedule { AtLogon = false, KeepRunning = false, AfterUnlock = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => new ScheduledTask("x").EnableScript("x", [], null,
            new TaskSchedule { KeepRunning = false, AfterUnlock = TimeSpan.FromMinutes(-1) }));
    }
}
