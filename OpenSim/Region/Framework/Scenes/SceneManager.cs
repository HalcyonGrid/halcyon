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
using System.Net;
using System.Reflection;
using OpenMetaverse;
using log4net;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using System.Text;
using System.IO;

namespace OpenSim.Region.Framework.Scenes
{
    public delegate void RestartSim(RegionInfo thisregion);

    public class SceneOwnerCounts
    {
        public string OwnerName;
        public int TotalObjects;
        public int TotalPrims;
    }

    /// <summary>
    /// Manager for adding, closing and restarting scenes.
    /// </summary>
    public class SceneManager
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public event RestartSim OnRestartSim;

        private readonly List<Scene> m_localScenes;
        private Scene m_currentScene = null;

        public List<Scene> Scenes
        {
            get { return m_localScenes; }
        }

        public Scene CurrentScene
        {
            get { return m_currentScene; }
        }

        public Scene CurrentOrFirstScene
        {
            get
            {
                if (m_currentScene == null)
                {
                    if (m_localScenes.Count > 0)
                    {
                        return m_localScenes[0];
                    }
                    else
                    {
                        return null;
                    }
                }
                else
                {
                    return m_currentScene;
                }
            }
        }

        public SceneManager()
        {
            m_localScenes = new List<Scene>();
        }

        public void Close()
        {
            // collect known shared modules in sharedModules
            Dictionary<string, IRegionModule> sharedModules = new Dictionary<string, IRegionModule>();
            for (int i = 0; i < m_localScenes.Count; i++)
            {
                // extract known shared modules from scene
                foreach (string k in m_localScenes[i].Modules.Keys)
                {
                    if (m_localScenes[i].Modules[k].IsSharedModule &&
                        !sharedModules.ContainsKey(k))
                        sharedModules[k] = m_localScenes[i].Modules[k];
                }
                // close scene/region
                m_localScenes[i].Close();
            }

            // all regions/scenes are now closed, we can now safely
            // close all shared modules
            foreach (IRegionModule mod in sharedModules.Values)
            {
                mod.Close();
            }
        }

        public void Close(Scene cscene)
        {
            if (m_localScenes.Contains(cscene))
            {
                for (int i = 0; i < m_localScenes.Count; i++)
                {
                    if (m_localScenes[i].Equals(cscene))
                    {
                        m_localScenes[i].Close();
                    }
                }
            }
        }

        public void Add(Scene scene)
        {
            scene.OnRestart += HandleRestart;
            m_localScenes.Add(scene);
        }

        public void HandleRestart(RegionInfo rdata)
        {
            m_log.Error("[SCENEMANAGER]: Got Restart message for region:" + rdata.RegionName + " Sending up to main");
            int RegionSceneElement = -1;
            for (int i = 0; i < m_localScenes.Count; i++)
            {
                if (rdata.RegionName == m_localScenes[i].RegionInfo.RegionName)
                {
                    RegionSceneElement = i;
                }
            }

            // Now we make sure the region is no longer known about by the SceneManager
            // Prevents duplicates.

            if (RegionSceneElement >= 0)
            {
                m_localScenes.RemoveAt(RegionSceneElement);
            }

            // Send signal to main that we're restarting this sim.
            OnRestartSim(rdata);
        }

        public void SendSimOnlineNotification(ulong regionHandle)
        {
            RegionInfo Result = null;

            for (int i = 0; i < m_localScenes.Count; i++)
            {
                if (m_localScenes[i].RegionInfo.RegionHandle == regionHandle)
                {
                    // Inform other regions to tell their avatar about me
                    Result = m_localScenes[i].RegionInfo;
                }
            }
            if (Result != null)
            {
                for (int i = 0; i < m_localScenes.Count; i++)
                {
                    if (m_localScenes[i].RegionInfo.RegionHandle != regionHandle)
                    {
                        // Inform other regions to tell their avatar about me
                        //m_localScenes[i].OtherRegionUp(Result);
                    }
                }
            }
            else
            {
                m_log.Error("[REGION]: Unable to notify Other regions of this Region coming up");
            }
        }

