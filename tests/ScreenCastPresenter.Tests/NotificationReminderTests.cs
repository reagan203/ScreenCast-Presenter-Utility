using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.Tests;

/// <summary>Reminding to turn on Do not disturb when the whole main screen is shown or recorded.</summary>
public class NotificationReminderTests
{
    [Fact]
    public void Reminds_once_when_pop_ups_would_reach_the_audience() =>
        Assert.True(NotificationReminder.ShouldRemind(enabled: true, alreadyReminded: false, popUpsWouldShow: true, showsMainScreen: true, privatePreview: false));

    [Theory]
    [InlineData(false, false, true, true, false)] // switched off in Settings
    [InlineData(true, true, true, true, false)]   // already reminded this run
    [InlineData(true, false, false, true, false)] // Do not disturb (or a full-screen app) already holds them back
    [InlineData(true, false, true, false, false)] // another screen: pop-ups appear on the main one
    [InlineData(true, false, true, true, true)]   // a private preview nobody else sees
    public void Stays_quiet_otherwise(bool enabled, bool reminded, bool popUps, bool mainScreen, bool preview) =>
        Assert.False(NotificationReminder.ShouldRemind(enabled, reminded, popUps, mainScreen, preview));

    [Fact]
    public void The_button_opens_the_right_page_for_the_Windows_version()
    {
        Assert.Equal("ms-settings:notifications", NotificationReminder.SettingsUri(windows11: true));
        Assert.Equal("ms-settings:quiethours", NotificationReminder.SettingsUri(windows11: false));
    }

    [Fact]
    public void Asking_Windows_never_throws() => _ = NotificationReminder.PopUpsWouldShow(); // true or false depends on this PC

    [Fact]
    public void On_by_default_and_remembered()
    {
        Assert.True(new AppSettings().RemindDoNotDisturb);
    }
}
