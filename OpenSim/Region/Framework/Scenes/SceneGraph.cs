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
using System.Reflection;
using OpenMetaverse;
using OpenMetaverse.Packets;
using log4net;
using OpenSim.Framework;
using OpenSim.Framework.Communications.Cache;
using OpenSim.Region.Framework.Scenes.Types;
using OpenSim.Region.Physics.Manager;
using System.Threading;

namespace OpenSim.Region.Framework.Scenes
{
    public delegate void PhysicsCrash();

    public delegate void ObjectDuplicateDelegate(EntityBase original, EntityBase clone);

    public delegate void ObjectCreateDelegate(EntityBase obj);

    public delegate void ObjectDeleteDelegate(EntityBase obj);

    /// <summary>
    /// This class used to be called InnerScene and may not yet truly be a SceneGraph.  The non scene graph components
    /// should be migrated out over time.
    /// </summary>
    public class SceneGraph
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        #region Events

        protected internal event PhysicsCrash UnRecoverableError;
        private PhysicsCrash handlerPhysicsCrash = null;

        public event ObjectDuplicateDelegate OnObjectDuplicate;
        public event ObjectCreateDelegate OnObjectCreate;
        public event ObjectDeleteDelegate OnObjectRemove;

        #endregion

        #region Fields

        protected internal Dictionary<UUID, ScenePresence> ScenePresences = new Dictionary<UUID, ScenePresence>();

        protected internal EntityManager Entities = new EntityManager();

        protected internal Dictionary<UUID, ScenePresence> RestorePresences = new Dictionary<UUID, ScenePresence>();

        protected internal BasicQuadTreeNode QuadTree;

        protected RegionInfo m_regInfo;
        protected Scene m_parentScene;

        private struct UpdateInfo
        {
            public SceneObjectGroup Group;
            public SceneObjectPart Part;
            public SceneObjectPart.UpdateLevel UpdateLevel;
            public PrimUpdateFlags UpdateFlags;
        }

        private Dictionary<uint, UpdateInfo> m_updateList = new Dictionary<uint, UpdateInfo>();
        protected int m_numRootAgents = 0;
        protected int m_numPrim = 0;
        protected int m_numChildAgents = 0;
        protected int m_physicalPrim = 0;

        protected int m_activeScripts = 0;
        protected int m_scriptLPS = 0;

        protected internal object m_syncRoot = new object();

        protected internal PhysicsScene _PhyScene;

        //        private Dictionary<uint, SceneObjectGroup> SceneObjectGroupsByLocalID = new Dictionary<uint, SceneObjectGroup>();
        //        private Dictionary<UUID, SceneObjectGroup> SceneObjectGroupsByFullID = new Dictionary<UUID, SceneObjectGroup>();
        private Dictionary<UUID, SceneObjectPart> SceneObjectPartsByFullID = new Dictionary<UUID, SceneObjectPart>();
        private Dictionary<uint, SceneObjectPart> SceneObjectPartsByLocalID = new Dictionary<uint, SceneObjectPart>();

        private readonly Object m_dictionary_lock = new Object();

        private SceneTransactionManager _transactionMgr;
        #endregion

        protected internal SceneGraph(Scene parent, RegionInfo regInfo)
        {
            _transactionMgr = new SceneTransactionManager(this);
            m_parentScene = parent;
            m_regInfo = regInfo;
            QuadTree = new BasicQuadTreeNode(null, "/0/", 0, 0, (short)Constants.RegionSize, (short)Constants.RegionSize);
            QuadTree.Subdivide();
            QuadTree.Subdivide();
        }

        public PhysicsScene PhysicsScene
        {
            get { return _PhyScene; }
            set
            {
                // If we're not doing the initial set
                // Then we've got to remove the previous
                // event handler

                if (_PhyScene != null)
                    _PhyScene.OnPhysicsCrash -= physicsBasedCrash;

                _PhyScene = value;

                if (_PhyScene != null)
                    _PhyScene.OnPhysicsCrash += physicsBasedCrash;
            }
        }

        protected internal void Close()
        {
            lock (ScenePresences)
            {
                ScenePresences.Clear();
            }
            lock (m_dictionary_lock)
            {
                SceneObjectPartsByFullID.Clear();
                SceneObjectPartsByLocalID.Clear();
                Entities.Clear();
            }
        }

        #region Update Methods

        protected internal void UpdatePreparePhysics()
        {
            // If we are using a threaded physics engine
            // grab the latest scene from the engine before
            // trying to process it.

            // PhysX does this (runs in the background).

            if (_PhyScene.IsThreaded)
            {
                _PhyScene.GetResults();
            }
        }

        protected internal void UpdatePresences()
        {
            List<ScenePresence> updateScenePresences = GetScenePresences();
            foreach (ScenePresence pres in updateScenePresences)
            {
                if (pres.IsDeleted || pres.IsInTransit)
                    continue;
                pres.Update();
                pres.UpdateMovement();
            }
        }

        #endregion

        #region Entity Methods

        /// <summary>
        /// Add an object into the scene that has come from __storage__
        /// </summary>
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
        protected internal bool AddRestoredSceneObject(
            SceneObjectGroup sceneObject, bool attachToBackup, bool alreadyPersisted)
        {
            if (!alreadyPersisted)
            {
                sceneObject.ForceInventoryPersistence();
                sceneObject.HasGroupChanged = true;
            }

            return AddGroupToSceneGraph(sceneObject, attachToBackup, alreadyPersisted, true, false);
        }

        /// <summary>
        /// Add a newly created object to the scene.  This will both update the scene, and send information about the
        /// new object to all clients interested in the scene.
        /// </summary>
        /// <param name="sceneObject"></param>
        /// <param name="attachToBackup">
        /// If true, the object is made persistent into the scene.
        /// If false, the object will not persist over server restarts
        /// </param>
        /// <returns>
        /// true if the object was added, false if an object with the same uuid was already in the scene
        /// </returns>
        protected internal bool AddNewSceneObject(SceneObjectGroup sceneObject, bool attachToBackup)
        {
            // Ensure that we persist this new scene object
            sceneObject.HasGroupChanged = true;

            return AddGroupToSceneGraph(sceneObject, attachToBackup, false, false, false);
        }

        /// <summary>
        /// Add an object to the scene.  This will both update the scene, and send information about the
        /// new object to all clients interested in the scene.
        /// </summary>
        /// <param name="sceneObject"></param>
        /// <param name="attachToBackup">
        /// If true, the object is made persistent into the scene.
        /// If false, the object will not persist over server restarts
        /// </param>
        /// <returns>true if the object was added, false if an object with the same uuid was already in the scene
        /// </returns>
        public bool AddGroupToSceneGraph(SceneObjectGroup sceneObject, bool attachToBackup, bool alreadyPersisted, bool fromStorage,
            bool fromCrossing)
        {
            if (sceneObject == null || sceneObject.RootPart == null || sceneObject.RootPart.UUID == UUID.Zero)
            {
                m_log.ErrorFormat("[SceneGraph]: {0} AddGroupToSceneGraph failed, invalid or null object.", m_parentScene.RegionInfo.RegionName);
                return false;
            }

            // Prime (cache) the owner's group list.
            m_parentScene.UserGroupsGet(sceneObject.OwnerID);

            foreach (SceneObjectPart part in sceneObject.GetParts())
            {
                Vector3 scale = part.Shape.Scale;
                if (scale.X < SceneObjectPart.MIN_PART_SCALE)
                {
                    scale.X = SceneObjectPart.MIN_PART_SCALE;
                    sceneObject.HasGroupChanged = true;
                }
                if (scale.Y < SceneObjectPart.MIN_PART_SCALE)
                {
                    scale.Y = SceneObjectPart.MIN_PART_SCALE;
                    sceneObject.HasGroupChanged = true;
                }
                if (scale.Z < SceneObjectPart.MIN_PART_SCALE)
                {
                    scale.Z = SceneObjectPart.MIN_PART_SCALE;
                    sceneObject.HasGroupChanged = true;
                }

                if (m_parentScene.m_clampPrimSize)
                {
                    if (scale.X > m_parentScene.m_maxNonphys)
                        scale.X = m_parentScene.m_maxNonphys;
                    if (scale.Y > m_parentScene.m_maxNonphys)
                        scale.Y = m_parentScene.m_maxNonphys;
                    if (scale.Z > m_parentScene.m_maxNonphys)
                        scale.Z = m_parentScene.m_maxNonphys;
                }
                part.Shape.Scale = scale;
            }

            // if physical, make sure we're above the ground by at least 10 cm
            if ((sceneObject.RootPart.Flags & PrimFlags.Physics) != 0)
            {
                OpenSim.Framework.Geom.Box bbox = sceneObject.BoundingBox();
                float groundHeight = m_parentScene.Heightmap.CalculateHeightAt(sceneObject.AbsolutePosition.X, sceneObject.AbsolutePosition.Y);
                if (sceneObject.AbsolutePosition.Z + bbox.Size.Z <= groundHeight)
                {
                    Vector3 newPos = sceneObject.AbsolutePosition;
                    newPos.Z = (bbox.Size.Z / 2.0f) + groundHeight + 0.1f;

                    sceneObject.RootPart.SetGroupPositionDirect(newPos);
                }

                AddPhysicalObject();
            }

            // Taint the map tile if qualifying.
            m_parentScene.MarkMapTileTainted(sceneObject.RootPart);

            // rationalize the group: fix any inconsistency in locked bits, and 
            // clear the default touch action of BUY for any objects not actually for sale, etc
            sceneObject.Rationalize(sceneObject.OwnerID, fromCrossing);

            sceneObject.AttachToScene(m_parentScene, fromStorage); // allocates the localIds for the prims
            if (!alreadyPersisted)
            {
                sceneObject.HasGroupChanged = true;
            }

            sceneObject.IsPersisted = alreadyPersisted;

            lock (sceneObject)
            {
                bool entityAdded = false;
                lock (m_dictionary_lock)
                {
                    if (!Entities.ContainsKey(sceneObject.LocalId))
                    {
                        Entities.Add(sceneObject);
                        foreach (SceneObjectPart part in sceneObject.GetParts())
                        {
                            SceneObjectPartsByFullID[part.UUID] = part;
                            SceneObjectPartsByLocalID[part.LocalId] = part;
                        }

                        entityAdded = true;
                    }
                }

                if (entityAdded)
                {
                    m_numPrim += sceneObject.PartCount;
                    m_parentScene.EventManager.TriggerParcelPrimCountTainted();

                    if (attachToBackup)
                    {
                        sceneObject.AttachToBackup();
                        //this gets the new object on the taint list
                        if (!alreadyPersisted)
                        {
                            sceneObject.HasGroupChanged = true;
                        }

                        m_parentScene.InspectForAutoReturn(sceneObject);
                    }

                    //This property doesn't work on rezzing, only on crossings or restarts
                    if ((fromStorage || fromCrossing) && sceneObject.RootPart.KeyframeAnimation != null)
                    {
                        if (sceneObject.RootPart.KeyframeAnimation.CurrentCommand == KeyframeAnimation.Commands.Play)
                        {
                            if (!fromStorage)
                            {
                                //Offset the initial position so that it is in the new region's coordinates
                                // (only if we are actually crossing from a different region)
                                Vector3 regionOffset = sceneObject.AbsolutePosition - sceneObject.OriginalEnteringScenePosition;
                                sceneObject.RootPart.KeyframeAnimation.InitialPosition += regionOffset;
                            }

                            sceneObject.AddKeyframedMotion(null, KeyframeAnimation.Commands.Play);
                        }
                    }

                    if (OnObjectCreate != null)
                        OnObjectCreate(sceneObject);

                    return true;
                }

            }

            return false;
        }

