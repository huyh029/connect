using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RecoilPathfinding;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace NhiQuoc.Tools
{
    /// <summary>
    /// Writes and reads the rank server the battle's leaderboard shows (<see cref="RecoilRankServer"/>):
    /// push one player (Single) or a JSON list typed in or read from a .json file (Multi) to POST /api/rank/update,
    /// look a player up, and list any of the four boards (weekly / monthly × điểm / chuỗi thắng).
    /// Each entry: { "tikTokUserId", "username", "avatar", "matchPoint", "killCount" } — matchPoint is the SCORE and
    /// killCount is the WIN STREAK (the server's names are misleading). Older files with
    /// { "tiktokID", "name", "avatarurl", "score" } are read too.
    /// The server has no delete or overwrite call: what a push does to an existing player is up to the server.
    /// </summary>
    public sealed class CongThanhLeaderboardTool : EditorWindow
    {
        private const string Prefix = "CongThanhLeaderboard.";
        private static readonly string[] BoardNames = { "Tuần · Điểm", "Tuần · Chuỗi thắng", "Tháng · Điểm", "Tháng · Chuỗi thắng" };

        private string _serverUrl;
        private int _mode; // 0 single, 1 multi
        private int _board; // RecoilRankServer.Board
        private RecoilRankServer.Entry _single = new();
        private string _multiJson = "[\n  { \"tikTokUserId\": \"user1\", \"username\": \"Người chơi 1\", \"avatar\": \"\", \"matchPoint\": 1000, \"killCount\": 2 }\n]";
        private readonly List<RecoilRankServer.Entry> _listed = new();
        private Vector2 _scroll, _listScroll;
        private string _status;
        private MessageType _statusType = MessageType.Info;
        private bool _busy;

        private RecoilRankServer.Board Board => (RecoilRankServer.Board)_board;

        [MenuItem("Game Tools/Công Thành/Leaderboard (Rank server)")]
        private static void Open() => GetWindow<CongThanhLeaderboardTool>("Rank server").minSize = new Vector2(520f, 520f);

        private void OnEnable()
        {
            _serverUrl = EditorPrefs.GetString(Prefix + "ServerUrl", RecoilRankServer.DefaultUrl);
            _mode = EditorPrefs.GetInt(Prefix + "Mode", 0);
            _board = Mathf.Clamp(EditorPrefs.GetInt(Prefix + "Board", 0), 0, BoardNames.Length - 1);
            _multiJson = EditorPrefs.GetString(Prefix + "MultiJson2", _multiJson);
        }

        private void SaveSettings()
        {
            EditorPrefs.SetString(Prefix + "ServerUrl", _serverUrl ?? "");
            EditorPrefs.SetInt(Prefix + "Mode", _mode);
            EditorPrefs.SetInt(Prefix + "Board", _board);
            EditorPrefs.SetString(Prefix + "MultiJson2", _multiJson ?? "");
        }

        // ---------------------------------------------------------------- UI

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                _serverUrl = EditorGUILayout.TextField("Server", _serverUrl);
                if (GUILayout.Button("Swagger", GUILayout.Width(64f)))
                    Application.OpenURL(RecoilRankServer.BoardUrl(_serverUrl, 0).Replace("/api/rank/weekly/point", "/swagger/index.html"));
            }
            if (EditorGUI.EndChangeCheck()) SaveSettings();
            EditorGUILayout.LabelField("matchPoint = điểm · killCount = chuỗi thắng (tên trường trên server đặt chưa đúng)", EditorStyles.miniLabel);

            EditorGUILayout.Space(6f);
            EditorGUI.BeginChangeCheck();
            _mode = GUILayout.Toolbar(_mode, new[] { "Single (một người)", "Multi (danh sách JSON)" });
            if (EditorGUI.EndChangeCheck()) SaveSettings();
            EditorGUILayout.Space(4f);
            using (new EditorGUI.DisabledScope(_busy))
            {
                if (_mode == 0) DrawSingle();
                else DrawMulti();
            }

            EditorGUILayout.Space(6f);
            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, _statusType);
            DrawList();
            EditorGUILayout.EndScrollView();
        }

        private void DrawSingle()
        {
            _single.tikTokUserId = EditorGUILayout.TextField("tikTokUserId", _single.tikTokUserId);
            _single.username = EditorGUILayout.TextField("username", _single.username);
            _single.avatar = EditorGUILayout.TextField("avatar (URL)", _single.avatar);
            _single.matchPoint = EditorGUILayout.IntField(new GUIContent("Điểm (matchPoint)", "Điểm của người chơi. Server gọi là matchPoint."), _single.matchPoint);
            _single.killCount = EditorGUILayout.IntField(new GUIContent("Chuỗi thắng (killCount)", "Chuỗi thắng của người chơi. Server gọi là killCount, không phải số mạng hạ."), _single.killCount);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Push", "POST /api/rank/update với một người."), GUILayout.Height(26f)))
                    Run(() => PushAll(new List<RecoilRankServer.Entry> { _single }));
                if (GUILayout.Button(new GUIContent("Xem người này", "GET /api/rank/me/{id} và hạng trên bảng đang chọn."), GUILayout.Height(26f)))
                    Run(() => Lookup(_single.tikTokUserId));
            }
        }

        private void DrawMulti()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Mảng JSON: [{ \"tikTokUserId\", \"username\", \"avatar\", \"matchPoint\", \"killCount\" }, …]", EditorStyles.miniLabel);
                if (GUILayout.Button("📂 Nhập từ file JSON…", GUILayout.Width(150f))) ImportJsonFile();
            }
            EditorGUI.BeginChangeCheck();
            _multiJson = EditorGUILayout.TextArea(_multiJson, GUILayout.MinHeight(140f));
            if (EditorGUI.EndChangeCheck()) SaveSettings();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Push tất cả", "Một lần POST /api/rank/update với cả danh sách."), GUILayout.Height(26f)) &&
                    TryParseMulti(out var push))
                    Run(() => PushAll(push));
                if (GUILayout.Button("Lấy bảng đang xem vào ô JSON", GUILayout.Height(26f)))
                {
                    _multiJson = ToJson(_listed);
                    SaveSettings();
                    GUI.FocusControl(null);
                }
            }
        }

        private void DrawList()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Trên bảng: {_listed.Count}", EditorStyles.boldLabel, GUILayout.Width(110f));
                EditorGUI.BeginChangeCheck();
                _board = GUILayout.Toolbar(_board, BoardNames);
                if (EditorGUI.EndChangeCheck()) { SaveSettings(); Run(() => LoadList(true)); }
                using (new EditorGUI.DisabledScope(_busy))
                    if (GUILayout.Button("↻ Tải", GUILayout.Width(70f))) Run(() => LoadList(true));
            }
            var unit = RecoilRankServer.Unit(Board);
            _listScroll = EditorGUILayout.BeginScrollView(_listScroll, GUILayout.MinHeight(180f));
            foreach (var entry in _listed.ToArray())
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    GUILayout.Label(entry.Rank.ToString(), GUILayout.Width(30f));
                    GUILayout.Label(entry.DisplayName, EditorStyles.boldLabel, GUILayout.MinWidth(100f));
                    GUILayout.Label(entry.tikTokUserId, EditorStyles.miniLabel, GUILayout.MinWidth(80f));
                    GUILayout.Label(entry.Value.ToString("N0") + " " + unit, GUILayout.Width(110f));
                    GUILayout.Label($"điểm {entry.matchPoint:N0} · chuỗi {entry.killCount:N0}", EditorStyles.miniLabel, GUILayout.Width(150f));
                    using (new EditorGUI.DisabledScope(_busy))
                        if (GUILayout.Button("Sửa", GUILayout.Width(40f)))
                        {
                            _mode = 0;
                            _single = new RecoilRankServer.Entry
                            {
                                tikTokUserId = entry.tikTokUserId, username = entry.username, avatar = entry.avatar,
                                matchPoint = entry.matchPoint, killCount = entry.killCount
                            };
                            GUI.FocusControl(null);
                        }
                }
            EditorGUILayout.EndScrollView();
        }

        /// <summary>Reads a .json file (an array of entries, or one entry) into the Multi list.</summary>
        private void ImportJsonFile()
        {
            var folder = EditorPrefs.GetString(Prefix + "JsonFolder", Application.dataPath);
            var path = EditorUtility.OpenFilePanel("Nhập danh sách bảng xếp hạng", folder, "json");
            if (string.IsNullOrEmpty(path)) return;
            EditorPrefs.SetString(Prefix + "JsonFolder", System.IO.Path.GetDirectoryName(path));
            string text;
            try { text = System.IO.File.ReadAllText(path, Encoding.UTF8); }
            catch (Exception exception) { SetStatus("Không đọc được file: " + exception.Message, MessageType.Error); return; }
            var previous = _multiJson;
            _multiJson = text;
            if (!TryParseMulti(out var entries)) { _multiJson = previous; return; }
            // Shown back as a tidy list in the server's field names, ready to push.
            _multiJson = ToJson(entries);
            _mode = 1;
            SaveSettings();
            GUI.FocusControl(null);
            SetStatus($"Đã nhập {entries.Count} người từ {System.IO.Path.GetFileName(path)}. Bấm Push tất cả để gửi lên.", MessageType.Info);
        }

        private static string ToJson(List<RecoilRankServer.Entry> entries)
        {
            var array = new JArray();
            foreach (var entry in entries) array.Add(entry.ToRequest());
            return array.ToString();
        }

        private bool TryParseMulti(out List<RecoilRankServer.Entry> entries)
        {
            entries = new List<RecoilRankServer.Entry>();
            try
            {
                var token = JToken.Parse(_multiJson);
                if (token is JObject holder && holder["players"] is JArray players) token = players;
                var array = token as JArray ?? new JArray(token);
                foreach (var item in array)
                    if (item is JObject row) entries.Add(RecoilRankServer.ParseEntry(row, RecoilRankServer.Board.WeeklyPoint));
                return true;
            }
            catch (Exception exception)
            {
                SetStatus("JSON không hợp lệ: " + exception.Message, MessageType.Error);
                return false;
            }
        }

        // ---------------------------------------------------------------- server calls

        private async void Run(Func<Task> work)
        {
            if (_busy) return;
            _busy = true;
            try { await work(); }
            catch (Exception exception) { SetStatus(exception.Message, MessageType.Error); }
            finally { _busy = false; Repaint(); }
        }

        /// <summary>One POST /api/rank/update with every player that has an id.</summary>
        private async Task PushAll(List<RecoilRankServer.Entry> entries)
        {
            var players = new List<RecoilRankServer.Entry>();
            var skipped = 0;
            foreach (var entry in entries)
                if (string.IsNullOrWhiteSpace(entry.tikTokUserId)) skipped++;
                else players.Add(entry);
            if (players.Count == 0) { SetStatus("Không có người nào có tikTokUserId để gửi.", MessageType.Warning); return; }
            SetStatus($"Đang gửi {players.Count} người…", MessageType.Info);
            var result = await Send("POST", RecoilRankServer.UpdateUrl(_serverUrl), RecoilRankServer.UpdateBody(players));
            if (result.Error != null) { SetStatus("Gửi thất bại: " + result.Error, MessageType.Error); return; }
            var note = skipped > 0 ? $" Bỏ qua {skipped} người thiếu tikTokUserId." : "";
            var reply = string.IsNullOrWhiteSpace(result.Body) ? "" : "\nServer trả về: " + Trim(result.Body);
            SetStatus($"Đã gửi {players.Count} người.{note}{reply}", skipped > 0 ? MessageType.Warning : MessageType.Info);
            await LoadList();
        }

        /// <summary>GET /api/rank/me/{id} plus the player's row on the board being viewed.</summary>
        private async Task Lookup(string tikTokUserId)
        {
            if (string.IsNullOrWhiteSpace(tikTokUserId)) { SetStatus("Nhập tikTokUserId.", MessageType.Warning); return; }
            SetStatus($"Đang tìm {tikTokUserId}…", MessageType.Info);
            var me = await Send("GET", RecoilRankServer.MeUrl(_serverUrl, tikTokUserId), null);
            var board = await Send("GET", RecoilRankServer.UserUrl(_serverUrl, Board, tikTokUserId), null);
            var text = new StringBuilder();
            text.Append("/me: ").Append(me.Error ?? Trim(me.Body));
            text.Append('\n').Append(BoardNames[_board]).Append(": ").Append(board.Error ?? Trim(board.Body));
            SetStatus(text.ToString(), me.Error != null && board.Error != null ? MessageType.Warning : MessageType.Info);
        }

        private async Task LoadList(bool announce = false)
        {
            var result = await Send("GET", RecoilRankServer.BoardUrl(_serverUrl, Board), null);
            if (result.Error != null) { SetStatus("Không tải được bảng: " + result.Error, MessageType.Error); return; }
            List<RecoilRankServer.Entry> entries;
            try { entries = RecoilRankServer.ParseList(result.Body, Board); }
            catch (Exception exception) { SetStatus("Bảng trả về không đọc được: " + exception.Message + "\n" + Trim(result.Body), MessageType.Error); return; }
            _listed.Clear();
            _listed.AddRange(entries);
            // After a push its own report stays on screen.
            if (announce) SetStatus($"Đã tải {entries.Count} người ({BoardNames[_board]}).", MessageType.Info);
        }

        private static string Trim(string body)
        {
            body = (body ?? "").Trim();
            return body.Length > 600 ? body.Substring(0, 600) + "…" : body;
        }

        private readonly struct Response
        {
            public readonly long Code;
            public readonly string Body, Error;
            public Response(long code, string body, string error) { Code = code; Body = body; Error = error; }
        }

        /// <summary>One call; waits and retries when the server asks to slow down.</summary>
        private static async Task<Response> Send(string method, string url, string body)
        {
            for (var attempt = 0; ; attempt++)
            {
                using var request = body != null
                    ? RecoilRankServer.JsonRequest(url, method, body)
                    : new UnityWebRequest(url, method) { downloadHandler = new DownloadHandlerBuffer(), timeout = 30 };
                await request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                    return new Response(request.responseCode, request.downloadHandler.text, null);
                if ((request.responseCode == 429 || request.responseCode == 503) && attempt < 3)
                {
                    var wait = float.TryParse(request.GetResponseHeader("Retry-After"), out var seconds) ? seconds : 2f;
                    await Task.Delay(TimeSpan.FromSeconds(Mathf.Clamp(wait, 1f, 30f)));
                    continue;
                }
                return new Response(request.responseCode, request.downloadHandler.text, RecoilRankServer.Describe(request));
            }
        }

        private void SetStatus(string text, MessageType type)
        {
            _status = text;
            _statusType = type;
            Repaint();
        }
    }
}
