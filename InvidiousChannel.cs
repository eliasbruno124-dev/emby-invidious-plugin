using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Collections.Generic;
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
        public string DataVersion => "1.0.18";

        public ChannelType Type => ChannelType.TV;
        public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
        public bool IsEnabledByDefault => true;

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

                // --- 1. MAIN MENU AUFBAUEN ---
                if (string.IsNullOrEmpty(query.FolderId))
                {
                    items.Add(new ChannelItemInfo { Name = "⭐ Popular", Id = "popular", Type = ChannelItemType.Folder });

                    var searches = (config.SavedSearches ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var s in searches)
                    {
                        var term = s.Trim();
                        if (!string.IsNullOrEmpty(term)) items.Add(new ChannelItemInfo { Name = $"🔍 Search: {term}", Id = $"search_x_{term}", Type = ChannelItemType.Folder });
                    }

                    var channels = (config.SavedChannels ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    var channelTasks = channels.Select(async c => {
                        var cId = c.Trim();
                        if (string.IsNullOrEmpty(cId)) return null;
                        string displayName = cId;
                        try
                        {
                            using var doc = await api.GetChannelDetailsAsync(baseUrl, cId, cancellationToken).ConfigureAwait(false);
                            var author = InvidiousApi.GetString(doc.RootElement, "author");
                            if (!string.IsNullOrEmpty(author)) displayName = author;
                        }
                        catch { }
                        return new ChannelItemInfo { Name = $"📺 Channel: {displayName}", Id = $"channel_x_{cId}", Type = ChannelItemType.Folder };
                    });

                    var channelResults = await Task.WhenAll(channelTasks).ConfigureAwait(false);
                    foreach (var res in channelResults) if (res != null) items.Add(res);

                    var playlists = (config.SavedPlaylists ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    var playlistTasks = playlists.Select(async p => {
                        var pId = p.Trim();
                        if (string.IsNullOrEmpty(pId)) return null;
                        string displayName = pId;
                        try
                        {
                            using var doc = await api.GetPlaylistDetailsAsync(baseUrl, pId, cancellationToken).ConfigureAwait(false);
                            var title = InvidiousApi.GetString(doc.RootElement, "title");
                            if (!string.IsNullOrEmpty(title)) displayName = title;
                        }
                        catch { }
                        return new ChannelItemInfo { Name = $"🎵 Playlist: {displayName}", Id = $"playlist_x_{pId}", Type = ChannelItemType.Folder };
                    });

                    var playlistResults = await Task.WhenAll(playlistTasks).ConfigureAwait(false);
                    foreach (var res in playlistResults) if (res != null) items.Add(res);

                    return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
                }

                // --- 2. POPULAR ---
                if (query.FolderId == "popular")
                {
                    using var doc = await api.GetPopularAsync(baseUrl, cancellationToken).ConfigureAwait(false);
                    items.AddRange(ExtractVideos(doc));
                    return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
                }

                // --- 3. PAGINATED CONTENT (100 Videos Limit) ---
                if (query.FolderId.StartsWith("search_") || query.FolderId.StartsWith("channel_") || query.FolderId.StartsWith("playlist_"))
                {
                    var parts = query.FolderId.Split(new[] { '_' }, 3);
                    if (parts.Length < 3) return new ChannelItemResult { Items = items, TotalRecordCount = 0 };

                    string type = parts[0];
                    string term = parts[2];

                    int startIndex = query.StartIndex ?? 0;
                    int limit = 100;

                    int startPage = (startIndex / 20) + 1;
                    int skipItems = startIndex % 20;
                    int currentPage = startPage;

                    var seenIds = new HashSet<string>();

                    while (items.Count < limit)
                    {
                        JsonDocument doc = null;
                        if (type == "search") doc = await api.SearchVideosAsync(baseUrl, term, currentPage, cancellationToken).ConfigureAwait(false);
                        else if (type == "channel") doc = await api.GetChannelVideosAsync(baseUrl, term, currentPage, cancellationToken).ConfigureAwait(false);
                        else if (type == "playlist") doc = await api.GetPlaylistVideosAsync(baseUrl, term, currentPage, cancellationToken).ConfigureAwait(false);

                        if (doc == null) break;

                        var tempItems = ExtractVideos(doc);
                        doc.Dispose();

                        if (tempItems.Count == 0) break;

                        foreach (var item in tempItems)
                        {
                            if (skipItems > 0)
                            {
                                skipItems--;
                                continue;
                            }
                            if (seenIds.Add(item.Id))
                            {
                                items.Add(item);
                            }
                            if (items.Count >= limit) break;
                        }

                        if (tempItems.Count < 10) break;
                        currentPage++;
                    }

                    if (items.Count == 0 && startIndex == 0)
                        return Msg(items, "No results found.");

                    int totalRecords = items.Count == limit ? startIndex + items.Count + 20 : startIndex + items.Count;

                    return new ChannelItemResult { Items = items, TotalRecordCount = totalRecords };
                }

                return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
            }
            catch (Exception ex)
            {
                return Msg(items, "MAIN ERROR: " + ex.Message);
            }
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
                else if (doc.RootElement.TryGetProperty("latestVideos", out var lv) && lv.ValueKind == JsonValueKind.Array)
                {
                    videoArray = lv;
                    foundArray = true;
                }
            }

            if (!foundArray) return list;

            foreach (var el in videoArray.EnumerateArray())
            {
                var title = InvidiousApi.GetString(el, "title") ?? "Untitled";
                var videoId = InvidiousApi.GetString(el, "videoId");
                var author = InvidiousApi.GetString(el, "author");

                var description = InvidiousApi.GetString(el, "description") ?? InvidiousApi.GetString(el, "descriptionHtml") ?? "";

                if (string.IsNullOrWhiteSpace(videoId)) continue;
                string thumbUrl = $"https://img.youtube.com/vi/{videoId}/hqdefault.jpg";

                string overviewText = $"📺 Channel: {author}";
                if (!string.IsNullOrWhiteSpace(description))
                {
                    overviewText += $"\n\n{description}";
                }

                list.Add(new ChannelItemInfo
                {
                    Name = title,
                    Id = "video:" + videoId,
                    Type = ChannelItemType.Media,
                    MediaType = ChannelMediaType.Video,
                    Overview = overviewText,
                    ImageUrl = thumbUrl
                });
            }
            return list;
        }

        public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
        {
            var videoId = id.Replace("video:", "");
            var config = Plugin.Instance!.Options;
            var baseUrl = (config.InvidiousUrl ?? "").TrimEnd('/');

            var api = new InvidiousApi();
            var playUrl = $"{baseUrl}/latest_version?id={videoId}&itag=22";

            try
            {
                using var videoDoc = await api.GetVideoAsync(baseUrl, videoId, cancellationToken).ConfigureAwait(false);
                var streamUrl = api.SelectBestStreamUrl(videoDoc);
                if (!string.IsNullOrWhiteSpace(streamUrl)) playUrl = streamUrl;
            }
            catch { }

            return new List<MediaSourceInfo>
            {
                new MediaSourceInfo
                {
                    // DER FIX GEGEN DEN HÄSSLICHEN LINK:
                    // Wir geben als "Name" des Streams einfach einen sauberen Text an. 
                    // Dadurch druckt Emby diesen sauberen Text unten bei "Media Info" hin statt der endlosen URL!
                    Name = "Invidious Stream (MP4)",
                    Path = playUrl,
                    Protocol = MediaProtocol.Http,
                    Id = "src:" + videoId,
                    IsInfiniteStream = false,
                    Container = "mp4", // Das hilft Emby zusätzlich, es ordentlich darzustellen
                    RequiredHttpHeaders = InvidiousApi.BuildPlaybackHeaders(baseUrl)
                }
            };
        }

        public IEnumerable<ImageType> GetSupportedChannelImages() => new List<ImageType> { ImageType.Thumb, ImageType.Primary };
        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken) => Task.FromResult<DynamicImageResponse>(null!);

        private static ChannelItemResult Msg(List<ChannelItemInfo> items, string msg)
        {
            items.Add(new ChannelItemInfo { Name = msg, Id = "msg", Type = ChannelItemType.Folder });
            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }
    }
}