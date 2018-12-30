/*
 * Copyright (c) InWorldz Halcyon Developers
 * Copyright (c) Contributors, http://opensimulator.org/
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following cofnditions are met:
 *     * Redistributions of source code must retain the above copyrightm_transitController
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
//using System.Drawing;
//using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Timers;
using System.Xml;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.Imaging;
using OpenMetaverse.Packets;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using OpenSim.Framework.Communications;
using OpenSim.Framework.Communications.Cache;
using OpenSim.Framework.Communications.Clients;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes.Scripting;
using OpenSim.Region.Framework.Scenes.Serialization;

using OpenSim.Region.Physics.Manager;
using Timer=System.Timers.Timer;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Reflection;

namespace OpenSim.Region.Framework.Scenes
{
    public delegate bool FilterAvatarList(ScenePresence avatar);

    public partial class Scene : SceneBase
    {
        [DllImport("kernel32.dll")]
        static extern bool SetThreadPriority(IntPtr hThread, ThreadPriorityLevel nPriority);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentThread();

        /// <summary>
        /// The lowest an object can fall/travel before being considered off world
        /// </summary>
        public const float NEGATIVE_OFFWORLD_Z = -128.0f;
        public const float POSITIVE_OFFWORLD_Z = 10000.0f;

        private const int BACKUP_THREAD_TIMEOUT = 5 * 60 * 1000; //5 minutes and the thread can stop
        private static Amib.Threading.SmartThreadPool _sBackupThreadPool
            = new Amib.Threading.SmartThreadPool(BACKUP_THREAD_TIMEOUT, 4, 1);

        public delegate void SynchronizeSceneHandler(Scene scene);
        public SynchronizeSceneHandler SynchronizeScene = null;

        private const long DEFAULT_MIN_TIME_FOR_PERSISTENCE = 60L;
        private const long DEFAULT_MAX_TIME_FOR_PERSISTENCE = 600L;

        protected int m_regionStartTime = Util.UnixTimeSinceEpoch();

        public static Vector3 DEFAULT_CHILD_AGENT_POS = new Vector3(128, 128, 70);

        // For returning users' where the preferred region is down
        private const string BLACKLIST_FILE = "blacklist.txt";
        private List<string> _BlacklistedNames = new List<string>();
        private List<UUID> _BlacklistedCreators = new List<UUID>();
        private List<UUID> _BlacklistedOwners = new List<UUID>();
        // _BlacklistedUsers goes beyond _BlacklistedOwners to actually
        // prevent region entry of the USER, not just the objects.
        private List<UUID> _BlacklistedUsers = new List<UUID>();

        #region Fields

        /// <summary>
        /// Timer that updates user attachments on a regular interval saving assets and database
        /// information
        /// </summary>
        private System.Timers.Timer _attachmentUpdateTimer = new Timer(2000);

        /// <summary>
        /// Groups that require persistance
        /// </summary>
        C5.HashSet<SceneObjectGroup> _taintedGroups = new C5.HashSet<SceneObjectGroup>();

        /// <summary>
        /// A group that has the potential to be returned at some point in time
        /// These classes collect in _returnGroups on rez and are checked during
        /// scene persistence
        /// </summary>
        private class PotentialTimedReturn : IComparable<PotentialTimedReturn>
        {
            private DateTime _timeRezzed;
            private SceneObjectGroup _group;

            public SceneObjectGroup Group
            {
                get
                {
                    return _group;
                }
            }

            public DateTime TimeRezzed
            {
                get
                {
                    return _timeRezzed;
                }
            }

            public PotentialTimedReturn(SceneObjectGroup group)
            {
                _group = group;
                _timeRezzed = DateTime.Now;
            }

            public int CompareTo(PotentialTimedReturn other)
            {
                if (_timeRezzed < other._timeRezzed)
                {
                    return -1;
                }

                if (_timeRezzed > other._timeRezzed)
                {
                    return 1;
                }

                return 0;
            }
        }

        /// <summary>
        /// Collects groups that may need a return
        /// </summary>
        LinkedList<PotentialTimedReturn> _potentialReturnGroups = new LinkedList<PotentialTimedReturn>();
        Dictionary<UUID, LinkedListNode<PotentialTimedReturn>> _returnGroupIndex = new Dictionary<UUID, LinkedListNode<PotentialTimedReturn>>();

        protected Timer m_restartWaitTimer = new Timer();

        protected Thread m_updateEntitiesThread;

        public SimStatsReporter StatsReporter;

        protected List<RegionInfo> m_regionRestartNotifyList = new List<RegionInfo>();

        /// <value>
        /// The scene graph for this scene
        /// </value>
        /// TODO: Possibly stop other classes being able to manipulate this directly.
        private SceneGraph m_sceneGraph;

        /// <summary>
        /// Are we applying physics to any of the prims in this scene?
        /// </summary>
        public bool m_physicalPrim;
        public float m_maxNonphys = 256;
        public float m_maxPhys = 10;
        public bool m_clampPrimSize = false;
        public bool m_allowScriptCrossings = false;
        public bool m_useFlySlow = false;
        public bool m_usePreJump = false;
        public bool m_seeIntoRegionFromNeighbor;
        // TODO: need to figure out how allow client agents but deny
        // root agents when ACL denies access to root agent
        public bool m_strictAccessControl = true;
        public int MaxUndoCount = 5;
        private int m_RestartTimerCounter;
        private readonly Timer m_restartTimer = new Timer(15000); // Wait before firing
        private int m_incrementsof15seconds = 0;
        private volatile bool m_backingup = false;

        private Dictionary<UUID, ReturnInfo> m_returns = new Dictionary<UUID, ReturnInfo>();

        protected ModuleLoader m_moduleLoader;
        protected StorageManager m_storageManager;
        public CommunicationsManager CommsManager;

        protected SceneCommunicationService m_sceneGridService;

        public SceneCommunicationService SceneGridService
        {
            get { return m_sceneGridService; }
        }

        public StorageManager StorageManager
        {
            get { return m_storageManager; }
        }

        public IXfer XferManager;

        protected IAssetService m_AssetService = null;

        public IAssetService AssetService
        {
            get
            {
                if (m_AssetService == null)
                    m_AssetService = RequestModuleInterface<IAssetService>();

                return m_AssetService;
            }
        }

        protected IXMLRPC m_xmlrpcModule;
        protected IWorldComm m_worldCommModule;
        protected IAvatarFactory m_AvatarFactory;
        protected IConfigSource m_config;
        protected IRegionSerializerModule m_serializer;
        private IInterregionCommsOut m_interregionCommsOut;
        public IInterregionCommsOut InterregionComms
        {
            get { return m_interregionCommsOut; }
        }


        protected IInterregionCommsIn m_interregionCommsIn;
        protected IDialogModule m_dialogModule;

        protected ICapabilitiesModule m_capsModule;
        public ICapabilitiesModule CapsModule
        {
            get { return m_capsModule; }
        }

        protected override IConfigSource GetConfig()
        {
            return m_config;
        }


        protected int m_fps = 32;
        protected int m_frame = 0;
        protected float m_timespan = 0.031f;
        protected short m_timespanMS = 31;

        private int m_update_entitiesquick = 1; // Run through objects that have scheduled updates checking for updates
        private int m_update_targets = 3; // Run through objects that have set llTargets checking for at_target/not_at_target (~94ms)
        private int m_update_presences = 1; // Update scene presence movements
        private int m_update_events = 1;
        private int m_update_backup = 1920; // about once per minute (rather than the previous once per 5 minutes 20 seconds) since auto-return is in minutes.
        private int m_update_terrain = 50;
        private int m_update_land = 1;
        private int m_update_coarse_locations = 50;
        private int m_update_watchdog = 100;

        private int frameMS = 0;
        private int physicsMS = 0;
        private int otherMS = 0;

        private bool m_physics_enabled = true;
        private bool m_scripts_enabled = true;
        private string m_defaultScriptEngine;
        private int m_LastLogin = 0;
        private Thread HeartbeatThread;
        private Thread TimingThread;
        private ManualResetEvent _timeOutSignal = new ManualResetEvent(true);
        private volatile bool shuttingdown = false;

        private int m_lastUpdate = Environment.TickCount;

        private object m_deleting_scene_object = new object();

        // the minimum time that must elapse before a changed object will be considered for persisted
        public long m_dontPersistBefore = DEFAULT_MIN_TIME_FOR_PERSISTENCE * 10000000L;
        // the maximum time that must elapse before a changed object will be considered for persisted
        public long m_persistAfter = DEFAULT_MAX_TIME_FOR_PERSISTENCE * 10000000L;

        private int m_maxRootAgents = 100;

        private int m_debugCrossingsLevel = 0;

        private Connection.AvatarConnectionManager m_connectionManager;

        #endregion

        #region Properties

        public Connection.AvatarConnectionManager ConnectionManager
        {
            get { return m_connectionManager; }
        }

        public SceneGraph SceneContents
        {
            get { return m_sceneGraph; }
        }

        // an instance to the physics plugin's Scene object.
        public PhysicsScene PhysicsScene
        {
            get { return m_sceneGraph.PhysicsScene; }
            set
            {
                // If we're not doing the initial set
                // Then we've got to remove the previous
                // event handler
                if (PhysicsScene != null && PhysicsScene.SupportsNINJAJoints)
                {
                    PhysicsScene.OnJointDeactivated -= jointDeactivated;
                    PhysicsScene.OnJointErrorMessage -= jointErrorMessage;
                }

                m_sceneGraph.PhysicsScene = value;

                if (PhysicsScene != null && m_sceneGraph.PhysicsScene.SupportsNINJAJoints)
                {
                    // register event handlers to respond to joint movement/deactivation
                    PhysicsScene.OnJointDeactivated += jointDeactivated;
                    PhysicsScene.OnJointErrorMessage += jointErrorMessage;
                }
            }
        }

        // This gets locked so things stay thread safe.
        public object SyncRoot
        {
            get { return m_sceneGraph.m_syncRoot; }
        }
        
        public int Frame
        {
            get { return m_frame; }
        }

        /// <summary>
        /// This is for llGetRegionFPS
        /// </summary>
        public float SimulatorFPS
        {
            get { return StatsReporter.getLastReportedSimFPS(); }
        }

        public string DefaultScriptEngine
        {
            get { return m_defaultScriptEngine; }
        }

        // Reference to all of the agents in the scene (root and child)
        protected Dictionary<UUID, ScenePresence> m_scenePresences
        {
            get { return m_sceneGraph.ScenePresences; }
            set { m_sceneGraph.ScenePresences = value; }
        }

        public EntityManager Entities
        {
            get { return m_sceneGraph.Entities; }
        }

        public Dictionary<UUID, ScenePresence> m_restorePresences
        {
            get { return m_sceneGraph.RestorePresences; }
            set { m_sceneGraph.RestorePresences = value; }
        }

        public int DebugCrossingsLevel
        {
            get { return m_debugCrossingsLevel; }
            set { m_debugCrossingsLevel = value; }
        }

        public int objectCapacity = RegionInfo.DEFAULT_REGION_PRIM_LIMIT;

        private SurroundingRegionManager _surroundingRegions;
        public SurroundingRegionManager SurroundingRegions
        {
            get
            {
                return _surroundingRegions;
            }
        }

        public string GridSendKey { get; set; }
        #endregion



        #region Constructors

        public Scene(RegionInfo regInfo,
                     CommunicationsManager commsMan, SceneCommunicationService sceneGridService,
                     StorageManager storeManager,
                     ModuleLoader moduleLoader, bool physicalPrim,
                     bool SeeIntoRegionFromNeighbor, IConfigSource config)
        {
            m_config = config;

            Random random = new Random();
            m_lastAllocatedLocalId = (int)((uint)(random.NextDouble() * (double)(uint.MaxValue/2))+(uint)(uint.MaxValue/4));
            m_moduleLoader = moduleLoader;
            CommsManager = commsMan;
            m_sceneGridService = sceneGridService;
            m_storageManager = storeManager;
            m_regInfo = regInfo;
            m_regionHandle = m_regInfo.RegionHandle;
            m_regionName = m_regInfo.RegionName;
            m_datastore = m_regInfo.DataStore;

            m_physicalPrim = physicalPrim;
            m_seeIntoRegionFromNeighbor = SeeIntoRegionFromNeighbor;

            m_eventManager = new EventManager();
            m_permissions = new ScenePermissions(this);

            m_asyncSceneObjectDeleter = new AsyncSceneObjectGroupDeleter(this);
            m_asyncSceneObjectDeleter.Enabled = true;

            // Load region settings
            m_regInfo.RegionSettings = m_storageManager.DataStore.LoadRegionSettings(m_regInfo.RegionID);
            if (m_storageManager.EstateDataStore != null)
                m_regInfo.EstateSettings = m_storageManager.EstateDataStore.LoadEstateSettings(m_regInfo.RegionID, m_regInfo.RegionName, m_regInfo.MasterAvatarAssignedUUID, true);

            //Bind Storage Manager functions to some land manager functions for this scene
            EventManager.OnLandObjectAdded +=
                new EventManager.LandObjectAdded(m_storageManager.DataStore.StoreLandObject);
            EventManager.OnLandObjectRemoved +=
                new EventManager.LandObjectRemoved(m_storageManager.DataStore.RemoveLandObject);

            m_sceneGraph = new SceneGraph(this, m_regInfo);

            // If the scene graph has an Unrecoverable error, restart this sim.
            // Currently the only thing that causes it to happen is two kinds of specific
            // Physics based crashes.
            //
            // Out of memory
            // Operating system has killed the plugin
            m_sceneGraph.UnRecoverableError += RestartNow;

            RegisterDefaultSceneEvents();

            m_scripts_enabled = !RegionInfo.RegionSettings.DisableScripts;

            m_physics_enabled = !RegionInfo.RegionSettings.DisablePhysics;

            StatsReporter = new SimStatsReporter(this);
            StatsReporter.OnSendStatsResult += SendSimStatsPackets;
            StatsReporter.OnStatsIncorrect += m_sceneGraph.RecalculateStats;

            StatsReporter.SetObjectCapacity(objectCapacity);

            IConfig netConfig = m_config.Configs["Network"];

            this.GridSendKey = netConfig.GetString("grid_send_key");
            _surroundingRegions = new SurroundingRegionManager(this, this.GridSendKey);


            try
            {
                IConfig startupConfig = m_config.Configs["Startup"];

                //Root agents
                m_maxRootAgents = startupConfig.GetInt("MaxRootAgents", m_maxRootAgents);

                //Animation states
                m_useFlySlow = startupConfig.GetBoolean("enableflyslow", false);
                // TODO: Change default to true once the feature is supported
                m_usePreJump = startupConfig.GetBoolean("enableprejump", false);

                m_maxNonphys = startupConfig.GetFloat("NonPhysicalPrimMax", m_maxNonphys);
                if (RegionInfo.NonphysPrimMax > 0)
                    m_maxNonphys = RegionInfo.NonphysPrimMax;

                m_maxPhys = startupConfig.GetFloat("PhysicalPrimMax", m_maxPhys);

                if (RegionInfo.PhysPrimMax > 0)
                    m_maxPhys = RegionInfo.PhysPrimMax;

                // Here, if clamping is requested in either global or
                // local config, it will be used
                //
                m_clampPrimSize = startupConfig.GetBoolean("ClampPrimSize", m_clampPrimSize);
                if (RegionInfo.ClampPrimSize)
                    m_clampPrimSize = true;

                m_allowScriptCrossings = startupConfig.GetBoolean("AllowScriptCrossing", m_allowScriptCrossings);
                m_dontPersistBefore =
                  startupConfig.GetLong("MinimumTimeBeforePersistenceConsidered", DEFAULT_MIN_TIME_FOR_PERSISTENCE);
                m_dontPersistBefore *= 10000000;
                m_persistAfter =
                  startupConfig.GetLong("MaximumTimeBeforePersistenceConsidered", DEFAULT_MAX_TIME_FOR_PERSISTENCE);
                m_persistAfter *= 10000000;

                m_defaultScriptEngine = startupConfig.GetString("DefaultScriptEngine", "InWorldz.Phlox");
                
                IConfig packetConfig = m_config.Configs["PacketPool"];
                if (packetConfig != null)
                {
                    PacketPool.Instance.RecyclePackets = packetConfig.GetBoolean("RecyclePackets", true);
                    PacketPool.Instance.RecycleDataBlocks = packetConfig.GetBoolean("RecycleDataBlocks", true);
                }

                m_strictAccessControl = startupConfig.GetBoolean("StrictAccessControl", m_strictAccessControl);
            }
            catch
            {
                m_log.Warn("[SCENE]: Failed to load StartupConfig");
            }

            LoadBlacklists();

            Preload();
        }

        private static void PreloadMethods(Type type)
        {
            foreach (var method in type.GetMethods(BindingFlags.DeclaredOnly |
                                BindingFlags.NonPublic |
                                BindingFlags.Public | BindingFlags.Instance |
                                BindingFlags.Static))
            {
                if (method.IsAbstract)
                    continue;
                if (method.ContainsGenericParameters || method.IsGenericMethod || method.IsGenericMethodDefinition)
                    continue;
                if ((method.Attributes & MethodAttributes.PinvokeImpl) > 0)
                    continue;

                try
                {
                    System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(method.MethodHandle);
                }
                catch
                {
                }
            }
        }

        private static void Preload()
        {
            //preload
            foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
            {
                PreloadMethods(type);
            }

            foreach (var type in Assembly.GetAssembly(typeof(OpenSim.Framework.AgentCircuitData)).GetTypes())
            {
                PreloadMethods(type);
            }

            foreach (var type in Assembly.GetAssembly(typeof(OpenMetaverse.Quaternion)).GetTypes())
            {
                PreloadMethods(type);
            }
        }

        /// <summary>
        /// LoadStringListFromFile
        /// </summary>
        /// <param name="theList"></param>
        /// <param name="fn"></param>
        /// <param name="desc"></param>
        private void LoadStringListFromFile(List<string> theList, string fn, string desc)
        {
            if (File.Exists(fn))
            {
                using (System.IO.StreamReader file = new System.IO.StreamReader(fn))
                {
                    string line;
                    while ((line = file.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (!String.IsNullOrEmpty(line) && !line.StartsWith(";") && !line.StartsWith("//"))
                        {
                            theList.Add(line);
                        }
                    }
                }
            }

        }

        private void LoadBlacklists()
        {
            List<string> blacklist = new List<string>();

            LoadStringListFromFile(blacklist, BLACKLIST_FILE, "blacklisted");

            foreach (string line in blacklist)
            {
                string[] tokens = Parser.Parse(line);
                if (tokens.Length < 2) continue;

                string keyword = tokens[0].ToLower();
                switch (keyword)
                {
                    case "name":
                    _BlacklistedNames.Add(tokens[1]);
                    m_log.InfoFormat("[SCENE]: Loaded blacklist name '{0}'.", tokens[1]);
                    break;

                    case "owner":
                    case "creator":
                    case "user":
                        UUID targetID = UUID.Zero;
                        if (UUID.TryParse(tokens[1], out targetID))
                        {
                            switch (keyword)
                            {
                                case "owner":
                                    _BlacklistedOwners.Add(targetID);
                                    break;
                                case "creator":
                                    _BlacklistedCreators.Add(targetID);
                                    break;
                                case "user":
                                    _BlacklistedUsers.Add(targetID);
                                    break;
                            }

                        }
                        else
                            m_log.ErrorFormat("[SCENE]: Invalid UUID for: {0} {1}.", tokens[0], tokens[1]);
                        break;

                    default:
                        m_log.ErrorFormat("[SCENE]: Unrecognized blacklist keyword '{0}'.", keyword);
                        break;
                }
            }
        }

        public void SaveBlacklists()
        {
            if (File.Exists(BLACKLIST_FILE))
            {
                string oldfile = Path.ChangeExtension(BLACKLIST_FILE, ".old");
                if (File.Exists(oldfile))
                    File.Delete(oldfile);
                File.Move(BLACKLIST_FILE, oldfile);
            }

            using (StreamWriter sw = new StreamWriter(BLACKLIST_FILE))
            {
                foreach (UUID user in _BlacklistedUsers)
                    sw.WriteLine("user " + user.ToString());
                foreach (UUID owner in _BlacklistedOwners)
                    sw.WriteLine("owner " + owner.ToString());
                foreach (UUID creator in _BlacklistedCreators)
                    sw.WriteLine("creator " + creator.ToString());
                foreach (string name in _BlacklistedNames)
                    sw.WriteLine("name \"" + name+"\"");
            }
            m_log.InfoFormat("[SCENE]: Changes saved to " + BLACKLIST_FILE);
        }

        public void AddBlacklistedOwner(UUID ownerID)
        {
            if (!_BlacklistedOwners.Contains(ownerID))
            {
                m_log.InfoFormat("[SCENE]: Blacklisting owner {0} ...", ownerID);
                _BlacklistedOwners.Add(ownerID);
                SaveBlacklists();
            }
        }
        public void AddBlacklistedCreator(UUID creatorID)
        {
            if (!_BlacklistedCreators.Contains(creatorID))
            {
                m_log.InfoFormat("[SCENE]: Blacklisting creator {0} ...", creatorID);
                _BlacklistedCreators.Add(creatorID);
                SaveBlacklists();
            }
        }
        public void AddBlacklistedName(string name)
        {
            name = name.Trim();
            if (!_BlacklistedNames.Contains(name))
            {
                m_log.InfoFormat("[SCENE]: Blacklisting name \"{0}\" ...", name);
                _BlacklistedNames.Add(name);
                SaveBlacklists();
            }
        }
        public void AddBlacklistedUser(UUID userID)
        {
            if (!_BlacklistedUsers.Contains(userID))
            {
                m_log.InfoFormat("[SCENE]: Blacklisting user {0} ...", userID);
                // First, kick the user if they are here.
                ScenePresence sp = GetScenePresence(userID);
                if (sp != null)
                {
                    sp.ControllingClient.Kick("You have been kicked from the region and blacklisted.");
                    System.Threading.Thread.Sleep(1000);
                    sp.Scene.IncomingCloseAgent(userID);
                    m_log.InfoFormat("[SCENE]: Blacklisted user {0} has been kicked from the region.", sp.Name);
                }
                _BlacklistedUsers.Add(userID);
                SaveBlacklists();
            }
        }
        public void BlacklistRemove(UUID targetID)
        {
            bool persist = false;
            if (_BlacklistedUsers.Contains(targetID))
            {
                m_log.InfoFormat("[SCENE]: Removing blacklist user {0} ...", targetID);
                _BlacklistedUsers.Remove(targetID);
                persist = true;
            }
            if (_BlacklistedOwners.Contains(targetID))
            {
                m_log.InfoFormat("[SCENE]: Removing blacklist owner {0} ...", targetID);
                _BlacklistedOwners.Remove(targetID);
                persist = true;
            }
            if (_BlacklistedCreators.Contains(targetID))
            {
                m_log.InfoFormat("[SCENE]: Removing blacklist creator {0} ...", targetID);
                _BlacklistedCreators.Remove(targetID);
                persist = true;
            }
            if (persist)
                SaveBlacklists();
        }
        public void BlacklistRemove(string name)
        {
            if (_BlacklistedNames.Contains(name))
            {
                m_log.InfoFormat("[SCENE]: Removing blacklist name \"{0}\" ...", name);
                _BlacklistedNames.Remove(name.Trim());
                SaveBlacklists();
            }
        }
        public void BlacklistClear()
        {
            m_log.InfoFormat("[SCENE]: Clearing blacklist ...");
            _BlacklistedUsers.Clear();
            _BlacklistedOwners.Clear();
            _BlacklistedCreators.Clear();
            _BlacklistedNames.Clear();
            SaveBlacklists();
        }
        public void BlacklistShow()
        {
            int count = _BlacklistedUsers.Count + _BlacklistedOwners.Count + _BlacklistedCreators.Count + _BlacklistedNames.Count;
            if (count > 0)
                m_log.InfoFormat("[SCENE]: Current blacklisted users and objects in {0}:", m_regionName);
            foreach (UUID user in _BlacklistedUsers)
                m_log.Info("[SCENE]:   blacklist user " + user.ToString());
            foreach (UUID owner in _BlacklistedOwners)
                m_log.Info("[SCENE]:   blacklist object owner " + owner.ToString());
            foreach (UUID creator in _BlacklistedCreators)
                m_log.Info("[SCENE]:   blacklist object creator " + creator.ToString());
            foreach (string name in _BlacklistedNames)
                m_log.Info("[SCENE]:   blacklist object name \"" + name + "\"");
            if (count > 0)
                m_log.InfoFormat("[SCENE]: {0} blacklist entries for {1}.", count, m_regionName);
            else
                m_log.InfoFormat("[SCENE]: The blacklist for {0} is empty.", m_regionName);
        }

        public bool IsBlacklistedUser(UUID userID)
        {
            return _BlacklistedUsers.Contains(userID);
        }
        public bool IsBlacklistedOwner(UUID ownerID)
        {
            return _BlacklistedOwners.Contains(ownerID) || _BlacklistedUsers.Contains(ownerID);
        }
        public bool IsBlacklistedCreator(UUID creatorID)
        {
            return _BlacklistedCreators.Contains(creatorID);
        }
        public bool IsBlacklistedName(string name)
        {
            foreach (var badName in _BlacklistedNames)
            {
                if (name.Trim().StartsWith(badName, StringComparison.InvariantCultureIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Mock constructor for unit tests.
        /// SceneObjectGroup RegionId property is delegated to Scene.
        /// </summary>
        /// <param name="regInfo"></param>
        public Scene(RegionInfo regInfo, CommunicationsManager commsManager, SceneCommunicationService gridSvc)
        {
            m_regInfo = regInfo;
            m_eventManager = new EventManager();
            CommsManager = commsManager;

            _surroundingRegions = new SurroundingRegionManager(this, "key");
            m_sceneGridService = gridSvc;
        }

        #endregion

        #region Startup / Close Methods

        public bool ShuttingDown
        {
            get { return shuttingdown; }
        }

        /// <value>
        /// The scene graph for this scene
        /// </value>
        /// TODO: Possibly stop other classes being able to manipulate this directly.
        public SceneGraph SceneGraph
        {
            get { return m_sceneGraph; }
        }

        protected virtual void RegisterDefaultSceneEvents()
        {
            IDialogModule dm = RequestModuleInterface<IDialogModule>();

            if (dm != null)
                m_eventManager.OnPermissionError += dm.SendAlertToUser;
        }

        public string GetEnv(string name)
        {
            IConfig config = null;
            string ret = String.Empty;

            // NOTE: All case values in must be lowercase for case-insensitive compare.
            switch (name.ToLower())
            {
                case "sim_channel":     // Get the region's channel string, for example "Halcyon Server" or "Second Life Server". Does not change across grids as this is about the simulator software.
                    ret = VersionInfo.SoftwareChannel;
                    break;
                case "sim_version":     // Get the region's version.revision number string, for example "0.9.18.9999". Does not change across grids as this is about the simulator software.
                    ret = VersionInfo.Version;
                    break;
                case "short_version":     // Get the region's version text, such as "Halcyon 1.2.3", without revision info.
                    ret = VersionInfo.ShortVersion;
                    break;
                case "long_version":     // Get the region's full version text, with revision info, such as "Halcyon 1.2.3 R9999".
                    ret = VersionInfo.FullVersion;
                    break;
                case "frame_number":    // (integer) Get the frame number of the simulator, for example "42042".
                    ret = m_frame.ToString();
                    break;
                case "region_idle":     // (integer) Get the region's idle status, "1" or "0".
                    ret = "0";  // false in InWorldz
                    break;
                case "dynamic_pathfinding": // (integer) Get the region's dynamic_pathfinding status, "1" or "0".
                    ret = "disabled";
                    break;
                case "inworldz": // (integer) Is this an InWorldz server? "1" or String.Empty.
                    config = m_config.Configs["GridInfo"];
                    ret = (config.GetString("gridmanagement", VersionInfo.DefaultGrid).Trim() == "InWorldz") ? "1" : String.Empty;
                    break;
                case "halcyon": // (integer) Is this a Halcyon server? "1" or String.Empty.
                    ret = (VersionInfo.SoftwareName == "Halcyon") ? "1" : String.Empty;
                    break;
                case "script_engine":
                    ret = "Phlox";  // always Phlox in Halcyon
                    break;
                case "iw_physics_fps":
                    ret = PhysicsScene.SimulationFPS.ToString();
                    break;
                case "simulator_netname":
                    ret = System.Environment.MachineName;
                    break;
                case "simulator_hostname":
                    ret = RegionInfo.ExternalHostName;
                    break;
                case "region_size_x":
                    ret = Constants.RegionSize.ToString();
                    break;
                case "region_size_y":
                    ret = Constants.RegionSize.ToString();
                    break;
                case "region_size_z":
                    ret = ((int)Constants.REGION_MAXIMUM_Z).ToString();
                    break;
                case "agent_limit":
                    ret = m_regInfo.RegionSettings.AgentLimit.ToString();
                    break;
                case "region_product_name":
                    switch (m_regInfo.Product)
                    {
                        case ProductRulesUse.FullUse:
                            if (m_regInfo.PrimLimit == 12000)
                                ret = "Estate / Landmass";
                            else
                                ret = "Estate / Full Region";
                            break;
                        case ProductRulesUse.OceanUse:
                            ret = "Estate / Ocean";
                            break;
                        case ProductRulesUse.PlusUse:
                            ret = "Estate / Plus Region";
                            break;
                        case ProductRulesUse.ScenicUse:
                            ret = "Estate / Scenic";
                            break;
                        case ProductRulesUse.UnknownUse:
                            ret = "Estate / Unknown";
                            break;
                    }
                    break;
                case "region_product_sku":
                    switch(m_regInfo.Product)
                    {
                        case ProductRulesUse.FullUse:
                            ret = "1";
                            break;
                        case ProductRulesUse.OceanUse:
                            ret = "2";
                            break;
                        case ProductRulesUse.ScenicUse:
                            ret = "3";
                            break;
                        case ProductRulesUse.PlusUse:
                            ret = "4";
                            break;
                        case ProductRulesUse.UnknownUse:
                            ret = "0";
                            break;
                    }
                    break;
                case "estate_id":
                    ret = m_regInfo.EstateSettings.EstateID.ToString();
                    break;
                case "estate_name":
                    ret = m_regInfo.EstateSettings.EstateName;
                    break;
                case "region_cpu_ratio":
                    ret = "1";
                    break;
                case "region_start_time":
                    ret = m_regionStartTime.ToString();
                    break;
                case "platform":
                    config = m_config.Configs["GridInfo"];    // Halcyon, OpenSim, SecondLife
                    return (config == null) ? String.Empty : config.GetString("platform", VersionInfo.SoftwareName).Trim();
                case "grid_management":
                    config = m_config.Configs["GridInfo"];    // InWorldz, ARL, LindenLab, etc.
                    return (config == null) ? String.Empty : config.GetString("gridmanagement", VersionInfo.DefaultGrid).Trim();
                case "grid_nick":
                    config = m_config.Configs["GridInfo"];    // InWorldz, InWorldzBeta, SecondLife, etc.
                    return (config == null) ? String.Empty : config.GetString("gridnick", VersionInfo.DefaultGrid).Trim();
                case "grid_name":
                    config = m_config.Configs["GridInfo"];    // InWorldz, InWorldz Beta, Second Life, etc.
                    return (config == null) ? String.Empty : config.GetString("gridname", VersionInfo.DefaultGrid).Trim();
                case "shard":
                    config = m_config.Configs["Network"];    // "InWorldz", "Beta", "Some Other Grid", "Testing", etc.
                    return (config == null) ? String.Empty : config.GetString("shard", VersionInfo.DefaultGrid).Trim();
            }
            return ret;
        }

        public bool HasNeighbor(uint x, uint y)
        {
            return _surroundingRegions.HasKnownNeighborAt(x, y);
        }

        public SimpleRegionInfo GetNeighborAtPosition(float x, float y)
        {
            uint neighborX = RegionInfo.RegionLocX;
            uint neighborY = RegionInfo.RegionLocY;
            if (x < 0.0)
                neighborX--;
            else
                if (x >= Constants.RegionSize)
                    neighborX++;
            if (y < 0.0)
                neighborY--;
            else
                if (y >= Constants.RegionSize)
                    neighborY++;

            return _surroundingRegions.GetKnownNeighborAt(neighborX, neighborY);
        }

        public bool HasNeighborAtPosition(float x, float y)
        {
            return GetNeighborAtPosition(x, y) != null;
        }

        /// <summary>
        /// Given float seconds, this will restart the region.
        /// </summary>
        /// <param name="seconds">float indicating duration before restart.</param>
        public override void Restart(int seconds)
        {
            // RestartNow() does immediate restarting.
            if (seconds == -1)
            {
                m_restartTimer.Stop();
                m_dialogModule.SendGeneralAlert("Restart Aborted");
            }
            else
            {
                // Now we figure out what to set the timer to that does the notifications and calls, RestartNow()
                m_restartTimer.Interval = 15000;
                m_incrementsof15seconds = seconds / 15;
                m_RestartTimerCounter = 0;
                m_restartTimer.AutoReset = true;
                m_restartTimer.Elapsed += new ElapsedEventHandler(RestartTimer_Elapsed);
                m_log.Info("[REGION]: Restarting Region in " + (seconds / 60) + " minutes");
                m_restartTimer.Start();
                SendRestartAlert(seconds);
            }
        }

        // The Restart timer has occured.
        // We have to figure out if this is a notification or if the number of seconds specified in Restart
        // have elapsed.
        // If they have elapsed, call RestartNow()
        public void RestartTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            m_RestartTimerCounter++;
            if (m_RestartTimerCounter <= m_incrementsof15seconds)
            {
                if (m_RestartTimerCounter == 4 || m_RestartTimerCounter == 6 || m_RestartTimerCounter == 7)
                {
                    SendRestartAlert((8 - m_RestartTimerCounter) * 15);
                }
            }
            else
            {
                m_restartTimer.Stop();
                m_restartTimer.AutoReset = false;
                RestartNow();
            }
        }

        private void SendRestartAlert(int seconds)
        {
            string message = String.Format("The region you are in now ({0}) is about to restart. If you stay in this region, you will be logged out", RegionInfo.RegionName);
            OSD paramMap = new OSDMap{ { "SECONDS", OSD.FromInteger(seconds) } }; // *TODO: Make this work with RegionRestartMinutes notice as well?
            m_dialogModule.SendGeneralAlert(message, "RegionRestartSeconds", paramMap);
        }

        // This causes the region to restart immediatley.
        public void RestartNow()
        {
            // Let's issue a full server restart request.
            m_log.Error("[REGION]: Firing Region Restart Command");
            MainConsole.Instance.RunCommand("restart");
        }

        public void SetSceneCoreDebug(bool ScriptEngine, bool CollisionEvents, bool PhysicsEngine)
        {
            if (m_scripts_enabled == ScriptEngine)
            {
                if (ScriptEngine)
                {
                    m_log.Info("Stopping all Scripts in Scene");
                    foreach (EntityBase ent in Entities)
                    {
                        var sceneObjectGroup = ent as SceneObjectGroup;
                        if (sceneObjectGroup != null)
                        {
                            sceneObjectGroup.RemoveScriptInstances();
                        }
                    }
                }
                else
                {
                    m_log.Info("Starting all Scripts in Scene");
                    lock (Entities)
                    {
                        foreach (EntityBase ent in Entities)
                        {
                            if (ent is SceneObjectGroup)
                            {
                                ((SceneObjectGroup)ent).CreateScriptInstances(0, ScriptStartFlags.None, DefaultScriptEngine, 0, null);
                            }
                        }
                    }
                }
                m_scripts_enabled = !ScriptEngine;
            }

            if (m_physics_enabled != !PhysicsEngine)
            {
                m_physics_enabled = !PhysicsEngine;
                PhysicsScene.Simulating = m_physics_enabled;
            }
        }

        public int GetKnownNeighborCount()
        {
            return _surroundingRegions.GetKnownNeighborCount();
        }

        // This is the method that shuts down the scene.
        public override void Close()
        {
            m_log.InfoFormat("[SCENE]: Closing down the single simulator: {0}", RegionInfo.RegionName);

            _attachmentUpdateTimer.Enabled = false;
            _attachmentUpdateTimer = null;

            // Kick all ROOT agents with a message
            ForEachScenePresence(delegate(ScenePresence avatar)
            {
                if (!avatar.IsChildAgent)
                    avatar.ControllingClient.Kick("This region is shutting down or restarting.");
            });

            // Wait here, or the kick messages won't actually get to the agents before the scene terminates.
            Thread.Sleep(500);

            // Stop all client threads.
            ForEachScenePresence(delegate(ScenePresence avatar) { avatar.ControllingClient.Close(); });

            // Stop updating the scene objects and agents.
            //m_heartbeatTimer.Close();
            shuttingdown = true;

            try
            {
                SendGoodbyeToNeighbors();
            }
            catch
            {
            }

            m_log.Debug("[SCENE]: Persisting changed objects");
            Backup(true);

            // De-register with region communications (events cleanup)
            UnRegisterRegionWithComms();

            // call the base class Close method.
            base.Close();

            //shut down physics
            m_log.Debug("[SCENE]: Stopping Physics");
            m_sceneGraph.PhysicsScene.Dispose();

            m_sceneGraph.Close();
        }

        private void SendGoodbyeToNeighbors()
        {
            _surroundingRegions.SendRegionDownToNeighbors();
        }

        /// <summary>
        /// Start the heartbeat and other timers
        /// </summary>
        public void Start()
        {
            HeartbeatThread = Watchdog.StartThread(new ThreadStart(Heartbeat), string.Format("Heartbeat for region {0}", RegionInfo.RegionName),
                ThreadPriority.Normal, false);

            TimingThread = Watchdog.StartThread(new ThreadStart(DoTiming), string.Format("Timing for heartbeat for region"),
                ThreadPriority.Highest, false);

            _attachmentUpdateTimer.Elapsed += new ElapsedEventHandler(attachmentUpdateTimer_Elapsed);
            _attachmentUpdateTimer.AutoReset = true;
            _attachmentUpdateTimer.Enabled = true;
        }

        /// <summary>
        /// This thread does nothing but keep the time for the heartbeat so that
        /// we can achieve the maximum simulation frame rates on situations where
        /// the frame time is less than the tick count
        /// </summary>
        void DoTiming()
        {
            if (Util.IsWindows)
            {
                IntPtr thrdHandle = GetCurrentThread();
                SetThreadPriority(thrdHandle, ThreadPriorityLevel.TimeCritical);
            }
            
            // This thread is already assigned ThreadPriority.Highest, shouldn't that be enough for most people?

            byte tickNum = 0;
            while (true)
            {
                Thread.Sleep(m_timespanMS);
                _timeOutSignal.Set();
                if (++tickNum >= 10)
                {
                    Watchdog.UpdateThread();
                    tickNum = 0;
                }
            }
        }

        void FlushPendingAssetUpdates()
        {
            List<KnownAssetUpdateRequest> updates;
            lock (_pendingAssetUpdates)
            {
                updates = new List<KnownAssetUpdateRequest>(_pendingAssetUpdates.Values);
                _pendingAssetUpdates.Clear();
            }

            foreach (KnownAssetUpdateRequest updateReq in updates)
            {
                DoStoreKnownAsset(updateReq.remoteClient, updateReq.attachment, updateReq.assetID, updateReq.agentID, updateReq.forDeletion);
            }
        }

        void attachmentUpdateTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            FlushPendingAssetUpdates();
        }

        /// <summary>
        /// Sets up references to modules required by the scene
        /// </summary>
        public void SetModuleInterfaces()
        {
            m_xmlrpcModule = RequestModuleInterface<IXMLRPC>();
            m_worldCommModule = RequestModuleInterface<IWorldComm>();
            XferManager = RequestModuleInterface<IXfer>();
            m_AvatarFactory = RequestModuleInterface<IAvatarFactory>();
            m_serializer = RequestModuleInterface<IRegionSerializerModule>();
            m_interregionCommsOut = RequestModuleInterface<IInterregionCommsOut>();
            m_interregionCommsIn = RequestModuleInterface<IInterregionCommsIn>();
            m_dialogModule = RequestModuleInterface<IDialogModule>();
            m_capsModule = RequestModuleInterface<ICapabilitiesModule>();

            m_transitController = new AvatarTransit.AvatarTransitController(this);
            m_connectionManager = new Connection.AvatarConnectionManager(m_capsModule, m_transitController);
        }

        #endregion

        #region Update Methods

        /// <summary>
        /// Performs per-frame updates regularly
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Heartbeat()
        {
            Update();
        }

        /// <summary>
        /// Performs per-frame updates on the scene, this should be the central scene loop
        /// </summary>
        public override void Update()
        {
            while (!shuttingdown)
            {
                int SinceLastFrame = Environment.TickCount - m_lastUpdate;

                frameMS = Environment.TickCount;
                try
                {
                    // Increment the frame counter
                    m_frame++;

                    // Loop it
                    if (m_frame == Int32.MaxValue)
                        m_frame = 0;

                    otherMS = Environment.TickCount;

                    // run through entities that have scheduled themselves for
                    // updates looking for updates
                    if (m_frame % m_update_entitiesquick == 0)
                        m_sceneGraph.ProcessUpdates();

                    // Run through objects that have set llTargets
                    // checking for at_target/not_at_target (~94ms)
                    if (m_frame % m_update_targets == 0)
                        m_sceneGraph.ProcessTargets();

                    // Run through scenepresences looking for updates
                    if (m_frame % m_update_presences == 0)
                        m_sceneGraph.UpdatePresences();

                    if (m_frame % m_update_coarse_locations == 0)
                    {
                        UpdateCoarseLocations();
                    }

                    // Delete temp-on-rez stuff
                    if (m_frame % m_update_backup == 0)
                        CleanTempObjects();

                    if (m_frame % m_update_watchdog == 0)
                        Watchdog.UpdateThread();

                    if (RegionStatus != RegionStatus.SlaveScene)
                    {
                        if (m_frame % m_update_events == 0)
                            UpdateEvents();

                        if (m_frame % m_update_backup == 0)
                            UpdateStorageBackup();

                        if (m_frame % m_update_terrain == 0)
                            UpdateTerrain();

                        if (m_frame % m_update_land == 0)
                            UpdateLand();

                        otherMS = Environment.TickCount - otherMS;

                        // if (m_frame%m_update_avatars == 0)
                        //   UpdateInWorldTime();
                        StatsReporter.SetPhysicsFPS(PhysicsScene.SimulationFPS);
                        StatsReporter.AddTimeDilation(m_timedilation);
                        StatsReporter.AddFPS(1);
                        StatsReporter.AddInPackets(0);
                        StatsReporter.SetRootAgents(m_sceneGraph.GetRootAgentCount());
                        StatsReporter.SetChildAgents(m_sceneGraph.GetChildAgentCount());
                        StatsReporter.SetObjects(m_sceneGraph.GetTotalObjectsCount());
                        StatsReporter.SetActiveObjects(m_sceneGraph.GetActiveObjectsCount());
                        frameMS = Environment.TickCount - frameMS;
                        StatsReporter.addFrameMS(frameMS);
                        StatsReporter.addPhysicsMS(physicsMS);
                        StatsReporter.addOtherMS(otherMS);
                        StatsReporter.SetActiveScripts(m_sceneGraph.GetActiveScriptsCount());
                        StatsReporter.addScriptLines(m_sceneGraph.GetScriptLPS());

                    }
                }
                catch (Exception e)
                {
                    m_log.Error("[Scene]: Failed with exception " + e.ToString() + " On Region: " + RegionInfo.RegionName);
                }
                finally
                {
                    //updateLock.ReleaseMutex();
                    // Get actual time dilation
                    float tmpval = (m_timespan / (SinceLastFrame / 1000.0f));

                    if (tmpval >= 0.95f)
                    {
                        tmpval = 1.0f;
                    }

                    m_timedilation = tmpval;

                    m_lastUpdate = Environment.TickCount;
                }


                _timeOutSignal.WaitOne();
                _timeOutSignal.Reset();
            }
        }

        private void UpdateCoarseLocations()
        {
            List<Vector3> coarseLocations;
            List<UUID> avatarUUIDs;
            List<ScenePresence> presenceList;

            lock (SyncRoot)
            {
                ScenePresence.CollectCoarseLocations(this, out coarseLocations, out avatarUUIDs);
                presenceList = GetScenePresences();
            }
            foreach (ScenePresence presence in presenceList)
            {
                // Send coarse locations to clients that are fully in the region
                if (!(presence.IsDeleted || presence.IsInTransit))
                    presence.SendCoarseLocations(coarseLocations, avatarUUIDs);
            }
        }

        private void SendSimStatsPackets(SimStats stats)
        {
            List<ScenePresence> StatSendAgents = GetScenePresences();
            foreach (ScenePresence agent in StatSendAgents)
            {
                if (!(agent.IsChildAgent || agent.IsDeleted || agent.IsInTransit))
                {
                    agent.ControllingClient.SendSimStats(stats);
                }
            }
        }

        private void UpdateLand()
        {
            if (LandChannel != null)
            {
                if (LandChannel.IsLandPrimCountTainted())
                {
                    EventManager.TriggerParcelPrimCountUpdate();
                }
            }
        }

        private void UpdateTerrain()
        {
            EventManager.TriggerTerrainTick();
        }

        private void UpdateStorageBackup()
        {
            if (!m_backingup)
            {
                m_backingup = true;
                _sBackupThreadPool.QueueWorkItem(new Action(BeginScheduledSceneBackup));
            }
            /*if (!m_backingup)
            {
                m_backingup = true;
                Thread backupthread = new Thread(Backup);
                backupthread.Name = "BackupWriter";
                backupthread.IsBackground = true;
                backupthread.Start();
            }*/
        }

        private void BeginScheduledSceneBackup()
        {
            Backup(false);
        }

        private void UpdateEvents()
        {
            m_eventManager.TriggerOnFrame();
        }

        /// <summary>
        /// Perform delegate action on all clients subscribing to updates from this region.
        /// </summary>
        /// <returns></returns>
        public void Broadcast(Action<IClientAPI> whatToDo)
        {
            ForEachScenePresence(delegate(ScenePresence presence) { whatToDo(presence.ControllingClient); });
        }

        /// <summary>
        /// Perform delegate action on all clients subscribing to updates from this region.
        /// </summary>
        /// <returns></returns>
        public void Broadcast(Action<ScenePresence> whatToDo)
        {
            ForEachScenePresence(delegate(ScenePresence presence) { whatToDo(presence); });
        }

        private void AddGridReturnNotificationForUser(UUID userId, int count, Vector3 location, string objectName, string reason)
        {
            lock (m_returns)
            {
                ReturnInfo retInfo = null;
                if (m_returns.TryGetValue(userId, out retInfo))
                {
                    retInfo.count++;
                }
                else
                {
                    retInfo = new ReturnInfo();
                    retInfo.count = 1;
                    retInfo.location = location;
                    retInfo.objectName = objectName;
                    retInfo.reason = reason;

                    m_returns.Add(userId, retInfo);
                }
            }
        }


        private void RemoveSinglePotentialReturnWorker(SceneObjectGroup group)
        {
            //find the index
            LinkedListNode<PotentialTimedReturn> node;
            if (_returnGroupIndex.TryGetValue(group.UUID, out node))
            {
                _returnGroupIndex.Remove(group.UUID);
                _potentialReturnGroups.Remove(node);
            }
        }

        /// <summary>
        /// Tracks the derez of one objects so that it's excluded from the
        /// return list on the next run
        /// </summary>
        /// <param name="groups"></param>
        public void RemoveFromPotentialReturns(SceneObjectGroup group)
        {
            lock (_potentialReturnGroups)
            {
                this.RemoveSinglePotentialReturnWorker(group);
            }
        }

        /// <summary>
        /// Tracks the derez of one or more groups of objects so that they're excluded from the
        /// return list on the next run
        /// </summary>
        /// <param name="groups"></param>
        public void RemoveFromPotentialReturns(List<SceneObjectGroup> groups)
        {
            lock (_potentialReturnGroups)
            {
                foreach (SceneObjectGroup group in groups)
                {
                    this.RemoveSinglePotentialReturnWorker(group);
                }
            }
        }

        /// <summary>
        /// This function does the actual persisting of changed objects in a region.
        /// </summary>
        private DateTime m_lastBackup = DateTime.Now;
        private void PerformBackup(bool forceBackup)
        {
            List<SceneObjectGroup> groupsNeedingBackup = new List<SceneObjectGroup>();
            List<SceneObjectGroup> deletedGroups = new List<SceneObjectGroup>();
            lock (_taintedGroups)
            {
                foreach (SceneObjectGroup grp in _taintedGroups)
                {
                    if (grp.NeedsBackup(forceBackup))
                    {
                        groupsNeedingBackup.Add(grp);
                    }
                    if (grp.IsDeleted)
                    {
                        deletedGroups.Add(grp);
                    }
                }

                _taintedGroups.RemoveAll(groupsNeedingBackup);
                _taintedGroups.RemoveAll(deletedGroups);
            }

            m_lastBackup = DateTime.Now;    // last attempt

            bool wasBackupError = false;
            try
            {
                SceneObjectGroup.ProcessBulkBackup(groupsNeedingBackup, m_storageManager.DataStore, forceBackup);
            }
            catch (Exception e)
            {
                wasBackupError = true;
                m_log.ErrorFormat("[SCENE]:  Persistance failed with exception {0}, objects will not be untainted", e.ToString());

                lock (_taintedGroups)
                {
                    //readd the backup groups to the taint list
                    _taintedGroups.AddAll(groupsNeedingBackup);
                }
            }

            if (!wasBackupError)
            {
                //Mark objects as clean, including inventories
                foreach (SceneObjectGroup group in groupsNeedingBackup)
                {
                    var parts = group.GetParts();
                    lock (_taintedGroups)
                    {
                        if (!_taintedGroups.Contains(group))
                        {
                            group.HasGroupChanged = false;

                            foreach (var part in parts)
                            {
                                part.Inventory.MarkInventoryClean();
                                part.Inventory.ClearDeletedItemList();
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// This method cleans up temp objects if expired and checks the need to auto-return others.
        /// </summary>
        private void PerformCleanup()
        {
            //test returns
            if (ShuttingDown) // if shutting down then there will be nothing to handle the return so leave till next restart
                return;

            List<SceneObjectGroup> derezGroups = new List<SceneObjectGroup>();
            lock (_potentialReturnGroups)
            {
                //first case.  if there are not groups to return, do nothing
                if (_potentialReturnGroups.Count == 0)
                {
                    return;
                }

                List<LinkedListNode<PotentialTimedReturn>> returnedGroups = new List<LinkedListNode<PotentialTimedReturn>>();

                //else, we have parts so we have to return anything valid
                LinkedListNode<PotentialTimedReturn> currNode = _potentialReturnGroups.First;
                do
                {
                    PotentialTimedReturn ret = currNode.Value;

                    //if returns have been turned off, just clear the list
                    ILandObject parcel = LandChannel.GetLandObject(
                        ret.Group.RootPart.GroupPosition.X, ret.Group.RootPart.GroupPosition.Y);

                    //not over anyone's land?
                    if (parcel == null || parcel.landData == null)
                    {
                        returnedGroups.Add(currNode);
                        continue;
                    }

                    parcel.landData.UpdateForcedAutoReturn(RegionInfo.MaxAutoReturn);
                    if (parcel.landData.OtherCleanTime == 0)
                    {
                        //returns have been disabled
                        returnedGroups.Add(currNode);
                        continue;
                    }

                    if (ret.Group.IsDeleted || ret.Group.InTransit)
                    {
                        // Leave queued to check for the next cleanup
                        continue;
                    }

                    if (ret.Group.IsAttachment)
                    {
                        //attachments should NEVER GET HERE
                        returnedGroups.Add(currNode);
                        m_log.WarnFormat("[SCENE] Attachment '{0}' {1} found in autoreturns. This should never happen", ret.Group.Name, ret.Group.UUID);
                        continue;
                    }

                    if (ret.Group.HasSittingAvatars)
                    {
                        //groups being sat upon are considered vehicles and will not be returned
                        returnedGroups.Add(currNode);
                        continue;
                    }

                    if ((RegionInfo.AllowRezzers == ProductRulesWho.OnlyEO) || (RegionInfo.Product == ProductRulesUse.PlusUse))
                    {
                        // The estate OWNER can rez in Scenic and Plus regions is exempt from auto-return.
                        if (IsEstateOwner(ret.Group.OwnerID))
                        {
                            returnedGroups.Add(currNode);
                            continue;
                        }
                        // And estate owner partner on Plus regions (if AllowPartnerRez).
                        if (RegionInfo.AllowPartnerRez && IsEstateOwnerPartner(ret.Group.OwnerID))
                        {
                            returnedGroups.Add(currNode);
                            continue;
                        }
                    }

                    if (RegionInfo.AllowRezzers != ProductRulesWho.OnlyEO)
                    {
                        if (RegionInfo.AllowPartnerRez)
                        {
                            UserProfileData parcelOwner = CommsManager.UserService.GetUserProfile(parcel.landData.OwnerID);
                            if ((parcelOwner == null) || (ret.Group.OwnerID == parcelOwner.Partner))
                            {
                                if (parcelOwner == null)
                                    m_log.WarnFormat("[LAND]: Could not fetch user profile for parcel {0}:'{1}', owner [{2}], auto-return incomplete.",
                                                                parcel.landData.LocalID, parcel.landData.Name, parcel.landData.OwnerID);
                                returnedGroups.Add(currNode);
                                continue;
                            }
                        }
                    }

                    if (parcel.landData.OwnerID != ret.Group.OwnerID &&
                        (parcel.landData.GroupID != ret.Group.GroupID ||
                        parcel.landData.GroupID == UUID.Zero))
                    {
                        // if it's not time for this part, break as the parts are sorted in order of rez time
                        if ((DateTime.Now - ret.TimeRezzed).TotalMinutes < parcel.landData.OtherCleanTime)
                            break;
                        ret.Group.DetachFromBackup();
                        derezGroups.Add(ret.Group); // Do the actual derez outside the _potentialReturnGroups lock
                        returnedGroups.Add(currNode);
                    }
                } while ((currNode = currNode.Next) != null);

                //clear out the returns
                foreach (LinkedListNode<PotentialTimedReturn> ret in returnedGroups)
                {
                    RemoveSinglePotentialReturnWorker(ret.Value.Group);
                }
            }   // end of lock (_potentialReturnGroups)

            // do the actual derez outside the lock
            foreach (SceneObjectGroup group in derezGroups)
            {
                m_log.InfoFormat("[SCENE]: Returning object '{0}' [{1}] due to parcel auto return", group.Name, group.RootPart.UUID.ToString());
                DeRezObject(null, group.RootPart.LocalId, group.RootPart.GroupID, DeRezAction.Return, UUID.Zero);

                UUID NotifyID = (group.OwnerID == group.GroupID) ? group.RootPart.LastOwnerID : group.OwnerID;
                this.AddGridReturnNotificationForUser(NotifyID, 1, group.AbsolutePosition, group.Name, "parcel auto return");
            }
        }

        private void SendReturnGridMessages()
        {
            lock (m_returns)
            {
                foreach (KeyValuePair<UUID, ReturnInfo> ret in m_returns)
                {
                    UUID transaction = UUID.Random();

                    GridInstantMessage msg = new GridInstantMessage();
                    msg.fromAgentID = new Guid(UUID.Zero.ToString()); // From server
                    msg.toAgentID = new Guid(ret.Key.ToString());
                    msg.imSessionID = new Guid(transaction.ToString());
                    msg.timestamp = (uint)Util.UnixTimeSinceEpoch();
                    msg.fromAgentName = "Server";
                    msg.dialog = (byte)19; // Object msg
                    msg.fromGroup = false;
                    msg.offline = (byte)1; //yes, store for fetching missed IMs on login
                    msg.ParentEstateID = RegionInfo.EstateSettings.ParentEstateID;
                    msg.Position = Vector3.Zero;
                    msg.RegionID = RegionInfo.RegionID.Guid;
                    msg.binaryBucket = new byte[0];
                    if (ret.Value.count > 1)
                        msg.message = string.Format("Your {0} objects were returned from {1} in region {2} due to {3}", ret.Value.count, ret.Value.location.ToString(), RegionInfo.RegionName, ret.Value.reason);
                    else
                        msg.message = string.Format("Your object {0} was returned from {1} in region {2} due to {3}", ret.Value.objectName, ret.Value.location.ToString(), RegionInfo.RegionName, ret.Value.reason);

                    IMessageTransferModule tr = RequestModuleInterface<IMessageTransferModule>();
                    if (tr != null)
                        tr.SendInstantMessage(msg, delegate(bool success) { });
                }
                m_returns.Clear();
            }
        }

        /// <summary>
        /// Backup the scene.  This acts as the main method of the backup thread.
        /// </summary>
        /// <returns></returns>
        public void Backup(bool forceBackup)
        {
            try
            {
                // Check for timed backup of region objects or forced backup
                TimeSpan elapsed = DateTime.Now - m_lastBackup;
                if (forceBackup)
                    this.FlushPendingAssetUpdates();
                if (forceBackup || (elapsed.TotalMinutes >= 5))
                    PerformBackup(forceBackup);

                // Now check for temp objects and auto-return
                this.PerformCleanup();
                this.SendReturnGridMessages();
            }
            finally
            {
                m_backingup = false;
            }
        }

        private void ForceBulkSceneObjectBackup(List<SceneObjectGroup> groups)
        {
            SceneObjectGroup.ProcessBulkBackup(groups, m_storageManager.DataStore, true);
        }

        public void AddReturn(UUID agentID, string objectName, Vector3 location, string reason)
        {
            lock (m_returns)
            {
                if (m_returns.ContainsKey(agentID))
                {
                    ReturnInfo info = m_returns[agentID];
                    info.count++;
                    m_returns[agentID] = info;
                }
                else
                {
                    ReturnInfo info = new ReturnInfo();
                    info.count = 1;
                    info.objectName = objectName;
                    info.location = location;
                    info.reason = reason;
                    m_returns[agentID] = info;
                }
            }
        }

        #endregion

        #region Load Terrain

        public void SaveTerrain()
        {
            m_storageManager.DataStore.StoreTerrain(Heightmap.GetDoubles(), RegionInfo.RegionID, Heightmap.RevisionNumber);
        }

        /// <summary>
        /// Loads the World heightmap
        /// </summary>
        public override void LoadWorldMap()
        {
            try
            {
                Tuple<double[,], int> map = m_storageManager.DataStore.LoadTerrain(RegionInfo.RegionID);
                if (map.Item1 == null)
                {
                    m_log.Info("[TERRAIN]: No default terrain. Generating a new terrain.");
                    Heightmap = new TerrainChannel();

                    m_storageManager.DataStore.StoreTerrain(Heightmap.GetDoubles(), RegionInfo.RegionID, Heightmap.IncrementRevisionNumber());
                }
                else
                {
                    Heightmap = new TerrainChannel(map.Item1, map.Item2);
                }
            }
            catch (Exception e)
            {
                m_log.Warn("[TERRAIN]: Scene.cs: LoadWorldMap() - Failed with exception " + e.ToString());
            }
        }

        /// <summary>
        /// Marks the world map as tainted and updates the map tile if enough time has passed.
        /// </summary>
        /// <param name="reason">What is the source of the taint?</param>
        public override void MarkMapTileTainted(WorldMapTaintReason reason)
        {
            IWorldMapModule mapModule = RequestModuleInterface<IWorldMapModule>();

            if (mapModule != null)
            {
                mapModule.MarkMapTileTainted(reason);
            }
        }

        /// <summary>
        /// If the prim qualifies to make a mark on the map, mark the world map as tainted and update the map tile if enough time has passed.
        /// </summary>
        /// <param name="part">The SOP that is being looked at for possibly being the source of the taint.</param>
        public override void MarkMapTileTainted(SceneObjectPart part)
        {
            if (
                part.Shape.PCode != (byte)PCode.Tree && part.Shape.PCode != (byte)PCode.NewTree && part.Shape.PCode != (byte)PCode.Grass
                && (part.Flags & (PrimFlags.Physics | PrimFlags.Temporary | PrimFlags.TemporaryOnRez)) == 0
                && part.Scale.X > 1f && part.Scale.Y > 1f && part.Scale.Z > 1f
                && part.AbsolutePosition.Z - GetGroundAt((int)part.AbsolutePosition.X, (int)part.AbsolutePosition.Y) <= 256f
            )
            {
                // Object qualifies to show on the world map.
                // See MapImageModule::DrawObjectVolume for details on how this is checked.
                MarkMapTileTainted(WorldMapTaintReason.PrimChange);
            }
        }

        /// <summary>
        /// Register this region with a grid service
        /// </summary>
        /// <exception cref="System.Exception">Thrown if registration of the region itself fails.</exception>
        public void RegisterRegionWithGrid()
        {
            RegisterCommsEvents();

            // These two 'commands' *must be* next to each other or sim rebooting fails.
            m_sceneGridService.RegisterRegion(m_interregionCommsOut, RegionInfo);
            Dictionary<string, string> dGridSettings = m_sceneGridService.GetGridSettings();

            if (dGridSettings.ContainsKey("allow_forceful_banlines"))
            {
                if (dGridSettings["allow_forceful_banlines"] != "TRUE")
                {
                    m_log.Info("[GRID]: Grid is disabling forceful parcel banlists");
                    EventManager.TriggerSetAllowForcefulBan(false);
                }
                else
                {
                    m_log.Info("[GRID]: Grid is allowing forceful parcel banlists");
                    EventManager.TriggerSetAllowForcefulBan(true);
                }
            }
        }

        public override void InformNeighborsImUp()
        {
            _surroundingRegions.RefreshNeighborsFromStorage().Wait();
            _surroundingRegions.SendRegionUpToNeighbors();
        }

        /// <summary>
        /// Create a terrain texture for this scene
        /// </summary>
        public void CreateTerrainTexture(bool temporary)
        {
            //create a texture asset of the terrain
            IMapImageGenerator terrain = RequestModuleInterface<IMapImageGenerator>();

            // Cannot create a map for a nonexistant heightmap yet.
            if (Heightmap == null)
                return;

            if (terrain == null)
                return;

            byte[] data = terrain.WriteJpeg2000Image();
            if (data != null)
            {
                IWorldMapModule mapModule = RequestModuleInterface<IWorldMapModule>();

                if (mapModule != null)
                    mapModule.LazySaveGeneratedMaptile(data, temporary);
            }
        }

        #endregion

        #region Load Land

        public void loadAllLandObjectsFromStorage(UUID regionID)
        {
            m_log.Info("[SCENE]: Loading land parcel definitions from storage");
            List<LandData> landData = m_storageManager.DataStore.LoadLandObjects(regionID);

            // Apply any region product type limitations. Fix any cases where land parcels were group tagged
            // or sold before a region began checking whatever current restrictions are supposed to be in place.
            m_log.InfoFormat("[SCENE]: Examining {0} parcels and repairing or applying restricted region product settings as needed...", landData.Count);
            foreach (LandData parcel in landData)
            {
                int origLocalID = parcel.LocalID;
                UUID origParcelID = parcel.GlobalID;
                UUID origOwner = parcel.OwnerID;
                UUID origGroup = parcel.GroupID;
                bool origGroupOwned = parcel.IsGroupOwned;
                uint origFlags = parcel.Flags;

                m_log.WarnFormat("[LAND]: Parcel init {0} [{1}] flags={2} owner={3} group={4} groupOwned={5}",
                    origLocalID, origParcelID, origFlags.ToString("X8"), origOwner, origGroup, origGroupOwned);

                // Override: Parcels in Plus regions are always [x] Public access parcels
                if (RegionInfo.Product == ProductRulesUse.PlusUse)
                    parcel.Flags &= ~(uint)ParcelFlags.UseAccessList;

                if (parcel.OwnerID == UUID.Zero)
                {
                    parcel.OwnerID = RegionInfo.EstateSettings.EstateOwner;
                    parcel.IsGroupOwned = false;
                    parcel.GroupID = UUID.Zero;
                    parcel.AuthBuyerID = UUID.Zero;
                    parcel.ClearSaleInfo();
                    m_log.WarnFormat("[SCENE]: Parcel {0} [{1}] had no owner - repaired (reclaimed) to Estate Owner [{2}]", parcel.LocalID, parcel.GlobalID, parcel.OwnerID);
                }

                // Check for inconsistencies in group-owned parcels.
                if (parcel.IsGroupOwned)
                {
                    if (parcel.GroupID == UUID.Zero)
                        parcel.IsGroupOwned = false;
                    else
                        parcel.OwnerID = parcel.GroupID;
                }

                // These are the region product checks, broken into separate product rules.
                if (RegionInfo.AllowOwners == ProductRulesWho.OnlyEO)
                {
                    // Must be owned by the estate owner
                    parcel.OwnerID = RegionInfo.EstateSettings.EstateOwner;
                    parcel.IsGroupOwned = false;
                    parcel.GroupID = UUID.Zero;
                    parcel.ClearSaleInfo();
                }
                if (!RegionInfo.AllowSales)
                {
                    // Not allowed to be sold.
                    parcel.ClearSaleInfo();
                }
                if (!RegionInfo.AllowDeeding)
                {
                    // Can be owned by someone else or tagged with a group, but not owned by one
                    if (parcel.IsGroupOwned)
                    {
                        parcel.OwnerID = RegionInfo.EstateSettings.EstateOwner;
                        parcel.IsGroupOwned = false;
                    }
                }
                if (!RegionInfo.AllowGroupTags)
                {
                    // Can be owned by someone else but not tagged with a group
                    if (parcel.IsGroupOwned)
                    {
                        parcel.OwnerID = RegionInfo.EstateSettings.EstateOwner;
                        parcel.IsGroupOwned = false;
                    }
                    parcel.GroupID = UUID.Zero;
                }

                // Check for changes
                if (origLocalID != parcel.LocalID)
                    m_log.WarnFormat("[LAND]: Parcel init changed local ID {0} -> {1}", origLocalID, parcel.LocalID);
                if (origParcelID != parcel.GlobalID)
                    m_log.WarnFormat("[LAND]: Parcel init changed parcel ID {0} -> {1}", origParcelID, parcel.GlobalID);
                if (origFlags != parcel.Flags)
                    m_log.WarnFormat("[LAND]: Parcel init changed flags {0} -> {1}", origFlags.ToString("X8"), parcel.Flags.ToString("X8"));
                if (origOwner != parcel.OwnerID)
                    m_log.WarnFormat("[LAND]: Parcel init changed owner {0} -> {1}", origParcelID, parcel.GlobalID);
                if (origGroup != parcel.GroupID)
                    m_log.WarnFormat("[LAND]: Parcel init changed group {0} -> {1}", origGroup, parcel.GroupID);
                if (origGroupOwned != parcel.IsGroupOwned)
                    m_log.WarnFormat("[LAND]: Parcel init changed group-owned {0} -> {1}", origGroupOwned, parcel.IsGroupOwned);
            }   // foreach parcel

            if (LandChannel != null)
            {
                if (landData.Count == 0)
                {
                    EventManager.TriggerNoticeNoLandDataFromStorage();
                }
                else
                {
                    EventManager.TriggerIncomingLandDataFromStorage(landData);
                }
            }
            else
            {
                m_log.Error("[SCENE]: Land Channel is not defined. Cannot load from storage!");
            }
        }

        #endregion

        #region Primitives Methods

        /// <summary>
        /// Adds a group to the taint list to be persisted
        /// </summary>
        /// <param name="group"></param>
        public void SetGroupTainted(SceneObjectGroup group)
        {
            lock (_taintedGroups)
            {
                _taintedGroups.Add(group);
            }
        }

        public void InspectForAutoReturn(SceneObjectGroup group, LandData landData)
        {
            //groups sat upon are considered vehicles and are never returned
            if (group.HasSittingAvatars)
                return;

            landData.UpdateForcedAutoReturn(RegionInfo.MaxAutoReturn);
            if (landData.OtherCleanTime != 0)
            {
                if (landData.OwnerID != group.OwnerID &&
                   (landData.GroupID != group.GroupID || landData.GroupID == UUID.Zero))
                {
                    lock (_potentialReturnGroups)
                    {
                        if (!_returnGroupIndex.ContainsKey(group.UUID))
                        {
                            LinkedListNode<PotentialTimedReturn> groupNode = new LinkedListNode<PotentialTimedReturn>(new PotentialTimedReturn(group));
                            _returnGroupIndex.Add(group.UUID, groupNode);
                            _potentialReturnGroups.AddLast(groupNode);
//                            m_log.WarnFormat("[SCENE]: Added {0} to potential auto-returns.", group.Name);
                        }
                    }
                }
            }
        }

        public void InspectForAutoReturn(SceneObjectGroup group)
        {
            ILandObject parcel = LandChannel.GetLandObject(group.RootPart.GroupPosition.X, group.RootPart.GroupPosition.Y);
            if ((parcel == null) || (parcel.landData == null))
                return;
            InspectForAutoReturn(group, parcel.landData);
        }

        bool IsBadUserLoad(SceneObjectGroup group)
        {
            if (IsBadUser(group.OwnerID))
            {
                m_log.WarnFormat("Refusing to load '{0}', bad owner {1}.", group.Name, group.OwnerID);
                return true;
            }
            return false;
        }

        bool IsBlacklistedLoad(SceneObjectGroup group)
        {
            if (IsBlacklistedOwner(group.OwnerID))
            {
                m_log.WarnFormat("Refusing to load '{0}', blacklisted owner {1}.", group.Name, group.OwnerID);
                return true;
            }
            if (IsBlacklistedName(group.Name))
            {
                m_log.WarnFormat("Refusing to load '{0}', blacklisted name, owned by {1}.", group.Name, group.OwnerID);
                return true;
            }

            foreach (SceneObjectPart part in group.GetParts())
            {
                if (IsBlacklistedCreator(part.CreatorID))
                {
                    m_log.WarnFormat("Refusing to load '{0}', blacklisted creator part {1}, owned by {2}.", group.Name, part.UUID, group.OwnerID);
                    return true;
                }
            };

            return false;
        }

        /// <summary>
        /// Loads the World's objects
        /// </summary>
        public virtual void LoadPrimsFromStorage(UUID regionID)
        {
            m_log.Info("[SCENE]: Loading objects from datastore");

            List<SceneObjectGroup> PrimsFromDB = m_storageManager.DataStore.LoadObjects(regionID);
            List<uint> returnIds = new List<uint>();
            foreach (SceneObjectGroup group in PrimsFromDB)
            {
                if (group.RootPart == null)
                {
                    m_log.ErrorFormat("[SCENE] Found a SceneObjectGroup with m_rootPart == null and {0} children",
                                      group.GetParts().Count);
                }

                if (IsBadUserLoad(group) || IsBlacklistedLoad(group))
                    continue;   // already reported above

                group.ForEachPart(delegate(SceneObjectPart part)
                {
                    /// This fixes inconsistencies between this part and the root part.
                    /// In the past, there was a bug in Link operations that did not force
                    /// these permissions on child prims when linking.
                    part.SyncChildPermsWithRoot();
                });

                AddRestoredSceneObject(group, true, true);
                SceneObjectPart rootPart = group.GetChildPart(group.UUID);
                group.RecalcScriptedStatus();
                rootPart.TrimPermissions();

                // Check if it rezzed off-world...
                Vector3 groupPos = group.AbsolutePosition;
                if (!Util.IsValidRegionXY(groupPos))
                {
                    m_log.ErrorFormat("[SCENE]: Returning off-world object '{0}' at {1},{2},{3} owner={4}",
                        group.Name, groupPos.X, groupPos.Y, groupPos.Z, group.OwnerID.ToString());
                    UUID NotifyID = (group.OwnerID == group.GroupID) ? group.RootPart.LastOwnerID : group.OwnerID;
                    AddReturn(NotifyID, group.Name, group.AbsolutePosition, "objects found off-world at region start");
                    returnIds.Add(group.RootPart.LocalId);
                }
            }
            m_log.Info("[SCENE]: Loaded " + PrimsFromDB.Count.ToString() + " SceneObject(s)");
            m_log.InfoFormat("[STATS]: PHYSICS_PRIMS_VERTS,{0},{1}", PhysicsScene.Mesher.TotalProcessedPrims, PhysicsScene.Mesher.TotalProcessedVerts);

            // Now clean up the off-world objects
            if (returnIds.Count > 0)
                DeRezObjects(null, returnIds, UUID.Zero, DeRezAction.Return, UUID.Zero);

            //begin simulating
            m_sceneGraph.PhysicsScene.Simulating = true;
        }

        public Vector3 GetRezLocationWithBoundingBox(UUID AgentId, Vector3 rayStart, Vector3 rayEnd, Vector3 scale)
        {
            float waterLevel = Convert.ToSingle(this.RegionInfo.RegionSettings.WaterHeight);
            // First check to see if we are trying to rez underwater
            if (rayEnd.Z <= waterLevel)
            {
                // Can be smarter about where to place the object based on the avatar?
                ScenePresence agent = null;
                if (AgentId != UUID.Zero)
                    agent = GetScenePresence(AgentId);

                // Check if the user camera is above or below the water.
                if ((agent != null) && (agent.CameraPosition.Z >= waterLevel))
                {
                    // User is rezzing and their camera is above the waterline. So
                    // calculate where the ray crosses the waterline and stop it there.
                    float rayDeltaZ = rayStart.Z - rayEnd.Z;
                    float waterDeltaZ = rayStart.Z - waterLevel;
                    Vector3 rayDelta = rayStart - rayEnd;
                    if (rayDeltaZ != 0.0f)
                    {
                        Vector3 waterDelta = rayDelta * (waterDeltaZ / rayDeltaZ);
                        rayEnd = rayStart - waterDelta; // update our target
                    }
                }
            }

            //offsets Z axis by bounding box
            Vector3 zoffset = new Vector3(0, 0, scale.Z / 2.0f);
            return rayEnd + zoffset;
        }

        public Vector3 GetNewRezLocation(Vector3 RayStart, Vector3 RayEnd, UUID RayTargetID, Quaternion rot, byte bypassRayCast, byte RayEndIsIntersection,
            bool frontFacesOnly, Vector3 scale, bool FaceCenter, UUID AgentId)
        {
            Vector3 pos = Vector3.Zero;
            if (RayEndIsIntersection == (byte)1)
            {
                pos = RayEnd;
                return pos;
            }

            if (RayTargetID != UUID.Zero)
            {
                SceneObjectPart target = GetSceneObjectPart(RayTargetID);

                Vector3 direction = Vector3.Normalize(RayEnd - RayStart);
                Vector3 AXOrigin = new Vector3(RayStart.X, RayStart.Y, RayStart.Z);
                Vector3 AXdirection = new Vector3(direction.X, direction.Y, direction.Z);

                if (target != null)
                {
                    pos = target.AbsolutePosition;
                    //m_log.Info("[OBJECT_REZ]: TargetPos: " + pos.ToString() + ", RayStart: " + RayStart.ToString() + ", RayEnd: " + RayEnd.ToString() + ", Volume: " + Util.GetDistanceTo(RayStart,RayEnd).ToString() + ", mag1: " + Util.GetMagnitude(RayStart).ToString() + ", mag2: " + Util.GetMagnitude(RayEnd).ToString());

                    // TODO: Raytrace better here

                    //EntityIntersection ei = m_sceneGraph.GetClosestIntersectingPrim(new Ray(AXOrigin, AXdirection));
                    Ray NewRay = new Ray(AXOrigin, AXdirection);

                    // Ray Trace against target here
                    EntityIntersection ei = target.TestIntersectionOBB(NewRay, Quaternion.Identity, frontFacesOnly, FaceCenter);

                    // Un-comment out the following line to Get Raytrace results printed to the console.
                    // m_log.Info("[RAYTRACERESULTS]: Hit:" + ei.HitTF.ToString() + " Point: " + ei.ipoint.ToString() + " Normal: " + ei.normal.ToString());
                    float ScaleOffset = 0.5f;

                    // If we hit something
                    if (ei.HitTF)
                    {
                        Vector3 scaleComponent = new Vector3(ei.AAfaceNormal.X, ei.AAfaceNormal.Y, ei.AAfaceNormal.Z);
                        if (scaleComponent.X != 0) ScaleOffset = scale.X;
                        if (scaleComponent.Y != 0) ScaleOffset = scale.Y;
                        if (scaleComponent.Z != 0) ScaleOffset = scale.Z;
                        ScaleOffset = Math.Abs(ScaleOffset);
                        Vector3 intersectionpoint = new Vector3(ei.ipoint.X, ei.ipoint.Y, ei.ipoint.Z);
                        Vector3 normal = new Vector3(ei.normal.X, ei.normal.Y, ei.normal.Z);
                        // Set the position to the intersection point
                        Vector3 offset = (normal * (ScaleOffset / 2f));
                        pos = (intersectionpoint + offset);
                    }

                    return pos;
                }
                else
                {
                    // We don't have a target here, so we're going to raytrace all the objects in the scene.

                    EntityIntersection ei = m_sceneGraph.GetClosestIntersectingPrim(new Ray(AXOrigin, AXdirection), true, false);

                    // Un-comment the following line to print the raytrace results to the console.
                    //m_log.Info("[RAYTRACERESULTS]: Hit:" + ei.HitTF.ToString() + " Point: " + ei.ipoint.ToString() + " Normal: " + ei.normal.ToString());

                    if (ei.HitTF)
                    {
                        pos = new Vector3(ei.ipoint.X, ei.ipoint.Y, ei.ipoint.Z);
                    } else
                    {
                        // fall back to our stupid functionality
                        pos = GetRezLocationWithBoundingBox(AgentId, RayStart, RayEnd, scale);
                    }

                    return pos;
                }
            }
            else
            {
                // fall back to our stupid functionality
                pos = GetRezLocationWithBoundingBox(AgentId, RayStart, RayEnd, scale);
                return pos;
            }
        }

        public bool CheckLandImpact(ILandObject parcel, int landImpact, out string reason)
        {
            int primsTotal = parcel.getSimulatorMaxPrimCount(parcel);
            int regionLimit = this.RegionInfo.PrimLimit;
            int regionUsed = this.RegionInfo.PrimTotal;
            int primsUsed = parcel.landData.SimwidePrims;
//            m_log.WarnFormat("[PrimLimit]: Total={0} Owner={1} Group={2} Other={3} Selected {4} (UsedTotal={5} SimwidePrims={6})",
//                primsTotal, parcel.landData.OwnerPrims, parcel.landData.GroupPrims, parcel.landData.OtherPrims, parcel.landData.SelectedPrims, primsUsed, parcel.landData.SimwidePrims);

            // Enforce the region-wide limit in all cases.
            if ((regionLimit > 0) && (regionUsed + landImpact > regionLimit))
            {
                reason = "region";
                return false;   // Absolute limit reached (e.g. 45K prims)
            }

            // Check if prims needed exceeds prims available to that parcel owner
            if (this.RegionInfo.EnforcePrimLimits && (landImpact + primsUsed > primsTotal))
            {
                reason = "parcel";
                return false;
            }

            reason = String.Empty;
            return true;
        }

        public const int REZ_OK = 0;
        public const int REZ_NOT_PERMITTED = 1;
        public const int REZ_PARCEL_LAND_IMPACT = 2;
        public const int REZ_REGION_LAND_IMPACT = 3;
        public const int REZ_NO_LAND_PARCEL = 4;
        public const int REZ_REGION_SCENIC = 5;
        public virtual int CheckRezError(UUID ownerID, UUID objectID, Vector3 pos, bool isTemp, int landImpact)
        {
            if (IsBadUser(ownerID))
                return REZ_NOT_PERMITTED;

            if (!Permissions.CanRezObject(0, ownerID, objectID, pos, false))
            {
                if (RegionInfo.Product == ProductRulesUse.ScenicUse)
                    return REZ_REGION_SCENIC;
                else
                    return REZ_NOT_PERMITTED;
            }
            string reason = String.Empty;
            ILandObject parcel = LandChannel.GetLandObject(pos.X, pos.Y);
            if (parcel == null)
                return REZ_NO_LAND_PARCEL;

            if (!CheckLandImpact(parcel, landImpact, out reason))
            {
                if (reason == "region")
                    return REZ_REGION_LAND_IMPACT;
                else
                    return REZ_PARCEL_LAND_IMPACT;
            }

            return REZ_OK;
        }

        public virtual void AddNewPrim(UUID ownerID, UUID groupID, Vector3 RayEnd, Quaternion rot, PrimitiveBaseShape shape,
                                       byte bypassRaycast, Vector3 RayStart, UUID RayTargetID,
                                       byte RayEndIsIntersection, IClientAPI remoteClient)
        {
            Vector3 pos = GetNewRezLocation(RayStart, RayEnd, RayTargetID, rot, bypassRaycast, RayEndIsIntersection, true, new Vector3(0.5f, 0.5f, 0.5f), false, remoteClient.AgentId);
            string reason = String.Empty;
            int landImpact = 1;

            if (IsBadUser(ownerID))
                return;

            // Pass 0 for landImpact here so that it can be tested separately.
            if (Permissions.CanRezObject(0, ownerID, UUID.Zero, pos, false))
            {
                reason = ". Cannot determine land parcel at "+(int)pos.X+","+(int)pos.Y;
                ILandObject parcel = LandChannel.GetLandObject(pos.X, pos.Y);
                if (parcel != null)
                {
                    if (CheckLandImpact(parcel, landImpact, out reason))
                    {
                        AddNewPrim(ownerID, groupID, pos, rot, shape, true, true);
                        return;
                    }
                    if (reason == "region")
                        reason = " because it would exceed the region land impact (LI) limit.";
                    else
                        reason = " because it would exceed the land impact (LI) limit.";
                }
            }
            else
            {
                if (RegionInfo.Product == ProductRulesUse.ScenicUse)
                    reason = ".  Only the Estate Owner or partner can create objects on a scenic region.";
                else
                    reason = ".  The owner of this land does not allow it.  Use About Land to see land parcel ownership.";
            }

            if (remoteClient != null)
            {
                string spos = Convert.ToInt16(pos.X).ToString() + "," + Convert.ToInt16(pos.Y).ToString() + "," + Convert.ToInt16(pos.Z).ToString();
                remoteClient.SendAlertMessage("You cannot create object at <" + spos + "> " + reason);
            }
        }

        public void ApplyDefaultPerms(SceneObjectPart part)
        {
            // Try to apply the avatar's preferred default permissions from AgentPrefs.
            ScenePresence sp = GetScenePresence(part.OwnerID);
            if (sp == null) return;
            AgentPreferencesData prefs = sp.AgentPrefs;
            if (prefs == null) return;

            part.NextOwnerMask = prefs.PermNextOwner & part.BaseMask;
            part.GroupMask = prefs.PermGroup & part.BaseMask;
            part.EveryoneMask = prefs.PermEveryone & part.BaseMask;

            // Check if trying to set the Export flag.
            if ((prefs.PermEveryone & (uint)PermissionMask.Export) != 0)
            {
                // Attempting to set export flag.
                if ((part.OwnerMask & (uint)PermissionMask.Export) == 0 || (part.BaseMask & (uint)PermissionMask.Export) == 0 || (part.NextOwnerMask & (uint)PermissionMask.All) != (uint)PermissionMask.All)
                    part.EveryoneMask &= ~(uint)PermissionMask.Export;
            }

            // If the owner has not provided full rights (MCT) for NextOwner, clear the Export flag.
            if ((part.NextOwnerMask &(uint)PermissionMask.All) != (uint)PermissionMask.All)
                part.EveryoneMask &= ~(uint)PermissionMask.Export;
        }

        public virtual SceneObjectGroup AddNewPrim(
            UUID ownerID, UUID groupID, Vector3 pos, Quaternion rot, PrimitiveBaseShape shape, bool rezSelected, bool sessionPerms)
        {
            //m_log.DebugFormat(
            //    "[SCENE]: Scene.AddNewPrim() pcode {0} called for {1} in {2}", shape.PCode, ownerID, RegionInfo.RegionName);

            // If an entity creator has been registered for this prim type then use that
            if (m_entityCreators.ContainsKey((PCode)shape.PCode))
                return m_entityCreators[(PCode)shape.PCode].CreateEntity(ownerID, groupID, pos, rot, shape);

            // Otherwise, use this default creation code;
            SceneObjectGroup sceneObject = new SceneObjectGroup(ownerID, pos, rot, shape, rezSelected);
            if (sessionPerms) ApplyDefaultPerms(sceneObject.RootPart);
            sceneObject.SetGroup(groupID, null);
            AddNewSceneObject(sceneObject, true);

            return sceneObject;
        }

        /// <summary>
        /// Add an object into the scene that has come from storage
        /// </summary>
        ///
        /// <param name="sceneObject"></param>
        /// <param name="attachToBackup">
        /// If true, changes to the object will be reflected in its persisted data
        /// If false, the persisted data will not be changed even if the object in the scene is changed
        /// </param>
        /// <param name="alreadyPersisted">
        /// If true, we won't persist this object until it changes
        /// If false, we'll persist this object immediately
        /// </param>
        /// <returns>
        /// true if the object was added, false if an object with the same uuid was already in the scene
        /// </returns>
        public bool AddRestoredSceneObject(
            SceneObjectGroup sceneObject, bool attachToBackup, bool alreadyPersisted)
        {
            return m_sceneGraph.AddRestoredSceneObject(sceneObject, attachToBackup, alreadyPersisted);
        }

        public bool AddSceneObjectFromOtherSceneToSceneGraph(SceneObjectGroup sceneObject, bool attachToBackup)
        {
            if (attachToBackup)
            {
                sceneObject.ForceInventoryPersistence();
                sceneObject.HasGroupChanged = true;
            }

            return m_sceneGraph.AddGroupToSceneGraph(sceneObject, attachToBackup, false, false, true);
        }

        /// <summary>
        /// Add a newly created object to the scene
        /// </summary>
        /// <param name="sceneObject"></param>
        /// <param name="attachToBackup">
        /// If true, the object is made persistent into the scene.
        /// If false, the object will not persist over server restarts
        /// </param>
        public bool AddNewSceneObject(SceneObjectGroup sceneObject, bool attachToBackup)
        {
            return m_sceneGraph.AddNewSceneObject(sceneObject, attachToBackup);
        }

        /// <summary>
        /// Delete every object from the scene
        /// </summary>
        public void DeleteAllSceneObjects()
        {
            lock (Entities)
            {
                ICollection<EntityBase> entities = new List<EntityBase>(Entities);

                foreach (EntityBase e in entities)
                {
                    if (e is SceneObjectGroup)
                    {
                        SceneObjectGroup sog = (SceneObjectGroup)e;
                        if (!sog.IsAttachment)
                            DeleteSceneObject(sog, false);
                    }
                }
            }
        }

        /// <summary>
        /// Provides a delegate to determine which objects to SKIP/EXCLUDE from the deletions.
        /// </summary>
        /// <param name="target">An object to test.</param>
        /// <returns>Returns true if the target object should be excluded from the deletions.</returns>
        public delegate bool SOGFilter( SceneObjectGroup target);

        /// <summary>
        /// Delete every object from the scene except those matching callback
        /// </summary>
        public void DeleteAllSceneObjectsExcept(SOGFilter ShouldSkipDelete)
        {
            lock (Entities)
            {
                ICollection<EntityBase> entities = new List<EntityBase>(Entities);

                foreach (EntityBase e in entities)
                {
                    if (e is SceneObjectGroup)
                    {
                        SceneObjectGroup sog = (SceneObjectGroup)e;
                        if (!sog.IsAttachment)
                            if (!ShouldSkipDelete(sog))
                                DeleteSceneObject(sog, false);
                    }
                }
            }
        }

        /// <summary>
        /// Synchronously delete the given object from the scene.
        /// If persist is true, also immediately pulls the object from the DB.
        /// </summary>
        /// <param name="group">The scene object in question.</param>
        /// <param name="silent">Suppress broadcasting changes to other clients.</param>
        /// <param name="fromCrossing">True if removing object due to it leaving the region.</param>
        /// <param name="silent">True when the database should be updated.</param>
        public void DeleteSceneObject(SceneObjectGroup group, bool silent, bool fromCrossing, bool persist)
        {
            if (!group.IsAttachment)    // Optimization, can't sit on something you're wearing
            {
                // Unsit the avatars sitting on the parts
                group.ForEachSittingAvatar((ScenePresence sp) =>
                {
                    if (!sp.IsChildAgent)
                        sp.StandUp(fromCrossing, false);
                });
            }

            // Serialize calls to RemoveScriptInstances to avoid
            // deadlocking on m_parts inside SceneObjectGroup
            lock (m_deleting_scene_object)
            {
                group.RemoveScriptInstances();
            }

            PhysicsActor physActor = group.RootPart.PhysActor;

            if (RemoveSceneObject(group, false, persist, fromCrossing))
            {
                EventManager.TriggerObjectBeingRemovedFromScene(group);
                EventManager.TriggerParcelPrimCountTainted();
            }

            group.DeleteGroup(silent, fromCrossing);

            if (physActor != null)
            {
                PhysicsScene.RemovePrim(physActor);
            }
        }

        /// <summary>
        /// Normal, non-crossing method to synchronously delete the given object from the scene.
        /// Also immediately pulls the object from the DB.
        /// </summary>
        /// <param name="group">The scene object in question.</param>
        /// <param name="silent">Suppress broadcasting changes to other clients.</param>
        public void DeleteSceneObject(SceneObjectGroup group, bool silent)
        {
            DeleteSceneObject(group, silent, false, true);
        }

        /// <summary>
        /// Synchronously delete the given object from the scene.  Does not pull the object
        /// from the DB
        /// </summary>
        /// <param name="group">Object Id</param>
        /// <param name="silent">Suppress broadcasting changes to other clients.</param>
        public void DeleteSceneObjectNoPersist(SceneObjectGroup group, bool silent)
        {
            this.DeleteSceneObject(group, silent, false, false);
        }

        public void DeleteSceneObjects(IEnumerable<SceneObjectGroup> groups, bool silent)
        {
            foreach (SceneObjectGroup group in groups)
            {
                this.DeleteSceneObjectNoPersist(group, silent);
            }

            m_storageManager.DataStore.BulkRemoveObjects(groups);
        }

        /// <summary>
        /// Unlink the given object from the scene.
        /// </summary>
        /// <param name="uuid">Id of object.</param>
        /// <returns>true if the object was in the scene, false if it was not</returns>
        /// <param name="softDelete">If true, only deletes from scene, but keeps object in database.</param>
        /// <param name="persist">Whether or not to make database modifications</param>
        public bool RemoveSceneObject(SceneObjectGroup group, bool softDelete, bool persist, bool fromCrossing)
        {
            if (persist)
            {
                RemoveFromPotentialReturns(group);

                if (!softDelete && group.IsPersisted)
                {
                    m_storageManager.DataStore.RemoveObject(group.UUID,
                                                            m_regInfo.RegionID);
                }
            }

            return m_sceneGraph.RemoveGroupFromSceneGraph(group.LocalId, softDelete, fromCrossing);
        }

        // Bad user list will auto-clear after an hour rather than clearing immediately after a nuke command completes.
        // This gives other regions a chance to issue the nuke as well, otherwise this one will let the objects back.
        private List<UUID> _badUsers = new List<UUID>();
        private DateTime _lastBad = new DateTime();
        public void AddBadUser(UUID agentId)
        {
            lock (_badUsers)
            {
                if (!_badUsers.Contains(agentId))
                    _badUsers.Add(agentId);
                _lastBad = DateTime.Now;
            }
        }
        public bool IsBadUser(UUID agentId)
        {
            if (IsBlacklistedOwner(agentId))
                return true;

            lock (_badUsers)
            {
                TimeSpan elapsed = DateTime.Now.Subtract(_lastBad);
                if (elapsed.TotalMinutes >= 60)
                {
                    _badUsers.Clear();
                    return false;
                }
                return _badUsers.Contains(agentId);
            }
        }

        public float GetGroundAt(int x, int y)
        {
            // Clamp to valid position
            if (x < 0)
                x = 0;
            else if (x >= this.Heightmap.Width)
                x = this.Heightmap.Width - 1;
            if (y < 0)
                y = 0;
            else if (y >= this.Heightmap.Height)
                y = this.Heightmap.Height - 1;

            return (float)this.Heightmap[x, y];
        }

        public Vector3 NearestLegalPos(Vector3 pos)
        {
            int maxX = this.Heightmap.Width - 1;
            int maxY = this.Heightmap.Height - 1;
            Vector3 best = pos;

            if (best.X > (float)maxX)
                best.X = (float)maxX;
            if (best.Y > (float)maxY)
                best.Y = (float)maxY;

            float minZ = GetGroundAt((int)best.X, (int)best.Y);
            if (best.X < 0.0f)
                best.X = 0.0f;
            if (best.Y < 0.0f)
                best.Y = 0.0f;
            if (best.Z < minZ)
                best.Z = minZ;

            return best;
        }

        /// <summary>
        /// Move the given scene object into a new region depending on which region its absolute position has moved
        /// into.
        ///
        /// This method locates the new region handle and offsets the prim position for the new region
        /// </summary>
        /// <param name="attemptedPosition">the attempted out of region position of the scene object</param>
        /// <param name="grp">the scene object that we're crossing</param>
        public bool CrossPrimGroupIntoNewRegion(Vector3 attemptedPosition, SceneObjectGroup grp, bool silent)
        {
            if (grp == null)
                return false;
            if (grp.IsDeleted)
                return false;

            int thisx = (int)RegionInfo.RegionLocX;
            int thisy = (int)RegionInfo.RegionLocY;
            int newx = thisx;
            int newy = thisy;
            ulong newRegionHandle = 0;
            Vector3 pos = attemptedPosition;

            if (attemptedPosition.X >= Constants.OUTSIDE_REGION)
            {
                // x + 1
                newx = thisx + 1;
                pos.X = ((pos.X - Constants.RegionSize));
            }
            else if (attemptedPosition.X < Constants.OUTSIDE_REGION_NEGATIVE_EDGE)
            {
                newx = thisx - 1;
                pos.X = ((pos.X + Constants.RegionSize));
            }

            if (attemptedPosition.Y >= Constants.OUTSIDE_REGION)
            {
                newy = thisy + 1;
                pos.Y = ((pos.Y - Constants.RegionSize));
            }
            else if (attemptedPosition.Y < Constants.OUTSIDE_REGION_NEGATIVE_EDGE)
            {
                newy = thisy - 1;
                pos.Y = ((pos.Y + Constants.RegionSize));
            }

            if (newx < 0 || newy < 0)
            {
                return false;
            }

            newRegionHandle = Util.RegionHandleFromLocation((uint)newx, (uint)newy);

            // If after adjusting the changed coordinates by RegionSize, if still out of range it's too big.
            if (!Util.IsValidRegionXY(pos))
            {
                m_log.ErrorFormat("Cannot cross group into new region, offset too large: {0} {1} at {2}", grp.Name, grp.UUID.ToString(), pos.ToString());
                this.returnObjects(new SceneObjectGroup[1] { grp }, "object went off world");
                return false;
            }

            //is it actually crossing anywhere?

            // Offset the positions for the new region across the border
            Vector3 oldGroupPosition = grp.RootPart.GroupPosition;
            ulong started = Util.GetLongTickCount();

            bool crossed = CrossPrimGroupIntoNewRegion(newRegionHandle, grp, silent, pos, false);
            m_log.InfoFormat("[SCENE]: Crossing for object took {0}ms: {1}", Util.GetLongTickCount()-started, grp.Name);

            // If we fail to cross the border, then reset the position of the scene object on that border.
            if (!crossed)
            {
                //physics will reset the prim position itself
                if (grp.RootPart.PhysActor == null)
                {
                    Vector3 safepos = NearestLegalPos(oldGroupPosition);
                    grp.OffsetForNewRegion(safepos);// no effect during transit
                }
                grp.ScheduleGroupForFullUpdate(PrimUpdateFlags.ForcedFullUpdate);
                return false;
            }

            return true;
        }

        public bool PrecheckAvatarCrossing(ScenePresence avatar, out String result)
        {
            //assert that this avatar is fully in this region before beginning a send
            if (avatar.Connection.State != OpenSim.Framework.AvatarConnectionState.Established)
            {
                result = "Can not begin transition to a new region while already in transit";
                return false;
            }

            //assert that this avatar is ready to leave the region
            if (!avatar.CanExitRegion)
            {
                result = "Can not move to a new region, until established in the current region";
                return false;
            }

            //assert that the dest region is available and this avatar has an established connection to that region
            if (avatar.RemotePresences.HasConnectionsEstablishing())
            {
                result = "Can not move to a new region, connections are still being established";
                return false;
            }

            result = String.Empty;
            return true;
        }

        public bool PrecheckCrossing(SceneObjectGroup grp, out String result)
        {
            bool rc = true;
            string result_msg = String.Empty;
            grp.ForEachSittingAvatar(delegate (ScenePresence avatar)
                {
                    // result is effectively an AND of all avatar prechecks.
                    if (rc)
                        rc = PrecheckAvatarCrossing(avatar, out result_msg);
                });
            result = result_msg;
            return rc;
        }

        /// <summary>
        /// Move the given scene object into a new region
        /// </summary>
        /// <param name="newRegionHandle"></param>
        /// <param name="grp">Scene Object Group that we're crossing</param>
        /// <returns>
        /// true if the crossing itself was successful, false on failure
        /// FIMXE: we still return true if the crossing object was not successfully deleted from the originating region
        /// </returns>
        public bool CrossPrimGroupIntoNewRegion(ulong newRegionHandle, SceneObjectGroup grp, bool silent, Vector3 posInOtherRegion,
            bool isAttachment)
        {
            bool success = false;

            if (newRegionHandle != 0)
            {
                bool wasPersisted = grp.IsPersisted;

                string result = String.Empty;
                if (!PrecheckCrossing(grp, out result))
                {
                    m_log.WarnFormat("[CROSSING]: {0} {1}", grp.Name, result);
                    grp.ForcePositionInRegion();
                    return false;
                }

                m_sceneGraph.RemoveGroupFromSceneGraph(grp.LocalId, false, true);
                SceneGraph.RemoveFromUpdateList(grp);

                grp.AvatarsToExpect = grp.AvatarCount;
                List<UUID> avatarIDs = new List<UUID>();
                grp.ForEachSittingAvatar(delegate (ScenePresence avatar)
                {
                    avatarIDs.Add(avatar.UUID);
                });

                //marks the sitting avatars in transit, and waits for this group to be sent
                //before sending the avatars over to the neighbor region
                Task avSendTask = grp.BeginCrossSittingAvatars(newRegionHandle);

                if (!grp.IsAttachment)
                    m_log.InfoFormat("{0}: Sending create for prim group {1} {2} {3} with {4} users", RegionInfo.RegionName, grp.Name, grp.UUID, grp.LocalId, grp.AvatarsToExpect);

                success = m_interregionCommsOut.SendCreateObject(newRegionHandle, grp, avatarIDs, true, posInOtherRegion, isAttachment);

                if (success)
                {
                    //separate kill step in case this object left the view of other avatars
                    var regionInfo = SurroundingRegions.GetKnownNeighborByHandle(newRegionHandle);

                    if (regionInfo != null)
                    {
                        //note that this won't affect riding avatars in transit.  there is a protection
                        this.SendKillObject(grp.LocalId, regionInfo);
                    }

                    //we sent the object over, let the transit avatars know so that they can proceed
                    grp.ForEachSittingAvatar((ScenePresence avatar) =>
                    {
                        m_transitController.HandleObjectSendResult(avatar.UUID, true);
                    });

                    WaitReportCrossingErrors(avSendTask);

                    // Even if CrossSittingAvatars failed for at least one user, we MUST delete the group since we successfully sent the Create above.
                    // We remove the object here
                    try
                    {
                        if (!grp.IsAttachment)
                            m_log.InfoFormat("{0}: Deleting group {1} {2} {3} after crossings complete", RegionInfo.RegionName, grp.Name, grp.UUID.ToString(), grp.LocalId.ToString());
                        DeleteSceneObject(grp, silent, true, true);
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat(
                            "[INTERREGION]: Exception deleting the old object left behind on a border crossing for {0}, {1}",
                            grp, e);
                    }
                }
                else
                {
                    m_log.ErrorFormat("[INTERREGION]: Prim crossing from {0} failed for {1} {2} {3}", RegionInfo.RegionName, grp.Name, grp.UUID.ToString(), grp.LocalId.ToString());
                    // Restore the prim in the scene graph that was removed above at the start (but not deleted).

                    m_sceneGraph.AddGroupToSceneGraph(grp, !grp.RootPart.IsTemporary, wasPersisted, false, true);
                    grp.ForcePositionInRegion();
                    CheckIsOffworld(grp);
                    CheckDieAtEdge(grp);

                    grp.CrossingFailure();

                    //the object failed to send. let the transit controller know so it can stop trying to send the avatars
                    grp.ForEachSittingAvatar((ScenePresence avatar) =>
                    {
                        m_transitController.HandleObjectSendResult(avatar.UUID, false);
                    });

                    WaitReportCrossingErrors(avSendTask);
                }
            }
            else
            {
                m_log.ErrorFormat("[INTERREGION]: CrossPrimGroupIntoNewRegion from {0}, region handle was unexpectedly 0 for crossing {1} {2} {3}", RegionInfo.RegionName, grp.Name, grp.UUID.ToString(), grp.LocalId.ToString());
            }

            return success;
        }

        private static void WaitReportCrossingErrors(Task avSendTask)
        {
            try
            {
                avSendTask.Wait();
            }
            catch (AggregateException ae)
            {
                foreach (var e in ae.InnerExceptions)
                {
                    m_log.ErrorFormat("[SCENE]: Error crossing avatar: {0}", e);
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[SCENE]: Error crossing avatars: {0}", e);
            }
        }

        public void CheckDieAtEdge(SceneObjectGroup grp)
        {
            if (grp.RootPart.DIE_AT_EDGE)
            {
                // We remove the object here
                try
                {
                    DeleteSceneObject(grp, false);
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("[SCENE]: exception when trying to remove DIE_AT_EDGE prim: {0}", e);
                }
                return;
            }
        }

        public void CheckIsOffworld(SceneObjectGroup grp)
        {
            if (grp.AbsolutePosition.Z < NEGATIVE_OFFWORLD_Z ||
                grp.AbsolutePosition.Z > POSITIVE_OFFWORLD_Z)
            {
                if (grp.RootPart.IsTemporary || IsBadUser(grp.OwnerID))
                {
                    this.DeleteSceneObject(grp, false);
                }
                else
                {
                    this.returnObjects(new SceneObjectGroup[] { grp }, "objects went off world");
                }
            }
        }

        public bool IncomingCreateObject(ISceneObject sog, List<UUID> avatars)
        {
            SceneObjectGroup newObject;
            try
            {
                newObject = (SceneObjectGroup)sog;
                m_log.Info("[SCENE]: IncomingCreateObject at " + (newObject.IsAttachment ? newObject.RawGroupPosition.ToString() : newObject.AbsolutePosition.ToString()) + " attached=" + newObject.IsAttachment.ToString() + " deleted=" + newObject.IsDeleted.ToString() + " name " + newObject.Name);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[SCENE]: Problem casting object: {0}", e.Message);
                return false;
            }

            string reason = "error";
            // Check this *before* calling TriggerIncomingAvatarsOnGroup
            if (!this.AuthorizeUserObject(newObject, avatars, out reason))
            {
                newObject.AvatarsToExpect = 0;
                m_log.ErrorFormat("[SCENE]: Denied adding scene object {0} in {1}: {2}", sog.UUID.ToString(), RegionInfo.RegionName, reason);
                return false;
            }

            newObject.RootPart.ResetExpire();   // don't expire during transit or immediately after

            if (newObject.AvatarsToExpect > 0)
            {
                //Triggered before the object is actually added to the scene to prepare modules
                EventManager.TriggerIncomingAvatarsOnGroup(newObject, newObject.AvatarsToExpect);
            }

            if (!AddSceneObjectFromOtherRegion(newObject.UUID, newObject, false))
            {
                // Shouldn't really happen after the AuthorizeUserObject check above.
                m_log.ErrorFormat("[SCENE]: Problem adding scene object {0} in {1} ", sog.UUID.ToString(), RegionInfo.RegionName);
                return false;
            }

            //check for any avatars coming over on the prim
            if (newObject.AvatarsToExpect > 0)
            {
                newObject.RootPart.ParentGroup.CreateScriptInstances(null, ScriptStartFlags.FromCrossing, DefaultScriptEngine, (int)ScriptStateSource.PrimData, null);

                //there are avatars coming over, they need an immediate update
                foreach (var userId in avatars)
                {
                    ScenePresence sp = GetScenePresence(userId);
                    if (sp != null)
                        newObject.SendFullUpdateToClientImmediate(sp.ControllingClient, true);
                }
            }
            else
            {
                newObject.RootPart.ParentGroup.CreateScriptInstances(null, ScriptStartFlags.FromCrossing, DefaultScriptEngine, (int)ScriptStateSource.PrimData, null);
            }


            return true;
        }

        public virtual bool IncomingCreateObject(UUID userID, UUID itemID)
        {
            ScenePresence sp = GetScenePresence(userID);
            if (sp != null)
            {
                sp.ControllingClient.RunAttachmentOperation(() =>
                {
                    uint attPt = (uint)sp.Appearance.GetAttachpoint(itemID);
                    bool append = true;

                    SceneObjectGroup sog = this.RezSingleAttachmentSync(sp.ControllingClient, itemID, attPt, append);
                    if (sog != null)
                    {
                        IAvatarFactory ava = RequestModuleInterface<IAvatarFactory>();
                        if (ava != null)
                            ava.UpdateDatabase(sp.ControllingClient.AgentId, sp.Appearance, null, null);
                    }
                });
            }

            return false;
        }

        public bool AddSceneObjectFromOtherRegion(UUID primID, SceneObjectGroup sceneObject, bool fromTeleport)
        {
            if (!sceneObject.IsAttachment)
            {
                // If we've taken too long to rez the object, don't allow it to rez.
                ulong elapsedTime = (Util.GetLongTickCount() - sceneObject.TimeReceived);
                if (elapsedTime > RegionClient.REZ_OBJECT_TIMEOUT)
                {
                    m_log.InfoFormat("[INTERREGION]: Prim crossing for user {0} took too long: {1} [{2}]", sceneObject.OwnerID, sceneObject.LocalId, sceneObject.UUID);
                    return false;
                }

                // If the user is banned, we won't let any of their objects enter.
                if (IsBadUserLoad(sceneObject) || IsBlacklistedLoad(sceneObject)) // does the error reporting
                    return false;
                if (m_regInfo.EstateSettings.IsBanned(sceneObject.OwnerID))
                {
                    m_log.Info("[INTERREGION]: Denied prim crossing for banned avatar " + sceneObject.OwnerID.ToString());
                    return false;
                }

                // Avoid the profile lookup when we don't need it.
                if (m_regInfo.ProductAccess != ProductAccessUse.Anyone)
                {
                    bool allowed = false;
                    // Region has access restricted to certain user types, i.e. Plus
                    if (IsGodUser(sceneObject.OwnerID))
                        allowed = true;
                    else
                    if (m_regInfo.ProductAccessAllowed(ProductAccessUse.PlusOnly))
                    {
                        // At least this call is limited to restricted regions only,
                        // and we'll cache this for immediate re-use.
                        UserProfileData profile = CommsManager.UserService.GetUserProfile(sceneObject.OwnerID);
                        if (profile != null)
                            if (m_regInfo.UserHasProductAccess(profile))
                                allowed = true;
                    }

                    if (!allowed)
                    {
                        // We failed to gain access to this restricted region via product access
                        m_log.WarnFormat("[SCENE]: Denied prim crossing to {0} because region is restricted", RegionInfo.RegionName);
                        return false;
                    }
                }
            }

            // Force allocation of new LocalId
            //
            foreach (SceneObjectPart p in sceneObject.GetParts())
            {
                p.LocalId = 0;
            }

            if (sceneObject.RootPart.IsPrim)
            {
                if (sceneObject.IsAttachment)
                {
                    SceneObjectPart RootPrim = sceneObject.RootPart;

                    // Fix up attachment Parent Local ID
                    ScenePresence sp = GetScenePresence(sceneObject.OwnerID);

                    //uint parentLocalID = 0;
                    if (sp != null)
                    {
                        sceneObject.RootPart.AddFlag(PrimFlags.TemporaryOnRez);
                        sceneObject.RootPart.SetParentLocalId(sp.LocalId);

                        //restore saved position and rotation since a create here is a "wear"
                        if (sceneObject.RootPart.SavedAttachmentPos != Vector3.Zero)
                            sceneObject.AbsolutePosition = sceneObject.RootPart.SavedAttachmentPos;

                        if (sceneObject.RootPart.SavedAttachmentRot != Quaternion.Identity)
                            sceneObject.RootPart.RotationOffset = sceneObject.RootPart.SavedAttachmentRot;

                        sceneObject.RootPart.AttachedPos = sceneObject.RootPart.SavedAttachmentPos;

                        AddSceneObjectFromOtherSceneToSceneGraph(sceneObject, false);

                        SceneObjectGroup grp = sceneObject;

                        // We're getting attachments one at a time but its a set so always do append here.
                        bool append = true;
                        AttachObject(sp.ControllingClient, grp.LocalId, (uint)0, append, true, AttachFlags.DontFireOnAttach | AttachFlags.FromCrossing);

                        RootPrim.RemFlag(PrimFlags.TemporaryOnRez);

                        //do not schedule an update here, AttachObject is async and will do so when it has completed
                        //grp.SendGroupFullUpdate();

                        //BUG MITIGATION
                        //When an attachment comes in during a TP, the initial appearance data that gets saved to the DB does not contain the attachment information
                        //when these attachments come over, we queue up an apparance save to fix the broken initial appearance save
                        //and reassociate the attachments
                        //TODO: Remove after thoosa
                        IAvatarFactory ava = this.RequestModuleInterface<IAvatarFactory>();
                        if (ava != null)
                        {
                            ava.UpdateDatabase(sp.UUID, sp.Appearance, null, null);
                        }
                    }
                    else
                    {
                        m_log.ErrorFormat("[ATTACHMENT]: AddSceneObjectFromOtherRegion could not find avatar wearing attachment");
                        RootPrim.RemFlag(PrimFlags.TemporaryOnRez);
                        RootPrim.AddFlag(PrimFlags.TemporaryOnRez);
                        return false;
                    }

                }
                else
                {
                    AddSceneObjectFromOtherSceneToSceneGraph(sceneObject, !sceneObject.RootPart.IsTemporary);

                    if (!Permissions.CanObjectEntry(sceneObject.UUID,
                            true, sceneObject.AbsolutePosition))
                    {
                        // Deny non attachments based on parcel settings
                        m_log.Info("[INTERREGION]: Denied prim crossing because of parcel settings");
                        DeleteSceneObject(sceneObject, false);
                        return false;
                    }
                }

                //trigger events that are appropriate for attachments and when not waiting for seated avatars.
                if (sceneObject.IsAttachment || (sceneObject.AvatarsToExpect == 0))
                {
                    sceneObject.TriggerScriptChangedEvent(Changed.REGION);
                    if (fromTeleport) sceneObject.TriggerScriptChangedEvent(Changed.TELEPORT);
                }
            }
            return true;
        }
        #endregion

        #region Add/Remove Avatar Methods

        public override void AddNewClient(IClientAPI client, bool isBot)
        {
            SubscribeToClientEvents(client);
            ScenePresence presence;

            if (m_restorePresences.ContainsKey(client.AgentId))
            {
                m_log.DebugFormat("[SCENE]: Restoring agent {0} {1} in {2}", client.Name, client.AgentId, RegionInfo.RegionName);

                presence = m_restorePresences[client.AgentId];
                m_restorePresences.Remove(client.AgentId);

                // This is one of two paths to create avatars that are
                // used.  This tends to get called more in standalone
                // than grid, not really sure why, but as such needs
                // an explicity appearance lookup here.
                AvatarAppearance appearance = null;
                GetAvatarAppearance(client, out appearance);
                presence.Appearance = appearance;

                presence.initializeScenePresence(client, RegionInfo, this);

                m_sceneGraph.AddScenePresence(presence);

                lock (m_restorePresences)
                {
                    Monitor.PulseAll(m_restorePresences);
                }
            }
            else
            {
                m_log.DebugFormat(
                    "[SCENE]: Adding new child agent for {0} in {1}",
                    client.Name, RegionInfo.RegionName);

                if (!isBot)
                    CommsManager.UserService.CacheUser(client.AgentId);

                CreateAndAddScenePresence(client);
            }

            m_LastLogin = Environment.TickCount;
            EventManager.TriggerOnNewClient(client);
        }

        protected virtual void SubscribeToClientEvents(IClientAPI client)
        {
            client.OnAddPrim += AddNewPrim;
            client.OnUpdatePrimGroupPosition += m_sceneGraph.UpdatePrimPosition;
            client.OnUpdatePrimSinglePosition += m_sceneGraph.UpdatePrimSinglePosition;
            client.OnUpdatePrimGroupRotation += m_sceneGraph.UpdatePrimRotation;
            client.OnUpdatePrimGroupMouseRotation += m_sceneGraph.UpdatePrimRotation;
            client.OnUpdatePrimSingleRotation += m_sceneGraph.UpdatePrimSingleRotation;
            client.OnUpdatePrimSingleRotationPosition += m_sceneGraph.UpdatePrimSingleRotationPosition;
            client.OnUpdatePrimScale += m_sceneGraph.UpdatePrimScale;
            client.OnUpdatePrimGroupScale += m_sceneGraph.UpdatePrimGroupScale;
            client.OnUpdateExtraParams += m_sceneGraph.UpdateExtraParam;
            client.OnUpdatePrimShape += m_sceneGraph.UpdatePrimShape;
            client.OnUpdatePrimTexture += m_sceneGraph.UpdatePrimTexture;
            client.OnObjectRequest += RequestPrim;
            client.OnTeleportLocationRequest += RequestTeleportLocation;
            client.OnTeleportLandmarkRequest += RequestTeleportLandmark;
            client.OnObjectSelect += SelectPrim;
            client.OnObjectDeselect += DeselectPrim;
            client.OnGrabUpdate += GrabUpdate;
            client.OnSpinStart += m_sceneGraph.SpinStart;
            client.OnSpinUpdate += m_sceneGraph.SpinObject;
            client.OnDeRezObjects += DeRezObjects;
            client.OnRezObject += RezObject;
            client.OnRestoreObject += RestoreObject;
            client.OnRezSingleAttachmentFromInv += RezSingleAttachment;
            client.OnRezMultipleAttachmentsFromInv += RezMultipleAttachments;
            client.OnDetachAttachmentIntoInv += DetachSingleAttachmentToInv;
            client.OnObjectAttach += m_sceneGraph.AttachObject;
            client.OnObjectDetach += m_sceneGraph.DetachObject;
            client.OnObjectDrop += m_sceneGraph.DropObject;
            client.OnNameFromUUIDRequest += CommsManager.HandleUUIDNameRequest;
            client.OnObjectDescription += m_sceneGraph.PrimDescription;
            client.OnObjectName += m_sceneGraph.PrimName;
            client.OnObjectClickAction += m_sceneGraph.PrimClickAction;
            client.OnObjectMaterial += m_sceneGraph.PrimMaterial;
            client.OnLinkObjects += m_sceneGraph.LinkObjects;
            client.OnDelinkObjects += m_sceneGraph.DelinkObjects;
            client.OnObjectDuplicate += m_sceneGraph.DuplicateObject;
            client.OnObjectDuplicateOnRay += doObjectDuplicateOnRay;
            client.OnUpdatePrimFlags += m_sceneGraph.UpdatePrimFlags;
            client.OnRequestObjectPropertiesFamily += m_sceneGraph.RequestObjectPropertiesFamily;
            client.OnObjectPermissions += HandleObjectPermissionsUpdate;
            client.OnCreateNewInventoryItem += CreateNewInventoryItem;
            client.OnLinkInventoryItem += HandleLinkInventoryItem;
            client.OnCreateNewInventoryFolder += HandleCreateInventoryFolder;
            client.OnUpdateInventoryFolder += HandleUpdateInventoryFolder;
            client.OnMoveInventoryFolder += HandleMoveInventoryFolder;
            client.OnFetchInventoryDescendents += HandleFetchInventoryDescendents;
            client.OnPurgeInventoryDescendents += HandlePurgeInventoryDescendents; // Called when the user empties the Trash or 'Lost and Found', or purges a folder that has children. If the last case, OnRemoveInventoryFolder is called first for some silly reason.
            client.OnFetchInventory += HandleFetchInventory;
            client.OnUpdateInventoryItem += UpdateInventoryItemAsset;
            client.OnCopyInventoryItem += CopyInventoryItem;
            client.OnMoveInventoryItem += MoveInventoryItem;
            client.OnRemoveInventoryItem += RemoveInventoryItem;
            client.OnRemoveInventoryFolder += HandlePurgeInventoryFolder; // Called when the user purges a folder.  If the folder has children OnPurgeInventoryDescendents is also called.
            client.OnRezScript += RezScript;
            client.OnRequestTaskInventory += RequestTaskInventory;
            client.OnRemoveTaskItem += RemoveTaskInventory;
            client.OnUpdateTaskInventory += UpdateTaskInventory;
            client.OnMoveTaskItem += ClientMoveTaskInventoryItem;
            client.OnGrabObject += ProcessObjectGrab;
            client.OnDeGrabObject += ProcessObjectDeGrab;
            client.OnMoneyTransferRequest += ProcessMoneyTransferRequest;
            client.OnParcelBuy += ProcessParcelBuy;
            client.OnAvatarPickerRequest += ProcessAvatarPickerRequest;
            client.OnObjectIncludeInSearch += m_sceneGraph.MakeObjectSearchable;
            client.OnTeleportHomeRequest += TeleportClientHome;
            client.OnSetStartLocationRequest += SetHomeRezPoint;
            client.OnUndo += m_sceneGraph.HandleUndo;
            client.OnRedo += m_sceneGraph.HandleRedo;
            client.OnObjectGroupRequest += m_sceneGraph.HandleObjectGroupUpdate;
            client.OnParcelReturnObjectsRequest += LandChannel.ReturnObjectsInParcel;
            client.OnParcelSetOtherCleanTime += LandChannel.SetParcelOtherCleanTime;
            client.OnObjectSaleInfo += ObjectSaleInfo;
            client.OnScriptReset += ProcessScriptReset;
            client.OnGetScriptRunning += GetScriptRunning;
            client.OnSetScriptRunning += SetScriptRunning;
            client.OnRegionHandleRequest += RegionHandleRequest;
            client.OnUnackedTerrain += TerrainUnAcked;
            client.OnObjectOwner += ObjectOwner;
            client.OnUserInfoRequest += HandleUserInfoRequest;
            client.OnUpdateUserInfo += HandleUpdateUserInfo;

            IGodsModule godsModule = RequestModuleInterface<IGodsModule>();
            client.OnGodKickUser += godsModule.KickUser;
            client.OnRequestGodlikePowers += godsModule.RequestGodlikePowers;

            client.OnNetworkStatsUpdate += StatsReporter.AddPacketsStats;
            client.OnViewerEffect += HandleViewerEffect;

            // EventManager.TriggerOnNewClient(client);
        }

        /// <summary>
        /// Called when the user opens the dialog for IM to email among other things
        /// </summary>
        /// <param name="client"></param>
        public void HandleUserInfoRequest(IClientAPI client)
        {
            UserPreferencesData prefs = CommsManager.UserService.RetrieveUserPreferences(client.AgentId);
            if (prefs != null)
            {
                client.SendUserInfoReply(prefs.ReceiveIMsViaEmail, prefs.ListedInDirectory, String.Empty);
            }
        }

        /// <summary>
        /// Called when the user saves preferences
        /// </summary>
        /// <param name="client"></param>
        public void HandleUpdateUserInfo(bool imViaEmail, bool visibleInDirectory, IClientAPI client)
        {
            CommsManager.UserService.SaveUserPreferences(new UserPreferencesData(client.AgentId, imViaEmail, visibleInDirectory));
        }

        private const float NEARBY_EFFECTS = 30.0f; // metres, same as chat distance for now at least
        private bool isNearby(ScenePresence sp, Vector3 otherPos)
        {
            if (sp == null)
                return true;    // assume nearby, show effects

            return Vector3.Distance(sp.AbsolutePosition, otherPos) <= NEARBY_EFFECTS;
        }

        void HandleViewerEffect(IClientAPI remoteClient, List<ViewerEffectEventHandlerArg> args)
        {
            bool forceSend = false;
            ViewerEffectPacket.EffectBlock[] effectBlockArray = new ViewerEffectPacket.EffectBlock[args.Count];
            for (int i = 0; i < args.Count; i++)
            {
                ViewerEffectPacket.EffectBlock effect = new ViewerEffectPacket.EffectBlock();
                effect.AgentID = args[i].AgentID;
                effect.Color = args[i].Color;
                effect.Duration = args[i].Duration;
                effect.ID = args[i].ID;
                effect.Type = args[i].Type;
                effect.TypeData = args[i].TypeData;
                effectBlockArray[i] = effect;

                if (effect.AgentID != remoteClient.AgentId)
                {
                    // Security check. Don't let rogue viewers cause other avatars to flail arms or have other effects.
                    // e.g. https://lists.secondlife.com/pipermail/jira-notify/2009-December/171805.html
                    m_log.WarnFormat("[SCENE]: Refusing unauthorized ViewerEffect {0} attempt by {1} on another avatar {2}.", effect.Type, remoteClient.AgentId, effect.AgentID);
                    return;
                }

                // Anything other than LookAt and Beam are shown at any distance
                forceSend = ((EffectType)effect.Type != EffectType.LookAt) && ((EffectType)effect.Type != EffectType.Beam);
            }

            ScenePresence mySP = GetScenePresence(remoteClient.AgentId);
            ForEachScenePresence(sp =>
            {
                if ((!sp.IsChildAgent) && (sp.ControllingClient.AgentId != remoteClient.AgentId))
                {
                    if (forceSend || isNearby(mySP, sp.CameraPosition))
                        sp.ControllingClient.SendViewerEffect(effectBlockArray);
                }
            });
        }

        /// <summary>
        /// Teleport an avatar to their home region
        /// </summary>
        /// <param name="agentId"></param>
        /// <param name="client"></param>
        public virtual void TeleportClientHome(UUID agentId, IClientAPI client)
        {
            ScenePresence target = GetScenePresence(agentId);
            if (target == null)
                return;

            if (target.IsBot)
            {
                //Bots get removed from the sim if they are teleported home
                IBotManager manager = RequestModuleInterface<IBotManager>();
                if (manager != null)
                    manager.RemoveBot(agentId, UUID.Zero); // this sends IncomingCloseAgent(agentId);
                return;
            }

            UserProfileData UserProfile = CommsManager.UserService.GetUserProfile(agentId);
            if (UserProfile != null)
            {
                RegionInfo regionInfo = CommsManager.GridService.RequestNeighbourInfo(UserProfile.HomeRegionID);
                if (regionInfo == null)
                {
                    regionInfo = CommsManager.GridService.RequestNeighbourInfo(UserProfile.HomeRegion);
                    if (regionInfo != null) // home region can be away temporarily, too
                    {
                        UserProfile.HomeRegionID = regionInfo.RegionID;
                        CommsManager.UserService.UpdateUserProfile(UserProfile);
                    }
                }
                if (regionInfo == null)
                {
                    // can't find the Home region: Tell viewer and abort
                    client.SendTeleportFailed("Your home-region could not be found.");
                    return;
                }
                RequestTeleportLocation(
                    client, regionInfo.RegionHandle, UserProfile.HomeLocation, UserProfile.HomeLookAt,
                    TeleportFlags.SetLastToTarget | TeleportFlags.ViaHome | TeleportFlags.DisableCancel);
            }
        }

        public void doObjectDuplicateOnRay(uint localID, uint dupeFlags, UUID AgentID, UUID GroupID,
                                           UUID RayTargetObj, Vector3 RayEnd, Vector3 RayStart,
                                           bool BypassRaycast, bool RayEndIsIntersection, bool CopyCenters, bool CopyRotates)
        {
            Vector3 pos;
            const bool frontFacesOnly = true;
            //m_log.Info("HITTARGET: " + RayTargetObj.ToString() + ", COPYTARGET: " + localID.ToString());
            SceneObjectPart target = GetSceneObjectPart(localID);
            SceneObjectPart target2 = GetSceneObjectPart(RayTargetObj);

            if (target != null && target2 != null)
            {
                Vector3 direction = Vector3.Normalize(RayEnd - RayStart);
                Vector3 AXOrigin = new Vector3(RayStart.X, RayStart.Y, RayStart.Z);
                Vector3 AXdirection = new Vector3(direction.X, direction.Y, direction.Z);

                if (target2.ParentGroup != null)
                {
                    pos = target2.AbsolutePosition;
                    //m_log.Info("[OBJECT_REZ]: TargetPos: " + pos.ToString() + ", RayStart: " + RayStart.ToString() + ", RayEnd: " + RayEnd.ToString() + ", Volume: " + Util.GetDistanceTo(RayStart,RayEnd).ToString() + ", mag1: " + Util.GetMagnitude(RayStart).ToString() + ", mag2: " + Util.GetMagnitude(RayEnd).ToString());

                    // TODO: Raytrace better here

                    //EntityIntersection ei = m_sceneGraph.GetClosestIntersectingPrim(new Ray(AXOrigin, AXdirection));
                    Ray NewRay = new Ray(AXOrigin, AXdirection);

                    // Ray Trace against target here
                    EntityIntersection ei = target2.TestIntersectionOBB(NewRay, Quaternion.Identity, frontFacesOnly, CopyCenters);

                    // Un-comment out the following line to Get Raytrace results printed to the console.
                    //m_log.Info("[RAYTRACERESULTS]: Hit:" + ei.HitTF.ToString() + " Point: " + ei.ipoint.ToString() + " Normal: " + ei.normal.ToString());
                    float ScaleOffset = 0.5f;

                    // If we hit something
                    if (ei.HitTF)
                    {
                        Vector3 scale = target.Scale;
                        Vector3 scaleComponent = new Vector3(ei.AAfaceNormal.X, ei.AAfaceNormal.Y, ei.AAfaceNormal.Z);
                        if (scaleComponent.X != 0) ScaleOffset = scale.X;
                        if (scaleComponent.Y != 0) ScaleOffset = scale.Y;
                        if (scaleComponent.Z != 0) ScaleOffset = scale.Z;
                        ScaleOffset = Math.Abs(ScaleOffset);
                        Vector3 intersectionpoint = new Vector3(ei.ipoint.X, ei.ipoint.Y, ei.ipoint.Z);
                        Vector3 normal = new Vector3(ei.normal.X, ei.normal.Y, ei.normal.Z);
                        Vector3 offset = normal * (ScaleOffset / 2f);
                        pos = intersectionpoint + offset;

                        // stick in offset format from the original prim
                        pos = pos - target.ParentGroup.AbsolutePosition;

                        if (CopyRotates)
                        {
                            Quaternion worldRot = target2.GetWorldRotation();

                            // SceneObjectGroup obj = m_sceneGraph.DuplicateObject(localID, pos, target.GetEffectiveObjectFlags(), AgentID, GroupID, worldRot);
                            m_sceneGraph.DuplicateObject(localID, pos, (uint)target.GetEffectiveObjectFlags(), AgentID, GroupID, worldRot);
                            //obj.Rotation = worldRot;
                            //obj.UpdateGroupRotation(worldRot);
                        }
                        else
                        {
                            m_sceneGraph.DuplicateObject(localID, pos, (uint)target.GetEffectiveObjectFlags(), AgentID, GroupID);
                        }
                    }

                    return;
                }

                return;
            }
        }

        public bool IsMasterAvatar(UUID userId)
        {
            if (userId == UUID.Zero) return false;

            return RegionInfo.MasterAvatarAssignedUUID == userId;
        }

        public bool IsGodUser(UUID user)
        {
            return Permissions.IsGod(user);
        }

        public bool IsEstateOwner(UUID userId)
        {
            if (userId == UUID.Zero) return false;

            if (IsMasterAvatar(userId)) return true;

            return RegionInfo.EstateSettings.IsEstateOwner(userId);
        }

        public bool IsEstateOwnerPartner(UUID userId)
        {
            UUID EstateOwner = RegionInfo.EstateSettings.EstateOwner;
            if (EstateOwner == UUID.Zero)
                EstateOwner = RegionInfo.MasterAvatarAssignedUUID;
            UserProfileData profile = CommsManager.UserService.GetUserProfile(EstateOwner);
            if (profile == null)
                return false;   // error
            return userId == profile.Partner;
        }

        public bool IsEstateManager(UUID user)
        {
            if (IsEstateOwner(user)) return true;

            return RegionInfo.EstateSettings.IsEstateManager(user);
        }

        /// <summary>
        /// Returns true if userId is the land parcel owner at location and the parcel reference in returnParcel.
        /// </summary>
        /// <param name="userId">The UUID of the user to check.</param>
        /// <param name="location">The x,y,z position within the current region of the land parcel</param>
        /// <param name="returnParcel">The matching parcel is returned.</param>
        /// <returns>Returns true if userId is the land parcel owner at location.</returns>
        public bool IsParcelOwner(UUID userId, ILandObject parcel)
        {
            if ((userId == UUID.Zero) || (parcel == null))
                return false;

            return (parcel.landData.OwnerID == userId);
        }
        // Alternative form of above where the parcel is not known.
        public bool IsParcelOwner(UUID userId, Vector3 location)
        {
            return IsParcelOwner(userId, LandChannel.GetLandObject(location.X, location.Y));
        }
        public bool IsLandOwner(UUID userId, ILandObject parcel)
        {
            // This now includes IsEstateOwner and IsMasterAvatar.
            if (IsEstateManager(userId)) return true;

            // If the caller needs the parcel reference, call IsParcelOwner directly.
            return IsParcelOwner(userId, parcel);
        }
        public bool IsLandOwner(UUID userId, Vector3 location)
        {
            return IsLandOwner(userId, LandChannel.GetLandObject(location.X, location.Y));
        }

        public virtual void SetHomeRezPoint(IClientAPI remoteClient, ulong regionHandle, Vector3 position, Vector3 lookAt, uint flags)
        {
            UserProfileData UserProfile = CommsManager.UserService.GetUserProfile(remoteClient.AgentId);
            ILandObject parcel = LandChannel.GetLandObject(position.X, position.Y);

            if ((UserProfile != null) && (parcel != null) && m_permissions.CanEditParcel(remoteClient.AgentId, parcel, GroupPowers.AllowSetHome))
            {
                if (RegionInfo.AllowSetHome == ProductRulesWho.OnlyEO)
                {
                    if (!IsEstateOwner(remoteClient.AgentId))
                    {
                        if (!IsEstateOwnerPartner(remoteClient.AgentId))
                        {
                            m_dialogModule.SendAlertToUser(remoteClient, "Only the Estate Owner or partner can set home on this kind of region.");
                            return;
                        }
                    }
                }
                // I know I'm ignoring the regionHandle provided by the teleport location request.
                // reusing the TeleportLocationRequest delegate, so regionHandle isn't valid
                UserProfile.HomeRegionID = RegionInfo.RegionID;
                // TODO: The next line can be removed, as soon as only homeRegionID based UserServers are around.
                // TODO: The HomeRegion property can be removed then, too
                UserProfile.HomeRegion = RegionInfo.RegionHandle;
                UserProfile.HomeLocation = position;
                UserProfile.HomeLookAt = lookAt;
                CommsManager.UserService.UpdateUserProfile(UserProfile);

                m_dialogModule.SendAlertToUser(remoteClient, "Home position set.", "HomePositionSet", new OSD());

            }
            else
            {
                m_dialogModule.SendAlertToUser(remoteClient, "You can only set your 'Home Location' on your land or group land that allows it.");
            }
        }

        /// <summary>
        /// Create a child agent scene presence and add it to this scene.
        /// </summary>
        /// <param name="client"></param>
        /// <returns></returns>
        protected virtual ScenePresence CreateAndAddScenePresence(IClientAPI client)
        {
            AvatarAppearance appearance = null;
            GetAvatarAppearance(client, out appearance);

            ScenePresence avatar = m_sceneGraph.CreateAndAddChildScenePresence(client, appearance);
            //avatar.KnownRegions = GetChildrenSeeds(avatar.UUID);

            m_eventManager.TriggerOnNewPresence(avatar);

            return avatar;
        }

        /// <summary>
        /// Get the avatar apperance for the given client.
        /// </summary>
        /// <param name="client"></param>
        /// <param name="appearance"></param>
        public void GetAvatarAppearance(IClientAPI client, out AvatarAppearance appearance)
        {
            Connection.AvatarConnection conn = m_connectionManager.GetConnection(client.AgentId);
            if (conn == null)
            {
                m_log.DebugFormat("[APPEARANCE] Client did not supply a circuit. Non-Linden? Creating default appearance.");
                appearance = new AvatarAppearance(client.AgentId);
                return;
            }

            appearance = conn.CircuitData.Appearance;
            if (appearance == null)
            {
                m_log.DebugFormat("[APPEARANCE]: Appearance not found in {0}, returning default", RegionInfo.RegionName);
                appearance = new AvatarAppearance(client.AgentId);
            }
        }

        /// <summary>
        /// Remove the given client from the scene.
        /// </summary>
        /// <param name="agentID"></param>
        public override void RemoveClient(UUID agentID)
        {
            bool isChildAgent = false;
            ScenePresence avatar = GetScenePresence(agentID);
            if (avatar != null)
            {
                isChildAgent = avatar.IsChildAgent;
                avatar.StandUp(false, true);
            }

            try
            {
                m_log.DebugFormat(
                    "[SCENE]: Removing {0} agent {1} from region {2}",
                    (isChildAgent ? "child" : "root"), agentID, RegionInfo.RegionName);

                m_sceneGraph.removeUserCount(!isChildAgent);

                if ((avatar != null) && (!avatar.IsBot))
                {
                    if (!avatar.IsChildAgent)
                    {
                        m_sceneGridService.LogOffUser(agentID, RegionInfo.RegionID, RegionInfo.RegionHandle, avatar.AbsolutePosition, avatar.Lookat);
                    }
                    CommsManager.UserService.UnmakeLocalUser(agentID);
                }

                m_eventManager.TriggerClientClosed(agentID, this);
            }
            catch (NullReferenceException)
            {
                // We don't know which count to remove it from
                // Avatar is already disposed :/
            }

            m_eventManager.TriggerOnRemovePresence(agentID);

            if (avatar != null && !avatar.IsChildAgent)
            {
               Broadcast(delegate(IClientAPI client)
                {
                    try
                    {
                        if (client.AgentId != agentID)
                            client.SendKillObject(avatar.RegionHandle, avatar.LocalId);
                    }
                    catch (NullReferenceException)
                    {
                        //We can safely ignore null reference exceptions.  It means the avatar are dead and cleaned up anyway.
                    }
                });
            }

            IAgentAssetTransactions agentTransactions = this.RequestModuleInterface<IAgentAssetTransactions>();
            if (agentTransactions != null)
            {
                agentTransactions.RemoveAgentAssetTransactions(agentID);
            }

            m_sceneGraph.RemoveScenePresence(agentID);

            if (avatar != null)
            {
                try
                {
                    avatar.Close();
                }
                catch (NullReferenceException)
                {
                    //We can safely ignore null reference exceptions.  It means the avatar are dead and cleaned up anyway.
                }
                catch (Exception e)
                {
                    m_log.Error("[SCENE] Scene.cs:RemoveClient exception: " + e.ToString());
                }
            }

            // Remove client agent from profile, so new logins will work
            if (!isChildAgent)
            {
                m_sceneGridService.ClearUserAgent(agentID);
            }
        }

        #endregion

        #region Entities

        /// <summary>
        /// Called to send a kill for an object that has possibly left an agent's DD
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="destinationRegion"></param>
        public void SendKillObject(uint localID, SimpleRegionInfo destinationRegion)
        {
            SceneObjectPart part = GetSceneObjectPart(localID);
            if (part != null) // It is a prim
            {
                if (part.ParentGroup != null && !part.ParentGroup.IsDeleted) // Valid
                {
                    if (part.ParentGroup.RootPart != part) // Child part
                        return;
                }
            }

            Broadcast(
                delegate(ScenePresence client)
                {
                    //never send a self kill
                    if (client.LocalId == localID) return;
                    //dont send kills to avatars in transit. it may be their prim
                    if (client.IsInTransit) return;

                    uint regionX;
                    uint regionY;

                    //we need to determine the coordinates of the region this presense is on
                    if (!client.IsChildAgent)
                    {
                        //this region
                        regionX = m_regInfo.RegionLocX;
                        regionY = m_regInfo.RegionLocY;
                    }
                    else
                    {
                        //some other region
                        Util.DetermineRegionCoordinatesFromOffset(m_regInfo.RegionLocX, m_regInfo.RegionLocY, client.AbsolutePosition,
                            out regionX, out regionY);
                    }

                    uint xmin, xmax, ymin, ymax;

                    Util.GetDrawDistanceBasedRegionRectangle((uint)client.DrawDistance, 0, regionX, regionY,
                        out xmin, out xmax, out ymin, out ymax);

                    if (!Util.IsWithinDDRectangle(destinationRegion.RegionLocX, destinationRegion.RegionLocY, xmin, xmax, ymin, ymax))
                    {
                        //the object or avatar passed into a region where it can no longer
                        //be seen by the SP. Kill it
                        m_log.DebugFormat("[SCENE]: Sending kill for {0} to {1}", localID, client.Name);
                        client.ControllingClient.SendKillObject(m_regionHandle, localID);
                    }

                });
        }

        #endregion

        #region RegionComms

        /// <summary>
        /// Register the methods that should be invoked when this scene receives various incoming events
        /// </summary>
        public void RegisterCommsEvents()
        {
            m_sceneGridService.OnExpectUser += HandleNewUserConnection;
            m_sceneGridService.OnAvatarCrossingIntoRegion += AgentCrossing;
            m_sceneGridService.OnCloseAgentConnection += IncomingCloseAgent;
            //m_sceneGridService.OnChildAgentUpdate += IncomingChildAgentDataUpdate;
            //m_sceneGridService.OnRemoveKnownRegionFromAvatar += HandleRemoveKnownRegionsFromAvatar;
            m_sceneGridService.OnLogOffUser += HandleLogOffUserFromGrid;
            m_sceneGridService.KiPrimitive += SendKillObject;
            m_sceneGridService.OnGetLandData += GetLandData;
            m_sceneGridService.OnGetLandDataByID += GetLandDataByID;

            if (m_interregionCommsIn != null)
            {
                m_log.Debug("[SCENE]: Registering with InterregionCommsIn");
                m_interregionCommsIn.OnChildAgentUpdate += IncomingChildAgentDataUpdate;
            }
            else
                m_log.Debug("[SCENE]: Unable to register with InterregionCommsIn");

        }

        /// <summary>
        /// Deregister this scene from receiving incoming region events
        /// </summary>
        public void UnRegisterRegionWithComms()
        {
            m_sceneGridService.KiPrimitive -= SendKillObject;
            m_sceneGridService.OnLogOffUser -= HandleLogOffUserFromGrid;
            //m_sceneGridService.OnRemoveKnownRegionFromAvatar -= HandleRemoveKnownRegionsFromAvatar;
            //m_sceneGridService.OnChildAgentUpdate -= IncomingChildAgentDataUpdate;
            m_sceneGridService.OnExpectUser -= HandleNewUserConnection;
            m_sceneGridService.OnAvatarCrossingIntoRegion -= AgentCrossing;
            m_sceneGridService.OnCloseAgentConnection -= IncomingCloseAgent;
            m_sceneGridService.OnGetLandData -= GetLandData;
            m_sceneGridService.OnGetLandDataByID -= GetLandDataByID;

            if (m_interregionCommsIn != null)
                m_interregionCommsIn.OnChildAgentUpdate -= IncomingChildAgentDataUpdate;

            m_sceneGridService.Close();
        }

        /// <summary>
        /// A handler for the SceneCommunicationService event, to match that events return type of void.
        /// Use NewUserConnection() directly if possible so the return type can refuse connections.
        /// Standalone services seems to be the only caller to this
        /// </summary>
        /// <param name="agent"></param>
        public void HandleNewUserConnection(AgentCircuitData agent)
        {
            string reason;
            NewUserConnection(agent, out reason);
        }

        /// <summary>
        /// Do the work necessary to initiate a new user connection for a particular scene.
        /// At the moment, this consists of setting up the caps infrastructure
        /// The return bool should allow for connections to be refused, but as not all calling paths
        /// take proper notice of it let, we allowed banned users in still.
        ///
        /// This is called when a user is coming from another region
        /// </summary>
        /// <param name="regionHandle"></param>
        /// <param name="agent"></param>
        /// <param name="reason"></param>
        public bool NewUserConnection(AgentCircuitData agent, out string reason)
        {
            return NewSceneUser(agent, false, out reason);
        }

        // This variant is called only on login to this region.
        public bool NewUserLogin(AgentCircuitData agent, out string reason)
        {
            return NewSceneUser(agent, true, out reason);
        }

        private bool NewSceneUser(AgentCircuitData agent, bool isInitialLogin, out string reason)
        {
            int newUserConnectionStart = Environment.TickCount;
            try
            {
                // Don't disable this log message - it's too helpful
                m_log.InfoFormat(
                    "[SCENE]: Incoming {0} agent {1} {2} {3} (circuit code {4}): {5}",
                    (agent.child ? "child" : "root"), agent.FirstName, agent.LastName,
                    agent.AgentID, agent.CircuitCode, agent.ClientVersion);

                reason = String.Empty;

                if (isInitialLogin)
                {
                    if (!AuthorizeUserInRegion(agent.AgentID, agent.FirstName, agent.LastName, agent.ClientVersion, out reason))
                    {
                        m_log.WarnFormat("[SCENE]: Region {0} not authorizing incoming {1} agent {2} {3} {4}: {5}",
                                            RegionInfo.RegionName, (agent.child ? "child" : "root"),
                                            agent.FirstName, agent.LastName, agent.AgentID, reason);
                        return false;
                    }
                }

                m_log.InfoFormat(
                    "[SCENE]: Region {0} authorized incoming {1} agent {2} {3} {4} (circuit code {5})",
                    RegionInfo.RegionName, (agent.child ? "child" : "root"), agent.FirstName, agent.LastName,
                    agent.AgentID, agent.CircuitCode);

                if (!agent.child) //i believe this is the case for initial login
                {
                    // Honor parcel landing type and position.
                    ILandObject land = LandChannel.GetLandObject(agent.startpos.X, agent.startpos.Y);
                    if (land != null)
                    {
                        ParcelPropertiesStatus denyReason;
                        float minZ;
                        if (TestBelowHeightLimit(agent.AgentID, agent.startpos, land, out minZ, out denyReason))
                        {
                            switch (denyReason)
                            {
                                case ParcelPropertiesStatus.CollisionBanned:
                                    reason = "Agent is banned from parcel.";
                                    break;
                                case ParcelPropertiesStatus.CollisionNotInGroup:
                                    reason = "Agent is not a member of parcel access group.";
                                    break;
                                case ParcelPropertiesStatus.CollisionNotOnAccessList:
                                    reason = "Agent is not permitted entry due to parcel access restrictions.";
                                    break;
                            }
                            //                      agent.startpos.Z = LandChannel.GetBanHeight() + 50.0f;
                            // Fail the new startpos (connection to this parcel/region)
                            m_log.WarnFormat("[SCENE]: Region {0} refusing incoming {1} agent {2} {3} {4}: {5}",
                                                RegionInfo.RegionName, (agent.child ? "child" : "root"),
                                                agent.FirstName, agent.LastName, agent.AgentID, reason);
                            return false;
                        }
                    }
                }


                //set up a new connection for this user
                Connection.EstablishedBy by = Connection.EstablishedBy.ChildAgent;
                if (isInitialLogin)
                {
                    by = Connection.EstablishedBy.Login;
                }

                try
                {
                    m_connectionManager.NewConnection(agent, by);
                }
                catch (Connection.ConnectionAlreadyEstablishedException e)
                {
                    m_log.WarnFormat("[SCENE]: Agent {0} is already connected, forcing disconnect", agent.FullName);
                    e.ExistingConnection.Terminate(true);

                    //this should now succeed
                    m_connectionManager.NewConnection(agent, by);
                }

                // rewrite session_id
                CachedUserInfo userinfo = CommsManager.UserService.GetUserDetails(agent.AgentID);
                if (userinfo != null)
                {
                    userinfo.SessionID = agent.SessionID;
                    // If this is a login operation, store that in the user info for the initial attachment rez.
                    if (isInitialLogin && !agent.child)
                    {
                        m_log.Debug("[SCENE]: SetNeedsInitialAttachmentRez TRUE");
                        ScenePresence.SetNeedsInitialAttachmentRez(agent.AgentID);
                    }
                    else
                    {
                        m_log.Debug("[SCENE]: SetNeedsInitialAttachmentRez FALSE");
                        ScenePresence.SetNoLongerNeedsInitialAttachmentRez(agent.AgentID);
                    }
                }
                else
                {
                    m_log.WarnFormat("[SCENE]: Region {0} could not find profile for incoming {1} agent {2} {3} {4}",
                                        RegionInfo.RegionName, (agent.child ? "child" : "root"),
                                        agent.FirstName, agent.LastName, agent.AgentID);
                }

                return true;
            }
            finally
            {
                m_log.DebugFormat("[REST COMM] NewUserConnection: {0} ms", Environment.TickCount - newUserConnectionStart);
            }
        }

        public virtual bool AuthenticateUser(AgentCircuitData agent, out string reason)
        {
            reason = String.Empty;

            bool result = CommsManager.UserService.VerifySession(agent.AgentID, agent.SessionID);
            m_log.Debug("[SCENE]: User authentication returned " + result);
            if (!result)
                reason = String.Format("Failed to authenticate user {0} {1}, access denied.", agent.FirstName, agent.LastName);

            return result;
        }

        // Returns false and minZ==non-zero if avatar is not allowed at this height, otherwise min height.
        public bool TestBelowHeightLimit(UUID agentID, Vector3 pos, ILandObject parcel, out float minZ, out ParcelPropertiesStatus reason)
        {
            float groundZ = (float)this.Heightmap.CalculateHeightAt(pos.X, pos.Y);

            minZ = groundZ;
            reason = ParcelPropertiesStatus.ParcelSelected;   // we can treat this as the no error case.

            // Optimization: Quick precheck with more restrictive ban height before checking reason.
            if (pos.Z > LandChannel.GetBanHeight(true))
                return false;   // not restricted

            // Okay now we need to know if they are restricted from entering, and why.
            if (!parcel.DenyParcelAccess(agentID, out reason))
                return false;   // not restricted

            // Finally, check if below their specific minimum Z position.
            minZ = groundZ + LandChannel.GetBanHeight(reason == ParcelPropertiesStatus.CollisionBanned);
            return (pos.Z < minZ);   // below restricted level?
        }

        private class UserMembershipInfo
        {
            private static readonly TimeSpan EXPIRY = new TimeSpan(0, 0, 30);

            private List<UUID> mGroupList = null;
            private DateTime mTimeStamp = DateTime.MinValue;

            public UserMembershipInfo()
            {
            }
            public UserMembershipInfo(List<UUID> groups)
            {
                Memberships = groups;
            }

            public List<UUID> Memberships
            {
                get
                {
                    lock (this)
                    {
                        return new List<UUID>(mGroupList);
                    }
                }
                set
                {
                    lock (this)
                    {
                        mGroupList = new List<UUID>(value);
                        mTimeStamp = DateTime.Now;
                    }
                }
            }

            public bool IsExpired
            {
                get
                {
                    lock (this)
                    {
                        return (DateTime.Now > mTimeStamp + EXPIRY);
                    }
                }
            }
        }

        // This is a cache for IsGroupMember, to ensure FastConfirmGroupMember is fast even if there's no client.
        private Dictionary<UUID, UserMembershipInfo> _UserGroups = new Dictionary<UUID, UserMembershipInfo>();

        private void UserGroupsSet(UUID agentId, List<UUID> memberships)
        {
            lock (_UserGroups)
            {
                _UserGroups[agentId] = new UserMembershipInfo(memberships);
            }
        }

        public void UserGroupsInvalidate(UUID agentId)
        {
            lock (_UserGroups)
            {
                _UserGroups.Remove(agentId);
            }
        }

        // Dictionary<UUID, List<UUID>> _UserGroups = new Dictionary<UUID, List<UUID>>();
        public List<UUID> UserGroupsGet(UUID agentId)
        {
            List<UUID> groups = null;
            bool asyncUpdate = false;
            lock (_UserGroups)
            {
                if (!_UserGroups.ContainsKey(agentId))
                {
                    // do it the slow hard way
                    IGroupsModule gm = RequestModuleInterface<IGroupsModule>();
                    if (gm != null)
                        groups = gm.GetAgentGroupList(agentId);
                    if (groups != null)
                        UserGroupsSet(agentId, groups); 
                } else
                {
                    UserMembershipInfo membership = _UserGroups[agentId];
                    groups = membership.Memberships;
                    asyncUpdate = membership.IsExpired;
                }
            }

            // Check for async update, but use the current data this time.
            if (asyncUpdate)
            {
                Util.FireAndForget((o) =>
                {
                    IGroupsModule gm = RequestModuleInterface<IGroupsModule>();
                    if (gm != null)
                    {
                        groups = gm.GetAgentGroupList(agentId);
                        if (groups != null) // not error
                            UserGroupsSet(agentId, groups);
                    }
                });
            }
            return groups;
        }

        // Just a quick check to see if it's already cached/known.
        public bool UserGroupsKnown(UUID agentId)
        {
            lock (_UserGroups)
            {
                return _UserGroups.ContainsKey(agentId);
            }
        }

        // Must pass either an LLCV client, or an agent UUID.
        public bool FastConfirmGroupMember(IClientAPI client, UUID agentId, UUID groupId)
        {
            if (client != null)
            {
                // use the optimized group membership test
                return client.IsGroupMember(groupId);
            }

            List<UUID> groups = UserGroupsGet(agentId);
            return (groups == null) ? false : groups.Contains(groupId);
        }

        public bool FastConfirmGroupMember(UUID agentId, UUID groupId)
        {
            ScenePresence sp = GetScenePresence(agentId);
            IClientAPI client = (sp == null) ? null : sp.ControllingClient;

            return FastConfirmGroupMember(client, agentId, groupId);
        }

        public bool FastConfirmGroupMember(ScenePresence sp, UUID groupId)
        {
            if (sp == null) // must not be null
                return false;

            return FastConfirmGroupMember(sp.ControllingClient, sp.UUID, groupId);
        }

        public virtual bool AuthorizeUserObject(SceneObjectGroup sog, List<UUID> avatars, out string reason)
        {
            reason = String.Empty;
            if (!m_strictAccessControl) return true;

            if (!AuthorizeUserInRegion(sog.OwnerID, String.Empty, String.Empty, null, out reason))
            {
                m_log.WarnFormat("[SCENE]: Object denied entry at {0} because user {1} does not have region access.", RegionInfo.RegionName, sog.OwnerID);
                reason = String.Format("Object owner does not have access to {0}.", RegionInfo.RegionName);
                return false;
            }

            Vector3 pos = sog.AbsolutePosition;
            ILandObject land = LandChannel.GetLandObject(pos.X, pos.Y);
            if (!AuthorizeUserInParcel(sog.OwnerID, String.Empty, String.Empty, land, pos, out reason))
            {
                m_log.WarnFormat("[SCENE]: Object denied entry at {0} because user {1} does not have parcel access.", RegionInfo.RegionName, sog.OwnerID);
                reason = String.Format("Object owner {0} does not have access to that parcel.", sog.OwnerID);
                return false;
            }

            if (avatars != null)
            {
                foreach (UUID agentID in avatars)
                {
                    // optimization for case where the owner is one of the sitters
                    if (agentID == sog.OwnerID)
                        continue;   // we already checked above

                    if (!AuthorizeUserInRegion(agentID, String.Empty, String.Empty, null, out reason))
                    {
                        m_log.WarnFormat("[SCENE]: Object denied entry at {0} because user {1} does not have region access.", RegionInfo.RegionName, agentID);
                        reason = String.Format("User {0} does not have access to {1}.", agentID, RegionInfo.RegionName);
                        return false;
                    }

                    if (!AuthorizeUserInParcel(agentID, String.Empty, String.Empty, land, pos, out reason))
                    {
                        m_log.WarnFormat("[SCENE]: Object denied entry at {0} because user {1} {2}", RegionInfo.RegionName, agentID, reason);
                        reason = String.Format("Object entry denied: user {0} {1}", agentID, reason);
                        return false;
                    }
                }
            }

            // If we make it here, the object and all sitters are authorized
            return true;
        }

        // If it helps for higher performance beyond logins, pass null for firstName, lastName and clientversion.
        public virtual bool AuthorizeUserInRegion(UUID agentId, string firstName, string lastName, string clientVersion, out string reason)
        {
            reason = String.Empty;
            string userName = "user";
            if ((firstName != null) && (lastName != null))
                userName = firstName + " " + lastName;

            if (!m_strictAccessControl) return true;

            try
            {
                if (Permissions.IsGod(agentId)) return true;
            }
            catch (UserProfileException e)
            {
                m_log.WarnFormat("[SCENE]: User {0} ({1}) was denied access to the region due to an exception {2}", agentId, userName, e);

                reason = String.Format("Unable to load your user profile/inventory. Try again later.");
                return false;
            }

            int currentRootAgents = SceneGraph.GetRootAgentCount();

            // Check hard limit on region.
            if (currentRootAgents >= m_maxRootAgents)
            {
                m_log.WarnFormat("[SCENE]: User {0} ({1}) was denied access to the region because it was full ({2})", agentId, userName, currentRootAgents);
                reason = String.Format("Region is full ({0} of {1})", currentRootAgents, m_maxRootAgents);
                return false;
            }

            // Check estate soft limit on region.
            if (currentRootAgents >= RegionInfo.RegionSettings.AgentLimit)
            {
                if (RegionInfo.EstateSettings.HasAccess(agentId) == false)
                {
                    m_log.WarnFormat(
                        "[SCENE]: User {0} ({1}) was denied access to the region because estate limit ({2} of {3}) was reached",
                        agentId, userName, currentRootAgents, RegionInfo.RegionSettings.AgentLimit);
                    reason = "Region estate limit has been reached";
                    return false;
                }
            }

            if (IsBlacklistedUser(agentId))
            {
                m_log.WarnFormat("[SCENE]: Denied access to: {0} ({1}) at {2} because the user is blacklisted",
                                    agentId, userName, RegionInfo.RegionName);
                reason = String.Format("{0} is blacklisted on that region", userName);
                return false;
            }

            if (m_regInfo.EstateSettings.IsBanned(agentId))
            {
                m_log.WarnFormat("[SCENE]: Denied access to: {0} ({1}) at {2} because the user is on the banlist",
                                agentId, userName, RegionInfo.RegionName);
                reason = String.Format("{0} is banned from that region", userName);
                return false;
            }

            if (clientVersion != null)  // if authorizing for login, not crossing
            {
                if (!EventManager.TriggerOnAuthorizeUser(agentId, firstName, lastName, clientVersion, ref reason))
                {
                    //module denied entry
                    m_log.WarnFormat("[SCENE]: Denied access to: {0} ({1} {2}) at {3} because of a module: {4}",
                                    agentId, firstName, lastName, RegionInfo.RegionName, reason);
                    return false;
                }
            }

            // Avoid the profile lookup when we don't need it.
            if (m_regInfo.ProductAccess != ProductAccessUse.Anyone)
            {
                bool allowed = false;
                // Region has access restricted to certain user types, i.e. Plus
                if (IsGodUser(agentId))
                    allowed = true;
                else
                if (m_regInfo.ProductAccessAllowed(ProductAccessUse.PlusOnly))
                {
                    // At least this call is limited to restricted regions only,
                    // and we'll cache this for immediate re-use.
                    UserProfileData profile = CommsManager.UserService.GetUserProfile(agentId);
                    if (profile != null)
                        if (m_regInfo.UserHasProductAccess(profile))
                            allowed = true;
                }

                if (!allowed)
                {
                    // We failed to gain access to this restricted region via product access
                    m_log.WarnFormat("[SCENE]: Denied access to: {0} ({1} {2}) at {3} because region is restricted.",
                            agentId, userName, RegionInfo.RegionName, reason);
                    reason = String.Format("Access to {0} is restricted", RegionInfo.RegionName);
                    return false;
                }
            }

            if (m_regInfo.EstateSettings.PublicAccess)
                return true;

            if (m_regInfo.EstateSettings.HasAccess(agentId))
                return true;

            IGroupsModule gm = RequestModuleInterface<IGroupsModule>();
            if (gm == null)
            {
                reason = "Groups module error";
                m_log.WarnFormat("[SCENE]: Denied access to: {0} ({1} {2}) at {3}: {4}",
                                agentId, firstName, lastName, RegionInfo.RegionName, reason);
                return false;
            }

            ScenePresence sp = GetScenePresence(agentId);
            IClientAPI client = (sp != null) ? sp.ControllingClient : null;
            foreach (UUID groupId in m_regInfo.EstateSettings.EstateGroups)
            {
                // check agent.Id in group
                if (FastConfirmGroupMember(client, agentId, groupId))
                    return true;
            }

            m_log.WarnFormat("[SCENE]: Denied access to: {0} ({1} {2}) at {3} because the user does not have region access",
                            agentId, firstName, lastName, RegionInfo.RegionName);
            reason = String.Format("{0} {1} does not have access to region: {2}", firstName, lastName, RegionInfo.RegionName);
            return false;
        }

        // If it helps for higher performance beyond logins, pass null for firstName and lastName.
        // This does not check region entry access.  (Call AuthorizeUserInRegion separately.)
        public virtual bool AuthorizeUserInParcel(UUID agentID, string firstName, string lastName, ILandObject land, Vector3 pos, out string reason)
        {
            reason = String.Empty;
            string userName = "user";
            if ((firstName != null) && (lastName != null))
                userName = firstName + " " + lastName;

            // Check land parcel access
            if (land != null)
            {
                ParcelPropertiesStatus denyReason;
                float minZ;
                if (TestBelowHeightLimit(agentID, pos, land, out minZ, out denyReason))
                {
                    string who;
                    if ((firstName != null) && (lastName != null))
                        who = firstName + " " + lastName;
                    else
                        who = agentID.ToString();

                    reason = String.Format("{0} denied access to destination parcel", who);
                    m_log.WarnFormat("[SCENE]: {0}", reason);
                    return false;
                }
            }
            return true;
        }

        public void HandleLogOffUserFromGrid(UUID AvatarID, UUID RegionSecret, string message)
        {
            ScenePresence loggingOffUser = null;
            loggingOffUser = GetScenePresence(AvatarID);
            if (loggingOffUser != null)
            {
                UUID localRegionSecret = UUID.Zero;
                bool parsedsecret = UUID.TryParse(m_regInfo.regionSecret, out localRegionSecret);

                // Region Secret is used here in case a new sessionid overwrites an old one on the user server.
                // Will update the user server in a few revisions to use it.

                if (RegionSecret == loggingOffUser.ControllingClient.SecureSessionId || (parsedsecret && RegionSecret == localRegionSecret))
                {
                    if (loggingOffUser.ControllingClient != null)   // may have been cleaned up
                    {
                        loggingOffUser.ControllingClient.Kick(message);
                        // Give them a second to receive the message!
                        Thread.Sleep(1000);
                        if (loggingOffUser.ControllingClient != null)   // may have been cleaned up
                            loggingOffUser.ControllingClient.Close();
                    }
                }
                else
                {
                    m_log.Info("[USERLOGOFF]: System sending the LogOff user message failed to sucessfully authenticate");
                }
            }
            else
            {
                m_log.InfoFormat("[USERLOGOFF]: Got a logoff request for {0} but the user isn't here.  The user might already have been logged out", AvatarID.ToString());
            }
        }

        /// <summary>
        /// Triggered when an agent crosses into this sim.  Also happens on initial login.
        /// </summary>
        /// <param name="agentID"></param>
        /// <param name="position"></param>
        /// <param name="isFlying"></param>
        public virtual void AgentCrossing(UUID agentID, Vector3 position, bool isFlying)
        {
            ScenePresence presence;

            lock (m_scenePresences)
            {
                m_scenePresences.TryGetValue(agentID, out presence);
            }

            if (presence != null)
            {
                try
                {
                    SceneObjectPart parent = presence.MakeRootAgent(position);
                    // Release any lock before calling PostProcessMakeRootAgent, it calls functions that use lock
                    presence.PostProcessMakeRootAgent(parent, isFlying);
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("[SCENE]: Unable to do agent crossing, exception {0}", e);
                }
            }
            else
            {
                m_log.ErrorFormat(
                    "[SCENE]: Could not find presence for agent {0} crossing into scene {1}",
                    agentID, RegionInfo.RegionName);
            }
        }

        public virtual bool IncomingChildAgentDataUpdate(AgentData cAgentData)
        {
            int childAgentDataUpdateStart = Environment.TickCount;

            try
            {
                //            m_log.DebugFormat(
                //                "[SCENE]: Incoming child agent update for {0} in {1}", cAgentData.AgentID, RegionInfo.RegionName);

                // We have to wait until the viewer contacts this region after receiving EAC.
                // That calls AddNewClient, which finally creates the ScenePresence
                ScenePresence SP = WaitScenePresence(cAgentData.AgentID, 5000);//WaitGetScenePresence(cAgentData.AgentID);
                if (SP == null)
                {
                    m_log.ErrorFormat("[SCENE]: Timeout waiting for ChildAgentUpdate presence for {0} in {1}", cAgentData.AgentID.ToString(), RegionInfo.RegionName);
                    return false;
                }

                SP.ChildAgentDataUpdate(cAgentData);
                return true;
            }
            finally
            {
                m_log.DebugFormat("[SCENE]: IncomingChildAgentDataUpdate {0} ms", Environment.TickCount - childAgentDataUpdateStart);
            }
        }

        public virtual bool IncomingChildAgentDataUpdate(AgentPosition cAgentData)
        {
            //            m_log.Info("[SCENE]: IncomingChildAgentDataUpdate in " + RegionInfo.RegionName + " at " + cAgentData.Position.ToString());
            ScenePresence childAgentUpdate = GetScenePresence(cAgentData.AgentID);
            if (childAgentUpdate == null)
            {
                //                m_log.WarnFormat("[SCENE]: Agent {0} not found in {1} for incoming child agent data update.", cAgentData.AgentID.ToString(), RegionInfo.RegionName);
                return false;
            }

            // I can't imagine *yet* why we would get an update if the agent is a root agent..
            // however to avoid a race condition crossing borders..
            if (childAgentUpdate.IsChildAgent)
            {
                uint rRegionX = (uint)(cAgentData.RegionHandle >> 40);
                uint rRegionY = (((uint)(cAgentData.RegionHandle)) >> 8);
                uint tRegionX = RegionInfo.RegionLocX;
                uint tRegionY = RegionInfo.RegionLocY;
                //Send Data to ScenePresence
                childAgentUpdate.ChildAgentPositionUpdate(cAgentData, tRegionX, tRegionY, rRegionX, rRegionY);
                // Not Implemented:
                //TODO: Do we need to pass the message on to one of our neighbors?
            }

            return true;
        }

        public ChildAgentUpdate2Response IncomingChildAgentDataUpdate2(AgentData data)
        {
            int childAgentDataUpdateStart = Environment.TickCount;
            string reason = "error";

            try
            {
                //            m_log.DebugFormat(
                //                "[SCENE]: Incoming child agent update for {0} in {1}", cAgentData.AgentID, RegionInfo.RegionName);

                ScenePresence SP = GetScenePresence(data.AgentID);
                if (SP == null)
                {
                    m_log.ErrorFormat("[SCENE]: ChildAgentUpdate2 couldn't find presence for {0} in {1}", data.AgentID, RegionInfo.RegionName);
                    return ChildAgentUpdate2Response.Error;
                }

                if (!SP.IsChildAgent)
                {
                    // Just ignore it (but no error)
                    reason = "authorized";
                    return ChildAgentUpdate2Response.Ok;
                }

                if (!AuthorizeUserInRegion(data.AgentID, SP.Firstname, SP.Lastname, null, out reason))
                {
                    SP.ControllingClient.SendAlertMessage("Could not enter region '" + RegionInfo.RegionName + "': " + reason);
                    return ChildAgentUpdate2Response.AccessDenied;
                }

                if (data.SatOnGroup == null)
                {
                    // In this case, data.Position is good.
                    ILandObject land = LandChannel.GetLandObject(data.Position.X, data.Position.Y);
                    if (!AuthorizeUserInParcel(data.AgentID, SP.Firstname, SP.Lastname, land, data.Position, out reason))
                    {
                        SP.ControllingClient.SendAlertMessage("Could not enter region '" + RegionInfo.RegionName + "': " + reason);
                        return ChildAgentUpdate2Response.AccessDenied;
                    }
                }
                // else data.Position is no good when seated, would use this:
                // Vector3 pos = GetSceneObjectPart(data.SatOnPrim).AbsolutePosition + data.SatOnPrimOffset;

                reason = "authorized";
                SP.ChildAgentDataUpdate2(data);

                Util.FireAndForget((o) =>
                {
                    SP.ConfirmHandoff(false);
                });
                return ChildAgentUpdate2Response.Ok;
            }
            finally
            {
                m_log.DebugFormat("[SCENE]: IncomingChildAgentDataUpdate {0} ms: {1}", Environment.TickCount - childAgentDataUpdateStart, reason);
            }
        }

        /// <summary>
        /// Waits for a presence by UUID
        /// </summary>
        /// <param name="avatarID"></param>
        /// <returns></returns>
        protected ScenePresence WaitScenePresence(UUID avatarID, int waitTime)
        {
            return m_sceneGraph.WaitScenePresence(avatarID, waitTime);
        }

        protected ScenePresence WaitGetScenePresence(UUID agentID)
        {
            int ntimes = 10;
            ScenePresence childAgentUpdate = null;
            while ((childAgentUpdate = GetScenePresence(agentID)) == null && (ntimes-- > 0))
                Thread.Sleep(1000);
            return childAgentUpdate;

        }

        public virtual bool IncomingRetrieveRootAgent(UUID id, out IAgentData agent)
        {
            agent = null;
            ScenePresence sp = GetScenePresence(id);
            if ((sp != null) && (!sp.IsChildAgent))
            {
                sp.IsChildAgent = true;
                sp.AgentInRegion = AgentInRegionFlags.None;
                return sp.CopyAgent(out agent);
            }

            return false;
        }

        public virtual bool IncomingReleaseAgent(UUID id)
        {
            return m_transitController.HandleReleaseAgent(id);
        }

        public void SendReleaseAgent(ulong regionHandle, UUID id, string uri)
        {
            m_interregionCommsOut.SendReleaseAgent(regionHandle, id, uri);
        }

        /// <summary>
        /// Tell a single agent to disconnect from the region.
        /// </summary>
        /// <param name="regionHandle"></param>
        /// <param name="agentID"></param>
        public bool IncomingCloseAgent(UUID agentID)
        {
            m_log.InfoFormat("[SCENE]: Processing incoming close agent for {0}", agentID);

            ScenePresence presence = m_sceneGraph.GetScenePresence(agentID);
            if (presence != null)
            {
                if (presence.IsBot)
                {
                    //Bots get removed from the sim if they are teleported home
                    IBotManager manager = presence.Scene.RequestModuleInterface<IBotManager>();
                    if (manager != null)
                        manager.RemoveBot(presence.UUID, UUID.Zero); // this sends IncomingCloseAgent(agentId);
                }

                presence.ControllingClient.Close();
                return true;
            }

            // Agent not here
            return false;
        }

        /// <summary>
        /// Requests information about this region from gridcomms
        /// </summary>
        /// <param name="regionHandle"></param>
        /// <returns></returns>
        public SimpleRegionInfo RequestNeighbouringRegionInfo(ulong regionHandle)
        {
            return _surroundingRegions.GetKnownNeighborByHandle(regionHandle);
        }

        /// <summary>
        /// Requests textures for map from minimum region to maximum region in world cordinates
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="minX"></param>
        /// <param name="minY"></param>
        /// <param name="maxX"></param>
        /// <param name="maxY"></param>
        public void RequestMapBlocks(IClientAPI remoteClient, int minX, int minY, int maxX, int maxY)
        {
            m_log.DebugFormat("[MAPBLOCK]: {0}-{1}, {2}-{3}", minX, minY, maxX, maxY);
            m_sceneGridService.RequestMapBlocks(remoteClient, minX, minY, maxX, maxY);
        }

        /// <summary>
        /// Tries to teleport agent to other region.
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="regionName"></param>
        /// <param name="position"></param>
        /// <param name="lookAt"></param>
        /// <param name="teleportFlags"></param>
        public void RequestTeleportLocation(IClientAPI remoteClient, string regionName, Vector3 position,
                                            Vector3 lookat, uint teleportFlags)
        {
            RegionInfo regionInfo = m_sceneGridService.RequestClosestRegion(regionName);
            if (regionInfo == null)
            {
                // can't find the region: Tell viewer and abort
                remoteClient.SendTeleportFailed("The region '" + regionName + "' could not be found.");
                return;
            }

            RequestTeleportLocation(remoteClient, regionInfo.RegionHandle, position, lookat, (TeleportFlags)teleportFlags);
        }

        /// <summary>
        /// Tries to teleport agent to other region.
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="regionHandle"></param>
        /// <param name="position"></param>
        /// <param name="lookAt"></param>
        /// <param name="teleportFlags"></param>
        public void RequestTeleportLocation(IClientAPI remoteClient, ulong regionHandle, Vector3 position,
                                            Vector3 lookAt, uint teleportFlags)
        {
            ScenePresence sp = null;
            lock (m_scenePresences)
            {
                m_scenePresences.TryGetValue(remoteClient.AgentId, out sp);
            }

            if (sp != null)
            {
                this.RequestTeleportToLocation(sp, regionHandle, position, lookAt, (TeleportFlags)teleportFlags);
            }
        }

        /// <summary>
        /// Tries to teleport agent to other region.
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="regionHandle"></param>
        /// <param name="position"></param>
        /// <param name="lookAt"></param>
        /// <param name="teleportFlags"></param>
        public void RequestTeleportLocation(IClientAPI remoteClient, ulong regionHandle, Vector3 position,
                                            Vector3 lookAt, TeleportFlags teleportFlags)
        {
            ScenePresence sp = null;
            lock (m_scenePresences)
            {
                m_scenePresences.TryGetValue(remoteClient.AgentId, out sp);
            }

            if (sp != null)
            {
                this.RequestTeleportToLocation(sp, regionHandle, position, lookAt, teleportFlags);
            }
        }

        /// <summary>
        /// Tries to teleport agent to landmark.
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="regionHandle"></param>
        /// <param name="position"></param>
        public void RequestTeleportLandmark(IClientAPI remoteClient, UUID regionID, Vector3 position)
        {
            RegionInfo info = CommsManager.GridService.RequestNeighbourInfo(regionID);

            if (info == null)
            {
                // can't find the region: Tell viewer and abort
                remoteClient.SendTeleportFailed("The teleport destination could not be found.");
                return;
            }

            ScenePresence sp = null;
            lock (m_scenePresences)
            {
                m_scenePresences.TryGetValue(remoteClient.AgentId, out sp);
            }

            if (sp != null)
            {
                this.RequestTeleportToLocation(sp, info.RegionHandle,
                    position, Vector3.Zero, TeleportFlags.SetLastToTarget | TeleportFlags.ViaLandmark);
            }
        }

        /// <summary>
        /// Try to teleport an agent to a new region.
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="RegionHandle"></param>
        /// <param name="position"></param>
        /// <param name="lookAt"></param>
        /// <param name="flags"></param>
        public void RequestTeleportToLocation(ScenePresence avatar, ulong regionHandle, Vector3 position,
                                                      Vector3 lookAt, OpenMetaverse.TeleportFlags teleportFlags)
        {
            if (avatar.IsInTransit || avatar.IsChildAgent)
            {
                m_log.DebugFormat("[SCENE COMMUNICATION SERVICE]: RequestTeleportToLocation cannot teleport {0} [{1}], not in region.", avatar.Name, avatar.UUID);
                return;
            }
            if (!Permissions.CanTeleport(avatar.UUID))
                return;

            Vector3 fromPosition = avatar.AbsolutePosition;
            IEventQueue eq = RequestModuleInterface<IEventQueue>();

            //lets just get this check out of the way. if the event queue is null we're totally screwed
            //with the current viewers
            if (eq == null) throw new InvalidOperationException("Can not teleport without an event queue");

            // Reset animations; the viewer does that in teleports.
            avatar.ResetAnimations();

            ulong fromRegionHandle = RegionInfo.RegionHandle;

            LandData fromParcel = CommsManager.GridService.RequestLandData(fromRegionHandle, (uint)fromPosition.X, (uint)fromPosition.Y);
            if (fromParcel == null)
            {
                avatar.ControllingClient.SendTeleportFailed("Teleport not available: Current region not available from grid service.");
                return;
            }

            LandData parcel = CommsManager.GridService.RequestLandData(regionHandle, (uint)position.X, (uint)position.Y);
            if (parcel == null)
            {
                avatar.ControllingClient.SendTeleportFailed("Destination region appears to be down.");
                return;
            }

            SimpleRegionInfo sreg;
            string destRegionName;

            if (regionHandle == fromRegionHandle)
            {
                sreg = RegionInfo;
                destRegionName = "this region";
            } else {
                sreg = RequestNeighbouringRegionInfo(regionHandle);
                if (sreg == null)
                {
                    //no real neighbor information about that region, consult the grid server
                    RegionInfo reg = m_sceneGridService.RequestNeighbouringRegionInfo(regionHandle);
                    if (reg == null)
                    {
                        avatar.ControllingClient.SendTeleportFailed("Destination region could not be found.");
                        return;
                    }
                    sreg = reg;
                    destRegionName = reg.RegionName;
                }
                else
                    destRegionName = String.Format("neighbor at ({0},{1})", sreg.RegionLocX, sreg.RegionLocY);
            }

            // Check for landing point or telehub override.
            if ((teleportFlags & (TeleportFlags.ViaLandmark | TeleportFlags.ViaLocation)) != 0)
            {
                bool set = false;
                EstateSettings neighborEstateSettings = StorageManager.EstateDataStore.LoadEstateSettings(sreg.RegionID);
                //Telehubs only work if allow direct teleport is disabled, and it only works on non estate owners/managers
                if (neighborEstateSettings != null && !neighborEstateSettings.AllowDirectTeleport && !neighborEstateSettings.IsEstateManager(avatar.UUID))
                {
                    //If no spawn points are set, just use the telehub's position
                    Telehub telehub = StorageManager.EstateDataStore.FindTelehub(sreg.RegionID);

                    if (telehub != null)
                    {
                        if (telehub.SpawnPos.Count == 0)
                        {
                            set = true;
                            position = telehub.TelehubLoc;
                        }
                        else
                        {
                            //Randomly put the user at one of the spawn points
                            set = true;
                            int spawnIndex = Util.RandomClass.Next(0, telehub.SpawnPos.Count);
                            position = telehub.SpawnPos[spawnIndex];
                        }
                    }
                }

                if (!set && (parcel.OwnerID != avatar.UUID) && (fromParcel.GlobalID != parcel.GlobalID))
                {   // Changing parcels and not the owner.
                    if (!(IsGodUser(avatar.UUID) || IsEstateManager(avatar.UUID) || IsEstateOwnerPartner(avatar.UUID)))
                    {
                        if ((parcel.LandingType == LandingType.LandingPoint) && (parcel.UserLocation != Vector3.Zero))
                        {   // Parcel in landing point mode with a location specified.
                            // Force the user to the landing point.
                            position = parcel.UserLocation;
                            lookAt = parcel.UserLookAt;
                        }
                        else
                        if ((parcel.LandingType == LandingType.Blocked))
                        {
                            avatar.ControllingClient.SendTeleportFailed("Teleport routing at destination parcel is blocked.");
                            return;
                        }
                    }
                }
            }

            // Preserve flying status to viewer; must be set before SendLocalTeleport.
            PhysicsActor pa = avatar.PhysicsActor;
            if (pa != null && pa.Flying)
                teleportFlags |= TeleportFlags.IsFlying;
            else
                teleportFlags &= ~TeleportFlags.IsFlying;

            if (regionHandle == fromRegionHandle)
            {
                // Teleport within the same region
                m_log.DebugFormat(
                    "[SCENE COMMUNICATION SERVICE]: RequestTeleportToLocation {0} within {1}",
                    position, destRegionName);

                if (!Util.IsValidRegionXYZ(position))
                {
                    m_log.WarnFormat(
                        "[SCENE COMMUNICATION SERVICE]: RequestTeleportToLocation() was given an illegal position of {0} for avatar {1}, {2}.  Refusing teleport.",
                        position, avatar.Name, avatar.UUID);
                    return;
                }

                float localAVHeight = avatar.Appearance.AvatarHeight;
                float posZLimit = 0.25f + (float)Heightmap[(int)position.X, (int)position.Y];
                float newPosZ = posZLimit + localAVHeight;
                if (posZLimit >= (position.Z - (localAVHeight / 2)) && !(Single.IsInfinity(newPosZ) || Single.IsNaN(newPosZ)))
                {
                    position.Z = newPosZ;
                }

                avatar.StandUp(false, true);
                avatar.ControllingClient.SendLocalTeleport(position, lookAt, (uint)teleportFlags);
                avatar.Teleport(position);

                List<SceneObjectGroup> attachments = avatar.GetAttachments();
                foreach (var grp in attachments)
                {
                    grp.TriggerScriptChangedEvent(Changed.TELEPORT);
                }
            }
            else
            {
                m_log.DebugFormat(
                    "[SCENE COMMUNICATION SERVICE]: RequestTeleportToLocation to {0} in {1}",
                    position, destRegionName);

                avatar.StandUp(false, true);

                AvatarTransit.TransitArguments args = new AvatarTransit.TransitArguments
                {
                    DestinationRegion = sreg,
                    LocationInDestination = position,
                    TeleportFlags = teleportFlags,
                    Type = AvatarTransit.TransitType.OutboundTeleport,
                    UserId = avatar.UUID,
                };

                try
                {
                    var task = m_transitController.TryBeginTransit(args);
                    task.Wait();
                }
                catch (AggregateException e)
                {
                    m_log.ErrorFormat("[SCENE]: Teleport failed for {0} {1}", avatar.Name, e);

                    StringBuilder message = new StringBuilder();
                    message.AppendLine("Unable to complete teleport: ");
                    message.AppendLine();
                    foreach (var ex in e.InnerExceptions)
                    {
                        message.AppendLine(ex.Message);
                    }

                    if (avatar.ControllingClient != null) // null if the avatar is already in transit/gone
                        avatar.ControllingClient.SendTeleportFailed(message.ToString());
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("[SCENE]: Teleport failed for {0} {1}", avatar.Name, e);
                    if (avatar.ControllingClient != null) // null if the avatar is already in transit/gone
                        avatar.ControllingClient.SendTeleportFailed(e.Message);
                }
            }
        }

        public Task CrossSittingAgentToNewRegion(ScenePresence scenePresence, SceneObjectGroup sceneObjectGroup,
            SceneObjectPart part, ulong newRegionHandle)
        {
            SimpleRegionInfo neighborInfo = _surroundingRegions.GetKnownNeighborByHandle(newRegionHandle);

            if (neighborInfo == null)
            {
                throw new InvalidOperationException(String.Format("Neighbor {0} was not found", newRegionHandle));
            }

            AvatarTransit.TransitArguments args = new AvatarTransit.TransitArguments
            {
                DestinationRegion = neighborInfo,
                LocationInDestination = scenePresence.GetRawPosition(),
                RideOnGroup = sceneObjectGroup,
                RideOnPart = part,
                TeleportFlags = 0,
                Type = AvatarTransit.TransitType.OutboundCrossing,
                UserId = scenePresence.UUID,
            };

            return m_transitController.TryBeginTransit(args);
        }

        public void SendOutChildAgentUpdates(AgentPosition cadu, ScenePresence presence)
        {
            m_sceneGridService.SendChildAgentDataUpdate(cadu, presence);
        }

#endregion

#region Other Methods

        public void SetObjectCapacity(int objects)
        {
            // Region specific config overrides global
            //
            if (RegionInfo.ObjectCapacity != 0)
                objects = RegionInfo.ObjectCapacity;

            if (StatsReporter != null)
            {
                StatsReporter.SetObjectCapacity(objects);
            }
            objectCapacity = objects;
        }

        public List<FriendListItem> GetFriendList(string id)
        {
            UUID avatarID;
            if (!UUID.TryParse(id, out avatarID))
                return new List<FriendListItem>();

            return CommsManager.GetUserFriendList(avatarID);
        }

        public Dictionary<UUID, FriendRegionInfo> GetFriendRegionInfos(List<UUID> uuids)
        {
            return CommsManager.GetFriendRegionInfos(uuids);
        }

        public virtual void StoreAddFriendship(UUID ownerID, UUID friendID, uint perms)
        {
            m_sceneGridService.AddNewUserFriend(ownerID, friendID, perms);
        }

        public virtual void StoreUpdateFriendship(UUID ownerID, UUID friendID, uint perms)
        {
            m_sceneGridService.UpdateUserFriendPerms(ownerID, friendID, perms);
            CommsManager.UserService.UpdateUserFriendPerms(ownerID, friendID, perms);
        }

        public virtual void StoreRemoveFriendship(UUID ownerID, UUID ExfriendID)
        {
            m_sceneGridService.RemoveUserFriend(ownerID, ExfriendID);
        }

#endregion

        public void HandleObjectPermissionsUpdate(IClientAPI controller, UUID agentID, UUID sessionID, byte field, uint localId, uint mask, byte set)
        {
            // Check for spoofing..  since this is permissions we're talking about here!
            if ((controller.SessionId == sessionID) && (controller.AgentId == agentID))
            {
                // Tell the object to do permission update
                if (localId != 0)
                {
                    SceneObjectGroup group = GetGroupByPrim(localId);
                    if (group != null)
                    {
                        // group.UpdatePermissions() returns false if the update was blocked because the object was attached.
                        if (!group.UpdatePermissions(agentID, field, localId, mask, set))
                            controller.SendAlertMessage("To change an attachment's permissions, you must first drop it or detach it.");
                    }
                }
            }
        }

        /// <summary>
        /// Causes all clients to get a full object update on all of the objects in the scene.
        /// </summary>
        public void ForceClientUpdate()
        {
            List<EntityBase> EntityList = GetEntities();

            foreach (EntityBase ent in EntityList)
            {
                if (ent is SceneObjectGroup)
                {
                    ((SceneObjectGroup)ent).ScheduleGroupForFullUpdate(PrimUpdateFlags.ForcedFullUpdate);
                }
            }
        }

        /// <summary>
        /// This is currently only used for scale (to scale to MegaPrim size)
        /// There is a console command that calls this in OpenSimMain
        /// </summary>
        /// <param name="cmdparams"></param>
        public void HandleEditCommand(string[] cmdparams)
        {
            m_log.Debug("Searching for Primitive: '" + cmdparams[2] + "'");

            List<EntityBase> EntityList = GetEntities();

            foreach (EntityBase ent in EntityList)
            {
                if (ent is SceneObjectGroup)
                {
                    SceneObjectPart part = ((SceneObjectGroup)ent).GetChildPart(((SceneObjectGroup)ent).UUID);
                    if (part != null)
                    {
                        if (part.Name == cmdparams[2])
                        {
                            part.Resize(
                                new Vector3(Convert.ToSingle(cmdparams[3]), Convert.ToSingle(cmdparams[4]),
                                              Convert.ToSingle(cmdparams[5])));

                            m_log.Debug("Edited scale of Primitive: " + part.Name);
                        }
                    }
                }
            }
        }

        public override void Show(string[] showParams)
        {
            base.Show(showParams);

            switch (showParams[0])
            {
                case "users":
                    m_log.Error("Current Region: " + RegionInfo.RegionName);
                    m_log.ErrorFormat("{0,-16}{1,-16}{2,-25}{3,-25}{4,-16}{5,-16}{6,-16}", "Firstname", "Lastname",
                                      "Agent ID", "Session ID", "Circuit", "IP", "World");

                    foreach (ScenePresence scenePresence in GetAvatars())
                    {
                        m_log.ErrorFormat("{0,-16}{1,-16}{2,-25}{3,-25}{4,-16},{5,-16}{6,-16}",
                                          scenePresence.Firstname,
                                          scenePresence.Lastname,
                                          scenePresence.UUID,
                                          scenePresence.ControllingClient.AgentId,
                                          "Unknown",
                                          "Unknown",
                                          RegionInfo.RegionName);
                    }

                    break;
            }
        }

#region Script Handling Methods

        /// <summary>
        /// Console command handler to send script command to script engine.
        /// </summary>
        /// <param name="args"></param>
        public void SendCommandToPlugins(string[] args)
        {
            m_eventManager.TriggerOnPluginConsole(args);
        }

        public LandData GetLandData(float x, float y)
        {
            ILandObject parcel = LandChannel.GetLandObject(x, y);
            if (parcel == null)
                return null;
            return parcel.landData;
        }

        public LandData GetLandData(uint x, uint y)
        {
            ILandObject parcel = LandChannel.GetLandObject((int)x, (int)y);
            if (parcel == null)
                return null;
            return parcel.landData;
        }

        public LandData GetLandDataByID(int localLandID)
        {
            ILandObject parcel = LandChannel.GetLandObject(localLandID);
            if (parcel == null)
                return null;
            return parcel.landData;
        }

        public RegionInfo RequestClosestRegion(string name)
        {
            return m_sceneGridService.RequestClosestRegion(name);
        }

#endregion

#region Script Engine

        private List<IScriptEngineInterface> ScriptEngines = new List<IScriptEngineInterface>();
        private AvatarTransit.AvatarTransitController m_transitController;

        /// <summary>
        ///
        /// </summary>
        /// <param name="scriptEngine"></param>
        public void AddScriptEngine(IScriptEngineInterface scriptEngine)
        {
            ScriptEngines.Add(scriptEngine);
            scriptEngine.InitializeEngine(this);
        }

        private bool ScriptDanger(SceneObjectPart part, Vector3 pos)
        {
            ILandObject parcel = LandChannel.GetLandObject(pos.X, pos.Y);
            if (part != null)
            {
                if (parcel != null)
                {
                    if ((parcel.landData.Flags & (uint)ParcelFlags.AllowOtherScripts) != 0)
                    {
                        return true;
                    }
                    else if ((parcel.landData.Flags & (uint)ParcelFlags.AllowGroupScripts) != 0)
                    {
                        if (part.OwnerID == parcel.landData.OwnerID
                            || (parcel.landData.IsGroupOwned && part.GroupID == parcel.landData.GroupID)
                            || Permissions.IsGod(part.OwnerID))
                        {
                            return true;
                        }
                        else
                        {
                            return false;
                        }
                    }
                    else
                    {
                        if (part.OwnerID == parcel.landData.OwnerID)
                        {
                            return true;
                        }
                        else
                        {
                            return false;
                        }
                    }
                }
                else
                {
                    // If the object is outside of this region, stop piping events to it.
                    // The only time parcel != null when an object is inside a region is when
                    // there is nothing behind the landchannel.  IE, no land plugin loaded.
                    return Util.IsValidRegionXY(pos);
                }
            }
            else
            {
                return false;
            }
        }

        public bool ScriptDanger(uint localID, Vector3 pos)
        {
            SceneObjectPart part = GetSceneObjectPart(localID);
            if (part != null)
            {
                return ScriptDanger(part, pos);
            }
            else
            {
                return false;
            }
        }

        public bool PipeEventsForScript(uint localID)
        {
            SceneObjectPart part = GetSceneObjectPart(localID);
            if (part != null)
            {
                // Changed so that child prims of attachments return ScriptDanger for their parent, so that
                //  their scripts will actually run.
                //      -- Leaf, Tue Aug 12 14:17:05 EDT 2008
                SceneObjectPart parent = part.ParentGroup.RootPart;
                if (parent != null && parent.IsAttachment)
                    return ScriptDanger(parent, parent.GetWorldPosition());
                else
                    return ScriptDanger(part, part.GetWorldPosition());
            }
            else
            {
                return false;
            }
        }

#endregion

#region SceneGraph wrapper methods

        /// <summary>
        ///
        /// </summary>
        /// <param name="localID"></param>
        /// <returns></returns>
        public UUID ConvertLocalIDToFullID(uint localID)
        {
            return m_sceneGraph.ConvertLocalIDToFullID(localID);
        }

        public void SwapRootAgentCount(bool becameChild)
        {
            m_sceneGraph.SwapRootChildAgent(becameChild);
        }

        public void SwapChildToRootAgent(UUID userId, uint oldLocalId, uint newLocalId)
        {
            Entities.SwapChildToRootAgent(userId, oldLocalId, newLocalId);
            m_sceneGraph.SwapRootChildAgent(false);
        }

        public void AddPhysicalObject()
        {
            m_sceneGraph.AddPhysicalObject();
        }

        public void RemovePhysicalObject()
        {
            m_sceneGraph.RemovePhysicalObject();
        }

        //The idea is to have a group of method that return a list of avatars meeting some requirement
        // ie it could be all m_scenePresences within a certain range of the calling prim/avatar.

        /// <summary>
        /// Return a list of all avatars in this region.
        /// This list is a new object, so it can be iterated over without locking.
        /// </summary>
        /// <returns></returns>
        public List<ScenePresence> GetAvatars()
        {
            return m_sceneGraph.GetAvatars();
        }

        /// <summary>
        /// Return a list of all ScenePresences in this region.  This returns child agents as well as root agents.
        /// This list is a new object, so it can be iterated over without locking.
        /// </summary>
        /// <returns></returns>
        public List<ScenePresence> GetScenePresences()
        {
            return m_sceneGraph.GetScenePresences();
        }

        /// <summary>
        /// Request a filtered list of ScenePresences in this region.
        /// This list is a new object, so it can be iterated over without locking.
        /// </summary>
        /// <param name="filter"></param>
        /// <returns></returns>
        public List<ScenePresence> GetScenePresences(FilterAvatarList filter)
        {
            return m_sceneGraph.GetScenePresences(filter);
        }

        public ScenePresence GetScenePresence(string first, string last)
        {
            string name = first + " " + last;
            ScenePresence avatar = null;
            if (TryGetAvatarByName(name, out avatar))
                return avatar;

            return null;
        }

        /// <summary>
        /// Request a scene presence by UUID
        /// </summary>
        /// <param name="avatarID"></param>
        /// <returns></returns>
        public ScenePresence GetScenePresence(UUID avatarID)
        {
            return m_sceneGraph.GetScenePresence(avatarID);
        }

        public ScenePresence GetScenePresence(uint localId)
        {
            return m_sceneGraph.GetScenePresence(localId);
        }

        public override bool PresenceChildStatus(UUID avatarID)
        {
            ScenePresence cp = GetScenePresence(avatarID);

            return cp.IsChildAgent;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="action"></param>
        public void ForEachScenePresence(Action<ScenePresence> action)
        {
            // We don't want to try to send messages if there are no avatars.
            if (m_scenePresences != null)
            {
                try
                {
                    List<ScenePresence> presenceList = GetScenePresences();
                    foreach (ScenePresence presence in presenceList)
                    {
                        action(presence);
                    }
                }
                catch (Exception e)
                {
                    m_log.Info("[BUG] in " + RegionInfo.RegionName + ": " + e.ToString());
                    m_log.Info("[BUG] Stack Trace: " + e.StackTrace);
                }
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="action"></param>
        //        public void ForEachObject(Action<SceneObjectGroup> action)
        //        {
        //            List<SceneObjectGroup> presenceList;
        //
        //            lock (m_sceneObjects)
        //            {
        //                presenceList = new List<SceneObjectGroup>(m_sceneObjects.Values);
        //            }
        //
        //            foreach (SceneObjectGroup presence in presenceList)
        //            {
        //                action(presence);
        //            }
        //        }

        /// <summary>
        /// Get a named prim contained in this scene (will return the first
        /// found, if there are more than one prim with the same name)
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public SceneObjectPart GetSceneObjectPart(string name)
        {
            return m_sceneGraph.GetSceneObjectPart(name);
        }

        /// <summary>
        /// Get a prim via its local id
        /// </summary>
        /// <param name="localID"></param>
        /// <returns></returns>
        public SceneObjectPart GetSceneObjectPart(uint localID)
        {
            return m_sceneGraph.GetSceneObjectPart(localID);
        }

        /// <summary>
        /// Get a prim via its UUID
        /// </summary>
        /// <param name="fullID"></param>
        /// <returns></returns>
        public SceneObjectPart GetSceneObjectPart(UUID fullID)
        {
            return m_sceneGraph.GetPrimByFullId(fullID);
        }

        public bool TryGetAvatar(UUID avatarId, out ScenePresence avatar)
        {
            return m_sceneGraph.TryGetAvatar(avatarId, out avatar);
        }

        public bool TryGetAvatarByName(string avatarName, out ScenePresence avatar)
        {
            return m_sceneGraph.TryGetAvatarByName(avatarName, out avatar);
        }

        public override void ForEachClient(Action<IClientAPI> action)
        {
            m_sceneGraph.ForEachClient(action);
        }

        public override bool TryGetClient(UUID avatarID, out IClientAPI client)
        {
            Connection.AvatarConnection conn = m_connectionManager.GetConnection(avatarID);
            if (conn != null)
            {
                client = conn.UdpCircuit;
                if (client != null)
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                client = null;
                return false;
            }
        }

        public bool TryGetClient(System.Net.IPEndPoint remoteEndPoint, out IClientAPI client)
        {
            Connection.AvatarConnection conn = m_connectionManager.GetConnection(remoteEndPoint);
            if (conn != null)
            {
                client = conn.UdpCircuit;
                if (client != null)
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                client = null;
                return false;
            }
        }

        /// <summary>
        /// Returns a list of the entities in the scene.  This is a new list so operations perform on the list itself
        /// will not affect the original list of objects in the scene.
        /// </summary>
        /// <returns></returns>
        public List<EntityBase> GetEntities()
        {
            return m_sceneGraph.GetEntities();
        }

#endregion

        public void RegionHandleRequest(IClientAPI client, UUID regionID)
        {
            RegionInfo info;
            if (regionID == RegionInfo.RegionID)
                info = RegionInfo;
            else
                info = CommsManager.GridService.RequestNeighbourInfo(regionID);

            if (info != null)
                client.SendRegionHandle(regionID, info.RegionHandle);
        }

        public void TerrainUnAcked(IClientAPI client, int patchX, int patchY)
        {
            //m_log.Debug("Terrain packet unacked, resending patch: " + patchX + " , " + patchY);
            client.SendLayerData(patchX, patchY, Heightmap.GetFloatsSerialized());
        }

        public void SetRootAgentScene(UUID agentID)
        {
            IInventoryTransferModule inv = RequestModuleInterface<IInventoryTransferModule>();
            if (inv == null)
                return;

            inv.SetRootAgentScene(agentID, this);

            EventManager.TriggerSetRootAgentScene(agentID, this);
        }

        public bool NeedSceneCacheClear(UUID agentID)
        {
            IInventoryTransferModule inv = RequestModuleInterface<IInventoryTransferModule>();
            if (inv == null)
                return true;

            return inv.NeedSceneCacheClear(agentID, this);
        }

        public void ObjectSaleInfo(IClientAPI client, UUID agentID, UUID sessionID, uint localID, byte saleType, int salePrice)
        {
            SceneObjectPart part = GetSceneObjectPart(localID);
            if (part == null || part.ParentGroup == null)
                return;

            if (part.ParentGroup.IsDeleted)
                return;

            if (client.AgentId != part.OwnerID)    // prevent spoofing/hacking
                return;

            SceneObjectGroup group = part.ParentGroup;
            part = group.RootPart;  // force the part to refer to the root part
            uint eperms = group.GetEffectivePermissions(true);

            // Check to make sure the sale type is valid. Since some viewers 
            // (including SL and IW3 viewers) don't have an Apply button, we can't
            // just outright reject the change, we need to allow it 
            // so that they can change the sale type, but we can warn them.
            switch (saleType)
            {
                case (byte)SaleType.Copy:
                    //make sure the person can actually copy and transfer this object
                    if ((eperms & (uint)PermissionMask.Copy) == 0)
                        client.SendAlertMessage("Warning: Cannot sell a copy of this object which is no-copy or has no-copy Contents.");
                    if ((eperms & (uint)PermissionMask.Transfer) == 0)
                        client.SendAlertMessage("Warning: Cannot transfer this object which is no-transfer or has no-transfer Contents.");
                    break;

                case (byte)SaleType.Original:
                    //make sure the person can actually transfer this object
                    if ((eperms & (uint)PermissionMask.Transfer) == 0)
                        client.SendAlertMessage("Warning: Cannot transfer this object which is no-transfer or has no-transfer Contents.");
                    break;
            }

            part.ObjectSaleType = saleType;
            part.SalePrice = salePrice;

            part.ParentGroup.HasGroupChanged = true;

            part.GetProperties(client);
        }

        public bool PerformObjectBuy(IClientAPI remoteClient, UUID categoryID,
                uint localID, byte saleType)
        {
            SceneObjectPart part = GetSceneObjectPart(localID);

            if (part == null)
                return false;

            if (part.ParentGroup == null)
                return false;

            SceneObjectGroup group = part.ParentGroup;

            switch (saleType)
            {
                case 1: // Sell as original (in-place sale)
                    if (! ChangeLiveSOGOwner(remoteClient, part, group))
                    {
                        return false;
                    }

                    break;

                case 2: // Sell a copy
                    byte[] sceneObjectBytes = this.DoSerializeSingleGroup(group, SerializationFlags.None);// SceneObjectSerializer.ToOriginalXmlFormat(group, false);

                    CachedUserInfo userInfo =
                        CommsManager.UserService.GetUserDetails(remoteClient.AgentId);

                    if (userInfo != null)
                    {
                        uint eperms = group.GetEffectivePermissions(true);

                        if ((eperms & (uint)PermissionMask.Copy) == 0)
                        {
                            m_dialogModule.SendAlertToUser(remoteClient, "This item doesn't appear to be copyable.");
                            return false;
                        }

                        if ((remoteClient.AgentId != group.OwnerID) && ((eperms & (uint)PermissionMask.Transfer) == 0))
                        {
                            m_dialogModule.SendAlertToUser(remoteClient, "This item doesn't appear to be transferable.");
                            return false;
                        }

                        AssetBase asset = CreateAsset(
                            group.GetPartName(localID),
                            group.GetPartDescription(localID),
                            (sbyte)AssetType.Object,
                            sceneObjectBytes);

                        try
                        {
                            CommsManager.AssetCache.AddAsset(asset, AssetRequestInfo.InternalRequest());
                        }
                        catch (AssetServerException e)
                        {
                            m_log.ErrorFormat("[PURCHASE] Unable to create asset for sale of object copy: {0}", e);
                            m_dialogModule.SendAlertToUser(remoteClient, "Internal asset system error.");
                            return false;
                        }

                        InventoryItemBase item = new InventoryItemBase();
                        item.CreatorId = part.CreatorID.ToString();

                        item.ID = UUID.Random();
                        item.Owner = remoteClient.AgentId;
                        item.GroupID = remoteClient.ActiveGroupId;
                        item.AssetID = asset.FullID;
                        item.Description = asset.Description;
                        item.Name = asset.Name;
                        item.AssetType = asset.Type;
                        item.InvType = (int)InventoryType.Object;
                        item.Folder = categoryID;

                        ItemPermissionBlock newPerms = group.GetNewItemPermissions(remoteClient.AgentId);
                        newPerms.ApplyToOther(item);

                        item.CreationDate = Util.UnixTimeSinceEpoch();

                        userInfo.AddItem(item);
                        remoteClient.SendInventoryItemCreateUpdate(item, 0);
                    }
                    else
                    {
                        m_dialogModule.SendAlertToUser(remoteClient, "Cannot buy now. Your inventory is unavailable");
                        return false;
                    }
                    break;

                case 3: // Sell contents
                    List<UUID> invList = part.Inventory.GetInventoryList();

                    bool okToSell = true;

                    foreach (UUID invID in invList)
                    {
                        TaskInventoryItem item = part.Inventory.GetInventoryItem(invID);
                        if (((item.CurrentPermissions & (uint)PermissionMask.Transfer) == 0) || ((item.CurrentPermissions & (uint)PermissionMask.Copy) == 0))
                        {
                            okToSell = false;
                            break;
                        }
                    }

                    if (!okToSell)
                    {
                        m_dialogModule.SendAlertToUser(
                            remoteClient, "This item's inventory doesn't appear to be copyable and transferable.");
                        return false;
                    }

                    if (invList.Count > 0)
                        MoveTaskInventoryItems(remoteClient.AgentId, part.Name,
                                part, invList);
                    break;
            }

            return true;
        }

        /// <summary>
        /// Changes the ownership of a SOG that is currently live and in the scene
        /// </summary>
        /// <param name="newOwner">The controlling client for the new user taking ownership</param>
        /// <param name="part">The root part of the group</param>
        /// <param name="group">The SOG</param>
        /// <returns>Whether the operation succeeded or failed </returns>
        public bool ChangeLiveSOGOwner(IClientAPI newOwner, SceneObjectPart part, SceneObjectGroup group)
        {
            uint effectivePerms = group.GetEffectivePermissions(true);

            if (newOwner.AgentId == group.OwnerID)
            {
                m_dialogModule.SendAlertToUser(newOwner, "You already own this item.");
                return false;
            }
            if ((effectivePerms & (uint)PermissionMask.Transfer) == 0)
            {
                m_dialogModule.SendAlertToUser(newOwner, "This item (or something in the Contents of one of the prims) does not appear to be transferable.");
                return false;
            }

            group.ChangeOwner(newOwner);
            return true;
        }

        public void CleanTempObjects()
        {
            List<EntityBase> objs = GetEntities();

            foreach (EntityBase obj in objs)
            {
                if (obj is SceneObjectGroup)
                {
                    SceneObjectGroup grp = (SceneObjectGroup)obj;

                    if (!(grp.IsDeleted || grp.InTransit))
                    {
                        if ((grp.RootPart.Flags & PrimFlags.TemporaryOnRez) != 0 && !grp.HasSittingAvatars)
                        {
                            if (grp.RootPart.Expires <= DateTime.Now)
                                DeleteSceneObject(grp, false);
                        }
                    }
                }
            }
        }

        public void DeleteFromStorage(UUID uuid)
        {
            m_storageManager.DataStore.RemoveObject(uuid, m_regInfo.RegionID);
        }


        public RegionSettings QueryOtherRegionSettings(UUID RegionID)
        {
            return m_storageManager.DataStore.LoadRegionSettings(RegionID);
        }

        public int GetHealth()
        {
            // Returns:
            // 1 = sim is up and accepting http requests. The heartbeat has
            // stopped and the sim is probably locked up, but a remote
            // admin restart may succeed
            //
            // 2 = Sim is up and the heartbeat is running. The sim is likely
            // usable for people within and logins _may_ work
            //
            // 3 = We have seen a new user enter within the past 4 minutes
            // which can be seen as positive confirmation of sim health
            //
            int health = 1; // Start at 1, means we're up

            if ((Environment.TickCount - m_lastUpdate) < 1000)
                health += 1;
            else
                return health;

            // A login in the last 4 mins? We can't be doing too badly
            //
            if ((Environment.TickCount - m_LastLogin) < 240000)
                health++;
            else
                return health;

            return health;
        }

        // This callback allows the PhysicsScene to call back to its caller (the SceneGraph) and
        // update non-physical objects like the joint proxy objects that represent the position
        // of the joints in the scene.

        // This routine is normally called from within a lock (OdeLock) from within the OdePhysicsScene
        // WARNING: be careful of deadlocks here if you manipulate the scene. Remember you are being called
        // from within the OdePhysicsScene.
        protected internal void jointDeactivated(PhysicsJoint joint)
        {
            //m_log.Debug("[NINJA] SceneGraph.jointDeactivated, joint:" + joint.ObjectNameInScene);
            SceneObjectPart jointProxyObject = GetSceneObjectPart(joint.ObjectNameInScene);
            if (jointProxyObject == null)
            {
                jointErrorMessage(joint, "WARNING, trying to deactivate (stop interpolation of) joint proxy, but not found, name " + joint.ObjectNameInScene);
                return;
            }

            // turn the proxy non-physical, which also stops its client-side interpolation
            bool wasUsingPhysics = ((jointProxyObject.ObjectFlags & (uint)PrimFlags.Physics) != 0);
            if (wasUsingPhysics)
            {
                jointProxyObject.UpdatePrimFlags(false, false, true, false, null); // FIXME: possible deadlock here; check to make sure all the scene alterations set into motion here won't deadlock
            }
        }

        // This callback allows the PhysicsScene to call back to its caller (the SceneGraph) and
        // alert the user of errors by using the debug channel in the same way that scripts alert
        // the user of compile errors.

        // This routine is normally called from within a lock (OdeLock) from within the OdePhysicsScene
        // WARNING: be careful of deadlocks here if you manipulate the scene. Remember you are being called
        // from within the OdePhysicsScene.
        public void jointErrorMessage(PhysicsJoint joint, string message)
        {
            
        }

        public Scene ConsoleScene()
        {
            if (MainConsole.Instance == null)
                return null;
            if (MainConsole.Instance.ConsoleScene is Scene)
                return (Scene)MainConsole.Instance.ConsoleScene;
            return null;
        }

        private class UpdateCounts
        {
            public uint Terse;
            public uint Full;
            public ulong WeightedTotal;

            public SceneObjectGroup Group;

            public static UpdateCounts operator -(UpdateCounts lhs, UpdateCounts rhs)
            {
                return new UpdateCounts
                {
                    Group = lhs.Group,
                    Terse = lhs.Terse - rhs.Terse,
                    Full = lhs.Full - rhs.Full,
                    WeightedTotal = lhs.WeightedTotal - rhs.WeightedTotal
                };
            }
        }

        public string GetTopUpdatesOutput(string[] showParams)
        {
            Dictionary<SceneObjectPart, UpdateCounts> firstPassUpdateCounts = new Dictionary<SceneObjectPart, UpdateCounts>();
            ExtractUpdateCounts(firstPassUpdateCounts);

            StringBuilder output = new StringBuilder();

            Dictionary<SceneObjectGroup, UpdateCounts> groupCounts1 = new Dictionary<SceneObjectGroup, UpdateCounts>();
            GatherGroupAndWeightedCounts(firstPassUpdateCounts, groupCounts1);

            if (showParams[1] == "total")
            {
                //we have the data we need
                List<UpdateCounts> updList = new List<UpdateCounts>(groupCounts1.Values);
                groupCounts1 = null;

                //sort by weighted total
                updList.Sort(UpdateCountsWeightedComparison());

                OutputUpdateCounts(output, updList);

                return output.ToString();
            }
            else
            {
                //wait 5 seconds and gather counts again
                System.Threading.Thread.Sleep(5000);

                Dictionary<SceneObjectPart, UpdateCounts> secondPassUpdateCounts = new Dictionary<SceneObjectPart, UpdateCounts>();
                ExtractUpdateCounts(secondPassUpdateCounts);

                Dictionary<SceneObjectGroup, UpdateCounts> groupCounts2 = new Dictionary<SceneObjectGroup, UpdateCounts>();
                GatherGroupAndWeightedCounts(secondPassUpdateCounts, groupCounts2);

                List<UpdateCounts> updList = new List<UpdateCounts>();

                //compare the counts and form a new list
                foreach (var gc1kvp in groupCounts1)
                {
                    UpdateCounts secondCount;
                    if (groupCounts2.TryGetValue(gc1kvp.Key, out secondCount))
                    {
                        updList.Add(secondCount - gc1kvp.Value);
                    }
                }

                //sort by weighted total
                updList.Sort(UpdateCountsWeightedComparison());

                OutputUpdateCounts(output, updList);

                return output.ToString();
            }
        }

        private static void OutputUpdateCounts(StringBuilder output, List<UpdateCounts> updList)
        {
            output.AppendLine("********** GROUP UPDATE COUNTS (TOP 10 BY WEIGHT) **********");
            output.AppendFormat("{0,-24} {1,-30} {2,10} {3,9} {4,9}\n", new object[] { "Name", "Location", "WeightedTot", "Terse", "Full" });

            int counter = 0;
            foreach (var upd in updList)
            {
                output.AppendFormat("{0,-24} {1,-30} {2,10} {3,9} {4,9}\n", new object[] { upd.Group.Name, upd.Group.AbsolutePosition, upd.WeightedTotal, upd.Terse, upd.Full });
                if (++counter == 10) break;
            }
        }

        private static Comparison<UpdateCounts> UpdateCountsWeightedComparison()
        {
            //sorts desc
            return (UpdateCounts lhs, UpdateCounts rhs) =>
            {
                if (lhs == rhs) return 0;
                if (lhs.WeightedTotal == rhs.WeightedTotal) return 0;
                if (lhs.WeightedTotal < rhs.WeightedTotal) return 1;

                return -1;
            };
        }

        private static void GatherGroupAndWeightedCounts(Dictionary<SceneObjectPart, UpdateCounts> firstPassUpdateCounts, Dictionary<SceneObjectGroup, UpdateCounts> groupCounts)
        {
            foreach (var countKvp in firstPassUpdateCounts)
            {
                UpdateCounts outCount;
                if (!groupCounts.TryGetValue(countKvp.Key.ParentGroup, out outCount))
                {
                    outCount = new UpdateCounts { Group = countKvp.Key.ParentGroup };
                    groupCounts.Add(countKvp.Key.ParentGroup, outCount);
                }

                outCount.Terse += countKvp.Value.Terse;
                outCount.Full += countKvp.Value.Full;
                outCount.WeightedTotal += (countKvp.Value.Terse * 1) + (countKvp.Value.Full * 2);
            }
        }

        private void ExtractUpdateCounts(Dictionary<SceneObjectPart, UpdateCounts> updateCountDict)
        {
            var entList = SceneGraph.GetEntities();

            foreach (var ent in entList)
            {
                SceneObjectGroup group = ent as SceneObjectGroup;
                if (group != null)
                {
                    group.ForEachPart(
                        (part) =>
                        {
                            updateCountDict.Add(part, new UpdateCounts { Terse = (uint)part.TerseUpdateCounter, Full = (uint)part.FullUpdateCounter });
                        }
                        );
                }
            }
        }

        public bool IncomingWaitScenePresence(UUID agentId, int maxSpWait)
        {
            return this.WaitScenePresence(agentId, maxSpWait) != null;
        }

        internal void CrossWalkingOrFlyingAgentToNewRegion(ScenePresence scenePresence, ulong neighborHandle, SimpleRegionInfo neighborInfo, Vector3 positionInNewRegion)
        {
            AvatarTransit.TransitArguments args = new AvatarTransit.TransitArguments
            {
                DestinationRegion = neighborInfo,
                LocationInDestination = positionInNewRegion,
                RideOnGroup = null,
                RideOnPart = null,
                TeleportFlags = 0,
                Type = AvatarTransit.TransitType.OutboundCrossing,
                UserId = scenePresence.UUID,
            };

            m_transitController.TryBeginTransit(args);
        }

        internal bool AvatarIsInTransit(UUID uuid)
        {
            return m_transitController?.AvatarIsInTransit(uuid) ?? false;
        }

        internal bool AvatarIsInTransitOnPrim(UUID uuid)
        {
            return m_transitController.AvatarIsInTransitOnPrim(uuid);
        }
    }
}
