using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace RecoilPathfinding
{
    /// <summary>
    /// The rank server (RankServer, https://rank-tft-production.up.railway.app, Swagger at /swagger/index.html).
    /// Four boards: weekly / monthly × point / "kill". One entry per TikTok viewer.
    /// <para>
    /// Mind the server's field names: <c>matchPoint</c> is the player's <b>score</b> (điểm) and <c>killCount</c> is
    /// the <b>win streak</b> (chuỗi thắng); the ".../kill" boards rank by win streak. Nothing here counts kills.
    /// </para>
    /// <list type="bullet">
    /// <item><c>GET /api/rank/{weekly|monthly}/{point|kill}</c> — a board (<see cref="Fetch"/>).</item>
    /// <item><c>GET /api/rank/{weekly|monthly}/{point|kill}/{tikTokUserId}</c>, <c>GET /api/rank/me/{tikTokUserId}</c> — one viewer.</item>
    /// <item><c>POST /api/rank/update</c> with <c>{ "players": [ { tikTokUserId, username, avatar, matchPoint, killCount } ] }</c>
    /// — match results (the editor tool, Game Tools > Công Thành > Leaderboard).</item>
    /// </list>
    /// </summary>
    public static class RecoilRankServer
    {
        public const string DefaultUrl = "https://rank-tft-production.up.railway.app";

        public enum Board { WeeklyPoint, WeeklyStreak, MonthlyPoint, MonthlyStreak }

        /// <summary>The board's path under /api/rank (the server calls the streak boards "kill").</summary>
        public static string BoardPath(Board board) => board switch
        {
            Board.WeeklyPoint => "weekly/point",
            Board.WeeklyStreak => "weekly/kill",
            Board.MonthlyPoint => "monthly/point",
            Board.MonthlyStreak => "monthly/kill",
            _ => "weekly/point"
        };

        public static string BoardTitle(Board board) => board switch
        {
            Board.WeeklyPoint => "Tuần · Điểm",
            Board.WeeklyStreak => "Tuần · Chuỗi thắng",
            Board.MonthlyPoint => "Tháng · Điểm",
            Board.MonthlyStreak => "Tháng · Chuỗi thắng",
            _ => "Tuần · Điểm"
        };

        public static bool IsStreak(Board board) => board == Board.WeeklyStreak || board == Board.MonthlyStreak;

        /// <summary>What a row's value is counted in on the given board.</summary>
        public static string Unit(Board board) => IsStreak(board) ? "trận thắng" : "điểm";

        /// <summary>One row: what the tool pushes and the game shows.</summary>
        [Serializable]
        public sealed class Entry
        {
            public string tikTokUserId;
            public string username;
            public string avatar;
            /// <summary>The score (điểm). The server's own name for it.</summary>
            public int matchPoint;
            /// <summary>The win streak (chuỗi thắng). The server's own name for it; not a kill count.</summary>
            public int killCount;
            /// <summary>1 = first on the board it was read from (0 when the server gave none).</summary>
            [NonSerialized] public int Rank;
            /// <summary>What the board it was read from ranks by: matchPoint on a point board, killCount on a streak board.</summary>
            [NonSerialized] public double Value;

            public string DisplayName => !string.IsNullOrWhiteSpace(username) ? username : tikTokUserId ?? "?";

            /// <summary>The entry as the server's MatchResultRequest.</summary>
            public JObject ToRequest() => new()
            {
                ["tikTokUserId"] = tikTokUserId ?? "", ["username"] = username ?? "", ["avatar"] = avatar ?? "",
                ["matchPoint"] = matchPoint, ["killCount"] = killCount
            };
        }

        public static string BoardUrl(string baseUrl, Board board) => $"{Base(baseUrl)}/api/rank/{BoardPath(board)}";
        public static string UserUrl(string baseUrl, Board board, string tikTokUserId) =>
            $"{BoardUrl(baseUrl, board)}/{Uri.EscapeDataString(tikTokUserId ?? "")}";
        public static string MeUrl(string baseUrl, string tikTokUserId) => $"{Base(baseUrl)}/api/rank/me/{Uri.EscapeDataString(tikTokUserId ?? "")}";
        public static string UpdateUrl(string baseUrl) => $"{Base(baseUrl)}/api/rank/update";

        private static string Base(string baseUrl) =>
            (string.IsNullOrWhiteSpace(baseUrl) ? DefaultUrl : baseUrl.Trim()).TrimEnd('/');

        /// <summary>The body of POST /api/rank/update for these players.</summary>
        public static string UpdateBody(IEnumerable<Entry> entries)
        {
            var players = new JArray();
            foreach (var entry in entries) players.Add(entry.ToRequest());
            return new JObject { ["players"] = players }.ToString();
        }

        // ---------------------------------------------------------------- reading (game)

        /// <summary>Reads a board. <paramref name="done"/> gets the rows (first first), or null and an error message.</summary>
        public static IEnumerator Fetch(string baseUrl, Board board, Action<List<Entry>, string> done)
        {
            using var request = UnityWebRequest.Get(BoardUrl(baseUrl, board));
            request.timeout = 15;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success) { done(null, Describe(request)); yield break; }
            List<Entry> entries;
            try { entries = ParseList(request.downloadHandler.text, board); }
            catch (Exception exception) { done(null, "Dữ liệu bảng xếp hạng không đọc được: " + exception.Message); yield break; }
            done(entries, null);
        }

        // ---------------------------------------------------------------- parsing (tolerant of the server's naming)

        /// <summary>
        /// A board response: a JSON array, or an object holding one (data / items / players / results / list).
        /// Rows are sorted by their value when the server gave no rank.
        /// </summary>
        public static List<Entry> ParseList(string json, Board board)
        {
            var entries = new List<Entry>();
            if (string.IsNullOrWhiteSpace(json)) return entries;
            var root = JToken.Parse(json);
            var array = root as JArray;
            if (array == null && root is JObject holder)
            {
                foreach (var key in new[] { "data", "items", "players", "results", "list", "ranks", "rank", "entries" })
                    if (Field(holder, key) is JArray inner) { array = inner; break; }
                if (array == null && Field(holder, "message") != null) return entries; // { "message": "No rank" }
                if (array == null) array = new JArray(holder);
            }
            if (array == null) return entries;
            foreach (var item in array)
                if (item is JObject row) entries.Add(ParseEntry(row, board));
            var ranked = true;
            foreach (var entry in entries) if (entry.Rank <= 0) { ranked = false; break; }
            if (ranked) entries.Sort((a, b) => a.Rank.CompareTo(b.Rank));
            else
            {
                entries.Sort((a, b) => b.Value.CompareTo(a.Value));
                for (var i = 0; i < entries.Count; i++) entries[i].Rank = i + 1;
            }
            return entries;
        }

        /// <summary>
        /// One row, whatever the server (or a JSON file) calls its fields: tikTokUserId / tiktokID / userId / id,
        /// username / name, avatar / avatarurl, matchPoint / point(s) / score, killCount / streak / winStreak.
        /// </summary>
        public static Entry ParseEntry(JObject row, Board board)
        {
            var entry = new Entry
            {
                tikTokUserId = Text(row, "tikTokUserId", "tiktokId", "tiktokID", "userId", "id", "playerId"),
                username = Text(row, "username", "userName", "name", "displayName", "nickname"),
                avatar = Text(row, "avatar", "avatarUrl", "avatarurl", "profilePicture", "profilePictureUrl"),
                Rank = (int)Number(row, 0d, "rank", "position", "index")
            };
            var point = Number(row, double.NaN, "matchPoint", "point", "points", "totalPoint", "totalPoints", "score", "weeklyPoint", "monthlyPoint");
            var streak = Number(row, double.NaN, "killCount", "streak", "winStreak", "kill", "kills", "totalKill", "totalKills", "weeklyKill", "monthlyKill");
            entry.matchPoint = double.IsNaN(point) ? 0 : (int)Math.Round(point);
            entry.killCount = double.IsNaN(streak) ? 0 : (int)Math.Round(streak);
            // What this board ranks by; a lone "value" field is taken as that when the named one is missing.
            var value = IsStreak(board) ? streak : point;
            if (double.IsNaN(value)) value = Number(row, 0d, "value", "total");
            entry.Value = value;
            return entry;
        }

        private static JToken Field(JObject row, string key)
        {
            foreach (var property in row.Properties())
                if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase)) return property.Value;
            return null;
        }

        private static string Text(JObject row, params string[] keys)
        {
            foreach (var key in keys)
            {
                var token = Field(row, key);
                if (token == null || token.Type == JTokenType.Null) continue;
                var text = token.Type == JTokenType.String ? (string)token : token.ToString();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            return null;
        }

        private static double Number(JObject row, double fallback, params string[] keys)
        {
            foreach (var key in keys)
            {
                var token = Field(row, key);
                if (token == null) continue;
                if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) return (double)token;
                if (token.Type == JTokenType.String &&
                    double.TryParse((string)token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
            }
            return fallback;
        }

        // ---------------------------------------------------------------- shared

        /// <summary>A request with a JSON body.</summary>
        public static UnityWebRequest JsonRequest(string url, string method, string body)
        {
            var request = new UnityWebRequest(url, method)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body ?? "")),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 30
            };
            request.SetRequestHeader("Content-Type", "application/json");
            return request;
        }

        /// <summary>A failed request in one line: status and the server's own message.</summary>
        public static string Describe(UnityWebRequest request)
        {
            var body = request.downloadHandler?.text;
            var detail = request.error;
            if (!string.IsNullOrEmpty(body))
            {
                try
                {
                    var json = JObject.Parse(body);
                    detail = (string)json["message"] ?? (string)json["detail"] ?? (string)json["title"] ?? (string)json["error"] ?? body;
                }
                catch { detail = body.Length > 200 ? body.Substring(0, 200) : body; }
            }
            return $"HTTP {request.responseCode}: {detail}";
        }
    }
}
