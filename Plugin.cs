using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using System;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace Emby.InvidiousPlugin
{
    public class Plugin : BasePluginSimpleUI<PluginConfiguration>, IHasThumbImage
    {
        private static readonly string PluginVersion =
            typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        public override string Name => $"Invidious v{PluginVersion}";
        public override string Description => "100% privacy-friendly YouTube via your self-hosted instance.";
        public override Guid Id => Guid.Parse("A1B2C3D4-E5F6-4A5B-8C9D-0E1F2A3B4C5D");

        public static Plugin? Instance { get; private set; }
        public static ISessionManager? SessionManager { get; private set; }
        public static string? LibraryDbPath { get; private set; }

        public Plugin(IApplicationHost applicationHost) : base(applicationHost)
        {
            Instance = this;
            try { SessionManager = applicationHost.Resolve<ISessionManager>(); } catch { }
            try
            {
                var paths = applicationHost.Resolve<IServerApplicationPaths>();
                if (paths != null)
                {
                    var candidate = Path.Combine(paths.DataPath, "library.db");
                    if (File.Exists(candidate))
                        LibraryDbPath = candidate;
                }
            }
            catch { }
        }

        public PluginConfiguration Options => GetOptions();

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public override PluginInfo GetPluginInfo()
        {
            var info = base.GetPluginInfo();
            var assemblyDir = Path.GetDirectoryName(AssemblyFilePath) ?? string.Empty;
            var thumbPath = Path.Combine(assemblyDir, "thumb.png");

            if (File.Exists(thumbPath))
            {
                var imageTag = File.GetLastWriteTimeUtc(thumbPath)
                    .Ticks
                    .ToString(CultureInfo.InvariantCulture);

                // Emby builds differ: some expect ImageTag, others ImageUrl.
                SetStringPropertyIfExists(info, "ImageTag", imageTag);

                var pngBytes = File.ReadAllBytes(thumbPath);
                var imageDataUrl = "data:image/png;base64," + Convert.ToBase64String(pngBytes);
                SetStringPropertyIfExists(info, "ImageUrl", imageDataUrl);
            }

            return info;
        }

        private static void SetStringPropertyIfExists(object target, string propertyName, string value)
        {
            var property = target.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance);

            if (property?.CanWrite == true && property.PropertyType == typeof(string))
            {
                property.SetValue(target, value);
            }
        }

        public Stream GetThumbImage()
        {
            var type = GetType();
            var stream = type.Assembly.GetManifestResourceStream(type.Namespace + ".thumb.png");
            return stream ?? new MemoryStream();
        }
    }

    public class PluginEntryPoint : IServerEntryPoint
    {
        public void Run()
        {
            InvidiousChannel.ScheduleSortNameFix();
        }

        public void Dispose()
        {
            MuxHelper.Shutdown();
        }
    }
}
