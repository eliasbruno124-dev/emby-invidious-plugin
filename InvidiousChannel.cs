using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Collections.Concurrent;
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
        public string Description => "Privacy-friendly YouTube via your Invidious instance.";
        public string Id => "invidious_channel_20";
        public string DataVersion => "9.0.0";
        public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
        public bool IsEnabledByDefault => true;

        private const string ChannelIdPrefix = "UC";
        private const int MinChannelIdLength = 20;
        private const string PlaylistPrefix = "PL";
        private const string HandlePrefix = "@";
        private const string FolderSeparator = "_x_";
        private const string Itag720p = "22";
        private const string Itag480p = "18";
        private const int MaxMetaCacheEntries = 2000;

        private record VideoMeta(
            string? Overview, DateTime? Premiere, int? Year,
            long? RuntimeTicks, DateTime CachedAt);

        private static readonly ConcurrentDictionary<string, VideoMeta> MetaCache = new();
        private static readonly TimeSpan MetaCacheTtl = TimeSpan.FromDays(365);

        // ── RATE LIMITER: verhindert API-Blocking ──
        // Max 4 gleichzeitige Enrichment-Calls + Mindestabstand 150ms pro Call
        private static readonly SemaphoreSlim EnrichSemaphore = new(4, 4);
        private const int EnrichDelayMs = 150;

        public ChannelFeatures GetChannelFeatures()
        {
            return new ChannelFeatures();
        }

        private static void EvictExpiredMetaCache()
        {
            if (MetaCache.Count <= MaxMetaCacheEntries) return;
            var now = DateTime.UtcNow;
            foreach (var kvp in MetaCache)
            {
                if ((now - kvp.Value.CachedAt) > MetaCacheTtl)
                    MetaCache.TryRemove(kvp.Key, out _);
            }
            if (MetaCache.Count <= MaxMetaCacheEntries) return;
            var oldest = MetaCache
                .OrderBy(kvp => kvp.Value.CachedAt)
                .Take(MetaCache.Count - MaxMetaCacheEntries)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in oldest)
                MetaCache.TryRemove(key, out _);
        }

        public async Task<ChannelItemResult> GetChannelItems(
            InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            var items = new List<ChannelItemInfo>();
            var plugin = Plugin.Instance;
            if (plugin == null) return Msg(items, "ERROR: Plugin not initialized.");

            var config = plugin.Options;
            var baseUrl = (config.InvidiousUrl ?? "").TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
                return Msg(items, "ERROR: Please configure the Invidious URL in the plugin settings.");

            try
            {
                if (string.IsNullOrEmpty(query.FolderId))
                {
                    if (config.ShowTrending)
                        items.Add(new ChannelItemInfo
                        {
                            Name = "Trending",
                            Id = "trending_x_all",
                            Type = ChannelItemType.Folder
                        });

                    var savedItems = (config.SavedItems ?? "")
                        .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                    var menuTasks = savedItems.Select(async s =>
                    {
                        var term = s.Trim();
                        if (string.IsNullOrEmpty(term)) return null;

                        if (term.StartsWith(HandlePrefix))
                        {
                            var d = await InvidiousApi.GetChannelDetailsAsync(
                                baseUrl, term, true, cancellationToken).ConfigureAwait(false);
                            return new ChannelItemInfo
                            {
                                Name = d.name ?? term,
                                Id = $"channel{FolderSeparator}{d.id ?? term}",
                                Type = ChannelItemType.Folder,
                                ImageUrl = d.thumb
                            };
                        }
                        if (term.StartsWith(ChannelIdPrefix) && term.Length > MinChannelIdLength)
                        {
                            var d = await InvidiousApi.GetChannelDetailsAsync(
                                baseUrl, term, false, cancellationToken).ConfigureAwait(false);
                            return new ChannelItemInfo
                            {
                                Name = d.name ?? "Channel",
                                Id = $"channel{FolderSeparator}{term}",
                                Type = ChannelItemType.Folder,
                                ImageUrl = d.thumb
                            };
                        }
                        if (term.StartsWith(PlaylistPrefix))
                        {
                            var d = await InvidiousApi.GetPlaylistDetailsAsync(
                                baseUrl, term, cancellationToken).ConfigureAwait(false);
                            return new ChannelItemInfo
                            {
                                Name = d.name ?? "Playlist",
                                Id = $"playlist{FolderSeparator}{term}",
                                Type = ChannelItemType.Folder,
                                ImageUrl = d.thumb
                            };
                        }
                        return new ChannelItemInfo
                        {
                            Name = $"Search: {term}",
                            Id = $"search{FolderSeparator}{term}",
                            Type = ChannelItemType.Folder
                        };
                    });

                    foreach (var res in await Task.WhenAll(menuTasks).ConfigureAwait(false))
                        if (res != null) items.Add(res);

                    return new ChannelItemResult
                    {
                        Items = items,
                        TotalRecordCount = items.Count
                    };
                }

                if (query.FolderId.Contains(FolderSeparator))
                {
                    var sepIdx = query.FolderId.IndexOf(FolderSeparator, StringComparison.Ordinal);
                    if (sepIdx < 0) return new ChannelItemResult { Items = items };
                    string type = query.FolderId.Substring(0, sepIdx);
                    string term = query.FolderId.Substring(sepIdx + FolderSeparator.Length);

                    if (type == "trending")
                    {
                        string region = (config.TrendingRegion ?? "").Trim();
                        return await LoadTrending(baseUrl, cancellationToken, region)
                            .ConfigureAwait(false);
                    }

                    int startIndex = query.StartIndex ?? 0;
                    int limit = type == "search"
                        ? ClampVideos(config.MaxSearchVideos)
                        : ClampVideos(config.MaxChannelVideos);

                    int firstPageSize = -1;
                    int lastPageSize = -1;
                    int currentPage = 1;
                    int skipItems = startIndex;
                    var seenIds = new HashSet<string>();
                    bool seeking = startIndex > 0;

                    while (items.Count < limit)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        JsonDocument? doc = null;
                        if (type == "search")
                            doc = await InvidiousApi.SearchVideosAsync(
                                baseUrl, term, currentPage, cancellationToken).ConfigureAwait(false);
                        else if (type == "channel")
                            doc = await InvidiousApi.GetChannelVideosAsync(
                                baseUrl, term, currentPage, cancellationToken,
                                config.ChannelSortBy).ConfigureAwait(false);
                        else if (type == "playlist")
                            doc = await InvidiousApi.GetPlaylistVideosAsync(
                                baseUrl, term, currentPage, cancellationToken).ConfigureAwait(false);

                        if (doc == null) break;

                        // ── ExtractVideos holt jetzt ALLE verfügbaren Felder
                        //    direkt aus der Liste (description, published, viewCount) ──
                        var tempItems = ExtractVideos(doc);
                        doc.Dispose();
                        lastPageSize = tempItems.Count;
                        if (firstPageSize < 0) firstPageSize = lastPageSize;
                        if (lastPageSize == 0) break;

                        var batch = new List<ChannelItemInfo>();
                        foreach (var item in tempItems)
                        {
                            if (skipItems > 0) { skipItems--; continue; }
                            seeking = false;
                            // Deduplizierung auf Basis der reinen Video-ID
                            // (ohne LIVE_/REEL_-Prefix)
                            var rawId = item.Id;
                            if (rawId.StartsWith(LivePrefix, StringComparison.Ordinal))
                                rawId = rawId.Substring(LivePrefix.Length);
                            else if (rawId.StartsWith(ReelPrefix, StringComparison.Ordinal))
                                rawId = rawId.Substring(ReelPrefix.Length);
                            if (seenIds.Add(rawId)) batch.Add(item);
                            if (items.Count + batch.Count >= limit) break;
                        }

                        // ── Cache-Lookup + selektives Enrichment ──
                        ApplyCachedMeta(batch);

                        // Nur Videos ohne Beschreibung UND ohne Cache enrichen
                        var uncached = batch
                            .Where(i => string.IsNullOrEmpty(i.Overview)
                                        && !MetaCache.ContainsKey(i.Id))
                            .ToList();

                        if (uncached.Count > 0)
                        {
                            await EnrichVideosThrottled(
                                baseUrl, uncached, cancellationToken).ConfigureAwait(false);
                            EvictExpiredMetaCache();
                        }

                        foreach (var item in batch)
                        {
                            item.Name = $"{(startIndex + items.Count + 1):D3} | {item.Name}";
                            items.Add(item);
                        }

                        if (items.Count >= limit) break;
                        if (batch.Count == 0 && !seeking) break;
                        if (currentPage > 1 && lastPageSize < firstPageSize) break;
                        currentPage++;
                    }

                    if (items.Count == 0 && startIndex == 0)
                        return Msg(items, "No results found.");

                    int total = (items.Count == limit && lastPageSize > 0)
                        ? startIndex + items.Count + 1
                        : startIndex + items.Count;

                    return new ChannelItemResult
                    {
                        Items = items,
                        TotalRecordCount = total
                    };
                }

                return new ChannelItemResult { Items = items };
            }
            catch (Exception ex)
            {
                return Msg(items, $"ERROR: {ex.Message}");
            }
        }

        // ────────────────────────────────────────────────────────────
        //  NEU: Cached-Meta auf Batch anwenden
        // ────────────────────────────────────────────────────────────
        private static void ApplyCachedMeta(List<ChannelItemInfo> batch)
        {
            foreach (var item in batch)
            {
                if (!MetaCache.TryGetValue(item.Id, out var cached)) continue;

                if (!string.IsNullOrEmpty(cached.Overview) && string.IsNullOrEmpty(item.Overview))
                    item.Overview = cached.Overview;

                if (cached.Premiere.HasValue && !item.PremiereDate.HasValue)
                {
                    item.PremiereDate = cached.Premiere;
                    item.DateCreated = cached.Premiere;
                }
                if (cached.Year.HasValue && !item.ProductionYear.HasValue)
                    item.ProductionYear = cached.Year;
                if (cached.RuntimeTicks.HasValue && !item.RunTimeTicks.HasValue)
                    item.RunTimeTicks = cached.RuntimeTicks;
            }
        }

        // ────────────────────────────────────────────────────────────
        //  NEU: Throttled Enrichment — max 2 gleichzeitig + Delay
        //  → Verhindert API-Blocking durch Invidious/YouTube
        // ────────────────────────────────────────────────────────────
        private static async Task EnrichVideosThrottled(
            string baseUrl, List<ChannelItemInfo> uncached, CancellationToken ct)
        {
            try
            {
                using var enrichCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                enrichCts.CancelAfter(TimeSpan.FromSeconds(30));
                var token = enrichCts.Token;

                // Semi-parallel: bis zu 4 gleichzeitig mit kurzem Delay
                var tasks = new List<Task>();
                foreach (var item in uncached)
                {
                    token.ThrowIfCancellationRequested();

                    await EnrichSemaphore.WaitAsync(token).ConfigureAwait(false);
                    var capturedItem = item;
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await EnrichSingleVideo(baseUrl, capturedItem, token)
                                .ConfigureAwait(false);
                            await Task.Delay(EnrichDelayMs, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { }
                        finally
                        {
                            EnrichSemaphore.Release();
                        }
                    }, token));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }

        private static async Task EnrichSingleVideo(
            string baseUrl, ChannelItemInfo item, CancellationToken ct)
        {
            using var vDoc = await InvidiousApi.TryGetVideoAsync(
                baseUrl, item.Id, ct).ConfigureAwait(false);

            if (vDoc == null)
            {
                // Negativ-Cache: nicht nochmal versuchen
                MetaCache[item.Id] = new VideoMeta(null, null, null, null, DateTime.UtcNow);
                return;
            }

            var r = vDoc.RootElement;

            var desc = InvidiousApi.GetString(r, "description");
            var views = InvidiousApi.GetLong(r, "viewCount");
            string? overview = null;
            if (!string.IsNullOrWhiteSpace(desc))
            {
                overview = (views > 0 ? $"{views:N0} views\n\n" : "") + desc;
                item.Overview = overview;
            }

            var pub = InvidiousApi.GetLong(r, "published");
            DateTime? premiere = null;
            int? year = null;
            if (pub.HasValue)
            {
                var dt = DateTimeOffset.FromUnixTimeSeconds(pub.Value).UtcDateTime;
                premiere = dt;
                year = dt.Year;
                item.PremiereDate = dt;
                item.DateCreated = dt;
                item.ProductionYear = dt.Year;
            }

            var len = InvidiousApi.GetInt(r, "lengthSeconds");
            long? ticks = null;
            if (len > 0)
            {
                ticks = TimeSpan.FromSeconds(len.Value).Ticks;
                item.RunTimeTicks = ticks;
            }

            MetaCache[item.Id] = new VideoMeta(overview, premiere, year, ticks, DateTime.UtcNow);
        }

        // ────────────────────────────────────────────────────────────
        //  Trending
        // ────────────────────────────────────────────────────────────
        private static async Task<ChannelItemResult> LoadTrending(
            string baseUrl, CancellationToken ct, string region = "")
        {
            var allVideos = new List<ChannelItemInfo>();
            var seenIds = new HashSet<string>();
            string? reg = string.IsNullOrEmpty(region) ? null : region;

            try
            {
                var tasks = new List<Task<JsonDocument?>>
                {
                    SafeGetJson(() => InvidiousApi.GetPopularAsync(baseUrl, ct)),
                    SafeGetJson(() => InvidiousApi.GetTrendingAsync(baseUrl, null, ct, reg)),
                    SafeGetJson(() => InvidiousApi.GetTrendingAsync(baseUrl, "music", ct, reg)),
                    SafeGetJson(() => InvidiousApi.GetTrendingAsync(baseUrl, "gaming", ct, reg)),
                    SafeGetJson(() => InvidiousApi.GetTrendingAsync(baseUrl, "movies", ct, reg)),
                };
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var doc in results)
                {
                    if (doc == null) continue;
                    foreach (var v in ExtractVideos(doc))
                        if (seenIds.Add(v.Id)) allVideos.Add(v);
                    doc.Dispose();
                }
            }
            catch (Exception ex)
            {
                return Msg(new List<ChannelItemInfo>(), $"ERROR: {ex.Message}");
            }

            if (allVideos.Count == 0)
                return Msg(new List<ChannelItemInfo>(), "No results.");

            // ── Enrichment: Beschreibungen für Trending-Videos laden ──
            ApplyCachedMeta(allVideos);
            var uncached = allVideos
                .Where(i => string.IsNullOrEmpty(i.Overview)
                            && !MetaCache.ContainsKey(i.Id))
                .ToList();
            if (uncached.Count > 0)
            {
                await EnrichVideosThrottled(baseUrl, uncached, ct)
                    .ConfigureAwait(false);
                EvictExpiredMetaCache();
            }

            for (int i = 0; i < allVideos.Count; i++)
                allVideos[i].Name = $"{(i + 1):D3} | {allVideos[i].Name}";

            return new ChannelItemResult
            {
                Items = allVideos,
                TotalRecordCount = allVideos.Count
            };
        }

        private static async Task<JsonDocument?> SafeGetJson(Func<Task<JsonDocument>> factory)
        {
            try { return await factory().ConfigureAwait(false); }
            catch { return null; }
        }

        // ────────────────────────────────────────────────────────────
        //  VERBESSERT: ExtractVideos holt jetzt description + viewCount
        //  direkt aus den Listendaten → kein Extra-API-Call nötig!
        // ────────────────────────────────────────────────────────────
        private const string LivePrefix = "LIVE_";
        private const string ReelPrefix = "REEL_";
        private const int ReelMaxSeconds = 180;

        private static List<ChannelItemInfo> ExtractVideos(JsonDocument doc)
        {
            var list = new List<ChannelItemInfo>();
            JsonElement arr = default;
            bool found = false;

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                arr = doc.RootElement;
                found = true;
            }
            else if (doc.RootElement.TryGetProperty("videos", out var v)
                     && v.ValueKind == JsonValueKind.Array)
            {
                arr = v;
                found = true;
            }
            if (!found) return list;

            foreach (var el in arr.EnumerateArray())
            {
                var videoId = InvidiousApi.GetString(el, "videoId");
                if (string.IsNullOrWhiteSpace(videoId)) continue;

                var title = InvidiousApi.GetString(el, "title") ?? "Untitled";
                var author = InvidiousApi.GetString(el, "author") ?? "Unknown";

                // ── Datum direkt aus der Suche/Channelliste ──
                var pubUnix = InvidiousApi.GetLong(el, "published");
                DateTime? premiere = pubUnix.HasValue
                    ? DateTimeOffset.FromUnixTimeSeconds(pubUnix.Value).UtcDateTime
                    : null;

                var len = InvidiousApi.GetInt(el, "lengthSeconds");

                // ── Beschreibung direkt aus der Liste holen ──
                var description = InvidiousApi.GetString(el, "description")
                               ?? InvidiousApi.GetString(el, "descriptionHtml");
                var viewCount = InvidiousApi.GetLong(el, "viewCount");

                string? overview = null;
                if (!string.IsNullOrWhiteSpace(description))
                {
                    overview = (viewCount > 0 ? $"{viewCount:N0} views\n\n" : "") + description;
                }
                else if (viewCount > 0)
                {
                    overview = $"{viewCount:N0} views";
                }

                bool isLive = false;
                if (el.TryGetProperty("liveNow", out var liveProp))
                    isLive = liveProp.ValueKind == JsonValueKind.True;
                if (!isLive && el.TryGetProperty("isUpcoming", out var upProp))
                    isLive = upProp.ValueKind == JsonValueKind.True;

                bool isReel = false;
                if (!isLive)
                {
                    if (el.TryGetProperty("isShort", out var shortProp)
                        && shortProp.ValueKind == JsonValueKind.True)
                        isReel = true;
                    else if (el.TryGetProperty("genre", out var genreProp)
                             && "short".Equals(genreProp.GetString(),
                                 StringComparison.OrdinalIgnoreCase))
                        isReel = true;
                    else if (len.HasValue && len.Value > 0 && len.Value <= ReelMaxSeconds)
                        isReel = true;
                }

                var thumb = BestVideoThumbnail(el, videoId!);

                string itemId = isLive ? LivePrefix + videoId
                    : isReel ? ReelPrefix + videoId
                    : videoId;
                string displayTitle = isLive ? $"🔴 LIVE: {title}"
                    : isReel ? $"▶ Short: {title}"
                    : title;

                var info = new ChannelItemInfo
                {
                    Name = displayTitle,
                    SeriesName = author,
                    Studios = new List<string> { author },
                    Overview = overview,  // ← NEU: direkt gesetzt
                    ProductionYear = premiere?.Year,
                    DateCreated = premiere,
                    PremiereDate = premiere,
                    RunTimeTicks = isLive
                        ? null
                        : (len > 0 ? TimeSpan.FromSeconds(len.Value).Ticks : null),
                    ContentType = ChannelMediaContentType.Episode,
                    Id = itemId,
                    Type = ChannelItemType.Media,
                    MediaType = ChannelMediaType.Video,
                    ImageUrl = thumb
                };

                // ── Sofort in Cache schreiben wenn wir Daten haben ──
                if (overview != null || premiere.HasValue || len > 0)
                {
                    MetaCache[itemId] = new VideoMeta(
                        overview, premiere, premiere?.Year,
                        len > 0 ? TimeSpan.FromSeconds(len.Value).Ticks : null,
                        DateTime.UtcNow);
                }

                list.Add(info);
            }
            return list;
        }

        private static string BestVideoThumbnail(JsonElement el, string videoId)
        {
            // Immer YouTube-CDN verwenden — Invidious-URLs brauchen Auth
            // und sind bei neuen Videos oft noch nicht verfügbar.
            var ytCdn = $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";

            if (el.TryGetProperty("videoThumbnails", out var thumbArr)
                && thumbArr.ValueKind == JsonValueKind.Array)
            {
                string? bestUrl = null;
                int bestWidth = 0;
                foreach (var t in thumbArr.EnumerateArray())
                {
                    var url = InvidiousApi.GetString(t, "url");
                    if (string.IsNullOrEmpty(url)) continue;
                    url = InvidiousApi.RewriteThumbnailUrl(url);

                    // Nur YouTube-CDN-URLs akzeptieren, keine Invidious-URLs
                    if (!url.Contains("ytimg.com", StringComparison.Ordinal)
                        && !url.Contains("ggpht.com", StringComparison.Ordinal))
                        continue;

                    var w = InvidiousApi.GetInt(t, "width") ?? 0;
                    if (w >= bestWidth) { bestWidth = w; bestUrl = url; }
                }
                if (!string.IsNullOrEmpty(bestUrl)) return bestUrl!;
            }

            return ytCdn;
        }

        // ────────────────────────────────────────────────────────────
        //  Media Playback — HLS Stottern behoben
        // ────────────────────────────────────────────────────────────
        public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(
            string id, CancellationToken cancellationToken)
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return new List<MediaSourceInfo>();

            var config = plugin.Options;
            var baseUrl = (config.InvidiousUrl ?? "").TrimEnd('/');
            var headers = InvidiousApi.BuildPlaybackHeaders(baseUrl);
            var sources = new List<MediaSourceInfo>();

            bool isLive = id.StartsWith(LivePrefix, StringComparison.Ordinal);
            bool isReel = !isLive && id.StartsWith(ReelPrefix, StringComparison.Ordinal);
            string videoId = isLive ? id.Substring(LivePrefix.Length)
                : isReel ? id.Substring(ReelPrefix.Length)
                : id;

            try
            {
                using var videoDoc = await InvidiousApi.GetVideoAsync(
                    baseUrl, videoId, cancellationToken).ConfigureAwait(false);
                var root = videoDoc.RootElement;

                if (isLive || (root.TryGetProperty("liveNow", out var ln)
                               && ln.ValueKind == JsonValueKind.True))
                {
                    var hlsUrl = InvidiousApi.GetString(root, "hlsUrl");
                    if (!string.IsNullOrEmpty(hlsUrl))
                    {
                        sources.Add(new MediaSourceInfo
                        {
                            Id = $"{videoId}_live_hls",
                            Name = "Live Stream",
                            Path = hlsUrl,
                            Protocol = MediaProtocol.Http,
                            Container = "hls",
                            IsRemote = true,
                            RequiredHttpHeaders = headers,
                            SupportsDirectPlay = true,
                            SupportsDirectStream = true,
                            SupportsTranscoding = true,
                            IsInfiniteStream = true,
                        });
                    }
                    var dashUrl = InvidiousApi.GetString(root, "dashUrl");
                    if (!string.IsNullOrEmpty(dashUrl))
                    {
                        sources.Add(new MediaSourceInfo
                        {
                            Id = $"{videoId}_live_dash",
                            Name = "Live Stream (DASH)",
                            Path = dashUrl,
                            Protocol = MediaProtocol.Http,
                            Container = "mpd",
                            IsRemote = true,
                            RequiredHttpHeaders = headers,
                            SupportsDirectPlay = true,
                            SupportsDirectStream = true,
                            SupportsTranscoding = true,
                            IsInfiniteStream = true,
                        });
                    }
                    if (sources.Count > 0) return sources;
                }

                if (!isReel)
                    isReel = IsVerticalVideo(root);

                if (isReel)
                {
                    var reelDims = GetVerticalDimensions(root);
                    int reelW = reelDims.w > 0 ? reelDims.w : 1080;
                    int reelH = reelDims.h > 0 ? reelDims.h : 1920;

                    var reelMuxed = ExtractMuxedStreams(root);
                    foreach (var m in reelMuxed.OrderByDescending(x => x.height))
                        sources.Add(BuildReelSource(videoId, m.itag, m.label,
                            reelW, reelH, baseUrl, headers));

                    if (sources.Count == 0)
                        sources.Add(BuildReelSource(videoId, Itag480p, "Short",
                            reelW, reelH, baseUrl, headers));

                    return sources;
                }

                var bestVideo = FindBestAdaptiveVideo(root);
                var bestAudio = FindBestAudio(root);
                var muxedStreams = ExtractMuxedStreams(root);

                int maxMuxedHeight = muxedStreams.Count > 0
                    ? muxedStreams.Max(m => m.height) : 0;

                string audioMuxUrl = !string.IsNullOrEmpty(bestAudio.url)
                    ? bestAudio.url!
                    : $"{baseUrl}/latest_version?id={videoId}&itag={bestAudio.itag}&local=true";

                var hlsQualities =
                    new List<(string itag, string? url, int height, string label, bool isVp9)>();
                bool canMux = bestVideo.itag != null
                              && (bestAudio.url != null || bestAudio.itag != null);

                if (canMux && (bestVideo.height >= 1080 || bestVideo.height > maxMuxedHeight))
                {
                    hlsQualities.Add((bestVideo.itag!, bestVideo.url, bestVideo.height,
                        bestVideo.label ?? $"{bestVideo.height}p", bestVideo.isVp9));

                    if (bestVideo.height > 1080 && bestVideo.fallback1080Itag != null)
                        hlsQualities.Add((bestVideo.fallback1080Itag!, bestVideo.fallback1080Url,
                            1080, bestVideo.fallback1080Label ?? "1080p",
                            bestVideo.fallback1080IsVp9));
                }

                foreach (var q in hlsQualities.OrderByDescending(x => x.height))
                {
                    var cachedStream = MuxHelper.GetCachedStreamPath(videoId, q.height);
                    string videoCodec = q.isVp9 ? "vp9" : "h264";
                    int w = HeightToWidth(q.height);

                    string videoMuxUrl = !string.IsNullOrEmpty(q.url)
                        ? q.url!
                        : $"{baseUrl}/latest_version?id={videoId}&itag={q.itag}&local=true";

                    if (cachedStream != null)
                    {
                        sources.Add(BuildHlsSource(videoId, q.height, w,
                            q.label, videoCodec, cachedStream));
                    }
                    else
                    {
                        var playbackPath = MuxHelper.PreparePlaybackPath(videoId, q.height);

                        _ = Task.Run(() => MuxHelper.MuxToHlsAsync(
                            videoMuxUrl, audioMuxUrl,
                            videoId, q.height, q.isVp9));

                        // Kurz warten (max 2s) bis mindestens 1 Segment da ist
                        // → sofortiges Abspielen möglich, aber kein langes Blockieren
                        await QuickWaitForFirstSegment(playbackPath, cancellationToken)
                            .ConfigureAwait(false);

                        sources.Add(BuildHlsSource(videoId, q.height, w,
                            q.label, videoCodec, playbackPath));
                    }
                }

                foreach (var m in muxedStreams.OrderByDescending(x => x.height))
                    sources.Add(BuildDirectSource(videoId, m.height, m.label,
                        m.itag, baseUrl, headers));

                if (sources.Count == 0)
                    sources.Add(BuildDirectSource(videoId, 480, "SD Fallback",
                        Itag480p, baseUrl, headers));
            }
            catch (OperationCanceledException) { }
            catch
            {
                sources.Add(BuildDirectSource(videoId, 480, "Invidious (Fallback)",
                    Itag480p, baseUrl, headers));
            }

            return sources;
        }

        private record AdaptiveVideoResult(
            string? itag, string? url, int height, string? label, bool isVp9,
            string? fallback1080Itag, string? fallback1080Url,
            string? fallback1080Label, bool fallback1080IsVp9);

        private record AdaptiveAudioResult(string? itag, string? url, int bitrate);

        private static AdaptiveVideoResult FindBestAdaptiveVideo(JsonElement root)
        {
            string? bestItag = null, bestUrl = null, bestLabel = null;
            int bestHeight = 0;
            bool bestIsVp9 = false;

            string? fb1080Itag = null, fb1080Url = null, fb1080Label = null;
            bool fb1080IsVp9 = false;

            if (!root.TryGetProperty("adaptiveFormats", out var adaptive)
                || adaptive.ValueKind != JsonValueKind.Array)
                return new AdaptiveVideoResult(null, null, 0, null, false,
                    null, null, null, false);

            foreach (var el in adaptive.EnumerateArray())
            {
                var type = InvidiousApi.GetString(el, "type") ?? "";
                var itag = InvidiousApi.GetString(el, "itag");
                var url = InvidiousApi.GetString(el, "url");
                if (string.IsNullOrEmpty(itag)) continue;

                bool isH264 = type.StartsWith("video/mp4") && type.Contains("avc1");
                bool isVp9 = type.StartsWith("video/webm") && type.Contains("vp9");
                if (!isH264 && !isVp9) continue;

                int h = ParseHeightFromElement(el);
                if (h <= 0) continue;
                var label = InvidiousApi.GetString(el, "qualityLabel") ?? $"{h}p";

                if (h > bestHeight || (h == bestHeight && isH264 && bestIsVp9))
                {
                    bestHeight = h; bestItag = itag; bestUrl = url;
                    bestLabel = label; bestIsVp9 = isVp9;
                }
                if (h == 1080 && (fb1080Itag == null || (isH264 && fb1080IsVp9)))
                {
                    fb1080Itag = itag; fb1080Url = url;
                    fb1080Label = label; fb1080IsVp9 = isVp9;
                }
            }

            return new AdaptiveVideoResult(bestItag, bestUrl, bestHeight, bestLabel,
                bestIsVp9, fb1080Itag, fb1080Url, fb1080Label, fb1080IsVp9);
        }

        private static AdaptiveAudioResult FindBestAudio(JsonElement root)
        {
            string? bestItag = null, bestUrl = null;
            int bestBitrate = 0;
            bool bestIsOriginal = false;

            if (!root.TryGetProperty("adaptiveFormats", out var adaptive)
                || adaptive.ValueKind != JsonValueKind.Array)
                return new AdaptiveAudioResult(null, null, 0);

            foreach (var el in adaptive.EnumerateArray())
            {
                var type = InvidiousApi.GetString(el, "type") ?? "";
                if (!type.StartsWith("audio/mp4") && !type.StartsWith("audio/m4a"))
                    continue;

                var itag = InvidiousApi.GetString(el, "itag");
                var url = InvidiousApi.GetString(el, "url");
                int br = InvidiousApi.GetInt(el, "bitrate") ?? 0;

                bool isOriginal = true;
                if (el.TryGetProperty("audioTrack", out var audioTrack)
                    && audioTrack.ValueKind == JsonValueKind.Object)
                {
                    if (audioTrack.TryGetProperty("audioIsDefault", out var defProp))
                    {
                        isOriginal = defProp.ValueKind == JsonValueKind.True
                            || (defProp.ValueKind == JsonValueKind.String
                                && "true".Equals(defProp.GetString(),
                                    StringComparison.OrdinalIgnoreCase));
                    }
                }

                bool shouldReplace = (isOriginal && !bestIsOriginal)
                                     || (isOriginal == bestIsOriginal && br > bestBitrate);
                if (shouldReplace)
                {
                    bestBitrate = br; bestItag = itag;
                    bestUrl = url; bestIsOriginal = isOriginal;
                }
            }

            return new AdaptiveAudioResult(bestItag, bestUrl, bestBitrate);
        }

        private static List<(string itag, int height, string label)> ExtractMuxedStreams(
            JsonElement root)
        {
            var result = new List<(string itag, int height, string label)>();
            if (!root.TryGetProperty("formatStreams", out var fmtArr)
                || fmtArr.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var el in fmtArr.EnumerateArray())
            {
                if (!(InvidiousApi.GetString(el, "container") ?? "")
                    .Contains("mp4", StringComparison.OrdinalIgnoreCase))
                    continue;
                var itag = InvidiousApi.GetString(el, "itag") ?? "";
                int h = ParseHeightFromElement(el);
                if (h == 0)
                {
                    if (itag == Itag720p) h = 720;
                    else if (itag == Itag480p) h = 480;
                }
                if (h > 0)
                {
                    var label = InvidiousApi.GetString(el, "qualityLabel") ?? $"{h}p";
                    if (itag == Itag480p && label.Contains("360")) label = "480p";
                    result.Add((itag, h, label));
                }
            }
            return result;
        }

        private static MediaSourceInfo BuildHlsSource(
            string videoId, int height, int width,
            string label, string codec, string path)
        {
            return new MediaSourceInfo
            {
                Id = $"{videoId}_hls_{height}p",
                Name = $"{label} (HD)",
                Path = path,
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
                        Type = MediaStreamType.Video, Index = 0,
                        Codec = codec, Width = width, Height = height,
                        IsDefault = true
                    },
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio, Index = 1,
                        Codec = "aac", Channels = 2, SampleRate = 44100,
                        IsDefault = true
                    }
                }
            };
        }

        private static MediaSourceInfo BuildDirectSource(
            string videoId, int height, string label, string itag,
            string baseUrl, Dictionary<string, string> headers)
        {
            return new MediaSourceInfo
            {
                Id = $"{videoId}_direct_{height}p",
                Name = $"{label} MP4",
                Path = $"{baseUrl}/latest_version?id={videoId}&itag={itag}&local=true",
                Protocol = MediaProtocol.Http,
                Container = "mp4",
                IsRemote = true,
                RequiredHttpHeaders = headers,
                SupportsDirectPlay = false,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
            };
        }

        // ────────────────────────────────────────────────────────────
        //  Kurzer Wait: max 2s, pollt alle 200ms ob mindestens
        //  1 Segment in playback.m3u8 steht → sofort abspielen.
        //  Gibt immer zurück — blockiert nie länger als 2s.
        // ────────────────────────────────────────────────────────────
        private static async Task QuickWaitForFirstSegment(
            string playbackPath, CancellationToken ct)
        {
            const int maxMs = 4500;
            const int pollMs = 200;
            int waited = 0;

            while (waited < maxMs)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(pollMs, ct).ConfigureAwait(false);
                waited += pollMs;

                try
                {
                    if (File.Exists(playbackPath))
                    {
                        var content = File.ReadAllText(playbackPath);
                        foreach (var line in content.Split('\n'))
                        {
                            var t = line.Trim();
                            if (t.EndsWith(".ts", StringComparison.Ordinal)
                                || t.EndsWith(".m4s", StringComparison.Ordinal))
                                return; // Mindestens 1 Segment → ready
                        }
                    }
                }
                catch { }
            }
            // Timeout → trotzdem zurückkehren, Emby bekommt die Source
        }


        private static MediaSourceInfo BuildReelSource(
            string videoId, string itag, string label,
            int width, int height, string baseUrl,
            Dictionary<string, string> headers)
        {
            return new MediaSourceInfo
            {
                Id = $"{videoId}_reel_{itag}",
                Name = $"Short {label}",
                Path = $"{baseUrl}/latest_version?id={videoId}&itag={itag}&local=true",
                Protocol = MediaProtocol.Http,
                Container = "mp4",
                IsRemote = true,
                RequiredHttpHeaders = headers,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                MediaStreams = new List<MediaStream>
                {
                    new MediaStream
                    {
                        Type = MediaStreamType.Video, Index = 0,
                        Codec = "h264", Width = width, Height = height,
                        AspectRatio = $"{width}:{height}", IsDefault = true
                    },
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio, Index = 1,
                        Codec = "aac", Channels = 2, SampleRate = 44100,
                        IsDefault = true
                    }
                }
            };
        }

        private static (int w, int h) GetVerticalDimensions(JsonElement root)
        {
            if (root.TryGetProperty("adaptiveFormats", out var adaptive)
                && adaptive.ValueKind == JsonValueKind.Array)
            {
                int bestW = 0, bestH = 0;
                foreach (var el in adaptive.EnumerateArray())
                {
                    var type = InvidiousApi.GetString(el, "type") ?? "";
                    if (!type.StartsWith("video/")) continue;
                    var size = InvidiousApi.GetString(el, "size");
                    if (string.IsNullOrEmpty(size)) continue;
                    var xIdx = size.IndexOf('x');
                    if (xIdx > 0
                        && int.TryParse(size.Substring(0, xIdx), out var w)
                        && int.TryParse(size.Substring(xIdx + 1), out var h)
                        && h > w && h > bestH)
                    {
                        bestW = w; bestH = h;
                    }
                }
                if (bestH > 0) return (bestW, bestH);
            }
            return (0, 0);
        }

        private static bool IsVerticalVideo(JsonElement root)
        {
            if (root.TryGetProperty("adaptiveFormats", out var adaptive)
                && adaptive.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in adaptive.EnumerateArray())
                {
                    var type = InvidiousApi.GetString(el, "type") ?? "";
                    if (!type.StartsWith("video/")) continue;
                    var size = InvidiousApi.GetString(el, "size");
                    if (string.IsNullOrEmpty(size)) continue;
                    var xIdx = size.IndexOf('x');
                    if (xIdx > 0
                        && int.TryParse(size.Substring(0, xIdx), out var w)
                        && int.TryParse(size.Substring(xIdx + 1), out var h))
                        return h > w;
                }
            }
            if (root.TryGetProperty("formatStreams", out var fmtArr)
                && fmtArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in fmtArr.EnumerateArray())
                {
                    var size = InvidiousApi.GetString(el, "size");
                    if (string.IsNullOrEmpty(size)) continue;
                    var xIdx = size.IndexOf('x');
                    if (xIdx > 0
                        && int.TryParse(size.Substring(0, xIdx), out var w)
                        && int.TryParse(size.Substring(xIdx + 1), out var h))
                        return h > w;
                }
            }
            return false;
        }

        private static int HeightToWidth(int height) => height switch
        {
            2160 => 3840,
            1440 => 2560,
            1080 => 1920,
            720 => 1280,
            480 => 854,
            _ => (int)(height * 16.0 / 9)
        };

        private static int ParseHeightFromElement(JsonElement el)
        {
            var size = InvidiousApi.GetString(el, "size");
            if (!string.IsNullOrEmpty(size))
            {
                var idx = size.IndexOf('x');
                if (idx > 0 && int.TryParse(size.Substring(idx + 1), out var h))
                    return h;
            }
            var res = InvidiousApi.GetString(el, "resolution");
            if (!string.IsNullOrEmpty(res))
            {
                var n = ExtractLeadingNumber(res);
                if (n > 0) return n;
            }
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
            return i > 0 && int.TryParse(s.Substring(0, i), out var v) ? v : 0;
        }

        private static int ClampVideos(int val) => Math.Clamp(val, 1, 150);

        public IEnumerable<ImageType> GetSupportedChannelImages() =>
            new List<ImageType> { ImageType.Thumb, ImageType.Primary };

        public Task<DynamicImageResponse> GetChannelImage(
            ImageType type, CancellationToken cancellationToken)
        {
            var response = new DynamicImageResponse();
            var t = GetType();
            var stream = t.Assembly.GetManifestResourceStream(t.Namespace + ".thumb.png");
            if (stream != null)
            {
                response.Format = ImageFormat.Png;
                response.Stream = stream;
                return Task.FromResult(response);
            }
            var assemblyDir = Path.GetDirectoryName(t.Assembly.Location) ?? "";
            var filePath = Path.Combine(assemblyDir, "thumb.png");
            if (File.Exists(filePath))
            {
                response.Format = ImageFormat.Png;
                response.Path = filePath;
            }
            return Task.FromResult(response);
        }

        private static ChannelItemResult Msg(List<ChannelItemInfo> items, string msg)
        {
            items.Add(new ChannelItemInfo
            {
                Name = msg,
                Id = "msg",
                Type = ChannelItemType.Folder
            });
            return new ChannelItemResult
            {
                Items = items,
                TotalRecordCount = items.Count
            };
        }
    }
}