/*
 * Copyright (c) 2015, InWorldz Halcyon Developers
 * All rights reserved.
 * 
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 * 
 *   * Redistributions of source code must retain the above copyright notice, this
 *     list of conditions and the following disclaimer.
 * 
 *   * Redistributions in binary form must reproduce the above copyright notice,
 *     this list of conditions and the following disclaimer in the documentation
 *     and/or other materials provided with the distribution.
 * 
 *   * Neither the name of halcyon nor the names of its
 *     contributors may be used to endorse or promote products derived from
 *     this software without specific prior written permission.
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
 * FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
 * DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
 * CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
 * OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */


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
using System.Drawing;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Permissions;
using System.Xml;
using System.Xml.Serialization;
using log4net;
using OpenMetaverse;
using OpenMetaverse.Packets;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes.Scripting;
using OpenSim.Region.Physics.Manager;
using System.Threading;

namespace OpenSim.Region.Framework.Scenes
{       
    #region Enumerations

    [Flags]
    public enum Changed : uint
    {   // See LSL_Constants.cs - MUST MATCH, but using it creates circular dependency
        INVENTORY = 1,              // CHANGED_INVENTORY
        COLOR = 2,                  // CHANGED_COLOR
        SHAPE = 4,                  // CHANGED_SHAPE
        SCALE = 8,                  // CHANGED_SCALE
        TEXTURE = 16,               // CHANGED_TEXTURE
        LINK = 32,                  // CHANGED_LINK
        ALLOWED_DROP = 64,          // CHANGED_ALLOW_DROP
        OWNER = 128,                // CHANGED_OWNER
        REGION = 256,               // CHANGED_REGION
        TELEPORT = 512,             // CHANGED_TELEPORT
        REGION_START = 1024,        // CHANGED_REGION_START, CHANGED_REGION_RESTART
        MEDIA = 2048,               // CHANGED_MEDIA
        ANIMATION = 16384,          // CHANGED_ANIMATION
    }

    // I don't really know where to put this except here.
    // Can't access the OpenSim.Region.ScriptEngine.Common.LSL_BaseClass.Changed constants
    [Flags]
    public enum ExtraParamType
    {
        Something1 = 1,
        Something2 = 2,
        Something3 = 4,
        Something4 = 8,
        Flexible = 16,
        Light = 32,
        Sculpt = 48,
        Something5 = 64,
        Something6 = 128
    }

    [Flags]
    public enum TextureAnimFlags : byte
    {
        NONE = 0x00,
        ANIM_ON = 0x01,
        LOOP = 0x02,
        REVERSE = 0x04,
        PING_PONG = 0x08,
        SMOOTH = 0x10,
        ROTATE = 0x20,
        SCALE = 0x40
    }

    [Flags]
    public enum PrimType : int
    {
        BOX = 0,
        CYLINDER = 1,
        PRISM = 2,
        SPHERE = 3,
        TORUS = 4,
        TUBE = 5,
        RING = 6,
        SCULPT = 7
    }

    [Flags]
    public enum ServerPrimFlags : uint
    {
        None = 0,

        // PRIM_SIT_TARGET supports TRUE/FALSE,pos,rot 
        // even for ZERO_VECTOR,ZERO_ROTATION
        // so we need to store this too.
        SitTargetActive = 1,

        // We need to know whether to use the legacy sit target persistence 
        // or the one above, or existing content will break.
        // If this bit is NOT set, ignore SitTargetActive
        // and use the legacy pos/rot != zero test.
        SitTargetStateSaved = 2
    }
    #endregion Enumerations

