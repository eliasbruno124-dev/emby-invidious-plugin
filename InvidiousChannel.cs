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

                        // Fetch full details in parallel (description, views, date)
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
                    ImageUrl = $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg"
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
                // Kill FFmpeg from previously playing videos
                MuxHelper.KillOtherVideos(id);

                var api = new InvidiousApi();
                using var videoDoc = await api.GetVideoAsync(baseUrl, id, cancellationToken).ConfigureAwait(false);
                var root = videoDoc.RootElement;

                // ── Find best adaptive video stream (h264 + vp9 for 4K) ──
                string? bestVideoItag = null;
                string? bestVideoUrl = null;
                int bestVideoHeight = 0;
                string? bestVideoLabel = null;
                bool bestVideoIsVp9 = false;

                string? vid1080Itag = null;
                string? vid1080Url = null;
                string? vid1080Label = null;
                bool vid1080IsVp9 = false;

                // ── Find best original audio stream (mp4/m4a only) ──
                string? bestAudioItag = null;
                string? bestAudioUrl = null;
                int bestAudioBitrate = 0;
                bool bestAudioIsOriginal = false;

                if (root.TryGetProperty("adaptiveFormats", out var adaptive) && adaptive.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in adaptive.EnumerateArray())
                    {
                        var type = InvidiousApi.GetString(el, "type") ?? "";
                        var itag = InvidiousApi.GetString(el, "itag");
                        var url = InvidiousApi.GetString(el, "url");
                        if (string.IsNullOrEmpty(itag)) continue;

                        // Video: h264 (mp4+avc1) OR vp9 (webm+vp9) for 4K support
                        bool isH264 = type.StartsWith("video/mp4") && type.Contains("avc1");
                        bool isVp9 = type.StartsWith("video/webm") && type.Contains("vp9");
                        if (isH264 || isVp9)
                        {
                            int h = ParseHeightFromElement(el);
                            if (h <= 0) continue;
                            var label = InvidiousApi.GetString(el, "qualityLabel") ?? $"{h}p";

                            // Best overall: higher height wins; at same height prefer h264
                            if (h > bestVideoHeight || (h == bestVideoHeight && isH264 && bestVideoIsVp9))
                            {
                                bestVideoHeight = h;
                                bestVideoItag = itag;
                                bestVideoUrl = url;
                                bestVideoLabel = label;
                                bestVideoIsVp9 = isVp9;
                            }

                            // Track 1080p separately: prefer h264 over vp9
                            if (h == 1080 && (vid1080Itag == null || (isH264 && vid1080IsVp9)))
                            {
                                vid1080Itag = itag;
                                vid1080Url = url;
                                vid1080Label = label;
                                vid1080IsVp9 = isVp9;
                            }
                        }

                        // Audio: prefer original language over AI-dubbed
                        if (type.StartsWith("audio/mp4") || type.StartsWith("audio/m4a"))
                        {
                            int br = InvidiousApi.GetInt(el, "bitrate") ?? 0;
                            bool isOriginal = false;

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
                            else
                            {
                                isOriginal = true; // No audioTrack info → treat as original
                            }

                            bool shouldReplace = false;
                            if (isOriginal && !bestAudioIsOriginal) shouldReplace = true;
                            else if (isOriginal == bestAudioIsOriginal && br > bestAudioBitrate) shouldReplace = true;

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

                // ── Collect ALL muxed streams (formatStreams) ──
                var muxedStreams = new List<(string itag, int height, string label)>();
                if (root.TryGetProperty("formatStreams", out var fmtArr) && fmtArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in fmtArr.EnumerateArray())
                    {
                        if (!(InvidiousApi.GetString(el, "container") ?? "").Contains("mp4", StringComparison.OrdinalIgnoreCase)) continue;
                        var itag = InvidiousApi.GetString(el, "itag") ?? "";
                        int h = ParseHeightFromElement(el);
                        if (h == 0)
                        {
                            if (itag == "22") h = 720;
                            else if (itag == "18") h = 480; // itag 18 is actually 480p, not 360p
                        }
                        if (h > 0)
                        {
                            var label = InvidiousApi.GetString(el, "qualityLabel") ?? $"{h}p";
                            // Fix mislabeled 360p → 480p (itag 18 is 480p)
                            if (itag == "18" && label.Contains("360")) label = "480p";
                            muxedStreams.Add((itag, h, label));
                        }
                    }
                }

                int maxMuxedHeight = muxedStreams.Count > 0 ? muxedStreams.Max(m => m.height) : 0;

                // ── Build list of HLS qualities (highest + 1080p if available) ──
                // Only ONE gets muxed per call. When user switches source, Emby
                // calls this method again → the cached one is skipped, next gets muxed.
                var hlsQualities = new List<(string itag, string? url, int height, string label, bool isVp9)>();
                if (bestVideoItag != null && bestAudioItag != null && bestVideoHeight > maxMuxedHeight)
                {
                    hlsQualities.Add((bestVideoItag, bestVideoUrl, bestVideoHeight, bestVideoLabel ?? $"{bestVideoHeight}p", bestVideoIsVp9));
                    if (bestVideoHeight > 1080 && vid1080Itag != null)
                        hlsQualities.Add((vid1080Itag!, vid1080Url, 1080, vid1080Label ?? "1080p", vid1080IsVp9));
                }

                bool alreadyMuxedOne = false;
                foreach (var q in hlsQualities.OrderByDescending(x => x.height))
                {
                    var expectedPath = MuxHelper.GetExpectedPlaybackPath(id, q.height);
                    var cachedStream = MuxHelper.GetCachedStreamPath(id, q.height);
                    string videoCodec = q.isVp9 ? "vp9" : "h264";

                    if (cachedStream != null)
                    {
                        int w = q.height switch { 2160 => 3840, 1440 => 2560, 1080 => 1920, 720 => 1280, _ => (int)(q.height * 16.0 / 9) };
                        sources.Add(new MediaSourceInfo
                        {
                            Id = $"{id}_hls_{q.height}p",
                            Name = $"🎬 {q.label} (HD)",
                            Path = cachedStream,
                            Protocol = MediaProtocol.File,
                            Container = "hls",
                            IsRemote = false,
                            SupportsDirectPlay = false,
                            SupportsDirectStream = true,
                            SupportsTranscoding = true,
                            DefaultAudioStreamIndex = 1,
                            MediaStreams = new List<MediaStream>
                            {
                                new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = videoCodec, Width = w, Height = q.height, IsDefault = true },
                                new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac", Channels = 2, SampleRate = 44100, IsDefault = true }
                            }
                        });
                    }
                    else if (!alreadyMuxedOne)
                    {
                        string fVid, fAud;
                        if (!string.IsNullOrEmpty(q.url) && !string.IsNullOrEmpty(bestAudioUrl))
                        { fVid = q.url; fAud = bestAudioUrl; }
                        else
                        {
                            var cb = InvidiousApi.GetCleanBaseUrl(baseUrl);
                            fVid = $"{cb}/latest_version?id={id}&itag={q.itag}&local=true";
                            fAud = $"{cb}/latest_version?id={id}&itag={bestAudioItag}&local=true";
                        }

                        var m3u8Path = await MuxHelper.MuxToHlsAsync(fVid, fAud, id, q.height, q.isVp9).ConfigureAwait(false);

                        if (string.IsNullOrEmpty(m3u8Path) && !string.IsNullOrEmpty(q.url))
                        {
                            var cb = InvidiousApi.GetCleanBaseUrl(baseUrl);
                            m3u8Path = await MuxHelper.MuxToHlsAsync(
                                $"{cb}/latest_version?id={id}&itag={q.itag}&local=true",
                                $"{cb}/latest_version?id={id}&itag={bestAudioItag}&local=true",
                                id, q.height, q.isVp9
                            ).ConfigureAwait(false);
                        }

                        if (!string.IsNullOrEmpty(m3u8Path) && File.Exists(m3u8Path))
                        {
                            // Mux succeeded → block further muxes this call
                            alreadyMuxedOne = true;
                            int w = q.height switch { 2160 => 3840, 1440 => 2560, 1080 => 1920, 720 => 1280, _ => (int)(q.height * 16.0 / 9) };
                            sources.Add(new MediaSourceInfo
                            {
                                Id = $"{id}_hls_{q.height}p",
                                Name = $"🎬 {q.label} (HD)",
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
                                    new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = videoCodec, Width = w, Height = q.height, IsDefault = true },
                                    new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac", Channels = 2, SampleRate = 44100, IsDefault = true }
                                }
                            });
                        }
                    }
                    else
                    {
                        int w = q.height switch { 2160 => 3840, 1440 => 2560, 1080 => 1920, 720 => 1280, _ => (int)(q.height * 16.0 / 9) };
                        sources.Add(new MediaSourceInfo
                        {
                            Id = $"{id}_hls_{q.height}p",
                            Name = $"🎬 {q.label} (HD)",
                            Path = expectedPath,
                            Protocol = MediaProtocol.File,
                            Container = "hls",
                            IsRemote = false,
                            SupportsDirectPlay = false,
                            SupportsDirectStream = true,
                            SupportsTranscoding = true,
                            DefaultAudioStreamIndex = 1,
                            MediaStreams = new List<MediaStream>
                            {
                                new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = videoCodec, Width = w, Height = q.height, IsDefault = true },
                                new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac", Channels = 2, SampleRate = 44100, IsDefault = true }
                            }
                        });
                    }
                }

                // ── Direct play for all muxed streams (480p, 720p, etc.) ──
                foreach (var m in muxedStreams.OrderByDescending(x => x.height))
                {
                    sources.Add(new MediaSourceInfo
                    {
                        Id = $"{id}_direct_{m.height}p",
                        Name = $"📺 {m.label} MP4 (Direct)",
                        Path = $"{baseUrl}/latest_version?id={id}&itag={m.itag}&local=true",
                        Protocol = MediaProtocol.Http,
                        Container = "mp4",
                        IsRemote = true,
                        RequiredHttpHeaders = headers,
                        SupportsDirectPlay = false,
                        SupportsDirectStream = true,
                        SupportsTranscoding = true,
                    });
                }

                // ── Fallback if nothing found ──
                if (sources.Count == 0)
                {
                    sources.Add(new MediaSourceInfo
                    {
                        Id = $"{id}_fallback",
                        Name = "📺 SD MP4 (Fallback)",
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