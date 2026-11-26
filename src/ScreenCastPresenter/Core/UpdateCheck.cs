using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenCastPresenter.Core;

/// <summary>What a check for a newer release found.</summary>
/// <param name="Tag">The newest release's tag (e.g. "v1.2.0"); null when there is none.</param>
/// <param name="Problem">Why it couldn't tell; null when it could.</param>
internal sealed record UpdateResult(string? Tag, Version? Version, string? Problem);

/// <summary>
/// The opt-in check for a newer release (FR-73). Off unless the user switches it on; then at most
/// once a day it asks GitHub which release of this project is the newest. Only that question is
/// sent: no data about the user or the PC. The asking happens in a short-lived helper (the same exe
/// with <see cref="Switch"/>), so ScreenCast Presenter itself never opens a network connection or
/// loads networking code. A newer version is only announced, with a link to its release page (built
/// here from a checked tag, never a link taken from the network); nothing is downloaded or installed.
/// </summary>
internal static partial class UpdateCheck
{
    /// <summary>Runs the exe as the update-check helper.</summary>
    public const string Switch = "--check-update";

    public const string Owner = "reagan203", Repository = "ScreenCast-Presenter-Utility";
    public static string ApiUrl => $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest";
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>A plain release tag: "v1.2", "1.2.3", "v1.2.3.4". Anything else (betas, words) isn't offered.</summary>
    [GeneratedRegex(@"^v?(\d{1,5})\.(\d{1,5})(?:\.(\d{1,5}))?(?:\.(\d{1,5}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    /// <summary>The version a tag names, or null for a pre-release or anything that isn't a plain version.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (tag is null || TagPattern().Match(tag.Trim()) is not { Success: true } m) return null;
        int Part(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        return new Version(Part(1), Part(2), Part(3), Part(4));
    }

    /// <summary>
    /// Worth telling: newer than this one and not skipped. Versions compare by all four parts, with
    /// missing parts as 0 (1.2 = 1.2.0.0).
    /// </summary>
    public static bool IsNewer(Version latest, Version current, string? skipped)
    {
        Version newest = Normalize(latest);
        if (newest <= Normalize(current)) return false;
        return ParseTag(skipped) is not { } skip || newest > Normalize(skip); // a version even newer than the skipped one is told
    }

    private static Version Normalize(Version v) => new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>Time to ask again: never asked, a day has passed, or the clock went backwards.</summary>
    public static bool IsDue(DateTime? lastCheckUtc, DateTime nowUtc) =>
        lastCheckUtc is not { } last || nowUtc - last >= Interval || last > nowUtc + TimeSpan.FromMinutes(5);

    /// <summary>The release page for <paramref name="tag"/>, or null if the tag isn't a plain version (then nothing is opened).</summary>
    public static string? ReleasePage(string? tag) =>
        ParseTag(tag) is null ? null : $"https://github.com/{Owner}/{Repository}/releases/tag/{Uri.EscapeDataString(tag!.Trim())}";

    /// <summary>
    /// Reads GitHub's answer about the latest release: its tag, unless it's a draft or a
    /// pre-release. A garbled answer is a problem, not a crash.
    /// </summary>
    public static UpdateResult Interpret(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new UpdateResult(null, null, "GitHub's answer couldn't be read.");
            bool Flag(string name) => root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.True;
            if (Flag("draft") || Flag("prerelease")) return new UpdateResult(null, null, null);
            string? tag = root.TryGetProperty("tag_name", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            return ParseTag(tag) is { } version ? new UpdateResult(tag!.Trim(), version, null) : new UpdateResult(null, null, null);
        }
        catch (JsonException)
        {
            return new UpdateResult(null, null, "GitHub's answer couldn't be read.");
        }
    }

    // ───────────── the helper's side of the conversation (one line on standard output) ─────────────

    public static string Reply(UpdateResult result) =>
        result.Problem is { } problem ? "failed " + CommandLine.Flatten(problem)
        : result.Tag is { } tag ? "latest " + tag
        : "none";

    public static UpdateResult ParseReply(string? line)
    {
        if (line is null) return new UpdateResult(null, null, "The check didn't answer.");
        if (line.StartsWith("latest ", StringComparison.Ordinal)) return ParseTag(line[7..]) is { } v ? new UpdateResult(line[7..].Trim(), v, null) : new UpdateResult(null, null, "The check's answer couldn't be read.");
        if (line == "none") return new UpdateResult(null, null, null);
        return new UpdateResult(null, null, line.StartsWith("failed ", StringComparison.Ordinal) ? line[7..] : "The check's answer couldn't be read.");
    }
}
