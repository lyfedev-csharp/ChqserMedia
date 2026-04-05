using ChqserMedia;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Valve.Newtonsoft.Json;

namespace ChqserMedia
{
    public class MediaManager : MonoBehaviour
    {
        // publicly readable playback state — only this class can write to them
        public static string Title { get; private set; } = "Unknown";
        public static string Artist { get; private set; } = "Unknown";
        public static Texture2D Icon { get; private set; } = new Texture2D(2, 2);
        public static bool Paused { get; private set; } = true;
        public static bool ValidData { get; private set; }
        public static float Position { get; private set; }
        public static float Duration { get; private set; }

        // path where the helper exe is written at runtime
        public static string ExePath { get; private set; }

        // singleton so static methods can reach instance members
        public static MediaManager Instance { get; private set; }

        // the exe is baked into the dll as an embedded resource
        private const string ExeResourceName = "ChqserMedia.Resources.GTMediaController.exe";
        private const string ExeFileName = "GTMediaController.exe";

        // ui references — all found by path in initialize
        private Image thumbnailImage;
        private TextMeshProUGUI songNameText;
        private TextMeshProUGUI songArtistText;
        private TextMeshProUGUI timeStampStartText;
        private TextMeshProUGUI timeStampEndText;
        private TextMeshProUGUI lyricsText;
        private Image progressFill;
        private Image playPauseIcon;
        private Image backgroundImage;
        private Image skipButton;
        private Image prevButton;
        private Image playButton;

        // gradient components added at runtime to tint the ui from the album art
        private UIGradient backgroundGradient;
        private UIGradient skipGradient;
        private UIGradient prevGradient;
        private UIGradient playGradient;

        // sprites loaded from the asset bundle
        private Sprite playSprite;
        private Sprite pauseSprite;

        // kept so we can destroy the old texture before loading a new one
        private Texture2D oldThumbnail;

        // parsed lrc lines — each entry is a timestamp and the lyric text
        private List<(float time, string line)> lyricLines = new List<(float, string)>();

        // pre-built strings for each lyric position so we don't rebuild every frame
        private string[] prebuiltLyricFrames;

        // controls how often we poll the helper exe
        private float updateDataLatency;
        private float fastPollUntil;

        // tracks which lyric line is currently shown to avoid redundant updates
        private int lastLyricIndex = -1;

        // how many seconds ahead of the timestamp we show the next lyric
        private const float LyricLookahead = 2f;

        // how many lines above and below the current line to show
        private const int LinesAbove = 2;
        private const int LinesBelow = 4;

        // apple music uses an em-dash separator: "artist — album"
        // we strip the album portion so lyrics are fetched by artist name only
        private const string AppleMusicSeparator = " \u2014 ";

        // data from the helper exe is written here on a background thread
        // then read and applied on the main thread in update
        private class PendingMediaData
        {
            public string Title;
            public string Artist;
            public bool Paused;
            public float Position;
            public float Duration;
            public bool SongChanged;
            public string ThumbnailBase64;
        }

        // volatile so the main thread always sees the latest write
        private volatile PendingMediaData pendingData = null;

        // matches the json shape returned by lrclib
        private class LyricsResponse
        {
            public string syncedLyrics { get; set; }
            public string plainLyrics { get; set; }
        }

        public void Awake()
        {
            Instance = this;

            // extract the helper exe from the dll to a temp path so we can run it
            ExePath = Path.Combine(Path.GetTempPath(), ExeFileName);

            using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ExeResourceName);
            if (s == null)
            {
                return;
            }

            // overwrite any stale version from a previous session
            if (File.Exists(ExePath))
                File.Delete(ExePath);

            using FileStream fs = new FileStream(ExePath, FileMode.Create, FileAccess.Write);
            s.CopyTo(fs);
        }