        /// <summary>
        /// Save the prims in the current scene to an xml file in OpenSimulator's original 'xml' format
        /// </summary>
        /// <param name="filename"></param>
        public void SaveCurrentSceneToXml(string filename)
        {
            IRegionSerializerModule serializer = CurrentOrFirstScene.RequestModuleInterface<IRegionSerializerModule>();
            if (serializer != null)            
                serializer.SavePrimsToXml(CurrentOrFirstScene, filename);
        }

        /// <summary>
        /// Load an xml file of prims in OpenSimulator's original 'xml' file format to the current scene
        /// </summary>
        /// <param name="filename"></param>
        /// <param name="generateNewIDs"></param>
        /// <param name="loadOffset"></param>
        public void LoadCurrentSceneFromXml(string filename, bool generateNewIDs, Vector3 loadOffset)
        {
            IRegionSerializerModule serializer = CurrentOrFirstScene.RequestModuleInterface<IRegionSerializerModule>();
            if (serializer != null)            
                serializer.LoadPrimsFromXml(CurrentOrFirstScene, filename, generateNewIDs, loadOffset);
        }

        /// <summary>
        /// Save the prims in the current scene to an xml file in OpenSimulator's current 'xml2' format
        /// </summary>
        /// <param name="filename"></param>
        public void SaveCurrentSceneToXml2(string filename)
        {
            IRegionSerializerModule serializer = CurrentOrFirstScene.RequestModuleInterface<IRegionSerializerModule>();
            if (serializer != null)            
                serializer.SavePrimsToXml2(CurrentOrFirstScene, filename);
        }

        public void SaveNamedPrimsToXml2(string primName, string filename)
        {
            IRegionSerializerModule serializer = CurrentOrFirstScene.RequestModuleInterface<IRegionSerializerModule>();
            if (serializer != null)               
                serializer.SaveNamedPrimsToXml2(CurrentOrFirstScene, primName, filename);
        }

        /// <summary>
        /// Load an xml file of prims in OpenSimulator's current 'xml2' file format to the current scene
        /// </summary>
        public void LoadCurrentSceneFromXml2(string filename)
        {
            IRegionSerializerModule serializer = CurrentOrFirstScene.RequestModuleInterface<IRegionSerializerModule>();
            if (serializer != null)              
                serializer.LoadPrimsFromXml2(CurrentOrFirstScene, filename);
        }

        /// <summary>
        /// Load an xml file of prims in OpenSimulator's current 'xml2' file format to the current scene
        /// </summary>
        /// <summary>
        /// Scans objects in an OAR file for creator IDs to save for assets.
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="saveCreators">true to save in database</param>
        public void ScanSceneForCreators(string filename)
        {
            IRegionArchiverModule archiver = CurrentOrFirstScene.RequestModuleInterface<IRegionArchiverModule>();
            if (archiver != null)
                archiver.ScanArchiveForAssetCreatorIDs(filename);
        }

        /// <summary>
        /// Save the current scene to an OpenSimulator archive.  This archive will eventually include the prim's assets
        /// as well as the details of the prims themselves.
        /// </summary>
        /// <param name="filename"></param>
        public void SaveCurrentSceneToArchive(string filename, bool storeAssets)
        {
            IRegionArchiverModule archiver = CurrentOrFirstScene.RequestModuleInterface<IRegionArchiverModule>();
            if (archiver != null)
                archiver.ArchiveRegion(filename, storeAssets);
        }

        /// <summary>
        /// Sets the debug level on loading or scanning an OpenSim archive.
        /// </summary>
        /// <param name="level"></param>
        public void SetOARDebug(int level)
        {
            IRegionArchiverModule archiver = CurrentOrFirstScene.RequestModuleInterface<IRegionArchiverModule>();
            if (archiver != null)
                archiver.SetDebug(level);
        }

