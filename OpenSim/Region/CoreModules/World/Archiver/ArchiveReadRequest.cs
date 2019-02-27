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
using OpenSim.Region.Framework.Scenes.Serialization;

namespace OpenSim.Region.CoreModules.World.Archiver
{
    /// <summary>
    /// Handles an individual archive read request
    /// </summary>
    public class ArchiveReadRequest
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly String ASSET_CREATORS = "asset_creators";

        private static readonly UUID LIBRARY_USER = new UUID("11111111-1111-0000-0000-000100bba000");

        private static readonly UUID DEFAULT_TERRAIN_1 = new UUID("b8d3965a-ad78-bf43-699b-bff8eca6c975");  // Terrain Dirt
        private static readonly UUID DEFAULT_TERRAIN_2 = new UUID("abb783e6-3e93-26c0-248a-247666855da3");  // Terrain Grass
        private static readonly UUID DEFAULT_TERRAIN_3 = new UUID("179cdabd-398a-9b6b-1391-4dc333ba321f");  // Terrain Mountain
        private static readonly UUID DEFAULT_TERRAIN_4 = new UUID("beb169c7-11ea-fff2-efe5-0f24dc881df2");  // Terrain Rock

        // Known viewer UUIDs
        private static readonly UUID TEXTURE_WHITE = new UUID("5748decc-f629-461c-9a36-a35a221fe21f");  // blank / white
        private static readonly UUID TEXTURE_PLYWOOD = new UUID("89556747-24cb-43ed-920b-47caed15465f");  // plywood
        private static readonly UUID TEXTURE_BLANK = new UUID("5748decc-f629-461c-9a36-a35a221fe21f");
        private static readonly UUID TEXTURE_TRANSPARENT = new UUID("8dcd4a48-2d37-4909-9f78-f7a9eb4ef903");
        private static readonly UUID TEXTURE_MEDIA = new UUID("8b5fec65-8d8d-9dc5-cda8-8fdf2716e361");

        // The following could be Primitive.TextureEntry.WHITE_TEXTURE but that hides replacements, so let's use the more obvious.
        private static readonly UUID DEFAULT_SUBSTITEXTURE = TEXTURE_PLYWOOD;  // plywood

        private Scene m_scene;
        private Stream m_loadStream;
        private Guid m_requestId;
        private string m_errorMessage;
        private IGroupsModule m_GroupsModule;

        private ConnectionFactory _connFactory;
        private IConfigSource m_config;
        private IRegionSerializerModule m_serializer;

        /// <value>
        /// Should the archive being loaded be merged with what is already on the region?
        /// </value>
        private bool m_merge;

        /// <summary>
        /// Should the dearchive process be allowed to reassign creators and previous users
        /// </summary>
        private bool m_allowUserReassignment;

        private bool m_skipErrorGroups = false;

        // Filtered OAR loading
        private HashSet<UUID> m_allowedUUIDs = null;

        private Dictionary<UUID, UUID> m_assetCreators = null;
        private Dictionary<UUID, String> m_libAssets = null;
        private IInventoryObjectSerializer m_inventorySerializer = null;

        int m_replacedPart = 0;
        int m_replacedItem = 0;
        int m_replacedTexture = 0;
        int m_replacedSound = 0;

        int m_keptPart = 0;
        int m_keptItem = 0;
        int m_keptTexture = 0;
        int m_keptSound = 0;

        int m_scannedObjects = 0;
        int m_scannedMesh = 0;
        int m_scannedParts = 0;
        int m_scannedItems = 0;
        int m_debugOars = 0;

        // false if we assume prim creator is creator of assets on prims, or has license to use, or will filter.
        bool m_filterPrimAssets = false;    // false==include prim assets like textures

        /// <summary>
        /// Used to cache lookups for valid uuids.
        /// </summary>
        private IDictionary<UUID, bool> m_validUserUuids =
            new Dictionary<UUID, bool>
            {
                {UUID.Zero, true},
                {LIBRARY_USER, true} //the "Mr Halcyon" user
            };

        public ArchiveReadRequest(IConfigSource config, Scene scene, string loadPath, bool merge, Guid requestId, bool allowUserReassignment, bool skipErrorGroups, int debug)
        {
            Stream loadStream = new GZipStream(GetStream(loadPath), CompressionMode.Decompress);
            InitArchiveRead(config, scene, loadStream, merge, requestId, allowUserReassignment, skipErrorGroups, debug);
        }

        public ArchiveReadRequest(IConfigSource config, Scene scene, Stream loadStream, bool merge, Guid requestId, bool allowUserReassignment, bool skipErrorGroups, int debug)
        {
            InitArchiveRead(config, scene, loadStream, merge, requestId, allowUserReassignment, skipErrorGroups, debug);
        }

        private void WhitelistLibraryFolder(InventoryFolderImpl parentFolder)
        {
            foreach (var libItem in parentFolder.RequestListOfItems())
            {
                m_libAssets[libItem.AssetID] = libItem.Name;
            }
            foreach (var libFolder in parentFolder.RequestListOfFolderImpls())
            {
                WhitelistLibraryFolder(libFolder);
            }
        }

