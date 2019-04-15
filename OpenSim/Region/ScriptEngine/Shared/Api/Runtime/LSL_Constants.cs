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
using vector = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3;
using rotation = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Quaternion;
using LSLInteger = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;

namespace OpenSim.Region.ScriptEngine.Shared.ScriptBase
{
    public partial class ScriptBaseClass : MarshalByRefObject
    {
        // LSL CONSTANTS
        public static readonly LSLInteger TRUE = new LSLInteger(1);
        public static readonly LSLInteger FALSE = new LSLInteger(0);

        public const int STATUS_PHYSICS = 1;
        public const int STATUS_ROTATE_X = 2;
        public const int STATUS_ROTATE_Y = 4;
        public const int STATUS_ROTATE_Z = 8;
        public const int STATUS_PHANTOM = 16;
        public const int STATUS_SANDBOX = 32;
        public const int STATUS_BLOCK_GRAB = 64;    // Note this will be treated as STATUS_BLOCK_GRAB_OBJECT
        public const int STATUS_DIE_AT_EDGE = 128;
        public const int STATUS_RETURN_AT_EDGE = 256;
        public const int STATUS_CAST_SHADOWS = 512;
        public const int STATUS_BLOCK_GRAB_OBJECT = 1024;

        public const int AGENT = 1;
        public const int ACTIVE = 2;
        public const int PASSIVE = 4;
        public const int SCRIPTED = 8;

        public const int CONTROL_FWD = 1;
        public const int CONTROL_BACK = 2;
        public const int CONTROL_LEFT = 4;
        public const int CONTROL_RIGHT = 8;
        public const int CONTROL_UP = 16;
        public const int CONTROL_DOWN = 32;
        public const int CONTROL_ROT_LEFT = 256;
        public const int CONTROL_ROT_RIGHT = 512;
        public const int CONTROL_LBUTTON = 268435456;
        public const int CONTROL_ML_LBUTTON = 1073741824;

        //Permissions
        public const int PERMISSION_DEBIT = 2;
        public const int PERMISSION_TAKE_CONTROLS = 4;
        public const int PERMISSION_REMAP_CONTROLS = 8;
        public const int PERMISSION_TRIGGER_ANIMATION = 16;
        public const int PERMISSION_ATTACH = 32;
        public const int PERMISSION_RELEASE_OWNERSHIP = 64;
        public const int PERMISSION_CHANGE_LINKS = 128;
        public const int PERMISSION_CHANGE_JOINTS = 256;
        public const int PERMISSION_CHANGE_PERMISSIONS = 512;
        public const int PERMISSION_TRACK_CAMERA = 1024;
        public const int PERMISSION_CONTROL_CAMERA = 2048;
        public const int PERMISSION_TELEPORT = 4096;                    // 0x1000
        public const int PERMISSION_SILENT_ESTATE_MANAGEMENT = 16384;   // 0x4000
        public const int PERMISSION_OVERRIDE_ANIMATIONS = 32768;        // 0x8000
        public const int PERMISSION_RETURN_OBJECTS = 65536;             //0x10000

        public const int AGENT_FLYING = 1;
        public const int AGENT_ATTACHMENTS = 2;
        public const int AGENT_SCRIPTED = 4;
        public const int AGENT_MOUSELOOK = 8;
        public const int AGENT_SITTING = 16;
        public const int AGENT_ON_OBJECT = 32;
        public const int AGENT_AWAY = 64;
        public const int AGENT_WALKING = 128;
        public const int AGENT_IN_AIR = 256;
        public const int AGENT_TYPING = 512;
        public const int AGENT_CROUCHING = 1024;
        public const int AGENT_BUSY = 2048;
        public const int AGENT_ALWAYS_RUN = 4096;

        //Particle Systems
        public const int PSYS_PART_INTERP_COLOR_MASK = 1;
        public const int PSYS_PART_INTERP_SCALE_MASK = 2;
        public const int PSYS_PART_BOUNCE_MASK = 4;
        public const int PSYS_PART_WIND_MASK = 8;
        public const int PSYS_PART_FOLLOW_SRC_MASK = 16;
        public const int PSYS_PART_FOLLOW_VELOCITY_MASK = 32;
        public const int PSYS_PART_TARGET_POS_MASK = 64;
        public const int PSYS_PART_TARGET_LINEAR_MASK = 128;
        public const int PSYS_PART_EMISSIVE_MASK = 256;
        public const int PSYS_PART_RIBBON_MASK = 1024;
        public const int PSYS_PART_FLAGS = 0;
        public const int PSYS_PART_START_COLOR = 1;
        public const int PSYS_PART_START_ALPHA = 2;
        public const int PSYS_PART_END_COLOR = 3;
        public const int PSYS_PART_END_ALPHA = 4;
        public const int PSYS_PART_START_SCALE = 5;
        public const int PSYS_PART_END_SCALE = 6;
        public const int PSYS_PART_MAX_AGE = 7;
        public const int PSYS_SRC_ACCEL = 8;
        public const int PSYS_SRC_PATTERN = 9;
        public const int PSYS_SRC_INNERANGLE = 10;
        public const int PSYS_SRC_OUTERANGLE = 11;
        public const int PSYS_SRC_TEXTURE = 12;
        public const int PSYS_SRC_BURST_RATE = 13;
        public const int PSYS_SRC_BURST_PART_COUNT = 15;
        public const int PSYS_SRC_BURST_RADIUS = 16;
        public const int PSYS_SRC_BURST_SPEED_MIN = 17;
        public const int PSYS_SRC_BURST_SPEED_MAX = 18;
        public const int PSYS_SRC_MAX_AGE = 19;
        public const int PSYS_SRC_TARGET_KEY = 20;
        public const int PSYS_SRC_OMEGA = 21;
        public const int PSYS_SRC_ANGLE_BEGIN = 22;
        public const int PSYS_SRC_ANGLE_END = 23;
        public const int PSYS_SRC_PATTERN_DROP = 1;
        public const int PSYS_SRC_PATTERN_EXPLODE = 2;
        public const int PSYS_SRC_PATTERN_ANGLE = 4;
        public const int PSYS_SRC_PATTERN_ANGLE_CONE = 8;
        public const int PSYS_SRC_PATTERN_ANGLE_CONE_EMPTY = 16;
        public const int PSYS_PART_BLEND_FUNC_SOURCE = 24;
        public const int PSYS_PART_BLEND_FUNC_DEST = 25;
        public const int PSYS_PART_BF_ONE = 0;
        public const int PSYS_PART_BF_ZERO = 1;
        public const int PSYS_PART_BF_DEST_COLOR = 2;
        public const int PSYS_PART_BF_SOURCE_COLOR = 3;
        public const int PSYS_PART_BF_ONE_MINUS_DEST_COLOR = 4;
        public const int PSYS_PART_BF_ONE_MINUS_SOURCE_COLOR = 5;
        public const int PSYS_PART_BF_SOURCE_ALPHA = 7;
        public const int PSYS_PART_BF_ONE_MINUS_SOURCE_ALPHA = 9;

