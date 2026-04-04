using Oculus.Platform;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace ChqserMedia
{
    internal class AudioManagement : MonoBehaviour
    {
        public static AudioManagement instance;
        private GameObject source;

        private void Awake()
        {
            instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public static void PlaySound(string resourceName, bool heil = false)
        {
            if (instance == null)
            {
                var audioManagerObject = new GameObject("AudioManager");
                instance = audioManagerObject.AddComponent<AudioManagement>();
            }

            instance.StartCoroutine(instance.PlayEmbeddedMp3(resourceName, heil));
        }

        public IEnumerator PlayEmbeddedMp3(string resourceName, bool heil = false)
        {
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ChqserMedia.Resources." + resourceName);
            if (s == null)
            {
                Debug.LogError("Audio File Not Found: " + "ChqserMedia.Resources." + resourceName);
                yield break;
            }

            var path = "ChqserMedia.Resources.";
            var tempPath = Path.Combine(Path.GetTempPath(), "temp_" + Guid.NewGuid() + ".mp3");
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(path + resourceName))
            using (var fileStream = File.Create(tempPath))
            {
                stream.CopyTo(fileStream);
            }

            using (var www = UnityWebRequestMultimedia.GetAudioClip("file://" + tempPath, AudioType.MPEG))
            {
                yield return www.SendWebRequest();
                if (www.result != UnityWebRequest.Result.Success) yield break;
                var clip = DownloadHandlerAudioClip.GetContent(www);
                if (source == null)
                {
                    source = new GameObject("AudioSource");
                    DontDestroyOnLoad(source);
                }

                var sourcer = source.AddComponent<AudioSource>();
                sourcer.clip = clip;
                sourcer.volume = heil ? 0.04f : 0.5f;
                sourcer.Play();
                Destroy(sourcer, clip.length + 0.5f);
            }
        }

        public static Dictionary<string, AudioClip> audioFilePool = new Dictionary<string, AudioClip>();

        public static void PlaySoundFromURL(string resourcePath, string fileName, bool heil = false)
        {
            if (instance == null)
            {
                var audioManagerObject = new GameObject("AudioManagement");
                instance = audioManagerObject.AddComponent<AudioManagement>();
            }

            var clip = LoadAudioFromURL(resourcePath, fileName);
            if (clip != null) instance.PlayClip(clip, heil);
        }

        private void PlayClip(AudioClip clip, bool heil)
        {
            if (source == null)
            {
                source = new GameObject("AudioSource");
                DontDestroyOnLoad(source);
            }

            var sourcer = source.AddComponent<AudioSource>();
            sourcer.clip = clip;
            sourcer.volume = heil ? 0.04f : 0.5f;
            sourcer.Play();
            Destroy(sourcer, clip.length + 0.5f);
        }

        public static AudioClip LoadAudioFromFile(string filePath)
        {
            if (audioFilePool.TryGetValue(filePath, out var cachedClip))
            {
                if (cachedClip != null && cachedClip.loadState == AudioDataLoadState.Loaded) return cachedClip;

                audioFilePool.Remove(filePath);
            }

            try
            {
                if (!File.Exists(filePath)) return null;

                var fileData = File.ReadAllBytes(filePath);
                var extension = Path.GetExtension(filePath).TrimStart('.').ToLower();
                var audioType = GetAudioType(extension);

                var tempPath = Path.Combine(UnityEngine.Application.temporaryCachePath, Path.GetFileName(filePath));
                File.WriteAllBytes(tempPath, fileData);

                var fileUri = "file:///" + tempPath.Replace("\\", "/");
                var request = UnityWebRequestMultimedia.GetAudioClip(fileUri, audioType);
                var handler = (DownloadHandlerAudioClip)request.downloadHandler;
                handler.streamAudio = false;

                var operation = request.SendWebRequest();
                while (!operation.isDone) Thread.Sleep(10);

                if (request.result != UnityWebRequest.Result.Success)
                {
                    request.Dispose();
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                    return null;
                }

                var clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null || clip.loadState != AudioDataLoadState.Loaded)
                {
                    request.Dispose();
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                    return null;
                }

                audioFilePool.Add(filePath, clip);
                request.Dispose();
                if (File.Exists(tempPath)) File.Delete(tempPath);
                return clip;
            }
            catch
            {
                return null;
            }
        }

        public static string Folder => Path.Combine(Directory.GetParent(UnityEngine.Application.dataPath).FullName, "ChqserMedia");

        public static string MenuSounds =>
            Path.Combine(Directory.GetParent(UnityEngine.Application.dataPath).FullName, Folder, "Menu Sounds");

        public static AudioClip LoadAudioFromURL(string resourcePath, string fileName)
        {
            var filePath = Path.Combine(MenuSounds, fileName);

            if (audioFilePool.TryGetValue(filePath, out var cachedClip))
            {
                if (cachedClip != null && cachedClip.loadState == AudioDataLoadState.Loaded) return cachedClip;

                audioFilePool.Remove(filePath);
            }

            var directory = Path.GetDirectoryName(filePath);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

            if (!File.Exists(filePath))
                using (var stream = new WebClient())
                {
                    stream.DownloadFile(resourcePath, filePath);
                }

            return LoadAudioFromFile(filePath);
        }

        private static AudioType GetAudioType(string extension)
        {
            switch (extension)
            {
                case "mp3": return AudioType.MPEG;
                case "wav": return AudioType.WAV;
                case "ogg": return AudioType.OGGVORBIS;
                case "aiff":
                case "aif": return AudioType.AIFF;
                default: return AudioType.UNKNOWN;
            }
        }
    }
}
