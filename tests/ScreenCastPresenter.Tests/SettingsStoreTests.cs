using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scp-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public SettingsStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (string f in Directory.EnumerateFiles(_dir)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Missing_file_gives_defaults_without_a_problem()
    {
        var store = new SettingsStore(FilePath);
        AppSettings s = store.Load();
        Assert.Null(store.LoadProblem);
        Assert.True(s.ShowOutline);
        Assert.Equal(ThemeMode.System, s.ThemeMode);
        Assert.Equal(9, s.Favourites.Length);
    }

    [Fact]
    public void Save_then_load_round_trips_and_leaves_no_temp_file()
    {
        var store = new SettingsStore(Path.Combine(_dir, "nested", "settings.json")); // folder doesn't exist yet
        store.Load();
        store.Current.Dim = 0.2;
        store.Current.ThemeMode = ThemeMode.Light;
        store.Current.Shortcuts["blank"] = "Ctrl+Alt+Shift+B";
        store.Current.Favourites[2] = new Favourite { AppName = "POWERPNT", Title = "Deck" };
        Assert.True(store.Save());

        var again = new SettingsStore(store.FilePath);
        AppSettings s = again.Load();
        Assert.Equal(0.2, s.Dim, 6);
        Assert.Equal(ThemeMode.Light, s.ThemeMode);
        Assert.Equal("Ctrl+Alt+Shift+B", s.Shortcuts["blank"]);
        Assert.Equal("Deck", s.Favourites[2]!.Title);
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"Dim\": \"lots\"}")]
    public void Corrupt_file_is_backed_up_and_defaults_load(string content)
    {
        File.WriteAllText(FilePath, content);
        var store = new SettingsStore(FilePath);
        AppSettings s = store.Load();

        Assert.NotNull(store.LoadProblem);
        Assert.Equal(0, s.Dim);
        Assert.False(File.Exists(FilePath));
        Assert.Single(Directory.GetFiles(_dir, "settings.corrupt-*.json"));
    }

    [Fact]
    public void Random_bytes_never_throw()
    {
        var random = new Random(1234);
        for (int i = 0; i < 200; i++)
        {
            byte[] bytes = new byte[random.Next(0, 2000)];
            random.NextBytes(bytes);
            File.WriteAllBytes(FilePath, bytes);
            var store = new SettingsStore(FilePath);
            AppSettings s = store.Load(); // must not throw
            Assert.NotNull(s);
        }
    }

    [Fact]
    public void Huge_file_is_rejected()
    {
        File.WriteAllText(FilePath, "{\"BlankMessage\":\"" + new string('x', (int)SettingsStore.MaxFileBytes) + "\"}");
        var store = new SettingsStore(FilePath);
        store.Load();
        Assert.NotNull(store.LoadProblem);
    }

    [Fact]
    public void Newer_version_fields_and_values_are_tolerated()
    {
        File.WriteAllText(FilePath, """
        {
          "Version": 7,
          "Theme": "Purple",
          "SomethingNew": { "a": 1 },
          "Dim": 3.5,
          "Favourites": [ { "AppName": "code", "Title": "x" } ],
          "BlankMessage": "   "
        }
        """);
        var store = new SettingsStore(FilePath);
        AppSettings s = store.Load();

        Assert.Null(store.LoadProblem);
        Assert.Equal(ThemeMode.System, s.ThemeMode);          // unknown theme → default
        Assert.Equal(AppSettings.MaxDim, s.Dim);              // clamped
        Assert.Equal(AppSettings.SlotCount, s.Favourites.Length); // padded to 9
        Assert.Equal("code", s.Favourites[0]!.AppName);
        Assert.Equal("We'll be right back", s.BlankMessage);  // blank → default
    }

    [Fact]
    public void Comments_and_trailing_commas_are_accepted()
    {
        File.WriteAllText(FilePath, "{ // hand edited\n \"ShowOutline\": false, }");
        var store = new SettingsStore(FilePath);
        Assert.False(store.Load().ShowOutline);
        Assert.Null(store.LoadProblem);
    }

    [Fact]
    public void Read_only_file_reports_a_save_problem_instead_of_throwing()
    {
        var store = new SettingsStore(FilePath);
        store.Load();
        Assert.True(store.Save());
        File.SetAttributes(FilePath, FileAttributes.ReadOnly);

        store.Current.Dim = 0.1;
        Assert.False(store.Save());
        Assert.NotNull(store.SaveProblem);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Long_message_is_trimmed()
    {
        var s = new AppSettings { BlankMessage = new string('a', 500) }.Normalize();
        Assert.Equal(AppSettings.MaxMessageLength, s.BlankMessage.Length);
    }

    [Fact]
    public void NaN_dim_becomes_zero()
    {
        Assert.Equal(0, new AppSettings { Dim = double.NaN }.Normalize().Dim);
    }
}
