using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Windows.Speech;
using Valve.VR;
using Debug = UnityEngine.Debug;

namespace ChqserMedia
{
    public class SpotifyBrowser : MonoBehaviour
    {
        // how fast the list scrolls when you move the stick
        private const float ScrollSpeed = 420f;

        // how smooth the scrolling feels (higher = snappier)
        private const float ScrollLerp = 8f;

        // ignore tiny stick movements below this value
        private const float StickDeadZone = 0.15f;

        // how many rows are visible at once
        private const float VisibleRows = 5f;

        // which screen we are currently showing
        private enum View { Hidden, Playlists, Tracks }
        private View _view = View.Hidden;

        // the list of playlists and tracks we fetched from spotify
        private List<SpotifyPlaylist> _playlists = new List<SpotifyPlaylist>();
        private List<SpotifyTrack> _tracks = new List<SpotifyTrack>();

        // the playlist the user tapped on
        private SpotifyPlaylist _activePlaylist;

        // true while we are waiting for data from spotify
        private bool _loading;

        // the playlist panel and all its ui pieces
        private GameObject _playlistPanel;
        private TextMeshProUGUI _plHeader;
        private TextMeshProUGUI _plStatus;
        private RectTransform _plScrollContent;
        private GameObject[] _plRows;
        private TextMeshProUGUI[] _plMain;
        private TextMeshProUGUI[] _plSub;

        // the track panel and all its ui pieces
        private GameObject _trackPanel;
        private TextMeshProUGUI _trHeader;
        private TextMeshProUGUI _trStatus;
        private RectTransform _trScrollContent;
        private GameObject[] _trRows;
        private TextMeshProUGUI[] _trMain;
        private TextMeshProUGUI[] _trSub;

        // scroll position values for each panel
        private float _plTargetY, _plCurrentY, _plMinY;
        private float _trTargetY, _trCurrentY, _trMinY;

        // height of one row in pixels
        private float _rowHeight = 86f;

        // voice search state for "press A to speak" track search
        private bool _aWasDown;
        private bool _voiceListening;
        private float _voiceCooldownUntil;
        private Coroutine _voiceTimeoutRoutine;
        private KeywordRecognizer _voiceCommandRecognizer;
        private readonly Dictionary<string, int> _voicePhraseToTrackIndex =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private const float VoiceTimeoutSeconds = 5f;

        public static SpotifyBrowser Instance { get; private set; }

        void Awake() => Instance = this;

        // called once at startup to find all the ui objects and wire up buttons
        public void Initialize(Transform menuRoot)
        {
            SpotifyConfig.Load();

            SetupPanel(menuRoot, "SpotifyPlaylistPanel",
                out _playlistPanel, out _plHeader, out _plStatus,
                out _plScrollContent, out _plRows, out _plMain, out _plSub,
                onBack: OnBack,
                onUp: () => NudgeScroll(ref _plTargetY, +1, _plMinY),
                onDown: () => NudgeScroll(ref _plTargetY, -1, _plMinY));

            SetupPanel(menuRoot, "SpotifyTrackPanel",
                out _trackPanel, out _trHeader, out _trStatus,
                out _trScrollContent, out _trRows, out _trMain, out _trSub,
                onBack: OnBack,
                onUp: () => NudgeScroll(ref _trTargetY, +1, _trMinY),
                onDown: () => NudgeScroll(ref _trTargetY, -1, _trMinY));

            _plMinY = ComputeMinY(0);
            _trMinY = ComputeMinY(0);

            // find the spotify button on the now playing screen and hook it up
            GameObject nowPlaying = menuRoot.Find("Background")?.gameObject;
            if (nowPlaying != null)
                AddEntryButton(nowPlaying);
        }

        void Update()
        {
            if (_view == View.Hidden) return;
            ReadJoystickScroll();
            ApplySmoothScroll();
            HandleVoiceSearchInput();
        }

