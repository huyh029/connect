using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace TiktokLibEditor
{
    [InitializeOnLoad]
    internal static class TikTokEditorConsoleMonitor
    {
        private const string EnabledKey = "RTS.TikTok.ConsoleMonitor";
        private static UdpClient socket;
        private static int listeningPort;
        private static double nextCheck;
        private static bool reportedBindFailure;
        private static bool receivedFirstPacket;

        static TikTokEditorConsoleMonitor()
        {
            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged += PlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
        }

        [MenuItem("Tools/TikTok/Log Events Without Play")]
        private static void Toggle()
        {
            EditorPrefs.SetBool(EnabledKey, !EditorPrefs.GetBool(EnabledKey, true));
            Stop(); nextCheck = 0; reportedBindFailure = false;
        }

        [MenuItem("Tools/TikTok/Log Events Without Play", true)]
        private static bool ValidateToggle()
        {
            Menu.SetChecked("Tools/TikTok/Log Events Without Play", EditorPrefs.GetBool(EnabledKey, true));
            return true;
        }

        private static void PlayModeChanged(PlayModeStateChange state)
        {
            Stop(); nextCheck = 0; reportedBindFailure = false;
            if (state == PlayModeStateChange.EnteredPlayMode && EditorPrefs.GetBool(EnabledKey, true))
                TikTokUdpReceiver.EnsureListening();
        }

        private static void Update()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling ||
                !EditorPrefs.GetBool(EnabledKey, true)) { Stop(); return; }
            if (EditorApplication.timeSinceStartup >= nextCheck)
            {
                nextCheck = EditorApplication.timeSinceStartup + 2;
                var receiver = UnityEngine.Object.FindFirstObjectByType<TikTokUdpReceiver>(FindObjectsInactive.Include);
                var port = receiver != null ? receiver.port : 7777;
                if (socket != null && listeningPort != port) Stop();
                if (socket == null)
                {
                    try
                    {
                        socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                        socket.Client.Blocking = false;
                        listeningPort = port; reportedBindFailure = false;
                        Debug.Log($"[TikTok Editor UDP] Listening on 0.0.0.0:{port} — Console logging without Play Mode.");
                    }
                    catch (Exception exception)
                    {
                        Stop();
                        if (!reportedBindFailure) Debug.LogWarning($"[TikTok Editor UDP] Cannot listen on port {port}: {exception.Message}");
                        reportedBindFailure = true;
                    }
                }
            }
            if (socket == null) return;
            try
            {
                var endpoint = new IPEndPoint(IPAddress.Any, 0);
                for (var count = 0; count < 128 && socket.Available > 0; count++)
                {
                    var json = Encoding.UTF8.GetString(socket.Receive(ref endpoint));
                    try
                    {
                        if (!receivedFirstPacket)
                        {
                            receivedFirstPacket = true;
                            Debug.Log($"[UDP CHECK 2 / Editor] First packet from {endpoint}: connector → Unity works.");
                        }
                        var message = JsonUtility.FromJson<TikEvent>(json);
                        TikTokUdpReceiver.LogConsoleEvent(message);
                    }
                    catch (Exception exception) { Debug.LogWarning("[TikTok Editor UDP] Invalid event: " + exception.Message); }
                }
            }
            catch (SocketException exception)
            {
                if (exception.SocketErrorCode != SocketError.WouldBlock)
                { Debug.LogWarning("[TikTok Editor UDP] " + exception.Message); Stop(); }
            }
        }

        private static void Stop()
        {
            if (socket == null) return;
            socket.Close(); socket = null; receivedFirstPacket = false;
        }
    }
}
