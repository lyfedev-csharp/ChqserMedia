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
        public static string Title { get; private set; } = "Unknown";
        public static string Artist { get; private set; } = "Unknown";
        public static Texture2D Icon { get; private set; } = new Texture2D(2, 2);
        public static bool Paused { get; private set; } = true;
        public static bool ValidData { get; private set; }
        public static float Position { get; private set; }
        public static float Duration { get; private set; }
        public static string ExePath { get; private set; }
        public static MediaManager Instance { get; private set; }

        private const string ExeResourceName = "ChqserMedia.Resources.GTMediaController.exe";
        private const string ExeFileName = "GTMediaController.exe";

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
        private UIGradient backgroundGradient;
        private UIGradient skipGradient;
        private UIGradient prevGradient;
        private UIGradient playGradient;
        private Sprite playSprite;
        private Sprite pauseSprite;
        private Texture2D oldThumbnail;

        private List<(float time, string line)> lyricLines = new List<(float, string)>();
        private string[] prebuiltLyricFrames;
        private float updateDataLatency;
        private float fastPollUntil;
        private int lastLyricIndex = -1;

        private const float LyricLookahead = 2f;
        private const int LinesAbove = 1;
        private const int LinesBelow = 3;

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

        private volatile PendingMediaData pendingData = null;

        private class LyricsResponse
        {
            public string syncedLyrics { get; set; }
            public string plainLyrics { get; set; }
        }

        public void Awake()
        {
            Instance = this;

            ExePath = Path.Combine(Path.GetTempPath(), ExeFileName);

            using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ExeResourceName);
            if (s == null)
            {
                return;
            }

            if (File.Exists(ExePath))
                File.Delete(ExePath);

            using FileStream fs = new FileStream(ExePath, FileMode.Create, FileAccess.Write);
            s.CopyTo(fs);
        }

        public void Initialize(AssetBundle bundle)
        {
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
            playSprite = bundle.LoadAsset<Sprite>("play");
            pauseSprite = bundle.LoadAsset<Sprite>("pause");
            playPauseIcon = root.Find("Background/Play/Icon")?.GetComponent<Image>();
            playButton = root.Find("Background/Play")?.GetComponent<Image>();

            backgroundGradient = backgroundImage?.gameObject.AddComponent<UIGradient>();
            skipGradient = skipButton?.gameObject.AddComponent<UIGradient>();
            prevGradient = prevButton?.gameObject.AddComponent<UIGradient>();
            playGradient = playButton?.gameObject.AddComponent<UIGradient>();
        }

        public void OnEnable()
        {
            updateDataLatency = 0f;
            fastPollUntil = Time.time + 3f;
        }

        public void Update()
        {
            float interval = Time.time < fastPollUntil ? 1f : 5f;

            if (Time.time > updateDataLatency)
            {
                updateDataLatency = Time.time + interval;
                StartCoroutine(UpdateDataCoroutine());
            }

            if (pendingData != null)
            {
                ApplyPendingData(pendingData);
                pendingData = null;
            }

            if (!Menu.MenuOpen) return;

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
            lastLyricIndex = -2;
            UpdateProgressBar();
            UpdateTimestamps();
            UpdateLyrics();
        }

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
            string newArtist = GetString(data, "Artist");

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

        private void ApplyPendingData(PendingMediaData d)
        {
            bool songChanged = d.SongChanged;

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
                lyricLines.Clear();
                prebuiltLyricFrames = null;
                lastLyricIndex = -1;
                if (lyricsText != null) lyricsText.text = "";
                _ = FetchLyrics(Title, Artist);
            }

            if (songChanged && string.IsNullOrEmpty(d.ThumbnailBase64))
                if (thumbnailImage != null) thumbnailImage.sprite = null;

            if (!string.IsNullOrEmpty(d.ThumbnailBase64))
                LoadThumbnail(d.ThumbnailBase64);
        }

        public IEnumerator UpdateDataCoroutine(float delay = 0f)
        {
            if (delay > 0f)
                yield return new WaitForSeconds(delay);

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
            playPauseIcon.sprite = Paused ? playSprite : pauseSprite;
        }

        private async Task FetchLyrics(string title, string artist)
        {
            try
            {
                string safeName = $"{title}_{artist}".Replace(" ", "_");
                foreach (char c in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(c, '_');
                string cacheFile = Path.Combine(Path.GetTempPath(), $"lrc_{safeName}.txt");

                string lrc = null;

                if (File.Exists(cacheFile))
                {
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
                        lrc = result.syncedLyrics;
                        await Task.Run(() => File.WriteAllText(cacheFile, lrc));
                    }
                    else if (result != null && !string.IsNullOrEmpty(result.plainLyrics))
                    {
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
                        sb.AppendLine($"<color=#FFFFFF><size=105%>{lyricLines[i].line}</size></color>");
                    else
                    {
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

            int currentIndex = -1;
            float adjustedPosition = Position + LyricLookahead;
            for (int i = 0; i < lyricLines.Count; i++)
            {
                if (adjustedPosition >= lyricLines[i].time)
                    currentIndex = i;
                else
                    break;
            }

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
                if (oldThumbnail != null) Destroy(oldThumbnail);
                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.LoadImage(bytes);
                tex.Apply();
                Icon = tex;
                oldThumbnail = tex;
                if (thumbnailImage != null) thumbnailImage.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                ApplyThumbnailColors(tex);
            }
            catch { }
        }

        private void ApplyThumbnailColors(Texture2D tex)
        {
            if (tex == null) return;
            Texture2D small = ScaleDown(tex, 32);
            Color[] pixels = small.GetPixels();
            Destroy(small);

            float r = 0, g = 0, b = 0;
            foreach (Color c in pixels) { r += c.r; g += c.g; b += c.b; }
            int count = pixels.Length;
            Color avg = new Color(r / count, g / count, b / count);

            Color top = Color.Lerp(avg, Color.white, 0.3f);
            Color bottom = Color.Lerp(avg, Color.black, 0.5f);

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
            fastPollUntil = Time.time + 5f;
            updateDataLatency = 0f;
            StartCoroutine(UpdateDataCoroutine(0.1f));
            Position = 0f;
            SendKey(VirtualKeyCodes.PREVIOUS_TRACK);
        }

        public void SkipTrack()
        {
            fastPollUntil = Time.time + 5f;
            updateDataLatency = 0f;
            StartCoroutine(UpdateDataCoroutine(0.1f));
            Position = 0f;
            SendKey(VirtualKeyCodes.NEXT_TRACK);
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall)]
        internal static extern void keybd_event(uint bVk, uint bScan, uint dwFlags, uint dwExtraInfo);
        internal static void SendKey(VirtualKeyCodes code) => keybd_event((uint)code, 0, 0, 0);

        internal enum VirtualKeyCodes : uint
        {
            NEXT_TRACK = 0xB0,
            PREVIOUS_TRACK = 0xB1,
            PLAY_PAUSE = 0xB3
        }
    }
}