        // read the right stick and move the scroll target, faster if trigger or grip is held
        private void ReadJoystickScroll()
        {
            float axis = 0f;
            float trigger = 0f;
            float grip = 0f;
            try
            {
                axis = SteamVR_Actions.gorillaTag_RightJoystick2DAxis
                           .GetAxis(SteamVR_Input_Sources.RightHand).y;
                trigger = SteamVR_Actions.gorillaTag_RightTriggerFloat
                           .GetAxis(SteamVR_Input_Sources.RightHand);
                grip = SteamVR_Actions.gorillaTag_RightGripFloat
                           .GetAxis(SteamVR_Input_Sources.RightHand);
            }
            catch { return; }

            if (Mathf.Abs(axis) < StickDeadZone) return;

            float speed = ScrollSpeed;
            if (trigger > 0.5f) speed *= 3f; // trigger = 3x speed
            if (grip > 0.5f) speed *= 3f;    // grip on top of trigger = 9x speed

            float delta = axis * speed * Time.deltaTime;

            if (_view == View.Playlists)
                _plTargetY = Mathf.Clamp(_plTargetY + delta, _plMinY, 0f);
            else if (_view == View.Tracks)
                _trTargetY = Mathf.Clamp(_trTargetY + delta, _trMinY, 0f);
        }

        // smoothly move the current scroll position toward the target each frame
        private void ApplySmoothScroll()
        {
            float t = ScrollLerp * Time.deltaTime;

            if (_view == View.Playlists && _plScrollContent != null)
            {
                _plCurrentY = Mathf.Lerp(_plCurrentY, _plTargetY, t);
                UpdateVirtualRows(_plRows, _plMain, _plSub, _plCurrentY,
                    i => i < _playlists.Count,
                    i => _playlists[i].Name,
                    i => $"{_playlists[i].TrackCount} tracks");
            }
            else if (_view == View.Tracks && _trScrollContent != null)
            {
                _trCurrentY = Mathf.Lerp(_trCurrentY, _trTargetY, t);
                UpdateVirtualRows(_trRows, _trMain, _trSub, _trCurrentY,
                    i => i < _tracks.Count,
                    i => _tracks[i].Name,
                    i => $"{_tracks[i].Artist}  •  {FormatMs(_tracks[i].Duration)}");
            }
        }

        // figure out which data items are in view and update only those rows
        private void UpdateVirtualRows(
            GameObject[] rows, TextMeshProUGUI[] mains, TextMeshProUGUI[] subs,
            float currentY,
            Func<int, bool> hasItem,
            Func<int, string> getMain,
            Func<int, string> getSub)
        {
            if (rows.Length == 0) return;

            // work out which data index sits at the top of the visible area
            int firstIndex = Mathf.FloorToInt(-currentY / _rowHeight);
            firstIndex = Mathf.Max(0, firstIndex);

            for (int i = 0; i < rows.Length; i++)
            {
                int dataIndex = firstIndex + i;
                bool active = hasItem(dataIndex);
                rows[i].SetActive(active);
                if (active)
                {
                    mains[i].text = getMain(dataIndex);
                    subs[i].text = getSub(dataIndex);
                }
            }
        }

        // work out the furthest we can scroll down before hitting the end of the list
        private float ComputeMinY(int itemCount)
        {
            float total = itemCount * _rowHeight;
            float visible = VisibleRows * _rowHeight;
            return Mathf.Min(0f, -(total - visible));
        }

        // move the scroll target by a fixed amount when the up/down buttons are pressed
        private static void NudgeScroll(ref float target, int direction, float minY)
            => target = Mathf.Clamp(target + direction * 3f * 86f, minY, 0f);

        // snap scroll back to the top
        private static void ResetScroll(
            ref float target, ref float current, RectTransform content)
        {
            target = current = 0f;
            if (content != null)
                content.anchoredPosition = new Vector2(content.anchoredPosition.x, 0f);
        }

        // called when the spotify button on the now playing screen is tapped
        private void OnEntryClicked()
        {
            // if playlists are already open, close them
            if (_view == View.Playlists)
            {
                HideAll();
                return;
            }

            // if credentials are missing tell the user how to set them up
            if (!SpotifyConfig.IsValid)
            {
                ShowPanel(View.Playlists);
                SetText(_plStatus,
                    "Open BepInEx/config/ChqserMedia.spotify.txt and fill in your credentials.");
                return;
            }

            ShowPanel(View.Playlists);
            _ = LoadPlaylistsAsync();
        }

