using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ScreenCastPresenter.Core;

/// <summary>
/// Voice commands (FR-110): "Presenter, next slide". Windows' own speech recognition on the PC (the
/// same as live captions) hears the room; only phrases that start with the wake word and are on
/// this list do anything, and nothing heard is kept. Like the phone remote: nothing that opens a
/// window, changes a setting or records.
/// </summary>
internal static class VoiceCommands
{
    public const string WakeWord = "presenter";

    /// <summary>Phrases (after the wake word) and the command each runs. "N" stands for a number 1–9.</summary>
    private static readonly (string[] Phrases, string Command)[] Table =
    [
        (["next slide", "next", "forward", "next page"], "next-slide"),
        (["previous slide", "previous", "back", "go back", "last slide", "previous page"], "previous-slide"),
        (["blank the screen", "blank", "blank screen", "black screen", "hide the screen"], "voice:blank-on"),
        (["show the screen", "unblank", "show it again", "come back"], "voice:blank-off"),
        (["freeze", "freeze the screen", "hold"], "voice:freeze-on"),
        (["unfreeze", "go live", "live"], "voice:freeze-off"),
        (["spotlight", "spotlight on"], "voice:on:spotlight"),
        (["spotlight off", "no spotlight"], "voice:off:spotlight"),
        (["zoom in"], "voice:zoom-in"),
        (["zoom out"], "voice:zoom-out"),
        (["take a break", "break", "start the break"], "blank-countdown"),
        (["show what's ready", "show whats ready", "go", "show the next thing"], "go"),
        (["stop showing", "stop"], "stop"),
        (["take a screenshot", "screenshot"], "screenshot"),
        (["save the last minute", "save that", "replay"], "save-replay"),
        (["show favourite N", "show favorite N", "favourite N", "favorite N"], "favourite-N"),
        (["show scene N", "scene N"], "scene-N"),
    ];

    private static readonly string[] Numbers = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    /// <summary>Every command voice can run (for the allowlist check).</summary>
    public static IReadOnlyList<string> Commands { get; } = [.. Table.Select(t => t.Command).Distinct()];

    /// <summary>What was heard, tidied: lower case, punctuation gone, number words as digits ("for" and "to" left alone).</summary>
    public static string Tidy(string heard)
    {
        var text = new StringBuilder();
        foreach (char c in heard.ToLowerInvariant()) text.Append(char.IsLetterOrDigit(c) || c == '\'' ? c : ' ');
        string[] words = text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            int n = Array.IndexOf(Numbers, words[i]);
            if (n > 0) words[i] = n.ToString(CultureInfo.InvariantCulture);
        }
        return string.Join(' ', words);
    }

    /// <summary>
    /// The command for what was heard, and the words matched, or null. With <paramref name="wakeWord"/>,
    /// the phrase must start with "presenter"; the rest must be one of the phrases, nothing more.
    /// </summary>
    public static (string Command, string Phrase)? Match(string? heard, bool wakeWord = true)
    {
        if (string.IsNullOrWhiteSpace(heard)) return null;
        string text = Tidy(heard);
        if (wakeWord)
        {
            if (!text.StartsWith(WakeWord + " ", StringComparison.Ordinal)) return null;
            text = text[(WakeWord.Length + 1)..];
        }
        if (text.EndsWith(" please", StringComparison.Ordinal)) text = text[..^7];
        foreach ((string[] phrases, string command) in Table)
        {
            foreach (string phrase in phrases)
            {
                if (!phrase.EndsWith(" N", StringComparison.Ordinal))
                {
                    if (text == phrase) return (command, phrase);
                    continue;
                }
                string stem = phrase[..^1];
                if (text.StartsWith(stem, StringComparison.Ordinal) && text.Length == stem.Length + 1 && text[^1] is >= '1' and <= '9')
                    return (command.Replace("N", text[^1..], StringComparison.Ordinal), text);
            }
        }
        return null;
    }

    /// <summary>The phrases to show in Settings ("Presenter, next slide").</summary>
    public static IReadOnlyList<string> Examples { get; } =
        ["next slide", "previous slide", "blank the screen", "show the screen", "freeze", "zoom in", "zoom out", "take a break", "show favourite 2", "show scene 3", "stop showing"];
}

/// <summary>
/// Speaker notes (FR-111): a few lines per favourite and scene, for the presenter's eyes only, on a
/// private window on their own screen and on the paired phone. Sent to the phone encrypted with
/// the pairing key (others on the Wi-Fi see only noise): a keystream from HMAC-SHA256 over a fresh
/// random nonce and a counter (the page has HMAC-SHA256 already; browsers give no other
/// cryptography over plain HTTP), then a tag over the nonce and the ciphertext, checked first.
/// </summary>
internal static class NotesCipher
{
    public const int MaxLength = 2000;
    public const int NonceBytes = 16;

    /// <summary>Notes tidied: line breaks kept, control characters gone, at most <see cref="MaxLength"/>.</summary>
    public static string Tidy(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return "";
        string text = new([.. notes.Replace("\r\n", "\n").Replace('\r', '\n').Where(c => c == '\n' || !char.IsControl(c))]);
        text = text.Trim();
        return text.Length <= MaxLength ? text : text[..MaxLength];
    }

    /// <summary>The notes encrypted for the phone: nonce, ciphertext (base64url) and tag (hex).</summary>
    public static (string Nonce, string Cipher, string Tag) Encrypt(byte[] key, string notes, byte[]? nonce = null)
    {
        nonce ??= RandomNumberGenerator.GetBytes(NonceBytes);
        byte[] plain = Encoding.UTF8.GetBytes(notes);
        byte[] cipher = Xor(key, nonce, plain);
        string n = RemoteAuth.Base64Url(nonce), c = RemoteAuth.Base64Url(cipher);
        return (n, c, Tag(key, n, c));
    }

    /// <summary>The notes back, or null when the tag doesn't match (tampered with, or another key).</summary>
    public static string? Decrypt(byte[] key, string nonce, string cipher, string tag)
    {
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Tag(key, nonce, cipher)), Encoding.ASCII.GetBytes(tag ?? ""))) return null;
        if (RemoteAuth.FromBase64Url(nonce) is not { } n || RemoteAuth.FromBase64UrlLong(cipher) is not { } c) return null;
        return Encoding.UTF8.GetString(Xor(key, n, c));
    }

    private static string Tag(byte[] key, string nonce, string cipher) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"notes-tag\n{nonce}\n{cipher}")));

    /// <summary>XOR with the keystream HMAC(key, "notes\n" + nonce + "\n" + block number), block after block.</summary>
    private static byte[] Xor(byte[] key, byte[] nonce, byte[] data)
    {
        var result = new byte[data.Length];
        string n = RemoteAuth.Base64Url(nonce);
        for (int block = 0; block * 32 < data.Length; block++)
        {
            byte[] stream = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"notes\n{n}\n{block}")));
            for (int i = 0; i < 32 && block * 32 + i < data.Length; i++) result[block * 32 + i] = (byte)(data[block * 32 + i] ^ stream[i]);
        }
        return result;
    }
}
