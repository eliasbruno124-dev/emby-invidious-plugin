using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.InvidiousPlugin
{
    public static class InvidiousApi
    {
        private static readonly HttpClient Http = new HttpClient(
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            })
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestHeaders =
            {
                { "User-Agent", "EmbyInvidiousPlugin/1.0 (+https://github.com/eliasbruno124-dev/Emby-Invidious-Plugin)" },
                { "Accept", "application/json" }
            }
        };

        private static readonly int[] RetryDelaysMs = { 800, 2500 };

        private static Uri BaseUri(string baseUrl) =>
            new Uri((baseUrl ?? "").TrimEnd('/') + "/");

        private static string? BuildAuthorizationHeader(Uri baseUri)
        {
            if (string.IsNullOrWhiteSpace(baseUri.UserInfo)) return null;
            var bytes = Encoding.UTF8.GetBytes(baseUri.UserInfo);
            return "Basic " + Convert.ToBase64String(bytes);
        }

        private static HttpRequestMessage CreateGetRequest(string baseUrl, string relativePath)
        {
            var baseUri = BaseUri(baseUrl);
            var auth = BuildAuthorizationHeader(baseUri);
            var url = new Uri(baseUri, relativePath.TrimStart('/'));
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(auth))
                req.Headers.TryAddWithoutValidation("Authorization", auth);
            return req;
        }

        private static bool IsTransientError(HttpStatusCode code) =>
            code == HttpStatusCode.TooManyRequests ||
            code == HttpStatusCode.InternalServerError ||
            code == HttpStatusCode.BadGateway ||
            code == HttpStatusCode.ServiceUnavailable ||
            code == HttpStatusCode.GatewayTimeout;

        private static async Task<JsonDocument> GetJsonAsync(string baseUrl, string relativePath, CancellationToken ct)
        {
            HttpStatusCode lastStatus = 0;

            for (int attempt = 0; attempt <= RetryDelaysMs.Length; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(RetryDelaysMs[attempt - 1], ct).ConfigureAwait(false);

                using var req = CreateGetRequest(baseUrl, relativePath);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                }

                lastStatus = resp.StatusCode;
                if (!IsTransientError(lastStatus))
                    break;
            }

            throw new HttpRequestException($"Invidious returned HTTP {(int)lastStatus} for: {relativePath}");
        }

        private static async Task<JsonDocument?> TryGetJsonAsync(string baseUrl, string relativePath, CancellationToken ct)
        {
            try { return await GetJsonAsync(baseUrl, relativePath, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InvidiousApi] TryGetJsonAsync failed for {relativePath}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        public static async Task<(string? id, string? name, string? thumb)> GetChannelDetailsAsync(
            string baseUrl, string query, bool isHandle, CancellationToken ct)
        {
            try
            {
                if (isHandle)
                {
                    var q = Uri.EscapeDataString(query);
                    using var doc = await TryGetJsonAsync(baseUrl, $"api/v1/search?q={q}&type=channel", ct).ConfigureAwait(false);
                    if (doc == null) return (null, null, null);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                    {
                        var first = root[0];
                        return (GetString(first, "authorId"), GetString(first, "author"), GetHighestResThumb(first, "authorThumbnails"));
                    }
                }
                else
                {
                    var id = Uri.EscapeDataString(query);
                    using var doc = await TryGetJsonAsync(baseUrl, $"api/v1/channels/{id}", ct).ConfigureAwait(false);
                    if (doc == null) return (null, null, null);
                    var root = doc.RootElement;
                    return (id, GetString(root, "author"), GetHighestResThumb(root, "authorThumbnails"));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InvidiousApi] GetChannelDetailsAsync failed for '{query}': {ex.GetType().Name}: {ex.Message}");
            }
            return (null, null, null);
        }

        public static async Task<(string? name, string? thumb)> GetPlaylistDetailsAsync(
            string baseUrl, string id, CancellationToken ct)
        {
            try
            {
                var escId = Uri.EscapeDataString(id);
                using var doc = await TryGetJsonAsync(baseUrl, $"api/v1/playlists/{escId}", ct).ConfigureAwait(false);
                if (doc == null) return (null, null);
                var root = doc.RootElement;
                var thumb = GetString(root, "playlistThumbnail") ?? GetHighestResThumb(root, "playlistThumbnails");
                return (GetString(root, "title"), thumb);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InvidiousApi] GetPlaylistDetailsAsync failed for '{id}': {ex.GetType().Name}: {ex.Message}");
            }
            return (null, null);
        }

        public static Task<JsonDocument> SearchVideosAsync(string baseUrl, string query, int page, CancellationToken ct)
        {
            var q = Uri.EscapeDataString(query ?? "");
            return GetJsonAsync(baseUrl, $"api/v1/search?q={q}&type=video&page={page}", ct);
        }

        public static Task<JsonDocument> GetTrendingAsync(string baseUrl, string? category, CancellationToken ct, string? region = null)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(category))
                parts.Add($"type={Uri.EscapeDataString(category)}");
            if (!string.IsNullOrEmpty(region))
                parts.Add($"region={Uri.EscapeDataString(region)}");
            var path = "api/v1/trending";
            if (parts.Count > 0) path += "?" + string.Join("&", parts);
            return GetJsonAsync(baseUrl, path, ct);
        }

        public static Task<JsonDocument> GetPopularAsync(string baseUrl, CancellationToken ct)
        {
            return GetJsonAsync(baseUrl, "api/v1/popular", ct);
        }

        public static Task<JsonDocument> GetChannelVideosAsync(string baseUrl, string channelId, int page, CancellationToken ct)
        {
            var id = Uri.EscapeDataString(channelId ?? "");
            return GetJsonAsync(baseUrl, $"api/v1/channels/{id}/videos?page={page}&sort_by=newest", ct);
        }

        public static Task<JsonDocument> GetPlaylistVideosAsync(string baseUrl, string playlistId, int page, CancellationToken ct)
        {
            var id = Uri.EscapeDataString(playlistId ?? "");
            return GetJsonAsync(baseUrl, $"api/v1/playlists/{id}?page={page}", ct);
        }

        public static Task<JsonDocument> GetVideoAsync(string baseUrl, string videoId, CancellationToken ct)
        {
            var id = Uri.EscapeDataString(videoId ?? "");
            return GetJsonAsync(baseUrl, $"api/v1/videos/{id}", ct);
        }

        public static Task<JsonDocument?> TryGetVideoAsync(string baseUrl, string videoId, CancellationToken ct)
        {
            var id = Uri.EscapeDataString(videoId ?? "");
            return TryGetJsonAsync(baseUrl, $"api/v1/videos/{id}", ct);
        }

        public static Dictionary<string, string> BuildPlaybackHeaders(string baseUrl)
        {
            var headers = new Dictionary<string, string>();
            var baseUri = BaseUri(baseUrl);
            var auth = BuildAuthorizationHeader(baseUri);
            if (!string.IsNullOrWhiteSpace(auth)) headers["Authorization"] = auth!;
            return headers;
        }

        public static string GetCleanBaseUrl(string baseUrl)
        {
            try
            {
                var uri = new Uri((baseUrl ?? "").TrimEnd('/') + "/");
                var port = uri.IsDefaultPort ? "" : ":" + uri.Port;
                return $"{uri.Scheme}://{uri.Host}{port}{uri.AbsolutePath}".TrimEnd('/');
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InvidiousApi] GetCleanBaseUrl parse failed: {ex.Message}");
                return (baseUrl ?? "").TrimEnd('/');
            }
        }

        public static string? GetHighestResThumb(JsonElement el, string propertyName)
        {
            if (!el.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;

            string? bestUrl = null;
            int bestWidth = 0;

            foreach (var thumb in arr.EnumerateArray())
            {
                var url = GetString(thumb, "url");
                if (string.IsNullOrWhiteSpace(url)) continue;
                url = RewriteThumbnailUrl(url);

                var w = GetInt(thumb, "width") ?? 0;
                if (w >= bestWidth) { bestWidth = w; bestUrl = url; }
            }

            return bestUrl;
        }

        internal static string RewriteThumbnailUrl(string url)
        {
            if (url.StartsWith("//")) url = "https:" + url;

            var ggphtIdx = url.IndexOf("/ggpht/", StringComparison.Ordinal);
            if (ggphtIdx >= 0 && !url.Contains("yt3.ggpht.com", StringComparison.Ordinal))
                url = "https://yt3.ggpht.com" + url.Substring(ggphtIdx + 6);

            var viIdx = url.IndexOf("/vi/", StringComparison.Ordinal);
            if (viIdx >= 0 && !url.Contains("i.ytimg.com", StringComparison.Ordinal)
                           && !url.Contains("yt3.ggpht.com", StringComparison.Ordinal))
                url = "https://i.ytimg.com" + url.Substring(viIdx);

            return url;
        }

        public static string? GetString(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var p)) return null;
            return p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString();
        }

        public static int? GetInt(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var p)) return null;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i)) return i;
            if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out var s)) return s;
            return null;
        }

        public static long? GetLong(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var p)) return null;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var i)) return i;
            if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var s)) return s;
            return null;
        }
    }
}
