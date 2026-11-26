using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenCastPresenter.Core;

/// <summary>One thing the check found: its kind and where it is (parts of the picture).</summary>
internal sealed record CheckFinding(SensitiveKinds Kind, CropRegion Box);

/// <summary>
/// A check before sharing (FR-102): before something is shown, a private picture of it is read on
/// this PC for everything automatic hiding can find (whether or not it's switched on), and the
/// presenter sees what was found, marked, and can show it as is, turn on hiding for what was found
/// first, or not show it. Pure: the report, its words, its line in the helper's protocol, and the
/// kinds to switch on.
/// </summary>
internal sealed record CheckReport(IReadOnlyList<CheckFinding> Findings)
{
    /// <summary>At most this many findings are kept (and drawn).</summary>
    public const int MaxFindings = 200;

    /// <summary>Everything the check looks for: every kind of text, password boxes and faces.</summary>
    public const SensitiveKinds Everything = SensitiveKinds.All | SensitiveKinds.Faces;

    public bool Clean => Findings.Count == 0;

    /// <summary>"2 email addresses and a password box", or "nothing".</summary>
    public string Words() => SensitiveText.Words([.. Findings.Select(f => new SensitiveMatch(f.Kind, RectangleF.Empty))]);

    /// <summary>The kinds found that automatic hiding (<paramref name="on"/>) doesn't cover yet.</summary>
    public SensitiveKinds Missing(SensitiveKinds on) =>
        Findings.Aggregate(SensitiveKinds.None, (k, f) => k | f.Kind) & ~on & SensitiveKinds.Known;

    /// <summary>One line for the helper's output.</summary>
    public string ToLine()
    {
        var found = new JsonArray();
        foreach (CheckFinding f in Findings.Take(MaxFindings))
            found.Add(new JsonArray((int)f.Kind, Round(f.Box.X), Round(f.Box.Y), Round(f.Box.Width), Round(f.Box.Height)));
        return "checked " + found.ToJsonString();
    }

    private static double Round(double v) => Math.Round(v, 5);

    /// <summary>The helper's line back to a report; null for anything else.</summary>
    public static CheckReport? Parse(string? line)
    {
        if (line is null || !line.StartsWith("checked ", StringComparison.Ordinal)) return null;
        try
        {
            if (JsonNode.Parse(line[8..]) is not JsonArray items) return null;
            var findings = new List<CheckFinding>();
            foreach (JsonNode? item in items.Take(MaxFindings))
            {
                if (item is not JsonArray { Count: 5 } a) continue;
                var kind = (SensitiveKinds)(int)a[0]!;
                if (kind == SensitiveKinds.None || (kind & ~SensitiveKinds.Known) != 0 || (int)kind is not (1 or 2 or 4 or 8 or 16 or 32 or 64 or 128 or 256)) continue; // one kind each
                if (CropRegion.Create((double)a[1]!, (double)a[2]!, (double)a[3]!, (double)a[4]!, 0.0001) is { } box) findings.Add(new CheckFinding(kind, box));
            }
            return new CheckReport(findings);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>A short label for a finding drawn on the preview.</summary>
    public static string Label(SensitiveKinds kind) => kind switch
    {
        SensitiveKinds.Emails => "email",
        SensitiveKinds.Phones => "phone",
        SensitiveKinds.Cards => "card",
        SensitiveKinds.Ibans => "bank",
        SensitiveKinds.IdNumbers => "ID",
        SensitiveKinds.Secrets => "key",
        SensitiveKinds.Custom => "your word",
        SensitiveKinds.PasswordFields => "password",
        SensitiveKinds.Faces => "face",
        _ => kind.ToString().ToLower(CultureInfo.InvariantCulture),
    };
}
