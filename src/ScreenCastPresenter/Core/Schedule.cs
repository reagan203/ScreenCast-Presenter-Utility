using System.Globalization;

namespace ScreenCastPresenter.Core;

/// <summary>What a schedule entry does (FR-83).</summary>
internal enum ScheduleAction
{
    /// <summary>Start the favourites slideshow on the TV.</summary>
    StartSlideshow,

    /// <summary>Stop the slideshow (what's showing stays).</summary>
    StopSlideshow,

    /// <summary>Show one favourite (<see cref="ScheduleEntry.Slot"/>).</summary>
    ShowFavourite,

    /// <summary>Blank the TV.</summary>
    Blank,

    /// <summary>Show the picture again after a blank.</summary>
    ShowAgain,

    /// <summary>Stop showing anything (the TV goes back to what it was).</summary>
    Stop,

    /// <summary>Set things up for a room (<see cref="ScheduleEntry.Room"/>).</summary>
    Room,

    /// <summary>Start a playlist (<see cref="ScheduleEntry.Playlist"/>) on its TV (FR-87).</summary>
    StartPlaylist,

    /// <summary>Stop the playlists.</summary>
    StopPlaylist,

    /// <summary>Show a scene (<see cref="ScheduleEntry.Slot"/>, FR-86).</summary>
    Scene,
}

/// <summary>
/// One thing done at a set time on chosen days (FR-83), e.g. "weekdays at 8:00, start the
/// slideshow". Times are the PC's local time. Kept as text in the settings file so it reads well.
/// </summary>
internal sealed class ScheduleEntry
{
    public const int Max = 20;

    public bool Enabled { get; set; } = true;

    /// <summary>Three-letter day names, e.g. "Mon,Tue,Wed,Thu,Fri"; all seven for every day.</summary>
    public string Days { get; set; } = "Mon,Tue,Wed,Thu,Fri";

    /// <summary>"HH:mm", 24-hour.</summary>
    public string Time { get; set; } = "08:00";

    public string Action { get; set; } = nameof(ScheduleAction.StartSlideshow);

    /// <summary>For <see cref="ScheduleAction.ShowFavourite"/>: the favourite, 1–9.</summary>
    public int Slot { get; set; } = 1;

    /// <summary>For <see cref="ScheduleAction.Room"/>: the room's name.</summary>
    public string? Room { get; set; }

    /// <summary>For <see cref="ScheduleAction.StartPlaylist"/>: the playlist's name.</summary>
    public string? Playlist { get; set; }

    public static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    public ScheduleAction What => Enum.TryParse(Action, ignoreCase: true, out ScheduleAction a) && Enum.IsDefined(a) ? a : ScheduleAction.StartSlideshow;

    /// <summary>The time of day, or null when it isn't a valid "HH:mm".</summary>
    public TimeSpan? At => ParseTime(Time);

    public HashSet<DayOfWeek> DaySet => ParseDays(Days);

    public static TimeSpan? ParseTime(string? text) =>
        text is not null && TimeSpan.TryParseExact(text.Trim(), ["h\\:mm", "hh\\:mm"], CultureInfo.InvariantCulture, out TimeSpan t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1)
            ? t : null;

