using System.Text.Json;
using Microsoft.Win32;

namespace ScreenCastPresenter.Core;

/// <summary>
/// Settings for IT teams (FR-109): rules set by the organisation that the presenter can't change,
/// from Group Policy or a deployment script, in the registry under
/// <c>HKEY_LOCAL_MACHINE\SOFTWARE\Policies\ScreenCastPresenter</c> (for every user of the PC) or
/// the same under <c>HKEY_CURRENT_USER</c>; the machine's rules win. Values:
/// <list type="bullet">
/// <item><c>NeverShowApps</c> (multi-string): added to everyone's never-show list, can't be removed.</item>
/// <item><c>AutoHide</c> (DWORD, the kinds' bits): always hidden automatically.</item>
/// <item><c>CheckBeforeShowing</c> (DWORD 1): always on.</item>
/// <item><c>AllowPhoneRemote</c>, <c>AllowRecording</c>, <c>AllowScreenshots</c>, <c>AllowReplay</c>,
/// <c>AllowUpdateCheck</c>, <c>AllowVoiceCommands</c> (DWORD 0): switched off and can't be switched on.</item>
/// </list>
/// Pure apart from reading the registry, so every rule is tested.
/// </summary>
internal sealed record PolicySet
{
    public const string KeyPath = @"SOFTWARE\Policies\ScreenCastPresenter";

    public IReadOnlyList<string> NeverShowApps { get; init; } = [];
    public SensitiveKinds AutoHide { get; init; }
    public bool CheckBeforeShowing { get; init; }
    public bool PhoneRemoteAllowed { get; init; } = true;
    public bool RecordingAllowed { get; init; } = true;
    public bool ScreenshotsAllowed { get; init; } = true;
    public bool ReplayAllowed { get; init; } = true;
    public bool UpdateCheckAllowed { get; init; } = true;
    public bool VoiceCommandsAllowed { get; init; } = true;

    public static PolicySet None { get; } = new();

    /// <summary>Any rule at all is set.</summary>
    public bool Any => this != None && (NeverShowApps.Count > 0 || AutoHide != SensitiveKinds.None || CheckBeforeShowing
        || !PhoneRemoteAllowed || !RecordingAllowed || !ScreenshotsAllowed || !ReplayAllowed || !UpdateCheckAllowed || !VoiceCommandsAllowed);

    /// <summary>The rules from registry values (machine first, then the user's for what the machine leaves unset).</summary>
    public static PolicySet From(IReadOnlyDictionary<string, object?>? machine, IReadOnlyDictionary<string, object?>? user)
    {
        object? Get(string name) => machine is not null && machine.TryGetValue(name, out object? m) && m is not null ? m
            : user is not null && user.TryGetValue(name, out object? u) ? u : null;
        bool Allowed(string name) => Get(name) is not int value || value != 0;
        IEnumerable<string> Apps(IReadOnlyDictionary<string, object?>? from) =>
            from is not null && from.TryGetValue("NeverShowApps", out object? v) ? v switch { string[] list => list, string one => one.Split(';', ','), _ => [] } : [];
        return new PolicySet
        {
            NeverShowApps = NeverShow.Normalize([.. Apps(machine), .. Apps(user)]),
            AutoHide = Get("AutoHide") is int kinds ? (SensitiveKinds)kinds & SensitiveKinds.Known : SensitiveKinds.None,
            CheckBeforeShowing = Get("CheckBeforeShowing") is int check && check != 0,
            PhoneRemoteAllowed = Allowed("AllowPhoneRemote"),
            RecordingAllowed = Allowed("AllowRecording"),
            ScreenshotsAllowed = Allowed("AllowScreenshots"),
            ReplayAllowed = Allowed("AllowReplay"),
            UpdateCheckAllowed = Allowed("AllowUpdateCheck"),
            VoiceCommandsAllowed = Allowed("AllowVoiceCommands"),
        };
    }

