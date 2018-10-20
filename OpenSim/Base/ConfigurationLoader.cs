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
 *     * Neither the name of the OpenSimulator Project nor the
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
using System.IO;
using System.Reflection;
using System.Threading;
using System.Xml;
using log4net;
using Nini.Config;
using OpenSim.Framework;

namespace OpenSim
{
    public class ConfigurationLoader
    {
        protected ConfigSettings m_configSettings;
        protected OpenSimConfigSource m_config;
        protected NetworkServersInfo m_networkServersInfo;

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public ConfigurationLoader()
        {
        }

        public OpenSimConfigSource LoadConfigSettings(IConfigSource configSource, out ConfigSettings configSettings,
                                                      out NetworkServersInfo networkInfo)
        {
            m_configSettings = configSettings = new ConfigSettings();
            m_networkServersInfo = networkInfo = new NetworkServersInfo();
            bool iniFileExists = false;

            IConfig startupConfig = configSource.Configs["Startup"];

            //old style or new style settings?
            string iniFileName = startupConfig.GetString("inifile", "Halcyon.ini");
            ApplicationBase.iniFilePath = Path.Combine(Util.configDir(), iniFileName);

            string masterFileName = startupConfig.GetString("inimaster", String.Empty);
            string masterfilePath = Path.Combine(Util.configDir(), masterFileName);

            string iniDirName = startupConfig.GetString("inidirectory", "config");
            //string iniDirPath = Path.Combine(Util.configDir(), iniDirName);

            m_config = new OpenSimConfigSource();
            m_config.Source = new IniConfigSource();
            m_config.Source.Merge(DefaultConfig());

            m_log.Info("[CONFIG] Reading configuration settings");

            Uri configUri;
            
            String xmlPath = Path.Combine(Util.configDir(), "Halcyon.xml");

            //check for master .INI file (name passed in command line, no default), or XML over http
            if (!String.IsNullOrEmpty(masterFileName)) // If a master file name is given ...
            {
                m_log.InfoFormat("[CONFIG] Reading config master file {0}", masterfilePath);

                bool isMasterUri = Uri.TryCreate(masterFileName, UriKind.Absolute, out configUri) &&
                                   configUri.Scheme == Uri.UriSchemeHttp;

                if (!ReadConfig(masterFileName, masterfilePath, m_config, isMasterUri))
                {
                    m_log.FatalFormat("[CONFIG] Could not open master config file {0}", masterfilePath);
                }
            }

            if (Directory.Exists(iniDirName))
            {
                m_log.InfoFormat("Searching folder: {0} , for config ini files", iniDirName);
                string[] fileEntries = Directory.GetFiles(iniDirName);
                foreach (string filePath in fileEntries)
                {
                    if (Path.GetExtension(filePath).ToLower() == ".ini")
                    {
                        // m_log.InfoFormat("reading ini file < {0} > from config dir", filePath);
                        ReadConfig(Path.GetFileName(filePath), filePath, m_config, false);
                    }
                }
            }

            // Check for .INI file (either default or name passed on command
            // line) or XML config source over http
            bool isIniUri = Uri.TryCreate(iniFileName, UriKind.Absolute, out configUri) &&
                            configUri.Scheme == Uri.UriSchemeHttp;
            iniFileExists = ReadConfig(iniFileName, ApplicationBase.iniFilePath, m_config, isIniUri);

            if (!iniFileExists)
            {
                // check for a xml config file                                
                if (File.Exists(xmlPath))
                {
                    ApplicationBase.iniFilePath = xmlPath;

                    m_log.InfoFormat("Reading XML configuration from {0}", Path.GetFullPath(xmlPath));
                    iniFileExists = true;

                    m_config.Source = new XmlConfigSource();
                    m_config.Source.Merge(new XmlConfigSource(ApplicationBase.iniFilePath));
                }
            }

            m_config.Source.Merge(configSource);

            if (!iniFileExists)
            {
                m_log.FatalFormat("[CONFIG] Could not load any configuration");
                if (!isIniUri)
                    m_log.FatalFormat("[CONFIG] Tried to load {0}, ", Path.GetFullPath(ApplicationBase.iniFilePath));
                else
                    m_log.FatalFormat("[CONFIG] Tried to load from URI {0}, ", iniFileName);
                m_log.FatalFormat("[CONFIG] and XML source {0}", Path.GetFullPath(xmlPath));

                string sampleName = Path.GetFileNameWithoutExtension(ApplicationBase.iniFilePath) + ".sample.ini";

                m_log.FatalFormat("[CONFIG] Did you copy the {0} file to {1}?", sampleName, ApplicationBase.iniFilePath);
                Environment.Exit(1);
            }

            ReadConfigSettings();

            return m_config;
        }

