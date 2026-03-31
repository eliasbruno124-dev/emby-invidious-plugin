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
        public string Id => "invidious_channel_20";
        public string DataVersion => "6.0.0";
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

                // ── Root: show saved items as folders ──
                if (string.IsNullOrEmpty(query.FolderId))
                {
                    var savedItems = (config.SavedItems ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    var menuTasks = savedItems.Select(async s =>
                    {
                        var term = s.Trim();
                        if (string.IsNullOrEmpty(term)) return null;
                        if (term.StartsWith("@"))
                        {
                            var d = await api.GetChannelDetailsAsync(baseUrl, term, true, cancellationToken).ConfigureAwait(false);
                            return new ChannelItemInfo { Name = $"📺 {d.name ?? term}", Id = $"channel_x_{d.id ?? term}", Type = ChannelItemType.Folder, ImageUrl = d.thumb };
                        }
                        if (term.StartsWith("UC") && term.Length > 20)
                        {
                            var d = await api.GetChannelDetailsAsync(baseUrl, term, false, cancellationToken).ConfigureAwait(false);
                            return new ChannelItemInfo { Name = $"📺 {d.name ?? "Channel"}", Id = $"channel_x_{term}", Type = ChannelItemType.Folder, ImageUrl = d.thumb };
                        }
                        if (term.StartsWith("PL"))
                        {
                            var d = await api.GetPlaylistDetailsAsync(baseUrl, term, cancellationToken).ConfigureAwait(false);
                            return new ChannelItemInfo { Name = $"🎵 {d.name ?? "Playlist"}", Id = $"playlist_x_{term}", Type = ChannelItemType.Folder, ImageUrl = d.thumb };
                        }
                        return new ChannelItemInfo { Name = $"🔍 {term}", Id = $"search_x_{term}", Type = ChannelItemType.Folder };
                    });
                    foreach (var res in await Task.WhenAll(menuTasks).ConfigureAwait(false))
                        if (res != null) items.Add(res);
                    return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
                }

                // ── Sub-folder: load videos ──
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
                        var tempItems = ExtractVideos(doc, baseUrl);
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
        private List<ChannelItemInfo> ExtractVideos(JsonDocument doc, string baseUrl)
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
                    ImageUrl = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg"
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
                bool bestAudioIsOriginal = false;

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
                        // Prefer original audio track over AI-dubbed versions
                        if (type.StartsWith("audio/mp4") || type.StartsWith("audio/m4a"))
                        {
                            int br = InvidiousApi.GetInt(el, "bitrate") ?? 0;
                            bool isOriginal = false;

                            // Check if this is the original/default audio track
                            if (el.TryGetProperty("audioTrack", out var audioTrack) && audioTrack.ValueKind == JsonValueKind.Object)
                            {
                                if (audioTrack.TryGetProperty("audioIsDefault", out var defProp))
                                {
                                    if (defProp.ValueKind == JsonValueKind.True)
                                        isOriginal = true;
                                    else if (defProp.ValueKind == JsonValueKind.String)
                                        isOriginal = defProp.GetString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
                                }
                            }
                            // No audioTrack info at all → treat as original (old API / single-track video)
                            else
                            {
                                isOriginal = true;
                            }

                            // Prefer original track; among same priority pick highest bitrate
                            bool currentIsOriginal = bestAudioIsOriginal;
                            bool shouldReplace = false;
                            if (isOriginal && !currentIsOriginal)
                                shouldReplace = true;  // Always prefer original over dubbed
                            else if (isOriginal == currentIsOriginal && br > bestAudioBitrate)
                                shouldReplace = true;  // Same priority → pick higher bitrate

                            if (shouldReplace)
                            {
                                bestAudioBitrate = br;
                                bestAudioItag = itag;
                                bestAudioUrl = url;
                                bestAudioIsOriginal = isOriginal;
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

                // ── 1080p+ via FFmpeg HLS mux ──
                if (bestVideoItag != null && bestAudioItag != null && bestVideoHeight > fallbackHeight)
                {
                    // Strategy:
                    //   1. Try direct CDN URLs (fast, works when IPs match e.g. Windows)
                    //   2. Fallback: Invidious proxy URLs (always works, no auth needed on internal network)
                    string ffmpegVideoUrl, ffmpegAudioUrl;
                    if (!string.IsNullOrEmpty(bestVideoUrl) && !string.IsNullOrEmpty(bestAudioUrl))
                    {
                        ffmpegVideoUrl = bestVideoUrl;
                        ffmpegAudioUrl = bestAudioUrl;
                    }
                    else
                    {
                        var cleanBase = InvidiousApi.GetCleanBaseUrl(baseUrl);
                        ffmpegVideoUrl = $"{cleanBase}/latest_version?id={id}&itag={bestVideoItag}&local=true";
                        ffmpegAudioUrl = $"{cleanBase}/latest_version?id={id}&itag={bestAudioItag}&local=true";
                    }

                    var m3u8Path = await MuxHelper.MuxToHlsAsync(
                        ffmpegVideoUrl, ffmpegAudioUrl, id, bestVideoHeight
                    ).ConfigureAwait(false);

                    // If CDN URLs failed, retry with Invidious proxy
                    if (string.IsNullOrEmpty(m3u8Path) && !string.IsNullOrEmpty(bestVideoUrl))
                    {
                        var cleanBase = InvidiousApi.GetCleanBaseUrl(baseUrl);
                        ffmpegVideoUrl = $"{cleanBase}/latest_version?id={id}&itag={bestVideoItag}&local=true";
                        ffmpegAudioUrl = $"{cleanBase}/latest_version?id={id}&itag={bestAudioItag}&local=true";
                        m3u8Path = await MuxHelper.MuxToHlsAsync(
                            ffmpegVideoUrl, ffmpegAudioUrl, id, bestVideoHeight
                        ).ConfigureAwait(false);
                    }

                    if (!string.IsNullOrEmpty(m3u8Path) && File.Exists(m3u8Path))
                    {
                        sources.Add(new MediaSourceInfo
                        {
                            Id = $"{id}_hls_{bestVideoHeight}p",
                            Name = $"🎬 {bestVideoLabel ?? $"{bestVideoHeight}p"} (HD)",
                            Path = m3u8Path,
                            Protocol = MediaProtocol.File,
                            Container = "hls",
                            IsRemote = false,
                            SupportsDirectPlay = false,
                            SupportsDirectStream = true,
                            SupportsTranscoding = true,
                            DefaultAudioStreamIndex = 1,
                            MediaStreams = new List<MediaStream>
                            {
                                new MediaStream
                                {
                                    Type = MediaStreamType.Video,
                                    Index = 0,
                                    Codec = "h264",
                                    Width = bestVideoHeight == 1080 ? 1920 : bestVideoHeight == 720 ? 1280 : bestVideoHeight == 1440 ? 2560 : 1920,
                                    Height = bestVideoHeight,
                                    IsDefault = true
                                },
                                new MediaStream
                                {
                                    Type = MediaStreamType.Audio,
                                    Index = 1,
                                    Codec = "aac",
                                    Channels = 2,
                                    SampleRate = 44100,
                                    IsDefault = true
                                }
                            }
                        });
                    }
                }

                // ── Fallback: direct muxed stream from Invidious ──
                string fbLabel = fallbackHeight > 0 ? $"{fallbackHeight}p" : "SD";
                sources.Add(new MediaSourceInfo
                {
                    Id = $"{id}_direct_{fbLabel}",
                    Name = $"📺 {fbLabel} MP4 (Direct)",
                    Path = $"{baseUrl}/latest_version?id={id}&itag={fallbackItag}&local=true",
                    Protocol = MediaProtocol.Http,
                    Container = "mp4",
                    IsRemote = true,
                    RequiredHttpHeaders = headers,
                    SupportsDirectPlay = false,
                    SupportsDirectStream = true,
                    SupportsTranscoding = true,
                });
            }
            catch
            {
                sources.Add(new MediaSourceInfo
                {
                    Id = id,
                    Name = "Invidious (Fallback)",
                    Path = $"{baseUrl}/latest_version?id={id}&itag=18&local=true",
                    Protocol = MediaProtocol.Http,
                    Container = "mp4",
                    IsRemote = true,
                    RequiredHttpHeaders = headers,
                    SupportsDirectPlay = false,
                    SupportsDirectStream = true,
                    SupportsTranscoding = true,
                });
            }

            return sources;
        }

        // ═══════════════════════════════════════════════════════════
        // HELPERS
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Parses video height from Invidious JSON element.
        /// Tries: "size" ("1920x1080") → "resolution" ("1080p") → "qualityLabel" ("1080p60")
        /// </summary>
        private static int ParseHeightFromElement(JsonElement el)
        {
            // Try "size": "1920x1080"
            var size = InvidiousApi.GetString(el, "size");
            if (!string.IsNullOrEmpty(size))
            {
                var idx = size.IndexOf('x');
                if (idx > 0 && int.TryParse(size.Substring(idx + 1), out var h))
                    return h;
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