        public const int PSYS_PART_START_GLOW = 26;
        public const int PSYS_PART_END_GLOW = 27;



        public const int VEHICLE_TYPE_NONE = 0;
        public const int VEHICLE_TYPE_SLED = 1;
        public const int VEHICLE_TYPE_CAR = 2;
        public const int VEHICLE_TYPE_BOAT = 3;
        public const int VEHICLE_TYPE_AIRPLANE = 4;
        public const int VEHICLE_TYPE_BALLOON = 5;
        public const int VEHICLE_TYPE_SAILBOAT = 10001;
        public const int VEHICLE_TYPE_MOTORCYCLE = 10002;

        public const int VEHICLE_LINEAR_FRICTION_TIMESCALE = 16;
        public const int VEHICLE_ANGULAR_FRICTION_TIMESCALE = 17;
        public const int VEHICLE_LINEAR_MOTOR_DIRECTION = 18;
        public const int VEHICLE_LINEAR_MOTOR_OFFSET = 20;
        public const int VEHICLE_ANGULAR_MOTOR_DIRECTION = 19;
        public const int VEHICLE_HOVER_HEIGHT = 24;
        public const int VEHICLE_HOVER_EFFICIENCY = 25;
        public const int VEHICLE_HOVER_TIMESCALE = 26;
        public const int VEHICLE_BUOYANCY = 27;
        public const int VEHICLE_LINEAR_DEFLECTION_EFFICIENCY = 28;
        public const int VEHICLE_LINEAR_DEFLECTION_TIMESCALE = 29;
        public const int VEHICLE_LINEAR_MOTOR_TIMESCALE = 30;
        public const int VEHICLE_LINEAR_MOTOR_DECAY_TIMESCALE = 31;
        public const int VEHICLE_ANGULAR_DEFLECTION_EFFICIENCY = 32;
        public const int VEHICLE_ANGULAR_DEFLECTION_TIMESCALE = 33;
        public const int VEHICLE_ANGULAR_MOTOR_TIMESCALE = 34;
        public const int VEHICLE_ANGULAR_MOTOR_DECAY_TIMESCALE = 35;
        public const int VEHICLE_VERTICAL_ATTRACTION_EFFICIENCY = 36;
        public const int VEHICLE_VERTICAL_ATTRACTION_TIMESCALE = 37;
        public const int VEHICLE_BANKING_EFFICIENCY = 38;
        public const int VEHICLE_BANKING_MIX = 39;
        public const int VEHICLE_BANKING_TIMESCALE = 40;
        public const int VEHICLE_REFERENCE_FRAME = 44;
        public const int VEHICLE_FLAG_NO_DEFLECTION_UP = 1;
        public const int VEHICLE_FLAG_LIMIT_ROLL_ONLY = 2;
        public const int VEHICLE_FLAG_HOVER_WATER_ONLY = 4;
        public const int VEHICLE_FLAG_HOVER_TERRAIN_ONLY = 8;
        public const int VEHICLE_FLAG_HOVER_GLOBAL_HEIGHT = 16;
        public const int VEHICLE_FLAG_HOVER_UP_ONLY = 32;
        public const int VEHICLE_FLAG_LIMIT_MOTOR_UP = 64;
        public const int VEHICLE_FLAG_MOUSELOOK_STEER = 128;
        public const int VEHICLE_FLAG_MOUSELOOK_BANK = 256;
        public const int VEHICLE_FLAG_CAMERA_DECOUPLED = 512;

        public const int INVENTORY_ALL = -1;
        public const int INVENTORY_NONE = -1;
        public const int INVENTORY_TEXTURE = 0;
        public const int INVENTORY_SOUND = 1;
        public const int INVENTORY_LANDMARK = 3;
        public const int INVENTORY_CLOTHING = 5;
        public const int INVENTORY_OBJECT = 6;
        public const int INVENTORY_NOTECARD = 7;
        public const int INVENTORY_SCRIPT = 10;
        public const int INVENTORY_BODYPART = 13;
        public const int INVENTORY_ANIMATION = 20;
        public const int INVENTORY_GESTURE = 21;

        public const int ATTACH_CHEST = 1;
        public const int ATTACH_HEAD = 2;
        public const int ATTACH_LSHOULDER = 3;
        public const int ATTACH_RSHOULDER = 4;
        public const int ATTACH_LHAND = 5;
        public const int ATTACH_RHAND = 6;
        public const int ATTACH_LFOOT = 7;
        public const int ATTACH_RFOOT = 8;
        public const int ATTACH_BACK = 9;
        public const int ATTACH_PELVIS = 10;
        public const int ATTACH_MOUTH = 11;
        public const int ATTACH_CHIN = 12;
        public const int ATTACH_LEAR = 13;
        public const int ATTACH_REAR = 14;
        public const int ATTACH_LEYE = 15;
        public const int ATTACH_REYE = 16;
        public const int ATTACH_NOSE = 17;
        public const int ATTACH_RUARM = 18;
        public const int ATTACH_RLARM = 19;
        public const int ATTACH_LUARM = 20;
        public const int ATTACH_LLARM = 21;
        public const int ATTACH_RHIP = 22;
        public const int ATTACH_RULEG = 23;
        public const int ATTACH_RLLEG = 24;
        public const int ATTACH_LHIP = 25;
        public const int ATTACH_LULEG = 26;
        public const int ATTACH_LLLEG = 27;
        public const int ATTACH_BELLY = 28;
        public const int ATTACH_LEFT_PEC = 29;
        public const int ATTACH_RIGHT_PEC = 30;
        public const int ATTACH_HUD_CENTER_2 = 31;
        public const int ATTACH_HUD_TOP_RIGHT = 32;
        public const int ATTACH_HUD_TOP_CENTER = 33;
        public const int ATTACH_HUD_TOP_LEFT = 34;
        public const int ATTACH_HUD_CENTER_1 = 35;
        public const int ATTACH_HUD_BOTTOM_LEFT = 36;
        public const int ATTACH_HUD_BOTTOM = 37;
        public const int ATTACH_HUD_BOTTOM_RIGHT = 38;
        public const int ATTACH_NECK = 39;
        public const int ATTACH_AVATAR_CENTER = 40;
        // Bento Additions
        public const int ATTACH_LHAND_RING1 = 41;
        public const int ATTACH_RHAND_RING1 = 42;
        public const int ATTACH_TAIL_BASE = 43;
        public const int ATTACH_TAIL_TIP = 44;
        public const int ATTACH_LWING = 45;
        public const int ATTACH_RWING = 46;
        public const int ATTACH_FACE_JAW = 47;
        public const int ATTACH_FACE_LEAR = 48;
        public const int ATTACH_FACE_REAR = 49;
        public const int ATTACH_FACE_LEYE = 50;
        public const int ATTACH_FACE_REYE = 51;
        public const int ATTACH_FACE_TONGUE = 52;
        public const int ATTACH_GROIN = 53;
        public const int ATTACH_HIND_LFOOT = 54;
        public const int ATTACH_HIND_RFOOT = 55;

