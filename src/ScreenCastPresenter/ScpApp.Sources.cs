using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.UI;

namespace ScreenCastPresenter;

/// <summary>
/// More things to show (SRS 3.12, rev 10): a PDF page by page (FR-115), a video with its sound
/// (FR-116), a whiteboard to draw on (FR-117) and the room sign (FR-125). Each is shown through the
/// capture helper like a picture, with fades, marks, automatic hiding and recordings.
/// </summary>
internal sealed partial class ScpApp
{
    /// <summary>Where made pictures that needn't roam with the settings are kept (PDF pages, the board, the room sign).</summary>
    private static string LocalFolder(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenCastPresenter", name);

    // ───────────────────────────── PDFs (FR-115) ─────────────────────────────

    private static string PagesRoot => LocalFolder("Pages");

    /// <summary>How many pages each PDF has, once its pages have been made (by its pages folder).</summary>
    private readonly Dictionary<string, int> _pdfPageCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pages being made ahead in the background, by folder (one helper at a time per PDF).</summary>
    private readonly HashSet<string> _pdfAhead = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many pages a PDF has, once its pages have been made; 0 until then.</summary>
    private int PdfPageCount(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? _pdfPageCounts.GetValueOrDefault(PdfPages.FolderFor(PagesRoot, path, info.Length, info.LastWriteTimeUtc)) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0;
        }
    }

