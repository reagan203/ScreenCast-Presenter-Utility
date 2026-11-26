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
        new("How do I show a window by clicking it?", "Point at it on your screen and click.", "pick-window", "click point mouse choose window select"),
        new("How do I share in Zoom or Teams?", "Show it in a window or on a Zoom screen, then share that in your meeting.", "zoom", "zoom teams meet share meeting call online"),
        new("How do I hide the TV for a moment?", "Blank it: the audience sees black until you show it again.", "blank", "hide black blank pause break private"),
        new("How do I show only part of a window?", "Drag a box around the part you want.", "crop", "crop part zoom area region bigger"),
        new("How do I zoom in on part of the screen?", "Zoom in around your mouse; the TV follows it.", "zoom-in", "zoom magnify closer bigger enlarge follow small text"),
        new("How do I show a countdown during a break?", "The TV goes black and counts down; you bring it back.", "blank-countdown", "break countdown timer coffee pause minutes back"),
        new("How do I show several things one after another?", "Your favourites take turns on the TV.", "slideshow", "slideshow loop rotate lobby waiting room cycle favourites carousel"),
        new("How do I show a webcam or document camera?", "Choose it under Cameras, next to the screens.", "picker", "camera webcam document visualiser visualizer doc cam paper"),
        new("How do I draw on the TV?", "Draw over what you show; the audience sees it as you draw.", "draw", "draw pen ink annotate highlighter marker sketch circle underline"),
        new("How do I use a laser pointer?", "A red dot follows your mouse on the TV.", "laser", "laser pointer red dot point"),
        new("How do I show my webcam in a corner?", "Add a small picture in a corner, over what you show.", "inset", "webcam face camera corner picture in picture pip small overlay me"),
        new("How do I hide part of a window from the audience?", "Drag a box over it: they see a solid box or a blur there, even as you scroll.", "hide-part", "hide redact cover blur private password email secret censor box part"),
        new("How do I see exactly what the audience sees?", "A small private window shows the TV live, only for you.", "audience-view", "preview monitor audience view confidence check see tv what"),
        new("How do I get the next thing ready before switching?", "Choose it and see it privately; one key then shows it.", "get-ready", "next queue cue prepare ready preview stage switch go"),
        new("How do I spotlight part of the screen?", "Everything on the TV dims except a circle around your mouse.", "spotlight", "spotlight torch focus dim attention highlight circle"),
        new("How do I add arrows, boxes or text on the TV?", "Draw on it and choose Arrow, Box or Text in the toolbar.", "draw", "arrow box rectangle text label annotate shapes point callout"),
        new("How do I put my name on the TV?", "A banner along the bottom with your name and title.", "caption", "name caption lower third banner title who speaker introduce"),
        new("How do I put my logo on the blank screen?", "Choose a picture in Settings; it shows above the message.", "settings:output", "logo brand picture company blank screen image"),
        new("How do I show a QR code for a link or the Wi-Fi?", "It's made on this PC and shows in a corner of the TV.", "qr", "qr code link wifi wi-fi phone scan guest network url website"),
        new("How do I set things up for a room in one click?", "Save the setup as a room, then choose it from the tray.", "settings:rooms", "room profile setup preset classroom meeting room lobby one click"),
        new("How do I start the lobby slideshow automatically?", "Add a schedule: start it at 8:00, stop it at 18:00.", "settings:rooms", "schedule timer automatic morning evening lobby signage start stop time"),
        new("How do I show live captions?", "What you say appears on the TV, worked out on this PC.", "live-captions", "captions subtitles deaf hard of hearing speech transcribe accessibility words"),
        new("How do I show something different on a second TV?", "Choose what the second TV shows; the first keeps its picture.", "second-tv", "second two tv dual another screen projector both different"),
        new("How do I switch to a whole setup with one key?", "Save it as a scene: what's shown, the small picture, your name and more.", "save-scene", "scene preset layout setup obs switch one key stream deck"),
        new("How do I run a lobby or waiting-room screen?", "Make a playlist of favourites and scenes; schedule it or start it with the app.", "settings:rooms", "playlist signage lobby waiting room loop rotate digital sign"),
        new("How do I hide email addresses and card numbers automatically?", "They're found on the PC and covered on the TV as they appear.", "auto-hide", "automatic hide redact email phone card bank iban personal details privacy ocr"),
        new("How do I save a transcript of my talk?", "Live captions can write down what's said, with subtitles.", "settings:output", "transcript notes subtitles srt minutes captions accessibility written"),
        new("How do I use my phone as a clicker?", "Pair your phone: next and previous slide, blank, favourites and scenes.", "phone-remote", "phone remote clicker mobile control slides pair wifi"),
        new("How do I stop showing?", "The TV goes dark; your own screen stays as it is.", "stop", "stop end finish done"),
        new("How do I record a webinar while I work?", "Choose the window; it keeps recording even when it's covered.", "record-picker", "record webinar video meeting save"),
        new("How do I take a screenshot?", "Saved as a picture and copied, ready to paste.", "screenshot", "screenshot snip picture image photo"),
        new("Where are my recordings?", "Opens the folder your recordings are saved in.", "open-recordings", "recordings videos files folder find saved"),
        new("How do I choose which screen is the TV?", "Pick it in Settings.", "settings:output", "tv screen monitor projector which second"),
        new("How do I make the text bigger?", "Choose a text size in Settings.", "settings:appearance", "text size bigger larger font read see"),
        new("How do I make sure an app is never shown?", "Put it on your never-show list, like a password manager.", "settings:privacy", "private password secret never hide block bank email privacy"),
        new("What are the keyboard shortcuts?", "See every shortcut, and change them.", "cheat-sheet", "keys keyboard shortcuts hotkeys"),
    ];
}