        public const int LAND_LEVEL = 0;
        public const int LAND_RAISE = 1;
        public const int LAND_LOWER = 2; 
        public const int LAND_SMOOTH = 3;
        public const int LAND_NOISE = 4;
        public const int LAND_REVERT = 5;
        public const int LAND_SMALL_BRUSH = 1;
        public const int LAND_MEDIUM_BRUSH = 2;
        public const int LAND_LARGE_BRUSH = 3;

        //Agent Dataserver
        public const int DATA_ONLINE = 1;
        public const int DATA_NAME = 2;
        public const int DATA_BORN = 3;
        public const int DATA_RATING = 4;
        public const int DATA_SIM_POS = 5;
        public const int DATA_SIM_STATUS = 6;
        public const int DATA_SIM_RATING = 7;
        public const int DATA_PAYINFO = 8;
        public const int DATA_SIM_RELEASE = 128;
        public const int DATA_ACCOUNT_TYPE = 11001;

        public const int ANIM_ON = 1;
        public const int LOOP = 2;
        public const int REVERSE = 4;
        public const int PING_PONG = 8;
        public const int SMOOTH = 16;
        public const int ROTATE = 32;
        public const int SCALE = 64;
        public const int ALL_SIDES = -1;
        public const int LINK_SET = -1;
        public const int LINK_ROOT = 1;
        public const int LINK_ALL_OTHERS = -2;
        public const int LINK_ALL_CHILDREN = -3;
        public const int LINK_THIS = -4;
        public const int CHANGED_INVENTORY = 1;
        public const int CHANGED_COLOR = 2;
        public const int CHANGED_SHAPE = 4;
        public const int CHANGED_SCALE = 8;
        public const int CHANGED_TEXTURE = 16;
        public const int CHANGED_LINK = 32;
        public const int CHANGED_ALLOWED_DROP = 64;
        public const int CHANGED_OWNER = 128;
        public const int CHANGED_REGION = 256;
        public const int CHANGED_TELEPORT = 512;
        public const int CHANGED_REGION_START = 1024;
        public const int CHANGED_REGION_RESTART = 1024;
        public const int CHANGED_MEDIA = 2048;
        public const int CHANGED_ANIMATION = 16384;
        public const int TYPE_INVALID = 0;
        public const int TYPE_INTEGER = 1;
        public const int TYPE_FLOAT = 2;
        public const int TYPE_STRING = 3;
        public const int TYPE_KEY = 4;
        public const int TYPE_VECTOR = 5;
        public const int TYPE_ROTATION = 6;

        //XML RPC Remote Data Channel
        public const int REMOTE_DATA_CHANNEL = 1;
        public const int REMOTE_DATA_REQUEST = 2;
        public const int REMOTE_DATA_REPLY = 3;

        //llHTTPRequest
        public const int HTTP_METHOD = 0;
        public const int HTTP_MIMETYPE = 1;
        public const int HTTP_BODY_MAXLENGTH = 2;
        public const int HTTP_VERIFY_CERT = 3;

        public const int PRIM_MATERIAL = 2;
        public const int PRIM_PHYSICS = 3;
        public const int PRIM_TEMP_ON_REZ = 4;
        public const int PRIM_PHANTOM = 5;
        public const int PRIM_POSITION = 6;
        public const int PRIM_SIZE = 7;
        public const int PRIM_ROTATION = 8;
        public const int PRIM_TYPE = 9;
        public const int PRIM_TEXTURE = 17;
        public const int PRIM_COLOR = 18;
        public const int PRIM_BUMP_SHINY = 19;
        public const int PRIM_FULLBRIGHT = 20;
        public const int PRIM_FLEXIBLE = 21;
        public const int PRIM_TEXGEN = 22;
        public const int PRIM_POINT_LIGHT = 23; // Huh?
        public const int PRIM_CAST_SHADOWS = 24; // Not implemented, here for completeness sake
        public const int PRIM_GLOW = 25;
        public const int PRIM_TEXT = 26;        // added by LL in server 1.38
        public const int PRIM_NAME = 27;        // added by LL in server 1.40
        public const int PRIM_DESC = 28;        // added by LL in server 1.40
        public const int PRIM_ROT_LOCAL = 29;   // added by LL in Oct 2010 thru Feb 2011 (JIRA SVC-93)
        public const int PRIM_PHYSICS_SHAPE_TYPE = 30;
        public const int PRIM_OMEGA = 32;
        public const int PRIM_POS_LOCAL = 33;
        public const int PRIM_LINK_TARGET = 34;
        public const int PRIM_SLICE = 35;
        public const int PRIM_SPECULAR = 36;
        public const int PRIM_NORMAL = 37;
        public const int PRIM_ALPHA_MODE = 38;
        public const int PRIM_ALLOW_UNSIT = 39;
        public const int PRIM_SCRIPTED_SIT_ONLY = 40;
        public const int PRIM_SIT_TARGET = 41;

        // large out of normal range value unlikely to conflict with future LL values
        
