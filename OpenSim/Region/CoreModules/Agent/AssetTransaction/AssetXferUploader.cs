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
using System.IO;
using System.Reflection;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Communications.Cache;

namespace OpenSim.Region.CoreModules.Agent.AssetTransaction
{
    public class AssetXferUploader
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private AssetBase m_asset;
        private int m_assetSize = 0;
        private int m_xferPacketSize = 1000;
        private UUID InventFolder = UUID.Zero;
        private sbyte invType = 0;
        private bool m_createItem = false;
        private uint m_createItemCallback = 0;
        private string m_description = String.Empty;
        private bool m_dumpAssetToFile;
        private bool m_finished = false;
        private string m_name = String.Empty;
        private bool m_storeLocal;
        private AgentAssetTransactions m_userTransactions;
        private uint nextPerm = 0;
        private IClientAPI ourClient;
        private UUID TransactionID = UUID.Zero;
        private sbyte type = 0;
        private byte wearableType = 0;
        public ulong XferID;

        private DateTime m_lastAccess;
        public DateTime LastAccess
        {
            get { return m_lastAccess; }
        }

        public delegate void UploadCompleteDelegate(AssetBase asset);
        private UploadCompleteDelegate m_assetUploadedDelegate = null;

        public AssetXferUploader(AgentAssetTransactions transactions, bool dumpAssetToFile)
        {
            m_userTransactions = transactions;
            m_dumpAssetToFile = dumpAssetToFile;
            m_lastAccess = DateTime.Now;
        }

        /// <summary>
        /// Process transfer data received from the client.
        /// </summary>
        /// <param name="xferID"></param>
        /// <param name="packetID"></param>
        /// <param name="data"></param>
        /// <returns>True if the transfer is complete, false otherwise or if the xferID was not valid</returns>
        private const uint PACKETID_COMPLETE = 0x80000000;
        public bool HandleXferPacket(ulong xferID, uint packetID, byte[] data)
        {
            bool isComplete = false;
            if (XferID == xferID)
            {
                m_lastAccess = DateTime.Now;
                if ((packetID & PACKETID_COMPLETE) != 0)
                {
                    isComplete = true;
                    packetID &= ~PACKETID_COMPLETE;
                }

                if (packetID == 0)
                {
                    // First (special) header packet with length.
                    int headerLen = sizeof(uint);  // 'size' field below
                    m_assetSize = Utils.BytesToInt(data);
                    m_xferPacketSize = data.Length - headerLen;
                    // We must use any existing buffer, in case we didn't get the first packet first.
                    if ((m_asset.Data == null) || (m_asset.Data.Length <  m_xferPacketSize))
                        m_asset.Data = new byte[m_xferPacketSize];
                    Array.Copy(data, headerLen, m_asset.Data, 0, data.Length - headerLen);
                    m_log.DebugFormat("[XFER]: xferID={0} packetID={1:X4} buffer={2} +{3}@0)", xferID, packetID, m_asset.Data.Length, m_xferPacketSize);
                }
                else
                {
                    // normal header, we already know how big it should be (m_xferPacketSize==1000).
                    if (m_asset.Data == null)
                        m_asset.Data = new byte[0]; // just in case we get the second packet first...

                    int offset = (int)packetID * m_xferPacketSize;  // destination for new data block
                    m_log.DebugFormat("[XFER]: xferID={0} packetID={1:X4} buffer={2} +{3}@{4}", xferID, packetID, m_asset.Data.Length, data.Length, offset);

                    int datalen = data.Length;                  // additional data block size
                    if (datalen > m_xferPacketSize)
                    {
                        // new data packet won't fit based on m_xferPacketSize
                        m_log.ErrorFormat("[XFER]: Expected {0}-byte data packet, received {1} bytes of data.", m_xferPacketSize, datalen);
                        datalen = m_xferPacketSize;
                    }

                    int newbuflen = offset + datalen;           // required size when inserting new block
                    int oldbuflen = m_asset.Data.Length;        // existing data size
                    if (newbuflen > oldbuflen)
                    {
                        // grow buffer
                        byte[] destinationArray = new byte[newbuflen];
                        Array.Copy(m_asset.Data, 0, destinationArray, 0, oldbuflen);
                        m_asset.Data = destinationArray;
                    }
                    // insert data at the correct offset
                    Array.Copy(data, 0, m_asset.Data, offset, datalen);
                }
                ourClient.SendConfirmXfer(xferID, packetID);

                if (isComplete)
                {
                    m_log.DebugFormat("[XFER]: xferID={0} packetID={1:X4} buffer={2} asset={3} (COMPLETE)", xferID, packetID, m_asset.Data.Length, m_assetSize);
                    SendCompleteMessage();
                }
            }

            return isComplete;
        }

