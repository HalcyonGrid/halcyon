/*
 * Copyright (c) InWorldz Halcyon Developers
 * Copyright (c) Contributors, http://opensimulator.org/
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Communications;
using OpenSim.Framework.Communications.Services;
using OpenSim.Framework.Communications.Cache;
using OpenSim.Framework.Console;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Framework.Statistics;
using OpenSim.Region.ClientStack;
using OpenSim.Region.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Physics.Manager;
using OpenSim.Region.Interfaces;

namespace OpenSim
{
    /// <summary>
    /// Common OpenSim simulator code
    /// </summary>
    public class OpenSimBase : RegionApplicationBase
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // These are the names of the plugin-points extended by this
        // class during system startup.

        private const string PLUGIN_ASSET_CACHE = "/OpenSim/AssetCache";
        private const string PLUGIN_ASSET_SERVER_CLIENT = "/OpenSim/AssetClient";

        protected string proxyUrl;
        protected int proxyOffset = 0;
        
        public string userStatsURI = String.Empty;

        protected bool m_autoCreateClientStack = true;

        /// <summary>
        /// The file used to load and save prim backup xml if no filename has been specified
        /// </summary>
        protected const string DEFAULT_PRIM_BACKUP_FILENAME = "prim-backup.xml";

        /// <summary>
        /// The file used to load and save an opensim archive if no filename has been specified
        /// </summary>
        protected const string DEFAULT_OAR_BACKUP_FILENAME = "region.oar";

        public ConfigSettings ConfigurationSettings
        {
            get { return m_configSettings; }
            set { m_configSettings = value; }
        }

        protected ConfigSettings m_configSettings;

        protected ConfigurationLoader m_configLoader;

        protected GridInfoService m_gridInfoService;

        public ConsoleCommand CreateAccount = null;

        protected List<IApplicationPlugin> m_plugins = new List<IApplicationPlugin>();

        /// <value>
        /// The config information passed into the OpenSim region server.
        /// </value>        
        public OpenSimConfigSource ConfigSource
        {
            get { return m_config; }
            set { m_config = value; }
        }

        protected OpenSimConfigSource m_config;

        public List<IClientNetworkServer> ClientServers
        {
            get { return m_clientServers; }
        }

        protected List<IClientNetworkServer> m_clientServers = new List<IClientNetworkServer>();
 
/*
        public uint HttpServerPort
        {
            get { return m_httpServerPort; }
        }
*/
        public ModuleLoader ModuleLoader
        {
            get { return m_moduleLoader; }
            set { m_moduleLoader = value; }
        }

        protected ModuleLoader m_moduleLoader;

        protected IRegistryCore m_applicationRegistry = new RegistryCore();

        public IRegistryCore ApplicationRegistry
        {
            get { return m_applicationRegistry; }
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="configSource"></param>
        public OpenSimBase(IConfigSource configSource) : base()
        {
            LoadConfigSettings(configSource);
        }

        protected virtual void LoadConfigSettings(IConfigSource configSource)
        {
            m_configLoader = new ConfigurationLoader();
            m_config = m_configLoader.LoadConfigSettings(configSource, out m_configSettings, out m_networkServersInfo);
            ReadExtraConfigSettings();
        }

        protected virtual void ReadExtraConfigSettings()
        {
            IConfig networkConfig = m_config.Source.Configs["Network"];
            if (networkConfig != null)
            {
                proxyUrl = networkConfig.GetString("proxy_url", String.Empty);
                proxyOffset = Int32.Parse(networkConfig.GetString("proxy_offset", "0"));
            }
        }

        protected virtual void LoadPlugins()
        {
            PluginLoader<IApplicationPlugin> loader =
                new PluginLoader<IApplicationPlugin>(new ApplicationPluginInitializer(this));

            loader.Load("/OpenSim/Startup");
            m_plugins = loader.Plugins;
        }

        protected override List<string> GetHelpTopics()
        {
            List<string> topics = base.GetHelpTopics();
            Scene s = SceneManager.CurrentOrFirstScene;
            if (s != null && s.GetCommanders() != null)
                topics.AddRange(s.GetCommanders().Keys);

            return topics;
        }

        /// <summary>
        /// Performs startup specific to the region server, including initialization of the scene 
        /// such as loading configuration from disk.
        /// </summary>
        protected override void StartupSpecific()
        {
            IConfig startupConfig = m_config.Source.Configs["Startup"];
            if (startupConfig != null)
            {
                string pidFile = startupConfig.GetString("PIDFile", String.Empty);
                if (!String.IsNullOrEmpty(pidFile))
                    CreatePIDFile(pidFile);
                
                userStatsURI = startupConfig.GetString("Stats_URI", String.Empty);

            }

            base.StartupSpecific();

            m_stats = StatsManager.StartCollectingSimExtraStats();

            // Create a ModuleLoader instance
            m_moduleLoader = new ModuleLoader(m_config.Source);

            LoadPlugins();
            foreach (IApplicationPlugin plugin in m_plugins)
            {
                plugin.PostInitialize();
            }

            // Only enable logins to the regions once we have completely finished starting up (apart from scripts)
            if ((m_commsManager != null) && (m_commsManager.GridService != null))
            {
                m_commsManager.GridService.RegionLoginsEnabled = true;
            }

            AddPluginCommands();
        }

        protected virtual void AddPluginCommands()
        {
            // If console exists add plugin commands.
            if (m_console != null)
            {
                List<string> topics = GetHelpTopics();

                foreach (string topic in topics)
                {
                    m_console.Commands.AddCommand("plugin", false, "help " + topic,
                                                  "help " + topic,
                                                  "Get help on plugin command '" + topic + "'",
                                                  HandleCommanderHelp);

                    m_console.Commands.AddCommand("plugin", false, topic,
                                                  topic,
                                                  "Execute subcommand for plugin '" + topic + "'",
                                                  null);

                    ICommander commander = null;

                    Scene s = SceneManager.CurrentOrFirstScene;

                    if (s != null && s.GetCommanders() != null)
                    {
                        if (s.GetCommanders().ContainsKey(topic))
                            commander = s.GetCommanders()[topic];
                    }

                    if (commander == null)
                        continue;

                    foreach (string command in commander.Commands.Keys)
                    {
                        m_console.Commands.AddCommand(topic, false,
                                                      topic + " " + command,
                                                      topic + " " + commander.Commands[command].ShortHelp(),
                                                      String.Empty, HandleCommanderCommand);
                    }
                }
            }
        }

        private void HandleCommanderCommand(string module, string[] cmd)
        {
            m_sceneManager.SendCommandToPluginModules(cmd);
        }

        private void HandleCommanderHelp(string module, string[] cmd)
        {
            // Only safe for the interactive console, since it won't
            // let us come here unless both scene and commander exist
            //
            ICommander moduleCommander = SceneManager.CurrentOrFirstScene.GetCommander(cmd[1]);
            if (moduleCommander != null)
                m_console.Notice(moduleCommander.Help);
        }

        protected override void Initialize()
        {
            // Called from base.StartUp()

            m_httpServerPort = m_networkServersInfo.HttpListenerPort;
            InitializeAssetCache();
        }

        /// <summary>
        /// Initializes the asset cache. This supports legacy configuration values
        /// to ensure consistent operation, but values outside of that namespace
        /// are handled by the more generic resolution mechanism provided by 
        /// the ResolveAssetServer virtual method. If extended resolution fails, 
        /// then the normal default action is taken.
        /// Creation of the AssetCache is handled by ResolveAssetCache. This
        /// function accepts a reference to the instantiated AssetServer and
        /// returns an IAssetCache implementation, if possible. This is a virtual
        /// method.
        /// </summary>
        protected virtual void InitializeAssetCache()
        {
            IAssetServer assetServer = null;
            string mode = m_configSettings.AssetStorage;

            if (String.IsNullOrEmpty(mode))
            {
                throw new Exception("No asset server specified");
            }

            AssetClientPluginInitializer linit = new AssetClientPluginInitializer(m_configSettings);

            //todo: hack to handle transition from whip
            if (mode.ToUpper() == "WHIP") mode = mode.ToUpper();

            assetServer = loadAssetServer(mode, linit);

            if (assetServer == null)
            {
                throw new Exception(String.Format("Asset server {0} could not be loaded", mode));
            }

            // Initialize the asset cache, passing a reference to the selected
            // asset server interface.
            m_assetCache = ResolveAssetCache(assetServer);
            
            assetServer.Start();
        }

        // This method loads the identified asset server, passing an approrpiately
        // initialized Initialize wrapper. There should to be exactly one match,
        // if not, then the first match is used.
        private IAssetServer loadAssetServer(string id, PluginInitializerBase pi)
        {
            if (!String.IsNullOrEmpty(id))
            {
                m_log.DebugFormat("[HALCYONBASE] Attempting to load asset server id={0}", id);

                try
                {
                    PluginLoader<IAssetServer> loader = new PluginLoader<IAssetServer>(pi);
                    loader.AddFilter(PLUGIN_ASSET_SERVER_CLIENT, new PluginProviderFilter(id));
                    loader.Load(PLUGIN_ASSET_SERVER_CLIENT);

                    if (loader.Plugins.Count > 0)
                    {
                        m_log.DebugFormat("[HALCYONBASE] Asset server {0} loaded", id);
                        return (IAssetServer) loader.Plugins[0];
                    }
                }
                catch (Exception e)
                {
                    m_log.DebugFormat("[HALCYONBASE] Asset server {0} not loaded ({1})", id, e.Message);
                }
            }
            return null;
        }

        /// <summary>
        /// Attempt to instantiate an IAssetCache implementation, using the
        /// provided IAssetServer reference.
        /// An asset cache implementation must provide a constructor that
        /// accepts two parameters;
        ///   [1] A ConfigSettings reference.
        ///   [2] An IAssetServer reference.
        /// The AssetCache value is obtained from the 
        /// [StartUp]/AssetCache value in the configuration file.
        /// </summary>
        protected virtual IAssetCache ResolveAssetCache(IAssetServer assetServer)
        {
            return new Framework.Communications.Cache.AssetCache(assetServer);
        }

        public void ProcessLogin(bool LoginEnabled)
        {
            if (LoginEnabled)
            {
                m_log.Info("[LOGIN]: Login is now enabled.");
                m_commsManager.GridService.RegionLoginsEnabled = true;
            }
            else
            {
                m_log.Info("[LOGIN]: Login is now disabled.");
                m_commsManager.GridService.RegionLoginsEnabled = false;
            }
        }

        /// <summary>
        /// Execute the region creation process.  This includes setting up scene infrastructure.
        /// </summary>
        /// <param name="regionInfo"></param>
        /// <param name="portadd_flag"></param>
        /// <returns></returns>
        public IClientNetworkServer CreateRegion(RegionInfo regionInfo, bool portadd_flag, out IScene scene)
        {
            return CreateRegion(regionInfo, portadd_flag, false, out scene);
        }

        /// <summary>
        /// Execute the region creation process.  This includes setting up scene infrastructure.
        /// </summary>
        /// <param name="regionInfo"></param>
        /// <param name="portadd_flag"></param>
        /// <param name="do_post_init"></param>
        /// <returns></returns>
        private IClientNetworkServer CreateRegion(RegionInfo regionInfo, bool portadd_flag, bool do_post_init, out IScene mscene)
        {
            int port = regionInfo.InternalEndPoint.Port;

            // set initial RegionID to originRegionID in RegionInfo. (it needs for loding prims)
            // Commented this out because otherwise regions can't register with
            // the grid as there is already another region with the same UUID
            // at those coordinates. This is required for the load balancer to work.
            // --Mike, 2009.02.25
            //regionInfo.originRegionID = regionInfo.RegionID;

            // set initial ServerURI
            regionInfo.HttpPort = m_httpServerPort;
            
            regionInfo.osSecret = m_osSecret;
            
            if (!String.IsNullOrEmpty(proxyUrl) && (portadd_flag))
            {
                // set proxy url to RegionInfo
                regionInfo.proxyUrl = proxyUrl;
                regionInfo.ProxyOffset = proxyOffset;
                Util.XmlRpcCommand(proxyUrl, "AddPort", port, port + proxyOffset, regionInfo.ExternalHostName);
            }

            IClientNetworkServer clientServer;
            Scene scene = SetupScene(regionInfo, proxyOffset, m_config.Source, out clientServer);

            m_log.Info("[MODULES]: Loading Region's modules (old style)");

            List<IRegionModule> modules = m_moduleLoader.PickupModules(scene, ".");

            // This needs to be ahead of the script engine load, so the
            // script module can pick up events exposed by a module
            m_moduleLoader.InitializeSharedModules(scene);

            // Use this in the future, the line above will be deprecated soon
            m_log.Info("[MODULES]: Loading Region's modules (new style)");
            IRegionModulesController controller;
            if (ApplicationRegistry.TryGet(out controller))
            {
                controller.AddRegionToModules(scene);
            }
            else m_log.Error("[MODULES]: The new RegionModulesController is missing...");

            // Check if a (any) permission module has been loaded.
            // This can be any permissions module, including the default PermissionsModule with m_bypassPermissions==true.
            // To disable permissions, specify [Startup] permissionmodules=DefaultPermissionsModule in the .ini file, and 
            // serverside_object_permissions=false, optionally propagate_permissions=false.
            if (!scene.Permissions.IsAvailable())
            {
                m_log.Error("[MODULES]: Permissions module is not set, or set incorrectly.");
                Environment.Exit(1);
                // Note that an Exit here can trigger a PhysX exception but if that happens it is the result of this 
                // permissions problem and hopefully the lot will show this error and intentional exit.
            }

            scene.SetModuleInterfaces();

            // this must be done before prims try to rez or they'll rez over no land
            scene.loadAllLandObjectsFromStorage(regionInfo.originRegionID);

            //we need to fire up the scene here. physics may depend ont he heartbeat running
            scene.Start();

            // Prims have to be loaded after module configuration since some modules may be invoked during the load            
            scene.LoadPrimsFromStorage(regionInfo.originRegionID);
            
            try
            {
                scene.RegisterRegionWithGrid();
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[STARTUP]: Registration of region with grid failed, aborting startup - {0}", e);

                // Carrying on now causes a lot of confusion down the
                // line - we need to get the user's attention
                Environment.Exit(1);
            }

            // We need to do this after we've initialized the
            // scripting engines.
            scene.CreateScriptInstances();

            scene.EventManager.TriggerParcelPrimCountUpdate();

            m_sceneManager.Add(scene);

            if (m_autoCreateClientStack)
            {
                m_clientServers.Add(clientServer);
                clientServer.Start();
            }

            if (do_post_init)
            {
                foreach (IRegionModule module in modules)
                {
                    module.PostInitialize();
                }
            }

            scene.EventManager.OnShutdown += delegate() { ShutdownRegion(scene); };

            mscene = scene;
            return clientServer;
        }

        private void ShutdownRegion(Scene scene)
        {
            m_log.DebugFormat("[SHUTDOWN]: Shutting down region {0}", scene.RegionInfo.RegionName);
            IRegionModulesController controller;
            if (ApplicationRegistry.TryGet<IRegionModulesController>(out controller))
            {
                controller.RemoveRegionFromModules(scene);
            }
        }

        public void RemoveRegion(Scene scene, bool cleanup)
        {
            // only need to check this if we are not at the
            // root level
            if ((m_sceneManager.CurrentScene != null) &&
                (m_sceneManager.CurrentScene.RegionInfo.RegionID == scene.RegionInfo.RegionID))
            {
                m_sceneManager.TrySetCurrentScene("..");
            }

            scene.DeleteAllSceneObjects();
            m_sceneManager.CloseScene(scene);

            if (!cleanup)
                return;

            if (!String.IsNullOrEmpty(scene.RegionInfo.RegionFile))
            {
                File.Delete(scene.RegionInfo.RegionFile);
                m_log.InfoFormat("[HALCYON]: deleting region file \"{0}\"", scene.RegionInfo.RegionFile);
            }
        }

        public void RemoveRegion(string name, bool cleanUp)
        {
            Scene target;
            if (m_sceneManager.TryGetScene(name, out target))
                RemoveRegion(target, cleanUp);
        }

        /// <summary>
        /// Create a scene and its initial base structures.
        /// </summary>
        /// <param name="regionInfo"></param>
        /// <param name="clientServer"> </param>
        /// <returns></returns>        
        protected Scene SetupScene(RegionInfo regionInfo, out IClientNetworkServer clientServer)
        {
            return SetupScene(regionInfo, 0, null, out clientServer);
        }

        /// <summary>
        /// Create a scene and its initial base structures.
        /// </summary>
        /// <param name="regionInfo"></param>
        /// <param name="proxyOffset"></param>
        /// <param name="configSource"></param>
        /// <param name="clientServer"> </param>
        /// <returns></returns>
        protected Scene SetupScene(
            RegionInfo regionInfo, int proxyOffset, IConfigSource configSource, out IClientNetworkServer clientServer)
        {
            IPAddress listenIP = regionInfo.InternalEndPoint.Address;
            //if (!IPAddress.TryParse(regionInfo.InternalEndPoint, out listenIP))
            //    listenIP = IPAddress.Parse("0.0.0.0");

            uint port = (uint) regionInfo.InternalEndPoint.Port;

            if (m_autoCreateClientStack)
            {
                clientServer
                    = m_clientStackManager.CreateServer(
                        listenIP, ref port, proxyOffset, regionInfo.m_allow_alternate_ports, configSource,
                        m_assetCache);
            }
            else
            {
                clientServer = null;
            }

            regionInfo.InternalEndPoint.Port = (int) port;

            // TODO: Remove this cruft once MasterAvatar is fully deprecated
            //Master Avatar Setup
            UserProfileData masterAvatar;
            if (regionInfo.MasterAvatarAssignedUUID == UUID.Zero)
            {
                masterAvatar =
                    m_commsManager.UserService.SetupMasterUser(regionInfo.MasterAvatarFirstName,
                                                               regionInfo.MasterAvatarLastName,
                                                               regionInfo.MasterAvatarSandboxPassword);
            }
            else
            {
                masterAvatar = m_commsManager.UserService.SetupMasterUser(regionInfo.MasterAvatarAssignedUUID);
            }

            if (masterAvatar == null)
            {
                regionInfo.MasterAvatarAssignedUUID = UUID.Zero;
                m_log.Error("[PARCEL]: No master avatar found, using null UUID for master avatar.");
                // Should be a fatal error?
            }
            else
            {
                regionInfo.MasterAvatarAssignedUUID = masterAvatar.ID;
                regionInfo.MasterAvatarFirstName = masterAvatar.FirstName;
                regionInfo.MasterAvatarLastName = masterAvatar.SurName;
                m_log.InfoFormat("[PARCEL]: Found master avatar {0} {1} [{2}]",
                        regionInfo.MasterAvatarFirstName,
                        regionInfo.MasterAvatarLastName,
                        regionInfo.MasterAvatarAssignedUUID.ToString()
                        );
            }

            Scene scene = CreateScene(regionInfo, m_storageManager);

            if (m_autoCreateClientStack)
            {
                clientServer.AddScene(scene);
            }

            scene.LoadWorldMap();

            scene.PhysicsScene = GetPhysicsScene(scene.RegionInfo.RegionName, scene.RegionInfo.RegionID);
            scene.PhysicsScene.TerrainChannel = scene.Heightmap;
            scene.PhysicsScene.RegionSettings = scene.RegionInfo.RegionSettings;
            scene.PhysicsScene.SetStartupTerrain(scene.Heightmap.GetFloatsSerialized(), scene.Heightmap.RevisionNumber);
            scene.PhysicsScene.SetWaterLevel((float) regionInfo.RegionSettings.WaterHeight);

            return scene;
        }

        protected override StorageManager CreateStorageManager()
        {
            return
                CreateStorageManager(m_configSettings.StorageConnectionString, m_configSettings.EstateConnectionString);
        }

        protected StorageManager CreateStorageManager(string connectionstring, string estateconnectionstring)
        {
            return new StorageManager(m_configSettings.StorageDll, connectionstring, estateconnectionstring);
        }

        protected override ClientStackManager CreateClientStackManager()
        {
            return new ClientStackManager(m_configSettings.ClientstackDll);
        }

        protected override Scene CreateScene(RegionInfo regionInfo, StorageManager storageManager)
        {
            SceneCommunicationService sceneGridService = new SceneCommunicationService(m_commsManager);

            return new Scene(
                regionInfo, m_commsManager, sceneGridService,
                storageManager, m_moduleLoader, m_configSettings.PhysicalPrim,
                m_configSettings.See_into_region_from_neighbor, m_config.Source);
        }

        # region Setup methods

        protected override PhysicsScene GetPhysicsScene(string osSceneIdentifier, UUID regionId)
        {
            return GetPhysicsScene(
                m_configSettings.PhysicsEngine, m_configSettings.MeshEngineName, m_config.Source, osSceneIdentifier,
                regionId);
        }

        /// <summary>
        /// Handler to supply the current status of this sim
        /// </summary>
        /// Currently this is always OK if the simulator is still listening for connections on its HTTP service
        public class SimStatusHandler : IStreamedRequestHandler
        {
            public string Name { get { return "SimStatusHandler"; } }
            public string Description { get { return String.Empty; } }

            public byte[] Handle(string path, Stream request,
                                 OSHttpRequest httpRequest, OSHttpResponse httpResponse)
            {
                return Encoding.UTF8.GetBytes("OK");
            }

            public string ContentType
            {
                get { return "text/plain"; }
            }

            public string HttpMethod
            {
                get { return "GET"; }
            }

            public string Path
            {
                get { return "/simstatus/"; }
            }
        }

        /// <summary>
        /// Handler to supply the current extended status of this sim
        /// Sends the statistical data in a json serialization 
        /// </summary>
        public class XSimStatusHandler : IStreamedRequestHandler
        {
            public string Name { get { return "XSimStatusHandler"; } }
            public string Description { get { return String.Empty; } }

            OpenSimBase m_opensim;
            string osXStatsURI = String.Empty;
        
            public XSimStatusHandler(OpenSimBase sim)
            {
                m_opensim = sim;
                osXStatsURI = Util.SHA1Hash(sim.osSecret);
            }
            
            public byte[] Handle(string path, Stream request,
                                 OSHttpRequest httpRequest, OSHttpResponse httpResponse)
            {
                return Encoding.UTF8.GetBytes(m_opensim.StatReport(httpRequest));
            }

            public string ContentType
            {
                get { return "text/plain"; }
            }

            public string HttpMethod
            {
                get { return "GET"; }
            }

            public string Path
            {   
                // This is for the OpenSim instance and is the osSecret hashed
                get { return "/" + osXStatsURI + "/"; }
            }
        }

        /// <summary>
        /// Handler to supply the current extended status of this sim to a user configured URI
        /// Sends the statistical data in a json serialization 
        /// If the request contains a key, "callback" the response will be wrappend in the 
        /// associated value for jsonp used with ajax/javascript
        /// </summary>
        public class UXSimStatusHandler : IStreamedRequestHandler
        {
            public string Name { get { return "UXSimStatusHandler"; } }
            public string Description { get { return String.Empty; } }

            OpenSimBase m_opensim;
            string osUXStatsURI = String.Empty;
        
            public UXSimStatusHandler(OpenSimBase sim)
            {
                m_opensim = sim;
                osUXStatsURI = sim.userStatsURI;
                
            }
            
            public byte[] Handle(string path, Stream request,
                                 OSHttpRequest httpRequest, OSHttpResponse httpResponse)
            {
                return Encoding.UTF8.GetBytes(m_opensim.StatReport(httpRequest));
            }

            public string ContentType
            {
                get { return "text/plain"; }
            }

            public string HttpMethod
            {
                get { return "GET"; }
            }

            public string Path
            {   
                // This is for the OpenSim instance and is the user provided URI 
                get { return "/" + osUXStatsURI + "/"; }
            }
        }

        #endregion

        /// <summary>
        /// Performs any last-minute sanity checking and shuts down the region server
        /// </summary>
        public override void ShutdownSpecific()
        {
            if (!String.IsNullOrEmpty(proxyUrl))
            {
                Util.XmlRpcCommand(proxyUrl, "Stop");
            }

            try
            {
                m_log.Info("[SHUTDOWN]: Closing scene manager");
                m_sceneManager.Close();
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[SHUTDOWN]: Ignoring failure during shutdown - {0}", e);
            }

            try
            {
                m_log.Info("[SHUTDOWN]: Disposing asset cache and services");
                m_assetCache.Dispose();
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[SHUTDOWN]: Ignoring failure during shutdown - {0}", e);
            }
        }

        /// <summary>
        /// Get the start time and up time of Region server
        /// </summary>
        /// <param name="starttime">The first out parameter describing when the Region server started</param>
        /// <param name="uptime">The second out parameter describing how long the Region server has run</param>
        public void GetRunTime(out string starttime, out string uptime)
        {
            starttime = m_startuptime.ToString();
            uptime = (DateTime.Now - m_startuptime).ToString();
        }

        /// <summary>
        /// Get the number of the avatars in the Region server
        /// </summary>
        /// <param name="usernum">The first out parameter describing the number of all the avatars in the Region server</param>
        public void GetAvatarNumber(out int usernum)
        {
            usernum = m_sceneManager.GetCurrentSceneAvatars().Count;
        }

        /// <summary>
        /// Get the number of regions
        /// </summary>
        /// <param name="regionnum">The first out parameter describing the number of regions</param>
        public void GetRegionNumber(out int regionnum)
        {
            regionnum = m_sceneManager.Scenes.Count;
        }
    }

    
    public class OpenSimConfigSource
    {
        public IConfigSource Source;

        public void Save(string path)
        {
            if (Source is IniConfigSource)
            {
                IniConfigSource iniCon = (IniConfigSource) Source;
                iniCon.Save(path);
            }
            else if (Source is XmlConfigSource)
            {
                XmlConfigSource xmlCon = (XmlConfigSource) Source;
                xmlCon.Save(path);
            }
        }
    }
}
