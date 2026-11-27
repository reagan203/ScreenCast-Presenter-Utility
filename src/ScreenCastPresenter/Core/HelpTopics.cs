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
        new("How do I show a PDF without opening it?", "Choose the PDF: its pages show one by one, and next slide turns the page.", "show-pdf", "pdf document handout pages acrobat reader page"),
        new("How do I play a video on the TV?", "Choose the video: it plays with its sound, and freezing pauses it.", "show-video", "video movie clip mp4 play sound film intro"),
        new("How do I draw on a blank board?", "Bring out the whiteboard and draw on it from its window.", "show-whiteboard", "whiteboard blackboard board blank draw sketch diagram explain"),
        new("How do I show whether the room is free?", "The room sign shows its bookings from the room's calendar file.", "room-sign", "room sign free busy booked booking calendar door lobby"),
        new("How do I make the TV follow the window I'm in?", "Turn on Follow me: switching to an app you've shown shows it; anything else stays private.", "follow-me", "follow active window alt tab switch automatic demo trainer"),
        new("How do I use my presenter clicker while I work in another window?", "Page Down and Page Up go to what's shown, wherever you're working.", "clicker-keys", "clicker remote presenter page down slides keys logitech another window"),
        new("How do I put a timer on the TV for an exercise?", "It counts down in a corner while what's shown stays up.", "timer", "timer countdown exercise activity group work minutes stopwatch"),
        new("How do I stop a white page dazzling a dark room?", "The night filter softens white pages or turns them dark.", "night-filter", "night dark bright white glare dazzle invert projector dark room"),
        new("How do I show my webcam in a circle?", "Choose the small picture's shape: a circle, rounded corners or a rectangle.", "inset-shape", "webcam circle round bubble shape face small picture"),
        new("How do I connect a wireless TV or projector?", "Windows' Cast list connects it; then it's offered as a TV.", "cast", "wireless cast miracast connect projector tv mirror"),
        new("How do I see my notes and the time on a stage monitor?", "The speaker's screen shows the time, your notes and what's next, facing you.", "speaker-screen", "speaker stage monitor confidence notes time left next screen"),
        new("How do I practise my talk?", "Rehearse: each part is timed and your pace and filler words are counted.", "rehearse", "rehearse practise practice pace filler um timing coach dry run"),
        new("How do I copy the text that's on the TV?", "It's read on this PC and put on the clipboard, ready to paste.", "copy-text", "copy text words ocr paste clipboard extract read"),
        new("How do I make a GIF from a recording?", "Choose the recording: its start becomes a short GIF beside it.", "make-gif", "gif animated animation clip share chat bug report"),
        new("How do I keep the taskbar and desktop icons off the TV?", "Both are left out when a whole screen is shown, unless you change it.", "settings:privacy", "taskbar desktop icons clock notifications badges whole screen privacy"),
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
