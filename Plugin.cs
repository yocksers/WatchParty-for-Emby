using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Drawing;

namespace WatchPartyForEmby
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IServerEntryPoint, IHasThumbImage
    {
        public event EventHandler ConfigurationUpdated;
        private readonly ILogger _logger;
        private readonly IJsonSerializer _jsonSerializer;
        private ExternalWebServer _externalWebServer;
        private int _lastPort = 0;
        private bool _lastEnabled = false;
        public static string ExternalWebServerStatus { get; private set; } = "Not Enabled";

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogManager logManager, IJsonSerializer jsonSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            _logger = logManager.GetLogger(GetType().Name);
            _jsonSerializer = jsonSerializer;
            
            ConfigurationUpdated += OnConfigurationUpdated;
        }

        public override string Name => "Watch Party";

        public override string Description => "Create synchronized watch parties";

        public override Guid Id => Guid.Parse("a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d");

        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".images.logo.jpg");
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Jpg;

        public static Plugin Instance { get; private set; }

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            base.UpdateConfiguration(configuration);
            ConfigurationUpdated?.Invoke(this, EventArgs.Empty);
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "watchpartyconfig",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
                },
                new PluginPageInfo
                {
                    Name = "configPagejs",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js"
                },
                new PluginPageInfo
                {
                    Name = "watchpartydashboard",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.dashboard.html"
                },
                new PluginPageInfo
                {
                    Name = "watchpartyexternal",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.external.html"
                },
                new PluginPageInfo
                {
                    Name = "watchpartyapi",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.api.js"
                },
                new PluginPageInfo
                {
                    Name = "watchpartyui",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.ui.js"
                },
                new PluginPageInfo
                {
                    Name = "watchpartymanager",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.partyManager.js"
                }
            };
        }

        public void Run()
        {
            StartWebServer();
        }

        private void OnConfigurationUpdated(object sender, EventArgs e)
        {
            var currentPort = Configuration.ExternalWebServerPort;
            var currentEnabled = Configuration.EnableExternalWebServer;
            
            if (_lastPort != currentPort || _lastEnabled != currentEnabled)
            {
                _logger.Info("[Watch Party] Web server configuration changed, restarting...");
                RestartWebServer();
            }
        }

        private void StartWebServer()
        {
            if (Configuration.EnableExternalWebServer)
            {
                _externalWebServer = new ExternalWebServer(_logger, _jsonSerializer, Configuration.ExternalWebServerPort);
                ExternalWebServerStatus = _externalWebServer.Start();
                _logger.Info($"[Watch Party] External web server status: {ExternalWebServerStatus}");
                
                _lastPort = Configuration.ExternalWebServerPort;
                _lastEnabled = Configuration.EnableExternalWebServer;
            }
            else
            {
                ExternalWebServerStatus = "Not Enabled";
                _logger.Info("[Watch Party] External web server is disabled in configuration");
                
                _lastPort = 0;
                _lastEnabled = false;
            }
        }

        private void RestartWebServer()
        {
            if (_externalWebServer != null)
            {
                _logger.Info("[Watch Party] Stopping existing web server...");
                _externalWebServer.Stop();
                _externalWebServer = null;
            }
            
            StartWebServer();
        }

        public void Dispose()
        {
            _externalWebServer?.Stop();
        }
    }
}
