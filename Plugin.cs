using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Drawing;
using System;
using System.IO;

namespace Emby.InvidiousPlugin
{
    public class Plugin : BasePluginSimpleUI<PluginConfiguration>, IHasThumbImage
    {
        public override string Name => "Invidious";
        public override string Description => "100% privacy-friendly YouTube via your self-hosted instance.";
        public override Guid Id => Guid.Parse("A1B2C3D4-E5F6-4A5B-8C9D-0E1F2A3B4C5D");

        public static Plugin? Instance { get; private set; }

        public Plugin(IApplicationHost applicationHost) : base(applicationHost)
        {
            Instance = this;
        }

        public PluginConfiguration Options => GetOptions();

        public Stream GetThumbImage()
        {
            var type = GetType();
            var stream = type.Assembly.GetManifestResourceStream(type.Namespace + ".thumb.png");
            return stream ?? new MemoryStream();
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;
    }
}