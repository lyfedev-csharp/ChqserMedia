using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Valve.Newtonsoft.Json;
using Debug = UnityEngine.Debug;

namespace ChqserMedia
{
    public static class SpotifyClient
    {
        // one shared http client for all requests
        private static readonly HttpClient Http = new HttpClient();

        // the access token we got from spotify and when it expires
        private static string _accessToken;
        private static DateTime _tokenExpiry = DateTime.MinValue;

        // where spotify sends the user back after they log in
        private const string RedirectUri = "http://127.0.0.1:5000/callback";

        // the list of things we need permission to do in spotify
        private const string Scopes = "playlist-read-private playlist-read-collaborative playlist-modify-public playlist-modify-private user-library-read user-follow-read user-modify-playback-state";

        // make a random string used to prove the login request came from us
        private static string GenerateCodeVerifier()
        {
            byte[] bytes = new byte[64];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return Base64UrlEncode(bytes);
        }

        // hash the verifier so we can send it safely in the url
        private static string GenerateCodeChallenge(string verifier)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.ASCII.GetBytes(verifier));
            return Base64UrlEncode(hash);
        }

        // convert bytes to a url-safe base64 string
        private static string Base64UrlEncode(byte[] bytes)
            => Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        // true if we have a saved refresh token (so we can skip the browser login)
        public static bool IsAuthorized =>
            !string.IsNullOrEmpty(SpotifyConfig.RefreshToken);

        // get a working access token, refreshing or re-logging in if needed
        public static async Task<string> GetTokenAsync()
        {
            // token is still valid, just return it
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry)
                return _accessToken;

            // we have a saved refresh token, use it to get a new access token quietly
            if (!string.IsNullOrEmpty(SpotifyConfig.RefreshToken))
            {
                bool ok = await RefreshAccessTokenAsync();
                if (ok) return _accessToken;
            }

            // no token at all, open the browser so the user can log in
            await AuthorizeAsync();
            return _accessToken;
        }

        // open the spotify login page in the browser and wait for the user to log in
        public static async Task AuthorizeAsync()
        {
            string verifier = GenerateCodeVerifier();
            string challenge = GenerateCodeChallenge(verifier);
            string state = Guid.NewGuid().ToString("N").Substring(0, 16);

            string authUrl =
                "https://accounts.spotify.com/authorize" +
                $"?client_id={Uri.EscapeDataString(SpotifyConfig.ClientId)}" +
                $"&response_type=code" +
                $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                $"&scope={Uri.EscapeDataString(Scopes)}" +
                $"&state={state}" +
                $"&code_challenge_method=S256" +
                $"&code_challenge={challenge}";

            Process.Start(new ProcessStartInfo { FileName = authUrl, UseShellExecute = true });

            // wait for spotify to redirect back to our local listener
            string code = await ListenForCallbackAsync(state);
            if (string.IsNullOrEmpty(code))
            {
                Debug.LogError("[SpotifyClient] No auth code received.");
                return;
            }

            // swap the one-time code for actual tokens
            var req = new HttpRequestMessage(HttpMethod.Post,
                "https://accounts.spotify.com/api/token");
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["client_id"] = SpotifyConfig.ClientId,
                ["code_verifier"] = verifier
            });

            HttpResponseMessage res = await Http.SendAsync(req);
            string body = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                Debug.LogError($"[SpotifyClient] Token exchange failed: {body}");
                return;
            }

            ApplyTokenResponse(body, save: true);
        }

        // use the saved refresh token to silently get a new access token
        private static async Task<bool> RefreshAccessTokenAsync()
        {
            var req = new HttpRequestMessage(HttpMethod.Post,
                "https://accounts.spotify.com/api/token");
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = SpotifyConfig.RefreshToken,
                ["client_id"] = SpotifyConfig.ClientId
            });

            HttpResponseMessage res = await Http.SendAsync(req);
            string body = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                Debug.LogWarning($"[SpotifyClient] Refresh failed: {body}");
                SpotifyConfig.ClearRefreshToken();
                return false;
            }

            ApplyTokenResponse(body, save: true);
            return true;
        }

        // read the token response and store everything we need
        private static void ApplyTokenResponse(string json, bool save)
        {
            var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
            _accessToken = obj["access_token"]?.ToString();
            int expiresIn = Convert.ToInt32(obj["expires_in"]);
            _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 30);

            // spotify sometimes sends a new refresh token, save it if so
            if (obj.TryGetValue("refresh_token", out object rt) && rt != null)
                SpotifyConfig.SaveRefreshToken(rt.ToString());
        }

        // open a tiny web server on port 5000 and wait for spotify to send back the login code
        private static async Task<string> ListenForCallbackAsync(string expectedState)
        {
            using var listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:5000/");
            listener.Start();

            Debug.Log("[SpotifyClient] Waiting for Spotify login in browser...");

            HttpListenerContext ctx = await Task.Run(() => listener.GetContext());
            HttpListenerRequest request = ctx.Request;

            // show a success message in the browser so the user knows to close it
            string responseHtml =
                "<html><body style='font-family:sans-serif;text-align:center;margin-top:80px'>" +
                "<h2>✓ Logged in! You can close this tab and return to the game.</h2>" +
                "</body></html>";
            byte[] buffer = Encoding.UTF8.GetBytes(responseHtml);
            ctx.Response.ContentLength64 = buffer.Length;
            ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
            ctx.Response.OutputStream.Close();
            listener.Stop();

            string code = request.QueryString["code"];
            string state = request.QueryString["state"];

            // make sure this response actually came from our login request
            if (state != expectedState)
            {
                Debug.LogError("[SpotifyClient] State mismatch — possible CSRF.");
                return null;
            }

            return code;
        }

        // tell spotify to start playing a specific track uri
        public static async Task PlayTrackAsync(string trackUri)
        {
            string token = await GetTokenAsync();
            if (token == null) return;

            var req = new HttpRequestMessage(HttpMethod.Put,
                "https://api.spotify.com/v1/me/player/play");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(
                $"{{\"uris\":[\"{trackUri}\"]}}",
                Encoding.UTF8, "application/json");

            HttpResponseMessage res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                string body = await res.Content.ReadAsStringAsync();
                Debug.LogWarning($"[SpotifyClient] PlayTrack failed ({res.StatusCode}): {body}");
            }
        }

        // fetch all playlists from the logged in user's account
        public static async Task<List<SpotifyPlaylist>> GetPublicPlaylistsAsync()
        {
            string token = await GetTokenAsync();
            if (token == null) return new List<SpotifyPlaylist>();

            var playlists = new List<SpotifyPlaylist>();
            string url = "https://api.spotify.com/v1/me/playlists?limit=50";

            // spotify pages results, so we loop until there are no more pages
            while (!string.IsNullOrEmpty(url))
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                HttpResponseMessage res = await Http.SendAsync(req);
                string body = await res.Content.ReadAsStringAsync();

                if (!res.IsSuccessStatusCode)
                {
                    Debug.LogError($"[SpotifyClient] GetPlaylists failed ({res.StatusCode}): {body}");
                    break;
                }

                var page = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
                var items = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(
                    page["items"].ToString());

                foreach (var item in items)
                {
                    if (item == null) continue;

                    int trackCount = 0;
                    if (item.TryGetValue("tracks", out object tracksObj) && tracksObj != null)
                    {
                        var td = JsonConvert.DeserializeObject<Dictionary<string, object>>(tracksObj.ToString());
                        if (td != null && td.TryGetValue("total", out object tot))
                            trackCount = Convert.ToInt32(tot);
                    }

                    playlists.Add(new SpotifyPlaylist
                    {
                        Id = item.TryGetValue("id", out object id) ? id?.ToString() ?? "" : "",
                        Name = item.TryGetValue("name", out object name) ? name?.ToString() ?? "" : "",
                        Description = item.TryGetValue("description", out object desc) ? desc?.ToString() ?? "" : "",
                        TrackCount = trackCount
                    });
                }

                // move to the next page, or stop if this was the last one
                url = page.TryGetValue("next", out object next) && next != null
                    ? next.ToString() : null;
            }

            return playlists;
        }

        // fetch all tracks inside a specific playlist
        public static async Task<List<SpotifyTrack>> GetPlaylistTracksAsync(string playlistId)
        {
            string token = await GetTokenAsync();
            if (token == null) return new List<SpotifyTrack>();

            // quick check that our token actually works
            var testReq = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me");
            testReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var testRes = await Http.SendAsync(testReq);

            var tracks = new List<SpotifyTrack>();
            string url = $"https://api.spotify.com/v1/playlists/{Uri.EscapeDataString(playlistId)}/items?limit=50";

            while (!string.IsNullOrEmpty(url))
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                HttpResponseMessage res = await Http.SendAsync(req);
                string body = await res.Content.ReadAsStringAsync();

                Debug.Log($"[SpotifyClient] Raw response: {body.Substring(0, Mathf.Min(500, body.Length))}");

                if (!res.IsSuccessStatusCode)
                {
                    Debug.LogError($"[SpotifyClient] GetPlaylistTracks failed ({res.StatusCode}): {body}");
                    break;
                }

                var page = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
                var items = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(page["items"].ToString());

                foreach (var item in items)
                {
                    if (item == null) continue;

                    // the actual track data is nested under the "item" key
                    Dictionary<string, object> track = null;
                    if (item.TryGetValue("item", out object trackObj) && trackObj != null)
                        track = JsonConvert.DeserializeObject<Dictionary<string, object>>(trackObj.ToString());

                    if (track == null) continue;

                    // skip podcasts and other non-track items
                    if (track.TryGetValue("type", out object typeObj) && typeObj?.ToString() != "track") continue;

                    // build a joined artist name like "artist1 & artist2"
                    string artist = "";
                    if (track.TryGetValue("artists", out object artistsObj) && artistsObj != null)
                    {
                        var artistList = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(artistsObj.ToString());
                        var names = new List<string>();
                        if (artistList != null)
                            foreach (var a in artistList)
                                if (a.TryGetValue("name", out object n) && n != null)
                                    names.Add(n.ToString());
                        artist = string.Join(" & ", names);
                    }

                    string album = "";
                    if (track.TryGetValue("album", out object albumObj) && albumObj != null)
                    {
                        var albumDict = JsonConvert.DeserializeObject<Dictionary<string, object>>(albumObj.ToString());
                        if (albumDict != null && albumDict.TryGetValue("name", out object an))
                            album = an?.ToString() ?? "";
                    }

                    int durationMs = 0;
                    if (track.TryGetValue("duration_ms", out object dur))
                        durationMs = Convert.ToInt32(dur);

                    tracks.Add(new SpotifyTrack
                    {
                        Id = track.TryGetValue("id", out object tid) ? tid?.ToString() ?? "" : "",
                        Name = track.TryGetValue("name", out object tn) ? tn?.ToString() ?? "" : "",
                        Uri = track.TryGetValue("uri", out object uri) ? uri?.ToString() ?? "" : "",
                        Artist = artist,
                        Album = album,
                        Duration = durationMs
                    });
                }

                Debug.Log($"[SpotifyClient] Parsed {tracks.Count} tracks so far");

                url = page.TryGetValue("next", out object next) && next != null
                    ? next.ToString() : null;
            }

            return tracks;
        }
    }

    // holds info about one spotify playlist
    public class SpotifyPlaylist
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int TrackCount { get; set; }
        public string ImageUrl { get; set; }
    }

    // holds info about one spotify track
    public class SpotifyTrack
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Artist { get; set; }
        public string Album { get; set; }
        public int Duration { get; set; }
        public string Uri { get; set; }
    }
}