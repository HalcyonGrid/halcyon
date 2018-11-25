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
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Serialization;
using OpenSim.Framework.Serialization.External;
using OpenSim.Framework.Communications.Cache;
using OpenSim.Region.CoreModules.World.Terrain;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework;
using OpenSim.Data.SimpleDB;
using System.Data;
using Nini.Config;

namespace OpenSim.Region.CoreModules.World.Archiver
{
    /// <summary>
    /// Handles an individual archive read request
    /// </summary>
    public class ArchiveReadRequest
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly String ASSET_CREATORS = "asset_creators";

        private Scene m_scene;
        private Stream m_loadStream;
        private Guid m_requestId;
        private string m_errorMessage;
        private IGroupsModule m_GroupsModule;

        private ConnectionFactory _connFactory;
        private IConfigSource m_config;

        /// <value>
        /// Should the archive being loaded be merged with what is already on the region?
        /// </value>
        private bool m_merge;

        /// <summary>
        /// Should the dearchive process be allowed to reassign creators and previous users
        /// </summary>
        private bool m_allowUserReassignment;

        private bool m_skipErrorGroups = false;

        /// <summary>
        /// Used to cache lookups for valid uuids.
        /// </summary>
        private IDictionary<UUID, bool> m_validUserUuids =
            new Dictionary<UUID, bool>
            {
                {UUID.Zero, true},
                {new UUID("11111111-1111-0000-0000-000100bba000"), true} //the "mr opensim" user
            };

        public ArchiveReadRequest(IConfigSource config, Scene scene, string loadPath, bool merge, Guid requestId, bool allowUserReassignment, bool skipErrorGroups)
        {
            m_config = config;
            m_scene = scene;
            m_loadStream = new GZipStream(GetStream(loadPath), CompressionMode.Decompress);
            m_errorMessage = String.Empty;
            m_merge = merge;
            m_requestId = requestId;
            m_allowUserReassignment = allowUserReassignment;
            m_GroupsModule = scene.RequestModuleInterface<IGroupsModule>();
            m_skipErrorGroups = skipErrorGroups;
        }

        public ArchiveReadRequest(IConfigSource config, Scene scene, Stream loadStream, bool merge, Guid requestId, bool allowUserReassignment, bool skipErrorGroups)
        {
            m_config = config;
            m_scene = scene;
            m_loadStream = loadStream;
            m_merge = merge;
            m_requestId = requestId;
            m_allowUserReassignment = allowUserReassignment;
            m_GroupsModule = scene.RequestModuleInterface<IGroupsModule>();
            m_skipErrorGroups = skipErrorGroups;
        }

        /// <summary>
        /// Dearchive the region embodied in this request.
        /// </summary>
        public void DearchiveRegion(string optionsTable)
        {
            // The same code can handle dearchiving 0.1 and 0.2 OpenSim Archive versions
            DearchiveRegion0DotStar(optionsTable);
        }

        public bool NoCopyObjectOrContents(SceneObjectGroup target)
        {
            if (m_allowUserReassignment)
                return false;    // ignore no-copy permissions for "load oar"

            // "loadexplicit oar" and rezzed in-world object is no-copy
            return ((target.GetEffectivePermissions(true) & (uint)PermissionMask.Copy) != (uint)PermissionMask.Copy);
        }

        // checks and updates _connFactory member.
        private void InitConnFactory()
        {
            if (_connFactory != null)
                return;

            string connString = null;
            IConfig networkConfig = m_config.Configs["Startup"];
            if (networkConfig != null)
            {
                connString = networkConfig.GetString("core_connection_string", String.Empty);
            }

            if (String.IsNullOrWhiteSpace(connString))
                return;

            _connFactory = new ConnectionFactory("MySQL", connString);
        }

        Dictionary<UUID, int> GetUserContentOptions(string optionsTable)
        {
            InitConnFactory();

            Dictionary<UUID, int> optInTable = new Dictionary<UUID, int>();
            try
            {
                using (ISimpleDB conn = _connFactory.GetConnection())
                {
                    string query = "select UUID,optContent from " + optionsTable + " LIMIT 999999999";
                    using (IDataReader reader = conn.QueryAndUseReader(query))
                    {
                        while (reader.Read())
                        {
                            UUID uuid = new UUID(reader["uuid"].ToString());
                            int optContent = Convert.ToInt32(reader["optContent"]);
                            optInTable[uuid] = optContent;
                        }
                        reader.Close();
                    }
                }
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
            }
            return optInTable;
        }