        /// @page IW_PRIM_ALPHA Constant: IW_PRIM_ALPHA
        /// @brief The alpha (opacity) of the specifed face.
        /// @par "Constant: integer IW_PRIM_ALPHA = 11001;"
        /// @parblock
        /// <tt>[ IW_PRIM_ALPHA, integer face, float alpha ]</tt>
        /// @endparblock
        /// @par Caveats
        /// @parblock
        /// @endparblock
        /// @includedoc lslconst.xhtml
        /** @ref IW_PRIM_ALPHA */
        public const int IW_PRIM_ALPHA = 11001;
        /// @page IW_PRIM_PROJECTOR Constant: IW_PRIM_PROJECTOR
        /// @brief Get or set all the projector properties in one shot.
        /// @par "Constant: integer IW_PRIM_PROJECTOR = 11100;"
        /// @parblock
        /// <tt>[ IW_PRIM_PROJECTOR, integer enabled, string texture, float field_of_view, float focus_dist, float ambience ]</tt>
        /// @endparblock
        /// @par Caveats
        /// @parblock
        /// @endparblock
        /// @sa IW_PRIM_PROJECTOR_ENABLED IW_PRIM_PROJECTOR_TEXTURE IW_PRIM_PROJECTOR_FOV IW_PRIM_PROJECTOR_FOCUS IW_PRIM_PROJECTOR_AMBIENCE
        /// @includedoc lslconst.xhtml
        /** @ref IW_PRIM_PROJECTOR */
        public const int IW_PRIM_PROJECTOR          = 11100;
        ///
        /// @page IW_PRIM_PROJECTOR_ENABLED Constant: IW_PRIM_PROJECTOR_ENABLED
        /// @brief Whether or not the projector portion of the light source is active.
        /// @par "Constant: integer IW_PRIM_PROJECTOR_ENABLED = 11101;"
        /// @parblock
        /// <tt>[ IW_PRIM_PROJECTOR_ENABLED, integer enabled ]</tt>
        /// @endparblock
        /// @par Caveats
        /// @parblock
        /// @endparblock
        /// @includedoc lslconst.xhtml
        /** @ref IW_PRIM_PROJECTOR_ENABLED */
        public const int IW_PRIM_PROJECTOR_ENABLED  = 11101;
        /// @page IW_PRIM_PROJECTOR_TEXTURE Constant: IW_PRIM_PROJECTOR_TEXTURE
        /// @brief The texture the projector emits.
        /// @par "Constant: integer IW_PRIM_PROJECTOR_TEXTURE = 11102;"
        /// @parblock
        /// <tt>[ IW_PRIM_PROJECTOR_TEXTURE, string texture ]</tt>
        /// You can use either the UUID of a texture or the name of a texture that is in the inventory of the same prim as the script.
        /// @endparblock
        /// @par Caveats
        /// @parblock
        /// @endparblock
        /// @includedoc lslconst.xhtml
        /** @ref IW_PRIM_PROJECTOR_TEXTURE */
        public const int IW_PRIM_PROJECTOR_TEXTURE  = 11102;
        /// @page IW_PRIM_PROJECTOR_FOV Constant: IW_PRIM_PROJECTOR_FOV
        /// @brief The field of view, in radians, the projector emits.
        /// @par "Constant: integer IW_PRIM_PROJECTOR_FOV = 11103;"
        /// @parblock
        /// <tt>[ IW_PRIM_PROJECTOR_FOV, float field_of_view ]</tt>
        /// Must be between 0.0 and 3.0 inclusive.
        /// @endparblock
        /// @par Caveats
        /// @parblock
        /// @endparblock
        /// @includedoc lslconst.xhtml
        /** @ref IW_PRIM_PROJECTOR_FOV */
        public const int IW_PRIM_PROJECTOR_FOV      = 11103;
        /// @page IW_PRIM_PROJECTOR_FOCUS Constant: IW_PRIM_PROJECTOR_FOCUS
        /// @brief The distance, in meters, at which the projected texture starts to blur.
        /// @par "Constant: integer IW_PRIM_PROJECTOR_FOCUS = 11104;"
        /// @parblock
        /// <tt>[ IW_PRIM_PROJECTOR_FOCUS, float focus_dist ]</tt>
        /// @endparblock
        /// @par Caveats
        /// @parblock
        /// @endparblock
        /// @includedoc lslconst.xhtml
        /** @ref IW_PRIM_PROJECTOR_FOCUS */
        public const int IW_PRIM_PROJECTOR_FOCUS    = 11104;
        /// @page IW_PRIM_PROJECTOR_AMBIENCE Constant: IW_PRIM_PROJECTOR_AMBIENCE
        /// @brief The amount of texture-controlled light to put on all faces with the FOV and range of the light.
        /// @par "Constant: integer IW_PRIM_PROJECTOR_AMBIENCE = 11001;"
        /// @parblock
        /// <tt>[ IW_PRIM_PROJECTOR_AMBIENCE, float ambience ]</tt>
        /// Cannot be negative.
        /// @endparblock
        /// @par Caveats
        /// @parblock
        /// @endparblock
        /// @includedoc lslconst.xhtml
        /** @ref IW_PRIM_PROJECTOR_AMBIENCE */
        public const int IW_PRIM_PROJECTOR_AMBIENCE = 11105;

        public const int PRIM_TEXGEN_DEFAULT = 0;
        public const int PRIM_TEXGEN_PLANAR = 1;

        public const int PRIM_TYPE_BOX = 0;
        public const int PRIM_TYPE_CYLINDER = 1;
        public const int PRIM_TYPE_PRISM = 2;
        public const int PRIM_TYPE_SPHERE = 3;
        public const int PRIM_TYPE_TORUS = 4;
        public const int PRIM_TYPE_TUBE = 5;
        public const int PRIM_TYPE_RING = 6;
        public const int PRIM_TYPE_SCULPT = 7;

        public const int PRIM_HOLE_DEFAULT = 0;
        public const int PRIM_HOLE_CIRCLE = 16;
        public const int PRIM_HOLE_SQUARE = 32;
        public const int PRIM_HOLE_TRIANGLE = 48;

        public const int PRIM_MATERIAL_STONE = 0;
        public const int PRIM_MATERIAL_METAL = 1;
        public const int PRIM_MATERIAL_GLASS = 2;
        public const int PRIM_MATERIAL_WOOD = 3;
        public const int PRIM_MATERIAL_FLESH = 4;
        public const int PRIM_MATERIAL_PLASTIC = 5;
        public const int PRIM_MATERIAL_RUBBER = 6;
        public const int PRIM_MATERIAL_LIGHT = 7;

        public const int PRIM_SHINY_NONE = 0;
        public const int PRIM_SHINY_LOW = 1;
        public const int PRIM_SHINY_MEDIUM = 2;
        public const int PRIM_SHINY_HIGH = 3;
        public const int PRIM_BUMP_NONE = 0;
        public const int PRIM_BUMP_BRIGHT = 1;
        public const int PRIM_BUMP_DARK = 2;
        public const int PRIM_BUMP_WOOD = 3;
        public const int PRIM_BUMP_BARK = 4;
        public const int PRIM_BUMP_BRICKS = 5;
        public const int PRIM_BUMP_CHECKER = 6;
        public const int PRIM_BUMP_CONCRETE = 7;
        public const int PRIM_BUMP_TILE = 8;
        public const int PRIM_BUMP_STONE = 9;
        public const int PRIM_BUMP_DISKS = 10;
        public const int PRIM_BUMP_GRAVEL = 11;
        public const int PRIM_BUMP_BLOBS = 12;
        public const int PRIM_BUMP_SIDING = 13;
        public const int PRIM_BUMP_LARGETILE = 14;
        public const int PRIM_BUMP_STUCCO = 15;
        public const int PRIM_BUMP_SUCTION = 16;
        public const int PRIM_BUMP_WEAVE = 17;