    /// <summary>The rules in the registry now (<paramref name="path"/> lets tests use their own key).</summary>
    public static PolicySet Read(string path = KeyPath)
    {
        try
        {
            using RegistryKey? machine = Registry.LocalMachine.OpenSubKey(path);
            using RegistryKey? user = Registry.CurrentUser.OpenSubKey(path);
            return From(Values(machine), Values(user));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return None;
        }
    }

    private static Dictionary<string, object?>? Values(RegistryKey? key) =>
        key?.GetValueNames().ToDictionary(n => n, n => key.GetValue(n), StringComparer.OrdinalIgnoreCase);

    /// <summary>The settings made to follow the rules (what's forced on is on; what's not allowed is off).</summary>
    public AppSettings Apply(AppSettings s)
    {
        if (NeverShowApps.Count > 0) s.NeverShowApps = NeverShow.Normalize([.. s.NeverShowApps, .. NeverShowApps]);
        if (AutoHide != SensitiveKinds.None) s.AutoHideKinds |= AutoHide;
        if (CheckBeforeShowing) s.CheckBeforeShowing = true;
        if (!PhoneRemoteAllowed) s.PhoneRemote = false;
        if (!ReplayAllowed) s.ReplayOn = false;
        if (!UpdateCheckAllowed) s.CheckForUpdates = false;
        if (!VoiceCommandsAllowed) s.VoiceCommands = false;
        return s;
    }

    /// <summary>The never-show app <paramref name="app"/> is on the list because of a rule (it can't be removed).</summary>
    public bool KeepsNeverShow(string app) => NeverShowApps.Contains(app, StringComparer.OrdinalIgnoreCase);

    /// <summary>"Your organisation …" lines for Settings.</summary>
    public List<string> Words()
    {
        var lines = new List<string>();
        if (NeverShowApps.Count > 0) lines.Add($"Never shown: {string.Join(", ", NeverShowApps)}.");
        if (AutoHide != SensitiveKinds.None) lines.Add("Some details are always hidden automatically.");
        if (CheckBeforeShowing) lines.Add("Everything is checked before it's shown.");
        var off = new List<string>();
        if (!PhoneRemoteAllowed) off.Add("the phone remote");
        if (!RecordingAllowed) off.Add("recording");
        if (!ScreenshotsAllowed) off.Add("screenshots");
        if (!ReplayAllowed) off.Add("keeping the last minute");
        if (!UpdateCheckAllowed) off.Add("checking for updates");
        if (!VoiceCommandsAllowed) off.Add("voice commands");
        if (off.Count > 0) lines.Add($"Turned off: {string.Join(", ", off)}.");
        return lines;
    }
}

/// <summary>
/// Settings to a file and back (FR-109): to set up several PCs the same way, or move to a new one.
/// What belongs to this PC or this person's Windows account isn't carried: the phone remote's key
/// (it's encrypted for this account), window positions and the screens' names.
/// </summary>
internal static class SettingsTransfer
{
    /// <summary>A settings file bigger than this isn't one.</summary>
    public const long MaxBytes = 2 * 1024 * 1024;

    public static string Export(AppSettings settings)
    {
        AppSettings copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings), SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        copy.RemoteKey = null;
        copy.FloatingWindow = null;
        copy.OutputDevice = null;
        return JsonSerializer.Serialize(copy, SettingsJsonContext.Default.AppSettings);
    }

    /// <summary>Settings read from an exported file, made sensible; null (with why) when it isn't one.</summary>
    public static (AppSettings? Settings, string? Problem) Import(string json, AppSettings current)
    {
        AppSettings? read;
        try
        {
            read = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);
        }
        catch (JsonException)
        {
            return (null, "That file isn't ScreenCast Presenter's settings.");
        }
        if (read is null) return (null, "That file is empty.");
        read.RemoteKey = current.RemoteKey; // this PC's own
        read.FloatingWindow = current.FloatingWindow;
        read.OutputDevice = current.OutputDevice;
        read.FirstRunDone = true;
        return (read.Normalize(), null);
    }
}
