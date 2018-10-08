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
 *     * Neither the name of the OpenSimulator Project nor the
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
using System.Reflection;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

using log4net;
using Nini.Config;
using Mono.Addins;

using OpenMetaverse;
using OpenMetaverse.Messages.Linden;
using OpenMetaverse.StructuredData;
using OpenMetaverse.Assets;

using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Framework.Communications.Capabilities;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;

using OpenSim.Region.Physics.Manager;

using Caps = OpenSim.Framework.Communications.Capabilities.Caps;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;
using OSDArray = OpenMetaverse.StructuredData.OSDArray;
using Amib.Threading;

namespace OpenSim.Region.CoreModules.Capabilities
{
    public delegate void UpLoadedAsset(
        string assetName, string description, UUID assetID, UUID inventoryItem, UUID parentFolder,
        byte[] data, string inventoryType, string assetType, PermissionMask nextOwnerPerm, PermissionMask groupPerm, PermissionMask everyonePerm);

    public delegate UpdateItemResponse UpdateItem(UUID itemID, byte[] data);

    public delegate UpdateItemResponse UpdateTaskScript(UUID itemID, UUID primID, bool isScriptRunning, byte[] data); 

    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule")]
    public class InventoryCapsModule : INonSharedRegionModule
    {

        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene m_Scene;

        #region INonSharedRegionModule

