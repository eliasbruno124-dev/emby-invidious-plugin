using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.InvidiousPlugin
{
    public class InvidiousApi
    {
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

        private static Uri BaseUri(string baseUrl) => new Uri((baseUrl ?? "").TrimEnd('/') + "/");

        private static string? BuildAuthorizationFromUserInfo(Uri baseUri)
        {
            if (string.IsNullOrWhiteSpace(baseUri.UserInfo)) return null;
            var bytes = Encoding.ASCII.GetBytes(baseUri.UserInfo);
            return "Basic " + Convert.ToBase64String(bytes);
        }

        private static HttpRequestMessage CreateGet(string baseUrl, string relative)
        {
            var baseUri = BaseUri(baseUrl);
            var auth = BuildAuthorizationFromUserInfo(baseUri);
            var url = new Uri(baseUri, relative.TrimStart('/'));

            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (!string.IsNullOrWhiteSpace(auth))
                req.Headers.TryAddWithoutValidation("Authorization", auth);

            return req;
        }

        private static async Task<JsonDocument> GetJsonAsync(string baseUrl, string relative, CancellationToken ct)
        {
            using var req = CreateGet(baseUrl, relative);
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
        }

        
        public async Task<(string? id, string? name, string? thumb)> GetChannelDetailsAsync(string baseUrl, string query, bool isHandle, CancellationToken ct)
        {
            try
            {
                if (isHandle)
                {
                    var q = Uri.EscapeDataString(query);
                    using var doc = await GetJsonAsync(baseUrl, $"api/v1/search?q={q}&type=channel", ct).ConfigureAwait(false);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                    {
                        var first = root[0];
                        var authorId = GetString(first, "authorId");
                        var author = GetString(first, "author");
                        var thumb = GetHighestResThumb(first, "authorThumbnails");
                        return (authorId, author, thumb);
                    }
                }
                else
                {
                    var id = Uri.EscapeDataString(query);
                    using var doc = await GetJsonAsync(baseUrl, $"api/v1/channels/{id}", ct).ConfigureAwait(false);
                    var root = doc.RootElement;
                    var author = GetString(root, "author");
                    var thumb = GetHighestResThumb(root, "authorThumbnails");
                    return (id, author, thumb);
                }
            }
            catch { }
            return (null, null, null);
        }

        
        public async Task<(string? name, string? thumb)> GetPlaylistDetailsAsync(string baseUrl, string id, CancellationToken ct)
        {
            try
            {
                var escId = Uri.EscapeDataString(id);
                using var doc = await GetJsonAsync(baseUrl, $"api/v1/playlists/{escId}", ct).ConfigureAwait(false);
                var root = doc.RootElement;
                var title = GetString(root, "title");
                var thumb = GetHighestResThumb(root, "playlistThumbnails");
                return (title, thumb);
            }
            catch { }
            return (null, null);
        }

        
        private static string? GetHighestResThumb(JsonElement el, string propertyName)
        {
            if (el.TryGetProperty(propertyName, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                string? bestUrl = null;
                int bestWidth = 0;
                foreach (var thumb in arr.EnumerateArray())
                {
                    var url = GetString(thumb, "url");
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    if (url.StartsWith("//")) url = "https:" + url;

                    var w = GetInt(thumb, "width") ?? 0;
                    if (w >= bestWidth)
                    {
                        bestWidth = w;
                        bestUrl = url;
                    }
                }
                return bestUrl;
            }
            return null;
        }

        public Task<JsonDocument> SearchVideosAsync(string baseUrl, string query, int page, CancellationToken ct)
        {
            var q = Uri.EscapeDataString(query ?? "");
            return GetJsonAsync(baseUrl, $"api/v1/search?q={q}&type=video&page={page}", ct);
        }

        public Task<JsonDocument> GetChannelVideosAsync(string baseUrl, string channelId, int page, CancellationToken ct)
        {
            var id = Uri.EscapeDataString(channelId ?? "");
            return GetJsonAsync(baseUrl, $"api/v1/channels/{id}/videos?page={page}", ct);
        }

        public Task<JsonDocument> GetPlaylistVideosAsync(string baseUrl, string playlistId, int page, CancellationToken ct)
        {
            var id = Uri.EscapeDataString(playlistId ?? "");
            return GetJsonAsync(baseUrl, $"api/v1/playlists/{id}?page={page}", ct);
        }

        public Task<JsonDocument> GetVideoAsync(string baseUrl, string videoId, CancellationToken ct)
        {
            var id = Uri.EscapeDataString(videoId ?? "");
            return GetJsonAsync(baseUrl, $"api/v1/videos/{id}", ct);
        }

        public (string? mp4, string? dash, string? hls) ExtractAllStreams(string baseUrl, JsonDocument videoDoc)
        {
            var root = videoDoc.RootElement;

            string? dashUrl = GetString(root, "dashUrl");
            if (!string.IsNullOrEmpty(dashUrl) && dashUrl.StartsWith("/")) dashUrl = baseUrl + dashUrl;

            string? hlsUrl = GetString(root, "hlsUrl");
            if (!string.IsNullOrEmpty(hlsUrl) && hlsUrl.StartsWith("/")) hlsUrl = baseUrl + hlsUrl;

            var candidates = new List<(string url, int bitrate, string container)>();

            if (root.TryGetProperty("formatStreams", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var url = GetString(el, "url");
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    var bitrate = GetInt(el, "bitrate") ?? 0;
                    var container = GetString(el, "container") ?? "";
                    candidates.Add((url!, bitrate, container));
                }
            }

            string? mp4Url = null;
            if (candidates.Count > 0)
            {
                mp4Url = candidates
                    .Where(c => c.container.ToLowerInvariant().Contains("mp4"))
                    .OrderByDescending(c => c.bitrate)
                    .FirstOrDefault().url ?? candidates.OrderByDescending(c => c.bitrate).First().url;
            }

            return (mp4Url, dashUrl, hlsUrl);
        }

        public static Dictionary<string, string> BuildPlaybackHeaders(string baseUrl)
        {
            var headers = new Dictionary<string, string>();
            var baseUri = BaseUri(baseUrl);
            var auth = BuildAuthorizationFromUserInfo(baseUri);
            if (!string.IsNullOrWhiteSpace(auth)) headers["Authorization"] = auth!;
            return headers;
        }

        public static string? GetString(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var p)) return null;
            if (p.ValueKind == JsonValueKind.String) return p.GetString();
            return p.ToString();
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