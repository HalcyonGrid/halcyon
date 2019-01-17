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
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Timers;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Console;
using OpenSim.Framework.Statistics;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework;
using System.Runtime;

namespace OpenSim
{
    /// <summary>
    /// Interactive OpenSim region server
    /// </summary>
    public class OpenSim : OpenSimBase
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected string m_startupCommandsFile;
        protected string m_shutdownCommandsFile;
        protected bool m_gui = false;
        protected string m_consoleType = "local";
        protected uint m_consolePort = 0;

        /// <summary>
        /// Prompt to use for simulator command line.
        /// </summary>
        private string m_consolePrompt;

        /// <summary>
        /// Regex for parsing out special characters in the prompt.
        /// </summary>
        private Regex m_consolePromptRegex = new Regex(@"([^\\])\\(\w)", RegexOptions.Compiled);

        private string m_timedScript = "disabled";
        private Timer m_scriptTimer;

        public OpenSim(IConfigSource configSource) : base(configSource)
        {
        }

        protected override void ReadExtraConfigSettings()
        {
            base.ReadExtraConfigSettings();

            IConfig startupConfig = m_config.Source.Configs["Startup"];
            IConfig networkConfig = m_config.Source.Configs["Network"];

            if (startupConfig != null)
            {
                m_startupCommandsFile = startupConfig.GetString("startup_console_commands_file", "startup_commands.txt");
                m_shutdownCommandsFile = startupConfig.GetString("shutdown_console_commands_file", "shutdown_commands.txt");

                if (String.IsNullOrEmpty(startupConfig.GetString("console", String.Empty)))
                    m_gui = startupConfig.GetBoolean("gui", false);
                else
                    m_consoleType= startupConfig.GetString("console", String.Empty);

                if (networkConfig != null)
                    m_consolePort = (uint)networkConfig.GetInt("console_port", 0);

                m_timedScript = startupConfig.GetString("timer_Script", "disabled");
                if (m_logFileAppender != null)
                {
                    if (m_logFileAppender is log4net.Appender.FileAppender)
                    {
                        log4net.Appender.FileAppender appender =
                                (log4net.Appender.FileAppender)m_logFileAppender;
                        string fileName = startupConfig.GetString("LogFile", String.Empty);
                        if (!String.IsNullOrEmpty(fileName))
                            appender.File = fileName;
                        m_log.InfoFormat("[LOGGING] Logging started to file {0}", appender.File);
                    }
                }
            }

            if (Util.FireAndForgetMethod == FireAndForgetMethod.SmartThreadPool)
            {
                Util.InitThreadPool(Util.PoolSelection.Default, 20);
                Util.InitThreadPool(Util.PoolSelection.LongIO, 16);
            }
        }

        /// <summary>
        /// Performs initialization of the scene, such as loading configuration from disk.
        /// </summary>
        protected override void StartupSpecific()
        {
            m_log.Info("====================================================================");
            m_log.Info("========================= STARTING HALCYON =========================");
            m_log.Info("====================================================================");
            m_log.InfoFormat("[HALCYON MAIN]: Running in {0} mode",
                             (ConfigurationSettings.Standalone ? "sandbox" : "grid"));
            m_log.InfoFormat("GC: Server mode: {0}, {1}", GCSettings.IsServerGC.ToString(), GCSettings.LatencyMode.ToString());
            //m_log.InfoFormat("[HALCYON MAIN]: GC Is Server GC: {0}", GCSettings.IsServerGC.ToString());
            // http://msdn.microsoft.com/en-us/library/bb384202.aspx
            //GCSettings.LatencyMode = GCLatencyMode.Batch;
            //m_log.InfoFormat("[HALCYON MAIN]: GC Latency Mode: {0}", GCSettings.LatencyMode.ToString());

            if (m_gui) // Driven by external GUI
            {
                m_console = new CommandConsole("Region");
            }
            else
            {
                switch (m_consoleType)
                {
                case "basic":
                    m_console = new CommandConsole("Region");
                    break;
                case "rest":
                    m_console = new RemoteConsole("Region");
                    ((RemoteConsole)m_console).ReadConfig(m_config.Source);
                    break;
                default:
                    m_console = new LocalConsole("Region");
                    break;
                }
            }

            MainConsole.Instance = m_console;

            RegisterConsoleCommands();

            base.StartupSpecific();

            if (m_console is RemoteConsole)
            {
                if (m_consolePort == 0)
                {
                    ((RemoteConsole)m_console).SetServer(m_httpServer);
                }
                else
                {
                    ((RemoteConsole)m_console).SetServer(MainServer.GetHttpServer(m_consolePort));
                }
            }

            //Run Startup Commands
            if (String.IsNullOrEmpty(m_startupCommandsFile))
            {
                m_log.Info("[STARTUP]: No startup command script specified. Moving on...");
            }
            else
            {
                RunCommandScript(m_startupCommandsFile);
            }

            // Start timer script (run a script every xx seconds)
            if (m_timedScript != "disabled")
            {
                m_scriptTimer = new Timer();
                m_scriptTimer.Enabled = true;
                m_scriptTimer.Interval = 1200*1000;
                m_scriptTimer.Elapsed += RunAutoTimerScript;
            }

            PrintFileToConsole("startuplogo.txt");

            // For now, start at the 'root' level by default
            if (m_sceneManager.Scenes.Count == 1) // If there is only one region, select it
                ChangeSelectedRegion("region",
                                     new string[] {"change", "region", m_sceneManager.Scenes[0].RegionInfo.RegionName});
            else
                ChangeSelectedRegion("region", new string[] {"change", "region", "root"});
        }