        /// <summary>
        /// Load an OpenSim archive into the current scene.  This will load both the shapes of the prims and upload
        /// their assets to the asset service.
        /// </summary>
        /// <param name="filename"></param>
        public void LoadArchiveToCurrentScene(string filename, bool allowUserReassignment, bool ignoreErrors, HashSet<UUID> allowedUUIDs)
        {
            IRegionArchiverModule archiver = CurrentOrFirstScene.RequestModuleInterface<IRegionArchiverModule>();
            if (archiver != null)            
                archiver.DearchiveRegion(filename, allowUserReassignment, ignoreErrors, allowedUUIDs);
        }

        public string SaveCurrentSceneMapToXmlString()
        {
            return CurrentOrFirstScene.Heightmap.SaveToXmlString();
        }

        public void LoadCurrenSceneMapFromXmlString(string mapData)
        {
            CurrentOrFirstScene.Heightmap.LoadFromXmlString(mapData);
        }

        public void SendCommandToPluginModules(string[] cmdparams)
        {
            ForEachCurrentScene(delegate(Scene scene) { scene.SendCommandToPlugins(cmdparams); });
        }

        public void SetBypassPermissionsOnCurrentScene(bool bypassPermissions)
        {
            ForEachCurrentScene(delegate(Scene scene) { scene.Permissions.SetBypassPermissions(bypassPermissions); });
        }

        private void ForEachCurrentScene(Action<Scene> func)
        {
            if (m_currentScene == null)
            {
                m_localScenes.ForEach(func);
            }
            else
            {
                func(m_currentScene);
            }
        }

        public void RestartCurrentScene()
        {
            ForEachCurrentScene(delegate(Scene scene) { scene.RestartNow(); });
        }

        public void BackupCurrentScene()
        {
            ForEachCurrentScene(delegate(Scene scene) { scene.Backup(true); });
        }

        public bool TrySetCurrentScene(string regionName)
        {
            if ((String.Compare(regionName, "root") == 0) 
                || (String.Compare(regionName, "..") == 0)
                || (String.Compare(regionName, "/") == 0))
            {
                m_currentScene = null;
                return true;
            }
            else
            {
                foreach (Scene scene in m_localScenes)
                {
                    if (String.Compare(scene.RegionInfo.RegionName, regionName, true) == 0)
                    {
                        m_currentScene = scene;
                        return true;
                    }
                }

                return false;
            }
        }

        public bool TrySetCurrentScene(UUID regionID)
        {
            m_log.Debug("Searching for Region: '" + regionID + "'");

            foreach (Scene scene in m_localScenes)
            {
                if (scene.RegionInfo.RegionID == regionID)
                {
                    m_currentScene = scene;
                    return true;
                }
            }

            return false;
        }

        public bool TryGetScene(string regionName, out Scene scene)
        {
            foreach (Scene mscene in m_localScenes)
            {
                if (String.Compare(mscene.RegionInfo.RegionName, regionName, true) == 0)
                {
                    scene = mscene;
                    return true;
                }
            }
            scene = null;
            return false;
        }

        public bool TryGetScene(UUID regionID, out Scene scene)
        {
            foreach (Scene mscene in m_localScenes)
            {
                if (mscene.RegionInfo.RegionID == regionID)
                {
                    scene = mscene;
                    return true;
                }
            }
            
            scene = null;
            return false;
        }

        public bool TryGetScene(uint locX, uint locY, out Scene scene)
        {
            foreach (Scene mscene in m_localScenes)
            {
                if (mscene.RegionInfo.RegionLocX == locX &&
                    mscene.RegionInfo.RegionLocY == locY)
                {
                    scene = mscene;
                    return true;
                }
            }
            
            scene = null;
            return false;
        }

        public bool TryGetScene(IPEndPoint ipEndPoint, out Scene scene)
        {
            foreach (Scene mscene in m_localScenes)
            {
                if ((mscene.RegionInfo.InternalEndPoint.Equals(ipEndPoint.Address)) &&
                    (mscene.RegionInfo.InternalEndPoint.Port == ipEndPoint.Port))
                {
                    scene = mscene;
                    return true;
                }
            }
            
            scene = null;
            return false;
        }

