using System.Globalization;
using System.Text;

namespace ScreenCastPresenter.Core;

/// <summary>One event from a calendar file, with when it repeats.</summary>
internal sealed record CalendarEvent(string Summary, DateTime Start, DateTime End, bool AllDay, Recurrence? Repeats, IReadOnlySet<DateTime> Except);

/// <summary>When an event happens again (RRULE): daily, weekly on some days, or monthly on the same date, every so often, until a date or a number of times.</summary>
internal sealed record Recurrence(string Frequency, int Interval, IReadOnlySet<DayOfWeek> Days, int? Count, DateTime? Until);

/// <summary>One time an event happens: local start and end, and its title.</summary>
internal sealed record CalendarOccurrence(DateTime Start, DateTime End, string Summary);

/// <summary>
/// Schedules from a calendar file (FR-108): a room's bookings exported from Outlook, Google or a
/// room system as an .ics file (or a synced copy of one). Read on the PC, never fetched from the
/// internet. The parts of iCalendar (RFC 5545) a room's bookings use: events with a start and an
/// end, in UTC, a named time zone or local time; all-day events (skipped: a room isn't set up for
/// a whole day); daily, weekly and monthly repeats with an interval, days, a count or an end; and
/// dates left out of a repeat.
/// </summary>
internal static class IcsCalendar
{
    /// <summary>At most this many events are read from one file, and this many times one event repeats.</summary>
    public const int MaxEvents = 2000, MaxRepeats = 1000;

    /// <summary>A file bigger than this isn't a room's calendar.</summary>
    public const long MaxBytes = 8 * 1024 * 1024;