        /// <summary>
        /// Provide same ini loader functionality for standard ini and master ini - file system or XML over http
        /// </summary>
        /// <param name="iniName">The name of the ini to load</param>
        /// <param name="iniPath">Full path to the ini</param>
        /// <param name="m_config">The current configuration source</param>
        /// <param name="isUri">Boolean representing whether the ini source is a URI path over http or a file on the system</param>
        /// <returns></returns>
        private bool ReadConfig(string iniName, string iniPath, OpenSimConfigSource m_config, bool isUri)
        {
            bool success = false;

            if (!isUri && File.Exists(iniPath))
            {
                m_log.InfoFormat("[CONFIG] Reading configuration file {0}", Path.GetFullPath(iniPath));

                // From reading Nini's code, it seems that later merged keys replace earlier ones.                
                m_config.Source.Merge(new IniConfigSource(iniPath));
                success = true;
            }
            else
            {
                if (isUri)
                {
                    m_log.InfoFormat("[CONFIG] {0} is a http:// URI, fetching ...", iniName);

                    // The ini file path is a http URI
                    // Try to read it
                    try
                    {
                        XmlReader r = XmlReader.Create(iniName);
                        XmlConfigSource cs = new XmlConfigSource(r);
                        m_config.Source.Merge(cs);

                        success = true;
                        m_log.InfoFormat("[CONFIG] Loaded config from {0}", iniName);
                    }
                    catch (Exception e)
                    {
                        m_log.FatalFormat("[CONFIG] Exception reading config from URI {0}\n" + e.ToString(), iniName);
                        Environment.Exit(1);
                    }
                }
            }
            return success;
        }

        /// <summary>
        /// Setup a default config values in case they aren't present in the ini file
        /// </summary>
        /// <returns></returns>
        public static IConfigSource DefaultConfig()
        {
            IConfigSource defaultConfig = new IniConfigSource();

            {
                IConfig config = defaultConfig.Configs["Startup"];

                if (null == config)
                    config = defaultConfig.AddConfig("Startup");

                config.Set("region_info_source", "filesystem");

                config.Set("gridmode", false);
                config.Set("physics", "basicphysics");
                config.Set("meshing", "Meshmerizer");
                config.Set("physical_prim", true);
                config.Set("see_into_this_sim_from_neighbor", true);
                config.Set("serverside_object_permissions", false);
                config.Set("storage_plugin", "OpenSim.Data.SQLite.dll");
                config.Set("storage_connection_string", "URI=file:OpenSim.db,version=3");
                config.Set("storage_prim_inventories", true);
                config.Set("startup_console_commands_file", String.Empty);
                config.Set("shutdown_console_commands_file", String.Empty);
                config.Set("DefaultScriptEngine", "XEngine");
                config.Set("asset_database", "default");
                config.Set("clientstack_plugin", "OpenSim.Region.ClientStack.LindenUDP.dll");
                // life doesn't really work without this
                config.Set("EventQueue", true);
            }

            {
                IConfig config = defaultConfig.Configs["StandAlone"];

                if (null == config)
                    config = defaultConfig.AddConfig("StandAlone");

                config.Set("accounts_authenticate", true);
                config.Set("welcome_message", "Welcome to Halcyon");
                config.Set("inventory_plugin", "OpenSim.Data.SQLite.dll");
                config.Set("inventory_source", String.Empty);
                config.Set("userDatabase_plugin", "OpenSim.Data.SQLite.dll");
                config.Set("user_source", String.Empty);
                config.Set("asset_plugin", "OpenSim.Data.SQLite.dll");
                config.Set("asset_source", "URI=file:Asset.db,version=3");
                config.Set("LibraryName", "InWorldz Library");
                config.Set("LibrariesXMLFile", string.Format(".{0}inventory{0}Libraries.xml", Path.DirectorySeparatorChar));
                config.Set("AssetSetsXMLFile", string.Format(".{0}assets{0}AssetSets.xml", Path.DirectorySeparatorChar));
                config.Set("dump_assets_to_file", false);
            }

            {
                IConfig config = defaultConfig.Configs["Network"];

                if (null == config)
                    config = defaultConfig.AddConfig("Network");

                config.Set("default_location_x", 1000);
                config.Set("default_location_y", 1000);
                config.Set("http_listener_port", ConfigSettings.DefaultRegionHttpPort);
                config.Set("remoting_listener_port", ConfigSettings.DefaultRegionRemotingPort);
                config.Set("grid_server_url", "http://127.0.0.1:" + ConfigSettings.DefaultGridServerHttpPort.ToString());
                config.Set("grid_send_key", "null");
                config.Set("grid_recv_key", "null");
                config.Set("user_server_url", "http://127.0.0.1:" + ConfigSettings.DefaultUserServerHttpPort.ToString());
                config.Set("user_send_key", "null");
                config.Set("user_recv_key", "null");
                config.Set("asset_server_url", "http://127.0.0.1:" + ConfigSettings.DefaultAssetServerHttpPort.ToString());
                config.Set("inventory_server_url", "http://127.0.0.1:" + ConfigSettings.DefaultInventoryServerHttpPort.ToString());
                config.Set("secure_inventory_server", "true");
            }

            return defaultConfig;
        }

