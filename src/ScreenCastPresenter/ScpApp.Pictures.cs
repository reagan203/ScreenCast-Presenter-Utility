using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.UI;

namespace ScreenCastPresenter;

/// <summary>
/// Pictures and cards on the TV (FR-103): a picture file, or a few lines of words made into a card
/// on this PC, shown like a window (fades, zoom, marks, automatic hiding) without opening an app.
/// </summary>
internal sealed partial class ScpApp
{
    /// <summary>Where cards' pictures are kept, next to the settings file.</summary>
    private string CardsFolder => Path.Combine(Path.GetDirectoryName(SettingsFilePath) ?? ".", "Cards");

    /// <summary>"show-picture": choose a picture file and show it.</summary>
    private async Task ChoosePictureAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Show a picture on the TV",
            Filter = PictureSource.Filter,
            InitialDirectory = Settings.LastPictureFolder is { } last && Directory.Exists(last) ? last : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        Settings.LastPictureFolder = Path.GetDirectoryName(dialog.FileName);
        Save();
        await ShowPictureAsync(dialog.FileName);
    }

    /// <summary>Shows a picture file; false (and says why) when it can't be.</summary>
    public async Task<bool> ShowPictureAsync(string path, string? title = null)
    {
        if (PictureSource.Problem(path) is { } problem)
        {
            Message(problem, BadgeDot.Warning);
            return false;
        }
        SourceWindow picture = SourceWindow.ForPicture(path, title);
        await PresentAsync(picture);
        return _session.State == SessionState.Presenting && picture.IsSameSource(_session.Source);
    }

    /// <summary>"show-card": type a few lines and show them as a card.</summary>
    private async Task AskForCardAsync()
    {
        using var form = new FieldsForm("A card on the TV", "A few lines of words, made into a picture on this PC: a welcome, a notice, the Wi-Fi name. The first line is the title.", "Show it");
        (_, TextBox words) = form.AddField("Words", Settings.LastCard ?? "", TextCard.MaxLength, "Welcome to Acme" + Environment.NewLine + "We start at 9:30", lines: 4);
        form.Check = () => TextCard.Tidy(words.Text).Length == 0 ? "Type what the card says." : null;
        if (!form.Ask()) return;
        Settings.LastCard = TextCard.Tidy(words.Text);
        Save();
        await ShowCardAsync(words.Text);
    }

    /// <summary>Shows a card with <paramref name="text"/>; false (and says why) when it can't be.</summary>
    public async Task<bool> ShowCardAsync(string text)
    {
        string tidy = TextCard.Tidy(text);
        if (tidy.Length == 0)
        {
            Message("Type what the card says.");
            return false;
        }
        if (TextCard.Render(CardsFolder, tidy) is not { } file)
        {
            Message("The card couldn't be made: the folder next to the settings can't be written.", BadgeDot.Warning);
            return false;
        }
        return await ShowPictureAsync(file, TextCard.Title(tidy));
    }

    /// <summary>Cards no playlist, scene or favourite uses any more are deleted (they're made again when needed).</summary>
    private void TidyCards()
    {
        try
        {
            if (!Directory.Exists(CardsFolder)) return;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PlaylistItem item in Settings.Playlists.SelectMany(p => p.Items).Where(i => i.IsCard && i.Text is not null))
                used.Add(TextCard.PathFor(CardsFolder, item.Text!));
            foreach (Favourite f in Settings.Favourites.OfType<Favourite>().Concat(Settings.Scenes.OfType<Scene>().Select(s => s.Source).OfType<Favourite>()))
                if (f.PicturePath is { } p) used.Add(p);
            if (Settings.LastCard is { } last) used.Add(TextCard.PathFor(CardsFolder, last));
            if (_session.Source?.Picture is { } shown) used.Add(shown);
            foreach (string file in Directory.EnumerateFiles(CardsFolder, "card-*.png"))
                if (!used.Contains(file)) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag("cards: " + ex.Message); // tidied next time
        }
    }
}