        // go back one level (tracks -> playlists, or playlists -> hidden)
        private void OnBack()
        {
            if (_view == View.Tracks)
            {
                ShowPanel(View.Playlists);
                RenderPlaylists();
            }
            else
            {
                HideAll();
            }
        }

        // called when the user taps a row in whichever panel is open
        private void OnRowClicked(int rowIndex)
        {
            int firstIndex = 0;
            if (_view == View.Playlists)
            {
                // work out which playlist they actually tapped (accounting for scroll)
                firstIndex = Mathf.Max(0, Mathf.FloorToInt(-_plCurrentY / _rowHeight));
                int absoluteIndex = firstIndex + rowIndex;
                if (absoluteIndex >= _playlists.Count) return;
                _activePlaylist = _playlists[absoluteIndex];
                ShowPanel(View.Tracks);
                _ = LoadTracksAsync(_activePlaylist.Id);
            }
            else if (_view == View.Tracks)
            {
                // work out which track they tapped and play it
                firstIndex = Mathf.Max(0, Mathf.FloorToInt(-_trCurrentY / _rowHeight));
                int absoluteIndex = firstIndex + rowIndex;
                if (absoluteIndex >= _tracks.Count) return;
                SelectTrack(_tracks[absoluteIndex]);
            }
        }

        // fetch all playlists from spotify and show them
        private async Task LoadPlaylistsAsync()
        {
            if (_loading) return;
            _loading = true;
            SetText(_plStatus, "Loading playlists…");

            try
            {
                _playlists = await SpotifyClient.GetPublicPlaylistsAsync();
                _plMinY = ComputeMinY(_playlists.Count);
                Debug.Log($"[SpotifyBrowser] plMinY={_plMinY} rowHeight={_rowHeight} count={_playlists.Count}");
                ResetScroll(ref _plTargetY, ref _plCurrentY, _plScrollContent);

                SetText(_plStatus, _playlists.Count == 0
                    ? "No public playlists found.  Make your playlists Public in Spotify first."
                    : "");

                RenderPlaylists();
            }
            catch (Exception e) { SetText(_plStatus, $"Error: {e.Message}"); }
            finally { _loading = false; }
        }

        // fetch all tracks in a playlist and show them
        private async Task LoadTracksAsync(string playlistId)
        {
            if (_loading) return;
            _loading = true;
            SetText(_trStatus, "Loading tracks…");

            try
            {
                _tracks = await SpotifyClient.GetPlaylistTracksAsync(playlistId);
                _trMinY = ComputeMinY(_tracks.Count);
                Debug.Log($"[SpotifyBrowser] trMinY={_trMinY} rowHeight={_rowHeight} count={_tracks.Count}");
                ResetScroll(ref _trTargetY, ref _trCurrentY, _trScrollContent);

                SetText(_trStatus, _tracks.Count == 0 ? "This playlist has no tracks." : "");
                RenderTracks();
            }
            catch (Exception e) { SetText(_trStatus, $"Error: {e.Message}"); }
            finally { _loading = false; }
        }

        // fill the visible rows with playlist names and track counts
        private void RenderPlaylists()
        {
            SetText(_plHeader, $"Playlists  ({_playlists.Count})");

            for (int i = 0; i < _plRows.Length; i++)
            {
                bool active = i < _playlists.Count;
                _plRows[i].SetActive(active);
                if (active)
                {
                    _plMain[i].text = _playlists[i].Name;
                    _plSub[i].text = $"{_playlists[i].TrackCount} tracks";
                }
            }
        }

        // fill the visible rows with track names and artist info
        private void RenderTracks()
        {
            SetText(_trHeader, _activePlaylist != null
                ? $"{_activePlaylist.Name}  ({_tracks.Count} tracks)"
                : "Tracks");

            for (int i = 0; i < _trRows.Length; i++)
            {
                bool active = i < _tracks.Count;
                _trRows[i].SetActive(active);
                if (active)
                {
                    _trMain[i].text = _tracks[i].Name;
                    _trSub[i].text = $"{_tracks[i].Artist}  •  {FormatMs(_tracks[i].Duration)}";
                }
            }

            if (_tracks.Count > 0 && !_voiceListening)
                SetText(_trStatus, "Press A to speak and find a song.");
        }