        /// <summary>
        /// Register standard set of region console commands
        /// </summary>
        private void RegisterConsoleCommands()
        {
            MainServer.RegisterHttpConsoleCommands(m_console);

            m_console.Commands.AddCommand("base", false, "trust reload",
                                          "trust reload",
                                          "Reloads the trust configuration for the trust manager", HandleTrustReload);

            m_console.Commands.AddCommand("region", false, "clear assets",
                                          "clear assets",
                                          "Clear the asset cache", HandleClearAssets);

            m_console.Commands.AddCommand("region", false, "force update",
                                          "force update",
                                          "Force the update of all objects on clients",
                                          HandleForceUpdate);

            m_console.Commands.AddCommand("region", false, "debug packet",
                                          "debug packet <level>",
                                          "Turn on packet debugging", Debug);

            m_console.Commands.AddCommand("region", false, "debug scene",
                                          "debug scene <cripting> <collisions> <physics>",
                                          "Turn on scene debugging", Debug);

            m_console.Commands.AddCommand("region", false, "debug crossings",
                                          "debug crossings <level>",
                                          "Turn on crossings debugging (0 or 1)", Debug);

            m_console.Commands.AddCommand("region", false, "debug oars",
                                          "debug oars <level>",
                                          "Turn on OAR debugging", Debug);

            m_console.Commands.AddCommand("region", false, "change region",
                                          "change region <region name>",
                                          "Change current console region", ChangeSelectedRegion);

            m_console.Commands.AddCommand("region", false, "save xml",
                                          "save xml",
                                          "Save a region's data in XML format", SaveXml);

            m_console.Commands.AddCommand("region", false, "save xml2",
                                          "save xml2",
                                          "Save a region's data in XML2 format", SaveXml2);

            m_console.Commands.AddCommand("region", false, "load xml",
                                          "load xml [-newIDs [<x> <y> <z>]]",
                                          "Load a region's data from XML format", LoadXml);

            m_console.Commands.AddCommand("region", false, "load xml2",
                                          "load xml2",
                                          "Load a region's data from XML2 format", LoadXml2);

            m_console.Commands.AddCommand("region", false, "save prims xml2",
                                          "save prims xml2 [<prim name> <file name>]",
                                          "Save named prim to XML2", SavePrimsXml2);

            m_console.Commands.AddCommand("region", false, "load oar",
                                          "load oar [--allow-reassign] [--ignore-errors] <oar name>",
                                          "Load a region's data from OAR archive", LoadOar);

            m_console.Commands.AddCommand("region", false, "scan iwoar",
                                          "scan iwoar <oar name>",
                                          "Scan's a region's data for creator IDs of assets from an InWorldz OAR backup", ScanIWOar);

            m_console.Commands.AddCommand("region", false, "load iwoar",
                                          "load iwoar <oar name>",
                                          "Load a region's data from an InWorldz OAR backup, filtering based on opt-in database", LoadIWOar);

            m_console.Commands.AddCommand("region", false, "save oar",
                                          "save oar <oar name> <store_assets>",
                                          "Save a region's data to an OAR archive",
                                          "Store an archive [<store assets> 1 to save assets in the file or 0 to omit]", SaveOar);

            m_console.Commands.AddCommand("region", false, "loadexplicit oar",
                                          "loadexplicit oar <region name> <oar name>",
                                          "Load a region's data from OAR archive", LoadExplicitOar);

            m_console.Commands.AddCommand("region", false, "saveportable oar",
                                          "saveportable oar <oar filename> [allowed_uuid allowed_uuid ...]",
                                          "Save a region's data to an OAR archive with assets suitable for export from inworldz",
                                          "Store an archive [<store assets> 1 to save assets in the file or 0 to omit]", SavePortableOar);

            m_console.Commands.AddCommand("region", false, "saveexplicit oar",
                                          "saveexplicit oar <region name> <oar filename> <store_assets>",
                                          "Save a region's data to an OAR archive",
                                          "Store an archive [<store assets> 1 to save assets in the file or 0 to omit]", SaveExplicitOar);

            m_console.Commands.AddCommand("region", false, "edit scale",
                                          "edit scale <name> <x> <y> <z>",
                                          "Change the scale of a named prim", HandleEditScale);

            m_console.Commands.AddCommand("region", false, "kick user",
                                          "kick user <first> <last> [message]",
                                          "Kick a user off the simulator", KickUserCommand);

            m_console.Commands.AddCommand("region", false, "teleport user",
                                          "teleport user <first> <last> regionName x y z",
                                          "Force-teleport a user to another location", TeleportUserCommand);

            m_console.Commands.AddCommand("region", false, "show assets",
                                          "show assets",
                                          "Show asset data", HandleShow);

            m_console.Commands.AddCommand("region", false, "show users",
                                          "show users [full]",
                                          "Show user data", HandleShow);

            m_console.Commands.AddCommand("region", false, "show remotes",
                                          "show remotes",
                                          "Shows avatar remote presences and their states", HandleShow);

            m_console.Commands.AddCommand("region", false, "show users full",
                                          "show users full",
                                          String.Empty, HandleShow);

            m_console.Commands.AddCommand("region", false, "show modules",
                                          "show modules",
                                          "Show module data", HandleShow);

            m_console.Commands.AddCommand("region", false, "show regions",
                                          "show regions",
                                          "Show region data", HandleShow);

            m_console.Commands.AddCommand("region", false, "show queues",
                                          "show queues",
                                          "Show queue data", HandleShow);
            m_console.Commands.AddCommand("region", false, "show ratings",
                                          "show ratings",
                                          "Show rating data", HandleShow);
            m_console.Commands.AddCommand("region", false, "show owners",
                                          "show owners",
                                          "Show owners of objects", HandleShow);

            m_console.Commands.AddCommand("region", false, "show object",
                                          "show object",
                                          "Show information on object specified by UUID or localID", HandleShow);

            m_console.Commands.AddCommand("region", false, "show collisions",
                                          "show collisions",
                                          "Collects information about current collisions and shows the data", HandleShow);

            m_console.Commands.AddCommand("region", false, "show updates",
                                          "show updates [delta/total]",
                                          "Collects information about objects posting updates", HandleShow);

            m_console.Commands.AddCommand("region", false, "nuke",
                                          "nuke ownerUUID | all",
                                          "Delete all objects owned by the specified UUID or all users", HandleNuke);

            m_console.Commands.AddCommand("region", false, "blacklist object owner",
                                          "blacklist object owner",
                                          "Prevent rezzing of all objects with the specified owner UUID", HandleBlacklistOwner);

            m_console.Commands.AddCommand("region", false, "blacklist object creator",
                                          "blacklist object creator",
                                          "Prevent rezzing of all objects with the specified creator UUID", HandleBlacklistCreator);

            m_console.Commands.AddCommand("region", false, "blacklist object name",
                                          "blacklist object name",
                                          "Prevent rezzing of all objects that start with the the specified partial name", HandleBlacklistName);

            m_console.Commands.AddCommand("region", false, "blacklist user",
                                          "blacklist user",
                                          "Prevent region entry and rezzing of all objects based on the user/owner", HandleBlacklistUser);

            m_console.Commands.AddCommand("region", false, "blacklist remove",
                                          "blacklist remove",
                                          "Remove a previous blacklist entry (uuid or name)", HandleBlacklistRemove);

            m_console.Commands.AddCommand("region", false, "blacklist clear",
                                          "blacklist clear",
                                          "Remove all existing blacklist entries (all types)", HandleBlacklistClear);

            m_console.Commands.AddCommand("region", false, "blacklist show",
                                          "blacklist show",
                                          "Display the existing blacklist entries", HandleBlacklistShow);

            m_console.Commands.AddCommand("region", false, "backup",
                                          "backup",
                                          "Persist objects to the database now", RunCommand);

            m_console.Commands.AddCommand("region", false, "create region",
                                          "create region",
                                          "Create a new region", HandleCreateRegion);

            m_console.Commands.AddCommand("region", false, "login enable",
                                          "login enable",
                                          "Enable logins to the simulator", HandleLoginEnable);

            m_console.Commands.AddCommand("region", false, "login disable",
                                          "login disable",
                                          "Disable logins to the simulator", HandleLoginDisable);

            m_console.Commands.AddCommand("region", false, "login status",
                                          "login status",
                                          "Display status of logins", HandleLoginStatus);

            m_console.Commands.AddCommand("region", false, "restart",
                                          "restart",
                                          "Restart all sims in this instance", RunCommand);

            m_console.Commands.AddCommand("region", false, "config set",
                                          "config set <section> <field> <value>",
                                          "Set a config option", HandleConfig);

            m_console.Commands.AddCommand("region", false, "config get",
                                          "config get <section> <field>",
                                          "Read a config option", HandleConfig);

            m_console.Commands.AddCommand("region", false, "config save",
                                          "config save",
                                          "Save current configuration", HandleConfig);

            m_console.Commands.AddCommand("region", false, "command-script",
                                          "command-script <script>",
                                          "Run a command script from file", RunCommand);

            m_console.Commands.AddCommand("region", false, "remove-region",
                                          "remove-region <name>",
                                          "Remove a region from this simulator", RunCommand);

            m_console.Commands.AddCommand("region", false, "delete-region",
                                          "delete-region <name>",
                                          "Delete a region from disk", RunCommand);

            m_console.Commands.AddCommand("region", false, "predecode-j2k",
                                          "predecode-j2k [<num threads>]>",
                                          "Precache assets,decode j2k layerdata", RunCommand);

            m_console.Commands.AddCommand("region", false, "modules list",
                                          "modules list",
                                          "List modules", HandleModules);

            m_console.Commands.AddCommand("region", false, "modules load",
                                          "modules load <name>",
                                          "Load a module", HandleModules);

            m_console.Commands.AddCommand("region", false, "modules unload",
                                          "modules unload <name>",
                                          "Unload a module", HandleModules);

            m_console.Commands.AddCommand("region", false, "Add-InventoryHost",
                                          "Add-InventoryHost <host>",
                                          String.Empty, RunCommand);

            if (ConfigurationSettings.Standalone)
            {
                m_console.Commands.AddCommand("region", false, "create user",
                                              "create user [<first> [<last> [<pass> [<x> <y> [<email>]]]]]",
                                              "Create a new user", HandleCreateUser);

                m_console.Commands.AddCommand("region", false, "reset user password",
                                              "reset user password [<first> [<last> [<password>]]]",
                                              "Reset a user password", HandleResetUserPassword);
            }

            m_console.Commands.AddCommand("region", false, "get script status",
                                          "get script status [itemid]",
                                          "Returns information about the run state of the given script", GetScriptStatus);

            m_console.Commands.AddCommand("region", false, "trace script",
                                          "trace script [itemid]",
                                          "Returns information about the run state of the given script", TraceScript);

            m_console.Commands.AddCommand("region", false, "trace disable all",
                                          "trace disable all",
                                          "Disables all script tracing", DisableAllTraces);

        }