        private Dictionary<UUID, UUID> GetAssetCreators()
        {
            InitConnFactory();

            Dictionary<UUID, UUID> assetCreatorsTable = new Dictionary<UUID, UUID>();
            try
            {
                using (ISimpleDB conn = _connFactory.GetConnection())
                {
                    string query = "select assetId,creatorId from " + ASSET_CREATORS + " LIMIT 999999999";
                    using (IDataReader reader = conn.QueryAndUseReader(query))
                    {
                        while (reader.Read())
                        {
                            UUID assetId = new UUID(reader["assetId"].ToString());
                            UUID creatorId = new UUID(reader["creatorId"].ToString());
                            assetCreatorsTable[assetId] = creatorId;
                        }
                        reader.Close();
                    }
                }
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
            }
            return assetCreatorsTable;
        }

        private void SaveAssetCreators(Dictionary<UUID,UUID> assetCreatorsTable)
        {
            InitConnFactory();

            try
            {
                using (ISimpleDB conn = _connFactory.GetConnection())
                {
                    string query = "INSERT INTO " + ASSET_CREATORS + 
                        " (assetId,creatorId) VALUES (?assetId,?creatorId) " +
                        "ON DUPLICATE KEY UPDATE creatorId = ?creatorId";
                    foreach (KeyValuePair<UUID,UUID> kvp in assetCreatorsTable)
                    {
                        Dictionary<string, object> parameters = new Dictionary<string, object>();
                        parameters["?assetId"] = kvp.Key.ToString();
                        parameters["?creatorId"] = kvp.Value.ToString();
                        conn.QueryNoResults(query, parameters);
                    }
                }
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
            }
        }

        private void ScanObjectForAssetCreatorIDs(IRegionSerializerModule serializer, string serializedSOG, bool saveCreators)
        {
            Dictionary<UUID, UUID> assetCreators = new Dictionary<UUID, UUID>();
            SceneObjectGroup sceneObject;
            try
            {
                sceneObject = serializer.DeserializeGroupFromXml2(serializedSOG);
                if (sceneObject == null)
                    return;

                foreach (SceneObjectPart part in sceneObject.GetParts())
                {
                    lock (part.TaskInventory)
                    {
                        TaskInventoryDictionary inv = part.TaskInventory;
                        foreach (KeyValuePair<UUID, TaskInventoryItem> kvp in inv)
                        {
                            TaskInventoryItem item = kvp.Value;

                            if (item.AssetID != UUID.Zero)
                            {
                                assetCreators[item.AssetID] = item.CreatorID;
                            }
                        }
                    }
                }
                if (saveCreators)
                    SaveAssetCreators(assetCreators);
            }
            catch (Exception e)
            {
                m_log.InfoFormat("[ARCHIVER]: Error while deserializing group: {0}", e);
            }
        }

        public void ScanArchiveForAssetCreatorIDs(bool saveCreators)
        {
            string filePath = "NONE";

            try
            {
                IRegionSerializerModule serializer = m_scene.RequestModuleInterface<IRegionSerializerModule>();
                TarArchiveReader archive = new TarArchiveReader(m_loadStream);
                TarArchiveReader.TarEntryType entryType;
                byte[] data;
                while ((data = archive.ReadEntry(out filePath, out entryType)) != null)
                {
                    if (TarArchiveReader.TarEntryType.TYPE_DIRECTORY == entryType)
                        continue;

                    if (filePath.StartsWith(ArchiveConstants.OBJECTS_PATH))
                    {
                        ScanObjectForAssetCreatorIDs(serializer, Encoding.UTF8.GetString(data), saveCreators);
                    }
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ARCHIVER]: Aborting creator scan with error in archive file {0}.  {1}", filePath, e);
            }
        }

