using System.Security.Cryptography;
using System.Text;

namespace ScreenCastPresenter.Remote;

/// <summary>
/// The page the phone opens (FR-90): one self-contained file, no outside resources, locked down by
/// a content security policy that allows only its own script and style (by hash) and calls back
/// to the same address. The pairing key comes from the address's #fragment, which browsers never
/// send, and stays in the phone's storage for this address. Commands are signed in the page with
/// HMAC-SHA256 (browsers only offer built-in cryptography on secure origins, so it's here).
/// </summary>
internal static class RemotePage
{
    /// <summary>SHA-256 and HMAC-SHA256 on byte arrays, in plain JavaScript (tested against .NET and RFC 4231).</summary>
    public const string Crypto = """
        const K = new Uint32Array([
          0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
          0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
          0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
          0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
          0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
          0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
          0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
          0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2]);
        function rotr(x, n) { return (x >>> n) | (x << (32 - n)); }
        function sha256(bytes) {
          const H = new Uint32Array([0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19]);
          const length = bytes.length, total = ((length + 9 + 63) >> 6) << 6;
          const m = new Uint8Array(total);
          m.set(bytes);
          m[length] = 0x80;
          const view = new DataView(m.buffer);
          view.setUint32(total - 4, (length * 8) >>> 0);
          view.setUint32(total - 8, Math.floor(length / 0x20000000));
          const W = new Uint32Array(64);
          for (let o = 0; o < total; o += 64) {
            for (let i = 0; i < 16; i++) W[i] = view.getUint32(o + i * 4);
            for (let i = 16; i < 64; i++) {
              const s0 = rotr(W[i - 15], 7) ^ rotr(W[i - 15], 18) ^ (W[i - 15] >>> 3);
              const s1 = rotr(W[i - 2], 17) ^ rotr(W[i - 2], 19) ^ (W[i - 2] >>> 10);
              W[i] = (W[i - 16] + s0 + W[i - 7] + s1) >>> 0;
            }
            let a = H[0], b = H[1], c = H[2], d = H[3], e = H[4], f = H[5], g = H[6], h = H[7];
            for (let i = 0; i < 64; i++) {
              const t1 = (h + (rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)) + ((e & f) ^ (~e & g)) + K[i] + W[i]) >>> 0;
              const t2 = ((rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)) + ((a & b) ^ (a & c) ^ (b & c))) >>> 0;
              h = g; g = f; f = e; e = (d + t1) >>> 0; d = c; c = b; b = a; a = (t1 + t2) >>> 0;
            }
            H[0] += a; H[1] += b; H[2] += c; H[3] += d; H[4] += e; H[5] += f; H[6] += g; H[7] += h;
          }
          const out = new Uint8Array(32), ov = new DataView(out.buffer);
          for (let i = 0; i < 8; i++) ov.setUint32(i * 4, H[i]);
          return out;
        }
        function hmac(key, message) {
          if (key.length > 64) key = sha256(key);
          const k = new Uint8Array(64); k.set(key);
          const inner = new Uint8Array(64 + message.length), outer = new Uint8Array(96);
          for (let i = 0; i < 64; i++) { inner[i] = k[i] ^ 0x36; outer[i] = k[i] ^ 0x5c; }
          inner.set(message, 64);
          outer.set(sha256(inner), 64);
          return sha256(outer);
        }
        function hex(bytes) { return Array.from(bytes, b => b.toString(16).padStart(2, "0")).join(""); }
        function fromBase64Url(text) {
          const b = atob(text.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((text.length + 3) % 4));
          return Uint8Array.from(b, c => c.charCodeAt(0));
        }
        """;