    /// <summary>"show-pdf": choose a PDF and show its first page.</summary>
    private async Task ChooseDocumentAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Show a PDF on the TV",
            Filter = PdfPages.Filter,
            InitialDirectory = Settings.LastDocumentFolder is { } last && Directory.Exists(last) ? last : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        Settings.LastDocumentFolder = Path.GetDirectoryName(dialog.FileName);
        Save();
        await ShowDocumentAsync(dialog.FileName, 1);
    }

    /// <summary>Shows page <paramref name="page"/> of a PDF; false (and says why) when it can't be.</summary>
    public async Task<bool> ShowDocumentAsync(string path, int page)
    {
        if (PdfPages.Problem(path) is { } problem)
        {
            Message(problem, BadgeDot.Warning);
            return false;
        }
        if (await DocumentPageAsync(path, page, quiet: false) is not { } source) return false;
        await PresentAsync(source);
        bool shown = _session.State == SessionState.Presenting && source.IsSameSource(_session.Source);
        if (shown && page == 1 && !_toldPdf)
        {
            _toldPdf = true;
            Message($"“{PdfPages.Name(path)}” is on {AudienceScreenName}. Next and previous slide turn the page{OrPress("next-slide")}.");
        }
        return shown;
    }

    private bool _toldPdf;

    /// <summary>
    /// A PDF's page as something to show: made into a picture first if it hasn't been (the next pages
    /// are made meanwhile, in the background). Null (and said, unless <paramref name="quiet"/>) when it can't be.
    /// </summary>
    private async Task<SourceWindow?> DocumentPageAsync(string path, int page, bool quiet)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (!quiet) Message($"“{Path.GetFileName(path)}” can't be found.", BadgeDot.Warning);
            return null;
        }
        string folder = PdfPages.FolderFor(PagesRoot, path, info.Length, info.LastWriteTimeUtc);
        int pages = _pdfPageCounts.GetValueOrDefault(folder);
        page = PdfPages.Clamp(page, pages);
        string file = PdfPages.PagePath(folder, page);
        if (!File.Exists(file) || pages == 0)
        {
            TidyPages(folder);
            (int count, IReadOnlyList<int> done, string? problem) = await PdfRenderer.RenderAsync(path, folder, PdfPages.ToMake(page, pages), TimeSpan.FromSeconds(30));
            if (count > 0) _pdfPageCounts[folder] = pages = count;
            if (pages > 0 && page > pages)
            {
                page = pages;
                file = PdfPages.PagePath(folder, page);
                if (!File.Exists(file)) (_, _, problem) = await PdfRenderer.RenderAsync(path, folder, [page], TimeSpan.FromSeconds(30));
            }
            if (!File.Exists(file))
            {
                if (!quiet) Message(problem ?? $"Page {page} of “{PdfPages.Name(path)}” couldn't be made into a picture.", BadgeDot.Warning);
                return null;
            }
        }
        else MakePagesAhead(path, folder, page, pages);
        return SourceWindow.ForPicture(file, PdfPages.Title(path, page, pages)) with { Document = path, Page = page, Pages = pages };
    }

    /// <summary>The next pages made before they're asked for, so turning the page is quick.</summary>
    private void MakePagesAhead(string path, string folder, int page, int pages)
    {
        List<int> missing = [.. PdfPages.ToMake(page, pages).Where(p => !File.Exists(PdfPages.PagePath(folder, p)))];
        if (missing.Count == 0 || !_pdfAhead.Add(folder)) return;
        _ = GuardAsync(async () =>
        {
            try
            {
                (int count, _, _) = await PdfRenderer.RenderAsync(path, folder, missing, TimeSpan.FromSeconds(30));
                if (count > 0) _pdfPageCounts[folder] = count;
            }
            finally
            {
                _pdfAhead.Remove(folder);
            }
        });
    }

    /// <summary>Only the newest few PDFs' pages are kept (the folder of the one being shown never goes).</summary>
    private static void TidyPages(string keep)
    {
        try
        {
            if (!Directory.Exists(PagesRoot)) return;
            foreach (DirectoryInfo old in new DirectoryInfo(PagesRoot).GetDirectories("pdf-*").Where(d => !string.Equals(d.FullName, keep, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(d => d.LastWriteTimeUtc).Skip(PdfPages.KeptDocuments - 1))
                old.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag("pdf pages: " + ex.Message); // tidied next time
        }
    }

    /// <summary>Next or previous slide while a PDF is shown: the page turns. False when what's shown isn't a PDF.</summary>
    private async Task<bool> TurnPageAsync(int by)
    {
        if (_session.State != SessionState.Presenting || _session.Source is not { IsDocument: true } shown) return false;
        if (PdfPages.Turn(shown.Page, shown.Pages, by) is not { } next)
        {
            Message(by > 0 ? "That's the last page." : "That's the first page.");
            return true;
        }
        if (await DocumentPageAsync(shown.Document!, next, quiet: false) is { } page)
        {
            _turningPage = true;
            try
            {
                await PresentAsync(page);
            }
            finally
            {
                _turningPage = false;
            }
        }
        return true;
    }

    /// <summary>A page is being turned (not the presenter choosing something new).</summary>
    private bool _turningPage;

    // ───────────────────────────── videos (FR-116) ─────────────────────────────

    /// <summary>"show-video": choose a video and play it on the TV.</summary>
    private async Task ChooseVideoAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Show a video on the TV",
            Filter = VideoSource.Filter,
            InitialDirectory = Settings.LastVideoFolder is { } last && Directory.Exists(last) ? last : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        Settings.LastVideoFolder = Path.GetDirectoryName(dialog.FileName);
        Save();
        await ShowVideoAsync(dialog.FileName);
    }

    /// <summary>Plays a video on the TV; false (and says why) when it can't be.</summary>
    public async Task<bool> ShowVideoAsync(string path, string? title = null)
    {
        if (VideoSource.Problem(path) is { } problem)
        {
            Message(problem, BadgeDot.Warning);
            return false;
        }
        SourceWindow video = SourceWindow.ForVideo(path, title);
        await PresentAsync(video);
        bool shown = _session.State == SessionState.Presenting && video.IsSameSource(_session.Source);
        if (shown && !_playlistSwitching)
            Message($"“{video.Title}” plays on {AudienceScreenName} with its sound. Freeze or blank to pause it; next and previous slide jump {VideoSource.JumpSeconds:0} seconds.", BadgeDot.Accent,
                Button("Pause", () => VideoCommandNow(VideoCommand.Pause), primary: false), Button("Start again", () => VideoCommandNow(VideoCommand.Restart), primary: false));
        return shown;
    }

    private void VideoCommandNow(VideoCommand command, double seconds = 0)
    {
        if (_session.Source is not { IsVideo: true }) return;
        _session.Effects?.Video(command, seconds);
    }

    /// <summary>Next or previous slide while a video is shown: it jumps. False when what's shown isn't a video.</summary>
    private bool JumpVideo(int by)
    {
        if (_session.State != SessionState.Presenting || _session.Source is not { IsVideo: true }) return false;
        VideoCommandNow(VideoCommand.Seek, by * VideoSource.JumpSeconds);
        return true;
    }

    /// <summary>The video ended or couldn't play: said, and a playlist moves on.</summary>
    private void OnVideoEvent(object? sender, string what)
    {
        if (_session.Source is not { IsVideo: true } video) return;
        if (what == "ended")
        {
            if (_playing.TryGetValue(PlaylistScreen.Main, out PlaylistRun? run))
            {
                _ = GuardAsync(() => OnPlaylistTickAsync(run)); // a playlist goes on to the next item
                return;
            }
            Message($"“{video.Title}” has ended. Its last picture stays on {AudienceScreenName}.", BadgeDot.Accent,
                Button("Play it again", () => VideoCommandNow(VideoCommand.Restart)), Button("Stop showing", StopShowingAsync, primary: false));
            return;
        }
        if (what.StartsWith("failed ", StringComparison.Ordinal))
        {
            Message($"“{video.Title}” couldn't be played: {what[7..]}", BadgeDot.Warning);
            if (_playing.TryGetValue(PlaylistScreen.Main, out PlaylistRun? run)) _ = GuardAsync(() => OnPlaylistTickAsync(run));
        }
    }

    // ───────────────────────────── the whiteboard (FR-117) ─────────────────────────────

    private BoardWindow? _board;

    /// <summary>The board's drawing: kept between drawing sessions while the board is out.</summary>
    private InkDrawing? _boardInk;

    /// <summary>What was shown before the board, to go back to when it's put away.</summary>
    private SourceWindow? _beforeBoard;

    public bool BoardOut => _board is { IsDisposed: false };

    /// <summary>"show-whiteboard": a blank board on the TV, drawn on from a private window. Again puts it away.</summary>
    private async Task ToggleWhiteboardAsync()
    {
        if (BoardOut && _session.Source is { IsBoard: true })
        {
            PutBoardAway();
            return;
        }
        if (Whiteboard.Render(LocalFolder("Board"), Settings.WhiteboardLight) is not { } file)
        {
            Message("The whiteboard couldn't be made: a folder on this PC can't be written.", BadgeDot.Warning);
            return;
        }
        _beforeBoard = _session.State == SessionState.Presenting && _session.Source is { IsBoard: false } before ? before : null;
        SourceWindow board = SourceWindow.ForPicture(file, "Whiteboard") with { IsBoard = true };
        await PresentAsync(board);
        if (_session.State != SessionState.Presenting || _session.Source is not { IsBoard: true }) return;
        _boardInk ??= new InkDrawing();
        OpenBoardWindow();
        StartDrawingOnBoard();
        Message($"Draw on the board in its window: the audience sees it on {AudienceScreenName} as you draw. It stays until you clear it or put the board away.", BadgeDot.Accent);
    }

    private void OpenBoardWindow()
    {
        if (BoardOut)
        {
            _board!.Activate();
            return;
        }
        var window = new BoardWindow(this, () => (IReadOnlyList<InkStroke>?)_boardInk?.Strokes ?? [], Settings.WhiteboardLight);
        CentreOnPresenterScreen(window);
        window.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_board, window)) _board = null;
            ScheduleTrim();
        };
        _board = window;
        window.Show();
    }

    /// <summary>Drawing on the board: the drawing layer covers the board window's board.</summary>
    private void StartDrawingOnBoard()
    {
        if (_drawing is not null || !BoardOut || _boardInk is null) return;
        ToggleDrawing();
    }

    /// <summary>The board window moved or changed size: the drawing layer follows.</summary>
    public void BoardMoved()
    {
        if (_drawing is not null && _session.Source is { IsBoard: true }) _drawingOverlay?.UpdateGeometry();
    }

    /// <summary>The board's "Clear": everything drawn goes, on the TV too.</summary>
    public void ClearBoard()
    {
        _boardInk?.Clear();
        _session.Effects?.InkClear();
        _board?.ShowDrawing(_drawing is not null);
    }

    /// <summary>The board's "Save as a picture": the board and its drawing, next to screenshots.</summary>
    public void SaveBoard()
    {
        if (_boardInk is null) return;
        string folder = Settings.ScreenshotFolder ?? SavedFiles.DefaultScreenshotFolder;
        try
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, Whiteboard.SaveName(DateTime.Now));
            using (Bitmap picture = Whiteboard.Snapshot(_boardInk.Strokes, Settings.WhiteboardLight)) picture.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Message($"The whiteboard was saved as “{Path.GetFileName(path)}”.", BadgeDot.Accent, Button("Show in folder", () => ShowInFolder(path), primary: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            Message("The whiteboard couldn't be saved: the screenshots folder can't be written.", BadgeDot.Warning);
        }
    }

    /// <summary>"Put the board away": what was shown before comes back (or showing stops); the drawing is forgotten.</summary>
    public void PutBoardAway()
    {
        SourceWindow? before = _beforeBoard;
        _beforeBoard = null;
        _boardInk = null;
        if (_drawing is not null && _drawingSource is { IsBoard: true }) StopDrawing();
        BoardWindow? board = _board;
        _board = null;
        if (board is { IsDisposed: false }) board.CloseFromApp();
        if (_session.Source is not { IsBoard: true }) return;
        _ = GuardAsync(async () =>
        {
            if (before is not null && (!before.IsWindow || WindowEnumerator.IsAlive(before.Handle))) await PresentAsync(before);
            else await StopShowingAsync();
        });
    }

    /// <summary>Settings › Where it shows › Whiteboard: dark or light.</summary>
    public void SetWhiteboardLight(bool light)
    {
        Settings.WhiteboardLight = light;
        Save();
        _board?.SetLight(light);
        if (_session.Source is { IsBoard: true } && Whiteboard.Render(LocalFolder("Board"), light) is { } file)
            _ = GuardAsync(() => PresentAsync(SourceWindow.ForPicture(file, "Whiteboard") with { IsBoard = true }));
    }

    // ───────────────────────────── the room sign (FR-125) ─────────────────────────────

    private string? _roomSignKey;
    private readonly System.Windows.Forms.Timer _roomSignTimer = new() { Interval = 20_000 };

    private string RoomSignName => Settings.RoomSignName.Length > 0 ? Settings.RoomSignName : Settings.Rooms.FirstOrDefault()?.Name ?? "";

    /// <summary>The sign as it should look now, made into its picture; null when it can't be written.</summary>
    private SourceWindow? RoomSignSource()
    {
        DateTime now = DateTime.Now;
        IReadOnlyList<CalendarOccurrence>? bookings = null;
        if (Settings.Calendar is { } link && CalendarEvents(link) is { } events)
            bookings = [.. IcsCalendar.Occurrences(events, now.Date.AddDays(-1), now.Date.AddDays(2)).Where(o => link.Wants(o) && o.End > now.Date && o.Start < now.Date.AddDays(1))];
        RoomSignState state = RoomSign.Build(RoomSignName, bookings, now, Settings.RoomSignTitles);
        string? file = RoomSign.Render(LocalFolder("Sign"), state, ref _roomSignKey);
        return file is null ? null : SourceWindow.ForPicture(file, "Room sign") with { IsRoomSign = true };
    }

    /// <summary>"room-sign": the room's bookings on the TV, kept up to date.</summary>
    private async Task ShowRoomSignAsync()
    {
        if (Settings.Calendar is null)
        {
            Message("The room sign reads the room's calendar file. Choose it first in Settings › Rooms & schedule › Calendar.", BadgeDot.Accent,
                Button("Open Settings", () => OpenSettings(SettingsForm.Section.Rooms)));
            return;
        }
        if (RoomSignSource() is not { } sign)
        {
            Message("The room sign couldn't be made: a folder on this PC can't be written.", BadgeDot.Warning);
            return;
        }
        await PresentAsync(sign);
    }

    /// <summary>While the sign is shown it's made again each minute that it says something new; the helper reads it in place, without a fade.</summary>
    private void UpdateRoomSignTimer()
    {
        bool want = !_quitting && (_session.Source is { IsRoomSign: true } || _second?.Source is { IsRoomSign: true });
        if (want == _roomSignTimer.Enabled) return;
        if (want) _roomSignTimer.Start();
        else _roomSignTimer.Stop();
    }

    private void OnRoomSignTick()
    {
        string? before = _roomSignKey;
        if (RoomSignSource() is null || _roomSignKey == before) return;
        if (_session.Source is { IsRoomSign: true }) _session.Refresh(); // the helper reads the picture again, in place
        if (_second?.Source is { IsRoomSign: true }) _second.Refresh();
    }

    /// <summary>Settings › Rooms & schedule › Room sign.</summary>
    public void SetRoomSign(string name, bool titles)
    {
        Settings.RoomSignName = InkStroke.TidyText(name, RoomSign.MaxName).Trim();
        Settings.RoomSignTitles = titles;
        Save();
        _roomSignKey = null;
        OnRoomSignTick();
    }

    // ───────────────────────────── shared ─────────────────────────────

    /// <summary>A saved PDF, video, whiteboard or room sign as something to show now; null when it isn't (or isn't one of these).</summary>
    private async Task<SourceWindow?> ResolveFileAsync(Favourite f)
    {
        if (f.Special == Favourite.WhiteboardSpecial)
            return Whiteboard.Render(LocalFolder("Board"), Settings.WhiteboardLight) is { } board ? SourceWindow.ForPicture(board, "Whiteboard") with { IsBoard = true } : null;
        if (f.Special == Favourite.RoomSignSpecial) return Settings.Calendar is null ? null : RoomSignSource();
        if (f.VideoPath is { } video) return VideoSource.Problem(video) is null ? SourceWindow.ForVideo(video, f.Title) : null;
        if (f.DocumentPath is { } pdf) return PdfPages.Problem(pdf) is null ? await DocumentPageAsync(pdf, f.DocumentPage, quiet: true) : null;
        return null;
    }

    private void StartSources()
    {
        _roomSignTimer.Tick += (_, _) => Guard(OnRoomSignTick);
    }

    private void UpdateSources()
    {
        UpdateRoomSignTimer();
        if (_board is { IsDisposed: false } && _session.Source is not { IsBoard: true }) PutBoardAway(); // something else is shown: the board goes
        if (_session.Effects is { } effects)
        {
            effects.VideoEvent -= OnVideoEvent;
            effects.VideoEvent += OnVideoEvent;
        }
    }

    private void DisposeSources()
    {
        _roomSignTimer.Dispose();
        _board?.Dispose();
    }
}