        // tell spotify to start playing this track, fetch lyrics, then close the browser
        private void SelectTrack(SpotifyTrack track)
        {
            _ = SpotifyClient.PlayTrackAsync(track.Uri, _activePlaylist?.Id);
            MediaManager.Instance?.FetchLyricsForTrack(track.Name, track.Artist);
            SetText(_trStatus, $"Playing {track.Name}");
            MediaManager.Instance?.StartCoroutine(
                MediaManager.Instance.UpdateDataCoroutine(1.5f));
        }

        // show one panel and hide the other
        private void ShowPanel(View v)
        {
            _view = v;
            _playlistPanel?.SetActive(v == View.Playlists);
            _trackPanel?.SetActive(v == View.Tracks);
        }

        // hide everything
        private void HideAll()
        {
            _view = View.Hidden;
            _playlistPanel?.SetActive(false);
            _trackPanel?.SetActive(false);
        }

        public void OnMenuClosed() => HideAll();

        private void OnDestroy()
        {
            StopVoiceListening(false);
            DisposeVoiceRecognizer();
        }

        // detect A-button press on right controller while track list is open
        private void HandleVoiceSearchInput()
        {
            if (_view != View.Tracks || _loading || _tracks.Count == 0) return;

            bool aDown = false;
            try
            {
                aDown = SteamVR_Actions.gorillaTag_RightPrimaryClick
                    .GetState(SteamVR_Input_Sources.RightHand);
            }
            catch { return; }

            if (aDown && !_aWasDown && !_voiceListening && Time.time >= _voiceCooldownUntil)
            {
                StartVoiceListening();
            }

            _aWasDown = aDown;
        }

        private void StartVoiceListening()
        {
            BuildVoiceTrackPhraseMap();
            if (_voicePhraseToTrackIndex.Count == 0)
            {
                SetText(_trStatus, "No usable track names for voice search.");
                return;
            }

            DisposeVoiceRecognizer();
            try
            {
                if (PhraseRecognitionSystem.Status == SpeechSystemStatus.Stopped)
                    PhraseRecognitionSystem.Restart();
            }
            catch { }

            _voiceCommandRecognizer = new KeywordRecognizer(
                _voicePhraseToTrackIndex.Keys.ToArray(),
                ConfidenceLevel.Low);
            _voiceCommandRecognizer.OnPhraseRecognized += OnVoiceTrackRecognized;

            _voiceListening = true;
            _voiceCooldownUntil = Time.time + 1f;
            SetText(_trStatus, "Speak now to find a song...");

            try
            {
                _voiceCommandRecognizer.Start();
                Debug.Log($"[SpotifyBrowser] Voice listening started with {_voicePhraseToTrackIndex.Count} phrases.");
            }
            catch
            {
                SetText(_trStatus, "Voice search failed to start.");
                StopVoiceListening(false);
                return;
            }

            if (_voiceTimeoutRoutine != null) StopCoroutine(_voiceTimeoutRoutine);
            _voiceTimeoutRoutine = StartCoroutine(VoiceTimeoutRoutine());
        }

        private void OnVoiceTrackRecognized(PhraseRecognizedEventArgs args)
        {
            if (!_voiceListening) return;

            string spoken = args.text?.Trim() ?? "";
            Debug.Log($"[SpotifyBrowser] Voice recognized: \"{spoken}\"");
            if (string.IsNullOrEmpty(spoken))
            {
                SetText(_trStatus, "Didn't hear a song. Press A and try again.");
                StopVoiceListening();
                return;
            }

            int trackIndex = ResolveRecognizedTrackIndex(spoken);
            if (trackIndex < 0 || trackIndex >= _tracks.Count)
            {
                SetText(_trStatus, $"No close match for \"{spoken}\".");
                StopVoiceListening();
                return;
            }

            SpotifyTrack match = _tracks[trackIndex];
            SetText(_trStatus, $"Heard \"{spoken}\". Playing {match.Name}.");

            float targetY = -(trackIndex * _rowHeight);
            _trTargetY = Mathf.Clamp(targetY, _trMinY, 0f);
            _trCurrentY = _trTargetY;
            RenderTracks();

            SelectTrack(match);
            StopVoiceListening(false);
        }