        /// <summary>
        /// Initialize asset transfer from the client
        /// </summary>
        /// <param name="xferID"></param>
        /// <param name="packetID"></param>
        /// <param name="data"></param>
        /// <returns>True if the transfer is complete, false otherwise</returns>
        public bool Initialize(IClientAPI remoteClient, UUID assetID, UUID transaction, sbyte type, byte[] data, bool storeLocal, bool tempFile)
        {
            ourClient = remoteClient;
            m_asset = new AssetBase();
            m_asset.FullID = assetID;
            m_asset.Type = type;
            m_asset.Data = data;
            m_asset.Name = "blank";
            m_asset.Description = "empty";
            m_asset.Local = storeLocal;
            m_asset.Temporary = tempFile;

            TransactionID = transaction;
            m_storeLocal = storeLocal;
            m_lastAccess = DateTime.Now;

            if (m_asset.Data.Length > 2)
            {
                SendCompleteMessage();
                return true;
            }
            else
            {
                RequestStartXfer();
            }

            return false;
        }

        protected void RequestStartXfer()
        {
            XferID = Util.GetNextXferID();
            ourClient.SendXferRequest(XferID, m_asset.Type, m_asset.FullID, 0, new byte[0]);
            m_lastAccess = DateTime.Now;
        }

        protected void SendCompleteMessage()
        {
            m_finished = true;
            if (m_createItem)
            {
                DoCreateItem(m_createItemCallback);
            }
            else if (m_storeLocal)
            {
                try
                {
                    m_userTransactions.Manager.MyScene.CommsManager.AssetCache.AddAsset(m_asset, AssetRequestInfo.GenericNetRequest());
                }
                catch (AssetServerException e)
                {
                    m_log.ErrorFormat("[ASSET TRANSACTIONS]: Error uploading asset data for transaction {0}", e);
                    ourClient.SendAgentAlertMessage("Unable to upload asset. Please try again later.", false);
                    ourClient.SendAssetUploadCompleteMessage(m_asset.Type, false, UUID.Zero);
                    return;
                }
            }

            ourClient.SendAssetUploadCompleteMessage(m_asset.Type, true, m_asset.FullID);
            m_log.DebugFormat("[ASSET TRANSACTIONS]: Uploaded asset data for transaction {0}", TransactionID);

            if (m_dumpAssetToFile)
            {
                DateTime now = DateTime.Now;
                string filename =
                    String.Format("{6}_{7}_{0:d2}{1:d2}{2:d2}_{3:d2}{4:d2}{5:d2}.dat", now.Year, now.Month, now.Day,
                                  now.Hour, now.Minute, now.Second, m_asset.Name, m_asset.Type);
                SaveAssetToFile(filename, m_asset.Data);
            }

            // Callback to the completion delegate if defined.
            if (m_assetUploadedDelegate != null)
                TriggerWhenUploadComplete(m_assetUploadedDelegate);
        }