        public override void ShutdownSpecific()
        {
            if (!String.IsNullOrEmpty(m_shutdownCommandsFile))
            {
                RunCommandScript(m_shutdownCommandsFile);
            }
            base.ShutdownSpecific();
        }

        private void RunAutoTimerScript(object sender, EventArgs e)
        {
            if (m_timedScript != "disabled")
            {
                RunCommandScript(m_timedScript);
            }
        }

        #region Console Commands

        private void KickUserCommand(string module, string[] cmdparams)
        {
            if (cmdparams.Length < 4)
                return;

            string alert = null;
            if (cmdparams.Length > 4)
                alert = String.Format("\n{0}\n", String.Join(" ", cmdparams, 4, cmdparams.Length - 4));

            IList agents = m_sceneManager.GetCurrentSceneAvatars();

            foreach (ScenePresence presence in agents)
            {
                RegionInfo regionInfo = m_sceneManager.GetRegionInfo(presence.RegionHandle);

                if (presence.Firstname.ToLower().Contains(cmdparams[2].ToLower()) &&
                    presence.Lastname.ToLower().Contains(cmdparams[3].ToLower()))
                {
                    m_console.Notice(
                        String.Format(
                            "Kicking user: {0,-16}{1,-16}{2,-37} in region: {3,-16}",
                            presence.Firstname, presence.Lastname, presence.UUID, regionInfo.RegionName));

                    // kick client...
                    if (alert != null)
                        presence.ControllingClient.Kick(alert);
                    else 
                        presence.ControllingClient.Kick("\nThe grid manager kicked you out.\n");

                    // ...and close on our side
                    presence.Scene.IncomingCloseAgent(presence.UUID);
                }
            }
            m_console.Notice(String.Empty);
        }

        private void TeleportUserCommand(string module, string[] cmdparams)
        {
            if (cmdparams.Length != 8)
            {
                m_console.Notice("teleport user <first> <last> regionName x y z");
                return;
            }

            string destreg = cmdparams[4];
            Vector3 destpos = new Vector3(128, 128, 25);
            try
            {
                destpos.X = Convert.ToUInt32(cmdparams[5]);
                destpos.Y = Convert.ToUInt32(cmdparams[6]);
                destpos.Z = Convert.ToUInt32(cmdparams[7]);
            }
            catch(Exception)
            {
                m_console.Notice("teleport user <first> <last> regionName x y z");
                return;
            }

            IList agents = m_sceneManager.GetCurrentSceneAvatars();

            foreach (ScenePresence presence in agents)
            {
                RegionInfo regionInfo = m_sceneManager.GetRegionInfo(presence.RegionHandle);

                if (presence.Firstname.ToLower().Contains(cmdparams[2].ToLower()) &&
                    presence.Lastname.ToLower().Contains(cmdparams[3].ToLower()))
                {
                    m_console.Notice(
                        String.Format(
                            "Force-teleporting user: {0,-16}{1,-16}{2,-37} in region: {3,-16}",
                            presence.Firstname, presence.Lastname, presence.UUID, regionInfo.RegionName));

                    // force-teleport client...
                    presence.ControllingClient.SendAlertMessage("The grid manager has teleported you to another location.");
                    if (destreg != presence.Scene.RegionInfo.RegionName) // diff region?
                        presence.ControllingClient.SendTeleportLocationStart();
                    presence.Scene.RequestTeleportLocation(presence.ControllingClient, destreg, destpos, presence.Lookat, (uint)TeleportFlags.ViaLocation);
                }
            }
        }

        /// <summary>
        /// Run an optional startup list of commands
        /// </summary>
        /// <param name="fileName"></param>
        private void RunCommandScript(string fileName)
        {
            if (File.Exists(fileName))
            {
                m_log.Info("[COMMANDFILE]: Running " + fileName);

                StreamReader readFile = File.OpenText(fileName);
                string currentCommand;
                while ((currentCommand = readFile.ReadLine()) != null)
                {
                    if (!String.IsNullOrEmpty(currentCommand))
                    {
                        m_log.Info("[COMMANDFILE]: Running '" + currentCommand + "'");
                        m_console.RunCommand(currentCommand);
                    }
                }
            }
        }

        private static void PrintFileToConsole(string fileName)
        {
            if (File.Exists(fileName))
            {
                StreamReader readFile = File.OpenText(fileName);
                string currentLine;
                while ((currentLine = readFile.ReadLine()) != null)
                {
                    m_log.Info("[!]" + currentLine);
                }
            }
        }

        private void HandleTrustReload(string module, string[] args)
        {
            TrustManager.Instance.ReloadTrustLists();
        }

        private void HandleClearAssets(string module, string[] args)
        {
            //NOP
        }

        private void HandleForceUpdate(string module, string[] args)
        {
            m_console.Notice("Updating all clients");
            m_sceneManager.ForceCurrentSceneClientUpdate();
        }

        private void HandleEditScale(string module, string[] args)
        {
            if (args.Length == 6)
            {
                m_sceneManager.HandleEditCommandOnCurrentScene(args);
            }
            else
            {
                m_console.Notice("Argument error: edit scale <prim name> <x> <y> <z>");
            }
        }