        public void Initialize(AssetBundle bundle)
        {
            // find all the ui elements by their hierarchy path
            Transform root = Menu.MenuInstance.transform;

            thumbnailImage = root.Find("Background/Thumbnail")?.GetComponent<Image>();
            songNameText = root.Find("Background/SongName")?.GetComponent<TextMeshProUGUI>();
            songArtistText = root.Find("Background/SongArtist")?.GetComponent<TextMeshProUGUI>();
            timeStampStartText = root.Find("Background/TimeStampStart")?.GetComponent<TextMeshProUGUI>();
            timeStampEndText = root.Find("Background/TimeStampEnd")?.GetComponent<TextMeshProUGUI>();
            lyricsText = root.Find("Background/Lyrics")?.GetComponent<TextMeshProUGUI>();
            progressFill = root.Find("Background/ProgressBar/Background/Fill")?.GetComponent<Image>();
            backgroundImage = root.Find("Background")?.GetComponent<Image>();
            skipButton = root.Find("Background/Skip")?.GetComponent<Image>();
            prevButton = root.Find("Background/Prev")?.GetComponent<Image>();
            playPauseIcon = root.Find("Background/Play/Icon")?.GetComponent<Image>();
            playButton = root.Find("Background/Play")?.GetComponent<Image>();

            // load the play and pause sprites from the bundle
            playSprite = bundle.LoadAsset<Sprite>("play");
            pauseSprite = bundle.LoadAsset<Sprite>("pause");

            // add gradient components so we can tint them from the album art later
            backgroundGradient = backgroundImage?.gameObject.AddComponent<UIGradient>();
            skipGradient = skipButton?.gameObject.AddComponent<UIGradient>();
            prevGradient = prevButton?.gameObject.AddComponent<UIGradient>();
            playGradient = playButton?.gameObject.AddComponent<UIGradient>();

            // let the lyrics box shrink text to fit rather than overflow or clip
            if (lyricsText != null)
            {
                lyricsText.enableAutoSizing = true;
                lyricsText.fontSizeMin = 20f;
                lyricsText.fontSizeMax = 26f;
                lyricsText.overflowMode = TextOverflowModes.Overflow;
                lyricsText.enableWordWrapping = true;
            }
        }

        public void OnEnable()
        {
            // reset timers so we poll immediately when the component turns on
            updateDataLatency = 0f;
            fastPollUntil = Time.time + 3f;
        }

        public void Update()
        {
            // poll faster right after a song change, slower otherwise
            float interval = Time.time < fastPollUntil ? 1f : 5f;

            if (Time.time > updateDataLatency)
            {
                updateDataLatency = Time.time + interval;
                StartCoroutine(UpdateDataCoroutine());
            }

            // apply any data that arrived from the background thread
            if (pendingData != null)
            {
                ApplyPendingData(pendingData);
                pendingData = null;
            }

            if (!Menu.MenuOpen) return;

            // tick the position forward ourselves so the bar moves smoothly
            if (!Paused && Duration > 0f)
            {
                Position += Time.deltaTime;
                Position = Mathf.Clamp(Position, 0f, Duration);
                UpdateProgressBar();
                UpdateTimestamps();
            }

            if (lyricLines.Count > 0)
                UpdateLyrics();
        }

        public void ForceRefresh()
        {
            // force lyric and progress updates — used when the menu first opens
            lastLyricIndex = -2;
            UpdateProgressBar();
            UpdateTimestamps();
            UpdateLyrics();
        }

        public static async Task UpdateDataAsync()
        {
            // run the helper exe and read its json output
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = "-all",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using Process proc = new Process { StartInfo = psi };
            proc.Start();
            string output = await proc.StandardOutput.ReadToEndAsync();
            await Task.Run(() => proc.WaitForExit());

            if (string.IsNullOrEmpty(output)) return;

            try
            {
                Instance?.ParseMediaData(output);
            }
            catch { }
        }

        private void ParseMediaData(string json)
        {
            Dictionary<string, object> data = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);

            string newTitle = GetString(data, "Title");
            string rawArtist = GetString(data, "Artist");

            // apple music formats the artist field as "artistname — albumname"
            // strip the album so we only send the real artist name to lyric lookups
            string newArtist = StripAppleMusicAlbum(rawArtist);