        public const int PRIM_SCULPT_TYPE_SPHERE = 1;
        public const int PRIM_SCULPT_TYPE_TORUS = 2;
        public const int PRIM_SCULPT_TYPE_PLANE = 3;
        public const int PRIM_SCULPT_TYPE_CYLINDER = 4;
        public const int PRIM_SCULPT_FLAG_INVERT = 64;
        public const int PRIM_SCULPT_FLAG_MIRROR = 128;

        public const int PRIM_PHYSICS_SHAPE_PRIM = 0;
        public const int PRIM_PHYSICS_SHAPE_NONE = 1;
        public const int PRIM_PHYSICS_SHAPE_CONVEX = 2;

        public const int PRIM_ALPHA_MODE_NONE = 0;
        public const int PRIM_ALPHA_MODE_BLEND = 1;
        public const int PRIM_ALPHA_MODE_MASK = 2;
        public const int PRIM_ALPHA_MODE_EMISSIVE = 3;

        public const int MASK_BASE = 0;
        public const int MASK_OWNER = 1;
        public const int MASK_GROUP = 2;
        public const int MASK_EVERYONE = 3;
        public const int MASK_NEXT = 4;

        public const int PERM_TRANSFER = 8192;
        public const int PERM_MODIFY = 16384;
        public const int PERM_COPY = 32768;
        public const int PERM_MOVE = 524288;
        public const int PERM_ALL = 2147483647;

        public const int PARCEL_MEDIA_COMMAND_STOP = 0;
        public const int PARCEL_MEDIA_COMMAND_PAUSE = 1;
        public const int PARCEL_MEDIA_COMMAND_PLAY = 2;
        public const int PARCEL_MEDIA_COMMAND_LOOP = 3;
        public const int PARCEL_MEDIA_COMMAND_TEXTURE = 4;
        public const int PARCEL_MEDIA_COMMAND_URL = 5;
        public const int PARCEL_MEDIA_COMMAND_TIME = 6;
        public const int PARCEL_MEDIA_COMMAND_AGENT = 7;
        public const int PARCEL_MEDIA_COMMAND_UNLOAD = 8;
        public const int PARCEL_MEDIA_COMMAND_AUTO_ALIGN = 9;
        public const int PARCEL_MEDIA_COMMAND_TYPE = 10;
        public const int PARCEL_MEDIA_COMMAND_SIZE = 11;
        public const int PARCEL_MEDIA_COMMAND_DESC = 12;

        public const int PARCEL_FLAG_ALLOW_FLY = 0x1;                           // parcel allows flying
        public const int PARCEL_FLAG_ALLOW_SCRIPTS = 0x2;                       // parcel allows outside scripts
        public const int PARCEL_FLAG_ALLOW_LANDMARK = 0x8;                      // parcel allows landmarks to be created
        public const int PARCEL_FLAG_ALLOW_TERRAFORM = 0x10;                    // parcel allows anyone to terraform the land
        public const int PARCEL_FLAG_ALLOW_DAMAGE = 0x20;                       // parcel allows damage
        public const int PARCEL_FLAG_ALLOW_CREATE_OBJECTS = 0x40;               // parcel allows anyone to create objects
        public const int PARCEL_FLAG_USE_ACCESS_GROUP = 0x100;                  // parcel limits access to a group
        public const int PARCEL_FLAG_USE_ACCESS_LIST = 0x200;                   // parcel limits access to a list of residents
        public const int PARCEL_FLAG_USE_BAN_LIST = 0x400;                      // parcel uses a ban list, including restricting access based on payment info
        public const int PARCEL_FLAG_USE_LAND_PASS_LIST = 0x800;                // parcel allows passes to be purchased
        public const int PARCEL_FLAG_LOCAL_SOUND_ONLY = 0x8000;                 // parcel restricts spatialized sound to the parcel
        public const int PARCEL_FLAG_RESTRICT_PUSHOBJECT = 0x200000;            // parcel restricts llPushObject
        public const int PARCEL_FLAG_ALLOW_GROUP_SCRIPTS = 0x2000000;           // parcel allows scripts owned by group
        public const int PARCEL_FLAG_ALLOW_CREATE_GROUP_OBJECTS = 0x4000000;    // parcel allows group object creation
        public const int PARCEL_FLAG_ALLOW_ALL_OBJECT_ENTRY = 0x8000000;        // parcel allows objects owned by any user to enter
        public const int PARCEL_FLAG_ALLOW_GROUP_OBJECT_ENTRY = 0x10000000;     // parcel allows with the same group to enter

        public const int REGION_FLAG_ALLOW_DAMAGE = 0x1;                        // region is entirely damage enabled
        public const int REGION_FLAG_FIXED_SUN = 0x10;                          // region has a fixed sun position
        public const int REGION_FLAG_BLOCK_TERRAFORM = 0x40;                    // region terraforming disabled
        public const int REGION_FLAG_SANDBOX = 0x100;                           // region is a sandbox
        public const int REGION_FLAG_DISABLE_COLLISIONS = 0x1000;               // region has disabled collisions
        public const int REGION_FLAG_DISABLE_PHYSICS = 0x4000;                  // region has disabled physics
        public const int REGION_FLAG_BLOCK_FLY = 0x80000;                       // region blocks flying
        public const int REGION_FLAG_ALLOW_DIRECT_TELEPORT = 0x100000;          // region allows direct teleports
        public const int REGION_FLAG_RESTRICT_PUSHOBJECT = 0x400000;            // region restricts llPushObject

        public static readonly LSLInteger PAY_HIDE = new LSLInteger(-1);
        public static readonly LSLInteger PAY_DEFAULT = new LSLInteger(-2);

