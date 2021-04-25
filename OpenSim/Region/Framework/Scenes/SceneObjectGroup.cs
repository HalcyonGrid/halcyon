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
using System.IO;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;
using OpenMetaverse;
using OpenMetaverse.Packets;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Physics.Manager;
using OpenSim.Framework.Geom;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace OpenSim.Region.Framework.Scenes
{
    struct ScriptPosTarget
    {
        public Vector3 TargetPos;
        public float Tolerance;
        public int Handle;
    }

    struct ScriptRotTarget
    {
        public Quaternion TargetRot;
        public float Tolerance;
        public int Handle;
    }

    public delegate void PrimCountTaintedDelegate();

    /// <summary>
    /// A scene object group is conceptually an object in the scene.  The object is constituted of SceneObjectParts
    /// (often known as prims), one of which is considered the root part.
    /// </summary>
    public partial class SceneObjectGroup : EntityBase, ISceneObject
    {
        /// <summary>
        /// Signal whether the non-inventory attributes of any prims in the group have changed
        /// since the group's last persistent backup
        /// </summary>
        private bool m_hasGroupChanged = false;
        private long timeFirstChanged;
        private long timeLastChanged;

        /// <summary>
        /// Has this object been persisted to the database? (used for deletes)
        /// </summary>
        [XmlIgnore]
        public bool IsPersisted { get; set; }

        /// <summary>
        /// Stored in object2 PUT message, used in object2 DELETE
        /// </summary>
        [XmlIgnore]
        public long NonceID = 0;

        public bool HasGroupChanged
        {
            set
            {
                if (value)
                {
                    timeLastChanged = DateTime.Now.Ticks;
                    if (!m_hasGroupChanged)
                        timeFirstChanged = DateTime.Now.Ticks;

                    //when a new object is rezzed to world, it's group is set before it is
                    //attached to the scene to prevent a race condition.  In this state
                    //m_scene is not set
                    if (m_scene != null && m_isBackedUp)
                    {
                        m_scene.SetGroupTainted(this);
                    }
                }
                m_hasGroupChanged = value;
            }

            get { return m_hasGroupChanged; }
        }

        public bool DisableUpdates { get; set; }

        [XmlIgnore]
        public bool TaintedAttachment { get; set; }

        private bool isTimeToPersist()
        {
            if (IsDeleted || IsAttachment)
                return false;
            if (!m_hasGroupChanged)
                return false;
            if (m_scene.ShuttingDown)
                return true;
            long currentTime = DateTime.Now.Ticks;
            if (currentTime - timeLastChanged > m_scene.m_dontPersistBefore || currentTime - timeFirstChanged > m_scene.m_persistAfter)
                return true;
            return false;
        }

        public bool HasPrimRoot
        {
            get { return m_rootPart.HasPrimRoot; }
        }

        public bool IsAttachment
        {
            get { return m_rootPart.IsAttachment; }
        }

        public byte AttachmentPoint
        {
            get { return m_rootPart.AttachmentPoint; }
        }

        public bool IsAttachedHUD
        {
            get { return m_rootPart.IsAttachedHUD; }
        }

        public UUID AttachedAvatar
        {
            get { return m_rootPart.AttachedAvatar; }
        }

        public double scriptScore = 0;
        private const double SAMPLE_MS = 30.0;
        private const int NUM_SCRIPT_SAMPLES = 16;
        private DateTime _scoreLastCleared = DateTime.Now;
        private float[] _samples = new float[NUM_SCRIPT_SAMPLES];
        public int _currSample = 0;

        [XmlIgnore]
        private bool m_isScripted = false;

        private bool m_isBackedUp = false;

        /// <summary>
        /// The constituent parts of this group
        /// </summary>
        private GroupPartsCollection m_childParts = new GroupPartsCollection();
        private AvatarPartsCollection m_childAvatars = new AvatarPartsCollection();
        private Dictionary<UUID, SitTargetInfo> m_sitTargets = new Dictionary<UUID, SitTargetInfo>();

        protected ulong m_regionHandle;
        protected SceneObjectPart m_rootPart;
        // private Dictionary<UUID, scriptEvents> m_scriptEvents = new Dictionary<UUID, scriptEvents>();

        const Int32 MAX_TARGETS = 8;
        private object m_targetsLock = new object();
        private List<ScriptPosTarget> m_targets = new List<ScriptPosTarget>(MAX_TARGETS);
        private List<ScriptRotTarget> m_rotTargets = new List<ScriptRotTarget>(MAX_TARGETS);

        private bool m_scriptListens_atTarget = false;
        private bool m_scriptListens_notAtTarget = false;
        private bool m_scriptListens_atRotTarget = false;
        private bool m_scriptListens_notAtRotTarget = false;

        public Dictionary<UUID, string> m_savedScriptState = null;

        private Box _calculatedBoundingBox = null;
        private object _bbLock = new object();  // this is a leaf lock, careful what is called from inside it
        private Vector3 _bbPos = Vector3.Zero;
        private Quaternion _bbRot = Quaternion.Identity;

        private ILandObject _currentParcel;

        #region Properties

        /// <summary>
        /// The name of an object grouping is always the same as its root part
        /// </summary>
        public override string Name
        {
            get
            {
                if (RootPart == null)
                    return String.Empty;
                return RootPart.Name;
            }
            set {
                RootPart.Name = value;
                // Also update the object name to keep it in sync with the root part name.
                // This mostly only affects debugging since the Name getter override above
                // pulls the name from the root part.
                this.m_name = value;
            }
        }

        // when a prim enters a new region, this is the number of avatars that must be seated (waited for) before the prim can exit the region
        private int m_avatarsToExpect = 0;
        public int AvatarsToExpect
        {
            get { return m_avatarsToExpect; }
            set { m_avatarsToExpect = value; }
        }

        public int RidingAvatarArrivedFromOtherSim()
        {
            return Interlocked.Decrement(ref m_avatarsToExpect);
        }

        /// <summary>
        /// Is this group on it's way to another sim?
        /// </summary>
        private int m_InTransition = 0;    // start all prims as in transition
        public bool InTransit
        {
            get { return m_InTransition != 0; }
        }

        public bool StartTransit()
        {
            if (Interlocked.CompareExchange(ref m_InTransition, 1, 0) == 0)
            {
                //suspend physics
                PhysicsActor physActor = this.RootPart.PhysActor;
                if (physActor != null)
                {
                    physActor.Suspend();
                }

                m_scene.EventManager.TriggerOnGroupBeginInTransit(this);
                return true;
            }

            return false;
        }
        public bool EndTransit(bool transitSuccess)
        {
            if (Interlocked.CompareExchange(ref m_InTransition, 0, 1) == 1)
            {
                if (!transitSuccess)    //this object failed to cross. restore physics
                {
                    PhysicsActor physActor = this.RootPart.PhysActor;
                    if (physActor != null)
                    {
                        physActor.Resume(false, null);
                    }
                }

                m_scene.EventManager.TriggerOnGroupEndInTransit(this, transitSuccess);

                if (!transitSuccess)
                {
                    // The group was marked as InTransit, so when it's original position was restored, 
                    // the update didn't make it to the viewer(s). Now transit is complete, we can
                    // send it now so the viewer knows where it is.
                    ScheduleGroupForFullUpdate(PrimUpdateFlags.ForcedFullUpdate);
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Added because the Parcel code seems to use it
        /// but not sure a object should have this
        /// as what does it tell us? that some avatar has selected it (but not what Avatar/user)
        /// think really there should be a list (or whatever) in each scenepresence
        /// saying what prim(s) that user has selected.
        /// </summary>
        protected bool m_isSelected = false;

        public ScriptEvents GroupScriptEvents
        {
            get
            {
                return m_aggregateScriptEvents;
            }
        }

        private ScriptEvents m_aggregateScriptEvents;

        /// <summary>
        /// Returns whether or not the aggregated script events for this group contain
        /// handlers for any collision events
        /// </summary>
        public bool WantsCollisionEvents
        {
            get
            {
                return ((m_aggregateScriptEvents & ScriptEvents.collision) != 0) ||
                    ((m_aggregateScriptEvents & ScriptEvents.collision_start) != 0) ||
                    ((m_aggregateScriptEvents & ScriptEvents.collision_end) != 0) ||
                    ((m_aggregateScriptEvents & ScriptEvents.land_collision) != 0) ||
                    ((m_aggregateScriptEvents & ScriptEvents.land_collision_start) != 0) ||
                    ((m_aggregateScriptEvents & ScriptEvents.land_collision_end) != 0);
            }
        }

        /// <summary>
        /// Returns whether or not the aggregated script events for this group contain
        /// handlers for collision or land_collision
        /// </summary>
        public bool WantsRepeatingCollisionEvents
        {
            get
            {
                return ((m_aggregateScriptEvents & ScriptEvents.collision) != 0) ||
                    ((m_aggregateScriptEvents & ScriptEvents.land_collision) != 0);
            }
        }

        /// <summary>
        /// Number of parts in this group
        /// </summary>
        public int PartCount
        {
            get
            {
                return m_childParts.Count;
            }
        }

        /// <summary>
        /// Number of avatars seated on this group
        /// </summary>
        public int AvatarCount
        {
            get
            {
                return m_childAvatars.Count;
            }
        }

        /// <summary>
        /// Number of parts in this group
        /// </summary>
        public int LinkCount
        {
            get
            {
                return m_childParts.Count + m_childAvatars.Count;
            }
        }

        /// <summary>
        /// Server Weight. For Mesh we use a value of 422 (in InventoryCapsModule) verts = 1.0f.  Default to 1.0
        /// for regular prims.  We will set this value in the mesh uploader if it should be changed.
        /// </summary>
        private static float WEIGHT_NOT_SET = -1.0f;    // -1 is a sentinel value meaning "not set yet"
        private float m_serverWeight = WEIGHT_NOT_SET;  // -1 is a sentinel value meaning "not set yet"
        public virtual float ServerWeight
        {
            get
            {
                if (m_serverWeight == WEIGHT_NOT_SET)
                    RecalcPrimWeights();

                return m_serverWeight;
            }
        }

        public void ServerWeightDelta(float delta)
        {
            if (delta == 0.0f) return;    // no change, let's avoid the silly message below which fills consoles

            m_serverWeight += delta;
            if (m_serverWeight < 0.0f)
                m_log.Warn("[SCENE OBJECT GROUP]: ServerWeight is " + m_serverWeight.ToString() + " after delta of " + delta.ToString() + " for "+this.LocalId.ToString());
        }

        /// <summary>
        /// Streaming Cost. Calculated based on a triangle budget and the LOD levels specified.
        /// We will set this value in the mesh uploader if it should be changed.
        /// </summary>
        private float m_streamingCost = WEIGHT_NOT_SET;  // -1 is a sentinel value meaning "not set yet"
        public virtual float StreamingCost
        {
            get
            {
                if (m_streamingCost == WEIGHT_NOT_SET)
                    RecalcPrimWeights();

                return m_streamingCost;
            }
        }

        public void StreamingCostDelta(float delta)
        {
            if (delta == 0.0f) return;    // no change, let's avoid the silly message below which fills consoles

            m_streamingCost += delta;
            if (m_streamingCost < 0.0f)
                m_log.Warn("[SCENE OBJECT GROUP]: StreamingCost is " + m_streamingCost.ToString() + " after delta of " + delta.ToString() + " for " + this.LocalId.ToString());
        }

        /// <summary>
        /// Land Impact.  Calculated from Server Weight. May be different from PrimCount.
        /// This value is calculated so not persisted.
        /// </summary>
        private float m_landCost = WEIGHT_NOT_SET;      // -1 is a sentinel value meaning "not set yet"

        [XmlIgnore]
        public int LandImpact
        {
            get
            {
                if (m_landCost == WEIGHT_NOT_SET)
                    RecalcPrimWeights();

                return (int)Math.Ceiling(m_landCost);
            }
        }

        public void LandCostDelta(float delta)
        {
            m_landCost += delta;
            if (m_landCost < 0.0f)
            {
                m_log.Warn("[SCENE OBJECT GROUP]: LandCost (LI) is " + m_landCost.ToString() + " after delta of " + delta.ToString() + " for " + this.LocalId.ToString());
            }
        }

        // Recalculate the Server, Streaming and Land Cost weights.
        public void RecalcPrimWeights()
        {
            float server_weight = 0.0f;
            float streaming_cost = 0.0f;
            float land_cost = 0.0f;

            m_childParts.ForEachPart((SceneObjectPart part) => {
                server_weight += part.ServerWeight;
                streaming_cost += part.StreamingCost;
                land_cost += part.LandCost;
            });

            m_serverWeight = server_weight;
            m_streamingCost = streaming_cost;
            m_landCost = land_cost;
        }

        public Quaternion GroupRotation
        {
            get { return m_rootPart.RotationOffset; }
        }

        public UUID GroupID
        {
            get { return m_rootPart.GroupID; }
            set { m_rootPart.GroupID = value; }
        }

        public bool IsGroupDeeded
        {
            get { return (GroupID != UUID.Zero) && (OwnerID == GroupID); }
        }

        /// <value>
        /// The root part of this scene object
        /// </value>
        public SceneObjectPart RootPart
        {
            get { return m_rootPart; }
        }

        // Returns seconds, and and group value represents the oldest prim
        public uint TimeStamp
        {
            get
            {
                uint groupTimeStamp = (uint)Util.UnixTimeSinceEpoch();

                m_childParts.ForEachPart((SceneObjectPart part) =>
                {
                    if (part.TimeStamp < groupTimeStamp)
                        groupTimeStamp = part.TimeStamp;
                });

                return groupTimeStamp;
            }
        }

        public ulong RegionHandle
        {
            get { return m_regionHandle; }
            set
            {
                m_regionHandle = value;
                m_childParts.ForEachPart((SceneObjectPart part) =>
                {
                    part.RegionHandle = m_regionHandle;
                });
            }
        }

        public bool IsBelowGround
        {
            get
            {
                if (m_scene != null)
                {
                    Vector3 pos = this.AbsolutePosition;
                    return pos.Z < m_scene.Heightmap.CalculateHeightAt(pos.X, pos.Y);
                }

                return false;
            }
        }

        /// <summary>
        /// The position of the entire group that this prim belongs to.
        /// Like GroupPosition except never based on the attached avatar position.
        /// </summary>
        public Vector3 RawGroupPosition
        {
            get
            {
                if (m_rootPart == null)
                {
                    throw new NullReferenceException(
                        string.Format("[SCENE OBJECT GROUP]: RawGroupPosition - Object {0} has no root part.", m_uuid));
                }

                return m_rootPart.RawGroupPosition;
            }
        }

        /// <summary>
        /// The absolute position of this scene object in the scene
        /// </summary>
        public override Vector3 AbsolutePosition
        {
            get
            {
                if (m_rootPart == null)
                {
                    throw new NullReferenceException(
                        string.Format("[SCENE OBJECT GROUP]: Object {0} has no root part.", m_uuid));
                }

                return m_rootPart.GroupPosition;
            }
            set
            {
                SetAbsolutePosition(value, false);
            }
        }

        public Vector3 OriginalEnteringScenePosition { get; set; }

        /// <summary>
        /// Time this group was received from another region
        /// Used to provide numbers for interpolation
        /// </summary>
        public ulong TimeReceived { get; set; }

        /// <summary>
        /// Whether this group was created by a region crossing
        /// </summary>
        public bool FromCrossing
        {
            get
            {
                return TimeReceived != 0;
            }
        }

        public void SetAbsolutePosition(Vector3 val, bool physicsTriggered)
        {
            if (m_scene == null)
            {
                // this object isn't even in the scene (yet), just update position
                m_rootPart.SetGroupPosition(val, false, false);
                return;
            }

            if (InTransit)
                return; // discard the update while in transit

            // If this is an attachment, none of the rest of the code needs to run.
            if (IsAttachment)
            {
                // Just update the position.
                m_rootPart.SetGroupPosition(val, false, physicsTriggered);
                return;
            }

            if (physicsTriggered &&
                (val.Z < OpenSim.Region.Framework.Scenes.Scene.NEGATIVE_OFFWORLD_Z ||
                val.Z > OpenSim.Region.Framework.Scenes.Scene.POSITIVE_OFFWORLD_Z))
            {
                if (IsBelowGround || val.Z > OpenSim.Region.Framework.Scenes.Scene.POSITIVE_OFFWORLD_Z)
                {
                    m_rootPart.PhysActor.Suspend();

                    if (!this.IsBeingDerezzed)
                    {
                        //this must be done in a thread, or else something getting returned by physics
                        //with a riding actor will cause this thread, the physics thread, to self deadlock
                        Util.FireAndForget((object state) =>
                        {
                            if (this.RootPart.IsTemporary)
                            {
                                m_scene.DeleteSceneObject(this, false);
                            }
                            else
                            {
                                m_scene.returnObjects(new SceneObjectGroup[] { this }, "objects went off world");
                            }
                        });
                    }
                    return;
                }
            }

            if (!Util.IsValidRegionXY(val))
            {
                if (!physicsTriggered || VelocityHeadedOutOfRegion(ref val))
                {
                    TestForAndBeginCrossing(val, physicsTriggered);
                }
            }
            else
            {
                // Find the current parcel before we update the position.
                Vector3 currentPos = RootPart.GroupPositionNoUpdate;
                if (CurrentParcel == null)
                    CurrentParcel = m_scene.LandChannel.GetLandObject(currentPos.X, currentPos.Y);

                ILandObject NewParcel = m_scene.LandChannel.GetLandObject(val.X, val.Y);
                if (NewParcel == null)
                    return; // Just don't allow it to change to something invalid

                // Optimization: Precheck with more restrictive general ban height before checking if the avatar is banned.
                if ((NewParcel != null) && (val.Z < m_scene.LandChannel.GetBanHeight(false)))
                {
                    // Possibly entering a restricted parcel.
                    ParcelPropertiesStatus reason;
                    if (physicsTriggered)
                    {
                        // If the sat-upon object is physical, we can't stop it.
                        // First, let's check each rider to see if we need to eject them.
                        this.ForEachSittingAvatar(delegate (ScenePresence sitter)
                        {
                            float minZ;
                            if (m_scene.TestBelowHeightLimit(sitter.UUID, val, NewParcel, out minZ, out reason))
                            {
                                Util.FireAndForget((o) =>
                                {
                                    Scene.LandChannel.RemoveAvatarFromParcel(sitter.UUID);
                                    if (reason == ParcelPropertiesStatus.CollisionBanned)
                                        sitter.ControllingClient.SendAlertMessage("You are not permitted to enter this parcel because you are banned.");
                                    else
                                        sitter.ControllingClient.SendAlertMessage("You are not permitted to enter this parcel due to parcel restrictions.");
                                });
                            }
                        });
                        // Then, if the owner of the object itself is banned, let's return it.
                        if (NewParcel.DenyParcelAccess(this, false, out reason))
                        {
                            this.IsSelected = true; // force kinematic to stop further pos updates/returns

                            Util.FireAndForget((o) =>
                            {
                                Thread.Sleep(500);  // Give the avatars a chance to stand above (not really needed, but may help)
                                m_scene.returnObjects(new SceneObjectGroup[] { this }, "physical object entry not permitted");
                            });
                        }
                        return; // it was a physical move, we've done all we can.
                    }

                    // If we reach here, the object is non-physical. Check entry and fix if denied.
                    if (NewParcel.DenyParcelAccess(this, true, out reason))
                    {
                        val = currentPos; // force undo the position change and continue
                    }
                }

                // Update the position.
                m_rootPart.SetGroupPosition(val, false, physicsTriggered);

                // val.X and val.Y is NOT valid here only for attachments since they are relative locations
                if ((m_scene != null) && !IsAttachment)
                {
                    //check for changing of parcel
                    if ((CurrentParcel != null) && (val != currentPos))
                    {
                        if (NewParcel.landData.LocalID != CurrentParcel.landData.LocalID)
                        {
                            m_scene.EventManager.TriggerGroupCrossedToNewParcel(this, CurrentParcel, NewParcel);
                            CurrentParcel = NewParcel;
                        }
                    }
                }
            }
        }

        private bool VelocityHeadedOutOfRegion(ref Vector3 position)
        {
            PhysicsActor physActor = m_rootPart.PhysActor;
            if (physActor == null) return false;

            Vector3 velocity = physActor.Velocity;
            Vector3 integratedPos = position + velocity;

            if (position.X >= Constants.OUTSIDE_REGION && integratedPos.X >= position.X)
            {
                return true;
            }

            if (position.X < Constants.OUTSIDE_REGION_NEGATIVE_EDGE && integratedPos.X < position.X)
            {
                return true;
            }

            if (position.Y >= Constants.OUTSIDE_REGION && integratedPos.Y >= position.Y)
            {
                return true;
            }

            if (position.Y < Constants.OUTSIDE_REGION_NEGATIVE_EDGE && integratedPos.Y < position.Y)
            {
                return true;
            }

            return false;
        }

        // We could use _lastGoodPosition, but let physics handle the position instead
        public void ForcePositionInRegion()
        {
            if (!IsDeleted)
            {
                PhysicsActor physActor = RootPart.PhysActor;
                if (physActor != null)
                {
                    physActor.CrossingFailure();
                }
                else
                {
                    //object is phantom. brute force
                    Vector3 pos = AbsolutePosition;
                    Util.ForceValidRegionXY(ref pos);
                    AbsolutePosition = pos;
                }
            }
        }

        private void TestForAndBeginCrossing(Vector3 val, bool physicsTriggered)
        {
            SimpleRegionInfo destRegion = this.Scene.GetNeighborAtPosition(val.X, val.Y);
            if (destRegion == null)
            {
                ForcePositionInRegion();
                Scene.CheckDieAtEdge(this);
                return;
            }

            //if there are any avatars currently in the region, sitting on this prim that
            //still have connections establishing, fail the crossing and let them establish
            this.ForEachSittingAvatar(delegate (ScenePresence avatar)
            {
                if (!avatar.CanExitRegion)
                {
                    // avatar.ControllingClient.SendAlertMessage("Can not move to a new region, still entering this one");
                    ForcePositionInRegion();
                    return;
                }


                if (avatar.RemotePresences.HasConnectionsEstablishing())
                {
                    // avatar.ControllingClient.SendAlertMessage("Can not move to a new region, connections are still being established");
                    ForcePositionInRegion();
                    return;
                }
            });

            //don't allow an object to quickly recross if it just got here and there
            //crossing avatars aboard
            const ulong MINIMUM_RECROSSING_WAIT_TIME = 3000; //ms
            if (this.AvatarsToExpect > 0 && 
                this.TimeReceived != 0 && 
                Util.GetLongTickCount() - this.TimeReceived < MINIMUM_RECROSSING_WAIT_TIME)
            {
                ForcePositionInRegion();
                return;
            }


            //crossing must be done outside of this thread because the prim crossing may
            //have been triggered by a script which means we're inside the script engine
            //execution thread and will deadlock requesting the script state
            //further down the path
            if (StartTransit())
            {
                Util.FireAndForget(delegate(object obj)
                {
                    bool success = false;
                    try
                    {
                        success = m_scene.CrossPrimGroupIntoNewRegion(val, this, true);
                    }
                    finally
                    {
                        EndTransit(success);
                    }
                });
            }

            return;
        }

        public ILandObject CurrentParcel
        {
            get
            {
                return _currentParcel;
            }
            set
            {
                _currentParcel = value;
                if ((!IsAttachment) && (_currentParcel != null) && (m_scene != null))
                    m_scene.InspectForAutoReturn(this, _currentParcel.landData);
            }
        }

        public override uint LocalId
        {
            get
            {
                if (m_rootPart == null)
                {
                    m_log.Error("[SCENE OBJECT GROUP]: Unable to find the rootpart for a LocalId Request!");
                    return 0;
                }

                return m_rootPart.LocalId;
            }
            set { m_rootPart.LocalId = value; }
        }

        public override UUID UUID
        {
            get
            {
                if (m_rootPart == null)
                {
                    m_log.Error("[SCENE OBJECT GROUP]: Got a null rootpart while requesting group UUID.");
                    return UUID.Zero;
                }
                else return m_rootPart.UUID;
            }
            set { m_rootPart.UUID = value; }
        }

        public UUID OwnerID
        {
            get
            {
                if (m_rootPart == null)
                    return UUID.Zero;

                return m_rootPart.OwnerID;
            }
            set { m_rootPart.OwnerID = value; }
        }

        public Color TextColor
        {
            get { return m_rootPart.TextColor; }
            set { m_rootPart.TextColor = value; }
        }

        public string Text
        {
            get
            {
                string returnstr = m_rootPart.Text;
                if (returnstr.Length > 255)
                {
                    returnstr = returnstr.Substring(0, 255);
                }
                return returnstr;
            }
            set { m_rootPart.Text = value; }
        }

        protected virtual bool InSceneBackup
        {
            get { return true; }
        }

        public bool IsSelected
        {
            get { return m_isSelected; }
            set
            {
                m_isSelected = value;
                // Tell physics engine that group is selected
                if (m_rootPart != null && m_rootPart.PhysActor != null)
                {
                    m_childParts.ForEachPart((SceneObjectPart part) => {
                        PhysicsActor physActor = part.PhysActor;
                        if (physActor != null)
                        {
                            physActor.Selected = value;
                        }
                    });

                }
            }
        }

        // The UUID for the Region this Object is in.
        public UUID RegionUUID
        {
            get
            {
                if (m_scene != null)
                {
                    return m_scene.RegionInfo.RegionID;
                }
                return UUID.Zero;
            }
        }

        private int _scriptsWithAvatarControls = 0;

        /// <summary>
        /// Weather or not any of the scripts in the prims are holding avatar controls as granted by llTakeControls
        /// </summary>
        public bool HasAvatarControls
        {
            get
            {
//                m_log.WarnFormat("[CONTROLS]: Object has {0} controls.", _scriptsWithAvatarControls);
                return _scriptsWithAvatarControls > 0;
            }
        }

        public int RecalcAvatarControls()
        {
            int total = 0;
            
            m_childParts.ForEachPart((SceneObjectPart part) => {
                total += part.Inventory.GetScriptedControlsCount();
                _scriptsWithAvatarControls = total;
            });

            return total;
        }

        private UUID _rezzedFromFolderId = UUID.Zero;
        public UUID RezzedFromFolderId
        {
            get
            {
                return _rezzedFromFolderId;
            }

            set
            {
                _rezzedFromFolderId = value;
            }
        }

        public bool IsBeingDerezzed { get; set; }

        public bool IsTempAttachment { get; set; }

        //public UUID FromAssetId { get; set; }

#endregion

#region Constructors

        /// <summary>
        /// Constructor
        /// </summary>
        public SceneObjectGroup()
        {
        }

        /// <summary>
        /// This constructor creates a SceneObjectGroup using a pre-existing SceneObjectPart.
        /// The original SceneObjectPart will be used rather than a copy, preserving
        /// its existing localID and UUID.
        /// </summary>
        public SceneObjectGroup(SceneObjectPart part)
        {
            SetRootPart(part);
        }

        /// <summary>
        /// Constructor.  This object is added to the scene later via AttachToScene()
        /// </summary>
        public SceneObjectGroup(UUID ownerID, Vector3 pos, Quaternion rot, PrimitiveBaseShape shape, bool rezSelected)
        {
            Vector3 rootOffset = new Vector3(0, 0, 0);
            SetRootPart(new SceneObjectPart(ownerID, shape, pos, rot, rootOffset, rezSelected));
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        public SceneObjectGroup(UUID ownerID, Vector3 pos, PrimitiveBaseShape shape)
            : this(ownerID, pos, Quaternion.Identity, shape, false)
        {
        }

        public void ResetInstance(bool isNewInstance, bool isScriptReset, UUID itemId)
        {
            ClearTargetWaypoints();
            ClearRotWaypoints();
            m_childAvatars.Clear();
            m_sitTargets.Clear();   // new UUIDs have been assigned, need to refresh
            m_childParts.ForEachPart((SceneObjectPart part) => {
                part.ResetInstance(isNewInstance, isScriptReset, itemId);
                this.SetSitTarget(part, part.SitTargetActive, part.SitTargetPosition, part.SitTargetOrientation, false);
            });
        }

        public void LoadScriptState(XmlDocument doc)
        {
            XmlNodeList nodes = doc.GetElementsByTagName("PhloxSavedSS");
            if (nodes.Count > 0)
            {
                m_savedScriptState = new Dictionary<UUID, string>();
                foreach (XmlNode node in nodes)
                {
                    if (node.Attributes["UUID"] != null)
                    {
                        UUID itemid = new UUID(node.Attributes["UUID"].Value);
                        m_savedScriptState.Add(itemid, node.InnerXml);
                    }
                }
            }
        }

        public void SetFromItemID(UUID AssetId)
        {
            m_childParts.ForEachPart((SceneObjectPart part) => {
                part.FromItemID = AssetId;
            });
        }

        public UUID GetFromItemID()
        {
            if (m_rootPart != null)
            {
                return m_rootPart.FromItemID;
            }
            return UUID.Zero;
        }

        /// <summary>
        /// Hooks this object up to the backup event so that it is persisted to the database when the update thread executes.
        /// </summary>
        public void AttachToBackup()
        {
            if (InSceneBackup)
            {
                //m_log.DebugFormat(
                //    "[SCENE OBJECT GROUP]: Attaching object {0} {1} to scene presistence sweep", Name, UUID);

                //if (!m_isBackedUp)
                //    m_scene.EventManager.OnBackup += ProcessBackup;

                m_isBackedUp = true;
            }
        }

        /// <summary>
        /// Attach this object to a scene.  It will also now appear to agents.
        /// </summary>
        /// <param name="scene"></param>
        public void AttachToScene(Scene scene, bool fromStorage)
        {
            m_scene = scene;
            RegionHandle = m_scene.RegionInfo.RegionHandle;

            if (!IsAttachment)
                m_rootPart.ParentID = 0;
            if (m_rootPart.LocalId == 0)
                m_rootPart.LocalId = m_scene.AllocateLocalId();
            
            m_childParts.ForEachPart((SceneObjectPart part) => {
                if (Object.ReferenceEquals(part, m_rootPart))
                    return;

                if (part.LocalId == 0)
                    part.LocalId = m_scene.AllocateLocalId();
                part.ParentID = m_rootPart.LocalId;
            });

            if (RootPart.PhysActor == null) //don't apply physics if we already have a physactor.
            {
                ApplyPhysics(m_scene.m_physicalPrim, fromStorage);
                ScheduleGroupForFullUpdate(PrimUpdateFlags.ForcedFullUpdate);
            }

            // Need the bounding box now.
            BoundingBox();

            //Signal a new object appearing in the scene
            Scene.EventManager.TriggerObjectAddedToScene(this);
        }

        /// <summary>
        /// Converts the given list of groups to groups located at an offset of the given 
        /// center point
        /// </summary>
        /// <param name="center"></param>
        /// <param name="boxes"></param>
        public static void TranslateToNewCenterPosition(Vector3 oldCenter, Vector3 newCenter, IEnumerable<SceneObjectGroup> groups)
        {
            List<KeyValuePair<SceneObjectGroup, Box>> objectsWithBoundingBox = new List<KeyValuePair<SceneObjectGroup, Box>>();
            List<Box> boxes = new List<Box>();

            //collect boxes with groups
            foreach (SceneObjectGroup group in groups)
            {
                KeyValuePair<SceneObjectGroup, Box> groupAndBounding
                    = new KeyValuePair<SceneObjectGroup, Box>(group, group.BoundingBox());

                objectsWithBoundingBox.Add(groupAndBounding);
                boxes.Add(groupAndBounding.Value);
            }

            //translate
            Box.RecenterUsingNewCenter(oldCenter, newCenter, boxes);

            // save the difference between the new rez location and old
            Vector3 rezDiff = newCenter - oldCenter;

            //assign
            foreach (KeyValuePair<SceneObjectGroup, Box> kvp in objectsWithBoundingBox)
            {
                //take into account that the root part of the group should be placed not
                //at the center of the bounding box, but at it's own offset from the center
                //this is because the center position of a group is actually the root prim
                Vector3 primDiff = kvp.Key.BoundingBox().Center - rezDiff;
                Vector3 rootPrimOffset = kvp.Key.RootPart.AbsolutePosition - primDiff; // position not update yet so diff with old

                kvp.Key.SetRegionAbsolutePosition(kvp.Value.Center + rootPrimOffset);
            }
        }

        /// <summary>
        /// Returns the bounding box for one or more groups
        /// </summary>
        /// <param name="groups"></param>
        /// <returns></returns>
        public static Box GetBoundingBoxForMultipleGroups(IEnumerable<SceneObjectGroup> groups)
        {
            List<Box> groupBoxes = new List<Box>();
            foreach (SceneObjectGroup group in groups)
            {
                groupBoxes.Add(group.BoundingBox());
            }

            return Box.CalculateBoundingBox(groupBoxes);
        }

        /// <summary>
        /// Returns the size of the bounding box for this group
        /// </summary>
        /// <returns></returns>
        public Vector3 GroupScale()
        {
            return this.BoundingBox().Size;
        }

        /// <summary>
        /// Returns the absolute bounding box for the object group
        /// Bounding box is based on absolute world coordinates and rotation
        /// </summary>
        /// <returns></returns>
        public Box BoundingBox()
        {
            Box returnBox;
            PhysicsActor physActor = m_rootPart.PhysActor;
            lock (_bbLock)
            {
                if ((physActor != null) && (physActor.IsPhysical))
                {
                    if (_calculatedBoundingBox != null)
                    {
                        // We have a cached BB for a physical object. See if it needs a recalc.
                        if (_bbRot != physActor.Rotation)
                            ClearBoundingBoxCache();
                        else
                        {
                            Vector3 pos = physActor.Position;
                            if (_bbPos != pos)
                                RepositionBoundingBox(pos);
                        }
                    }
                }
                returnBox = _calculatedBoundingBox;
            }
            // If it makes it here it either needs a BB recalc (cleared above) or it already has one.
            if (returnBox == null)
                returnBox = CalculateBoundingBox();

            return returnBox;
        }

        public void ClearBoundingBoxCache()
        {
            lock (_bbLock)
            {
                _calculatedBoundingBox = null;
            }
        }

        private Box CalculateBoundingBox()
        {
            // This method always recalcs the BB. To use the cached version, call BoundingBox.
            Box returnBox;
            List<Box> boundingBoxes = new List<Box>();

            PhysicsActor physActor = m_rootPart.PhysActor;
            if ((physActor != null) && (physActor.IsPhysical))
            {
                // For physical objects, save the pos/rot used for BB calc.
                _bbPos = physActor.Position;
                _bbRot = physActor.Rotation;
            }

            m_childParts.ForEachPart((SceneObjectPart part) => {
                boundingBoxes.Add(part.BoundingBox);
            });

            lock (_bbLock)
            {
                // update _calculatedBoundingBox inside the lock
                _calculatedBoundingBox = Box.CalculateBoundingBox(boundingBoxes);
                returnBox = _calculatedBoundingBox;
            }
            
            return returnBox;
        }

        public void RepositionBoundingBox(Vector3 newPos)
        {
            lock (_bbLock)
            {
                if (_calculatedBoundingBox == null)
                    return; // we haven't calculated on yet so no incremental update needed.

                _calculatedBoundingBox.Center = newPos;
            }
        }

        /// <summary>
        /// Returns the relative bounding box for the object group
        /// Bounding box is based on root-relative coordinates and rotation
        /// </summary>
        /// <returns></returns>
        public Box RelativeBoundingBox(bool includephantom)
        {
            var parts = this.GetParts();
            List<Box> boundingBoxes = new List<Box>();
            
            foreach (SceneObjectPart part in parts)
            {
                if (includephantom || !(part.IsPhantom || part.Shape.FlexiEntry || part.Shape.PreferredPhysicsShape == PhysicsShapeType.None))
                    boundingBoxes.Add(part.RelativeBoundingBox);
            }
            

            return Box.CalculateBoundingBox(boundingBoxes);
        }

        public EntityIntersection TestIntersection(Ray hRay, bool frontFacesOnly, bool faceCenters)
        {
            // We got a request from the inner_scene to raytrace along the Ray hRay
            // We're going to check all of the prim in this group for intersection with the ray
            // If we get a result, we're going to find the closest result to the origin of the ray
            // and send back the intersection information back to the innerscene.

            EntityIntersection returnresult = new EntityIntersection();

            m_childParts.ForEachPart((SceneObjectPart part) => {
                // Temporary commented to stop compiler warning
                //Vector3 partPosition =
                //    new Vector3(part.AbsolutePosition.X, part.AbsolutePosition.Y, part.AbsolutePosition.Z);
                Quaternion parentrotation = GroupRotation;

                // Telling the prim to raytrace.
                //EntityIntersection inter = part.TestIntersection(hRay, parentrotation);

                EntityIntersection inter = part.TestIntersectionOBB(hRay, parentrotation, frontFacesOnly, faceCenters);

                // This may need to be updated to the maximum draw distance possible..
                // We might (and probably will) be checking for prim creation from other sims
                // when the camera crosses the border.
                float idist = Constants.RegionSize;

                if (inter.HitTF)
                {
                    // We need to find the closest prim to return to the testcaller along the ray
                    if (inter.distance < idist)
                    {
                        returnresult.HitTF = true;
                        returnresult.ipoint = inter.ipoint;
                        returnresult.obj = part;
                        returnresult.normal = inter.normal;
                        returnresult.distance = inter.distance;
                    }
                }
            });
            
            return returnresult;
        }

#endregion

        /// <summary>
        /// Used to set absolute positioning without crossing the prim into a new scene in case of negative values
        /// Should not be called on live objects already attached to a scene
        /// </summary>
        /// <param name="absolutePosition"></param>
        public void SetRegionAbsolutePosition(Vector3 absolutePosition)
        {
            m_childParts.ForEachPart((SceneObjectPart part) => {
                part.GroupPosition = absolutePosition;
            });
        }

        public void SetSitTarget(SceneObjectPart part, bool isActive, Vector3 pos, Quaternion rot, bool preserveSitter)
        {
            SitTargetInfo sitInfo = new SitTargetInfo(part, isActive, pos, rot);
            if (preserveSitter)
            {
                SitTargetInfo oldInfo;
                if (m_sitTargets.TryGetValue(part.UUID, out oldInfo))
                    sitInfo.Sitter = oldInfo.Sitter;
            }

            m_sitTargets[part.UUID] = sitInfo;
        }

        public void RemoveSitTarget(UUID partID)
        {
            if (m_sitTargets.ContainsKey(partID))
                m_sitTargets.Remove(partID);
        }

        public SitTargetInfo SitTargetForPart(UUID partID)
        {
            SitTargetInfo info;
            if (m_sitTargets.TryGetValue(partID, out info))
                return info;

            return SitTargetInfo.None;
        }

        public void SaveScriptedState(XmlTextWriter writer, StopScriptReason stopScriptReason)
        {
            Dictionary<UUID, string> states = new Dictionary<UUID, string>();
            
            m_childParts.ForEachPart((SceneObjectPart part) => {
                Dictionary<UUID, string> pstates = part.Inventory.GetScriptStates(stopScriptReason);
                foreach (UUID itemid in pstates.Keys)
                {
                    states.Add(itemid, pstates[itemid]);
                }
            });

            if (states.Count > 0)
            {
                // Now generate the necessary XML wrappings
                writer.WriteStartElement(String.Empty, "PhloxGroupScriptStates", String.Empty);
                foreach (UUID itemid in states.Keys)
                {
                    writer.WriteStartElement(String.Empty, "PhloxSavedSS", String.Empty);
                    writer.WriteAttributeString(String.Empty, "UUID", String.Empty, itemid.ToString());
                    writer.WriteRaw(states[itemid]); // Writes ScriptState element
                    writer.WriteEndElement(); // End of SavedScriptState
                }
                writer.WriteEndElement(); // End of GroupScriptStates
            }
        }

        /// <summary>
        /// Attach this scene object to the given avatar.
        /// </summary>
        /// <param name="agentID"></param>
        /// <param name="attachmentpoint"></param>
        /// <param name="AttachOffset"></param>
        public void AttachToAgent(UUID agentID, uint attachmentpoint, bool silent)
        {
            ScenePresence avatar = m_scene.GetScenePresence(agentID);
            if (avatar != null)
            {
                DetachFromBackup();
                Scene.RemoveFromPotentialReturns(this);

                // Remove from database and parcel prim count
                //
                if (IsPersisted) m_scene.DeleteFromStorage(UUID);
                m_scene.EventManager.TriggerParcelPrimCountTainted();

                if (m_rootPart.PhysActor != null)
                {
                    m_scene.PhysicsScene.RemovePrim(m_rootPart.PhysActor);
                    m_rootPart.PhysActor = null;
                }

                m_rootPart.SetParentLocalId(avatar.LocalId);
                SetAttachmentPoint(Convert.ToByte(attachmentpoint));

                if (avatar.ControllingClient.ActiveGroupId != this.GroupID)
                {
                    IClientAPI client = silent ? null : avatar.ControllingClient;
                    this.SetGroup(avatar.ControllingClient.ActiveGroupId, client);
                }

                avatar.AddAttachment(this);

                if (!silent)
                {
                    IsSelected = false; // fudge....
                    ScheduleGroupForFullUpdate(PrimUpdateFlags.ForcedFullUpdate);
                }
            }
        }

        public byte GetCurrentAttachmentPoint()
        {
            if (m_rootPart == null)
                return (byte)0;

            // If not currently attached, returns the previous attachment point.
            return AttachmentPoint;
        }

        public byte GetBestAttachmentPoint()
        {
            if (m_rootPart == null)
                return (byte)0;

            // If not currently attached, returns the previous attachment point.
            return m_rootPart.GetBestAttachmentPoint();
        }

        public Vector3 GetSavedAttachmentPos(byte attached)
        {
            if (m_rootPart == null)
                return Vector3.Zero;

            return m_rootPart.GetSavedAttachmentPos(attached);
        }

        public void ClearPartAttachmentData()
        {
            SetAttachmentPoint((Byte)0);
        }

        public void DetachToGround()
        {
            ScenePresence avatar = m_scene.GetScenePresence(m_rootPart.AttachedAvatar);
            if (avatar == null)
                return;

            avatar.RemoveAttachment(this);

            Vector3 detachedpos = new Vector3(127f, 127f, 127f);
            if (avatar == null)
                return;

            detachedpos = avatar.AbsolutePosition;

            AbsolutePosition = detachedpos;
            m_rootPart.SetParentLocalId(0);
            SetAttachmentPoint((byte)0);
            SetFromItemID(UUID.Zero);
            this.ApplyPhysics(m_scene.m_physicalPrim, false);
            HasGroupChanged = true;
            RootPart.Rezzed = DateTime.Now;
            RootPart.RemFlag(PrimFlags.TemporaryOnRez);
            AttachToBackup();
            m_scene.InspectForAutoReturn(this);
            m_scene.EventManager.TriggerParcelPrimCountTainted();
            m_rootPart.ScheduleFullUpdate(PrimUpdateFlags.ForcedFullUpdate);
            m_rootPart.ClearUndoState();
        }

        public void DetachToInventoryPrep()
        {
            ScenePresence avatar = m_scene.GetScenePresence(m_rootPart.AttachedAvatar);
            //Vector3 detachedpos = new Vector3(127f, 127f, 127f);
            if (avatar != null)
            {
                //detachedpos = avatar.AbsolutePosition;
                avatar.RemoveAttachment(this);
            }

            AbsolutePosition = m_rootPart.AttachedPos;
            m_rootPart.SetParentLocalId(0);
            // SetAttachmentPoint((byte)0);    // call this at the end to finalize attachment change
        }
        /// <summary>
        ///
        /// </summary>
        /// <param name="part"></param>
        private void SetPartAsNonRoot(SceneObjectPart part)
        {
            part.ParentID = m_rootPart.LocalId;
            part.ClearUndoState();
        }

        public override void UpdateMovement()
        {
            m_childParts.ForEachPart((SceneObjectPart part) => {
                part.UpdateMovement();
            });
        }

        public float GetTimeDilation()
        {
            return m_scene.TimeDilation;
        }

        /// <summary>
        /// Added as a way for the storage provider to reset the scene,
        /// most likely a better way to do this sort of thing but for now...
        /// </summary>
        /// <param name="scene"></param>
        public void SetScene(Scene scene)
        {
            m_scene = scene;
        }

        /// <summary>
        /// Set a part to act as the root part for this scene object
        /// </summary>
        /// <param name="part"></param>
        public void SetRootPart(SceneObjectPart part)
        {
            m_rootPart = part;
            part.SetParent(this);

            if (!IsAttachment)
                part.ParentID = 0;
            part.LinkNum = 0;

            // SOG should not be in the scene yet - one can't change root parts after
            // the scene object has been attached to the scene
            m_childParts.AddPart(m_rootPart);

            // If this is being called from CopyPart, as part of the persistence backup, 
            // then be aware it is a duplicate copy of the SOP/SOG that has UUID/LocalID
            // that matches the in-world copy. But also has its own sit target dictionary,
            // so even if the IDs match, the SOP references are different.
            SetSitTarget(part, part.SitTargetActive, part.SitTargetPosition, part.SitTargetOrientation, false);

            //Rebuild the bounding box
            ClearBoundingBoxCache();
            // Update the ServerWeight/LandImpact and StreamingCost
            if (m_childParts.Count == 1) // avoids the recalcs on Copy calls for backup of prims
            {
                m_serverWeight = part.ServerWeight;
                m_streamingCost = part.StreamingCost;
            }
            else
            {
                RecalcPrimWeights();
            }
            RecalcScriptedStatus();
        }

        /// <summary>
        /// Add a new part to this scene object.  The part must already be correctly configured.
        /// </summary>
        /// <param name="part"></param>
        public void AddPart(SceneObjectPart part)
        {
            part.SetParentAndUpdatePhysics(this);
            int count = m_childParts.AddPart(part);

            part.LinkNum = count;

            if (part.LinkNum == 2 && RootPart != null)
                RootPart.LinkNum = 1;

            //Rebuild the bounding box
            ClearBoundingBoxCache();

            SetSitTarget(part, part.SitTargetActive, part.SitTargetPosition, part.SitTargetOrientation, false);

            // Update the ServerWeight/LandImpact
            RecalcPrimWeights();
            RecalcScriptedStatus();
        }

        /// <summary>
        /// Make sure that every non root part has the proper parent root part local id
        /// </summary>
        private void UpdateParentIDs()
        {
            m_childParts.ForEachPart((SceneObjectPart part) => {
                if (part.UUID != m_rootPart.UUID)
                {
                    part.ParentID = m_rootPart.LocalId;
                }
            });
        }
        
        // helper provided for parts.
        public int GetSceneMaxUndo()
        {
            if (m_scene != null)
                return m_scene.MaxUndoCount;
            return 5;
        }

        public UUID GetPartsFullID(uint localID)
        {
            SceneObjectPart part = GetChildPart(localID);
            if (part != null)
            {
                return part.UUID;
            }
            return UUID.Zero;
        }

        public void ObjectGrabHandler(uint localId, Vector3 offsetPos, IClientAPI remoteClient)
        {
            if (m_rootPart.LocalId == localId)
            {
                OnGrabGroup(offsetPos, remoteClient);
            }
            else
            {
                SceneObjectPart part = GetChildPart(localId);
                OnGrabPart(part, offsetPos, remoteClient);

            }
        }

        public virtual void OnGrabPart(SceneObjectPart part, Vector3 offsetPos, IClientAPI remoteClient)
        {
            part.StoreUndoState();
            part.OnGrab(offsetPos, remoteClient);
        }

        public virtual void OnGrabGroup(Vector3 offsetPos, IClientAPI remoteClient)
        {
            m_scene.EventManager.TriggerGroupGrab(UUID, offsetPos, remoteClient.AgentId);
        }

        public void DeleteGroup(bool silent)
        {
            this.DeleteGroup(silent, false);
        }

        /// <summary>
        /// Delete this group from its scene and tell all the scene presences about that deletion.
        /// </summary>        
        /// <param name="silent">Broadcast deletions to all clients.</param>
        public void DeleteGroup(bool silent, bool fromCrossing)
        {
            int threadId = Thread.CurrentThread.ManagedThreadId;
            if (!IsAttachment)
                m_log.InfoFormat("[SCENE] {0}: Thread {1} DeleteGroup {2} with localID {3}.", Scene.RegionInfo.RegionName, threadId.ToString(), UUID.ToString(), LocalId.ToString());

            List<uint> localIds = new List<uint>();
            lock (Scene.SyncRoot)
            {
                List<ScenePresence> avatars = Scene.GetScenePresences();

                ClearTargetWaypoints();
                ClearRotWaypoints();

                m_childAvatars.ForEach((ScenePresence SP) => {
                    m_log.WarnFormat("[SCENE]: DeleteGroup {0} with avatar {1} seated on prim (crossing={2}).",
                                        UUID.ToString(), SP.ControllingClient.AgentId, fromCrossing.ToString());
                    SP.StandUp(fromCrossing, false);
                });

                m_childParts.ForEachPart((SceneObjectPart part) => {
                    if (m_rootPart != null && part == m_rootPart)
                    {
                        if (!silent)
                        {
                            // We need to keep track of this state in case this group is still queued for backup or updates
                            localIds.Add(part.LocalId);
                        }
                    }
                });
                
                // We need to keep track of this state in case this group is still queued for backup.
                m_isDeleted = true;

                DetachFromBackup();
                Scene.RemoveFromPotentialReturns(this);
                if (!silent)
                {
                    foreach (ScenePresence SP in avatars)
                    {
                        SP.SceneView.SendKillObjects(this, localIds);
                    }
                }
            }
        }

        public double GetScriptsAverage()
        {
            return _samples.Average();
        }

        public void AddScriptLPS(double count)
        {
            if ((DateTime.Now - _scoreLastCleared).TotalMilliseconds >= SAMPLE_MS)
            {
                _scoreLastCleared = DateTime.Now;
                _samples[_currSample++] = (float)scriptScore;
                if (_currSample >= NUM_SCRIPT_SAMPLES) _currSample = 0;

                scriptScore = 0.0;
            }

            scriptScore += count;
            /*SceneGraph d = m_scene.SceneGraph;
            d.AddToScriptLPS(count);*/
        }

        public void AddActiveScriptCount(int count)
        {
            SceneGraph d = m_scene.SceneGraph;
            d.AddActiveScripts(count);
        }

        public void aggregateScriptEvents()
        {
            uint objectflagupdate = (uint)RootPart.GetEffectiveObjectFlags();

            ScriptEvents aggregateScriptEvents = 0;

            m_childParts.ForEachPart((SceneObjectPart part) => {
                if (part == null)
                    return;
                if (part != RootPart)
                {
                    // Preserve the Scripted flag status for each part.
                    bool isScripted = (part.Flags & PrimFlags.Scripted) != 0;
                    part.ObjectFlags = objectflagupdate;    // update
                    // Now restore the per-part Scripted flag.
                    if (isScripted)
                        part.ObjectFlags |= (uint)PrimFlags.Scripted;
                    else
                        part.ObjectFlags &= ~(uint)PrimFlags.Scripted;
                }
                aggregateScriptEvents |= part.AggregateScriptEvents;
            });

            m_scriptListens_atTarget = (aggregateScriptEvents & ScriptEvents.at_target) != 0;
            m_scriptListens_notAtTarget = (aggregateScriptEvents & ScriptEvents.not_at_target) != 0;
            m_scriptListens_atRotTarget = (aggregateScriptEvents & ScriptEvents.at_rot_target) != 0;
            m_scriptListens_notAtRotTarget = (aggregateScriptEvents & ScriptEvents.not_at_rot_target) != 0;

            if (!(m_scriptListens_atTarget || m_scriptListens_notAtTarget))
            {
                ClearTargetWaypoints();
            }

            if (!(m_scriptListens_atRotTarget || m_scriptListens_notAtRotTarget))
            {
                ClearRotWaypoints();
            }

            m_aggregateScriptEvents = aggregateScriptEvents;

            ScheduleGroupForFullUpdate(PrimUpdateFlags.FindBest);
        }

        public override void SetText(string text, Vector3 color, double alpha)
        {
            TextColor = Color.FromArgb(0xff - (int)(alpha * 0xff),
                                   (int)(color.X * 0xff),
                                   (int)(color.Y * 0xff),
                                   (int)(color.Z * 0xff));
            Text = text;

            HasGroupChanged = true;
            m_rootPart.ScheduleFullUpdate(PrimUpdateFlags.Text);
        }

        /// <summary>
        /// Apply physics to this group
        /// </summary>
        /// <param name="m_physicalPrim"></param>
        public void ApplyPhysics(bool allowPhysicalPrims, bool fromStorage)
        {
            // Some diagnostic thresholds for reporting slow/inefficient operations.
            const int SLOW_CALC_THRESHOLD = 1000;   // > this in ms considered slow calc
            const int PHYS_PRIMS_THRESHOLD = 16; // > this reports many physical prims

            if (!m_rootPart.PhysicsSummary.NeedsPhysicsShape)
                return;

            if (m_childParts.Count > 1)
            {
                List<BulkShapeData> allParts = new List<BulkShapeData>();
                int count = 1;
                Vector3 pos = this.AbsolutePosition;
                string prefix = String.Format("[SCENE]: Slow GenerateBulkShapeData for '{0}' at {1}/{2}/{3} ", this.Name, (int)pos.X, (int)pos.Y, (int)pos.Z);
                Util.ReportIfSlow(prefix+"(object)", SLOW_CALC_THRESHOLD, () => 
                {
                    Util.ReportIfSlow(prefix+"(root)", SLOW_CALC_THRESHOLD, () => 
                    {
                        allParts.Add(m_rootPart.GenerateBulkShapeData());
                    });

                    m_childParts.ForEachPart((SceneObjectPart part) => {
                        if (part.LocalId != m_rootPart.LocalId)
                        {
                            if (part.RequiresPhysicalShape)
                            {
                                count++;
                                Util.ReportIfSlow(prefix+"(#"+part.LinkNum.ToString()+")", SLOW_CALC_THRESHOLD, () =>
                                {
                                    allParts.Add(part.GenerateBulkShapeData());
                                });
                            }
                        }
                    });
                });
                if (count > PHYS_PRIMS_THRESHOLD)
                    m_log.WarnFormat("[SCENE]: ApplyPhysics object included {0} physical prims.", count);

                m_scene.PhysicsScene.BulkAddPrimShapes(allParts, m_rootPart.GeneratePhysicsAddPrimShapeFlags(allowPhysicalPrims, fromStorage));

                for (int i = 0; i < allParts.Count; i++)
                {
                    SceneObjectPart part = (SceneObjectPart)allParts[i].Part;
                    part.AttachToPhysicsShape(allParts[i].OutActor, i != 0);

                    if (allParts[i].PhysicsProperties == null)
                    {
                        //restore targetomega as it may be stored in the part properties 
                        //instead of the physactor properties
                        part.RestorePrePhysicsTargetOmega();
                    }
                }

                //NOTE: This is called inside AttachToPhysicsShape, HOWEVER
                //the first time it is called it doesnt do the right thing because
                //the child parts do not yet have physactors and the collisions arent
                //resubscribed. So dont remove this -DD
                m_rootPart.CheckForScriptCollisionEventsAndSubscribe();
            }
            else // m_children.Count == 1
            {
                m_rootPart.ApplyPhysics(fromStorage);
            }
        }

        public void SetOwnerId(UUID userId)
        {
            ForEachPart(delegate(SceneObjectPart part) { part.OwnerID = userId; });
        }

        public void ForEachPart(Action<SceneObjectPart> whatToDo)
        {
            m_childParts.ForEachPart(whatToDo);
        }

#region Events

        private static SceneObjectGroup CopyObjectForBackup(SceneObjectGroup group)
        {
            try
            {
                return group.Copy(group.OwnerID, group.GroupID, false, true);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[BACKUP]: Exception copying object '{0}' at {1}: {2}\n{3}", 
                    group.RootPart.Name, group.RootPart.AbsolutePosition, e.Message, e.StackTrace);
            }
            return null;
        }

        public static void ProcessBulkBackup(IList<SceneObjectGroup> groups, IRegionDataStore datastore, bool forcedBackup)
        {
            List<SceneObjectGroup> groupsThatNeedBackup = new List<SceneObjectGroup>();

            foreach (SceneObjectGroup group in groups)
            {
                if (group.NeedsBackup(forcedBackup))
                {

                    SceneObjectGroup backup_group = CopyObjectForBackup(group);
                    if (backup_group != null)
                    {
                        group.IsPersisted = true;

                        //backup_group.RegionUUID = group.m_scene.RegionInfo.RegionID;
                        groupsThatNeedBackup.Add(backup_group);
                    }
                }
            }

            if (groupsThatNeedBackup.Count > 0)
            {
                m_log.DebugFormat(
                            "[SCENE]: Bulk storing {0} objects",
                            groupsThatNeedBackup.Count);

                datastore.BulkStoreObjects(groupsThatNeedBackup);


                List<KeyValuePair<UUID, IEnumerable<TaskInventoryItem>>> objectInventories
                    = new List<KeyValuePair<UUID, IEnumerable<TaskInventoryItem>>>();

                List<KeyValuePair<UUID, IEnumerable<UUID>>> deletedInventoryItems
                    = new List<KeyValuePair<UUID, IEnumerable<UUID>>>();

                foreach (SceneObjectGroup group in groupsThatNeedBackup)
                {
                    group.ForEachPart(delegate(SceneObjectPart part)
                    {
                        if (part.Inventory.InventoryNeedsBackup())
                        {
                            objectInventories.Add(part.Inventory.CollectInventoryForBackup());
                            deletedInventoryItems.Add(part.Inventory.GetDeletedItemList());
                        }
                    });
                }

                datastore.BulkStoreObjectInventories(objectInventories, deletedInventoryItems);

                m_log.Debug("[SCENE]: Bulk storage completed");
            }
        }

        /// <summary>
        /// Returns whether or not this group needs a backup
        /// </summary>
        /// <returns></returns>
        public bool NeedsBackup(bool forcedBackup)
        {
            if (!m_isBackedUp)
                return false;

            if (IsDeleted || UUID == UUID.Zero)
                return false;

            if (!HasGroupChanged)
                return false;

            uint flags = (uint)RootPart.GetEffectiveObjectFlags();

            if ((flags & (uint)PrimFlags.Temporary) != 0)
                return false;

            if ((flags & (uint)PrimFlags.TemporaryOnRez) != 0)
                return false;

            if (isTimeToPersist() || forcedBackup)
                return true;

            return false;
        }

#endregion

#region Client Updating

        public void SendFullUpdateToClient(IClientAPI remoteClient, PrimUpdateFlags updateFlags)
        {
            SendPartFullUpdate(remoteClient, RootPart, m_scene.Permissions.GenerateClientFlags(remoteClient.AgentId, RootPart.UUID, false), updateFlags);

            m_childParts.ForEachPart((SceneObjectPart part) => {
                if (part != RootPart)
                    SendPartFullUpdate(remoteClient, part, m_scene.Permissions.GenerateClientFlags(remoteClient.AgentId, part.UUID, false), updateFlags);
            });
        }

        public void SendFullUpdateToAllClientsImmediate(bool fastCheck)
        {
            IEnumerable<ScenePresence> avatars = Scene.GetScenePresences();
            foreach (var sp in avatars)
            {
                this.SendFullUpdateToClientImmediate(sp.ControllingClient, fastCheck);
            }
        }

        public void SendFullUpdateToClientImmediate(IClientAPI remoteClient, bool fastCheck)
        {
            //Since this is immediate, the update flags are ignored, but for clarity, we'll use ForcedFullUpdate
            SendPartFullUpdate(remoteClient, RootPart, m_scene.Permissions.GenerateClientFlags(remoteClient.AgentId, RootPart.UUID, fastCheck),
                true, PrimUpdateFlags.ForcedFullUpdate);
            
            m_childParts.ForEachPart((SceneObjectPart part) => {
                if (part != RootPart)
                    SendPartFullUpdate(remoteClient, part,
                        m_scene.Permissions.GenerateClientFlags(remoteClient.AgentId, part.UUID, fastCheck), true, PrimUpdateFlags.ForcedFullUpdate);
            });
        }

        internal void SendPartFullUpdate(IClientAPI remoteClient, SceneObjectPart part, uint clientFlags, PrimUpdateFlags updateFlags)
        {
            SendPartFullUpdate(remoteClient, part, clientFlags, false, updateFlags);
        }

        /// <summary>
        /// Send a full update to the client for the given part
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="part"></param>
        internal void SendPartFullUpdate(IClientAPI remoteClient, SceneObjectPart part, uint clientFlags,
            bool immediate, PrimUpdateFlags updateFlags)
        {
            if (m_rootPart != null && m_rootPart.UUID == part.UUID)
            {
                if (IsAttachment)
                {
                    if (immediate)
                    {
                        part.SendFullUpdateToClientImmediate(remoteClient, m_rootPart.AttachedPos, clientFlags);
                    }
                    else
                    {
                        part.SendFullUpdateToClient(remoteClient, m_rootPart.AttachedPos, clientFlags, updateFlags);
                    }
                }
                else
                {
                    if (immediate)
                    {
                        part.SendFullUpdateToClientImmediate(remoteClient, AbsolutePosition, clientFlags);
                    }
                    else
                    {
                        part.SendFullUpdateToClient(remoteClient, AbsolutePosition, clientFlags, updateFlags);
                    }
                }
            }
            else
            {
                if (immediate)
                {
                    Vector3 lPos;
                    lPos = part.OffsetPosition;
                    part.SendFullUpdateToClientImmediate(remoteClient, lPos, clientFlags);
                }
                else
                {
                    part.SendFullUpdateToClient(remoteClient, clientFlags, updateFlags);
                }
            }
        }


#endregion

#region Copying


        public SceneObjectGroup Copy(UUID cAgentID, UUID cGroupID, bool userExposed, bool serializePhysicsState)
        {
            SceneObjectGroup dupe = (SceneObjectGroup)MemberwiseClone();
            dupe.m_InTransition = 0;
            dupe.m_isBackedUp = false;

            // The MemberwiseClone() above is a shallow copy. All of the object references from the old SOG need new instances.
            dupe.m_childParts = new GroupPartsCollection();
            dupe.m_childAvatars = new AvatarPartsCollection();
            dupe.m_sitTargets = new Dictionary<UUID, SitTargetInfo>();
            dupe.m_targets = new List<ScriptPosTarget>(MAX_TARGETS);
            dupe.m_rotTargets = new List<ScriptRotTarget>(MAX_TARGETS);
            dupe.m_targetsLock = new object();
            dupe._bbLock = new object();

            dupe.m_targets.InsertRange(0, m_targets);
            dupe.m_rotTargets.InsertRange(0, m_rotTargets);

            dupe.CopyRootPart(m_rootPart, OwnerID, GroupID, userExposed, serializePhysicsState);
            // dupe.ParentGroup is set now.
            dupe.RootPart.CopySitTarget(m_rootPart);

            //before setting the position, clear the physactor from the root prim this prevents
            //the physactor of the original part from being affected
            dupe.RootPart.PhysActor = null;
            dupe.RootPart.SetGroupPositionDirect(AbsolutePosition);

            dupe.m_rootPart.LinkNum = m_rootPart.LinkNum;

            if (userExposed)
            {
                dupe.m_rootPart.TrimPermissions();
            }

            // Now we've made a copy that replaces this one, we need to
            // switch the owner to the person who did the copying
            // Second Life copies an object and duplicates the first one in it's place
            // So, we have to make a copy of this one, set it in it's place then set the owner on this one
            if (userExposed)
            {
                SetRootPartOwner(dupe.m_rootPart, cAgentID, cGroupID);
                dupe.m_rootPart.ScheduleFullUpdate(PrimUpdateFlags.ForcedFullUpdate);
            }

            List<SceneObjectPart> partList = new List<SceneObjectPart>(m_childParts.GetAllParts());
            
            partList.Sort(delegate(SceneObjectPart p1, SceneObjectPart p2)
            {
                return p1.LinkNum.CompareTo(p2.LinkNum);
            }
            );

            foreach (SceneObjectPart part in partList)
            {
                if (part.UUID != m_rootPart.UUID)
                {
                    SceneObjectPart newPart = dupe.CopyPart(part, OwnerID, GroupID, userExposed, serializePhysicsState);
                    newPart.LinkNum = part.LinkNum;

                    if (userExposed)
                    {
                        SetPartOwner(newPart, cAgentID, cGroupID);
                        newPart.ScheduleFullUpdate(PrimUpdateFlags.ForcedFullUpdate);
                    }
                }
            }

            // Copy the calculated server weight (LI) and StreamingCost
            dupe.m_serverWeight = this.m_serverWeight;
            dupe.m_streamingCost = this.m_streamingCost;

            if (userExposed)
            {
                foreach (SceneObjectPart part in dupe.GetParts())
                {
                    part.PhysActor = null;
                }

                //reapply physics for new objects
                dupe.ApplyPhysics(m_scene.m_physicalPrim, false);

                dupe.UpdateParentIDs();
                dupe.HasGroupChanged = true;
                dupe.AttachToBackup();
                m_scene.InspectForAutoReturn(dupe);

                ScheduleGroupForFullUpdate(PrimUpdateFlags.ForcedFullUpdate);
            }

            dupe.IsScripted = this.IsScripted;

            return dupe;
        }

        /// <summary>
        /// Duplicates this object, including operations such as physics set up and attaching to the backup event.
        /// </summary>
        /// <returns></returns>
        public SceneObjectGroup Copy(UUID cAgentID, UUID cGroupID, bool userExposed)
        {
            return this.Copy(cAgentID, cGroupID, userExposed, false);
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="part"></param>
        /// <param name="cAgentID"></param>
        /// <param name="cGroupID"></param>
        public void CopyRootPart(SceneObjectPart part, UUID cAgentID, UUID cGroupID, bool userExposed)
        {
            this.CopyRootPart(part, cAgentID, cGroupID, userExposed, false);
        }

        public void CopyRootPart(SceneObjectPart part, UUID cAgentID, UUID cGroupID, bool userExposed, bool serializePhysicsState)
        {
            SceneObjectPart dupe = part.Copy(m_scene.AllocateLocalId(), OwnerID, GroupID, m_childParts.Count, userExposed, serializePhysicsState);
            SetRootPart(dupe);
        }

        public void ScriptSetPhysicsStatus(bool UsePhysics)
        {
            bool IsTemporary = ((RootPart.Flags & PrimFlags.TemporaryOnRez) != 0);
            bool IsPhantom = ((RootPart.Flags & PrimFlags.Phantom) != 0);
            bool IsVolumeDetect = RootPart.VolumeDetectActive;
            UpdateFlags(UsePhysics, IsTemporary, IsPhantom, IsVolumeDetect, null);
        }

        public void ScriptSetTemporaryStatus(bool TemporaryStatus)
        {
            bool UsePhysics = ((RootPart.Flags & PrimFlags.Physics) != 0);
            bool IsPhantom = ((RootPart.Flags & PrimFlags.Phantom) != 0);
            bool IsVolumeDetect = RootPart.VolumeDetectActive;
            UpdateFlags(UsePhysics, TemporaryStatus, IsPhantom, IsVolumeDetect, null);
        }

        public void ScriptSetPhantomStatus(bool PhantomStatus)
        {
            bool UsePhysics = ((RootPart.Flags & PrimFlags.Physics) != 0);
            bool IsTemporary = ((RootPart.Flags & PrimFlags.TemporaryOnRez) != 0);
            bool IsVolumeDetect = RootPart.VolumeDetectActive;
            UpdateFlags(UsePhysics, IsTemporary, PhantomStatus, IsVolumeDetect, null);
        }

        public void ScriptSetVolumeDetect(bool VDStatus)
        {
            bool UsePhysics = ((RootPart.Flags & PrimFlags.Physics) != 0);
            bool IsTemporary = ((RootPart.Flags & PrimFlags.TemporaryOnRez) != 0);
            bool IsPhantom = ((RootPart.Flags & PrimFlags.Phantom) != 0);
            UpdateFlags(UsePhysics, IsTemporary, IsPhantom, VDStatus, null);
        }

        public void ApplyImpulse(OpenMetaverse.Vector3 impulse, bool local)
        {
            // We check if rootpart is null here because scripts don't delete if you delete the host.
            // This means that unfortunately, we can pass a null physics actor to Simulate!
            // Make sure we don't do that!
            SceneObjectPart rootpart = m_rootPart;
            if (rootpart != null)
            {
                if (IsAttachment)
                {
                    ScenePresence avatar = m_scene.GetScenePresence(rootpart.AttachedAvatar);
                    if (avatar != null)
                    {
                        avatar.AddForce(impulse, local ? ForceType.LocalLinearImpulse : ForceType.GlobalLinearImpulse);
                    }
                }
                else
                {
                    PhysicsActor physActor = rootpart.PhysActor;
                    if (physActor != null)
                    {
                        physActor.AddForce(impulse, local ? ForceType.LocalLinearImpulse : ForceType.GlobalLinearImpulse);
                    }
                }
            }
        }

        public void ApplyAngularImpulse(OpenMetaverse.Vector3 impulse, bool local)
        {
            // We check if rootpart is null here because scripts don't delete if you delete the host.
            // This means that unfortunately, we can pass a null physics actor to Simulate!
            // Make sure we don't do that!
            SceneObjectPart rootpart = m_rootPart;
            if (rootpart != null)
            {
                if (IsAttachment)
                {
                    ScenePresence avatar = m_scene.GetScenePresence(rootpart.AttachedAvatar);
                    if (avatar != null)
                    {
                        avatar.AddAngularForce(impulse, local ? ForceType.LocalAngularImpulse : ForceType.GlobalAngularImpulse);
                    }
                }
                else
                {
                    PhysicsActor physActor = rootpart.PhysActor;
                    if (physActor != null)
                    {
                        physActor.AddAngularForce(impulse, local ? ForceType.LocalAngularImpulse : ForceType.GlobalAngularImpulse);
                    }
                }
            }
        }

        public void setAngularImpulse(OpenMetaverse.Vector3 impulse)
        {
            // We check if rootpart is null here because scripts don't delete if you delete the host.
            // This means that unfortunately, we can pass a null physics actor to Simulate!
            // Make sure we don't do that!
            SceneObjectPart rootpart = m_rootPart;
            if (rootpart != null)
            {
                PhysicsActor physActor = rootpart.PhysActor;
                if (physActor != null)
                {
                    if (!IsAttachment)
                    {
                        physActor.Torque = impulse;
                    }
                }
            }
        }

        public void SetForce(OpenMetaverse.Vector3 force, bool local)
        {
            // Scripts can run for a bit after an object (SOG) deletion.
            // Protect against passing null refs.
            SceneObjectPart rootpart = m_rootPart;
            if (rootpart != null)
            {
                if (IsAttachment)
                {
                    ScenePresence avatar = m_scene.GetScenePresence(rootpart.AttachedAvatar);
                    if (avatar != null)
                    {
                        avatar.AddForce(force, local ? ForceType.ConstantLocalLinearForce : ForceType.ConstantGlobalLinearForce);
                    }
                }
                else
                {
                    PhysicsActor physActor = rootpart.PhysActor;
                    if (physActor != null)
                    {
                        physActor.AddForce(force, local ? ForceType.ConstantLocalLinearForce : ForceType.ConstantGlobalLinearForce);
                    }
                }
            }
        }

        public Vector3 GetTorque()
        {
            // We check if rootpart is null here because scripts don't delete if you delete the host.
            // This means that unfortunately, we can pass a null physics actor to Simulate!
            // Make sure we don't do that!
            SceneObjectPart rootpart = m_rootPart;
            if (rootpart != null)
            {
                PhysicsActor physActor = rootpart.PhysActor;
                if (physActor != null)
                {
                    if (!IsAttachment)
                    {
                        return physActor.Torque;
                    }
                }
            }
            return Vector3.Zero;
        }

        public void moveToTarget(Vector3 target, float tau)
        {
            SceneObjectPart rootpart = m_rootPart;
            if (rootpart != null)
            {
                if (IsAttachment)
                {
                    ScenePresence avatar = m_scene.GetScenePresence(rootpart.AttachedAvatar);
                    if (avatar != null)
                    {
                        List<string> coords = new List<string>();
                        uint regionX = 0;
                        uint regionY = 0;
                        Utils.LongToUInts(Scene.RegionInfo.RegionHandle, out regionX, out regionY);
                        target.X += regionX;
                        target.Y += regionY;
                        coords.Add(target.X.ToString());
                        coords.Add(target.Y.ToString());
                        coords.Add(target.Z.ToString());
                        avatar.DoMoveToPosition(avatar, String.Empty, coords);
                    }
                }
                else
                {
                    PhysicsActor physActor = rootpart.PhysActor;
                    if (physActor != null)
                    {
                        physActor.SetMoveToTarget(target, tau);
                    }
                }
            }
        }

        public void stopMoveToTarget()
        {
            SceneObjectPart rootpart = m_rootPart;
            if (rootpart != null)
            {
                if (IsAttachment)
                {
                    ScenePresence avatar = m_scene.GetScenePresence(rootpart.AttachedAvatar);
                    if (avatar != null)
                    {
                        avatar.StopMoveToTarget();
                    }
                }
                else
                {
                    PhysicsActor physActor = rootpart.PhysActor;
                    if (physActor != null)
                    {
                        physActor.SetMoveToTarget(Vector3.Zero, 0.0f);
                    }
                }
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
            SceneObjectPart rootpart = m_rootPart;
            if (rootpart != null)
            {
                PhysicsActor physActor = rootpart.PhysActor;
                if (physActor != null)
                {
                    if (height != 0f)
                    {
                        physActor.SetHover(hoverType, height, tau, 1.0f);
                    }
                    else
                    {
                        physActor.ClearHover();
                    }
                }
            }
        }




        /// <summary>
        /// Set the owner of the root part.
        /// This specific method does not send a full update (caller must).
        /// </summary>
        /// <param name="part"></param>
        /// <param name="cAgentID"></param>
        /// <param name="cGroupID"></param>
        public void SetRootPartOwner(SceneObjectPart part, UUID cAgentID, UUID cGroupID)
        {
            part.GroupID = cGroupID;

            if (part.OwnerID != cAgentID)
            {
                part.LastOwnerID = part.OwnerID;
                part.OwnerID = cAgentID;

                // Apply Next Owner Permissions if we're not bypassing permissions
                if (!m_scene.Permissions.BypassPermissions())
                    ApplyNextOwnerPermissions();
            }

            part.ScheduleFullUpdate(PrimUpdateFlags.ForcedFullUpdate);
        }

        /// <summary>
        /// Changes the ownership of a SOG that is currently live and in the scene
        /// </summary>
        /// <param name="AgentId">The new user taking ownership</param>
        /// <param name="ActiveGroupId">The group to assign to the prim (or UUID.Zero)</param>
        /// <returns>Whether the operation succeeded or failed </returns>
        public bool ChangeOwner(UUID AgentId, UUID ActiveGroupId)
        {
            uint effectivePerms = this.GetEffectivePermissions(true);

            if (AgentId == this.OwnerID)
            {
                // new owner already owns this item
                return false;
            }
            if ((effectivePerms & (uint)PermissionMask.Transfer) == 0)
            {
                // This object (or something in the Contents of one of the prims) does not appear to be transferable
                return false;
            }

            this.SetOwnerId(AgentId);
            this.SetRootPartOwner(this.RootPart, AgentId, ActiveGroupId);

            var partList = this.GetParts();

            if (this.Scene.Permissions.PropagatePermissions())
            {
                foreach (SceneObjectPart child in partList)
                {
                    child.Inventory.ChangeInventoryOwner(AgentId);
                    child.ApplyNextOwnerPermissions();
                }
            }

            this.Rationalize(AgentId, false);
            this.HasGroupChanged = true;
            this.RezzedFromFolderId = UUID.Zero;
            Scene.InspectForAutoReturn(this);
            return true;
        }

        /// <summary>
        /// Changes the ownership of a SOG that is currently live and in the scene
        /// </summary>
        /// <param name="newOwner">The controlling client for the new user taking ownership</param>
        /// <returns>Whether the operation succeeded or failed </returns>
        public bool ChangeOwner(IClientAPI newOwner)
        {
            bool result = ChangeOwner(newOwner.AgentId, newOwner.ActiveGroupId);
            if (result)
            {
                GetProperties(newOwner);
                ScheduleGroupForFullUpdate(PrimUpdateFlags.ForcedFullUpdate);
            }
            return result;
        }

        /// <summary>
        /// Make a copy of the given part.
        /// </summary>
        /// <param name="part"></param>
        /// <param name="cAgentID"></param>
        /// <param name="cGroupID"></param>
        public SceneObjectPart CopyPart(SceneObjectPart part, UUID cAgentID, UUID cGroupID, bool userExposed, bool serializePhysicsState)
        {
            SceneObjectPart newPart = part.Copy(m_scene.AllocateLocalId(), OwnerID, GroupID, m_childParts.Count, userExposed, serializePhysicsState);

            // This is always called with the new duplicate SOG as the 'this', so newPart.SetParent(this) is assigning the dup parent to the dup part.
            if (userExposed)
            {
                newPart.SetParentAndUpdatePhysics(this);
            }
            else
            {
                newPart.SetParent(this);
            }
            
            m_childParts.AddPart(newPart);

            SetPartAsNonRoot(newPart);

            newPart.CopySitTarget(part);

            return newPart;
        }

        /// <summary>
        /// Make a copy of the given part.
        /// </summary>
        /// <param name="part"></param>
        /// <param name="cAgentID"></param>
        /// <param name="cGroupID"></param>
        public SceneObjectPart CopyPart(SceneObjectPart part, UUID cAgentID, UUID cGroupID, bool userExposed)
        {
            return this.CopyPart(part, cAgentID, cGroupID, userExposed, false);
        }

        /// <summary>
        /// Reset the UUIDs for all the prims that make up this group.
        ///
        /// This is called by methods which want to add a new group to an existing scene, in order
        /// to ensure that there are no clashes with groups already present.
        /// </summary>
        public void ResetIDs()
        {
            // As this is only ever called for prims which are not currently part of the scene (and hence
            // not accessible by clients), there should be no need to lock
            List<SceneObjectPart> partsList = new List<SceneObjectPart>(m_childParts.GetAllParts());

            foreach (SceneObjectPart part in partsList)
            {
                m_childParts.RemovePart(part); // the old IDs are changing
                part.ResetIDs(part.LinkNum); // Don't change link nums
                m_childParts.AddPart(part); // update the part lists with new IDs
                SetSitTarget(part, part.SitTargetActive, part.SitTargetPosition, part.SitTargetOrientation, false);
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="part"></param>
        public void ServiceObjectPropertiesFamilyRequest(IClientAPI remoteClient, UUID AgentID, uint RequestFlags)
        {

            remoteClient.SendObjectPropertiesFamilyData(RequestFlags, RootPart.UUID, RootPart.ObjectOwner, RootPart.GroupID, RootPart.BaseMask,
                                                        RootPart.OwnerMask, RootPart.GroupMask, RootPart.EveryoneMask, RootPart.NextOwnerMask,
                                                        RootPart.OwnershipCost, RootPart.ObjectSaleType, RootPart.SalePrice, RootPart.Category,
                                                        RootPart.CreatorID, RootPart.Name, RootPart.Description);
        }

        public void SetPartOwner(SceneObjectPart part, UUID cAgentID, UUID cGroupID)
        {
            part.OwnerID = cAgentID;
            part.GroupID = cGroupID;
        }

#endregion

#region Scheduling

        public override void Update()
        {
            //intentionally now does nothing. 
        }
        
        /// <summary>
        /// Schedule a full update for this scene object
        /// </summary>
        public void ScheduleGroupForFullUpdate(PrimUpdateFlags updateFlags)
        {
            if (IsDeleted)
                return;

            RootPart.ScheduleFullUpdate(updateFlags);
            m_childParts.ForEachPart((SceneObjectPart part) => {
                if (part != RootPart)
                    part.ScheduleFullUpdate(updateFlags);
            });
        }

        /// <summary>
        /// Schedule a terse update for this scene object
        /// </summary>
        public void ScheduleGroupForTerseUpdate()
        {
            if (IsDeleted)
                return;

            m_childParts.ForEachPart((SceneObjectPart part) => {
                if (part != RootPart)
                    part.ScheduleTerseUpdate();
            });
        }

        /// <summary>
        /// Immediately send a full update for this scene object.
        /// </summary>
        public void SendGroupFullUpdate(PrimUpdateFlags updateFlags)
        {
            if (IsDeleted)
                return;

            List<ScenePresence> avatars = Scene.GetScenePresences();

            RootPart.SendFullUpdateToAllClients(avatars, updateFlags);
            m_childParts.ForEachPart((SceneObjectPart part) => {
                if (part != RootPart)
                    part.SendFullUpdateToAllClients(avatars, updateFlags);
            });
        }

        public void QueueForUpdateCheck(SceneObjectPart part, SceneObjectPart.UpdateLevel level, PrimUpdateFlags updateFlags)
        {
            if (m_scene == null) // Need to check here as it's null during object creation
            {
                //m_log.DebugFormat("[SOG] Tried to schedule update, but m_scene is null. Part: {0}, Level: {1}", part, level);
                return;
            }

            if (DisableUpdates)
                return;

            m_scene.SceneGraph.AddToUpdateList(this, part, level, updateFlags);
        }

        /// <summary>
        /// Immediately send a terse update for this scene object.
        /// </summary>
        public void SendGroupTerseUpdate()
        {
            if (IsDeleted)
                return;

            List<ScenePresence> avatars = Scene.GetScenePresences();
            RootPart.SendTerseUpdateToAllClients(avatars);

            m_childParts.ForEachPart((SceneObjectPart part) => {
                if (part != RootPart)
                    part.SendTerseUpdateToAllClients(avatars);
            });
        }

#endregion

#region SceneGroupPart Methods

        /// <summary>
        /// Get the child part by LinkNum
        /// </summary>
        /// <param name="linknum"></param>
        /// <returns>null if no child part with that linknum or child part</returns>
        public SceneObjectPart GetLinkNumPart(int linknum)
        {
            if ((linknum > m_childParts.Count) || (linknum < 0))
                return null;    // skip O(n) search if out of range

            //TODO: This is terrible. We really should be indexing this somehow so we dont need O(n)
            foreach (SceneObjectPart part in m_childParts.GetAllParts())
            {
                if (part.LinkNum == linknum)
                {
                    return part;
                }
            }

            return null;
        }

        /// <summary>
        /// Get a child part with a given UUID
        /// </summary>
        /// <param name="primID"></param>
        /// <returns>null if a child part with the primID was not found</returns>
        public SceneObjectPart GetChildPart(UUID primID)
        {
            return m_childParts.FindPartByFullId(primID);
        }

        /// <summary>
        /// Get a child part with a given local ID
        /// </summary>
        /// <param name="localID"></param>
        /// <returns>null if a child part with the local ID was not found</returns>
        public SceneObjectPart GetChildPart(uint localID)
        {
            return m_childParts.FindPartByLocalId(localID);
        }
        

        /// <summary>
        /// Does this group contain the child prim
        /// should be able to remove these methods once we have a entity index in scene
        /// </summary>
        /// <param name="primID"></param>
        /// <returns></returns>
        public bool HasChildPrim(UUID primID)
        {
            return GetChildPart(primID) != null;
        }

        /// <summary>
        /// Does this group contain the child prim
        /// should be able to remove these methods once we have a entity index in scene
        /// </summary>
        /// <param name="localID"></param>
        /// <returns></returns>
        public bool HasChildPrim(uint localID)
        {
            return GetChildPart(localID) != null;
        }

#endregion

#region Packet Handlers

        /// <summary>
        /// Link the prims in otherGroup to this group
        /// </summary>
        /// <param name="objectGroup">The group of prims which should be linked to this group</param>
        public void LinkOtherGroupPrimsToThisGroup(SceneObjectGroup otherGroup)
        {
            if (otherGroup.RootPart == this.RootPart)
                return; // can't link that group to itself.


            SceneObjectPart otherGroupRoot = otherGroup.m_rootPart;

            Vector3 oldGroupPosition = otherGroupRoot.GroupPosition;
            Quaternion oldRootRotation = otherGroupRoot.RotationOffset;

            //calculate the difference between the other Groups Root position and ours,
            //then, use that result to set the offset position for the other group root
            otherGroupRoot.SetOffsetPositionDirect(otherGroupRoot.GroupPosition - AbsolutePosition);
            otherGroupRoot.SetGroupPositionDirect(AbsolutePosition);

            //use our new offset position that is now relative to our new parent which
            //is this groups root
            Vector3 axPos = otherGroupRoot.OffsetPosition;


            Quaternion parentRot = m_rootPart.RotationOffset;
            axPos *= Quaternion.Inverse(parentRot);

            otherGroupRoot.SetOffsetPositionDirect(axPos);
            Quaternion oldRot = otherGroupRoot.RotationOffset;
            Quaternion newRot = Quaternion.Inverse(parentRot) * oldRot;
            otherGroupRoot.SetRotationOffsetDirect(newRot);

            otherGroupRoot.ParentID = m_rootPart.LocalId;
            if (m_rootPart.LinkNum == 0)
                m_rootPart.LinkNum = 1;

            m_childParts.AddPartIfNotExists(otherGroupRoot);

            // Insert in terms of link numbers, the new links
            // before the current ones (with the exception of 
            // the root prim. Shuffle the old ones up
            int otherGroupPrimCount = otherGroup.PartCount;
                
            m_childParts.ForEachPart((SceneObjectPart part) => {
                if (part.LinkNum != 1)
                {
                    // Don't update root prim link number
                    part.LinkNum += otherGroupPrimCount;

                    // Repair any next permissions of child prims in the existing group
                    // to match those of the root prim. Only update the ones that affect others.
                    // The current user perms are not affected by a link.
                    // part.BaseMask = this.RootPart.BaseMask & part.BaseMask;
                    // part.OwnerMask = this.RootPart.OwnerMask & part.BaseMask;
                    part.NextOwnerMask = m_rootPart.NextOwnerMask & part.BaseMask;
                    part.GroupMask = m_rootPart.GroupMask & part.BaseMask;
                    part.EveryoneMask = m_rootPart.EveryoneMask & part.BaseMask;
                }
            });

            otherGroupRoot.LinkNum = 2;

            otherGroupRoot.SetParentAndUpdatePhysics(this);
            otherGroupRoot.ClearUndoState();
            otherGroupRoot.AddFlag(PrimFlags.CreateSelected);

            SetSitTarget(otherGroupRoot, otherGroupRoot.SitTargetActive, otherGroupRoot.SitTargetPosition, otherGroupRoot.SitTargetOrientation, false);

            // rest of parts
            int linkNum = 3;
            otherGroup.m_childParts.ForEachPart((SceneObjectPart part) =>
            {
                if (part.UUID != otherGroup.m_rootPart.UUID)
                {
                    LinkNonRootPart(part, oldGroupPosition, oldRootRotation, linkNum++);
                }

                // Now update permissions of child prims in the other group to match those of the new root prim.
                // Only update the ones that affect others. The current user perms are not affected by a link.
                // part.BaseMask = this.RootPart.BaseMask & part.BaseMask;
                // part.OwnerMask = this.RootPart.OwnerMask & part.BaseMask;
                part.NextOwnerMask = m_rootPart.NextOwnerMask & part.BaseMask;
                part.GroupMask = m_rootPart.GroupMask & part.BaseMask;
                part.EveryoneMask = m_rootPart.EveryoneMask & part.BaseMask;

                part.ClearUndoState();

                SetSitTarget(part, part.SitTargetActive, part.SitTargetPosition, part.SitTargetOrientation, false);
            });            

            otherGroup.m_childParts.Clear();

            m_scene.RemoveSceneObject(otherGroup, true, true, false);
            otherGroup.m_isDeleted = true;

            // Can't do this yet since backup still makes use of the root part without any synchronization
            //            objectGroup.m_rootPart = null;

            AttachToBackup();
            m_scene.InspectForAutoReturn(this);

            //Rebuild the bounding box
            ClearBoundingBoxCache();
            // Update the ServerWeight/LandImpact And StreamingCost
            RecalcPrimWeights();

            RootPart.ClearUndoState();
            RecalcScriptedStatus();

            HasGroupChanged = true;
            ScheduleGroupForFullUpdate(PrimUpdateFlags.ForcedFullUpdate);
        }

        /// <summary>
        /// Delink the given prim from this group.  The delinked prim is established as
        /// an independent SceneObjectGroup.
        /// </summary>
        /// <param name="partID"></param>
        public void DelinkFromGroup(uint partID)
        {
            DelinkFromGroup(partID, true);
        }

        /// <summary>
        /// Delink the given prim from this group.  The delinked prim is established as
        /// an independent SceneObjectGroup.
        /// </summary>
        /// <param name="partID"></param>
        /// <param name="sendEvents"></param>
        public void DelinkFromGroup(uint partID, bool sendEvents)
        {
            SceneObjectPart linkPart = GetChildPart(partID);

            if (linkPart != null)
            {
                DelinkFromGroup(linkPart, sendEvents, true);
            }
            else
            {
                m_log.InfoFormat("[SCENE OBJECT GROUP]: " +
                                 "DelinkFromGroup(): Child prim {0} not found in object {1}, {2}",
                                 partID, LocalId, UUID);
            }
        }

        public void DelinkFromGroup(SceneObjectPart linkPart, bool sendEvents, bool sendGroupUpdate)
        {
            linkPart.ClearUndoState();
            //                m_log.DebugFormat(
            //                    "[SCENE OBJECT GROUP]: Delinking part {0}, {1} from group with root part {2}, {3}",
            //                    linkPart.Name, linkPart.UUID, RootPart.Name, RootPart.UUID);

            Quaternion worldRot = linkPart.GetWorldRotation();

            this.RemoveSitTarget(linkPart.UUID);

            // Remove the part from this object
            m_childParts.RemovePart(linkPart);

            if (this.PartCount == 1 && RootPart != null) //Single prim is left
            {
                RootPart.LinkNum = 0;
            }
            else
            {
                m_childParts.ForEachPart((SceneObjectPart p) => {
                    if (p.LinkNum > linkPart.LinkNum)
                        p.LinkNum--;
                });
            }

            linkPart.ParentID = 0;
            linkPart.LinkNum = 0;

            // We need to reset the child part's position
            // ready for life as a separate object after being a part of another object
            Quaternion parentRot = m_rootPart.RotationOffset;

            Vector3 axPos = linkPart.OffsetPosition;
            axPos *= parentRot;

            Vector3 groupPosition = AbsolutePosition + axPos;

            linkPart.SetGroupPositionDirect(groupPosition);
            linkPart.SetOffsetPositionDirect(Vector3.Zero);
            linkPart.SetRotationOffsetDirect(worldRot);

            SceneObjectGroup objectGroup = new SceneObjectGroup(linkPart);

            PhysicsActor partActor = linkPart.PhysActor;
            if (partActor != null)
            {
                partActor.DelinkFromParent(groupPosition, worldRot);
            }

            m_scene.AddNewSceneObject(objectGroup, true);

            if (sendEvents)
                linkPart.TriggerScriptChangedEvent(Changed.LINK);

            linkPart.Rezzed = RootPart.Rezzed;

            //Rebuild the bounding box
            ClearBoundingBoxCache();

            // Update the ServerWeight/LandImpact and StreamingCost
            RecalcPrimWeights();
            RecalcScriptedStatus();

            if (sendGroupUpdate)
            {
                HasGroupChanged = true;
                ScheduleGroupForFullUpdate(PrimUpdateFlags.ForcedFullUpdate);
                objectGroup.ScheduleGroupForFullUpdate(PrimUpdateFlags.ForcedFullUpdate);
            }
        }

        /// <summary>
        /// Stop this object from being persisted over server restarts.
        /// </summary>
        /// <param name="objectGroup"></param>
        public void DetachFromBackup()
        {
            m_isBackedUp = false;
        }

        private void LinkNonRootPart(SceneObjectPart part, Vector3 oldGroupPosition, Quaternion oldGroupRotation, int linkNum)
        {
            Quaternion parentRot = oldGroupRotation;
            Quaternion oldRot = part.RotationOffset;
            Quaternion worldRot = parentRot * oldRot;

            Vector3 partOffsetPosition = part.OffsetPosition;
            Vector3 partGroupPosition = part.GroupPosition;
            Quaternion partRotationOffset = part.RotationOffset;

            parentRot = oldGroupRotation;

            Vector3 axPos = partOffsetPosition;
            axPos *= parentRot;
            partOffsetPosition = axPos;

            partGroupPosition = oldGroupPosition + partOffsetPosition;
            partOffsetPosition = Vector3.Zero;
            partRotationOffset = worldRot;
            
            m_childParts.AddPart(part);

            part.LinkNum = linkNum;

            partOffsetPosition = partGroupPosition - AbsolutePosition;

            Quaternion rootRotation = m_rootPart.RotationOffset;

            Vector3 pos = partOffsetPosition;
            pos *= Quaternion.Inverse(rootRotation);
            partOffsetPosition = pos;

            parentRot = m_rootPart.RotationOffset;
            oldRot = partRotationOffset;
            Quaternion newRot = Quaternion.Inverse(parentRot) * oldRot;
            partRotationOffset = newRot;

            part.SetOffsetPositionDirect(partOffsetPosition);
            part.SetGroupPositionDirect(partGroupPosition);
            part.SetRotationOffsetDirect(partRotationOffset);

            part.ParentID = m_rootPart.LocalId;
            part.SetParentAndUpdatePhysics(this);
            SetSitTarget(part, part.SitTargetActive, part.SitTargetPosition, part.SitTargetOrientation, false);
        }

        public bool GetBlockGrab()
        {
            PhysicsActor physActor = m_rootPart.PhysActor;
            if (physActor != null)
            {
                return physActor.Properties.BlockGrab;
            }
            return false;
        }

        public void SetBlockGrab(bool grab)
        {
            PhysicsActor physActor = m_rootPart.PhysActor;
            if (physActor != null)
            {
                physActor.Properties.BlockGrab = grab;
            }
        }

        /// <summary>
        /// If object is physical, apply force to move it around
        /// If object is not physical, just put it at the resulting location
        /// </summary>
        /// <param name="offset">Always seems to be 0,0,0, so ignoring</param>
        /// <param name="pos">New position.  We do the math here to turn it into a force</param>
        /// <param name="remoteClient"></param>
        public void GrabMovement(Vector3 offset, Vector3 pos, IClientAPI remoteClient)
        {
            if (m_scene.EventManager.TriggerGroupMove(UUID, pos))
            {
                // Updated to avoid grab drag of non-phys objects with simple mouse clicks (match SL)
                PhysicsActor physActor = m_rootPart.PhysActor;
                if (physActor != null)
                {
                    if (physActor.IsPhysical)
                    {
                        physActor.SetGrabTarget(pos, 0.25f);
                    }
                }
            }
        }

        public void DeGrab(IClientAPI remoteClient)
        {
            PhysicsActor physActor = m_rootPart.PhysActor;
            if (physActor != null)
            {
                if (physActor.IsPhysical)
                {
                    physActor.SetGrabTarget(Vector3.Zero, 0.0f);
                }
            }
        }

        public void NonPhysicalGrabMovement(Vector3 pos)
        {
            AbsolutePosition = pos;

            List<ScenePresence> avatars = Scene.GetScenePresences();
            m_rootPart.SendTerseUpdateToAllClients(avatars);
        }

        /// <summary>
        /// If object is physical, prepare for spinning torques (set flag to save old orientation)
        /// </summary>
        /// <param name="rotation">Rotation.  We do the math here to turn it into a torque</param>
        /// <param name="remoteClient"></param>
        public void SpinStart(IClientAPI remoteClient)
        {
            if (m_scene.EventManager.TriggerGroupSpinStart(UUID))
            {
                PhysicsActor physActor = m_rootPart.PhysActor;
                if (physActor != null)
                {
                    if (physActor.IsPhysical)
                    {
                        m_rootPart.IsWaitingForFirstSpinUpdatePacket = true;
                    }
                }
            }
        }

        /// <summary>
        /// If object is physical, apply torque to spin it around
        /// </summary>
        /// <param name="rotation">Rotation.  We do the math here to turn it into a torque</param>
        /// <param name="remoteClient"></param>
        public void SpinMovement(Quaternion newOrientation, IClientAPI remoteClient)
        {
            // The incoming newOrientation, sent by the client, "seems" to be the 
            // desired target orientation. This needs further verification; in particular, 
            // one would expect that the initial incoming newOrientation should be
            // fairly close to the original prim's physical orientation, 
            // m_rootPart.PhysActor.Orientation. This however does not seem to be the
            // case (might just be an issue with different quaternions representing the
            // same rotation, or it might be a coordinate system issue).
            //
            // Since it's not clear what the relationship is between the PhysActor.Orientation
            // and the incoming orientations sent by the client, we take an alternative approach
            // of calculating the delta rotation between the orientations being sent by the 
            // client. (Since a spin is invoked by ctrl+shift+drag in the client, we expect
            // a steady stream of several new orientations coming in from the client.)
            // This ensures that the delta rotations are being calculated from self-consistent
            // pairs of old/new rotations. Given the delta rotation, we apply a torque around
            // the delta rotation axis, scaled by the object mass times an arbitrary scaling
            // factor (to ensure the resulting torque is not "too strong" or "too weak").
            // 
            // Ideally we need to calculate (probably iteratively) the exact torque or series
            // of torques needed to arrive exactly at the destination orientation. However, since 
            // it is not yet clear how to map the destination orientation (provided by the viewer)
            // into PhysActor orientations (needed by the physics engine), we omit this step. 
            // This means that the resulting torque will at least be in the correct direction, 
            // but it will result in over-shoot or under-shoot of the target orientation.
            // For the end user, this means that ctrl+shift+drag can be used for relative,
            // but not absolute, adjustments of orientation for physical prims.

            if (m_scene.EventManager.TriggerGroupSpin(UUID, newOrientation))
            {
                PhysicsActor physActor = m_rootPart.PhysActor;
                if (physActor != null)
                {
                    if (physActor.IsPhysical)
                    {
                        if (m_rootPart.IsWaitingForFirstSpinUpdatePacket)
                        {
                            // first time initialization of "old" orientation for calculation of delta rotations
                            m_rootPart.SpinOldOrientation = newOrientation;
                            m_rootPart.IsWaitingForFirstSpinUpdatePacket = false;
                        }
                        else
                        {
                            // save and update old orientation
                            Quaternion old = m_rootPart.SpinOldOrientation;
                            m_rootPart.SpinOldOrientation = newOrientation;
                            //m_log.Error("[SCENE OBJECT GROUP]: Old orientation is " + old);
                            //m_log.Error("[SCENE OBJECT GROUP]: Incoming new orientation is " + newOrientation);

                            // compute difference between previous old rotation and new incoming rotation
                            Quaternion minimalRotationFromQ1ToQ2 = Quaternion.Inverse(old) * newOrientation;

                            float rotationAngle;
                            Vector3 rotationAxis;
                            minimalRotationFromQ1ToQ2.GetAxisAngle(out rotationAxis, out rotationAngle);
                            rotationAxis.Normalize();

                            //m_log.Error("SCENE OBJECT GROUP]: rotation axis is " + rotationAxis);
                            OpenMetaverse.Vector3 spinforce = new OpenMetaverse.Vector3(rotationAxis.X, rotationAxis.Y, rotationAxis.Z);
                            physActor.SetGrabSpinVelocity(spinforce * 3.0f);
                        }
                    }
                    else
                    {
                        //NonPhysicalSpinMovement(pos);
                    }
                }
                else
                {
                    //NonPhysicalSpinMovement(pos);
                }
            }
        }

        /// <summary>
        /// Return metadata about a prim (name, description, sale price, etc.)
        /// </summary>
        /// <param name="client"></param>
        public void GetProperties(IClientAPI client)
        {
            m_rootPart.GetProperties(client);
        }

        /// <summary>
        /// Set the name of a prim
        /// </summary>
        /// <param name="name"></param>
        /// <param name="localID"></param>
        public void SetPartName(string name, uint localID)
        {
            SceneObjectPart part = GetChildPart(localID);
            if (part != null)
            {
                part.Name = name;
            }
        }

        public void SetPartDescription(string des, uint localID)
        {
            SceneObjectPart part = GetChildPart(localID);
            if (part != null)
            {
                part.Description = des;
            }
        }

        public void SetPartText(string text, uint localID)
        {
            SceneObjectPart part = GetChildPart(localID);
            if (part != null)
            {
                part.SetText(text);
            }
        }

        public void SetPartText(string text, UUID partID)
        {
            SceneObjectPart part = GetChildPart(partID);
            if (part != null)
            {
                part.SetText(text);
            }
        }

        public string GetPartName(uint localID)
        {
            SceneObjectPart part = GetChildPart(localID);
            if (part != null)
            {
                return part.Name;
            }
            return String.Empty;
        }

        public string GetPartDescription(uint localID)
        {
            SceneObjectPart part = GetChildPart(localID);
            if (part != null)
            {
                return part.Description;
            }
            return String.Empty;
        }

        /// <summary>
        /// Update prim flags for this group.
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="type"></param>
        /// <param name="inUse"></param>
        /// <param name="data"></param>
        public void UpdateFlags(bool UsePhysics, bool IsTemporary, bool IsPhantom, bool IsVolumeDetect,
                                    ObjectFlagUpdatePacket.ExtraPhysicsBlock[] blocks)
        {
            SceneObjectPart rootPart = RootPart;

            if (IsTemporary)
            {
                //only do this if it is actually changing since it is a heavy op
                //and can be blocked by object persistence
                if ((RootPart.Flags & PrimFlags.TemporaryOnRez) == 0)
                {
                    DetachFromBackup();
                    Scene.RemoveFromPotentialReturns(this);
                    // Remove from database and parcel prim count
                    //
                    if (IsPersisted) m_scene.DeleteFromStorage(UUID);
                }
            }
            else
            {
                // Ensure that changes made to this object are persisted.
                AttachToBackup();
                Scene.InspectForAutoReturn(this);
            }

            // I don't think the prim counts look at temp objects, but this was here in the normal->Temp case.
            // If it needs to remain at all, it also needs to be in the else clause, the Temp->normal case.
            // (We need to include both temp and normal prims in counts or it could be a griefer performance exploit.)
            m_scene.EventManager.TriggerParcelPrimCountTainted();

            //changing any of these flags puts the root part in charge of setting flags
            //on the children
            rootPart.UpdatePrimFlags(UsePhysics, IsTemporary, IsPhantom, IsVolumeDetect, blocks);
        }

        public void AddGroupFlagValue(PrimFlags flag)
        {
            ForEachPart((SceneObjectPart part) =>
            {
                part.AddFlag(flag);
            }
            );
        }

        public void RemGroupFlagValue(PrimFlags flag)
        {
            ForEachPart((SceneObjectPart part) =>
            {
                part.RemFlag(flag);
            }
            );
        }

        public void UpdateExtraParam(uint localID, ushort type, bool inUse, byte[] data)
        {
            SceneObjectPart part = GetChildPart(localID);
            if (part != null)
            {
                part.UpdateExtraParam(type, inUse, data);
            }
        }

        public int GetLinkNumFor(object o)
        {
            if (o is SceneObjectPart)
                return (o as SceneObjectPart).LinkNum;

            if (o is ScenePresence)
                return (o as ScenePresence).LinkNum;

            return 0;
        }

        /// <summary>
        /// Get the parts of this scene object safely
        /// </summary>
        /// <returns></returns>
        public IReadOnlyCollection<SceneObjectPart> GetParts()
        {
            return m_childParts.GetAllParts();
        }

        /// <summary>
        /// Get the parts of this scene object safely excluding the passed in part
        /// </summary>
        /// <returns></returns>
        public IEnumerable<SceneObjectPart> GetPartsExcluding(SceneObjectPart child)
        {
            IReadOnlyCollection<SceneObjectPart> parts = m_childParts.GetAllParts();
            return parts.Except(new List<SceneObjectPart> { child });
        }

        /// <summary>
        /// Get the avatars seated on this scene object safely
        /// </summary>
        /// <returns>the list of ScenePresence objects</returns>
        public IReadOnlyCollection<ScenePresence> GetSeatedAvatars()
        {
            return m_childAvatars.GetAllParts();
        }

        public List<ScenePresence> GetAvatarsAsList()
        {
            List<ScenePresence> ret = new List<ScenePresence>();
            m_childAvatars.ForEach((ScenePresence sp) => {
                ret.Add(sp);
            });
            ret.Sort(delegate(ScenePresence p1, ScenePresence p2)
                {
                    return p1.LinkNum.CompareTo(p2.LinkNum);
                });

            return ret;
        }

        public List<object> GetAllLinksAsList(bool includeAvatars)
        {
            List<object> ret = new List<object>();
            m_childParts.ForEachPart((SceneObjectPart part) => {
                ret.Add(part);
            });
            if (includeAvatars)
            {
                m_childAvatars.ForEach((ScenePresence sp) =>
                {
                    ret.Add(sp);
                });
            }
            ret.Sort(delegate(object o1, object o2)
                {
                    return GetLinkNumFor(o1).CompareTo(GetLinkNumFor(o2));
                });
            return ret;
        }

        public List<object> GetAllLinksAsListExcept(SceneObjectPart except, bool includeAvatars)
        {
            List<object> ret = new List<object>();
            m_childParts.ForEachPart((SceneObjectPart part) => {
                if ((except == null) || (part.UUID != except.UUID))
                    ret.Add(part);
            });
            if (includeAvatars)
            {
                m_childAvatars.ForEach((ScenePresence sp) =>
                {
                    ret.Add(sp);
                });
            }
            ret.Sort(delegate(object o1, object o2)
                {
                    return GetLinkNumFor(o1).CompareTo(GetLinkNumFor(o2));
                });
            return ret;
        }

        /// <summary>
        /// Update the texture entry for this part
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="textureEntry"></param>
        public void UpdateTextureEntry(uint localID, byte[] textureEntry)
        {
            SceneObjectPart part = GetChildPart(localID);
            if (part != null)
            {
                part.UpdateTextureEntry(textureEntry);
            }
        }

        // Returns false if the update was blocked because the object was attached.
        public bool UpdatePermissions(UUID AgentID, byte field, uint localID,
                uint mask, byte addRemTF)
        {
            bool rc = true;
            
            m_childParts.ForEachPart((SceneObjectPart part) => {
                rc &= part.UpdatePermissions(AgentID, field, localID, mask,
                        addRemTF);
            });

            HasGroupChanged = true;
            return rc;
        }

#endregion

#region Shape

        /// <summary>
        ///
        /// </summary>
        /// <param name="shapeBlock"></param>
        public void UpdateShape(ObjectShapePacket.ObjectDataBlock shapeBlock, uint localID)
        {
            SceneObjectPart part = GetChildPart(localID);
            if (part != null)
            {
                part.UpdateShape(shapeBlock);
            }
        }

#endregion

#region Resize

        /// <summary>
        /// Resize the given part
        /// </summary>
        /// <param name="scale"></param>
        /// <param name="localID"></param>
        public void Resize(Vector3 scale, uint localID)
        {
            if (scale.X > m_scene.m_maxNonphys)
                scale.X = m_scene.m_maxNonphys;
            if (scale.Y > m_scene.m_maxNonphys)
                scale.Y = m_scene.m_maxNonphys;
            if (scale.Z > m_scene.m_maxNonphys)
                scale.Z = m_scene.m_maxNonphys;

            SceneObjectPart part = GetChildPart(localID);
            if (part != null)
            {
                part.Resize(scale);

                PhysicsActor physActor = part.PhysActor;
                if (physActor != null)
                {
                    if (physActor.IsPhysical)
                    {
                        if (scale.X > m_scene.m_maxPhys)
                            scale.X = m_scene.m_maxPhys;
                        if (scale.Y > m_scene.m_maxPhys)
                            scale.Y = m_scene.m_maxPhys;
                        if (scale.Z > m_scene.m_maxPhys)
                            scale.Z = m_scene.m_maxPhys;
                    }

                    m_scene.PhysicsScene.AddPhysicsActorTaint(physActor, TaintType.ChangedScale);
                }
                //if (part.UUID != m_rootPart.UUID)

                HasGroupChanged = true;
                ScheduleGroupForFullUpdate(PrimUpdateFlags.FindBest);
            }
        }

        public void GroupResize(Vector3 scale, uint localID)
        {
            SceneObjectPart part = GetChildPart(localID);
            if (part != null)
            {
                if (scale.X > m_scene.m_maxNonphys)
                    scale.X = m_scene.m_maxNonphys;
                if (scale.Y > m_scene.m_maxNonphys)
                    scale.Y = m_scene.m_maxNonphys;
                if (scale.Z > m_scene.m_maxNonphys)
                    scale.Z = m_scene.m_maxNonphys;

                PhysicsActor physActor = part.PhysActor;
                if (physActor != null && physActor.IsPhysical)
                {
                    if (scale.X > m_scene.m_maxPhys)
                        scale.X = m_scene.m_maxPhys;
                    if (scale.Y > m_scene.m_maxPhys)
                        scale.Y = m_scene.m_maxPhys;
                    if (scale.Z > m_scene.m_maxPhys)
                        scale.Z = m_scene.m_maxPhys;
                }
                float x = (scale.X / part.Scale.X);
                float y = (scale.Y / part.Scale.Y);
                float z = (scale.Z / part.Scale.Z);

                
                if (x > 1.0f || y > 1.0f || z > 1.0f)
                {
                    m_childParts.ForEachPart((SceneObjectPart obPart) => {
                        if (obPart.UUID != m_rootPart.UUID)
                        {
                            Vector3 oldSize = new Vector3(obPart.Scale);

                            float f = 1.0f;
                            float a = 1.0f;

                            PhysicsActor partActor = part.PhysActor;
                            if (partActor != null && partActor.IsPhysical)
                            {
                                if (oldSize.X * x > m_scene.m_maxPhys)
                                {
                                    f = m_scene.m_maxPhys / oldSize.X;
                                    a = f / x;
                                    x *= a;
                                    y *= a;
                                    z *= a;
                                }
                                if (oldSize.Y * y > m_scene.m_maxPhys)
                                {
                                    f = m_scene.m_maxPhys / oldSize.Y;
                                    a = f / y;
                                    x *= a;
                                    y *= a;
                                    z *= a;
                                }
                                if (oldSize.Z * z > m_scene.m_maxPhys)
                                {
                                    f = m_scene.m_maxPhys / oldSize.Z;
                                    a = f / z;
                                    x *= a;
                                    y *= a;
                                    z *= a;
                                }
                            }
                            else
                            {
                                if (oldSize.X * x > m_scene.m_maxNonphys)
                                {
                                    f = m_scene.m_maxNonphys / oldSize.X;
                                    a = f / x;
                                    x *= a;
                                    y *= a;
                                    z *= a;
                                }
                                if (oldSize.Y * y > m_scene.m_maxNonphys)
                                {
                                    f = m_scene.m_maxNonphys / oldSize.Y;
                                    a = f / y;
                                    x *= a;
                                    y *= a;
                                    z *= a;
                                }
                                if (oldSize.Z * z > m_scene.m_maxNonphys)
                                {
                                    f = m_scene.m_maxNonphys / oldSize.Z;
                                    a = f / z;
                                    x *= a;
                                    y *= a;
                                    z *= a;
                                }
                            }
                        }
                    });
                }

                if (x == 1 && y == 1 && z == 1)
                    return;//No point, it's the same scale

                m_childParts.ForEachPart((SceneObjectPart obPart) => {
                    obPart.StoreUndoState();
                    obPart.IgnoreUndoUpdate = true;
                });

                Vector3 prevScale = part.Scale;
                prevScale.X *= x;
                prevScale.Y *= y;
                prevScale.Z *= z;
                part.Resize(prevScale);

                
                m_childParts.ForEachPart((SceneObjectPart obPart) => {
                    if (obPart.UUID != m_rootPart.UUID)
                    {
                        Vector3 currentpos = new Vector3(obPart.OffsetPosition);
                        currentpos.X *= x;
                        currentpos.Y *= y;
                        currentpos.Z *= z;
                        Vector3 newSize = new Vector3(obPart.Scale);
                        newSize.X *= x;
                        newSize.Y *= y;
                        newSize.Z *= z;
                        obPart.Resize(newSize);
                        obPart.UpdateOffSet(currentpos);
                    }

                    obPart.IgnoreUndoUpdate = false;
                });
                

                PhysicsActor partActor2 = part.PhysActor;
                if (partActor2 != null)
                {
                    m_scene.PhysicsScene.AddPhysicsActorTaint(partActor2, TaintType.ChangedScale);
                }

                HasGroupChanged = true;
                ScheduleGroupForTerseUpdate();
            }
        }

#endregion

#region Position

        /// <summary>
        /// Move this scene object
        /// </summary>
        /// <param name="pos"></param>
        public void UpdateGroupPosition(Vector3 pos, bool SaveUpdate)
        {
            if (InTransit)
                return; // discard the update while in transit

            if (pos == AbsolutePosition)
                return; // nothing to do

            if (SaveUpdate)
            {
                m_childParts.ForEachPart((SceneObjectPart part) => {
                    part.StoreUndoState();
                });
            }

            if (m_scene.EventManager.TriggerGroupMove(UUID, pos))
            {
                if (IsAttachment)
                {
                    m_rootPart.AttachedPos = pos;
                    m_rootPart.SavedAttachmentPos = pos;
                }

                AbsolutePosition = pos;

                HasGroupChanged = true;
            }

            //we need to do a terse update even if the move wasn't allowed
            // so that the position is reset in the client (the object snaps back)
            if (!(InTransit || IsDeleted))
            {
                // ScheduleGroupForTerseUpdate();   
                // If moving the whole object, only send the update for the root part
                m_rootPart.ScheduleTerseUpdate();
            }

        }

        /// <summary>
        /// Update the position of a single part of this scene object
        /// </summary>
        /// <param name="pos"></param>
        /// <param name="localID"></param>
        public void UpdateSinglePosition(Vector3 pos, uint localID, bool saveUpdate)
        {
            SceneObjectPart part = GetChildPart(localID);

            if (saveUpdate)
            {
                part.StoreUndoState();
                part.IgnoreUndoUpdate = true;
            }

            if (part != null)
            {
                if (part.UUID == m_rootPart.UUID)
                {
                    UpdateRootPosition(pos, saveUpdate);
                }
                else
                {
                    part.UpdateOffSet(pos);
                }

                part.IgnoreUndoUpdate = false;

                HasGroupChanged = true;
            }
        }

        /// <summary>
        /// Changes the position of the root in the group, meaning we're not moving the entire group,
        /// just the root prim, so all child subpositions need to be recalculated
        /// </summary>
        /// <param name="pos"></param>
        private void UpdateRootPosition(Vector3 pos, bool saveUpdate)
        {
            if (saveUpdate)
            {
                m_childParts.ForEachPart((SceneObjectPart part) => {
                    part.StoreUndoState();
                });
            }

            Vector3 newPos = pos;
            Vector3 oldPos = AbsolutePosition + m_rootPart.OffsetPosition;
            if (IsAttachment)
            {
                oldPos = m_rootPart.AttachedPos;
                m_rootPart.AttachedPos = newPos;
                m_rootPart.SavedAttachmentPos = pos;
            }

            Vector3 diff = oldPos - newPos;
            Vector3 axDiff = diff;
            Quaternion partRotation = m_rootPart.RotationOffset;
            axDiff *= Quaternion.Inverse(partRotation);
            diff = axDiff;

            //            m_log.DebugFormat("UpdateRootPosition: Abs={0} Old={1} New={2} Diff={3}\n", AbsolutePosition, oldPos, newPos, diff);

            // pos (newpos) comes in as the new root/group offset, not an absolute position

            
            m_childParts.ForEachPart((SceneObjectPart obPart) => {
                if (obPart.UUID != m_rootPart.UUID)
                {
                    obPart.OffsetPosition = obPart.OffsetPosition + diff;
                }
            });
            

            AbsolutePosition = newPos;    // Updates GroupPosition in all parts

            HasGroupChanged = true;
            ScheduleGroupForTerseUpdate();
        }

        public void OffsetForNewRegion(Vector3 offset)
        {
            m_childParts.ForEachPart((SceneObjectPart part) => {
                part.StoreUndoState();
            });

            m_rootPart.SetGroupPosition(offset, true, false);
        }

#endregion

#region Rotation

        /// <summary>
        ///
        /// </summary>
        /// <param name="rot"></param>
        public void UpdateGroupRotation(Quaternion rot, bool SaveUpdate)
        {
            if (SaveUpdate)
            {
                m_childParts.ForEachPart((SceneObjectPart part) => {
                    part.StoreUndoState();
                });
            }

            m_rootPart.UpdateRotation(rot);

            HasGroupChanged = true;
            ScheduleGroupForTerseUpdate();
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="pos"></param>
        /// <param name="rot"></param>
        public void UpdateGroupRotation(Vector3 pos, Quaternion rot)
        {
            UpdateGroupRotation(rot, true);  // does a StoreUndoState for all prims in the group
            UpdateGroupPosition(pos, false);

            HasGroupChanged = true;
            ScheduleGroupForTerseUpdate();
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="rot"></param>
        /// <param name="localID"></param>
        public void UpdateSingleRotation(Quaternion rot, uint localID)
        {
            m_childParts.ForEachPart((SceneObjectPart ppart) => {
                ppart.StoreUndoState();
            });

            SceneObjectPart part = GetChildPart(localID);
            if (part != null)
            {
                if (part.UUID == m_rootPart.UUID)
                {
                    UpdateRootPositionRotation(part.AbsolutePosition, rot);
                }
                else
                {
                    part.IgnoreUndoUpdate = true;
                    part.UpdateRotation(rot);
                    part.IgnoreUndoUpdate = false;
                    part.StoreUndoState();
                }
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="rot"></param>
        /// <param name="pos"></param>
        /// <param name="localID"></param>
        public void UpdateSingleRotation(Quaternion rot, Vector3 pos, uint localID)
        {
            SceneObjectPart part = GetChildPart(localID);
            if (part != null)
            {
                if (part.UUID == m_rootPart.UUID)
                {
                    UpdateRootPositionRotation(pos, rot);
                }
                else
                {
                    part.StoreUndoState();
                    part.IgnoreUndoUpdate = true;
                    part.UpdateRotation(rot);
                    part.OffsetPosition = pos;
                    part.IgnoreUndoUpdate = false;
                }
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="rot"></param>
        private void UpdateRootPositionRotation(Vector3 pos, Quaternion rot)
        {
            Quaternion axRot = rot;
            Quaternion oldParentRot = m_rootPart.RotationOffset;
            Vector3 oldParentPos = IsAttachment ? m_rootPart.AttachedPos : m_rootPart.AbsolutePosition;
            
            m_childParts.ForEachPart((SceneObjectPart prim) => {
                prim.StoreUndoState();
                prim.IgnoreUndoUpdate = true;
            });

            UpdateGroupPosition(pos, false);
            m_rootPart.UpdateRotation(rot);

            PhysicsActor rootActor = m_rootPart.PhysActor;
            if (rootActor != null)
            {
                rootActor.Rotation = m_rootPart.RotationOffset;
            }

            m_childParts.ForEachPart((SceneObjectPart prim) => {
                if (prim.UUID != m_rootPart.UUID)
                {
                    // Normalize old pos/rot
                    Vector3 axPos = prim.OffsetPosition;
                    axPos *= oldParentRot;

                    // Update for new pos/rot
                    axPos -= (pos - oldParentPos);
                    prim.OffsetPosition = axPos * Quaternion.Inverse(axRot);

                    //get the prim world rotation back
                    Quaternion newRot = oldParentRot * prim.RotationOffset;
                    //calculate the new child offset rotation
                    prim.RotationOffset = Quaternion.Inverse(axRot) * newRot;

                    prim.ScheduleTerseUpdate();
                }
            });

            m_childParts.ForEachPart((SceneObjectPart prim) => {
                prim.IgnoreUndoUpdate = false;
            });

            m_rootPart.ScheduleTerseUpdate();
        }

#endregion

        public int RegisterTargetWaypoint(Vector3 target, float tolerance)
        {
            ScriptPosTarget waypoint = new ScriptPosTarget();
            waypoint.TargetPos = target;
            waypoint.Tolerance = tolerance;
            int handle = (int)m_scene.AllocateLocalId();
            waypoint.Handle = handle;
            lock (m_targetsLock)
            {
                if (m_targets.Count >= MAX_TARGETS)
                {   // table full, remove the first one
                    ScriptPosTarget discard = m_targets[0];
                    m_scene.SceneGraph.UnregisterObjectWithTargets(discard.Handle);
                    m_targets.RemoveAt(0);
                }
                m_targets.Add(waypoint);
            }
            m_scene.SceneGraph.RegisterObjectWithTargets(handle, this);
            return handle;
        }

        public void UnregisterTargetWaypoint(int handle)
        {
            lock (m_targetsLock)
            {
                int ndx = m_targets.FindIndex(
                                delegate(ScriptPosTarget target)
                                {
                                    return handle == target.Handle;
                                }
                                );
                if (ndx >= 0)
                {   // found that handle
                    ScriptPosTarget discard = m_targets[ndx];
                    m_scene.SceneGraph.UnregisterObjectWithTargets(discard.Handle);
                    m_targets.RemoveAt(ndx);
                }
            }
        }

        internal int RegisterRotationTarget(Quaternion rot, float error)
        {
            ScriptRotTarget rotTarget = new ScriptRotTarget
            {
                TargetRot = rot,
                Tolerance = error,
                Handle = (int)m_scene.AllocateLocalId()
            };

            lock (m_targetsLock)
            {
                if (m_rotTargets.Count >= MAX_TARGETS)
                {   // table full, remove the first one
                    ScriptRotTarget discard = m_rotTargets[0];
                    m_scene.SceneGraph.UnregisterObjectWithTargets(discard.Handle);
                    m_rotTargets.RemoveAt(0);
                }
                m_rotTargets.Add(rotTarget);
            }
            m_scene.SceneGraph.RegisterObjectWithTargets(rotTarget.Handle, this);
            return rotTarget.Handle;
        }

        internal void UnregisterRotationTarget(int handle)
        {
            lock (m_targetsLock)
            {
                int ndx = m_rotTargets.FindIndex(
                                delegate(ScriptRotTarget target)
                                {
                                    return handle == target.Handle;
                                }
                                );
                if (ndx >= 0)
                {   // found that handle
                    ScriptRotTarget discard = m_rotTargets[ndx];
                    m_scene.SceneGraph.UnregisterObjectWithTargets(discard.Handle);
                    m_rotTargets.RemoveAt(ndx);
                }
            }
        }

        private void ClearTargetWaypoints()
        {
            lock (m_targetsLock)
            {
                foreach (ScriptPosTarget target in m_targets)
                {
                    m_scene.SceneGraph.UnregisterObjectWithTargets(target.Handle);
                }
                m_targets.Clear();
            }
        }

        private void ClearRotWaypoints()
        {
            lock (m_targetsLock)
            {
                foreach (ScriptRotTarget target in m_rotTargets)
                {
                    m_scene.SceneGraph.UnregisterObjectWithTargets(target.Handle);
                }
                m_rotTargets.Clear();
            }
        }

        public void CheckAtTargets()
        {
            if (m_scriptListens_atTarget || m_scriptListens_notAtTarget)
            {
                DoCheckPosTargets();
            }

            if (m_scriptListens_atRotTarget || m_scriptListens_notAtRotTarget)
            {
                DoCheckRotTargets();
            }
        }

        private void DoCheckRotTargets()
        {
            if (m_rotTargets.Count > 0)
            {
                bool at_target = false;
                List<ScriptRotTarget> atTargets = new List<ScriptRotTarget>(MAX_TARGETS);
                lock (m_targetsLock)
                {
                    foreach (ScriptRotTarget target in m_rotTargets)
                    {
                        Quaternion normRot = Quaternion.Normalize(m_rootPart.GetWorldRotation());
                        if (target.TargetRot.ApproxEquals(normRot, target.Tolerance) ||
                            target.TargetRot.ApproxEquals(Quaternion.Negate(normRot), target.Tolerance))    //remember the negated quat is the same rotation
                        {
                            at_target = true;
                            // trigger at_target
                            if (m_scriptListens_atRotTarget)
                            {
                                atTargets.Add(target);
                            }
                        }
                    }
                }

                if (atTargets.Count > 0)
                {
                    uint[] localids = this.CollectLocalIds();
                    
                    for (int ctr = 0; ctr < localids.Length; ctr++)
                    {
                        foreach (ScriptRotTarget att in atTargets)
                        {
                            m_scene.EventManager.TriggerAtRotTargetEvent(
                                localids[ctr], att.Handle, att.TargetRot, m_rootPart.GetWorldRotation());
                        }
                    }

                    return;
                }

                if (m_scriptListens_notAtRotTarget && !at_target)
                {
                    //trigger not_at_target
                    uint[] localids = this.CollectLocalIds();

                    for (int ctr = 0; ctr < localids.Length; ctr++)
                    {
                        m_scene.EventManager.TriggerNotAtRotTargetEvent(localids[ctr]);
                    }
                }
            }
        }

        private void DoCheckPosTargets()
        {
            if (m_targets.Count > 0)
            {
                bool at_target = false;
                List<ScriptPosTarget> atTargets = new List<ScriptPosTarget>(MAX_TARGETS);
                lock (m_targetsLock)
                {
                    foreach (ScriptPosTarget target in m_targets)
                    {
                        if (Util.GetDistanceTo(target.TargetPos, m_rootPart.GroupPosition) <= target.Tolerance)
                        {
                            at_target = true;
                            // trigger at_target
                            if (m_scriptListens_atTarget)
                            {
                                ScriptPosTarget att = new ScriptPosTarget();
                                att.TargetPos = target.TargetPos;
                                att.Tolerance = target.Tolerance;
                                att.Handle = target.Handle;
                                atTargets.Add(att);
                            }
                        }
                    }
                }

                if (atTargets.Count > 0)
                {
                    uint[] localids = CollectLocalIds();

                    for (int ctr = 0; ctr < localids.Length; ctr++)
                    {
                        foreach (ScriptPosTarget att in atTargets)
                        {
                            m_scene.EventManager.TriggerAtTargetEvent(
                                localids[ctr], att.Handle, att.TargetPos, m_rootPart.GroupPosition);
                        }
                    }

                    return;
                }

                if (m_scriptListens_notAtTarget && !at_target)
                {
                    //trigger not_at_target
                    uint[] localids = this.CollectLocalIds();

                    for (int ctr = 0; ctr < localids.Length; ctr++)
                    {
                        m_scene.EventManager.TriggerNotAtTargetEvent(localids[ctr]);
                    }
                }
            }
        }

        /// <summary>
        /// Returns a collection of all prim local IDs that belong to this group
        /// </summary>
        /// <returns></returns>
        private uint[] CollectLocalIds()
        {
            IReadOnlyCollection<SceneObjectPart> children = m_childParts.GetAllParts();

            uint[] localids = new uint[children.Count];
            int cntr = 0;

            foreach (SceneObjectPart part in children)
            {
                localids[cntr] = part.LocalId;
                cntr++;
            }

            return localids;
        }

        public float GetMass()
        {
            float retmass = 0f;

            m_childParts.ForEachPart((SceneObjectPart part) => {
                retmass += part.GetMass();
            });

            return retmass;
        }

        public float GetAvatarMass()
        {
            SceneObjectPart rootpart = m_rootPart;
            if (rootpart != null)
            {
                ScenePresence avatar = m_scene.GetScenePresence(rootpart.AttachedAvatar);
                if (avatar != null)
                {
                    var phyActor = avatar.PhysicsActor;

                    if (phyActor != null)
                    {
                        return phyActor.Mass;
                    }
                }
            }

            return 0;
        }

        /// <summary>
        /// Set the user group to which this scene object belongs.
        /// </summary>
        /// <param name="GroupID"></param>
        /// <param name="client"></param>
        public void SetGroup(UUID GroupID, IClientAPI client)
        {
            m_childParts.ForEachPart((SceneObjectPart part) => {
                part.SetGroup(GroupID, client);
                part.Inventory.ChangeInventoryGroup(GroupID);
            });

            HasGroupChanged = true;

            ScheduleGroupForFullUpdate(PrimUpdateFlags.PrimData);
        }

        public void SetGeneration(int value)
        {
            m_childParts.ForEachPart((SceneObjectPart part) => {
                part.Generation = value;
            });
        }

        public void TriggerScriptChangedEvent(Changed val)
        {
            m_childParts.ForEachPart((SceneObjectPart part) => {
                part.TriggerScriptChangedEvent(val);
            });
        }

        public override string ToString()
        {
            return String.Format("{0} {1} ({2})", Name, UUID, AbsolutePosition);
        }

        public void SetAttachmentPoint(byte point)
        {
            m_childParts.ForEachPart((SceneObjectPart part) => {
                part.SetAttachmentPoint(point);
            });
        }

#region ISceneObject

        public virtual ISceneObject CloneForNewScene()
        {
            SceneObjectGroup sog = Copy(this.OwnerID, this.GroupID, false);
            sog.m_isDeleted = false;
            sog.m_scene = null;
            return sog;
        }

        // This function replaces all the item IDs (UUIDs) of the group's inventory contents with new IDs,
        // then returns a dictionary of the map[oldID] = newID.
        public Dictionary<UUID, UUID> RemapItemIDs()
        {
            Dictionary<UUID, UUID> itemIDMap = new Dictionary<UUID, UUID>();
            
            m_childParts.ForEachPart((SceneObjectPart part) => {
                TaskInventoryDictionary newInventory = new TaskInventoryDictionary();
                lock (part.TaskInventory)
                {
                    foreach (KeyValuePair<UUID, TaskInventoryItem> inv in part.TaskInventory)
                    {
                        UUID oldID = new UUID(inv.Key);
                        UUID newID = UUID.Random();
                        TaskInventoryItem item = inv.Value;

                        itemIDMap[oldID] = newID;

                        item.ItemID = newID;
                        newInventory[newID] = item;
                    }
                    part.TaskInventory = newInventory;
                }
            });
            
            return itemIDMap;
        }

        public virtual string ExtraToXmlString()
        {
            return "<ExtraFromAssetID>" + GetFromItemID().ToString() + "</ExtraFromAssetID>";
        }

        public virtual void ExtraFromXmlString(string xmlstr)
        {
            string id = xmlstr.Substring(xmlstr.IndexOf("<ExtraFromAssetID>"));
            id = xmlstr.Replace("<ExtraFromAssetID>", String.Empty);
            id = id.Replace("</ExtraFromAssetID>", String.Empty);

            UUID uuid = UUID.Zero;
            UUID.TryParse(id, out uuid);

            SetFromItemID(uuid);
        }
#endregion

        internal void LocalIdUpdated(SceneObjectPart part, uint oldLocalId, uint value)
        {
            m_childParts.PartLocalIdUpdated(part, oldLocalId, value);
        }

        public bool Rationalize(UUID itemOwner, bool fromCrossing)
        {
            bool ownerChanged = false;

            m_childParts.ForEachPart((SceneObjectPart part) => {
                ownerChanged |= part.Rationalize(itemOwner, fromCrossing);
            });

            return ownerChanged;
        }

        public bool HasSittingAvatars
        {
            get
            {
                if (IsAttachment) return false;

                return m_childAvatars.Count != 0;
            }
        }

        public bool IsSeatedAnywhere(UUID agentID)
        {
            if (agentID == UUID.Zero)
                return false;

            return m_childAvatars.FindPartByFullId(agentID) != null;
        }

        public void ForEachSittingAvatar(Action<ScenePresence> action)
        {
            m_childAvatars.ForEach(action);
        }

        // These two must be called from the correspondingly-named SceneObjectPart methods.
        // They handle updates to the part data and script notifications.
        public void AddSeatedAvatar(UUID partID, ScenePresence sp, bool sendEvent)
        {
            if (m_childAvatars.AddAvatar(sp))
            {
                sp.LinkNum = LinkCount; // add to end

                SitTargetInfo sitInfo = SitTargetForPart(partID);
                sitInfo.SeatAvatar(sp);

                if (sendEvent)
                    TriggerScriptChangedEvent(Changed.LINK);
            }
        }
        public void RemoveSeatedAvatar(UUID partID, ScenePresence sp, bool sendEvent)
        {
            SitTargetInfo sitInfo = SitTargetForPart(partID);
            sitInfo.SeatAvatar(null);

            int linkNum = sp.LinkNum;   // save it
            sp.LinkNum = 0;
            if (m_childAvatars.RemoveAvatar(sp))
            {
                // Now fix avatar link numbers
                RecalcSeatedAvatarLinks();
                if (sendEvent)
                    TriggerScriptChangedEvent(Changed.LINK);
            }
        }
        public ScenePresence GetSeatedAvatarByLink(int linknum)
        {
            ScenePresence result = null;
            m_childAvatars.ForEach((ScenePresence sp) =>
            {
                if (sp.LinkNum == linknum)
                    result = sp;
            });
            return result;
        }

        /// <summary>
        /// This method updates all seated avatars with new link numbers after new links are added or and avatar or link removed.
        /// Avatars are always added to the end (so no need to call this).
        /// </summary>
        public void RecalcSeatedAvatarLinks()
        {
            List<ScenePresence> avatars = GetAvatarsAsList();   // comes back sorted by link number
            int next = PartCount+1;

            avatars.ForEach((ScenePresence sp) =>
            {
                sp.LinkNum = next++;
            });
        }

        /// <summary>
        /// Marks the sitting avatars in transit, and waits for this group to be sent
        /// before sending the avatars over to the neighbor region
        /// </summary>
        /// <param name="newRegionHandle"></param>
        /// <returns></returns>
        public async Task BeginCrossSittingAvatars(ulong newRegionHandle)
        {
            System.Lazy<List<Task>> crossingUsers = new System.Lazy<List<Task>>();

            ForEachSittingAvatar((ScenePresence sp) =>
            {
                PositionInfo posInfo = sp.GetPosInfo();
                if (posInfo != null)
                    crossingUsers.Value.Add(sp.CrossIntoNewRegionWithGroup(this, posInfo.Parent, newRegionHandle));
            });

            if (crossingUsers.IsValueCreated)
            {
                await Task.WhenAll(crossingUsers.Value.ToArray());
            }
        }

        /// <summary>
        /// Kludge to get around opensims IsSelected setting physics selection count to -1 on copy
        /// </summary>
        internal void SetUnselectedForCopy()
        {
            m_isSelected = false;
        }

        public void BeginRotLookAt(Quaternion target, float strength, float damping)
        {
            PhysicsActor physActor = RootPart.PhysActor;
            if (physActor != null)
            {
                physActor.SetRotLookAtTarget(target, strength, damping);
            }
        }

        public void StopRotLookAt()
        {
            PhysicsActor physActor = RootPart.PhysActor;
            if (physActor != null)
            {
                physActor.StopRotLookAt();
            }
        }

        public void SetAngularVelocity(Vector3 force, bool local)
        {
            PhysicsActor physActor = RootPart.PhysActor;
            if (physActor != null)
            {
                physActor.SetAngularVelocity(force, local);
            }
        }

        public void SetVelocity(Vector3 force, bool local)
        {
            PhysicsActor physActor = RootPart.PhysActor;
            if (physActor != null)
            {
                physActor.SetVelocity(force, local);
            }
        }

        /// <summary>
        /// For each of the items, if i am the owner, permissions don't change, otherwise apply next owner perms
        /// </summary>
        /// <param name="inventoryOwner"></param>
        /// <param name="sceneItem"></param>
        /// <param name="newItem"></param>
        public ItemPermissionBlock GetNewItemPermissions(UUID inventoryOwner)
        {
            ItemPermissionBlock newItem = new ItemPermissionBlock();
            uint eperms = this.GetEffectivePermissions(true);
            uint nperms = this.GetEffectiveNextPermissions(true);

            // item perms become folded, but ignored if rezzed by the same owner
            if (inventoryOwner != this.OwnerID && this.Scene != null && this.Scene.Permissions.PropagatePermissions())
            {
                newItem.BasePermissions = eperms & this.RootPart.NextOwnerMask;
                newItem.CurrentPermissions = eperms & nperms;
                newItem.NextPermissions = eperms & nperms;
                newItem.EveryOnePermissions = 0;
                newItem.GroupPermissions = 0;
            }
            else
            {
                newItem.BasePermissions = eperms;
                newItem.CurrentPermissions = eperms;
                newItem.NextPermissions = eperms & nperms;
                newItem.EveryOnePermissions = eperms & this.RootPart.EveryoneMask;
                newItem.GroupPermissions = eperms & this.RootPart.GroupMask;
            }

            newItem.ItemId = this.RootPart.UUID;

            return newItem;
        }

        public void AddKeyframedMotion(KeyframeAnimation animation, KeyframeAnimation.Commands command)
        {
            if (command == KeyframeAnimation.Commands.Play)
            {
                if (animation != null)//This will happen if just the play command is being set
                    m_rootPart.KeyframeAnimation = animation;
                //Only have one at a time
                m_scene.EventManager.OnFrame -= moveKeyframeMotion;
                m_scene.EventManager.OnFrame += moveKeyframeMotion;
            }
            else
            {
                m_scene.EventManager.OnFrame -= moveKeyframeMotion;
                if(m_rootPart.KeyframeAnimation != null)
                {
                    m_rootPart.KeyframeAnimation.TimeLastTick = 0;
                    if (command == KeyframeAnimation.Commands.Stop)
                    {
                        //Stop will reset the transformations while Pause does not
                        m_rootPart.KeyframeAnimation.CurrentAnimationPosition = 0;
                        //Reset the time elapsed too so that we don't start in the middle of a move
                        m_rootPart.KeyframeAnimation.TimeElapsed = 0;
                    }
                }
            }
            if (m_rootPart.KeyframeAnimation != null)
                m_rootPart.KeyframeAnimation.CurrentCommand = command;
            HasGroupChanged = true;
        }

        public void moveKeyframeMotion()
        {
            try
            {
                if (m_scene.Frame % 2 != 0)
                    return;//Limit it to only running every two frames so that we don't send far too many updates

                if (IsDeleted || m_rootPart.KeyframeAnimation == null || m_rootPart.KeyframeAnimation.TimeList.Length == 0)
                {
                    //Remove it, as we don't have anything to do anymore
                    m_scene.EventManager.OnFrame -= moveKeyframeMotion;
                    return;
                }
                if (InTransit)
                    return;

                if (m_rootPart.KeyframeAnimation.TimeLastTick == 0)
                    m_rootPart.KeyframeAnimation.TimeLastTick = Environment.TickCount;

                // Range check added to avoid index out-of-range errors seen on Utopia Skye (and IB5, see R4198)
                int animLen = m_rootPart.KeyframeAnimation.TimeList.Length;
                if (m_rootPart.KeyframeAnimation.CurrentAnimationPosition >= animLen)
                {
                    m_log.ErrorFormat("[KeyframeAnimation]: Invalid current anim position {0} of {1} for '{2}' at {3}",
                            m_rootPart.KeyframeAnimation.CurrentAnimationPosition, animLen, m_rootPart.Name, m_rootPart.AbsolutePosition);
                    m_rootPart.KeyframeAnimation.CurrentAnimationPosition = 0;    // repair by starting over since it wrapped anyway
                    if (animLen < 1)
                        return;
                }

                TimeSpan timeSpanForCurrentAnimation = m_rootPart.KeyframeAnimation.TimeList
                    [m_rootPart.KeyframeAnimation.CurrentAnimationPosition];

                Vector3 positionTarget = m_rootPart.KeyframeAnimation.PositionList == null
                                                ? Vector3.Zero
                                                : m_rootPart.KeyframeAnimation.PositionList[
                                                    m_rootPart.KeyframeAnimation.CurrentAnimationPosition];
                Quaternion rotationTarget = m_rootPart.KeyframeAnimation.RotationList == null
                                        ? Quaternion.Identity
                                        : m_rootPart.KeyframeAnimation.RotationList[
                                            m_rootPart.KeyframeAnimation.CurrentAnimationPosition];

                //Add to time elapsed and figure out the progress of the move
                int timeSinceEpoch = Environment.TickCount;
                int timeSinceLastTick = timeSinceEpoch - m_rootPart.KeyframeAnimation.TimeLastTick;
                m_rootPart.KeyframeAnimation.TimeElapsed += timeSinceLastTick;
                m_rootPart.KeyframeAnimation.TimeLastTick = timeSinceEpoch;
                float progress = Util.Clip(((float)m_rootPart.KeyframeAnimation.TimeElapsed) /
                    ((float)timeSpanForCurrentAnimation.TotalMilliseconds), 0f, 1f);

                bool AllDoneMoving = false;
                bool MadeItToCheckpoint = false;

                bool isInReverse = m_rootPart.KeyframeAnimation.CurrentMode == KeyframeAnimation.Modes.Reverse;
                if (m_rootPart.KeyframeAnimation.CurrentMode == KeyframeAnimation.Modes.PingPong &&
                    !m_rootPart.KeyframeAnimation.PingPongForwardMotion)
                    isInReverse = true;

                //Determine whether we need to switch to the next checkpoint in the list
                if (m_rootPart.KeyframeAnimation.TimeElapsed > timeSpanForCurrentAnimation.TotalMilliseconds)
                {
                    //The current move has finished, determine which transformation will need to be applied next
                    if (m_rootPart.KeyframeAnimation.CurrentMode == KeyframeAnimation.Modes.Forward)
                    {
                        m_rootPart.KeyframeAnimation.CurrentAnimationPosition += 1;
                        if (m_rootPart.KeyframeAnimation.CurrentAnimationPosition ==
                            m_rootPart.KeyframeAnimation.TimeList.Length)
                        {
                            //All done moving...
                            AllDoneMoving = true;
                            m_scene.EventManager.OnFrame -= moveKeyframeMotion;
                        }
                    }
                    else if (m_rootPart.KeyframeAnimation.CurrentMode == KeyframeAnimation.Modes.Reverse)
                    {
                        m_rootPart.KeyframeAnimation.CurrentAnimationPosition -= 1;
                        if (m_rootPart.KeyframeAnimation.CurrentAnimationPosition < 0)
                        {
                            //All done moving...
                            AllDoneMoving = true;
                            m_scene.EventManager.OnFrame -= moveKeyframeMotion;
                        }
                    }
                    else if (m_rootPart.KeyframeAnimation.CurrentMode == KeyframeAnimation.Modes.Loop)
                    {
                        m_rootPart.KeyframeAnimation.CurrentAnimationPosition += 1;
                        if (m_rootPart.KeyframeAnimation.CurrentAnimationPosition ==
                            m_rootPart.KeyframeAnimation.TimeList.Length)
                            m_rootPart.KeyframeAnimation.CurrentAnimationPosition = 0;
                    }
                    else if (m_rootPart.KeyframeAnimation.CurrentMode == KeyframeAnimation.Modes.PingPong)
                    {
                        if (m_rootPart.KeyframeAnimation.PingPongForwardMotion)
                        {
                            m_rootPart.KeyframeAnimation.CurrentAnimationPosition += 1;
                            if (m_rootPart.KeyframeAnimation.CurrentAnimationPosition ==
                                m_rootPart.KeyframeAnimation.TimeList.Length)
                            {
                                m_rootPart.KeyframeAnimation.PingPongForwardMotion =
                                    !m_rootPart.KeyframeAnimation.PingPongForwardMotion;
                                m_rootPart.KeyframeAnimation.CurrentAnimationPosition = m_rootPart.KeyframeAnimation.TimeList.Length - 1;
                            }
                        }
                        else
                        {
                            m_rootPart.KeyframeAnimation.CurrentAnimationPosition -= 1;
                            if (m_rootPart.KeyframeAnimation.CurrentAnimationPosition < 0)
                            {
                                m_rootPart.KeyframeAnimation.PingPongForwardMotion =
                                    !m_rootPart.KeyframeAnimation.PingPongForwardMotion;
                            m_rootPart.KeyframeAnimation.CurrentAnimationPosition  = 0;
                            }
                        }
                    }
                    m_rootPart.KeyframeAnimation.TimeLastTick = Environment.TickCount;
                    m_rootPart.KeyframeAnimation.TimeElapsed = 0;
                    MadeItToCheckpoint = true;
                }


                if(isInReverse)
                {
                    positionTarget *= -1;
                    rotationTarget = Quaternion.Inverse(rotationTarget);
                }

                //Now move and/or rotate the prim to the next position/angle
                if (m_rootPart.KeyframeAnimation.PositionList != null && positionTarget != Vector3.Zero)
                {
                    Vector3 _target_velocity = Vector3.Lerp(Vector3.Zero, positionTarget, progress);
                    if (MadeItToCheckpoint)
                    {
                        if (m_rootPart.PhysActor != null)
                        {
                            if (AllDoneMoving)//If we're all done, make sure to kill the velocity so that it doesn't continue moving at all in the client
                                m_rootPart.PhysActor.SetVelocity(Vector3.Zero, false);
                        }
                        SetAbsolutePosition(m_rootPart.KeyframeAnimation.InitialPosition + positionTarget, false);
                        m_rootPart.KeyframeAnimation.InitialPosition = m_rootPart.KeyframeAnimation.InitialPosition + positionTarget;
                    }
                    else
                    {
                        if(m_rootPart.PhysActor != null)
                        {
                            m_rootPart.PhysActor.SetVelocity(((m_rootPart.KeyframeAnimation.InitialPosition + _target_velocity) - AbsolutePosition) * -1f, false);
                        }
                        if (!_target_velocity.ApproxEquals(Vector3.Zero, 0.001f))//Only send the update if we are actually moving
                            SetAbsolutePosition(m_rootPart.KeyframeAnimation.InitialPosition + _target_velocity, false);

                        //If it is phantom, then an update never gets sent out for some reason
                        // even though SetAbsolutePosition is called. We also don't want to overdo
                        // this, so we only do it once every 12 frames to not overload the sim
                        if (m_rootPart.PhysActor == null && m_scene.Frame % 12 != 0)
                            RootPart.ScheduleTerseUpdate();
                    }
                }

                if (m_rootPart.KeyframeAnimation.RotationList != null && rotationTarget != Quaternion.Identity)
                {
                    rotationTarget = m_rootPart.KeyframeAnimation.InitialRotation * rotationTarget;
                    if (MadeItToCheckpoint)
                    {
                        //Force set it to the right position, just to be sure
                        m_rootPart.UpdateRotation(rotationTarget);
                        m_rootPart.KeyframeAnimation.InitialRotation = rotationTarget;
                    }
                    else
                    {
                        Quaternion newInterpolation = Quaternion.Slerp(m_rootPart.KeyframeAnimation.InitialRotation, rotationTarget, progress);
                        newInterpolation.Normalize();
                        m_rootPart.UpdateRotation(newInterpolation);
                    }
                }
            }
            catch (Exception e)
            {
                m_scene.EventManager.OnFrame -= moveKeyframeMotion;
                //If we're all done, make sure to kill the velocity so that it doesn't continue moving at all in the client
                if (m_rootPart.PhysActor != null)
                {
                    m_rootPart.PhysActor.SetVelocity(Vector3.Zero, false);
                }
                m_log.ErrorFormat("[KeyframeAnimation]: Exception animating object '{0}' at {1}:", m_rootPart.Name, m_rootPart.AbsolutePosition);
                m_log.Error("[KeyframeAnimation]: Error: " + e.Message);
            }
        }

        private const int MAX_FAILURES_BEFORE_RETURN = 100;
        private int _numCrossingFailures = 0;
        
        /// <summary>
        /// Adds 1 to the crossing failure count for this group. If we get to 
        /// MAX_FAILURES_BEFORE_RETURN attempts, we return the object
        /// </summary>
        internal void CrossingFailure()
        {
            if (++_numCrossingFailures == MAX_FAILURES_BEFORE_RETURN)
            {
                m_scene.returnObjects(new SceneObjectGroup[1] { this }, "object crossing failed too many times");
            }
        }

        public void ForceAboveParcel(float height)
        {
            if (!IsDeleted)
            {
                Vector3 pos = AbsolutePosition;
                pos.Z = height;
                AbsolutePosition = pos; // AbsolutePosition setter takes care of all cases
            }
        }

        /// <summary>
        /// Prepares an attachment for rezzing by setting the appropriate attachment points
        /// as well as the group position and restoring appropriate parameters
        /// </summary>
        /// <param name="AttachmentPt">The attachment point the user has requested this object on</param>
        /// <param name="shouldTaint">Whether the object needs to be marked as tainted needing persistence (if the attachpoint changed)</param>
        /// <returns></returns>
        public bool PrepareForRezAsAttachment(uint AttachmentPt, out bool shouldTaint, bool fromCrossing)
        {
            byte restored = this.GetBestAttachmentPoint();
            Vector3 attachPos = this.RawGroupPosition;
            Quaternion attachRot = this.RootPart.RotationOffset;   // default to the current value                         // save the one asked for cuz we're gonna change it

            // AttachmentPt 0 means the client chose to 'wear' the attachment. 
            // Check object for stored attachment point, and wear there if set
            if (AttachmentPt == 0)
                AttachmentPt = restored;    // wear on previous att point

            if (this.RootPart.IsLindenPlant())
            {
                m_log.WarnFormat("[SCENE]: Refusing attempt to wear Linden plant '{0}' owned by {1}.", this.Name, this.OwnerID);
                shouldTaint = false;
                return false;
            }

            if (AttachmentPt > SceneObjectPart.MAX_ATTACHMENT)
            {
                m_log.WarnFormat("[SCENE] Invalid attachment point ({0}) for '{1}'.", AttachmentPt, this.Name);
                shouldTaint = false;
                return false;
            }

            // If the attachment point isn't the same as the one previously used
            // set it's offset position = 0 so that it appears on the attachment point
            // and not in a weird location somewhere unknown.
            if ((AttachmentPt != 0) && (AttachmentPt != restored))
            {
                // New attachment point, clear attachment params.
                attachPos = Vector3.Zero;
                attachRot = Quaternion.Identity;
                this.RootPart.SavedAttachmentPos = attachPos;
                this.RootPart.SavedAttachmentRot = attachRot;
            }
            else
            {
                // Restore what we can for attachment parameters
                if (this.RootPart.SavedAttachmentPos != Vector3.Zero)
                    attachPos = this.RootPart.SavedAttachmentPos;
                if (this.RootPart.SavedAttachmentRot != Quaternion.Identity)
                    attachRot = this.RootPart.SavedAttachmentRot;
            }

            // Enforce a max distance of 10m before we consider the position to be invalid.
            // See http://wiki.secondlife.com/wiki/LlSetPos for more.
            //                  if (Math.Sqrt(attachPos.X * attachPos.X + attachPos.Y * attachPos.Y + attachPos.Z * attachPos.Z) > 10.0)
            if ((attachPos.X * attachPos.X + attachPos.Y * attachPos.Y + attachPos.Z * attachPos.Z) > 100.0) // 10 squared
            {
                attachPos = Vector3.Zero;
            }

            // if we still didn't find a suitable attachment point.......
            if (AttachmentPt == 0)
            {
                // Stick it on left hand with Zero Offset from the attachment point.
                AttachmentPt = (uint)OpenMetaverse.AttachmentPoint.LeftHand;
                attachPos = Vector3.Zero;
                attachRot = Quaternion.Identity;
            }

            if (AttachmentPt != 0 && AttachmentPt != restored)
            {
                shouldTaint = true;
            }
            else
            {
                shouldTaint = false;
            }

            //also check if the force taint is set from another region
            if (fromCrossing && TaintedAttachment)
            {
                shouldTaint = true;
            }

            //always clear the TaintedAttachment status on a rez
            TaintedAttachment = false;

            this.SetAttachmentPoint(Convert.ToByte(AttachmentPt));

            this.AbsolutePosition = attachPos;
            this.RootPart.RotationOffset = attachRot;  // group.Rotation seems to be rarely used, only undo system?
            this.RootPart.SavedAttachmentPos = attachPos;
            this.RootPart.SavedAttachmentRot = attachRot;
            this.RootPart.AttachedPos = attachPos;

            return true;
        }

        public bool IsScripted
        {
            get { return m_isScripted; }
            set { m_isScripted = value; }
        }

        /// <summary>
        /// Checks if this group or any of its children are scripted and sets 
        /// a flag appropriately if so
        /// </summary>
        public void RecalcScriptedStatus()
        {
            bool scripted = false;
            foreach (SceneObjectPart part in  m_childParts.GetAllParts())
            {
                if (part == null)
                    continue;
                if ((part.GetEffectiveObjectFlags() & PrimFlags.Scripted) != 0)
                {
                    scripted = true;
                    break;
                }
            }
            IsScripted = scripted;
        }

        
    }
}
