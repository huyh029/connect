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
    /// Unity Gaming Services leaderboard over its web API (the same service and data the Leaderboards SDK uses).
    /// One entry per TikTok viewer: the player id is the TikTok id, the score is the score, and the metadata holds
    /// { "tiktokID", "name", "avatarurl" }.
    /// <list type="bullet">
    /// <item>The game reads it as an anonymous player (<see cref="Fetch"/>).</item>
    /// <item>The editor tool (Game Tools > Công Thành > Leaderboard) writes it with a service account.</item>
    /// </list>
    /// </summary>
    public static class RecoilCloudLeaderboard
    {
        public const string DefaultLeaderboardId = "Weekly";
        public const string PlayerApi = "https://leaderboards.services.api.unity.com/v1";
        public const string PlayerAuthApi = "https://player-auth.services.api.unity.com/v1";
        private const string SessionTokenKey = "RecoilCloudLeaderboard.SessionToken";

        /// <summary>One row: what the tool pushes and the game shows.</summary>
        [Serializable]
        public sealed class Entry
        {
            public string tiktokID;
            public string name;
            public string avatarurl;
            public double score;
            [NonSerialized] public int Rank;       // 1 = first (from the service)
            [NonSerialized] public string PlayerId; // the leaderboard's player id (the TikTok id)

            public string DisplayName => !string.IsNullOrWhiteSpace(name) ? name : tiktokID ?? PlayerId ?? "?";

            /// <summary>The metadata stored with the score.</summary>
            public JObject Metadata() => new() { ["tiktokID"] = tiktokID ?? "", ["name"] = name ?? "", ["avatarurl"] = avatarurl ?? "" };
        }

        public sealed class Page
        {
            public readonly List<Entry> Entries = new();
            public int Total;
        }

        // ---------------------------------------------------------------- reading (game, anonymous player)

        private static string _idToken;
        private static float _idTokenExpiresAt;

        /// <summary>
        /// Reads the top <paramref name="limit"/> (at most 100) of <paramref name="leaderboardId"/>, metadata
        /// included. <paramref name="done"/> gets the page, or null and an error message.
        /// </summary>
        public static IEnumerator Fetch(string leaderboardId, string environment, int limit, Action<Page, string> done)
        {
            var projectId = Application.cloudProjectId;
            if (string.IsNullOrEmpty(projectId)) { done(null, "Dự án chưa liên kết Unity Cloud (thiếu Project ID)."); yield break; }
            if (string.IsNullOrEmpty(_idToken) || Time.realtimeSinceStartup > _idTokenExpiresAt)
            {
                string error = null;
                yield return SignIn(projectId, environment, e => error = e);
                if (error != null) { done(null, error); yield break; }
            }
            var url = $"{PlayerApi}/projects/{projectId}/leaderboards/{Uri.EscapeDataString(leaderboardId)}/scores" +
                      $"?offset=0&limit={Mathf.Clamp(limit, 1, 100)}&includeMetadata=true";
            using var request = UnityWebRequest.Get(url);
            request.SetRequestHeader("Authorization", "Bearer " + _idToken);
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                if (request.responseCode == 401) _idToken = null; // signed out: sign in again next time
                done(null, Describe(request));
                yield break;
            }
            Page page;
            try { page = ParseScores(request.downloadHandler.text); }
            catch (Exception exception) { done(null, "Dữ liệu bảng xếp hạng không đọc được: " + exception.Message); yield break; }
            done(page, null);
        }

        /// <summary>Anonymous sign-in, reusing the same anonymous player between runs (its session token).</summary>
        private static IEnumerator SignIn(string projectId, string environment, Action<string> failed)
        {
            var session = PlayerPrefs.GetString(SessionTokenKey, "");
            if (!string.IsNullOrEmpty(session))
            {
                using var resume = AuthRequest($"{PlayerAuthApi}/authentication/session-token", projectId, environment,
                    new JObject { ["sessionToken"] = session }.ToString());
                yield return resume.SendWebRequest();
                if (resume.result == UnityWebRequest.Result.Success && ReadAuth(resume.downloadHandler.text)) yield break;
            }
            using var anonymous = AuthRequest($"{PlayerAuthApi}/authentication/anonymous", projectId, environment, "{}");
            yield return anonymous.SendWebRequest();
            if (anonymous.result != UnityWebRequest.Result.Success || !ReadAuth(anonymous.downloadHandler.text))
                failed("Không đăng nhập được Unity Authentication: " + Describe(anonymous));
        }

        private static UnityWebRequest AuthRequest(string url, string projectId, string environment, string body)
        {
            var request = new UnityWebRequest(url, "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer()
            };
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("ProjectId", projectId);
            if (!string.IsNullOrEmpty(environment)) request.SetRequestHeader("UnityEnvironment", environment);
            return request;
        }

        private static bool ReadAuth(string json)
        {
            var auth = JObject.Parse(json);
            var token = (string)auth["idToken"];
            if (string.IsNullOrEmpty(token)) return false;
            _idToken = token;
            var expiresIn = auth["expiresIn"] != null ? (float)auth["expiresIn"] : 3600f;
            _idTokenExpiresAt = Time.realtimeSinceStartup + Mathf.Max(60f, expiresIn - 60f);
            var session = (string)auth["sessionToken"];
            if (!string.IsNullOrEmpty(session)) PlayerPrefs.SetString(SessionTokenKey, session);
            return true;
        }

        // ---------------------------------------------------------------- shared

        /// <summary>A "get scores" response: results with rank, score and metadata (an object or a JSON string).</summary>
        public static Page ParseScores(string json)
        {
            var root = JObject.Parse(json);
            var page = new Page { Total = root["total"] != null ? (int)root["total"] : 0 };
            if (root["results"] is not JArray results) return page;
            foreach (var item in results)
            {
                var entry = new Entry
                {
                    PlayerId = (string)item["playerId"],
                    Rank = item["rank"] != null ? (int)item["rank"] + 1 : page.Entries.Count + 1, // the service ranks from 0
                    score = item["score"] != null ? (double)item["score"] : 0d
                };
                var metadata = item["metadata"];
                if (metadata is { Type: JTokenType.String })
                {
                    var text = (string)metadata;
                    metadata = string.IsNullOrWhiteSpace(text) ? null : JToken.Parse(text);
                }
                if (metadata is JObject fields)
                {
                    entry.tiktokID = (string)fields["tiktokID"];
                    entry.name = (string)fields["name"];
                    entry.avatarurl = (string)fields["avatarurl"];
                }
                entry.tiktokID ??= entry.PlayerId;
                entry.name ??= (string)item["playerName"];
                page.Entries.Add(entry);
            }
            return page;
        }

        /// <summary>A failed request in one line: status and the service's own message.</summary>
        public static string Describe(UnityWebRequest request)
        {
            var body = request.downloadHandler?.text;
            var detail = request.error;
            if (!string.IsNullOrEmpty(body))
            {
                try
                {
                    var json = JObject.Parse(body);
                    detail = (string)json["detail"] ?? (string)json["title"] ?? (string)json["message"] ?? body;
                }
                catch { detail = body.Length > 200 ? body.Substring(0, 200) : body; }
            }
            return $"HTTP {request.responseCode}: {detail}";
        }
    }
}
