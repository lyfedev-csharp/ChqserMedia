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
    }
}