        private int ResolveRecognizedTrackIndex(string spoken)
        {
            if (_voicePhraseToTrackIndex.TryGetValue(spoken, out int idx))
                return idx;

            string spokenNorm = NormalizeForMatch(spoken);
            for (int i = 0; i < _tracks.Count; i++)
            {
                string nameNorm = NormalizeForMatch(_tracks[i].Name);
                string artistNorm = NormalizeForMatch(_tracks[i].Artist);
                if (spokenNorm == nameNorm || spokenNorm == $"{nameNorm} by {artistNorm}".Trim())
                    return i;
            }

            return -1;
        }

        private void BuildVoiceTrackPhraseMap()
        {
            _voicePhraseToTrackIndex.Clear();

            for (int i = 0; i < _tracks.Count; i++)
            {
                string title = NormalizeForMatch(_tracks[i].Name);
                string artist = NormalizeForMatch(_tracks[i].Artist);
                if (string.IsNullOrEmpty(title)) continue;

                AddTrackPhrase(title, i);
                AddTrackPhrase($"play {title}", i);

                // Add simple aliases so exact keyword matching is less strict.
                string shortTitle = SimplifyTrackTitle(title);
                if (!string.IsNullOrEmpty(shortTitle) && shortTitle != title)
                {
                    AddTrackPhrase(shortTitle, i);
                    AddTrackPhrase($"play {shortTitle}", i);
                }

                if (!string.IsNullOrEmpty(artist))
                {
                    AddTrackPhrase($"{title} by {artist}", i);
                    AddTrackPhrase($"play {title} by {artist}", i);
                }
            }
        }

        private void AddTrackPhrase(string phrase, int index)
        {
            string key = phrase.Trim();
            if (key.Length < 2) return;
            if (!_voicePhraseToTrackIndex.ContainsKey(key))
                _voicePhraseToTrackIndex[key] = index;
        }

        private IEnumerator VoiceTimeoutRoutine()
        {
            yield return new WaitForSeconds(VoiceTimeoutSeconds);
            if (!_voiceListening) yield break;
            SetText(_trStatus, "Voice timed out. Press A and try again.");
            StopVoiceListening();
        }

        private void StopVoiceListening(bool resetStatus = true)
        {
            _voiceListening = false;

            if (_voiceTimeoutRoutine != null)
            {
                StopCoroutine(_voiceTimeoutRoutine);
                _voiceTimeoutRoutine = null;
            }

            if (_voiceCommandRecognizer != null && _voiceCommandRecognizer.IsRunning)
            {
                try { _voiceCommandRecognizer.Stop(); } catch { }
            }

            if (resetStatus && _view == View.Tracks && _tracks.Count > 0)
                SetText(_trStatus, "Press A to speak and find a song.");
        }

        private void DisposeVoiceRecognizer()
        {
            if (_voiceCommandRecognizer == null) return;
            try { _voiceCommandRecognizer.OnPhraseRecognized -= OnVoiceTrackRecognized; } catch { }
            try { _voiceCommandRecognizer.Stop(); } catch { }
            try { _voiceCommandRecognizer.Dispose(); } catch { }
            _voiceCommandRecognizer = null;
        }

        private static string NormalizeForMatch(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";

            var sb = new StringBuilder(input.Length);
            for (int i = 0; i < input.Length; i++)
            {
                char c = char.ToLowerInvariant(input[i]);
                if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
                    sb.Append(c);
                else
                    sb.Append(' ');
            }

            string cleaned = sb.ToString();
            while (cleaned.Contains("  "))
                cleaned = cleaned.Replace("  ", " ");
            return cleaned.Trim();
        }

        private static string SimplifyTrackTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return "";

            string result = title;

            int paren = result.IndexOf('(');
            if (paren > 0) result = result.Substring(0, paren).Trim();

            int bracket = result.IndexOf('[');
            if (bracket > 0) result = result.Substring(0, bracket).Trim();

            int dash = result.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0) result = result.Substring(0, dash).Trim();

