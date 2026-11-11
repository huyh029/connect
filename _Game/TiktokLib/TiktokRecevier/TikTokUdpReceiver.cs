using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Concurrent;
using UnityEngine;

[RequireComponent(typeof(MainThreadDispatcher))]
public class TikTokUdpReceiver : MonoBehaviour
{
    public static TikTokUdpReceiver Instance { get; private set;  }
    [Header("UDP")]
    public int port = 7777;
    public bool logRaw = false;
    public bool logGiftEvents = true;
    public bool logLikeEvents = true;
    public bool logOtherEvents = true;
    
    [Header("Performance")]
    [Tooltip("Timeout cho mỗi lần Receive (ms). Giảm = phản hồi nhanh hơn khi stop, tăng = ít CPU hơn")]
    [Range(10, 5000)]
    public int receiveTimeoutMs = 500;  // Giảm từ 1000ms → 100ms
    
    [Header("Auto Start")]
    [Tooltip("Tự động start receiver khi OnEnable. Tắt nếu muốn GameManager control")]
    public bool autoStart = true;

    public static Action<TikEvent> OnEvent;

    private UdpClient _udp;
    private Thread _thread;
    private volatile bool _running;
    private readonly ConcurrentQueue<string> _receivedJson = new();
    public bool IsListening => _running;
    private bool _receivedFirstPacket, _reportedNoPacket;
    private float _listeningSince;

    public static TikTokUdpReceiver EnsureListening()
    {
        var receiver = Instance;
        if (receiver == null) receiver = FindFirstObjectByType<TikTokUdpReceiver>(FindObjectsInactive.Include);
        if (receiver == null) receiver = new GameObject("TikTok UDP Receiver").AddComponent<TikTokUdpReceiver>();
        if (!receiver.gameObject.activeInHierarchy) receiver.gameObject.SetActive(true);
        receiver.enabled = true;
        receiver.StartReceiver();
        return receiver;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnEnable()
    {
        if (Instance == this && autoStart) StartReceiver();
    }
    private void OnDisable() => StopReceiver();
    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        StopReceiver();
    }

    public void StartReceiver()
    {
        if (_running) return;
        try
        {
            var client = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            client.Client.ReceiveTimeout = Math.Max(10, receiveTimeoutMs);
            _udp = client;
            _receivedFirstPacket = _reportedNoPacket = false;
            _listeningSince = Time.unscaledTime;
            _running = true;
            _thread = new Thread(() => Loop(client)) { IsBackground = true, Name = "TikTok UDP" };
            _thread.Start();
            Debug.Log($"[UDP CHECK 1] Listening on 0.0.0.0:{port} | gameplay subscribers={OnEvent?.GetInvocationList().Length ?? 0} | waiting for connector packets", this);
        }
        catch (Exception exception)
        {
            StopReceiver();
            Debug.LogError($"[UDP] Cannot listen on port {port}: {exception.Message}", this);
        }
    }

    public void StopReceiver()
    {
        _running = false;
        try { _udp?.Close(); } catch { }
        _udp = null;
        try { _thread?.Join(200); } catch { }
        _thread = null;
        while (_receivedJson.TryDequeue(out _)) { }
    }

    private void Loop(UdpClient client)
    {
        var endpoint = new IPEndPoint(IPAddress.Any, 0);
        while (_running && ReferenceEquals(client, _udp))
        {
            try { _receivedJson.Enqueue(Encoding.UTF8.GetString(client.Receive(ref endpoint))); }
            catch (SocketException exception)
            {
                if (exception.SocketErrorCode == SocketError.TimedOut || exception.SocketErrorCode == SocketError.WouldBlock) continue;
                if (_running) Debug.LogWarning("[UDP] " + exception.Message);
                break;
            }
            catch (ObjectDisposedException) { break; }
            catch (Exception exception) { Debug.LogWarning("[UDP] " + exception.Message); break; }
        }
        if (ReferenceEquals(client, _udp)) _running = false;
    }

    private void Update()
    {
        if (_running && !_receivedFirstPacket && !_reportedNoPacket && Time.unscaledTime - _listeningSince >= 10f)
        {
            _reportedNoPacket = true;
            Debug.LogWarning($"[UDP CHECK] No packets received on port {port} after 10s. Check connector host 127.0.0.1 / port {port}, or click Test Comment UDP.", this);
        }
        for (var count = 0; count < 128 && _receivedJson.TryDequeue(out var json); count++)
        {
            try
            {
                if (!_receivedFirstPacket)
                {
                    _receivedFirstPacket = true;
                    Debug.Log($"[UDP CHECK 2] First packet received on port {port}: {Encoding.UTF8.GetByteCount(json)} bytes. Connector → Unity UDP works.", this);
                }
                if (logRaw) Debug.Log("[UDP RAW] " + json, this);
                var message = JsonUtility.FromJson<TikEvent>(json);
                if (message == null) continue;
                message.rawJson = json;
                DispatchEvent(message);
            }
            catch (Exception exception) { Debug.LogWarning("[UDP] Invalid event or handler error: " + exception.Message, this); }
        }
    }
    public static void LogConsoleEvent(TikEvent message, bool logGiftEvents = true, bool logLikeEvents = true, bool logOtherEvents = true, UnityEngine.Object context = null)
    {
        if (message == null) return;
        var kind = (message.type ?? "unknown").ToLowerInvariant();
        if (kind == "connect" || kind == "connected" || kind == "disconnect" || kind == "disconnected")
        {
            Debug.Log($"[TikTok CONNECT CHECK] status={kind} | account={message.unique_id ?? message.user?.unique_id} | roomId={message.room_id} | timestamp={message.ts}", context);
            return;
        }
        var isGift = kind == "gift" || kind == "gift_final" || kind == "gift_tick";
        if ((isGift && logGiftEvents) || (kind == "like" && logLikeEvents) ||
            (!isGift && kind != "like" && logOtherEvents))
        {
            var userId = !string.IsNullOrWhiteSpace(message.user?.user_id)
                ? message.user.user_id : message.user?.unique_id ?? "unknown";
            var identity = $"userId={userId} | user={message.user?.nickname} | uniqueId={message.user?.unique_id}";
            if (isGift)
            {
                var quantity = Math.Max(0, message.delta > 0 ? message.delta : message.count);
                Debug.Log($"[TikTok Gift:{kind}] {identity} | giftId={message.gift?.id ?? 0} | " +
                    $"gift={message.gift?.name} | count={quantity} | coins={(long)(message.gift?.price ?? 0) * quantity}", context);
            }
            else if (kind == "like")
                Debug.Log($"[TikTok Like] {identity} | likeCount={message.like_count} | totalLikes={message.total_likes}", context);
            else if (kind == "comment")
                Debug.Log($"[TikTok Comment] {identity} | comment={message.comment}", context);
            else
                Debug.Log($"[TikTok {message.type ?? "unknown"}] {identity} | timestamp={message.ts}", context);
        }
    }

    private void DispatchEvent(TikEvent message)
    {
        if (message == null) return;
        LogConsoleEvent(message, logGiftEvents, logLikeEvents, logOtherEvents, this);
        OnEvent?.Invoke(message);
    }
    // Public method để test - trigger event manually
    public void TriggerTestEvent(TikEvent tikEvent)
    {
        DispatchEvent(tikEvent);
    }
}