using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+P", "Ctrl+Alt+P")]
    [InlineData("ctrl + alt + p", "Ctrl+Alt+P")]
    [InlineData("Alt+Control+Shift+1", "Ctrl+Alt+Shift+1")]
    [InlineData("Ctrl+Alt+PageDown", "Ctrl+Alt+PgDn")]
    [InlineData("Ctrl+Alt+Back", "Ctrl+Alt+Backspace")]
    [InlineData("Win+Alt+F12", "Alt+Win+F12")]
    [InlineData("Ctrl+Alt+Num 5", "Ctrl+Alt+Num 5")]
    [InlineData("Ctrl+Alt+Space", "Ctrl+Alt+Space")]
    public void Parses_and_formats(string text, string expected)
    {
        Assert.True(Hotkey.TryParse(text, out Hotkey hotkey));
        Assert.Equal(expected, hotkey.ToString());
        Assert.True(Hotkey.TryParse(hotkey.ToString(), out Hotkey again));
        Assert.Equal(hotkey, again);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("P")]                 // no modifier
    [InlineData("Shift+P")]           // Shift alone would block typing
    [InlineData("Ctrl+Alt")]          // no key
    [InlineData("Ctrl+Alt+P+Q")]      // two keys
    [InlineData("Ctrl+Alt+Banana")]   // unknown key
    [InlineData("Ctrl+Alt+;")]        // layout-dependent punctuation isn't offered
    public void Rejects_unusable_shortcuts(string? text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void Digits_get_a_numpad_alias()
    {
        Assert.Equal(Keys.NumPad4, Hotkey.CtrlAlt(Keys.D4).NumpadAlias!.Value.Key);
        Assert.Null(Hotkey.CtrlAlt(Keys.P).NumpadAlias);
    }

    [Fact]
    public void Parts_are_keycap_labels() =>
        Assert.Equal(["Ctrl", "Alt", "Shift", "PgUp"], Hotkey.CtrlAlt(Keys.PageUp).WithShift().Parts);
}

public class ShortcutResolverTests
{
    private static readonly Dictionary<string, string> NoOverrides = [];
    private static bool NeverTypes(Hotkey _) => false;

    private static ShortcutBinding Find(List<ShortcutBinding> list, string id) => list.Single(b => b.Action.Id == id);

    [Fact]
    public void Catalog_defaults_are_valid_unique_and_avoid_risky_keys()
    {
        var defaults = ShortcutCatalog.All.Where(a => a.Default is not null).Select(a => a.Default!.Value).ToList();
        Assert.All(defaults, h => Assert.True(h.IsValid, h.ToString()));
        Assert.Equal(defaults.Count, defaults.Distinct().Count());
        // Ctrl+Alt+arrows rotate the screen on some laptops; Ctrl+Alt+Del is reserved.
        Assert.DoesNotContain(defaults, h => h.Key is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Delete);
        Assert.Equal(ShortcutCatalog.All.Count, ShortcutCatalog.All.Select(a => a.Id).Distinct().Count());
    }

    [Fact]
    public void Available_defaults_are_pending_and_later_features_are_not_registered()
    {
        var list = ShortcutResolver.Resolve(ShortcutCatalog.All, NoOverrides, enabled: true, paused: false, NeverTypes);
        Assert.Equal(ShortcutStatus.Pending, Find(list, "picker").Status);
        Assert.Equal(ShortcutStatus.Pending, Find(list, "record").Status); // available since M4
        Assert.Equal(ShortcutStatus.Pending, Find(list, "switch-output").Status); // available since M5
        Assert.DoesNotContain(ShortcutCatalog.All, a => !a.Available); // every feature has shipped

        // A future feature in the catalogue is listed but never registered.
        ShortcutAction later = new("later", "Coming later", ShortcutGroup.OnTheTv, Hotkey.CtrlAlt(Keys.F12), Available: false);
        Assert.Equal(ShortcutStatus.Unavailable, ShortcutResolver.Resolve([later], NoOverrides, true, false, NeverTypes).Single().Status);
        Assert.Equal(ShortcutStatus.Pending, Find(list, "freeze").Status);
        Assert.Equal(ShortcutStatus.Off, Find(list, "toggle-theme").Status); // no default
    }

    [Fact]
    public void User_can_switch_off_change_and_break_a_shortcut()
    {
        var overrides = new Dictionary<string, string> { ["blank"] = "", ["stop"] = "Ctrl+Alt+F9", ["dim"] = "nonsense" };
        var list = ShortcutResolver.Resolve(ShortcutCatalog.All, overrides, true, false, NeverTypes);
        Assert.Equal(ShortcutStatus.Off, Find(list, "blank").Status);
        Assert.Equal("Ctrl+Alt+F9", Find(list, "stop").Hotkey.ToString());
        Assert.Equal(ShortcutStatus.Invalid, Find(list, "dim").Status);
    }

    [Fact]
    public void AltGr_layout_moves_a_default_to_shift()
    {
        // Pretend Ctrl+Alt+4 types € (UK layout).
        bool Types(Hotkey h) => h == Hotkey.CtrlAlt(Keys.D4);
        var list = ShortcutResolver.Resolve(ShortcutCatalog.All, NoOverrides, true, false, Types);
        ShortcutBinding fav4 = Find(list, "favourite-4");
        Assert.Equal("Ctrl+Alt+Shift+4", fav4.Hotkey.ToString());
        Assert.StartsWith("Moved", fav4.Note);
        // ...which collides with "save favourite 4" (already Ctrl+Alt+Shift+4): the earlier action keeps it.
        Assert.Equal(ShortcutStatus.Taken, Find(list, "save-favourite-4").Status);
    }

    [Fact]
    public void Default_that_types_a_character_even_with_shift_is_switched_off()
    {
        bool Types(Hotkey h) => h.Key == Keys.D7; // e.g. German: Ctrl+Alt+7 = {, Ctrl+Alt+Shift+7 also maps
        var list = ShortcutResolver.Resolve(ShortcutCatalog.All, NoOverrides, true, false, Types);
        Assert.Equal(ShortcutStatus.Off, Find(list, "favourite-7").Status);
    }

    [Fact]
    public void Two_actions_with_the_same_keys_conflict()
    {
        var overrides = new Dictionary<string, string> { ["stop"] = "Ctrl+Alt+P" }; // same as picker
        var list = ShortcutResolver.Resolve(ShortcutCatalog.All, overrides, true, false, NeverTypes);
        Assert.Equal(ShortcutStatus.Pending, Find(list, "picker").Status);
        ShortcutBinding stop = Find(list, "stop");
        Assert.Equal(ShortcutStatus.Taken, stop.Status);
        Assert.Contains("Open the picker", stop.Note);
    }

    [Fact]
    public void Paused_keeps_only_the_resume_shortcut()
    {
        var list = ShortcutResolver.Resolve(ShortcutCatalog.All, NoOverrides, enabled: true, paused: true, NeverTypes);
        var pending = list.Where(b => b.Status == ShortcutStatus.Pending).Select(b => b.Action.Id).ToList();
        Assert.Equal([ShortcutCatalog.PauseShortcutsId], pending);
    }

    [Fact]
    public void Disabled_registers_nothing()
    {
        var list = ShortcutResolver.Resolve(ShortcutCatalog.All, NoOverrides, enabled: false, paused: false, NeverTypes);
        Assert.DoesNotContain(list, b => b.Status == ShortcutStatus.Pending);
    }
}
