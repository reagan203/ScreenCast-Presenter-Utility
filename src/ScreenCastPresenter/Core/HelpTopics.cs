namespace ScreenCastPresenter.Core;

/// <summary>
/// One "How do I…?" in Search and help. Choosing it does the thing, or opens the place where it
/// is done, rather than only describing it. <see cref="Command"/> is an action id or one of the
/// app's commands (see ScpApp.RunCommand).
/// </summary>
internal sealed record HelpTopic(string Question, string Answer, string Command, string Keywords);

/// <summary>The questions people new to SCP ask, in their words, each one click from the answer.</summary>
internal static class HelpTopics
{
    public static IReadOnlyList<HelpTopic> All { get; } =
    [
        new("How do I show a window on the TV?", "Choose it, then click Show.", "picker", "present tv window slides powerpoint show projector"),
        new("How do I share in Zoom or Teams?", "Show it in a window or on a Zoom screen, then share that in your meeting.", "zoom", "zoom teams meet share meeting call online"),
        new("How do I hide the TV for a moment?", "Blank it: the audience sees black until you show it again.", "blank", "hide black blank pause break private"),
        new("How do I show only part of a window?", "Drag a box around the part you want.", "crop", "crop part zoom area region bigger"),
        new("How do I stop showing?", "The TV goes dark; your own screen stays as it is.", "stop", "stop end finish done"),
        new("How do I record a webinar while I work?", "Choose the window; it keeps recording even when it's covered.", "record-picker", "record webinar video meeting save"),
        new("How do I take a screenshot?", "Saved as a picture and copied, ready to paste.", "screenshot", "screenshot snip picture image photo"),
        new("Where are my recordings?", "Opens the folder your recordings are saved in.", "open-recordings", "recordings videos files folder find saved"),
        new("How do I choose which screen is the TV?", "Pick it in Settings.", "settings:output", "tv screen monitor projector which second"),
        new("How do I make the text bigger?", "Choose a text size in Settings.", "settings:appearance", "text size bigger larger font read see"),
        new("What are the keyboard shortcuts?", "See every shortcut, and change them.", "cheat-sheet", "keys keyboard shortcuts hotkeys"),
    ];
}
