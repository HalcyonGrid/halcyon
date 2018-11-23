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
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Timers;
using System.Threading.Tasks;

using log4net;
using log4net.Config;
using Nini.Config;
using OpenMetaverse;

using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Framework.Communications;
using OpenSim.Framework.Communications.Cache;
using OpenSim.Framework.Console;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Framework.Statistics;
using OpenSim.Grid.Framework;
using OpenSim.Grid.UserServer.Modules;
using System.Threading;

namespace OpenSim.Grid.UserServer
{
    /// <summary>
    /// Grid user server main class
    /// </summary>
    public class OpenUser_Main : BaseOpenSimServer, IGridServiceCore
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected UserConfig Cfg;

        protected UserDataBaseService m_userDataBaseService;

        public Modules.UserManager m_userManager;

        protected UserServerAvatarAppearanceModule m_avatarAppearanceModule;
        protected UserServerFriendsModule m_friendsModule;

        public UserLoginService m_loginService;
        public MessageServersConnector m_messagesService;

        protected GridInfoServiceModule m_gridInfoService;

        protected UserServerCommandModule m_consoleCommandModule;
        protected UserServerEventDispatchModule m_eventDispatcher;

        protected InWorldz.RemoteAdmin.RemoteAdmin m_radmin;

        protected JWTAuthenticator m_jwtAuthenticator;

        private ManualResetEvent Terminating = new ManualResetEvent(false);

        private bool m_useJwt;

        public static void Main(string[] args)
        {
            // Please note that if you are changing something in this function you should check to see if you need to change the other server's Main functions as well.

            // Under any circumstance other than an explicit exit the exit code should be 1.
            Environment.ExitCode = 1;

            ServicePointManager.DefaultConnectionLimit = 12;

            // Add the arguments supplied when running the application to the configuration
            var configSource = new ArgvConfigSource(args);

            configSource.Alias.AddAlias("On", true);
            configSource.Alias.AddAlias("Off", false);
            configSource.Alias.AddAlias("True", true);
            configSource.Alias.AddAlias("False", false);
            configSource.Alias.AddAlias("Yes", true);
            configSource.Alias.AddAlias("No", false);

            configSource.AddSwitch("Startup", "background");
            configSource.AddSwitch("Startup", "pidfile");

            m_log.Info("[SERVER]: Launching UserServer...");

            var pidFile = new PIDFileManager(configSource.Configs["Startup"].GetString("pidfile", string.Empty));
            XmlConfigurator.Configure();

            var userserver = new OpenUser_Main();

            pidFile.SetStatus(PIDFileManager.Status.Starting);
            userserver.Startup();

            pidFile.SetStatus(PIDFileManager.Status.Running);
            userserver.Work(configSource.Configs["Startup"].GetBoolean("background", false));
        }

        public OpenUser_Main()
        {
            m_console = new LocalConsole("User");
            MainConsole.Instance = m_console;
        }

        public void Work(bool background)
        {
            if (background)
            {
                Terminating.WaitOne();
                Terminating.Close();
            }
            else
            {
                m_console.Notice("Enter help for a list of commands\n");

                while (true)
                {
                    m_console.Prompt();
                }
            }
        }

        protected override void StartupSpecific()
        {
            StartupCoreComponents();

            m_stats = StatsManager.StartCollectingUserStats();

            //setup services/modules
            StartupUserServerModules();

            StartOtherComponents();

            //PostInitialize the modules
            PostInitializeModules();

            //register http handlers and start http server
            m_log.Info("[STARTUP]: Starting HTTP process");
            RegisterHttpHandlers();
            m_httpServer.Start();



            base.StartupSpecific();
        }

