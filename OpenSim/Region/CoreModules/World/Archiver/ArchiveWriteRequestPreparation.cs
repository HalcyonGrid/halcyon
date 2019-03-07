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
using System.Reflection;
using System.Text;
using System.Xml;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Serialization;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Data;

namespace OpenSim.Region.CoreModules.World.Archiver
{
    /// <summary>
    /// Prepare to write out an archive.
    /// </summary>
    public class ArchiveWriteRequestPreparation
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected Scene m_scene;
        protected Stream m_saveStream;
        protected Guid m_requestId;
        protected bool m_storeAssets = true;
        protected C5.HashSet<UUID> m_allowedCreatorIds = new C5.HashSet<UUID>();

        public bool MustCheckCreatorIds
        {
            get
            {
                return m_allowedCreatorIds.Count > 0;
            }
        }

        public ArchiveWriteRequestPreparation(Scene scene, string savePath, Guid requestId, bool storeAssets, 
            IEnumerable<UUID> allowedCreatorIds) : this(scene, savePath, requestId, storeAssets)
        {
            foreach (UUID creatorId in allowedCreatorIds)
            {
                m_allowedCreatorIds.Add(creatorId);
            }
        }

        /// <summary>
        /// Constructor
        /// </summary>
        public ArchiveWriteRequestPreparation(Scene scene, string savePath, Guid requestId, bool storeAssets)
        {
            m_scene = scene;
            m_saveStream = new GZipStream(new FileStream(savePath, FileMode.Create), CompressionMode.Compress);
            m_requestId = requestId;
            m_storeAssets = storeAssets;
        }
        
        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="scene"></param>
        /// <param name="saveStream">The stream to which to save data.</param>
        /// <param name="requestId">The id associated with this request</param>
        public ArchiveWriteRequestPreparation(Scene scene, Stream saveStream, Guid requestId)
        {
            m_scene = scene;
            m_saveStream = saveStream;
            m_requestId = requestId;
        }

        private bool ExportIsAllowed(UUID creatorId)
        {
            if (m_allowedCreatorIds.IsEmpty)
            {
                return true;
            }
            else
            {
                return m_allowedCreatorIds.Contains(creatorId);
            }
        }

        protected void AddObjectUsersToList(List<UUID> userlist, SceneObjectGroup grp)
        {
            foreach (SceneObjectPart part in grp.GetParts())
            {
                if (!userlist.Contains(part.OwnerID))
                    userlist.Add(part.OwnerID);
                if (!userlist.Contains(part.CreatorID))
                    userlist.Add(part.CreatorID);
                foreach (TaskInventoryItem item in part.TaskInventory.Values)
                {
                    if (!userlist.Contains(item.CreatorID))
                        userlist.Add(item.CreatorID);
                }
            }
        }