    public static List<CalendarEvent> Parse(string text)
    {
        var events = new List<CalendarEvent>();
        Dictionary<string, (string Value, Dictionary<string, string> Params)>? current = null;
        var except = new HashSet<DateTime>();
        foreach (string line in Unfold(text))
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                current = new(StringComparer.OrdinalIgnoreCase);
                except = [];
                continue;
            }
            if (current is null) continue;
            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (ToEvent(current, except) is { } e) events.Add(e);
                current = null;
                if (events.Count == MaxEvents) break;
                continue;
            }
            (string name, Dictionary<string, string> parameters, string value) = Property(line);
            if (name.Length == 0) continue;
            if (name.Equals("EXDATE", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (When(part.Trim(), parameters) is { } ex) except.Add(ex.Time);
                continue;
            }
            current.TryAdd(name, (value, parameters));
        }
        return events;
    }

    /// <summary>Lines with folded continuations joined (a line starting with a space or tab continues the one before).</summary>
    internal static IEnumerable<string> Unfold(string text)
    {
        var line = new StringBuilder();
        foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
            {
                line.Append(raw, 1, raw.Length - 1);
                continue;
            }
            if (line.Length > 0) yield return line.ToString();
            line.Clear().Append(raw);
        }
        if (line.Length > 0) yield return line.ToString();
    }

    /// <summary>"DTSTART;TZID=Europe/London:20261126T090000" into its name, parameters and value.</summary>
    internal static (string Name, Dictionary<string, string> Params, string Value) Property(string line)
    {
        int colon = -1;
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted)
            {
                colon = i;
                break;
            }
        }
        if (colon <= 0) return ("", [], "");
        string[] head = line[..colon].Split(';');
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string p in head.Skip(1))
        {
            int eq = p.IndexOf('=');
            if (eq > 0) parameters[p[..eq].Trim()] = p[(eq + 1)..].Trim().Trim('"');
        }
        return (head[0].Trim(), parameters, line[(colon + 1)..]);
    }

    private static CalendarEvent? ToEvent(Dictionary<string, (string Value, Dictionary<string, string> Params)> p, HashSet<DateTime> except)
    {
        if (!p.TryGetValue("DTSTART", out var startProp) || When(startProp.Value, startProp.Params) is not { } start) return null;
        if (p.TryGetValue("STATUS", out var status) && status.Value.Trim().Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)) return null;
        DateTime end;
        if (p.TryGetValue("DTEND", out var endProp) && When(endProp.Value, endProp.Params) is { } e) end = e.Time;
        else if (p.TryGetValue("DURATION", out var durationProp) && Duration(durationProp.Value) is { } d) end = start.Time + d;
        else end = start.AllDay ? start.Time.AddDays(1) : start.Time;
        if (end < start.Time) end = start.Time;
        string summary = p.TryGetValue("SUMMARY", out var s) ? Unescape(s.Value) : "";
        Recurrence? repeats = p.TryGetValue("RRULE", out var rule) ? Rule(rule.Value) : null;
        return new CalendarEvent(InkStroke.TidyText(summary, 200), start.Time, end, start.AllDay, repeats, except);
    }

    /// <summary>A date-time value as local time (UTC, a named zone or floating), or an all-day date.</summary>
    internal static (DateTime Time, bool AllDay)? When(string value, IReadOnlyDictionary<string, string> parameters)
    {
        value = value.Trim();
        if (value.Length == 8 && DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
            return (date, true);
        bool utc = value.EndsWith('Z');
        string bare = utc ? value[..^1] : value;
        if (!DateTime.TryParseExact(bare, ["yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime t)) return null;
        if (utc) return (DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime(), false);
        if (parameters.TryGetValue("TZID", out string? zone) && Zone(zone) is { } tz)
        {
            try
            {
                return (TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), tz, TimeZoneInfo.Local), false);
            }
            catch (ArgumentException)
            {
                return (t, false); // a time that doesn't exist there (the clocks went forward): as written
            }
        }
        return (DateTime.SpecifyKind(t, DateTimeKind.Local), false); // floating: the PC's own time
    }

    /// <summary>A time zone by Windows name ("GMT Standard Time") or IANA name ("Europe/London"); null when unknown.</summary>
    internal static TimeZoneInfo? Zone(string id)
    {
        id = id.Trim().Trim('"');
        if (id.StartsWith('/')) id = id[1..]; // "/Europe/London" from some exporters
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            // The app runs without ICU (invariant globalization, for memory), so Windows can't map
            // IANA names itself: the common ones are mapped here (from CLDR's windowsZones).
            return IanaToWindows.TryGetValue(id, out string? windows) && !windows.Equals(id, StringComparison.Ordinal) ? Zone(windows) : null;
        }
    }

    /// <summary>Common IANA time zones and their Windows names.</summary>
    internal static readonly IReadOnlyDictionary<string, string> IanaToWindows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["UTC"] = "UTC", ["Etc/UTC"] = "UTC", ["Etc/GMT"] = "UTC",
        ["Europe/London"] = "GMT Standard Time", ["Europe/Dublin"] = "GMT Standard Time", ["Europe/Lisbon"] = "GMT Standard Time",
        ["Europe/Berlin"] = "W. Europe Standard Time", ["Europe/Amsterdam"] = "W. Europe Standard Time", ["Europe/Rome"] = "W. Europe Standard Time",
        ["Europe/Vienna"] = "W. Europe Standard Time", ["Europe/Zurich"] = "W. Europe Standard Time", ["Europe/Stockholm"] = "W. Europe Standard Time",
        ["Europe/Oslo"] = "W. Europe Standard Time", ["Europe/Luxembourg"] = "W. Europe Standard Time",
        ["Europe/Paris"] = "Romance Standard Time", ["Europe/Brussels"] = "Romance Standard Time", ["Europe/Madrid"] = "Romance Standard Time",
        ["Europe/Copenhagen"] = "Romance Standard Time",
        ["Europe/Prague"] = "Central Europe Standard Time", ["Europe/Budapest"] = "Central Europe Standard Time",
        ["Europe/Warsaw"] = "Central European Standard Time", ["Europe/Zagreb"] = "Central European Standard Time",
        ["Europe/Athens"] = "GTB Standard Time", ["Europe/Bucharest"] = "GTB Standard Time",
        ["Europe/Helsinki"] = "FLE Standard Time", ["Europe/Kyiv"] = "FLE Standard Time", ["Europe/Kiev"] = "FLE Standard Time", ["Europe/Riga"] = "FLE Standard Time",
        ["Europe/Istanbul"] = "Turkey Standard Time", ["Europe/Moscow"] = "Russian Standard Time",
        ["America/New_York"] = "Eastern Standard Time", ["America/Toronto"] = "Eastern Standard Time", ["America/Detroit"] = "Eastern Standard Time",
        ["America/Chicago"] = "Central Standard Time", ["America/Winnipeg"] = "Central Standard Time",
        ["America/Denver"] = "Mountain Standard Time", ["America/Edmonton"] = "Mountain Standard Time", ["America/Phoenix"] = "US Mountain Standard Time",
        ["America/Los_Angeles"] = "Pacific Standard Time", ["America/Vancouver"] = "Pacific Standard Time",
        ["America/Anchorage"] = "Alaskan Standard Time", ["Pacific/Honolulu"] = "Hawaiian Standard Time",
        ["America/Halifax"] = "Atlantic Standard Time", ["America/Mexico_City"] = "Central Standard Time (Mexico)",
        ["America/Sao_Paulo"] = "E. South America Standard Time", ["America/Argentina/Buenos_Aires"] = "Argentina Standard Time",
        ["America/Bogota"] = "SA Pacific Standard Time", ["America/Lima"] = "SA Pacific Standard Time", ["America/Santiago"] = "Pacific SA Standard Time",
        ["Africa/Johannesburg"] = "South Africa Standard Time", ["Africa/Lagos"] = "W. Central Africa Standard Time",
        ["Africa/Nairobi"] = "E. Africa Standard Time", ["Africa/Addis_Ababa"] = "E. Africa Standard Time", ["Africa/Kampala"] = "E. Africa Standard Time",
        ["Africa/Dar_es_Salaam"] = "E. Africa Standard Time", ["Africa/Cairo"] = "Egypt Standard Time", ["Africa/Casablanca"] = "Morocco Standard Time",
        ["Africa/Accra"] = "Greenwich Standard Time",
        ["Asia/Dubai"] = "Arabian Standard Time", ["Asia/Riyadh"] = "Arab Standard Time", ["Asia/Jerusalem"] = "Israel Standard Time",
        ["Asia/Karachi"] = "Pakistan Standard Time", ["Asia/Kolkata"] = "India Standard Time", ["Asia/Calcutta"] = "India Standard Time",
        ["Asia/Dhaka"] = "Bangladesh Standard Time", ["Asia/Bangkok"] = "SE Asia Standard Time", ["Asia/Jakarta"] = "SE Asia Standard Time",
        ["Asia/Ho_Chi_Minh"] = "SE Asia Standard Time", ["Asia/Singapore"] = "Singapore Standard Time", ["Asia/Kuala_Lumpur"] = "Singapore Standard Time",
        ["Asia/Manila"] = "Singapore Standard Time", ["Asia/Hong_Kong"] = "China Standard Time", ["Asia/Shanghai"] = "China Standard Time",
        ["Asia/Taipei"] = "Taipei Standard Time", ["Asia/Tokyo"] = "Tokyo Standard Time", ["Asia/Seoul"] = "Korea Standard Time",
        ["Australia/Sydney"] = "AUS Eastern Standard Time", ["Australia/Melbourne"] = "AUS Eastern Standard Time", ["Australia/Canberra"] = "AUS Eastern Standard Time",
        ["Australia/Brisbane"] = "E. Australia Standard Time", ["Australia/Adelaide"] = "Cen. Australia Standard Time", ["Australia/Perth"] = "W. Australia Standard Time",
        ["Pacific/Auckland"] = "New Zealand Standard Time",
    };

    /// <summary>"PT1H30M", "P1D", "-PT15M" (only the time parts a meeting uses).</summary>
    internal static TimeSpan? Duration(string text)
    {
        text = text.Trim().ToUpperInvariant();
        bool negative = text.StartsWith('-');
        text = text.TrimStart('+', '-');
        if (!text.StartsWith('P')) return null;
        TimeSpan total = TimeSpan.Zero;
        int number = 0;
        bool any = false;
        foreach (char c in text[1..])
        {
            if (char.IsAsciiDigit(c))
            {
                number = number * 10 + (c - '0');
                if (number > 100_000) return null;
                continue;
            }
            switch (c)
            {
                case 'W': total += TimeSpan.FromDays(7 * number); break;
                case 'D': total += TimeSpan.FromDays(number); break;
                case 'H': total += TimeSpan.FromHours(number); break;
                case 'M': total += TimeSpan.FromMinutes(number); break;
                case 'S': total += TimeSpan.FromSeconds(number); break;
                case 'T': continue;
                default: return null;
            }
            number = 0;
            any = true;
        }
        return any ? negative ? -total : total : null;
    }

    /// <summary>"FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=10" (and UNTIL); null for a rule it doesn't follow.</summary>
    internal static Recurrence? Rule(string text)
    {
        var parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim().ToUpperInvariant(), p => p[1].Trim(), StringComparer.Ordinal);
        if (!parts.TryGetValue("FREQ", out string? freq) || freq.ToUpperInvariant() is not ("DAILY" or "WEEKLY" or "MONTHLY")) return null;
        int interval = parts.TryGetValue("INTERVAL", out string? iv) && int.TryParse(iv, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is > 0 and <= 366 ? n : 1;
        var days = new HashSet<DayOfWeek>();
        if (parts.TryGetValue("BYDAY", out string? byDay))
        {
            string[] codes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];
            foreach (string d in byDay.Split(','))
            {
                string code = d.Trim().ToUpperInvariant();
                code = code.Length >= 2 ? code[^2..] : code; // "1MO" (the first Monday) is read as Monday
                int i = Array.IndexOf(codes, code);
                if (i >= 0) days.Add((DayOfWeek)i);
            }
        }
        int? count = parts.TryGetValue("COUNT", out string? c) && int.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out int cn) && cn > 0 ? Math.Min(cn, MaxRepeats) : null;
        DateTime? until = parts.TryGetValue("UNTIL", out string? u) && When(u, new Dictionary<string, string>()) is { } ut ? ut.AllDay ? ut.Time.AddDays(1).AddTicks(-1) : ut.Time : null;
        return new Recurrence(freq.ToUpperInvariant(), interval, days, count, until);
    }

    private static string Unescape(string s) =>
        s.Replace("\\n", " ", StringComparison.OrdinalIgnoreCase).Replace("\\,", ",", StringComparison.Ordinal).Replace("\\;", ";", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

    /// <summary>
    /// Every time the events happen between <paramref name="from"/> and <paramref name="to"/>
    /// (starting in that window), in order; all-day events are left out.
    /// </summary>
    public static List<CalendarOccurrence> Occurrences(IReadOnlyList<CalendarEvent> events, DateTime from, DateTime to)
    {
        var found = new List<CalendarOccurrence>();
        foreach (CalendarEvent e in events)
        {
            if (e.AllDay) continue;
            TimeSpan length = e.End - e.Start;
            foreach (DateTime start in Starts(e, to))
            {
                if (start >= to) break;
                if (start >= from && !e.Except.Contains(start)) found.Add(new CalendarOccurrence(start, start + length, e.Summary));
            }
        }
        found.Sort((a, b) => a.Start.CompareTo(b.Start));
        return found;
    }

    /// <summary>The event's starts in order, up to <paramref name="to"/> (at most <see cref="MaxRepeats"/>).</summary>
    private static IEnumerable<DateTime> Starts(CalendarEvent e, DateTime to)
    {
        if (e.Repeats is not { } r)
        {
            yield return e.Start;
            yield break;
        }
        int made = 0;
        if (r.Frequency == "WEEKLY")
        {
            IReadOnlySet<DayOfWeek> days = r.Days.Count > 0 ? r.Days : new HashSet<DayOfWeek> { e.Start.DayOfWeek };
            DateTime weekStart = e.Start.Date.AddDays(-(((int)e.Start.DayOfWeek + 6) % 7)); // Monday of its week
            for (int week = 0; made < MaxRepeats; week += r.Interval)
            {
                DateTime monday = weekStart.AddDays(7 * week);
                if (monday > to) yield break;
                for (int d = 0; d < 7; d++)
                {
                    DateTime day = monday.AddDays(d);
                    if (!days.Contains(day.DayOfWeek)) continue;
                    DateTime start = day + e.Start.TimeOfDay;
                    if (start < e.Start) continue;
                    if (r.Until is { } until && start > until || r.Count is { } count && made >= count) yield break;
                    made++;
                    yield return start;
                }
            }
            yield break;
        }
        for (int i = 0; made < MaxRepeats; i += r.Interval)
        {
            DateTime start = r.Frequency == "DAILY" ? e.Start.AddDays(i) : e.Start.AddMonths(i);
            if (r.Frequency == "MONTHLY" && start.Day != e.Start.Day) continue; // the 31st in a shorter month: skipped, as calendars do
            if (start > to || r.Until is { } until && start > until || r.Count is { } count && made >= count) yield break;
            made++;
            yield return start;
        }
    }
}