        protected virtual void StartupCoreComponents()
        {
            Cfg = new UserConfig("USER SERVER", (Path.Combine(Util.configDir(), "UserServer_Config.xml")));

            m_httpServer = new BaseHttpServer(Cfg.HttpPort, null);

            RegisterInterface<CommandConsole>(m_console);
            RegisterInterface<UserConfig>(Cfg);

            IConfigSource defaultConfig = new IniConfigSource("Halcyon.ini");
            IConfig startupConfig = defaultConfig.Configs["Startup"];
            IConfig inventoryConfig = defaultConfig.Configs["Inventory"];
            IConfig jwtConfig = defaultConfig.Configs["JWT"];

            m_useJwt = jwtConfig?.GetBoolean("enabled") ?? false;

            OpenSim.Framework.ConfigSettings settings = new ConfigSettings();
            settings.CoreConnectionString = startupConfig.GetString("core_connection_string");

            settings.InventoryPlugin = inventoryConfig.GetString("inventory_plugin", "Halcyon.Data.Inventory.MySQL.dll");
            settings.InventorySource = inventoryConfig.GetString("legacy_inventory_source", "");
            settings.InventorySource = inventoryConfig.GetString("inventory_connection_string", settings.InventorySource);

            // Everything has defaults
            settings.InventoryPlugin = inventoryConfig.GetString("inventory_plugin", "Halcyon.Data.Inventory.MySQL.dll");
            settings.InventorySource = inventoryConfig.GetString("legacy_inventory_source", settings.CoreConnectionString);
            settings.InventorySource = inventoryConfig.GetString("inventory_connection_string", settings.InventorySource);

            PluginLoader<IInventoryStoragePlugin> loader = new PluginLoader<IInventoryStoragePlugin>();
            loader.Add("/OpenSim/InventoryStorage", new PluginProviderFilter(settings.InventoryPlugin));
            loader.Load();

            if (loader.Plugin != null)
                loader.Plugin.Initialize(settings);
        }

        /// <summary>
        /// Start up the user manager
        /// </summary>
        /// <param name="inventoryService"></param>
        protected virtual void StartupUserServerModules()
        {
            m_log.Info("[STARTUP]: Establishing data connection");

            //we only need core components so we can request them from here
            IInterServiceInventoryServices inventoryService;
            TryGet<IInterServiceInventoryServices>(out inventoryService);

            CommunicationsManager commsManager = new UserServerCommsManager();

            //setup database access service, for now this has to be created before the other modules.
            m_userDataBaseService = new UserDataBaseService(commsManager);
            m_userDataBaseService.Initialize(this);
            RegisterInterface<UserDataBaseService>(m_userDataBaseService);

            //TODO: change these modules so they fetch the databaseService class in the PostInitialize method
            m_userManager = new Modules.UserManager(m_userDataBaseService);
            m_userManager.Initialize(this);

            m_avatarAppearanceModule = new UserServerAvatarAppearanceModule(m_userDataBaseService);
            m_avatarAppearanceModule.Initialize(this);

            m_friendsModule = new UserServerFriendsModule(m_userDataBaseService);
            m_friendsModule.Initialize(this);

            if (m_useJwt)
            {
                m_jwtAuthenticator = new JWTAuthenticator();
                m_jwtAuthenticator.Initialize(this);
            }

            m_consoleCommandModule = new UserServerCommandModule();
            m_consoleCommandModule.Initialize(this);

            m_messagesService = new MessageServersConnector();
            m_messagesService.Initialize(this);

            m_gridInfoService = new GridInfoServiceModule();
            m_gridInfoService.Initialize(this);
        }

        protected virtual void StartOtherComponents()
        {
            StartupLoginService();
            //
            // Get the minimum defaultLevel to access to the grid
            //
            m_loginService.setloginlevel((int)Cfg.DefaultUserLevel);

            RegisterInterface<UserLoginService>(m_loginService); //TODO: should be done in the login service

            m_eventDispatcher = new UserServerEventDispatchModule(m_userManager, m_messagesService, m_loginService);
            m_eventDispatcher.Initialize(this);
        }

