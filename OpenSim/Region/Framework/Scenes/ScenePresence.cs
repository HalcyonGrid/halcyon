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
using System.Threading;
using System.Collections.Generic;
using System.Reflection;
using OpenMetaverse;
using log4net;
using OpenSim.Framework;
using OpenSim.Framework.Client;
using OpenSim.Framework.Communications.Cache;
using OpenSim.Framework.Geom;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Physics.Manager;
using OpenSim.Region.Framework.Scenes.Serialization;
using System.Threading.Tasks;
using System.Linq;

namespace OpenSim.Region.Framework.Scenes
{
    enum ScriptControlled : uint
    {
        CONTROL_ZERO = 0,
        CONTROL_FWD = 1,                    // 0x00000001
        CONTROL_BACK = 2,                   // 0x00000002
        CONTROL_LEFT = 4,                   // 0x00000004
        CONTROL_RIGHT = 8,                  // 0x00000008
        CONTROL_UP = 16,                    // 0x00000010
        CONTROL_DOWN = 32,                  // 0x00000020
        CONTROL_ROT_LEFT = 256,             // 0x00000100
        CONTROL_ROT_RIGHT = 512,            // 0x00000200
        CONTROL_MOUSELOOK = 262144,         // 0x00040000
        CONTROL_NUDGE_AT_POS = 524288,      // 0x00080000
        CONTROL_NUDGE_AT_NEG = 1048576,     // 0x00100000
        CONTROL_NUDGE_LEFT_POS = 2097152,   // 0x00200000
        CONTROL_NUDGE_LEFT_NEG = 4194304,   // 0x00400000
        CONTROL_NUDGE_UP_POS = 8388608,     // 0x00800000
        CONTROL_NUDGE_UP_NEG = 16777216,    // 0x01000000
        CONTROL_TURN_LEFT = 33554432,       // 0x02000000
        CONTROL_TURN_RIGHT = 67108864,      // 0x04000000
        CONTROL_AWAY = 134217728,           // 0x08000000
        CONTROL_LBUTTON = 268435456,        // 0x10000000
        CONTROL_LBUTTON_UP = 536870912,     // 0x20000000
        CONTROL_ML_LBUTTON = 1073741824     // 0x40000000
    }

    struct ScriptControllers
    {
        public UUID itemID;
        public uint objID;
        public ScriptControlled ignoreControls;
        public ScriptControlled eventControls;
    }

    [Flags]
    public enum AgentInRegionFlags : byte
    {
        None = 0,
        CompleteMovementReceived = 1,
        FetchedProfile = 2,
        InitialDataReady = 4,
        ParcelInfoSent = 8,
        CanExitRegion = CompleteMovementReceived,   // don't care much about this region if leaving
        // FullyInRegion doesn't need parcel info to start sending updates, especially with 250ms delay
        FullyInRegion = CompleteMovementReceived|FetchedProfile|InitialDataReady
    }

    public class ScenePresence : EntityBase
    {
        const float DEFAULT_AV_HEIGHT = 1.56f;
        const float JUMP_FORCE = 8.0f;
        const float FLY_LAUNCH_FORCE = 2.0f;
        const float NUDGE_DURATION_AT = 100;            // Fwd/back nudge interval (keep it small for accurate positioning)
        const float NUDGE_DURATION_LR = 350;            // Strafe left/right nudge interval.
        const float MOVE_TO_TARGET_TOLERANCE = 0.50f;
        const uint CONTROLS_REPEAT_DELAY = 100;         // control repeat delay in milliseconds.

        // a rough estimate based on a human falling in air
        // http://en.wikipedia.org/wiki/Terminal_velocity
        private const float TERMINAL_VELOCITY = -54.0f;


        public static readonly Vector3 VIEWER_DEFAULT_OFFSET = new Vector3(0.34f, 0.0f, 0.55f);

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public static byte[] DefaultTexture;

//        internal static RegionSettings s_RegionSettings;

        public UUID currentParcelUUID = UUID.Zero;
        private AnimationSet m_animations = new AnimationSet();
        private Dictionary<UUID, ScriptControllers> m_scriptedcontrols = new Dictionary<UUID, ScriptControllers>();
        private ScriptControlled IgnoredControls = ScriptControlled.CONTROL_ZERO;
        private ScriptControlled LastCommands = ScriptControlled.CONTROL_ZERO;
        private bool MouseDown = false;
        private SceneObjectGroup proxyObjectGroup;
        //private SceneObjectPart proxyObjectPart = null;
        public Vector3 lastKnownAllowedPosition;
        public bool sentMessageAboutRestrictedParcelFlyingDown;

        private bool m_updateflag;
        private uint m_movementflag;
        private readonly List<Vector3> m_forcesList = new List<Vector3>();
        private uint m_requestedSitTargetID = 0;
        private UUID m_requestedSitTargetUUID = UUID.Zero;
        private SceneObjectPart m_sitTargetPart = null;
        private Vector3 m_requestedSitTargetOffset;

        private bool m_startAnimationSet;

        //private Vector3 m_requestedSitOffset = new Vector3();

        private Vector3 m_LastFinitePos;

        // experimentally determined "fudge factor" to make sit-target positions
        // the same as in SecondLife. Fudge factor was tested for 36 different
        // test cases including prims of type box, sphere, cylinder, and torus,
        // with varying parameters for sit target location, prim size, prim
        // rotation, prim cut, prim twist, prim taper, and prim shear. See mantis
        // issue #1716
        private bool ADJUST_SIT_TARGET = true;  // do it the old OpenSim way for content compatibility
        private static readonly Vector3 m_sitTargetCorrectionOffset = new Vector3(0.1f, 0.0f, 0.3f);
        private float m_godlevel;

        private bool m_invulnerable = true;

        private Vector3 m_LastChildAgentUpdatePosition;
//        private Vector3 m_lastChildAgentUpdateCamPosition;
        private Vector3 m_LastRegionPosition = new Vector3(128, 128, 128);

        private int m_perfMonMS;
        private int m_lastCullCheckMS;

        private bool m_setAlwaysRun;

        private string m_movementAnimation = "DEFAULT";
        private string m_previousMovement = String.Empty;        // this doubles as our thread reentrancy lock (critical region) on anim updates
        private long m_animPersistUntil = 0;
        private bool m_allowFalling = false;
        private bool m_useFlySlow = false;
        private bool m_usePreJump = false;
        private bool m_forceFly = false;
        private bool m_flyDisabled = false;

        private float m_speedModifier = 1.0f;

        private Quaternion m_bodyRot= Quaternion.Identity;

        public override Quaternion Rotation
        {
            get {
                lock (m_posInfo)
                {
                    return m_bodyRot;
                }
            }
            set
            {
                lock (m_posInfo)
                {
                    m_bodyRot = value;
                    var pa = PhysicsActor;
                    if (pa != null)
                        pa.Rotation = value;
                    if (m_scene != null)
                        SendTerseUpdateToAllClients();
                }
            }
        }

        public bool IsRestrictedToRegion;

        public string JID = String.Empty;

        // Agent moves with a PID controller causing a force to be exerted.
        private float m_health = 100f;

        // Default AV Height
        private float m_avHeight = DEFAULT_AV_HEIGHT;

        protected RegionInfo m_regionInfo;

        // Position of agent's camera in world (region cordinates)
        protected Vector3 m_CameraCenter = Vector3.Zero;
        protected Vector3 m_lastCameraCenter = Vector3.Zero;

        // Use these three vectors to figure out what the agent is looking at
        // Convert it to a Matrix and/or Quaternion
        protected Vector3 m_CameraAtAxis = Vector3.Zero;
        protected Vector3 m_CameraLeftAxis = Vector3.Zero;
        protected Vector3 m_CameraUpAxis = Vector3.Zero;
        private uint m_AgentControlFlags;
        private Quaternion m_headrotation = Quaternion.Identity;
        private AgentState m_state;

        //Reuse the Vector3 instead of creating a new one on the UpdateMovement method
        private Vector3 movementvector = Vector3.Zero;

        private bool m_autopilotMoving;
        private Vector3 m_autoPilotTarget = Vector3.Zero;
        private bool m_sitAtAutoTarget;

        private string m_nextSitAnimation = String.Empty;

        //PauPaw:Proper PID Controler for autopilot************
        private bool m_moveToPositionInProgress;
        private Vector3 m_moveToPositionTarget = Vector3.Zero;
        //private int m_moveToPositionStateStatus = 0;
        //*****************************************************

        // Agent's Draw distance.
        protected float m_DrawDistance = 32.0f; // start with a minimum default draw distance (could be anything > 0)

        protected AvatarAppearance m_appearance;

        // Because appearance setting is in a module, we actually need
        // to give it access to our appearance directly, otherwise we
        // get a synchronization issue.
        public AvatarAppearance Appearance
        {
            get { return m_appearance; }
            set { m_appearance = value; }
        }

        public AgentPreferencesData m_agentPrefs;

        public AgentPreferencesData AgentPrefs
        {
            get { return m_agentPrefs; }
            set
            {
                bool updatedHover = (m_agentPrefs == null) ? true : (m_agentPrefs.HoverHeight != (float)value.HoverHeight);
                m_agentPrefs = value;
                if (updatedHover)
                {
                    // keep other viewers in sync
                    SendAppearanceToAllOtherAgents();
                }
            }
        }

        protected List<SceneObjectGroup> m_attachments = new List<SceneObjectGroup>();

        protected List<UUID> m_groupsRegisteredForCollisionEvents = new List<UUID>();

        protected enum Cardinals
        {
            N=1,NE,E,SE,S,SW,W,NW
        }
        /// <summary>
        /// Position at which a significant movement was made
        /// </summary>
        private Vector3 posLastSignificantMove;
        private Vector3 posLastCullCheck;

        // For teleports and crossings callbacks
        object m_callbackLock = new object();
        string m_callbackURI;
        ulong m_rootRegionHandle;
        ulong m_callbackTime;

        private IScriptModule[] m_scriptEngines;

        /// <summary>
        /// TickCount when we started a nudge, or zero if no nudge is running
        /// </summary>
        uint _nudgeStart;
        float _nudgeDuration;

        private ISceneView m_sceneView;

        public ISceneView SceneView
        {
            get { return m_sceneView; }
        }

        private Vector3 m_velocity;

        private bool m_sittingGround;

        private AgentUpdateArgs m_lastAgentUpdate;

        private bool m_closed = false;

        private Vector3 m_restoredConstantForce;

        private bool m_restoredConstantForceIsLocal;

        #region Properties

        public bool Closed
        {
            get { return m_closed; }
        }

        /// <summary>
        /// Physical scene representation of this Avatar.
        /// </summary>
        public PhysicsActor PhysicsActor { get; set; }

        public uint MovementFlag
        {
            set { m_movementflag = value; }
            get { return m_movementflag; }
        }

        public bool Updated
        {
            set { m_updateflag = value; }
            get { return m_updateflag; }
        }

        public bool Invulnerable
        {
            set { m_invulnerable = value; }
            get { return m_invulnerable; }
        }

        public float GodLevel
        {
            get { return m_godlevel; }
        }

        private readonly ulong m_regionHandle;

        public ulong RegionHandle
        {
            get { return m_regionHandle; }
        }

        public Vector3 CameraPosition
        {
            get { return m_CameraCenter; }
        }

        public Quaternion CameraRotation
        {
            get { return Util.Axes2Rot(m_CameraAtAxis, m_CameraLeftAxis, m_CameraUpAxis); }
        }

        public Vector3 Lookat
        {
            get
            {
                Vector3 a = new Vector3(m_CameraAtAxis.X, m_CameraAtAxis.Y, 0);

                if (a == Vector3.Zero)
                    return a;

                return Util.GetNormalizedVector(a);
            }
        }

        public string Firstname
        {
            get;
            set;
        }

        public string Lastname
        {
            get;
            set;
        }

        private string m_grouptitle;

        public string Grouptitle
        {
            get { return m_grouptitle; }
            set { m_grouptitle = value; }
        }

        public float DrawDistance
        {
            get { return m_DrawDistance; }
        }

        protected bool m_allowMovement = true;

        public bool AllowMovement
        {
            get { return m_allowMovement; }
            set { m_allowMovement = value; }
        }

        public bool SetAlwaysRun
        {
            get
            {
                var pa = PhysicsActor;
                if (pa != null)
                {
                    return pa.SetAlwaysRun;
                }
                else
                {
                    return m_setAlwaysRun;
                }
            }
            set
            {
                var pa = PhysicsActor;
                m_setAlwaysRun = value;
                if (pa != null)
                {
                    pa.SetAlwaysRun = value;
                }
            }
        }

        public AgentState State
        {
            get { return m_state; }
            set { m_state = value; }
        }

        public uint AgentControlFlags
        {
            get { return m_AgentControlFlags; }
            set { m_AgentControlFlags = value; }
        }

        /// <summary>
        /// This works out to be the ClientView object associated with this avatar, or it's client connection manager
        /// </summary>
        private IClientAPI m_controllingClient;

        /// <summary>
        /// This object manages the connection from this avatar to the server
        /// </summary>
        private Connection.AvatarConnection m_connection;
        public Connection.AvatarConnection Connection
        {
            get { return m_connection; }
        }


        private bool m_newPhysActorNeedsUpdate;

        private bool m_shouldJump = false;

        public bool ShouldJump { get { return m_shouldJump; } set { m_shouldJump = value; } }

        public SceneObjectPart SitTargetPart
        {
            get { return m_sitTargetPart;  }
        }

        // LinkNum 0 means avatar not seated
        private int m_linkNum = 0;
        public int LinkNum
        {
            get { return m_linkNum; }
            set { m_linkNum = value; }
        }

        private bool m_avatarMovesWithPart = true; // i.e. legacy mode, NOT avatar-as-a-prim (SL) mode
        public bool AvatarMovesWithPart
        {
            get { return m_avatarMovesWithPart;  }
            set { m_avatarMovesWithPart = value;  }
        }

        /// <value>
        /// The client controlling this presence
        /// </value>
        public IClientAPI ControllingClient
        {
            get { return m_controllingClient; }
        }

        public IClientCore ClientView
        {
            get { return (IClientCore) m_controllingClient; }
        }

        public void ForceAgentPositionInRegion()
        {
            const float BORDER_DISTANCE = 2.0f;
            Vector3 previous;
            Vector3 pos;

            lock (m_posInfo)
            {
                if (IsChildAgent)
                    return;
                if (m_posInfo.Parent != null)
                    return; // this function doesn't support all the combinations of seated prims

                pos = m_posInfo.Position;
                previous = pos;

                // Bring it nearby on a correction, but not 
                if (pos.X < Constants.OUTSIDE_REGION_NEGATIVE_EDGE)
                    pos.X = BORDER_DISTANCE;
                if (pos.Y < Constants.OUTSIDE_REGION_NEGATIVE_EDGE)
                    pos.Y = BORDER_DISTANCE;
                if (pos.X >= Constants.OUTSIDE_REGION)
                    pos.X = (float)Constants.RegionSize - BORDER_DISTANCE;
                if (pos.Y >= Constants.OUTSIDE_REGION)
                    pos.Y = (float)Constants.RegionSize - BORDER_DISTANCE;
                m_posInfo.Position = pos;
            }
            if (pos != previous)
                m_log.Error("[SCENE PRESENCE]: ForceAgentPositionInRegion - Unexpected position " + previous.ToString());
        }

        // This function updates AbsolutePosition (m_pos) even during transit
        // Call this version if you already have m_posInfo locked!
        // You must either supply the parcel at agentPos, supply a non-null parent, or call the other variant without a parcel and WITHOUT m_posInfo locked.
        public void SetAgentPositionInfo(ILandObject parcel, bool forced, Vector3 agentPos, SceneObjectPart parent, Vector3 parentPos, Vector3 velocity)
        {
            Vector3 pos;
            Vector3 oldVelocity;

            lock (m_posInfo)
            {
                if ((!forced) && (IsInTransit) && (parent != m_posInfo.Parent))
                    return;

                if (parent == null)
                {
                    // not seated
                    if (parcel != null)
                    {
                        ParcelPropertiesStatus reason;
                        float minZ;
                        // Returns false and minZ==non-zero if avatar is not allowed at this height, otherwise min height.
                        if (Scene.TestBelowHeightLimit(this.UUID, agentPos, parcel, out minZ, out reason))
                        {
                            // not a valid position for this avatar
                            if (!forced)
                                return; // illegal position
                        }
                        else
                            lastKnownAllowedPosition = agentPos;
                    }
                }

                m_posInfo.Set(agentPos, parent, parentPos);
                oldVelocity = m_velocity;
                m_velocity = velocity;
                ForceAgentPositionInRegion();
                pos = m_posInfo.Position;
            }

            var pa = PhysicsActor;
            if (pa != null)
            {
                if (velocity != oldVelocity)
                    pa.Velocity = velocity;
                pa.Position = pos;
            }
        }

        // You must only call this variant if m_posInfo is NOT already locked!
        // If it is locked you must call the other variant with a parcel above.
        public void SetAgentPositionInfo(bool forced, Vector3 agentPos, SceneObjectPart parent, Vector3 parentPos, Vector3 velocity)
        {
            ILandObject parcel = Scene.LandChannel.GetLandObject(agentPos.X, agentPos.Y);   // outside the lock

            SetAgentPositionInfo(parcel, forced, agentPos, parent, parentPos, velocity);
        }
        // You must only call this variant if m_posInfo is NOT already locked!
        // If it is locked you must call the other variant with a parcel above.
        public void SetAgentPositionInfo(bool forced, Vector3 agentPos, SceneObjectPart parent, Vector3 parentPos)
        {
            SetAgentPositionInfo(forced, agentPos, parent, parentPos, m_velocity);
        }

        private Vector3 _prevPosition = new Vector3(-1.0f, -1.0f, 999999.0f);   // any invalid position
        private Vector3 _GetPosition(bool checkParcelChange, bool updateFromPhysics)
        {
            bool inTransit;
            EntityBase.PositionInfo posinfo;
            SceneObjectPart parent = null;
            PhysicsActor physActor = null;
            Vector3 pos;
            Vector3 ppos;
            // Grab copies of self-referentially consistent data inside the lock.
            lock (m_posInfo)
            {
                inTransit = IsInTransit; //IsInTransit takes a lock really far down so do this here for good measure
                posinfo = GetPosInfo();
                pos = posinfo.Position;
                parent = posinfo.Parent;
                physActor = PhysicsActor;
                if (updateFromPhysics && (physActor != null))
                    ppos = physActor.Position;   // this seems to be safe to call inside the posInfo lock
                else
                    ppos = pos;
            }

            if ((physActor != null) && !inTransit)
            {
                bool posForced = false;
                if (IsBot && !Util.IsValidRegionXY(ppos))
                {
                    Util.ForceValidRegionXY(ref ppos);
                    physActor.Velocity = Vector3.Zero;
                    posForced = true;
                }

                bool mayHaveChangedParcels = (ppos.X != _prevPosition.X) || (ppos.Y != _prevPosition.Y) || (ppos.Z < _prevPosition.Z);
                if (checkParcelChange && mayHaveChangedParcels)    // needs re-check
                {
                    ILandObject parcel = Scene.LandChannel.GetLandObject(ppos.X, ppos.Y);
                    if (parcel != null)
                    {
                        ParcelPropertiesStatus reason;
                        float minZ;
                        // Returns false and minZ==non-zero if avatar is not allowed at this height, otherwise min height.
                        if (Scene.TestBelowHeightLimit(this.UUID, ppos, parcel, out minZ, out reason))
                        {
                            bool enforce = false;
                            if (parent == null)   // not seated
                                enforce = true;
                            else
                            if (parent != null)   // seated
                            {
                                if (parent.PhysActor != null)
                                    if (parent.PhysActor.IsPhysical)
                                        enforce = true;
                            }

                            if (enforce)
                            {
                                Vector3 newpos = this.lastKnownAllowedPosition;   // force back into valid location
                                // If not retreating from the parcel, bounce them on top of it.
                                ILandObject parcel2 = Scene.LandChannel.GetLandObject(newpos.X, newpos.Y);
                                if ((parcel2 != null) && (parcel2.landData.LocalID == parcel.landData.LocalID))
                                {
                                    // New parcel is the same parcel, still illegal
                                    newpos.Z = minZ + Constants.AVATAR_BOUNCE;
                                    Vector3 vel = physActor.Velocity;
                                    vel.Z = 0.0f;
                                    physActor.Velocity = vel;
                                }
                                ppos = newpos;
                                posForced = true;
                            }
                        }
                        else
                            this.lastKnownAllowedPosition = ppos;
                    }
                }
                _prevPosition = ppos;

                if (updateFromPhysics)
                {
                    lock (m_posInfo)
                    {
                        m_posInfo.SetPosition(ppos.X, ppos.Y, ppos.Z);
                        pos = m_posInfo.Position;
                        var pa = PhysicsActor;
                        if (posForced && pa != null) // in case it changed
                        {
                            pa.Position = pos;
                        }
                    }
                }
            }

            lock (m_posInfo)
            {
                SceneObjectPart part = parent;
                if (part != null)
                {
                    SceneObjectPart rootPart = part.ParentGroup.RootPart;

                    pos *= part.RotationOffset;
                    if (part != rootPart)
                    {
                        pos += part.OffsetPosition;   // already included in pos?
                        pos *= rootPart.RotationOffset;
                    }
                    pos += rootPart.GetWorldPosition();
                }

                // Sanity "bear trap" test for debugging
                if (!m_isChildAgent)
                {
                    float lower = -100.0f;
                    float upper = 356.0f;
                    if ((pos.X < lower) || (pos.Y < lower) || (pos.Y > upper) || (pos.X > upper))
                        m_log.Error("[SCENE PRESENCE]: AbsolutePosition - Unexpected position " + pos.ToString());
                }
            }
            return pos;
        }

        /// <summary>
        /// Absolute position of this avatar in 'region cordinates'
        /// </summary>
        public override Vector3 AbsolutePosition
        {
            get
            {
                return _GetPosition(false, false);  // quick position
            }
            set
            {
                // Clears parent position
                SetAgentPositionInfo(false, value, m_posInfo.Parent, Vector3.Zero, m_velocity);
            }
        }

        /// <summary>
        /// This function returns the absolute position if it is safe to do so.
        /// It is unsafe when the agent is in transit or being deleted.
        /// Otherwise it returns a zero vector.
        /// </summary>
        /// <param name="avpos">the returned position</param>
        /// <returns>Boolean indicating if it was possible to obtain the position.</returns>
        public bool HasSafePosition(out Vector3 avpos)
        {
            bool isSafe = false;
            lock (m_posInfo)
            {
                Vector3 pos = _GetPosition(false, false);
                isSafe = (!IsDeleted) && (!IsInTransit);
                if (isSafe)
                    avpos = AbsolutePosition;
                else
                    avpos = Vector3.Zero;
            }
            return isSafe;
        }

        public void SetAvatarAsAPrimMode()
        {
            lock (m_posInfo)
            {
                SceneObjectPart oldPart = m_posInfo.m_parent;
                SceneObjectPart rootPart = oldPart.ParentGroup.RootPart;
                if (rootPart != oldPart)
                {
                    // Reparent to root prim if not already
                    m_bodyRot = m_bodyRot / m_posInfo.m_parent.RotationOffset;
                    m_posInfo.Position += oldPart.OffsetPosition;
                    m_posInfo.m_parentPos = rootPart.GroupPosition;
                    m_posInfo.m_parent = rootPart;
                    oldPart.ReparentSeatedAvatar(this, rootPart);
                    rootPart.ReparentSeatedAvatar(this, rootPart);
                }

                // now in Avatar-As-A-Prim mode, only moves with root prim, not child prims
                m_avatarMovesWithPart = false;
            }
        }

        public void UpdateSeatedPosition(Vector3 newpos)
        {
            lock (m_posInfo)
            {
                m_posInfo.Position = newpos;
            }
            SendTerseUpdateToAllClients();
        }