/// <summary>
/// What a room does around its bookings (FR-108): before each event (whose title contains
/// <see cref="Match"/>, if set) does <see cref="AtStart"/>, and when it ends does <see cref="AtEnd"/>.
/// The calendar file is read again whenever it changes.
/// </summary>
internal sealed class CalendarLink
{
    public const int MaxLeadMinutes = 30;

    public bool Enabled { get; set; } = true;

    /// <summary>The .ics file (a synced or exported copy of the room's calendar).</summary>
    public string? File { get; set; }

    /// <summary>Only events whose title contains this (any capitals); empty for all.</summary>
    public string Match { get; set; } = "";

    /// <summary>The start's action runs this many minutes before the event starts.</summary>
    public int LeadMinutes { get; set; }

    /// <summary>What happens when an event starts (its Time and Days aren't used).</summary>
    public ScheduleEntry? AtStart { get; set; }

    /// <summary>What happens when an event ends.</summary>
    public ScheduleEntry? AtEnd { get; set; }

    public CalendarLink Normalized()
    {
        Match = InkStroke.TidyText(Match ?? "", 60);
        LeadMinutes = Math.Clamp(LeadMinutes, 0, MaxLeadMinutes);
        File = string.IsNullOrWhiteSpace(File) ? null : File.Trim();
        return this;
    }

