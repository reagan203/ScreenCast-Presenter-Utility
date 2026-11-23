using System.Globalization;
using System.Runtime.InteropServices;

namespace TestWindowHost;

/// <summary>
/// Opens test windows for SCP's integration and stress tests, in its own process.
///
///   TestWindowHost.exe kind[:title] [kind[:title] ...]
///
/// Kinds: normal, dark, darker, tool, owned, cloaked, untitled, noactivate, appwindow-tool,
/// resizer (resizes 60×/s), retitler (new title 20×/s), big (3840×2160), tiny (40×40).
/// Prints "HWND &lt;handle&gt; &lt;kind&gt; &lt;title&gt;" per window, then "READY".
/// Commands on stdin: close|minimize|restore|front &lt;hwnd&gt;, title &lt;hwnd&gt; &lt;text&gt;,
/// move &lt;hwnd&gt; x y w h, hang &lt;ms&gt; (blocks this UI thread), open kind[:title], exit.
/// </summary>
internal static partial class Program
{
    private static readonly Dictionary<nint, Form> Windows = [];
    private static Form? _anchor;

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        _anchor = new Form { ShowInTaskbar = false, Opacity = 0, Size = new Size(1, 1), StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) };
        _anchor.Load += (_, _) =>
        {
            int offset = 0;
            foreach (string spec in args) Open(spec, offset++);
            Console.Out.WriteLine("READY");
            Console.Out.Flush();
            var reader = new Thread(ReadCommands) { IsBackground = true };
            reader.Start();
        };
        Application.Run(_anchor);
    }

    private static void ReadCommands()
    {
        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            string command = line;
            try
            {
                _anchor!.Invoke(() => Execute(command));
            }
            catch (InvalidOperationException)
            {
                return; // closing
            }
        }
        _anchor?.BeginInvoke(Application.Exit);
    }

    private static void Execute(string line)
    {
        string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        Form? target = parts.Length > 1 && long.TryParse(parts[1], CultureInfo.InvariantCulture, out long h) ? Windows.GetValueOrDefault((nint)h) : null;
        switch (parts[0])
        {
            case "exit": Application.Exit(); break;
            case "open": Open(parts.Length > 1 ? string.Join(' ', parts[1..]) : "normal", Windows.Count); break;
            case "close": target?.Close(); break;
            case "minimize": if (target is not null) target.WindowState = FormWindowState.Minimized; break;
            case "restore": if (target is not null) target.WindowState = FormWindowState.Normal; break;
            case "front": target?.Activate(); break;
            case "title": if (target is not null && parts.Length > 2) target.Text = parts[2]; break;
            case "move":
                string[] n = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (target is not null && n.Length == 6)
                    target.Bounds = new Rectangle(int.Parse(n[2], CultureInfo.InvariantCulture), int.Parse(n[3], CultureInfo.InvariantCulture),
                        int.Parse(n[4], CultureInfo.InvariantCulture), int.Parse(n[5], CultureInfo.InvariantCulture));
                break;
            case "hang":
                Thread.Sleep(parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 5000);
                break;
        }
        Console.Out.WriteLine("OK " + parts[0]);
        Console.Out.Flush();
    }

    private static void Open(string spec, int offset)
    {
        string[] parts = spec.Split(':', 2);
        string kind = parts[0].ToLowerInvariant();
        string title = parts.Length > 1 ? parts[1] : $"SCP test {kind}";

        var form = new TestForm(kind)
        {
            Text = kind == "untitled" ? "" : title,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(80 + offset * 30, 80 + offset * 30),
            Size = kind switch { "big" => new Size(3840, 2160), "tiny" => new Size(40, 40), _ => new Size(900, 600) },
            BackColor = kind switch
            {
                "dark" => Color.FromArgb(0x10, 0x10, 0x16),
                "darker" => Color.FromArgb(0x16, 0x10, 0x12),
                _ => Color.FromArgb(0x2E, 0x3A, 0x5C),
            },
        };
        if (kind == "owned") form.Owner = _anchor;
        form.FormClosed += (_, _) => Windows.Remove(form.Handle);
        form.Show();
        Windows[form.Handle] = form;

        if (kind == "cloaked")
        {
            int cloak = 1;
            _ = DwmSetWindowAttribute(form.Handle, 13 /* DWMWA_CLOAK */, ref cloak, sizeof(int));
        }
        if (kind == "resizer") StartTimer(form, 16, f => f.Width = f.Width == 900 ? 700 : 900);
        if (kind == "retitler")
        {
            int n = 0;
            StartTimer(form, 50, f => f.Text = $"{title} {++n}");
        }

        Console.Out.WriteLine($"HWND {form.Handle.ToInt64().ToString(CultureInfo.InvariantCulture)} {kind} {form.Text}");
        Console.Out.Flush();
    }

    private static void StartTimer(Form form, int interval, Action<Form> tick)
    {
        var timer = new System.Windows.Forms.Timer { Interval = interval };
        timer.Tick += (_, _) => tick(form);
        form.FormClosed += (_, _) => timer.Dispose();
        timer.Start();
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    private sealed class TestForm(string kind) : Form
    {
        private const int WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                if (kind is "tool" or "appwindow-tool") cp.ExStyle |= WS_EX_TOOLWINDOW;
                if (kind == "appwindow-tool") cp.ExStyle |= WS_EX_APPWINDOW;
                if (kind == "noactivate") cp.ExStyle |= WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // A few shapes so thumbnails and captures have content; dark kinds stay below #303030.
            bool dark = kind is "dark" or "darker";
            using var brush = new SolidBrush(dark ? Color.FromArgb(0x24, 0x24, 0x2A) : Color.FromArgb(0xE0, 0x7A, 0x5F));
            e.Graphics.FillRectangle(brush, ClientSize.Width / 8, ClientSize.Height / 6, ClientSize.Width / 2, ClientSize.Height / 10);
            e.Graphics.FillRectangle(brush, ClientSize.Width / 8, ClientSize.Height / 3, ClientSize.Width / 3, ClientSize.Height / 3);
        }
    }
}