        public void UpdateSeatedRotation(Quaternion newrot)
        {
            lock (m_posInfo)
            {
                m_bodyRot = newrot;
            }
            SendTerseUpdateToAllClients();
        }

        /// <summary>
        /// If this is true, agent doesn't have a representation in this scene.
        ///    this is an agent 'looking into' this scene from a nearby scene(region)
        ///
        /// if False, this agent has a representation in this scene
        /// </summary>
        private bool m_isChildAgent = true;
        public bool IsChildAgent
        {
            get
            {
                lock (m_posInfo)    // not really needed in this function but used as a critical section barrier to avoid parallelism
                {
                    return m_isChildAgent;
                }
            }
            set
            {
                lock (m_posInfo)    // not really needed in this function but used as a critical section barrier to avoid parallelism
                {
                    m_isChildAgent = value;
                }
            }
        }

        public bool IsBot { get; set; }

        /// <summary>
        /// If this user is a bot, then the bot has an owner
        /// </summary>
        public UUID OwnerID { get; set; }

        /// <summary>
        /// Current velocity of the avatar.
        /// </summary>
        public Vector3 Velocity
        {
            get
            {
                var pa = PhysicsActor;
                if (pa != null)
                {
                    m_velocity = pa.Velocity;
                }
                else
                {
                    return m_velocity;
                }

                return m_velocity;
            }
            set
            {
                //m_log.DebugFormat("[SCENE PRESENCE]: In {0} setting velocity of {1} to {2}", m_scene.RegionInfo.RegionName, Name, value);

                if (value.Z < TERMINAL_VELOCITY)    // < because this case both negative
                    value.Z = TERMINAL_VELOCITY;    // minimum value (-54.2 m/s/s)

                var pa = PhysicsActor;
                if (pa != null)
                {
                    try
                    {
                        pa.Velocity = value;
                    }
                    catch (Exception e)
                    {
                        m_log.Error("[SCENE PRESENCE]: VELOCITY " + e.Message);
                        pa.Velocity = OpenMetaverse.Vector3.Zero;
                        value = Vector3.Zero;
                    }
                }

                m_velocity = value;
            }
        }

        public float Health
        {
            get { return m_health; }
            set { m_health = value; }
        }

        /// <summary>
        /// These are the region handles known by the avatar.
        /// </summary>
        public IEnumerable<ulong> KnownChildRegionHandles
        {
            get 
            {
                return m_remotePresences.GetEstablishedRemotePresenceList().Select(
                    (AvatarRemotePresence p) => 
                    {
                        return p.PresenceInfo.RegionInfo.RegionHandle; 
                    });
            }
        }

        public AnimationSet Animations
        {
            get { return m_animations;  }
        }

        private bool m_mouseLook;
        private bool m_leftButtonDown;

        private AgentInRegionFlags m_AgentInRegionFlags = AgentInRegionFlags.None;
        public AgentInRegionFlags AgentInRegion
        {
            get { return m_AgentInRegionFlags; }
            set { m_AgentInRegionFlags = value; }
        }
        public bool IsFullyInRegion
        {
            get { return (m_AgentInRegionFlags & AgentInRegionFlags.FullyInRegion) == AgentInRegionFlags.FullyInRegion; }
        }
        public bool CanExitRegion
        {
            get { return (m_AgentInRegionFlags & AgentInRegionFlags.CanExitRegion) == AgentInRegionFlags.CanExitRegion; }
        }

        public bool IsInTransit
        {
            get { return m_scene.AvatarIsInTransit(UUID); }
        }

        public bool IsInTransitOnPrim
        {
            get { return m_scene.AvatarIsInTransitOnPrim(UUID); }
        }


        public float SpeedModifier
        {
            get { return m_speedModifier; }
            set { m_speedModifier = value; }
        }

        public bool ForceFly
        {
            get { return m_forceFly; }
            set { m_forceFly = value; }
        }

        public bool FlyDisabled
        {
            get { return m_flyDisabled; }
            set { m_flyDisabled = value; }
        }

        private int _attachmentRezCalled;

        private AvatarRemotePresences m_remotePresences;
        public AvatarRemotePresences RemotePresences
        {
            get
            {
                return m_remotePresences;
            }
        }

        #endregion

        #region Constructor(s)

        private ScenePresence(IClientAPI client, Scene world, RegionInfo reginfo)
        {
            m_regionHandle = reginfo.RegionHandle;
            m_controllingClient = client;
            Firstname = m_controllingClient.FirstName;
            Lastname = m_controllingClient.LastName;
            m_name = String.Format("{0} {1}", Firstname, Lastname);
            m_scene = world;
            m_uuid = client.AgentId;
            m_regionInfo = reginfo;
            m_localId = m_scene.AllocateLocalId();

            m_useFlySlow = m_scene.m_useFlySlow;
            m_usePreJump = m_scene.m_usePreJump;

            IGroupsModule gm = m_scene.RequestModuleInterface<IGroupsModule>();
            if (gm != null)
                m_grouptitle = gm.GetGroupTitle(m_uuid);

            m_scriptEngines = m_scene.RequestModuleInterfaces<IScriptModule>();

            ISceneViewModule sceneViewModule = m_scene.RequestModuleInterface<ISceneViewModule>();
            if (sceneViewModule != null)
                m_sceneView = sceneViewModule.CreateSceneView(this);
            else
                m_log.Warn("[SCENE PRESENCE]: Failed to create a scene view");

            SetAgentPositionInfo(null, true, m_controllingClient.StartPos, null, Vector3.Zero, m_velocity);

            m_animPersistUntil = 0;
            
            RegisterToEvents();
            SetDirectionVectors();
            SetDirectionFlags();

            m_remotePresences = new AvatarRemotePresences(world, this);
            m_connection = world.ConnectionManager.GetConnection(this.UUID);
            if (m_connection != null)   // can be null for bots which don't have a LLCV
                m_connection.ScenePresence = this;

            // Prime (cache) the user's group list.
            m_scene.UserGroupsGet(this.UUID);
        }

        public ScenePresence(IClientAPI client, Scene world, RegionInfo reginfo, AvatarAppearance appearance)
            : this(client, world, reginfo)
        {
            m_appearance = appearance;
        }

        public void RegisterToEvents()
        {
            m_controllingClient.OnRequestWearables += SendWearables;
            m_controllingClient.OnSetAppearance += SetAppearance;
            m_controllingClient.OnCompleteMovementToRegion += CompleteMovement;
            m_controllingClient.OnAgentUpdate += FilterAgentUpdate;
            m_controllingClient.OnAgentRequestSit += HandleAgentRequestSit;
            m_controllingClient.OnAgentSit += HandleAgentSit;
            m_controllingClient.OnSetAlwaysRun += HandleSetAlwaysRun;
            m_controllingClient.OnStartAnim += HandleStartAnim;
            m_controllingClient.OnStopAnim += HandleStopAnim;
            m_controllingClient.OnForceReleaseControls += HandleForceReleaseControls;
            m_controllingClient.OnAutoPilotGo += DoAutoPilot;
            m_controllingClient.AddGenericPacketHandler("autopilot", DoMoveToPosition);
            m_controllingClient.OnLogout += m_controllingClient_OnLogout;
            m_controllingClient.OnActivateGroup += HandleActivateGroup;
        }

        void m_controllingClient_OnLogout(IClientAPI obj)
        {
            m_remotePresences.TerminateAllNeighbors();
        }

        /// <summary>
        /// Implemented Control Flags
        /// </summary>
        internal enum Dir_ControlFlags
        {
            DIR_CONTROL_FLAG_FORWARD = AgentManager.ControlFlags.AGENT_CONTROL_AT_POS,
            DIR_CONTROL_FLAG_BACK = AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG,
            DIR_CONTROL_FLAG_LEFT = AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS,
            DIR_CONTROL_FLAG_RIGHT = AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG,
            DIR_CONTROL_FLAG_UP = AgentManager.ControlFlags.AGENT_CONTROL_UP_POS,
            DIR_CONTROL_FLAG_DOWN = AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG,
            DIR_CONTROL_FLAG_UP_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_POS,
            DIR_CONTROL_FLAG_DOWN_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_NEG,
            DIR_CONTROL_FLAG_FORWARD_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_POS,
            DIR_CONTROL_FLAG_REVERSE_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_NEG,
            DIR_CONTROL_FLAG_LEFT_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_LEFT_POS,
            DIR_CONTROL_FLAG_RIGHT_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_LEFT_NEG,
        }

        // These are the controls that re considered primary movements.
        internal static uint PrimaryMovements = (uint)(Dir_ControlFlags.DIR_CONTROL_FLAG_FORWARD | Dir_ControlFlags.DIR_CONTROL_FLAG_BACK |
                                                       Dir_ControlFlags.DIR_CONTROL_FLAG_LEFT | Dir_ControlFlags.DIR_CONTROL_FLAG_RIGHT |
                                                       Dir_ControlFlags.DIR_CONTROL_FLAG_UP | Dir_ControlFlags.DIR_CONTROL_FLAG_DOWN);

        private const int MaxControlElements = 12;
        private readonly Vector3[] Dir_Vectors = new Vector3[MaxControlElements];
        private readonly Dir_ControlFlags[] Dir_Flags = new Dir_ControlFlags[MaxControlElements];

        /// <summary>
        /// Direction flags array
        /// </summary>
        private void SetDirectionFlags()
        {
            Dir_Flags[0] = Dir_ControlFlags.DIR_CONTROL_FLAG_FORWARD;
            Dir_Flags[1] = Dir_ControlFlags.DIR_CONTROL_FLAG_BACK;
            Dir_Flags[2] = Dir_ControlFlags.DIR_CONTROL_FLAG_LEFT;
            Dir_Flags[3] = Dir_ControlFlags.DIR_CONTROL_FLAG_RIGHT;
            Dir_Flags[4] = Dir_ControlFlags.DIR_CONTROL_FLAG_UP;
            Dir_Flags[5] = Dir_ControlFlags.DIR_CONTROL_FLAG_DOWN;
            Dir_Flags[6] = Dir_ControlFlags.DIR_CONTROL_FLAG_UP_NUDGE;
            Dir_Flags[7] = Dir_ControlFlags.DIR_CONTROL_FLAG_DOWN_NUDGE;
            Dir_Flags[8] = Dir_ControlFlags.DIR_CONTROL_FLAG_FORWARD_NUDGE;
            Dir_Flags[9] = Dir_ControlFlags.DIR_CONTROL_FLAG_REVERSE_NUDGE;
            Dir_Flags[10] = Dir_ControlFlags.DIR_CONTROL_FLAG_LEFT_NUDGE;
            Dir_Flags[11] = Dir_ControlFlags.DIR_CONTROL_FLAG_RIGHT_NUDGE;
        }

        /// <summary>
        /// Direction vectors array
        /// </summary>
        private void SetDirectionVectors()
        {
            Dir_Vectors[0] = new Vector3(1, 0, 0); //FORWARD
            Dir_Vectors[1] = new Vector3(-1, 0, 0); //BACK
            Dir_Vectors[2] = new Vector3(0, 1, 0); //LEFT
            Dir_Vectors[3] = new Vector3(0, -1, 0); //RIGHT
            Dir_Vectors[4] = new Vector3(0, 0, 1); //UP
            Dir_Vectors[5] = new Vector3(0, 0, -1); //DOWN
            Dir_Vectors[6] = new Vector3(0, 0, 0.1f); //UP_Nudge
            Dir_Vectors[7] = new Vector3(0, 0, 0.05f); //DOWN_Nudge     -- Small positive improves landing from hover
            Dir_Vectors[8] = new Vector3(2, 0, 0); //FORWARD*2
            Dir_Vectors[9] = new Vector3(-2, 0, 0); //BACK
            Dir_Vectors[10] = new Vector3(0, 6, 0); // LEFT_Nudge        -- Strafe nudge is faster than fwd/back nudges
            Dir_Vectors[11] = new Vector3(0, -6, 0); // RIGHT_Nudge     --
        }

        /// <summary>
        /// Mouselook camera walk direction array
        /// </summary>
        /// <returns>Vector array of camera directions</returns>
        private Vector3[] GetWalkDirectionVectors()
        {
            Vector3[] vector = new Vector3[MaxControlElements];
            vector[0] = new Vector3(m_CameraUpAxis.Z, 0, -m_CameraAtAxis.Z); //FORWARD
            vector[1] = new Vector3(-m_CameraUpAxis.Z, 0, m_CameraAtAxis.Z); //BACK
            vector[2] = new Vector3(0, 1, 0); //LEFT
            vector[3] = new Vector3(0, -1, 0); //RIGHT
            vector[4] = new Vector3(m_CameraAtAxis.Z, 0, m_CameraUpAxis.Z); //UP
            vector[5] = new Vector3(-m_CameraAtAxis.Z, 0, -m_CameraUpAxis.Z); //DOWN
            vector[6] = new Vector3(m_CameraAtAxis.Z, 0, m_CameraUpAxis.Z); //UP_Nudge
            vector[7] = new Vector3(-m_CameraAtAxis.Z, 0, -m_CameraUpAxis.Z); //DOWN_Nudge
            vector[8] = (new Vector3(m_CameraUpAxis.Z, 0f, -m_CameraAtAxis.Z) * 2); //FORWARD Nudge
            vector[9] = new Vector3(-m_CameraUpAxis.Z, 0f, m_CameraAtAxis.Z); //BACK Nudge
            vector[10] = new Vector3(0, 1, 0); //LEFT_Nudge
            vector[11] = new Vector3(0, -1, 0); //RIGHT_Nudge
            return vector;
        }

#endregion

        public uint GenerateClientFlags(UUID ObjectID)
        {
            return m_scene.Permissions.GenerateClientFlags(m_uuid, ObjectID, false);
        }

#region Status Methods

        public SceneObjectPart GetSitTargetPart()
        {
            SceneObjectPart part = null;
            if (m_requestedSitTargetUUID != UUID.Zero)
                part = m_scene.GetSceneObjectPart(m_requestedSitTargetUUID);

            return part;
        }

        /// <summary>
        /// This turns a child agent, into a root agent
        /// This is called when an agent teleports into a region, or if an
        /// agent crosses into this region from a neighbor over the border
        /// Returns the parent ID of the agent, captured while the posInfo was locked.
        /// </summary>
        public SceneObjectPart MakeRootAgent(Vector3 pos)
        {
            SceneObjectPart part = GetSitTargetPart();

            DumpDebug("MakeRootAgent", "n/a");
            m_log.DebugFormat(
                "[SCENE]: Upgrading child to root agent for {0} in {1}",
                Name, m_scene.RegionInfo.RegionName);

            //m_log.DebugFormat("[SCENE]: known regions in {0}: {1}", Scene.RegionInfo.RegionName, KnownChildRegionHandles.Count);

            m_scene.SetRootAgentScene(m_uuid);

            if (m_requestedSitTargetID == 0)
            {
                //interpolate
                if (_childAgentUpdateTime != 0)
                {
                    const float MAX_CROSSING_INTERP_SECS = 3f;

                    float timePassed = Math.Min((Util.GetLongTickCount() - _childAgentUpdateTime) / 1000.0f, MAX_CROSSING_INTERP_SECS);

                    pos = pos + (Velocity * timePassed);
                    _childAgentUpdateTime = 0;
                    Util.ForceValidRegionXYZ(ref pos);  // don't let interpolation initiate a second crossing
                }

                if (!Util.IsValidRegionXYZ(pos))
                {
                    Vector3 emergencyPos = new Vector3(128, 128, 128);

                    m_log.WarnFormat(
                        "[SCENE PRESENCE]: MakeRootAgent() was given an illegal position of {0} for avatar {1}, {2}.  Substituting {3}",
                        pos, Name, UUID, emergencyPos);

                    pos = emergencyPos;
                }

                float posZLimit = (float)m_scene.Heightmap[(int)pos.X, (int)pos.Y];
                float newPosZ = posZLimit + m_avHeight / 2;
                if (posZLimit >= (pos.Z - (m_avHeight / 2)) && !(Single.IsInfinity(newPosZ) || Single.IsNaN(newPosZ)))
                {
                    pos.Z = newPosZ;
                }

                SetAgentPositionInfo(null, true, pos, null, Vector3.Zero, m_velocity);

                // get a new localid
                SwapToRootAgent();
                m_isChildAgent = false;
                if (!IsBot)
                    m_scene.CommsManager.UserService.MakeLocalUser(m_uuid);

                if (m_appearance != null)
                {
                    if (m_appearance.AvatarHeight > 0)
                        SetHeight(m_appearance.AvatarHeight);
                }
                else
                {
                    m_log.ErrorFormat("[SCENE PRESENCE]: null appearance in MakeRoot in {0}", Scene.RegionInfo.RegionName);
                    // emergency; this really shouldn't happen
                    m_appearance = new AvatarAppearance(UUID);
                }
            }
            else
            {
                // get a new localid
                SwapToRootAgent();

                //avatar is coming in sitting on a group
                ContinueSitAsRootAgent(m_controllingClient, part, m_requestedSitTargetOffset);
            }

            return m_posInfo.Parent;
        }

        /// <summary>
        /// Gets a new local ID for us to become root and informs the scene manager of the swap
        /// </summary>
        private void SwapToRootAgent()
        {
            uint oldId = m_localId;
            m_localId = m_scene.AllocateLocalId();
            m_scene.SwapChildToRootAgent(UUID, oldId, m_localId);
        }

        // This exists for those callers who want to wrap makeRootAgent in a lock. 
        // So the stuff that can't be nested inside locks, or doesn't need to be in the lock, goes here.
        public void PostProcessMakeRootAgent(SceneObjectPart parent, bool isFlying)
        {
            // Now that all the presence members are updated, add the physicsActor if needed.
            if (parent == null)
            {
                AddToPhysicalScene(isFlying);
                if (m_forceFly)
                {
                    PhysicsActor.Flying = true;
                }
                else if (m_flyDisabled)
                {
                    PhysicsActor.Flying = false;
                }
            }

            //if we have attachments to rez, do it up
            if (m_serializedAttachmentData != null)
            {
                var engine = ProviderRegistry.Instance.Get<ISerializationEngine>();

                m_log.InfoFormat("[SCENE PRESENCE]: CompleteMovement: Rezzing {0} attachments for {1}",
                    m_serializedAttachmentData.Count, this.UUID);

                foreach (byte[] attData in m_serializedAttachmentData)
                {
                    SceneObjectGroup attachment = engine.SceneObjectSerializer.DeserializeGroupFromBytes(attData);
                    if (m_scene.AddSceneObjectFromOtherRegion(attachment.UUID, attachment, (m_locomotionFlags & AgentLocomotionFlags.Teleport) != 0))
                    {
                        UUID itemID = attachment.GetFromItemID();
                        UUID assetID = attachment.UUID;

                        if (!attachment.IsTempAttachment)
                        {
                            m_appearance.SetAttachment(attachment.AttachmentPoint, false, itemID, assetID);
                        }

                        attachment.CreateScriptInstances(0, ScriptStartFlags.FromCrossing, m_scene.DefaultScriptEngine, (int)ScriptStateSource.PrimData, null);
                    }
                }

                m_serializedAttachmentData = null;
            }

            List<ScenePresence> AnimAgents = m_scene.GetScenePresences();
            foreach (ScenePresence p in AnimAgents)
            {
                if (p != this)
                    p.SendAnimPackToClient(ControllingClient);
            }

            ClearSceneView();

            m_scene.EventManager.TriggerOnMakeRootAgent(this);
        }

        private void ClearSceneView()
        {
            foreach (ScenePresence sp in Scene.GetScenePresences())
                sp.SceneView.ClearFromScene(this);
            SceneView.ClearScene();
        }

        private void ContinueSitAsRootAgent(IClientAPI client, SceneObjectPart part, Vector3 offset)
        {
            Quaternion sitOrientation = Quaternion.Identity;

            DumpDebug("ContinueSitAsRootAgent", "n/a");

            if (part != null)
            {
                lock (m_posInfo)
                {
                    SceneObjectGroup group = part.ParentGroup;
                    SitTargetInfo sitInfo = group.SitTargetForPart(part.UUID);

                    part.AddSeatedAvatar(this, false);
                    sitOrientation = sitInfo.Rotation;

                    m_requestedSitTargetUUID = part.UUID;
                    m_requestedSitTargetID = part.LocalId;

                    if (m_avatarMovesWithPart)
                    {
                        Vector3 newPos = sitInfo.Offset;
                        newPos += m_sitTargetCorrectionOffset;
                        m_bodyRot = sitInfo.Rotation;
                        //Rotation = sitTargetOrient;
                        SetAgentPositionInfo(null, true, newPos, part, part.AbsolutePosition, Vector3.Zero);
                    }
                }
                //m_animPersistUntil = 0;    // abort any timed animation

                // Avatar has arrived on prim
                int avatarsRemainingOnPrim = part.ParentGroup.RidingAvatarArrivedFromOtherSim();
                if (avatarsRemainingOnPrim > 0)
                {
                    m_log.InfoFormat("[SCENE PRESENCE]: {0} Recognizing incoming avatar {1} seated on {2} ({3} remain)",
                        Scene.RegionInfo.RegionName, this.UUID.ToString(), part.ParentGroup.Name, part.ParentGroup.AvatarsToExpect);
                }

                m_scene.EventManager.TriggerOnCrossedAvatarReady(part, this.UUID);

                //mitigation for client not getting the required immediate update for crossing objects
                //part.ParentGroup.SendFullUpdateToClientImmediate(this.ControllingClient);

                // Trigger any remaining events that rely on the avatar being present.
                if (!part.ParentGroup.IsAttachment)
                    if (avatarsRemainingOnPrim == 0)
                        part.ParentGroup.TriggerScriptChangedEvent(Changed.REGION);

                if (ControllingClient.DebugCrossings)
                {
                    ulong elapsedMs = (Util.GetLongTickCount() - part.ParentGroup.TimeReceived);
                    string msg = (elapsedMs / 1000.0f).ToString("0.000") + " seconds to confirm seated on object  for " + this.Name;
                    m_log.Info("[CROSSING]: " + msg);
                    MessageToUserFromServer(msg);
                }
            }
            else
            {
                m_log.ErrorFormat("[SCENE PRESENCE]: ContinueSitAsRootAgent could not find seated prim {0} for agent {1}", part.UUID.ToString(), client.AgentId.ToString());
            }
        }

        /// <summary>
        /// This turns a root agent into a child agent
        /// when an agent departs this region for a neighbor, this gets called.
        ///
        /// It doesn't get called for a teleport.  Reason being, an agent that
        /// teleports out may not end up anywhere near this region
        /// </summary>
        public void MakeChildAgent(ulong destinationRegionHandle)
        {
            DumpDebug("MakeChildAgent", "n/a");
            m_log.Info("[SCENE]: " + Scene.RegionInfo.RegionName + ": MakeChildAgent...");

            m_animations.Clear(m_scene.AvatarIsInTransitOnPrim(UUID));

            // Clear controls in the child SP. (They'll be set again if the root agent returns, if needed.
            lock (m_scriptedcontrols)
            {
                m_scriptedcontrols.Clear();
                IgnoredControls = ScriptControlled.CONTROL_ZERO;
            }

//            m_log.DebugFormat(
//                 "[SCENE PRESENCE]: Downgrading root agent {0}, {1} to a child agent in {2}",
//                 Name, UUID, m_scene.RegionInfo.RegionName);

            // Don't zero out the velocity since this can cause problems when an avatar is making a region crossing,
            // depending on the exact timing.  This shouldn't matter anyway since child agent positions are not updated.
            //Velocity = Vector3.Zero;

            RemoveFromPhysicalScene();
            m_requestedSitTargetID = 0;
            m_requestedSitTargetUUID = UUID.Zero;
            lock (m_posInfo)
            {
                Vector3 restorePos = m_posInfo.Parent == null ? AbsolutePosition : m_posInfo.m_parentPos;
                this.AgentInRegion = AgentInRegionFlags.None;
                m_isChildAgent = true;
                m_posInfo.Position = restorePos;
                m_posInfo.Parent = null;
                m_posInfo.m_parentPos = Vector3.Zero;
                m_scene.SwapRootAgentCount(true);
                currentParcelUUID = UUID.Zero;  // so that if the agent reenters this region, it recognizes it as a parcel change.
                if (!IsBot)
                    m_scene.CommsManager.UserService.UnmakeLocalUser(m_uuid);
            }
            m_scene.EventManager.TriggerOnMakeChildAgent(this);

            ClearSceneView();
        }