    public bool Wants(CalendarOccurrence o) => Match.Length == 0 || o.Summary.Contains(Match, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What's due in (<paramref name="from"/>, <paramref name="to"/>]: an event's start (less the
    /// lead) or its end, in time order. A start and an end at the same moment (back-to-back
    /// meetings): the end first, so the next meeting's setup wins.
    /// </summary>
    public List<(bool Start, CalendarOccurrence Event)> Due(IReadOnlyList<CalendarOccurrence> occurrences, DateTime from, DateTime to)
    {
        var due = new List<(DateTime At, bool Start, CalendarOccurrence Event)>();
        TimeSpan lead = TimeSpan.FromMinutes(LeadMinutes);
        foreach (CalendarOccurrence o in occurrences)
        {
            if (!Wants(o)) continue;
            DateTime start = o.Start - lead;
            if (AtStart is not null && start > from && start <= to) due.Add((start, true, o));
            if (AtEnd is not null && o.End > from && o.End <= to) due.Add((o.End, false, o));
        }
        return [.. due.OrderBy(d => d.At).ThenBy(d => d.Start).Select(d => (d.Start, d.Event))];
    }

    /// <summary>The calendar file read (null when it can't be: missing, too big, not a calendar).</summary>
    public static List<CalendarEvent>? Read(string? path)
    {
        if (path is null) return null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > IcsCalendar.MaxBytes) return null;
            string text = System.IO.File.ReadAllText(path);
            return text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase) ? IcsCalendar.Parse(text) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