        private void HandleCreateRegion(string module, string[] cmd)
        {
            if (cmd.Length < 4 || !cmd[3].EndsWith(".xml"))
            {
                m_console.Error("Usage: create region <region name> <region_file.xml>");
                return;
            }

            string regionsDir = ConfigSource.Source.Configs["Startup"].GetString("regionload_regionsdir", "Regions").Trim();
            string regionFile = String.Format("{0}/{1}", regionsDir, cmd[3]);
            // Allow absolute and relative specifiers
            if (cmd[3].StartsWith("/") || cmd[3].StartsWith("\\") || cmd[3].StartsWith(".."))
                regionFile = cmd[3];

            IScene scene;
            CreateRegion(new RegionInfo(cmd[2], regionFile, false, ConfigSource.Source), true, out scene);
        }

        private void HandleLoginEnable(string module, string[] cmd)
        {
            ProcessLogin(true);
        }

        private void HandleLoginDisable(string module, string[] cmd)
        {
            ProcessLogin(false);
        }

        private void HandleLoginStatus(string module, string[] cmd)
        {
            if (m_commsManager.GridService.RegionLoginsEnabled == false)

                m_log.Info("[ Login ]  Login are disabled ");
            else
                m_log.Info("[ Login ]  Login are enabled");
        }

        private void HandleConfig(string module, string[] cmd)
        {
            List<string> args = new List<string>(cmd);
            args.RemoveAt(0);
            string[] cmdparams = args.ToArray();
            string n = "CONFIG";

            if (cmdparams.Length > 0)
            {
                switch (cmdparams[0].ToLower())
                {
                    case "set":
                        if (cmdparams.Length < 4)
                        {
                            m_console.Error(n, "SYNTAX: " + n + " SET SECTION KEY VALUE");
                        }
                        else
                        {
                            // IConfig c = DefaultConfig().Configs[cmdparams[1]];
                            // if (c == null)
                            //     c = DefaultConfig().AddConfig(cmdparams[1]);
                            IConfig c;
                            IConfigSource source = new IniConfigSource();
                            c = source.AddConfig(cmdparams[1]);
                            if (c != null)
                            {
                                string _value = String.Join(" ", cmdparams, 3, cmdparams.Length - 3);
                                c.Set(cmdparams[2], _value);
                                m_config.Source.Merge(source);

                                m_console.Error(n, n + " " + n + " " + cmdparams[1] + " " + cmdparams[2] + " " +
                                                   _value);
                            }
                        }
                        break;

                    case "get":
                        if (cmdparams.Length < 3)
                        {
                            m_console.Error(n, "SYNTAX: " + n + " GET SECTION KEY");
                            m_console.Error(n, "EXAMPLE: " + n + " GET ScriptEngine.DotNetEngine NumberOfScriptThreads");
                        }
                        else
                        {
                            IConfig c = m_config.Source.Configs[cmdparams[1]]; // DefaultConfig().Configs[cmdparams[1]];
                            if (c == null)
                            {
                                m_console.Notice(n, "Section \"" + cmdparams[1] + "\" does not exist.");
                                break;
                            }
                            else
                            {
                                m_console.Notice(n + " GET " + cmdparams[1] + " " + cmdparams[2] + ": " +
                                                 c.GetString(cmdparams[2]));
                            }
                        }

                        break;

                    case "save":
                        m_console.Notice("Saving configuration file: " + ApplicationBase.iniFilePath);
                        m_config.Save(ApplicationBase.iniFilePath);
                        break;
                }
            }
        }