    private const string App = """
        const KEY_STORE = "scp-remote-key";
        const STATUS_EVERY_MS = 10000;   // what's on is asked for this often while the page is open
        const TAP_BUZZ_MS = 12;          // a short buzz confirms a command reached the PC
        let key = null, session = null, last = 0;
        const $ = id => document.getElementById(id);
        function say(text, problem) { $("status").textContent = text; $("status").className = problem ? "problem" : ""; }
        function loadKey() {
          const m = location.hash.match(/[#&]k=([A-Za-z0-9_-]+)/);
          if (m) { try { localStorage.setItem(KEY_STORE, m[1]); } catch (e) {} history.replaceState(null, "", location.pathname); }
          let stored = null;
          try { stored = localStorage.getItem(KEY_STORE); } catch (e) {}
          stored = stored || (m && m[1]);
          return stored ? fromBase64Url(stored) : null;
        }
        async function getSession() {
          const r = await fetch("/session", { cache: "no-store" });
          session = (await r.json()).s;
        }
        async function send(command, again) {
          if (!key) { say("Scan the code on the presenter's screen to pair this phone.", true); return; }
          try {
            if (!session) await getSession();
            const counter = Math.max(Date.now(), last + 1);
            last = counter;
            const mac = hex(hmac(key, new TextEncoder().encode(session + "\n" + counter + "\n" + command)));
            const body = "s=" + encodeURIComponent(session) + "&c=" + counter + "&a=" + encodeURIComponent(command) + "&m=" + mac;
            const r = await fetch("/cmd", { method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded" }, body, cache: "no-store" });
            const answer = await r.json().catch(() => ({}));
            if (r.status === 200) { show(answer); if (navigator.vibrate && command !== "status") navigator.vibrate(TAP_BUZZ_MS); return; }
            if (!again && r.status === 409 && answer.last) { last = answer.last; return send(command, true); }
            if (!again && r.status === 401 && answer.session === "stale") { session = null; return send(command, true); }
            if (r.status === 401) say("This phone isn't paired any more. Scan the code on the presenter's screen again.", true);
            else if (r.status === 429) say("Slow down a little.", true);
            else say("That didn't work (" + r.status + ").", true);
          } catch (e) { say("Can't reach ScreenCast Presenter. Is the phone on the same Wi-Fi, and the remote still on?", true); }
        }
        function show(answer) {
          if (answer.status) say(answer.status, false);
          if (answer.note) say(answer.note, true);
          fill("favourites", answer.favourites || [], "favourite-");
          fill("scenes", answer.scenes || [], "scene-");
          fill("playlists", answer.playlists || [], "playlist-");
          $("playlist-section").hidden = !answer.playlist;
          if (answer.playlist) $("playlist-name").textContent = "Playing “" + answer.playlist + "”";
        }
        function fill(id, names, prefix) {
          const box = $(id), section = $(id + "-section");
          box.textContent = "";
          names.forEach((name, i) => {
            if (!name) return;
            const b = document.createElement("button");
            b.textContent = (i + 1) + "  " + name;
            b.addEventListener("click", () => send(prefix + (i + 1)));
            box.appendChild(b);
          });
          section.hidden = box.childElementCount === 0;
        }
        document.querySelectorAll("[data-a]").forEach(b => b.addEventListener("click", () => send(b.dataset.a)));
        key = loadKey();
        if (key) { send("status"); setInterval(() => { if (!document.hidden) send("status"); }, STATUS_EVERY_MS); }
        else say("Scan the code on the presenter's screen to pair this phone.", true);
        """;

    private const string Style = """
        :root { color-scheme: dark; --bg: #111114; --panel: #1d1d22; --text: #f2f2f4; --dim: #a0a0a8; --accent: #ffc83d; --warn: #ff8a80; }
        * { box-sizing: border-box; }
        body { margin: 0; padding: 16px; background: var(--bg); color: var(--text); font: 16px system-ui, -apple-system, "Segoe UI", sans-serif; }
        h1 { font-size: 18px; margin: 4px 0 12px; }
        #status { color: var(--dim); min-height: 2.6em; margin-bottom: 12px; }
        #status.problem { color: var(--warn); }
        .row { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; margin-bottom: 10px; }
        button { width: 100%; min-height: 56px; border: 0; border-radius: 14px; background: var(--panel); color: var(--text); font: inherit; font-size: 17px; }
        button:active { background: #2c2c33; }
        .big button { min-height: 110px; font-size: 22px; background: var(--accent); color: #111; font-weight: 600; }
        h2 { font-size: 13px; letter-spacing: .06em; text-transform: uppercase; color: var(--dim); margin: 18px 0 8px; }
        .list { display: grid; gap: 8px; }
        .list button { text-align: left; padding: 0 16px; }
        """;

    private const string Body = """
        <h1>ScreenCast Presenter</h1>
        <div id="status">Connecting…</div>
        <div class="row big"><button data-a="previous-slide">◀ Back</button><button data-a="next-slide">Next ▶</button></div>
        <div class="row"><button data-a="blank">Blank / show</button><button data-a="freeze">Freeze / live</button></div>
        <div class="row"><button data-a="spotlight">Spotlight</button><button data-a="go">Show what's ready</button></div>
        <div class="row"><button data-a="previous-source">Previous</button><button data-a="stop">Stop showing</button></div>
        <div class="row"><button data-a="zoom-in">Zoom in / out</button><button data-a="caption">Name banner</button></div>
        <div class="row"><button data-a="live-captions">Live captions</button><button data-a="blank-countdown">Break countdown</button></div>
        <div id="playlist-section" hidden><h2 id="playlist-name">Playlist</h2><div class="row"><button data-a="stop-playlist">Stop the playlist</button></div></div>
        <div id="favourites-section" hidden><h2>Favourites</h2><div id="favourites" class="list"></div></div>
        <div id="scenes-section" hidden><h2>Scenes</h2><div id="scenes" class="list"></div></div>
        <div id="playlists-section" hidden><h2>Playlists</h2><div id="playlists" class="list"></div></div>
        """;

    private static string Script => Crypto + "\n" + App;

    /// <summary>The page itself.</summary>
    public static string Html { get; } =
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
        + "<meta name=\"referrer\" content=\"no-referrer\"><title>ScreenCast Presenter remote</title>"
        + "<style>" + Style + "</style></head><body>" + Body + "<script>" + Script + "</script></body></html>";

    /// <summary>Only this page's own script and style run; it talks only to where it came from.</summary>
    public static string ContentSecurityPolicy { get; } =
        $"default-src 'none'; script-src 'sha256-{Hash(Script)}'; style-src 'sha256-{Hash(Style)}'; connect-src 'self'; img-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static string Hash(string text) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