        /// <summary>
        /// Set the debug packet level on the current scene.  This level governs which packets are printed out to the
        /// console.
        /// </summary>
        /// <param name="newDebug"></param>
        public void SetDebugPacketLevelOnCurrentScene(int newDebug)
        {
            ForEachCurrentScene(delegate(Scene scene)
                                {
                                    List<ScenePresence> scenePresences = scene.GetScenePresences();

                                    foreach (ScenePresence scenePresence in scenePresences)
                                    {
                                        //if (!scenePresence.IsChildAgent)
                                        //{
                                            m_log.ErrorFormat("Packet debug for {0} {1} set to {2}",
                                                              scenePresence.Firstname,
                                                              scenePresence.Lastname,
                                                              newDebug);

                                            scenePresence.ControllingClient.SetDebugPacketLevel(newDebug);
                                        //}
                                    }
                                });
        }

        /// <summary>
        /// Set the debug crossings level on the current scene(s).  This level governs the level of debug info printed out to the
        /// console during a crossing.
        /// </summary>
        /// <param name="newDebug"></param>
        public void SetDebugCrossingsLevelOnCurrentScene(int newDebug)
        {
            ForEachCurrentScene(delegate(Scene scene)
            {
                m_log.ErrorFormat("Crossings debug for {0} set to {1}",
                    scene.RegionInfo.RegionName, newDebug.ToString());
                scene.DebugCrossingsLevel = newDebug;
            });
        }

        public List<ScenePresence> GetCurrentSceneAvatars()
        {
            List<ScenePresence> avatars = new List<ScenePresence>();

            ForEachCurrentScene(delegate(Scene scene)
            {
                List<ScenePresence> scenePresences = scene.GetScenePresences();

                foreach (ScenePresence scenePresence in scenePresences)
                {
                    if (!scenePresence.IsChildAgent)
                    {
                        avatars.Add(scenePresence);
                    }
                }
            });

            return avatars;
        }

        public Dictionary<UUID,SceneOwnerCounts> GetCurrentSceneOwnerCounts()
        {
            Dictionary<UUID,SceneOwnerCounts> SOGTable = new Dictionary<UUID,SceneOwnerCounts>();
            foreach (Scene scene in m_localScenes)
            {
                List<EntityBase> EntityList = scene.GetEntities();
                foreach (EntityBase ent in EntityList)
                {
                    if (ent is SceneObjectGroup)
                    {
                        SceneObjectGroup SOG = (SceneObjectGroup)ent;
                        if (SOGTable.ContainsKey(SOG.OwnerID))
                        {
                            SceneOwnerCounts counts = SOGTable[SOG.OwnerID];
                            counts.TotalObjects++;
                            counts.TotalPrims += SOG.LandImpact;
                            SOGTable[SOG.OwnerID] = counts;
                        }
                        else
                        {
                            SceneOwnerCounts counts = new SceneOwnerCounts();
                            UserProfileData profile = scene.CommsManager.UserService.GetUserProfile(SOG.OwnerID);
                            if (profile == null)
                                counts.OwnerName = "(Group-deeded, or unknown user)";
                            else
                                counts.OwnerName = profile.Name;
                            counts.TotalObjects = 1;
                            counts.TotalPrims = SOG.LandImpact;
                            SOGTable[SOG.OwnerID] = counts;
                        }
                    }
                }
            }
            return SOGTable;
        }

