using BepInEx;
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
    public class Menu : MonoBehaviour
    {
        // the actual menu object in the scene
        public static GameObject MenuInstance;

        // small sphere on the right hand used to press buttons
        public static GameObject Pointer;

        // prevents the menu from toggling too fast
        public static float MenuOpenDelay;

        // tracks whether the menu is currently visible
        public static bool MenuOpen = false;

        // the scale the menu animates to when opening
        public static Vector3 TargetScale = new Vector3(0.00075f, 0.00075f, 0.00075f);

        // reference to the running open/close animation so we can cancel it
        private static Coroutine animationRoutine;

        // keeps a reference to this component so static methods can start coroutines
        private static Menu instance;

        // stops buttons from being pressed multiple times in a row too quickly
        private static float interactCooldown;

        // the loaded asset bundle containing the prefab and sprites
        private static AssetBundle loadedBundle;

        // the media manager component that handles playback data
        private static MediaManager mediaManager;

        // maps each button gameobject to the action it should run when pressed
        internal static Dictionary<GameObject, Action> utilityButtons = new Dictionary<GameObject, Action>();

        void Awake()
        {
            instance = this;

            // load the asset bundle from embedded resources
            loadedBundle = LoadAssetBundle("ChqserMedia.Resources.chqsermedia");

            if (loadedBundle != null)
            {
                // grab the menu prefab from the bundle
                GameObject prefab = loadedBundle.LoadAsset<GameObject>("assets/chqsermedia.prefab");

                if (prefab != null)
                {
                    // spawn the menu and put it on the ignore raycast layer
                    MenuInstance = Instantiate(prefab);
                    MenuInstance.layer = 2;
                    MenuInstance.SetActive(false);

                    // make sure the canvas renders in world space so it sits in 3d
                    Canvas canvas = MenuInstance.GetComponent<Canvas>();
                    if (canvas != null) canvas.renderMode = RenderMode.WorldSpace;

                    // fix the tmp shader so text renders correctly in vr
                    Shader textShader = Shader.Find("TextMeshPro/Distance Field");
                    foreach (TextMeshProUGUI text in MenuInstance.GetComponentsInChildren<TextMeshProUGUI>(true))
                    {
                        if (text.fontMaterial != null) text.fontMaterial.shader = textShader;
                        // disable raycasting on text so it doesn't block button hits
                        text.raycastTarget = false;
                    }

                    // put every child on layer 2 so the pointer sphere can hit them
                    foreach (Transform t in MenuInstance.GetComponentsInChildren<Transform>(true))
                        t.gameObject.layer = 2;

                    // start invisible so the open animation can scale it up
                    MenuInstance.transform.localScale = Vector3.zero;
                    DontDestroyOnLoad(MenuInstance);

                    // attach the media manager and give it the bundle so it can load sprites
                    mediaManager = MenuInstance.AddComponent<MediaManager>();
                    mediaManager.Initialize(loadedBundle);

                    SpotifyBrowser spotifyBrowser = MenuInstance.AddComponent<SpotifyBrowser>();
                    spotifyBrowser.Initialize(MenuInstance.transform);

                    // register the three control buttons with their actions
                    SetupUtility("Background/Skip", () => mediaManager.SkipTrack());
                    SetupUtility("Background/Prev", () => mediaManager.PreviousTrack());
                    SetupUtility("Background/Play", () => mediaManager.PauseTrack());
                }
            }
        }

        void LateUpdate()
        {
            // runs after all animations so the menu position never lags behind the hand
            try
            {
                // check if the left primary button is held
                bool toggle = SteamVR_Actions.gorillaTag_LeftPrimaryClick.GetState(SteamVR_Input_Sources.LeftHand);
                if (toggle && Time.time > MenuOpenDelay)
                {
                    // set the cooldown so it doesnt flip back immediately
                    MenuOpenDelay = Time.time + 0.3f;
                    MenuOpen = !MenuOpen;

                    if (MenuInstance != null)
                    {
                        if (MenuOpen)
                        {
                            MenuInstance.SetActive(true);
                            UpdatePointer(true);

                            // cancel any in-progress close animation before opening
                            if (animationRoutine != null) StopCoroutine(animationRoutine);
                            animationRoutine = StartCoroutine(ScaleAnimation(Vector3.zero, TargetScale));
                            SafePlaySound("Open.mp3");

                            // force the ui to refresh so data isn't stale
                            mediaManager?.ForceRefresh();
                        }
                        else
                        {
                            UpdatePointer(false);

                            // cancel any inprogress open animation before closing
                            if (animationRoutine != null) StopCoroutine(animationRoutine);
                            animationRoutine = StartCoroutine(ScaleAnimation(MenuInstance.transform.localScale, Vector3.zero, true));
                            SpotifyBrowser.Instance?.OnMenuClosed();
                            SafePlaySound("Close.mp3");
                        }
                    }
                }

                // stick the menu to the left hand every frame while it's open
                if (MenuOpen && MenuInstance != null && GTPlayer.Instance != null)
                {
                    Transform hand = GTPlayer.Instance.leftHand.controllerTransform;
                    MenuInstance.transform.position = hand.position + hand.rotation * new Vector3(0.05f, 0.1f, 0.15f);
                    MenuInstance.transform.rotation = hand.rotation * Quaternion.Euler(-180f, -90f, -90f);

                    // only check for button presses if the pointer exists
                    if (Pointer != null)
                        HandleInteraction();
                }
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        private static void HandleInteraction()
        {
            // bail out if we're still in the cooldown window
            if (Time.time < interactCooldown) return;

            // find any colliders the pointer sphere is touching on layer 2
            Collider[] hits = Physics.OverlapSphere(Pointer.transform.position, 0.02f, 1 << 2);

            foreach (var hit in hits)
            {
                // check if this collider belongs to a registered button
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
            // find the button by path under the menu root
            Transform t = MenuInstance.transform.Find(path);
            if (t != null)
            {
                // register it and add a collider so the pointer can hit it
                utilityButtons[t.gameObject] = act;
                SetupCollider(t.gameObject);
            }
        }

        private static void SetupCollider(GameObject obj)
        {
            // remove any existing collider first to avoid duplicates
            BoxCollider old = obj.GetComponent<BoxCollider>();
            if (old != null) Destroy(old);

            BoxCollider col = obj.AddComponent<BoxCollider>();
            col.isTrigger = true;

            // size the collider to exactly match the rect — no extra depth added
            RectTransform rect = obj.GetComponent<RectTransform>();
            float w = rect ? rect.rect.width : 50f;
            float h = rect ? rect.rect.height : 50f;
            col.size = new Vector3(w, h, 0.001f);
            col.center = Vector3.zero;
            obj.layer = 2;
        }

        private static void UpdatePointer(bool active)
        {
            if (active)
            {
                // create the pointer sphere the first time it's needed
                if (Pointer == null)
                {
                    Pointer = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    Pointer.layer = 2;
                    Pointer.transform.localScale = Vector3.one * 0.0075f;

                    // remove the sphere collider so it doesn't interfere with physics
                    Destroy(Pointer.GetComponent<SphereCollider>());
                    Pointer.GetComponent<Renderer>().material.color = Color.white;
                }

                // attach it to the right hand and position it at the fingertip
                Pointer.transform.parent = GorillaTagger.Instance.rightHandTransform;
                Pointer.transform.localPosition = new Vector3(0f, -0.1f, 0f);
                Pointer.SetActive(true);
            }
            else if (Pointer != null) Pointer.SetActive(false);
        }

        private IEnumerator ScaleAnimation(Vector3 start, Vector3 end, bool disableAfter = false)
        {
            // animate the scale over 0.2 seconds
            float d = 0.2f, e = 0f;
            while (e < d)
            {
                e += Time.deltaTime;
                MenuInstance.transform.localScale = Vector3.Lerp(start, end, e / d);

                // keep the ui elements up to date during the animation
                mediaManager?.ForceRefresh();
                yield return null;
            }

            MenuInstance.transform.localScale = end;

            // hide the object after the close animation finishes
            if (disableAfter) MenuInstance.SetActive(false);
        }

        public static AssetBundle LoadAssetBundle(string path)
        {
            // load an asset bundle that was embedded as a resource in the dll
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(path);
            return s != null ? AssetBundle.LoadFromStream(s) : null;
        }

        public static Sprite LoadSpriteFromResource(string path)
        {
            // load an image from embedded resources and turn it into a sprite
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
            // wrapped in try/catch so a missing sound file doesn't crash anything
            try { AudioManagement.PlaySound(name); } catch { }
        }

        public static void RegisterButton(GameObject go, Action act)
        {
            // easily adds/sets up a button anywhere in the menu by providing the gameobject and action to run when it's pressed
            if (go != null && act != null)
                utilityButtons[go] = act;
        }

        public static void UnregisterButton(GameObject go)
        {
            // removes a button from the menu and destroys its collider so it can't be pressed anymore
            if (go != null)
                utilityButtons.Remove(go);
        }
    }
}