        /// <summary>
        /// Removes physics plugin scene representation of this agent if it exists.
        /// </summary>
        public void RemoveFromPhysicalScene()
        {
            DumpDebug("RemoveFromPhysicalScene", "n/a");
            Velocity = Vector3.Zero; 
            PhysicsActor pa = PhysicsActor;
            if (pa != null)
            {
                pa.OnRequestTerseUpdate -= SendTerseUpdateToAllClients;
                pa.OnPositionUpdate -= new PositionUpdate(m_physicsActor_OnPositionUpdate);
                m_scene.PhysicsScene.RemoveAvatar(pa);
                pa.UnSubscribeEvents();
                pa.OnCollisionUpdate -= PhysicsCollisionUpdate;
                PhysicsActor = null;
            }
        }

        public void VerifyInPhysicalScene(bool isFlying)
        {
            if (!IsChildAgent)
                if (PhysicsActor == null)
                    AddToPhysicalScene(isFlying);
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="pos"></param>
        public void Teleport(Vector3 pos)
        {
            Box boundingBox = GetBoundingBox(false);
            float zmin = (float)Scene.Heightmap.CalculateHeightAt(pos.X, pos.Y);
            if (pos.Z < zmin + (boundingBox.Extent.Z / 2))
                pos.Z = zmin + (boundingBox.Extent.Z / 2);
            Velocity = Vector3.Zero;
            AbsolutePosition = pos;

            SendTerseUpdateToAllClients();
        }

        public void TeleportWithMomentum(Vector3 pos)
        {
            AbsolutePosition = pos;

            SendTerseUpdateToAllClients();
        }

        /// <summary>
        ///
        /// </summary>
        public void StopMovement()
        {
        }

        public void StopFlying()
        {
            // It turns out to get the agent to stop flying, you have to feed it stop flying velocities
            // and send a full object update.
            // There's no message to send the client to tell it to stop flying

            m_animPersistUntil = 0;    // abort any timed animation
            TrySetMovementAnimation("LAND");
            SceneView.SendFullUpdateToAllClients();
        }

#endregion

#region Event Handlers

        /// <summary>
        /// Sets avatar height in the physics plugin
        /// </summary>
        public void SetHeight(float height)
        {
            m_avHeight = height;
            var pa = PhysicsActor;
            if (pa != null && !IsChildAgent)
            {
                pa.Size = new Vector3(0f, 0f, m_avHeight);
            }
        }

        public void MessageToUserFromServer(string msg)
        {
            Scene.SimChat(msg, ChatTypeEnum.Direct, 0, Vector3.Zero, this.Scene.RegionInfo.RegionName, UUID.Zero, this.UUID, UUID.Zero, false);
        }

        public void ConfirmHandoff(bool fromViewer)
        {
            ulong elapsedMs = (Util.GetLongTickCount() - m_callbackTime);
            string callbackURI;
            //m_log.Error(">>>>>>>>>> CONFIRM HANDOFF of "+this.Name+" fromViewer="+fromViewer.ToString());

            // There's a race between a fast viewer response and the server response. 
            // Server will almost always come in first but on a local test server it might not.
            lock (m_callbackLock)
            {
                // only send the release from one thread.
                callbackURI = m_callbackURI;
                m_callbackURI = null;
            }

            if (!String.IsNullOrEmpty(callbackURI))
            {
                m_log.WarnFormat("[SCENE PRESENCE]: Releasing agent for {0} in URI {1}", this.Name, callbackURI);
                Scene.SendReleaseAgent(m_rootRegionHandle, UUID, callbackURI);
            }
            if (ControllingClient.DebugCrossings && fromViewer && (m_callbackTime != 0))
            {
                string elapsed = (elapsedMs / 1000.0).ToString("0.000");
                string msg = elapsed + " seconds to confirm crossing complete for " + this.Name;
                m_log.Info("[CROSSING]: "+msg);
                MessageToUserFromServer(msg);
            }
        }

        /// <summary>
        /// Complete Avatar's movement into the region
        /// </summary>
        public void CompleteMovement()
        {
            int completeMovementStart = Environment.TickCount;
            m_log.InfoFormat("[SCENE PRESENCE]: CompleteMovement received for {0} ({1}) in region {2} status={3}",
                UUID.ToString(), Name, Scene.RegionInfo.RegionName, (uint)this.AgentInRegion);
            DumpDebug("CompleteMovement", "n/a");

            if (this.AgentInRegion != AgentInRegionFlags.None)
                return; // Duplicate parallel request? Avoid duplicate online notifications and other problems.

            // this.AgentInRegion is initialized to AgentInRegionFlags.None
            try
            {
                Vector3 look = Velocity;
                if (m_isChildAgent)
                {
                    Vector3 pos;
                    bool flying;
                    SceneObjectPart parent = null;
                    if (m_requestedSitTargetID != 0)
                        parent = Scene.GetSceneObjectPart(m_requestedSitTargetID);

                    lock (m_posInfo)
                    {
                        m_isChildAgent = false;
                        flying = ((m_AgentControlFlags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_FLY) != 0);
                        // m_log.Warn("[SCENE PRESENCE]: CompleteMovement, flying=" + flying.ToString());

                        pos = AbsolutePosition;
                        if (parent == null)
                        {
                            // not already marked seated, check for sitting
                            if (AvatarMovesWithPart && (m_requestedSitTargetID != 0))
                            {
                                // now make it all consistent with updated parent ID while inside the lock
                                SetAgentPositionInfo(null, true, m_sitTargetCorrectionOffset, parent, pos, m_velocity);
                            }
                        }
                    }

                    parent = MakeRootAgent(pos);

                    // Release the lock before calling PostProcessMakeRootAgent, it calls functions that use lock
                    PostProcessMakeRootAgent(parent, flying);
                    ConfirmHandoff(true);
                    //m_log.DebugFormat("[SCENE PRESENCE]: Completed movement");
                }

                m_controllingClient.MoveAgentIntoRegion(m_regionInfo, AbsolutePosition, look);
                this.AgentInRegion |= AgentInRegionFlags.CompleteMovementReceived;

                Util.FireAndForget(delegate(object o)
                {
                    int triggerOnCompletedMovementToNewRegionStart = Environment.TickCount;

                    try
                    {
                        Scene.CommsManager.UserService.GetUserProfile(this.UUID, true);  // force a cache refresh
                        this.AgentInRegion |= AgentInRegionFlags.FetchedProfile;

                        SendInitialData();
                        Scene.EventManager.TriggerOnCompletedMovementToNewRegion(this);
                    }
                    finally
                    {
                        m_log.DebugFormat("[SCENE PRESENCE]: TriggerOnCompletedMovementToNewRegion {0} ms", Environment.TickCount - triggerOnCompletedMovementToNewRegionStart);
                    }

                    Thread.Sleep(250);
                    m_scene.LandChannel.RefreshParcelInfo(m_controllingClient, true);
                    this.AgentInRegion |= AgentInRegionFlags.ParcelInfoSent;
                });
            }
            finally
            {
                m_log.DebugFormat("[SCENE PRESENCE]: CompleteMovement {0} ms", Environment.TickCount - completeMovementStart);
                this.AgentInRegion |= AgentInRegionFlags.CompleteMovementReceived;  // just in case
            }
        }

        /// <summary>
        /// Do everything required once a client completes its movement into a region
        /// </summary>
        public void SendInitialData()
        {
            // Moved this into CompleteMovement to ensure that m_appearance is initialized before
            // the inventory arrives
            // m_scene.GetAvatarAppearance(m_controllingClient, out m_appearance);

            if (!IsBot)
                SendAvatarData(ControllingClient, true);
            this.AgentInRegion |= AgentInRegionFlags.InitialDataReady;
            SceneView.SendInitialFullUpdateToAllClients();

            SendAnimPack();
        }

        public void FilterAgentUpdate(IClientAPI remoteClient, OpenMetaverse.Packets.AgentUpdatePacket.AgentDataBlock x)
        {
            bool update;

            if (m_lastAgentUpdate != null)
            {
                // These should be ordered from most-likely to
                // least likely to change. I've made an initial
                // guess at that.
                update =
                   (
                    (m_moveToPositionInProgress) ||
                    (Util.NotEquals(x.BodyRotation, m_lastAgentUpdate.BodyRotation, Util.DefaultComparePrecision)) ||
                    (Util.NotEquals(x.CameraAtAxis, m_lastAgentUpdate.CameraAtAxis, Util.DefaultComparePrecision)) ||
                    (Util.NotEquals(x.CameraCenter, m_lastAgentUpdate.CameraCenter, Util.DefaultComparePrecision)) ||
                    (Util.NotEquals(x.CameraLeftAxis, m_lastAgentUpdate.CameraLeftAxis, Util.DefaultComparePrecision)) ||
                    (Util.NotEquals(x.CameraUpAxis, m_lastAgentUpdate.CameraUpAxis, Util.DefaultComparePrecision)) ||
                    (x.ControlFlags != m_lastAgentUpdate.ControlFlags) ||
                    (Util.NotEquals(x.Far, m_lastAgentUpdate.Far, Util.DefaultComparePrecision)) ||
                    (x.Flags != m_lastAgentUpdate.Flags) ||
                    (x.State != m_lastAgentUpdate.State) ||
                    (Util.NotEquals(x.HeadRotation, m_lastAgentUpdate.HeadRotation, Util.DefaultComparePrecision)) ||
                    (x.SessionID != m_lastAgentUpdate.SessionID) ||
                    (x.AgentID != m_lastAgentUpdate.AgentID)
                   );
            }
            else
            {
                update = true;
            }

            if (update)
            {
                // m_log.WarnFormat("[SCENE PRESENCE]: AgentUpdate: {0} {1} {2} {3} {4}", this.Name, x.Flags.ToString("X2"), x.ControlFlags.ToString("X8"), x.State.ToString(), this.Velocity.ToString());
                AgentUpdateArgs arg = new AgentUpdateArgs();
                arg.AgentID = x.AgentID;
                arg.BodyRotation = x.BodyRotation;
                arg.CameraAtAxis = x.CameraAtAxis;
                arg.CameraCenter = x.CameraCenter;
                arg.CameraLeftAxis = x.CameraLeftAxis;
                arg.CameraUpAxis = x.CameraUpAxis;
                arg.ControlFlags = x.ControlFlags;
                arg.Far = x.Far;
                arg.Flags = x.Flags;
                arg.HeadRotation = x.HeadRotation;
                arg.SessionID = x.SessionID;
                arg.State = x.State;

                m_lastAgentUpdate = arg; // save this set of arguments for nexttime
                HandleAgentUpdate(remoteClient, arg);
            }
        }

        public Box GetBoundingBox(bool isRelative)
        {
            Vector3 center;
            Vector3 size;
            if (Animations.DefaultAnimation.AnimID == AnimationSet.Animations.AnimsUUID["SIT_GROUND_CONSTRAINED"])
            {
                // This is for ground sitting avatars
                float height = Appearance.AvatarHeight / 2.66666667f;
                size = new Vector3(0.675f, 0.9f, height);
                center = new Vector3(0.0f, 0.0f, -height);
            }
            else
            {
                // This is for standing/flying avatars
                size = new Vector3(0.45f, 0.6f, Appearance.AvatarHeight);
                center = Vector3.Zero;
            }
            if (!isRelative)
            {
                PositionInfo info = GetPosInfo();
                center += info.Position;
            }

            return new Box(center, size);
        }

        public bool IsAtTarget(Vector3 target, float tolerance)
        {
            Box shell = this.GetBoundingBox(false);
            Vector3 size = shell.Size;
            size.X += tolerance;
            size.Y += tolerance;
            size.Z += tolerance;
            shell = new Box(shell.Center, size);

            return shell.ContainsPoint(target);
        }

        public bool IsAtTarget(Vector3 target)
        {
            return IsAtTarget(target, MOVE_TO_TARGET_TOLERANCE * 2.0f);
        }

        public void UpdateForDrawDistanceChange()
        {
            m_remotePresences.HandleDrawDistanceChanged((uint)m_DrawDistance);
        }

        /// <summary>
        /// This is the event handler for client movement.   If a client is moving, this event is triggering.
        /// </summary>
        public void HandleAgentUpdate(IClientAPI remoteClient, AgentUpdateArgs agentData)
        {
            bool recoverPhysActor = false;
            if (m_isChildAgent)
            {
                //m_log.Warn("[CROSSING]: HandleAgentUpdate from child agent ignored "+agentData.AgentID.ToString());
                return;
            }
            if (IsInTransit)
            {
                // m_log.Error("[CROSSING]: AgentUpdate called during transit! Ignored.");
                return;
            }

            SceneObjectPart part = m_posInfo.Parent;
            EntityBase.PositionInfo posInfo = GetPosInfo();
            if (part != null)
            {   // sitting on a prim
                if (part.ParentGroup.InTransit)
                {
                    // m_log.Warn("[CROSSING]: AgentUpdate called during prim transit! Ignored.");
                    return;
                }
            }

            if (!posInfo.Position.IsFinite())
            {
                RemoveFromPhysicalScene();
                m_log.Error("[SCENE PRESENCE]: NonFinite Avatar position detected... Reset Position. Mantis this please. Error# 9999902");

                if (m_LastFinitePos.IsFinite())
                {
                    SetAgentPositionInfo(false, m_LastFinitePos, posInfo.Parent, Vector3.Zero, Vector3.Zero);
                }
                else
                {
                    Vector3 emergencyPos = new Vector3(127.0f, 127.0f, 127.0f);
                    SetAgentPositionInfo(false, emergencyPos, posInfo.Parent, Vector3.Zero, Vector3.Zero);
                    m_log.Error("[SCENE PRESENCE]: NonFinite Avatar position detected... Reset Position. Mantis this please. Error# 9999903");
                }

                AddToPhysicalScene(false);
            }
            else
            {
                m_LastFinitePos = m_posInfo.Position;
            }

            m_perfMonMS = Environment.TickCount;

            uint flags = agentData.ControlFlags;
            Quaternion bodyRotation = agentData.BodyRotation;

            // Camera location in world.  We'll need to raytrace
            // from this location from time to time.

            bool doCullingCheck = false;
            bool update_rotation = false;
            if (m_sceneView != null && m_sceneView.UseCulling)
            {
                if (!m_sceneView.NeedsFullSceneUpdate && (Environment.TickCount - m_lastCullCheckMS) > 0 &&
                    Vector3.DistanceSquared(agentData.CameraCenter, m_lastCameraCenter) > m_sceneView.DistanceBeforeCullingRequired * m_sceneView.DistanceBeforeCullingRequired)
                {
                    //Check for new entities that we may now be able to see with this camera movement
                    m_lastCameraCenter = agentData.CameraCenter;
                    doCullingCheck = true;
                }
                else if (!m_sceneView.NeedsFullSceneUpdate && agentData.Far > m_DrawDistance)
                {
                    //Check to see if the draw distance has gone up
                    doCullingCheck = true;
                }
                //Do a culling check, if required
                if (doCullingCheck)
                {
                    m_sceneView.CheckForDistantEntitiesToShow();
                    //Also tell all child regions about the change
                    SendChildAgentUpdate();
                    m_lastCullCheckMS = Environment.TickCount + 1000;//Only do the camera check at the most once a sec
                }
            }



            m_CameraCenter = agentData.CameraCenter;

            // Use these three vectors to figure out what the agent is looking at
            // Convert it to a Matrix and/or Quaternion
            m_CameraAtAxis = agentData.CameraAtAxis;
            m_CameraLeftAxis = agentData.CameraLeftAxis;
            m_CameraUpAxis = agentData.CameraUpAxis;


            // check if the Agent's Draw distance setting has changed
            if (m_DrawDistance != agentData.Far)
            {
                m_DrawDistance = agentData.Far;
                UpdateForDrawDistanceChange();
            }

            if ((flags & (uint) AgentManager.ControlFlags.AGENT_CONTROL_STAND_UP) != 0)
            {
                StandUp(false, true);
                bodyRotation = m_bodyRot;   // if standing, preserve the current rotation
                update_rotation = true;
            }

            m_mouseLook = (flags & (uint) AgentManager.ControlFlags.AGENT_CONTROL_MOUSELOOK) != 0;

            m_leftButtonDown = (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_LBUTTON_DOWN) != 0;

            lock (m_scriptedcontrols)
            {
                if (m_scriptedcontrols.Count > 0)
                {
                    SendControlToScripts(flags);
                    flags = RemoveIgnoredControls(flags, IgnoredControls);
                }
            }

            PhysicsActor physActor = PhysicsActor;

            m_AgentControlFlags = flags;
            m_headrotation = agentData.HeadRotation;
            m_state = (AgentState)agentData.State;

            if (physActor == null)
            {
                Velocity = Vector3.Zero;
                return;
            }

            if (m_autopilotMoving)
                CheckAtSitTarget();

            if ((flags & (uint) AgentManager.ControlFlags.AGENT_CONTROL_SIT_ON_GROUND) != 0)
            {
                m_animPersistUntil = 0;    // abort any timed animation
                TrySetMovementAnimation("SIT_GROUND_CONSTRAINED");
                m_sittingGround = true;
            }
            // In the future, these values might need to go global.
            // Here's where you get them.

            if (m_allowMovement)
            {
                bool update_movementflag = false;
                bool DCFlagKeyPressed = false;
                Vector3 agent_control_v3 = Vector3.Zero;

                // Update the physactor's rotation. This communicates the rotation to the character controller.
                physActor.Rotation = bodyRotation;

                bool oldflying = physActor.Flying;

                if (m_forceFly)
                    physActor.Flying = true;
                else if (m_flyDisabled)
                    physActor.Flying = false;
                else
                    physActor.Flying = ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_FLY) != 0);

                if (physActor.Flying != oldflying)
                {
                    update_movementflag = true;

                    if (physActor.Flying && physActor.CollidingGround)
                    {
                        physActor.AddForce(new Vector3(0f, 0f, FLY_LAUNCH_FORCE) * physActor.Mass, ForceType.GlobalLinearImpulse);
                    }
                }

                if (bodyRotation != m_bodyRot)
                {
                    m_bodyRot = bodyRotation;
                    update_rotation = true;
                }

                if (m_posInfo.Parent != null)
                {
                    // abort any automated movement
                    m_moveToPositionTarget = Vector3.Zero;
                    m_moveToPositionInProgress = false;
                    update_movementflag = true;
                }
                else
                {
                
                    bool bAllowUpdateMoveToPosition = false;
                    bool bResetMoveToPosition = false;

                    Vector3[] dirVectors;

                    // use camera up angle when in mouselook and not flying or when holding the left mouse button down and not flying
                    // this prevents 'jumping' in inappropriate situations.
                    if ((m_mouseLook || m_leftButtonDown) && !physActor.Flying)
                        dirVectors = GetWalkDirectionVectors();
                    else
                        dirVectors = Dir_Vectors;

                    bool nudgeStarted = false;
                    for (int i=0; i<Dir_Flags.Length; ++i)
                    {
                        Dir_ControlFlags DCF = Dir_Flags[i];

                        if ((flags & (uint) DCF) != 0)
                        {
                            bResetMoveToPosition = true;
                            DCFlagKeyPressed = true;
                            agent_control_v3 += dirVectors[i];
                            
                            if ((m_movementflag & (uint) DCF) == 0)
                            {
                                m_movementflag += (uint)DCF & PrimaryMovements;   // This is an abomination.
                                update_movementflag = true;

                                // The viewers do not send up or down nudges.
                                if ((DCF & Dir_ControlFlags.DIR_CONTROL_FLAG_FORWARD_NUDGE) != 0 ||
                                    (DCF & Dir_ControlFlags.DIR_CONTROL_FLAG_REVERSE_NUDGE) != 0 ||
                                    (DCF & Dir_ControlFlags.DIR_CONTROL_FLAG_DOWN_NUDGE) != 0 ||
                                    (DCF & Dir_ControlFlags.DIR_CONTROL_FLAG_LEFT_NUDGE) != 0 ||
                                    (DCF & Dir_ControlFlags.DIR_CONTROL_FLAG_RIGHT_NUDGE) != 0
                                   )
                                {
                                    //when we start a nudge, we let it run for a period of time and then cancel the force
                                    _nudgeStart = (uint)Environment.TickCount;
                                    if ((DCF & Dir_ControlFlags.DIR_CONTROL_FLAG_LEFT_NUDGE) != 0 ||
                                        (DCF & Dir_ControlFlags.DIR_CONTROL_FLAG_RIGHT_NUDGE) != 0)
                                        _nudgeDuration = NUDGE_DURATION_LR;
                                    else
                                        _nudgeDuration = NUDGE_DURATION_AT;

                                    nudgeStarted = true;
                                }
                                else
                                {
                                    if (!nudgeStarted) _nudgeStart = 0;
                                }

                                if (((DCF & Dir_ControlFlags.DIR_CONTROL_FLAG_UP) != 0) && physActor.CollidingGround && !physActor.Flying)
                                {
                                    //begin a jump
                                    physActor.AddForce(new Vector3(0.0f, 0.0f, JUMP_FORCE) * physActor.Mass, ForceType.LocalLinearImpulse);
                                }
                            }
                        }
                        else
                        {
                            if ((m_movementflag & (uint) DCF) != 0)
                            {
                                m_movementflag -= (uint) DCF & PrimaryMovements;  // This is an abomination.
                                update_movementflag = true;
                                if (!nudgeStarted) _nudgeStart = 0;
                            }
                            else
                            {
                                bAllowUpdateMoveToPosition = true;
                            }
                        }
                    }

                    //Paupaw:Do Proper PID for Autopilot here
                    if (bResetMoveToPosition)
                    {
                        m_moveToPositionTarget = Vector3.Zero;
                        m_moveToPositionInProgress = false;
                        update_movementflag = true;
                        bAllowUpdateMoveToPosition = false;
                    }

                    if (bAllowUpdateMoveToPosition && (m_moveToPositionInProgress && !m_autopilotMoving))
                    {
                        //Check the error term of the current position in relation to the target position
                        if (IsAtTarget(m_moveToPositionTarget))
                        {
                            // we are close enough to the target
                            m_moveToPositionTarget = Vector3.Zero;
                            m_moveToPositionInProgress = false;
                            update_movementflag = true;
                        }
                        else
                        {
                            try
                            {
                                // move avatar in 3D at one meter/second towards target, in avatar coordinate frame.
                                // This movement vector gets added to the velocity through AddNewMovement().
                                // Theoretically we might need a more complex PID approach here if other 
                                // unknown forces are acting on the avatar and we need to adaptively respond
                                // to such forces, but the following simple approach seems to works fine.
                                Vector3 LocalVectorToTarget =
                                    (m_moveToPositionTarget - AbsolutePosition) // vector from cur. pos to target in global coords
                                    * Matrix4.CreateFromQuaternion(Quaternion.Inverse(bodyRotation)); // change to avatar coords
                                LocalVectorToTarget.Normalize();
                                agent_control_v3 += LocalVectorToTarget;

                                Vector3 movementPush = (m_moveToPositionTarget - AbsolutePosition);
                                movementPush.Normalize();
                                movementPush.Z *= physActor.Mass;
                                if (physActor.IsColliding)
                                    movementPush.Z *= FLY_LAUNCH_FORCE;
                                physActor.AddForce(movementPush, ForceType.GlobalLinearImpulse);

                                // update avatar movement flags. the avatar coordinate system is as follows:
                                //
                                //                        +X (forward)
                                //
                                //                        ^
                                //                        |
                                //                        |
                                //                        |
                                //                        |
                                //     (left) +Y <--------o--------> -Y
                                //                       avatar
                                //                        |
                                //                        |
                                //                        |
                                //                        |
                                //                        v
                                //                        -X
                                //

                                // based on the above avatar coordinate system, classify the movement into 
                                // one of left/right/back/forward.
                                if (LocalVectorToTarget.Y > 0)//MoveLeft
                                {
                                    m_movementflag += (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_LEFT;
                                    update_movementflag = true;
                                }
                                else if (LocalVectorToTarget.Y < 0) //MoveRight
                                {
                                    m_movementflag += (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_RIGHT;
                                    update_movementflag = true;
                                }
                                if (LocalVectorToTarget.X < 0) //MoveBack
                                {
                                    m_movementflag += (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_BACK;
                                    update_movementflag = true;
                                }
                                else if (LocalVectorToTarget.X > 0) //Move Forward
                                {
                                    m_movementflag += (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_FORWARD;
                                    update_movementflag = true;
                                }
                                if (LocalVectorToTarget.Z > 0) //Up
                                {
                                    // Don't set these flags for up - doing so will make the avatar
                                    // keep trying to jump even if walking along level ground.
                                    // m_movementflag += (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_UP;
                                    update_movementflag = true;
                                }
                                else if (LocalVectorToTarget.Z < 0) //Down
                                {
                                    // Don't set these flags for down - doing so will make the avatar crouch.
                                    // m_movementflag += (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_DOWN;
                                    update_movementflag = true;
                                }
                            }
                            catch (Exception)
                            {

                                //Avoid system crash, can be slower but...
                            }

                        }
                    }

                    // Determine whether the user has said to stop and the agent is not sitting.
                    physActor.SetAirBrakes = (m_AgentControlFlags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_STOP) != 0 && !IsInTransitOnPrim && !m_moveToPositionInProgress;

                }
                
                // Cause the avatar to stop flying if it's colliding
                // with something with the down arrow pressed.

                // Only do this if we're flying
                if (physActor != null && physActor.Flying && !m_forceFly)
                {
                    // Are the landing controls requirements filled?
                    bool controlland = (((flags & (uint) AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG) != 0) ||
                                        ((flags & (uint) AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_NEG) != 0));

                    // Are the collision requirements fulfilled?
                    bool colliding = (physActor.CollidingGround == true);

                    if (physActor.Flying && colliding && controlland)
                    {
                        StopFlying();
                    }
                }

                if (m_newPhysActorNeedsUpdate && physActor != null)
                    update_movementflag = true;

                if (!m_sittingGround && (update_movementflag || (update_rotation && DCFlagKeyPressed)))
                {
                    AddNewMovement(agent_control_v3, bodyRotation);

                    if (update_movementflag)
                        UpdateMovementAnimations();

                    if (physActor != null)
                        m_newPhysActorNeedsUpdate = false;
                }
                else if (update_rotation)
                {
                    //avatar is spinning with no other changes
//                    m_log.WarnFormat("[SP]: HandleAgentUpdate: Sending terse update vel={0}",this.Velocity);
                    SendTerseUpdateToAllClients();
                }
            }

            m_scene.EventManager.TriggerOnClientMovement(this);

            m_scene.StatsReporter.AddAgentTime(Environment.TickCount - m_perfMonMS);
        }

        public void DoAutoPilot(uint not_used, Vector3 Pos, IClientAPI remote_client)
        {
            if (IsChildAgent || IsInTransit)
            {
                m_log.Info("[SCENE PRESENCE]: DoAutoPilot: Request from child agent ignored - " + this.UUID.ToString());
                return;
            }

            //m_log.Debug("[SCENE PRESENCE]: DoAutoPilot: Auto-move " + this.UUID.ToString() + " to " + Pos.ToString());
            m_autopilotMoving = true;
            m_autoPilotTarget = Pos;
            m_sitAtAutoTarget = false;
            PrimitiveBaseShape proxy = PrimitiveBaseShape.Default;
            //proxy.PCode = (byte)PCode.ParticleSystem;

            proxyObjectGroup = new SceneObjectGroup(UUID, Pos, Rotation, proxy, false);
            proxyObjectGroup.AttachToScene(m_scene, false);
            
            // Commented out this code since it could never have executed, but might still be informative.
//            if (proxyObjectGroup != null)
//            {
                proxyObjectGroup.SendGroupFullUpdate(PrimUpdateFlags.ForcedFullUpdate);
                remote_client.SendSitResponse(proxyObjectGroup.UUID, Vector3.Zero, Quaternion.Identity, true, Vector3.Zero, Vector3.Zero, false);
                m_scene.DeleteSceneObject(proxyObjectGroup, false);
//            }
//            else
//            {
//                m_autopilotMoving = false;
//                m_autoPilotTarget = Vector3.Zero;
//                ControllingClient.SendAlertMessage("Autopilot cancelled");
//            }
        }

        public void DoMoveToPosition(Object sender, string method, List<String> args)
        {
            if (m_isChildAgent)
            {
                m_log.Info("[SCENE PRESENCE]: DoMoveToPosition: Request from child agent ignored - " + this.UUID.ToString());
                return;
            }
            try
            {
                float locx = 0f;
                float locy = 0f;
                float locz = 0f;
                uint regionX = 0;
                uint regionY = 0;
                try
                {
                    Utils.LongToUInts(Scene.RegionInfo.RegionHandle, out regionX, out regionY);
                    locx = Convert.ToSingle(args[0]) - (float)regionX;
                    locy = Convert.ToSingle(args[1]) - (float)regionY;
                    locz = Convert.ToSingle(args[2]);
                }
                catch (InvalidCastException)
                {
                    m_log.Error("[SCENE PRESENCE]: Invalid autopilot request");
                    return;
                }
                m_moveToPositionInProgress = true;
                m_moveToPositionTarget = new Vector3(locx, locy, locz);
            }
            catch (Exception ex)
            {
                //Why did I get this error?
                m_log.Error("[SCENE PRESENCE]: DoMoveToPosition" + ex);
            }
        }

        public void StopMoveToTarget()
        {
            m_moveToPositionInProgress = false;
            m_moveToPositionTarget = Vector3.Zero;
        }

        private void CheckAtSitTarget()
        {
            // m_log.Debug("[SCENE PRESENCE]: " + Util.GetDistanceTo(AbsolutePosition, m_autoPilotTarget).ToString());
            if (Util.GetDistanceTo(AbsolutePosition, m_autoPilotTarget) <= 1.5)
            {
                if (m_sitAtAutoTarget)
                {
                    SceneObjectPart part = GetSitTargetPart();
                    if (part != null)
                    {
                        lock (m_posInfo)
                        {
                            if (m_posInfo.Parent != null)
                            {
                                m_log.ErrorFormat("[SCENE PRESENCE]: CheckAtSitTarget {0} while already sitting on {1}.", m_autoPilotTarget.ToString(), m_posInfo.Parent.UUID.ToString());
                            }
                            else
                            {
                                m_requestedSitTargetUUID = UUID.Zero;
                                m_requestedSitTargetID = 0;
                                SetAgentPositionInfo(null, false, part.AbsolutePosition, null, Vector3.Zero, m_velocity);
                            }
                        }
                        SceneView.SendFullUpdateToAllClients();
                    }
                }
                    /*
                else
                {
                    //ControllingClient.SendAlertMessage("Autopilot cancelled");
                    //SendTerseUpdateToAllClients();
                    //PrimitiveBaseShape proxy = PrimitiveBaseShape.Default;
                    //proxy.PCode = (byte)PCode.ParticleSystem;
                    ////uint nextUUID = m_scene.NextLocalId;

                    //proxyObjectGroup = new SceneObjectGroup(m_scene, m_scene.RegionInfo.RegionHandle, UUID, nextUUID, m_autoPilotTarget, Quaternion.Identity, proxy);
                    //if (proxyObjectGroup != null)
                    //{
                        //proxyObjectGroup.SendGroupFullUpdate();
                        //ControllingClient.SendSitResponse(UUID.Zero, m_autoPilotTarget, Quaternion.Identity, true, Vector3.Zero, Vector3.Zero, false);
                        //m_scene.DeleteSceneObject(proxyObjectGroup);
                    //}
                }
                */
                m_autoPilotTarget = Vector3.Zero;
                m_autopilotMoving = false;
            }
        }

        public event StandUpDelegate OnMustReleaseControls;
        public delegate void StandUpDelegate(ScenePresence presence, SceneObjectPart part, TaskInventoryItem item, bool silent);
        // Called on user Stand Up or on ForceReleaseControls
        public void TriggerOnReleaseControls(ScenePresence presence, SceneObjectPart part, TaskInventoryItem item, bool silent)
        {
            StandUpDelegate handlerReleaseControls = OnMustReleaseControls;
            if (handlerReleaseControls != null)
            {
                handlerReleaseControls(this, part, item, silent);
            }
        }

        private void DumpDebug(string where, string fromCrossing)
        {
            if (Scene.DebugCrossingsLevel == 0)
                return;

            int threadId = Thread.CurrentThread.ManagedThreadId;
            PositionInfo posInfo = this.GetPosInfo();
            SceneObjectPart part = posInfo.Parent;
            if (part == null)
                m_log.InfoFormat("[SCENE PRESENCE]: Thread {0} from {1} in {2} fromCrossing={3} child={4} phys={5} InTransit={6} group: null Pos={7}",
                        threadId.ToString(), where, m_scene.RegionInfo.RegionName, fromCrossing, IsChildAgent.ToString(), (PhysicsActor != null).ToString(), IsInTransit, posInfo.Position.ToString());
            else
                m_log.InfoFormat("[SCENE PRESENCE]: Thread {0} from {1} in {2} fromCrossing={3} child={4} phys={5} m_pos={6} m_parentPos={7} parentID={8} InTransit={9} grpIsInTransit={10} grpDel={11} Cam={12}",
                        threadId.ToString(), where, m_scene.RegionInfo.RegionName, fromCrossing, IsChildAgent.ToString(), (PhysicsActor != null).ToString(), posInfo.Position.ToString(), part.UUID.ToString(), IsInTransit,
                        part.ParentGroup.InTransit.ToString(), part.ParentGroup.IsDeleted.ToString(), part.AbsolutePosition.ToString(), part.GetCameraAtOffset().ToString());
        }

        // Any sit target offset beyond this distance from the prim is assumed to be a teleporter
        // and on a StandUp, we do not try to reposition the avatar on the top of the prim.
        const float TELEPORTER_THRESHOLD_DISTANCE = 5.0f;
        /// <summary>
        /// Perform the logic necessary to stand the client up.  This method also executes
        /// the stand animation.
        /// </summary>
        public void StandUp(bool fromCrossing, bool recheckGroupForAutoreturn)
        {
            SceneObjectPart part = null;
            float partTop = 0.0f;
            //bool clearTransit = false;
            bool clearCam = false;

            //standing from ground?
            if (m_sittingGround)
            {
                m_sittingGround = false;
                TrySetMovementAnimation("STAND");
                return;
            }

            lock (m_scene.SyncRoot)
            {
                DumpDebug("StandUp", fromCrossing.ToString());

                PositionInfo info = GetPosInfo();

                if (info.Parent != null)
                {
                    if (m_requestedSitTargetUUID != UUID.Zero)
                        part = Scene.GetSceneObjectPart(m_requestedSitTargetUUID);  // stand from sit target
                    else
                        part = info.Parent;     // stand from prim without sit target

                    if (part == null)
                    {
                        m_log.ErrorFormat("[SCENE PRESENCE]: StandUp for {0} with parentID={1} NOT FOUND in scene. Recovering position.", this.Name, (info.Parent == null) ? 0 : info.Parent.LocalId);
                        // Recover, by emulating a default sit, for the code below.
                        info.Position = m_sitTargetCorrectionOffset + m_LastRegionPosition;
                    }
                    else
                    {
                        // Need to release controls from all scripts on ALL prim in this object where the target user is this one.
                        var parts = part.ParentGroup.GetParts();
                        foreach (SceneObjectPart prim in parts)
                        {
                            TaskInventoryDictionary taskIDict = prim.TaskInventory;
                            if (taskIDict != null)
                            {
                                lock (taskIDict)
                                {
                                    foreach (UUID taskID in taskIDict.Keys)
                                    {
                                        TaskInventoryItem item = taskIDict[taskID];
                                        bool silent = IsChildAgent || fromCrossing; // never send the controls updates to child agents on a stand
                                        this.TriggerOnReleaseControls(this, prim, item, silent); // handler checks first param for match
                                    }
                                }

                            }
                        }

                        // This avatar is no longer seated.
                        part.RemoveSeatedAvatar(this, !fromCrossing);
                        m_avatarMovesWithPart = true;  // reset SP to legacy mode, not avatar-as-a-prim

                        if (!fromCrossing)
                            clearCam = true;

                        if (IsChildAgent)
                            return; // cleanup complete

                        if (part.ParentGroup.InTransit && (!fromCrossing) && ControllingClient.IsActive)
                        {
                            m_log.Warn("[SCENE PRESENCE]: StandUp called during transit! Ignored.");
                            return;
                        }

                        /*
                        clearTransit = true;
                        part.ParentGroup.StartTransit();
                        */

                        // if not phantom, adjust for the height of the prim to stand up on top of it
                        PhysicsActor physActor = part.PhysActor;
                        if (physActor != null)
                        {
                            Vector3 bbox = part.BoundingBox.Size * part.RotationOffset;
                            partTop = part.BoundingBox.Center.Z + Math.Abs(bbox.Z) / 2.0f;
                        }

                        m_scene.InspectForAutoReturn(part.ParentGroup);
                    }

                    m_requestedSitTargetID = 0;
                    m_requestedSitTargetUUID = UUID.Zero;

                    if (fromCrossing)
                    {
                        Velocity = Vector3.Zero;
                        info.Parent = null;
                        m_posInfo.Set(info);

                        // This is all we do when standing up from a prim so it can be removed from a scene for a crossing.
                        /*if (clearTransit)
                            part.ParentGroup.EndTransit();
                         */
                    }
                    else
                    {
                        Quaternion targetRot = this.Rotation;
                        if (part != null)
                        {
                            if ((part.StandTargetPos != Vector3.Zero) || (part.StandTargetRot != Quaternion.Identity))
                            {
                                // Stand target defined
                                Quaternion partRot = part.GetWorldRotation();
                                info.Position = part.AbsolutePosition + (part.StandTargetPos * partRot);
                                targetRot = partRot * part.StandTargetRot;
                            }
                            else
                            {
                                Vector3 partPos = part.GetWorldPosition();

                                SitTargetInfo sitInfo = part.ParentGroup.SitTargetForPart(part.UUID);

                                // Otherwise, we have a lot to do, it's a real stand up operation.
                                // if there is a part with a sit target

                                if (sitInfo.IsActive)
                                {   // prim not found, or has a sit target (just use that offset)
                                    info.Position = AbsolutePosition;   // don't change it from where we are now, but update with the current absolute position
                                }
                                else
                                {   // no prim, or prim does not have a sit target beyond the teleporter threshold
                                    Vector3 newpos;
                                    if (partTop != 0.0f)
                                    {
                                        // Let's position the avatar on top of the prim they were on.
                                        newpos = info.Position + partPos;   // original position but...
                                        newpos.Z = partTop;             // but on the top of it
                                    }
                                    else
                                    {
                                        // Can't find the prim they were on so put them back where they
                                        // were with the sit target on the poseball set with the offset.
                                        newpos = partPos - (info.Position - m_sitTargetCorrectionOffset);
                                    }

                                    // And let's assume that the sit pos is roughly at vertical center of the avatar, and that when standing,
                                    // we should add roughtly half the height of the avatar to put their feet at the target location, not their center.
                                    newpos.Z += (m_avHeight / 2.0f);

                                    // now finally update it
                                    info.Position = newpos;
                                }
                            }
                        }

                        // Now finally update the actual SP position
                        info.Parent = null;
                        m_posInfo.Set(info);
                        this.Rotation = targetRot;
                        bool needsSetHeight = true;
                        if (PhysicsActor == null)
                        {
                            needsSetHeight = false;
                            // be careful, do not call this when fromCrossing is true
                            AddToPhysicalScene(false);
                        }

                        /*if (clearTransit && (part != null))
                            part.ParentGroup.EndTransit();*/

                        SceneView.SendFullUpdateToAllClients();
                        if (clearCam)
                            ControllingClient.SendClearFollowCamProperties(part.ParentGroup.UUID);

                        if (needsSetHeight && m_avHeight > 0)
                        {
                            SetHeight(m_avHeight);
                        }

                        m_animPersistUntil = 0;    // abort any timed animation
                        TrySetMovementAnimation("STAND");
                    }
                }
            }
        }

        private SceneObjectPart FindNextAvailableSitTarget(UUID targetID)
        {
            SceneObjectPart targetPart = m_scene.GetSceneObjectPart(targetID);
            if (targetPart == null)
                return null;

            // If the primitive the player clicked on has a sit target and that sit target is not full, that sit target is used.
            // If the primitive the player clicked on has no sit target, and one or more other linked objects have sit targets that are not full, the sit target of the object with the lowest link number will be used.

            // Get our own copy of the part array, and sort into the order we want to test
            var allParts = targetPart.ParentGroup.GetParts();
            SceneObjectPart[] partArray = allParts.ToArray();

            Array.Sort(partArray, delegate(SceneObjectPart p1, SceneObjectPart p2)
                       {
                           // we want the originally selected part first, then the rest in link order -- so make the selected part link num (-1)
                           int linkNum1 = p1==targetPart ? -1 : p1.LinkNum;
                           int linkNum2 = p2==targetPart ? -1 : p2.LinkNum;
                           return linkNum1 - linkNum2;
                       }
                );

            //look for prims with explicit sit targets that are available
            foreach (SceneObjectPart part in partArray)
            {
                // Is a sit target available?
                SitTargetInfo sitInfo = part.ParentGroup.SitTargetForPart(part.UUID);
                if (sitInfo.IsActive && !sitInfo.HasSitter)
                {
                    //switch the target to this prim
                    return part;
                }
            }

            // no explicit sit target found - use original target
            return targetPart;
        }

        private void SendSitResponse(IClientAPI remoteClient, UUID targetID, Vector3 offset)
        {
            // The viewer requires parent ID, position and rotation to be relative to the root prim.
            // Internally, we will continue to track parentID, offset and m_bodyRot relative to the child prim.
            // The following three variables with a 'v' prefix refer to the viewer-centric model.
            UUID vParentID;     // parentID to send to viewer, always the root prim
            Vector3 vPos;       // viewer position of avatar relative to root prim
            Quaternion vRot;    // viewer rotation of avatar relative to root prim

            // We'll use the next two to update the internal avatar position and rotation.
            Vector3 avSitPos;
            Quaternion avSitRot;

            Vector3 cameraEyeOffset = Vector3.Zero;
            Vector3 cameraAtOffset = Vector3.Zero;
            bool forceMouselook = false;

            DumpDebug("SendSitResponse", "n/a");
            SceneObjectPart part = null;
            lock (m_scene.SyncRoot)
            {
                part = FindNextAvailableSitTarget(targetID);
                if (part == null)
                {
                    m_log.Error("[SCENE PRESENCE]: SendSitResponse could not find part " + targetID.ToString());
                    remoteClient.SendAgentAlertMessage("Could not sit - seat could not be found in region.", false);
                    return;
                }

                SitTargetInfo sitInfo = part.ParentGroup.SitTargetForPart(part.UUID);

                // First, remove the PhysicsActor since we're going to be sitting, so that physics doesn't interfere while we're doing this update.
                if (PhysicsActor != null)
                {
                    RemoveFromPhysicalScene();
                }

                // Determine position to sit at based on scene geometry; don't trust offset from client
                // see http://wiki.secondlife.com/wiki/User:Andrew_Linden/Office_Hours/2007_11_06 for details on how LL does it

                // The viewer requires parent ID, position and rotation to be relative to the root prim.
                // Internally, we will continue to track parentID, offset and m_bodyRot relative to the child prim.
                // The following three variables with a 'v' prefix refer to the viewer-centric model.
                SceneObjectPart rootPart = part.ParentGroup.RootPart;
                vParentID = rootPart.UUID;         // parentID to send to viewer, always the root prim
                vPos = Vector3.Zero;            // viewer position of avatar relative to root prim
                vRot = Quaternion.Identity;  // viewer rotation of avatar relative to root prim
                avSitPos = Vector3.Zero;
                avSitRot = rootPart.RotationOffset;

                if (part != rootPart)
                {
                    vRot *= part.RotationOffset;
                    avSitRot *= part.RotationOffset;
                }

                // Viewer seems to draw the avatar based on the hip position.
                // If you don't include HipOffset (which is raising the avatar 
                // since it's normally negative), then the viewer will draw 
                // the avatar walking with toes underground/inside prim.
                // Full updates were missing this, so a rebake would reproduce it.
                // This adjustment gives the viewer the position it expects.
                vPos.Z -= m_appearance.HipOffset;

                if (sitInfo.IsActive)
                {
                    avSitPos += sitInfo.Offset;
                    if (ADJUST_SIT_TARGET)
                    {
                        // If we want to support previous IW sit target offsets, rather than SL-accurate sit targets,
                        // we need to apply the OpenSim sit target correction adjustment.
                        avSitPos += m_sitTargetCorrectionOffset;
                    }
                    avSitRot *= sitInfo.Rotation;
                    vRot *= sitInfo.Rotation;
                }
                else
                {
                    // Make up a desired facing, relative to the child prim seated on.
                    // We'll use the prim rotation for now.
//                    avSitPos += Vector3.Zero;         // could put the avatar on top of the prim
//                    avSitRot *= Quaternion.Identity;  // could face the avatar to the side clicked on
//                    vRot *= Quaternion.Identity;  // could face the avatar to the side clicked on
                }

#if false
                // I believe this is correct for root-relative storage but not for now, 
                // while we want to maintain it relative to the parentID pointing at child prim.
                avSitPos += part.OffsetPosition;
                if (part == rootPart)
                    avSitPos *= rootPart.RotationOffset;
#endif
                // The only thing left to make avSitPos completely absolute would be to add rootPart.AbsolutePosition
                // but SetAgentPositionInfo takes that as a parameter.

                lock (m_posInfo)
                {
                    // Update these together
                    SetAgentPositionInfo(null, true, avSitPos, part, part.AbsolutePosition, Vector3.Zero);
                    // now update the part to reflect the new avatar
                    part.AddSeatedAvatar(this, true);
                    // Now update the SP.Rotation with the sit rotation
                    // m_bodyRot also needs the root rotation
                    m_bodyRot = avSitRot;
                }

                cameraAtOffset = part.GetCameraAtOffset();
                cameraEyeOffset = part.GetCameraEyeOffset();
                forceMouselook = part.GetForceMouselook();

                m_requestedSitTargetUUID = part.UUID;
                m_requestedSitTargetID = part.LocalId;

                // This calls HandleAgentSit twice, once from here, and the client calls
                // HandleAgentSit itself after it gets to the location
                // It doesn't get to the location until we've moved them there though
                // which happens in HandleAgentSit :P
                m_autopilotMoving = false;
                m_autoPilotTarget = offset;
                m_sitAtAutoTarget = false;
            }

            //we're sitting on a prim, so definitely not on the ground anymore
            //this should've been cleared by a previous sit request, but setting here is safe
            m_sittingGround = false;

            HandleAgentSit(remoteClient, UUID);
            ControllingClient.SendSitResponse(vParentID, vPos, vRot, false, cameraAtOffset, cameraEyeOffset, forceMouselook);
            SceneView.SendFullUpdateToAllClients();
            part.ParentGroup.ScheduleGroupForFullUpdate(PrimUpdateFlags.ForcedFullUpdate);//Tell all avatars about this object, as otherwise avatars will show up at <0,0,0> on the radar if they have not seen this object before (culling)
        }

        public void HandleAgentRequestSit(IClientAPI remoteClient, UUID agentID, UUID targetID, Vector3 offset)
        {
            if (IsChildAgent)
            {
                remoteClient.SendAgentAlertMessage("Cannot sit on an object in a different region.", false);
                return;
            }

            StandUp(false, true);

            //SceneObjectPart part = m_scene.GetSceneObjectPart(targetID);
            SceneObjectPart part = FindNextAvailableSitTarget(targetID);
            if (part == null)
            {
                m_log.Warn("[SCENE PRESENCE]: Sit requested on unknown object: " + targetID.ToString());
                return;
            }

            if (part.RegionHandle != remoteClient.Scene.RegionInfo.RegionHandle)
            {
                //m_log.InfoFormat("[SCENE PRESENCE]: Viewer requested a sit to the wrong region server: {0} {1}", remoteClient.Name, remoteClient.AgentId);
                remoteClient.SendAgentAlertMessage("Cannot sit on an object in a different region.", false);
                return;
            }

            m_nextSitAnimation = "SIT";

            if (!String.IsNullOrEmpty(part.SitAnimation))
            {
                m_nextSitAnimation = part.SitAnimation;
            }
            m_requestedSitTargetID = part.LocalId;
            m_requestedSitTargetUUID = part.UUID;
            //m_requestedSitOffset = offset;

            SendSitResponse(remoteClient, targetID, offset);
        }
        
        public void HandleAgentRequestSit(IClientAPI remoteClient, UUID agentID, UUID targetID, Vector3 offset, string sitAnimation)
        {
//            m_log.InfoFormat("[SCENE PRESENCE]: HandleAgentRequestSit agent {0} at {1} requesing sit at {2} ", agentID.ToString(), m_pos.ToString(), offset.ToString());
//            m_movementflag = 0;

            StandUp(false, true);

            if (!String.IsNullOrEmpty(sitAnimation))
            {
                m_nextSitAnimation = sitAnimation;
            }
            else
            {
                m_nextSitAnimation = "SIT";
            }

            //SceneObjectPart part = m_scene.GetSceneObjectPart(targetID);
            SceneObjectPart part =  FindNextAvailableSitTarget(targetID);
            if (part != null)
            {
                m_requestedSitTargetID = part.LocalId;
                m_requestedSitTargetUUID = part.UUID;
                //m_requestedSitOffset = offset;
            }
            else
            {
                m_log.Warn("[SCENE PRESENCE]: Sit requested on unknown object: " + targetID);
            }
            
            SendSitResponse(remoteClient, targetID, offset);
        }

        public void HandleAgentSit(IClientAPI remoteClient, UUID agentID)
        {
            if (!String.IsNullOrEmpty(m_nextSitAnimation))
            {
                HandleAgentSit(remoteClient, agentID, m_nextSitAnimation);
            }
            else
            {
                HandleAgentSit(remoteClient, agentID, "SIT");
            }
        }

        public void HandleAgentSit(IClientAPI remoteClient, UUID agentID, string sitAnimation)
        {
            SceneObjectPart part = m_scene.GetSceneObjectPart(m_requestedSitTargetID);

            if (m_sitAtAutoTarget || !m_autopilotMoving)
            {
                if (part == null)
                {
                    m_log.Warn("[SCENE PRESENCE]: Sit requested on unknown object: " + m_requestedSitTargetID);
                    return;
                }
                // First, remove the PhysicsActor so it doesn't mess with anything that happens below
                RemoveFromPhysicalScene();
                Velocity = Vector3.Zero;
                m_animPersistUntil = 0;    // abort any timed animation
                TrySetMovementAnimation(sitAnimation);
            }
        }

        /// <summary>
        /// Event handler for the 'Always run' setting on the client
        /// Tells the physics plugin to increase speed of movement.
        /// </summary>
        public void HandleSetAlwaysRun(IClientAPI remoteClient, bool pSetAlwaysRun)
        {
            m_setAlwaysRun = pSetAlwaysRun;
            var pa = PhysicsActor;
            if (pa != null)
            {
                pa.SetAlwaysRun = pSetAlwaysRun;
            }
        }
        public BinBVHAnimation GenerateRandomAnimation()
        {
            int rnditerations = 3;
            BinBVHAnimation anim = new BinBVHAnimation();
            List<string> parts = new List<string>();
            parts.Add("mPelvis");parts.Add("mHead");parts.Add("mTorso");
            parts.Add("mHipLeft");parts.Add("mHipRight");parts.Add("mHipLeft");parts.Add("mKneeLeft");
            parts.Add("mKneeRight");parts.Add("mCollarLeft");parts.Add("mCollarRight");parts.Add("mNeck");
            parts.Add("mElbowLeft");parts.Add("mElbowRight");parts.Add("mWristLeft");parts.Add("mWristRight");
            parts.Add("mShoulderLeft");parts.Add("mShoulderRight");parts.Add("mAnkleLeft");parts.Add("mAnkleRight");
            parts.Add("mEyeRight");parts.Add("mChest");parts.Add("mToeLeft");parts.Add("mToeRight");
            parts.Add("mFootLeft");parts.Add("mFootRight");parts.Add("mEyeLeft");
            anim.HandPose = 1;
            anim.InPoint = 0;
            anim.OutPoint = (rnditerations * .10f);
            anim.Priority = 7;
            anim.Loop = false;
            anim.Length = (rnditerations * .10f);
            anim.ExpressionName = "afraid";
            anim.EaseInTime = 0;
            anim.EaseOutTime = 0;

            string[] strjoints = parts.ToArray();
            anim.Joints = new binBVHJoint[strjoints.Length];
            for (int j = 0; j < strjoints.Length; j++)
            {
                anim.Joints[j] = new binBVHJoint();
                anim.Joints[j].Name = strjoints[j];
                anim.Joints[j].Priority = 7;
                anim.Joints[j].positionkeys = new binBVHJointKey[rnditerations];
                anim.Joints[j].rotationkeys = new binBVHJointKey[rnditerations];
                Random rnd = new Random();
                for (int i = 0; i < rnditerations; i++)
                {
                    anim.Joints[j].rotationkeys[i] = new binBVHJointKey();
                    anim.Joints[j].rotationkeys[i].time = (i*.10f);
                    anim.Joints[j].rotationkeys[i].key_element.X = ((float) rnd.NextDouble()*2 - 1);
                    anim.Joints[j].rotationkeys[i].key_element.Y = ((float) rnd.NextDouble()*2 - 1);
                    anim.Joints[j].rotationkeys[i].key_element.Z = ((float) rnd.NextDouble()*2 - 1);
                    anim.Joints[j].positionkeys[i] = new binBVHJointKey();
                    anim.Joints[j].positionkeys[i].time = (i*.10f);
                    anim.Joints[j].positionkeys[i].key_element.X = 0;
                    anim.Joints[j].positionkeys[i].key_element.Y = 0;
                    anim.Joints[j].positionkeys[i].key_element.Z = 0;
                }
            }


            AssetBase Animasset = new AssetBase();
            Animasset.Data = anim.ToBytes();
            Animasset.Temporary = true;
            Animasset.Local = true;
            Animasset.FullID = UUID.Random();
            Animasset.ID = Animasset.FullID.ToString();
            Animasset.Name = "Random Animation";
            Animasset.Type = (sbyte)AssetType.Animation;
            Animasset.Description = "dance";
            //BinBVHAnimation bbvhanim = new BinBVHAnimation(Animasset.Data);


            m_scene.CommsManager.AssetCache.AddAsset(Animasset, AssetRequestInfo.InternalRequest());
            AddAnimation(Animasset.FullID, UUID);
            return anim;
        }

        // Returns String.Empty on success, otherwise an error to shout on DEBUG_CHANNEL if scripted.
        public string AddAnimation(UUID animID, UUID objectID)
        {
            if (m_isChildAgent)
                return "Could not start animation from a different region";

            if (m_animations.Add(animID, m_controllingClient.NextAnimationSequenceNumber, objectID))
                SendAnimPack();
            return String.Empty;
        }

        // Called from scripts
        // Returns String.Empty on success, otherwise an error to shout on DEBUG_CHANNEL if scripted.
        public string AddAnimation(string name, UUID objectID)
        {
            if (m_isChildAgent)
                return "Could not start animation '" + name + "' from a different region";

            UUID animID = m_controllingClient.GetDefaultAnimation(name);
            if (animID == UUID.Zero)    // this is the important return case
                return "Could not find animation '"+name+"'";

            return AddAnimation(animID, objectID);
        }

        public void RemoveAnimation(UUID animID)
        {
            if (m_isChildAgent)
                return;

            if (m_animations.Remove(animID))
                SendAnimPack();
        }

        // Called from scripts
        public void RemoveAnimation(string name)
        {
            if (m_isChildAgent)
                return;

            UUID animID = m_controllingClient.GetDefaultAnimation(name);
            if (animID == UUID.Zero)
                return;

            RemoveAnimation(animID);
        }

        public UUID[] GetAnimationArray()
        {
            UUID[] animIDs;
            int[] sequenceNums;
            UUID[] objectIDs;
            m_animations.GetArrays( out animIDs, out sequenceNums, out objectIDs);
            return animIDs;
        }

        public void HandleStartAnim(IClientAPI remoteClient, UUID animID)
        {
            AddAnimation(animID, UUID.Zero);
        }

        public void HandleStopAnim(IClientAPI remoteClient, UUID animID)
        {
            RemoveAnimation(animID);
        }

        /// <summary>
        /// The movement animation is reserved for "main" animations
        /// that are mutually exclusive, e.g. flying and sitting.
        /// </summary>
        protected void TrySetMovementAnimation(string anim)
        {
            //m_log.DebugFormat("[SCENE PRESENCE]: Updating movement animation to {0}", anim);
            
            if (!m_isChildAgent)
            {
                // disregard duplicate updates
                lock (m_previousMovement)    // only one place (here) references m_previousMovement
                {
                    if (anim == m_previousMovement)
                        return;
//                    m_log.DebugFormat(">>>> Thread {0} [{1}] changing {2} --> {3}", Thread.CurrentThread.Name, Thread.CurrentThread.ManagedThreadId.ToString(), m_previousMovement, anim);
                    m_previousMovement = anim;
                    if (anim == "DEFAULT")
                        anim = (m_posInfo.Parent == null) ? "STAND" : "SIT";
                    m_movementAnimation = anim;
                }

                m_animations.TrySetDefaultAnimation(anim, m_controllingClient.NextAnimationSequenceNumber, UUID.Zero);
                // other code can change the default anims, so don't check for changes before notifying the viewers
                // for example, if anyone has called ResetDefaultAnimation() to stop an anim, when we call it above, it will return false.
                if ((m_scriptEngines != null) && (!IsInTransit))
                {
                    lock (m_attachments)
                    {
                        foreach (SceneObjectGroup grp in m_attachments)
                        {
                            // Send CHANGED_ANIMATION to all attachment root prims
                            foreach (IScriptModule m in m_scriptEngines)
                            {
                                if (m == null) // No script engine loaded
                                    continue;
//                                m_log.DebugFormat(">>>> Thread {0} [{1}] sending changed({2})", Thread.CurrentThread.Name, Thread.CurrentThread.ManagedThreadId.ToString(), anim);
                                m.PostObjectEvent(grp.RootPart.LocalId, "changed", new Object[] { (int)Changed.ANIMATION }); // CHANGED_ANIMATION
                            }
                        }
                    }
                }
                SendAnimPack();
            }
        }

        /// <summary>
        /// This method determines the proper movement related animation
        /// </summary>
        public string GetMovementAnimation()
        {
            if ((m_animPersistUntil > 0) && (m_animPersistUntil > DateTime.Now.Ticks))
            {
                //We don't want our existing state to end yet.
                return m_movementAnimation;

            }
            else if ((m_posInfo.Parent != null) || IsInTransitOnPrim || m_sittingGround)
            {
                //We are sitting on something, so we don't want our existing state to change
                if (m_movementAnimation == "DEFAULT")
                    return "SIT";
                return m_movementAnimation;
            }
            else if (m_movementflag != 0)
            {
                //We're moving
                m_allowFalling = true;
                PhysicsActor pa = PhysicsActor;
                if (pa != null && pa.IsColliding)
                {
                    //And colliding. Can you guess what it is yet?
                    if ((m_movementflag & (uint)AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG) != 0)
                    {
                        //Down key is being pressed.
                        if (pa.Flying)
                        {
                            return "LAND";
                        }
                        else
                            if ((m_movementflag & (uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG) + (m_movementflag & (uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_POS) != 0)
                            {
                                return "CROUCHWALK";
                            }
                            else
                            {
                                return "CROUCH";
                            }
                    }
                    else if (m_movementAnimation == "PREJUMP")
                    {
                        // m_log.DebugFormat("[SCENE PRESENCE]: GetMovementAnimation: PREJUMP");
                        return "PREJUMP";
                    }
                    else
                        if (pa.Flying)
                        {
                            // if (m_movementAnimation != "FLY") m_log.DebugFormat("[SCENE PRESENCE]: GetMovementAnimation: {0} --> FLY", m_movementAnimation);
                            return "FLY";
                        }
                        else
                            if ((m_movementflag & (uint)AgentManager.ControlFlags.AGENT_CONTROL_UP_POS) != 0)
                            {
                                // if (m_movementAnimation != "JUMP") m_log.DebugFormat("[SCENE PRESENCE]: GetMovementAnimation: {0} --> JUMP", m_movementAnimation);
                                return "JUMP";
                            }
                            else if (m_setAlwaysRun)
                            {
                                return "RUN";
                            }
                            else
                            {
                                // if (m_movementAnimation != "WALK") m_log.DebugFormat("[SCENE PRESENCE]: GetMovementAnimation: {0} --> WALK", m_movementAnimation);
                                return "WALK";
                            }
                }
                else
                {
                    //We're not colliding. Colliding isn't cool these days.
                    if (pa != null && pa.Flying)
                    {
                        //Are we moving forwards or backwards?
                        if ((m_movementflag & (uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_POS) != 0 || (m_movementflag & (uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG) != 0)
                        {
                            //Then we really are flying
                            if (m_setAlwaysRun)
                            {
                                return "FLY";
                            }
                            else
                            {
                                if (m_useFlySlow == false)
                                {
                                    return "FLY";
                                }
                                else
                                {
                                    return "FLYSLOW";
                                }
                            }
                        }
                        else
                        {
                            if ((m_movementflag & (uint)AgentManager.ControlFlags.AGENT_CONTROL_UP_POS) != 0)
                            {
                                return "HOVER_UP";
                            }
                            else
                            {
                                return "HOVER_DOWN";
                            }
                        }

                    }
                    else if (m_movementAnimation == "JUMP")
                    {
                        //If we were already jumping, continue to jump until we collide
                        return "JUMP";
                    }
                    else if (m_movementAnimation == "PREJUMP" && (m_movementflag & (uint)AgentManager.ControlFlags.AGENT_CONTROL_UP_POS) == 0)
                    {
                        //If we were in a prejump, and the UP key is no longer being held down
                        //then we're not going to fly, so we're jumping
                        return "JUMP";

                    }
                    else if ((m_movementflag & (uint)AgentManager.ControlFlags.AGENT_CONTROL_UP_POS) != 0)
                    {
                        //They're pressing up, so we're either going to fly or jump
                        return "PREJUMP";
                    }
                    else
                    {
                        //If we're moving and not flying and not jumping and not colliding..

                        if (m_movementAnimation == "WALK" || m_movementAnimation == "RUN")
                        {
                            //Let's not enter a FALLDOWN state here, since we're probably
                            //not colliding because we're going down hill.
                            return m_movementAnimation;
                        }

                        //Record the time we enter this state so we know whether to "land" or not
                        if (m_movementAnimation != "FALLDOWN")
                            m_animPersistUntil = DateTime.Now.Ticks;
                        return "FALLDOWN";

                    }
                }
            }
            else
            {
                //We're not moving.
                PhysicsActor pa = PhysicsActor;
                if (pa != null && pa.IsColliding)
                {
                    //But we are colliding.
                    if (m_movementAnimation == "FALLDOWN")
                    {
                        //We're re-using the m_animPersistUntil value here to see how long we've been falling
                        if ((DateTime.Now.Ticks - m_animPersistUntil) > TimeSpan.TicksPerSecond)
                        {
                            //Make sure we don't change state for a bit
                            if (m_movementAnimation != "LAND")
                                m_animPersistUntil = DateTime.Now.Ticks + TimeSpan.TicksPerSecond;
                            return "LAND";
                        }
                        else
                        {
                            //We haven't been falling very long, we were probably just walking down hill
                            return "STAND";
                        }
                    }
                    else if ((m_movementflag & (uint)AgentManager.ControlFlags.AGENT_CONTROL_UP_POS) != 0)
                    {
                        return "PREJUMP";
                    }
                    else if (m_movementAnimation == "JUMP" || m_movementAnimation == "HOVER_DOWN")
                    {
                        //Make sure we don't change state for a bit
                        if (m_movementAnimation != "SOFT_LAND")
                            m_animPersistUntil = DateTime.Now.Ticks + (1 * TimeSpan.TicksPerSecond);
                        return "SOFT_LAND";

                    }
                    else if (pa != null && pa.Flying)
                    {
                        m_allowFalling = true;
                        if ((m_movementflag & (uint)AgentManager.ControlFlags.AGENT_CONTROL_UP_POS) != 0)
                        {
                            return "HOVER_UP";
                        }
                        else if ((m_movementflag & (uint)AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG) != 0)
                        {
                            return "HOVER_DOWN";
                        }
                        else
                        {
                            return "HOVER";
                        }
                    }
                    else
                    {
                        return "STAND";
                    }

                }
                else
                {
                    //We're not colliding.
                    if (pa != null && pa.Flying)
                    {

                        return "HOVER";

                    }
                    else if ((m_movementAnimation == "JUMP" || m_movementAnimation == "PREJUMP") && (m_movementflag & (uint)AgentManager.ControlFlags.AGENT_CONTROL_UP_POS) == 0)
                    {

                        return "JUMP";

                    }
                    else if ((m_movementAnimation == "STAND") || (m_movementAnimation == "LAND"))
                    {
                        // Sometimes PhysicsActor.IsColliding returns false when standing on the ground.
                        // Try to recognize that by not falling from STAND until you move.
                        return m_movementAnimation;
                    }
                    else
                    {
                        //Record the time we enter this state so we know whether to "land" or not
                        if (m_movementAnimation != "FALLDOWN")
                            m_animPersistUntil = DateTime.Now.Ticks;
                        return "FALLDOWN";
                    }
                }
            }
        }

        /// <summary>
        /// Update the movement animation of this avatar according to its current state
        /// </summary>
        public void UpdateMovementAnimations()
        {
            if (IsInTransit)
                return;

            string movementAnimation = GetMovementAnimation();
            // if we ignore this calculated movementAnimation, we need to also clear m_animPersistUntil
        
            if (movementAnimation == "FALLDOWN" && m_allowFalling == false)
            {    // don't update m_movementAnimation
                movementAnimation = m_movementAnimation;    // save *current* anim
                m_animPersistUntil = 0;    // overriding movementAnimation, so abort any calculated timed animation
            }

            if (movementAnimation == "PREJUMP" && m_usePreJump == false)
            {
                //This was the previous behavior before PREJUMP
                m_animPersistUntil = 0;    // overriding movementAnimation, so abort any calculated timed animation
                movementAnimation = "JUMP";
            }

            // now set it to whatever that all worked out to
            TrySetMovementAnimation(movementAnimation);
        }

        /// <summary>
        /// Rotate the avatar to the given rotation and apply a movement in the given relative vector
        /// </summary>
        /// <param name="vec">The vector in which to move.  This is relative to the rotation argument</param>
        /// <param name="rotation">The direction in which this avatar should now face.</param>        
        public void AddNewMovement(Vector3 vec, Quaternion rotation)
        {
            if (m_isChildAgent)
            {
                m_log.Debug("[SCENE PRESENCE]: AddNewMovement: child agent");
                return;
            }

            m_perfMonMS = Environment.TickCount;

            m_rotation = rotation;
            Vector3 direc = vec * rotation;
            direc.Normalize();

            direc *= 0.03f * 128f * m_speedModifier;
            PhysicsActor pa = PhysicsActor;
            if (pa != null)
            {
                if (pa.Flying)
                {
                    direc *= 4;
                }
                else
                {
                    if (!pa.Flying && pa.IsColliding || m_shouldJump)
                    {
                        if (direc.Z > 2.0f || m_shouldJump)
                        {
                            if (m_shouldJump)
                                direc.Z = Math.Max(direc.Z, 2.8f);
                            m_shouldJump = false;
                            direc.Z *= 3;
                            m_animPersistUntil = 0;    // abort any timed animation
                            if (m_movementAnimation != "JUMP")
                                TrySetMovementAnimation("PREJUMP");
                            TrySetMovementAnimation("JUMP");
                        }
                    }
                }
            }

            lock (m_forcesList)
            {
                m_forcesList.Add(direc);
            }

            m_scene.StatsReporter.AddAgentTime(Environment.TickCount - m_perfMonMS);
        }

#endregion

#region Overridden Methods

        public override void Update()
        {
            if (m_sceneView != null)
                m_sceneView.SendPrimUpdates();

            if ((!IsChildAgent) && !(IsInTransit))
            {
                if (m_movementflag != 0) // scripted movement (?)
                {
                    lock (m_posInfo)
                    {
                        if (m_posInfo.Parent != null)   // seated?
                        {   // this is the last update unless something else changes
                            m_movementflag = 0;
                        }
                    }
                }

                // followed suggestion from mic bowman. reversed the two lines below.
                CheckForBorderCrossing();
                CheckForSignificantMovement(); // sends update to the modules.
            }
        }

#endregion

#region Update Client(s)

        /// <summary>
        /// Sends a location update to the client connected to this scenePresence
        /// </summary>
        /// <param name="remoteClient"></param>
        public void SendTerseUpdateToClient(ScenePresence presence)
        {
            // If the client is inactive, it's getting its updates from another
            // server.
            if (!presence.ControllingClient.IsActive)
                return;

            presence.SceneView.SendAvatarTerseUpdate(this);
        }

        public void RecalcVisualPosition(out Vector3 vPos, out Quaternion vRot, out uint vParentID)
        {
            // Grab the current avatar values as the default starting points
            SceneObjectPart parent;
            Vector3 pos;
            Quaternion rot;
            lock (m_posInfo)
            {   // collect the position info inside the lock so it's consistent
                parent = m_posInfo.Parent;
                pos = m_posInfo.Position;
                rot = m_bodyRot;
            }

            // Viewer-ready variables to send
            vParentID = (parent == null) ? 0 : parent.LocalId;  // parent localID to send to viewer
            vPos = pos;     // viewer position of avatar relative to root prim
            vRot = rot;     // viewer rotation of avatar relative to root prim

            SceneObjectPart part = parent;
            SceneObjectPart rootPart = (part == null) ? null : part.ParentGroup.RootPart;

            if (!this.AvatarMovesWithPart)
            {   // avatar-as-a-prim mode
                return;
            }

            // Viewer seems to draw the avatar based on the hip position.
            // If you don't include HipOffset (which is raising the avatar 
            // since it's normally negative), then the viewer will draw 
            // the avatar walking with toes underground/inside prim.
            // Full updates were missing this, so a rebake would reproduce it.
            // This adjustment gives the viewer the position it expects.
            vPos.Z -= m_appearance.HipOffset;

            // Actual rotation is rootPart.RotationOffset * part.RotationOffset * part.SitTargetOrientation
            // but we need to specify it relative to the root prim.

            if (rootPart != null)
            {
                // Avatar is seated on a prim. Send viewer root-relative info.
                vParentID = rootPart.LocalId;   // Viewer parent ID always the root prim.
                SitTargetInfo sitInfo = part.ParentGroup.SitTargetForPart(part.UUID);
                if (sitInfo.Offset != Vector3.Zero)
                {
                    vPos = sitInfo.Offset;  // start with the sit target position
                    vPos.Z -= m_appearance.HipOffset;   // reapply correction
                    if (ADJUST_SIT_TARGET)
                    {
                        // If we want to support previous IW sit target offsets, rather than SL-accurate sit targets,
                        // we need to apply the OpenSim sit target correction adjustment.
                        vPos += m_sitTargetCorrectionOffset;
                    }

                    if (part != rootPart)      // sitting on a child prim
                    {
                        // if the part is rotated compared to the root prim, adjust relative pos/rot
                        vPos *= part.RotationOffset;
                        vPos += part.OffsetPosition;
                        vRot = part.RotationOffset * sitInfo.Rotation;
                    }
                    else
                        vRot = sitInfo.Rotation;
                }
                else
                {
                    // no sit target
                    vPos = VIEWER_DEFAULT_OFFSET;
                    vPos.Z -= m_appearance.HipOffset;
                    // make up a default rotation -- aligned with the prim for now
                    Quaternion defaultRot = Quaternion.Identity;

                    if (part != rootPart)      // sitting on a child prim
                    {
                        // adjust to be relative to root prim
                        vPos *= part.RotationOffset;
                        vPos += part.OffsetPosition;
                        vRot = part.RotationOffset * defaultRot;
                    }
                    else
                        vRot = defaultRot;
                }
            }
        }

        /// <summary>
        /// Send a location/velocity/accelleration update to all agents in scene
        /// </summary>
        public void SendTerseUpdateToAllClients()
        {
            m_perfMonMS = Environment.TickCount;

            m_scene.Broadcast(SendTerseUpdateToClient);

            m_scene.StatsReporter.AddAgentTime(Environment.TickCount - m_perfMonMS);
        }

        public void SendCoarseLocations(List<Vector3> CoarseLocations, List<UUID> AvatarUUIDs)
        {
            m_perfMonMS = Environment.TickCount;

            DoSendCoarseLocationUpdates(CoarseLocations, AvatarUUIDs);

            m_scene.StatsReporter.AddAgentTime(Environment.TickCount - m_perfMonMS);
        }

        public static void CollectCoarseLocations(Scene scene, out List<Vector3> CoarseLocations, out List<UUID> AvatarUUIDs)
        {
            CoarseLocations = new List<Vector3>();
            AvatarUUIDs = new List<UUID>();
            lock (scene.SyncRoot)
            {
                List<ScenePresence> avatars = scene.GetAvatars();
                foreach (ScenePresence avatar in avatars)
                {
                    lock (avatar.m_posInfo)
                    {
                        if (avatar.IsInTransit || avatar.IsDeleted)
                            continue;
                        SceneObjectPart sop = avatar.m_posInfo.Parent;
                        if (sop != null)    // is seated?
                            if (sop.ParentGroup.InTransit)    // and in transit
                                continue;        // skip this one since we don't have a reliable position

                        CoarseLocations.Add(avatar.AbsolutePosition);
                    }
                    AvatarUUIDs.Add(avatar.UUID);
                }
            }
        }

        private void DoSendCoarseLocationUpdates(List<Vector3> CoarseLocations, List<UUID> AvatarUUIDs)
        {
            m_controllingClient.SendCoarseLocationUpdate(AvatarUUIDs, CoarseLocations);
        }

        public void SendAvatarData(IClientAPI client, bool immediate)
        {
            ulong regionHandle = this.RegionHandle;
            string firstName = this.Firstname;
            string lastName = this.Lastname;
            string grouptitle = this.Grouptitle;
            UUID avatarID = this.UUID;
            uint avatarLocalID = this.LocalId;
            byte[] textureEntry = m_appearance.Texture.GetBytes();
            PhysicsActor physActor = this.PhysicsActor;
            Vector4 collisionPlane = physActor != null ? physActor.CollisionPlane : Vector4.UnitW;

            Vector3 vPos;
            Quaternion vRot;
            uint vParentID;
            RecalcVisualPosition(out vPos, out vRot, out vParentID);

            //m_log.WarnFormat("[SP]: Sending full avatar update for {0} to {1}. Face[0]: {2}, Pos: {3}, Parent: {4}", this.Name, client.Name,
            //    m_appearance.Texture.FaceTextures[0] != null ? m_appearance.Texture.FaceTextures[0].TextureID.ToString() : "null",
            //    vPos, vParentID);

            client.SendAvatarData(regionHandle, firstName, lastName, grouptitle, avatarID,
                            avatarLocalID, vPos, textureEntry, vParentID, vRot, collisionPlane,
                            Velocity, immediate);
            m_scene.StatsReporter.AddAgentUpdates(1);
        }

        /// <summary>
        /// Tell the client for this scene presence what items it should be wearing now
        /// </summary>
        public void SendWearables()
        {   
            ControllingClient.SendWearables(m_appearance.GetWearables().ToArray(), m_appearance.Serial);
        }

        /// <summary>
        ///
        /// </summary>
        public void SendAppearanceToAllOtherAgents()
        {
            m_perfMonMS = Environment.TickCount;

            m_scene.ForEachScenePresence(delegate(ScenePresence scenePresence)
                                         {
                                             if (scenePresence.UUID != UUID)
                                             {
                                                 SendAppearanceToOtherAgent(scenePresence);
                                             }
                                         });
            
            m_scene.StatsReporter.AddAgentTime(Environment.TickCount - m_perfMonMS);
        }

        /// <summary>
        /// Send MY appearance data to ANOTHER different avatar (that isn't this one).
        /// </summary>
        /// <param name="otherAvatar"></param>
        public void SendAppearanceToOtherAgent(ScenePresence otherAvatar)
        {
            //m_log.WarnFormat("[SP]: Sending avatar appearance for {0} to {1}. Face[0]: {2}, Owner: {3}", this.Name, avatar.Name,
            //    m_appearance.Texture.FaceTextures[0] != null ? m_appearance.Texture.FaceTextures[0].TextureID.ToString() : "null", m_appearance.Owner);

            float hover = (this.AgentPrefs != null) ? (float)this.AgentPrefs.HoverHeight : 0.0f;
            otherAvatar.ControllingClient.SendAppearance(m_appearance, new Vector3(0.0f, 0.0f, (float)hover));
        }

        private void InitialAttachmentRez()
        {
            //retrieve all attachments
            List<AvatarAttachment> attachments = m_appearance.GetAttachments();
            m_appearance.ClearAttachments();
            bool updated = false;

            m_log.DebugFormat("[SCENE PRESENCE]: InitialAttachmentRez for {0} attachments", attachments.Count);

            foreach (AvatarAttachment attachment in attachments)
            {
                if (attachment.ItemID == UUID.Zero)
                    continue;

                // intial rez always appends
                SceneObjectGroup sog =
                    m_scene.RezSingleAttachmentSync(ControllingClient, attachment.ItemID, (uint)attachment.AttachPoint, true);
                if (sog != null)
                    updated = true;
            }

            if (updated)
            {
                IAvatarFactory ava = m_scene.RequestModuleInterface<IAvatarFactory>();
                if ((ava != null) && ((ControllingClient != null) && ControllingClient.IsActive))
                    ava.UpdateDatabase(ControllingClient.AgentId, Appearance, null, null);
            }
        }

        /// <summary>
        /// Set appearance data (textureentry and slider settings) received from the client
        /// </summary>
        /// <param name="texture"></param>
        /// <param name="visualParam"></param>
        public void SetAppearance(byte[] texture, List<byte> visualParam, WearableCache[] cachedItems, uint serial)
        {
            Primitive.TextureEntry textureEnt = new Primitive.TextureEntry(texture, 0, texture.Length);
            m_appearance.SetAppearance(textureEnt, visualParam.ToArray());
            if (m_appearance.AvatarHeight > 0)
                SetHeight(m_appearance.AvatarHeight);

            Dictionary<UUID, UUID> bakedTextures = new Dictionary<UUID,UUID>();
            foreach (WearableCache cache in cachedItems)
            {
                //V2 changed to send the actual texture index, and not the baked texture index
                int index = cache.TextureIndex >= 5 ? cache.TextureIndex :
                    (int)AppearanceManager.BakeTypeToAgentTextureIndex((BakeType)cache.TextureIndex);

                if(m_appearance.Texture != null && m_appearance.Texture.FaceTextures[index] != null)
                    bakedTextures.Add(cache.CacheID, m_appearance.Texture.FaceTextures[index].TextureID);
            }

            // Cof version number.
            m_appearance.Serial = (int)serial;

            if (!this.IsInTransit)
            {
                // Don't update the database with changes while a teleport/crossing is in progress.
                IAvatarFactory ava = m_scene.RequestModuleInterface<IAvatarFactory>();
                if (ava != null)
                {
                    ava.UpdateDatabase(m_uuid, m_appearance, SendAppearanceToAllOtherAgents, bakedTextures);
                }
            }

            if (!m_startAnimationSet)
            {
                UpdateMovementAnimations();
                m_startAnimationSet = true;
            }

            //
            // Handle initial attachment rez.  We need to do this for V1. V2 wants to manage its own
            // but we dont really have a good way to tell its a V2 client.
            //
            if (Interlocked.CompareExchange(ref _attachmentRezCalled, 1, 0) == 0)
            {
                //retrieve all attachments
                CachedUserInfo userInfo = m_scene.CommsManager.UserService.GetUserDetails(m_uuid);
                if (userInfo == null)
                    return;
                // If this is after a login in this region and not done yet, add the initial attachments
                if (ScenePresence.CheckNeedsInitialAttachmentRezAndReset(m_uuid))
                {
                    if (!IsChildAgent && (HasAttachments() == false))
                    {
                        ControllingClient.RunAttachmentOperation(() =>
                        {
                            this.InitialAttachmentRez();
                        });
                    }
                }
            }

            if (!IsBot)
                SendAvatarData(m_controllingClient, false);
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="animations"></param>
        /// <param name="seqs"></param>
        /// <param name="objectIDs"></param>
        public void SendAnimPack(UUID[] animations, int[] seqs, UUID[] objectIDs)
        {
            if (m_isChildAgent)
                return;
            // this should be sent when the initial data is also sent, which matches when culling is ready to send (IsFullyInRegion).
            if (!this.IsFullyInRegion)
            {
//                m_log.WarnFormat("[SCENE PRESENCE]: NOT sending anim pack to {0}: avatar not yet in region.", this.Name);
                return;
            }

//            m_log.WarnFormat("[SCENE PRESENCE]: Sending anim pack to {0}.", this.Name);
            m_scene.Broadcast(
                delegate(IClientAPI client) { client.SendAnimations(animations, seqs, m_controllingClient.AgentId, objectIDs); });
        }

        public void SendAnimPackToClient(IClientAPI client)
        {
            if (m_isChildAgent)
                return;
            UUID[] animIDs;
            int[] sequenceNums;
            UUID[] objectIDs;

            m_animations.GetArrays(out animIDs, out sequenceNums, out objectIDs);

            client.SendAnimations(animIDs, sequenceNums, m_controllingClient.AgentId, objectIDs);
        }

        

        /// <summary>
        /// Send animation information about this avatar to all clients.
        /// </summary>
        public void SendAnimPack()
        {
            //m_log.Debug("[SCENE PRESENCE]: Sending animation pack to all");
            
            if (m_isChildAgent)
                return;

            UUID[] animIDs;
            int[] sequenceNums;
            UUID[] objectIDs;

            m_animations.GetArrays(out animIDs, out sequenceNums, out objectIDs);

            SendAnimPack(animIDs, sequenceNums, objectIDs);
        }


#endregion

#region Significant Movement Method

        /// <summary>
        /// This checks for a significant movement and sends a courselocationchange update
        /// </summary>
        protected void CheckForSignificantMovement()
        {
            float childAgentUpdateDistance = 32;
            Vector3 pos = _GetPosition(true, true); // check for parcel changes and updates from physics

            if (Util.GetDistanceTo(pos, posLastSignificantMove) > 0.5)
            {
                posLastSignificantMove = pos;
                m_scene.EventManager.TriggerSignificantClientMovement(m_controllingClient);
            }

            if (m_sceneView != null && m_sceneView.UseCulling)
            {
                //Check to see if the agent has moved enough to warrent another culling check
                if ((!IsBot) && (Util.GetDistanceTo(pos, posLastCullCheck) > m_sceneView.DistanceBeforeCullingRequired))
                {
                    posLastCullCheck = pos;
                    m_sceneView.CheckForDistantEntitiesToShow();
                }
                childAgentUpdateDistance = m_sceneView.DistanceBeforeCullingRequired;
            }

            if (Util.GetDistanceTo(pos, m_LastChildAgentUpdatePosition) >= childAgentUpdateDistance)
                SendChildAgentUpdate();
        }

        public void SendChildAgentUpdate()
        {
            Vector3 pos = AbsolutePosition;
            m_LastChildAgentUpdatePosition.X = pos.X;
            m_LastChildAgentUpdatePosition.Y = pos.Y;
            m_LastChildAgentUpdatePosition.Z = pos.Z;

            ChildAgentDataUpdate cadu = new ChildAgentDataUpdate();
            cadu.ActiveGroupID = UUID.Zero.Guid;
            cadu.AgentID = UUID.Guid;
            cadu.alwaysrun = m_setAlwaysRun;
            cadu.AVHeight = m_avHeight;
            sLLVector3 tempCameraCenter = new sLLVector3(new Vector3(m_CameraCenter.X, m_CameraCenter.Y, m_CameraCenter.Z));
            cadu.cameraPosition = tempCameraCenter;
            cadu.drawdistance = m_DrawDistance;
            if (!this.IsBot)    // bots don't need IsGod checks
                if (m_scene.Permissions?.IsGod(new UUID(cadu.AgentID)) ?? false)
                    cadu.godlevel = m_godlevel;
            cadu.GroupAccess = 0;
            cadu.Position = new sLLVector3(pos);
            cadu.regionHandle = m_scene.RegionInfo.RegionHandle;

            float multiplier = CalculateNeighborBandwidthMultiplier();
            //m_log.Info("[NeighborThrottle]: " + m_scene.GetInaccurateNeighborCount().ToString() + " - m: " + multiplier.ToString());
            cadu.throttles = ControllingClient.GetThrottlesPacked(multiplier);
            cadu.Velocity = new sLLVector3(Velocity);

            AgentPosition agentpos = new AgentPosition();
            agentpos.CopyFrom(cadu);

            m_scene.SendOutChildAgentUpdates(agentpos, this);
        }

        // Neighbor regions bandwidth percentage (as float).
        private const float NEIGHBORS_BANDWIDTH_PERCENTAGE = 0.60f;
        private const float MINIMUM_BANDWIDTH_PERCENTAGE = 0.10f;
        private float CalculateNeighborBandwidthMultiplier()
        {
            int innacurateNeighbors = this.m_remotePresences.GetRemotePresenceCount();

            if (innacurateNeighbors != 0)
            {
                //only allow a percentage of our bandwidth to be used for neighbor regions
                float multiplier = NEIGHBORS_BANDWIDTH_PERCENTAGE / (float)innacurateNeighbors;
                return (multiplier < MINIMUM_BANDWIDTH_PERCENTAGE) ? MINIMUM_BANDWIDTH_PERCENTAGE : multiplier;
            }
            else
            {
                return NEIGHBORS_BANDWIDTH_PERCENTAGE;
            }
        }

#endregion
#region Border Crossing Methods

        /// <summary>
        /// Checks to see if the avatar is in range of a border and calls CrossToNewRegion
        /// </summary>
        protected void CheckForBorderCrossing()
        {
            int neighbor = 0;
            Vector3 pos;
            Vector3 pos2;
            float[] fix = new float[2];

            lock (m_posInfo)
            {
                if (IsChildAgent)
                    return;
                if (IsBot)
                    return;
                if (IsInTransit)
                    return;
                if (m_posInfo.Parent != null)
                    return;   // let the prim we're sitting on drag the presence across

                Vector3 vel = Velocity;
                pos = m_posInfo.Position;
                pos2 = pos;

                // This is an emergency position to restore if a crossing fails, so put it a bit safely inside
                m_LastRegionPosition = pos2;
                if (m_LastRegionPosition.X < 1.0f)
                    m_LastRegionPosition.X = 1.0f;
                if (m_LastRegionPosition.Y < 1.0f)
                    m_LastRegionPosition.Y = 1.0f;
                if (m_LastRegionPosition.X > 255.0f)
                    m_LastRegionPosition.X = 255.0f;
                if (m_LastRegionPosition.Y > 255.0f)
                    m_LastRegionPosition.Y = 255.0f;

                //add frames of interpolation
                float timeStep = 0.0156f * 2.0f; //TODO: This should be based on the phyiscs constant for min timestep
                //the remainder of dynamic interpolation will be done by the receiving side

                pos2.X = pos2.X + (vel.X * timeStep);
                pos2.Y = pos2.Y + (vel.Y * timeStep);
                pos2.Z = pos2.Z + (vel.Z * timeStep);
            }
            // Meaty call below so do this outside the lock above.

            ulong neighborHandle = 0;
            SimpleRegionInfo neighborInfo = null;

            // Checks if where it's headed exists a region
            if (pos2.X < 0)
            {
                if (pos2.Y < Constants.OUTSIDE_REGION_NEGATIVE_EDGE)
                    neighbor = HaveNeighbor(Cardinals.SW, ref fix, ref neighborHandle, ref neighborInfo);
                else if (pos2.Y >= Constants.OUTSIDE_REGION)
                    neighbor = HaveNeighbor(Cardinals.NW, ref fix, ref neighborHandle, ref neighborInfo);
                else
                    neighbor = HaveNeighbor(Cardinals.W, ref fix, ref neighborHandle, ref neighborInfo);
            }
            else if (pos2.X >= Constants.OUTSIDE_REGION)
            {
                if (pos2.Y < Constants.OUTSIDE_REGION_NEGATIVE_EDGE)
                    neighbor = HaveNeighbor(Cardinals.SE, ref fix, ref neighborHandle, ref neighborInfo);
                else if (pos2.Y >= Constants.OUTSIDE_REGION)
                    neighbor = HaveNeighbor(Cardinals.NE, ref fix, ref neighborHandle, ref neighborInfo);
                else
                    neighbor = HaveNeighbor(Cardinals.E, ref fix, ref neighborHandle, ref neighborInfo);
            }
            else if (pos2.Y < Constants.OUTSIDE_REGION_NEGATIVE_EDGE)
            {
                neighbor = HaveNeighbor(Cardinals.S, ref fix, ref neighborHandle, ref neighborInfo);
            }
            else if (pos2.Y >= Constants.OUTSIDE_REGION)
            {
                neighbor = HaveNeighbor(Cardinals.N, ref fix, ref neighborHandle, ref neighborInfo);
            }

            // Makes sure avatar does not end up outside region
            if (PhysicsActor != null)
            {
                if (neighbor < 0)
                {
                    AbsolutePosition = new Vector3(
                                                    pos.X + 3 * fix[0],
                                                    pos.Y + 3 * fix[1],
                                                    pos.Z);
                }
            }

            // Makes sure avatar does not end up outside region
            if (PhysicsActor != null)
            {
                if (neighbor > 0)
                {
                    pos2.X += fix[0];
                    pos2.Y += fix[1];

                    CrossToNewRegion(neighborHandle, neighborInfo, pos2);
                }
            }
        }

        protected int HaveNeighbor(Cardinals car, ref float[] fix, ref ulong neighborHandle, ref SimpleRegionInfo neighborInfo)
        {
            uint neighbourx = m_regionInfo.RegionLocX;
            uint neighboury = m_regionInfo.RegionLocY;

            float fixX = 0;
            float fixY = 0;

            int dir = (int)car;

            if (dir > 1 && dir < 5) //Heading East
            {
                neighbourx++;
                fixX -= (float)Constants.RegionSize;
            }
            else if (dir > 5) // Heading West
            {
                neighbourx--;
                fixX += (float)Constants.OUTSIDE_REGION_POSITIVE_EDGE;
            }

            if (dir < 3 || dir == 8) // Heading North
            {
                neighboury++;
                fixY -= (float)Constants.RegionSize;
            }
            else if (dir > 3 && dir < 7) // Heading Sout
            {
                neighboury--;
                fixY += (float)Constants.OUTSIDE_REGION_POSITIVE_EDGE;
            }

            neighborHandle = Util.RegionHandleFromLocation(neighbourx, neighboury);
            neighborInfo = m_scene.RequestNeighbouringRegionInfo(neighborHandle);

            if (neighborInfo == null)
            {

                fix[0] = (float)(int)(m_regionInfo.RegionLocX - neighbourx);
                fix[1] = (float)(int)(m_regionInfo.RegionLocY - neighboury);
                
                return dir * (-1);
            }
            else
            {
                fix[0] = fixX;
                fix[1] = fixY;
                return dir;
            }
        }

        /// <summary>
        /// Moves the agent outside the region bounds
        /// Tells neighbor region that we're crossing to it
        /// If the neighbor accepts, remove the agent's viewable avatar from this scene
        /// set them to a child agent.
        /// </summary>
        protected void CrossToNewRegion(ulong neighborHandle, SimpleRegionInfo neighborInfo, Vector3 positionInNewRegion)
        {
            ulong started = Util.GetLongTickCount();

            lock (m_posInfo)    // SetInTransit and AbsolutePosition will grab this
            {
                if (PhysicsActor == null)
                {
                    //when a user is crossing on a border due to being attached to a moving object
                    //they will have no physics actor. This is our signal to let the object
                    //do the crossing for us
                    return;
                }
            }

            m_scene.CrossWalkingOrFlyingAgentToNewRegion(this, neighborHandle, neighborInfo, positionInNewRegion);

            m_log.InfoFormat("[SCENE]: Crossing for avatar took {0} ms for {1}.", Util.GetLongTickCount()-started, this.Name);
        }

        public Task CrossIntoNewRegionWithGroup(SceneObjectGroup sceneObjectGroup, SceneObjectPart part, ulong newRegionHandle)
        {
            return m_scene.CrossSittingAgentToNewRegion(this, sceneObjectGroup, part, newRegionHandle);
        }

        // Figure out if this agent is supposed to have a physics actor or not
        public void RestoreInCurrentScene(bool isFlying)
        {
            bool add = false;
            lock (m_posInfo)
            {
                if ((!IsChildAgent) && (m_posInfo.Parent == null) && (!m_closed))
                {
                    m_posInfo.Position = m_LastRegionPosition;
                    add = true;
                }
            }
            if (add)
            {
                AddToPhysicalScene(isFlying);
                // m_log.WarnFormat("[SCENE PRESENCE]: RestoreInCurrentScene: Sending terse update vel={0} and pos change.", this.Velocity);
                SendTerseUpdateToAllClients();
            }
            else
            {
                RemoveFromPhysicalScene();
            }


            
        }

        public void Reset(SimpleRegionInfo destinationRegion)
        {
            m_scene.SendKillObject(m_localId, destinationRegion);
            ResetAnimations();
        }

        public void ResetAnimations()
        {
            bool bSitting = (m_posInfo.Parent != null);
            m_animations.Clear(bSitting);
        }

        public bool IsRegionVisibleFrom(uint regionX, uint regionY, uint fromX, uint fromY, Vector3 fromPos, float drawDistance)
        {
            if (!Util.IsOutsideView(fromX, regionX, fromY, regionY))
                return true;

            // Disabling the draw distance based visibility until the algorithm handles proper tiling path rules for region visibility.
            // e.g. Diagonal regions (i.e. checkerboard region layouts) are not supposed to be visible without a horizontal/vertical connection region.
#if false
            // region offset, e.g. (1002, 999) viewing (1000,1000) would be diffX=-2, diffY=1 (left 2, up 1)
            int diffX = (int)regionX - (int)fromX;
            int diffY = (int)regionY - (int)fromY;
            // distance to nearest edge of that region from current pos
            float distX = 0.0f; 
            float distY = 0.0f;

            if (diffX < 0)
                distX = -((Constants.RegionSize * (float)(diffX-1)) + (float)fromPos.X);
            else
            if (diffX > 0)
                distX = ((Constants.RegionSize * (float)diffX) - (float)fromPos.X);
            if (distX > drawDistance)
                return false;

            if (diffY < 0)
                distY = -((Constants.RegionSize * (float)(diffY - 1)) + (float)fromPos.Y);
            else
            if (diffY > 0)
                distY = ((Constants.RegionSize * (float)diffY) - (float)fromPos.Y);
            if (distY > drawDistance)
                return false;

            return true;
#else
            return false;   // draw distance does not extend the nearest-region-only visibility, for now
#endif
        }

#endregion

        /// <summary>
        /// This allows the Sim owner the abiility to kick users from their sim currently.
        /// It tells the client that the agent has permission to do so.
        /// </summary>
        public void GrantGodlikePowers(UUID agentID, UUID sessionID, UUID token, bool godStatus)
        {
            if (godStatus)
            {
                // For now, assign god level 200 to anyone
                // who is granted god powers, but has no god level set.
                //
                UserProfileData profile = m_scene.CommsManager.UserService.GetUserProfile(agentID);
                if (profile.GodLevel > 0)
                    m_godlevel = profile.GodLevel;
                else
                    m_godlevel = 200;
            }
            else
            {
                m_godlevel = 0;
            }

            ControllingClient.SendAdminResponse(token, (uint)m_godlevel);
        }

#region Child Agent Updates

        public void ChildAgentDataUpdate(AgentData cAgentData)
        {
            // m_log.Debug("[SCENE PRESENCE]: >>> ChildAgentDataUpdate <<< " + Scene.RegionInfo.RegionName);
            if (!IsChildAgent)
                return;

            CopyFrom(cAgentData);
        }

        private ulong _childAgentUpdateTime = 0;
        public void ChildAgentDataUpdate2(AgentData cAgentData)
        {
            if (!IsChildAgent)
                return;

            _childAgentUpdateTime = cAgentData.AgentDataCreatedOn;
            CopyFrom(cAgentData);
        }

        private static Vector3 NO_POSITION = new Vector3(-1, -1, -1);
        private Vector3 oldPos = NO_POSITION;
        /// <summary>
        /// This updates important decision making data about a child agent
        /// The main purpose is to figure out what objects to send to a child agent that's in a neighboring region
        /// </summary>
        public void ChildAgentPositionUpdate(AgentPosition cAgentData, uint tRegionX, uint tRegionY, uint rRegionX, uint rRegionY)
        {
            // m_log.Warn("[SCENE PRESENCE]: >>> ChildAgentPositionUpdate (" + rRegionX + "," + rRegionY+") at "+cAgentData.Position.ToString());
            int shiftx = ((int)rRegionX - (int)tRegionX) * (int)Constants.RegionSize;
            int shifty = ((int)rRegionY - (int)tRegionY) * (int)Constants.RegionSize;

            // m_log.ErrorFormat("[SCENE PRESENCE]: SP.ChildAgentPositionUpdate for R({0},{1}) T({2},{3}) at {4}+{5},{6}+{7},{8}",
            //          rRegionX.ToString(), rRegionY.ToString(), tRegionX.ToString(), tRegionY.ToString(),
            //          cAgentData.Position.X.ToString(), shiftx.ToString(), cAgentData.Position.Y.ToString(), shifty, cAgentData.Position.Z.ToString());

            if (!IsChildAgent)
            {
                m_log.Error("[SCENE PRESENCE]: ChildAgentPositionUpdate is NOT child agent - refused.");
                return;
            }
            if (IsInTransit)
            {
                m_log.Info("[SCENE PRESENCE]: ChildAgentPositionUpdate while in transit - ignored.");
                return;
            }
            if (cAgentData.Position != NO_POSITION) // if valid, use it.
            {
                lock (m_posInfo)
                {
                    // if (cAgentData.Position.CompareTo(m_posInfo.m_pos) != 0)
                    //    m_log.Warn("[SCENE PRESENCE]: >>> ChildAgentPositionUpdate (" + rRegionX + "," + rRegionY + ") at " + cAgentData.Position.ToString());
                    if (m_posInfo.Parent != null)
                    {
                        m_log.InfoFormat("[SCENE PRESENCE]: ChildAgentPositionUpdate move to {0} refused for agent already sitting at {1}.",
                                        cAgentData.Position.ToString(), cAgentData.Position.ToString());
                        return;
                    }
                    AbsolutePosition = new Vector3(cAgentData.Position.X + shiftx, cAgentData.Position.Y + shifty, cAgentData.Position.Z);
                }
            }

            // Do this after updating the position!
            m_DrawDistance = cAgentData.Far;    // update the SP draw distance *before* checking culling
            if (m_sceneView != null && m_sceneView.UseCulling)
            {
                //Check for things that may have just entered the draw distance of the user
                m_sceneView.CheckForDistantEntitiesToShow();
            }

            m_CameraCenter = new Vector3(cAgentData.Center.X + shiftx, cAgentData.Center.Y + shifty, cAgentData.Center.Z);

            m_avHeight = cAgentData.Size.Z;
            //SetHeight(cAgentData.AVHeight);

            if ((cAgentData.Throttles != null) && cAgentData.Throttles.Length > 0)
                ControllingClient.SetChildAgentThrottle(cAgentData.Throttles);
        }

        /// <summary>
        /// Returns this presense's raw stored position or position offset
        /// </summary>
        /// <returns></returns>
        public Vector3 GetRawPosition()
        {
            lock (m_posInfo)
            {
                return m_posInfo.Position;
            }
        }

        public void CopyToForRootAgent(AgentData cAgent)
        {
            cAgent.AgentID = UUID;
            cAgent.RegionHandle = m_scene.RegionInfo.RegionHandle;

            lock (m_posInfo)
            {
                // m_log.WarnFormat("[PRESENCE]: CopyToForRootAgent for {0} position {1} was {2}", cAgent.AgentID, m_posInfo.Position, cAgent.Position);

                cAgent.AvatarAsAPrim = !AvatarMovesWithPart;    // default (false) in protocol is legacy value (AvatarMovesWithPart==true).

                cAgent.Position = m_posInfo.Position;
                cAgent.Velocity = Velocity;
                cAgent.Center = m_CameraCenter;
                cAgent.Size = new Vector3(0, 0, m_avHeight);
                cAgent.AtAxis = m_CameraAtAxis;
                cAgent.LeftAxis = m_CameraLeftAxis;
                cAgent.UpAxis = m_CameraUpAxis;

                cAgent.Far = m_DrawDistance;

                cAgent.HeadRotation = m_headrotation;
                cAgent.BodyRotation = m_bodyRot;
                cAgent.ControlFlags = m_AgentControlFlags;
                cAgent.AlwaysRun = m_setAlwaysRun;

                if (m_posInfo.Parent != null)
                {
                    cAgent.SatOnPrimOffset = m_posInfo.Position;
                    cAgent.SatOnPrim = m_posInfo.Parent.UUID;
                    cAgent.SatOnGroup = m_posInfo.Parent.ParentGroup.UUID;
                }
            }

            // Throttles
            cAgent.Throttles = ControllingClient.GetThrottlesPacked(1.0f);

            cAgent.PresenceFlags = 0;
            if (ControllingClient.NeighborsRange == 1)
                cAgent.PresenceFlags |= (ulong)PresenceFlags.LimitNeighbors;
            if (ControllingClient.DebugCrossings)
                cAgent.PresenceFlags |= (ulong)PresenceFlags.DebugCrossings;
            if (m_scene.Permissions.IsGod(new UUID(cAgent.AgentID)))
                cAgent.GodLevel = (byte)m_godlevel;
            else 
                cAgent.GodLevel = (byte) 0;

            cAgent.Appearance = new AvatarAppearance(m_appearance);
            cAgent.AgentPrefs = new AgentPreferencesData(m_agentPrefs);

            // Animations
            try
            {
                cAgent.Anims = m_animations.ToArray();
            }
            catch { }

            List<RemotePresenceInfo> presInfo 
                = new List<RemotePresenceInfo>(m_remotePresences.GetRemotePresenceList().Select((AvatarRemotePresence pres) => { return pres.PresenceInfo; }));

            //add THIS presence since on another sim it will be remote
            presInfo.Add(new RemotePresenceInfo { CapsPath = m_connection.CircuitData.CapsPath, RegionInfo = m_scene.RegionInfo });

            cAgent.RemoteAgents = presInfo;

            PhysicsActor pa = PhysicsActor;
            if (pa != null)
            {
                cAgent.ConstantForces = pa.ConstantForce;
                cAgent.ConstantForcesAreLocal = pa.ConstantForceIsLocal;
            }

            //group data
            List<AgentGroupData> groupPowers = m_controllingClient.GetAllGroupPowers();
            if (groupPowers != null && groupPowers.Count > 0)
            {
                cAgent.Groups = groupPowers.ToArray();
            }

            cAgent.ActiveGroupID = m_controllingClient.ActiveGroupId;
        }

        public void CopyFrom(AgentData cAgent)
        {
            SceneObjectPart satOnPart = null;
            if (cAgent.SatOnPrim != UUID.Zero)
                satOnPart = m_scene.GetSceneObjectPart(cAgent.SatOnPrim);

            AvatarMovesWithPart = !cAgent.AvatarAsAPrim;    // default (false) in protocol is legacy value (AvatarMovesWithPart==true).

            m_rootRegionHandle = cAgent.RegionHandle;
            m_callbackURI = cAgent.CallbackURI;
            m_callbackTime = Util.GetLongTickCount();

            lock(m_posInfo)
            {
                // Now handle position info first, and other quick fields inside the lock
                if (satOnPart != null)
                {
                    m_requestedSitTargetID = satOnPart.LocalId;
                    m_requestedSitTargetUUID = cAgent.SatOnPrim;
                    m_requestedSitTargetOffset = cAgent.SatOnPrimOffset;

                    if (cAgent.Anims == null || cAgent.Anims.Length == 0)
                    {
                        UUID animID = m_controllingClient.GetDefaultAnimation("SIT");
                        this.AddAnimation(animID, m_requestedSitTargetUUID);
                    }
                    m_posInfo.Set(cAgent.SatOnPrimOffset, satOnPart, satOnPart.AbsolutePosition);
                }
                else
                {
                    cAgent.SatOnGroup = UUID.Zero;
                    m_requestedSitTargetID = 0;
                    m_requestedSitTargetUUID = UUID.Zero;
                    m_posInfo.Set(cAgent.Position, null, Vector3.Zero);
                }

                // Velocity = Vector3.Zero;
                Velocity = cAgent.Velocity;

                m_headrotation = cAgent.HeadRotation;
                m_bodyRot = cAgent.BodyRotation;
                m_AgentControlFlags = cAgent.ControlFlags; 
                m_avHeight = cAgent.Size.Z;

                m_CameraCenter = cAgent.Center;
                m_CameraAtAxis = cAgent.AtAxis;
                m_CameraLeftAxis = cAgent.LeftAxis;
                m_CameraUpAxis = cAgent.UpAxis;

                m_DrawDistance = cAgent.Far;

                m_setAlwaysRun = cAgent.AlwaysRun;

                m_restoredConstantForce = cAgent.ConstantForces;
                m_restoredConstantForceIsLocal = cAgent.ConstantForcesAreLocal;
            }
            if ((cAgent.Throttles != null) && cAgent.Throttles.Length > 0)
                ControllingClient.SetChildAgentThrottle(cAgent.Throttles);

            ControllingClient.DebugCrossings = (cAgent.PresenceFlags & (ulong)PresenceFlags.DebugCrossings) != 0;
            ControllingClient.NeighborsRange = ((cAgent.PresenceFlags & (ulong)PresenceFlags.LimitNeighbors) != 0) ? 1U : 2U;

            if (m_scene.Permissions.IsGod(new UUID(cAgent.AgentID)))
                m_godlevel = cAgent.GodLevel;

            m_appearance = new AvatarAppearance(cAgent.Appearance);
            m_agentPrefs = new AgentPreferencesData(cAgent.AgentPrefs);

            // Animations
            try
            {
                m_animations.Clear(cAgent.SatOnGroup != UUID.Zero);
                m_animations.FromArray(cAgent.Anims);
            }
            catch {  }

            if (cAgent.Groups != null)
            {
                m_controllingClient.SetGroupPowers(cAgent.Groups);
                m_controllingClient.SetActiveGroupInfo(new AgentGroupData { GroupID = cAgent.ActiveGroupID });
            }

            m_serializedAttachmentData = cAgent.SerializedAttachments;
            m_locomotionFlags = cAgent.LocomotionFlags;

            if (cAgent.RemoteAgents != null && !IsBot)
            {
                m_remotePresences.SetInitialPresences(cAgent.RemoteAgents);
            }
        }

        public bool CopyAgent(out IAgentData agent)
        {
            agent = new CompleteAgentData();
            CopyToForRootAgent((AgentData)agent);
            return true;
        }

#endregion Child Agent Updates

        /// <summary>
        /// Handles part of the PID controller function for moving an avatar.
        /// </summary>
        public override void UpdateMovement()
        {
            lock (m_forcesList)
            {
                if (_nudgeStart != 0 && (uint)Environment.TickCount - _nudgeStart >= _nudgeDuration)
                {
                    //the nudge is over
                    m_forcesList.Add(Vector3.Zero);
                    _nudgeStart = 0;
                }

                if (m_forcesList.Count > 0)
                {
                    for (int i = 0; i < m_forcesList.Count; i++)
                    {
                        Vector3 force = m_forcesList[i];

                        m_updateflag = true;
                        try
                        {
                            movementvector = force;
                            Velocity = movementvector;
                        }
                        catch (NullReferenceException)
                        {
                            // Under extreme load, this returns a NullReference Exception that we can ignore.
                            // Ignoring this causes no movement to be sent to the physics engine...
                            // which when the scene is moving at 1 frame every 10 seconds, it doesn't really matter!
                        }
                    }

                    m_forcesList.Clear();
                }
            }
        }

        static ScenePresence()
        {
            Primitive.TextureEntry textu = AvatarAppearance.GetDefaultTexture();
            DefaultTexture = textu.GetBytes();
            
        }

        public override void SetText(string text, Vector3 color, double alpha)
        {
            throw new Exception("Can't set Text on avatar.");
        }

        /// <summary>
        /// Adds a physical representation of the avatar to the Physics plugin
        /// </summary>
        public void AddToPhysicalScene(bool isFlying)
        {
            if (PhysicsActor != null)
            {
                DumpDebug("AddToPhysicalScene(existing)", "n/a");
                RemoveFromPhysicalScene();
            }
            DumpDebug("AddToPhysicalScene(clean)", "n/a"); 

            PhysicsScene scene = m_scene.PhysicsScene;

            OpenMetaverse.Vector3 pVec =
                new OpenMetaverse.Vector3(AbsolutePosition.X, AbsolutePosition.Y,
                                    AbsolutePosition.Z);
            OpenMetaverse.Quaternion pRot = Rotation;
            PhysicsActor pa = scene.AddAvatar(Firstname + "." + Lastname, pVec, pRot,
                                                new Vector3(0, 0, m_avHeight), isFlying,
                                                Velocity);

            PhysicsActor = pa;
            scene.AddPhysicsActorTaint(pa);

            pa.OnRequestTerseUpdate += SendTerseUpdateToAllClients;
            pa.OnPositionUpdate += new PositionUpdate(m_physicsActor_OnPositionUpdate);
            pa.OnCollisionUpdate += PhysicsCollisionUpdate;
            pa.SubscribeCollisionEvents(1000);
            pa.LocalID = LocalId;

            pa.AddForce(m_restoredConstantForce, m_restoredConstantForceIsLocal ? ForceType.ConstantLocalLinearForce : ForceType.ConstantGlobalLinearForce);
            m_restoredConstantForce = Vector3.Zero;
            m_restoredConstantForceIsLocal = false;

            // We have a new physActor... force agent update
            m_newPhysActorNeedsUpdate = true;
        }

        void m_physicsActor_OnPositionUpdate()
        {
        }

        /// <summary>
        /// Causes collision events for the avatar to be sent to the grp
        /// </summary>
        /// <param name="grp"></param>
        public void RegisterGroupToCollisionUpdates(SceneObjectGroup grp)
        {
            lock (m_groupsRegisteredForCollisionEvents)
            {
                if (m_groupsRegisteredForCollisionEvents.Contains(grp.UUID))
                    return;
                m_groupsRegisteredForCollisionEvents.Add(grp.UUID);
            }
        }

        /// <summary>
        /// Stops collision events for the avatar to be sent to the grp
        /// </summary>
        /// <param name="grp"></param>
        public void DeregisterGroupFromCollisionUpdates(SceneObjectGroup grp)
        {
            lock (m_groupsRegisteredForCollisionEvents)
            {
                if (!m_groupsRegisteredForCollisionEvents.Contains(grp.UUID))
                    return;
                m_groupsRegisteredForCollisionEvents.Remove(grp.UUID);
            }
        }

        /// <summary>
        /// Gets a list of all groups that are listening to this avatar's collision events
        /// </summary>
        /// <returns></returns>
        private List<SceneObjectGroup> GetGroupsRegisteredForCollisionUpdates()
        {
            List<SceneObjectGroup> grps = new List<SceneObjectGroup>();

            lock (m_groupsRegisteredForCollisionEvents)
            {
                foreach (UUID id in m_groupsRegisteredForCollisionEvents)
                {
                    SceneObjectPart prt = m_scene.GetSceneObjectPart(id);
                    if (prt == null)
                        continue;
                    if (prt.GetRootPartUUID() == id)
                        grps.Add(prt.ParentGroup);
                }
            }
            return grps;
        }

        // Event called by the physics plugin to tell the avatar about a collision.
        private void PhysicsCollisionUpdate(EventArgs e)
        {
            CollisionEventUpdate collisionData = (CollisionEventUpdate)e;

            List<SceneObjectGroup> attList = this.GetAttachments();
            attList.AddRange(GetGroupsRegisteredForCollisionUpdates());

            switch (collisionData.Type)
            {
                case CollisionEventUpdateType.CollisionBegan:
                    SceneObjectPart part = m_scene.SceneGraph.GetPrimByLocalId(collisionData.OtherColliderLocalId);

                    if (part != null)
                    {
                        HandleDamage(part);
                    }
                    
                    foreach (SceneObjectGroup group in attList)
                    {
                        if (group.WantsCollisionEvents)
                        {
                            group.RootPart.PhysicsCollision(e);
                        }
                    }
                    
                    break;

                case CollisionEventUpdateType.CollisionEnded:
                case CollisionEventUpdateType.LandCollisionBegan:
                case CollisionEventUpdateType.LandCollisionEnded:
                case CollisionEventUpdateType.CharacterCollisionBegan:
                case CollisionEventUpdateType.CharacterCollisionEnded:
                    foreach (SceneObjectGroup group in attList)
                    {
                        if (group.WantsCollisionEvents)
                        {
                            group.RootPart.PhysicsCollision(e);
                        }
                    }
                    break;

                case CollisionEventUpdateType.LandCollisionContinues:
                case CollisionEventUpdateType.CollisionContinues:
                case CollisionEventUpdateType.CharacterCollisionContinues:
                    foreach (SceneObjectGroup group in attList)
                    {
                        if (group.WantsRepeatingCollisionEvents)
                        {
                            group.RootPart.PhysicsCollision(e);
                        }
                    }
                    break;
            }

            UpdateMovementAnimations();
        }

        private void HandleDamage(SceneObjectPart part)
        {
            float starthealth = m_health;
            uint killerObj = 0;

            if (!m_invulnerable)
            {
                m_health -= CalculateDamage(part);

                if (m_health <= 0)
                {
                    killerObj = part.LocalId;
                }

                if (starthealth != m_health)
                {
                    //we can not fire TriggerAvatarKill from this thread because we're 
                    //inside the physics thread and some of the actions that need to 
                    //happen for an avatar kill are physics related. This will cause 
                    //the physics thread to wait on itself
                    Util.FireAndForget(delegate(object o)
                    {
                        ControllingClient.SendHealth(m_health);

                        if (m_health <= 0)
                        {
                            m_scene.EventManager.TriggerAvatarKill(killerObj, this);
                        }
                    });
                }
            }
        }

        private float CalculateDamage(SceneObjectPart part)
        {
            //sort of a guesstimate on a reasonable amount of total force for an instant kill
            const float KILL_FORCE = 3000;

            SceneObjectPart parentPart = part.ParentGroup.RootPart;
            PhysicsActor grpActor = parentPart.PhysActor;

            if (grpActor != null)
            {
                float force = Vector3.Mag(grpActor.Velocity) * grpActor.Mass;
                float damage = (force / KILL_FORCE) * 100;

                if (damage < 1.0f) return 0;
                else return damage;
            }

            return 0.0f;
        }

        public void setHealthWithUpdate(float health)
        {
            Health = health;
            ControllingClient.SendHealth(Health);
        }

        public void Close()
        {
            m_remotePresences.OnScenePresenceClosed();
            m_remotePresences = null;

            List<SceneObjectGroup> attList = GetAttachments();

            // Save and Delete attachments from scene only if we're a root and not a bot
            if (!IsChildAgent)
            {
                foreach (SceneObjectGroup grp in attList)
                {
                    if (IsBot)
                        m_scene.DeleteAttachment(grp);
                    else
                        m_scene.SaveAndDeleteAttachment(null, grp, grp.GetFromItemID(), grp.OwnerID);
                }
            }

            lock (m_attachments)
            {
                m_attachments.Clear();
            }
            
            RemoveFromPhysicalScene();

            if (!IsBot)
                m_scene.CommsManager.UserService.UnmakeLocalUser(m_uuid); 
            
            m_closed = true;

            ClearSceneView();
            SceneView.ClearAllTracking();
            m_sceneView = null; // free the reference
            m_controllingClient = null;
        }

        /// <summary>
        /// Ctor used for unit tests only
        /// </summary>
        public ScenePresence(Scene currentRegion, float drawDistance, IClientAPI mockClient)
        {
            m_scene = currentRegion;

            if (DefaultTexture == null)
            {
                Primitive.TextureEntry textu = AvatarAppearance.GetDefaultTexture();
                DefaultTexture = textu.GetBytes();
            }

            m_remotePresences = new AvatarRemotePresences(currentRegion, this);
            m_DrawDistance = drawDistance;
            m_controllingClient = mockClient;
        }

        public IEnumerable<UUID> CollectAttachmentItemIds()
        {
            List<UUID> itemIds = new List<UUID>();
            lock (m_attachments)
            {
                foreach (SceneObjectGroup grp in m_attachments)
                {
                    itemIds.Add(grp.GetFromItemID());
                }
            }

            return itemIds;
        }

        public IEnumerable<UUID> CollectVisibleAttachmentIds()
        {
            List<UUID> itemIds = new List<UUID>();
            lock (m_attachments)
            {
                foreach (SceneObjectGroup grp in m_attachments)
                {
                    if (!grp.IsAttachedHUD)
                        itemIds.Add(grp.UUID);
                }
            }

            return itemIds;
        }


        /// <summary>
        /// Thread safe getting of attachments
        /// </summary>
        /// <returns></returns>
        public List<SceneObjectGroup> GetAttachments()
        {
            List<SceneObjectGroup> grp;
            lock (m_attachments)
            {
                grp = new List<SceneObjectGroup>(m_attachments);
            }

            return grp;
        }

        public void AddAttachment(SceneObjectGroup gobj)
        {
            lock (m_attachments)
            {
                m_attachments.Add(gobj);
            }
        }

        public bool HasAttachments()
        {
            return m_attachments.Count > 0;   
        }

        public bool HasScriptedAttachments()
        {
            lock (m_attachments)
            {
                foreach (SceneObjectGroup gobj in m_attachments)
                {
                    if (gobj != null)
                    {
                        if (gobj.RootPart.Inventory.ContainsScripts())
                            return true;
                    }
                }
            }
            return false;
        }

        public SceneObjectGroup GetAttachmentByItemID(UUID itemID)
        {
            lock (m_attachments)
            {
                foreach (SceneObjectGroup grp in m_attachments)
                {
                    if (grp.GetFromItemID() == itemID)
                        return grp;
                }
            }
            return null;
        }

        public void RemoveAttachment(SceneObjectGroup gobj)
        {
            lock (m_attachments)
            {
                m_attachments.Remove(gobj);
            }
        }

        public bool ValidateAttachments()
        {
            lock (m_attachments)
            {
                // Validate
                foreach (SceneObjectGroup gobj in m_attachments)
                {
                    if (gobj == null)
                        return false;

                    if (gobj.IsDeleted)
                        return false;
                }
            }
            return true;
        }

        public void HandleActivateGroup(IClientAPI remoteClient, UUID groupID)
        {
            if (IsChildAgent)
                return;

            lock (m_attachments)
            {
                foreach (SceneObjectGroup gobj in m_attachments)
                {
                    if (gobj != null)
                    {
                        gobj.SetGroup(groupID, remoteClient);
                    }
                }
            }
        }

        /// <summary>
        /// Sends a full update for all attachments on us to presence
        /// 
        /// SHOULD ONLY BE CALLED FROM SCENEVIEW
        /// </summary>
        /// <param name="presence"></param>
        public void SendFullUpdateForAttachments(ScenePresence presence)
        {
            List<SceneObjectGroup> attachments = GetAttachments();

            // Validate
            foreach (SceneObjectGroup gobj in attachments)
            {
                if (gobj == null)
                    continue;

                if (gobj.IsDeleted)
                    continue;

                if (gobj.IsAttachedHUD)
                    continue;

                //Send an immediate update
                presence.SceneView.SendGroupUpdate(gobj, PrimUpdateFlags.ForcedFullUpdate);
            }
        }

        internal void AttachmentsCrossedToNewRegion()
        {
            List<SceneObjectGroup> attachments = this.GetAttachments();

            foreach (SceneObjectGroup gobj in attachments)
            {
                m_scene.DeleteSceneObject(gobj, true, true, true);
            }

            lock (m_attachments)
            {
                m_attachments.Clear();
            }
        }

        public List<SceneObjectGroup> CollectAttachmentsForCrossing()
        {
            List<SceneObjectGroup> attachments = new List<SceneObjectGroup>();
            lock (m_attachments)
            {
                // Validate
                foreach (SceneObjectGroup gobj in m_attachments)
                {
                    if (gobj == null || gobj.IsDeleted)
                        continue;

                    // Set the parent localID to 0 so it transfers over properly.
                    gobj.RootPart.SetParentLocalId(0);
                    gobj.RootPart.SavedAttachmentPoint = (byte)gobj.RootPart.AttachmentPoint;
                    gobj.RootPart.SavedAttachmentPos = gobj.RootPart.AttachedPos;
                    gobj.RootPart.SavedAttachmentRot = gobj.RootPart.RotationOffset;

                    attachments.Add(gobj);
                }
            }

            return attachments;
        }

        public void initializeScenePresence(IClientAPI client, RegionInfo region, Scene scene)
        {
            m_controllingClient = client;
            m_regionInfo = region;
            m_scene = scene;
        }

        internal void AddForce(OpenMetaverse.Vector3 force, ForceType ftype)
        {
            var phyActor = PhysicsActor;
            if (phyActor != null)
            {
                phyActor.AddForce(force, ftype);
            }
        }

        internal void AddAngularForce(OpenMetaverse.Vector3 force, ForceType ftype)
        {
            var pa = PhysicsActor;
            if (pa != null)
            {
                pa.AddAngularForce(force, ftype);
            }
        }

        CameraData physActor_OnPhysicsRequestingCameraData()
        {
            return new CameraData { Valid = true, CameraPosition = this.CameraPosition, CameraRotation = this.CameraRotation, MouseLook = this.m_mouseLook,
                                    HeadRotation = this.m_headrotation, BodyRotation = this.m_bodyRot };
        }

        /// <summary>
        /// This function adds the extra controls flags SL supplies in its ScriptControlChange packets to the viewer.
        /// </summary>
        /// <param name="controls">The raw controls request from the script.</param>
        /// <returns>The corresponding full mask with additional nudge flags to pass to the viewer.</returns>
        int AdjustControlsForNudges(int controls)
        {
            if ((controls & (int)ScriptControlled.CONTROL_FWD) != 0)
                controls |= (int)ScriptControlled.CONTROL_NUDGE_AT_POS;
            if ((controls & (int)ScriptControlled.CONTROL_BACK) != 0)
                controls |= (int)ScriptControlled.CONTROL_NUDGE_AT_NEG;
            if ((controls & (int)ScriptControlled.CONTROL_LEFT) != 0)
                controls |= (int)ScriptControlled.CONTROL_NUDGE_LEFT_POS;
            if ((controls & (int)ScriptControlled.CONTROL_RIGHT) != 0)
                controls |= (int)ScriptControlled.CONTROL_NUDGE_LEFT_NEG;
            return controls;
        }

        ///////////////////////////////////////////////////////////////////////////////////////////////
        // These ControlEvents functions below must only be called indirectly through LSLSystemAPI.cs 
        // via TriggerOnReleaseControls, so that the scripts there are also unhooked.
        public void RegisterControlEventsToScript(int controls, int accept, int pass_on, uint Obj_localID, UUID Script_item_UUID)
        {
            this.RegisterControlEventsToScript(-1, 0, controls, accept, pass_on, Obj_localID, Script_item_UUID, false);
        }

        public void RegisterControlEventsToScript(int oldControls, int oldPassOn, int controls, int accept, int pass_on, uint Obj_localID, UUID Script_item_UUID, bool silent)
        {
            ScriptControllers obj = new ScriptControllers();
            obj.ignoreControls = ScriptControlled.CONTROL_ZERO;
            obj.eventControls = ScriptControlled.CONTROL_ZERO;

            obj.itemID = Script_item_UUID;
            obj.objID = Obj_localID;

            SceneObjectPart part = m_scene.GetSceneObjectPart(Obj_localID);
            if (part == null)
            {
                m_log.ErrorFormat("[SCENE PRESENCE]: Could not register control events to script in object {0}. Object was not found in the scene", Obj_localID);
                return;
            }

            lock (m_scriptedcontrols)
            {
                if (pass_on == 0 && accept == 0)
                {
                    IgnoredControls |= (ScriptControlled)controls;
                    obj.ignoreControls = (ScriptControlled)controls;
                }

                if (pass_on == 0 && accept == 1)
                {
                    IgnoredControls |= (ScriptControlled)controls;
                    obj.ignoreControls = (ScriptControlled)controls;
                    obj.eventControls = (ScriptControlled)controls;
                }
                if (pass_on == 1 && accept == 1)
                {
                    IgnoredControls = ScriptControlled.CONTROL_ZERO;
                    obj.eventControls = (ScriptControlled)controls;
                    obj.ignoreControls = ScriptControlled.CONTROL_ZERO;
                }

                if (pass_on == 1 && accept == 0)
                {
                    IgnoredControls &= ~(ScriptControlled)controls;
                    RemoveScriptFromControlNotifications(Script_item_UUID, part);
                }
                else
                {
                    AddScriptToControlNotifications(Script_item_UUID, part, ref obj);
                }
            }

            if (!silent)
            {
                // SL includes these extra bits if you request FWD/BACK/LEFT/RIGHT
                oldControls = AdjustControlsForNudges(oldControls);
                controls = AdjustControlsForNudges(controls);
                
                if (accept == 0)
                {
                    if (pass_on == 0)   // a=false, p=false
                        ControllingClient.SendTakeControls2(oldControls, false, oldPassOn != 0, ~controls, true, false);
                    else   // false, true
                        ControllingClient.SendTakeControls2(oldControls, false, oldPassOn != 0, ~controls, true, true);
                }
                else
                {
                    if (pass_on == 0)   // a=true, p=false
                        ControllingClient.SendTakeControls2(oldControls, false, oldPassOn != 0, controls, true, false);
                    else   // true, true

                        ControllingClient.SendTakeControls2(oldControls, false, oldPassOn != 0, controls, true, true);
                }
            }
        }

        private void AddScriptToControlNotifications(OpenMetaverse.UUID Script_item_UUID, SceneObjectPart part, ref ScriptControllers obj)
        {
            m_scriptedcontrols[Script_item_UUID] = obj;

            PhysicsActor physActor = part.ParentGroup.RootPart.PhysActor;
            if (physActor != null)
            {
                physActor.OnPhysicsRequestingCameraData -= physActor_OnPhysicsRequestingCameraData;
                physActor.OnPhysicsRequestingCameraData += physActor_OnPhysicsRequestingCameraData;
            }
        }

        private void RemoveScriptFromControlNotifications(OpenMetaverse.UUID Script_item_UUID, SceneObjectPart part)
        {
            m_scriptedcontrols.Remove(Script_item_UUID);

            if (part != null)
            {
                PhysicsActor physActor = part.ParentGroup.RootPart.PhysActor;
                if (physActor != null)
                {
                    physActor.OnPhysicsRequestingCameraData -= physActor_OnPhysicsRequestingCameraData;
                }
            }
        }

        public void UnRegisterControlEventsToScript(uint Obj_localID, UUID Script_item_UUID)
        {
            UnRegisterControlEventsToScript(Obj_localID, Script_item_UUID, false);
        }

        public void UnRegisterControlEventsToScript(uint Obj_localID, UUID Script_item_UUID, bool silent)
        {
            SceneObjectPart part = m_scene.GetSceneObjectPart(Obj_localID);

            lock (m_scriptedcontrols)
            {
                int controls;
                bool accept;
                bool pass_on;
                if (m_scriptedcontrols.ContainsKey(Script_item_UUID))
                {
                    ScriptControllers takecontrolls = m_scriptedcontrols[Script_item_UUID];

                    if (m_isChildAgent)
                        m_log.InfoFormat("[SCENE PRESENCE]: UnRegisterControlEventsToScript2: Request({0}) from CHILD agent {1}", silent.ToString(), this.UUID.ToString());

                    if (takecontrolls.ignoreControls == ScriptControlled.CONTROL_ZERO)
                    {
                        // the only one with zero ignoreControls is a=true,p=true case
                        controls = (int)takecontrolls.eventControls;
                        accept = true;
                        pass_on = true;
                    } else
                    if (takecontrolls.eventControls == ScriptControlled.CONTROL_ZERO)
                    {
                        // the only one with zero eventControls is a=false,p=false case
                        controls = (int)takecontrolls.ignoreControls;
                        accept = false;
                        pass_on = false;
                    } else {
                        // that only leaves a=true,p=false case
                        controls = (int)takecontrolls.eventControls;    // also in ignoreControls
                        accept = true;
                        pass_on = false;
                    }

                    RemoveScriptFromControlNotifications(Script_item_UUID, part);

                    IgnoredControls = ScriptControlled.CONTROL_ZERO;
                    foreach (ScriptControllers scData in m_scriptedcontrols.Values)
                    {
                        IgnoredControls |= scData.ignoreControls;
                    }
                }
                else
                {
                    // the only one with no m_scriptedcontrols is a=false,p=true
                    controls = 0;
                    accept = false;
                    pass_on = true;
                }

                // SL includes these extra bits if you request FWD/BACK/LEFT/RIGHT
                controls = AdjustControlsForNudges(controls);

                // accept==false sends inverted controls bits
                if (accept == false)
                    controls = ~controls;

                if (!(silent || m_isChildAgent))  // don't notify the viewer if silent or child agent
                {
                    // accept isn't used in this case, always false on release
                    ControllingClient.SendTakeControls(controls, false, pass_on);
                }
            }
        }

        // These ControlEvents functions above must only be called indirectly through LSLSystemAPI.cs 
        // via TriggerOnReleaseControls, so that the scripts there are also unhooked.
        ///////////////////////////////////////////////////////////////////////////////////////////

        public void HandleForceReleaseControls(IClientAPI remoteClient, UUID agentID)
        {
            if (IsChildAgent)
            {
                m_log.Info("[SCENE PRESENCE]: HandleForceReleaseControls: Request from child agent - " + agentID.ToString());
                return;
            }
            List<ScriptControllers> scList;
            lock (m_scriptedcontrols)
            {
                scList = m_scriptedcontrols.Values.ToList<ScriptControllers>();
            }

            bool found = false;
            foreach (ScriptControllers scData in scList)
            {
                TaskInventoryItem item = null;
                SceneObjectPart part = m_scene.GetSceneObjectPart(scData.objID);
                if (part != null)
                    item = part.Inventory.GetInventoryItem(scData.itemID);
                if (item != null)
                {
                    TriggerOnReleaseControls(this, part, item, false);  // // handler checks first param for match
                    found = true;   // found at least one set of controls to release
                }
            }

            StandUp(false, true); // SL stands up the user on a forced controls release

            if (!found) // fail-safe... do *something* when this is called.
                ControllingClient.SendTakeControls2(-1, false, false, -1, false, true);
        }

        /// <summary>
        /// This function sends a new release/take combo packet for each script that has controls in a sat-upon prim.
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="agentID"></param>
        public void ResendVehicleControls()
        {
            if (IsChildAgent)
            {
                m_log.Info("[SCENE PRESENCE]: ResendVehicleControls: Request from child agent - " + this.UUID.ToString());
                return;
            }
            List<ScriptControllers> scList;
            lock (m_scriptedcontrols)
            {
                scList = m_scriptedcontrols.Values.ToList<ScriptControllers>();
            }

            foreach (ScriptControllers scData in scList)
            {
                TaskInventoryItem item = null;
                SceneObjectPart part = m_scene.GetSceneObjectPart(scData.objID);
                if (part != null)
                    item = part.Inventory.GetInventoryItem(scData.itemID);
                if (item != null)
                {
                    if ((item.PermsGranter == this.UUID) && (scData.eventControls == scData.ignoreControls))    // true,false case
                    {
                        int controls = AdjustControlsForNudges((int)scData.eventControls);
                        this.ControllingClient.SendTakeControls2(controls, false, false, controls, true, false);
                    }
                }
            }
        }

        private uint m_LastControlsTime;
        private uint m_NextControlsTime;
        private uint m_LastControls;

        internal void SendControlToScripts(uint flags)
        {
            ScriptControlled allflags = ScriptControlled.CONTROL_ZERO;

            // The viewers send a stream of control flags repeatedly while there is movement. If no control is active, 
            // viewers send a CONTROL_NONE flag. This logic introduces a small repeat delay in controls to mimic the
            // behavior in SL and thus retain LSL script compatibility.
            //

            // If an active control flag is sent and it has been a while since the last one,
            // begin the delay interval.
            if (flags != (uint)AgentManager.ControlFlags.NONE)
            {
                if (((uint)Environment.TickCount - m_LastControlsTime) > CONTROLS_REPEAT_DELAY)
                {
                    m_NextControlsTime = 0;
                }
                m_LastControlsTime = (uint)Environment.TickCount;
            }

            // Let the first control event through, but set a delay interval.
            if (m_NextControlsTime == 0)
            {
                m_NextControlsTime = (uint)Environment.TickCount + CONTROLS_REPEAT_DELAY;
            }

            // Drop the events until either the delay ends or a different set of flags arrive.
            else if ((uint)Environment.TickCount < m_NextControlsTime && flags == m_LastControls)
            {
                // m_log.DebugFormat("[SCENE PRESENCE]: Skipped flags={0} now={1} next={2}", (AgentManager.ControlFlags)flags, Environment.TickCount, m_NextControlsTime);
                return;
            }
            m_LastControls = flags;
            // m_log.DebugFormat("[SCENE PRESENCE]: Processed flags={0} now={1} next={2}", (AgentManager.ControlFlags)flags, Environment.TickCount, m_NextControlsTime);

            if (MouseDown)
            {
                allflags = LastCommands & (ScriptControlled.CONTROL_ML_LBUTTON | ScriptControlled.CONTROL_LBUTTON);
                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_LBUTTON_UP) != 0 || (flags & unchecked((uint)AgentManager.ControlFlags.AGENT_CONTROL_ML_LBUTTON_UP)) != 0)
                {
                    allflags = ScriptControlled.CONTROL_ZERO;
                    MouseDown = true;
                }
            }

            if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_ML_LBUTTON_DOWN) != 0)
            {
                allflags |= ScriptControlled.CONTROL_ML_LBUTTON;
                MouseDown = true;
            }
            if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_LBUTTON_DOWN) != 0)
            {
                allflags |= ScriptControlled.CONTROL_LBUTTON;
                MouseDown = true;
            }

            // find all activated controls, whether the scripts are interested in them or not
            if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_POS) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_POS) != 0)
            {
                allflags |= ScriptControlled.CONTROL_FWD;
            }
            if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_NEG) != 0)
            {
                allflags |= ScriptControlled.CONTROL_BACK;
            }
            if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_UP_POS) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_POS) != 0)
            {
                allflags |= ScriptControlled.CONTROL_UP;
            }
            if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_NEG) != 0)
            {
                allflags |= ScriptControlled.CONTROL_DOWN;
            }
            if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_LEFT_POS) != 0)
            {
                allflags |= ScriptControlled.CONTROL_LEFT;
            }
            if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_LEFT_NEG) != 0)
            {
                allflags |= ScriptControlled.CONTROL_RIGHT;
            }
            if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_YAW_NEG) != 0)
            {
                allflags |= ScriptControlled.CONTROL_ROT_RIGHT;
            }
            if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_YAW_POS) != 0)
            {
                allflags |= ScriptControlled.CONTROL_ROT_LEFT;
            }
            // optimization; we have to check per script, but if nothing is pressed and nothing changed, we can skip that
            if (allflags != ScriptControlled.CONTROL_ZERO || allflags != LastCommands)
            {
                UUID[] scripts;
                lock (m_scriptedcontrols)
                {
                    // Must not call TriggerControlEvent with thes controls locked (deadlocks with m_items iterations).
                    scripts = new UUID[m_scriptedcontrols.Count];
                    m_scriptedcontrols.Keys.CopyTo(scripts, 0);
                }
                foreach (UUID scriptUUID in scripts)
                {
                    ScriptControllers scriptControlData = m_scriptedcontrols[scriptUUID];
                    ScriptControlled localHeld = allflags & scriptControlData.eventControls;     // the flags interesting for us
                    ScriptControlled localLast = LastCommands & scriptControlData.eventControls; // the activated controls in the last cycle
                    ScriptControlled localChange = localHeld ^ localLast;                        // the changed bits
                    if (localHeld != ScriptControlled.CONTROL_ZERO || localChange != ScriptControlled.CONTROL_ZERO)
                    {
                        // only send if still pressed or just changed
                        m_scene.EventManager.TriggerControlEvent(scriptControlData.objID, scriptUUID, UUID, (uint)localHeld, (uint)localChange);
                    }
                }
            }

            LastCommands = allflags;
        }

        internal static uint RemoveIgnoredControls(uint flags, ScriptControlled Ignored)
        {
            if (Ignored == ScriptControlled.CONTROL_ZERO)
                return flags;
            if ((Ignored & ScriptControlled.CONTROL_BACK) != 0)
                flags &= ~((uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG | (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_NEG);
            if ((Ignored & ScriptControlled.CONTROL_FWD) != 0)
                flags &= ~((uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_POS | (uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_POS);
            if ((Ignored & ScriptControlled.CONTROL_DOWN) != 0)
                flags &= ~((uint)AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG | (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_NEG);
            if ((Ignored & ScriptControlled.CONTROL_UP) != 0)
                flags &= ~((uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_POS | (uint)AgentManager.ControlFlags.AGENT_CONTROL_UP_POS);
            if ((Ignored & ScriptControlled.CONTROL_LEFT) != 0)
                flags &= ~((uint)AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS | (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_LEFT_POS);
            if ((Ignored & ScriptControlled.CONTROL_RIGHT) != 0)
                flags &= ~((uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_LEFT_NEG | (uint)AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG);
            if ((Ignored & ScriptControlled.CONTROL_ROT_LEFT) != 0)
                flags &= ~((uint)AgentManager.ControlFlags.AGENT_CONTROL_YAW_NEG);
            if ((Ignored & ScriptControlled.CONTROL_ROT_RIGHT) != 0)
                flags &= ~((uint)AgentManager.ControlFlags.AGENT_CONTROL_YAW_POS);
            if ((Ignored & ScriptControlled.CONTROL_ML_LBUTTON) != 0)
                flags &= ~((uint)AgentManager.ControlFlags.AGENT_CONTROL_ML_LBUTTON_DOWN);
            if ((Ignored & ScriptControlled.CONTROL_LBUTTON) != 0)
                flags &= ~((uint)AgentManager.ControlFlags.AGENT_CONTROL_LBUTTON_UP | (uint)AgentManager.ControlFlags.AGENT_CONTROL_LBUTTON_DOWN);
                //DIR_CONTROL_FLAG_FORWARD = AgentManager.ControlFlags.AGENT_CONTROL_AT_POS,
                //DIR_CONTROL_FLAG_BACK = AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG,
                //DIR_CONTROL_FLAG_LEFT = AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS,
                //DIR_CONTROL_FLAG_RIGHT = AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG,
                //DIR_CONTROL_FLAG_UP = AgentManager.ControlFlags.AGENT_CONTROL_UP_POS,
                //DIR_CONTROL_FLAG_DOWN = AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG,
                //DIR_CONTROL_FLAG_DOWN_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_NEG
            return flags;
        }

        //
        //HACK HACK HACK
        // This is to get rid of a race condition in the user profile cache.
        // we were storing whether or not the user needs an initial attachment rez
        // inside the profile which uses a timed cache. theory is that that cache item
        // was expiring before the user could log in since many operations load the user
        // information and can cause the cache entry to be invalidated at any time
        // we use a timed cache here, but only the initialRez call will set it, leaving
        // the user with up to 5 minutes to log into this region after we set the need
        // for an initial rez. We will also update and replace this entry
        //
        static private Dictionary<UUID, ulong> s_needsInitialRez = new Dictionary<UUID, ulong>();
        private List<byte[]> m_serializedAttachmentData;
        private AgentLocomotionFlags m_locomotionFlags;
        
        static public void SetNeedsInitialAttachmentRez(UUID userId)
        {
            lock (s_needsInitialRez)
            {
                s_needsInitialRez[userId] = Util.GetLongTickCount();
            }
        }

        static public void SetNoLongerNeedsInitialAttachmentRez(UUID userId)
        {
            lock (s_needsInitialRez)
            {
                s_needsInitialRez.Remove(userId);
            }
        }

        static public bool CheckNeedsInitialAttachmentRezAndReset(UUID userId)
        {
            const ulong INITIAL_REZ_TIMEOUT = 5 * 60 * 1000;
            lock (s_needsInitialRez)
            {
                ulong time;
                if (s_needsInitialRez.TryGetValue(userId, out time))
                {
                    s_needsInitialRez.Remove(userId);

                    if (Util.GetLongTickCount() - time < INITIAL_REZ_TIMEOUT)
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
                    return false;
                }
            }
        }
    }
}
