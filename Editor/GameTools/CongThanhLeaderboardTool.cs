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
    /// Writes the Unity Cloud leaderboard the battle shows (Weekly by default) with a service account:
    /// push, update and delete scores, one entry at a time (Single) or a JSON list (Multi), and see what is on it.
    /// Each entry: { "tiktokID", "name", "avatarurl", "score" }; the TikTok id is the leaderboard's player id and
    /// tiktokID / name / avatarurl go in the score's metadata.
    /// The service account key stays on this computer (EditorPrefs); it is never saved in the project.
    /// </summary>
    public sealed class CongThanhLeaderboardTool : EditorWindow
    {
        private const string Prefix = "CongThanhLeaderboard.";
        private const string AdminApi = "https://services.api.unity.com";

        private string _projectId, _environmentId, _leaderboardId, _keyId, _secret;
        private int _mode; // 0 single, 1 multi
        private RecoilCloudLeaderboard.Entry _single = new();
        private string _multiJson = "[\n  { \"tiktokID\": \"user1\", \"name\": \"Người chơi 1\", \"avatarurl\": \"\", \"score\": 1000 }\n]";
        private readonly List<RecoilCloudLeaderboard.Entry> _listed = new();
        private int _listedTotal;
        private Vector2 _scroll, _listScroll;
        private string _status;
        private MessageType _statusType = MessageType.Info;
        private bool _busy, _showKey;
        private string _accessToken;
        private DateTime _accessTokenExpires;

        [MenuItem("Game Tools/Công Thành/Leaderboard (Unity Cloud)")]
        private static void Open() => GetWindow<CongThanhLeaderboardTool>("Leaderboard Cloud").minSize = new Vector2(460f, 520f);

        private void OnEnable()
        {
            _projectId = EditorPrefs.GetString(Prefix + "Project", CloudProjectSettings.projectId);
            if (string.IsNullOrEmpty(_projectId)) _projectId = CloudProjectSettings.projectId;
            _environmentId = EditorPrefs.GetString(Prefix + "Environment", "");
            _leaderboardId = EditorPrefs.GetString(Prefix + "Leaderboard", RecoilCloudLeaderboard.DefaultLeaderboardId);
            _keyId = EditorPrefs.GetString(Prefix + "KeyId", "");
            _secret = EditorPrefs.GetString(Prefix + "Secret", "");
            _mode = EditorPrefs.GetInt(Prefix + "Mode", 0);
            _multiJson = EditorPrefs.GetString(Prefix + "MultiJson", _multiJson);
        }

        private void SaveSettings()
        {
            EditorPrefs.SetString(Prefix + "Project", _projectId ?? "");
            EditorPrefs.SetString(Prefix + "Environment", _environmentId ?? "");
            EditorPrefs.SetString(Prefix + "Leaderboard", _leaderboardId ?? "");
            EditorPrefs.SetString(Prefix + "KeyId", _keyId ?? "");
            EditorPrefs.SetString(Prefix + "Secret", _secret ?? "");
            EditorPrefs.SetInt(Prefix + "Mode", _mode);
            EditorPrefs.SetString(Prefix + "MultiJson", _multiJson ?? "");
        }

        // ---------------------------------------------------------------- UI

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.LabelField("Kết nối", EditorStyles.boldLabel);
            _projectId = EditorGUILayout.TextField(new GUIContent("Project ID", "Unity Cloud project (mặc định lấy từ dự án)."), _projectId);
            _environmentId = EditorGUILayout.TextField(new GUIContent("Environment ID",
                "ID môi trường (Dashboard > Projects > Environments), ví dụ môi trường production."), _environmentId);
            _leaderboardId = EditorGUILayout.TextField("Leaderboard ID", _leaderboardId);
            _keyId = EditorGUILayout.TextField(new GUIContent("Service account Key ID",
                "Dashboard > Administration > Service accounts > Keys. Cần quyền Leaderboards."), _keyId);
            using (new EditorGUILayout.HorizontalScope())
            {
                _secret = _showKey ? EditorGUILayout.TextField("Secret key", _secret) : EditorGUILayout.PasswordField("Secret key", _secret);
                _showKey = GUILayout.Toggle(_showKey, "Hiện", GUILayout.Width(44f));
            }
            EditorGUILayout.HelpBox("Khoá chỉ lưu trên máy này (EditorPrefs), không nằm trong dự án hay bản build.", MessageType.None);
            if (EditorGUI.EndChangeCheck()) { SaveSettings(); _accessToken = null; }

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
            _single.tiktokID = EditorGUILayout.TextField("tiktokID", _single.tiktokID);
            _single.name = EditorGUILayout.TextField("name", _single.name);
            _single.avatarurl = EditorGUILayout.TextField("avatarurl", _single.avatarurl);
            _single.score = EditorGUILayout.DoubleField("score", _single.score);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Push", "Gửi điểm. Bảng giữ điểm theo kiểu cập nhật đã đặt trên Dashboard (vd. giữ điểm cao nhất)."), GUILayout.Height(26f)))
                    Run(() => PushAll(new List<RecoilCloudLeaderboard.Entry> { _single }, false));
                if (GUILayout.Button(new GUIContent("Update", "Đặt đúng điểm này (xoá điểm cũ rồi ghi lại), kể cả khi thấp hơn."), GUILayout.Height(26f)))
                    Run(() => PushAll(new List<RecoilCloudLeaderboard.Entry> { _single }, true));
                if (GUILayout.Button("Delete", GUILayout.Height(26f)) &&
                    EditorUtility.DisplayDialog("Xoá điểm", $"Xoá {_single.tiktokID} khỏi {_leaderboardId}?", "Xoá", "Huỷ"))
                    Run(() => DeleteAll(new List<string> { _single.tiktokID }));
            }
        }

        private void DrawMulti()
        {
            EditorGUILayout.LabelField("Mảng JSON: [{ \"tiktokID\", \"name\", \"avatarurl\", \"score\" }, …]", EditorStyles.miniLabel);
            EditorGUI.BeginChangeCheck();
            _multiJson = EditorGUILayout.TextArea(_multiJson, GUILayout.MinHeight(140f));
            if (EditorGUI.EndChangeCheck()) SaveSettings();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Push tất cả", GUILayout.Height(26f)) && TryParseMulti(out var push)) Run(() => PushAll(push, false));
                if (GUILayout.Button("Update tất cả", GUILayout.Height(26f)) && TryParseMulti(out var update)) Run(() => PushAll(update, true));
                if (GUILayout.Button("Delete tất cả", GUILayout.Height(26f)) && TryParseMulti(out var delete) &&
                    EditorUtility.DisplayDialog("Xoá điểm", $"Xoá {delete.Count} người trong danh sách khỏi {_leaderboardId}?", "Xoá", "Huỷ"))
                    Run(() => DeleteAll(delete.ConvertAll(e => e.tiktokID)));
            }
            if (GUILayout.Button("Lấy danh sách hiện tại vào ô JSON"))
            {
                var array = new JArray();
                foreach (var entry in _listed)
                    array.Add(new JObject { ["tiktokID"] = entry.tiktokID, ["name"] = entry.name, ["avatarurl"] = entry.avatarurl, ["score"] = entry.score });
                _multiJson = array.ToString();
                SaveSettings();
                GUI.FocusControl(null);
            }
        }

        private void DrawList()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Trên bảng: {_listed.Count}/{_listedTotal}", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(_busy))
                {
                    if (GUILayout.Button("↻ Tải", GUILayout.Width(70f))) Run(() => LoadList(true));
                    if (GUILayout.Button("Xoá toàn bộ", GUILayout.Width(90f)) && _listed.Count > 0 &&
                        EditorUtility.DisplayDialog("Xoá toàn bộ", $"Xoá cả {_listed.Count} người đã tải khỏi {_leaderboardId}?", "Xoá hết", "Huỷ"))
                        Run(() => DeleteAll(_listed.ConvertAll(e => e.PlayerId ?? e.tiktokID)));
                }
            }
            _listScroll = EditorGUILayout.BeginScrollView(_listScroll, GUILayout.MinHeight(180f));
            foreach (var entry in _listed.ToArray())
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    GUILayout.Label(entry.Rank.ToString(), GUILayout.Width(30f));
                    GUILayout.Label(entry.DisplayName, EditorStyles.boldLabel, GUILayout.MinWidth(100f));
                    GUILayout.Label(entry.tiktokID, EditorStyles.miniLabel, GUILayout.MinWidth(80f));
                    GUILayout.Label(entry.score.ToString("N0"), GUILayout.Width(90f));
                    using (new EditorGUI.DisabledScope(_busy))
                    {
                        if (GUILayout.Button("Sửa", GUILayout.Width(40f)))
                        {
                            _mode = 0;
                            _single = new RecoilCloudLeaderboard.Entry
                                { tiktokID = entry.tiktokID, name = entry.name, avatarurl = entry.avatarurl, score = entry.score };
                            GUI.FocusControl(null);
                        }
                        if (GUILayout.Button("✕", GUILayout.Width(24f)) &&
                            EditorUtility.DisplayDialog("Xoá điểm", $"Xoá {entry.DisplayName} khỏi {_leaderboardId}?", "Xoá", "Huỷ"))
                            Run(() => DeleteAll(new List<string> { entry.PlayerId ?? entry.tiktokID }));
                    }
                }
            EditorGUILayout.EndScrollView();
        }

        private bool TryParseMulti(out List<RecoilCloudLeaderboard.Entry> entries)
        {
            entries = new List<RecoilCloudLeaderboard.Entry>();
            try
            {
                var token = JToken.Parse(_multiJson);
                var array = token as JArray ?? new JArray(token);
                foreach (var item in array)
                    entries.Add(new RecoilCloudLeaderboard.Entry
                    {
                        tiktokID = (string)item["tiktokID"], name = (string)item["name"],
                        avatarurl = (string)item["avatarurl"], score = item["score"] != null ? (double)item["score"] : 0d
                    });
                return true;
            }
            catch (Exception exception)
            {
                SetStatus("JSON không hợp lệ: " + exception.Message, MessageType.Error);
                return false;
            }
        }

        // ---------------------------------------------------------------- service calls

        private async void Run(Func<Task> work)
        {
            if (_busy) return;
            if (string.IsNullOrWhiteSpace(_projectId) || string.IsNullOrWhiteSpace(_environmentId) ||
                string.IsNullOrWhiteSpace(_leaderboardId) || string.IsNullOrWhiteSpace(_keyId) || string.IsNullOrWhiteSpace(_secret))
            {
                SetStatus("Điền đủ Project ID, Environment ID, Leaderboard ID và khoá service account.", MessageType.Warning);
                return;
            }
            _busy = true;
            try { await work(); }
            catch (Exception exception) { SetStatus(exception.Message, MessageType.Error); }
            finally { _busy = false; Repaint(); }
        }

        private async Task PushAll(List<RecoilCloudLeaderboard.Entry> entries, bool replace)
        {
            var done = 0;
            var failures = new List<string>();
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.tiktokID)) { failures.Add("(thiếu tiktokID)"); continue; }
                SetStatus($"{(replace ? "Update" : "Push")} {done + 1}/{entries.Count}: {entry.tiktokID}…", MessageType.Info);
                // Update sets exactly this score: the old one goes first (the board may keep only the best).
                if (replace)
                {
                    var removed = await Send("DELETE", AdminScoreUrl(entry.tiktokID), null);
                    if (removed.Error != null && removed.Code != 404) { failures.Add($"{entry.tiktokID}: {removed.Error}"); continue; }
                }
                var body = new JObject { ["score"] = entry.score, ["metadata"] = entry.Metadata() }.ToString();
                var result = await Send("POST", PlayerScoreUrl(entry.tiktokID), body);
                if (result.Error != null) failures.Add($"{entry.tiktokID}: {result.Error}");
                else done++;
            }
            Report(done, entries.Count, replace ? "cập nhật" : "đẩy", failures);
            await LoadList();
        }

        private async Task DeleteAll(List<string> ids)
        {
            var done = 0;
            var failures = new List<string>();
            foreach (var id in ids)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                SetStatus($"Xoá {done + 1}/{ids.Count}: {id}…", MessageType.Info);
                var result = await Send("DELETE", AdminScoreUrl(id), null);
                if (result.Error != null && result.Code != 404) failures.Add($"{id}: {result.Error}");
                else done++;
            }
            Report(done, ids.Count, "xoá", failures);
            await LoadList();
        }

        private async Task LoadList(bool announce = false)
        {
            var entries = new List<RecoilCloudLeaderboard.Entry>();
            var total = 0;
            for (var offset = 0; offset < 1000; offset += 100)
            {
                var result = await Send("GET", $"{RecoilCloudLeaderboard.PlayerApi}/projects/{_projectId}/leaderboards/" +
                                               $"{Uri.EscapeDataString(_leaderboardId)}/scores?offset={offset}&limit=100&includeMetadata=true", null);
                if (result.Error != null)
                {
                    SetStatus("Không tải được bảng: " + result.Error, MessageType.Error);
                    return;
                }
                var page = RecoilCloudLeaderboard.ParseScores(result.Body);
                entries.AddRange(page.Entries);
                total = Mathf.Max(page.Total, entries.Count);
                if (page.Entries.Count < 100 || entries.Count >= total) break;
            }
            _listed.Clear();
            _listed.AddRange(entries);
            _listedTotal = total;
            // After a push / delete its own report stays on screen.
            if (announce) SetStatus($"Đã tải {entries.Count} người từ {_leaderboardId}.", MessageType.Info);
        }

        private void Report(int done, int count, string verb, List<string> failures)
        {
            if (failures.Count == 0) SetStatus($"Đã {verb} {done}/{count}.", MessageType.Info);
            else SetStatus($"Đã {verb} {done}/{count}. Lỗi:\n" + string.Join("\n", failures), MessageType.Warning);
        }

        private string PlayerScoreUrl(string playerId) =>
            $"{RecoilCloudLeaderboard.PlayerApi}/projects/{_projectId}/leaderboards/{Uri.EscapeDataString(_leaderboardId)}" +
            $"/scores/players/{Uri.EscapeDataString(playerId)}";

        private string AdminScoreUrl(string playerId) =>
            $"{AdminApi}/leaderboards/v1/projects/{_projectId}/environments/{_environmentId}/leaderboards/" +
            $"{Uri.EscapeDataString(_leaderboardId)}/scores/players/{Uri.EscapeDataString(playerId)}";

        private readonly struct Response
        {
            public readonly long Code;
            public readonly string Body, Error;
            public Response(long code, string body, string error) { Code = code; Body = body; Error = error; }
        }

        /// <summary>One call with the service account's token; waits and retries when the service asks to slow down.</summary>
        private async Task<Response> Send(string method, string url, string body)
        {
            for (var attempt = 0; ; attempt++)
            {
                var token = await AccessToken();
                using var request = new UnityWebRequest(url, method) { downloadHandler = new DownloadHandlerBuffer() };
                if (body != null)
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    request.SetRequestHeader("Content-Type", "application/json");
                }
                request.SetRequestHeader("Authorization", "Bearer " + token);
                await request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                    return new Response(request.responseCode, request.downloadHandler.text, null);
                if (request.responseCode == 429 && attempt < 4)
                {
                    var wait = float.TryParse(request.GetResponseHeader("Retry-After"), out var seconds) ? seconds : 2f;
                    await Task.Delay(TimeSpan.FromSeconds(Mathf.Clamp(wait, 1f, 30f)));
                    continue;
                }
                if (request.responseCode == 401 && attempt == 0) { _accessToken = null; continue; }
                return new Response(request.responseCode, request.downloadHandler.text, RecoilCloudLeaderboard.Describe(request));
            }
        }

        /// <summary>A short-lived token for this project and environment, from the service account key.</summary>
        private async Task<string> AccessToken()
        {
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _accessTokenExpires) return _accessToken;
            var url = $"{AdminApi}/auth/v1/token-exchange?projectId={_projectId}&environmentId={_environmentId}";
            using var request = new UnityWebRequest(url, "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("{}")),
                downloadHandler = new DownloadHandlerBuffer()
            };
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization",
                "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(_keyId.Trim() + ":" + _secret.Trim())));
            await request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
                throw new Exception("Không lấy được token từ service account: " + RecoilCloudLeaderboard.Describe(request));
            _accessToken = (string)JObject.Parse(request.downloadHandler.text)["accessToken"];
            if (string.IsNullOrEmpty(_accessToken)) throw new Exception("Token exchange không trả về accessToken.");
            _accessTokenExpires = DateTime.UtcNow.AddMinutes(50);
            return _accessToken;
        }

        private void SetStatus(string text, MessageType type)
        {
            _status = text;
            _statusType = type;
            Repaint();
        }
    }
}
