using System;
using System.IO;
using BepInEx;
using UnityEngine;

namespace ChqserMedia
{
    public static class SpotifyConfig
    {
        // the four values we read from the config file
        public static string ClientId { get; private set; } = "";
        public static string ClientSecret { get; private set; } = "";
        public static string Username { get; private set; } = "";
        public static string RefreshToken { get; private set; } = "";

        // true only when both required values are filled in
        public static bool IsValid => !string.IsNullOrEmpty(ClientId)
                                   && !string.IsNullOrEmpty(ClientSecret);

        // where the config file lives on disk
        private static readonly string ConfigPath =
            Path.Combine(Paths.ConfigPath, "ChqserMedia.spotify.txt");

        // load the file as soon as this class is first used
        static SpotifyConfig() => Load();

        public static void Load()
        {
            // if the file doesn't exist yet, create it with setup instructions
            if (!File.Exists(ConfigPath))
            {
                File.WriteAllText(ConfigPath,
                    "# ================================================================\n" +
                    "# ChqserMedia - Spotify Setup Guide\n" +
                    "# ================================================================\n" +
                    "#\n" +
                    "# STEP 1 - Create a Spotify Developer App:\n" +
                    "#   1. Go to https://developer.spotify.com/dashboard\n" +
                    "#   2. Log in with your Spotify account\n" +
                    "#   3. Click 'Create App'\n" +
                    "#   4. Fill in any name/description\n" +
                    "#   5. Under 'Which API/SDKs are you planning to use?' select 'Web API'\n" +
                    "#   6. Set Redirect URI to: http://127.0.0.1:5000/callback\n" +
                    "#   7. Once created, click 'Settings'\n" +
                    "#   8. Copy your 'Client ID' shown there\n" +
                    "#\n" +
                    "# STEP 2 - First Launch (Browser Login):\n" +
                    "#   When you open the playlist browser in game for the first time,\n" +
                    "#   your browser will open asking you to log into Spotify.\n" +
                    "#   After logging in you can close the browser - your playlists\n" +
                    "#   will load automatically. You only need to do this once.\n" +
                    "#   Your login is saved as a RefreshToken below.\n" +
                    "#\n" +
                    "# STEP 3 - Make sure Spotify is open and active on your PC:\n" +
                    "#   The playback API requires Spotify to already be running and\n" +
                    "#   have been played at least once so it has an active device.\n" +
                    "#   If playback doesn't start, click play on anything in Spotify\n" +
                    "#   first, then try selecting a track from the in-game browser.\n" +
                    "#\n" +
                    "# NOTE - Spotify Premium is required for playback control.\n" +
                    "#   Free accounts can browse playlists but cannot start playback.\n" +
                    "#\n" +
                    "# NOTE - If you ever get a 'Permissions missing' error, clear the\n" +
                    "#   RefreshToken below (set it to blank) and relaunch the game\n" +
                    "#   to re-authorize with updated permissions.\n" +
                    "#\n" +
                    "# ================================================================\n" +
                    "ClientId=\n" +
                    "ClientSecret=\n" +
                    "Username=\n" +
                    "RefreshToken=\n");
                return;
            }

            // read each line and store values we recognise
            foreach (string raw in File.ReadAllLines(ConfigPath))
            {
                string line = raw.Trim();
                if (line.StartsWith("#") || !line.Contains("=")) continue;
                int sep = line.IndexOf('=');
                string key = line.Substring(0, sep).Trim();
                string val = line.Substring(sep + 1).Trim();
                switch (key)
                {
                    case "ClientId": ClientId = val; break;
                    case "ClientSecret": ClientSecret = val; break;
                    case "Username": Username = val; break;
                    case "RefreshToken": RefreshToken = val; break;
                }
            }
        }

        // update the refresh token in memory and on disk
        public static void SaveRefreshToken(string token)
        {
            RefreshToken = token;
            if (!File.Exists(ConfigPath)) return;

            string[] lines = File.ReadAllLines(ConfigPath);
            bool found = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("RefreshToken="))
                {
                    lines[i] = $"RefreshToken={token}";
                    found = true;
                    break;
                }
            }

            // if there was no refresh token line at all, add one at the bottom
            if (!found)
            {
                Array.Resize(ref lines, lines.Length + 1);
                lines[lines.Length - 1] = $"RefreshToken={token}";
            }
            File.WriteAllLines(ConfigPath, lines);
        }

        // wipe the saved refresh token so the user has to log in again
        public static void ClearRefreshToken()
        {
            SaveRefreshToken("");
        }
    }
}