        private void DearchiveRegion0DotStar(string optionsTable)
        {
            int successfulAssetRestores = 0;
            int failedAssetRestores = 0;
            List<string> serializedSceneObjects = new List<string>();
            string filePath = "NONE";
            Dictionary<UUID, int> optInTable = null;
            Dictionary<UUID, UUID> assetCreators = null;

            if (!String.IsNullOrWhiteSpace(optionsTable))
            {
                optInTable = GetUserContentOptions(optionsTable);
                assetCreators = GetAssetCreators();
            }

            try
            {
                TarArchiveReader archive = new TarArchiveReader(m_loadStream);
               
                byte[] data;
                TarArchiveReader.TarEntryType entryType;

                while ((data = archive.ReadEntry(out filePath, out entryType)) != null)
                {                    
                    //m_log.DebugFormat(
                    //    "[ARCHIVER]: Successfully read {0} ({1} bytes)", filePath, data.Length);
                    
                    if (TarArchiveReader.TarEntryType.TYPE_DIRECTORY == entryType)
                        continue;                    

                    if (filePath.StartsWith(ArchiveConstants.OBJECTS_PATH))
                    {
                        serializedSceneObjects.Add(Encoding.UTF8.GetString(data));
                    }
                    else if (filePath.StartsWith(ArchiveConstants.ASSETS_PATH))
                    {
                        if (LoadAsset(filePath, data, optInTable, assetCreators))
                            successfulAssetRestores++;
                        else
                            failedAssetRestores++;
                    }
                    else if (!m_merge && filePath.StartsWith(ArchiveConstants.TERRAINS_PATH))
                    {
                        LoadTerrain(filePath, data);
                    }
                    else if (!m_merge && filePath.StartsWith(ArchiveConstants.SETTINGS_PATH))
                    {
                        LoadRegionSettings(filePath, data);
                    }
                }

                //m_log.Debug("[ARCHIVER]: Reached end of archive");

                archive.Close();
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[ARCHIVER]: Aborting load with error in archive file {0}.  {1}", filePath, e);
                m_errorMessage += e.ToString();
                m_scene.EventManager.TriggerOarFileLoaded(m_requestId, m_errorMessage);
                return;
            }

            m_log.InfoFormat("[ARCHIVER]: Restored {0} assets", successfulAssetRestores);

            if (failedAssetRestores > 0)
            {
                m_log.ErrorFormat("[ARCHIVER]: Failed to load {0} assets", failedAssetRestores);
                m_errorMessage += String.Format("Failed to load {0} assets", failedAssetRestores);
            }

            // Reload serialized prims
            m_log.InfoFormat("[ARCHIVER]: Preparing {0} scene objects.  Please wait.", serializedSceneObjects.Count);

            IRegionSerializerModule serializer = m_scene.RequestModuleInterface<IRegionSerializerModule>();
            int sceneObjectsLoadedCount = 0;

            List<SceneObjectGroup> backupObjects = new List<SceneObjectGroup>();
            Dictionary<UUID, UUID> OriginalBackupIDs = new Dictionary<UUID, UUID>();

            bool objectFixingFailed = false;

            foreach (string serializedSceneObject in serializedSceneObjects)
            {
                SceneObjectGroup sceneObject;
                try
                {
                    sceneObject = serializer.DeserializeGroupFromXml2(serializedSceneObject);
                }
                catch (Exception e)
                {
                    m_log.InfoFormat("[ARCHIVER]: Error while deserializing group: {0}", e);
                    if (m_skipErrorGroups) continue;
                    else throw;
                }

                if (sceneObject == null)
                {
                    if (m_skipErrorGroups) continue;
                    else throw new Exception("Error while deserializing group");
                }

                if (!optInTable.ContainsKey(sceneObject.RootPart.CreatorID))
                    continue;   // filtered
                int optIn = optInTable[sceneObject.RootPart.CreatorID];
                switch (optIn)
                {
                    case 2: break; // allow the asset in so everyone can use it
                    case 1: if (sceneObject.RootPart.OwnerID == sceneObject.RootPart.CreatorID) break; else continue;   // allow the asset in so creator can use it
                    case 0: continue;   // asset is not allowed in
                    default: continue;  // unknown status, cannot assume opt-in
                }


                // For now, give all incoming scene objects new uuids.  This will allow scenes to be cloned
                // on the same region server and multiple examples a single object archive to be imported
                // to the same scene (when this is possible).
                UUID OldUUID = sceneObject.UUID;
                sceneObject.ResetIDs();
                // if sceneObject is no-copy, save the old ID with the new ID.
                OriginalBackupIDs[sceneObject.UUID] = OldUUID;

                // Try to retain the original creator/owner/lastowner if their uuid is present on this grid
                // otherwise, use the master avatar uuid instead
                UUID masterAvatarId = m_scene.RegionInfo.MasterAvatarAssignedUUID;

                if (m_scene.RegionInfo.EstateSettings.EstateOwner != UUID.Zero)
                    masterAvatarId = m_scene.RegionInfo.EstateSettings.EstateOwner;

                foreach (SceneObjectPart part in sceneObject.GetParts())
                {
                    if (!ResolveUserUuid(part.CreatorID))
                    {
                        m_log.WarnFormat("[ARCHIVER]: Could not resolve av/group ID {0} for object '{1}' part creator", part.CreatorID, sceneObject.Name);
                        // Don't fail to load an object owned by a valid user, just because a creator no longer exists in the DB. (We've seen this with some of YadNi's stuff.)
                        // objectFixingFailed = true;
                        // part.CreatorID = masterAvatarId;
                    }

                    if (!ResolveUserUuid(part.OwnerID))
                    {
                        m_log.WarnFormat("[ARCHIVER]: Could not resolve av/group ID {0} for object '{1}' part owner", part.OwnerID, sceneObject.Name);
                        objectFixingFailed = true;
                        part.OwnerID = masterAvatarId;
                    }

                    if (!ResolveUserUuid(part.LastOwnerID))
                    {
                        m_log.WarnFormat("[ARCHIVER]: Could not resolve av/group ID {0} for object '{1}' part last owner", part.LastOwnerID, sceneObject.Name);
                        objectFixingFailed = true;
                        part.LastOwnerID = masterAvatarId;
                    }


                    // Fix ownership/creator of inventory items
                    // Not doing so results in inventory items
                    // being no copy/no mod for everyone
                    lock (part.TaskInventory)
                    {
                        TaskInventoryDictionary inv = part.TaskInventory;
                        foreach (KeyValuePair<UUID, TaskInventoryItem> kvp in inv)
                        {
                            if (!ResolveUserUuid(kvp.Value.OwnerID))
                            {
                                m_log.WarnFormat("[ARCHIVER]: Could not resolve av/group ID {0} for object '{1}' inventory item owner", kvp.Value.OwnerID, sceneObject.Name);
                                objectFixingFailed = true;
                                kvp.Value.OwnerID = masterAvatarId;
                            }

                            if (!ResolveUserUuid(kvp.Value.CreatorID))
                            {
                                m_log.WarnFormat("[ARCHIVER]: Could not resolve av/group ID {0} for object '{1}' inventory item creator", kvp.Value.CreatorID, sceneObject.Name);
                                // Don't fail to load an object owned by a valid user, just because a creator no longer exists in the DB. (We've seen this with some of YadNi's stuff.)
                                // objectFixingFailed = true;
                                // kvp.Value.CreatorID = masterAvatarId;
                            }
                        }
                    }
                }

                backupObjects.Add(sceneObject);
            }

            if (objectFixingFailed && !m_allowUserReassignment)
            {
                m_log.Error("[ARCHIVER]: Could not restore scene objects. One or more avatar accounts not found.");
                return;
            }

            Dictionary<UUID, SceneObjectGroup> ExistingNoCopyObjects = new Dictionary<UUID,SceneObjectGroup>();
            if (!m_merge)
            {
                m_log.Info("[ARCHIVER]: Clearing all existing scene objects");
                m_scene.DeleteAllSceneObjectsExcept(delegate(SceneObjectGroup existingSOG)
                                {   // Return true if this object should be skipped in the delete.

                                    // Don't delete any no-copy objects.
                                    if (NoCopyObjectOrContents(existingSOG)) 
                                    {
                                        ExistingNoCopyObjects.Add(existingSOG.UUID, existingSOG);
                                        return true;
                                    }
                                    return false;
                                });
            }

            m_log.InfoFormat("[ARCHIVER]: Loading {0} scene objects.  Please wait.", serializedSceneObjects.Count);

            // sceneObject is the one from backup to restore to the scene
            foreach (SceneObjectGroup backupObject in backupObjects)
            {
                SceneObjectGroup existingObject = null;
                UUID originalUUID = OriginalBackupIDs[backupObject.UUID];
                // Don't restore any no-copy objects unless there was an existing matching UUID in the scene.
                if (ExistingNoCopyObjects.ContainsKey(originalUUID))
                    existingObject = ExistingNoCopyObjects[originalUUID];
                // existingSOG here means existing NO-COPY object, not deleted from scene above

                if (NoCopyObjectOrContents(backupObject))
                {
                    if ((existingObject != null) && !existingObject.IsAttachment)
                    {
                        // copy only position and rotation from backup
                        existingObject.Rotation = backupObject.Rotation;
                        existingObject.AbsolutePosition = backupObject.AbsolutePosition;
                    }
                    // don't restore no-copy items
                }
                else
                if (m_scene.AddRestoredSceneObject(backupObject, true, false))
                {
                    // this may have added 2nd copyable copy if existingObject is no-copy
                    sceneObjectsLoadedCount++;
                    backupObject.CreateScriptInstances(0, ScriptStartFlags.PostOnRez, m_scene.DefaultScriptEngine, 0, null);
                }
            }

            m_log.InfoFormat("[ARCHIVER]: Restored {0} scene objects to the scene", sceneObjectsLoadedCount);

            int ignoredObjects = serializedSceneObjects.Count - sceneObjectsLoadedCount;

            if (ignoredObjects > 0)
                m_log.WarnFormat("[ARCHIVER]: Ignored {0} scene objects that already existed in the scene", ignoredObjects);

            m_log.InfoFormat("[ARCHIVER]: Successfully loaded archive");

            m_scene.EventManager.TriggerOarFileLoaded(m_requestId, m_errorMessage);
        }