        /// <summary>
        /// Archive the region requested.
        /// </summary>
        /// <exception cref="System.IO.IOException">if there was an io problem with creating the file</exception>
        public void ArchiveRegion()
        {            
            Dictionary<UUID, int> assetUuids = new Dictionary<UUID, int>();

            List<EntityBase> entities = m_scene.GetEntities();
            List<SceneObjectGroup> sceneObjects = new List<SceneObjectGroup>();
            List<UUID> userlist = new List<UUID>();

            // Filter entities so that we only have scene objects.
            // FIXME: Would be nicer to have this as a proper list in SceneGraph, since lots of methods
            // end up having to do this
            foreach (EntityBase entity in entities)
            {
                if (entity is SceneObjectGroup)
                {
                    SceneObjectGroup sceneObject = (SceneObjectGroup)entity;

                    // If storing assets, assume cross-grid and include the user list file
                    if (m_storeAssets)
                    {
                        AddObjectUsersToList(userlist, sceneObject);
                    }
                    if (MustCheckCreatorIds)
                    {
                        bool failedCreatorCheck = false;
                        foreach (SceneObjectPart part in sceneObject.GetParts())
                        {
                            if (!ExportIsAllowed(part.CreatorID))
                            {
                                failedCreatorCheck = true;
                                break;
                            }
                        }

                        if (failedCreatorCheck)
                        {
                            continue;
                        }
                    }

                    if (!sceneObject.IsDeleted && !sceneObject.IsAttachment)
                        sceneObjects.Add(sceneObject);
                }
            }

            if (m_storeAssets)
            {
                UuidGatherer assetGatherer = new UuidGatherer(m_scene.CommsManager.AssetCache);

                foreach (SceneObjectGroup sceneObject in sceneObjects)
                {
                    assetGatherer.GatherAssetUuids(sceneObject, assetUuids);
                }
            }

            // Make sure that we also request terrain texture assets
            RegionSettings regionSettings = m_scene.RegionInfo.RegionSettings;

            if (m_storeAssets)
            {
                if (regionSettings.TerrainTexture1 != RegionSettings.DEFAULT_TERRAIN_TEXTURE_1)
                    assetUuids[regionSettings.TerrainTexture1] = 1;

                if (regionSettings.TerrainTexture2 != RegionSettings.DEFAULT_TERRAIN_TEXTURE_2)
                    assetUuids[regionSettings.TerrainTexture2] = 1;

                if (regionSettings.TerrainTexture3 != RegionSettings.DEFAULT_TERRAIN_TEXTURE_3)
                    assetUuids[regionSettings.TerrainTexture3] = 1;

                if (regionSettings.TerrainTexture4 != RegionSettings.DEFAULT_TERRAIN_TEXTURE_4)
                    assetUuids[regionSettings.TerrainTexture4] = 1;
            }

            if (MustCheckCreatorIds)
            {
                int originalCount = assetUuids.Count;

                m_log.DebugFormat(
                    "[ARCHIVER]: Filtering {0} asset IDs for {1} allowed creators",
                    originalCount, m_allowedCreatorIds.Count);

                C5.HashSet<UUID> assetsCreatedByAllowedUsers = this.CollectCreatedAssetIdsFromUserInventories();

                IEnumerable<UUID> uuids = new List<UUID>(assetUuids.Keys);
                assetUuids.Clear();

                foreach (UUID assetId in uuids)
                {
                    if (assetsCreatedByAllowedUsers.Contains(assetId))
                    {
                        assetUuids.Add(assetId, 1);
                    }
                }

                m_log.DebugFormat(
                    "[ARCHIVER]: Allowing export of {0} of {1} assets",
                    assetUuids.Count, originalCount);
            }

            m_log.DebugFormat(
                "[ARCHIVER]: {0} scene objects to serialize requiring save of {1} assets",
                sceneObjects.Count, assetUuids.Count);

            TarArchiveWriter archiveWriter = new TarArchiveWriter(m_saveStream);
            
            // Asynchronously request all the assets required to perform this archive operation
            ArchiveWriteRequestExecution awre
                = new ArchiveWriteRequestExecution(
                    sceneObjects,
                    m_scene.RequestModuleInterface<ITerrainModule>(),
                    m_scene.RequestModuleInterface<IRegionSerializerModule>(),
                    m_scene,
                    archiveWriter,
                    m_requestId);

            // Write out archive.xml control file first
            archiveWriter.WriteFile(ArchiveConstants.CONTROL_FILE_PATH, awre.CreateControlFile(assetUuids.Count > 0));
            m_log.InfoFormat("[ARCHIVER]: Added {0} control file to archive.", ArchiveConstants.CONTROL_FILE_PATH);

            // Now include the user list file (only if assets are being saved and it produced a list).
            if (userlist.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                foreach (UUID id in userlist)
                {
                    String name = m_scene.CommsManager.UserService.Key2Name(id, false);
                    if (!String.IsNullOrWhiteSpace(name))
                        sb.AppendFormat("{0} {1}{2}", id, name, Environment.NewLine);
                }
                String userlistContents = sb.ToString();
                if (!String.IsNullOrWhiteSpace(userlistContents))
                {
                    archiveWriter.WriteFile(ArchiveConstants.USERLIST_FILE_PATH, userlistContents);
                    m_log.InfoFormat("[ARCHIVER]: Added {0} file to archive.", ArchiveConstants.USERLIST_FILE_PATH);
                }
            }

            new AssetsRequest(
                new AssetsArchiver(archiveWriter, m_scene), assetUuids.Keys, 
                m_scene.CommsManager.AssetCache, awre.ReceivedAllAssets).Execute();
        }

        private C5.HashSet<UUID> CollectCreatedAssetIdsFromUserInventories()
        {
            C5.HashSet<UUID> retAssets = new C5.HashSet<UUID>();

            foreach (UUID creatorId in m_allowedCreatorIds)
            {
                this.CollectCreatedAssetIdsFromUserInventory(creatorId, retAssets);
            }

            return retAssets;
        }

        private void CollectCreatedAssetIdsFromUserInventory(UUID creatorId, C5.HashSet<UUID> retAssets)
        {
            IInventoryProviderSelector selector = ProviderRegistry.Instance.Get<IInventoryProviderSelector>();
            IInventoryStorage provider = selector.GetProvider(creatorId);

            List<InventoryFolderBase> skel = provider.GetInventorySkeleton(creatorId);
            foreach (InventoryFolderBase folder in skel)
            {
                InventoryFolderBase fullFolder = provider.GetFolder(folder.ID);
                foreach (InventoryItemBase item in fullFolder.Items)
                {
                    if (m_allowedCreatorIds.Contains(item.CreatorIdAsUuid))
                    {
                        retAssets.Add(item.AssetID);
                    }
                }
            }
        }
    }
}