        public const string NULL_KEY = "00000000-0000-0000-0000-000000000000";
        public const string EOF = "
        ///
        ///
        ///";
        public const double PI = 3.14159274f;
        public const double TWO_PI = 6.28318548f;
        public const double PI_BY_TWO = 1.57079637f;
        public const double DEG_TO_RAD = 0.01745329238f;
        public const double RAD_TO_DEG = 57.29578f;
        public const double SQRT2 = 1.414213538f;
        public const int STRING_TRIM_HEAD = 1;
        public const int STRING_TRIM_TAIL = 2;
        public const int STRING_TRIM = 3;
        public const int LIST_STAT_RANGE = 0;
        public const int LIST_STAT_MIN = 1;
        public const int LIST_STAT_MAX = 2;
        public const int LIST_STAT_MEAN = 3;
        public const int LIST_STAT_MEDIAN = 4;
        public const int LIST_STAT_STD_DEV = 5;
        public const int LIST_STAT_SUM = 6;
        public const int LIST_STAT_SUM_SQUARES = 7;
        public const int LIST_STAT_NUM_COUNT = 8;
        public const int LIST_STAT_GEOMETRIC_MEAN = 9;
        public const int LIST_STAT_HARMONIC_MEAN = 100;

        //ParcelPrim Categories
        public const int PARCEL_COUNT_TOTAL = 0;
        public const int PARCEL_COUNT_OWNER = 1;
        public const int PARCEL_COUNT_GROUP = 2;
        public const int PARCEL_COUNT_OTHER = 3;
        public const int PARCEL_COUNT_SELECTED = 4;
        public const int PARCEL_COUNT_TEMP = 5;

        public const int DEBUG_CHANNEL = 0x7FFFFFFF;
        public const int PUBLIC_CHANNEL = 0x00000000;

        // http://wiki.secondlife.com/wiki/LlGetObjectDetails
        public const int OBJECT_NAME = 1;
        public const int OBJECT_DESC = 2;
        public const int OBJECT_POS = 3;
        public const int OBJECT_ROT = 4;
        public const int OBJECT_VELOCITY = 5;
        public const int OBJECT_OWNER = 6;
        public const int OBJECT_GROUP = 7;
        public const int OBJECT_CREATOR = 8;
        public const int OBJECT_RUNNING_SCRIPT_COUNT = 9;
        public const int OBJECT_TOTAL_SCRIPT_COUNT = 10;
        public const int OBJECT_SCRIPT_MEMORY = 11;     // http://wiki.secondlife.com/wiki/LSL_Script_Memory
        public const int OBJECT_SCRIPT_TIME = 12;
        public const int OBJECT_PRIM_EQUIVALENCE = 13;
        public const int OBJECT_SERVER_COST = 14;       // http://wiki.secondlife.com/wiki/Mesh/Mesh_Server_Weight
        public const int OBJECT_STREAMING_COST = 15;    // http://wiki.secondlife.com/wiki/Mesh/Mesh_Streaming_Cost
        public const int OBJECT_PHYSICS_COST = 16;      // http://wiki.secondlife.com/wiki/Mesh/Mesh_physics
        public const int OBJECT_CHARACTER_TIME = 17;
        public const int OBJECT_ROOT = 18;
        public const int OBJECT_ATTACHED_POINT = 19;
        public const int OBJECT_PATHFINDING_TYPE = 20;
        public const int OBJECT_PHYSICS = 21;
        public const int OBJECT_PHANTOM = 22;
        public const int OBJECT_TEMP_ON_REZ = 23;
        public const int OBJECT_RENDER_WEIGHT = 24;
        public const int OBJECT_HOVER_HEIGHT = 25;
        public const int OBJECT_BODY_SHAPE_TYPE = 26;
        public const int OBJECT_LAST_OWNER_ID = 27;
        public const int OBJECT_CLICK_ACTION = 28;
        public const int IW_OBJECT_SCRIPT_MEMORY_USED = 10001;

        // Values for llGetObjectDetails(OBJECT_PATHFINDING_TYPE) above
        public const int OPT_OTHER = -1;            // Attachments, Linden trees & grass
        public const int OPT_LEGACY_LINKSET = 0;    // Movable obstacles, movable phantoms, physical, and volumedetect objects
        public const int OPT_AVATAR = 1;            // Avatars
        public const int OPT_CHARACTER = 2;         // Pathfinding characters
        public const int OPT_WALKABLE = 3;          // Walkable objects
        public const int OPT_STATIC_OBSTACLE = 4;   // Static obstacles
        public const int OPT_MATERIAL_VOLUME = 5;   // Material volumes
        public const int OPT_EXCLUSION_VOLUME = 6;

        // Can not be public const?
        public static readonly vector ZERO_VECTOR = new vector(0.0, 0.0, 0.0);
        public static readonly rotation ZERO_ROTATION = new rotation(0.0, 0.0, 0.0, 1.0);

        // constants for llSetCameraParams
        public const int CAMERA_PITCH = 0;
        public const int CAMERA_FOCUS_OFFSET = 1;
        public const int CAMERA_FOCUS_OFFSET_X = 2;
        public const int CAMERA_FOCUS_OFFSET_Y = 3;
        public const int CAMERA_FOCUS_OFFSET_Z = 4;
        public const int CAMERA_POSITION_LAG = 5;
        public const int CAMERA_FOCUS_LAG = 6;
        public const int CAMERA_DISTANCE = 7;
        public const int CAMERA_BEHINDNESS_ANGLE = 8;
        public const int CAMERA_BEHINDNESS_LAG = 9;
        public const int CAMERA_POSITION_THRESHOLD = 10;
        public const int CAMERA_FOCUS_THRESHOLD = 11;
        public const int CAMERA_ACTIVE = 12;
        public const int CAMERA_POSITION = 13;
        public const int CAMERA_POSITION_X = 14;
        public const int CAMERA_POSITION_Y = 15;
        public const int CAMERA_POSITION_Z = 16;
        public const int CAMERA_FOCUS = 17;
        public const int CAMERA_FOCUS_X = 18;
        public const int CAMERA_FOCUS_Y = 19;
        public const int CAMERA_FOCUS_Z = 20;
        public const int CAMERA_POSITION_LOCKED = 21;
        public const int CAMERA_FOCUS_LOCKED = 22;

        // constants for llGetParcelDetails
        public const int PARCEL_DETAILS_NAME = 0;
        public const int PARCEL_DETAILS_DESC = 1;
        public const int PARCEL_DETAILS_OWNER = 2;
        public const int PARCEL_DETAILS_GROUP = 3;
        public const int PARCEL_DETAILS_AREA = 4;
        public const int PARCEL_DETAILS_ID = 5;
        public const int PARCEL_DETAILS_SEE_AVATARS = 6;

