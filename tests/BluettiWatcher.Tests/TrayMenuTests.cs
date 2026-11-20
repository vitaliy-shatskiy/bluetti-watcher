using BluettiWatcher.Decision;
using BluettiWatcher.Storage;
using BluettiWatcher.Tray;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

// The tray menu as data: Auto and the modes as one radio group, Open panel, Exit tray (README, Tray).
public sealed class TrayMenuTests
{
    private static readonly TrayMode[] Modes = [new("eco", "Eco"), new("max", "Max")];

    [Fact]
    public void The_menu_lists_auto_the_modes_then_panel_and_exit()
    {
        var items = TrayMenu.Build(Modes, null);
        Assert.Equal(["Auto", "Eco", "Max", "", "Open panel", "Exit tray"], items.Select(i => i.Text));
        Assert.Equal([true, true, true, false, false, false], items.Select(i => i.Radio));
        Assert.True(items[3].Separator);
        // The radio ids are one consecutive range (the shell's radio check takes first..last).
        Assert.Equal([1, 2, 3], items.Where(i => i.Radio).Select(i => i.Id));
    }

    [Theory]
    [InlineData(null, "Auto")]
    [InlineData("eco", "Eco")]
    [InlineData("max", "Max")]
    public void Exactly_the_current_choice_is_checked(string? current, string text)
    {
        var items = TrayMenu.Build(Modes, current);
        Assert.Equal(text, Assert.Single(items, i => i.Checked).Text);
    }

    [Fact]
    public void A_current_mode_no_longer_listed_checks_nothing() =>
        Assert.DoesNotContain(TrayMenu.Build(Modes, "gone"), i => i.Checked);

    [Fact]
    public void Each_id_resolves_to_its_command()
    {
        var items = TrayMenu.Build(Modes, null);
        Assert.Equal((TrayCommand.SelectMode, (string?)null), TrayMenu.Resolve(items[0].Id, Modes));
        Assert.Equal((TrayCommand.SelectMode, "eco"), TrayMenu.Resolve(items[1].Id, Modes));
        Assert.Equal((TrayCommand.SelectMode, "max"), TrayMenu.Resolve(items[2].Id, Modes));
        Assert.Equal((TrayCommand.OpenPanel, (string?)null), TrayMenu.Resolve(items[4].Id, Modes));
        Assert.Equal((TrayCommand.ExitTray, (string?)null), TrayMenu.Resolve(items[5].Id, Modes));
    }

    [Theory]
    [InlineData(0)]     // the menu closed with nothing chosen
    [InlineData(4)]     // past the last mode
    [InlineData(-1)]
    [InlineData(999)]
    public void An_unknown_id_does_nothing(int id) => Assert.Equal(TrayCommand.None, TrayMenu.Resolve(id, Modes).Command);

    [Fact]
    public async Task The_manual_modes_follow_and_set_the_page_choice()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using (var store = new StationStore(Path.Combine(dir, "station.db")))
            using (var service = new StationService(new StationConfig { DataDir = dir }, new QueueReader(), new FakeActions(), new FakeHibernator(true),
                new FixedFlag(Flag()), store, new ManualClock(T0), _ => { }))
            {
                var modes = new ManualTrayModes(service);
                Assert.Equal(["eco", "max"], modes.Modes.Select(m => m.Id));
                Assert.Null(modes.Current);
                await modes.SelectAsync("max", CancellationToken.None);
                Assert.Equal("max", modes.Current);
                Assert.Equal(ManualMode.Max, service.State.Manual);
                await modes.SelectAsync(null, CancellationToken.None);
                Assert.Null(modes.Current);
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => modes.SelectAsync("turbo", CancellationToken.None));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }
}
