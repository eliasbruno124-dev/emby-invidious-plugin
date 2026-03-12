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
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.InvidiousPlugin
{
    public class InvidiousChannel : IChannel, IRequiresMediaInfoCallback
    {
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(4)
        };

        public string Name => "Invidious";
        public string Description => "Privacy-friendly YouTube";
        public string Id => "invidious_channel_40";
        public string DataVersion => "9.0.0";

        public ChannelType Type => ChannelType.TV;
        public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
        public bool IsEnabledByDefault => true;

        public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            var items = new List<ChannelItemInfo>();
            var plugin = Plugin.Instance;

            if (plugin == null)
                return Msg(items, "ERROR: Plugin not initialized.");

            var config = plugin.Options;
            var baseUrl = (config.InvidiousUrl ?? "").TrimEnd('/');

            if (string.IsNullOrWhiteSpace(baseUrl))
                return Msg(items, "ERROR: Please set the Invidious URL in the plugin settings.");

            try
            {
                var api = new InvidiousApi();

                if (string.IsNullOrEmpty(query.FolderId))
                {
                    var savedItems = (config.SavedItems ?? "")
                        .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                    var menuTasks = savedItems.Select(async s =>
                    {
                        var term = s.Trim();
                        if (string.IsNullOrEmpty(term))
                            return null;

                        if (term.StartsWith("@"))
                        {
                            var details = await api.GetChannelDetailsAsync(baseUrl, term, true, cancellationToken).ConfigureAwait(false);
                            var name = string.IsNullOrEmpty(details.name) ? term : details.name;
                            var cId = string.IsNullOrEmpty(details.id) ? term : details.id;

                            return new ChannelItemInfo
                            {
                                Name = $"📺 {name}",
                                Id = $"channel_x_{cId}",
                                Type = ChannelItemType.Folder,
                                ImageUrl = details.thumb
                            };
                        }
                        else if (term.StartsWith("UC") && term.Length > 20)
                        {
                            var details = await api.GetChannelDetailsAsync(baseUrl, term, false, cancellationToken).ConfigureAwait(false);
                            var name = string.IsNullOrEmpty(details.name) ? "Channel" : details.name;

                            return new ChannelItemInfo
                            {
                                Name = $"📺 {name}",
                                Id = $"channel_x_{term}",
                                Type = ChannelItemType.Folder,
                                ImageUrl = details.thumb
                            };
                        }
                        else if (term.StartsWith("PL"))
                        {
                            var details = await api.GetPlaylistDetailsAsync(baseUrl, term, cancellationToken).ConfigureAwait(false);
                            var name = string.IsNullOrEmpty(details.name) ? "Playlist" : details.name;

                            return new ChannelItemInfo
                            {
                                Name = $"🎵 {name}",
                                Id = $"playlist_x_{term}",
                                Type = ChannelItemType.Folder,
                                ImageUrl = details.thumb
                            };
                        }
                        else
                        {
                            return new ChannelItemInfo
                            {
                                Name = $"🔍 {term}",
                                Id = $"search_x_{term}",
                                Type = ChannelItemType.Folder
                            };
                        }
                    });

                    var results = await Task.WhenAll(menuTasks).ConfigureAwait(false);
                    foreach (var res in results)
                    {
                        if (res != null)
                            items.Add(res);
                    }

                    return new ChannelItemResult
                    {
                        Items = items,
                        TotalRecordCount = items.Count
                    };
                }

                if (query.FolderId.Contains("_x_"))
                {
                    var parts = query.FolderId.Split(new[] { '_' }, 3);
                    if (parts.Length < 3)
                        return new ChannelItemResult { Items = items, TotalRecordCount = 0 };

                    string type = parts[0];
                    string term = parts[2];

                    int startIndex = query.StartIndex ?? 0;
                    int limit = type == "search" ? config.MaxSearchVideos : config.MaxChannelVideos;

                    if (limit <= 0) limit = 50;
                    if (limit > 150) limit = 150;

                    int startPage = (startIndex / 20) + 1;
                    int skipItems = startIndex % 20;
                    int currentPage = startPage;

                    var seenIds = new HashSet<string>();

                    while (items.Count < limit)
                    {
                        JsonDocument? doc = null;

                        if (type == "search")
                            doc = await api.SearchVideosAsync(baseUrl, term, currentPage, cancellationToken).ConfigureAwait(false);
                        else if (type == "channel")
                            doc = await api.GetChannelVideosAsync(baseUrl, term, currentPage, cancellationToken).ConfigureAwait(false);
                        else if (type == "playlist")
                            doc = await api.GetPlaylistVideosAsync(baseUrl, term, currentPage, cancellationToken).ConfigureAwait(false);

                        if (doc == null)
                            break;

                        var tempItems = ExtractVideos(doc);
                        doc.Dispose();

                        if (tempItems.Count == 0)
                            break;

                        var itemsToProcess = new List<ChannelItemInfo>();

                        foreach (var item in tempItems)
                        {
                            if (skipItems > 0)
                            {
                                skipItems--;
                                continue;
                            }

                            if (seenIds.Add(item.Id))
                                itemsToProcess.Add(item);

                            if (items.Count + itemsToProcess.Count >= limit)
                                break;
                        }

                        if (config.UseYtProxy)
                            _ = Task.Run(() => PrefetchBatchAsync(config.YtProxyUrl, itemsToProcess.Select(x => x.Id).ToList(), CancellationToken.None));

                        var semaphore = new SemaphoreSlim(20);

                        var detailTasks = itemsToProcess.Select(async item =>
                        {
                            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                            try
                            {
                                var vId = item.Id;
                                using var vDoc = await api.GetVideoAsync(baseUrl, vId, cancellationToken).ConfigureAwait(false);
                                var root = vDoc.RootElement;

                                var fullDesc = InvidiousApi.GetString(root, "description");
                                if (!string.IsNullOrWhiteSpace(fullDesc))
                                {
                                    var viewCount = InvidiousApi.GetLong(root, "viewCount");
                                    string overviewText = "";

                                    if (viewCount.HasValue && viewCount.Value > 0)
                                        overviewText += $"👁 {viewCount.Value:N0} Aufrufe\n\n";

                                    overviewText += fullDesc;
                                    item.Overview = overviewText;
                                }

                                var truePublishedUnix = InvidiousApi.GetLong(root, "published");
                                if (truePublishedUnix.HasValue)
                                {
                                    var trueDate = DateTimeOffset.FromUnixTimeSeconds(truePublishedUnix.Value).UtcDateTime;
                                    item.PremiereDate = trueDate;
                                    item.DateCreated = trueDate;
                                    item.ProductionYear = trueDate.Year;
                                }

                                var lengthSeconds = InvidiousApi.GetInt(root, "lengthSeconds");
                                if (lengthSeconds.HasValue && lengthSeconds.Value > 0)
                                    item.RunTimeTicks = TimeSpan.FromSeconds(lengthSeconds.Value).Ticks;
                            }
                            catch
                            {
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        });

                        await Task.WhenAll(detailTasks).ConfigureAwait(false);

                        foreach (var item in itemsToProcess)
                        {
                            int currentIndex = startIndex + items.Count + 1;
                            item.Name = $"{currentIndex:D3} | {item.Name}";
                            items.Add(item);
                        }

                        if (tempItems.Count < 10)
                            break;

                        currentPage++;
                    }

                    if (items.Count == 0 && startIndex == 0)
                        return Msg(items, "No results found.");

                    int totalRecords = items.Count == limit
                        ? startIndex + items.Count + 20
                        : startIndex + items.Count;

                    return new ChannelItemResult
                    {
                        Items = items,
                        TotalRecordCount = totalRecords
                    };
                }

                return new ChannelItemResult
                {
                    Items = items,
                    TotalRecordCount = items.Count
                };
            }
            catch (Exception ex)
            {
                return Msg(items, "MAIN ERROR: " + ex.Message);
            }
        }

        private async Task PrefetchBatchAsync(string proxyBaseUrl, List<string> ids, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(proxyBaseUrl) || ids == null || ids.Count == 0)
                return;

            try
            {
                var url = proxyBaseUrl.TrimEnd('/') + "/prefetch_batch";
                var json = JsonSerializer.Serialize(new { ids = ids.Distinct().Take(200).ToList() });
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                using var resp = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task<bool> IsMkvReadyAsync(string proxyBaseUrl, string videoId, CancellationToken cancellationToken)
        {
            try
            {
                var url = proxyBaseUrl.TrimEnd('/') + "/ready/" + Uri.EscapeDataString(videoId);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return false;

                var raw = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(raw);

                if (doc.RootElement.TryGetProperty("mkv_complete", out var prop) && prop.ValueKind == JsonValueKind.True)
                    return true;
            }
            catch
            {
            }
            return false;
        }

        private List<ChannelItemInfo> ExtractVideos(JsonDocument doc)
        {
            var list = new List<ChannelItemInfo>();
            JsonElement videoArray = default;
            bool foundArray = false;

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                videoArray = doc.RootElement;
                foundArray = true;
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("videos", out var v) && v.ValueKind == JsonValueKind.Array)
                {
                    videoArray = v;
                    foundArray = true;
                }
            }

            if (!foundArray)
                return list;

            foreach (var el in videoArray.EnumerateArray())
            {
                var title = InvidiousApi.GetString(el, "title") ?? "Untitled";
                var videoId = InvidiousApi.GetString(el, "videoId");
                var author = InvidiousApi.GetString(el, "author") ?? "Unknown Channel";

                if (string.IsNullOrWhiteSpace(videoId))
                    continue;

                string thumbUrl = $"https://img.youtube.com/vi/{videoId}/hqdefault.jpg";

                var rawDescription = InvidiousApi.GetString(el, "description") ?? "";
                var viewCount = InvidiousApi.GetLong(el, "viewCount");
                string overviewText = "";

                if (viewCount.HasValue && viewCount.Value > 0)
                    overviewText += $"👁 {viewCount.Value:N0} Aufrufe\n\n";

                overviewText += rawDescription;

                var publishedUnix = InvidiousApi.GetLong(el, "published");
                DateTime? premiereDate = null;
                int? productionYear = null;

                if (publishedUnix.HasValue)
                {
                    premiereDate = DateTimeOffset.FromUnixTimeSeconds(publishedUnix.Value).UtcDateTime;
                    productionYear = premiereDate.Value.Year;
                }

                var lengthSeconds = InvidiousApi.GetInt(el, "lengthSeconds");
                long? runTimeTicks = null;

                if (lengthSeconds.HasValue && lengthSeconds.Value > 0)
                    runTimeTicks = TimeSpan.FromSeconds(lengthSeconds.Value).Ticks;

                list.Add(new ChannelItemInfo
                {
                    Name = title,
                    SeriesName = author,
                    Studios = new List<string> { author },
                    ProductionYear = productionYear,
                    Overview = overviewText,
                    DateCreated = premiereDate,
                    PremiereDate = premiereDate,
                    RunTimeTicks = runTimeTicks,
                    ContentType = ChannelMediaContentType.Episode,
                    Id = videoId,
                    Type = ChannelItemType.Media,
                    MediaType = ChannelMediaType.Video,
                    ImageUrl = thumbUrl
                });
            }

            return list;
        }

        public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
        {
            var videoId = id;
            var config = Plugin.Instance!.Options;
            var baseUrl = (config.InvidiousUrl ?? "https://yewtu.be").TrimEnd('/');
            var proxyUrl = (config.YtProxyUrl ?? "http://localhost:8080").TrimEnd('/');

            var api = new InvidiousApi();
            var headers = new Dictionary<string, string>();

            string playUrl;
            string name;
            string container;
            long? runTimeTicks = null;

            try
            {
                using var videoDoc = await api.GetVideoAsync(baseUrl, videoId, cancellationToken).ConfigureAwait(false);
                var root = videoDoc.RootElement;

                var lengthSeconds = InvidiousApi.GetInt(root, "lengthSeconds");
                if (lengthSeconds.HasValue && lengthSeconds.Value > 0)
                    runTimeTicks = TimeSpan.FromSeconds(lengthSeconds.Value).Ticks;

                if (config.UseYtProxy)
                {
                    _ = Task.Run(() => PrefetchBatchAsync(config.YtProxyUrl, new List<string> { videoId }, CancellationToken.None));

                    var ready = await IsMkvReadyAsync(proxyUrl, videoId, cancellationToken).ConfigureAwait(false);
                    if (ready)
                    {
                        playUrl = $"{proxyUrl}/mkv/{Uri.EscapeDataString(videoId)}.mkv";
                        name = "yt-dlp Cached MKV";
                        container = "mkv";
                    }
                    else
                    {
                        var sessionId = Guid.NewGuid().ToString("N");
                        playUrl = $"{proxyUrl}/hls/{Uri.EscapeDataString(videoId)}/{sessionId}/index.m3u8";
                        name = "yt-dlp Fresh HLS";
                        container = "hls";
                    }
                }
                else
                {
                    name = "Invidious Direct";
                    container = "mp4";
                    headers = InvidiousApi.BuildPlaybackHeaders(baseUrl);
                    playUrl = $"{baseUrl}/latest_version?id={Uri.EscapeDataString(videoId)}";

                    var streams = api.ExtractAllStreams(baseUrl, videoDoc);

                    if (!string.IsNullOrWhiteSpace(streams.mp4))
                    {
                        playUrl = streams.mp4!;
                        name = "Invidious Direct (MP4)";
                        container = "mp4";
                    }
                    else if (!string.IsNullOrWhiteSpace(streams.hls))
                    {
                        playUrl = streams.hls!;
                        name = "Invidious Direct (HLS)";
                        container = "hls";
                    }
                    else if (!string.IsNullOrWhiteSpace(streams.dash))
                    {
                        playUrl = streams.dash!;
                        name = "Invidious Direct (DASH)";
                        container = "mpd";
                    }
                }
            }
            catch
            {
                if (config.UseYtProxy)
                {
                    var sessionId = Guid.NewGuid().ToString("N");
                    playUrl = $"{proxyUrl}/hls/{Uri.EscapeDataString(videoId)}/{sessionId}/index.m3u8";
                    name = "yt-dlp Fresh HLS fallback";
                    container = "hls";
                }
                else
                {
                    playUrl = $"{baseUrl}/latest_version?id={Uri.EscapeDataString(videoId)}";
                    name = "Invidious Direct";
                    container = "mp4";
                    headers = InvidiousApi.BuildPlaybackHeaders(baseUrl);
                }
            }

            return new List<MediaSourceInfo>
            {
                new MediaSourceInfo
                {
                    Id = videoId,
                    Name = name,
                    Path = playUrl,
                    Protocol = MediaProtocol.Http,
                    IsRemote = true,
                    IsInfiniteStream = false,
                    Container = container,
                    RequiredHttpHeaders = headers,
                    RunTimeTicks = runTimeTicks
                }
            };
        }

        public IEnumerable<ImageType> GetSupportedChannelImages() =>
            new List<ImageType> { ImageType.Thumb, ImageType.Primary };

        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        {
            var typeName = GetType();
            var stream = typeName.Assembly.GetManifestResourceStream(typeName.Namespace + ".thumb.png");

            if (stream == null)
                return Task.FromResult<DynamicImageResponse>(null!);

            return Task.FromResult(new DynamicImageResponse
            {
                Format = ImageFormat.Png,
                Stream = stream
            });
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