        /// <summary>
        /// Look up the given user id to check whether it's one that is valid for this grid.
        /// </summary>
        /// <param name="uuid"></param>
        /// <returns></returns>
        private bool ResolveUserUuid(UUID uuid)
        {
            if (!m_validUserUuids.ContainsKey(uuid))
            {
                try
                {
                    UserProfileData profile = m_scene.CommsManager.UserService.GetUserProfile(uuid);
                    if (profile != null)
                    {
                        m_validUserUuids.Add(uuid, true);
                    }
                    else
                    {
                        //what about group ids?
                        GroupRecord grpRec = m_GroupsModule.GetGroupRecord(uuid);

                        if (grpRec != null)
                        {
                            m_validUserUuids.Add(uuid, true);
                        }
                        else
                        {
                            m_validUserUuids.Add(uuid, false);
                        }
                    }
                }
                catch (UserProfileException)
                {
                    //what about group ids?
                    GroupRecord grpRec = m_GroupsModule.GetGroupRecord(uuid);

                    if (grpRec != null)
                    {
                        m_validUserUuids.Add(uuid, true);
                    }
                    else
                    {
                        m_validUserUuids.Add(uuid, false);
                    }
                }
            }

            if (m_validUserUuids[uuid])
                return true;
            else
                return false;
        }