        protected virtual void ReadConfigSettings()
        {
            IConfig startupConfig = m_config.Source.Configs["Startup"];
            IConfig inventoryConfig = m_config.Source.Configs["Inventory"];
            if (startupConfig != null)
            {
                m_configSettings.Standalone = !startupConfig.GetBoolean("gridmode", false);
                m_configSettings.PhysicsEngine = startupConfig.GetString("physics");
                m_configSettings.MeshEngineName = startupConfig.GetString("meshing");
                m_configSettings.PhysicalPrim = startupConfig.GetBoolean("physical_prim", true);

                m_configSettings.See_into_region_from_neighbor = startupConfig.GetBoolean("see_into_this_sim_from_neighbor", true);

                m_configSettings.StorageDll = startupConfig.GetString("storage_plugin");
                if (m_configSettings.StorageDll == "OpenSim.DataStore.MonoSqlite.dll")
                {
                    m_configSettings.StorageDll = "OpenSim.Data.SQLite.dll";
                    m_log.Warn("WARNING: OpenSim.DataStore.MonoSqlite.dll is deprecated. Set storage_plugin to OpenSim.Data.SQLite.dll.");
                    Thread.Sleep(3000);
                }

                m_configSettings.StorageConnectionString 
                    = startupConfig.GetString("storage_connection_string");
                m_configSettings.EstateConnectionString 
                    = startupConfig.GetString("estate_connection_string", m_configSettings.StorageConnectionString);
                m_configSettings.AssetStorage 
                    = startupConfig.GetString("asset_database");
                m_configSettings.AssetCache 
                    = startupConfig.GetString("AssetCache", "OpenSim.Framework.Communications.Cache.AssetCache");
                m_configSettings.ClientstackDll 
                    = startupConfig.GetString("clientstack_plugin", "OpenSim.Region.ClientStack.LindenUDP.dll");
            }

            IConfig standaloneConfig = m_config.Source.Configs["StandAlone"];
            if (standaloneConfig != null)
            {
                m_configSettings.StandaloneAuthenticate = standaloneConfig.GetBoolean("accounts_authenticate", true);
                m_configSettings.StandaloneWelcomeMessage = standaloneConfig.GetString("welcome_message");

                m_configSettings.StandaloneInventoryPlugin = standaloneConfig.GetString("inventory_plugin");
                m_configSettings.StandaloneInventorySource = standaloneConfig.GetString("inventory_source");
                m_configSettings.StandaloneUserPlugin = standaloneConfig.GetString("userDatabase_plugin");
                m_configSettings.StandaloneUserSource = standaloneConfig.GetString("user_source");
                m_configSettings.StandaloneAssetSource = standaloneConfig.GetString("asset_source");

                m_configSettings.LibraryName = standaloneConfig.GetString("LibraryName");
                m_configSettings.LibrariesXMLFile = standaloneConfig.GetString("LibrariesXMLFile");
                m_configSettings.AssetSetsXMLFile = standaloneConfig.GetString("AssetSetsXMLFile");
            }

            m_networkServersInfo.loadFromConfiguration(m_config.Source);

            m_configSettings.CoreConnectionString = startupConfig.GetString("core_connection_string");

            if (inventoryConfig != null)
            {
                // Everything has defaults
                m_configSettings.InventoryPlugin = inventoryConfig.GetString("inventory_plugin", "Halycon.Data.Inventory.MySQL");
                m_configSettings.InventorySource = inventoryConfig.GetString("legacy_inventory_source", m_configSettings.CoreConnectionString);
                m_configSettings.InventorySource = inventoryConfig.GetString("inventory_source", m_configSettings.InventorySource);
                m_configSettings.InventoryMigrationActive = inventoryConfig.GetBoolean("migration_active", false);
                m_configSettings.InventoryCluster = inventoryConfig.GetString("inventory_cluster", String.Empty);
            }
            else
            {
                m_log.Warn("[INVENTORY] New style inventory configuration information not found");
            }

            m_configSettings.SettingsFile = m_config.Source.Configs;
        }
    }
}