    public class SceneObjectPart : IScriptHost, ISceneEntity
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType); 
        
        public static readonly int MAX_GENERATION = 5;  // Grey goo fence, starts at 0
        public static readonly uint GENERATION_COOLDOWN = 300;  // seconds, grey goo fence cools at 1 generation per every 5 minutes

        public static readonly int PAY_HIDE = -1;
        public static readonly int PAY_DEFAULT = -2;
        public static readonly int PAY_DEFAULT1 = 20;
        public static readonly int PAY_DEFAULT2 = 50;
        public static readonly int PAY_DEFAULT3 = 100;
        public static readonly int PAY_DEFAULT4 = 200;

        // use only one serializer to give the runtime a chance to optimize it (it won't do that if you
        // use a new instance every time)
        private static XmlSerializer serializer = new XmlSerializer(typeof (SceneObjectPart));

        #region Constants

        /// <value>
        /// Denote all sides of the prim
        /// </value>
        public const int ALL_SIDES = -1;

        private const uint PERM_TRANS = (uint)0x00002000;
        private const uint PERM_MODIFY  = (uint)0x00004000; // 16384
        private const uint PERM_COPY    = (uint)0x00008000;
        private const uint PERM_MOVE    = (uint)0x00080000; // 524288

        public const float MIN_PART_SCALE = 0.001f;         // smallest size of a prim dimension

        #endregion Constants

        #region Fields

        [XmlIgnore]
        public bool AllowedDrop = false;

        [XmlIgnore]
        public bool DIE_AT_EDGE = false;

        // The default PayPrice is PAY_DEFAULT, set in the SceneObjectPart constructor.
        // These fields are initialized to PAY_HIDE for security reasons when loading a asset with no saved price info.
        public int[] PayPrice = { PAY_HIDE, PAY_HIDE, PAY_HIDE, PAY_HIDE, PAY_HIDE };

        [XmlIgnore]
        public PhysicsActor PhysActor = null;

        //Xantor 20080528 Sound stuff:
        //  Note: This isn't persisted in the database right now, as the fields for that aren't just there yet.
        //        Not a big problem as long as the script that sets it remains in the prim on startup.
        //        for SL compatibility it should be persisted though (set sound / displaytext / particlesystem, kill script)
        public UUID Sound;
        public byte SoundOptions;   // See OpenMetaverse.SoundFlags
        public float SoundGain;
        public float SoundRadius = 20.0f;  // default sound radius from LSLSystemAPI.cs (moved here).

        [XmlIgnore]
        private uint TimeStampRezzed = (uint)Util.UnixTimeSinceEpoch();  // for dating the age of a group

        [XmlIgnore]
        private uint TimeStampRez = (uint)0;  // time of most recent llRez...

        [XmlIgnore]
        public int Generation = 0; // for grey goo checks, recursive rezzing

        [XmlIgnore]
        public int FullUpdateCounter = 0;  // for updates

        [XmlIgnore]
        public int TerseUpdateCounter = 0;

        [XmlIgnore]
        public uint TimeStampLastActivity = 0; // Will be used for AutoReturn
        
        [XmlIgnore]
        public UUID FromItemID = UUID.Zero;
               
        /// <value>
        /// The UUID of the user inventory item from which this object was rezzed if this is a root part.  
        /// If UUID.Zero then either this is not a root part or there is no connection with a user inventory item.
        /// </value>
        private UUID m_fromUserInventoryItemID = UUID.Zero;
        
        [XmlIgnore]
        public UUID FromUserInventoryItemID
        {
            get { return m_fromUserInventoryItemID; }
        }

        [XmlIgnore]
        public bool IsPrim
        {
            get { return m_shape.PCode == (byte)PCode.Prim; }
        }

        [XmlIgnore]
        public ScriptEvents AggregateScriptEvents = 0;
        
        public bool HasPrimRoot
        {
            get { return ParentGroup.RootPart.Shape.PCode == (byte)PCode.Prim; }
        }

        [XmlIgnore]
        public bool IsAttachment
        {
            get
            {
                if ((ParentGroup == null) || (ParentGroup.RootPart == null))
                    return false;   // bootstrapping case

                if (ParentGroup.RootPart != this)   // this is a child prim
                    return ParentGroup.IsAttachment;

                if (m_shape.PCode != (byte)PCode.Prim)
                    return false;       // not a prim, assume not attached

                return (m_shape.State != 0);
            }
        }

        [XmlIgnore]
        public byte AttachmentPoint
        {
            get
            {
                if (ParentGroup.RootPart != this)
                    return ParentGroup.AttachmentPoint;

                if (!IsAttachment)
                    return 0;
                return m_shape.State;
            }
        }

        public const byte MIN_ATTACHMENT = (byte)OpenMetaverse.AttachmentPoint.Chest;           // 1
        public const byte MIN_HUD        = (byte)OpenMetaverse.AttachmentPoint.HUDCenter2;      // 31
        public const byte MAX_HUD        = (byte)OpenMetaverse.AttachmentPoint.HUDBottomRight;  // 38
        public const byte MAX_ATTACHMENT = (byte)OpenMetaverse.AttachmentPoint.RightHindFoot;   // 55

        // Attachment points with the high bit set must be converted to 7-bit before these calls.
        public static bool IsAttachmentPointOnHUD(uint attachPoint)
        {
            return (attachPoint >= MIN_HUD) && (attachPoint <= MAX_HUD);
        }
        public static bool IsValidAttachmentPoint(uint attachPoint)
        {
            return (attachPoint >= MIN_ATTACHMENT) && (attachPoint <= MAX_ATTACHMENT);
        }

        public bool IsAttachedHUD
        {
            get
            {
                if (!IsAttachment)
                    return false;
                byte att = AttachmentPoint;
                return (att >= MIN_HUD) && (att <= MAX_HUD);
            }
        }
        public bool IsLindenPlant()
        {
            return (m_shape.PCode == (byte)PCode.Grass)
                || (m_shape.PCode == (byte)PCode.Tree)
                || (m_shape.PCode == (byte)PCode.NewTree);
        }

        [XmlIgnore]
        public UUID AttachedAvatar
        {
            get
            {
                if (!IsRootPart())
                    return ParentGroup.AttachedAvatar;

                if (!IsAttachment)
                    return UUID.Zero;   // not attached

                return OwnerID;
            }
        }

        [XmlIgnore]
        public bool IsTemporary
        {
            get
            {
                return (Flags & PrimFlags.Temporary) != 0 ||
                    (Flags & PrimFlags.TemporaryOnRez) != 0;
            }
        }
        
        [XmlIgnore]
        public Vector3 AttachedPos = Vector3.Zero;

        [XmlIgnore]
        public OpenMetaverse.Vector3 RotationAxis_deprecated;  // Deprecated, not used.

        [XmlIgnore]
        public bool VolumeDetectActive = false; // XmlIgnore set to avoid problems with persistance until I come to care for this
                                                // Certainly this must be a persistant setting finally

        [XmlIgnore]
        public bool IsWaitingForFirstSpinUpdatePacket = false;
        [XmlIgnore]
        public Quaternion SpinOldOrientation = new Quaternion();

        /// <summary>
        /// This part's inventory
        /// </summary>
        [XmlIgnore]
        public IEntityInventory Inventory
        {
            get { return m_inventory; }
        }       
        protected SceneObjectPartInventory m_inventory;

        [XmlIgnore]
        public bool Undoing = false;

        [XmlIgnore]
        public bool IgnoreUndoUpdate = false;

        [XmlIgnore]
        private PrimFlags LocalFlags = 0;
        private byte[] m_TextureAnimation;
        private byte m_clickAction = 0;
        private Color m_textColor = Color.Black;
        private string m_description = String.Empty;
        // private OpenMetaverse.Vector3 m_lastRotationalVelocity = OpenMetaverse.Vector3.Zero;
        private int m_linkNum = 0;
        [XmlIgnore]
        private int m_scriptAccessPin = 0;
        [XmlIgnore]
        private readonly Dictionary<UUID, ScriptEvents> m_scriptEvents = new Dictionary<UUID, ScriptEvents>();
        private string m_sitName = String.Empty;
        private Quaternion m_sitTargetOrientation = Quaternion.Identity;
        private Vector3 m_sitTargetPosition = Vector3.Zero;
        private string m_sitAnimation = "SIT";
        private string m_text = String.Empty;
        private string m_touchName = String.Empty;
        private UndoStack<UndoState> m_undo = new UndoStack<UndoState>(5);
        private UndoStack<UndoState> m_redo = new UndoStack<UndoState>(5);
        private object m_undoLock = new object();
        private UUID _creatorID;
        private bool m_passTouches = false;

        public enum UpdateLevel : byte
        {
            None = 0,
            Terse = 1,
            Compressed = 2,
            Full = 3
        }

        protected Vector3 m_acceleration;
        protected Vector3 m_angularVelocity;

        //unkown if this will be kept, added as a way of removing the group position from the group class
        protected Vector3 m_groupPosition;
        protected uint m_localId;
        protected Material m_material = OpenMetaverse.Material.Wood; // Wood
        protected string m_name;
        protected Vector3 m_offsetPosition;

        // FIXME, TODO, ERROR: 'ParentGroup' can't be in here, move it out.
        protected SceneObjectGroup m_parentGroup;
        protected byte[] m_particleSystem = new byte[0];
        protected ulong m_regionHandle;
        protected Quaternion m_rotationOffset;
        protected PrimitiveBaseShape m_shape = null;
        protected UUID m_uuid;
        protected Vector3 m_serializedVelocity;
        protected Vector3 m_serializedPhysicalAngularVelocity;

        /// <summary>
        /// Stores media texture data
        /// </summary>
        protected string m_mediaUrl;

        // TODO: Those have to be changed into persistent properties at some later point,
        // or sit-camera on vehicles will break on sim-crossing.
        private Vector3 m_cameraEyeOffset = new Vector3(0.0f, 0.0f, 0.0f);
        private Vector3 m_cameraAtOffset = new Vector3(0.0f, 0.0f, 0.0f);
        private bool m_forceMouselook = false;

        // TODO: Collision sound should have default.
        private UUID m_collisionSound = UUID.Zero;
        private float m_collisionSoundVolume = 0.0f;

        /// <summary>
        /// Defines whether this part is currently part of a transaction.  If it is, 
        /// updates will not be sent to the client
        /// </summary>
        [XmlIgnore]
        private bool _isInTransaction = false;

        /// <summary>
        /// A temporary variable used when loading a prim from the database or the network
        /// used to set and retrieve serialized physics data that the correct physics
        /// plugin can use. Calling get{} is also useful if there is a physactor present
        /// </summary>
        private byte[] _serializedPhysicsData;
        public byte[] SerializedPhysicsData 
        {
            get
            {
                PhysicsActor physActor = this.PhysActor;
                if (physActor != null)
                {
                    return physActor.GetSerializedPhysicsProperties();
                }

                return _serializedPhysicsData;
            }

            set
            {
                _serializedPhysicsData = value;
            }
        }

        /// <summary>
        /// Server Weight. For Mesh we use a value of 422 (in InventoryCapsModule) verts = 1.0f.  Defaults to 1.0
        /// for regular prims.  We will set this value in the mesh uploader if it should be changed.
        /// </summary>
        private static float WEIGHT_NOT_SET = -1.0f;    // -1 is a sentinel value meaning "not set yet"
        private float _serverWeight = WEIGHT_NOT_SET;
        public virtual float ServerWeight
        {
            get
            {
                if (_serverWeight <= 0.0f)
                    return 1.0f;
                return _serverWeight;
            }
            set
            {
                SceneObjectGroup parent = ParentGroup;
                float previous = _serverWeight;
                _serverWeight = value;
                if (parent != null)
                {
                    // Rare but called when part.ServerWeight is assigned (e.g. from llSetPrimitiveParams on a shape change).
                    if (previous <= 0.0f)
                        previous = 1.0f;
                    if (previous != value)
                        parent.ServerWeightDelta(_serverWeight - previous);
                }
            }
        }

        private Dictionary<OpenMetaverse.UUID, byte[]> _serializedScriptByteCode;
        [XmlIgnore]
        public Dictionary<OpenMetaverse.UUID, byte[]> SerializedScriptByteCode
        {
            get
            {
                return _serializedScriptByteCode;
            }

            set
            {
                _serializedScriptByteCode = value;
            }
        }

        /// <summary>
        /// A temporary variable used when loading a prim from the database or the network
        /// used to set and retrieve serialized physics shapes that the correct physics
        /// plugin can use. Calling get{} is also useful if there is a physactor present
        /// </summary>
        private byte[] _serializedPhysicsShapes;
        public byte[] SerializedPhysicsShapes
        {
            get
            {
                PhysicsActor physActor = this.PhysActor;
                if (physActor != null)
                {
                    return physActor.GetSerializedPhysicsShapes();
                }

                return _serializedPhysicsShapes;
            }

            set
            {
                _serializedPhysicsShapes = value;
            }
        }

        /// <summary>
        /// StreamingCost. Based on a triangle budget and LOD levels.  Defaults to 1.0f
        /// This value can change on a prim resize so we need to recalculate it
        /// </summary>
        private float m_streamingCost = WEIGHT_NOT_SET;
        public virtual float StreamingCost
        {
            get
            {
                if (m_streamingCost <= 0.0f)
                {
                    // If its a mesh recalc Streaming Cost
                    if (m_shape.SculptType == (byte)SculptType.Mesh)
                        m_streamingCost = Shape.GetStreamingCost();
                    else
                        m_streamingCost = 1.0f;

                    SceneObjectGroup parent = ParentGroup;
                    if (parent != null)
                        parent.StreamingCostDelta(m_streamingCost);
                }

                return m_streamingCost;
            }
            set
            {
                SceneObjectGroup parent = ParentGroup;
                float previousStreamingCost = m_streamingCost;
                m_streamingCost = value;

                if (parent != null)
                {
                    // Rare but called when part.StreamingCost is assigned (e.g. from llSetPrimitiveParams on a shape change).
                    if (previousStreamingCost <= 0.0f)
                        previousStreamingCost = 1.0f;

                    // Happens on a resize.  StreamingCost is recalculated based on the new prim size.
                    if (previousStreamingCost != value)
                    {
                        // Adjust Land Cost
                        LandCost = CalculateLandCost(_serverWeight, m_streamingCost);

                        // Add the delta of the old and new value into the SOG
                        parent.StreamingCostDelta(m_streamingCost - previousStreamingCost);
                    }
                }
            }
        }

        // We call this in multiple places so keep the actual algorithm in one spot.
        private float CalculateLandCost(float serverWeight, float streamingCost)
        {
            return Math.Min(serverWeight, streamingCost);
        }

        // Land Impact is the minimum of ServerWeight and StreamingCost
        private float m_landCost = WEIGHT_NOT_SET;

        [XmlIgnore]
        public virtual float LandCost
        {
            get
            {
                if (m_landCost <= 0.0f)
                    m_landCost = CalculateLandCost(ServerWeight, StreamingCost);

                return m_landCost;
            }
            private set
            {
                SceneObjectGroup parent = ParentGroup;
                float previousLandCost = m_landCost;
                m_landCost = value;

                if (parent != null)
                {
                    if (previousLandCost <= 0.0f)
                        previousLandCost = 1.0f;
                    if (previousLandCost != m_landCost)
                        parent.LandCostDelta(m_landCost - previousLandCost);
                }
            }
        }

        #endregion Fields

        #region Constructors

        /// <summary>
        /// No arg constructor called by region restore db code
        /// </summary>
        public SceneObjectPart()
        {
            if (m_TextureAnimation == null)
            {
                m_TextureAnimation = new byte[0];
            }

            if (m_particleSystem == null)
            {
                m_particleSystem = new byte[0];
            }

            Rezzed = DateTime.Now;
            
            m_inventory = new SceneObjectPartInventory(this);
        }

        /// <summary>
        /// Create a completely new SceneObjectPart (prim).  This will need to be added separately to a SceneObjectGroup
        /// </summary>
        /// <param name="ownerID"></param>
        /// <param name="shape"></param>
        /// <param name="position"></param>
        /// <param name="rotationOffset"></param>
        /// <param name="offsetPosition"></param>
        public SceneObjectPart(
            UUID ownerID, PrimitiveBaseShape shape, Vector3 groupPosition, 
            Quaternion rotationOffset, Vector3 offsetPosition, bool rezSelected)
        {
            m_name = "Primitive";

            Rezzed = DateTime.Now;
            _creationDate = (Int32) (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            _ownerID = ownerID;
            _creatorID = _ownerID;
            _lastOwnerID = UUID.Zero;
            UUID = UUID.Random();
            Shape = shape;
            // Todo: Add More Object Parameter from above!
            _ownershipCost = 0;
            _objectSaleType = (byte) 0;
            _salePrice = 0;
            _category = (uint) 0;
            _lastOwnerID = _creatorID;
            // End Todo: ///
            GroupPosition = groupPosition;
            OffsetPosition = offsetPosition;
            RotationOffset = rotationOffset;
            Velocity = new Vector3(0, 0, 0);
            AngularVelocity = new Vector3(0, 0, 0);     
            m_TextureAnimation = new byte[0];
            m_particleSystem = new byte[0];

            for (int x = 0; x < 5; x++)
                PayPrice[x] = PAY_DEFAULT;

                // Prims currently only contain a single folder (Contents).  From looking at the Second Life protocol,
                // this appears to have the same UUID (!) as the prim.  If this isn't the case, one can't drag items from
                // the prim into an agent inventory (Linden client reports that the "Object not found for drop" in its log

            if (rezSelected) _flags |= PrimFlags.CreateSelected;
            else _flags = 0;

            TrimPermissions();
            //m_undo = new UndoStack<UndoState>(ParentGroup.GetSceneMaxUndo());
            
            m_inventory = new SceneObjectPartInventory(this);
        }

        #endregion Constructors

        #region XML Schema

        private UUID _lastOwnerID;
        private UUID _ownerID;
        private UUID _groupID;
        private int _ownershipCost;
        private byte _objectSaleType;
        private int _salePrice;
        private uint _category;
        private Int32 _creationDate;
        private uint _parentID = 0;
        private uint _baseMask = (uint)(PermissionMask.All | PermissionMask.Export);
        private uint _ownerMask = (uint)(PermissionMask.All | PermissionMask.Export);
        private uint _groupMask = (uint)PermissionMask.None;
        private uint _everyoneMask = (uint)PermissionMask.None;
        private uint _nextOwnerMask = (uint)PermissionMask.All;
        private PrimFlags _flags = 0;
        private DateTime m_expires;
        private DateTime m_rezzed;
        private byte _prevAttPt= (byte)0;
        private Vector3 _prevAttPos = Vector3.Zero;
        private Quaternion _prevAttRot = Quaternion.Identity;
        private Vector3 _standTargetPos = Vector3.Zero;
        private Quaternion _standTargetRot = Quaternion.Identity;
        private ServerPrimFlags _serverFlags = 0;

        [XmlIgnore]
        public bool IsInTransaction
        {
            get
            {
                return _isInTransaction;
            }

            set
            {
                _isInTransaction = value;
            }
        }

        public UUID CreatorID 
        {
            get
            {
                return _creatorID;
            }
            set
            {
                _creatorID = value;
            }
        }

        /// <summary>
        /// A relic from when we we thought that prims contained folder objects. In 
        /// reality, prim == folder  
        /// Exposing this is not particularly good, but it's one of the least evils at the moment to see
        /// folder id from prim inventory item data, since it's not (yet) actually stored with the prim.
        /// </summary>
        public UUID FolderID
        {
            get { return UUID; }
            set { } // Don't allow assignment, or legacy prims wil b0rk - but we need the setter for legacy serialization.
        }

        /// <value>
        /// Access should be via Inventory directly - this property temporarily remains for xml serialization purposes
        /// </value>
        public uint InventorySerial
        {
            get { return m_inventory.Serial; }
            set { m_inventory.Serial = value; }
        }

        /// <value>
        /// Access should be via Inventory directly - this property temporarily remains for xml serialization purposes
        /// </value>        
        public TaskInventoryDictionary TaskInventory
        {
            get { return m_inventory.Items; }
            set { m_inventory.Items = value; }
        }

        public uint ObjectFlags
        {
            get { return (uint)_flags; }
            set { _flags = (OpenMetaverse.PrimFlags)value; }
        }

        public UUID UUID
        {
            get { return m_uuid; }
            set { m_uuid = value; }
        }

        public uint LocalId
        {
            get 
            { 
                return m_localId; 
            }
            set 
            {
                uint oldLocalId = m_localId;
                m_localId = value;

                if (m_parentGroup != null)
                {
                    m_parentGroup.LocalIdUpdated(this, oldLocalId, value);
                }
            }
        }

        public virtual string Name
        {
            get { return m_name; }
            set 
            { 
                m_name = value;
                PhysicsActor physActor = PhysActor;
                if (physActor != null)
                {
                    physActor.SOPName = value;
                }
            }
        }

        public byte Material
        {
            get { return (byte) m_material; }
            set
            {
                m_material = (Material)value;

                PhysicsActor physActor = PhysActor;
                if (physActor != null)
                {
                    physActor.SetMaterial((Material)value, false);
                }
            }
        }

        public bool PassTouches
        {
            get { return m_passTouches; }
            set
            {
                if ((m_passTouches != value) && (ParentGroup != null))
                    ParentGroup.HasGroupChanged = true;
                m_passTouches = value;
            }
        }

        public ulong RegionHandle
        {
            get { return m_regionHandle; }
            set { m_regionHandle = value; }
        }

        public int ScriptAccessPin
        {
            get { return m_scriptAccessPin; }
            set { m_scriptAccessPin = (int)value; }
        }

        public Byte[] TextureAnimation
        {
            get { return m_TextureAnimation; }
            set { m_TextureAnimation = value; }
        }

        public Byte[] ParticleSystem
        {
            get { return m_particleSystem; }
            set { m_particleSystem = value; }
        }

        [XmlIgnore]
        public DateTime Expires
        {
            get { return m_expires; }
            set { m_expires = value; }
        }

        [XmlIgnore]
        public DateTime Rezzed
        {
            get { return m_rezzed; }
            set { m_rezzed = value; }
        }

        /// <summary>
        /// The position of the entire group that this prim belongs to.
        /// Like GroupPosition except never based on the attached avatar position.
        /// </summary>
        public Vector3 RawGroupPosition
        {
            get
            {
                // If this is a linkset, we don't want the physics engine mucking up our group position here.
                if (PhysActor != null && _parentID == 0)
                {
                    Vector3 physPos = PhysActor.Position;   // grab it once, for consistent members
                    m_groupPosition.X = physPos.X;
                    m_groupPosition.Y = physPos.Y;
                    m_groupPosition.Z = physPos.Z;
                }

                return m_groupPosition;
            }
        }

        public Vector3 GroupPositionNoUpdate
        {
            get
            {
                return m_groupPosition;
            }
        }

        /// <summary>
        /// The local position of this group according to the parent coordinate system
        /// Like GroupPosition except never based on the attached avatar position.
        /// </summary>
        [XmlIgnore]
        public Vector3 LocalPos
        {
            get
            {
                if (IsAttachment)
                {
                    if (IsRootPart())
                    {
                        return AttachedPos;
                    }
                    else
                    {
                        return OffsetPosition;
                    }
                }

                if (IsRootPart())
                {
                    return GetWorldPosition();
                }
                else
                {
                    return OffsetPosition;
                }
            }
        }

        /// <summary>
        /// The position of the entire group that this prim belongs to.
        /// </summary>
        public Vector3 GroupPosition
        {
            get
            {
                if (IsAttachment)
                {
                    return GetWearerPosition();
                }

                if (IsRootPart())
                {
                    return GetWorldPosition();
                }
                else
                {
                    return m_parentGroup.RootPart.GroupPosition;
                }
            }
            set
            {
                SetGroupPosition(value, false, false);
            }
        }

        // Internal function that has all the logic but also allows the operation to be forced (even if in transit)
        public bool SetGroupPosition(Vector3 value, bool forced, bool physicsTriggered)
        {
            if ((!forced) && (ParentGroup != null) && ParentGroup.InTransit) // it's null at startup time
            {
                m_log.WarnFormat("[SCENEOBJECTPART]: GroupPosition update for {0} to {1} ignored while in transit.", ParentGroup.Name, value.ToString());
                return false;
            }

            //check for nan and inf for x y and z.  refuse to set position
            //in these cases
            if (Single.IsNaN(value.X) || Single.IsInfinity(value.X) ||
                Single.IsNaN(value.Y) || Single.IsInfinity(value.Y) ||
                Single.IsNaN(value.Z) || Single.IsInfinity(value.Z))
            {
                return false;
            }

            SetGroupPositionDirect(value);

            if (!physicsTriggered)
            {
                PhysicsActor physActor = PhysActor;
                if (physActor != null)
                {
                    try
                    {
                        // Root prim actually goes at Position
                        if (IsRootPart())
                        {
                            physActor.Position = value;
                        }
                    }
                    catch (Exception e)
                    {
                        m_log.Error("[SCENEOBJECTPART]: GROUP POSITION. " + e.Message);
                    }
                }
            }

            return true;
        }

        private void UpdateSeatedAvatarPositions()
        {
            m_seatedAvatars.ForEach((ScenePresence sp) =>
            {
                if (sp.AvatarMovesWithPart)
                    sp.SendTerseUpdateToAllClients();
            });
        }
        
        public Vector3 OffsetPosition
        {
            get 
            { 
                return m_offsetPosition; 
            }
            set
            {
                StoreUndoState();
                m_offsetPosition = value;
                //Rebuild the bounding box for the parent group
                if(m_parentGroup != null) m_parentGroup.ClearBoundingBoxCache();

                PhysicsActor physActor = PhysActor;
                if (physActor != null)
                {
                    physActor.UpdateOffsetPosition(m_offsetPosition, m_rotationOffset);
                }
                UpdateSeatedAvatarPositions();
            }
        }

        public Quaternion RotationOffset
        {
            get
            {
                PhysicsActor physActor = PhysActor;
                if (physActor != null && IsRootPart())
                {
                    return physActor.Rotation;
                }

                return m_rotationOffset;
            }
            set
            {
                StoreUndoState();
                m_rotationOffset = value;
                //Rebuild the bounding box for the parent group
                if (m_parentGroup != null) m_parentGroup.ClearBoundingBoxCache();

                PhysicsActor physActor = PhysActor;
                if (physActor != null)
                {
                    physActor.Rotation = value;
                }
                UpdateSeatedAvatarPositions();
            }
        }

        [XmlIgnore]
        public Vector3 SerializedVelocity
        {
            get
            {
                return m_serializedVelocity;
            }
        }

        /// <summary></summary>
        public Vector3 Velocity
        {
            get
            {
                PhysicsActor physActor = PhysActor;
                if (physActor != null && physActor.IsPhysical)
                {
                    return physActor.Velocity;
                }
                else if (IsAttachment)
                {
                    return GetWearerVelocity();
                }
                else
                {
                    return Vector3.Zero;
                }
            }

            set
            {
                m_serializedVelocity = value; //used when this object is restored from XML 

                PhysicsActor physActor = PhysActor;
                if (physActor != null && physActor.IsPhysical)
                {
                    physActor.Velocity = value;
                }
            }
        }

        public Vector3 GetWearerVelocity()
        {
            if (m_parentGroup == null || m_parentGroup.Scene == null)
                return Vector3.Zero;

            ScenePresence sp = m_parentGroup.Scene.GetScenePresence(AttachedAvatar);
            if (sp == null)
                return Vector3.Zero;

            return sp.Velocity;
        }

        /// <summary>
        /// This has been relegated to be TargetOmega ONLY. This does NOT represent physical rotational velocities any longer
        /// </summary>
        public Vector3 AngularVelocity //Should be renamed to AngularVelocityTarget in protobuf2
        {
            get
            {
                PhysicsActor physActor = PhysActor;
                if ((physActor != null) && IsRootPart())
                {
                    return physActor.AngularVelocityTarget;
                }
                else
                {
                    return m_angularVelocity;
                }
            }
            set 
            { 
                m_angularVelocity = value;

                //NOTE: This will NOT set the physActor.AngularVelocity on deserialization restore
                //because the physactor doesn't exist yet. 
                PhysicsActor physActor = PhysActor;
                if (physActor != null)
                {
                    physActor.AngularVelocity = value;
                }
            }
        }

        /// <summary>
        /// Used to store and retrieve the physical angular velocity for serialization
        /// </summary>
        public Vector3 PhysicalAngularVelocity
        {
            get
            {
                PhysicsActor physActor = PhysActor;
                if ((physActor != null) && IsRootPart() && physActor.IsPhysical)
                {
                    return physActor.AngularVelocity;
                }
                else
                {
                    return m_serializedPhysicalAngularVelocity;
                }
            }
            set
            {
                m_serializedPhysicalAngularVelocity = value;
            }
        }

        [XmlIgnore]
        public Vector3 Acceleration
        {
            get 
            {
                PhysicsActor physActor = PhysActor;
                if (physActor != null)
                {
                    return physActor.Acceleration;
                }
                else
                {
                    return Vector3.Zero;
                }
            }
        }

        public string Description
        {
            get { return m_description; }
            set 
            {
                m_description = value;

                PhysicsActor physActor = PhysActor;
                if (physActor != null)
                {
                    physActor.SOPDescription = value;
                }
            }
        }

        [XmlIgnore()] 
        public Color TextColor
        {
            get { return m_textColor; }
            set
            {
                m_textColor = value;
            }
        }

        public string XmlColorType
        {
            get
            {
                return Util.SerializeColor(m_textColor);
            }
            set
            {
                m_textColor = Util.DeserializeColor(value);
            }

        }

        public string Text
        {
            get
            {
                if (m_text.Length > 255)
                    return m_text.Substring(0, 254);
                return m_text;
            }
            set
            {
                m_text = value;
            }
        }


        public string SitName
        {
            get { return m_sitName; }
            set { m_sitName = value; }
        }

        public string TouchName
        {
            get { return m_touchName; }
            set { m_touchName = value; }
        }

        public int LinkNum
        {
            get { return m_linkNum; }
            set { m_linkNum = value; }
        }

        public byte ClickAction
        {
            get { return m_clickAction; }
            set
            {
                m_clickAction = value;
            }
        }

        public PrimitiveBaseShape Shape
        {
            get { return m_shape; }
            set
            {
                bool shape_changed = false;
                // TODO: this should really be restricted to the right
                // set of attributes on shape change.  For instance,
                // changing the lighting on a shape shouldn't cause
                // this.
                if (m_shape != null)
                    shape_changed = true;

                m_shape = value;

                if (shape_changed)
                    TriggerScriptChangedEvent(Changed.SHAPE);
            }
        }
        
        public Vector3 Scale
        {
            get { return m_shape.Scale; }
            set
            {
                if (m_shape != null)
                {
                    if (value.X < MIN_PART_SCALE)
                        value.X = MIN_PART_SCALE;
                    if (value.Y < MIN_PART_SCALE)
                        value.Y = MIN_PART_SCALE;
                    if (value.Z < MIN_PART_SCALE)
                        value.Z = MIN_PART_SCALE;

                    if (m_shape.Scale != value)
                    {
                        StoreUndoState();
                        m_shape.Scale = value;

                        PhysicsActor physActor = PhysActor;
                        if (physActor != null && m_parentGroup != null)
                        {
                            if (m_parentGroup.Scene != null)
                            {
                                if (m_parentGroup.Scene.PhysicsScene != null)
                                {
                                    m_parentGroup.Scene.PhysicsScene.AddPhysicsActorTaint(physActor, TaintType.ChangedScale);
                                }
                            }
                        }

                        // If its a mesh recalc Streaming Cost
                        if (m_shape.SculptType == (byte)SculptType.Mesh)
                        {
                            //have we initialized the extracted shape data yet? ...
                            if (Shape.HighLODBytes != 0)
                            {
                                //... yes, use the data we have to calculate the streaming cost
                                StreamingCost = Shape.GetStreamingCost();
                            }
                            else
                            {
                                //... no, grab the asset and perform the calculation
                                if (m_shape.SculptTexture != UUID.Zero && m_parentGroup != null && m_parentGroup.Scene != null)
                                {
                                    AssetRequestInfo reqInfo = AssetRequestInfo.InternalRequest();
                                    m_parentGroup.Scene.CommsManager.AssetCache.GetAsset(m_shape.SculptTexture, (UUID assetId, AssetBase asset) =>
                                        {
                                            if (asset != null)
                                            {
                                                int highBytes, midBytes, lowBytes, lowestBytes;

                                                SceneObjectPartMeshCost.GetMeshLODByteCounts(asset.Data, out highBytes, out midBytes,
                                                    out lowBytes, out lowestBytes);

                                                Shape.HighLODBytes = highBytes;
                                                Shape.MidLODBytes = midBytes;
                                                Shape.LowLODBytes = lowBytes;
                                                Shape.LowestLODBytes = lowestBytes;

                                                int vertCount;
                                                SceneObjectPartMeshCost.GetMeshVertexCount(asset.Data, out vertCount);

                                                Shape.VertexCount = vertCount;

                                                StreamingCost = Shape.GetStreamingCost();
                                                this.ParentGroup.HasGroupChanged = true;
                                            }
                                        },
                                        reqInfo
                                    );
                                }
                            }
                        }

                        //Rebuild the bounding box for the parent group
                        if (m_parentGroup != null) 
                            m_parentGroup.ClearBoundingBoxCache();

                        TriggerScriptChangedEvent(Changed.SCALE);
                    }
                }
            }
        }

        /// <summary>
        /// Used for media on a prim.
        /// </summary>
        /// Do not change this value directly - always do it through an IMoapModule.
        public string MediaUrl
        {
            get
            {
                return m_mediaUrl;
            }
            set
            {
                m_mediaUrl = value;
                if (ParentGroup != null)
                    ParentGroup.HasGroupChanged = true;
            }
        }

        public KeyframeAnimation KeyframeAnimation { get; set; }

        public uint ServerFlags
        {
            get { return (uint)_serverFlags; }
            set { _serverFlags = (ServerPrimFlags)value; }
        }

        public bool SitTargetActive
        {
            get { return (_serverFlags & ServerPrimFlags.SitTargetActive) != 0; }
            set
            {
                if (value)  // enable or disable SitTargetEnabled flag
                    _serverFlags |= ServerPrimFlags.SitTargetActive;
                else
                    _serverFlags &= ~ServerPrimFlags.SitTargetActive;
            }
        }

        #endregion

        //---------------
        #region Public Properties with only Get

        public uint TimeStamp
        {
            get
            {
                return TimeStampRezzed;
            }
        }

        public uint TimeStampLastRez
        {
            get
            {
                return TimeStampRez;
            }
        }

        private OpenSim.Framework.Geom.Box GetBoundingBox(bool isRelative)
        {
            // Check for the simpler case of root prim since it's all root-relative. ;)
            if (isRelative && IsRootPart())
                return new OpenSim.Framework.Geom.Box(Vector3.Zero, this.Scale);

            List<Vector3> corners = new List<Vector3>();
            Vector3 scale = this.Scale / 2.0f;
            Quaternion rot = this.RotationOffset;
            Quaternion rot2 = (isRelative || IsRootPart()) ? Quaternion.Identity : this.ParentGroup.RootPart.RotationOffset;
            corners.Add((new Vector3(-scale.X, -scale.Y, -scale.Z) * rot) * rot2);
            corners.Add((new Vector3(-scale.X, -scale.Y,  scale.Z) * rot) * rot2);
            corners.Add((new Vector3(-scale.X,  scale.Y, -scale.Z) * rot) * rot2);
            corners.Add((new Vector3(-scale.X,  scale.Y,  scale.Z) * rot) * rot2);
            corners.Add((new Vector3( scale.X, -scale.Y, -scale.Z) * rot) * rot2);
            corners.Add((new Vector3( scale.X, -scale.Y,  scale.Z) * rot) * rot2);
            corners.Add((new Vector3( scale.X,  scale.Y, -scale.Z) * rot) * rot2);
            corners.Add((new Vector3( scale.X,  scale.Y,  scale.Z) * rot) * rot2);

            Vector3 minCorner = Vector3.Zero;
            Vector3 maxCorner = Vector3.Zero;
            foreach (Vector3 corner in corners)
            {
                if (corner.X < minCorner.X) minCorner.X = corner.X;
                if (corner.Y < minCorner.Y) minCorner.Y = corner.Y;
                if (corner.Z < minCorner.Z) minCorner.Z = corner.Z;
                if (corner.X > maxCorner.X) maxCorner.X = corner.X;
                if (corner.Y > maxCorner.Y) maxCorner.Y = corner.Y;
                if (corner.Z > maxCorner.Z) maxCorner.Z = corner.Z;
            }
            Vector3 rscale = new Vector3(maxCorner.X - minCorner.X, maxCorner.Y - minCorner.Y, maxCorner.Z - minCorner.Z);

            // Dimensions/sizes (rscale values) of the prim must be positive, but max-min should be.
            Vector3 offset = isRelative ? this.OffsetPosition : this.AbsolutePosition;
            return new OpenSim.Framework.Geom.Box(offset, rscale);
        }

        public OpenSim.Framework.Geom.Box BoundingBox
        {
            get
            {
                return GetBoundingBox(false);
            }
        }

        // This returns the bounding box with the rotation applied and corners transformed.
        // This returns info completely relative to the root prim's position and orientation.
        public OpenSim.Framework.Geom.Box RelativeBoundingBox
        {
            get
            {
                return GetBoundingBox(true);
            }
        }

        public Vector3 AbsolutePosition
        {
            get {
                if (IsAttachment)
                    return GroupPosition;

                return GetWorldPosition();
            }
        }

        public UUID ObjectCreator
        {
            get { return _creatorID; }
        }

        public UUID ObjectOwner
        {
            get { return _ownerID; }
        }

        public SceneObjectGroup ParentGroup
        {
            get { return m_parentGroup; }
        }

        public ScriptEvents ScriptEvents
        {
            get { return AggregateScriptEvents; }
        }


        public Quaternion SitTargetOrientation
        {
            get { return m_sitTargetOrientation; }
            set { m_sitTargetOrientation = value; }
        }

        public Vector3 SitTargetPosition
        {
            get { return m_sitTargetPosition; }
            set { m_sitTargetPosition = value; }
        }

        public bool Stopped
        {
            get {
                double threshold = 0.02;
                return (Math.Abs(Velocity.X) < threshold &&
                        Math.Abs(Velocity.Y) < threshold &&
                        Math.Abs(Velocity.Z) < threshold &&
                        Math.Abs(AngularVelocity.X) < threshold &&
                        Math.Abs(AngularVelocity.Y) < threshold &&
                        Math.Abs(AngularVelocity.Z) < threshold);
            }
        }

        public uint ParentID
        {
            get { return _parentID; }
            set { _parentID = value; }
        }

        public int CreationDate
        {
            get { return _creationDate; }
            set { _creationDate = value; }
        }

        public uint Category
        {
            get { return _category; }
            set { _category = value; }
        }

        public int SalePrice
        {
            get { return _salePrice; }
            set { _salePrice = value; }
        }

        public byte ObjectSaleType
        {
            get { return _objectSaleType; }
            set { _objectSaleType = value; }
        }

        public int OwnershipCost
        {
            get { return _ownershipCost; }
            set { _ownershipCost = value; }
        }

        public UUID GroupID
        {
            get { return _groupID; }
            set { _groupID = value; }
        }

        public UUID OwnerID
        {
            get { return _ownerID; }
            set { _ownerID = value; }
        }

        public UUID LastOwnerID
        {
            get { return _lastOwnerID; }
            set { _lastOwnerID = value; }
        }

        public bool IsGroupDeeded
        {
            get { return (GroupID != UUID.Zero) && (OwnerID == GroupID); }
        }

        public uint BaseMask
        {
            get { return _baseMask; }
            set { _baseMask = value; }
        }

        public uint OwnerMask
        {
            get { return _ownerMask; }
            set { _ownerMask = value; }
        }

        public uint GroupMask
        {
            get { return _groupMask; }
            set { _groupMask = value; }
        }

        public uint EveryoneMask
        {
            get { return _everyoneMask; }
            set { _everyoneMask = value; }
        }

        public uint NextOwnerMask
        {
            get { return _nextOwnerMask; }
            set { _nextOwnerMask = value; }
        }

        public byte SavedAttachmentPoint
        {
            get { return _prevAttPt; }
            set { _prevAttPt = value; }
        }

        public Vector3 SavedAttachmentPos
        {
            get { return _prevAttPos; }
            set { _prevAttPos = value; }
        }

        public Quaternion SavedAttachmentRot
        {
            get { return _prevAttRot; }
            set { _prevAttRot = value; }
        }

        public Vector3 StandTargetPos
        {
            get { return _standTargetPos; }
            set { _standTargetPos = value; }
        }

        public Quaternion StandTargetRot
        {
            get { return _standTargetRot; }
            set { _standTargetRot = value; }
        }

        public PrimFlags Flags
        {
            get { return _flags; }
            set { _flags = value; }
        }

        [XmlIgnore]
        public virtual UUID RegionID
        {
            get
            {
                if (ParentGroup != null && ParentGroup.Scene != null)
                    return ParentGroup.Scene.RegionInfo.RegionID;
                else
                    return UUID.Zero;
            }
            set {} // read only
        }

        [XmlIgnore]
        public string SitAnimation
        {
            get { return m_sitAnimation; }
            set { m_sitAnimation = value; }
        }

        public UUID CollisionSound
        {
            get { return m_collisionSound; }
            set
            {
                m_collisionSound = value;
                DoAggregateScriptEvents();
            }
        }

        public float CollisionSoundVolume
        {
            get { return m_collisionSoundVolume; }
            set { m_collisionSoundVolume = value; }
        }

        #endregion Public Properties with only Get

        

        #region Private Methods

        [XmlIgnore]
        private AvatarPartsCollection m_seatedAvatars = new AvatarPartsCollection ();

        [XmlIgnore]
        public int NumAvatarsSeated
        {
            get { return m_seatedAvatars.Count; }
        }

        public void ForEachSittingAvatar(Action<ScenePresence> action)
        {
            m_seatedAvatars.ForEach(action);
        }

        private uint ApplyMask(uint val, bool set, uint mask)
        {
            if (set)
            {
                return val |= mask;
            }
            else
            {
                return val &= ~mask;
            }
        }

        private void SendObjectPropertiesToClient(UUID AgentID)
        {
            ScenePresence avatar = m_parentGroup.Scene.GetScenePresence(AgentID);
            if (avatar != null)
                m_parentGroup.GetProperties(avatar.ControllingClient);
        }

        #endregion Private Methods

        #region Public Methods

        public bool IsRootPart()
        {
            return m_parentGroup == null             // no parent group...consider this root
                || m_parentGroup.RootPart == null    // no parent part... consider this root
                || m_parentGroup.RootPart == this    // matches?
            ;
        }

        // Note that enabled=false is not the same as removing a sit target.
        public void SetSitTarget(bool isActive, Vector3 pos, Quaternion rot, bool preserveSitter)
        {
            // Take care of this prim.
            SitTargetPosition = pos;
            SitTargetOrientation = rot;
            SitTargetActive = isActive;

            // Now update the parent group.
            if (ParentGroup != null)
                ParentGroup.SetSitTarget(this, isActive, pos, rot, preserveSitter);
        }

        public void RemoveSitTarget()
        {
            // Take care of this prim.
            SitTargetPosition = Vector3.Zero;
            SitTargetOrientation = Quaternion.Identity;
            SitTargetActive = false;

            // Now update the parent group.
            if (ParentGroup != null)
                ParentGroup.RemoveSitTarget(this.UUID);
        }

        // This function must be called on asset load (inventory rez) or database load (rezzed)
        // with SOP.ServerFlags initialized, which may be updated before return.
        // Returns true if there is an active sit target after calculation.
        public bool PrepSitTargetFromStorage(Vector3 sitTargetPos, Quaternion sitTargetRot)
        {
            // Now the sit target info itself.
            bool sitTargetEnabled = ((this.ServerFlags & (uint) ServerPrimFlags.SitTargetActive) != 0);
            if (!sitTargetEnabled) // check if legacy data
            {
                if ((this.ServerFlags & (uint) ServerPrimFlags.SitTargetStateSaved) == 0) // not set
                {   // check if non-zero sit target in pos/rot
                    if ((sitTargetPos != Vector3.Zero) || (sitTargetRot != Quaternion.Identity))
                    {
                        sitTargetEnabled = true;
                        this.ServerFlags |= (uint)ServerPrimFlags.SitTargetActive;
                    }
                }
            }
            // Mark this one as updated to using this ServerFlags.
            this.ServerFlags |= (uint)ServerPrimFlags.SitTargetStateSaved;
            return sitTargetEnabled;
        }

        public static readonly uint LEGACY_BASEMASK = 0x7FFFFFF0;
        public static bool IsLegacyBasemask(uint basemask)
        {
            return ((basemask & SceneObjectPart.LEGACY_BASEMASK) == SceneObjectPart.LEGACY_BASEMASK);
        }

        public void ResetExpire()
        {
            Expires = DateTime.Now + new TimeSpan(600000000);
        }

        public void AddFlag(PrimFlags flag)
        {
            // PrimFlags prevflag = Flags;
            if ((ObjectFlags & (uint) flag) == 0)
            {
                _flags |= flag;

                if (flag == PrimFlags.TemporaryOnRez)
                    ResetExpire();
                if (ParentGroup != null)    // null when called from the persistence backup
                    if((flag & PrimFlags.Scripted) != 0 && !ParentGroup.IsScripted)
                        ParentGroup.RecalcScriptedStatus();
            }
        }

        public void StampLastRez()
        {
            TimeStampRez = (uint)Util.UnixTimeSinceEpoch();
        }

        /// <summary>
        /// Tell all scene presences that they should send updates for this part to their clients
        /// </summary>
        public void AddFullUpdateToAllAvatars(PrimUpdateFlags flags)
        {
            List<ScenePresence> avatars = m_parentGroup.Scene.GetScenePresences();
            foreach (ScenePresence avatar in avatars)
            {
                if (!(avatar.IsDeleted || avatar.IsInTransit))
                    avatar.SceneView.QueuePartForUpdate(this, flags);
            }
        }
        
        public void AddNewParticleSystem(Primitive.ParticleSystem pSystem)
        {
            m_particleSystem = pSystem.GetBytes();
        }

        public void RemoveParticleSystem()
        {
            m_particleSystem = new byte[0];
        }

        /// Terse updates
        public void AddTerseUpdateToAllAvatars()
        {
            List<ScenePresence> avatars = m_parentGroup.Scene.GetScenePresences();
            foreach (ScenePresence avatar in avatars)
            {
                if (!(avatar.IsDeleted || avatar.IsInTransit))
                    avatar.SceneView.QueuePartForUpdate(this, PrimUpdateFlags.TerseUpdate);
            }
        }
        
        public void AddTextureAnimation(Primitive.TextureAnimation pTexAnim)
        {
            byte[] data = new byte[16];
            int pos = 0;

            // The flags don't like conversion from uint to byte, so we have to do
            // it the crappy way.  See the above function :(

            data[pos] = ConvertScriptUintToByte((uint)pTexAnim.Flags); pos++;
            data[pos] = (byte)pTexAnim.Face; pos++;
            data[pos] = (byte)pTexAnim.SizeX; pos++;
            data[pos] = (byte)pTexAnim.SizeY; pos++;

            Utils.FloatToBytes(pTexAnim.Start).CopyTo(data, pos);
            Utils.FloatToBytes(pTexAnim.Length).CopyTo(data, pos + 4);
            Utils.FloatToBytes(pTexAnim.Rate).CopyTo(data, pos + 8);

            m_TextureAnimation = data;
        }

        public void AdjustSoundGain(double volume)
        {
            if (volume > 1)
                volume = 1;
            if (volume < 0)
                volume = 0;

            List<ScenePresence> avatars = m_parentGroup.Scene.GetAvatars();
            foreach (ScenePresence avatar in avatars)
            {
                if ((!avatar.IsDeleted) && (!avatar.IsInTransit))
                    avatar.ControllingClient.SendAttachedSoundGainChange(UUID, (float)volume);
            }
        }

        /// <summary>
        /// hook to the physics scene to apply impulse
        /// This is sent up to the group, which then finds the root prim
        /// and applies the force on the root prim of the group
        /// </summary>
        /// <param name="impulse">Vector force</param>
        /// <param name="localGlobalTF">true for the local frame, false for the global frame</param>
        public void ApplyImpulse(Vector3 impulse, bool local)
        {
            if (m_parentGroup != null)
            {
                m_parentGroup.ApplyImpulse(impulse, local);
            }
        }


        /// <summary>
        /// hook to the physics scene to apply angular impulse
        /// This is sent up to the group, which then finds the root prim
        /// and applies the force on the root prim of the group
        /// </summary>
        /// <param name="impulsei">Vector force</param>
        /// <param name="localGlobalTF">true for the local frame, false for the global frame</param>
        public void ApplyAngularImpulse(Vector3 impulsei, bool local)
        {
            if (m_parentGroup != null)
            {
                m_parentGroup.ApplyAngularImpulse(impulsei, local);
            }
        }

        /// <summary>
        /// hook to the physics scene to apply angular impulse
        /// This is sent up to the group, which then finds the root prim
        /// and applies the force on the root prim of the group
        /// </summary>
        /// <param name="impulsei">Vector force</param>
        /// <param name="localGlobalTF">true for the local frame, false for the global frame</param>
        public void SetAngularImpulse(Vector3 impulsei, bool localGlobalTF)
        {
            OpenMetaverse.Vector3 impulse = new OpenMetaverse.Vector3(impulsei.X, impulsei.Y, impulsei.Z);

            if (localGlobalTF)
            {
                Quaternion grot = GetWorldRotation();
                Quaternion AXgrot = grot;
                Vector3 AXimpulsei = impulsei;
                Vector3 newimpulse = AXimpulsei * AXgrot;
                impulse = new OpenMetaverse.Vector3(newimpulse.X, newimpulse.Y, newimpulse.Z);
            }

            if (m_parentGroup != null)
            {
                m_parentGroup.setAngularImpulse(impulse);
            }
        }

        public Vector3 GetTorque()
        {
            if (m_parentGroup != null)
            {
                m_parentGroup.GetTorque();
            }
            return Vector3.Zero;
        }

        public SceneObjectPartPhysicsSummary PhysicsSummary
        {
            get
            {
                return SceneObjectPartPhysicsSummary.SummaryFromParams(Shape.FlexiEntry, IsAttachment, VolumeDetectActive, 
                    ((GetEffectiveObjectFlags() & PrimFlags.Physics) != 0), ((GetEffectiveObjectFlags() & PrimFlags.Phantom) != 0));
            }
        }

        /// <summary>
        /// Apply physics to this part. (should only be called from SceneObjectGroup)
        /// </summary>
        /// <param name="rootObjectFlags"></param>
        /// <param name="m_physicalPrim"></param>
        internal void ApplyPhysics(bool fromStorage)
        {
            SceneObjectPartPhysicsSummary phySummary = this.PhysicsSummary;

            // The only time the physics scene shouldn't know about the prim is if it's phantom or an attachment, which is phantom by definition
            if (phySummary.NeedsPhysicsShape)
            {
                AddPhysicsShapeAndHookToEvents(phySummary.NeedsDynamicActor, fromStorage);
            }
        }

        private void AddPhysicsShapeAndHookToEvents(bool physical, bool fromStorage)
        {
            PhysicsScene.AddPrimShapeFlags flags = this.GeneratePhysicsAddPrimShapeFlags(physical, fromStorage);

            PhysicsActor actor = m_parentGroup.Scene.PhysicsScene.AddPrimShape(
                Name + m_uuid,
                flags,
                this.GenerateBulkShapeData());

            if (_serializedPhysicsData == null)
            {
                this.RestorePrePhysicsTargetOmega();
            }

            //clean up
            _serializedPhysicsData = null;
            _serializedPhysicsShapes = null;
            m_serializedPhysicalAngularVelocity = Vector3.Zero;

            AttachToPhysicsShape(actor, false);
        }

        private void SetPhysActorRelationProperties()
        {
            //SOPNme/Desc don't matter anymore with physx. Retaining for possible
            //debug assistance
            PhysActor.SOPName = this.Name;
            PhysActor.SOPDescription = this.Description;
            PhysActor.LocalID = LocalId;
            PhysActor.Uuid = UUID;
        }

        public void AttachToPhysicsShape(PhysicsActor shape, bool isChild)
        {
            PhysActor = shape;
            SetPhysActorRelationProperties();

            if (!isChild)
            {
                PhysActor.OnRequestTerseUpdate -= this.PhysicsRequestingTerseUpdate;
                PhysActor.OnRequestTerseUpdate += this.PhysicsRequestingTerseUpdate;
                PhysActor.OnComplexityError -= new PhysicsActor.ComplexityError(PhysActor_OnComplexityError);
                PhysActor.OnComplexityError += new PhysicsActor.ComplexityError(PhysActor_OnComplexityError);
                PhysActor.OnPhysicsRequestingOBB -= PhysActor_OnPhysicsRequestingOBB;
                PhysActor.OnPhysicsRequestingOBB += PhysActor_OnPhysicsRequestingOBB;
            }

            PhysActor.OnNeedsPersistence -= new PhysicsActor.RequestPersistence(PhysActor_OnNeedsPersistence);
            PhysActor.OnNeedsPersistence += new PhysicsActor.RequestPersistence(PhysActor_OnNeedsPersistence);
            PhysActor.OnPositionUpdate -= new PositionUpdate(PhysActor_OnPositionUpdate);
            PhysActor.OnPositionUpdate += new PositionUpdate(PhysActor_OnPositionUpdate);

            this.CheckForScriptCollisionEventsAndSubscribe();
        }

        OpenSim.Framework.Geom.Box PhysActor_OnPhysicsRequestingOBB()
        {
            return this.ParentGroup.RelativeBoundingBox(false);
        }

        void PhysActor_OnComplexityError(string info)
        {
            IDialogModule dm = m_parentGroup.Scene.RequestModuleInterface<IDialogModule>();

            if (dm == null)
                return;

            dm.SendAlertToUser(this.OwnerID, "Object was too complex to be made physical: " + info);

            //disable the physics flag
            this.ParentGroup.RemGroupFlagValue(PrimFlags.Physics);
            this.ScheduleFullUpdate(PrimUpdateFlags.PrimFlags);
        }

        void PhysActor_OnPositionUpdate()
        {
            PhysicsActor physActor = PhysActor;
            if (physActor != null)
            {
                m_parentGroup.SetAbsolutePosition(physActor.Position, true);
            }
        }

        void PhysActor_OnNeedsPersistence()
        {
            this.ParentGroup.HasGroupChanged = true;
        }

        public void ClearUndoState()
        {
            lock (m_undoLock)
            {
                m_undo = new UndoStack<UndoState>(5);
                m_redo = new UndoStack<UndoState>(5);
            }
            StoreUndoState();
        }

        public byte ConvertScriptUintToByte(uint indata)
        {
            byte outdata = (byte)TextureAnimFlags.NONE;
            if ((indata & 1) != 0) outdata |= (byte)TextureAnimFlags.ANIM_ON;
            if ((indata & 2) != 0) outdata |= (byte)TextureAnimFlags.LOOP;
            if ((indata & 4) != 0) outdata |= (byte)TextureAnimFlags.REVERSE;
            if ((indata & 8) != 0) outdata |= (byte)TextureAnimFlags.PING_PONG;
            if ((indata & 16) != 0) outdata |= (byte)TextureAnimFlags.SMOOTH;
            if ((indata & 32) != 0) outdata |= (byte)TextureAnimFlags.ROTATE;
            if ((indata & 64) != 0) outdata |= (byte)TextureAnimFlags.SCALE;
            return outdata;
        }

        // part.ParentGroup must be initialized for this.
        public void CopySitTarget(SceneObjectPart part)
        {
            this.SetSitTarget(part.SitTargetActive, part.SitTargetPosition, part.SitTargetOrientation, false);
        }

        /// <summary>
        /// Duplicates this part.
        /// </summary>
        /// <returns></returns>
        public SceneObjectPart Copy(uint localID, UUID AgentID, UUID GroupID, int linkNum, bool userExposed, bool serializePhysicsState)
        {
            SceneObjectPart dupe = (SceneObjectPart)MemberwiseClone();
            dupe.m_parentGroup = null;
            dupe.m_undo = new UndoStack<UndoState>(5);
            dupe.m_redo = new UndoStack<UndoState>(5);
            dupe.m_shape = m_shape.Copy();
            dupe.m_regionHandle = m_regionHandle;
            dupe.PayPrice = (int[])PayPrice.Clone();
            if (userExposed)
            {
                dupe.UUID = UUID.Random();
            }

            //memberwiseclone means it also clones the physics actor reference
            //we dont want to steal our copies physactor
            dupe.PhysActor = null;

            dupe._ownerID = AgentID;
            dupe._groupID = GroupID;
            dupe.SetGroupPositionDirect(GroupPosition);
            dupe.SetOffsetPositionDirect(OffsetPosition);
            dupe.SetRotationOffsetDirect(RotationOffset);

            if (serializePhysicsState)
            {
                dupe.m_serializedVelocity = this.Velocity;//new Vector3(0, 0, 0);
                dupe.m_angularVelocity = this.AngularVelocity;//new Vector3(0, 0, 0);
                dupe.m_serializedPhysicalAngularVelocity = this.PhysicalAngularVelocity;
            }
            else
            {
                dupe.m_serializedVelocity = this.m_serializedVelocity;//new Vector3(0, 0, 0);
                dupe.m_angularVelocity = this.m_angularVelocity;//new Vector3(0, 0, 0);
                dupe.m_angularVelocity = this.m_serializedPhysicalAngularVelocity;
            }

            dupe.m_particleSystem = m_particleSystem;
            dupe.ObjectFlags = ObjectFlags;
            dupe.ServerFlags = ServerFlags;

            dupe._ownershipCost = _ownershipCost;
            dupe._objectSaleType = _objectSaleType;
            dupe._salePrice = _salePrice;
            dupe._category = _category;
            dupe.m_rezzed = m_rezzed;

            lock (TaskInventory)
            {
                dupe.m_inventory = dupe.m_inventory.CloneForPartCopy(dupe, userExposed);
            }

            if (userExposed)
            {
                dupe.ResetIDs(linkNum);
                dupe.m_inventory.HasInventoryChanged = true;
            }
            else
            {
                dupe.m_inventory.HasInventoryChanged = m_inventory.HasInventoryChanged;
            }

            // Move afterwards ResetIDs as it clears the localID
            dupe.LocalId = localID;

            dupe.LastOwnerID = _lastOwnerID;

            byte[] extraP = new byte[Shape.ExtraParams.Length];
            Array.Copy(Shape.ExtraParams, extraP, extraP.Length);
            dupe.Shape.ExtraParams = extraP;

            if (userExposed)
            {
                if (dupe.m_shape.SculptEntry && dupe.m_shape.SculptTexture != UUID.Zero)
                {
                    m_parentGroup.Scene.CommsManager.AssetCache.GetAsset(
                        dupe.m_shape.SculptTexture, dupe.SculptTextureCallback,
                        AssetRequestInfo.InternalRequest());
                }
            }

            if (serializePhysicsState)
            {
                dupe.SerializedPhysicsData = this.GetSerializedPhysicsProperties();
            }

            ParentGroup.Scene.EventManager.TriggerOnSceneObjectPartCopy(dupe, this, userExposed);

            return dupe;
        }

        /// <summary>
        /// Duplicates this part.
        /// </summary>
        /// <returns></returns>
        public SceneObjectPart Copy(uint localID, UUID AgentID, UUID GroupID, int linkNum, bool userExposed)
        {
            return this.Copy(localID, AgentID, GroupID, linkNum, userExposed, false);
        }

        public static SceneObjectPart Create()
        {
            SceneObjectPart part = new SceneObjectPart();
            part.UUID = UUID.Random();

            PrimitiveBaseShape shape = PrimitiveBaseShape.Create();
            part.Shape = shape;

            part.Name = "Primitive";
            part._ownerID = UUID.Random();

            return part;
        }

        /// <summary>
        /// Adjusts the dynamics setting for an existing physactor. Will not
        /// set up a new physactor if one does not exist
        /// </summary>
        /// <param name="useDynamics"></param>
        /// <param name="isNew"></param>
        public void AdjustPhysactorDynamics(bool useDynamics, bool isNew)
        {
            if (IsRootPart())
            {
                PhysicsActor physActor = PhysActor;
                if (physActor != null)
                {
                    if (useDynamics)
                    {
                        physActor.OnRequestTerseUpdate += this.PhysicsRequestingTerseUpdate;
                        PhysActor.OnNeedsPersistence += new PhysicsActor.RequestPersistence(PhysActor_OnNeedsPersistence);
                        PhysActor.OnComplexityError += new PhysicsActor.ComplexityError(PhysActor_OnComplexityError);
                        m_parentGroup.Scene.PhysicsScene.AddPhysicsActorTaint(physActor, TaintType.MadeDynamic);
                    }
                    else
                    {
                        physActor.OnRequestTerseUpdate -= this.PhysicsRequestingTerseUpdate;
                        PhysActor.OnNeedsPersistence -= new PhysicsActor.RequestPersistence(PhysActor_OnNeedsPersistence);
                        PhysActor.OnComplexityError -= new PhysicsActor.ComplexityError(PhysActor_OnComplexityError);
                        m_parentGroup.Scene.PhysicsScene.AddPhysicsActorTaint(physActor, TaintType.MadeStatic);
                    }
                }
            }
        }

        /// <summary>
        /// Restore this part from the serialized xml representation.
        /// </summary>
        /// <param name="xmlReader"></param>
        /// <returns></returns>
        public static SceneObjectPart FromXml(XmlReader xmlReader)
        {
            return FromXml(UUID.Zero, xmlReader);
        }

        /// <summary>
        /// Restore this part from the serialized xml representation.
        /// </summary>
        /// <param name="fromUserInventoryItemId">The inventory id from which this part came, if applicable</param>
        /// <param name="xmlReader"></param>
        /// <returns></returns>
        public static SceneObjectPart FromXml(UUID fromUserInventoryItemId, XmlReader xmlReader)
        {
            SceneObjectPart part = (SceneObjectPart)serializer.Deserialize(xmlReader);
            part.DoPostDeserializationCleanups(fromUserInventoryItemId);

            return part;
        }

        public void DoPostDeserializationCleanups(UUID fromUserInventoryItemId)
        {
            this.m_fromUserInventoryItemID = fromUserInventoryItemId;

            // for tempOnRez objects, we have to fix the Expire date.
            if ((this.Flags & PrimFlags.TemporaryOnRez) != 0) this.ResetExpire();
        }

        public bool GetDieAtEdge()
        {
            if (m_parentGroup == null)
                return false;
            if (m_parentGroup.IsDeleted)
                return false;

            return m_parentGroup.RootPart.DIE_AT_EDGE;
        }

        public bool GetBlockGrab()
        {
            if (m_parentGroup != null)
            {
                return m_parentGroup.GetBlockGrab();
            }

            return false;
        }

        public double GetDistanceTo(Vector3 a, Vector3 b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            float dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public PrimFlags GetEffectiveObjectFlags()
        {
            return _flags | LocalFlags;
        }

        public Vector3 GetGeometricCenter()
        {
            PhysicsActor physActor = PhysActor;
            if (physActor != null)
            {
                return new Vector3(physActor.CenterOfMass.X, physActor.CenterOfMass.Y, physActor.CenterOfMass.Z);
            }
            else
            {
                return new Vector3(0, 0, 0);
            }
        }

        public float GetMass()
        {
            // If the prim has a physics actor, get its mass.
            PhysicsActor physActor = PhysActor;
            if (physActor != null)
            {
                return physActor.Mass;
            }

            // If the prim is attached, use the avatar's mass.
            else if (IsAttachment)
            {
                if (m_parentGroup != null)
                {
                    return  m_parentGroup.GetAvatarMass();
                }
            }

            return 0;
        }

        public OpenMetaverse.Vector3 GetForce()
        {
            PhysicsActor physActor = PhysActor;
            if (physActor != null)
                return physActor.Force;
            else
                return new OpenMetaverse.Vector3();
        }

        public void GetProperties(IClientAPI client)
        {
            ulong micros = 1000000UL * (uint)_creationDate;
//            uint perms = ParentGroup.GetEffectivePermissions(false);    // folded ownermask of all prims
            // Permissions of an object match those of the root prim.
            // It's up to the Link operation to change child prims to match, and transfers to update all prims from Next.

            uint perms = ParentGroup.GetEffectivePermissions(false);
            uint nextperms = perms & ParentGroup.RootPart.NextOwnerMask;

            client.SendObjectPropertiesReply(
                m_fromUserInventoryItemID, micros, _creatorID, UUID.Zero, UUID.Zero,
                _groupID, (short)InventorySerial, _lastOwnerID, UUID, _ownerID,
                ParentGroup.RootPart.TouchName, new byte[0], ParentGroup.RootPart.SitName, Name, Description,
                perms & ParentGroup.RootPart._ownerMask,
                nextperms & ParentGroup.RootPart._nextOwnerMask,
                perms & ParentGroup.RootPart._groupMask,
                perms & ParentGroup.RootPart._everyoneMask,
                perms & ParentGroup.RootPart._baseMask,
                ParentGroup.GetEffectivePermissions(true),      // folded owner perms
                ParentGroup.GetEffectiveNextPermissions(true),  // folded next owner perms
                ParentGroup.RootPart.ObjectSaleType,
                ParentGroup.RootPart.SalePrice);
        }

        public UUID GetRootPartUUID()
        {
            if (m_parentGroup != null)
            {
                return m_parentGroup.UUID;
            }
            return UUID.Zero;
        }

        /// <summary>
        /// Method for a prim to get it's world position from the group.
        /// Remember, the Group Position simply gives the position of the group itself
        /// </summary>
        /// <returns>An object's absolute position in world</returns>
        public Vector3 GetWorldPosition()
        {
            if (IsRootPart())
            {
                if (PhysActor == null)
                {
                    if (IsAttachment)
                    {
                        return GetWearerPosition();
                    }
                    else
                    {
                        return m_groupPosition;
                    }
                }
                else
                {
                    return PhysActor.Position;
                }
            }
            else
            {
                Quaternion parentRot = ParentGroup.RootPart.RotationOffset;

                Vector3 axPos = OffsetPosition;
                axPos *= parentRot;
                Vector3 translationOffsetPosition = axPos;

                return GroupPosition + translationOffsetPosition;
            }
        }

        /// <summary>
        /// Method for a prim to get it's world position from the group, but returning
        /// a value corresponding to SL's nonsense values for attached child prims.
        /// </summary>
        /// <returns>A nonsense value as defined by LL.</returns>
        public Vector3 GetSLCompatiblePosition()
        {
            // If this is a child prim on an attachment, return the child prim offset applied to the avatar pos + rot.
            // This makes no sense but it's what SL does.
            if (IsRootPart() || (!IsAttachment))
                return GetWorldPosition();

            Vector3 wearerPos = Vector3.Zero;
            Quaternion wearerRot = Quaternion.Identity;

            // First find the wearer
            if (m_parentGroup == null || m_parentGroup.Scene == null)
                return GetWorldPosition();

            ScenePresence sp = m_parentGroup.Scene.GetScenePresence(AttachedAvatar);
            if (sp == null)
                return GetWorldPosition();

            wearerPos = sp.AbsolutePosition;
            wearerRot = sp.Rotation;
            return (OffsetPosition * wearerRot) + wearerPos;
        }

        private Vector3 GetWearerPosition()
        {
            if (m_parentGroup == null || m_parentGroup.Scene == null)
                return Vector3.Zero;

            ScenePresence sp = m_parentGroup.Scene.GetScenePresence(AttachedAvatar);
            if (sp == null)
                return Vector3.Zero;

            return sp.AbsolutePosition;
        }

        /// <summary>
        /// Gets the rotation of this prim offset by the group rotation
        /// </summary>
        /// <returns></returns>
        public Quaternion GetWorldRotation()
        {
            Quaternion newRot;

            if (IsRootPart())
            {
                if (IsAttachment)
                {
                    return GetWearerRotation();
                }
                else
                {
                    newRot = RotationOffset;
                }
            }
            else
            {
                Quaternion parentRot = ParentGroup.RootPart.RotationOffset;
                Quaternion oldRot = RotationOffset;
                newRot = parentRot * oldRot;
            }

            return newRot;
        }

        private Quaternion GetWearerRotation()
        {
            ScenePresence sp = m_parentGroup.Scene.GetScenePresence(AttachedAvatar);
            if (sp != null)
            {
                return sp.Rotation;
            }
            else
            {
                return Quaternion.Identity;
            }
        }

        public void MoveToTarget(Vector3 target, float tau)
        {
            if (tau > 0)
            {
                m_parentGroup.moveToTarget(target, tau);
            }
            else
            {
                StopMoveToTarget();
            }
        }

        /// <summary>
        /// Uses a PID to attempt to clamp the object on the Z axis at the given height over tau seconds.
        /// </summary>
        /// <param name="height">Height to hover.  Height of zero disables hover.</param>
        /// <param name="hoverType">Determines what the height is relative to </param>
        /// <param name="tau">Number of seconds over which to reach target</param>
        public void SetHoverHeight(float height, PIDHoverFlag hoverType, float tau)
        {
            m_parentGroup.SetHoverHeight(height, hoverType, tau);
        }

        public void StopHover()
        {
            m_parentGroup.SetHoverHeight(0f, PIDHoverFlag.None, 0f);
        }


        public virtual void OnGrab(Vector3 offsetPos, IClientAPI remoteClient)
        {
        }

        public void PhysicsCollision(EventArgs e)
        {
            CollisionEventUpdate update = (CollisionEventUpdate)e;

            switch (update.Type)
            {
                case CollisionEventUpdateType.CollisionBegan:
                    HandleCollisionBegan(update);
                    break;

                case CollisionEventUpdateType.CollisionEnded:
                    HandleCollisionEnded(update);
                    break;

                case CollisionEventUpdateType.CollisionContinues:
                    HandleCollisionContinues(update);
                    break;

                case CollisionEventUpdateType.BulkCollisionsContinue:
                    HandleBulkCollisionsContinue(update);
                    break;

                case CollisionEventUpdateType.BulkAvatarCollisionsContinue:
                    HandleBulkCollisionsContinue(update);
                    break;

                case CollisionEventUpdateType.LandCollisionBegan:
                    HandleLandCollisionBegan(update);
                    break;

                case CollisionEventUpdateType.LandCollisionEnded:
                    HandleLandCollisionEnded(update);
                    break;

                case CollisionEventUpdateType.LandCollisionContinues:
                    HandleLandCollisionContinues(update);
                    break;

                case CollisionEventUpdateType.CharacterCollisionBegan:
                    HandleCharacterCollisionBegan(update);
                    break;

                case CollisionEventUpdateType.CharacterCollisionEnded:
                    HandleCharacterCollisionEnded(update);
                    break;

                case CollisionEventUpdateType.CharacterCollisionContinues:
                    HandleCollisionContinues(update);
                    break;

                default:
                    m_log.ErrorFormat("[PHYSICS] Unhandled collision event: {0}", update.Type);
                    break;
            }
        }

        private void HandleCharacterCollisionEnded(CollisionEventUpdate update)
        {
            HandleGenericCollisionEvent(update, Scenes.ScriptEvents.collision_end,
                m_parentGroup.Scene.EventManager.TriggerScriptCollidingEnd, false);
        }

        private void HandleCharacterCollisionBegan(CollisionEventUpdate update)
        {
            HandleGenericCollisionEvent(update, Scenes.ScriptEvents.collision_start,
                m_parentGroup.Scene.EventManager.TriggerScriptCollidingStart, true);
        }

        private void HandleLandCollisionContinues(CollisionEventUpdate update)
        {
            HandleGenericLandCollisionEvent(update, Scenes.ScriptEvents.land_collision,
                m_parentGroup.Scene.EventManager.TriggerScriptLandColliding, false);
        }

        private void HandleLandCollisionEnded(CollisionEventUpdate update)
        {
            HandleGenericLandCollisionEvent(update, Scenes.ScriptEvents.land_collision_end,
                m_parentGroup.Scene.EventManager.TriggerScriptLandCollidingEnd, false);
        }

        private void HandleLandCollisionBegan(CollisionEventUpdate update)
        {
            HandleGenericLandCollisionEvent(update, Scenes.ScriptEvents.land_collision_start,
                m_parentGroup.Scene.EventManager.TriggerScriptLandCollidingStart, true);
        }

        private void HandleGenericLandCollisionEvent(CollisionEventUpdate update, Scenes.ScriptEvents eventType,
            EventManager.ScriptLandColliding callback, bool playSound)
        {
            // play the sound.
            if (playSound && CollisionSound != UUID.Zero && eventType == ScriptEvents.collision_start && CollisionSoundVolume > 0.0f)
            {
                SendSound(CollisionSound, CollisionSoundVolume, (byte)SoundFlags.None, true);
            }

            SceneObjectPart handlingPart = FindCollisionHandlingPart(eventType);
            if (handlingPart == null) return; //no one to handle the event

            if (m_parentGroup == null)
                return;
            if (m_parentGroup.Scene == null)
                return;

            callback(handlingPart.LocalId, update.CollisionLocation);
        }

        private enum ColliderBasicType
        {
            Agent,
            Object,
            Ground
        }

        private void HandleGenericCollisionEvent(CollisionEventUpdate update, Scenes.ScriptEvents eventType, EventManager.ScriptColliding callback,
            bool playSound)
        {
            // play the sound.
            if (playSound && CollisionSound != UUID.Zero && eventType == ScriptEvents.collision_start && CollisionSoundVolume > 0.0f)
            {
                SendSound(CollisionSound.ToString(), CollisionSoundVolume, true, (byte)0);
            }

            SceneObjectPart handlingPart = FindCollisionHandlingPart(eventType);
            if (handlingPart == null) return; //no one to handle the event

            ColliderArgs colliderArgs = new ColliderArgs();
            List<DetectedObject> colliding = new List<DetectedObject>();

            bool otherIsPrim;
            if (update.Type == CollisionEventUpdateType.CollisionBegan ||
                update.Type == CollisionEventUpdateType.CollisionContinues ||
                update.Type == CollisionEventUpdateType.BulkCollisionsContinue ||
                update.Type == CollisionEventUpdateType.CollisionEnded)
            {
                otherIsPrim = true;
            }
            else
            {
                otherIsPrim = false;
            }

            if (update.BulkCollisionData == null)
            {
                TryExtractCollider(update.OtherColliderLocalId, colliding, otherIsPrim, update.OtherColliderUUID);
            }
            else
            {
                foreach (uint localId in update.BulkCollisionData)
                {
                    TryExtractCollider(localId, colliding, otherIsPrim, update.OtherColliderUUID);
                }
            }
            

            if (colliding.Count > 0)
            {
                colliderArgs.Colliders = colliding;
                // always running this check because if the user deletes the object it would return a null reference.
                if (m_parentGroup == null)
                    return;
                if (m_parentGroup.Scene == null)
                    return;
                callback(handlingPart.LocalId, colliderArgs);
            }
        }

        /// <summary>
        /// Finds the part (this one or its parent or none) that should be handling collision events
        /// </summary>
        /// <param name="eventType"></param>
        /// <returns></returns>
        private SceneObjectPart FindCollisionHandlingPart(Scenes.ScriptEvents eventType)
        {
            SceneObjectPart handlingPart = null;

            //check this part first for the event, then if it doesnt exist here check the root
            PhysicsActor myPhyActor = this.PhysActor;
            if ((this.ScriptEvents & eventType) != 0 && 
                (IsAttachment || (myPhyActor != null && (this.IsRootPart() || !myPhyActor.Properties.PassCollisions))))
            {
                handlingPart = this;
            }
            else if (!this.IsRootPart())
            {
                //can the root handle it?
                SceneObjectPart parentPart = this.ParentGroup.RootPart;
                PhysicsActor parentPhyActor = parentPart.PhysActor;

                if ((parentPart.ScriptEvents & eventType) != 0 && parentPhyActor != null)
                {
                    handlingPart = parentPart;
                }
            }

            return handlingPart;
        }

        [Flags]
        enum DetectedType
        {
            None = 0,
            Agent = 1,
            Active = 2,
            Passive = 4,
            Scripted = 8
        }

        private void TryExtractCollider(uint localId, List<DetectedObject> colliding, bool isPrim, UUID fallbackUuid)
        {
            //TODO: this extracts a DetectedObject from the collision, BUT this is then further changed into a 
            //detectparams later making most of this information useless since the change will rediscover
            //the colliding object again. I have no idea why opensim did it this way.

            if (isPrim)
            {
                // always running this check because if the user deletes the object it would return a null reference.
                SceneObjectPart obj = m_parentGroup.Scene.GetSceneObjectPart(localId);

                if (obj != null)
                {
                    DetectedObject detobj = new DetectedObject();
                    detobj.keyUUID = obj.UUID;
                    detobj.nameStr = obj.Name;
                    detobj.ownerUUID = obj._ownerID;
                    detobj.posVector = obj.AbsolutePosition;
                    detobj.rotQuat = obj.GetWorldRotation();
                    detobj.velVector = obj.Velocity;
                    detobj.groupUUID = obj._groupID;
                    detobj.colliderType = (int)DetermineColliderType(obj);
                    detobj.linkNum = this.LinkNum;
                    colliding.Add(detobj);
                }
                else
                {
                    DetectedObject detobj = new DetectedObject();
                    detobj.keyUUID = fallbackUuid;
                    colliding.Add(detobj);
                }
            }
            else
            {
                ScenePresence presence = m_parentGroup.Scene.GetScenePresence(localId);

                if (presence != null)
                {
                    DetectedObject detobj = new DetectedObject();
                    detobj.keyUUID = presence.UUID;
                    detobj.nameStr = presence.Firstname + " " + presence.Lastname;
                    detobj.ownerUUID = presence.UUID;
                    detobj.posVector = presence.AbsolutePosition;
                    detobj.rotQuat = presence.Rotation;
                    detobj.velVector = presence.Velocity;
                    detobj.linkNum = this.LinkNum;
                    detobj.colliderType = (int)DetectedType.Agent | (int)(presence.Velocity != OpenMetaverse.Vector3.Zero ? DetectedType.Active : DetectedType.Passive);
                    colliding.Add(detobj);
                }
            }
        }

        private DetectedType DetermineColliderType(SceneObjectPart obj)
        {
            DetectedType type = DetectedType.None;

            if (obj.ParentGroup.GroupScriptEvents != Scenes.ScriptEvents.None || obj.ParentGroup.IsScripted)
            {
                type |= DetectedType.Scripted;
            }

            if (obj.Velocity == Vector3.Zero)
                type |= DetectedType.Passive;
            else
                type |= DetectedType.Active;

            return type;
        }

        private void HandleCollisionBegan(CollisionEventUpdate update)
        {
            this.HandleGenericCollisionEvent(update, Scenes.ScriptEvents.collision_start, m_parentGroup.Scene.EventManager.TriggerScriptCollidingStart, true);
        }

        private void HandleCollisionEnded(CollisionEventUpdate update)
        {
            this.HandleGenericCollisionEvent(update, Scenes.ScriptEvents.collision_end, m_parentGroup.Scene.EventManager.TriggerScriptCollidingEnd, false);
        }

        private void HandleCollisionContinues(CollisionEventUpdate update)
        {
            this.HandleGenericCollisionEvent(update, Scenes.ScriptEvents.collision, m_parentGroup.Scene.EventManager.TriggerScriptColliding, false);
        }

        private void HandleBulkCollisionsContinue(CollisionEventUpdate update)
        {
            HandleGenericCollisionEvent(update, Scenes.ScriptEvents.collision, m_parentGroup.Scene.EventManager.TriggerScriptColliding, false);
        }

        public void PhysicsOutOfBounds(OpenMetaverse.Vector3 pos)
        {
            m_log.Warn("[PHYSICS]: Physical Object went out of bounds.");
            RemFlag(PrimFlags.Physics);
            AdjustPhysactorDynamics(false, true);
        }

        public void PhysicsRequestingTerseUpdate()
        {
            ScheduleTerseUpdate();
        }

        public void PreloadSound(string sound)
        {
            // UUID ownerID = OwnerID;
            UUID objectID = UUID;
            UUID soundID = UUID.Zero;

            if (!UUID.TryParse(sound, out soundID))
            {
                //Trys to fetch sound id from prim's inventory.
                //Prim's inventory doesn't support non script items yet
                
                lock (TaskInventory)
                {
                    foreach (KeyValuePair<UUID, TaskInventoryItem> item in TaskInventory)
                    {
                        if (item.Value.Name == sound)
                        {
                            soundID = item.Value.AssetID;
                            break;
                        }
                    }
                }
            }

            List<ScenePresence> avatars = m_parentGroup.Scene.GetAvatars();
            foreach (ScenePresence avatar in avatars)
            {
                // TODO: some filtering by distance of avatar
                if ((!avatar.IsDeleted) && (!avatar.IsInTransit))
                    avatar.ControllingClient.SendPreLoadSound(objectID, objectID, soundID);
            }
        }

        public void RemFlag(PrimFlags flag)
        {
            // PrimFlags prevflag = Flags;
            if ((ObjectFlags & (uint) flag) != 0)
            {
                //m_log.Debug("Removing flag: " + ((PrimFlags)flag).ToString());
                _flags &= ~flag;
                if ((flag & PrimFlags.Scripted) != 0)
                    if(ParentGroup != null)
                        ParentGroup.RecalcScriptedStatus();
            }
            //m_log.Debug("prev: " + prevflag.ToString() + " curr: " + Flags.ToString());
            //ScheduleFullUpdate();
        }

        public void RemoveScriptEvents(UUID scriptid)
        {
            bool hadScript = false;
            lock (m_scriptEvents)
            {
                if (m_scriptEvents.ContainsKey(scriptid))
                {
                    ScriptEvents oldparts = ScriptEvents.None;
                    oldparts = (ScriptEvents) m_scriptEvents[scriptid];

                    // remove values from aggregated script events
                    AggregateScriptEvents &= ~oldparts;
                    m_scriptEvents.Remove(scriptid);
                    hadScript = true;
                }
            }

            if (hadScript)
            {
                DoAggregateScriptEvents();
            }
        }

        public void ResetInstance(bool isNewInstance, bool isScriptReset, UUID itemId)
        {
            // Validated indirectly through EventManager.TriggerScriptReset when requested from a viewer,
            // or called directly from llResetScript and other internal code.

            if (isScriptReset)
            {
                this.Inventory.ResetItems(isNewInstance, isScriptReset, itemId);
            }

            if (isNewInstance)
            {
                this.m_seatedAvatars.Clear();
            }
        }

        /// <summary>
        /// Reset UUIDs for this part.  This involves generate this part's own UUID and
        /// generating new UUIDs for all the items in the inventory.
        /// </summary>
        /// <param name="linkNum">Link number for the part</param>
        public void ResetIDs(int linkNum)
        {
            UUID = UUID.Random();
            LinkNum = linkNum;
            LocalId = 0;
            Inventory.ResetInventoryIDs();
        }

        /// <summary>
        /// Resize this part.
        /// </summary>
        /// <param name="scale"></param>
        public void Resize(Vector3 scale)
        {
            Scale = scale;

            ParentGroup.HasGroupChanged = true;
            if (m_parentGroup != null) m_parentGroup.ClearBoundingBoxCache();
            ScheduleFullUpdate(PrimUpdateFlags.Shape);
        }

        /// <summary>
        /// Schedules this prim for a full update
        /// </summary>
        public void ScheduleFullUpdate(PrimUpdateFlags updateFlags)
        {
            if (_isInTransaction) return;

            if (m_parentGroup != null)
            {
                if (m_parentGroup.Scene == null)
                    return; // no updates unless the object is at least in the scene

                m_parentGroup.QueueForUpdateCheck(this, UpdateLevel.Full, updateFlags);
            }

            Interlocked.Increment(ref FullUpdateCounter);

            //            m_log.DebugFormat(
            //                "[SCENE OBJECT PART]: Scheduling full  update for {0}, {1} at {2}",
            //                UUID, Name, TimeStampFull);
        }

        /// <summary>
        /// Schedule a terse update for this prim.  Terse updates only send position,
        /// rotation, velocity, rotational velocity and shape information.
        /// </summary>
        public void ScheduleTerseUpdate()
        {
            if (_isInTransaction) return;

            if (m_parentGroup != null)
            {
                if (m_parentGroup.Scene == null)
                    return; // no updates unless the object is at least in the scene

                m_parentGroup.QueueForUpdateCheck(this, UpdateLevel.Terse, PrimUpdateFlags.TerseUpdate);
                m_parentGroup.HasGroupChanged = true;
            }

            Interlocked.Increment(ref TerseUpdateCounter);
        }

        public void ScriptSetPhantomStatus(bool Phantom)
        {
            if (m_parentGroup != null)
            {
                m_parentGroup.ScriptSetPhantomStatus(Phantom);
            }
        }

        public void ScriptSetTemporaryStatus(bool Temporary)
        {
            if (m_parentGroup != null)
            {
                m_parentGroup.ScriptSetTemporaryStatus(Temporary);
            }
        }

        public void ScriptSetPhysicsStatus(bool UsePhysics)
        {    
            if (m_parentGroup == null)
                AdjustPhysactorDynamics(UsePhysics, false);
            else
                m_parentGroup.ScriptSetPhysicsStatus(UsePhysics);
        }

        public void ScriptSetVolumeDetect(bool SetVD)
        {
            if (m_parentGroup != null)
            {
                m_parentGroup.ScriptSetVolumeDetect(SetVD);
            }
        }


        public void SculptTextureCallback(UUID textureID, AssetBase texture)
        {
            if (m_shape.SculptEntry)
            {
                if (texture != null)
                {
                    m_shape.SculptData = texture.Data;

                    PhysicsActor physActor = PhysActor;
                    if (physActor != null)
                    {
                        m_parentGroup.Scene.PhysicsScene.AddPhysicsActorTaint(physActor, TaintType.ChangedShape);
                    }
                }
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="remoteClient"></param>
        public void SendFullUpdate(IClientAPI remoteClient, uint clientFlags, PrimUpdateFlags updateFlags)
        {
            if (m_parentGroup.InTransit)
                return;
            if (m_parentGroup.IsDeleted)
                return;

            m_parentGroup.SendPartFullUpdate(remoteClient, this, clientFlags, updateFlags);
        }

        /// <summary>
        ///
        /// </summary>
        public void SendFullUpdateToAllClients(IEnumerable<ScenePresence> avatars, PrimUpdateFlags updateFlags)
        {
            if (m_parentGroup.InTransit)
                return;
            if (m_parentGroup.IsDeleted)
                return;

            //List<ScenePresence> avatars = m_parentGroup.Scene.GetScenePresences();
            foreach (ScenePresence avatar in avatars)
            {
                if (!(avatar.IsDeleted || avatar.IsInTransit))
                    m_parentGroup.SendPartFullUpdate(avatar.ControllingClient, this, avatar.GenerateClientFlags(UUID), updateFlags);
            }
        }

        public void SendFullUpdateToAllClientsExcept(UUID agentID, PrimUpdateFlags updateFlags)
        {
            if (m_parentGroup.InTransit)
                return;
            if (m_parentGroup.IsDeleted)
                return;

            List<ScenePresence> avatars = m_parentGroup.Scene.GetScenePresences();
            foreach (ScenePresence avatar in avatars)
            {
                if (avatar.UUID != agentID)
                    if (!(avatar.IsDeleted || avatar.IsInTransit))
                        m_parentGroup.SendPartFullUpdate(avatar.ControllingClient, this, avatar.GenerateClientFlags(UUID), updateFlags);
            }
        }

        /// <summary>
        /// Sends a full update to the client
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="clientFlags"></param>
        public void SendFullUpdateToClient(IClientAPI remoteClient, uint clientflags, PrimUpdateFlags updateFlags)
        {
            if (m_parentGroup.InTransit)
                return;
            if (m_parentGroup.IsDeleted)
                return;

            Vector3 lPos;
            lPos = OffsetPosition;
            SendFullUpdateToClient(remoteClient, lPos, clientflags, updateFlags);
        }

        /// <summary>
        /// Sends a full update to the client
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="lPos"></param>
        /// <param name="clientFlags"></param>
        /// <param name="updateFlags">The properties that were changed that caused this update</param>
        public void SendFullUpdateToClient(IClientAPI remoteClient, Vector3 lPos,
            uint clientFlags, PrimUpdateFlags updateFlags)
        {
            if (m_parentGroup.IsDeleted)
                return;
            if (m_parentGroup.InTransit)
                return;

            clientFlags &= ~(uint) PrimFlags.CreateSelected;
            if ((uint)(_flags & PrimFlags.Scripted) != 0)
                clientFlags |= (uint)PrimFlags.Scripted;
            else
                clientFlags &= ~(uint)PrimFlags.Scripted;

            if (remoteClient.AgentId == _ownerID)
            {
                if ((uint)(_flags & PrimFlags.CreateSelected) != 0)
                {
                    clientFlags |= (uint)PrimFlags.CreateSelected;
                    _flags &= ~PrimFlags.CreateSelected;
                }
            }
            else
            {   // Someone else's object
                // If it's worn as a HUD, don't send this no matter what
                if (ParentGroup.IsAttachedHUD)
                    return;
            }

            remoteClient.SendPrimitiveToClient(this, clientFlags, lPos, updateFlags);
        }

        public void SendFullUpdateToClientImmediate(IClientAPI remoteClient, Vector3 lPos, uint clientFlags)
        {
            clientFlags &= ~(uint)PrimFlags.CreateSelected;
            if ((uint)(_flags & PrimFlags.Scripted) != 0)
                clientFlags |= (uint)PrimFlags.Scripted;
            else
                clientFlags &= ~(uint)PrimFlags.Scripted;

            if (remoteClient.AgentId == _ownerID)
            {
                if ((uint)(_flags & PrimFlags.CreateSelected) != 0)
                {
                    clientFlags |= (uint)PrimFlags.CreateSelected;
                    _flags &= ~PrimFlags.CreateSelected;
                }
            }
            else
            {   // Someone else's object
                // If it's worn as a HUD, don't send this no matter what
                if (ParentGroup.IsAttachedHUD)
                    return;
            }

            remoteClient.SendPrimitiveToClientImmediate(this, clientFlags, lPos);
        }

        /// <summary>
        /// Tell all the prims which have had updates scheduled
        /// </summary>
        public void SendScheduledUpdates(UpdateLevel level, PrimUpdateFlags updateFlags)
        {
            switch (level)
            {
                case UpdateLevel.Terse:
                    AddTerseUpdateToAllAvatars();
                    break;

                /*case UpdateLevel.Compressed:
                    AddCompressedUpdateToAllAvatars();
                    break;*/

                case UpdateLevel.Full:
                    AddFullUpdateToAllAvatars(updateFlags);
                    break;
            }
        }

        /// <summary>
        /// Trigger or play an attached sound in this part's inventory.
        /// </summary>
        /// <param name="sound"></param>
        /// <param name="volume"></param>
        /// <param name="triggered"></param>
        /// <param name="flags"></param>
        public void SendSound(UUID soundID, float volume, byte flags, bool triggered)
        {
            if (volume > 1)
                volume = 1;
            if (volume < 0)
                volume = 0;

            Vector3 position = AbsolutePosition; // region local
            UUID parentID = GetRootPartUUID();
            if (parentID == UUID)
                parentID = UUID.Zero;   // SL sends a null key for sounds from the root prim

            ISoundModule soundModule = m_parentGroup.Scene.RequestModuleInterface<ISoundModule>();
            if (soundModule != null)
            {
                if (triggered)
                    soundModule.TriggerSound(soundID, _ownerID, UUID, parentID, volume, position, m_parentGroup.Scene.RegionInfo.RegionHandle);
                else
                    soundModule.PlayAttachedSound(soundID, _ownerID, UUID, volume, position, flags);
            }
        }

        /// <summary>
        /// Trigger or play an attached sound in this part's inventory.
        /// </summary>
        /// <param name="sound"></param>
        /// <param name="volume"></param>
        /// <param name="triggered"></param>
        /// <param name="flags"></param>
        public void SendSound(string sound, float volume, bool triggered, byte flags)
        {
            UUID soundID = UUID.Zero;

            if (!UUID.TryParse(sound, out soundID))
            {
                // search sound file from inventory
                lock (TaskInventory)
                {
                    foreach (KeyValuePair<UUID, TaskInventoryItem> item in TaskInventory)
                    {
                        if (item.Value.Name == sound && item.Value.Type == (int)AssetType.Sound)
                        {
                            soundID = item.Value.ItemID;
                            break;
                        }
                    }
                }
            }

            SendSound(soundID, volume, flags, triggered);
        }

        /// <summary>
        /// Update the sound attached to this part.
        /// </summary>
        /// <param name="sound"></param>
        /// <param name="volume"></param>
        /// <param name="flags"></param>
        public void UpdateSound(UUID soundID, float volume, SoundFlags flags)
        {
            if (volume > 1)
                volume = 1;
            if (volume < 0)
                volume = 0;

            // First, figure out what we should send for a sound.
            byte newFlags = (byte)SoundOptions;
            // Clear all of the flags except the Queue flag.
            newFlags &= (byte)SoundFlags.Queue;
            // Add the specified new flags from the parameter.
            newFlags |= (byte)flags;
            SendSound(soundID, volume, newFlags, false);

            // Next figure out what should persist in the prim.
            if (flags == SoundFlags.Loop)
                Sound = soundID;
            else
                Sound = UUID.Zero;
            SoundGain = volume;
            SoundOptions = newFlags;
            ParentGroup.HasGroupChanged = true;
            // v1 viewers seem to require the update in order to stop playing the sound.
            if (Sound == UUID.Zero)
                ScheduleFullUpdate(PrimUpdateFlags.Sound);
        }

        /// <summary>
        /// Send a terse update to all clients
        /// </summary>
        public void SendTerseUpdateToAllClients(IEnumerable<ScenePresence> avatars)
        {
            foreach (ScenePresence avatar in avatars)
            {
                if (!(avatar.IsDeleted || avatar.IsInTransit))
                    SendTerseUpdateToClient(avatar.ControllingClient);
            }
        }

        public void SetAttachmentPoint(uint NewAttachmentPoint)
        {
            if (m_shape.State != NewAttachmentPoint)
            {
                // attachment point has changed
                if (NewAttachmentPoint == 0)
                {
                    // we are clearing the attachment info
                    // save the previous attachment point
                    SavedAttachmentPoint = m_shape.State;
                    // Don't update SavedAttachmentPos from AbsolutePosition,
                    // since it has already been changed to an non-attached (abs) pos.
                }
                else
                {
                    // we are attaching it in some form
                    if (m_shape.State != 0)
                    {
                        // changing attachment locations
                        SavedAttachmentPoint = (byte)NewAttachmentPoint;
                        SavedAttachmentPos = Vector3.Zero;
                        SavedAttachmentRot = Quaternion.Identity;
                    }
                }
            }
            m_shape.State = (byte)NewAttachmentPoint;
        }

        public byte GetBestAttachmentPoint()
        {
            byte attached = AttachmentPoint;

            // If not currently attached, returns the previous attachment point.
            if (Shape.State == (byte)0)
            {
                Shape.State = SavedAttachmentPoint;
            }
            return Shape.State;
        }

        // Returns the previous position of an attachment, if you ask for the previous attachment point.
        public Vector3 GetSavedAttachmentPos(byte attachment)
        {
            if (attachment == SavedAttachmentPoint)
                return SavedAttachmentPos;
            return Vector3.Zero;
        }

        public void AddSeatedAvatar(ScenePresence sp, bool sendEvent)
        {
            m_seatedAvatars.AddAvatar(sp);
            if (ParentGroup != null)
                ParentGroup.AddSeatedAvatar(this.UUID, sp, sendEvent);    // event sent if needed
        }

        public void RemoveSeatedAvatar(ScenePresence sp, bool sendEvent)
        {
            if (ParentGroup != null)
                ParentGroup.RemoveSeatedAvatar(this.UUID, sp, sendEvent); // event sent if needed
            m_seatedAvatars.RemoveAvatar(sp);
        }

        // Called the same way on both old and new parent part.
        public void ReparentSeatedAvatar(ScenePresence sp, SceneObjectPart newParent)
        {
            if (newParent == this)
                m_seatedAvatars.AddAvatar(sp);
            else
                m_seatedAvatars.RemoveAvatar(sp);
        }

        public void SetBuoyancy(float fvalue)
        {
            PhysicsActor physActor = PhysActor;
            if (physActor != null)
            {
                physActor.Buoyancy = fvalue;
            }
        }

        public void SetDieAtEdge(bool p)
        {
            if (m_parentGroup == null)
                return;
            if (m_parentGroup.IsDeleted)
                return;

            m_parentGroup.RootPart.DIE_AT_EDGE = p;
        }

        public void SetBlockGrab(bool grab)
        {
            if (m_parentGroup != null)
            {
                m_parentGroup.SetBlockGrab(grab);
            }
        }

        public void SetFloatOnWater(int floatYN)
        {
            PhysicsActor physActor = PhysActor;
            if (physActor != null)
            {
                if (floatYN == 1)
                {
                    physActor.FloatOnWater = true;
                }
                else
                {
                    physActor.FloatOnWater = false;
                }
            }
        }

        /// <summary>
        /// hook to the physics scene to apply force
        /// This is sent up to the group, which then finds the root prim
        /// and applies the force on the root prim of the group
        /// </summary>
        /// <param name="force">Vector force</param>
        /// <param name="localGlobalTF">true for the local frame, false for the global frame</param>
        public void SetForce(OpenMetaverse.Vector3 force, bool local)
        {
            if (m_parentGroup != null)
            {
                m_parentGroup.SetForce(force, local);
            }
        }

        public void SetVehicleType(Physics.Manager.Vehicle.VehicleType type)
        {
            PhysicsActor physActor = PhysActor;
            if (physActor != null)
            {
                physActor.SetVehicleType(type);
            }
        }

        public void SetVehicleFloatParam(Physics.Manager.Vehicle.FloatParams param, float value)
        {
            PhysicsActor physActor = PhysActor;
            if (physActor != null)
            {
                physActor.SetVehicleFloatParam(param, value);
            }
        }

        public void SetVehicleVectorParam(Physics.Manager.Vehicle.VectorParams param, OpenMetaverse.Vector3 value)
        {
            PhysicsActor physActor = PhysActor;
            if (physActor != null)
            {
                physActor.SetVehicleVectorParam(param, value);
            }
        }

        public void SetVehicleRotationParam(Physics.Manager.Vehicle.RotationParams param, Quaternion rotation)
        {
            PhysicsActor physActor = PhysActor;
            if (physActor != null)
            {
                physActor.SetVehicleRotationParam(param, rotation);
            }
        }

        public void SetVehicleFlags(Physics.Manager.Vehicle.VehicleFlags vehicleFlags)
        {
            PhysicsActor physActor = PhysActor;
            if (physActor != null)
            {
                physActor.SetVehicleFlags(vehicleFlags);
            }
        }

        public void RemoveVehicleFlags(Physics.Manager.Vehicle.VehicleFlags vehicleFlags)
        {
            PhysicsActor physActor = PhysActor;
            if (physActor != null)
            {
                physActor.RemoveVehicleFlags(vehicleFlags);
            }
        }


        /// <summary>
        /// Get the number of sides that this part has.
        /// </summary>
        /// <returns></returns>
        public int GetNumberOfSides()
        {
            int ret = 0;
            bool hasCut;
            bool hasHollow;
            bool hasDimple;
            bool hasProfileCut;

            PrimType primType = GetPrimType();
            HasCutHollowDimpleProfileCut(primType, Shape, out hasCut, out hasHollow, out hasDimple, out hasProfileCut);

            switch (primType)
            {
                case PrimType.BOX:
                    ret = 6;
                    if (hasCut) ret += 2;
                    if (hasHollow) ret += 1;
                    break;
                case PrimType.CYLINDER:
                    ret = 3;
                    if (hasCut) ret += 2;
                    if (hasHollow) ret += 1;
                    break;
                case PrimType.PRISM:
                    ret = 5;
                    if (hasCut) ret += 2;
                    if (hasHollow) ret += 1;
                    break;
                case PrimType.SPHERE:
                    ret = 1;
                    if (hasCut) ret += 2;
                    if (hasDimple) ret += 2;
                    if (hasHollow) ret += 1;
                    break;
                case PrimType.TORUS:
                    ret = 1;
                    if (hasCut) ret += 2;
                    if (hasProfileCut) ret += 2;
                    if (hasHollow) ret += 1;
                    break;
                case PrimType.TUBE:
                    ret = 4;
                    if (hasCut) ret += 2;
                    if (hasProfileCut) ret += 2;
                    if (hasHollow) ret += 1;
                    break;
                case PrimType.RING:
                    ret = 3;
                    if (hasCut) ret += 2;
                    if (hasProfileCut) ret += 2;
                    if (hasHollow) ret += 1;
                    break;
                case PrimType.SCULPT:
                    // Special mesh handling
                    if (Shape.SculptType == (byte)SculptType.Mesh)
                        ret = 8; // if it's a mesh then max 8 faces
                    else
                        ret = 1; // if it's a sculpt then max 1 face
                    break;
            }

            return ret;
        }

        /// <summary>
        /// Tell us what type this prim is
        /// </summary>
        /// <param name="primShape"></param>
        /// <returns></returns>
        public PrimType GetPrimType()
        {
            if (Shape.SculptEntry)
                return PrimType.SCULPT;

            if ((Shape.ProfileCurve & 0x07) == (byte)ProfileShape.Square)
            {
                if (Shape.PathCurve == (byte)Extrusion.Straight)
                    return PrimType.BOX;
                else if (Shape.PathCurve == (byte)Extrusion.Curve1)
                    return PrimType.TUBE;
            }
            else if ((Shape.ProfileCurve & 0x07) == (byte)ProfileShape.Circle)
            {
                if (Shape.PathCurve == (byte)Extrusion.Straight)
                    return PrimType.CYLINDER;
                // ProfileCurve seems to combine hole shape and profile curve so we need to only compare against the lower 3 bits
                else if (Shape.PathCurve == (byte)Extrusion.Curve1)
                    return PrimType.TORUS;
            }
            else if ((Shape.ProfileCurve & 0x07) == (byte)ProfileShape.HalfCircle)
            {
                if (Shape.PathCurve == (byte)Extrusion.Curve1 || Shape.PathCurve == (byte)Extrusion.Curve2)
                    return PrimType.SPHERE;
            }
            else if ((Shape.ProfileCurve & 0x07) == (byte)ProfileShape.EquilateralTriangle)
            {
                if (Shape.PathCurve == (byte)Extrusion.Straight)
                    return PrimType.PRISM;
                else if (Shape.PathCurve == (byte)Extrusion.Curve1)
                    return PrimType.RING;
            }

            return PrimType.BOX;
        }

        /// <summary>
        /// Tell us if this object has cut, hollow, dimple, and other factors affecting the number of faces 
        /// </summary>
        /// <param name="primType"></param>
        /// <param name="shape"></param>
        /// <param name="hasCut"></param>
        /// <param name="hasHollow"></param>
        /// <param name="hasDimple"></param>
        /// <param name="hasProfileCut"></param>
        protected static void HasCutHollowDimpleProfileCut(PrimType primType, PrimitiveBaseShape shape, out bool hasCut, out bool hasHollow,
            out bool hasDimple, out bool hasProfileCut)
        {
            if (primType == PrimType.BOX || primType == PrimType.CYLINDER || primType == PrimType.PRISM)
                hasCut = (shape.ProfileBegin > 0) || (shape.ProfileEnd > 0);
            else
                hasCut = (shape.PathBegin > 0) || (shape.PathEnd > 0);

            if (primType == PrimType.TORUS || primType == PrimType.RING || primType == PrimType.TUBE)
            {
                if ((shape.PathTaperX != 0) || (shape.PathTaperY != 0) || 
                         (shape.PathTwistBegin != shape.PathTwist) || (shape.PathSkew != 0) || 
                         (shape.PathRadiusOffset != 0) || (shape.PathRevolutions > 1))
                    hasCut = true;
            }

            hasHollow = shape.ProfileHollow > 0;
            hasDimple = (shape.ProfileBegin > 0) || (shape.ProfileEnd > 0); // taken from llSetPrimitiveParms
            hasProfileCut = hasDimple; // is it the same thing?
        }



        public void SetGroup(UUID groupID, IClientAPI client)
        {
            _groupID = groupID;
            if (client != null)
                GetProperties(client);
            ScheduleFullUpdate(PrimUpdateFlags.ForcedFullUpdate);
        }

        /// <summary>
        /// Returns true if the parent has changed.
        /// </summary>
        public bool SetParent(SceneObjectGroup parent)
        {
            bool hasChanged = (m_parentGroup != parent);
            m_parentGroup = parent;
            return hasChanged;
        }

        public void SetParentAndUpdatePhysics(SceneObjectGroup parent)
        {
            this.SetParent(parent);

            PhysicsActor physActor = PhysActor;
            if (physActor != null)
            {
                //does the parent have a physactor?
                PhysicsActor parentPhysactor = parent.RootPart.PhysActor;

                if (parentPhysactor != null)
                {
                    physActor.LinkToNewParent(parent.RootPart.PhysActor, this.OffsetPosition, this.RotationOffset);
                }
                else
                {
                    //we're linking a non-phantom prim to a phantom parent, phantomize this prim as well
                    //NOTE: Don't use UpdatePrimFlags here, it only works for roots, and this is not a root 
                    //any longer
                    this.AddFlag(PrimFlags.Phantom);
                    m_parentGroup.Scene.PhysicsScene.RemovePrim(physActor);
                    PhysActor = null;
                }
            }
        }

        // Use this for attachments!  LocalID should be avatar's localid
        public void SetParentLocalId(uint localID)
        {
            _parentID = localID;
        }

        public void SetScriptEvents(UUID scriptid, int events)
        {
            // scriptEvents oldparts;
            lock (m_scriptEvents)
            {
                if (m_scriptEvents.ContainsKey(scriptid))
                {
                    // oldparts = m_scriptEvents[scriptid];

                    // remove values from aggregated script events
                    if (m_scriptEvents[scriptid] == (ScriptEvents) events)
                        return;
                    m_scriptEvents[scriptid] = (ScriptEvents) events;
                }
                else
                {
                    m_scriptEvents.Add(scriptid, (ScriptEvents) events);
                }
            }
            DoAggregateScriptEvents();
        }

        /// <summary>
        /// Set the text displayed for this part.
        /// </summary>
        /// <param name="text"></param>
        public void SetText(string text)
        {
            Text = text;

            ParentGroup.HasGroupChanged = true;
            ScheduleFullUpdate(PrimUpdateFlags.Text);
        }

        /// <summary>
        /// Set the text displayed for this part.
        /// </summary>
        /// <param name="text"></param>
        /// <param name="color"></param>
        /// <param name="alpha"></param>
        public void SetText(string text, Vector3 color, double alpha)
        {
            TextColor = Color.FromArgb(0xff - (int) (alpha*0xff),
                                   (int) (color.X*0xff),
                                   (int) (color.Y*0xff),
                                   (int) (color.Z*0xff));
            SetText(text);
        }

        public void StopMoveToTarget()
        {
            m_parentGroup.stopMoveToTarget();

            m_parentGroup.ScheduleGroupForTerseUpdate();
            //m_parentGroup.ScheduleGroupForFullUpdate();
        }

        public void StoreUndoState()
        {
            if (!Undoing)
            {
                if (!IgnoreUndoUpdate)
                {
                    if (m_parentGroup != null)
                    {
                        lock (m_undoLock)
                        {
                            if (m_undo.Count > 0)
                            {
                                UndoState last = m_undo.Peek();
                                if (last != null)
                                {
                                    if (last.Compare(this))
                                        return;
                                }
                            }

                            if (m_parentGroup.GetSceneMaxUndo() > 0)
                            {
                                UndoState nUndo = new UndoState(this);
                                m_undo.Push(nUndo);
                                m_redo.Clear();
                            }
                        }
                    }
                }
            }
        }

        public EntityIntersection TestIntersection(Ray iray, Quaternion parentrot)
        {
            // In this case we're using a sphere with a radius of the largest dimention of the prim
            // TODO: Change to take shape into account


            EntityIntersection returnresult = new EntityIntersection();
            Vector3 vAbsolutePosition = AbsolutePosition;
            Vector3 vScale = Scale;
            Vector3 rOrigin = iray.Origin;
            Vector3 rDirection = iray.Direction;

            //rDirection = rDirection.Normalize();
            // Buidling the first part of the Quadratic equation
            Vector3 r2ndDirection = rDirection*rDirection;
            float itestPart1 = r2ndDirection.X + r2ndDirection.Y + r2ndDirection.Z;

            // Buidling the second part of the Quadratic equation
            Vector3 tmVal2 = rOrigin - vAbsolutePosition;
            Vector3 r2Direction = rDirection*2.0f;
            Vector3 tmVal3 = r2Direction*tmVal2;

            float itestPart2 = tmVal3.X + tmVal3.Y + tmVal3.Z;

            // Buidling the third part of the Quadratic equation
            Vector3 tmVal4 = rOrigin*rOrigin;
            Vector3 tmVal5 = vAbsolutePosition*vAbsolutePosition;

            Vector3 tmVal6 = vAbsolutePosition*rOrigin;


            // Set Radius to the largest dimention of the prim
            float radius = 0f;
            if (vScale.X > radius)
                radius = vScale.X;
            if (vScale.Y > radius)
                radius = vScale.Y;
            if (vScale.Z > radius)
                radius = vScale.Z;

            // the second part of this is the default prim size
            // once we factor in the aabb of the prim we're adding we can
            // change this to;
            // radius = (radius / 2) - 0.01f;
            //
            radius = (radius / 2) + (0.5f / 2) - 0.1f;

            //radius = radius;

            float itestPart3 = tmVal4.X + tmVal4.Y + tmVal4.Z + tmVal5.X + tmVal5.Y + tmVal5.Z -
                               (2.0f*(tmVal6.X + tmVal6.Y + tmVal6.Z + (radius*radius)));

            // Yuk Quadradrics..    Solve first
            float rootsqr = (itestPart2*itestPart2) - (4.0f*itestPart1*itestPart3);
            if (rootsqr < 0.0f)
            {
                // No intersection
                return returnresult;
            }
            float root = ((-itestPart2) - (float) Math.Sqrt((double) rootsqr))/(itestPart1*2.0f);

            if (root < 0.0f)
            {
                // perform second quadratic root solution
                root = ((-itestPart2) + (float) Math.Sqrt((double) rootsqr))/(itestPart1*2.0f);

                // is there any intersection?
                if (root < 0.0f)
                {
                    // nope, no intersection
                    return returnresult;
                }
            }

            // We got an intersection.  putting together an EntityIntersection object with the
            // intersection information
            Vector3 ipoint =
                new Vector3(iray.Origin.X + (iray.Direction.X*root), iray.Origin.Y + (iray.Direction.Y*root),
                            iray.Origin.Z + (iray.Direction.Z*root));

            returnresult.HitTF = true;
            returnresult.ipoint = ipoint;

            // Normal is calculated by the difference and then normalizing the result
            Vector3 normalpart = ipoint - vAbsolutePosition;
            returnresult.normal = normalpart / normalpart.Length();

            // It's funny how the Vector3 object has a Distance function, but the Axiom.Math object doesn't.
            // I can write a function to do it..    but I like the fact that this one is Static.

            Vector3 distanceConvert1 = new Vector3(iray.Origin.X, iray.Origin.Y, iray.Origin.Z);
            Vector3 distanceConvert2 = new Vector3(ipoint.X, ipoint.Y, ipoint.Z);
            float distance = (float) Util.GetDistanceTo(distanceConvert1, distanceConvert2);

            returnresult.distance = distance;

            return returnresult;
        }

        public EntityIntersection TestIntersectionOBB(Ray iray, Quaternion parentrot, bool frontFacesOnly, bool faceCenters)
        {
            // In this case we're using a rectangular prism, which has 6 faces and therefore 6 planes
            // This breaks down into the ray---> plane equation.
            // TODO: Change to take shape into account
            Vector3[] vertexes = new Vector3[8];

            // float[] distance = new float[6];
            Vector3[] FaceA = new Vector3[6]; // vertex A for Facei
            Vector3[] FaceB = new Vector3[6]; // vertex B for Facei
            Vector3[] FaceC = new Vector3[6]; // vertex C for Facei
            Vector3[] FaceD = new Vector3[6]; // vertex D for Facei

            Vector3[] normals = new Vector3[6]; // Normal for Facei
            Vector3[] AAfacenormals = new Vector3[6]; // Axis Aligned face normals

            AAfacenormals[0] = new Vector3(1, 0, 0);
            AAfacenormals[1] = new Vector3(0, 1, 0);
            AAfacenormals[2] = new Vector3(-1, 0, 0);
            AAfacenormals[3] = new Vector3(0, -1, 0);
            AAfacenormals[4] = new Vector3(0, 0, 1);
            AAfacenormals[5] = new Vector3(0, 0, -1);

            Vector3 AmBa = new Vector3(0, 0, 0); // Vertex A - Vertex B
            Vector3 AmBb = new Vector3(0, 0, 0); // Vertex B - Vertex C
            Vector3 cross = new Vector3();

            Vector3 pos = GetWorldPosition();
            Quaternion rot = GetWorldRotation();

            // Variables prefixed with AX are Axiom.Math copies of the LL variety.

            Quaternion AXrot = rot;
            AXrot.Normalize();

            Vector3 AXpos = pos;

            // tScale is the offset to derive the vertex based on the scale.
            // it's different for each vertex because we've got to rotate it
            // to get the world position of the vertex to produce the Oriented Bounding Box

            Vector3 tScale = Vector3.Zero;

            Vector3 AXscale = new Vector3(Scale.X * 0.5f, Scale.Y * 0.5f, Scale.Z * 0.5f);

            //Vector3 pScale = (AXscale) - (AXrot.Inverse() * (AXscale));
            //Vector3 nScale = (AXscale * -1) - (AXrot.Inverse() * (AXscale * -1));

            // rScale is the rotated offset to find a vertex based on the scale and the world rotation.
            Vector3 rScale = new Vector3();

            // Get Vertexes for Faces Stick them into ABCD for each Face
            // Form: Face<vertex>[face] that corresponds to the below diagram
            #region ABCD Face Vertex Map Comment Diagram
            //                   A _________ B
            //                    |         |
            //                    |  4 top  |
            //                    |_________|
            //                   C           D

            //                   A _________ B
            //                    |  Back   |
            //                    |    3    |
            //                    |_________|
            //                   C           D

            //   A _________ B                     B _________ A
            //    |  Left   |                       |  Right  |
            //    |    0    |                       |    2    |
            //    |_________|                       |_________|
            //   C           D                     D           C

            //                   A _________ B
            //                    |  Front  |
            //                    |    1    |
            //                    |_________|
            //                   C           D

            //                   C _________ D
            //                    |         |
            //                    |  5 bot  |
            //                    |_________|
            //                   A           B
            #endregion

            #region Plane Decomposition of Oriented Bounding Box
            tScale = new Vector3(AXscale.X, -AXscale.Y, AXscale.Z);
            rScale = tScale * AXrot;
            vertexes[0] = (new Vector3((pos.X + rScale.X), (pos.Y + rScale.Y), (pos.Z + rScale.Z)));
               // vertexes[0].X = pos.X + vertexes[0].X;
            //vertexes[0].Y = pos.Y + vertexes[0].Y;
            //vertexes[0].Z = pos.Z + vertexes[0].Z;

            FaceA[0] = vertexes[0];
            FaceB[3] = vertexes[0];
            FaceA[4] = vertexes[0];

            tScale = AXscale;
            rScale = tScale * AXrot;
            vertexes[1] = (new Vector3((pos.X + rScale.X), (pos.Y + rScale.Y), (pos.Z + rScale.Z)));

               // vertexes[1].X = pos.X + vertexes[1].X;
               // vertexes[1].Y = pos.Y + vertexes[1].Y;
            //vertexes[1].Z = pos.Z + vertexes[1].Z;

            FaceB[0] = vertexes[1];
            FaceA[1] = vertexes[1];
            FaceC[4] = vertexes[1];

            tScale = new Vector3(AXscale.X, -AXscale.Y, -AXscale.Z);
            rScale = tScale * AXrot;

            vertexes[2] = (new Vector3((pos.X + rScale.X), (pos.Y + rScale.Y), (pos.Z + rScale.Z)));

            //vertexes[2].X = pos.X + vertexes[2].X;
            //vertexes[2].Y = pos.Y + vertexes[2].Y;
            //vertexes[2].Z = pos.Z + vertexes[2].Z;

            FaceC[0] = vertexes[2];
            FaceD[3] = vertexes[2];
            FaceC[5] = vertexes[2];

            tScale = new Vector3(AXscale.X, AXscale.Y, -AXscale.Z);
            rScale = tScale * AXrot;
            vertexes[3] = (new Vector3((pos.X + rScale.X), (pos.Y + rScale.Y), (pos.Z + rScale.Z)));

            //vertexes[3].X = pos.X + vertexes[3].X;
               // vertexes[3].Y = pos.Y + vertexes[3].Y;
               // vertexes[3].Z = pos.Z + vertexes[3].Z;

            FaceD[0] = vertexes[3];
            FaceC[1] = vertexes[3];
            FaceA[5] = vertexes[3];

            tScale = new Vector3(-AXscale.X, AXscale.Y, AXscale.Z);
            rScale = tScale * AXrot;
            vertexes[4] = (new Vector3((pos.X + rScale.X), (pos.Y + rScale.Y), (pos.Z + rScale.Z)));

               // vertexes[4].X = pos.X + vertexes[4].X;
               // vertexes[4].Y = pos.Y + vertexes[4].Y;
               // vertexes[4].Z = pos.Z + vertexes[4].Z;

            FaceB[1] = vertexes[4];
            FaceA[2] = vertexes[4];
            FaceD[4] = vertexes[4];

            tScale = new Vector3(-AXscale.X, AXscale.Y, -AXscale.Z);
            rScale = tScale * AXrot;
            vertexes[5] = (new Vector3((pos.X + rScale.X), (pos.Y + rScale.Y), (pos.Z + rScale.Z)));

               // vertexes[5].X = pos.X + vertexes[5].X;
               // vertexes[5].Y = pos.Y + vertexes[5].Y;
               // vertexes[5].Z = pos.Z + vertexes[5].Z;

            FaceD[1] = vertexes[5];
            FaceC[2] = vertexes[5];
            FaceB[5] = vertexes[5];

            tScale = new Vector3(-AXscale.X, -AXscale.Y, AXscale.Z);
            rScale = tScale * AXrot;
            vertexes[6] = (new Vector3((pos.X + rScale.X), (pos.Y + rScale.Y), (pos.Z + rScale.Z)));

               // vertexes[6].X = pos.X + vertexes[6].X;
               // vertexes[6].Y = pos.Y + vertexes[6].Y;
               // vertexes[6].Z = pos.Z + vertexes[6].Z;

            FaceB[2] = vertexes[6];
            FaceA[3] = vertexes[6];
            FaceB[4] = vertexes[6];

            tScale = new Vector3(-AXscale.X, -AXscale.Y, -AXscale.Z);
            rScale = tScale * AXrot;
            vertexes[7] = (new Vector3((pos.X + rScale.X), (pos.Y + rScale.Y), (pos.Z + rScale.Z)));

               // vertexes[7].X = pos.X + vertexes[7].X;
               // vertexes[7].Y = pos.Y + vertexes[7].Y;
               // vertexes[7].Z = pos.Z + vertexes[7].Z;

            FaceD[2] = vertexes[7];
            FaceC[3] = vertexes[7];
            FaceD[5] = vertexes[7];
            #endregion

            // Get our plane normals
            for (int i = 0; i < 6; i++)
            {
                //m_log.Info("[FACECALCULATION]: FaceA[" + i + "]=" + FaceA[i] + " FaceB[" + i + "]=" + FaceB[i] + " FaceC[" + i + "]=" + FaceC[i] + " FaceD[" + i + "]=" + FaceD[i]);

                // Our Plane direction
                AmBa = FaceA[i] - FaceB[i];
                AmBb = FaceB[i] - FaceC[i];

                cross = Vector3.Cross(AmBb, AmBa);

                // normalize the cross product to get the normal.
                normals[i] = cross / cross.Length();

                //m_log.Info("[NORMALS]: normals[ " + i + "]" + normals[i].ToString());
                //distance[i] = (normals[i].X * AmBa.X + normals[i].Y * AmBa.Y + normals[i].Z * AmBa.Z) * -1;
            }

            EntityIntersection returnresult = new EntityIntersection();

            returnresult.distance = 1024;
            float c = 0;
            float a = 0;
            float d = 0;
            Vector3 q = new Vector3();

            #region OBB Version 2 Experiment
            //float fmin = 999999;
            //float fmax = -999999;
            //float s = 0;

            //for (int i=0;i<6;i++)
            //{
                //s = iray.Direction.Dot(normals[i]);
                //d = normals[i].Dot(FaceB[i]);

                //if (s == 0)
                //{
                    //if (iray.Origin.Dot(normals[i]) > d)
                    //{
                        //return returnresult;
                    //}
                   // else
                    //{
                        //continue;
                    //}
                //}
                //a = (d - iray.Origin.Dot(normals[i])) / s;
                //if (iray.Direction.Dot(normals[i]) < 0)
                //{
                    //if (a > fmax)
                    //{
                        //if (a > fmin)
                        //{
                            //return returnresult;
                        //}
                        //fmax = a;
                    //}

                //}
                //else
                //{
                    //if (a < fmin)
                    //{
                        //if (a < 0 || a < fmax)
                        //{
                            //return returnresult;
                        //}
                        //fmin = a;
                    //}
                //}
            //}
            //if (fmax > 0)
            //    a= fmax;
            //else
               //     a=fmin;

            //q = iray.Origin + a * iray.Direction;
            #endregion

            // Loop over faces (6 of them)
            for (int i = 0; i < 6; i++)
            {
                AmBa = FaceA[i] - FaceB[i];
                AmBb = FaceB[i] - FaceC[i];
                d = Vector3.Dot(normals[i], FaceB[i]);

                //if (faceCenters)
                //{
                //    c = normals[i].Dot(normals[i]);
                //}
                //else
                //{
                c = Vector3.Dot(iray.Direction, normals[i]);
                //}
                if (c == 0)
                    continue;

                a = (d - Vector3.Dot(iray.Origin, normals[i])) / c;

                if (a < 0)
                    continue;

                // If the normal is pointing outside the object
                if (Vector3.Dot(iray.Direction, normals[i]) < 0 || !frontFacesOnly)
                {
                    //if (faceCenters)
                    //{   //(FaceA[i] + FaceB[i] + FaceC[1] + FaceD[i]) / 4f;
                    //    q =  iray.Origin + a * normals[i];
                    //}
                    //else
                    //{
                        q = iray.Origin + iray.Direction * a;
                    //}

                    float distance2 = (float)GetDistanceTo(q, AXpos);
                    // Is this the closest hit to the object's origin?
                    //if (faceCenters)
                    //{
                    //    distance2 = (float)GetDistanceTo(q, iray.Origin);
                    //}

                    if (distance2 < returnresult.distance)
                    {
                        returnresult.distance = distance2;
                        returnresult.HitTF = true;
                        returnresult.ipoint = q;
                        //m_log.Info("[FACE]:" + i.ToString());
                        //m_log.Info("[POINT]: " + q.ToString());
                        //m_log.Info("[DIST]: " + distance2.ToString());
                        if (faceCenters)
                        {
                            returnresult.normal = AAfacenormals[i] * AXrot;

                            Vector3 scaleComponent = AAfacenormals[i];
                            float ScaleOffset = 0.5f;
                            if (scaleComponent.X != 0) ScaleOffset = AXscale.X;
                            if (scaleComponent.Y != 0) ScaleOffset = AXscale.Y;
                            if (scaleComponent.Z != 0) ScaleOffset = AXscale.Z;
                            ScaleOffset = Math.Abs(ScaleOffset);
                            Vector3 offset = returnresult.normal * ScaleOffset;
                            returnresult.ipoint = AXpos + offset;

                            ///pos = (intersectionpoint + offset);
                        }
                        else
                        {
                            returnresult.normal = normals[i];
                        }
                        returnresult.AAfaceNormal = AAfacenormals[i];
                    }
                }
            }
            return returnresult;
        }

        /// <summary>
        /// Serialize this part to xml.
        /// </summary>
        /// <param name="xmlWriter"></param>
        public void ToXml(XmlWriter xmlWriter)
        {
            //Create our own namespaces for the output and add an empty namespace
            XmlSerializerNamespaces ns = new XmlSerializerNamespaces();
            ns.Add("", "");
            serializer.Serialize(xmlWriter, this, ns);
        }

        public void TriggerScriptChangedEvent(Changed val)
        {
            if (m_parentGroup != null && m_parentGroup.Scene != null)
            {
                m_parentGroup.Scene.EventManager.TriggerOnScriptChangedEvent(LocalId, (uint)val);
            }
            /*else
            {
                m_log.ErrorFormat("[SCENE]: Not triggering script changed event for '{0}' because {1}", this.Name, m_parentGroup == null ? "Group is null" : "Scene is null");
            }
            */
        }

        public void TrimPermissions()
        {
            if (SceneObjectPart.IsLegacyBasemask(_baseMask))
            {
                // Trim old unused (including new Export for non-owners)
                uint trimMask = (uint)PermissionMask.All;
                if (_creatorID == _ownerID)
                    trimMask |= (uint)PermissionMask.Export;

                // Default to Export permission off (including for old content).
                _baseMask &= trimMask;
                _ownerMask &= trimMask;
                _groupMask &= trimMask;
                _everyoneMask &= trimMask;
                _nextOwnerMask &= trimMask;
            }
            else
            {
                // Handle items created after the PermissionMask.All value change, but before the PermissionMask.Export support.
                // These may be owned full rights by the creator but not have the Export flag.
                if ((_creatorID == _ownerID) && ((_baseMask & (uint)PermissionMask.All) == (uint)PermissionMask.All))
                {
                    _baseMask |= (uint)PermissionMask.Export;
                    _ownerMask |= (uint)PermissionMask.Export;
                }

                // Use the masks that are already stored, for all valid bits.
                _baseMask &= (uint)(PermissionMask.All | PermissionMask.Export);
                _ownerMask &= (uint)(PermissionMask.All | PermissionMask.Export);
                _groupMask &= (uint)(PermissionMask.All | PermissionMask.Export);
                _everyoneMask &= (uint)(PermissionMask.All | PermissionMask.Export);
                _nextOwnerMask &= (uint)(PermissionMask.All | PermissionMask.Export);
            }
        }

        public void Undo()
        {
            lock (m_undoLock)
            {
                if (m_undo.Count > 0)
                {
                    UndoState goback = m_undo.Pop();
                    if (goback != null)
                    {
                        m_redo.Push(new UndoState(this));
                        goback.PlaybackState(this);
                    }
                }
            }
        }

        public void Redo()
        {
            lock (m_undoLock)
            {
                if (m_redo.Count > 0)
                {
                    UndoState gofwd = m_redo.Pop();
                    if (gofwd != null)
                    {
                        m_undo.Push(new UndoState(this));
                        gofwd.PlayfwdState(this);
                    }
                }
            }
        }

        public void UpdateExtraParam(ushort type, bool inUse, byte[] data)
        {
            PrimitiveBaseShape tempShape = m_shape.Copy();

            tempShape.ReadInUpdateExtraParam(type, inUse, data);
            if (tempShape.SculptEntry)
            {
                if ((tempShape.SculptType == (byte)OpenMetaverse.SculptType.Mesh) && 
                      (m_shape.SculptType != (byte)OpenMetaverse.SculptType.Mesh))
                {
                    // Cannot change m_shape from non-mesh to mesh. Bypasses the mesh uploader.
                    return;
                }
            }
            m_shape = tempShape.Copy();

            if (type == 0x30)
            {
                if (m_shape.SculptEntry && m_shape.SculptTexture != UUID.Zero)
                {
                    m_parentGroup.Scene.CommsManager.AssetCache.GetAsset(
                        m_shape.SculptTexture, SculptTextureCallback, 
                        AssetRequestInfo.InternalRequest());
                }
            }

            ParentGroup.HasGroupChanged = true;
            ScheduleFullUpdate(PrimUpdateFlags.ExtraData);
        }

        public void UpdateGroupPosition(Vector3 pos)
        {
            if ((pos.X != GroupPosition.X) ||
                (pos.Y != GroupPosition.Y) ||
                (pos.Z != GroupPosition.Z))
            {
                Vector3 newPos = new Vector3(pos.X, pos.Y, pos.Z);
                GroupPosition = newPos;
                ScheduleTerseUpdate();
            }
        }

        public virtual void UpdateMovement()
        {
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="pos"></param>
        public void UpdateOffSet(Vector3 pos)
        {
            if ((pos.X != OffsetPosition.X) ||
                (pos.Y != OffsetPosition.Y) ||
                (pos.Z != OffsetPosition.Z))
            {
                OffsetPosition = pos;
                ScheduleTerseUpdate();
            }
        }

        // Returns false if the update was blocked because the prim was attached.
        public bool UpdatePermissions(UUID AgentID, byte field, uint localID, uint mask, byte addRemTF)
        {
            if (IsAttachment)
            {
                // Emulate SL behavior (but better): Force no changes, refreshing the old data and a message to the viewer.
                // Alternative is to update the inventory item with folded perms to keep it in sync, but good luck with that. :p
                ScheduleFullUpdate(PrimUpdateFlags.ForcedFullUpdate);
                SendObjectPropertiesToClient(AgentID);
                return false;
            }

            bool set = addRemTF == 1;
            bool god = m_parentGroup.Scene.Permissions.IsGod(AgentID);

            // Rationalize the 2-bit "LOCKED" flag to be either on or off.
            // Some code somewhere is allowing the permissions to be
            // set to 0x00004000 which has HALF of the LOCKED "bit" set.
            // This causes a user with MOD permissions to be unable to clear the locked status.
            if ((_baseMask & PERM_MODIFY) == PERM_MODIFY)
                _baseMask |= PERM_MOVE;
            uint baseMask = _baseMask;
            if (god)
                baseMask = LEGACY_BASEMASK;

            // Are we the owner?
            if ((AgentID == _ownerID) || god)
            {
                switch (field)
                {
                    case (byte)PermissionWho.Base:
                        if (god)
                        {
                            _baseMask = ApplyMask(_baseMask, set, mask);
                            Inventory.ApplyGodPermissions(_baseMask);
                        }

                        break;
                    case (byte)PermissionWho.Owner:
                        _ownerMask = ApplyMask(_ownerMask, set, mask) &
                                baseMask;
                        break;
                    case (byte)PermissionWho.Group:
                        _groupMask = ApplyMask(_groupMask, set, mask) &
                                baseMask;
                        break;
                    case (byte)PermissionWho.Everyone:
                        if (set && ((mask & (uint)PermissionMask.Export) != 0))
                        {
                            // Attempting to set export flag.
                            if ((OwnerMask & (uint)PermissionMask.Export) == 0 || (BaseMask & (uint)PermissionMask.Export) == 0 || (NextOwnerMask & (uint)PermissionMask.All) != (uint)PermissionMask.All)
                                mask &= ~(uint)PermissionMask.Export;
                        }
                        _everyoneMask = ApplyMask(_everyoneMask, set, mask) & baseMask;
                        break;
                    case (byte)PermissionWho.NextOwner:
                        _nextOwnerMask = ApplyMask(_nextOwnerMask, set, mask) & baseMask;
                        // If the owner has not provided full rights (MCT) for NextOwner, clear the Export flag.
                        if ((_nextOwnerMask &(uint)PermissionMask.All) != (uint)PermissionMask.All)
                            _everyoneMask &= ~(uint)PermissionMask.Export;
                        break;
                }
                ScheduleFullUpdate(PrimUpdateFlags.PrimData);
                SendObjectPropertiesToClient(AgentID);
            }
            return true;
        }

        public bool IsHingeJoint()
        {
            // For now, we use the NINJA naming scheme for identifying joints.
            // In the future, we can support other joint specification schemes such as a 
            // custom checkbox in the viewer GUI.
            if (m_parentGroup.Scene.PhysicsScene.SupportsNINJAJoints)
            {
                string hingeString = "hingejoint";
                return (Name.Length >= hingeString.Length && Name.Substring(0, hingeString.Length) == hingeString);
            }
            else
            {
                return false;
            }
        }

        public bool IsBallJoint()
        {
            // For now, we use the NINJA naming scheme for identifying joints.
            // In the future, we can support other joint specification schemes such as a 
            // custom checkbox in the viewer GUI.
            if (m_parentGroup.Scene.PhysicsScene.SupportsNINJAJoints)
            {
                string ballString = "balljoint";
                return (Name.Length >= ballString.Length && Name.Substring(0, ballString.Length) == ballString);
            }
            else
            {
                return false;
            }
        }

        public bool IsJoint()
        {
            // For now, we use the NINJA naming scheme for identifying joints.
            // In the future, we can support other joint specification schemes such as a 
            // custom checkbox in the viewer GUI.
            if (m_parentGroup.Scene.PhysicsScene.SupportsNINJAJoints)
            {
                return IsHingeJoint() || IsBallJoint();
            }
            else
            {
                return false;
            }
        }

        public void UpdatePrimFlags(bool UsePhysics, bool IsTemporary, bool IsPhantom, bool IsVD,
                                    ObjectFlagUpdatePacket.ExtraPhysicsBlock[] blocks)
        {
            if (blocks != null && blocks.Length != 0)
            {
                ObjectFlagUpdatePacket.ExtraPhysicsBlock block = blocks[0];

                Shape.PreferredPhysicsShape = (PhysicsShapeType)block.PhysicsShapeType;
                PhysicsShapeChanged();

                if (PhysActor != null)
                {
                    PhysActor.SetMaterial(
                       new MaterialDesc
                       {
                           Restitution = block.Restitution,
                           StaticFriction = block.Friction,
                           DynamicFriction = block.Friction / 1.75f,
                           Density = block.Density,
                           GravityMultiplier = block.GravityMultiplier,
                           MaterialPreset = MaterialDesc.NO_PRESET
                       }, true, MaterialChanges.Restitution
                       );
                }
            }

            if (!IsRootPart()) return;

            SceneObjectPartPhysicsSummary targetSummary = SceneObjectPartPhysicsSummary.SummaryFromParams(Shape.FlexiEntry,
                IsAttachment, IsVD, UsePhysics, IsPhantom);

            SceneObjectPartPhysicsSummary.ChangeFlags changed = SceneObjectPartPhysicsSummary.Compare(this.PhysicsSummary, targetSummary);

            //has to be done before the ChangeFlags are tested because they do not cover the
            //temporary status
            if (IsTemporary)
            {
                m_parentGroup.AddGroupFlagValue(PrimFlags.TemporaryOnRez);
            }
            else
            {
                m_parentGroup.RemGroupFlagValue(PrimFlags.TemporaryOnRez);
            }

            if (changed == SceneObjectPartPhysicsSummary.ChangeFlags.NoChange)
            {
                return;
            }

            if ((changed & SceneObjectPartPhysicsSummary.ChangeFlags.IsPhantomChanged) != 0)
            {
                if (targetSummary.IsPhantom)
                {
                    m_parentGroup.AddGroupFlagValue(PrimFlags.Phantom);
                }
                else
                {
                    m_parentGroup.RemGroupFlagValue(PrimFlags.Phantom);
                }
            }

            if ((changed & SceneObjectPartPhysicsSummary.ChangeFlags.IsVolumeDetectChanged) != 0)
            {
                if (targetSummary.IsVolumeDetect)
                {
                    VolumeDetectActive = true;
                }
                else
                {
                    VolumeDetectActive = false;
                }
            }

            if ((changed & SceneObjectPartPhysicsSummary.ChangeFlags.NeedsDynamicActorChanged) != 0)
            {
                if (targetSummary.NeedsDynamicActor)
                {
                    m_parentGroup.Scene.AddPhysicalObject();
                    m_parentGroup.AddGroupFlagValue(PrimFlags.Physics);
                }
                else
                {
                    m_parentGroup.Scene.RemovePhysicalObject();
                    m_parentGroup.RemGroupFlagValue(PrimFlags.Physics);
                }
            }


            //apply the actual physics changes
            if ((changed & SceneObjectPartPhysicsSummary.ChangeFlags.NeedsPhysicsShapeChanged) != 0)
            {
                if (targetSummary.NeedsPhysicsShape)
                {
                    //reapplies physics to the entire group
                    m_parentGroup.ApplyPhysics(true, false);
                }
                else
                {
                    PhysicsActor physActor = PhysActor;
                    if (physActor != null)
                    {
                        Vector3 velIgnore;
                        Vector3 accelIgnore;
                        Vector3 angularVelIgnore;

                        physActor.GatherTerseUpdate(out m_groupPosition, out m_rotationOffset, out velIgnore, out accelIgnore, out angularVelIgnore);
                        SetOffsetPositionDirect(Vector3.Zero);
                        RemoveFromPhysicalScene(physActor);
                    }
                }
            }

            if ((changed & SceneObjectPartPhysicsSummary.ChangeFlags.IsVolumeDetectChanged) != 0)
            {
                PhysicsActor physActor = PhysActor;
                if (physActor != null)
                {
                    physActor.SetVolumeDetect(targetSummary.IsVolumeDetect);
                }
            }
            else
            {
                if ((changed & SceneObjectPartPhysicsSummary.ChangeFlags.NeedsDynamicActorChanged) != 0
                && (changed & SceneObjectPartPhysicsSummary.ChangeFlags.NeedsPhysicsShapeChanged) == 0)
                {
                    //needs an actor change, but didnt also need a physics shape change which would've 
                    //already created the appropriate actor
                    AdjustPhysactorDynamics(targetSummary.NeedsDynamicActor, false);
                }

                if ((changed & SceneObjectPartPhysicsSummary.ChangeFlags.IsPhantomChanged) != 0)
                {
                    PhysicsActor physActor = PhysActor;
                    if (physActor != null)
                    {
                        //we're still using physics and are now phantom, so we need to update
                        //the prim
                        physActor.SetPhantom(targetSummary.IsPhantom);
                    }
                }

            }

            //m_log.Debug(targetSummary);

            ParentGroup.HasGroupChanged = true;
            ScheduleFullUpdate(PrimUpdateFlags.PrimFlags);
        }

        private void RemoveFromPhysicalScene(PhysicsActor physActor)
        {
            if (IsRootPart())
            {
                m_parentGroup.Scene.PhysicsScene.RemovePrim(physActor);
                m_parentGroup.ForEachPart((SceneObjectPart part) =>
                {
                    part.PhysActor = null;
                }
                );
            }
        }

        internal bool CheckForScriptCollisionEventsAndSubscribe()
        {
            PhysicsActor physActor = PhysActor;
            if (physActor != null)
            {
                if (
                    ((AggregateScriptEvents & ScriptEvents.collision) != 0) ||
                    ((AggregateScriptEvents & ScriptEvents.collision_end) != 0) ||
                    ((AggregateScriptEvents & ScriptEvents.collision_start) != 0) ||
                    ((AggregateScriptEvents & ScriptEvents.land_collision) != 0) ||
                    ((AggregateScriptEvents & ScriptEvents.land_collision_end) != 0) ||
                    ((AggregateScriptEvents & ScriptEvents.land_collision_start) != 0) ||
                    (CollisionSound != UUID.Zero)
                    )
                {
                    physActor.OnCollisionUpdate -= PhysicsCollision;
                    physActor.OnCollisionUpdate += PhysicsCollision;
                    physActor.SubscribeCollisionEvents(1000);

                    //if we have children, this subscribe also needs to apply to them
                    if (IsRootPart())
                    {
                        this.ParentGroup.ForEachPart((SceneObjectPart part) =>
                            {
                                if (part != this)
                                {
                                    PhysicsActor partActor = part.PhysActor;

                                    if (partActor != null)
                                    {
                                        partActor.OnCollisionUpdate -= part.PhysicsCollision;
                                        partActor.OnCollisionUpdate += part.PhysicsCollision;
                                    }
                                }
                            });
                    }

                    return true;
                }
            }

            return false;
        }

        public void UpdateRotation(Quaternion rot)
        {
            if (ParentGroup.InTransit)
                return;
            if (ParentGroup.IsDeleted)
                return;

            if ((rot.X != RotationOffset.X) ||
                (rot.Y != RotationOffset.Y) ||
                (rot.Z != RotationOffset.Z) ||
                (rot.W != RotationOffset.W))
            {
                //StoreUndoState();
                RotationOffset = rot;
                if (IsAttachment)
                    SavedAttachmentRot = rot;
                ParentGroup.HasGroupChanged = true;
                ScheduleTerseUpdate();
            }
        }

        /// <summary>
        /// Update the shape of this part.
        /// </summary>
        /// <param name="shapeBlock"></param>
        public void UpdateShape(ObjectShapePacket.ObjectDataBlock shapeBlock)
        {
            m_shape.PathBegin = shapeBlock.PathBegin;
            m_shape.PathEnd = shapeBlock.PathEnd;
            m_shape.PathScaleX = shapeBlock.PathScaleX;
            m_shape.PathScaleY = shapeBlock.PathScaleY;
            m_shape.PathShearX = shapeBlock.PathShearX;
            m_shape.PathShearY = shapeBlock.PathShearY;
            m_shape.PathSkew = shapeBlock.PathSkew;
            m_shape.ProfileBegin = shapeBlock.ProfileBegin;
            m_shape.ProfileEnd = shapeBlock.ProfileEnd;
            m_shape.PathCurve = shapeBlock.PathCurve;
            m_shape.ProfileCurve = shapeBlock.ProfileCurve;
            m_shape.ProfileHollow = shapeBlock.ProfileHollow;
            m_shape.PathRadiusOffset = shapeBlock.PathRadiusOffset;
            m_shape.PathRevolutions = shapeBlock.PathRevolutions;
            m_shape.PathTaperX = shapeBlock.PathTaperX;
            m_shape.PathTaperY = shapeBlock.PathTaperY;
            m_shape.PathTwist = shapeBlock.PathTwist;
            m_shape.PathTwistBegin = shapeBlock.PathTwistBegin;

            PhysicsActor physActor = PhysActor;
            if (physActor != null)
            {
                physActor.Shape = m_shape;
            }

            // We calculate mesh ServerWeight seperately on upload.  Any change to the shape
            // however is to a non-mesh one (there is no way to force a prim to be a mesh).
            // So reset ServerWeight and StreamingCost to be 1.0 which is default for all other prims.
            ServerWeight = 1.0f;
            StreamingCost = 1.0f;

            // This is what makes vehicle trailers work
            // A script in a child prim re-issues
            // llSetPrimitiveParams(PRIM_TYPE) every few seconds. That
            // prevents autoreturn. This is not well known. It also works
            // in SL.
            //
            if (ParentGroup.RootPart != this)
                ParentGroup.RootPart.Rezzed = DateTime.Now;

            TriggerScriptChangedEvent(Changed.SHAPE);
            ParentGroup.HasGroupChanged = true;
            ScheduleFullUpdate(PrimUpdateFlags.Shape);
        }

        /// <summary>
        /// Updates the prim's texture data (not the blob bytes)
        /// This also nulls the m_textureEntryBytes (blob) and TextureEntryBytes
        /// until someone calls them.
        /// </summary>
        /// <param name="tex">TextureEntry</param>
        /// <param name="change">changed event to send</param>
        public void UpdateTexture(Primitive.TextureEntry tex, Changed change)
        {
            m_shape.Textures = tex;

            if (change != 0)
                TriggerScriptChangedEvent(change);
            ParentGroup.HasGroupChanged = true;
            ScheduleFullUpdate(PrimUpdateFlags.Textures);
        }
        public void UpdateTexture(Primitive.TextureEntry tex)
        {
            UpdateTexture(tex, Changed.TEXTURE);
        }

        /// <summary>
        /// Directly update the texture entry blob (and texture data) for this part.
        /// This also updates the Textures property.
        /// </summary>
        /// <param name="textureEntry"></param>
        public void UpdateTextureEntry(byte[] textureEntryBytes)
        {
            m_shape.TextureEntryBytes = textureEntryBytes;

            TriggerScriptChangedEvent(Changed.TEXTURE);
            ParentGroup.HasGroupChanged = true;
            ScheduleFullUpdate(PrimUpdateFlags.Textures);
        }

        public void DoAggregateScriptEvents()
        {
            AggregateScriptEvents = 0;

            // Aggregate script events
            lock (m_scriptEvents)
            {
                foreach (ScriptEvents s in m_scriptEvents.Values)
                {
                    AggregateScriptEvents |= s;
                }
            }

            uint objectflagupdate = 0;

            if (
                ((AggregateScriptEvents & ScriptEvents.touch) != 0) ||
                ((AggregateScriptEvents & ScriptEvents.touch_end) != 0) ||
                ((AggregateScriptEvents & ScriptEvents.touch_start) != 0)
                )
            {
                objectflagupdate |= (uint) PrimFlags.Touch;
            }

            if ((AggregateScriptEvents & ScriptEvents.money) != 0)
            {
                objectflagupdate |= (uint) PrimFlags.Money;
            }

            if (AllowedDrop)
            {
                objectflagupdate |= (uint) PrimFlags.AllowInventoryDrop;
            }

            if (! CheckForScriptCollisionEventsAndSubscribe())
            {
                PhysicsActor physActor = PhysActor;
                if (physActor != null)
                {
                    physActor.UnSubscribeEvents();
                    physActor.OnCollisionUpdate -= PhysicsCollision;
                }
            }

            if (m_parentGroup == null)
            {
                ScheduleFullUpdate(PrimUpdateFlags.FindBest);
                return;
            }

            LocalFlags=(PrimFlags)objectflagupdate;

            if (m_parentGroup != null && m_parentGroup.RootPart == this)
                m_parentGroup.aggregateScriptEvents();
            else
                ScheduleFullUpdate(PrimUpdateFlags.FindBest);
        }

        public int registerTargetWaypoint(Vector3 target, float tolerance)
        {
            if (m_parentGroup != null)
            {
                return m_parentGroup.RegisterTargetWaypoint(target, tolerance);
            }
            return 0;
        }

        public void unregisterTargetWaypoint(int handle)
        {
            if (m_parentGroup != null)
            {
                m_parentGroup.UnregisterTargetWaypoint(handle);
            }
        }

        public void SetCameraAtOffset(Vector3 v)
        {
            m_cameraAtOffset = v;
        }

        public void SetCameraEyeOffset(Vector3 v)
        {
            m_cameraEyeOffset = v;
        }

        public void SetForceMouselook(bool force)
        {
            m_forceMouselook = force;
        }

        public Vector3 GetCameraAtOffset()
        {
            return m_cameraAtOffset;
        }

        public Vector3 GetCameraEyeOffset()
        {
            return m_cameraEyeOffset;
        }

        public bool GetForceMouselook()
        {
            return m_forceMouselook;
        }
        
        public override string ToString()
        {
            return String.Format("{0} {1} (parent {2}))", Name, UUID, ParentGroup);
        }        

        #endregion Public Methods

        public void SendTerseUpdateToClient(IClientAPI remoteClient)
        {
            if (ParentGroup == null || ParentGroup.IsDeleted || ParentGroup.InTransit)
                return;

            remoteClient.SendPrimTerseUpdate(this);
        }
                
        public void AddScriptLPS(int count)
        {
            m_parentGroup.AddScriptLPS(count);
        }
        
        public void ApplyNextOwnerPermissions()
        {
            uint nperms = ParentGroup.GetEffectiveNextPermissions(false);

            // Objects flagged as export need it preserved.
            if ((_everyoneMask & (uint)PermissionMask.Export) == (uint)PermissionMask.Export)
                nperms |= (uint)PermissionMask.Export;
            else
                nperms &= ~(uint)PermissionMask.Export;

            _baseMask &= nperms;
            _ownerMask &= nperms;
            _everyoneMask &= nperms;

            Inventory.ApplyNextOwnerPermissions();
        }

        /// <summary>
        /// This fixes inconsistencies between this part and the root part.
        /// In the past, there was a bug in Link operations that did not force 
        /// these permissions on child prims when linking.
        /// </summary>
        public void SyncChildPermsWithRoot()
        {
            // Repair any next permissions of child prims in the existing group
            // to match those of the root prim. Only update the ones that affect others.
            // The current user perms are not affected by a link.
            // part.BaseMask = this.RootPart.BaseMask & part.BaseMask;
            // part.OwnerMask = this.RootPart.OwnerMask & part.BaseMask;
            NextOwnerMask = BaseMask & ParentGroup.RootPart.NextOwnerMask;
            GroupMask = BaseMask & ParentGroup.RootPart.GroupMask;
            EveryoneMask = BaseMask & ParentGroup.RootPart.EveryoneMask;
        }

        // fix locked bits, for sale status, and default touch action when rezzing or transfering (selling) an object
        public bool Rationalize(UUID itemOwner, bool fromCrossing)
        {
            bool ownerChanged = false;

            // Fix the BASE mask locked bit to allow unlock even if no transfer
            // Note: the fake "locked" bit only applies to the current owner, not the next permissions
            _baseMask |= PERM_MOVE;

            if (OwnerID != itemOwner)   // changing owners / transfer
            {
                //Need to kill the for sale here
                ObjectSaleType = 0;
                SalePrice = 10;
                // Fix the OWNER mask locked bit to be unlocked on a transfer
                _ownerMask |= PERM_MOVE;   // unlock the object on a transfer
                ownerChanged = true;
            }

            if (ObjectSaleType == 0)    // not for sale
            {
                if (ClickAction == 2)  // CLICK_ACTION_BUY
                    ClickAction = 0;   // CLICK_ACTION_TOUCH
            }

            //Make sure scripts within are cleared for new owners BUT ->
            //Don't rationalize inventory on crossing, this clears out perms
            //granted for non-owners crossing on someone else's object


            if (fromCrossing)
            {
                return ownerChanged;
            }
            else
            {
                return ownerChanged | Inventory.Rationalize(itemOwner);
            }
        }

        static public List<uint> PartListToLocalIdList(IEnumerable<SceneObjectPart> parts)
        {
            List<uint> retList = new List<uint>();

            foreach (SceneObjectPart part in parts)
            {
                retList.Add(part.LocalId);
            }

            return retList;
        }

        internal void SetOffsetPositionDirect(Vector3 partOffsetPosition)
        {
            m_offsetPosition = partOffsetPosition;
            if (m_parentGroup != null) m_parentGroup.ClearBoundingBoxCache();
        }

        internal void SetGroupPositionDirect(Vector3 partGroupPosition)
        {
            m_groupPosition = partGroupPosition;
            if (m_parentGroup != null)
                m_parentGroup.RepositionBoundingBox(m_groupPosition);
        }

        internal void SetRotationOffsetDirect(Quaternion partRotationOffset)
        {
            m_rotationOffset = partRotationOffset;
            if (m_parentGroup != null) m_parentGroup.ClearBoundingBoxCache();
        }

        internal BulkShapeData GenerateBulkShapeData()
        {
            BulkShapeData bulkShapeData = new BulkShapeData
            {
                Part = this,
                Pbs = m_shape,
                Position = LocalPos,
                Rotation = m_rotationOffset,
                Size = Scale,
                Material = m_parentGroup.Scene.PhysicsScene.FindMaterialImpl(m_material),
                PhysicsProperties = this.SerializedPhysicsData,
                Velocity = this.m_serializedVelocity,
                AngularVelocity = this.m_serializedPhysicalAngularVelocity,
                SerializedShapes = this._serializedPhysicsShapes,
                ObjectReceivedOn = this.ParentGroup.TimeReceived
            };

            //clean up
            _serializedPhysicsData = null;
            _serializedPhysicsShapes = null; 
            m_serializedPhysicalAngularVelocity = Vector3.Zero;

            return bulkShapeData;
        }

        internal PhysicsScene.AddPrimShapeFlags GeneratePhysicsAddPrimShapeFlags(bool allowPhysicalPrims, bool fromStorage)
        {
            PhysicsScene.AddPrimShapeFlags flags = PhysicsScene.AddPrimShapeFlags.None;
            if (allowPhysicalPrims && (GetEffectiveObjectFlags() & PrimFlags.Physics) != 0) flags |= PhysicsScene.AddPrimShapeFlags.Physical;
            if (fromStorage) flags |= PhysicsScene.AddPrimShapeFlags.FromSceneStartup;
            if ((GetEffectiveObjectFlags() & PrimFlags.Phantom) != 0) flags |= PhysicsScene.AddPrimShapeFlags.Phantom;
            if (ParentGroup.AvatarsToExpect > 0) flags |= PhysicsScene.AddPrimShapeFlags.Interpolate;
            if (ParentGroup.FromCrossing) flags |= PhysicsScene.AddPrimShapeFlags.FromCrossing;

            return flags;
        }

        public bool IsPhantom
        {
            get
            {
                return (ObjectFlags & (uint)PrimFlags.Phantom) != 0;
            }
        }

        public bool RequiresPhysicalShape
        {
            get
            {
                return this.PhysicsSummary.NeedsPhysicsShape;
            }
        }

        public void GatherTerseUpdate(out OpenMetaverse.Vector3 position, out OpenMetaverse.Quaternion rotation,
            out OpenMetaverse.Vector3 velocity, out OpenMetaverse.Vector3 acceleration, out OpenMetaverse.Vector3 angularVelocity)
        {
            PhysicsActor myActor = PhysActor;
            if (myActor != null)
            {
                myActor.GatherTerseUpdate(out position, out rotation, out velocity, out acceleration, out angularVelocity);
            }
            else
            {
                position = LocalPos;
                velocity = OpenMetaverse.Vector3.Zero;
                acceleration = OpenMetaverse.Vector3.Zero;
                angularVelocity = m_angularVelocity;
                rotation = RotationOffset;
            }
        }

        public void PhysicsShapeChanged()
        {
            PhysicsActor physActor = PhysActor;

            if (physActor != null)
            {
                m_parentGroup.Scene.PhysicsScene.AddPhysicsActorTaint(physActor, TaintType.ChangedShape);
            }
        }

        public byte[] GetSerializedPhysicsProperties()
        {
            PhysicsActor physActor = PhysActor;

            if (physActor != null)
            {
                return physActor.GetSerializedPhysicsProperties();
            }
            else
            {
                return null;
            }
        }

        internal void ReplaceSerializedVelocity(Vector3 vel)
        {
            m_serializedVelocity = vel;
        }

        internal void RestorePrePhysicsTargetOmega()
        {
            if (m_angularVelocity != Vector3.Zero)
            {
                AngularVelocity = m_angularVelocity;
            }
        }

        public int RegisterRotationTarget(Quaternion rot, float error)
        {
            if (m_parentGroup != null)
            {
                return m_parentGroup.RegisterRotationTarget(rot, error);
            }

            return 0;
        }

        public void UnregisterRotationTarget(int number)
        {
            if (m_parentGroup != null)
            {
                m_parentGroup.UnregisterRotationTarget(number);
            }
        }

        private Dictionary<UUID, byte[]> _savedScriptStates;

        public bool HasSavedScriptStates 
        {
            get
            {
                return _savedScriptStates != null;
            }
        }

        public void SetSavedScriptStates(Dictionary<UUID, byte[]> states)
        {
            _savedScriptStates = states;
        }

        public bool TryExtractSavedScriptState(UUID scriptId, out byte[] binaryState)
        {
            if (_savedScriptStates != null && _savedScriptStates.TryGetValue(scriptId, out binaryState))
            {
                _savedScriptStates.Remove(scriptId);

                if (_savedScriptStates.Count == 0)
                {
                    _savedScriptStates = null;
                }

                return true;
            }

            binaryState = null;
            return false;
        }
    }        
}
