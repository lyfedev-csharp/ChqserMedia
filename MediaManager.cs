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

        // path on disk where the helper exe is written when the game starts
        public static string ExePath { get; private set; }

        // the single instance of this class so other scripts can call it
        public static MediaManager Instance { get; private set; }

        // the helper exe is stored inside the dll and extracted at runtime
        private const string ExeResourceName = "ChqserMedia.Resources.GTMediaController.exe";
        private const string ExeFileName = "GTMediaController.exe";

        // ui elements found by path in the scene hierarchy
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

        // the spotify browser ui elements that also get the album color gradient
        private Image spotifyButtonImage;
        private Image spotifyPlaylistPanelImage;
        private Image spotifyTrackPanelImage;

        // gradient components added at runtime to tint each ui element
        private UIGradient backgroundGradient;
        private UIGradient skipGradient;
        private UIGradient prevGradient;
        private UIGradient playGradient;
        private UIGradient spotifyButtonGradient;
        private UIGradient spotifyPlaylistPanelGradient;
        private UIGradient spotifyTrackPanelGradient;

        // the play and pause icons loaded from the asset bundle
        private Sprite playSprite;
        private Sprite pauseSprite;

        // the previous thumbnail texture so we can destroy it before loading a new one
        private Texture2D oldThumbnail;

        // each entry is a timestamp (in seconds) and the lyric text for that moment
        private List<(float time, string line)> lyricLines = new List<(float, string)>();

        // one pre-built string per lyric position so we only build them once
        private string[] prebuiltLyricFrames;

        // controls how often we ask the helper exe for new data
        private float updateDataLatency;
        private float fastPollUntil;

        // tracks which lyric line is showing so we don't update unnecessarily
        private int lastLyricIndex = -1;

        // show the lyric slightly before its actual timestamp so it feels natural
        private const float LyricLookahead = 2f;

        // how many lyric lines to show above and below the current one
        private const int LinesAbove = 2;
        private const int LinesBelow = 4;

        // apple music puts the album name after an em-dash in the artist field
        private const string AppleMusicSeparator = " \u2014 ";

        // data collected on a background thread, then applied on the main thread
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

        // volatile so the main thread always sees the freshest value
        private volatile PendingMediaData pendingData = null;

        // matches the json shape that lrclib returns
        private class LyricsResponse
        {
            public string syncedLyrics { get; set; }
            public string plainLyrics { get; set; }
        }

        public void Awake()
        {
            Instance = this;

            // extract the helper exe from inside the dll to a temp folder so we can run it
            ExePath = Path.Combine(Path.GetTempPath(), ExeFileName);

            using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ExeResourceName);
            if (s == null) return;

            // always overwrite so we don't run a stale version from last session
            if (File.Exists(ExePath))
                File.Delete(ExePath);

            using FileStream fs = new FileStream(ExePath, FileMode.Create, FileAccess.Write);
            s.CopyTo(fs);
        }

        public void Initialize(AssetBundle bundle)
        {
            Transform root = Menu.MenuInstance.transform;

            // find every ui element we need by its path in the hierarchy
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

            // find the spotify browser panels so we can tint them with the album color too
            spotifyButtonImage = root.Find("Background/SpotifyButton")?.GetComponent<Image>();
            spotifyPlaylistPanelImage = root.Find("Background/SpotifyPlaylistPanel")?.GetComponent<Image>();
            spotifyTrackPanelImage = root.Find("Background/SpotifyTrackPanel")?.GetComponent<Image>();

            // load the play and pause sprites from the asset bundle
            playSprite = bundle.LoadAsset<Sprite>("play");
            pauseSprite = bundle.LoadAsset<Sprite>("pause");

            // add gradient components to every image that should receive the album tint
            backgroundGradient = backgroundImage?.gameObject.AddComponent<UIGradient>();
            skipGradient = skipButton?.gameObject.AddComponent<UIGradient>();
            prevGradient = prevButton?.gameObject.AddComponent<UIGradient>();
            playGradient = playButton?.gameObject.AddComponent<UIGradient>();
            spotifyButtonGradient = spotifyButtonImage?.gameObject.AddComponent<UIGradient>();
            spotifyPlaylistPanelGradient = spotifyPlaylistPanelImage?.gameObject.AddComponent<UIGradient>();
            spotifyTrackPanelGradient = spotifyTrackPanelImage?.gameObject.AddComponent<UIGradient>();

            // let the lyrics text shrink to fit without clipping
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
            // reset timers so we poll right away when the component is switched on
            updateDataLatency = 0f;
            fastPollUntil = Time.time + 3f;
        }

        public void Update()
        {
            // poll faster just after a song change, slower when nothing is happening
            float interval = Time.time < fastPollUntil ? 1f : 5f;

            if (Time.time > updateDataLatency)
            {
                updateDataLatency = Time.time + interval;
                StartCoroutine(UpdateDataCoroutine());
            }

            // if the background thread finished collecting data, apply it now
            if (pendingData != null)
            {
                ApplyPendingData(pendingData);
                pendingData = null;
            }

            if (!Menu.MenuOpen) return;

            // advance the playback position ourselves so the progress bar moves smoothly
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
            // force everything to redraw immediately, used when the menu first opens
            lastLyricIndex = -2;
            UpdateProgressBar();
            UpdateTimestamps();
            UpdateLyrics();
        }

        // run the helper exe and collect its output asynchronously
        public static async Task UpdateDataAsync()
        {
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

            try { Instance?.ParseMediaData(output); }
            catch { }
        }

        // read the json from the helper exe and store it for the main thread to apply
        private void ParseMediaData(string json)
        {
            Dictionary<string, object> data = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);

            string newTitle = GetString(data, "Title");
            string rawArtist = GetString(data, "Artist");

            // apple music adds " — albumname" after the artist, strip it off
            string newArtist = StripAppleMusicAlbum(rawArtist);

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

        // strip everything after the em-dash that apple music adds to the artist field
        private static string StripAppleMusicAlbum(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            int idx = raw.IndexOf(AppleMusicSeparator, StringComparison.Ordinal);
            return idx >= 0 ? raw.Substring(0, idx).Trim() : raw;
        }

        // apply the collected data to the ui (must run on the main thread)
        private void ApplyPendingData(PendingMediaData d)
        {
            bool songChanged = d.SongChanged;

            // poll faster for a bit after the song changes
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
                // clear old lyrics and kick off a fresh fetch for the new song
                lyricLines.Clear();
                prebuiltLyricFrames = null;
                lastLyricIndex = -1;
                if (lyricsText != null) lyricsText.text = "";
                _ = FetchLyricsForTrack(Title, Artist);
            }

            // blank the thumbnail when the song changes and no new image has arrived yet
            if (songChanged && string.IsNullOrEmpty(d.ThumbnailBase64))
                if (thumbnailImage != null) thumbnailImage.sprite = null;

            if (!string.IsNullOrEmpty(d.ThumbnailBase64))
                LoadThumbnail(d.ThumbnailBase64);
        }

        // coroutine wrapper so the rest of the game keeps running while we wait
        public IEnumerator UpdateDataCoroutine(float delay = 0f)
        {
            if (delay > 0f)
                yield return new WaitForSeconds(delay);

            Task task = UpdateDataAsync();
            while (!task.IsCompleted)
                yield return null;
        }

        // move the progress bar fill to match how far through the track we are
        private void UpdateProgressBar()
        {
            if (progressFill == null || Duration <= 0f) return;
            progressFill.fillAmount = Mathf.Clamp01(Position / Duration);
        }

        // update the two timestamp labels (current position and total length)
        private void UpdateTimestamps()
        {
            if (timeStampStartText != null) timeStampStartText.text = FormatTime(Position);
            if (timeStampEndText != null) timeStampEndText.text = FormatTime(Duration);
        }

        // swap between the play and pause icons depending on the current state
        private void UpdatePlayPauseIcon()
        {
            if (playPauseIcon == null) return;
            playPauseIcon.sprite = Paused ? playSprite : pauseSprite;
        }

        // download lyrics for the current track (uses a local cache to avoid repeat requests)
        public async Task FetchLyricsForTrack(string title, string artist)
        {
            try
            {
                // build a safe filename so we can cache the lyrics on disk
                string safeName = $"{title}_{artist}".Replace(" ", "_");
                foreach (char c in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(c, '_');
                string cacheFile = Path.Combine(Path.GetTempPath(), $"lrc_{safeName}.txt");

                string lrc = null;

                if (File.Exists(cacheFile))
                {
                    // load from cache instead of hitting the api again
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
                        // synced lyrics have timestamps so they scroll with the song
                        lrc = result.syncedLyrics;
                        await Task.Run(() => File.WriteAllText(cacheFile, lrc));
                    }
                    else if (result != null && !string.IsNullOrEmpty(result.plainLyrics))
                    {
                        // no timestamps available, show the full lyrics statically
                        if (lyricsText != null) lyricsText.text = $"<color=#909090>{result.plainLyrics}</color>";
                        return;
                    }
                }

                if (!string.IsNullOrEmpty(lrc))
                    ParseLyrics(lrc);
            }
            catch { }
        }

        // turn the raw lrc text into a list of (timestamp, text) pairs
        private void ParseLyrics(string lrc)
        {
            lyricLines.Clear();

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

        // build one ready-to-display string for each lyric position so we don't rebuild every frame
        private void PreBuildLyricFrames()
        {
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
                        // highlight the current line in white and slightly bigger
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

        // every frame, check if we've moved to a new lyric line and update the text if so
        private void UpdateLyrics()
        {
            if (lyricsText == null || lyricLines.Count == 0 || prebuiltLyricFrames == null) return;

            // find the last line whose timestamp we've reached (slightly ahead for feel)
            int currentIndex = -1;
            float adjustedPosition = Position + LyricLookahead;
            for (int i = 0; i < lyricLines.Count; i++)
            {
                if (adjustedPosition >= lyricLines[i].time)
                    currentIndex = i;
                else
                    break;
            }

            // no change, skip the update
            if (currentIndex == lastLyricIndex) return;
            lastLyricIndex = currentIndex;

            if (currentIndex >= 0 && currentIndex < prebuiltLyricFrames.Length)
                lyricsText.text = prebuiltLyricFrames[currentIndex];
        }

        // decode the base64 thumbnail, create a texture, and display it
        private void LoadThumbnail(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return;
            try
            {
                byte[] bytes = Convert.FromBase64String(base64);

                // free the previous texture from gpu memory before creating a new one
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

        // sample the album art and apply a matching gradient tint to all ui panels
        private void ApplyThumbnailColors(Texture2D tex)
        {
            if (tex == null) return;

            // scale to a tiny image so averaging is cheap
            Texture2D small = ScaleDown(tex, 32);
            Color[] pixels = small.GetPixels();
            Destroy(small);

            // average every pixel to find the dominant color
            float r = 0, g = 0, b = 0;
            foreach (Color c in pixels) { r += c.r; g += c.g; b += c.b; }
            int count = pixels.Length;
            Color avg = new Color(r / count, g / count, b / count);

            // lighter at the top, darker at the bottom
            Color top = Color.Lerp(avg, Color.white, 0.3f);
            Color bottom = Color.Lerp(avg, Color.black, 0.5f);

            // reset base colors to white so the gradient tints cleanly
            if (backgroundImage != null) backgroundImage.color = Color.white;
            if (skipButton != null) skipButton.color = Color.white;
            if (prevButton != null) prevButton.color = Color.white;
            if (playButton != null) playButton.color = Color.white;
            if (spotifyButtonImage != null) spotifyButtonImage.color = Color.white;
            if (spotifyPlaylistPanelImage != null) spotifyPlaylistPanelImage.color = Color.white;
            if (spotifyTrackPanelImage != null) spotifyTrackPanelImage.color = Color.white;

            // apply the gradient to each element
            backgroundGradient?.SetColors(top, bottom);
            skipGradient?.SetColors(top, bottom);
            prevGradient?.SetColors(top, bottom);
            playGradient?.SetColors(top, bottom);
            spotifyButtonGradient?.SetColors(top, bottom);
            spotifyPlaylistPanelGradient?.SetColors(top, bottom);
            spotifyTrackPanelGradient?.SetColors(top, bottom);
        }

        // blit to a render texture then read back the pixels to create a smaller copy
        private Texture2D ScaleDown(Texture2D src, int size = 32)
        {
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

        // safely read a string value from the data dictionary
        private static string GetString(Dictionary<string, object> data, string key)
        {
            return data.TryGetValue(key, out object value) ? value?.ToString() ?? "" : "";
        }

        // safely read a float value from the data dictionary
        private static float GetFloat(Dictionary<string, object> data, string key)
        {
            if (data.TryGetValue(key, out object value))
                try { return Convert.ToSingle(value); } catch { }
            return 0f;
        }

        // turn a number of seconds into a "m:ss" string
        private string FormatTime(float seconds)
        {
            int m = (int)seconds / 60;
            int s = (int)seconds % 60;
            return $"{m}:{s:D2}";
        }

        // toggle play/pause and send the media key to the os
        public void PauseTrack()
        {
            Paused = !Paused;
            SendKey(VirtualKeyCodes.PLAY_PAUSE);
            UpdatePlayPauseIcon();
        }

        // jump to the previous track
        public void PreviousTrack()
        {
            fastPollUntil = Time.time + 5f;
            updateDataLatency = 0f;
            StartCoroutine(UpdateDataCoroutine(0.1f));
            Position = 0f;
            SendKey(VirtualKeyCodes.PREVIOUS_TRACK);
        }

        // jump to the next track
        public void SkipTrack()
        {
            fastPollUntil = Time.time + 5f;
            updateDataLatency = 0f;
            StartCoroutine(UpdateDataCoroutine(0.1f));
            Position = 0f;
            SendKey(VirtualKeyCodes.NEXT_TRACK);
        }

        // call the windows api to press a keyboard key
        [DllImport("user32.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall)]
        internal static extern void keybd_event(uint bVk, uint bScan, uint dwFlags, uint dwExtraInfo);
        internal static void SendKey(VirtualKeyCodes code) => keybd_event((uint)code, 0, 0, 0);

        // the three media key codes we need
        internal enum VirtualKeyCodes : uint
        {
            NEXT_TRACK = 0xB0,
            PREVIOUS_TRACK = 0xB1,
            PLAY_PAUSE = 0xB3
        }
    }
}