    public static HashSet<DayOfWeek> ParseDays(string? text)
    {
        var days = new HashSet<DayOfWeek>();
        foreach (string part in (text ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            int i = Array.FindIndex(DayNames, d => part.StartsWith(d, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) days.Add((DayOfWeek)i);
        }
        return days;
    }

    public static string DaysText(IEnumerable<DayOfWeek> days) => string.Join(",", days.Distinct().OrderBy(d => ((int)d + 6) % 7).Select(d => DayNames[(int)d]));

    /// <summary>"Weekdays", "Every day", "Weekends" or "Mon, Wed".</summary>
    public static string DaysWords(IReadOnlySet<DayOfWeek> days)
    {
        if (days.Count == 7) return "Every day";
        if (days.Count == 5 && !days.Contains(DayOfWeek.Saturday) && !days.Contains(DayOfWeek.Sunday)) return "Weekdays";
        if (days.Count == 2 && days.Contains(DayOfWeek.Saturday) && days.Contains(DayOfWeek.Sunday)) return "Weekends";
        return days.Count == 0 ? "Never" : DaysText(days).Replace(",", ", ");
    }

    /// <summary>"Weekdays at 08:00: start the slideshow".</summary>
    public string Words(Func<int, string>? favouriteName = null) => $"{DaysWords(DaySet)} at {Time}: {WhatWords(favouriteName)}";

    /// <summary>"start the slideshow", "show favourite 2", "set up for “Lobby”".</summary>
    public string WhatWords(Func<int, string>? favouriteName = null) => What switch
    {
        ScheduleAction.StartSlideshow => "start the slideshow",
        ScheduleAction.StopSlideshow => "stop the slideshow",
        ScheduleAction.ShowFavourite => "show " + (favouriteName?.Invoke(Slot) ?? $"favourite {Slot}"),
        ScheduleAction.Blank => "blank the TV",
        ScheduleAction.ShowAgain => "show the picture again",
        ScheduleAction.Stop => "stop showing",
        ScheduleAction.Room => $"set up for “{Room}”",
        ScheduleAction.StartPlaylist => $"play “{Playlist}”",
        ScheduleAction.StopPlaylist => "stop the playlists",
        ScheduleAction.Scene => $"show scene {Slot}",
        _ => "",
    };

    /// <summary>Entries from a settings file made sensible: valid times and days, at most <see cref="Max"/>.</summary>
    public static List<ScheduleEntry> NormalizeAll(List<ScheduleEntry>? entries)
    {
        var result = new List<ScheduleEntry>();
        foreach (ScheduleEntry? e in entries ?? [])
        {
            if (e?.At is not { } at) continue; // a hand-edited file can hold nulls or nonsense
            e.Time = at.ToString("hh\\:mm", CultureInfo.InvariantCulture);
            e.Days = DaysText(e.DaySet);
            e.Action = e.What.ToString();
            e.Slot = Math.Clamp(e.Slot, 1, AppSettings.SlotCount);
            e.Room = e.What == ScheduleAction.Room ? RoomProfile.TidyName(e.Room) : null;
            if (e.What == ScheduleAction.Room && e.Room!.Length == 0) continue;
            e.Playlist = e.What == ScheduleAction.StartPlaylist ? RoomProfile.TidyName(e.Playlist) : null;
            if (e.What == ScheduleAction.StartPlaylist && e.Playlist!.Length == 0) continue;
            if (e.DaySet.Count == 0) continue;
            result.Add(e);
            if (result.Count == Max) break;
        }
        return result;
    }
}

/// <summary>
/// When schedule entries are due (FR-83). Checked every <see cref="CheckEvery"/>: an entry is due
/// when its time passed since the last check. If the PC slept or the app wasn't running, an entry
/// missed by more than <see cref="CatchUp"/> is skipped rather than done late (a lobby slideshow
/// starting at 14:00 because the PC woke up then would surprise people).
/// </summary>
internal static class Scheduler
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan CatchUp = TimeSpan.FromMinutes(5);

    /// <summary>Entries due in (<paramref name="since"/>, <paramref name="now"/>], oldest first.</summary>
    public static List<ScheduleEntry> Due(IEnumerable<ScheduleEntry> entries, DateTime since, DateTime now)
    {
        var due = new List<(DateTime When, int Order, ScheduleEntry Entry)>();
        if (now <= since) return []; // the clock went back: nothing is due twice
        DateTime earliest = now - CatchUp > since ? now - CatchUp : since;
        int order = 0;
        foreach (ScheduleEntry e in entries)
        {
            order++;
            if (!e.Enabled || e.At is not { } at) continue;
            HashSet<DayOfWeek> days = e.DaySet;
            for (DateTime day = earliest.Date; day <= now.Date; day = day.AddDays(1))
            {
                DateTime when = day + at;
                if (when > earliest && when <= now && days.Contains(day.DayOfWeek)) due.Add((when, order, e));
            }
        }
        return [.. due.OrderBy(d => d.When).ThenBy(d => d.Order).Select(d => d.Entry)];
    }

    /// <summary>The next time anything is due after <paramref name="now"/> (within a week), for "next: Mon 08:00".</summary>
    public static (DateTime When, ScheduleEntry Entry)? Next(IEnumerable<ScheduleEntry> entries, DateTime now)
    {
        (DateTime When, ScheduleEntry Entry)? best = null;
        foreach (ScheduleEntry e in entries)
        {
            if (!e.Enabled || e.At is not { } at) continue;
            HashSet<DayOfWeek> days = e.DaySet;
            for (int i = 0; i <= 7; i++)
            {
                DateTime when = now.Date.AddDays(i) + at;
                if (when <= now || !days.Contains(when.DayOfWeek)) continue;
                if (best is null || when < best.Value.When) best = (when, e);
                break;
            }
        }
        return best;
    }
}

/// <summary>What the schedule's "Add" form offers (FR-83).</summary>
internal static class ScheduleChoices
{
    /// <summary>Common day sets first, then each day on its own.</summary>
    public static IReadOnlyList<(string Words, string Days)> Days { get; } =
    [
        ("Weekdays (Monday to Friday)", "Mon,Tue,Wed,Thu,Fri"),
        ("Every day", "Mon,Tue,Wed,Thu,Fri,Sat,Sun"),
        ("Weekends", "Sat,Sun"),
        ("Mondays", "Mon"), ("Tuesdays", "Tue"), ("Wednesdays", "Wed"), ("Thursdays", "Thu"), ("Fridays", "Fri"), ("Saturdays", "Sat"), ("Sundays", "Sun"),
    ];

