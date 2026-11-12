namespace WinBackground.Tests;

/// <summary>The real Windows calls, read-only apart from a power request that's created and cleared at once.
/// Brought over from ai-keep-awake.</summary>
public class WindowsTests
{
    [Fact]
    public void A_power_request_can_be_held_and_released()
    {
        using var request = new SystemRequired("background-tools-lib test (cleared at once)");
        request.Hold(true);
        Assert.True(request.Held);
        Assert.NotEqual(0u, Power.ExecutionState() & Power.EsSystemRequired);
        request.Hold(false);
        Assert.False(request.Held);
    }

    [Fact]
    public void The_lock_settings_and_idle_time_can_be_read()
    {
        var settings = Power.LockSettings();
        Assert.False(string.IsNullOrEmpty(settings.SleepSource));
        Assert.True(Power.IdleSeconds() >= 0);
        Assert.True(Power.LockSettings(ac: true).OnMains);
    }
}