        /// <summary>
        /// Load an asset
        /// </summary>
        /// <param name="assetFilename"></param>
        /// <param name="data"></param>
        /// <returns>true if asset was successfully loaded, false otherwise</returns>
        private bool LoadAsset(string assetPath, byte[] data, Dictionary<UUID, int> optInTable, Dictionary<UUID, UUID> assetCreators)
        {
            // Right now we're nastily obtaining the UUID from the filename
            string filename = assetPath.Remove(0, ArchiveConstants.ASSETS_PATH.Length);
            int i = filename.LastIndexOf(ArchiveConstants.ASSET_EXTENSION_SEPARATOR);

            if (i == -1)
            {
                m_log.ErrorFormat(
                    "[ARCHIVER]: Could not find extension information in asset path {0} since it's missing the separator {1}.  Skipping",
                    assetPath, ArchiveConstants.ASSET_EXTENSION_SEPARATOR);

                return false;
            }

            string extension = filename.Substring(i);
            string uuid = filename.Remove(filename.Length - extension.Length);

            if (ArchiveConstants.EXTENSION_TO_ASSET_TYPE.ContainsKey(extension))
            {
                sbyte assetType = ArchiveConstants.EXTENSION_TO_ASSET_TYPE[extension];

                //m_log.DebugFormat("[ARCHIVER]: Importing asset {0}, type {1}", uuid, assetType);

                AssetBase asset = new AssetBase(new UUID(uuid), String.Empty);
                asset.Type = assetType;
                asset.Data = data;

                if (optInTable != null)
                {
                    // this is a `load iwoar` command and we need to filter based on opt-in status
                    if (!assetCreators.ContainsKey(asset.FullID))
                        return false;
                    UUID creatorId = assetCreators[asset.FullID];
                    if (!optInTable.ContainsKey(creatorId))
                        return false;
                    int optIn = optInTable[creatorId];
                    switch (optIn)
                    {
                        case 2: break; // allow the asset in so everyone can use it
                        case 1: break; // allow the asset in so creator can use it
                        case 0: return false;   // asset is not allowed in
                        default: return false;  // unknown status, cannot assume opt-in
                    }
                }

                try
                {
                    m_scene.CommsManager.AssetCache.AddAsset(asset, AssetRequestInfo.InternalRequest());
                }
                catch (AssetServerException e)
                {
                    m_log.ErrorFormat("[ARCHIVER] Uploading asset {0} failed: {1}", uuid, e);
                }

                /**
                 * Create layers on decode for image assets.  This is likely to significantly increase the time to load archives so
                 * it might be best done when dearchive takes place on a separate thread
                if (asset.Type=AssetType.Texture)
                {
                    IJ2KDecoder cacheLayerDecode = scene.RequestModuleInterface<IJ2KDecoder>();
                    if (cacheLayerDecode != null)
                        cacheLayerDecode.syncdecode(asset.FullID, asset.Data);
                }
                */

                return true;
            }
            else
            {
                m_log.ErrorFormat(
                    "[ARCHIVER]: Tried to dearchive data with path {0} with an unknown type extension {1}",
                    assetPath, extension);

                return false;
            }
        }