        private void SaveAssetToFile(string filename, byte[] data)
        {
            string assetPath = "UserAssets";
            if (!Directory.Exists(assetPath))
            {
                Directory.CreateDirectory(assetPath);
            }
            FileStream fs = File.Create(Path.Combine(assetPath, filename));
            BinaryWriter bw = new BinaryWriter(fs);
            bw.Write(data);
            bw.Close();
            fs.Close();
        }

        public void RequestCreateInventoryItem(IClientAPI remoteClient, UUID transactionID, UUID folderID,
                                               uint callbackID, string description, string name, sbyte invType,
                                               sbyte type, byte wearableType, uint nextOwnerMask)
        {
            if (TransactionID == transactionID)
            {
                InventFolder = folderID;
                m_name = name;
                m_description = description;
                this.type = type;
                this.invType = invType;
                this.wearableType = wearableType;
                nextPerm = nextOwnerMask;
                m_asset.Name = name;
                m_asset.Description = description;
                m_asset.Type = type;

                if (m_finished)
                {
                    DoCreateItem(callbackID);
                }
                else
                {
                    m_createItem = true; //set flag so the inventory item is created when upload is complete
                    m_createItemCallback = callbackID;
                }
                m_lastAccess = DateTime.Now;
            }
        }

        private void DoCreateItem(uint callbackID)
        {
            m_createItem = false;   // don't create or bill for the same item more than once

            try
            {
                m_userTransactions.Manager.MyScene.CommsManager.AssetCache.AddAsset(m_asset, AssetRequestInfo.GenericNetRequest());
            }
            catch (AssetServerException e)
            {
                ourClient.SendAgentAlertMessage("Unable to upload asset. Please try again later.", false);
                m_log.ErrorFormat("[ASSET TRANSACTIONS] Asset storage failed: {0}", e);
                return;
            }

            CachedUserInfo userInfo =
                m_userTransactions.Manager.MyScene.CommsManager.UserService.GetUserDetails(
                    ourClient.AgentId);

            if (userInfo != null)
            {
                InventoryItemBase item = new InventoryItemBase();
                item.Owner = ourClient.AgentId;
                item.CreatorId = ourClient.AgentId.ToString();
                item.ID = UUID.Random();
                item.AssetID = m_asset.FullID;
                item.Description = m_description;
                item.Name = m_name;
                item.AssetType = type;
                item.InvType = invType;
                item.Folder = InventFolder;
                item.BasePermissions = (uint)(PermissionMask.All | PermissionMask.Export);
                item.CurrentPermissions = (uint)(PermissionMask.All | PermissionMask.Export);
                item.GroupPermissions = (uint)PermissionMask.None;
                item.EveryonePermissions = (uint)PermissionMask.None;
                item.NextPermissions = nextPerm;
                item.Flags = (uint)wearableType;

                item.CreationDate = Util.UnixTimeSinceEpoch();

                userInfo.AddItem(item);
                ourClient.SendInventoryItemCreateUpdate(item, callbackID);

                // Charge for the upload if appropriate.
                IMoneyModule mm = ourClient.Scene.RequestModuleInterface<IMoneyModule>();
                if (mm != null)
                {
                    if (mm.UploadChargeApplies((AssetType)m_asset.Type) && !m_asset.Temporary)
                        mm.ApplyUploadCharge(ourClient.AgentId);
                }
            }
            else
            {
                m_log.ErrorFormat("[ASSET TRANSACTIONS]: Could not find user {0} for inventory item creation", ourClient.AgentId);
            }
        }

        /// <summary>
        /// Get the asset data uploaded in this transfer.
        /// </summary>
        /// <returns>null if the asset has not finished uploading</returns>
        public AssetBase GetAssetData()
        {
            if (m_finished)
            {
                return m_asset;
            }

            return null;
        }

        public void TriggerWhenUploadComplete(UploadCompleteDelegate callback)
        {
            if (m_finished && callback != null)
            {
                callback(m_asset);
                m_assetUploadedDelegate = null; // we've used this one
            }
            else
            {
                m_assetUploadedDelegate = callback; // save it for later
            }
        }
    }
}