        private bool nukeRunning = false;
        public void NukeObjectsOwnedBy(UUID OwnerID)
        {
            if (nukeRunning) return;

            try
            {
                nukeRunning = true;
                foreach (Scene scene in m_localScenes)
                {
                    // first disable physics to ensure CPU is available
                    bool usePhysics = scene.PhysicsScene.Simulating;
                    scene.PhysicsScene.Simulating = false;
                    // anti-griefer: stop further rezzing including attaches.
                    scene.AddBadUser(OwnerID);  // lasts 1 hour

                    try
                    {
                        List<EntityBase> EntityList = scene.GetEntities();
                        foreach (EntityBase ent in EntityList)
                        {
                            if (ent is SceneObjectGroup)
                            {
                                SceneObjectGroup SOG = (SceneObjectGroup)ent;
                                // Allow UUID.Zero to represent a wildcard for all owners.
                                if ((OwnerID == SOG.OwnerID) || ((OwnerID == UUID.Zero) && !SOG.IsAttachment))
                                    scene.DeleteSceneObject(SOG, false, false, true);
                            }
                        }
                    }
                    finally
                    {
                        // don't force a region restart to reenable... and restore physics
                        scene.PhysicsScene.Simulating = usePhysics;
                    }
                }
            }
            finally
            {
                nukeRunning = false;
            }
        }

        private bool blacklistRunning = false;
        public enum BlacklistOp : int { Owner, Creator, Name, User, Remove, Clear, Show };
        public void BlacklistOperation(BlacklistOp operation, string param, string param2)
        {
            if (blacklistRunning) return;

            // In cases where both are supported, if targetName is empty, targetID is used.
            string targetName = param;
            UUID targetID = UUID.Zero;

            try
            {
                blacklistRunning = true;
                bool nukeObjects;

                // BlacklistTarget.User is like BlacklistTarget.Owner except that it also prevents the user from entering the region.
                switch (operation)
                {
                    case BlacklistOp.User:
                    case BlacklistOp.Owner:
                        // accepts either UUID or First Last
                        if (String.IsNullOrEmpty(param2))
                        {
                            if (!UUID.TryParse(param, out targetID))
                            {
                                m_log.Warn("You must specify either a UUID or name (First Last).");
                                return;
                            }
                        }
                        else
                        {
                            // name was specified
                            UserProfileData profile = m_localScenes[0].CommsManager.UserService.GetUserProfile(param, param2);
                            if (profile == null)
                            {
                                m_log.WarnFormat("Could not find a user named '{0} {1}'.", param, param2);
                                return;
                            }
                            targetID = profile.ID;
                        }
                        nukeObjects = true;
                        break;
                    case BlacklistOp.Creator:
                        if (!UUID.TryParse(param, out targetID))
                        {
                            m_log.Warn("That command requires a UUID.");
                            return;
                        }
                        nukeObjects = true;
                        break;
                    case BlacklistOp.Name:
                        targetName = param.Trim();
                        nukeObjects = true;
                        break;
                    case BlacklistOp.Remove:
                        if (UUID.TryParse(param, out targetID))
                        {
                            targetName = String.Empty;
                        }
                        else
                        {
                            targetID = UUID.Zero;
                        }
                        nukeObjects = false;
                        break;
                    case BlacklistOp.Clear:
                    case BlacklistOp.Show:
                        // nothing to parse
                        nukeObjects = false;
                        break;
                    default:
                        return;
                }

                foreach (Scene scene in m_localScenes)
                {
                    if (operation == BlacklistOp.Show)
                    {
                        scene.BlacklistShow();
                        return;
                    }

                    switch (operation) 
                    {
                        case BlacklistOp.Creator:
                            scene.AddBlacklistedCreator(targetID);
                            break;
                        case BlacklistOp.Owner:
                            scene.AddBlacklistedOwner(targetID);
                            // also temporarily stop further rezzing including attaches.
                            scene.AddBadUser(targetID);
                            break;
                        case BlacklistOp.Name:
                            scene.AddBlacklistedName(targetName);
                            break;
                        case BlacklistOp.User:
                            scene.AddBlacklistedUser(targetID);
                            break;
                        case BlacklistOp.Remove:
                            if (!String.IsNullOrEmpty(targetName))
                                scene.BlacklistRemove(targetName);
                            else
                                scene.BlacklistRemove(targetID);
                            break;
                        case BlacklistOp.Clear:
                            scene.BlacklistClear();
                            break;
                    }

                    if (nukeObjects)
                    {
                        // first disable physics to ensure CPU is available
                        bool usePhysics = scene.PhysicsScene.Simulating;
                        scene.PhysicsScene.Simulating = false;

                        try
                        {
                            List<EntityBase> EntityList = scene.GetEntities();
                            foreach (EntityBase ent in EntityList)
                            {
                                if (ent is SceneObjectGroup)
                                {
                                    SceneObjectGroup SOG = (SceneObjectGroup)ent;
                                    bool match = false;
                                    switch (operation)
                                    {
                                        case BlacklistOp.Creator:
                                            SOG.ForEachPart(delegate(SceneObjectPart part)
                                                {
                                                    match |= (part.CreatorID == targetID);
                                                });
                                            break;
                                        case BlacklistOp.Owner:
                                        case BlacklistOp.User:
                                            match = (SOG.OwnerID == targetID);
                                            break;
                                        case BlacklistOp.Name:
                                            match = SOG.Name.Trim().StartsWith(targetName, StringComparison.InvariantCultureIgnoreCase);
                                            break;
                                    }
                                    if (match)
                                        scene.DeleteSceneObject(SOG, false, false, true);
                                }
                            }
                        }
                        finally
                        {
                            // don't force a region restart to reenable... and restore physics
                            scene.PhysicsScene.Simulating = usePhysics;
                        }
                    }
                }
            }
            finally
            {
                blacklistRunning = false;
            }
        }