        // constants for llSetClickAction
        public const int CLICK_ACTION_NONE = 0;
        public const int CLICK_ACTION_TOUCH = 0;
        public const int CLICK_ACTION_SIT = 1;
        public const int CLICK_ACTION_BUY = 2;
        public const int CLICK_ACTION_PAY = 3;
        public const int CLICK_ACTION_OPEN = 4;
        public const int CLICK_ACTION_PLAY = 5;
        public const int CLICK_ACTION_OPEN_MEDIA = 6;
        public const int CLICK_ACTION_ZOOM = 7;

        // constants for the llDetectedTouch* functions
        public const int TOUCH_INVALID_FACE = -1;
        public static readonly vector TOUCH_INVALID_TEXCOORD = new vector(-1.0, -1.0, 0.0);
        public static readonly vector TOUCH_INVALID_VECTOR = ZERO_VECTOR;

        // constants for llGetPrimMediaParams/llSetPrimMediaParams
        public const int PRIM_MEDIA_ALT_IMAGE_ENABLE = 0;
        public const int PRIM_MEDIA_CONTROLS = 1;
        public const int PRIM_MEDIA_CURRENT_URL = 2;
        public const int PRIM_MEDIA_HOME_URL = 3;
        public const int PRIM_MEDIA_AUTO_LOOP = 4;
        public const int PRIM_MEDIA_AUTO_PLAY = 5;
        public const int PRIM_MEDIA_AUTO_SCALE = 6;
        public const int PRIM_MEDIA_AUTO_ZOOM = 7;
        public const int PRIM_MEDIA_FIRST_CLICK_INTERACT = 8;
        public const int PRIM_MEDIA_WIDTH_PIXELS = 9;
        public const int PRIM_MEDIA_HEIGHT_PIXELS = 10;
        public const int PRIM_MEDIA_WHITELIST_ENABLE = 11;
        public const int PRIM_MEDIA_WHITELIST = 12;
        public const int PRIM_MEDIA_PERMS_INTERACT = 13;
        public const int PRIM_MEDIA_PERMS_CONTROL = 14;

        public const int PRIM_MEDIA_CONTROLS_STANDARD = 0;
        public const int PRIM_MEDIA_CONTROLS_MINI = 1;

        public const int PRIM_MEDIA_PERM_NONE = 0;
        public const int PRIM_MEDIA_PERM_OWNER = 1;
        public const int PRIM_MEDIA_PERM_GROUP = 2;
        public const int PRIM_MEDIA_PERM_ANYONE = 4;

        // extra constants for llSetPrimMediaParams
        public static readonly LSLInteger LSL_STATUS_OK = new LSLInteger(0);
        public static readonly LSLInteger LSL_STATUS_MALFORMED_PARAMS = new LSLInteger(1000);
        public static readonly LSLInteger LSL_STATUS_TYPE_MISMATCH = new LSLInteger(1001);
        public static readonly LSLInteger LSL_STATUS_BOUNDS_ERROR = new LSLInteger(1002);
        public static readonly LSLInteger LSL_STATUS_NOT_FOUND = new LSLInteger(1003);
        public static readonly LSLInteger LSL_STATUS_NOT_SUPPORTED = new LSLInteger(1004);
        public static readonly LSLInteger LSL_STATUS_INTERNAL_ERROR = new LSLInteger(1999);
        public static readonly LSLInteger LSL_STATUS_WHITELIST_FAILED = new LSLInteger(2001);

        // Constants for default textures
        public const string TEXTURE_BLANK = "5748decc-f629-461c-9a36-a35a221fe21f";
        public const string TEXTURE_DEFAULT = "89556747-24cb-43ed-920b-47caed15465f";
        public const string TEXTURE_PLYWOOD = "89556747-24cb-43ed-920b-47caed15465f";
        public const string TEXTURE_TRANSPARENT = "8dcd4a48-2d37-4909-9f78-f7a9eb4ef903";
        public const string TEXTURE_MEDIA = "8b5fec65-8d8d-9dc5-cda8-8fdf2716e361";

        // llGetAgentList and iwGetAgentList scopes
        public const int AGENT_LIST_PARCEL = 1;
        public const int AGENT_LIST_PARCEL_OWNER = 2;
        public const int AGENT_LIST_REGION = 4;

        // For llManageEstateAccess -- Warning, the constant values do not match SL, they should have been bit masks
        // to permit simultaneous add/remove, but it is too late to change that
        public const int ESTATE_ACCESS_ALLOWED_AGENT_ADD = 0;
        public const int ESTATE_ACCESS_ALLOWED_AGENT_REMOVE = 1;
        public const int ESTATE_ACCESS_ALLOWED_GROUP_ADD = 2;
        public const int ESTATE_ACCESS_ALLOWED_GROUP_REMOVE = 3;
        public const int ESTATE_ACCESS_BANNED_AGENT_ADD = 4;
        public const int ESTATE_ACCESS_BANNED_AGENT_REMOVE = 5;
        /// @page ESTATE_ACCESS_QUERY_CAN_MANAGE Constant: ESTATE_ACCESS_QUERY_CAN_MANAGE
        /// @brief Whether the script can manage the estate.
        /// @par "Constant: integer ESTATE_ACCESS_QUERY_CAN_MANAGE = 11000;"
        /// @parblock
        /// @endparblock
        /// @par Caveats
        /// @parblock
        /// @endparblock
        /// @includedoc lslconst.xhtml
        /** @ref ESTATE_ACCESS_QUERY_CAN_MANAGE */
        public const int ESTATE_ACCESS_QUERY_CAN_MANAGE = 11000;
        /// @page ESTATE_ACCESS_QUERY_ALLOWED_AGENT Constant: ESTATE_ACCESS_QUERY_ALLOWED_AGENT
        /// @brief TODO: Short description
        /// @par "Constant: integer ESTATE_ACCESS_QUERY_ALLOWED_AGENT = 11001;"
        /// @parblock
        /// @endparblock
        /// @par Caveats
        /// @parblock
        /// @endparblock
        /// @includedoc lslconst.xhtml
        /** @ref ESTATE_ACCESS_QUERY_ALLOWED_AGENT */
        public const int ESTATE_ACCESS_QUERY_ALLOWED_AGENT = 11001;
        /// @page ESTATE_ACCESS_QUERY_ALLOWED_GROUP Constant: ESTATE_ACCESS_QUERY_ALLOWED_GROUP
        /// @brief TODO: Short description
        /// @par "Constant: integer ESTATE_ACCESS_QUERY_ALLOWED_GROUP = 11002;"
        /// @parblock
        /// @endparblock
        /// @par Caveats
        /// @parblock
        /// @endparblock
        /// @includedoc lslconst.xhtml
        /** @ref ESTATE_ACCESS_QUERY_ALLOWED_GROUP */
        public const int ESTATE_ACCESS_QUERY_ALLOWED_GROUP = 11002;
        /// @page ESTATE_ACCESS_QUERY_BANNED_AGENT Constant: ESTATE_ACCESS_QUERY_BANNED_AGENT
        /// @brief Whether the script can ban agents.
        /// @par "Constant: integer ESTATE_ACCESS_QUERY_BANNED_AGENT = 11003;"
        /// @parblock
        /// @endparblock
        /// @par Caveats
        /// @parblock
        /// @endparblock
        /// @includedoc lslconst.xhtml
        /** @ref ESTATE_ACCESS_QUERY_BANNED_AGENT */
        public const int ESTATE_ACCESS_QUERY_BANNED_AGENT = 11003;

