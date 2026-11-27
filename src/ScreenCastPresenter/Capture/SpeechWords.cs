using System.Runtime.InteropServices;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// The presenter's words for live captions (FR-128), added to Windows' speech dictionary (SAPI's
/// user lexicon) while captions listen, so the recognizer can hear "Ninthmind" as a word. Words
/// already there are left alone; those added here are taken out again when it's disposed, so the
/// presenter's Windows is as it was. Through raw COM calls: ISpLexicon (sapi.h), in the captions helper only.
/// </summary>
internal sealed unsafe partial class SpeechWords : IDisposable
{
    private static readonly Guid ClsidSpLexicon = new("0655E396-25D0-11D3-9C26-00C04F8EF87C");
    private static readonly Guid IidISpLexicon = new("DA41A7C2-5383-4DB2-916B-6C1719E3DB58");

    // ISpLexicon: IUnknown (3), GetPronunciations (3), AddPronunciation (4), RemovePronunciation (5).
    private const int SlotAdd = 4, SlotRemove = 5;
    private const int PartOfSpeechNoun = 0x1000; // SPPS_Noun

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint obj);

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetUserDefaultUILanguage();

    private nint _lexicon;
    private readonly ushort _language;
    private readonly List<string> _added = [];

    private SpeechWords(nint lexicon, ushort language)
    {
        _lexicon = lexicon;
        _language = language;
    }

    /// <summary>The words added (those not already known); empty when Windows' dictionary can't be reached.</summary>
    public IReadOnlyList<string> Added => _added;

    /// <summary>Adds <paramref name="words"/>; null when the dictionary can't be reached (captions still work, just without them).</summary>
    public static SpeechWords? Add(IReadOnlyList<string> words)
    {
        if (words.Count == 0) return null;
        if (CoCreateInstance(ClsidSpLexicon, 0, 1 /* in-process */, IidISpLexicon, out nint lexicon) < 0 || lexicon == 0) return null;
        var added = new SpeechWords(lexicon, GetUserDefaultUILanguage());
        foreach (string word in words)
        {
            fixed (char* text = word)
            {
                // No pronunciation: the recognizer works one out from the spelling.
                int hr = ((delegate* unmanaged[Stdcall]<nint, char*, ushort, int, ushort*, int>)Slot(lexicon, SlotAdd))(lexicon, text, added._language, PartOfSpeechNoun, null);
                if (hr == 0) added._added.Add(word); // S_OK: added now (a success code such as "already there" leaves it alone)
            }
        }
        return added;
    }

    /// <summary>Takes <paramref name="words"/> out of Windows' speech dictionary (words a helper added and couldn't take out, FR-128).</summary>
    public static void Remove(IReadOnlyList<string> words)
    {
        if (words.Count == 0) return;
        if (CoCreateInstance(ClsidSpLexicon, 0, 1, IidISpLexicon, out nint lexicon) < 0 || lexicon == 0) return;
        try
        {
            ushort language = GetUserDefaultUILanguage();
            foreach (string word in words)
            {
                fixed (char* text = word)
                    _ = ((delegate* unmanaged[Stdcall]<nint, char*, ushort, int, ushort*, int>)Slot(lexicon, SlotRemove))(lexicon, text, language, PartOfSpeechNoun, null);
            }
        }
        finally
        {
            Marshal.Release(lexicon);
        }
    }

    private static void* Slot(nint obj, int index) => (*(void***)obj)[index];

    public void Dispose()
    {
        if (_lexicon == 0) return;
        foreach (string word in _added)
        {
            fixed (char* text = word)
                _ = ((delegate* unmanaged[Stdcall]<nint, char*, ushort, int, ushort*, int>)Slot(_lexicon, SlotRemove))(_lexicon, text, _language, PartOfSpeechNoun, null);
        }
        _added.Clear();
        Marshal.Release(_lexicon);
        _lexicon = 0;
    }
}