            string[] cutWords = { " feat ", " featuring ", " ft " };
            for (int i = 0; i < cutWords.Length; i++)
            {
                int idx = result.IndexOf(cutWords[i], StringComparison.Ordinal);
                if (idx > 0)
                {
                    result = result.Substring(0, idx).Trim();
                    break;
                }
            }

            return result;
        }

        // find all the child objects in a panel and wire up the buttons
        private void SetupPanel(
            Transform menuRoot,
            string panelName,
            out GameObject panel,
            out TextMeshProUGUI header,
            out TextMeshProUGUI status,
            out RectTransform scrollContent,
            out GameObject[] rows,
            out TextMeshProUGUI[] mains,
            out TextMeshProUGUI[] subs,
            Action onBack, Action onUp, Action onDown)
        {
            Transform t = menuRoot.Find(panelName);
            panel = t?.gameObject;

            header = FindTMP(t, "Header");
            status = FindTMP(t, "Status");
            scrollContent = t?.Find("ScrollContent")?.GetComponent<RectTransform>();

            rows = Array.Empty<GameObject>();
            mains = Array.Empty<TextMeshProUGUI>();
            subs = Array.Empty<TextMeshProUGUI>();

            if (scrollContent != null)
            {
                int count = scrollContent.childCount;
                rows = new GameObject[count];
                mains = new TextMeshProUGUI[count];
                subs = new TextMeshProUGUI[count];

                for (int i = 0; i < count; i++)
                {
                    Transform rowT = scrollContent.GetChild(i);
                    rows[i] = rowT.gameObject;
                    mains[i] = rowT.Find("Main")?.GetComponent<TextMeshProUGUI>();
                    subs[i] = rowT.Find("Sub")?.GetComponent<TextMeshProUGUI>();

                    // measure the first row to know the row height
                    if (i == 0)
                    {
                        RectTransform rrt = rowT.GetComponent<RectTransform>();
                        if (rrt != null) _rowHeight = Mathf.Max(1f, rrt.rect.height);
                    }

                    EnsureCollider(rows[i]);
                    int captured = i;
                    Menu.RegisterButton(rows[i], () => OnRowClicked(captured));
                }
            }

            RegisterPanelButton(panel, "BackButton", onBack);
            RegisterPanelButton(panel, "ScrollUpButton", onUp);
            RegisterPanelButton(panel, "ScrollDownButton", onDown);

            panel?.SetActive(false);
        }

        // find the spotify button and register a click handler on it
        private void AddEntryButton(GameObject nowPlayingRoot)
        {
            Transform existing = nowPlayingRoot.transform.Find("SpotifyButton");
            if (existing == null) return;

            EnsureCollider(existing.gameObject);
            Menu.RegisterButton(existing.gameObject, OnEntryClicked);
        }

        // find a named child button and register a click handler on it
        private void RegisterPanelButton(GameObject panel, string childName, Action act)
        {
            if (panel == null) return;
            Transform t = panel.transform.Find(childName);
            if (t == null) return;
            EnsureCollider(t.gameObject);
            Menu.RegisterButton(t.gameObject, act);
        }

        // add a box collider so the vr hand can tap this object
        private static void EnsureCollider(GameObject go)
        {
            BoxCollider old = go.GetComponent<BoxCollider>();
            if (old != null) Destroy(old);

            BoxCollider col = go.AddComponent<BoxCollider>();
            col.isTrigger = true;
            RectTransform rect = go.GetComponent<RectTransform>();
            col.size = new Vector3(rect ? rect.rect.width : 100f,
                                     rect ? rect.rect.height : 50f, 0.001f);
            col.center = Vector3.zero;
            go.layer = 2;
        }

        // find a text component by name inside a parent transform
        private static TextMeshProUGUI FindTMP(Transform root, string name)
            => root?.Find(name)?.GetComponent<TextMeshProUGUI>();

        // safely set a text label (does nothing if the label is missing)
        private static void SetText(TextMeshProUGUI label, string text)
        {
            if (label != null) label.text = text;
        }

        // turn a duration in milliseconds into "m:ss"
        private static string FormatMs(int ms)
        {
            int s = ms / 1000;
            return $"{s / 60}:{(s % 60):D2}";
        }
    }
}