    /// <summary>Everything a scheduled entry can do, with the favourites and rooms there are now.</summary>
    public static IReadOnlyList<(string Words, ScheduleAction Action, int Slot, string? Room)> Actions(IReadOnlyList<Favourite?> favourites, IReadOnlyList<RoomProfile> rooms)
    {
        var list = new List<(string, ScheduleAction, int, string?)>
        {
            ("Start the slideshow of favourites", ScheduleAction.StartSlideshow, 1, null),
            ("Stop the slideshow", ScheduleAction.StopSlideshow, 1, null),
            ("Blank the TV (with its message)", ScheduleAction.Blank, 1, null),
            ("Show the picture again", ScheduleAction.ShowAgain, 1, null),
            ("Stop showing", ScheduleAction.Stop, 1, null),
        };
        for (int i = 0; i < favourites.Count; i++)
        {
            if (favourites[i] is { } f) list.Add(($"Show favourite {i + 1}: {TrayMenuPlan.Shorten(f.Title, 36)}", ScheduleAction.ShowFavourite, i + 1, null));
        }
        foreach (RoomProfile room in rooms) list.Add(($"Set up for “{room.Name}”", ScheduleAction.Room, 1, room.Name));
        return list;
    }

    /// <summary>The same, with scenes and playlists (the room name field carries a playlist's name for <see cref="ScheduleAction.StartPlaylist"/>).</summary>
    public static IReadOnlyList<(string Words, ScheduleAction Action, int Slot, string? Room)> Actions(IReadOnlyList<Favourite?> favourites, IReadOnlyList<RoomProfile> rooms,
        IReadOnlyList<Scene?> scenes, IReadOnlyList<Playlist> playlists)
    {
        var list = Actions(favourites, rooms).ToList();
        for (int i = 0; i < scenes.Count; i++)
        {
            if (scenes[i] is { } scene) list.Add(($"Show scene {i + 1}: {TrayMenuPlan.Shorten(scene.Title(i + 1), 36)}", ScheduleAction.Scene, i + 1, null));
        }
        foreach (Playlist p in playlists) list.Add(($"Play “{p.Name}”", ScheduleAction.StartPlaylist, 1, p.Name));
        if (playlists.Count > 0) list.Add(("Stop the playlists", ScheduleAction.StopPlaylist, 1, null));
        return list;
    }
}
