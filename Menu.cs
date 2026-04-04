using BepInEx;
using Chqser.Classes;
using GorillaLocomotion;
using GorillaNetworking;
using HarmonyLib;
using Photon.Pun;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Valve.VR;

namespace ChqserMedia
{
    [HarmonyPatch(typeof(GTPlayer), "LateUpdate")]
    public class Menu : MonoBehaviour
    {
        public static GameObject MenuInstance;
        public static GameObject Pointer;
        public static float MenuOpenDelay;
        public static bool MenuOpen = false;
        public static Vector3 TargetScale = new Vector3(0.00075f, 0.00075f, 0.00075f);
        private static Coroutine animationRoutine;
        private static Menu instance;
        private static float interactCooldown;
        private static AssetBundle loadedBundle;
        private static MediaManager mediaManager;

        private static Dictionary<GameObject, Action> utilityButtons = new Dictionary<GameObject, Action>();

        void Awake()
        {
            instance = this;
            loadedBundle = LoadAssetBundle("ChqserMedia.Resources.chqsermedia");

            if (loadedBundle != null)
            {
                GameObject prefab = loadedBundle.LoadAsset<GameObject>("assets/chqsermedia.prefab");

                if (prefab != null)
                {
                    MenuInstance = Instantiate(prefab);
                    MenuInstance.layer = 2;
                    MenuInstance.SetActive(false);

                    Canvas canvas = MenuInstance.GetComponent<Canvas>();
                    if (canvas != null) canvas.renderMode = RenderMode.WorldSpace;

                    Shader textShader = Shader.Find("TextMeshPro/Distance Field");
                    foreach (TextMeshProUGUI text in MenuInstance.GetComponentsInChildren<TextMeshProUGUI>(true))
                    {
                        if (text.fontMaterial != null) text.fontMaterial.shader = textShader;
                        text.raycastTarget = false;
                    }

                    foreach (Transform t in MenuInstance.GetComponentsInChildren<Transform>(true))
                        t.gameObject.layer = 2;

                    MenuInstance.transform.localScale = Vector3.zero;
                    DontDestroyOnLoad(MenuInstance);

                    mediaManager = MenuInstance.AddComponent<MediaManager>();
                    mediaManager.Initialize(loadedBundle);

                    SetupUtility("Background/Skip", () => mediaManager.SkipTrack());
                    SetupUtility("Background/Prev", () => mediaManager.PreviousTrack());
                    SetupUtility("Background/Play", () => mediaManager.PauseTrack());
                }
            }
        }

        public static void Prefix()
        {
            try
            {
                bool toggle = SteamVR_Actions.gorillaTag_RightJoystickClick.GetState(SteamVR_Input_Sources.RightHand);
                if (toggle && Time.time > MenuOpenDelay)
                {
                    MenuOpenDelay = Time.time + 0.3f;
                    MenuOpen = !MenuOpen;

                    if (MenuInstance != null)
                    {
                        if (MenuOpen)
                        {
                            MenuInstance.SetActive(true);
                            UpdatePointer(true);
                            if (animationRoutine != null) instance.StopCoroutine(animationRoutine);
                            animationRoutine = instance.StartCoroutine(ScaleAnimation(Vector3.zero, TargetScale));
                            SafePlaySound("Open.mp3");
                        }
                        else
                        {
                            UpdatePointer(false);
                            if (animationRoutine != null) instance.StopCoroutine(animationRoutine);
                            animationRoutine = instance.StartCoroutine(ScaleAnimation(MenuInstance.transform.localScale, Vector3.zero, true));
                            SafePlaySound("Close.mp3");
                        }
                    }
                }

                if (MenuOpen && MenuInstance != null && Pointer != null)
                {
                    Transform hand = GTPlayer.Instance.leftHand.controllerTransform;
                    MenuInstance.transform.position = hand.position + hand.rotation * new Vector3(0.05f, 0f, 0f);
                    MenuInstance.transform.rotation = hand.rotation * Quaternion.Euler(-180f, -90f, -90f);

                    HandleInteraction();
                }
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        private static void HandleInteraction()
        {
            if (Time.time < interactCooldown) return;
            Collider[] hits = Physics.OverlapSphere(Pointer.transform.position, 0.02f, 1 << 2);

            foreach (var hit in hits)
            {
                if (utilityButtons.TryGetValue(hit.gameObject, out Action act))
                {
                    act.Invoke();
                    interactCooldown = Time.time + 0.3f;
                    SafePlaySound("Click.mp3");
                    return;
                }
            }
        }

        private void SetupUtility(string path, Action act)
        {
            Transform t = MenuInstance.transform.Find(path);
            if (t != null)
            {
                utilityButtons[t.gameObject] = act;
                SetupCollider(t.gameObject);
            }
        }

        private static void SetupCollider(GameObject obj)
        {
            BoxCollider old = obj.GetComponent<BoxCollider>();
            if (old != null) Destroy(old);
            BoxCollider col = obj.AddComponent<BoxCollider>();
            col.isTrigger = true;
            RectTransform rect = obj.GetComponent<RectTransform>();
            float w = rect ? rect.rect.width : 50f;
            float h = rect ? rect.rect.height : 50f;
            col.size = new Vector3(w, h, 15f);
            col.center = new Vector3(0, 0, -5f);
            obj.layer = 2;
        }

        private static void UpdatePointer(bool active)
        {
            if (active)
            {
                if (Pointer == null)
                {
                    Pointer = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    Pointer.layer = 2;
                    Pointer.transform.localScale = Vector3.one * 0.0075f;
                    Destroy(Pointer.GetComponent<SphereCollider>());
                    Pointer.GetComponent<Renderer>().material.color = Color.white;
                }
                Pointer.transform.parent = GorillaTagger.Instance.rightHandTransform;
                Pointer.transform.localPosition = new Vector3(0f, -0.1f, 0f);
                Pointer.SetActive(true);
            }
            else if (Pointer != null) Pointer.SetActive(false);
        }

        private static IEnumerator ScaleAnimation(Vector3 start, Vector3 end, bool disableAfter = false)
        {
            float d = 0.2f, e = 0f;
            while (e < d)
            {
                e += Time.deltaTime;
                MenuInstance.transform.localScale = Vector3.Lerp(start, end, e / d);
                yield return null;
            }
            MenuInstance.transform.localScale = end;
            if (disableAfter) MenuInstance.SetActive(false);
        }

        public static AssetBundle LoadAssetBundle(string path)
        {
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(path);
            return s != null ? AssetBundle.LoadFromStream(s) : null;
        }

        public static Sprite LoadSpriteFromResource(string path)
        {
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(path);
            if (s == null) return null;
            byte[] buffer = new byte[s.Length];
            s.Read(buffer, 0, buffer.Length);
            Texture2D tex = new Texture2D(2, 2);
            tex.LoadImage(buffer);
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
        }

        private static void SafePlaySound(string name)
        {
            try { AudioManagement.PlaySound(name); } catch { }
        }
    }
}