        private string SimpleLocation(Scene scene, SceneObjectPart part)
        {
            int x = (int)part.AbsolutePosition.X;
            int y = (int)part.AbsolutePosition.Y;
            int z = (int)part.AbsolutePosition.Z;
            return scene.RegionInfo.RegionName + "/" + x.ToString() + "/" + y.ToString() + "/" + z.ToString();
        }

        private void DumpPart(Scene scene, SceneObjectPart part)
        {
            if (part.ParentID == 0)
            {
                m_log.InfoFormat("[Show]: Object {0} [{1}] '{2}' at {3}", part.LocalId.ToString(), part.UUID.ToString(), part.Name, SimpleLocation(scene, part));
            } else
            {
                SceneObjectPart root = part.ParentGroup.RootPart;
                m_log.InfoFormat("[Show]: Child prim {0} [{1}] '{2}' at {3}", part.LocalId.ToString(), part.UUID.ToString(), part.Name, SimpleLocation(scene, part));
                m_log.InfoFormat("[Show]: Root prim {0} [{1}] '{2}' at {3}", root.LocalId.ToString(), root.UUID.ToString(), root.Name, SimpleLocation(scene, root));
            }
        }

        public void ShowObject(string[] showParams)
        {
            if (showParams.Length > 1)
            {
                string arg = showParams[1];
                UUID uuid = UUID.Zero;
                uint localID = 0;

                if (UUID.TryParse(arg, out uuid))
                {
                    foreach (Scene scene in m_localScenes)
                    {
                        SceneObjectPart part = scene.GetSceneObjectPart(uuid);
                        if (part != null)
                        {
                            DumpPart(scene, part);
                            return;
                        }
                    }
                    m_log.InfoFormat("Error: Could not find an part with UUID: {0}", uuid.ToString());
                    return;
                }
                if (uint.TryParse(arg, out localID))
                {
                    foreach (Scene scene in m_localScenes)
                    {
                        SceneObjectPart part = scene.GetSceneObjectPart(localID);
                        if (part != null)
                        {
                            DumpPart(scene, part);
                            return;
                        }
                    }
                    m_log.InfoFormat("Error: Could not find an part with local ID: {0}", localID.ToString());
                    return;
                }
            }

            m_log.Info("Error: expected either a UUID or a local ID for an object.");
        }

        public List<ScenePresence> GetCurrentScenePresences()
        {
            List<ScenePresence> presences = new List<ScenePresence>();

            ForEachCurrentScene(delegate(Scene scene)
            {
                List<ScenePresence> scenePresences = scene.GetScenePresences();
                presences.AddRange(scenePresences);
            });

            return presences;
        }