        public string Name
        {
            get { return "InventoryCapsModule"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void Initialize(IConfigSource source)
        {
        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
            m_Scene = scene;
        }

        public void RemoveRegion(Scene scene)
        {
            m_Scene.EventManager.OnRegisterCaps -= OnRegisterCaps;
            m_Scene.EventManager.OnDeregisterCaps -= OnDeregisterCaps;
        }

        public void RegionLoaded(Scene scene)
        {
            m_Scene.EventManager.OnRegisterCaps += OnRegisterCaps;
            m_Scene.EventManager.OnDeregisterCaps += OnDeregisterCaps;
        }

        public void PostInitialize()
        {
        }

        #endregion

        private Dictionary<UUID, InventoryCapsHandler> m_userInventoryHandlers = new Dictionary<UUID, InventoryCapsHandler>();

        private void OnRegisterCaps(UUID agentID, Caps caps)
        {
            InventoryCapsHandler handler = new InventoryCapsHandler(m_Scene, agentID, caps);
            handler.RegisterHandlers();

            lock (m_userInventoryHandlers)
            {
                m_userInventoryHandlers[agentID] = handler;
            }
        }

        private void OnDeregisterCaps(UUID agentID, Caps caps)
        {
            InventoryCapsHandler handler;
            lock (m_userInventoryHandlers)
            {
                if (m_userInventoryHandlers.TryGetValue(agentID, out handler))
                {
                    m_userInventoryHandlers.Remove(agentID);
                }
            }

            if (handler != null)
            {
                handler.Close();
            }
        }



        protected class InventoryCapsHandler
        {
            private static readonly string m_newInventory = "0002/";
            private static readonly string m_notecardUpdatePath = "0004/";
            private static readonly string m_notecardTaskUpdatePath = "0005/";
            private static readonly string m_fetchInventoryPath = "0006/";

            private UUID m_agentID;
            private Caps m_Caps;
            private Scene m_Scene;
            private IHttpServer m_httpServer;
            private string m_regionName;
            private IAssetCache m_assetCache;
            private InventoryFolderImpl m_libraryFolder = null;
            private IInventoryProviderSelector m_inventoryProviderSelector;
            private ICheckedInventoryStorage m_checkedStorageProvider;

            private SmartThreadPool m_inventoryPool = new SmartThreadPool(60 * 1000, 2, 0);

            public InventoryCapsHandler(Scene scene, UUID agentID, Caps caps)
            {
                m_agentID = agentID;
                m_Caps = caps;
                m_Scene = scene;
                m_httpServer = m_Caps.HttpListener;
                m_regionName = m_Scene.RegionInfo.RegionName;
                m_assetCache = m_Scene.CommsManager.AssetCache;
                m_inventoryProviderSelector = ProviderRegistry.Instance.Get<IInventoryProviderSelector>();
                m_checkedStorageProvider = m_inventoryProviderSelector.GetCheckedProvider(m_Caps.AgentID);
                m_libraryFolder = m_Scene.CommsManager.LibraryRoot;

                m_inventoryPool.Name = "Inventory Caps " + agentID; 
            }

            /// <summary>
            /// Register a bunch of CAPS http service handlers
            /// </summary>
            public void RegisterHandlers()
            {
                try
                {
                    IRequestHandler requestHandler;

                    requestHandler = new RestStreamHandler("POST", m_Caps.CapsBase + m_notecardTaskUpdatePath, ScriptTaskInventory);
                    m_Caps.RegisterHandler("UpdateScriptTaskInventory", requestHandler);
                    m_Caps.RegisterHandler("UpdateScriptTask", requestHandler);

                    requestHandler = new RestStreamHandler("POST", m_Caps.CapsBase + m_notecardUpdatePath, NoteCardAgentInventory);
                    m_Caps.RegisterHandler("UpdateNotecardAgentInventory", requestHandler);
                    m_Caps.RegisterHandler("UpdateScriptAgentInventory", requestHandler);
                    m_Caps.RegisterHandler("UpdateScriptAgent", requestHandler);

                    requestHandler = new RestStreamHandler("POST", m_Caps.CapsBase + "/NewFileAgentInventory/", NewAgentInventoryRequest);
                    m_Caps.RegisterHandler("NewFileAgentInventory", requestHandler);
                    //requestHandler = new RestStreamHandler("POST", m_Caps.CapsBase + "/NewFileAgentInventoryVariablePrice/", NewAgentInventoryRequestVariablePrice);
                    //m_Caps.RegisterHandler("NewFileAgentInventoryVariablePrice", requestHandler);

                    requestHandler = new AsyncRequestHandler("POST", m_Caps.CapsBase + m_fetchInventoryPath, AsyncFetchInventoryDescendents);
                    m_Caps.RegisterHandler("FetchInventoryDescendents", requestHandler);
                    m_Caps.RegisterHandler("WebFetchInventoryDescendents", requestHandler); 
                    m_Caps.RegisterHandler("FetchInventoryDescendents2", requestHandler);
                    m_Caps.RegisterHandler("FetchLibDescendents", requestHandler);
                    m_Caps.RegisterHandler("FetchLibDescendents2", requestHandler);

                    requestHandler = new RestStreamHandler("POST", "/CAPS/" + UUID.Random(), FetchInventoryRequest);
                    m_Caps.RegisterHandler("FetchInventory", requestHandler);
                    m_Caps.RegisterHandler("FetchInventory2", requestHandler);

                    requestHandler = new RestStreamHandler("POST", "/CAPS/" + UUID.Random(), FetchLibraryRequest);
                    m_Caps.RegisterHandler("FetchLib", requestHandler);
                    m_Caps.RegisterHandler("FetchLib2", requestHandler);

                    requestHandler = new RestStreamHandler("POST", "/CAPS/" + UUID.Random(), CopyInventoryFromNotecard);
                    m_Caps.RegisterHandler("CopyInventoryFromNotecard", requestHandler);

                    //requestHandler = new RestStreamHandler("POST", m_Caps.CapsBase + UUID.Random(), CreateInventoryCategory);
                    //m_Caps.RegisterHandler("CreateInventoryCategory", requestHandler);
                }
                catch (Exception e)
                {
                    m_log.Error("[CAPS]: " + e.ToString());
                }
            }

            /// <summary>
            /// Called when new asset data for an agent inventory item update has been uploaded.
            /// </summary>
            /// <param name="itemID">Item to update</param>
            /// <param name="data">New asset data</param>
            /// <returns></returns>
            public UpdateItemResponse ItemUpdated(UUID itemID, byte[] data)
            {
                return (m_Scene.CapsUpdateInventoryItemAsset(m_Caps.AgentID, itemID, data));
            }


            /// <summary>
            /// Handle a request from the client for a Uri to upload a baked texture.
            /// </summary>
            /// <param name="request"></param>
            /// <param name="path"></param>
            /// <param name="param"></param>
            /// <param name="httpRequest"></param>
            /// <param name="httpResponse"></param>
            /// <returns>The upload response if the request is successful, null otherwise.</returns>
            public string ScriptTaskInventory(
                string request, string path, string param, OSHttpRequest httpRequest, OSHttpResponse httpResponse)
            {
                m_log.Debug("[CAPS]: ScriptTaskInventory Request in region: " + m_regionName);
                //m_log.DebugFormat("[CAPS]: request: {0}, path: {1}, param: {2}", request, path, param);

                try
                {
                    string capsBase = m_Caps.CapsBase;
                    string uploaderPath = Util.RandomClass.Next(5000, 8000).ToString("0000");

                    Hashtable hash = (Hashtable)LLSD.LLSDDeserialize(Utils.StringToBytes(request));
                    LLSDTaskScriptUpdate llsdUpdateRequest = new LLSDTaskScriptUpdate();
                    LLSDHelpers.DeserializeOSDMap(hash, llsdUpdateRequest);

                    TaskInventoryScriptUpdater uploader =
                        new TaskInventoryScriptUpdater(
                            llsdUpdateRequest.item_id,
                            llsdUpdateRequest.task_id,
                            llsdUpdateRequest.is_script_running,
                            capsBase + uploaderPath,
                            m_httpServer);

                    uploader.OnUpLoad += TaskScriptUpdated;

                    m_httpServer.AddStreamHandler(new BinaryStreamHandler("POST", capsBase + uploaderPath, uploader.uploaderCaps));

                    string uploaderURL = m_httpServer.ServerURI + capsBase + uploaderPath;
                    LLSDAssetUploadResponse uploadResponse = new LLSDAssetUploadResponse();
                    uploadResponse.uploader = uploaderURL;
                    uploadResponse.state = "upload";

                    // m_log.InfoFormat("[CAPS]: " +"ScriptTaskInventory response: {0}", LLSDHelpers.SerializeLLSDReply(uploadResponse)));

                    return LLSDHelpers.SerializeLLSDReply(uploadResponse);

                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("[UPLOAD SCRIPT TASK HANDLER]: {0}{1}", e.Message, e.StackTrace);
                }

                return null;
            }

            /// <summary>
            /// Called when new asset data for an agent inventory item update has been uploaded.
            /// </summary>
            /// <param name="itemID">Item to update</param>
            /// <param name="primID">Prim containing item to update</param>
            /// <param name="isScriptRunning">Signals whether the script to update is currently running</param>
            /// <param name="data">New asset data</param>
            public UpdateItemResponse TaskScriptUpdated(UUID itemID, UUID primID, bool isScriptRunning, byte[] data)
            {
                return (m_Scene.CapsUpdateTaskInventoryScriptAsset(m_agentID, itemID, primID, isScriptRunning, data));
            }

            /// <summary>
            /// Called by the notecard update handler.  Provides a URL to which the client can upload a new asset.
            /// </summary>
            /// <param name="request"></param>
            /// <param name="path"></param>
            /// <param name="param"></param>
            /// <returns></returns>
            public string NoteCardAgentInventory(string request, string path, string param,
                                                 OSHttpRequest httpRequest, OSHttpResponse httpResponse)
            {
                //m_log.Debug("[CAPS]: NoteCardAgentInventory Request in region: " + m_regionName + "\n" + request);
                //m_log.Debug("[CAPS]: NoteCardAgentInventory Request is: " + request);

                //OpenMetaverse.StructuredData.OSDMap hash = (OpenMetaverse.StructuredData.OSDMap)OpenMetaverse.StructuredData.LLSDParser.DeserializeBinary(Utils.StringToBytes(request));
                Hashtable hash = (Hashtable)LLSD.LLSDDeserialize(Utils.StringToBytes(request));
                LLSDItemUpdate llsdRequest = new LLSDItemUpdate();
                LLSDHelpers.DeserializeOSDMap(hash, llsdRequest);

                string capsBase = m_Caps.CapsBase;
                string uploaderPath = Util.RandomClass.Next(5000, 8000).ToString("0000");

                ItemUpdater uploader =
                    new ItemUpdater(llsdRequest.item_id, capsBase + uploaderPath, m_httpServer);

                uploader.OnUpLoad += ItemUpdated;

                m_httpServer.AddStreamHandler(new BinaryStreamHandler("POST", capsBase + uploaderPath, uploader.uploaderCaps));
                string uploaderURL = m_httpServer.ServerURI + capsBase + uploaderPath;

                LLSDAssetUploadResponse uploadResponse = new LLSDAssetUploadResponse();
                uploadResponse.uploader = uploaderURL;
                uploadResponse.state = "upload";

                //            m_log.InfoFormat("[CAPS]: " +
                //                             "NoteCardAgentInventory response: {0}",
                //                             LLSDHelpers.SerializeLLSDReply(uploadResponse)));

                return LLSDHelpers.SerializeLLSDReply(uploadResponse);
            }

            /// <summary>
            /// Calculate (possibly variable priced) charges.
            /// </summary>
            /// <param name="asset_type"></param>
            /// <param name="map"></param>
            /// <param name="charge"></param>
            /// <param name="resourceCost"></param>
            /// <returns>upload charge to user</returns>
            private void CalculateCosts(IMoneyModule mm, string asset_type, OSDMap map, out int uploadCost, out int resourceCost)
            {
                uploadCost = 0;
                resourceCost = 0;

                if (mm != null)
                {
                    int upload_price = mm.GetEconomyData().PriceUpload;

                    if (asset_type == "texture" ||
                        asset_type == "animation" ||
                        asset_type == "snapshot" ||
                        asset_type == "sound")
                    {
                        uploadCost = upload_price;
                        resourceCost = 0;
                    }
                    else if (asset_type == "mesh" ||
                             asset_type == "object")
                    {
                        OSDMap meshMap = (OSDMap)map["asset_resources"];
                        int meshCount = meshMap.ContainsKey("mesh_list") ?
                            ((OSDArray)meshMap["mesh_list"]).Count : 0;
                        int textureCount = meshMap.ContainsKey("texture_list") ?
                            ((OSDArray)meshMap["texture_list"]).Count : 0;

                        uploadCost = mm.MeshUploadCharge(meshCount, textureCount);

                        // simplified resource cost, for now
                        // see http://wiki.secondlife.com/wiki/Mesh/Mesh_physics for LL implementation
                        resourceCost = meshCount * upload_price;
                    }
                }
            }

            private void ApplyMeshCharges(UUID agentID, int meshCount, int textureCount)
            {
                IMoneyModule mm = m_Scene.RequestModuleInterface<IMoneyModule>();

                if (mm != null)
                    mm.ApplyMeshUploadCharge(agentID, meshCount, textureCount);
            }

            /// <summary>
            /// Handle the NewAgentInventory Upload request.
            /// </summary>
            /// <param name="request"></param>
            /// <param name="path"></param>
            /// <param name="param"></param>
            /// <param name="httpRequest"></param>
            /// <param name="httpResponse"></param>
            /// <returns></returns>
            public string NewAgentInventoryRequest(string request, string path, string param, OSHttpRequest httpRequest, OSHttpResponse httpResponse)
            {
                OSDMap map = (OSDMap)OSDParser.DeserializeLLSDXml(Utils.StringToBytes(request));
                
                string asset_type = map["asset_type"].AsString();
                int uploadCost = 0;
                int resourceCost = 0;

                // Check to see if mesh uploads are enabled.
                if (asset_type == "mesh")
                {
                    ISimulatorFeaturesModule m_SimulatorFeatures = m_Scene.RequestModuleInterface<ISimulatorFeaturesModule>();
                    if ((m_SimulatorFeatures == null) || (m_SimulatorFeatures.MeshEnabled == false))
                    {
                        OSDMap errorResponse = new OSDMap();
                        errorResponse["uploader"] = String.Empty;
                        errorResponse["state"] = "error";
                        return (errorResponse);
                    }
                }

                string assetName = map["name"].AsString();
                string assetDes = map["description"].AsString();
                UUID parentFolder = map["folder_id"].AsUUID();
                string inventory_type = map["inventory_type"].AsString();

                PermissionMask nextOwnerPerm = PermissionMask.All;
                if (map.ContainsKey("next_owner_mask")) nextOwnerPerm = (PermissionMask)map["next_owner_mask"].AsUInteger();
                PermissionMask groupPerm = PermissionMask.None;
                if (map.ContainsKey("group_mask")) groupPerm = (PermissionMask)map["group_mask"].AsUInteger();
                PermissionMask everyonePerm = PermissionMask.None;
                if (map.ContainsKey("everyone_mask")) everyonePerm = (PermissionMask)map["everyone_mask"].AsUInteger();

                UUID newAsset = UUID.Random();
                UUID newInvItem = UUID.Random();

                IMoneyModule mm = m_Scene.RequestModuleInterface<IMoneyModule>();
                CalculateCosts(mm, asset_type, map, out uploadCost, out resourceCost);

                IClientAPI client = m_Scene.SceneContents.GetControllingClient(m_agentID);
                if ((mm == null) || !mm.AmountCovered(client.AgentId, uploadCost))
                {
                    if (client != null)
                    {
                        if (mm == null)
                            client.SendAgentAlertMessage("Unable to upload asset. Missing money module.", false);
                        else
                            client.SendAgentAlertMessage("Unable to upload asset. Insufficient funds.", false);
                    }

                    OSDMap errorResponse = new OSDMap();
                    errorResponse["uploader"] = String.Empty;
                    errorResponse["state"] = "error";
                    return OSDParser.SerializeLLSDXmlString(errorResponse);
                }
                
                // Build the response
                OSDMap uploadResponse = new OSDMap();

                try
                {
                    // Handle mesh asset "data" block
                    if (asset_type == "mesh")
                    {
                        OSDMap meshMap = (OSDMap)map["asset_resources"];

                        OSDArray mesh_list = (OSDArray)meshMap["mesh_list"];
                        OSDArray instance_list = (OSDArray)meshMap["instance_list"];
                        float streaming_cost = 0.0f;
                        float server_weight = 0.0f;

                        for (int i = 0; i < mesh_list.Count; i++)
                        {
                            OSDMap inner_instance_list = (OSDMap)instance_list[i];
                            byte[] mesh_data = mesh_list[i].AsBinary();
                            Vector3 scale = inner_instance_list["scale"].AsVector3();

                            int vertex_count = 0;
                            int hibytes = 0;
                            int midbytes = 0;
                            int lowbytes = 0;
                            int lowestbytes = 0;

                            SceneObjectPartMeshCost.GetMeshVertexCount(mesh_data, out vertex_count);
                            SceneObjectPartMeshCost.GetMeshLODByteCounts(mesh_data, out hibytes, out midbytes, out lowbytes, out lowestbytes);

                            server_weight += PrimitiveBaseShape.GetServerWeight(vertex_count);
                            streaming_cost += PrimitiveBaseShape.GetStreamingCost(scale, hibytes, midbytes, lowbytes, lowestbytes);
                        }

                        OSDMap data = new OSDMap();
                        data["resource_cost"] = Math.Min(server_weight, streaming_cost);
                        data["model_streaming_cost"] = streaming_cost;
                        data["simulation_cost"] = server_weight;
                        data["physics_cost"] = 0.0f;

                        uploadResponse["data"] = data;
                    }
                }
                catch (Exception)
                {
                    OSDMap errorResponse = new OSDMap();
                    errorResponse["uploader"] = String.Empty;
                    errorResponse["state"] = "error";
                    return OSDParser.SerializeLLSDXmlString(errorResponse);
                }

                string uploaderPath = Util.RandomClass.Next(5000, 8000).ToString("0000");
                string uploaderURL = m_Caps.HttpListener.ServerURI + m_Caps.CapsBase + uploaderPath;
                AssetUploader uploader =
                    new AssetUploader(assetName, assetDes, newAsset, newInvItem, parentFolder, inventory_type,
                                      asset_type, nextOwnerPerm, groupPerm, everyonePerm, 
                                      m_Caps.CapsBase + uploaderPath, m_Caps.HttpListener);
                uploader.OnUpLoad += UploadCompleteHandler;

                m_Caps.HttpListener.AddStreamHandler(
                    new BinaryStreamHandler("POST", m_Caps.CapsBase + uploaderPath, uploader.uploaderCaps));

                uploadResponse["uploader"] = uploaderURL;
                uploadResponse["state"] = "upload";
                uploadResponse["resource_cost"] = resourceCost;
                uploadResponse["upload_price"] = uploadCost;

                return OSDParser.SerializeLLSDXmlString(uploadResponse);
            }

            /// <summary>
            /// Upload Complete CAP handler. Instantiated from the upload request above.
            /// </summary>
            /// <param name="assetName"></param>
            /// <param name="assetDescription"></param>
            /// <param name="assetID"></param>
            /// <param name="inventoryItem"></param>
            /// <param name="parentFolder"></param>
            /// <param name="data"></param>
            /// <param name="inventoryType"></param>
            /// <param name="assetType"></param>
            public void UploadCompleteHandler(string assetName, string assetDescription, UUID assetID,
                                              UUID inventoryItem, UUID parentFolder, byte[] data, string inventoryType,
                                              string assetType, PermissionMask nextOwnerPerm, PermissionMask groupPerm, PermissionMask everyonePerm)


            {
                sbyte assType = (sbyte)AssetType.Texture;
                sbyte inType = (sbyte)InventoryType.Texture;

                if (inventoryType == "sound")
                {
                    inType = (sbyte)InventoryType.Sound;
                    assType = (sbyte)AssetType.Sound;
                }
                else if (inventoryType == "animation")
                {
                    inType = (sbyte)InventoryType.Animation;
                    assType = (sbyte)AssetType.Animation;
                }
                else if (inventoryType == "snapshot")
                {
                    inType = (sbyte)InventoryType.Snapshot;
                    assType = (sbyte)AssetType.Texture;
                }
                else if (inventoryType == "wearable")
                {
                    inType = (sbyte)InventoryType.Wearable;
                    switch (assetType)
                    {
                        case "bodypart":
                            assType = (sbyte)AssetType.Bodypart;
                            break;
                        case "clothing":
                            assType = (sbyte)AssetType.Clothing;
                            break;
                    }
                }
                else if (inventoryType == "object")
                {   
                    inType = (sbyte)InventoryType.Object;
                    assType = (sbyte)AssetType.Object;
                    data = ObjectUploadComplete(assetName, data);
                }

                // Bill for upload (mesh is separately billed).
                IMoneyModule mm = m_Scene.RequestModuleInterface<IMoneyModule>();
                if (mm != null)
                {
                    if (mm.UploadChargeApplies((AssetType)assType))
                        mm.ApplyUploadCharge(m_Caps.AgentID);
                }

                AssetBase asset;
                asset = new AssetBase();
                asset.FullID = assetID;
                asset.Type = assType;
                asset.Name = assetName;
                asset.Data = data;

                try
                {
                    if (m_assetCache != null)
                    {
                        m_assetCache.AddAsset(asset, AssetRequestInfo.InternalRequest());
                    }
                }
                catch (AssetServerException e)
                {
                    m_log.ErrorFormat("[CAPS UPLOAD] Asset write failed: {0}", e);
                    return;
                }

                InventoryItemBase item = new InventoryItemBase();
                item.Owner = m_Caps.AgentID;
                item.CreatorId = m_Caps.AgentID.ToString();
                item.ID = inventoryItem;
                item.AssetID = asset.FullID;
                item.Description = assetDescription;
                item.Name = assetName;
                item.AssetType = assType;
                item.InvType = inType;
                item.Folder = parentFolder;

                item.CreationDate = Util.UnixTimeSinceEpoch();

                item.BasePermissions = (uint)(PermissionMask.All | PermissionMask.Export);
                item.CurrentPermissions = (uint)(PermissionMask.All | PermissionMask.Export);
                item.GroupPermissions = (uint)groupPerm;
                item.EveryonePermissions = (uint)everyonePerm;
                item.NextPermissions = (uint)nextOwnerPerm;

                m_Scene.AddInventoryItem(m_Caps.AgentID, item);

                IClientAPI client = null;
                m_Scene.TryGetClient(m_Caps.AgentID, out client); 
                if (client != null)
                    client.SendInventoryItemCreateUpdate(item,0);
            }

            private byte[] ObjectUploadComplete(string assetName, byte[] data)
            {
                List<Vector3> positions = new List<Vector3>();
                List<Quaternion> rotations = new List<Quaternion>();
                OSDMap request = (OSDMap)OSDParser.DeserializeLLSDXml(data); 
                
                OSDArray instance_list = (OSDArray)request["instance_list"];
                OSDArray mesh_list = (OSDArray)request["mesh_list"];
                OSDArray texture_list = (OSDArray)request["texture_list"];
                SceneObjectGroup grp = null;

                InventoryFolderBase textureFolder = 
                    m_checkedStorageProvider.FindFolderForType(m_Caps.AgentID, AssetType.Texture);

                ScenePresence avatar;
                IClientAPI remoteClient = null;
                if (m_Scene.TryGetAvatar(m_Caps.AgentID, out avatar))
                    remoteClient = avatar.ControllingClient;

                List<UUID> textures = new List<UUID>();
                for (int i = 0; i < texture_list.Count; i++)
                {
                    AssetBase textureAsset = new AssetBase(UUID.Random(), assetName, (sbyte)AssetType.Texture, String.Empty);
                    textureAsset.Data = texture_list[i].AsBinary();

                    try
                    {
                        m_assetCache.AddAsset(textureAsset, AssetRequestInfo.GenericNetRequest());
                    }
                    catch (AssetServerException)
                    {
                        if (remoteClient != null) remoteClient.SendAgentAlertMessage("Unable to upload asset. Please try again later.", false);
                        throw;
                    }

                    textures.Add(textureAsset.FullID);

                    if (textureFolder == null)
                        continue;

                    InventoryItemBase item = 
                        new InventoryItemBase(UUID.Random(), m_Caps.AgentID)
                        {
                            AssetType = (int)AssetType.Texture,
                            AssetID = textureAsset.FullID,
                            CreatorId = m_Caps.AgentID.ToString(),
                            CreationDate = Util.UnixTimeSinceEpoch(),
                            Folder = textureFolder.ID,
                            InvType = (int)InventoryType.Texture,
                            Name = assetName + " - " + i.ToString(),
                            BasePermissions = (uint)(PermissionMask.All | PermissionMask.Export),
                            CurrentPermissions = (uint)(PermissionMask.All | PermissionMask.Export),
                            GroupPermissions = (uint)PermissionMask.None,
                            EveryonePermissions = (uint)PermissionMask.None,
                            NextPermissions =  (uint)PermissionMask.All
                        };

                    m_Scene.AddInventoryItem(m_Caps.AgentID, item);

                    if (remoteClient != null)
                        remoteClient.SendBulkUpdateInventory(item);
                }

                for (int i = 0; i < mesh_list.Count; i++)
                {
                    PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();

                    Primitive.TextureEntry textureEntry
                        = new Primitive.TextureEntry(Primitive.TextureEntry.WHITE_TEXTURE);
                    OSDMap inner_instance_list = (OSDMap)instance_list[i];
                    byte[] mesh_data = mesh_list[i].AsBinary();

                    OSDArray face_list = (OSDArray)inner_instance_list["face_list"];
                    for (uint face = 0; face < face_list.Count; face++)
                    {
                        OSDMap faceMap = (OSDMap)face_list[(int)face];
                        Primitive.TextureEntryFace f = pbs.Textures.CreateFace(face);
                        if (faceMap.ContainsKey("fullbright"))
                            f.Fullbright = faceMap["fullbright"].AsBoolean();
                        if (faceMap.ContainsKey("diffuse_color"))
                            f.RGBA = faceMap["diffuse_color"].AsColor4();

                        int textureNum = faceMap["image"].AsInteger();
                        float imagerot = faceMap["imagerot"].AsInteger();
                        float offsets = (float)faceMap["offsets"].AsReal();
                        float offsett = (float)faceMap["offsett"].AsReal();
                        float scales = (float)faceMap["scales"].AsReal();
                        float scalet = (float)faceMap["scalet"].AsReal();

                        if (imagerot != 0)
                            f.Rotation = imagerot;
                        if (offsets != 0)
                            f.OffsetU = offsets;
                        if (offsett != 0)
                            f.OffsetV = offsett;
                        if (scales != 0)
                            f.RepeatU = scales;
                        if (scalet != 0)
                            f.RepeatV = scalet;

                        if (textures.Count > textureNum)
                            f.TextureID = textures[textureNum];
                        else
                            f.TextureID = Primitive.TextureEntry.WHITE_TEXTURE;

                        textureEntry.FaceTextures[face] = f;
                    }
                    pbs.Textures = textureEntry;

                    AssetBase meshAsset = new AssetBase(UUID.Random(), assetName, (sbyte)AssetType.Mesh, String.Empty);
                    meshAsset.Data = mesh_data;

                    try
                    {
                        m_assetCache.AddAsset(meshAsset, AssetRequestInfo.GenericNetRequest());
                    }
                    catch (AssetServerException)
                    {
                        if (remoteClient != null) remoteClient.SendAgentAlertMessage("Unable to upload asset. Please try again later.", false);
                        throw;
                    }

                    pbs.SculptEntry = true;
                    pbs.SculptType = (byte)SculptType.Mesh;
                    pbs.SculptTexture = meshAsset.FullID;
                    pbs.SculptData = mesh_data;
                    pbs.Scale = inner_instance_list["scale"].AsVector3();

                    int vertex_count = 0;
                    int hibytes = 0;
                    int midbytes = 0;
                    int lowbytes = 0;
                    int lowestbytes = 0;

                    SceneObjectPartMeshCost.GetMeshVertexCount(mesh_data, out vertex_count);
                    SceneObjectPartMeshCost.GetMeshLODByteCounts(mesh_data, out hibytes, out midbytes, out lowbytes, out lowestbytes);

                    pbs.VertexCount = vertex_count;
                    pbs.HighLODBytes = hibytes;
                    pbs.MidLODBytes = midbytes;
                    pbs.LowLODBytes = lowbytes;
                    pbs.LowestLODBytes = lowestbytes;

                    Vector3 position = inner_instance_list["position"].AsVector3();
                    Quaternion rotation = inner_instance_list["rotation"].AsQuaternion();

                    UUID owner_id = m_Caps.AgentID;
                    
                    //m_log.DebugFormat("[MESH] Meshing high_lod returned vertex count of {0} at index {1}", vertex_count, i);
                    SceneObjectPart prim = new SceneObjectPart(owner_id, pbs, position, Quaternion.Identity, Vector3.Zero, false);

                    prim.Name = assetName;
                    prim.Description = String.Empty;

                    rotations.Add(rotation);
                    positions.Add(position);

                    prim.UUID = UUID.Random();
                    prim.CreatorID = owner_id;
                    prim.OwnerID = owner_id;
                    prim.GroupID = UUID.Zero;
                    prim.LastOwnerID = prim.OwnerID;
                    prim.CreationDate = Util.UnixTimeSinceEpoch();
                    prim.ServerWeight = pbs.GetServerWeight();
                    prim.StreamingCost = pbs.GetStreamingCost();

                    if (grp == null)
                        grp = new SceneObjectGroup(prim);
                    else
                        grp.AddPart(prim);
                }

                Vector3 rootPos = positions[0];
                // Fix first link number
                if (mesh_list.Count > 1)
                {
                    Quaternion rootRotConj = Quaternion.Conjugate(rotations[0]);
                    Quaternion tmprot;
                    Vector3 offset;

                    // Fix child rotations and positions
                    foreach (SceneObjectPart part in grp.GetParts())
                    {
                        // The part values are likely to come out of order from the collection,
                        // but the positions and rotations arrays are ordered by link number.
                        int i = part.LinkNum;
                        if (i > 0) i--; // need to convert to 0-based except for single-prim
                        if (i == 0)
                            continue;   // the root prim does not need an adjustment
                        tmprot = rotations[i];
                        tmprot = rootRotConj * tmprot;
                        part.RotationOffset = tmprot;
                        offset = positions[i] - rootPos;
                        offset *= rootRotConj;
                        part.OffsetPosition = offset;
                    }

                    grp.AbsolutePosition = rootPos;
                    grp.UpdateGroupRotation(rotations[0], false);
                }
                else
                {
                    grp.AbsolutePosition = rootPos;
                    grp.UpdateGroupRotation(rotations[0], false);
                }

                // This also notifies the user.
                ApplyMeshCharges(m_Caps.AgentID, mesh_list.Count, texture_list.Count);

                ISerializationEngine engine;
                if (ProviderRegistry.Instance.TryGet<ISerializationEngine>(out engine))
                {
                    return engine.InventoryObjectSerializer.SerializeGroupToInventoryBytes(grp, SerializationFlags.None);
                }
                else
                {
                    return Utils.StringToBytes(SceneObjectSerializer.ToOriginalXmlFormat(grp, StopScriptReason.None));
                }
            }

            const ulong INVENTORY_FETCH_TIMEOUT = 60 * 1000;

            public void Close()
            {
                m_inventoryPool.Shutdown(false, 0);
            }

            private void AsyncFetchInventoryDescendents(IHttpServer server, string path, OSHttpRequest httpRequest, OSHttpResponse httpResponse)
            {
                AsyncHttpRequest request = new AsyncHttpRequest(server, httpRequest, httpResponse, m_agentID, null, 0);

                m_inventoryPool.QueueWorkItem((AsyncHttpRequest req) =>
                    {
                        try
                        {
                            ulong age = Util.GetLongTickCount() - req.RequestTime;
                            if (age >= INVENTORY_FETCH_TIMEOUT)
                            {
                                SendFetchTimeout(req, age);
                                return;
                            }

                            byte[] ret = this.FetchInventoryDescendentsRequest((string)req.RequestData["body"],
                                (string)req.RequestData["uri"], String.Empty, req.HttpRequest, req.HttpResponse);

                            var respData = new Hashtable();
                            respData["response_binary"] = ret;
                            respData["int_response_code"] = 200;
                            respData["content_type"] = "application/llsd+xml";

                            req.SendResponse(respData);
                        }
                        catch (Exception e)
                        {
                            m_log.ErrorFormat("[CAPS/INVENTORY]: HandleAsyncFetchInventoryDescendents threw an exception: {0}", e);
                            var respData = new Hashtable();
                            respData["response_binary"] = new byte[0];
                            respData["int_response_code"] = 500;
                            req.SendResponse(respData);
                        }
                    },

                    request
                );
            }

            private void SendFetchTimeout(AsyncHttpRequest pRequest, ulong age)
            {
                var timeout = new Hashtable();
                timeout["response_binary"] = new byte[0];
                timeout["int_response_code"] = 504; //gateway timeout

                m_log.WarnFormat("[CAPS/INVENTORY]: HandleAsyncFetchInventoryDescendents: Request was too old to be processed {0}ms for {1}", age, m_agentID);

                pRequest.SendResponse(timeout);
            }

            /// <summary>
            /// Stores a list of folders we've blacklisted with a fail count and timestamp
            /// </summary>
            private Dictionary<UUID, TimestampedItem<int>> m_blackListedFolders = new Dictionary<UUID, TimestampedItem<int>>();
            private const int MAX_FOLDER_FAIL_COUNT = 5;
            private const int FOLDER_FAIL_TRACKING_TIME = 15 * 60; //15 minutes

            public byte[] FetchInventoryDescendentsRequest(
                string request, string path, string param, OSHttpRequest httpRequest, OSHttpResponse httpResponse)
            {
                OpenMetaverse.StructuredData.OSDMap map = (OpenMetaverse.StructuredData.OSDMap)OSDParser.DeserializeLLSDXml(request);
                OpenMetaverse.StructuredData.OSDArray osdFoldersRequested = (OpenMetaverse.StructuredData.OSDArray)map["folders"];

                // m_log.ErrorFormat("[CAPS/INVENTORY] Handling a FetchInventoryDescendents Request for {0}: {1}", m_Caps.AgentID, map.ToString());

                LLSDSerializationDictionary contents = new LLSDSerializationDictionary();
                contents.WriteStartMap("llsd"); //Start llsd

                contents.WriteKey("folders"); //Start array items
                contents.WriteStartArray("folders"); //Start array folders

                foreach (OSD folderInfo in osdFoldersRequested)
                {
                    OpenMetaverse.StructuredData.OSDMap fiMap = folderInfo as OpenMetaverse.StructuredData.OSDMap;
                    if (fiMap == null)
                        continue;

                    UUID folderId = fiMap["folder_id"].AsUUID();
                    bool fetchItems = fiMap["fetch_items"].AsBoolean();
                    bool fetchFolders = fiMap["fetch_folders"].AsBoolean();
                    int count = 0;

                    InventoryFolderBase folder = null;
                    
                    try
                    {
                        if (folderId == UUID.Zero)
                        {
                            //indicates the client wants the root for this user
                            folder = m_checkedStorageProvider.FindFolderForType(m_Caps.AgentID, (AssetType)FolderType.Root);
                            folderId = folder.ID;
                        }
                        else
                        {
                            lock (m_blackListedFolders)
                            {
                                TimestampedItem<int> entry;
                                if (m_blackListedFolders.TryGetValue(folderId, out entry))
                                {
                                    if (entry.ElapsedSeconds > FOLDER_FAIL_TRACKING_TIME)
                                    {
                                        m_blackListedFolders.Remove(folderId);
                                    }
                                    else
                                    {
                                        if (entry.Item >= MAX_FOLDER_FAIL_COUNT)
                                        {
                                            //we're at the fail threshold. return a fake folder
                                            folder = new InventoryFolderBase { ID = folderId, Name = "[unable to load folder]", Owner = m_agentID };

                                            m_log.ErrorFormat("[CAPS/INVENTORY]: Fail threshold reached for {0}, sending empty folder", folderId);
                                        }
                                    }
                                }
                            }

                            if (folder == null)
                            {
                                // See if its a library folder
                                if (m_libraryFolder != null)
                                    folder = m_libraryFolder.FindFolder(folderId);

                                if (folder == null)
                                {
                                    // Nope, Look for it in regular folders
                                    folder = m_checkedStorageProvider.GetFolder(m_Caps.AgentID, folderId);
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat("[CAPS/INVENTORY] Could not retrieve requested folder {0} for {1}: {2}",
                            folderId, m_Caps.AgentID, e);

                        if (folderId != UUID.Zero)
                        {
                            lock (m_blackListedFolders)
                            {
                                TimestampedItem<int> entry;
                                if (m_blackListedFolders.TryGetValue(folderId, out entry))
                                {
                                    entry.ResetTimestamp();
                                    entry.Item = entry.Item + 1;
                                }
                                else
                                {
                                    m_blackListedFolders.Add(folderId, new TimestampedItem<int>(1));
                                }
                            }
                        }

                        continue;
                    }

                    contents.WriteStartMap("internalContents"); //Start internalContents kvp

                    //Set the normal stuff
                    contents["agent_id"] = folder.Owner;
                    contents["owner_id"] = folder.Owner;
                    contents["folder_id"] = folder.ID;

                    contents.WriteKey("items"); //Start array items
                    contents.WriteStartArray("items");

                    List<UUID> linkedFolders = new List<UUID>();

                    if (fetchItems)
                    {
                        foreach (InventoryItemBase item in folder.Items)
                        {
                            if (item.AssetType == (int)AssetType.LinkFolder)
                            {
                                // Add this when we do categories below
                                linkedFolders.Add(item.AssetID);
                            }
                            else if (item.AssetType == (int)AssetType.Link)
                            {
                                try 
                                {
                                    InventoryItemBase linkedItem = m_checkedStorageProvider.GetItem(m_agentID, item.AssetID, UUID.Zero);

                                    if (linkedItem != null)
                                    {
                                        linkedItem.SerializeToLLSD(contents);
                                    }
                                    else
                                    {
                                        m_log.ErrorFormat(
                                            "[CAPS/INVENTORY] Failed to resolve link to item {0} for {1}",
                                            item.AssetID, m_Caps.AgentID);
                                    }
                                    // Don't add it to the count. It was accounted for with the link.
                                    //count++;
                                }
                                catch (Exception e)
                                {
                                    m_log.ErrorFormat(
                                        "[CAPS/INVENTORY] Failed to resolve link to item {0} for {1}: {2}",
                                        item.AssetID, m_Caps.AgentID, e.Message);
                                }
                            }

                            // Now that we've sent the original to the viewer, and it has created a dummy 
                            // node for the parent if it's new to the viewer, we can send the actual link
                            // to the item or folder it is now aware of.
                            item.SerializeToLLSD(contents);
                            count++; 
                        } 
                    }

                    contents.WriteEndArray(/*"items"*/); //end array items

                    contents.WriteKey("categories"); //Start array cats
                    contents.WriteStartArray("categories"); //We don't send any folders

                    // If there were linked folders include the folders referenced here
                    if (linkedFolders.Count > 0)
                    {
                        foreach (UUID linkedFolderID in linkedFolders)
                        {
                            try
                            {
                                InventoryFolderBase linkedFolder = m_checkedStorageProvider.GetFolderAttributes(m_agentID, linkedFolderID);
                                if (linkedFolder != null)
                                {
                                    linkedFolder.SerializeToLLSD(contents);
                                    // Don't add it to the count.. it was accounted for with the link.
                                    //count++;
                                }
                            }
                            catch (InventoryObjectMissingException)
                            {
                                m_log.ErrorFormat("[CAPS/INVENTORY] Failed to resolve link to folder {0} for {1}",
                                    linkedFolderID, m_agentID);
                            }
                            catch (Exception e)
                            {
                                m_log.ErrorFormat(
                                    "[CAPS/INVENTORY] Failed to resolve link to folder {0} for {1}: {2}",
                                    linkedFolderID, m_agentID, e);
                            }
                        }
                    }
                        
                    if (fetchFolders)
                    {
                        foreach (InventorySubFolderBase subFolder in folder.SubFolders)
                        {       
                            subFolder.SerializeToLLSD(contents, folder.ID);
                            count++;
                        }
                    }

                    contents.WriteEndArray(/*"categories"*/);

                    contents["descendents"] = count;
                    contents["version"] = folder.Version;

                    //Now add it to the folder array
                    contents.WriteEndMap(); //end array internalContents
                }

                contents.WriteEndArray(); //end array folders
                contents.WriteEndMap(/*"llsd"*/); //end llsd
                
                return (contents.GetSerializer());
            }

            /// <summary>
            /// Stores a list of items we've blacklisted with a fail count and timestamp
            /// </summary>
            private Dictionary<UUID, TimestampedItem<int>> m_BlacklistedItems = new Dictionary<UUID, TimestampedItem<int>>();
            private const int MAX_ITEM_FAIL_COUNT = 5;
            private const int ITEM_FAIL_TRACKING_TIME = 5 * 60; //5 minutes

            /// <summary>
            /// Tracks that there was an error fetching an item
            /// </summary>
            /// <param name="itemId"></param>
            private void AccountItemFetchFailure(UUID itemId)
            {
                lock (m_BlacklistedItems)
                {
                    TimestampedItem<int> entry;
                    if (m_BlacklistedItems.TryGetValue(itemId, out entry))
                    {
                        entry.ResetTimestamp();
                        entry.Item = entry.Item + 1;
                    }
                    else
                    {
                        //add this brand new one
                        m_BlacklistedItems.Add(itemId, new TimestampedItem<int>(1));

                        //also clean out old ones
                        System.Lazy<List<UUID>> deadKeys = new System.Lazy<List<UUID>>();
                        foreach (var kvp in m_BlacklistedItems)
                        {
                            if (kvp.Value.ElapsedSeconds > ITEM_FAIL_TRACKING_TIME)
                            {
                                deadKeys.Value.Add(kvp.Key);
                            }
                        }

                        if (deadKeys.IsValueCreated)
                        {
                            foreach (UUID id in deadKeys.Value)
                            {
                                m_BlacklistedItems.Remove(id);
                            }
                        }
                    }
                }
            }

            public string FetchInventoryRequest(string request, string path, string param, OSHttpRequest httpRequest, OSHttpResponse httpResponse)
            {
                //            m_log.DebugFormat("[FETCH INVENTORY HANDLER]: Received FetchInventory capabilty request");

                OSDMap requestmap = (OSDMap)OSDParser.DeserializeLLSDXml(Utils.StringToBytes(request));
                OSDArray itemsRequested = (OSDArray)requestmap["items"];
                string reply;
                LLSDFetchInventory llsdReply = new LLSDFetchInventory();
                llsdReply.agent_id = m_agentID;

                foreach (OSDMap osdItemId in itemsRequested)
                {
                    UUID itemId = osdItemId["item_id"].AsUUID();

                    if (itemId == UUID.Zero)
                        continue;   // don't bother

                    try
                    {
                        //see if we already know that this is a fail
                        lock (m_BlacklistedItems)
                        {
                            TimestampedItem<int> val;
                            if (m_BlacklistedItems.TryGetValue(itemId, out val))
                            {
                                //expired?
                                if (val.ElapsedSeconds > ITEM_FAIL_TRACKING_TIME)
                                {
                                    m_BlacklistedItems.Remove(itemId);
                                }
                                else
                                {
                                    if (val.Item >= MAX_ITEM_FAIL_COUNT)
                                    {
                                        //at the max fail count, don't even try to look this one up
                                        continue;
                                    }
                                }
                            }
                        }

                        InventoryItemBase item = m_checkedStorageProvider.GetItem(m_agentID, itemId, UUID.Zero);

                        if (item != null)
                        {
                            llsdReply.items.Array.Add(ConvertInventoryItem(item));
                        }
                        else
                        {
                            AccountItemFetchFailure(itemId);
                        }
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat("[CAPS/FETCH INVENTORY HANDLER] Could not retrieve requested inventory item {0} for {1}: {2}",
                            itemId, m_Caps.AgentID, e);

                        AccountItemFetchFailure(itemId);
                    }
                }

                reply = LLSDHelpers.SerializeLLSDReply(llsdReply);

                return reply;
            }


            public string FetchLibraryRequest(string request, string path, string param, OSHttpRequest httpRequest, OSHttpResponse httpResponse)
            {
                //            m_log.DebugFormat("[FETCH LIBRARY HANDLER]: Received FetchInventory capabilty request");

                OSDMap requestmap = (OSDMap)OSDParser.DeserializeLLSDXml(Utils.StringToBytes(request));
                OSDArray itemsRequested = null;
                string reply;

                LLSDFetchInventory llsdReply = new LLSDFetchInventory();

                if (requestmap.ContainsKey("items"))
                {
                    itemsRequested = (OSDArray)requestmap["items"];

                    foreach (OSDMap osdItemId in itemsRequested)
                    {
                        UUID itemId = osdItemId["item_id"].AsUUID();

                        try
                        {
                            InventoryItemBase item = m_libraryFolder.FindItem(itemId);

                            if (item != null)
                            {
                                llsdReply.agent_id = item.Owner;
                                llsdReply.items.Array.Add(ConvertInventoryItem(item));
                            }
                        }
                        catch (Exception e)
                        {
                            m_log.ErrorFormat(
                                "[CAPS/FETCH LIBRARY HANDLER] Could not retrieve requested library item {0} for {1}: {2}",
                                itemId, m_Caps.AgentID, e);
                        }
                    }
                } else
                {
                    // Cool VL seems to invoke this with only "agent_id" and "cap_name"=="FetchLib2" (perhaps for the root folder of the Library?)
                    // At any rate, this causes an exception and craps out the whole function with no handling.  
                    // Instead, we'll respond to Cool VL with an empty LLSDFetchInventory response, which has agent_id filled and empty items array.
                    if (requestmap.ContainsKey("cap_name") && requestmap.ContainsKey("agent_id"))
                    {
                        UUID agent_id = requestmap["agent_id"].AsUUID();
                        string cap_name = requestmap["cap_name"].AsString();
                        // Since we don't understand the request, and nothing was passed for requestmap["items"], 
                        // rather than crash with an exception, leave llsdReply.items as an empty array.
                        llsdReply.agent_id = agent_id;
                        m_log.ErrorFormat(
                            "[CAPS/FETCH LIBRARY HANDLER] Did not understand library request {0} for {1}", cap_name, agent_id);
                    }
                }

                reply = LLSDHelpers.SerializeLLSDReply(llsdReply);

                return reply;
            }

            /// <summary>
            /// Called by the CopyInventoryFromNotecard caps handler.
            /// </summary>
            /// <param name="request"></param>
            /// <param name="path"></param>
            /// <param name="param"></param>
            public string CopyInventoryFromNotecard(string request, string path, string param, OSHttpRequest httpRequest, OSHttpResponse httpResponse)
            {
                Hashtable response = new Hashtable();
                response["int_response_code"] = 404;
                response["content_type"] = "text/plain";
                response["str_response_string"] = String.Empty;

                IClientAPI client = null;
                m_Scene.TryGetClient(m_Caps.AgentID, out client); 
                if (client == null)
                    return LLSDHelpers.SerializeLLSDReply(response);

                try
                {        
                    OSDMap content = (OSDMap)OSDParser.DeserializeLLSDXml(request);
                    UUID objectID = content["object-id"].AsUUID();
                    UUID notecardID = content["notecard-id"].AsUUID();
                    UUID folderID = content["folder-id"].AsUUID();
                    UUID itemID = content["item-id"].AsUUID();

                    // m_log.InfoFormat("[CAPS]: CopyInventoryFromNotecard, FolderID:{0}, ItemID:{1}, NotecardID:{2}, ObjectID:{3}", folderID, itemID, notecardID, objectID);

                    InventoryItemBase notecardItem = null;
                    IInventoryStorage inventoryService = m_inventoryProviderSelector.GetProvider(m_Caps.AgentID);

                    // Is this an objects task inventory?
                    if (objectID != UUID.Zero)
                    {
                        SceneObjectPart part = m_Scene.GetSceneObjectPart(objectID);
                        if (part != null)
                        {
                            TaskInventoryItem item = part.Inventory.GetInventoryItem(notecardID);
                            if (m_Scene.Permissions.CanCopyObjectInventory(notecardID, objectID, m_Caps.AgentID))
                            {
                                notecardItem = new InventoryItemBase(notecardID, m_agentID) { AssetID = item.AssetID };
                            }
                        }
                    }
                    // else its in inventory directly
                    else
                    {
                        notecardItem = inventoryService.GetItem(notecardID);
                    }

                    if ((notecardItem != null) && (notecardItem.Owner == m_agentID))
                    {
                        // Lookup up the notecard asset
                        IAssetCache assetCache = m_Scene.CommsManager.AssetCache;
                        AssetBase asset = assetCache.GetAsset(notecardItem.AssetID, AssetRequestInfo.InternalRequest());

                        if (asset != null)
                        {
                            AssetNotecard notecardAsset = new AssetNotecard(UUID.Zero, asset.Data);
                            notecardAsset.Decode();

                            InventoryItemBase item = null;
                            foreach (InventoryItem notecardObjectItem in notecardAsset.EmbeddedItems)
                            {
                                if (notecardObjectItem.UUID == itemID)
                                {
                                    item = new InventoryItemBase(UUID.Random(), m_Caps.AgentID);
                                    item.CreatorId = notecardObjectItem.CreatorID.ToString();
                                    item.CreationDate = Util.UnixTimeSinceEpoch();
                                    item.GroupID = notecardObjectItem.GroupID;
                                    item.AssetID = notecardObjectItem.AssetUUID;
                                    item.Name = notecardObjectItem.Name;
                                    item.Description = notecardObjectItem.Description;
                                    item.AssetType = (int)notecardObjectItem.AssetType;
                                    item.InvType = (int)notecardObjectItem.InventoryType;
                                    item.Folder = folderID;

                                    //item.BasePermissions = (uint)notecardObjectItem.Permissions.BaseMask;
                                    item.BasePermissions = (uint)(PermissionMask.All | PermissionMask.Export);
                                    item.CurrentPermissions = (uint)(PermissionMask.All | PermissionMask.Export);
                                    item.GroupPermissions = (uint)PermissionMask.None;
                                    item.EveryonePermissions = (uint)PermissionMask.None;
                                    item.NextPermissions = (uint)PermissionMask.All;
                                    break;
                                }
                            }

                            if (item != null)
                            {
                                m_Scene.AddInventoryItem(m_Caps.AgentID, item);
                                // m_log.InfoFormat("[CAPS]: SendInventoryItemCreateUpdate ItemID:{0}, AssetID:{1}", item.ID, item.AssetID);
                                client.SendInventoryItemCreateUpdate(item,0);
                                response["int_response_code"] = 200;
                                return LLSDHelpers.SerializeLLSDReply(response);
                            }
                        }
                        else
                        {
                            m_log.ErrorFormat("[CAPS]: CopyInventoryFromNotecard could not find asset {0} for '{1}' owned by {2}", notecardItem.AssetID, notecardItem.Name, notecardItem.Owner);
                        }
                    }
                    else
                    {
                        if (notecardItem == null)
                            m_log.ErrorFormat("[CAPS]: CopyInventoryFromNotecard could not find notecard {0} in object {1} item {2} in folder {3} for {4}",
                                notecardID, objectID, itemID, folderID, m_agentID);
                        else
                        if (notecardItem.Owner != m_agentID)
                            m_log.ErrorFormat("[CAPS]: CopyInventoryFromNotecard notecard '{0}' owned by {1} NOT {2}", notecardItem.Name, notecardItem.Owner, m_agentID);
                    }
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("[CAPS]: CopyInventoryFromNotecard : {0}", e.ToString());
                }

                // Failure case
                client.SendAlertMessage("Failed to retrieve item");
                return LLSDHelpers.SerializeLLSDReply(response);
            }


            public string CreateInventoryCategory(string request, string path, string param, OSHttpRequest httpRequest, OSHttpResponse httpResponse)
            {
                Hashtable response = new Hashtable();
                response["int_response_code"] = 404;
                response["content_type"] = "text/plain";
                response["str_response_string"] = String.Empty; 
                
                OSDMap map = (OSDMap)OSDParser.DeserializeLLSDXml(request);
                UUID folder_id = map["folder_id"].AsUUID();
                UUID parent_id = map["parent_id"].AsUUID();
                int type = map["type"].AsInteger();
                string name = map["name"].AsString();

                try
                {
                    UUID newFolderId = UUID.Random();
                    InventoryFolderBase newFolder =
                        new InventoryFolderBase(newFolderId, name, m_Caps.AgentID, (short)type, parent_id, 1);

                    m_checkedStorageProvider.CreateFolder(m_Caps.AgentID, newFolder);

                    OSDMap resp = new OSDMap();
                    resp["folder_id"] = folder_id;
                    resp["parent_id"] = parent_id;
                    resp["type"] = type;
                    resp["name"] = name;

                    response["int_response_code"] = 200; 
                    response["str_response_string"] = OSDParser.SerializeLLSDXmlString(resp);
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat(
                        "[CAPS/INVENTORY] Could not create requested folder {0} in parent {1}: {2}", folder_id, m_Caps.AgentID, e);
                }

                return LLSDHelpers.SerializeLLSDReply(response);
            }

            /// <summary>
            /// Convert an internal inventory item object into an LLSD object.
            /// </summary>
            /// <param name="invItem"></param>
            /// <returns></returns>
            private LLSDInventoryItem ConvertInventoryItem(InventoryItemBase invItem)
            {
                LLSDInventoryItem llsdItem = new LLSDInventoryItem();
                llsdItem.asset_id = invItem.AssetID;
                llsdItem.created_at = invItem.CreationDate;
                llsdItem.desc = invItem.Description;
                llsdItem.flags = (int)invItem.Flags;
                llsdItem.item_id = invItem.ID;
                llsdItem.name = invItem.Name;
                llsdItem.parent_id = invItem.Folder;
                llsdItem.type = invItem.AssetType;
                llsdItem.inv_type = invItem.InvType;

                llsdItem.permissions = new LLSDPermissions();
                llsdItem.permissions.creator_id = invItem.CreatorIdAsUuid;
                llsdItem.permissions.base_mask = (int)invItem.CurrentPermissions;
                llsdItem.permissions.everyone_mask = (int)invItem.EveryonePermissions;
                llsdItem.permissions.group_id = invItem.GroupID;
                llsdItem.permissions.group_mask = (int)invItem.GroupPermissions;
                llsdItem.permissions.is_owner_group = invItem.GroupOwned;
                llsdItem.permissions.next_owner_mask = (int)invItem.NextPermissions;
                llsdItem.permissions.owner_id = invItem.Owner;
                llsdItem.permissions.owner_mask = (int)invItem.CurrentPermissions;
                llsdItem.sale_info = new LLSDSaleInfo();
                llsdItem.sale_info.sale_price = invItem.SalePrice;
                llsdItem.sale_info.sale_type = invItem.SaleType;

                return llsdItem;
            }


            public class AssetUploader
            {
                public event UpLoadedAsset OnUpLoad;
                private UpLoadedAsset handlerUpLoad = null;

                private string uploaderPath = String.Empty;
                private UUID newAssetID;
                private UUID inventoryItemID;
                private UUID parentFolder;
                private IHttpServer httpListener;
                private string m_assetName = String.Empty;
                private string m_assetDes = String.Empty;

                private string m_invType = String.Empty;
                private string m_assetType = String.Empty;

                private PermissionMask m_nextOwnerMask = PermissionMask.All;
                private PermissionMask m_groupMask = PermissionMask.None;
                private PermissionMask m_everyoneMask = PermissionMask.None;

                public AssetUploader(string assetName, string description, UUID assetID, UUID inventoryItem,
                                     UUID parentFolderID, string invType, string assetType, 
                                     PermissionMask nextOwnerPerm, PermissionMask groupPerm, PermissionMask everyonePerm,
                                     string path, IHttpServer httpServer)
                {
                    m_assetName = assetName;
                    m_assetDes = description;
                    newAssetID = assetID;
                    inventoryItemID = inventoryItem;
                    uploaderPath = path;
                    httpListener = httpServer;
                    parentFolder = parentFolderID;
                    m_assetType = assetType;
                    m_invType = invType;
                    m_nextOwnerMask = nextOwnerPerm;
                    m_groupMask = groupPerm;
                    m_everyoneMask = everyonePerm;
                }

                /// <summary>
                ///
                /// </summary>
                /// <param name="data"></param>
                /// <param name="path"></param>
                /// <param name="param"></param>
                /// <returns></returns>
                public string uploaderCaps(byte[] data, string path, string param)
                {
                    UUID inv = inventoryItemID;
                    string res = String.Empty;
                    LLSDAssetUploadComplete uploadComplete = new LLSDAssetUploadComplete();
                    uploadComplete.new_asset = newAssetID.ToString();
                    uploadComplete.new_inventory_item = inv;
                    uploadComplete.state = "complete";

                    res = LLSDHelpers.SerializeLLSDReply(uploadComplete);

                    httpListener.RemoveStreamHandler("POST", uploaderPath);

                    handlerUpLoad = OnUpLoad;
                    if (handlerUpLoad != null)
                    {
                        handlerUpLoad(m_assetName, m_assetDes, newAssetID, inv, parentFolder, data, m_invType, m_assetType, m_nextOwnerMask, m_groupMask, m_everyoneMask);
                    }

                    return res;
                }
            }

            /// <summary>
            /// This class is a callback invoked when a client sends asset data to
            /// an agent inventory notecard update url
            /// </summary>
            public class ItemUpdater
            {
                public event UpdateItem OnUpLoad;

                private UpdateItem handlerUpdateItem = null;

                private string uploaderPath = String.Empty;
                private UUID inventoryItemID;
                private IHttpServer httpListener;

                public ItemUpdater(UUID inventoryItem, string path, IHttpServer httpServer)
                {
                    inventoryItemID = inventoryItem;
                    uploaderPath = path;
                    httpListener = httpServer;
                }

                /// <summary>
                ///
                /// </summary>
                /// <param name="data"></param>
                /// <param name="path"></param>
                /// <param name="param"></param>
                /// <returns></returns>
                public string uploaderCaps(byte[] data, string path, string param)
                {
                    UUID inv = inventoryItemID;
                    string res = String.Empty;

                    UpdateItemResponse response = new UpdateItemResponse();
                    handlerUpdateItem = OnUpLoad;
                    if (handlerUpdateItem != null)
                    {
                        response = handlerUpdateItem(inv, data);
                    }

                    if (response.AssetKind == AssetType.LSLText)
                    {
                        if (response.SaveErrors != null && response.SaveErrors.Count > 0)
                        {
                            LLSDScriptCompileFail compFail = new LLSDScriptCompileFail();
                            compFail.new_asset = response.AssetId.ToString();
                            compFail.new_inventory_item = inv;
                            compFail.state = "complete";

                            foreach (string str in response.SaveErrors)
                            {
                                compFail.errors.Array.Add(str);
                            }

                            res = LLSDHelpers.SerializeLLSDReply(compFail);
                        }
                        else
                        {
                            LLSDScriptCompileSuccess compSuccess = new LLSDScriptCompileSuccess();
                            compSuccess.new_asset = response.AssetId.ToString();
                            compSuccess.new_inventory_item = inv;
                            compSuccess.state = "complete";

                            res = LLSDHelpers.SerializeLLSDReply(compSuccess);
                        }
                    }
                    else
                    {
                        LLSDAssetUploadComplete uploadComplete = new LLSDAssetUploadComplete();
                        uploadComplete.new_asset = response.AssetId.ToString();
                        uploadComplete.new_inventory_item = inv;
                        uploadComplete.state = "complete";

                        res = LLSDHelpers.SerializeLLSDReply(uploadComplete);
                    }

                    httpListener.RemoveStreamHandler("POST", uploaderPath);

                    return res;
                }
            }

        }

        /// <summary>
        /// This class is a callback invoked when a client sends asset data to
        /// a task inventory script update url
        /// </summary>
        protected class TaskInventoryScriptUpdater
        {
            public event UpdateTaskScript OnUpLoad;

            private UpdateTaskScript handlerUpdateTaskScript = null;

            private string uploaderPath = String.Empty;
            private UUID inventoryItemID;
            private UUID primID;
            private bool isScriptRunning;
            private IHttpServer httpListener;

            public TaskInventoryScriptUpdater(UUID inventoryItemID, UUID primID, int isScriptRunning,
                                                string path, IHttpServer httpServer)
            {
                this.inventoryItemID = inventoryItemID;
                this.primID = primID;

                // This comes in over the packet as an integer, but actually appears to be treated as a bool
                this.isScriptRunning = (0 == isScriptRunning ? false : true);

                uploaderPath = path;
                httpListener = httpServer;
            }

            /// <summary>
            ///
            /// </summary>
            /// <param name="data"></param>
            /// <param name="path"></param>
            /// <param name="param"></param>
            /// <returns></returns>
            public string uploaderCaps(byte[] data, string path, string param)
            {
                try
                {
                    //                    m_log.InfoFormat("[CAPS]: " +
                    //                                     "TaskInventoryScriptUpdater received data: {0}, path: {1}, param: {2}",
                    //                                     data, path, param));

                    string res = String.Empty;

                    UpdateItemResponse response = new UpdateItemResponse();

                    handlerUpdateTaskScript = OnUpLoad;
                    if (handlerUpdateTaskScript != null)
                    {
                        response = handlerUpdateTaskScript(inventoryItemID, primID, isScriptRunning, data);
                    }

                    if (response.AssetKind == AssetType.LSLText)
                    {
                        if (response.SaveErrors != null && response.SaveErrors.Count > 0)
                        {
                            LLSDScriptCompileFail compFail = new LLSDScriptCompileFail();
                            compFail.new_asset = response.AssetId.ToString();
                            compFail.state = "complete";

                            foreach (string str in response.SaveErrors)
                            {
                                compFail.errors.Array.Add(str);
                            }

                            res = LLSDHelpers.SerializeLLSDReply(compFail);
                        }
                        else
                        {
                            LLSDScriptCompileSuccess compSuccess = new LLSDScriptCompileSuccess();
                            compSuccess.new_asset = response.AssetId.ToString();
                            compSuccess.state = "complete";

                            res = LLSDHelpers.SerializeLLSDReply(compSuccess);
                        }
                    }
                    else
                    {
                        LLSDAssetUploadComplete uploadComplete = new LLSDAssetUploadComplete();
                        uploadComplete.new_asset = response.AssetId.ToString();
                        uploadComplete.state = "complete";

                        res = LLSDHelpers.SerializeLLSDReply(uploadComplete);
                    }

                    httpListener.RemoveStreamHandler("POST", uploaderPath);

                    // m_log.InfoFormat("[CAPS]: TaskInventoryScriptUpdater.uploaderCaps res: {0}", res);

                    return res;
                }
                catch (Exception e)
                {
                    m_log.Error("[CAPS]: " + e.ToString());
                }

                // XXX Maybe this should be some meaningful error packet
                return null;
            }

        }

    }
}