        /// <summary>
        /// Load region settings data
        /// </summary>
        /// <param name="settingsPath"></param>
        /// <param name="data"></param>
        /// <returns>
        /// true if settings were loaded successfully, false otherwise
        /// </returns>
        private bool LoadRegionSettings(string settingsPath, byte[] data)
        {
            RegionSettings loadedRegionSettings;

            try
            {
                loadedRegionSettings = RegionSettingsSerializer.Deserialize(data);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[ARCHIVER]: Could not parse region settings file {0}.  Ignoring.  Exception was {1}",
                    settingsPath, e);
                return false;
            }

            RegionSettings currentRegionSettings = m_scene.RegionInfo.RegionSettings;

            currentRegionSettings.AgentLimit = loadedRegionSettings.AgentLimit;
            currentRegionSettings.AllowDamage = loadedRegionSettings.AllowDamage;
            currentRegionSettings.AllowLandJoinDivide = loadedRegionSettings.AllowLandJoinDivide;
            currentRegionSettings.AllowLandResell = loadedRegionSettings.AllowLandResell;
            currentRegionSettings.BlockFly = loadedRegionSettings.BlockFly;
            currentRegionSettings.BlockShowInSearch = loadedRegionSettings.BlockShowInSearch;
            currentRegionSettings.BlockTerraform = loadedRegionSettings.BlockTerraform;
            currentRegionSettings.DisableCollisions = loadedRegionSettings.DisableCollisions;
            currentRegionSettings.DisablePhysics = loadedRegionSettings.DisablePhysics;
            currentRegionSettings.DisableScripts = loadedRegionSettings.DisableScripts;
            currentRegionSettings.Elevation1NE = loadedRegionSettings.Elevation1NE;
            currentRegionSettings.Elevation1NW = loadedRegionSettings.Elevation1NW;
            currentRegionSettings.Elevation1SE = loadedRegionSettings.Elevation1SE;
            currentRegionSettings.Elevation1SW = loadedRegionSettings.Elevation1SW;
            currentRegionSettings.Elevation2NE = loadedRegionSettings.Elevation2NE;
            currentRegionSettings.Elevation2NW = loadedRegionSettings.Elevation2NW;
            currentRegionSettings.Elevation2SE = loadedRegionSettings.Elevation2SE;
            currentRegionSettings.Elevation2SW = loadedRegionSettings.Elevation2SW;
            currentRegionSettings.FixedSun = loadedRegionSettings.FixedSun;
            currentRegionSettings.ObjectBonus = loadedRegionSettings.ObjectBonus;
            currentRegionSettings.RestrictPushing = loadedRegionSettings.RestrictPushing;
            currentRegionSettings.TerrainLowerLimit = loadedRegionSettings.TerrainLowerLimit;
            currentRegionSettings.TerrainRaiseLimit = loadedRegionSettings.TerrainRaiseLimit;
            currentRegionSettings.TerrainTexture1 = loadedRegionSettings.TerrainTexture1;
            currentRegionSettings.TerrainTexture2 = loadedRegionSettings.TerrainTexture2;
            currentRegionSettings.TerrainTexture3 = loadedRegionSettings.TerrainTexture3;
            currentRegionSettings.TerrainTexture4 = loadedRegionSettings.TerrainTexture4;
            currentRegionSettings.UseEstateSun = loadedRegionSettings.UseEstateSun;
            currentRegionSettings.WaterHeight = loadedRegionSettings.WaterHeight;