        private void InitArchiveRead(IConfigSource config, Scene scene, Stream loadStream, bool merge, Guid requestId, bool allowUserReassignment, bool skipErrorGroups, int debug)
        {
            m_config = config;
            m_scene = scene;
            m_loadStream = loadStream;
            m_merge = merge;
            m_requestId = requestId;
            m_allowUserReassignment = allowUserReassignment;
            m_GroupsModule = scene.RequestModuleInterface<IGroupsModule>();
            m_skipErrorGroups = skipErrorGroups;
            m_debugOars = debug;

            m_serializer = m_scene.RequestModuleInterface<IRegionSerializerModule>();

            ISerializationEngine engine;
            if (ProviderRegistry.Instance.TryGet<ISerializationEngine>(out engine))
            {
                m_inventorySerializer = engine.InventoryObjectSerializer;
            }

            m_scannedObjects = 0;
            m_scannedMesh = 0;
            m_scannedParts = 0;
            m_scannedItems = 0;

            m_libAssets = new Dictionary<UUID, string>();
            WhitelistLibraryFolder(m_scene.CommsManager.LibraryRoot);

            // Add known viewer defaults we want to assume are okay to use.
            // These may or may not match those in the Library, doesn't matter, let's collect both.
            m_libAssets[TEXTURE_WHITE] = "TEXTURE_WHITE";
            m_libAssets[TEXTURE_PLYWOOD] = "TEXTURE_PLYWOOD";
            m_libAssets[TEXTURE_BLANK] = "TEXTURE_BLANK";
            m_libAssets[TEXTURE_TRANSPARENT] = "TEXTURE_TRANSPARENT";
            m_libAssets[TEXTURE_MEDIA] = "TEXTURE_MEDIA";
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
                        // Add an exception for the standard Library "user".
                        optInTable[LIBRARY_USER] = 2; // Library user opts-in FULL.
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

            m_log.InfoFormat("[ARCHIVER]: Saved {0} asset creators referenced from OAR file.", assetCreatorsTable.Count);
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

        private void ScanContentsForAssetCreatorIDs(SceneObjectPart part)
        {
            try
            {
                TaskInventoryDictionary inv = part.TaskInventory;
                foreach (KeyValuePair<UUID, TaskInventoryItem> kvp in inv)
                {
                    m_scannedItems++;
                    if ((m_scannedItems % 1000) == 0)
                        m_log.InfoFormat("[ARCHIVER]: {0} objects, {1} mesh, {2} parts, {3} items scanned.", m_scannedObjects, m_scannedMesh, m_scannedParts, m_scannedItems);

                    TaskInventoryItem item = kvp.Value;
                    if (item.AssetID != UUID.Zero)
                    {
                        m_assetCreators[item.AssetID] = item.CreatorID;
                    }
                    if (item.InvType == (int)InventoryType.Object)
                    {
                        AssetBase asset = GetAsset(item.AssetID);
                        if (asset != null)
                        {
                            if (item.ContainsMultipleItems)
                            {
                                CoalescedObject obj = m_inventorySerializer.DeserializeCoalescedObjFromInventoryBytes(asset.Data);
                                foreach (SceneObjectGroup grp in obj.Groups)
                                {
                                    ScanObjectForAssetCreatorIDs(grp);
                                }
                            }
                            else
                            {
                                SceneObjectGroup inventoryObject = DeserializeObject(part.OwnerID, item.ItemID, asset.Data);
                                if (inventoryObject != null)
                                    ScanObjectForAssetCreatorIDs(inventoryObject);
                            }
                        }
                    }
                }

                // Check if part is mesh, assume SculptTexture asset is created by part creator.
                int basicSculptType = part.Shape.SculptType & (byte)0x3F;
                if (basicSculptType != (byte)SculptType.None)
                {
                    if ((basicSculptType == (byte)SculptType.Mesh) && (part.Shape.SculptTexture != UUID.Zero))
                    {
                        // Assume that mesh textures are created by the prim creator
                        m_assetCreators[part.Shape.SculptTexture] = part.CreatorID;
                        m_scannedMesh++;
                    }
                }
            }
            catch (Exception e)
            {
                m_log.InfoFormat("[ARCHIVER]: Error while deserializing group: {0}", e);
            }
        }

        private void ScanObjectForAssetCreatorIDs(SceneObjectGroup sceneObject)
        {
            try
            {
                m_scannedObjects++;
                foreach (SceneObjectPart part in sceneObject.GetParts())
                {
                    m_scannedParts++;
                    if ((m_scannedParts % 1000) == 0)
                        m_log.InfoFormat("[ARCHIVER]: {0} objects, {1} mesh, {2} parts, {3} items scanned.", m_scannedObjects, m_scannedMesh, m_scannedParts, m_scannedItems);
                    ScanContentsForAssetCreatorIDs(part);
                }
            }
            catch (Exception e)
            {
                m_log.InfoFormat("[ARCHIVER]: Error while deserializing group: {0}", e);
            }
        }

        private void ScanObjectForAssetCreatorIDs(string serializedSOG)
        {
            SceneObjectGroup sceneObject;
            try
            {
                sceneObject = m_serializer.DeserializeGroupFromXml2(serializedSOG);
                if (sceneObject == null)
                    return;

                ScanObjectForAssetCreatorIDs(sceneObject);
            }
            catch (Exception e)
            {
                m_log.InfoFormat("[ARCHIVER]: Error while deserializing group: {0}", e);
            }
        }

        public void ScanArchiveForAssetCreatorIDs()
        {
            m_assetCreators = new Dictionary<UUID, UUID>();
            string filePath = "NONE";

            try
            {
                TarArchiveReader archive = new TarArchiveReader(m_loadStream);
                TarArchiveReader.TarEntryType entryType;
                byte[] data;
                while ((data = archive.ReadEntry(out filePath, out entryType)) != null)
                {
                    if (TarArchiveReader.TarEntryType.TYPE_DIRECTORY == entryType)
                        continue;

                    if (filePath.StartsWith(ArchiveConstants.OBJECTS_PATH))
                    {
                        ScanObjectForAssetCreatorIDs(Encoding.UTF8.GetString(data));
                    }
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ARCHIVER]: Aborting creator scan with error in archive file {0}.  {1}", filePath, e);
            }
            finally
            {
                m_log.InfoFormat("[ARCHIVER]: Scan complete: {0} objects, {1} mesh, {2} parts, {3} items.", m_scannedObjects, m_scannedMesh, m_scannedParts, m_scannedItems);
                SaveAssetCreators(m_assetCreators);
            }
        }

        private static void ResetGroupAfterDeserialization(UUID ownerID, UUID itemId, SceneObjectGroup grp)
        {
            if (grp.OwnerID == UUID.Zero)
                grp.OwnerID = ownerID;
            grp.ResetInstance(true, false, UUID.Zero);
            foreach (var part in grp.GetParts())
            {
                if (part.OwnerID == UUID.Zero)
                    part.OwnerID = ownerID;
                foreach (var kvp in part.TaskInventory)
                {
                    TaskInventoryItem item = kvp.Value;
                    if (item.OwnerID == UUID.Zero)
                    {
                        item.OwnerID = ownerID;
                    }
                }
                part.DoPostDeserializationCleanups(itemId);
                part.TrimPermissions();
            }
        }

        private SceneObjectGroup DeserializeObject(UUID ownerID, UUID itemId, byte[] bytes)
        {
            SceneObjectGroup grp;

            if (m_inventorySerializer == null) return null;
            if (m_inventorySerializer.IsValidGroup(bytes))
            {
                grp = m_inventorySerializer.DeserializeGroupFromInventoryBytes(bytes);
            }
            else
            {
                string xmlData = Utils.BytesToString(bytes);
                grp = SceneObjectSerializer.FromOriginalXmlFormat(itemId, xmlData);
            }
            ResetGroupAfterDeserialization(ownerID, itemId, grp);

            return grp;
        }

        private CoalescedObject DeserializeCoalescedObject(byte[] bytes)
        {
            if (m_inventorySerializer == null) return null;
            if (m_inventorySerializer.CanDeserialize(bytes))
            {
                return m_inventorySerializer.DeserializeCoalescedObjFromInventoryBytes(bytes);
            }
            return null;
        }

        private AssetBase SerializeObjectToAsset(SceneObjectGroup grp)
        {
            if (m_inventorySerializer == null) return null;

            byte[] bytes = m_inventorySerializer.SerializeGroupToInventoryBytes(grp, SerializationFlags.None);
            AssetBase asset = new AssetBase(UUID.Random(), String.Empty);
            asset.Type = (int)AssetType.Object;
            asset.Data = bytes;
            return asset;
        }

        private AssetBase ReserializeCoalescedToAsset(IList<SceneObjectGroup> items, List<ItemPermissionBlock> itemPermissions, SerializationFlags flags)
        {
            if (m_inventorySerializer == null) return null;

            Dictionary<UUID, ItemPermissionBlock> perms = new Dictionary<UUID, ItemPermissionBlock>();
            for (int i = 0; i < items.Count; i++)
            {
                perms.Add(items[i].UUID, itemPermissions[i]);
            }
            CoalescedObject csog = new CoalescedObject(items, perms);
            byte[] bytes = m_inventorySerializer.SerializeCoalescedObjToInventoryBytes(csog, flags);

            AssetBase asset = new AssetBase(UUID.Random(), String.Empty);
            asset.Type = (int)AssetType.Object;
            asset.Data = bytes;
            return asset;
        }

        private bool MustFilterByOwner(UUID ownerID)
        {
            if (m_allowedUUIDs == null)
                return false;   // filter disabled

            return !m_allowedUUIDs.Contains(ownerID);
        }

        private bool MustReplaceByCreator(UUID creatorID)
        {
            if (m_allowedUUIDs == null)
                return false;   // filter disabled

            // Also include the LIBRARY_USER in the creator whitelist.
            if (creatorID == LIBRARY_USER)
                return false;

            return !m_allowedUUIDs.Contains(creatorID);
        }

        private bool MustReplaceByAsset(UUID assetID, UUID ownerID, UUID creatorID)
        {
            if (assetID == UUID.Zero) return false;

            if (m_allowedUUIDs == null)
                return false;   // filter disabled

            if (m_libAssets.ContainsKey(assetID))
                return false;   // it's in the Library

            // Filter out owners who aren't in the allowed list.
            if (MustFilterByOwner(ownerID))
                return true;    // filter out this user

            if (creatorID == UUID.Zero) {
                // See if we can figure out who the asset creator is.
                if (m_assetCreators.ContainsKey(assetID))
                {
                    // We have explicit wishes from the creator
                    creatorID = m_assetCreators[assetID];
                }
            }

            // Don't filter if we can't identify a creator ID and the owner is allowed.
            if (creatorID == UUID.Zero)
                return false;

            // Filter out creators who aren't in the allowed list.
            return MustReplaceByCreator(creatorID);
        }

        private void ReplaceDescription(SceneObjectPart part, UUID prevCreatorID)
        {
            if (String.IsNullOrWhiteSpace(part.Description) || part.Description.ToLower().Equals("(no description)"))
            {
                part.Description = $"(Replaced: Prim created by {prevCreatorID})";
            }
        }

        private void ReplacePartWithDefaultPrim(SceneObjectPart part, UUID ownerID)
        {
            if (m_debugOars >= 1)
                m_log.InfoFormat("[ARCHIVER]: Substituting part '{0}' for owner {1}.", part.Name, ownerID);

            // First, replace the prim with a default prim.
            part.Shape = PrimitiveBaseShape.Default.Copy();
            ReplaceDescription(part, part.CreatorID);
            // No need to replace textures since the whole prim was replaced.
            m_replacedPart++;
        }

        private Primitive.TextureEntryFace FilterFaceTexture(SceneObjectPart part, Primitive.TextureEntry te, Primitive.TextureEntryFace face, UUID ownerID)
        {
            if (MustReplaceByAsset(face.TextureID, ownerID, UUID.Zero))
            {
                if (m_debugOars >= 1)
                    m_log.InfoFormat("[ARCHIVER]: Filtering prim texture {0} in part '{1}' for owner {2}.", face.TextureID, part.Name, ownerID);
                face.TextureID = DEFAULT_SUBSTITEXTURE;
                // Shortcut: if we're dropping the face's actual texture, assume we drop the materials too.
                if (part.Shape.RenderMaterials.ContainsMaterial(face.MaterialID))
                    part.Shape.RenderMaterials.RemoveMaterial(face.MaterialID);
                face.MaterialID = UUID.Zero;
                m_replacedTexture++;
                return face;
            }
            else
            {
                m_keptTexture++;
                return null;
            }
        }

        private bool FilterPrimTexturesByCreator(SceneObjectPart part, UUID ownerID)
        {
            if (!m_filterPrimAssets) return false;

            bool filtered = false;

            int basicSculptType = part.Shape.SculptType & (byte)0x3F;
            if (basicSculptType != (byte)SculptType.None)
            {
                if (basicSculptType == (byte)SculptType.Mesh)
                {
                    // Assume that mesh textures are created by the prim creator
                    if (m_debugOars >= 1)
                        m_log.InfoFormat("[ARCHIVER]: Retaining MESH shape {0} in part '{1}' for owner {2}.", part.Shape.SculptTexture, part.Name, ownerID);
                    return false;
                }

                if (MustReplaceByAsset(part.Shape.SculptTexture, ownerID, UUID.Zero))
                {
                    if (m_debugOars >= 1)
                        m_log.InfoFormat("[ARCHIVER]: Filtering SCULPT shape {0} in part '{1}' for owner {2}.", part.Shape.SculptTexture, part.Name, ownerID);
                    ReplacePartWithDefaultPrim(part, ownerID);
                    filtered = true;
                }
            }

            // Now let's filter actual textures on faces.
            Primitive.TextureEntry te = new Primitive.TextureEntry(part.Shape.TextureEntry, 0, part.Shape.TextureEntry.Length);

            // Start with the default texture
            Primitive.TextureEntryFace face = te.DefaultTexture;
            Primitive.TextureEntryFace newFace = FilterFaceTexture(part, te, face, ownerID);
            if (newFace != null)
            {
                te.DefaultTexture = face;
                filtered = true;
            }

            // Now filter the textures on any of the faces.
            for (int i = 0; i < Primitive.TextureEntry.MAX_FACES; i++)
            {
                if (te.FaceTextures[i] != null)
                {
                    face = te.FaceTextures[i];
                    newFace = FilterFaceTexture(part, te, face, ownerID);
                    if (newFace != null)
                    {
                        te.FaceTextures[i] = newFace;
                        filtered = true;
                    }
                }
            }

            // And save the changes
            if (filtered)
                part.UpdateTexture(te);
            return filtered;
        }

        private bool FilterOtherPrimAssetsByCreator(SceneObjectPart part, UUID ownerID)
        {
            if (!m_filterPrimAssets) return false;

            bool filtered = false;
            if (part.Sound != UUID.Zero)
            {
                if (MustReplaceByAsset(part.Sound, ownerID, UUID.Zero))
                {
                    if (m_debugOars >= 1)
                        m_log.InfoFormat("[ARCHIVER]: Filtering prim sound {0} in part '{1}' for owner {2}.", part.Sound, part.Name, ownerID);
                    part.Sound = UUID.Zero;
                    m_replacedSound++;
                    filtered = true;
                }
                else
                {
                    if (m_debugOars >= 1)
                        m_log.InfoFormat("[ARCHIVER]: Retaining prim sound {0} in part '{1}' for owner {2}.", part.Sound, part.Name, ownerID);
                    m_keptSound++;
                }
            }

            if (part.CollisionSound != UUID.Zero)
            {
                if (MustReplaceByAsset(part.CollisionSound, ownerID, UUID.Zero))
                {
                    if (m_debugOars >= 1)
                        m_log.InfoFormat("[ARCHIVER]: Filtering prim collision sound {0} in part '{1}' for owner {2}.", part.CollisionSound, part.Name, ownerID);
                    part.CollisionSound = UUID.Zero;
                    m_replacedSound++;
                    filtered = true;
                }
                else
                {
                    if (m_debugOars >= 2)
                        m_log.InfoFormat("[ARCHIVER]: Retaining prim collision sound {0} in part '{1}' for owner {2}.", part.CollisionSound, part.Name, ownerID);
                    m_keptSound++;
                }
            }
            return filtered;
        }

        private bool FilterPart(SceneObjectPart part, UUID ownerID)
        {
            bool filtered = false;

            if (m_allowUserReassignment)
            {
                if (part.OwnerID != ownerID)
                {
                    // we're reassigning ownership, mark as filtered so that changes are saved
                    if (m_debugOars >= 1)
                                m_log.InfoFormat("[ARCHIVER]: Reassigning prim ownership in part '{0}' in '{1}' for owner {2} to {3}.", part.Name, part.ParentGroup.Name, part.OwnerID, ownerID);
                    part.OwnerID = ownerID;
                    filtered = true;
                }
            }

            // Check if object creator has opted in
            if (MustReplaceByCreator(part.CreatorID))
            {
                // Creator of prim has not opted-in for this instance.
                // First, replace the prim with a default prim.
                ReplacePartWithDefaultPrim(part, ownerID);
                filtered = true;
            }
            else
            {
                m_keptPart++;
                filtered = FilterPrimTexturesByCreator(part, ownerID) | filtered;
            }
            // Now in both cases filter other prim assets
            filtered = FilterOtherPrimAssetsByCreator(part, ownerID) | filtered;

            return filtered;
        }

        private void ReserializeAssetIntoItem(TaskInventoryItem item, AssetBase newAsset)
        {
            // We're filtering an object inside the Contents, so
            // replace the asset with a filtered one in this nested object.
            // Must re-serialize this part and store as an asset for reference 
            // for when this part's Contents are opened in the future
            if (newAsset != null)
            {
                m_scene.CommsManager.AssetCache.AddAsset(newAsset, AssetRequestInfo.InternalRequest());
                if (m_debugOars >= 1)
                    m_log.InfoFormat("[ARCHIVER]: Reserializing new asset {0} in item '{1}'.", newAsset.FullID, item.Name);
                item.AssetID = newAsset.FullID;
            }
        }

        private bool FilterItem(SceneObjectPart part, TaskInventoryItem item, UUID ownerID, int depth)
        {
            if (item.AssetID == UUID.Zero)
            {
                if (m_debugOars >= 1)
                    m_log.InfoFormat("[ARCHIVER]: Filtering NULL asset for item '{0}'.", item.Name);
                return false;
            }

            AssetBase asset = GetAsset(item.AssetID);
            if (asset == null)
            {
                if (m_debugOars >= 1)
                    m_log.InfoFormat("[ARCHIVER]: GetAsset returned NULL for asset {0} in item '{1}'.", item.AssetID, item.Name);
                return false;
            }


            bool filtered = false;
            if (item.ContainsMultipleItems)
            {
                if (m_debugOars >= 2)
                    m_log.InfoFormat("[ARCHIVER]: Part '{0}' has coalesced item '{1}'.", part.Name, item.Name);
                // Need to reserialize coalesced item
                CoalescedObject obj = m_inventorySerializer.DeserializeCoalescedObjFromInventoryBytes(asset.Data);
                List<SceneObjectGroup> items = new List<SceneObjectGroup>();
                List<ItemPermissionBlock> perms = new List<ItemPermissionBlock>();
                foreach (SceneObjectGroup inventoryObject in obj.Groups)
                {
                    ItemPermissionBlock itemperms = obj.FindPermissions(inventoryObject.UUID);
                    if ((inventoryObject != null) && FilterObjectByCreators(inventoryObject, ownerID, depth + 1))
                    {
                        if (m_debugOars >= 2)
                            m_log.InfoFormat("[ARCHIVER]: Coalesced object '{0}' in item '{1}' needs filtering.", inventoryObject.Name, item.Name);
                        filtered = true; // ripple effect. this object's Contents changed, new asset ID in items.
                    }
                    inventoryObject.OwnerID = ownerID;  // save the current owner before reserializing object
                    items.Add(inventoryObject);
                    perms.Add(itemperms);
                }
                if (filtered)
                {
                    // ReserializeCoalescedToAsset allocates a new asset ID
                    if (m_debugOars >= 1)
                        m_log.InfoFormat("[ARCHIVER]: Reserializing coalesced item '{0}'.", item.Name);
                    asset = ReserializeCoalescedToAsset(items, perms, SerializationFlags.None);
                    ReserializeAssetIntoItem(item, asset);
                }
            }
            else
            {
                SceneObjectGroup inventoryObject = DeserializeObject(part.OwnerID, item.ItemID, asset.Data);
                if ((inventoryObject != null) && FilterObjectByCreators(inventoryObject, ownerID, depth + 1))
                {
                    if (m_debugOars >= 1)
                        m_log.InfoFormat("[ARCHIVER]: Object '{0}' in item '{1}' needs filtering.", inventoryObject.Name, item.Name);
                    inventoryObject.OwnerID = ownerID;  // save the current owner before reserializing object
                    // SerializeObjectToAsset allocates a new asset ID
                    ReserializeAssetIntoItem(item, SerializeObjectToAsset(inventoryObject));
                    filtered = true; // ripple effect. this object's Contents changed, new asset ID in items.
                }
            }
            item.OwnerID = ownerID; // save the current owner before reserializing item too
            return filtered;
        }

        // depth==0 when it's the top-level object (no need to reserialize changes as asset)
        private bool FilterContents(SceneObjectPart part, UUID ownerID, int depth)
        {
            bool filtered = false;
            // Now let's take a look inside the Contents
            lock (part.TaskInventory)
            {
                TaskInventoryDictionary inv = part.TaskInventory;
                List<TaskInventoryItem> replacedItems = new List<TaskInventoryItem>();
                foreach (KeyValuePair<UUID, TaskInventoryItem> kvp in inv)
                {
                    TaskInventoryItem item = kvp.Value;
                    // First, let's cache the creator.
                    if (item.CreatorID.Equals(ownerID) && (item.AssetID != UUID.Zero))
                    {
                        m_assetCreators[item.AssetID] = item.CreatorID;
                    }

                    if (m_allowUserReassignment)
                    {
                        if (item.OwnerID != ownerID)
                        {
                            // we're reassigning ownership, mark as filtered so that changes are saved
                            item.OwnerID = ownerID;
                            filtered = true;
                        }
                    }


                    // Now let's check whether the item needs filtering...
                    if (item.InvType == (int)InventoryType.Object)
                    {
                        filtered = FilterItem(part, item, ownerID, depth) || filtered;
                        if (filtered)
                        {
                            replacedItems.Add(item);
                            m_replacedItem++;
                        }
                    }
                    else
                    if (item.CreatorID.Equals(ownerID))
                    {
                        if (m_debugOars >= 2)
                            m_log.InfoFormat("[ARCHIVER]: Item '{0}' in part '{1}' has owner {2} matching creator.", item.Name, part.Name, item.OwnerID);
                        m_keptItem++;
                    }
                    else
                    if (MustReplaceByAsset(item.AssetID, ownerID, item.CreatorID))
                    {
                        if (m_debugOars >= 1)
                            m_log.InfoFormat("[ARCHIVER]: Item '{0}' in part '{1}' must zero asset {2}.", item.Name, part.Name, item.AssetID);
                        item.AssetID = UUID.Zero;
                        filtered = true;
                        replacedItems.Add(item);
                        m_replacedItem++;
                    }
                    else
                    {
                        if (m_debugOars >= 2)
                            m_log.InfoFormat("[ARCHIVER]: Item '{0}' in part '{1}' can retain asset {2}.", item.Name, part.Name, item.AssetID);
                        m_keptItem++;
                    }
                }

                // Now, while not iterating the dictionary any more, update some of the items with the mods.
                foreach (var item in replacedItems)
                {
                    if (m_debugOars >= 1)
                        m_log.InfoFormat("[ARCHIVER]: replacedItems '{0}' in part '{1}' with asset {2}.", item.Name, part.Name, item.AssetID);
                    part.TaskInventory.Remove(item.ItemID);
                    item.ItemID = UUID.Random();
                    part.TaskInventory[item.ItemID] = item;
                }
            }
            return filtered;
        }

        // returns true if anything in the object should be skipped on OAR file restore
        private bool FilterObjectByCreators(SceneObjectGroup sceneObject, UUID ownerID, int depth)
        {
            bool filtered = false;
            if (m_allowedUUIDs == null) return false; // no filtering

            if (sceneObject == null) return true;

            foreach (SceneObjectPart part in sceneObject.GetParts())
            {
                try
                {
                    filtered = FilterPart(part, ownerID) | filtered;
                    filtered = FilterContents(part, ownerID, depth) | filtered;
                }
                catch (Exception e)
                {
                    m_log.InfoFormat("[ARCHIVER]: Error while filtering object: {0}", e);
                }
            }
            return filtered;
        }

        private AssetBase GetAsset(UUID assetID)
        {
            if (assetID == UUID.Zero)
                return null;
            return m_scene.CommsManager.AssetCache.GetAsset(assetID, AssetRequestInfo.InternalRequest());
        }

        private void DearchiveSceneObject(SceneObjectGroup sceneObject, UUID ownerID, bool checkContents, Dictionary<UUID, UUID> OriginalBackupIDs)
        {
            UUID resolveWithUser = UUID.Zero;   // if m_allowUserReassignment, this is who gets it all.

            // For now, give all incoming scene objects new uuids.  This will allow scenes to be cloned
            // on the same region server and multiple examples a single object archive to be imported
            // to the same scene (when this is possible).
            UUID OldUUID = sceneObject.UUID;
            sceneObject.ResetIDs();
            // if sceneObject is no-copy, save the old ID with the new ID.
            OriginalBackupIDs[sceneObject.UUID] = OldUUID;

            if (m_allowUserReassignment)
            {
                // Try to retain the original creator/owner/lastowner if their uuid is present on this grid
                // otherwise, use the master avatar uuid instead
                if (m_scene.RegionInfo.EstateSettings.EstateOwner != UUID.Zero)
                    resolveWithUser = m_scene.RegionInfo.EstateSettings.EstateOwner;
                else
                    resolveWithUser = m_scene.RegionInfo.MasterAvatarAssignedUUID;
            }

            if (!ResolveUserUuid(ownerID))
            {
                if (ResolveUserUuid(sceneObject.OwnerID))
                {
                    ownerID = sceneObject.OwnerID;
                } else
                {
                    m_log.WarnFormat("[ARCHIVER]: Could not resolve av/group ID {0} for object '{1}' owner", sceneObject.OwnerID, sceneObject.Name);
                    ownerID = resolveWithUser;
                }
            }

            FilterObjectByCreators(sceneObject, ownerID, 0);
        }

        /// <summary>
        /// Dearchive the region embodied in this request.
        /// </summary>
        public void DearchiveRegion(HashSet<UUID> allowedUUIDs)
        {
            int successfulAssetRestores = 0;
            int failedAssetRestores = 0;
            List<string> serializedSceneObjects = new List<string>();
            string filePath = "NONE";

            if (allowedUUIDs != null)
            {
                // Adopt this whitelist for filtering a load.
                m_allowedUUIDs = allowedUUIDs;
                // Now a normal filtered load.
                m_assetCreators = GetAssetCreators();
            }

            try
            {
                TarArchiveReader archive = new TarArchiveReader(m_loadStream);
                TarArchiveReader.TarEntryType entryType;
                byte[] data;
                while ((data = archive.ReadEntry(out filePath, out entryType)) != null)
                {                    
                    //m_log.DebugFormat(
                    //    "[ARCHIVER]: Successfully read {0} ({1} bytes)", filePath, data.Length);
                    
                    if (TarArchiveReader.TarEntryType.TYPE_DIRECTORY == entryType)
                        continue;                    

                    if (filePath.StartsWith(ArchiveConstants.OBJECTS_PATH))
                    {
                        serializedSceneObjects.Add(Encoding.UTF8.GetString(data));
                        if ((serializedSceneObjects.Count % 1000) == 0)
                            m_log.InfoFormat("[ARCHIVER]: Region objects found in OAR: {0}", serializedSceneObjects.Count);
                    }
                    else if (filePath.StartsWith(ArchiveConstants.ASSETS_PATH))
                    {
                        if (LoadAsset(filePath, data))
                            successfulAssetRestores++;
                        else
                            failedAssetRestores++;

                        if (((successfulAssetRestores + failedAssetRestores) % 1000) == 0)
                            m_log.InfoFormat("[ARCHIVER]: Assets {0} of {1} restored (so far)...", successfulAssetRestores, successfulAssetRestores + failedAssetRestores);
                    }
                    else if (!m_merge && filePath.StartsWith(ArchiveConstants.TERRAINS_PATH))
                    {
                        m_log.InfoFormat("[ARCHIVER]: Terrain loading...");
                        LoadTerrain(filePath, data);
                        m_log.InfoFormat("[ARCHIVER]: Terrain loaded.");
                    }
                    else if (!m_merge && filePath.StartsWith(ArchiveConstants.SETTINGS_PATH))
                    {
                        LoadRegionSettings(filePath, data);
                        m_log.InfoFormat("[ARCHIVER]: Region settings loaded.");
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
                m_log.ErrorFormat("[ARCHIVER]: Filtered or failed to load {0} assets", failedAssetRestores);
                m_errorMessage += String.Format("Filtered or failed to load {0} assets", failedAssetRestores);
            }

            // Reload serialized prims
            m_log.InfoFormat("[ARCHIVER]: Preparing {0} scene objects.  Please wait.", serializedSceneObjects.Count);

            int sceneObjectsLoadedCount = 0;

            List<SceneObjectGroup> backupObjects = new List<SceneObjectGroup>();
            Dictionary<UUID, UUID> OriginalBackupIDs = new Dictionary<UUID, UUID>();

            foreach (string serializedSceneObject in serializedSceneObjects)
            {
                SceneObjectGroup sceneObject;
                try
                {
                    sceneObject = m_serializer.DeserializeGroupFromXml2(serializedSceneObject);
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

                if (MustFilterByOwner(sceneObject.OwnerID))
                    continue;

                DearchiveSceneObject(sceneObject, sceneObject.OwnerID, true, OriginalBackupIDs);

                backupObjects.Add(sceneObject);
                if ((backupObjects.Count % 500) == 0)
                    m_log.InfoFormat("[ARCHIVER]: Prepared {0} of {1} scene objects...", backupObjects.Count, serializedSceneObjects.Count);

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

            m_log.InfoFormat("[ARCHIVER]: Loading {0} scene objects.  Please wait.", backupObjects.Count);

            // sceneObject is the one from backup to restore to the scene
            foreach (SceneObjectGroup backupObject in backupObjects)
            {
                SceneObjectGroup existingObject = null;
                UUID originalUUID = OriginalBackupIDs[backupObject.UUID];
                // Don't restore any no-copy objects unless there was an existing matching UUID in the scene.
                if (ExistingNoCopyObjects.ContainsKey(originalUUID))
                    existingObject = ExistingNoCopyObjects[originalUUID];
                // existingSOG here means existing NO-COPY object, not deleted from scene above

                if ((m_allowedUUIDs == null) && NoCopyObjectOrContents(backupObject))
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
                if ((sceneObjectsLoadedCount % 1000) == 0)
                    m_log.InfoFormat("[ARCHIVER]: Loaded {0} of {1} scene objects...", sceneObjectsLoadedCount, backupObjects.Count);
            }

            m_log.InfoFormat("[ARCHIVER]: Restored {0} scene objects to the scene", sceneObjectsLoadedCount);

            int ignoredObjects = serializedSceneObjects.Count - sceneObjectsLoadedCount;

            if (ignoredObjects > 0)
                m_log.WarnFormat("[ARCHIVER]: Ignored {0} scene objects that already existed in the scene", ignoredObjects);

            if (m_allowedUUIDs != null)
            {
                m_log.WarnFormat("[ARCHIVER]: Prim shapes replaced={0} restored={1}", m_replacedPart, m_keptPart);
                m_log.WarnFormat("[ARCHIVER]: Item assets replaced={0} restored={1}", m_replacedItem, m_keptItem);
                m_log.WarnFormat("[ARCHIVER]: Texture assets replaced={0} restored={1}", m_replacedTexture, m_keptTexture);
                m_log.WarnFormat("[ARCHIVER]: Sound assets replaced={0} restored={1}", m_replacedSound, m_keptSound);
            }

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
            if (!m_allowUserReassignment)
                return true;

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
        private bool LoadAsset(string assetPath, byte[] data)
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
                UUID assetID = new UUID(uuid);

                try
                {
                    if (m_debugOars >= 1)
                        m_log.DebugFormat("[ARCHIVER]: Importing asset {0}, type {1}", uuid, assetType);
                    AssetBase asset = new AssetBase(assetID, String.Empty);
                    asset.Type = assetType;
                    asset.Data = data;
                    m_scene.CommsManager.AssetCache.AddAsset(asset, AssetRequestInfo.InternalRequest());
                }
                catch (AssetServerException e)
                {
                    m_log.ErrorFormat("[ARCHIVER]: Uploading asset {0} failed: {1}", assetID, e);
                }
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
            if (m_allowedUUIDs != null)
            {
                // this is a `load filtered` command, we need to filter based on the allowed whitelist
                UUID ownerID = m_scene.RegionInfo.EstateSettings.EstateOwner;
                if (MustReplaceByAsset(loadedRegionSettings.TerrainTexture1, ownerID, UUID.Zero))
                    currentRegionSettings.TerrainTexture1 = DEFAULT_TERRAIN_1;
                else
                    currentRegionSettings.TerrainTexture1 = loadedRegionSettings.TerrainTexture1;
                if (MustReplaceByAsset(loadedRegionSettings.TerrainTexture2, ownerID, UUID.Zero))
                    currentRegionSettings.TerrainTexture2 = DEFAULT_TERRAIN_2;
                else
                    currentRegionSettings.TerrainTexture2 = loadedRegionSettings.TerrainTexture2;
                if (MustReplaceByAsset(loadedRegionSettings.TerrainTexture3, ownerID, UUID.Zero))
                    currentRegionSettings.TerrainTexture3 = DEFAULT_TERRAIN_3;
                else
                    currentRegionSettings.TerrainTexture3 = loadedRegionSettings.TerrainTexture3;
                if (MustReplaceByAsset(loadedRegionSettings.TerrainTexture4, ownerID, UUID.Zero))
                    currentRegionSettings.TerrainTexture4 = DEFAULT_TERRAIN_4;
                else
                    currentRegionSettings.TerrainTexture4 = loadedRegionSettings.TerrainTexture4;
            }
            else
            {
                currentRegionSettings.TerrainTexture1 = loadedRegionSettings.TerrainTexture1;
                currentRegionSettings.TerrainTexture2 = loadedRegionSettings.TerrainTexture2;
                currentRegionSettings.TerrainTexture3 = loadedRegionSettings.TerrainTexture3;
                currentRegionSettings.TerrainTexture4 = loadedRegionSettings.TerrainTexture4;
            }
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