        /// <summary>
        /// Delete an object from the scene.
        /// </summary>
        /// <returns>true if the object was deleted, false if there was no object to delete</returns>
        public bool RemoveGroupFromSceneGraph(uint localId, bool resultOfObjectLinked, bool fromCrossing)
        {
            lock (m_dictionary_lock)
            {
                if (Entities.ContainsKey(localId))
                {
                    SceneObjectGroup group = (SceneObjectGroup)Entities[localId];

                    if (!resultOfObjectLinked)
                    {
                        m_numPrim -= group.PartCount;

                        if ((group.RootPart.Flags & PrimFlags.Physics) == PrimFlags.Physics)
                        {
                            RemovePhysicalObject();
                        }
                    }

                    if (OnObjectRemove != null)
                        OnObjectRemove(group);

                    if (!resultOfObjectLinked)
                    {
                        foreach (SceneObjectPart part in group.GetParts())
                        {
                            // Taint the map tile if qualifying.  Note that physical objects are being ignored anyway.
                            m_parentScene.MarkMapTileTainted(part);

                            if (!SceneObjectPartsByLocalID.Remove(part.LocalId))
                            {
                                m_log.ErrorFormat("[SceneGraph]: DeleteSceneObject: Unable to find part ID {0} of parent {1} in SceneObjectPartsByLocalID", part.LocalId, group.LocalId);
                            }
                            if (!SceneObjectPartsByFullID.Remove(part.UUID))
                            {
                                m_log.ErrorFormat("[SceneGraph]: DeleteSceneObject: Unable to find part {0} of parent {1} in SceneObjectPartsByFullID", part.UUID, group.UUID);
                            }
                        }
                    }


                    Entities.Remove(localId);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Add an entity to the list of prims to process on the next update
        /// </summary>
        /// <param name="obj">
        /// A <see cref="EntityBase"/>
        /// </param>
        protected internal void AddToUpdateList(SceneObjectGroup group, SceneObjectPart part, SceneObjectPart.UpdateLevel level, PrimUpdateFlags updateFlags)
        {
            lock (m_updateList)
            {
                UpdateInfo updInfo;
                if (!m_updateList.TryGetValue(part.LocalId, out updInfo))
                {
                    m_updateList[part.LocalId] = new UpdateInfo { Group = group, Part = part, UpdateLevel = level, UpdateFlags = updateFlags };
                }
                else if(updInfo.UpdateLevel <= level || updInfo.Group != group)
                {
                    UpdateInfo oldInfo = m_updateList[part.LocalId];
                    m_updateList[part.LocalId] = new UpdateInfo { Group = group, Part = part, UpdateLevel = level, UpdateFlags = updateFlags | oldInfo.UpdateFlags };
                }
            }

            m_parentScene.MarkMapTileTainted(OpenSim.Region.Framework.Interfaces.WorldMapTaintReason.PrimChange);
        }

        protected internal void RemoveFromUpdateList(SceneObjectGroup obj)
        {
            //done here so that it happens outside the lock
            var objParts = obj.GetParts();

            lock (m_updateList)
            {
                foreach (var part in objParts)
                {
                    m_updateList.Remove(part.LocalId);
                }
            }
        }

        // Handling for scripted targets, via llTarget
        protected Dictionary<int, SceneObjectGroup> mGroupsWithTargets = new Dictionary<int, SceneObjectGroup>();
        public void RegisterObjectWithTargets(int handle, SceneObjectGroup group)
        {
            lock (mGroupsWithTargets)
            {
                mGroupsWithTargets[handle] = group;
            }
        }

        public void UnregisterObjectWithTargets(int handle)
        {
            lock (mGroupsWithTargets)
            {
                mGroupsWithTargets.Remove(handle);
            }
        }

        // Called every sixth frame (~100ms)
        protected internal void ProcessTargets()
        {
            List<SceneObjectGroup> groups;

            lock (mGroupsWithTargets)
            {
                groups = new List<SceneObjectGroup>(mGroupsWithTargets.Values);
            }
            foreach (SceneObjectGroup group in groups)
            {
                group.CheckAtTargets();
            }
        }

        /// <summary>
        /// Process all pending updates
        /// </summary>
        protected internal void ProcessUpdates()
        {
            List<UpdateInfo> rootUpdates;
            List<UpdateInfo> childUpdates;

            // Some updates add more updates to the updateList. 
            // Get the current list of updates and clear the list before iterating
            // always process the roots first to ensure 
            lock (m_updateList)
            {
                if (m_updateList.Count == 0) return;

                rootUpdates = new List<UpdateInfo>();
                childUpdates = new List<UpdateInfo>();

                foreach (UpdateInfo update in m_updateList.Values)
                {
                    if (update.Part.IsRootPart())
                    {
                        rootUpdates.Add(update);
                    }
                    else
                    {
                        childUpdates.Add(update);
                    }
                }

                m_updateList.Clear();
            }

            //always do root updates first
            ProcessAndSendUpdates(rootUpdates);
            ProcessAndSendUpdates(childUpdates);
        }

        private static void ProcessAndSendUpdates(List<UpdateInfo> updateList)
        {
            foreach (var update in updateList)
            {
                // Check that the group was not deleted before the scheduled update
                if (update.Group.IsDeleted)
                {
                    //this is too common to send out under normal circumstances
                    //m_log.ErrorFormat("[SceneObjectGroup]: Update skipped for prim w/deleted group {0} {1}", update.Part.LocalId, update.Part.UUID);
                    continue;
                }

                // This is what happens when an orphanced link set child prim's
                // group was queued when it was linked
                //
                if (update.Group.RootPart == null)
                {
                    m_log.ErrorFormat("[SceneObjectGroup]: Update skipped for group with null root prim {0} {1}", update.Group.LocalId, update.Group.UUID);
                    continue;
                }

                //also check the sanity of the update. if this prim is no longer part of the
                //given group, this update doesnt make sense
                if ((!update.Group.HasChildPrim(update.Part.LocalId)) ||
                    (update.Part.ParentGroup != update.Group))
                {
                    m_log.ErrorFormat("[SceneObjectGroup]: Update skipped for mismatched child to parent. Grp:{0} Prim:{1}", update.Group.UUID, update.Part.UUID);
                    continue;
                }

                try
                {
                    update.Part.SendScheduledUpdates(update.UpdateLevel, update.UpdateFlags);
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat(
                        "[INNER SCENE]: Failed to update {0}, {1} {2} - {3}", update.Part.Name, update.Part.UUID, update.Part.LocalId, e);
                }
            }
        }

        protected internal void AddPhysicalObject()
        {
            Interlocked.Increment(ref m_physicalPrim);
        }

        protected internal void RemovePhysicalObject()
        {
            Interlocked.Decrement(ref m_physicalPrim);
        }

        protected internal void AddToScriptLPS(int number)
        {
            Interlocked.Add(ref m_scriptLPS, number);
        }

        protected internal void AddActiveScripts(int number)
        {
            Interlocked.Add(ref m_activeScripts, number);
        }

        public void DropObject(uint objectLocalID, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(objectLocalID);
            if (group != null)
            {
                if (group.OwnerID == remoteClient.AgentId)
                {
                    if (group.IsAttachment && !group.IsTempAttachment)
                    {
                        m_parentScene.DetachSingleAttachmentToGround(group.UUID, remoteClient);

                        // Taint the map tile if qualifying.  Note that physical objects are being ignored anyway.
                        foreach (SceneObjectPart part in group.GetParts())
                        {
                            m_parentScene.MarkMapTileTainted(part);
                        }
                    }
                }
            }
        }

        protected internal void DetachObject(uint objectLocalID, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(objectLocalID);
            if (group != null)
            {
                if (group.OwnerID == remoteClient.AgentId)
                {
                    m_parentScene.DetachSingleAttachmentToInv(group.GetFromItemID(), remoteClient);
                }
            }
        }

        protected internal void HandleUndo(IClientAPI remoteClient, UUID primId)
        {
            if (primId != UUID.Zero)
            {
                SceneObjectPart part = m_parentScene.GetSceneObjectPart(primId);
                if (part != null)
                {
                    if (m_parentScene.Permissions.CanEditObject(part.ParentGroup.RootPart.UUID, remoteClient.AgentId, 0))
                    {
                        part.Undo();
                    }
                }
            }
        }

        protected internal void HandleRedo(IClientAPI remoteClient, UUID primId)
        {
            if (primId != UUID.Zero)
            {
                SceneObjectPart part = m_parentScene.GetSceneObjectPart(primId);
                if (part != null)
                {
                    if (m_parentScene.Permissions.CanEditObject(part.ParentGroup.RootPart.UUID, remoteClient.AgentId, 0))
                    {
                        part.Redo();
                    }
                }
            }
        }

        protected internal void HandleObjectGroupUpdate(
            IClientAPI remoteClient, UUID GroupID, uint objectLocalID, UUID Garbage)
        {
            SceneObjectGroup group = GetGroupByPrim(objectLocalID);
            if (group != null)
            {
                if (group.OwnerID == remoteClient.AgentId)
                {
                    UUID oldId = group.GroupID;
                    group.SetGroup(GroupID, remoteClient);
                    m_parentScene.InspectForAutoReturn(group);

                    m_parentScene.EventManager.TriggerOnSOGOwnerGroupChanged(group, oldId, GroupID);
                }
            }
        }

        /// <summary>
        /// Event Handling routine for Attach Object
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="objectLocalID"></param>
        /// <param name="AttachmentPt"></param>
        /// <param name="rot"></param>
        protected internal void AttachObject(
            IClientAPI remoteClient, uint objectLocalID, uint AttachmentPt, bool appendMode, Quaternion rot, bool silent)
        {
            remoteClient.RunAttachmentOperation(() =>
                {
                    // If we can't take it, we can't attach it!
                    //
                    SceneObjectPart part = m_parentScene.GetSceneObjectPart(objectLocalID);
                    if (part == null)
                        return;

                    if (!m_parentScene.Permissions.CanTakeObject(part.ParentGroup.UUID, remoteClient.AgentId))
                        return;

                    SceneObjectGroup group = part.ParentGroup;
                    group.Rotation = rot;

                    AttachObject(remoteClient, objectLocalID, AttachmentPt, appendMode, false, AttachFlags.FromInWorld);

                    ScenePresence sp = m_parentScene.GetScenePresence(remoteClient.AgentId);
                    UUID itemID = group.GetFromItemID();
                    if (sp != null) sp.Appearance.SetAttachment((int)AttachmentPt, appendMode, itemID, UUID.Zero);
                });
        }

        /// <summary>
        /// Rez and Attach a single item on the specified attachmentpt in either "Append/Add" or "Replace/Wear" mode
        /// </summary>
        /// <param name="remoteClient">The client receiving the attachment</param>
        /// <param name="itemID">What we are attaching</param>
        /// <param name="AttachmentPt">The attachment point to receive it</param>
        /// <param name="appendMode">Should we append/add or replace/wear the attachment</param>
        /// <returns>The SceneObjectGroup we rezzed and attached</returns>
        public SceneObjectGroup RezSingleAttachment(IClientAPI remoteClient, UUID itemID, uint AttachmentPt, bool appendMode)
        {
            SceneObjectGroup objatt = m_parentScene.RezObject(remoteClient, remoteClient.ActiveGroupId,
                itemID, Vector3.Zero, Vector3.Zero, UUID.Zero, (byte)1, true,
                false, false, remoteClient.AgentId, true, AttachmentPt, 0);


            if (objatt != null)
            {
                bool tainted = false;
                if (AttachmentPt != 0 && AttachmentPt != objatt.GetBestAttachmentPoint())
                    tainted = true;

                AttachObject(remoteClient, objatt.LocalId, AttachmentPt, appendMode, false, AttachFlags.None);

                //Send the attachment immediately, so that it goes out before other prims that may be in the sending queue
                objatt.SendGroupFullUpdate(PrimUpdateFlags.ForcedFullUpdate);
                //objatt.ScheduleGroupForFullUpdate();
                if (tainted)
                    objatt.HasGroupChanged = true;

                // Fire after attach, so we don't get messy perms dialogs
                //
                objatt.CreateScriptInstances(0, ScriptStartFlags.PostOnRez, m_parentScene.DefaultScriptEngine, (int)ScriptStateSource.PrimData, null);
            }
            return objatt;
        }

        // This method uses an *inventory* UUID (because it is coming from the source (in this case an LSL script?) as an inventory ID).
        private void DetachSingleAttachmentFromItemID(UUID itemID, UUID groupId, IClientAPI remoteClient, bool isBot)
        {
            if (itemID == UUID.Zero) // If this happened, someone made a mistake....
                return;

            if (groupId != UUID.Zero)
            {
                SceneObjectPart part = null;
                lock (m_dictionary_lock)
                {
                    SceneObjectPartsByFullID.TryGetValue(groupId, out part);
                }

                if (part != null)
                {
                    SceneObjectGroup group = part.ParentGroup;
                    if (group.OwnerID == remoteClient.AgentId)
                    {
                        if (isBot)
                            m_parentScene.DeleteAttachment(group);
                        else
                            m_parentScene.SaveAndDeleteAttachment(remoteClient, group,
                                group.GetFromItemID(), group.OwnerID);
                    }
                }
            }
            else
            {
                // We can NOT use the dictionries here, as we are looking
                // for an entity by the fromAssetID, which is NOT the prim UUID
                //
                List<EntityBase> detachEntities = GetEntities();
                SceneObjectGroup group;

                foreach (EntityBase entity in detachEntities)
                {
                    if (entity is SceneObjectGroup)
                    {
                        group = (SceneObjectGroup)entity;
                        if (group.GetFromItemID() == itemID)
                        {
                            if (group.OwnerID == remoteClient.AgentId)
                            {
                                if (isBot)
                                    m_parentScene.DeleteAttachment(group);
                                else
                                    m_parentScene.SaveAndDeleteAttachment(remoteClient, group,
                                        group.GetFromItemID(), group.OwnerID);
                                return;
                            }
                        }
                    }
                }
            }
        }

        // This method uses an *inventory* UUID (because it is coming from the source (in this case an LSL script?) as an inventory ID).
        public void DetachSingleBotAttachment(UUID itemID, UUID groupId, IClientAPI remoteClient)
        {
            DetachSingleAttachmentFromItemID(itemID, groupId, remoteClient, true);
        }

        // This method uses an *inventory* UUID (because it is coming from the source (in this case an LSL script?) as an inventory ID).
        public void DetachSingleAttachmentToInv(UUID itemID, UUID groupId, IClientAPI remoteClient)
        {
            DetachSingleAttachmentFromItemID(itemID, groupId, remoteClient, false);
        }

        // This one tries to detach using an attachment point.
        // It will free up all objects on that attachment point except 'SkipGroup'.
        public void DetachSingleAttachmentPointToInv(uint AttachmentPt, IClientAPI remoteClient, SceneObjectGroup skipGroup)
        {
            // We can NOT use the dictionries here, as we are looking
            // for an entity by the attachment point.
            //
            List<EntityBase> detachEntities = GetEntities();
            SceneObjectGroup group;

            foreach (EntityBase entity in detachEntities)
            {
                if (entity is SceneObjectGroup)
                {
                    group = (SceneObjectGroup)entity;
                    if (group == skipGroup)
                        continue;   // don't remove this one
                    if (group.OwnerID != remoteClient.AgentId)
                        continue;    // don't remove others' attachments
                    byte currentAttachment = group.GetCurrentAttachmentPoint();
                    if (currentAttachment != (byte)AttachmentPt)
                        continue;   // we don't care about that attachment point
                    m_parentScene.SaveAndDeleteAttachment(remoteClient, group,
                            group.GetFromItemID(), group.OwnerID);
                    //                    return;  // remove ALL attachments on this attachment point
                }
            }
        }

        public void AttachObject(
            IClientAPI remoteClient, uint objectLocalID, uint AttachmentPt, bool appendMode, bool silent,
            AttachFlags flags)
        {
            SceneObjectGroup group = GetGroupByPrim(objectLocalID);
            if (group == null)
            {
                m_log.ErrorFormat("[SCENE] Could not find localID {0} attach object to {1}.", objectLocalID, remoteClient.AgentId);
                return;
            }

            if (group.HasSittingAvatars)
            {
                m_log.ErrorFormat("[SCENE] Not allowing {0} to attach object '{1}'. Group has sitting avatars.", remoteClient.AgentId, group.Name);
                remoteClient.SendAgentAlertMessage("You can not attach an object that has seated avatars", false);
                return;
            }

            if (m_parentScene.Permissions.CanTakeObject(group.UUID, remoteClient.AgentId) == false)
            {
                m_log.ErrorFormat("[SCENE] Insufficient permission for {0} to attach object '{1}'.", remoteClient.AgentId, group.Name);
                remoteClient.SendAgentAlertMessage("You don't have sufficient permissions to attach this object", false);
                return;
            }

            if ((group.OwnerID != remoteClient.AgentId))
            {
                // Even for bots, the group.OwnerID will be set to the bot ID, so this is a safe check.
                m_log.WarnFormat(
                    "[SCENE] Invalid wear attachment owned by {0} on {1} object '{2}'.",
                    group.UUID, remoteClient.AgentId, group.Name);
                return;
            }

            bool isTainted = false;
            if ((flags & (AttachFlags.FromInWorld | AttachFlags.FromCrossing)) != 0) //if this object is from in-world, we need to prep it first
            {
                bool fromCrossing = (flags & AttachFlags.FromCrossing) == AttachFlags.FromCrossing;
                if (!group.PrepareForRezAsAttachment(AttachmentPt, out isTainted, fromCrossing))
                {
                    return;
                }

                // Taint the map tile if qualifying.
                foreach (SceneObjectPart part in group.GetParts())
                {
                    m_parentScene.MarkMapTileTainted(part);
                }
            }
            else
            {
                //this is an inventory based rez. check if the attachment point has changed
                isTainted = group.TaintedAttachment;
                group.TaintedAttachment = false;
            }

            //at this point attachment point has been set by one of the calls to PrepareForRezAsAttachment, we should trust what is in the object
            AttachmentPt = group.AttachmentPoint;

            //this shouldnt happen
            if (AttachmentPt == 0)
            {
                m_log.WarnFormat("[SCENE]: AttachmentPoint still ZERO and about to attach! PrepareForRezAsAttachment not called?");
                // In some cases, it's very bad to continue. Something completely invalid has happened, fundamental assumptions are wrong. This should have been taken care of by the callers, so fail attach.
                return;
            }

            // Now that we know which AttachmentPt, free up all objects on that attachment point except 'group'.
            if (!appendMode)
                DetachSingleAttachmentPointToInv(AttachmentPt, remoteClient, group);

            // Saves and gets assetID
            UUID itemId = group.GetFromItemID();

            if ((flags & AttachFlags.FromInWorld) != 0 && (flags & AttachFlags.Temp) == 0)
                m_parentScene.AttachObjectAssetStore(remoteClient, group, remoteClient.AgentId, group.RezzedFromFolderId, out itemId);

            if ((flags & AttachFlags.Temp) != 0 || group.IsTempAttachment)
            {
                group.IsTempAttachment = true;
                group.SetFromItemID(UUID.Random());
            }
            else
            {
                group.IsTempAttachment = false;
            }

            group.AttachToAgent(remoteClient.AgentId, AttachmentPt, silent);

            // In case it is later dropped again, don't let
            // it get cleaned up
            //
            group.RootPart.RemFlag(PrimFlags.TemporaryOnRez);

            //SETS or CLEARS HasGroupChanged in the case when the only change is the 
            //fact that a bunch of methods are called when the attachment rezzes
            group.HasGroupChanged = isTainted;
            if (!silent)
                group.SendFullUpdateToAllClientsImmediate(false);

            if ((flags & AttachFlags.DontFireOnAttach) == 0)
            {
                //this needs to be called here as well because attaching greated a brand new prim
                m_parentScene.EventManager.TriggerOnAttachObject(remoteClient.AgentId, group.LocalId);
            }
        }

        protected internal ScenePresence CreateAndAddChildScenePresence(IClientAPI client, AvatarAppearance appearance)
        {
            ScenePresence newAvatar = new ScenePresence(client, m_parentScene, m_regInfo, appearance);
            newAvatar.IsChildAgent = true;

            AddScenePresence(newAvatar);

            return newAvatar;
        }

        /// <summary>
        /// Add a presence to the scene
        /// </summary>
        /// <param name="presence"></param>
        protected internal void AddScenePresence(ScenePresence presence)
        {
            bool child = presence.IsChildAgent;

            if (child)
            {
                Interlocked.Increment(ref m_numChildAgents);
            }
            else
            {
                Interlocked.Increment(ref m_numRootAgents);
                presence.AddToPhysicalScene(false);
            }

            Entities[presence.LocalId] = presence;

            lock (ScenePresences)
            {
                ScenePresences[presence.UUID] = presence;
                Monitor.PulseAll(ScenePresences);
            }
        }

        /// <summary>
        /// Remove a presence from the scene
        /// </summary>
        protected internal void RemoveScenePresence(UUID agentID)
        {
            if (!Entities.Remove(agentID))
            {
                m_log.WarnFormat(
                    "[SCENE] Tried to remove non-existent scene presence with agent ID {0} from scene Entities list",
                    agentID);
            }

            lock (ScenePresences)
            {
                if (!ScenePresences.Remove(agentID))
                {
                    m_log.WarnFormat("[SCENE] Tried to remove non-existent scene presence with agent ID {0} from scene ScenePresences list", agentID);
                }
            }
        }

        protected internal void SwapRootChildAgent(bool becameChild)
        {
            if (becameChild)
            {
                Interlocked.Decrement(ref m_numRootAgents);
                Interlocked.Increment(ref m_numChildAgents);
            }
            else
            {
                Interlocked.Decrement(ref m_numChildAgents);
                Interlocked.Increment(ref m_numRootAgents);
            }
        }

        public void removeUserCount(bool isRoot)
        {
            if (isRoot)
            {
                Interlocked.Decrement(ref m_numRootAgents);
            }
            else
            {
                Interlocked.Decrement(ref m_numChildAgents);
            }
        }

        public void RecalculateStats()
        {
            List<ScenePresence> SPList = GetScenePresences();
            int rootcount = 0;
            int childcount = 0;

            foreach (ScenePresence user in SPList)
            {
                if (user.IsChildAgent)
                    childcount++;
                else
                    rootcount++;
            }
            m_numRootAgents = rootcount;
            m_numChildAgents = childcount;

        }

        public int GetChildAgentCount()
        {
            // some network situations come in where child agents get closed twice.
            if (m_numChildAgents < 0)
            {
                m_numChildAgents = 0;
            }

            return m_numChildAgents;
        }

        public int GetRootAgentCount()
        {
            return m_numRootAgents;
        }

        public int GetTotalObjectsCount()
        {
            return m_numPrim;
        }

        public int GetActiveObjectsCount()
        {
            return m_physicalPrim;
        }

        public int GetActiveScriptsCount()
        {
            return m_activeScripts;
        }

        public int GetScriptLPS()
        {
            int returnval = m_scriptLPS;
            m_scriptLPS = 0;
            return returnval;
        }

        #endregion

        #region Get Methods

        /// <summary>
        /// Request a List of all scene presences in this scene.  This is a new list, so no
        /// locking is required to iterate over it.
        /// </summary>
        /// <returns></returns>
        protected internal List<ScenePresence> GetScenePresences()
        {
            lock (ScenePresences)
            {
                return new List<ScenePresence>(ScenePresences.Values);
            }
        }

        protected internal List<ScenePresence> GetAvatars()
        {
            List<ScenePresence> result =
                GetScenePresences(delegate(ScenePresence scenePresence) { return !scenePresence.IsChildAgent; });

            return result;
        }

        /// <summary>
        /// Get the controlling client for the given avatar, if there is one.
        ///
        /// FIXME: The only user of the method right now is Caps.cs, in order to resolve a client API since it can't
        /// use the ScenePresence.  This could be better solved in a number of ways - we could establish an
        /// OpenSim.Framework.IScenePresence, or move the caps code into a region package (which might be the more
        /// suitable solution).
        /// </summary>
        /// <param name="agentId"></param>
        /// <returns>null if either the avatar wasn't in the scene, or
        /// they do not have a controlling client</returns>
        /// <remarks>this used to be protected internal, but that
        /// prevents CapabilitiesModule from accessing it</remarks>
        public IClientAPI GetControllingClient(UUID agentId)
        {
            ScenePresence presence = GetScenePresence(agentId);

            if (presence != null)
            {
                return presence.ControllingClient;
            }

            return null;
        }

        /// <summary>
        /// Request a filtered list of m_scenePresences in this World
        /// </summary>
        /// <returns></returns>
        protected internal List<ScenePresence> GetScenePresences(FilterAvatarList filter)
        {
            // No locking of scene presences here since we're passing back a list...

            List<ScenePresence> result = new List<ScenePresence>();
            List<ScenePresence> ScenePresencesList = GetScenePresences();

            foreach (ScenePresence avatar in ScenePresencesList)
            {
                if (filter(avatar))
                {
                    result.Add(avatar);
                }
            }

            return result;
        }

        /// <summary>
        /// Request a scene presence by UUID
        /// </summary>
        /// <param name="avatarID"></param>
        /// <returns>null if the agent was not found</returns>
        protected internal ScenePresence GetScenePresence(UUID agentID)
        {
            ScenePresence sp;

            lock (ScenePresences)
            {
                ScenePresences.TryGetValue(agentID, out sp);
            }

            return sp;
        }

        /// <summary>
        /// Request a scene presence by localid
        /// </summary>
        /// <param name="avatarID"></param>
        /// <returns>null if the agent was not found</returns>
        protected internal ScenePresence GetScenePresence(uint localId)
        {
            EntityBase sp;

            if (Entities.TryGetValue(localId, out sp))
            {
                return sp as ScenePresence;
            }

            return null;
        }

        /// <summary>
        /// Waits for the given scene presence to appear, or times out after timeout seconds
        /// </summary>
        /// <param name="agentID"></param>
        /// <returns></returns>
        protected internal ScenePresence WaitScenePresence(UUID agentID, int waitTime)
        {
            ScenePresence sp = null;
            lock (ScenePresences)
            {
                DateTime waitStarted = DateTime.Now;

                while (DateTime.Now - waitStarted < TimeSpan.FromMilliseconds(waitTime))
                {
                    ScenePresences.TryGetValue(agentID, out sp);
                    if (sp == null)
                    {
                        if (Monitor.Wait(ScenePresences, Math.Min(1000, waitTime)))
                        {
                            ScenePresences.TryGetValue(agentID, out sp);

                            if (sp != null)
                            {
                                return sp;
                            }
                        }
                    }
                    else
                    {
                        return sp;
                    }
                }
            }

            return null;
        }

        public SceneObjectPart GetPrimByLocalId(uint localID)
        {
            lock (m_dictionary_lock)
            {
                SceneObjectPart part;
                if (SceneObjectPartsByLocalID.TryGetValue(localID, out part))
                {
                    return part;
                }
            }

            return null;
        }

        public SceneObjectPart GetPrimByFullId(UUID fullID)
        {
            lock (m_dictionary_lock)
            {
                SceneObjectPart part;
                if (SceneObjectPartsByFullID.TryGetValue(fullID, out part))
                {
                    return part;
                }
            }
            return null;
        }

        /// <summary>
        /// Get a scene object group that contains the prim with the given local id
        /// </summary>
        /// <param name="localID"></param>
        /// <returns>null if no scene object group containing that prim is found</returns>
        public SceneObjectGroup GetGroupByPrim(uint localID)
        {
            lock (m_dictionary_lock)
            {
                if (Entities.ContainsKey(localID))
                    return Entities[localID] as SceneObjectGroup;
            }

            //m_log.DebugFormat("Entered GetGroupByPrim with localID {0}", localID);
            lock (m_dictionary_lock)
            {
                //finally our cached part list which should have this if it exists at all
                SceneObjectPart part;
                if (SceneObjectPartsByLocalID.TryGetValue(localID, out part))
                {
                    return part.ParentGroup;
                }
            }

            return null;
        }

        /// <summary>
        /// Get a scene object group that contains the prim with the given uuid
        /// </summary>
        /// <param name="fullID"></param>
        /// <returns>null if no scene object group containing that prim is found</returns>
        public SceneObjectGroup GetGroupByPrim(UUID fullID)
        {
            SceneObjectPart sop;
            lock (m_dictionary_lock)
            {
                if (SceneObjectPartsByFullID.TryGetValue(fullID, out sop))
                {
                    return sop.ParentGroup;
                }
            }

            List<EntityBase> EntityList = GetEntities();

            foreach (EntityBase ent in EntityList)
            {
                if (ent is SceneObjectGroup)
                {
                    if (((SceneObjectGroup)ent).HasChildPrim(fullID))
                    {
                        SceneObjectGroup sog = (SceneObjectGroup)ent;
                        lock (m_dictionary_lock)
                        {
                            foreach (SceneObjectPart part in sog.GetParts())
                            {
                                SceneObjectPartsByFullID[part.UUID] = part;
                                SceneObjectPartsByLocalID[part.LocalId] = part;
                            }
                        }
                        m_log.WarnFormat("[SceneGraph]: GetGroupByPrim found an unknown UUID {0} as a SOG {1} owned by {2}", fullID, sog.Name, sog.OwnerID);
                        return sog;
                    }
                }
            }

            return null;
        }

        protected internal EntityIntersection GetClosestIntersectingPrim(Ray hray, bool frontFacesOnly, bool faceCenters)
        {
            // Primitive Ray Tracing
            float closestDistance = 280f;
            EntityIntersection returnResult = new EntityIntersection();
            List<EntityBase> EntityList = GetEntities();
            foreach (EntityBase ent in EntityList)
            {
                if (ent is SceneObjectGroup)
                {
                    SceneObjectGroup reportingG = (SceneObjectGroup)ent;
                    EntityIntersection result = reportingG.TestIntersection(hray, frontFacesOnly, faceCenters);
                    if (result.HitTF)
                    {
                        if (result.distance < closestDistance)
                        {
                            closestDistance = result.distance;
                            returnResult = result;
                        }
                    }
                }
            }
            return returnResult;
        }

        /// <summary>
        /// Get a part contained in this scene.
        /// </summary>
        /// <param name="localID"></param>
        /// <returns>null if the part was not found</returns>
        protected internal SceneObjectPart GetSceneObjectPart(uint localID)
        {
            lock (m_dictionary_lock)
            {
                SceneObjectPart part;
                if (SceneObjectPartsByLocalID.TryGetValue(localID, out part))
                {
                    return part;
                }
            }

            return null;
        }

        /// <summary>
        /// Get a named prim contained in this scene (will return the first 
        /// found, if there are more than one prim with the same name)
        /// </summary>
        /// <param name="name"></param>
        /// <returns>null if the part was not found</returns>
        protected internal SceneObjectPart GetSceneObjectPart(string name)
        {
            List<EntityBase> EntityList = GetEntities();

            // FIXME: use a dictionary here
            foreach (EntityBase ent in EntityList)
            {
                if (ent is SceneObjectGroup)
                {
                    foreach (SceneObjectPart p in ((SceneObjectGroup)ent).GetParts())
                    {
                        if (p.Name == name)
                        {
                            return p;
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Get a part contained in this scene.
        /// </summary>
        /// <param name="fullID"></param>
        /// <returns>null if the part was not found</returns>
        protected internal SceneObjectPart GetSceneObjectPart(UUID fullID)
        {
            SceneObjectGroup group = GetGroupByPrim(fullID);
            if (group == null)
                return null;
            return group.GetChildPart(fullID);
        }

        protected internal bool TryGetAvatar(UUID avatarId, out ScenePresence avatar)
        {
            ScenePresence presence;

            lock (ScenePresences)
            {
                if (ScenePresences.TryGetValue(avatarId, out presence))
                {
                    avatar = presence;
                    return true;

                    //if (!presence.IsChildAgent)
                    //{
                    //    avatar = presence;
                    //    return true;
                    //}
                    //else
                    //{
                    //    m_log.WarnFormat(
                    //        "[INNER SCENE]: Requested avatar {0} could not be found in scene {1} since it is only registered as a child agent!",
                    //        avatarId, m_parentScene.RegionInfo.RegionName);
                    //}
                }
            }

            avatar = null;
            return false;
        }

        protected internal bool TryGetAvatarByName(string avatarName, out ScenePresence avatar)
        {
            lock (ScenePresences)
            {
                foreach (ScenePresence presence in ScenePresences.Values)
                {
                    if (!presence.IsChildAgent)
                    {
                        string name = presence.ControllingClient.Name;

                        if (String.Compare(avatarName, name, true) == 0)
                        {
                            avatar = presence;
                            return true;
                        }
                    }
                }
            }

            avatar = null;
            return false;
        }

        /// <summary>
        /// Returns a list of the entities in the scene.  This is a new list so no locking is required to iterate over
        /// it
        /// </summary>
        /// <returns></returns>
        protected internal List<EntityBase> GetEntities()
        {
            return Entities.GetEntities();
        }

        public Dictionary<uint, float> GetTopScripts()
        {
            Dictionary<uint, float> topScripts = new Dictionary<uint, float>();

            List<EntityBase> EntityList = GetEntities();
            int limit = 0;
            foreach (EntityBase ent in EntityList)
            {
                if (ent is SceneObjectGroup)
                {
                    SceneObjectGroup grp = (SceneObjectGroup)ent;
                    //if ((grp.RootPart.GetEffectiveObjectFlags() & PrimFlags.Scripted) != 0)
                    if(grp.IsScripted)
                    {
                        //this will cause a clear if the script hasnt run in a while
                        grp.AddScriptLPS(0.0);
                        if (grp.scriptScore >= 0.01)
                        {
                            topScripts.Add(grp.LocalId, (float)(grp.GetScriptsAverage()));
                            limit++;
                            if (limit >= 100)
                            {
                                break;
                            }
                        }
                    }
                }
            }

            return topScripts;
        }

        #endregion

        #region Other Methods

        protected internal void physicsBasedCrash()
        {
            handlerPhysicsCrash = UnRecoverableError;
            if (handlerPhysicsCrash != null)
            {
                handlerPhysicsCrash();
            }
        }

        protected internal UUID ConvertLocalIDToFullID(uint localID)
        {
            SceneObjectGroup group = GetGroupByPrim(localID);
            if (group != null)
                return group.GetPartsFullID(localID);
            else
                return UUID.Zero;
        }

        protected internal void ForEachClient(Action<IClientAPI> action)
        {
            List<ScenePresence> splist = GetScenePresences();
            foreach (ScenePresence presence in splist)
            {
                try
                {
                    action(presence.ControllingClient);
                }
                catch (Exception e)
                {
                    // Catch it and move on. This includes situations where splist has inconsistent info
                    m_log.WarnFormat("[SCENE]: Problem processing action in ForEachClient: ", e.Message);
                }
            }
        }

        #endregion

        #region Client Event handlers

        /// <summary>
        ///
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="scale"></param>
        /// <param name="remoteClient"></param>
        protected internal void UpdatePrimScale(uint localID, Vector3 scale, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(localID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanEditObject(group.UUID, remoteClient.AgentId, (uint)PermissionMask.Modify))
                {
                    group.Resize(scale, localID);

                    // Taint the map tile if qualifying.
                    foreach (SceneObjectPart part in group.GetParts())
                    {
                        m_parentScene.MarkMapTileTainted(part);
                    }
                }
            }
        }

        protected internal void UpdatePrimGroupScale(uint localID, Vector3 scale, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(localID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanEditObject(group.UUID, remoteClient.AgentId, (uint)PermissionMask.Modify))
                {
                    group.GroupResize(scale, localID);

                    // Taint the map tile if qualifying.
                    foreach (SceneObjectPart part in group.GetParts())
                    {
                        m_parentScene.MarkMapTileTainted(part);
                    }
                }
            }
        }

        /// <summary>
        /// This handles the nifty little tool tip that you get when you drag your mouse over an object
        /// Send to the Object Group to process.  We don't know enough to service the request
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="AgentID"></param>
        /// <param name="RequestFlags"></param>
        /// <param name="ObjectID"></param>
        protected internal void RequestObjectPropertiesFamily(
             IClientAPI remoteClient, UUID AgentID, uint RequestFlags, UUID ObjectID)
        {
            SceneObjectGroup group = GetGroupByPrim(ObjectID);
            if (group != null)
            {
                group.ServiceObjectPropertiesFamilyRequest(remoteClient, AgentID, RequestFlags);
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="rot"></param>
        /// <param name="remoteClient"></param>
        protected internal void UpdatePrimSingleRotation(uint localID, Quaternion rot, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(localID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanMoveObject(group.UUID, remoteClient.AgentId))
                {
                    group.UpdateSingleRotation(rot, localID);

                    // Taint the map tile if qualifying.
                    foreach (SceneObjectPart part in group.GetParts())
                    {
                        m_parentScene.MarkMapTileTainted(part);
                    }
                }
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="rot"></param>
        /// <param name="pos"></param>
        /// <param name="remoteClient"></param>
        protected internal void UpdatePrimSingleRotationPosition(uint localID, Quaternion rot, Vector3 pos, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(localID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanMoveObject(group.UUID, remoteClient.AgentId))
                {
                    group.UpdateSingleRotation(rot, pos, localID);

                    // Taint the map tile if qualifying.
                    foreach (SceneObjectPart part in group.GetParts())
                    {
                        m_parentScene.MarkMapTileTainted(part);
                    }
                }
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="rot"></param>
        /// <param name="remoteClient"></param>
        protected internal void UpdatePrimRotation(uint localID, Quaternion rot, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(localID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanMoveObject(group.UUID, remoteClient.AgentId))
                {
                    group.UpdateGroupRotation(rot, true);

                    // Taint the map tile if qualifying.
                    foreach (SceneObjectPart part in group.GetParts())
                    {
                        m_parentScene.MarkMapTileTainted(part);
                    }
                }
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="pos"></param>
        /// <param name="rot"></param>
        /// <param name="remoteClient"></param>
        protected internal void UpdatePrimRotation(uint localID, Vector3 pos, Quaternion rot, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(localID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanMoveObject(group.UUID, remoteClient.AgentId))
                {
                    group.UpdateGroupRotation(pos, rot);

                    // Taint the map tile if qualifying.
                    foreach (SceneObjectPart part in group.GetParts())
                    {
                        m_parentScene.MarkMapTileTainted(part);
                    }
                }
            }
        }

        /// <summary>
        /// Update the position of the given part
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="pos"></param>
        /// <param name="remoteClient"></param>
        protected internal void UpdatePrimSinglePosition(uint localID, Vector3 pos, IClientAPI remoteClient, bool saveUpdate)
        {
            SceneObjectGroup group = GetGroupByPrim(localID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanMoveObject(group.UUID, remoteClient.AgentId))
                {
                    group.UpdateSinglePosition(pos, localID, saveUpdate);

                    // Taint the map tile if qualifying.
                    foreach (SceneObjectPart part in group.GetParts())
                    {
                        m_parentScene.MarkMapTileTainted(part);
                    }
                }
            }
        }

        /// <summary>
        /// Update the position of the given part
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="pos"></param>
        /// <param name="remoteClient"></param>
        protected internal void UpdatePrimPosition(uint localID, Vector3 pos, IClientAPI remoteClient, bool SaveUpdate)
        {
            SceneObjectGroup group = GetGroupByPrim(localID);
            if (group != null)
            {

                // Vector3 oldPos = group.AbsolutePosition;
                if (group.IsAttachment)
                {
                    // If this is an attachment, then we need to save the modified
                    // object back into the avatar's inventory. First we update the
                    // relative positioning (which caused this method to get driven
                    // in the first place. Then we save the asset back into the
                    // appropriate inventory entry.
                    group.UpdateGroupPosition(pos, SaveUpdate);
                    group.AbsolutePosition = group.RootPart.AttachedPos;
                    //m_parentScene.updateKnownAsset(remoteClient, group, group.GetFromAssetID(), group.OwnerID, false);
                }
                else
                {
                    if (m_parentScene.Permissions.CanMoveObject(group.UUID, remoteClient.AgentId) && m_parentScene.Permissions.CanObjectEntry(group.UUID, false, pos))
                    {
                        group.UpdateGroupPosition(pos, SaveUpdate);

                        // Taint the map tile if qualifying.
                        foreach (SceneObjectPart part in group.GetParts())
                        {
                            m_parentScene.MarkMapTileTainted(part);
                        }
                    }
                }
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="texture"></param>
        /// <param name="remoteClient"></param>
        protected internal void UpdatePrimTexture(uint localID, byte[] texture, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(localID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanEditObject(group.UUID, remoteClient.AgentId, (uint)PermissionMask.Modify))
                {
                    group.UpdateTextureEntry(localID, texture);

                    // Taint the map tile if qualifying.
                    foreach (SceneObjectPart part in group.GetParts())
                    {
                        m_parentScene.MarkMapTileTainted(part);
                    }
                }
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="packet"></param>
        /// <param name="remoteClient"></param>
        /// This routine seems to get called when a user changes object settings in the viewer.
        /// If some one can confirm that, please change the comment according.
        protected internal void UpdatePrimFlags(uint localID, bool UsePhysics, bool IsTemporary, bool IsPhantom,
            IClientAPI remoteClient, ObjectFlagUpdatePacket.ExtraPhysicsBlock[] blocks)
        {
            SceneObjectPart part = GetPrimByLocalId(localID);
            if (part != null)
            {
                if (m_parentScene.Permissions.CanEditObject(part.ParentGroup.UUID, remoteClient.AgentId, (uint)PermissionMask.Modify))
                {
                    bool VolDetect = false;  // VolumeDetect can't be set via UI and will always be off when a change is made there
                    if (part.IsRootPart())
                        part.ParentGroup.UpdateFlags(UsePhysics, IsTemporary, IsPhantom, VolDetect, blocks);
                    else
                        part.UpdatePrimFlags(UsePhysics, IsTemporary, IsPhantom, VolDetect, blocks);
                    
                    // Taint the map tile if qualifying.
                    m_parentScene.MarkMapTileTainted(part);
                }
            }
        }

        protected internal void DegrabObject(uint localID, IClientAPI remoteClient, List<SurfaceTouchEventArgs> surfaceArgs)
        {
            SceneObjectGroup group = GetGroupByPrim(ConvertLocalIDToFullID(localID));

            if (group != null)
            {
                if (m_parentScene.Permissions.CanMoveObject(group.UUID, remoteClient.AgentId))// && PermissionsMngr.)
                {
                    group.DeGrab(remoteClient);
                }

                group.RootPart.ScheduleTerseUpdate();
            }
        }

        /// <summary>
        /// Move the given object
        /// </summary>
        /// <param name="objectID"></param>
        /// <param name="offset"></param>
        /// <param name="pos"></param>
        /// <param name="remoteClient"></param>
        protected internal void MoveObject(UUID objectID, Vector3 offset, Vector3 pos, IClientAPI remoteClient, List<SurfaceTouchEventArgs> surfaceArgs)
        {
            SceneObjectGroup group = GetGroupByPrim(objectID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanMoveObject(group.UUID, remoteClient.AgentId))// && PermissionsMngr.)
                {
                    group.GrabMovement(offset, pos, remoteClient);

                    // Taint the map tile if qualifying.
                    foreach (SceneObjectPart part in group.GetParts())
                    {
                        m_parentScene.MarkMapTileTainted(part);
                    }
                }

                group.RootPart.ScheduleTerseUpdate();
            }
        }

        /// <summary>
        /// Start spinning the given object
        /// </summary>
        /// <param name="objectID"></param>
        /// <param name="rotation"></param>
        /// <param name="remoteClient"></param>
        protected internal void SpinStart(UUID objectID, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(objectID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanMoveObject(group.UUID, remoteClient.AgentId))// && PermissionsMngr.)
                {
                    group.SpinStart(remoteClient);
                }
            }
        }

        /// <summary>
        /// Spin the given object
        /// </summary>
        /// <param name="objectID"></param>
        /// <param name="rotation"></param>
        /// <param name="remoteClient"></param>
        protected internal void SpinObject(UUID objectID, Quaternion rotation, IClientAPI remoteClient)
        {
            SceneObjectGroup group = GetGroupByPrim(objectID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanMoveObject(group.UUID, remoteClient.AgentId))// && PermissionsMngr.)
                {
                    group.SpinMovement(rotation, remoteClient);
                }
                // This is outside the above permissions condition
                // so that if the object is locked the client moving the object
                // get's it's position on the simulator even if it was the same as before
                // This keeps the moving user's client in sync with the rest of the world.
                group.SendGroupTerseUpdate();
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="primLocalID"></param>
        /// <param name="description"></param>
        protected internal void PrimName(IClientAPI remoteClient, uint primLocalID, string name)
        {
            SceneObjectGroup group = GetGroupByPrim(primLocalID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanEditObject(group.UUID, remoteClient.AgentId, (uint)PermissionMask.Modify))
                {
                    group.SetPartName(Util.CleanString(name), primLocalID);
                    group.HasGroupChanged = true;
                }
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="primLocalID"></param>
        /// <param name="description"></param>
        protected internal void PrimDescription(IClientAPI remoteClient, uint primLocalID, string description)
        {
            SceneObjectGroup group = GetGroupByPrim(primLocalID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanEditObject(group.UUID, remoteClient.AgentId, (uint)PermissionMask.Modify))
                {
                    group.SetPartDescription(Util.CleanString(description), primLocalID);
                    group.GetProperties(remoteClient);
                    group.HasGroupChanged = true;
                }
            }
        }

        protected internal void PrimClickAction(IClientAPI remoteClient, uint primLocalID, string clickAction)
        {
            SceneObjectGroup group = GetGroupByPrim(primLocalID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanEditObject(group.UUID, remoteClient.AgentId, (uint)PermissionMask.Modify))
                {
                    SceneObjectPart part = m_parentScene.GetSceneObjectPart(primLocalID);
                    part.ClickAction = Convert.ToByte(clickAction);
                    group.HasGroupChanged = true;
                }
            }
        }

        protected internal void PrimMaterial(IClientAPI remoteClient, uint primLocalID, string material)
        {
            SceneObjectGroup group = GetGroupByPrim(primLocalID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanEditObject(group.UUID, remoteClient.AgentId, (uint)PermissionMask.Modify))
                {
                    SceneObjectPart part = m_parentScene.GetSceneObjectPart(primLocalID);
                    part.Material = Convert.ToByte(material);
                    group.HasGroupChanged = true;
                }
            }
        }

        protected internal void UpdateExtraParam(UUID agentID, uint primLocalID, ushort type, bool inUse, byte[] data)
        {
            SceneObjectGroup group = GetGroupByPrim(primLocalID);

            if (group != null)
            {
                if (m_parentScene.Permissions.CanEditObject(group.UUID, agentID, (uint)PermissionMask.Modify))
                {
                    group.UpdateExtraParam(primLocalID, type, inUse, data);
                }
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="primLocalID"></param>
        /// <param name="shapeBlock"></param>
        protected internal void UpdatePrimShape(UUID agentID, uint primLocalID, UpdateShapeArgs shapeBlock)
        {
            SceneObjectGroup group = GetGroupByPrim(primLocalID);
            if (group != null)
            {
                if (m_parentScene.Permissions.CanEditObject(group.UUID, agentID, (uint)PermissionMask.Modify))
                {
                    ObjectShapePacket.ObjectDataBlock shapeData = new ObjectShapePacket.ObjectDataBlock();
                    shapeData.ObjectLocalID = shapeBlock.ObjectLocalID;
                    shapeData.PathBegin = shapeBlock.PathBegin;
                    shapeData.PathCurve = shapeBlock.PathCurve;
                    shapeData.PathEnd = shapeBlock.PathEnd;
                    shapeData.PathRadiusOffset = shapeBlock.PathRadiusOffset;
                    shapeData.PathRevolutions = shapeBlock.PathRevolutions;
                    shapeData.PathScaleX = shapeBlock.PathScaleX;
                    shapeData.PathScaleY = shapeBlock.PathScaleY;
                    shapeData.PathShearX = shapeBlock.PathShearX;
                    shapeData.PathShearY = shapeBlock.PathShearY;
                    shapeData.PathSkew = shapeBlock.PathSkew;
                    shapeData.PathTaperX = shapeBlock.PathTaperX;
                    shapeData.PathTaperY = shapeBlock.PathTaperY;
                    shapeData.PathTwist = shapeBlock.PathTwist;
                    shapeData.PathTwistBegin = shapeBlock.PathTwistBegin;
                    shapeData.ProfileBegin = shapeBlock.ProfileBegin;
                    shapeData.ProfileCurve = shapeBlock.ProfileCurve;
                    shapeData.ProfileEnd = shapeBlock.ProfileEnd;
                    shapeData.ProfileHollow = shapeBlock.ProfileHollow;

                    group.UpdateShape(shapeData, primLocalID);

                    // Taint the map tile if qualifying.
                    foreach (SceneObjectPart part in group.GetParts())
                    {
                        m_parentScene.MarkMapTileTainted(part);
                    }
                }
            }
        }

        /// <summary>
        /// Initial method invoked when we receive a link objects request from the client.
        /// </summary>
        /// <param name="client"></param>
        /// <param name="parentPrim"></param>
        /// <param name="childPrims"></param>
        protected internal void LinkObjects(IClientAPI client, uint parentPrimId, List<uint> childPrimIds)
        {
            List<uint> newChildPrimIds = new List<uint>();
            SceneObjectGroup parentGroup = null;
            //m_log.DebugFormat("Linking Group, parent is {0}", parentPrimId);
            lock (m_dictionary_lock)
            {
                parentGroup = GetGroupByPrim(parentPrimId);
                if (parentGroup == null) return;

                if (!m_parentScene.Permissions.CanEditObject(parentGroup.UUID, client.AgentId, (uint)PermissionMask.Modify))
                    return;
                if ((parentGroup.RootPart.OwnerMask & (uint)PermissionMask.Modify) != (uint)PermissionMask.Modify)
                    return;

                if (parentGroup.IsAttachment)
                {
                    client.SendAlertMessage("Cannot link objects while attached: nothing to link.");
                    return;
                }

                foreach (uint id in childPrimIds)
                {
                    SceneObjectGroup group = this.GetGroupByPrim(id);
                    if (group.OwnerID != parentGroup.OwnerID)
                        return; // two different owners
                    if (!m_parentScene.Permissions.CanEditObject(group.UUID, client.AgentId, (uint)PermissionMask.Modify))
                        return;
                    if ((group.RootPart.OwnerMask & (uint)PermissionMask.Modify) != (uint)PermissionMask.Modify)
                        return;
                    if (group.IsAttachment)
                    {
                        client.SendAlertMessage("Cannot link objects while attached: nothing to link.");
                        return;
                    }

                    if (group.RootPart.LocalId != parentPrimId)
                    {
                        newChildPrimIds.Add(id);
                    }

                }
            }

            using (SceneTransaction transaction = _transactionMgr.BeginTransaction(newChildPrimIds, parentPrimId))
            {
                //would this link actually change the group?
                //if not, don't touch it because most likely the client
                //has already hit the link button and has pressed it twice
                if (newChildPrimIds.Count == 0)
                {
                    return;
                }

                List<SceneObjectGroup> childGroups = new List<SceneObjectGroup>();

                // We do this in reverse to get the link order of the prims correct
                for (int i = newChildPrimIds.Count - 1; i >= 0; i--)
                {
                    SceneObjectGroup child = GetGroupByPrim(newChildPrimIds[i]);
                    if (child != null)
                    {
                        // Make sure no child prim is set for sale
                        // So that, on delink, no prims are unwittingly
                        // left for sale and sold off
                        child.RootPart.ObjectSaleType = 0;
                        child.RootPart.SalePrice = 10;
                        child.Rationalize(child.OwnerID, false);
                        childGroups.Add(child);
                    }
                }

                foreach (SceneObjectGroup child in childGroups)
                {
                    parentGroup.LinkOtherGroupPrimsToThisGroup(child);
                }

                // Now fix avatar link numbers
                parentGroup.RecalcSeatedAvatarLinks();

                // We need to explicitly resend the newly link prim's object properties since no other actions
                // occur on link to invoke this elsewhere (such as object selection)
                parentGroup.RootPart.AddFlag(PrimFlags.CreateSelected);
                parentGroup.TriggerScriptChangedEvent(Changed.LINK);
                parentGroup.HasGroupChanged = true;
                parentGroup.ScheduleGroupForFullUpdate(PrimUpdateFlags.ForcedFullUpdate);
            }
        }

        public SceneTransaction BeginPrimTransaction(IEnumerable<uint> primIds)
        {
            return _transactionMgr.BeginTransaction(primIds);
        }

        /// <summary>
        /// Delink a linkset
        /// </summary>
        /// <param name="prims"></param>
        protected internal void DelinkObjects(IClientAPI remoteClient, List<uint> primIds)
        {
            DelinkObjects(remoteClient, primIds, true);
        }

        protected internal void DelinkObjects(IClientAPI remoteClient, List<uint> primIds, bool sendEvents)
        {
            List<SceneObjectPart> childParts = new List<SceneObjectPart>();
            List<SceneObjectPart> rootParts = new List<SceneObjectPart>();
            List<SceneObjectGroup> affectedGroups = new List<SceneObjectGroup>();
            C5.HashSet<uint> allAffectedIds = new C5.HashSet<uint>();

            lock (m_dictionary_lock)
            {
                // Look them all up in one go, since that is comparatively expensive
                //
                foreach (uint primID in primIds)
                {
                    SceneObjectPart part = this.GetSceneObjectPart(primID);
                    if (part != null)
                    {
                        if (!m_parentScene.Permissions.CanEditObject(part.ParentGroup.RootPart.UUID, remoteClient.AgentId, (uint)PermissionMask.Modify))
                            continue;
                        if ((part.ParentGroup.RootPart.OwnerMask & (uint)PermissionMask.Modify) != (uint)PermissionMask.Modify)
                            continue;

                        if (part.LinkNum < 2) // Root or single
                            rootParts.Add(part);
                        else
                            childParts.Add(part);

                        SceneObjectGroup group = part.ParentGroup;
                        if (!affectedGroups.Contains(group))
                            affectedGroups.Add(group);

                        allAffectedIds.Add(part.LocalId);
                        allAffectedIds.Add(group.LocalId);
                    }
                    else
                    {
                        m_log.ErrorFormat("Viewer requested unlink of nonexistent part {0}", primID);
                    }
                }
            }

            using (SceneTransaction transaction = _transactionMgr.BeginTransaction(allAffectedIds))
            {
                foreach (SceneObjectPart child in childParts)
                {
                    // Unlink all child parts from their groups
                    //
                    child.ParentGroup.DelinkFromGroup(child, sendEvents, false);
                }

                foreach (SceneObjectPart root in rootParts)
                {
                    // In most cases, this will run only one time, and the prim
                    // will be a solo prim
                    // However, editing linked parts and unlinking may be different
                    //
                    SceneObjectGroup group = root.ParentGroup;
                    List<SceneObjectPart> newSet = new List<SceneObjectPart>(group.GetParts());
                    int numChildren = newSet.Count;

                    // If there are prims left in a link set, but the root is
                    // slated for unlink, we need to do this
                    //
                    if (numChildren != 1)
                    {
                        // Unlink the remaining set
                        //
                        bool sendEventsToRemainder = true;
                        if (numChildren > 1)
                            sendEventsToRemainder = false;

                        foreach (SceneObjectPart p in newSet)
                        {
                            if (p != group.RootPart)
                                group.DelinkFromGroup(p, sendEventsToRemainder, false);
                        }

                        // If there is more than one prim remaining, we
                        // need to re-link
                        //
                        if (numChildren > 2)
                        {
                            // Remove old root
                            //
                            if (newSet.Contains(root))
                                newSet.Remove(root);

                            // Preserve link ordering
                            //
                            newSet.Sort(delegate(SceneObjectPart a, SceneObjectPart b)
                            {
                                return a.LinkNum.CompareTo(b.LinkNum);
                            });

                            // Determine new root
                            //
                            SceneObjectPart newRoot = newSet[0];
                            newSet.RemoveAt(0);

                            List<uint> linkIDs = new List<uint>();

                            foreach (SceneObjectPart newChild in newSet)
                            {
                                linkIDs.Add(newChild.LocalId);
                            }

                            LinkObjects(remoteClient, newRoot.LocalId, linkIDs);
                            if (!affectedGroups.Contains(newRoot.ParentGroup))
                                affectedGroups.Add(newRoot.ParentGroup);
                        }
                    }
                }

                // Now fix avatar link numbers
                foreach (SceneObjectGroup g in affectedGroups)
                {
                    g.RecalcSeatedAvatarLinks();
                }

                // Finally, trigger events in the roots
                //
                foreach (SceneObjectGroup g in affectedGroups)
                {
                    g.TriggerScriptChangedEvent(Changed.LINK);
                    g.ScheduleGroupForFullUpdate(PrimUpdateFlags.ForcedFullUpdate);
                }

                //m_log.DebugFormat("Delink finished for {0} prims", primIds.Count);
            }
        }

        protected internal void MakeObjectSearchable(IClientAPI remoteClient, bool IncludeInSearch, uint localID)
        {
            UUID user = remoteClient.AgentId;
            UUID objid = UUID.Zero;
            SceneObjectPart obj = null;

            List<EntityBase> EntityList = GetEntities();
            foreach (EntityBase ent in EntityList)
            {
                if (ent is SceneObjectGroup)
                {
                    foreach (SceneObjectPart subent in ((SceneObjectGroup)ent).GetParts())
                    {
                        if (subent.LocalId == localID)
                        {
                            objid = subent.UUID;
                            obj = subent;
                        }
                    }
                }
            }

            //Protip: In my day, we didn't call them searchable objects, we called them limited point-to-point joints
            //aka ObjectFlags.JointWheel = IncludeInSearch

            //Permissions model: Object can be REMOVED from search IFF:
            // * User owns object
            //use CanEditObject

            //Object can be ADDED to search IFF:
            // * User owns object
            // * Asset/DRM permission bit "modify" is enabled
            //use CanEditObjectPosition

            // libomv will complain about PrimFlags.JointWheel being
            // deprecated, so we
#pragma warning disable 0612
            if (IncludeInSearch && m_parentScene.Permissions.CanEditObject(obj.ParentGroup.UUID, user, (uint)PermissionMask.Modify))
            {
                obj.ParentGroup.RootPart.AddFlag(PrimFlags.JointWheel);
                obj.ParentGroup.HasGroupChanged = true;
            }
            else if (!IncludeInSearch && m_parentScene.Permissions.CanMoveObject(obj.ParentGroup.UUID, user))
            {
                obj.ParentGroup.RootPart.RemFlag(PrimFlags.JointWheel);
                obj.ParentGroup.HasGroupChanged = true;
            }
#pragma warning restore 0612
        }

        /// <summary>
        /// Duplicate the given object, Fire and Forget, No rotation, no return wrapper
        /// </summary>
        /// <param name="originalPrim"></param>
        /// <param name="offset"></param>
        /// <param name="flags"></param>
        protected internal void DuplicateObject(uint originalPrim, Vector3 offset, uint flags, UUID AgentID, UUID GroupID)
        {
            //m_log.DebugFormat("[SCENE]: Duplication of object {0} at offset {1} requested by agent {2}", originalPrim, offset, AgentID);

            // SceneObjectGroup dupe = DuplicateObject(originalPrim, offset, flags, AgentID, GroupID, Quaternion.Zero);
            DuplicateObject(originalPrim, offset, flags, AgentID, GroupID, Quaternion.Identity);
        }

        /// <summary>
        /// Duplicate the given object.
        /// </summary>
        /// <param name="originalPrim"></param>
        /// <param name="offset"></param>
        /// <param name="flags"></param>
        protected internal SceneObjectGroup DuplicateObject(uint originalPrimID, Vector3 offset, uint flags, UUID AgentID, UUID GroupID, Quaternion rot)
        {
            //m_log.DebugFormat("[SCENE]: Duplication of object {0} at offset {1} requested by agent {2}", originalPrim, offset, AgentID);
            SceneObjectGroup original = GetGroupByPrim(originalPrimID);
            if (original != null)
            {
                if (m_parentScene.Permissions.CanDuplicateObject(original.PartCount, original.UUID, AgentID, original.AbsolutePosition))
                {
                    ScenePresence sp;
                    if (!TryGetAvatar(AgentID, out sp)) sp = null;
                    UUID ActiveGroupID = UUID.Zero;
                    IClientAPI remoteClient = null;
                    if (sp != null)
                        remoteClient = sp.ControllingClient;
                    if (remoteClient  != null)
                        ActiveGroupID = remoteClient.ActiveGroupId;

                    // Clone it but don't change owner yet (if required).
                    SceneObjectGroup copy = original.Copy(original.OwnerID, original.GroupID, true);
                    copy.AbsolutePosition = copy.AbsolutePosition + offset;
                    copy.ResetInstance(true, true, UUID.Zero);

                    //TODO:  VIOLATES DRY: This is super sucky and I added it to fix a bug of copied objects not
                    //getting accounted right.  But the real answer is to go through AddSceneObject and
                    //determine why just calling would or would not be appropriate
                    lock (m_dictionary_lock)
                    {
                        Entities.Add(copy);
                        foreach (SceneObjectPart part in copy.GetParts())
                        {
                            SceneObjectPartsByFullID[part.UUID] = part;
                            SceneObjectPartsByLocalID[part.LocalId] = part;
                        }
                    }

                    // Since we copy from a source group that is in selected
                    // state, but the copy is shown deselected in the viewer,
                    // We need to clear the selection flag here, else that
                    // prim never gets persisted at all. The client doesn't
                    // think it's selected, so it will never send a deselect...
                    copy.SetUnselectedForCopy();

                    m_numPrim += copy.PartCount;

                    if (copy.OwnerID != AgentID)
                        copy.ChangeOwner(AgentID, GroupID);

                    if (rot != Quaternion.Identity)
                    {
                        copy.UpdateGroupRotation(rot, true);
                    }

                    copy.CreateScriptInstances(0, ScriptStartFlags.None, m_parentScene.DefaultScriptEngine, 0, null);
                    copy.HasGroupChanged = true;
                    if (remoteClient != null)
                        copy.GetProperties(remoteClient);
                    copy.ScheduleGroupForFullUpdate(PrimUpdateFlags.ForcedFullUpdate);

                    if (OnObjectDuplicate != null)
                        OnObjectDuplicate(original, copy);

                    // Signal a new object in the scene 
                    m_parentScene.EventManager.TriggerObjectAddedToScene(copy);

                    // Taint the map tile if qualifying.
                    foreach (SceneObjectPart part in copy.GetParts())
                    {
                        m_parentScene.MarkMapTileTainted(part);
                    }

                    return copy;
                }
            }
            else
            {
                m_log.WarnFormat("[SCENE]: Attempted to duplicate nonexistant prim id {0}", GroupID);
            }

            return null;
        }

        /// <summary>
        /// Calculates the distance between two Vector3s
        /// </summary>
        /// <param name="v1"></param>
        /// <param name="v2"></param>
        /// <returns></returns>
        protected internal float Vector3Distance(Vector3 v1, Vector3 v2)
        {
            // We don't really need the double floating point precision...
            // so casting it to a single

            return
                (float)
                Math.Sqrt((v1.X - v2.X) * (v1.X - v2.X) + (v1.Y - v2.Y) * (v1.Y - v2.Y) + (v1.Z - v2.Z) * (v1.Z - v2.Z));
        }

        #endregion
    }
}