        private void HandleModules(string module, string[] cmd)
        {
            List<string> args = new List<string>(cmd);
            args.RemoveAt(0);
            string[] cmdparams = args.ToArray();

            if (cmdparams.Length > 0)
            {
                switch (cmdparams[0].ToLower())
                {
                    case "list":
                        foreach (IRegionModule irm in m_moduleLoader.GetLoadedSharedModules)
                        {
                            m_console.Notice("Shared region module: " + irm.Name);
                        }
                        break;
                    case "unload":
                        if (cmdparams.Length > 1)
                        {
                            foreach (IRegionModule rm in new ArrayList(m_moduleLoader.GetLoadedSharedModules))
                            {
                                if (rm.Name.ToLower() == cmdparams[1].ToLower())
                                {
                                    m_console.Notice("Unloading module: " + rm.Name);
                                    m_moduleLoader.UnloadModule(rm);
                                }
                            }
                        }
                        break;
                    case "load":
                        if (cmdparams.Length > 1)
                        {
                            foreach (Scene s in new ArrayList(m_sceneManager.Scenes))
                            {
                                m_console.Notice("Loading module: " + cmdparams[1]);
                                m_moduleLoader.LoadRegionModules(cmdparams[1], s);
                            }
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Runs commands issued by the server console from the operator
        /// </summary>
        /// <param name="command">The first argument of the parameter (the command)</param>
        /// <param name="cmdparams">Additional arguments passed to the command</param>
        public void RunCommand(string module, string[] cmdparams)
        {
            List<string> args = new List<string>(cmdparams);
            if (args.Count < 1)
                return;

            string command = args[0];
            args.RemoveAt(0);

            cmdparams = args.ToArray();

            switch (command)
            {
                case "command-script":
                    if (cmdparams.Length > 0)
                    {
                        RunCommandScript(cmdparams[0]);
                    }
                    break;

                case "backup":
                    m_sceneManager.BackupCurrentScene();
                    break;

                case "remove-region":
                    string regRemoveName = CombineParams(cmdparams, 0);

                    Scene removeScene;
                    if (m_sceneManager.TryGetScene(regRemoveName, out removeScene))
                        RemoveRegion(removeScene, false);
                    else
                        m_console.Error("no region with that name");
                    break;

                case "delete-region":
                    string regDeleteName = CombineParams(cmdparams, 0);

                    Scene killScene;
                    if (m_sceneManager.TryGetScene(regDeleteName, out killScene))
                        RemoveRegion(killScene, true);
                    else
                        m_console.Error("no region with that name");
                    break;

                case "restart":
                    // Let's issue a full server shutdown and let the management tools take care of the restart.
                    // Use a status of 63 (0b00111111) to indicate that this "error" is an explicit restart request and not a real error.
                    Shutdown(63);
                    break;

                case "predecode-j2k":
                    if (cmdparams.Length > 0)
                    {
                        m_sceneManager.CacheJ2kDecode(Convert.ToInt32(cmdparams[0]));
                    }
                    else
                    {
                        m_sceneManager.CacheJ2kDecode(1);
                    }
                    break;
            }
        }

        /// <summary>
        /// Change the currently selected region.  The selected region is that operated upon by single region commands.
        /// </summary>
        /// <param name="cmdParams"></param>
        protected void ChangeSelectedRegion(string module, string[] cmdparams)
        {
            if (cmdparams.Length > 2)
            {
                string newRegionName = CombineParams(cmdparams, 2);

                if (!m_sceneManager.TrySetCurrentScene(newRegionName))
                    m_console.Error("Couldn't select region " + newRegionName);
            }
            else
            {
                m_console.Error("Usage: change region <region name>");
            }

            string regionName = (m_sceneManager.CurrentScene == null ? "root" : m_sceneManager.CurrentScene.RegionInfo.RegionName);
            m_console.Notice(String.Format("Currently selected region is {0}", regionName));
            m_console.DefaultPrompt = String.Format("Region ({0}) ", regionName);
            m_console.ConsoleScene = m_sceneManager.CurrentScene;
        }

        /// <summary>
        /// Execute switch for some of the create commands
        /// </summary>
        /// <param name="args"></param>
        private void HandleCreateUser(string module, string[] cmd)
        {
            if (ConfigurationSettings.Standalone)
            {
                CreateUser(cmd);
            }
            else
            {
                m_console.Notice("Create user is not available in grid mode, use the user server.");
            }
        }

        /// <summary>
        /// Execute switch for some of the reset commands
        /// </summary>
        /// <param name="args"></param>
        protected void HandleResetUserPassword(string module, string[] cmd)
        {
            if (ConfigurationSettings.Standalone)
            {
                ResetUserPassword(cmd);
            }
            else
            {
                m_console.Notice("Reset user password is not available in grid mode, use the user-server.");
            }
        }

        /// <summary>
        /// Turn on some debugging values for OpenSim.
        /// </summary>
        /// <param name="args"></param>
        protected void Debug(string module, string[] args)
        {
            if (args.Length == 1)
                return;

            switch (args[1])
            {
                case "packet":
                    if (args.Length > 2)
                    {
                        int newDebug;
                        if (int.TryParse(args[2], out newDebug))
                        {
                            m_sceneManager.SetDebugPacketLevelOnCurrentScene(newDebug);
                        }
                        else
                        {
                            m_console.Error("packet debug should be 0..255");
                        }
                        m_console.Notice("New packet debug: " + newDebug.ToString());
                    }

                    break;

                case "scene":
                    if (args.Length == 5)
                    {
                        if (m_sceneManager.CurrentScene == null)
                        {
                            m_console.Notice("Please use 'change region <regioname>' first");
                        }
                        else
                        {
                            bool scriptingOn = !Convert.ToBoolean(args[2]);
                            bool collisionsOn = !Convert.ToBoolean(args[3]);
                            bool physicsOn = !Convert.ToBoolean(args[4]);
                            m_sceneManager.CurrentScene.SetSceneCoreDebug(scriptingOn, collisionsOn, physicsOn);

                            m_console.Notice(
                                "CONSOLE",
                                String.Format(
                                    "Set debug scene scripting = {0}, collisions = {1}, physics = {2}",
                                    !scriptingOn, !collisionsOn, !physicsOn));
                        }
                    }
                    else
                    {
                        m_console.Error("debug scene <scripting> <collisions> <physics> (where inside <> is true/false)");
                    }

                    break;

                case "crossings":
                    if (args.Length > 2)
                    {
                        int newDebug;
                        if (int.TryParse(args[2], out newDebug))
                        {
                            if ((newDebug >= 0) && (newDebug <= 1))
                            {
                                m_sceneManager.SetDebugCrossingsLevelOnCurrentScene(newDebug);
                                m_console.Notice("New crossings debug: " + newDebug.ToString());
                                break;
                            }
                        }
                        m_console.Error("crossings debug should be 0 or 1");
                    }

                    break;

                case "oars":
                    if (args.Length > 2)
                    {
                        int newDebug;
                        if (int.TryParse(args[2], out newDebug))
                        {
                            m_sceneManager.SetOARDebug(newDebug);
                        }
                        else
                        {
                            m_console.Error("packet debug should be 0..255");
                        }
                        m_console.Notice("New OAR debug: " + newDebug.ToString());
                    }

                    break;

                default:
                    m_console.Error("Unknown debug");
                    break;
            }
        }

        // see BaseOpenSimServer
        public override void HandleShow(string mod, string[] cmd)
        {
            base.HandleShow(mod, cmd);

            List<string> args = new List<string>(cmd);
            args.RemoveAt(0);
            string[] showParams = args.ToArray();

            switch (showParams[0])
            {
                case "assets":
                    bool resetStats = (showParams.Length > 1) && (showParams[1] == "reset");
                    m_assetCache.ShowState(resetStats);
                    break;

                case "users":
                    IBotManager botManager = m_sceneManager.CurrentOrFirstScene.RequestModuleInterface<IBotManager>();
                    IList agents;
                    bool isFullList = (showParams.Length > 1 && showParams[1] == "full");
                    if (isFullList)
                    {
                        agents = m_sceneManager.GetCurrentScenePresences();
                    }
                    else
                    {
                        agents = m_sceneManager.GetCurrentSceneAvatars();
                    }

                    m_console.Notice(
                        String.Format("{0,-16}{1,-16}{2,-37}{3}", 
                                "Firstname", "Lastname", "Agent ID", "Type"));
                    int nRoot = 0;
                    int nChild = 0;
                    int nBot = 0;
                    foreach (ScenePresence presence in agents)
                    {
                        bool isBot = false;
                        if (botManager != null)
                            isBot = botManager.IsBot(presence.UUID);

                        if (isFullList || !isBot)
                        {
                            m_console.Notice(
                                String.Format(
                                    "{0,-16}{1,-16}{2,-37}{3}",
                                    presence.Firstname,
                                    presence.Lastname,
                                    presence.UUID,
                                    isBot ? "Bot" : presence.IsChildAgent ? "Child" : "Root"));

                            if (isBot)
                                nBot++;
                            else
                            if (presence.IsChildAgent)
                                nChild++;
                            else
                                nRoot++;
                        }
                    }
                    if (isFullList)
                        m_console.Notice(String.Format("Total agents: {0},  Root: {1}, Bot: {2}, Child: {3}", agents.Count, nRoot, nBot, nChild));
                    else
                        m_console.Notice(String.Format("Agents connected: {0}", nRoot));

                    m_console.Notice(String.Empty);
                    break;

                case "remotes":
                    DoShowRemotes();
                    break;

                case "modules":
                    m_console.Notice("The currently loaded shared modules are:");
                    foreach (IRegionModule module in m_moduleLoader.GetLoadedSharedModules)
                    {
                        m_console.Notice("Shared Module: " + module.Name);
                    }
                    break;

                case "regions":
                    m_sceneManager.ForEachScene(
                        delegate(Scene scene)
                            {
                                m_console.Notice("Region Name: " + scene.RegionInfo.RegionName + " , Region XLoc: " +
                                                 scene.RegionInfo.RegionLocX + " , Region YLoc: " +
                                                 scene.RegionInfo.RegionLocY + " , Region Port: " +
                                                 scene.RegionInfo.InternalEndPoint.Port.ToString());
                            });
                    break;

                case "queues":
                    Notice(GetQueuesReport());
                    break;

                case "ratings":
                    m_sceneManager.ForEachScene(
                    delegate(Scene scene)
                    {
                        string rating = String.Empty;
                        if (scene.RegionInfo.RegionSettings.Maturity == 1)
                        {
                            rating = "MATURE";
                        }
                        else if (scene.RegionInfo.RegionSettings.Maturity == 2)
                        {
                            rating = "ADULT";
                        }
                        else
                        {
                            rating = "PG";
                        }
                        m_console.Notice("Region Name: " + scene.RegionInfo.RegionName + " , Region Rating: " +
                                         rating);
                    });
                    break;
                case "owners":
                    int MinCount = 1;
                    if (showParams.Length > 1)
                    {
                        try { MinCount = Convert.ToInt32(showParams[1]); }
                        catch { 
                            m_console.Notice("Invalid minimum threshold count for prims."); 
                            break; 
                        }
                    }
                    Dictionary<UUID,SceneOwnerCounts> counts = m_sceneManager.GetCurrentSceneOwnerCounts();

                    m_console.Notice(String.Format("Objects Prims UUID Name {0}",MinCount>1?"(with prim counts above "+MinCount.ToString()+")" : String.Empty));
                    foreach (KeyValuePair<UUID, SceneOwnerCounts> kvp in counts)
                    {
                        SceneOwnerCounts count = kvp.Value;
                        if (count.TotalPrims >= MinCount)
                            m_console.Notice(String.Format("{0} {1} {2} {3}", count.TotalObjects, count.TotalPrims, kvp.Key.ToString(), count.OwnerName));
                    }
                    break;

                case "object":
                    m_sceneManager.ShowObject(showParams);
                    break;

                case "collisions":
                    m_sceneManager.ShowCollisions(showParams);
                    break;

                case "updates":
                    m_sceneManager.ShowUpdates(showParams);
                    break;
            }
        }

        private void DoShowRemotes()
        {
            IBotManager botManager = m_sceneManager.CurrentOrFirstScene.RequestModuleInterface<IBotManager>();
            IList agents = m_sceneManager.GetCurrentSceneAvatars();

            foreach (ScenePresence presence in agents)
            {
                bool isBot = false;
                if (botManager != null)
                    isBot = botManager.IsBot(presence.UUID);

                if (!isBot)
                {
                    m_console.Notice(presence.Name + ":");

                    List<AvatarRemotePresence> presences = presence.RemotePresences.GetRemotePresenceList();
                    
                    foreach (var remote in presences)
                    {
                        m_console.Notice(
                            String.Format(
                                "  {0} {1} {2}",
                                remote.PresenceInfo.RegionInfo.RegionHandle,
                                remote.PresenceInfo.RegionInfo.ExternalHostName,
                                remote.State
                                ));
                    }

                }
            }

            m_console.Notice(String.Empty);
        }

        public void DisableAllTraces(string mod, string[] cmd)
        {
            IScriptModule module = m_sceneManager.CurrentOrFirstScene.RequestModuleInterface<IScriptModule>();

            if (module != null)
            {
                module.DisableScriptTraces();
            }
        }

        public void TraceScript(string mod, string[] cmd)
        {

        }

        public void GetScriptStatus(string mod, string[] cmd)
        {
            if (cmd.Length < 4) return;

            UUID itemId;
            if (UUID.TryParse(cmd[3], out itemId))
            {
                IScriptModule module = m_sceneManager.CurrentOrFirstScene.RequestModuleInterface<IScriptModule>();

                if (module != null)
                {
                    ScriptRuntimeInformation runtimeInfo = module.GetScriptInformation(itemId);

                    m_console.Output(String.Format("Script Information for Item {0}", itemId));
                    m_console.Output(String.Format("RunState: {0}", runtimeInfo.CurrentState));
                    m_console.Output(String.Format("Memory used: {0}", runtimeInfo.MemoryUsed));
                    m_console.Output(String.Format("Next Wakeup: {0}", runtimeInfo.NextWakeup));
                    m_console.Output(String.Format("Current Event: {0}", runtimeInfo.CurrentEvent == null ? "none" : runtimeInfo.CurrentEvent));
                    m_console.Output(String.Format("Timer Interval: {0}", runtimeInfo.TimerInterval));
                    m_console.Output(String.Format("Timer Last Scheduled: {0}", runtimeInfo.TimerLastScheduledOn));
                    m_console.Output(String.Format("Current Function: {0}", runtimeInfo.StackFrameFunctionName == null ? "none" : runtimeInfo.StackFrameFunctionName));
                    m_console.Output(String.Empty);
                }
            }
        }

        // see BaseOpenSimServer
        public override void HandleNuke(string mod, string[] cmd)
        {
            base.HandleNuke(mod, cmd);

            List<string> args = new List<string>(cmd);
            args.RemoveAt(0);
            string[] showParams = args.ToArray();

            if (showParams.Length > 0)
            {
                UUID OwnerID;
                if (showParams[0] == "all")
                {
                    m_sceneManager.NukeObjectsOwnedBy(UUID.Zero);
                }
                else
                if (UUID.TryParse(showParams[0], out OwnerID) && (OwnerID != UUID.Zero))
                {
                    m_sceneManager.NukeObjectsOwnedBy(OwnerID);
                } else
                {
                    m_console.Notice("That does not look like a valid UUID.");
                    return;
                }
                m_console.Notice("Nuke complete.");
            }
            else
            {
                m_console.Notice("You must specify the UUID of the owner, or 'all'.");
            }
        }

        // blacklist object owner <UUID>
        public override void HandleBlacklistOwner(string module, string[] cmd)
        {
            base.HandleBlacklistOwner(module, cmd);

            List<string> tokens = new List<string>(cmd);
            tokens.RemoveAt(0);
            string[] args = tokens.ToArray();

            if (args.Length > 2)
            {
                string param2 = (args.Length > 3) ? args[3] : String.Empty;
                SceneManager.BlacklistOp operation = SceneManager.BlacklistOp.Owner;
                m_sceneManager.BlacklistOperation(operation, args[2], param2);
            }
            else
            {
                m_console.Notice("You must specify the UUID or name of the owner.");
            }
        }
        // blacklist object creator <UUID>
        public override void HandleBlacklistCreator(string module, string[] cmd)
        {
            base.HandleBlacklistCreator(module, cmd);

            List<string> tokens = new List<string>(cmd);
            tokens.RemoveAt(0);
            string[] args = tokens.ToArray();

            if (args.Length > 2)
            {
                string param2 = (args.Length > 3) ? args[3] : String.Empty;
                SceneManager.BlacklistOp operation = SceneManager.BlacklistOp.Creator;
                SceneManager.BlacklistOperation(operation, args[2], param2);
            }
            else
            {
                m_console.Notice("You must specify the UUID or name of the creator.");
            }
        }
        // blacklist object name "<name>"
        public override void HandleBlacklistName(string module, string[] cmd)
        {
            base.HandleBlacklistName(module, cmd);

            List<string> tokens = new List<string>(cmd);
            tokens.RemoveAt(0);
            string[] args = tokens.ToArray();

            if (args.Length > 2)
            {
                SceneManager.BlacklistOp operation = SceneManager.BlacklistOp.Name;
                m_sceneManager.BlacklistOperation(operation, args[2], String.Empty);
            }
            else
            {
                m_console.Notice("You must specify a starting name portion.");
            }
        }
        // Either: blacklist user <UUID>
        //     or: blacklist user <First> <Last>
        public override void HandleBlacklistUser(string module, string[] cmd)
        {
            // blacklist object owner <UUID>
            base.HandleBlacklistUser(module, cmd);

            List<string> tokens = new List<string>(cmd);
            tokens.RemoveAt(0);
            string[] args = tokens.ToArray();

            if (args.Length > 1)
            {
                string param2 = (args.Length > 2) ? args[2] : String.Empty;
                SceneManager.BlacklistOp operation = SceneManager.BlacklistOp.User;
                m_sceneManager.BlacklistOperation(operation, args[1], param2);
            }
            else
            {
                m_console.Notice("You must specify the UUID or name of the user.");
            }
        }
        // blacklist remove <something>
        public override void HandleBlacklistRemove(string module, string[] cmd)
        {
            base.HandleBlacklistRemove(module, cmd);

            List<string> tokens = new List<string>(cmd);
            tokens.RemoveAt(0);
            string[] args = tokens.ToArray();

            if (args.Length > 1)
            {
                SceneManager.BlacklistOp operation = SceneManager.BlacklistOp.Remove;
                m_sceneManager.BlacklistOperation(operation, args[1], String.Empty);
            }
            else
            {
                m_console.Notice("You must specify the user (UUID or name).");
            }
        }
        // blacklist clear
        public override void HandleBlacklistClear(string module, string[] cmd)
        {
            base.HandleBlacklistClear(module, cmd);

            SceneManager.BlacklistOp operation = SceneManager.BlacklistOp.Clear;
            m_sceneManager.BlacklistOperation(operation, String.Empty, String.Empty);
        }
        // blacklist show
        public override void HandleBlacklistShow(string module, string[] cmd)
        {
            base.HandleBlacklistShow(module, cmd);

            SceneManager.BlacklistOp operation = SceneManager.BlacklistOp.Show;
            m_sceneManager.BlacklistOperation(operation, String.Empty, String.Empty);
        }

        private string GetQueuesReport()
        {
            string report = String.Empty;

            m_sceneManager.ForEachScene(delegate(Scene scene)
                                            {
                                                scene.ForEachClient(delegate(IClientAPI client)
                                                                        {
                                                                            if (client is IStatsCollector)
                                                                            {
                                                                                report = report + client.Name + "\n";

                                                                                IStatsCollector stats =
                                                                                    (IStatsCollector) client;

                                                                                report = report + string.Format("{0,7} {1,7} {2,7} {3,7} {4,7} {5,7} {6,7} {7,7} {8,7} {9,7}\n",
                                                                                             "Send",
                                                                                             "In",
                                                                                             "Out",
                                                                                             "Resend",
                                                                                             "Land",
                                                                                             "Wind",
                                                                                             "Cloud",
                                                                                             "Task",
                                                                                             "Texture",
                                                                                             "Asset");
                                                                                report = report + stats.Report() +
                                                                                         "\n\n";
                                                                            }
                                                                        });
                                            });

            return report;
        }

        /// <summary>
        /// Create a new user
        /// </summary>
        /// <param name="cmdparams">string array with parameters: firstname, lastname, password, locationX, locationY, email</param>
        protected void CreateUser(string[] cmdparams)
        {
            string firstName;
            string lastName;
            string password;
            string email;
            uint regX = 1000;
            uint regY = 1000;

            if (cmdparams.Length < 3)
                firstName = MainConsole.Instance.CmdPrompt("First name", "Default");
            else firstName = cmdparams[2];

            if (cmdparams.Length < 4)
                lastName = MainConsole.Instance.CmdPrompt("Last name", "User");
            else lastName = cmdparams[3];

            if (cmdparams.Length < 5)
                password = MainConsole.Instance.PasswdPrompt("Password");
            else password = cmdparams[4];

            if (cmdparams.Length < 6)
                regX = Convert.ToUInt32(MainConsole.Instance.CmdPrompt("Start Region X", regX.ToString()));
            else regX = Convert.ToUInt32(cmdparams[5]);

            if (cmdparams.Length < 7)
                regY = Convert.ToUInt32(MainConsole.Instance.CmdPrompt("Start Region Y", regY.ToString()));
            else regY = Convert.ToUInt32(cmdparams[6]);

            if (cmdparams.Length < 8)
                email = MainConsole.Instance.CmdPrompt("Email", String.Empty);
            else email = cmdparams[7];

            if (null == m_commsManager.UserService.GetUserProfile(firstName, lastName))
            {
                m_commsManager.UserAdminService.AddUser(firstName, lastName, password, email, regX, regY);
            }
            else
            {
                m_log.ErrorFormat("[CONSOLE]: A user with the name {0} {1} already exists!", firstName, lastName);
            }
        }

        /// <summary>
        /// Reset a user password.
        /// </summary>
        /// <param name="cmdparams"></param>
        private void ResetUserPassword(string[] cmdparams)
        {
            string firstName;
            string lastName;
            string newPassword;

            if (cmdparams.Length < 4)
                firstName = MainConsole.Instance.CmdPrompt("First name");
            else firstName = cmdparams[3];

            if (cmdparams.Length < 5)
                lastName = MainConsole.Instance.CmdPrompt("Last name");
            else lastName = cmdparams[4];

            if (cmdparams.Length < 6)
                newPassword = MainConsole.Instance.PasswdPrompt("New password");
            else newPassword = cmdparams[5];

            m_commsManager.UserAdminService.ResetUserPassword(firstName, lastName, newPassword);
        }

        protected void SavePrimsXml2(string module, string[] cmdparams)
        {
            if (cmdparams.Length > 5)
            {
                m_sceneManager.SaveNamedPrimsToXml2(cmdparams[3], cmdparams[4]);
            }
            else
            {
                m_sceneManager.SaveNamedPrimsToXml2("Primitive", DEFAULT_PRIM_BACKUP_FILENAME);
            }
        }

        protected void SaveXml(string module, string[] cmdparams)
        {
            m_log.Error("[CONSOLE]: PLEASE NOTE, save-xml is DEPRECATED and may be REMOVED soon.  If you are using this and there is some reason you can't use save-xml2, please file a mantis detailing the reason.");

            if (cmdparams.Length > 0)
            {
                m_sceneManager.SaveCurrentSceneToXml(cmdparams[2]);
            }
            else
            {
                m_sceneManager.SaveCurrentSceneToXml(DEFAULT_PRIM_BACKUP_FILENAME);
            }
        }

        protected void LoadXml(string module, string[] cmdparams)
        {
            m_log.Error("[CONSOLE]: PLEASE NOTE, load-xml is DEPRECATED and may be REMOVED soon.  If you are using this and there is some reason you can't use load-xml2, please file a mantis detailing the reason.");

            Vector3 loadOffset = new Vector3(0, 0, 0);
            if (cmdparams.Length > 2)
            {
                bool generateNewIDS = false;
                if (cmdparams.Length > 3)
                {
                    if (cmdparams[3] == "-newUID")
                    {
                        generateNewIDS = true;
                    }
                    if (cmdparams.Length > 4)
                    {
                        loadOffset.X = (float) Convert.ToDouble(cmdparams[4]);
                        if (cmdparams.Length > 5)
                        {
                            loadOffset.Y = (float) Convert.ToDouble(cmdparams[5]);
                        }
                        if (cmdparams.Length > 6)
                        {
                            loadOffset.Z = (float) Convert.ToDouble(cmdparams[6]);
                        }
                        m_console.Error("loadOffsets <X,Y,Z> = <" + loadOffset.X + "," + loadOffset.Y + "," +
                                        loadOffset.Z + ">");
                    }
                }
                m_sceneManager.LoadCurrentSceneFromXml(cmdparams[0], generateNewIDS, loadOffset);
            }
            else
            {
                try
                {
                    m_sceneManager.LoadCurrentSceneFromXml(DEFAULT_PRIM_BACKUP_FILENAME, false, loadOffset);
                }
                catch (FileNotFoundException)
                {
                    m_console.Error("Default xml not found. Usage: load-xml <filename>");
                }
            }
        }

        protected void SaveXml2(string module, string[] cmdparams)
        {
            if (cmdparams.Length > 2)
            {
                m_sceneManager.SaveCurrentSceneToXml2(cmdparams[2]);
            }
            else
            {
                m_sceneManager.SaveCurrentSceneToXml2(DEFAULT_PRIM_BACKUP_FILENAME);
            }
        }

        protected void LoadXml2(string module, string[] cmdparams)
        {
            if (cmdparams.Length > 2)
            {
                try
                {
                    m_sceneManager.LoadCurrentSceneFromXml2(cmdparams[2]);
                }
                catch (FileNotFoundException)
                {
                    m_console.Error("Specified xml not found. Usage: load xml2 <filename>");
                }
            }
            else
            {
                try
                {
                    m_sceneManager.LoadCurrentSceneFromXml2(DEFAULT_PRIM_BACKUP_FILENAME);
                }
                catch (FileNotFoundException)
                {
                    m_console.Error("Default xml not found. Usage: load xml2 <filename>");
                }
            }
        }

        /// <summary>
        /// Scan's a region's data for creator IDs of assets from an InWorldz OAR backup
        /// scan iwoar [--save] oarname
        /// </summary>
        /// <param name="cmdparams"></param>
        protected void ScanIWOar(string module, string[] cmdparams)
        {
            string fileName;
            bool saveCreators;
            if (cmdparams.Length > 3 && cmdparams[2] == "--save")
            {
                saveCreators = true;
                fileName = cmdparams[3];
            }
            else
            {
                saveCreators = false;
                fileName = cmdparams[2];
            }

            try
            {
                m_sceneManager.ScanSceneForCreators(fileName);
            }
            catch (FileNotFoundException)
            {
                m_console.Error("Specified oar not found. Usage: load oar <filename>");
            }
        }

        /// <summary>
        /// Load a whole region from an opensim archive with optional table name.
        /// </summary>
        /// <param name="cmdparams"></param>
        protected void LoadOarWithOptions(string module, string[] cmdparams, string optionsTable)
        {
            string fileName = DEFAULT_OAR_BACKUP_FILENAME;
            bool allowReassign = false; // should UUIDs for missing users get reassigned to master avatar
            bool ignoreErrors = false;

            // Skip "load oar" and "load iwoar" in cmdparams (start at 2).
            for (int param = 2; param < cmdparams.Length; param++)
            {
                switch (cmdparams[param])
                {
                    case "--ignore-errors": ignoreErrors = true; break;
                    case "--allow-reassign": allowReassign = true; break;
                    default: fileName = cmdparams[param]; break;
                }
            }

            try
            {
                m_sceneManager.LoadArchiveToCurrentScene(fileName, allowReassign, ignoreErrors, optionsTable);
            }
            catch (FileNotFoundException)
            {
                m_console.Error("OAR file not found. Usage: load oar <filename>");
            }
        }

        /// <summary>
        /// Load a whole region from an opensim archive.
        /// </summary>
        /// <param name="cmdparams"></param>
        protected void LoadOar(string module, string[] cmdparams)
        {
            LoadOarWithOptions(module, cmdparams, null);
        }

        /// <summary>
        /// Load a region from an opensim archive with filtering based on opt-in status in a database table.
        /// </summary>
        /// <param name="cmdparams"></param>
        protected void LoadIWOar(string module, string[] cmdparams)
        {
            LoadOarWithOptions(module, cmdparams, "iwopt");
        }

        /// <summary>
        /// Save a region to a file, including all the assets needed to restore it.
        /// </summary>
        /// <param name="cmdparams"></param>
        protected void SaveOar(string module, string[] cmdparams)
        {
            if (cmdparams.Length == 2)
            {
                m_sceneManager.SaveCurrentSceneToArchive(DEFAULT_OAR_BACKUP_FILENAME, true);
            }
            else if (cmdparams.Length == 3)
            {
                m_sceneManager.SaveCurrentSceneToArchive(cmdparams[2], true);
            }
            else if (cmdparams.Length == 4)
            {
                m_sceneManager.SaveCurrentSceneToArchive(cmdparams[2], cmdparams[3] == "1");
            }
        }

        private Scene FindSceneByName(string name)
        {
            return m_sceneManager.FindSceneByName(name);
        }

        /// <summary>
        /// Load a whole region from an opensim archive.
        /// </summary>
        /// <param name="cmdparams"></param>
        protected void LoadExplicitOar(string module, string[] cmdparams)
        {
            if (cmdparams.Length != 4)
            {
                m_console.Error("Usage: loadexplicit oar <region name> <filename>");
                return;
            }

            try
            {
                Scene targetScene = this.FindSceneByName(cmdparams[2]);

                if (targetScene == null)
                {
                    m_console.Error("Could not find requested scene");
                    return;
                }

                IRegionArchiverModule archiver = targetScene.RequestModuleInterface<IRegionArchiverModule>();
                if (archiver != null)
                    archiver.DearchiveRegion(cmdparams[3], false, false, null);
            }
            catch (FileNotFoundException)
            {
                m_console.Error("Specified oar not found. Usage: loadexplicit oar <region> <filename>");
            }
        }

        /// <summary>
        /// Save a region to a file, including all the assets needed to restore it 
        /// </summary>
        /// <param name="cmdparams"></param>
        protected void SavePortableOar(string module, string[] cmdparams)
        {
            //saveportable oar <oar filename> [creator_uuid creator_uuid ...]
            if (cmdparams.Length < 3)
            {
                m_console.Error("Usage: saveportable oar <oar filename> [creator_uuid creator_uuid ...]");
                return;
            }

            try
            {
                Scene targetScene = m_sceneManager.Scenes[0];

                if (targetScene == null)
                {
                    m_console.Error("Could not find scene");
                    return;
                }

                IRegionArchiverModule archiver = targetScene.RequestModuleInterface<IRegionArchiverModule>();
                if (archiver != null)
                {
                    List<UUID> userIds = new List<UUID>();
                    for (int i = 3; i < cmdparams.Length; i++)
                    {
                        userIds.Add(UUID.Parse(cmdparams[i]));
                    }

                    archiver.ArchiveRegion(cmdparams[2], Guid.Empty, userIds);
                }
            }
            catch (FileNotFoundException)
            {
                m_console.Error("Specified oar not found. Usage: loadexplicit oar <region> <filename>");
            }
        }

        

        /// <summary>
        /// Save a region to a file, including all the assets needed to restore it.
        /// </summary>
        /// <param name="cmdparams"></param>
        protected void SaveExplicitOar(string module, string[] cmdparams)
        {
            //saveexplicit oar <region name> <oar filename> <store_assets>
            if (cmdparams.Length != 5)
            {
                m_console.Error("Usage: saveexplicit oar <region name> <oar filename> <store_assets>");
                return;
            }

            m_sceneManager.SaveExplicitOar(cmdparams[2], cmdparams[3], cmdparams[4] == "1");
        }

        private static string CombineParams(string[] commandParams, int pos)
        {
            string result = String.Empty;
            for (int i = pos; i < commandParams.Length; i++)
            {
                result += commandParams[i] + " ";
            }
            result = result.TrimEnd(' ');
            return result;
        }

        #endregion
    }
}