        // llJsonXXX
        public const string JSON_INVALID = "\uFDD0"; 
        public const string JSON_OBJECT = "\uFDD1";
        public const string JSON_ARRAY = "\uFDD2";
        public const string JSON_NUMBER = "\uFDD3";
        public const string JSON_STRING = "\uFDD4";
        public const string JSON_NULL = "\uFDD5";
        public const string JSON_TRUE = "\uFDD6";
        public const string JSON_FALSE = "\uFDD7";
        public const string JSON_DELETE = "\uFDD8";

        public const int JSON_APPEND = -1;

        // llSetContentType content types
        public const int CONTENT_TYPE_TEXT  = 0;
        public const int CONTENT_TYPE_HTML  = 1;
        public const int CONTENT_TYPE_XML   = 2;
        public const int CONTENT_TYPE_XHTML = 3;
        public const int CONTENT_TYPE_ATOM  = 4;
        public const int CONTENT_TYPE_JSON  = 5;
        public const int CONTENT_TYPE_LLSD  = 6;
        public const int CONTENT_TYPE_FORM  = 7;
        public const int CONTENT_TYPE_RSS   = 8;

        // iwSetWind types
        public const int WIND_SPEED_DEFAULT = 0;
        public const int WIND_SPEED_FIXED = 1;

        public const int RC_REJECT_TYPES = 0;
        public const int RC_DETECT_PHANTOM = 1;
        public const int RC_DATA_FLAGS = 2;
        public const int RC_MAX_HITS = 3;

        public const int RC_REJECT_AGENTS = 1;
        public const int RC_REJECT_PHYSICAL = 2;
        public const int RC_REJECT_NONPHYSICAL = 4;
        public const int RC_REJECT_LAND = 8;

        public const int RC_GET_NORMAL = 1;
        public const int RC_GET_ROOT_KEY = 2;
        public const int RC_GET_LINK_NUM = 4;

        public const int RCERR_UNKNOWN = -1;
        public const int RCERR_SIM_PERF_LOW = -2;
        public const int RCERR_CAST_TIME_EXCEEDED = -3;

        public const int KFM_ROTATION = 1;
        public const int KFM_TRANSLATION = 2;

        public const int KFM_COMMAND = 0;
        public const int KFM_MODE = 1;
        public const int KFM_DATA = 2;

        public const int KFM_FORWARD = 0;
        public const int KFM_LOOP = 1;
        public const int KFM_PING_PONG = 2;
        public const int KFM_REVERSE = 3;

        public const int KFM_CMD_PLAY = 0;
        public const int KFM_CMD_STOP = 1;
        public const int KFM_CMD_PAUSE = 2;

        public const int BOT_ERROR = -3;
        public const int BOT_USER_NOT_FOUND = -2;
        public const int BOT_NOT_FOUND = -1;
        public const int BOT_SUCCESS = 0;

        public const int BOT_ALLOW_RUNNING = 1;
        public const int BOT_ALLOW_FLYING = 2;
        public const int BOT_ALLOW_JUMPING = 3;
        public const int BOT_FOLLOW_OFFSET = 4;
        public const int BOT_REQUIRES_LINE_OF_SIGHT = 5;
        public const int BOT_START_FOLLOWING_DISTANCE = 6;
        public const int BOT_STOP_FOLLOWING_DISTANCE = 7;
        public const int BOT_LOST_AVATAR_DISTANCE = 8;

        public const int BOT_TRAVELMODE_WALK = 1;
        public const int BOT_TRAVELMODE_RUN = 2;
        public const int BOT_TRAVELMODE_FLY = 3;
        public const int BOT_TRAVELMODE_TELEPORT = 4;
        public const int BOT_TRAVELMODE_WAIT = 5;

        public const int BOT_MOVEMENT_TYPE = 0;
        public const int BOT_MOVEMENT_TELEPORT_AFTER = 1;

        public const int BOT_MOVEMENT_FLAG_NONE = 0;
        public const int BOT_MOVEMENT_FLAG_FOLLOW_INDEFINITELY = 1;

        public const int BOT_CREATE_DEFAULT = 0;
        public const int BOT_CREATE_NO_OWNER = 1;

        public const int BOT_MOVE_COMPLETE = 1;
        public const int BOT_MOVE_UPDATE = 2;
        public const int BOT_MOVE_FAILED = 3;
        public const int BOT_MOVE_AVATAR_LOST = 4;

        public const int BOT_WANDER_MOVEMENT_TYPE = 1;
        public const int BOT_WANDER_TIME_BETWEEN_NODES = 2;

        public const int BOT_ABOUT_TEXT = 1;
        public const int BOT_EMAIL = 2;
        public const int BOT_IMAGE_UUID = 3;
        public const int BOT_PROFILE_URL = 4;

        // Return codes for iwDeliverInventory and iwDeliverInventoryList
        public const int IW_DELIVER_OK = 0;
        public const int IW_DELIVER_BADKEY = 1;
        public const int IW_DELIVER_MUTED = 2;
        public const int IW_DELIVER_ITEM = 3;
        public const int IW_DELIVER_PRIM = 4;
        public const int IW_DELIVER_USER = 5;
        public const int IW_DELIVER_PERM = 6;
        public const int IW_DELIVER_NONE = 7;

        // Used by llReturnObjectsByOwner
        public const int OBJECT_RETURN_PARCEL = 1;
        public const int OBJECT_RETURN_PARCEL_OWNER = 2;
        public const int OBJECT_RETURN_REGION = 4;

        // Returned by llReturnObjectsByOwner and llReturnObjectsByID
        public const int ERR_GENERIC = -1;
        public const int ERR_PARCEL_PERMISSIONS = -2;
        public const int ERR_MALFORMED_PARAMS = -3;
        public const int ERR_RUNTIME_PERMISSIONS = -4;
        public const int ERR_THROTTLED = -5;
   }
}