        public RegionInfo GetRegionInfo(ulong regionHandle)
        {
            foreach (Scene scene in m_localScenes)
            {
                if (scene.RegionInfo.RegionHandle == regionHandle)
                {
                    return scene.RegionInfo;
                }
            }

            return null;
        }

        public void ForceCurrentSceneClientUpdate()
        {
            ForEachCurrentScene(delegate(Scene scene) { scene.ForceClientUpdate(); });
        }

        public void HandleEditCommandOnCurrentScene(string[] cmdparams)
        {
            ForEachCurrentScene(delegate(Scene scene) { scene.HandleEditCommand(cmdparams); });
        }

        public bool TryGetAvatar(UUID avatarId, out ScenePresence avatar)
        {
            foreach (Scene scene in m_localScenes)
            {
                if (scene.TryGetAvatar(avatarId, out avatar))
                {
                    return true;
                }
            }

            avatar = null;
            return false;
        }

        public bool TryGetAvatarsScene(UUID avatarId, out Scene scene)
        {
            ScenePresence avatar = null;
            foreach (Scene mScene in m_localScenes)
            {
                if (mScene.TryGetAvatar(avatarId, out avatar))
                {
                    scene = mScene;
                    return true;
                }
            }

            scene = null;
            return false;
        }

        public void CloseScene(Scene scene)
        {
            m_localScenes.Remove(scene);
            scene.Close();
        }

        public bool TryGetAvatarByName(string avatarName, out ScenePresence avatar)
        {
            foreach (Scene scene in m_localScenes)
            {
                if (scene.TryGetAvatarByName(avatarName, out avatar))
                {
                    return true;
                }
            }

            avatar = null;
            return false;
        }

        public void ForEachScene(Action<Scene> action)
        {
            m_localScenes.ForEach(action);
        }

        public void CacheJ2kDecode(int threads)
        {
            if (threads < 1) threads = 1;

            IJ2KDecoder m_decoder = m_localScenes[0].RequestModuleInterface<IJ2KDecoder>();

            List<UUID> assetRequestList = new List<UUID>();

            #region AssetGathering!
            foreach (Scene scene in m_localScenes)
            {
                List<EntityBase> entitles = scene.GetEntities();
                foreach (EntityBase entity in entitles)
                {
                    if (entity is SceneObjectGroup)
                    {
                        SceneObjectGroup sog = (SceneObjectGroup) entity;
                        foreach (SceneObjectPart part in sog.GetParts())
                        {
                            if (part.Shape != null)
                            {
                                OpenMetaverse.Primitive.TextureEntry te = part.Shape.Textures;
                                if (te.DefaultTexture != null) // this has been null for some reason...
                                {
                                    if (te.DefaultTexture.TextureID != UUID.Zero)
                                        assetRequestList.Add(te.DefaultTexture.TextureID);
                                }
                                for (int i=0; i<te.FaceTextures.Length; i++)
                                {
                                    if (te.FaceTextures[i] != null)
                                    {
                                        if (te.FaceTextures[i].TextureID != UUID.Zero)
                                        {
                                            assetRequestList.Add(te.FaceTextures[i].TextureID);
                                        }
                                    }
                                }
                                if (part.Shape.SculptTexture != UUID.Zero)
                                {
                                    assetRequestList.Add(part.Shape.SculptTexture);
                                }

                            }
                        }
                    }
                }
            }
            #endregion

            int entries_per_thread = (assetRequestList.Count / threads) + 1;

            UUID[] arrAssetRequestList = assetRequestList.ToArray();

            List<UUID[]> arrvalus = new List<UUID[]>();

            //split into separate arrays
            for (int j = 0; j < threads; j++)
            {
                List<UUID> val = new List<UUID>();

                for (int k = j * entries_per_thread; k < ((j + 1) * entries_per_thread); k++)
                {
                    if (k < arrAssetRequestList.Length)
                    {
                        val.Add(arrAssetRequestList[k]);
                    }

                }
                arrvalus.Add(val.ToArray());
            }

            for (int l = 0; l < arrvalus.Count; l++)
            {
                DecodeThreadContents threadworkItem = new DecodeThreadContents();
                threadworkItem.sn = m_localScenes[0];
                threadworkItem.j2kdecode = m_decoder;
                threadworkItem.arrassets = arrvalus[l];

                System.Threading.Thread decodethread =
                    new System.Threading.Thread(new System.Threading.ParameterizedThreadStart(threadworkItem.run));
                
                threadworkItem.SetThread(decodethread);

                decodethread.Priority = System.Threading.ThreadPriority.Lowest;
                decodethread.Name = "J2kCacheDecodeThread_" + l + 1;
                ThreadTracker.Add(decodethread);
                decodethread.Start();
                
            }
        }

