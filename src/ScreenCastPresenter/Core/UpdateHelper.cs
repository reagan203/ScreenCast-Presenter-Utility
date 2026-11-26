using System.Net;
using System.Text;

namespace ScreenCastPresenter.Core;

/// <summary>
/// The update-check helper process (FR-73): the exe started with <see cref="UpdateCheck.Switch"/>.
/// The only code in ScreenCast Presenter that uses the network, and it runs in its own short-lived
/// process, so the app never opens a connection or keeps networking code in memory. It asks
/// GitHub for this project's latest release, writes one line (see <see cref="UpdateCheck.Reply"/>)
/// and exits. The request names the app and nothing else: no user, PC or settings data.
/// </summary>
internal static class UpdateHelper
{
    public static int Run()
    {
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        output.WriteLine(UpdateCheck.Reply(Ask()));
        return 0;
    }

    private static UpdateResult Ask()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1024 * 1024 };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ScreenCastPresenter-UpdateCheck"); // GitHub requires one; it names the app only
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using HttpResponseMessage response = http.GetAsync(UpdateCheck.ApiUrl).GetAwaiter().GetResult();
            if (response.StatusCode == HttpStatusCode.NotFound) return new UpdateResult(null, null, null); // no release published yet
            if (!response.IsSuccessStatusCode) return new UpdateResult(null, null, $"GitHub answered with code {(int)response.StatusCode}. Try again later.");
            return UpdateCheck.Interpret(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        }
        catch (HttpRequestException)
        {
            return new UpdateResult(null, null, "There's no internet connection, or GitHub couldn't be reached.");
        }
        catch (TaskCanceledException)
        {
            return new UpdateResult(null, null, "GitHub took too long to answer.");
        }
    }
}