            // store the result so the main thread can pick it up in update
            pendingData = new PendingMediaData
            {
                Title = newTitle,
                Artist = newArtist,
                Paused = GetString(data, "Status") != "Playing",
                Position = GetFloat(data, "ElapsedTime"),
                Duration = GetFloat(data, "EndTime"),
                SongChanged = newTitle != Title,
                ThumbnailBase64 = GetString(data, "ThumbnailBase64")
            };
        }

        // apple music reports the artist field as "{artistName} — {albumName}"
        // this strips everything after the em-dash so lyrics fetch correctly
        private static string StripAppleMusicAlbum(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            int idx = raw.IndexOf(AppleMusicSeparator, StringComparison.Ordinal);
            return idx >= 0 ? raw.Substring(0, idx).Trim() : raw;
        }

        private void ApplyPendingData(PendingMediaData d)
        {
            bool songChanged = d.SongChanged;

            // poll more aggressively for a few seconds after a track change
            if (songChanged)
                fastPollUntil = Time.time + 5f;

            Title = d.Title;
            Artist = d.Artist;
            Paused = d.Paused;
            Position = d.Position;
            Duration = d.Duration;
            ValidData = true;

            if (songNameText != null) songNameText.text = Title;
            if (songArtistText != null) songArtistText.text = Artist;

            UpdateProgressBar();
            UpdateTimestamps();
            UpdatePlayPauseIcon();

            if (songChanged)
            {
                // clear old lyric data and kick off a new fetch
                lyricLines.Clear();
                prebuiltLyricFrames = null;
                lastLyricIndex = -1;
                if (lyricsText != null) lyricsText.text = "";
                _ = FetchLyrics(Title, Artist);
            }

            // clear the thumbnail if the song changed and no new one arrived yet
            if (songChanged && string.IsNullOrEmpty(d.ThumbnailBase64))
                if (thumbnailImage != null) thumbnailImage.sprite = null;

            if (!string.IsNullOrEmpty(d.ThumbnailBase64))
                LoadThumbnail(d.ThumbnailBase64);
        }

        public IEnumerator UpdateDataCoroutine(float delay = 0f)
        {
            if (delay > 0f)
                yield return new WaitForSeconds(delay);

            // wait for the async task without blocking the main thread
            Task task = UpdateDataAsync();
            while (!task.IsCompleted)
                yield return null;
        }

        private void UpdateProgressBar()
        {
            if (progressFill == null || Duration <= 0f) return;
            progressFill.fillAmount = Mathf.Clamp01(Position / Duration);
        }

        private void UpdateTimestamps()
        {
            if (timeStampStartText != null) timeStampStartText.text = FormatTime(Position);
            if (timeStampEndText != null) timeStampEndText.text = FormatTime(Duration);
        }

        private void UpdatePlayPauseIcon()
        {
            if (playPauseIcon == null) return;
            // show play when paused, pause when playing
            playPauseIcon.sprite = Paused ? playSprite : pauseSprite;
        }

        private async Task FetchLyrics(string title, string artist)
        {
            try
            {
                // build a safe filename from the title and artist for caching
                string safeName = $"{title}_{artist}".Replace(" ", "_");
                foreach (char c in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(c, '_');
                string cacheFile = Path.Combine(Path.GetTempPath(), $"lrc_{safeName}.txt");

                string lrc = null;

                if (File.Exists(cacheFile))
                {
                    // load from disk instead of hitting the api again
                    lrc = await Task.Run(() => File.ReadAllText(cacheFile));
                }
                else
                {
                    string url = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
                    using HttpClient http = new HttpClient();
                    string json = await http.GetStringAsync(url);
                    var result = JsonConvert.DeserializeObject<LyricsResponse>(json);

                    if (result != null && !string.IsNullOrEmpty(result.syncedLyrics))
                    {
                        // synced lyrics have timestamps — prefer these
                        lrc = result.syncedLyrics;
                        await Task.Run(() => File.WriteAllText(cacheFile, lrc));
                    }
                    else if (result != null && !string.IsNullOrEmpty(result.plainLyrics))
                    {
                        // no timestamps available, just show the plain text
                        if (lyricsText != null) lyricsText.text = $"<color=#909090>{result.plainLyrics}</color>";
                        return;
                    }
                }

                if (!string.IsNullOrEmpty(lrc))
                    ParseLyrics(lrc);
            }
            catch { }
        }

        private void ParseLyrics(string lrc)
        {
            lyricLines.Clear();

            // each line looks like "[mm:ss.xx] lyric text"
            foreach (string line in lrc.Split('\n'))
            {
                if (line.Length < 10 || line[0] != '[') continue;
                int close = line.IndexOf(']');
                if (close < 0) continue;
                string timeStr = line.Substring(1, close - 1);
                string text = line.Substring(close + 1).Trim();
                string[] parts = timeStr.Split(':');
                if (parts.Length != 2) continue;
                if (float.TryParse(parts[0], out float mins) &&
                    float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float secs))
                    lyricLines.Add((mins * 60f + secs, text));
            }

            PreBuildLyricFrames();
        }

        private void PreBuildLyricFrames()
        {
            // build one string per lyric line so updating the text is just an assignment
            lastLyricIndex = -2;
            prebuiltLyricFrames = new string[lyricLines.Count];

            for (int active = 0; active < lyricLines.Count; active++)
            {
                int start = Mathf.Max(0, active - LinesAbove);
                int end = Mathf.Min(lyricLines.Count - 1, active + LinesBelow);

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                for (int i = start; i <= end; i++)
                {
                    if (string.IsNullOrWhiteSpace(lyricLines[i].line)) continue;

                    if (i == active)
                        // current line is white and slightly larger
                        sb.AppendLine($"<color=#FFFFFF><size=105%>{lyricLines[i].line}</size></color>");
                    else
                    {
                        // lines further away get progressively darker
                        int distance = Mathf.Abs(i - active);
                        string hex = distance == 1 ? "#909090" : distance == 2 ? "#606060" : "#404040";
                        sb.AppendLine($"<color={hex}>{lyricLines[i].line}</color>");
                    }
                }
                prebuiltLyricFrames[active] = sb.ToString();
            }
        }

        private void UpdateLyrics()
        {
            if (lyricsText == null || lyricLines.Count == 0 || prebuiltLyricFrames == null) return;

            // find the last line whose timestamp we've passed (with lookahead)
            int currentIndex = -1;
            float adjustedPosition = Position + LyricLookahead;
            for (int i = 0; i < lyricLines.Count; i++)
            {
                if (adjustedPosition >= lyricLines[i].time)
                    currentIndex = i;
                else
                    break;
            }

            // skip the update if the line hasn't changed
            if (currentIndex == lastLyricIndex) return;
            lastLyricIndex = currentIndex;

            if (currentIndex >= 0 && currentIndex < prebuiltLyricFrames.Length)
                lyricsText.text = prebuiltLyricFrames[currentIndex];
        }

        private void LoadThumbnail(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return;
            try
            {
                byte[] bytes = Convert.FromBase64String(base64);

                // destroy the previous texture to free gpu memory
                if (oldThumbnail != null) Destroy(oldThumbnail);

                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.LoadImage(bytes);
                tex.Apply();
                Icon = tex;
                oldThumbnail = tex;

                if (thumbnailImage != null)
                    thumbnailImage.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));

                ApplyThumbnailColors(tex);
            }
            catch { }
        }

        private void ApplyThumbnailColors(Texture2D tex)
        {
            if (tex == null) return;

            // scale down to 32x32 so averaging the color is cheap
            Texture2D small = ScaleDown(tex, 32);
            Color[] pixels = small.GetPixels();
            Destroy(small);

            // average all the pixels to get the dominant color
            float r = 0, g = 0, b = 0;
            foreach (Color c in pixels) { r += c.r; g += c.g; b += c.b; }
            int count = pixels.Length;
            Color avg = new Color(r / count, g / count, b / count);

            // top is lighter, bottom is darker — gives a natural gradient feel
            Color top = Color.Lerp(avg, Color.white, 0.3f);
            Color bottom = Color.Lerp(avg, Color.black, 0.5f);

            // reset base colors to white so the gradient tints cleanly
            if (backgroundImage != null) backgroundImage.color = Color.white;
            if (skipButton != null) skipButton.color = Color.white;
            if (prevButton != null) prevButton.color = Color.white;
            if (playButton != null) playButton.color = Color.white;

            backgroundGradient?.SetColors(top, bottom);
            skipGradient?.SetColors(top, bottom);
            prevGradient?.SetColors(top, bottom);
            playGradient?.SetColors(top, bottom);
        }

        private Texture2D ScaleDown(Texture2D src, int size = 32)
        {
            // blit to a render texture then read the pixels back into a new texture
            RenderTexture rt = RenderTexture.GetTemporary(size, size);
            Graphics.Blit(src, rt);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D result = new Texture2D(size, size, TextureFormat.RGBA32, false);
            result.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            result.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return result;
        }

        private static string GetString(Dictionary<string, object> data, string key)
        {
            return data.TryGetValue(key, out object value) ? value?.ToString() ?? "" : "";
        }

        private static float GetFloat(Dictionary<string, object> data, string key)
        {
            if (data.TryGetValue(key, out object value))
                try { return Convert.ToSingle(value); } catch { }
            return 0f;
        }

        private string FormatTime(float seconds)
        {
            // turns a raw second count into "m:ss"
            int m = (int)seconds / 60;
            int s = (int)seconds % 60;
            return $"{m}:{s:D2}";
        }

        public void PauseTrack()
        {
            Paused = !Paused;
            SendKey(VirtualKeyCodes.PLAY_PAUSE);
            UpdatePlayPauseIcon();
        }

        public void PreviousTrack()
        {
            // speed up polling and reset position, then send the media key
            fastPollUntil = Time.time + 5f;
            updateDataLatency = 0f;
            StartCoroutine(UpdateDataCoroutine(0.1f));
            Position = 0f;
            SendKey(VirtualKeyCodes.PREVIOUS_TRACK);
        }

        public void SkipTrack()
        {
            // same as previous but sends the next track key
            fastPollUntil = Time.time + 5f;
            updateDataLatency = 0f;
            StartCoroutine(UpdateDataCoroutine(0.1f));
            Position = 0f;
            SendKey(VirtualKeyCodes.NEXT_TRACK);
        }

        // calls the win32 keyboard input api to send a media key
        [DllImport("user32.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall)]
        internal static extern void keybd_event(uint bVk, uint bScan, uint dwFlags, uint dwExtraInfo);
        internal static void SendKey(VirtualKeyCodes code) => keybd_event((uint)code, 0, 0, 0);

        // virtual key codes for the three media keys we need
        internal enum VirtualKeyCodes : uint
        {
            NEXT_TRACK = 0xB0,
            PREVIOUS_TRACK = 0xB1,
            PLAY_PAUSE = 0xB3
        }
    }
}