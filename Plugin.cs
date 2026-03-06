using MediaBrowser.Common;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using System;

namespace Emby.InvidiousPlugin
{
    public class Plugin : BasePluginSimpleUI<PluginConfiguration>
    {
        public override string Name => "Invidious";
        public override string Description => "Watch privacy-friendly YouTube videos via Invidious.";
        public override Guid Id => Guid.Parse("A1B2C3D4-E5F6-4A5B-8C9D-0E1F2A3B4C5D");

        public static Plugin? Instance { get; private set; }

        public Plugin(IApplicationHost applicationHost) : base(applicationHost)
        {
            Instance = this;
        }

        public PluginConfiguration Options => GetOptions();
    }
}