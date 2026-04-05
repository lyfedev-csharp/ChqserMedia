using Valve.Newtonsoft.Json;
using Valve.Newtonsoft.Json.Linq;
using Photon.Pun;
using System;
using System.Collections;
using UnityEngine;
using WebSocketSharp;

namespace ChqserMedia
{
    public class UnityMainThreadDispatcher : MonoBehaviour
    {
        private static readonly System.Collections.Generic.Queue<Action> _queue = new System.Collections.Generic.Queue<Action>();
        private static UnityMainThreadDispatcher _instance;

        public static void Enqueue(Action action) { lock (_queue) { _queue.Enqueue(action); } }

        private void Update() { lock (_queue) { while (_queue.Count > 0) _queue.Dequeue()?.Invoke(); } }

        public static void EnsureExists()
        {
            if (_instance != null) return;
            var go = new GameObject("UnityMainThreadDispatcher");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<UnityMainThreadDispatcher>();
        }
    }

    public class ChqserNetwork : MonoBehaviour
    {
        public static ChqserNetwork Instance { get; private set; }

        private const string SERVER_HOST = "wss://ws.chqser.lol";
        private WebSocket _ws;
        private string _myId;
        private string _myName;
        private string _myMenu;

        public bool IsConnected { get; private set; }
        public bool _disconnectNotified = false;

        private bool _intentionalDisconnect = false;
        private int _reconnectAttempts = 0;
        private const int _maxBackoffSeconds = 60;

        public event Action<bool> OnConnectionChanged;
        public event Action<PresencePayload> OnPresenceUpdated;

        [Serializable]
        public class PresencePayload
        {
            public string user_id;
            public string username;
            public bool is_online;
            public string current_room;
        }

        public static void CreateInstance()
        {
            if (Instance != null) return;
            UnityMainThreadDispatcher.EnsureExists();
            var go = new GameObject("ChqserNetwork");
            DontDestroyOnLoad(go);
            go.AddComponent<ChqserNetwork>();
        }

        private void Awake()
        {
            Instance = this;
            StartCoroutine(CoWaitAndConnect());

            if (NetworkSystem.Instance != null)
                NetworkSystem.Instance.OnJoinedRoomEvent += OnJoinedRoom;
            else
                StartCoroutine(CoSubscribeRoomEvent());
        }

        private IEnumerator CoWaitAndConnect()
        {
            yield return new WaitUntil(() =>
                PhotonNetwork.LocalPlayer != null &&
                !string.IsNullOrEmpty(PhotonNetwork.LocalPlayer.UserId));

            Connect(PhotonNetwork.LocalPlayer.UserId, PhotonNetwork.LocalPlayer.NickName, "chqser media");
        }

        private IEnumerator CoSubscribeRoomEvent()
        {
            yield return new WaitUntil(() => NetworkSystem.Instance != null);
            NetworkSystem.Instance.OnJoinedRoomEvent += OnJoinedRoom;
        }

        private void OnJoinedRoom()
        {
            if (!IsConnected) return;
            UpdatePresence(PhotonNetwork.CurrentRoom?.Name ?? "", true);
        }

        private void OnDestroy()
        {
            if (NetworkSystem.Instance != null)
                NetworkSystem.Instance.OnJoinedRoomEvent -= OnJoinedRoom;
            Disconnect();
        }

        public void Connect(string playfabId, string displayName, string menu)
        {
            _intentionalDisconnect = false;
            _reconnectAttempts = 0;
            _myId = playfabId;
            _myName = displayName;
            _myMenu = menu;
            WsConnect();
        }

        public void Disconnect()
        {
            _intentionalDisconnect = true;
            try { _ws?.Close(); } catch { }
            IsConnected = false;
        }

        private void WsConnect()
        {
            if (_intentionalDisconnect) return;
            try
            {
                _ws = new WebSocket(SERVER_HOST);
                _ws.SslConfiguration.ServerCertificateValidationCallback = (sender, cert, chain, errors) => true;

                _ws.OnOpen += (sender, e) =>
                {
                    IsConnected = true;
                    _disconnectNotified = false;
                    _reconnectAttempts = 0;
                    WsSend(new { type = "auth", playfabId = _myId, username = _myName, menu = _myMenu });
                    UnityMainThreadDispatcher.Enqueue(() => OnConnectionChanged?.Invoke(true));
                };

                _ws.OnMessage += (sender, e) => HandleMessage(e.Data);

                _ws.OnClose += (sender, e) =>
                {
                    IsConnected = false;
                    UnityMainThreadDispatcher.Enqueue(() => OnConnectionChanged?.Invoke(false));
                    if (!_intentionalDisconnect)
                        UnityMainThreadDispatcher.Enqueue(() => StartCoroutine(ReconnectAfterDelay()));
                };

                _ws.OnError += (sender, e) =>
                    Debug.LogWarning($"[ChqserNetwork] WS error: {e.Message}");

                _ws.ConnectAsync();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ChqserNetwork] WS connect failed: {ex.Message}");
                UnityMainThreadDispatcher.Enqueue(() => StartCoroutine(ReconnectAfterDelay()));
            }
        }

        private IEnumerator ReconnectAfterDelay()
        {
            if (_intentionalDisconnect) yield break;
            _reconnectAttempts++;
            float delay = Mathf.Min(Mathf.Pow(2f, _reconnectAttempts), _maxBackoffSeconds);
            Debug.Log($"[ChqserNetwork] Reconnecting in {delay}s (attempt {_reconnectAttempts})...");
            yield return new WaitForSeconds(delay);
            if (!_intentionalDisconnect && !string.IsNullOrEmpty(_myId))
                WsConnect();
        }

        private void WsSend(object payload)
        {
            if (_ws == null || !_ws.IsAlive) return;
            try { _ws.Send(JsonConvert.SerializeObject(payload)); } catch { }
        }

        private void HandleMessage(string json)
        {
            try
            {
                var root = JObject.Parse(json);
                if (root["type"]?.ToString() == "presence_update")
                {
                    var presence = root.ToObject<PresencePayload>();
                    UnityMainThreadDispatcher.Enqueue(() => OnPresenceUpdated?.Invoke(presence));
                }
            }
            catch { }
        }

        public void UpdatePresence(string room, bool online)
        {
            WsSend(new
            {
                type = "presence_update",
                playfabId = _myId,
                username = _myName,
                current_room = room,
                is_online = online
            });
        }
    }
}