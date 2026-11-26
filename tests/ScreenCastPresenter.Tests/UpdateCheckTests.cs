using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.Tests;

/// <summary>FR-73: the opt-in check for a newer release.</summary>
public class UpdateCheckTests
{
    [Theory]
    [InlineData("v1.2.0", "1.2.0.0")]
    [InlineData("1.2", "1.2.0.0")]
    [InlineData(" v2.10.3.4 ", "2.10.3.4")]
    public void Plain_version_tags_are_understood(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateCheck.ParseTag(tag));

    [Theory]
    [InlineData("v1.2.0-beta")]
    [InlineData("1.2.0-rc.1")]
    [InlineData("latest")]
    [InlineData("v1")]
    [InlineData("v1.2.3.4.5")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("v1.2.0/../../evil")]
    public void Anything_else_is_not_offered(string? tag) => Assert.Null(UpdateCheck.ParseTag(tag));

    private static readonly Version Current = new(1, 0, 0);

    [Fact]
    public void Only_a_newer_version_is_announced()
    {
        Assert.True(UpdateCheck.IsNewer(new Version(1, 0, 1), Current, null));
        Assert.True(UpdateCheck.IsNewer(new Version(1, 1), Current, null));
        Assert.False(UpdateCheck.IsNewer(new Version(1, 0, 0), Current, null));
        Assert.False(UpdateCheck.IsNewer(new Version(1, 0), new Version(1, 0, 0, 0), null)); // 1.0 is 1.0.0.0
        Assert.False(UpdateCheck.IsNewer(new Version(0, 9, 9), Current, null));
    }

    [Fact]
    public void A_skipped_version_isnt_announced_again_but_a_newer_one_is()
    {
        Assert.False(UpdateCheck.IsNewer(new Version(1, 2, 0), Current, "v1.2.0"));
        Assert.True(UpdateCheck.IsNewer(new Version(1, 3, 0), Current, "v1.2.0"));
        Assert.True(UpdateCheck.IsNewer(new Version(1, 2, 0), Current, "garbage")); // an odd skipped value doesn't hide anything
    }

    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Asks_at_most_once_a_day()
    {
        Assert.True(UpdateCheck.IsDue(null, Now));
        Assert.False(UpdateCheck.IsDue(Now.AddHours(-23), Now));
        Assert.True(UpdateCheck.IsDue(Now.AddHours(-24), Now));
        Assert.True(UpdateCheck.IsDue(Now.AddDays(3), Now)); // the clock went backwards: ask, rather than never again
    }

    [Fact]
    public void The_link_is_built_here_from_a_checked_tag()
    {
        Assert.Equal("https://github.com/reagan203/ScreenCast-Presenter-Utility/releases/tag/v1.2.0", UpdateCheck.ReleasePage("v1.2.0"));
        Assert.Null(UpdateCheck.ReleasePage("v1.2.0\" onclick=\"x"));
        Assert.Null(UpdateCheck.ReleasePage("https://evil.example/v1.2.0"));
        Assert.StartsWith("https://api.github.com/repos/reagan203/ScreenCast-Presenter-Utility/", UpdateCheck.ApiUrl);
    }

    [Fact]
    public void GitHubs_answer_is_read_carefully()
    {
        Assert.Equal("v1.4.0", UpdateCheck.Interpret("""{ "tag_name": "v1.4.0", "draft": false, "prerelease": false, "html_url": "https://evil.example" }""").Tag);
        Assert.Null(UpdateCheck.Interpret("""{ "tag_name": "v1.4.0", "prerelease": true }""").Tag);
        Assert.Null(UpdateCheck.Interpret("""{ "tag_name": "v1.4.0", "draft": true }""").Tag);
        Assert.Null(UpdateCheck.Interpret("""{ "name": "no tag" }""").Tag);
        Assert.Null(UpdateCheck.Interpret("""{ "tag_name": 12 }""").Problem);
        Assert.NotNull(UpdateCheck.Interpret("not json").Problem);
        Assert.NotNull(UpdateCheck.Interpret("[1,2]").Problem);
    }

    [Fact]
    public void The_helpers_one_line_answer_survives_the_trip()
    {
        UpdateResult newer = UpdateCheck.ParseReply(UpdateCheck.Reply(new UpdateResult("v1.4.0", new Version(1, 4, 0, 0), null)));
        Assert.Equal(("v1.4.0", new Version(1, 4, 0, 0)), (newer.Tag, newer.Version));
        Assert.Null(UpdateCheck.ParseReply(UpdateCheck.Reply(new UpdateResult(null, null, null))).Tag);
        Assert.Equal("No internet.", UpdateCheck.ParseReply(UpdateCheck.Reply(new UpdateResult(null, null, "No internet."))).Problem);
        Assert.NotNull(UpdateCheck.ParseReply(null).Problem);
        Assert.NotNull(UpdateCheck.ParseReply("latest not-a-version").Problem);
        Assert.NotNull(UpdateCheck.ParseReply("hello").Problem);
    }

    [Fact]
    public void Off_unless_switched_on_and_the_switch_is_never_a_command()
    {
        Assert.False(new AppSettings().CheckForUpdates);
        Assert.False(CommandLine.IsRequest([UpdateCheck.Switch]));
        Assert.True(TrayMenuPlan.IsKnownCommand("check-updates"));
    }
}