        /// <summary>
        /// Start up the login service
        /// </summary>
        /// <param name="inventoryService"></param>
        protected virtual void StartupLoginService()
        {
            m_loginService = new UserLoginService(
                m_userDataBaseService, new LibraryRootFolder(Cfg.LibraryXmlfile, Cfg.LibraryName), Cfg.MapServerURI, Cfg.ProfileServerURI, Cfg, Cfg.DefaultStartupMsg, new RegionProfileServiceProxy());
        }

        protected virtual void PostInitializeModules()
        {
            m_consoleCommandModule.PostInitialize(); //it will register its Console command handlers in here
            m_userDataBaseService.PostInitialize();
            m_messagesService.PostInitialize();
            m_eventDispatcher.PostInitialize(); //it will register event handlers in here
            m_gridInfoService.PostInitialize();
            m_userManager.PostInitialize();
            m_avatarAppearanceModule.PostInitialize();
            m_friendsModule.PostInitialize();

            if (m_useJwt)
            {
                m_jwtAuthenticator.PostInitialize(Cfg.SSLPrivateCertFile, Cfg.SSLPublicCertFile);
            }
        }

        protected virtual void RegisterHttpHandlers()
        {
            m_loginService.RegisterHandlers(m_httpServer, true);

            m_userManager.RegisterHandlers(m_httpServer);
            m_friendsModule.RegisterHandlers(m_httpServer);
            m_avatarAppearanceModule.RegisterHandlers(m_httpServer);
            m_messagesService.RegisterHandlers(m_httpServer);
            m_gridInfoService.RegisterHandlers(m_httpServer);

            if (m_useJwt)
            {
                m_jwtAuthenticator.RegisterHandlers(m_httpServer);
            }


            m_radmin = new InWorldz.RemoteAdmin.RemoteAdmin(Cfg.SSLPublicCertFile);
            m_radmin.AddCommand("UserService", "Shutdown", UserServerShutdownHandler);
            m_radmin.AddHandler(m_httpServer);
        }

        public object UserServerShutdownHandler(IList args, IPEndPoint remoteClient)
        {
            m_radmin.CheckSessionValid(new UUID((string)args[0]));

            try
            {
                int delay = (int)args[1];
                string message;

                if (delay > 0)
                    message = "Server is going down in " + delay.ToString() + " second(s).";
                else
                    message = "Server is going down now.";

                m_log.DebugFormat("[RADMIN] Shutdown: {0}", message);

                // Perform shutdown
                if (delay > 0)
                    System.Threading.Thread.Sleep(delay * 1000);

                // Do this on a new thread so the actual shutdown call returns successfully.
                Task.Factory.StartNew(() => Shutdown());
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[RADMIN] Shutdown: failed: {0}", e.Message);
                m_log.DebugFormat("[RADMIN] Shutdown: failed: {0}", e.ToString());
                throw;
            }

            m_log.Info("[RADMIN]: Shutdown Administrator Request complete");
            return true;
        }

        public override void ShutdownSpecific()
        {
            Terminating.Set();
            m_eventDispatcher.Close();
        }

        #region IUGAIMCore
        protected Dictionary<Type, object> m_moduleInterfaces = new Dictionary<Type, object>();

        /// <summary>
        /// Register an Module interface.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iface"></param>
        public void RegisterInterface<T>(T iface)
        {
            lock (m_moduleInterfaces)
            {
                if (!m_moduleInterfaces.ContainsKey(typeof(T)))
                {
                    m_moduleInterfaces.Add(typeof(T), iface);
                }
            }
        }

        public bool TryGet<T>(out T iface)
        {
            if (m_moduleInterfaces.ContainsKey(typeof(T)))
            {
                iface = (T)m_moduleInterfaces[typeof(T)];
                return true;
            }
            iface = default(T);
            return false;
        }

        public T Get<T>()
        {
            return (T)m_moduleInterfaces[typeof(T)];
        }

        public BaseHttpServer GetHttpServer()
        {
            return m_httpServer;
        }
        #endregion

        public void TestResponse(List<InventoryFolderBase> resp)
        {
            m_console.Notice("response got");
        }
    }
}