            currentRegionSettings.Save();

            IEstateModule estateModule = m_scene.RequestModuleInterface<IEstateModule>();
            estateModule.sendRegionHandshakeToAll();

            return true;
        }

        /// <summary>
        /// Load terrain data
        /// </summary>
        /// <param name="terrainPath"></param>
        /// <param name="data"></param>
        /// <returns>
        /// true if terrain was resolved successfully, false otherwise.
        /// </returns>
        private bool LoadTerrain(string terrainPath, byte[] data)
        {
            ITerrainModule terrainModule = m_scene.RequestModuleInterface<ITerrainModule>();

            MemoryStream ms = new MemoryStream(data);
            terrainModule.LoadFromStream(terrainPath, ms);
            ms.Close();

            m_log.DebugFormat("[ARCHIVER]: Restored terrain {0}", terrainPath);

            return true;
        }

        /// <summary>
        /// Resolve path to a working FileStream
        /// </summary>
        private Stream GetStream(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    return new FileStream(path, FileMode.Open, FileAccess.Read);
                }
                else
                {
                    Uri uri = new Uri(path); // throw exception if not valid URI
                    if (uri.Scheme == "file")
                    {
                        return new FileStream(uri.AbsolutePath, FileMode.Open, FileAccess.Read);
                    }
                    else
                    {
                        if (uri.Scheme != "http")
                            throw new Exception(String.Format("Unsupported URI scheme ({0})", path));

                        // OK, now we know we have an HTTP URI to work with

                        return URIFetch(uri);
                    }
                }
            }
            catch (Exception e)
            {
                throw new Exception(String.Format("Unable to create file input stream for {0}: {1}", path, e));
            }
        }

        private static Stream URIFetch(Uri uri)
        {
            HttpWebRequest request  = (HttpWebRequest)  WebRequest.Create(uri);

            // request.Credentials = credentials;

            request.ContentLength = 0;

            WebResponse response = request.GetResponse();
            Stream file = response.GetResponseStream();

            if (response.ContentType != "application/x-oar")
                throw new Exception(String.Format("{0} does not identify an OAR file", uri.ToString()));

            if (response.ContentLength == 0)
                throw new Exception(String.Format("{0} returned an empty file", uri.ToString()));

            // return new BufferedStream(file, (int) response.ContentLength);
            return new BufferedStream(file, 1000000);
        }
    }
}
