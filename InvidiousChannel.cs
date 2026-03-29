using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.InvidiousPlugin
{
    public class InvidiousChannel : IChannel, IRequiresMediaInfoCallback
    {
        public string Name => "Invidious";
        public string Description => "Privacy-friendly YouTube";
        public string Id => "invidious_channel_19";
        public string DataVersion => "4.0.6"; 

        public ChannelType Type => ChannelType.TV;
        public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
        public bool IsEnabledByDefault => true;

        // ═══════════════════════════════════════════════════════════
        // FOLDER LISTING
        // ═══════════════════════════════════════════════════════════
        public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            var items = new List<ChannelItemInfo>();
            var plugin = Plugin.Instance;
            if (plugin == null) return Msg(items, "ERROR: Plugin not initialized.");
            var config = plugin.Options;
            var baseUrl = (config.InvidiousUrl ?? "").TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl)) return Msg(items, "ERROR: Please set the Invidious URL in the plugin settings.");

            try
            {
                var api = new InvidiousApi();

             
                if (string.IsNullOrEmpty(query.FolderId))
                {
                    var savedItems = (config.SavedItems ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    var menuTasks = savedItems.Select(async s =>
                    {
                        var term = s.Trim();
                        if (string.IsNullOrEmpty(term)) return null;
                        if (term.StartsWith("@"))
                        {
                            
                            var details = await api.GetChannelDetailsAsync(baseUrl, term, true, cancellationToken).ConfigureAwait(false);
                            var name = string.IsNullOrEmpty(details.name) ? term : details.name;
                            var cId = string.IsNullOrEmpty(details.id) ? term : details.id;

                            return new ChannelItemInfo { Name = $"📺 {name}", Id = $"channel_x_{cId}", Type = ChannelItemType.Folder, ImageUrl = details.thumb };
                        }
                        if (term.StartsWith("UC") && term.Length > 20)
                        {
                            var d = await api.GetChannelDetailsAsync(baseUrl, term, false, cancellationToken).ConfigureAwait(false);
                            return new ChannelItemInfo { Name = $"📺 {d.name ?? "Channel"}", Id = $"channel_x_{term}", Type = ChannelItemType.Folder, ImageUrl = d.thumb };
                        }
                        if (term.StartsWith("PL"))
                        {
                         
                            var details = await api.GetPlaylistDetailsAsync(baseUrl, term, cancellationToken).ConfigureAwait(false);
                            var name = string.IsNullOrEmpty(details.name) ? "Playlist" : details.name;

                            return new ChannelItemInfo { Name = $"🎵 {name}", Id = $"playlist_x_{term}", Type = ChannelItemType.Folder, ImageUrl = details.thumb };
                        }
                        return new ChannelItemInfo { Name = $"🔍 {term}", Id = $"search_x_{term}", Type = ChannelItemType.Folder };
                    });
                    foreach (var res in await Task.WhenAll(menuTasks).ConfigureAwait(false))
                        if (res != null) items.Add(res);
                    return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
                }

           
                if (query.FolderId.Contains("_x_"))
                {
                    var parts = query.FolderId.Split(new[] { '_' }, 3);
                    if (parts.Length < 3) return new ChannelItemResult { Items = items, TotalRecordCount = 0 };
                    string type = parts[0], term = parts[2];
                    int startIndex = query.StartIndex ?? 0;
                    int limit = type == "search" ? config.MaxSearchVideos : config.MaxChannelVideos;
                    if (limit <= 0) limit = 50;
                    if (limit > 150) limit = 150;
                    int currentPage = (startIndex / 20) + 1;
                    int skipItems = startIndex % 20;
                    var seenIds = new HashSet<string>();

                    while (items.Count < limit)
                    {
                        JsonDocument? doc = null;
                        if (type == "search") doc = await api.SearchVideosAsync(baseUrl, term, currentPage, cancellationToken).ConfigureAwait(false);
                        else if (type == "channel") doc = await api.GetChannelVideosAsync(baseUrl, term, currentPage, cancellationToken).ConfigureAwait(false);
                        else if (type == "playlist") doc = await api.GetPlaylistVideosAsync(baseUrl, term, currentPage, cancellationToken).ConfigureAwait(false);
                        if (doc == null) break;
                        var tempItems = ExtractVideos(doc);
                        doc.Dispose();
                        if (tempItems.Count == 0) break;

                        var batch = new List<ChannelItemInfo>();
                        foreach (var item in tempItems)
                        {
                            if (skipItems > 0) { skipItems--; continue; }
                            if (seenIds.Add(item.Id)) batch.Add(item);
                            if (items.Count + batch.Count >= limit) break;
                        }

                        // Fetch full details in parallel
                        var sem = new SemaphoreSlim(20);
                        await Task.WhenAll(batch.Select(async item =>
                        {
                            await sem.WaitAsync();
                            try
                            {
                                using var vDoc = await api.GetVideoAsync(baseUrl, item.Id, cancellationToken).ConfigureAwait(false);
                                var r = vDoc.RootElement;
                                var desc = InvidiousApi.GetString(r, "description");
                                var views = InvidiousApi.GetLong(r, "viewCount");
                                if (!string.IsNullOrWhiteSpace(desc))
                                    item.Overview = (views > 0 ? $"👁 {views:N0} Aufrufe\n\n" : "") + desc;
                                var pub = InvidiousApi.GetLong(r, "published");
                                if (pub.HasValue)
                                {
                                    var dt = DateTimeOffset.FromUnixTimeSeconds(pub.Value).UtcDateTime;
                                    item.PremiereDate = dt; item.DateCreated = dt; item.ProductionYear = dt.Year;
                                }
                                var len = InvidiousApi.GetInt(r, "lengthSeconds");
                                if (len > 0) item.RunTimeTicks = TimeSpan.FromSeconds(len.Value).Ticks;
                            }
                            catch { }
                            finally { sem.Release(); }
                        })).ConfigureAwait(false);

                        foreach (var item in batch)
                        {
                            item.Name = $"{(startIndex + items.Count + 1):D3} | {item.Name}";
                            items.Add(item);
                        }
                        if (tempItems.Count < 10) break;
                        currentPage++;
                    }

                    if (items.Count == 0 && startIndex == 0)
                        return Msg(items, "No results found.");
                    return new ChannelItemResult { Items = items, TotalRecordCount = items.Count == limit ? startIndex + items.Count + 20 : startIndex + items.Count };
                }
                return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
            }
            catch (Exception ex) { return Msg(items, "MAIN ERROR: " + ex.Message); }
        }

        // ═══════════════════════════════════════════════════════════
        // VIDEO LIST PARSING
        // ═══════════════════════════════════════════════════════════
        private List<ChannelItemInfo> ExtractVideos(JsonDocument doc)
        {
            var list = new List<ChannelItemInfo>();
            JsonElement arr = default;
            bool found = false;
            if (doc.RootElement.ValueKind == JsonValueKind.Array) { arr = doc.RootElement; found = true; }
            else if (doc.RootElement.TryGetProperty("videos", out var v) && v.ValueKind == JsonValueKind.Array) { arr = v; found = true; }
            if (!found) return list;

            foreach (var el in arr.EnumerateArray())
            {
                var videoId = InvidiousApi.GetString(el, "videoId");
                if (string.IsNullOrWhiteSpace(videoId)) continue;
                var title = InvidiousApi.GetString(el, "title") ?? "Untitled";
                var author = InvidiousApi.GetString(el, "author") ?? "Unknown";
                var pubUnix = InvidiousApi.GetLong(el, "published");
                DateTime? premiere = pubUnix.HasValue ? DateTimeOffset.FromUnixTimeSeconds(pubUnix.Value).UtcDateTime : null;
                var len = InvidiousApi.GetInt(el, "lengthSeconds");

                list.Add(new ChannelItemInfo
                {
                    Name = title,
                    SeriesName = author,
                    Studios = new List<string> { author },
                    ProductionYear = premiere?.Year,
                    DateCreated = premiere,
                    PremiereDate = premiere,
                    RunTimeTicks = len > 0 ? TimeSpan.FromSeconds(len.Value).Ticks : null,
                    ContentType = ChannelMediaContentType.Episode,
                    Id = videoId,
                    Type = ChannelItemType.Media,
                    MediaType = ChannelMediaType.Video,
                    ImageUrl = $"https://img.youtube.com/vi/{videoId}/hqdefault.jpg"
                });
            }
            return list;
        }

        // ═══════════════════════════════════════════════════════════
        // PLAYBACK: MEDIA SOURCE SELECTION
        // ═══════════════════════════════════════════════════════════
        public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Options;
            var baseUrl = (config.InvidiousUrl ?? "").TrimEnd('/');
            var headers = InvidiousApi.BuildPlaybackHeaders(baseUrl);
            var sources = new List<MediaSourceInfo>();

            try
            {
                var api = new InvidiousApi();
                using var videoDoc = await api.GetVideoAsync(baseUrl, id, cancellationToken).ConfigureAwait(false);
                var root = videoDoc.RootElement;

                // ── Find best h264 adaptive video stream ──
                string? bestVideoItag = null;
                string? bestVideoUrl = null;  // Direct CDN URL (no auth needed!)
                int bestVideoHeight = 0;
                string? bestVideoLabel = null;

                // ── Find best audio stream (mp4/m4a only) ──
                string? bestAudioItag = null;
                string? bestAudioUrl = null;  // Direct CDN URL
                int bestAudioBitrate = 0;

                if (root.TryGetProperty("adaptiveFormats", out var adaptive) && adaptive.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in adaptive.EnumerateArray())
                    {
                        var type = InvidiousApi.GetString(el, "type") ?? "";
                        var itag = InvidiousApi.GetString(el, "itag");
                        var url = InvidiousApi.GetString(el, "url");  // Direct googlevideo.com URL
                        if (string.IsNullOrEmpty(itag)) continue;

                        // Video: only h264 (avc1) in mp4 container
                        if (type.StartsWith("video/mp4") && type.Contains("avc1"))
                        {
                            int h = ParseHeightFromElement(el);
                            if (h > bestVideoHeight)
                            {
                                bestVideoHeight = h;
                                bestVideoItag = itag;
                                bestVideoUrl = url;
                                bestVideoLabel = InvidiousApi.GetString(el, "qualityLabel") ?? $"{h}p";
                            }
                        }

                        // Audio: mp4a/m4a only (not opus/webm)
                        if (type.StartsWith("audio/mp4") || type.StartsWith("audio/m4a"))
                        {
                            int br = InvidiousApi.GetInt(el, "bitrate") ?? 0;
                            if (br > bestAudioBitrate)
                            {
                                bestAudioBitrate = br;
                                bestAudioItag = itag;
                                bestAudioUrl = url;
                            }
                        }
                    }
                }

                // ── Find best muxed stream (formatStreams) ──
                string fallbackItag = "18";
                int fallbackHeight = 0;
                if (root.TryGetProperty("formatStreams", out var fmtArr) && fmtArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in fmtArr.EnumerateArray())
                    {
                        if (!(InvidiousApi.GetString(el, "container") ?? "").Contains("mp4", StringComparison.OrdinalIgnoreCase)) continue;
                        int h = ParseHeightFromElement(el);
                        if (h == 0)
                        {
                            var it = InvidiousApi.GetString(el, "itag") ?? "";
                            if (it == "22") h = 720; else if (it == "18") h = 360;
                        }
                        if (h > fallbackHeight) { fallbackHeight = h; fallbackItag = InvidiousApi.GetString(el, "itag") ?? fallbackItag; }
                    }
                }

        public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
        {
            var videoId = id;
            var config = Plugin.Instance!.Options;
            var baseUrl = (config.InvidiousUrl ?? "https://yewtu.be").TrimEnd('/');

            var api = new InvidiousApi();
            var headers = InvidiousApi.BuildPlaybackHeaders(baseUrl);
            var playUrl = $"{baseUrl}/latest_version?id={videoId}&itag=22";

            try
            {
                using var videoDoc = await api.GetVideoAsync(baseUrl, videoId, cancellationToken).ConfigureAwait(false);
                var streams = api.ExtractAllStreams(baseUrl, videoDoc);

                if (!string.IsNullOrWhiteSpace(streams.mp4))
                {
                    playUrl = streams.mp4;
                }
            }
            catch { }

            return new List<MediaSourceInfo>
            {
                sources.Add(new MediaSourceInfo
                {
                    Id = videoId,
                    Name = "Invidious Direct (MP4)",
                    Path = playUrl,
                    Protocol = MediaProtocol.Http,
                    Container = "mp4",
                    RequiredHttpHeaders = headers
                }
            };
        }

            // Try "resolution": "1080p"
            var res = InvidiousApi.GetString(el, "resolution");
            if (!string.IsNullOrEmpty(res))
            {
                var n = ExtractLeadingNumber(res);
                if (n > 0) return n;
            }

            // Try "qualityLabel": "1080p60"
            var ql = InvidiousApi.GetString(el, "qualityLabel");
            if (!string.IsNullOrEmpty(ql))
            {
                var n = ExtractLeadingNumber(ql);
                if (n > 0) return n;
            }

            return 0;
        }

        private static int ExtractLeadingNumber(string s)
        {
            int i = 0;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            return i > 0 && int.TryParse(s.Substring(0, i), out var val) ? val : 0;
        }

        public IEnumerable<ImageType> GetSupportedChannelImages() => new List<ImageType> { ImageType.Thumb, ImageType.Primary };

        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        {
            var t = GetType();
            var stream = t.Assembly.GetManifestResourceStream(t.Namespace + ".thumb.png");
            return Task.FromResult(stream != null
                ? new DynamicImageResponse { Format = ImageFormat.Png, Stream = stream }
                : null!);
        }

        private static ChannelItemResult Msg(List<ChannelItemInfo> items, string msg)
        {
            items.Add(new ChannelItemInfo { Name = msg, Id = "msg", Type = ChannelItemType.Folder });
            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }
    }
}