        public void ShowCollisions(string[] showParams)
        {
            foreach (Scene scene in m_localScenes)
            {
                scene.PhysicsScene.DumpCollisionInfo();
            }
        }

        public void ShowUpdates(string[] showParams)
        {
            if (showParams.Length != 2)
            {
                m_log.InfoFormat("Usage: show updates [delta/total]");
                return;
            }

            ForEachScene(delegate(Scene scene)
            {
                m_log.Info(scene.GetTopUpdatesOutput(showParams));
            });
        }

        private bool HasScriptEngine(Scene scene)
        {
            IScriptModule[] engines = scene.RequestModuleInterfaces<IScriptModule>();
            if (engines.Length == 0)
                return false;

            if (engines[0] == null)   // happens under Phlox if disabled
                return false;

            return true;
        }

        public void SaveExplicitOar(string regionName, string oarFilename, bool storeAssets)
        {
            Scene targetScene = this.FindSceneByName(regionName);

            if (targetScene == null) throw new Exception(String.Format("Region {0} was not found", regionName));

            if (!HasScriptEngine(targetScene))
                m_log.Warn("[SCENE]: Warning: Script engine disabled. No script states will be saved in OAR file.");

            IRegionArchiverModule archiver = targetScene.RequestModuleInterface<IRegionArchiverModule>();
            if (archiver != null)
            {
                try
                {
                    archiver.ArchiveRegion(oarFilename, storeAssets);

                    //create a file for external code to know we're done writing this OAR
                    //cheap and fragile IPC, but Im not yet taking the time to allow interaction
                    //by anything but the console.
                    using (FileStream completedfile = System.IO.File.Create(OarStatusNameFromRegionName(regionName)))
                    {
                        completedfile.WriteByte(1);
                        completedfile.Close();
                    }
                }
                catch (Exception e)
                {
                    using (FileStream completedfile = System.IO.File.Create(OarStatusNameFromRegionName(regionName)))
                    {
                        completedfile.WriteByte(0);
                        byte[] errorMessage = System.Text.Encoding.UTF8.GetBytes(e.Message);
                        completedfile.Write(errorMessage, 0, errorMessage.Length);
                        completedfile.Close();
                    }

                    throw;
                }
            }
        }

        private static string OarStatusNameFromRegionName(string regionName)
        {
            return regionName.Replace(" ", String.Empty).Replace("\'", String.Empty) + ".oarstatus";
        }

        public Scene FindSceneByName(string name)
        {
            Scene targetScene = null;
            this.ForEachScene(
                delegate(Scene scene)
                {
                    if (scene.RegionInfo.RegionName == name)
                    {
                        targetScene = scene;
                    }
                }
            );

            return targetScene;
        }
    }
    
    public class DecodeThreadContents
    {
        public Scene sn;
        public UUID[] arrassets;
        public IJ2KDecoder j2kdecode;
        private System.Threading.Thread thisthread;

        public void run( object o)
        {
            for (int i=0;i<arrassets.Length;i++)
            {
                AssetBase ab = sn.CommsManager.AssetCache.GetAsset(arrassets[i], AssetRequestInfo.InternalRequest());
                if (ab != null && ab.Data != null)
                {
                    j2kdecode.Decode(arrassets[i], ab.Data);
                }
            }
            ThreadTracker.Remove(thisthread);
        }

        public void SetThread(System.Threading.Thread thr)
        {
            thisthread = thr;
        }
    }
}
