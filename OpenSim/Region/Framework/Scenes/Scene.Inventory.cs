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
using System.Text;
using System.Threading;
using System.Timers;
using OpenMetaverse;
using OpenMetaverse.Packets;
using log4net;
using OpenSim.Framework;
using OpenSim.Framework.Communications.Cache;
using OpenSim.Region.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes.Serialization;
using System.Diagnostics;
using OpenSim.Framework.Geom;

namespace OpenSim.Region.Framework.Scenes
{
    public class ScenePermBits
    {
        public const uint SLAM = 0x00000008;
        public const uint BASEMASK = 0x7fffff0;
    }

    public partial class Scene
    {
        private static readonly ILog m_log
            = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Allows asynchronous derezzing of objects from the scene into a client's inventory.
        /// </summary>
        protected AsyncSceneObjectGroupDeleter m_asyncSceneObjectDeleter;

        /// <summary>
        /// Start all the scripts in the scene which should be started.
        /// </summary>
        public void CreateScriptInstances()
        {
            m_log.Info("[PRIM INVENTORY]: Starting scripts in scene");

            foreach (EntityBase group in Entities)
            {
                SceneObjectGroup sog = group as SceneObjectGroup;
                if (sog != null)
                {
                    sog.CreateScriptInstances(null, ScriptStartFlags.ChangedRegionStart, DefaultScriptEngine, (int)ScriptStateSource.RegionLocalDisk, null);
                }
            }
        }

        public bool AddInventoryItemReturned(UUID AgentId, InventoryItemBase item)
        {
            CachedUserInfo userInfo
                = CommsManager.UserService.GetUserDetails(AgentId);
            if (userInfo != null)
            {
                userInfo.AddItem(item);
                return true;
            }
            else
            {
                m_log.ErrorFormat(
                    "[AGENT INVENTORY]: Agent was not found for add of item {1} {2}", item.Name, item.ID);

                return false;
            }
        }

        public void AddInventoryItem(UUID AgentID, InventoryItemBase item)
        {
            CachedUserInfo userInfo
                = CommsManager.UserService.GetUserDetails(AgentID);

            if (userInfo != null)
            {
                userInfo.AddItem(item);
            }
            else
            {
                m_log.ErrorFormat(
                    "[AGENT INVENTORY]: Agent {1} was not found for add of item {2} {3}",
                    AgentID, item.Name, item.ID);

                return;
            }
        }

        /// <summary>
        /// Add an inventory item to an avatar's inventory.
        /// </summary>
        /// <param name="remoteClient">The remote client controlling the avatar</param>
        /// <param name="item">The item.  This structure contains all the item metadata, including the folder
        /// in which the item is to be placed.</param>
        public void AddInventoryItem(IClientAPI remoteClient, InventoryItemBase item)
        {
            CachedUserInfo userInfo
                = CommsManager.UserService.GetUserDetails(remoteClient.AgentId);

            if (userInfo != null)
            {
                AddInventoryItem(remoteClient.AgentId, item);
                remoteClient.SendInventoryItemCreateUpdate(item, 0);
            }
            else
            {
                m_log.ErrorFormat(
                    "[AGENT INVENTORY]: Could not resolve user {0} for adding an inventory item",
                    remoteClient.AgentId);
            }
        }

        /// <summary>
        /// Capability originating call to update the asset of an item in an agent's inventory
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="itemID"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public virtual UpdateItemResponse CapsUpdateInventoryItemAsset(IClientAPI remoteClient, UUID itemID, byte[] data)
        {
            CachedUserInfo userInfo = CommsManager.UserService.GetUserDetails(remoteClient.AgentId);
            if (userInfo != null)
            {
                InventoryItemBase item = userInfo.FindItem(itemID);

                if (item != null)
                {
                    //does the item belong to this user?
                    if (item.Owner != remoteClient.AgentId)
                    {
                        m_log.ErrorFormat(
                            "[AGENT INVENTORY]: Not allowing CAPS update from user {0} that doesn't own item {1}",
                            remoteClient.AgentId, itemID);

                        return new UpdateItemResponse();
                    }

                    if ((InventoryType)item.InvType == InventoryType.Notecard)
                    {
                        if (!Permissions.CanEditNotecard(itemID, UUID.Zero, remoteClient.AgentId))
                        {
                            remoteClient.SendAgentAlertMessage("Insufficient permissions to edit notecard", false);
                            return new UpdateItemResponse();
                        }
                    }
                    else if ((InventoryType)item.InvType == InventoryType.LSL)
                    {
                        if (!Permissions.CanEditScript(itemID, UUID.Zero, remoteClient.AgentId))
                        {
                            remoteClient.SendAgentAlertMessage("Insufficient permissions to edit script", false);
                            return new UpdateItemResponse();
                        }
                    }

                    AssetBase asset =
                        CreateAsset(item.Name, item.Description, (sbyte)item.AssetType, data);

                    try
                    {
                        CommsManager.AssetCache.AddAsset(asset, AssetRequestInfo.GenericNetRequest());
                    }
                    catch (AssetServerException e)
                    {
                        m_log.ErrorFormat("[AGENT INVENTORY] Caps update failed due to asset write error {0}", e);
                        remoteClient.SendAgentAlertMessage("Asset server error, try again later", false);
                        return new UpdateItemResponse();
                    }

                    item.AssetID = asset.FullID;
                    userInfo.UpdateItem(item);

                        
                    if ((InventoryType)item.InvType == InventoryType.LSL)
                    {
                        //compile script and return results to the user
                        using (MessageStoringCompilationListener compListener = new MessageStoringCompilationListener())
                        {
                            EventManager.TriggerCompileScript(Utils.BytesToString(data), compListener);

                            if (compListener.HasErrors())
                            {
                                return new UpdateItemResponse(item.AssetID, AssetType.LSLText, compListener.Messages);
                            }
                        }
                    }

                    // remoteClient.SendInventoryItemCreateUpdate(item);
                    return new UpdateItemResponse(asset.FullID, (AssetType) item.AssetType);
                }
            }
            else
            {
                m_log.ErrorFormat(
                    "[AGENT INVENTORY]: Could not resolve user {0} for caps inventory update",
                    remoteClient.AgentId);
            }

            return new UpdateItemResponse();
        }

        /// <summary>
        /// <see>CapsUpdatedInventoryItemAsset(IClientAPI, UUID, byte[])</see>
        /// </summary>
        public UpdateItemResponse CapsUpdateInventoryItemAsset(UUID avatarId, UUID itemID, byte[] data)
        {
            ScenePresence avatar;

            if (TryGetAvatar(avatarId, out avatar))
            {
                return CapsUpdateInventoryItemAsset(avatar.ControllingClient, itemID, data);
            }
            else
            {
                m_log.ErrorFormat(
                    "[AGENT INVENTORY]: " +
                    "Avatar {0} cannot be found to update its inventory item asset",
                    avatarId);
            }

            return new UpdateItemResponse();
        }

        /// <summary>
        /// Capability originating call to update the asset of a script in a prim's (task's) inventory
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="itemID"></param>
        /// <param name="primID">The prim which contains the item to update</param>
        /// <param name="isScriptRunning">Indicates whether the script to update is currently running</param>
        /// <param name="data"></param>
        public UpdateItemResponse CapsUpdateTaskInventoryScriptAsset(IClientAPI remoteClient, UUID itemId,
                                                       UUID primId, bool isScriptRunning, byte[] data)
        {
            if (!Permissions.CanEditScript(itemId, primId, remoteClient.AgentId))
            {
                remoteClient.SendAgentAlertMessage("Insufficient permissions to edit script", false);
                return new UpdateItemResponse();
            }

            // Retrieve group
            SceneObjectPart part = GetSceneObjectPart(primId);
            SceneObjectGroup group = part.ParentGroup;
            if (null == group)
            {
                m_log.ErrorFormat(
                    "[PRIM INVENTORY]: " +
                    "Prim inventory update requested for item ID {0} in prim ID {1} but this prim does not exist",
                    itemId, primId);

                return new UpdateItemResponse();
            }

            // Retrieve item
            TaskInventoryItem item = group.GetInventoryItem(part.LocalId, itemId);

            if (null == item)
            {
                m_log.ErrorFormat(
                    "[PRIM INVENTORY]: Tried to retrieve item ID {0} from prim {1}, {2} for caps script update "
                        + " but the item does not exist in this inventory",
                    itemId, part.Name, part.UUID);

                return new UpdateItemResponse();
            }

            AssetBase asset = CreateAsset(item.Name, item.Description, (sbyte)AssetType.LSLText, data);
            m_log.InfoFormat("[ASSETS]: CapsUpdateTaskInventoryScriptAsset created new asset {0} -> {1} for {2}", item.AssetID, asset.FullID, item.Name);

            try
            {
                CommsManager.AssetCache.AddAsset(asset, AssetRequestInfo.GenericNetRequest());
            }
            catch (AssetServerException e)
            {
                m_log.ErrorFormat("[PRIM INVENTORY] Unable to update script: {0}", e);
                remoteClient.SendAgentAlertMessage("Unable to update script asset. Try again later.", false);
                return new UpdateItemResponse();
            }

            // Update item with new asset
            group.UpdateInventoryItemAsset(item.ParentPartID, item.ItemID, asset.FullID);
            part.GetProperties(remoteClient);

            using (MessageStoringCompilationListener storingListener = new MessageStoringCompilationListener())
            {
                // Trigger rerunning of script (use TriggerRezScript event, see RezScript)

                ScriptStartFlags startFlags = ScriptStartFlags.None;
                if (!isScriptRunning)
                {
                    startFlags |= ScriptStartFlags.StartGloballyDisabled;
                }

                part.Inventory.CreateScriptInstance(item.ItemID, 0, startFlags, DefaultScriptEngine, (int)ScriptStateSource.None, storingListener, true);
                storingListener.WaitForCompilation(MessageStoringCompilationListener.DEFAULT_TIMEOUT);

                if (storingListener.HasErrors())
                {
                    return new UpdateItemResponse(item.AssetID, AssetType.LSLText, storingListener.Messages);
                }
                else
                {
                    return new UpdateItemResponse(item.AssetID, AssetType.LSLText);
                }
            }
        }

        /// <summary>
        /// <see>CapsUpdateTaskInventoryScriptAsset(IClientAPI, UUID, UUID, bool, byte[])</see>
        /// </summary>
        public UpdateItemResponse CapsUpdateTaskInventoryScriptAsset(UUID avatarId, UUID itemId,
                                                        UUID primId, bool isScriptRunning, byte[] data)
        {
            ScenePresence avatar;

            if (TryGetAvatar(avatarId, out avatar))
            {
                return CapsUpdateTaskInventoryScriptAsset(
                    avatar.ControllingClient, itemId, primId, isScriptRunning, data);
            }
            else
            {
                m_log.ErrorFormat(
                    "[PRIM INVENTORY]: " +
                    "Avatar {0} cannot be found to update its prim item asset",
                    avatarId);
            }

            return new UpdateItemResponse();
        }


        bool ChangingInventoryItemPerms(InventoryItemBase itemOrig, InventoryItemBase itemUpd)
        {
            if (itemOrig.EveryonePermissions != itemUpd.EveryonePermissions)
                return true;
            if (itemOrig.GroupPermissions != itemUpd.GroupPermissions)
                return true;
            if (itemOrig.NextPermissions != itemUpd.NextPermissions)
                return true;

            return false;
        }

        bool InventoryItemIsAttached(IClientAPI remoteClient, UUID itemID)
        {
            ScenePresence presence;
            TryGetAvatar(remoteClient.AgentId, out presence);
            if (presence == null)
                return false;

            return presence.Appearance.GetAttachpoint(itemID) != 0;
        }

        bool MatchInventoryRootPartStoredOwner(InventoryItemBase item, int index, UUID who)
        {
            AssetBase rezAsset = CommsManager.AssetCache.GetAsset(item.AssetID, AssetRequestInfo.InternalRequest());

            if (rezAsset == null)
                return true;    // there's no serialized data so assume that it's ok to change perms
            if (rezAsset.Type != (sbyte)OpenMetaverse.AssetType.Object)
                return true;    // there's no serialized data so assume that it's ok to change perms

            ISerializationEngine engine;
            SceneObjectPart part = null;

            if (ProviderRegistry.Instance.TryGet<ISerializationEngine>(out engine) && engine.InventoryObjectSerializer.CanDeserialize(rezAsset.Data))
            {
                if (item.ContainsMultipleItems)
                {
                    CoalescedObject obj = engine.InventoryObjectSerializer.DeserializeCoalescedObjFromInventoryBytes(rezAsset.Data);
                    
                    int idx = 0;
                    SceneObjectGroup foundGroup = null;
                    foreach (SceneObjectGroup grp in obj.Groups)
                    {
                        if (idx == index) 
                        {
                            foundGroup = grp;
                            break;
                        }

                        idx++;
                    }

                    if (foundGroup != null)
                    {
                        part = foundGroup.RootPart;
                    }
                }
                else
                {
                    if (index == 0) // there's only one item so it must be index 0 or no go.
                    {
                        SceneObjectGroup grp = engine.InventoryObjectSerializer.DeserializeGroupFromInventoryBytes(rezAsset.Data);
                        part = grp.RootPart;
                    }
                }
            }
            else
            {
                string xmlData = Utils.BytesToString(rezAsset.Data);
            
                if (item.ContainsMultipleItems)
                    part = CoalescedSceneObjectSerializer.RootPartXmlObject(item.ID, xmlData, index);
                else
                {
                    //rez single group
                    if (index == 0) // there's only one item so it must be index 0 or no go.
                        part = SceneObjectSerializer.RootPartInOriginalXmlFormat(item.ID, xmlData);
                }
            }

            if (part == null)
                return false;   // there's stored data but we can't check the owner

            return who == part.OwnerID;
        }

        /// <summary>
        /// Update an item which is either already in the client's inventory or is within
        /// a transaction
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="transactionID">The transaction ID.  If this is UUID.Zero we will
        /// assume that we are not in a transaction</param>
        /// <param name="itemID">The ID of the updated item</param>
        /// <param name="name">The name of the updated item</param>
        /// <param name="description">The description of the updated item</param>
        /// <param name="nextOwnerMask">The permissions of the updated item</param>
/*        public void UpdateInventoryItemAsset(IClientAPI remoteClient, UUID transactionID,
                                             UUID itemID, string name, string description,
                                             uint nextOwnerMask)*/
        public void UpdateInventoryItemAsset(IClientAPI remoteClient, UUID transactionID,
                                             UUID itemID, InventoryItemBase itemUpd)
        {
            CachedUserInfo userInfo = CommsManager.UserService.GetUserDetails(remoteClient.AgentId);
            if (userInfo == null)
            {
                m_log.Error("[AGENT INVENTORY]: Agent ID " + remoteClient.AgentId + " inventory not found for item update.");
                return;
            }

            InventoryItemBase item = userInfo.FindItem(itemID);
            if (item == null)
            {
                m_log.Error("[AGENTINVENTORY]: Item ID " + itemID + " not found for an inventory item update.");
                return;
            }

            //make sure we actually OWN the item
            if (item.Owner != remoteClient.AgentId)
            {
                m_log.ErrorFormat("[AGENT INVENTORY]: User {0} does not own item {1}, not updating",
                    remoteClient.AgentId, itemID);
                remoteClient.SendInventoryItemDetails(item.Owner, item);
                return;
            }

            // Update the item with the changes passed to us from the viewer
            item.Name = itemUpd.Name;
            item.Description = itemUpd.Description;

            // Limit perms updates to base permissions (0 means no change?)
            if (itemUpd.EveryonePermissions == 0)
                itemUpd.EveryonePermissions = item.EveryonePermissions;
            else
                itemUpd.EveryonePermissions &= item.BasePermissions;
            if (itemUpd.GroupPermissions == 0)
                itemUpd.GroupPermissions &= item.GroupPermissions;
            else
                itemUpd.GroupPermissions &= item.BasePermissions;
            if (itemUpd.NextPermissions == 0)
                itemUpd.NextPermissions &= item.NextPermissions;
            else
                itemUpd.NextPermissions &= item.BasePermissions;
            // Check for permissions changes
            if (ChangingInventoryItemPerms(item, itemUpd))
            {
                if (InventoryItemIsAttached(remoteClient, itemID))
                {
                    remoteClient.SendAlertMessage("To change an attachment's permissions, you must first drop it or detach it.");
                    remoteClient.SendInventoryItemDetails(item.Owner, item);
                    return;
                }

                if (!MatchInventoryRootPartStoredOwner(item, 0, item.Owner))
                {
                    // This item has been recently transferred between users, and not rezzed.
                    // We cannot allow permissions changes in this state. Rezzing it fixes it.
                    // See http://inworldz.com/mantis/view.php?id=1664
                    remoteClient.SendAlertMessage("You cannot change the Next Owner permissions on this item until it has been rezzed in-world by you at least once.");
                    InformClientOfInvChange(remoteClient, item);
                    return;
                }

                // Perform the actual permissions update now.
                item.EveryonePermissions = itemUpd.EveryonePermissions;
                item.GroupPermissions = itemUpd.GroupPermissions;
                item.NextPermissions = itemUpd.NextPermissions;
                if (item.InvType == (int)InventoryType.Object)
                {
                    item.CurrentPermissions |= ScenePermBits.SLAM;            // Slam!
                    item.Flags |= (uint)InventoryItemFlags.ObjectSlamPerm;    // Tell the viewer we are going to slam this
                }
            }

            item.GroupID = itemUpd.GroupID;
            item.GroupOwned = itemUpd.GroupOwned;
            item.CreationDate = itemUpd.CreationDate;
            // The client sends zero if its newly created?
            if (itemUpd.CreationDate == 0)
                item.CreationDate = Util.UnixTimeSinceEpoch();
            else
                item.CreationDate = itemUpd.CreationDate;

            // TODO: Check if folder changed and move item
            //item.NextPermissions = itemUpd.Folder;
            item.InvType = itemUpd.InvType;
            item.SalePrice = itemUpd.SalePrice;
            item.SaleType = itemUpd.SaleType;
            item.Flags = itemUpd.Flags;

            // Check if the viewer has passed us a transaction to use
            if (UUID.Zero != transactionID) {
                IAgentAssetTransactions agentTransactions = this.RequestModuleInterface<IAgentAssetTransactions>();
                if (agentTransactions != null)
                    if (agentTransactions.HandleItemUpdateFromTransaction(remoteClient, transactionID, item))
                        return;  // great, this one has been handled (as a transaction update)
            }

            remoteClient.HandleWithInventoryWriteThread(() =>
                {
                    // Otherwise fall through and do a normal inventory update
                    userInfo.UpdateItem(item);
                    remoteClient.SendInventoryItemDetails(item.Owner, item);
                });
        }

        /// <summary>
        /// Give an inventory item from one user to another
        /// </summary>
        /// <param name="recipientClient"></param>
        /// <param name="senderId">ID of the sender of the item</param>
        /// <param name="itemId"></param>
        public virtual void GiveInventoryItem(IClientAPI recipientClient, UUID senderId, UUID itemId)
        {
            InventoryItemBase itemCopy = GiveInventoryItem(recipientClient.AgentId, senderId, itemId);

            if (itemCopy != null)
                recipientClient.SendInventoryItemCreateUpdate(itemCopy,0);
        }

        /// <summary>
        /// Give an inventory item from one user to another
        /// </summary>
        /// <param name="recipient"></param>
        /// <param name="senderId">ID of the sender of the item</param>
        /// <param name="itemId"></param>
        /// <returns>The inventory item copy given, null if the give was unsuccessful</returns>
        public virtual InventoryItemBase GiveInventoryItem(UUID recipient, UUID senderId, UUID itemId)
        {
            // This function is assumed to be used by gives from users only, not group attachments.
            return GiveInventoryItem(recipient, senderId, itemId, UUID.Zero);
        }

        // Used to add group notice attachments or deliver them to members.  Perms checks already done by caller.  May need to apply next owner perms.
        // Note: When adding to a group notice, senderId is a user and senderUserInfo is non-null. Item is NOT added here but item to add returned.
        //       When delivering from a group notice, senderID is the group ID and senderUserInfo is null. Item is delivered to user.
        public virtual InventoryItemBase CheckDeliverGroupItem(InventoryItemBase item, UUID recipientId, UUID senderId, CachedUserInfo senderUserInfo)
        {
            CachedUserInfo recipientUserInfo = null;
            bool isDelivery = (senderUserInfo == null);
            bool applyNextPerms = false;    // don't bother applying them when attaching, and always apply on delivery

            if (isDelivery)
            {
                // delivery from group notice to user - always apply next owner perms
                applyNextPerms = true;
                recipientUserInfo = CommsManager.UserService.GetUserDetails(recipientId);
                if (recipientUserInfo == null)
                {
                    m_log.ErrorFormat("[GROUPS]: Group notice attachment could not be delivered - unknown user {0}", recipientId);
                    return null;
                }
                if (!recipientUserInfo.HasReceivedInventory)
                    recipientUserInfo.FetchInventory();
            }

            // Insert a copy of the item into the recipient
            InventoryItemBase itemCopy = new InventoryItemBase();

            // It's a new inventory item
            itemCopy.ID = UUID.Random();
            itemCopy.GroupOwned = false;
            itemCopy.GroupID = UUID.Zero;
            itemCopy.Folder = UUID.Zero;
            itemCopy.SaleType = (byte)SaleType.Not;
            itemCopy.SalePrice = 0;

            // Now copy the other fields
            itemCopy.InvType = item.InvType;
            itemCopy.AssetType = item.AssetType;
            itemCopy.AssetID = item.AssetID;
            itemCopy.CreatorId = item.CreatorId;
            itemCopy.Name = item.Name;
            itemCopy.Description = item.Description;
            itemCopy.Flags = item.Flags;

            // on final delivery, the owner of the new copy is the recipient
            itemCopy.Owner = isDelivery ? recipientId : item.Owner;

            CalcItemPermsFromInvItem(itemCopy, item, applyNextPerms);

            if (senderUserInfo == null)
            {
                // delivery from group notice to user
                recipientUserInfo.AddItem(itemCopy);
            }
            else
            {
                // attaching from a user inventory to a group notice
                // just return the item to the caller who will add it.
            }

            return itemCopy;
        }

        const uint REQUIRED_NOTICE_ATTACH_PERMS = (uint)PermissionMask.Transfer | (uint)PermissionMask.Copy;
        public virtual InventoryItemBase InventoryItemForGroup(UUID groupId, UUID senderId, UUID itemId)
        {
            // This function is assumed to be used only to add a single group notice attachment.
            CachedUserInfo senderUserInfo = null;
            InventoryItemBase item = null;

            // Retrieve the item from the sender
            senderUserInfo = CommsManager.UserService.GetUserDetails(senderId);
            if (senderUserInfo == null)
            {
                m_log.ErrorFormat(
                        "[AGENT INVENTORY]: Failed to find sending user {0} for item {1}", senderId, itemId);

                return null;
            }

            item = senderUserInfo.FindItem(itemId);
            if (item == null)
            {
                m_log.ErrorFormat(
                    "[AGENT INVENTORY]: Failed to find user item {0} to give to {1}", itemId, senderId);
                return null;
            }

            if (!Permissions.BypassPermissions())
            {
                if ((item.CurrentPermissions & REQUIRED_NOTICE_ATTACH_PERMS) != REQUIRED_NOTICE_ATTACH_PERMS)
                {
                    m_log.ErrorFormat(
                        "[AGENT INVENTORY]: User {0} item {1} does not have sufficient permissions to be sent to a group.", senderId, itemId);
                    return null;
                }
            }

            return CheckDeliverGroupItem(item, groupId, senderId, senderUserInfo);
        }

        public virtual void CalcItemPermsFromInvItem(InventoryItemBase itemCopy, InventoryItemBase item, bool isOwnerTransfer)
        {
            if (isOwnerTransfer && Permissions.PropagatePermissions())
            {
                // on a transferbase is limited to the next perms
                itemCopy.BasePermissions = item.BasePermissions & item.NextPermissions;
                itemCopy.NextPermissions = item.NextPermissions;
                itemCopy.EveryonePermissions = 0;
                itemCopy.GroupPermissions = 0;

                // Apply next perms to the inventory item copy
                itemCopy.CurrentPermissions = item.CurrentPermissions & item.NextPermissions;
                if (item.InvType == (int)InventoryType.Object)
                {
                    // Preserve SLAM, may have been cleared by & with NextPermissions above
                    if ((item.CurrentPermissions & ScenePermBits.SLAM) != 0)
                    {
                        itemCopy.CurrentPermissions |= ScenePermBits.SLAM;            // Slam!
                        itemCopy.Flags |= (uint)InventoryItemFlags.ObjectSlamPerm;    // Tell the viewer we are going to slam this
                    }
                }
            }
            else
            {
                itemCopy.BasePermissions = item.BasePermissions;
                itemCopy.CurrentPermissions = item.CurrentPermissions;
            }

            // Copy the remaining perms from the existing item to the new one
            itemCopy.NextPermissions = item.NextPermissions;
            itemCopy.EveryonePermissions = item.EveryonePermissions;
            itemCopy.GroupPermissions = item.GroupPermissions;
            itemCopy.GroupID = item.GroupID;

            // On a transfer, clear group/everyone perms
            if (isOwnerTransfer)
            {
                itemCopy.EveryonePermissions = 0;
                itemCopy.GroupPermissions = 0;
                itemCopy.GroupID = UUID.Zero;
            }
        }

        public virtual void CalcItemPermsFromTaskItem(InventoryItemBase itemCopy, TaskInventoryItem item, bool isOwnerTransfer)
        {
            if (isOwnerTransfer && Permissions.PropagatePermissions())
            {
                // on a transferbase is limited to the next perms
                itemCopy.BasePermissions = item.BasePermissions & item.NextPermissions;
                itemCopy.NextPermissions = item.NextPermissions;
                itemCopy.EveryonePermissions = 0;
                itemCopy.GroupPermissions = 0;

                // Apply next perms to the inventory item copy
                itemCopy.CurrentPermissions = item.CurrentPermissions & item.NextPermissions;
                if (item.InvType == (int)InventoryType.Object)
                {
                    // Preserve SLAM, may have been cleared by & with NextPermissions above
                    if ((item.CurrentPermissions & ScenePermBits.SLAM) != 0)
                    {
                        itemCopy.CurrentPermissions |= ScenePermBits.SLAM;            // Slam!
                        itemCopy.Flags |= (uint)InventoryItemFlags.ObjectSlamPerm;    // Tell the viewer we are going to slam this
                    }
                }
            }
            else
            {
                itemCopy.BasePermissions = item.BasePermissions;
                itemCopy.CurrentPermissions = item.CurrentPermissions;
            }

            // Copy the remaining perms from the existing item to the new one
            itemCopy.NextPermissions = item.NextPermissions;
            itemCopy.EveryonePermissions = 0;
            itemCopy.GroupPermissions = item.GroupPermissions;
            itemCopy.GroupID = item.GroupID;

            // On a transfer, clear group/everyone perms
            if (isOwnerTransfer)
            {
                itemCopy.GroupPermissions = 0;
                itemCopy.GroupID = UUID.Zero;
            }
        }

        // Used to deliver group notice attachments.  Perms checks already done at send time.  May need to apply next owner perms.
        // Also called by GiveInventoryItem since it factors most of the guts of that operation.
        // Note: when sender is a group, senderID is groupID and senderUserInfo is null, otherwise senderUserInfo must be non-null.
        public virtual InventoryItemBase DeliverItem(InventoryItemBase item, UUID recipientId, UUID recipientFolderId, UUID senderId, CachedUserInfo senderUserInfo)
        {
            CachedUserInfo recipientUserInfo
                = CommsManager.UserService.GetUserDetails(recipientId);

            if (recipientUserInfo != null)
            {
                if (!recipientUserInfo.HasReceivedInventory)
                    recipientUserInfo.FetchInventory();

                // Insert a copy of the item into the recipient
                InventoryItemBase itemCopy = new InventoryItemBase();
                itemCopy.Owner = recipientId;
                itemCopy.CreatorId = item.CreatorId;
                itemCopy.ID = UUID.Random();
                itemCopy.AssetID = item.AssetID;
                itemCopy.Description = item.Description;
                itemCopy.Name = item.Name;
                itemCopy.AssetType = item.AssetType;
                itemCopy.InvType = item.InvType;
                itemCopy.Folder = recipientFolderId;

                CalcItemPermsFromInvItem(itemCopy, item, (recipientId != senderId));
                        
                // copy is never group owned
                itemCopy.GroupOwned = false;

                itemCopy.Flags = item.Flags;
                itemCopy.SalePrice = item.SalePrice;
                itemCopy.SaleType = item.SaleType;

                recipientUserInfo.AddItem(itemCopy);

                if (!Permissions.BypassPermissions())
                {
                    if (senderUserInfo != null) //  if not a group item (notice attachment)
                    {
                        if ((item.CurrentPermissions & (uint)PermissionMask.Copy) == 0)
                        {
                            senderUserInfo.DeleteItem(item);
                        }
                    }
                }

                return itemCopy;
            }
            else
            {
                m_log.ErrorFormat(
                    "[AGENT INVENTORY]: Could not find userinfo for recipient user {0} of item {1}, {2} from {3}",
                    recipientId, item.Name,
                    item.ID, senderId);
            }
            return null;
        }

        /// <summary>
        /// Give an inventory item from one user to another
        /// </summary>
        /// <param name="recipientId"></param>
        /// <param name="senderId">ID of the sender of the item</param>
        /// <param name="itemId"></param>
        /// <param name="recipientFolderId">
        /// The id of the folder in which the copy item should go.  If UUID.Zero then the item is placed in the most
        /// appropriate default folder.
        /// </param>
        /// <returns>
        /// The inventory item copy given, null if the give was unsuccessful
        /// </returns>
        public virtual InventoryItemBase GiveInventoryItem(
            UUID recipientId, UUID senderId, UUID itemId, UUID recipientFolderId)
        {
            CachedUserInfo senderUserInfo = null;
            InventoryItemBase item = null;

            // Retrieve the item from the sender
            senderUserInfo = CommsManager.UserService.GetUserDetails(senderId);
            if (senderUserInfo == null)
            {
                m_log.ErrorFormat(
                        "[AGENT INVENTORY]: Failed to find sending user {0} for item {1}", senderId, itemId);

                return null;
            }

            item = senderUserInfo.FindItem(itemId);
            if (item == null)
            {
                m_log.ErrorFormat(
                    "[AGENT INVENTORY]: Failed to find user item {0} to give to {1}", itemId, senderId);
                return null;
            }

            if (!Permissions.BypassPermissions())
            {
                if ((item.CurrentPermissions & (uint)PermissionMask.Transfer) == 0)
                {
                    m_log.ErrorFormat(
                        "[AGENT INVENTORY]: Failed to find user item {0} to give to {1}", itemId, senderId);
                    return null;
                }
            }

            return DeliverItem(item, recipientId, recipientFolderId, senderId, senderUserInfo);
        }

        /// <summary>
        /// Give an entire inventory folder from one user to another.  The entire contents (including all descendent
        /// folders) is given.
        /// </summary>
        /// <param name="recipientId"></param>
        /// <param name="senderId">ID of the sender of the item</param>
        /// <param name="folderId"></param>
        /// <param name="recipientParentFolderId">
        /// The id of the receipient folder in which the send folder should be placed.  If UUID.Zero then the
        /// recipient folder is the root folder
        /// </param>
        /// <returns>
        /// The inventory folder copy given, null if the copy was unsuccessful
        /// </returns>
        public virtual InventoryFolderBase GiveInventoryFolder(
            UUID recipientId, UUID senderId, UUID folderId, UUID recipientParentFolderId)
        {
            // Retrieve the folder from the sender
            CachedUserInfo senderUserInfo = CommsManager.UserService.GetUserDetails(senderId);

            if (null == senderUserInfo)
            {
                m_log.ErrorFormat(
                     "[AGENT INVENTORY]: Failed to find sending user {0} for folder {1}", senderId, folderId);

                return null;
            }

            if (!senderUserInfo.HasReceivedInventory)
            {
                m_log.DebugFormat(
                     "[AGENT INVENTORY]: Could not give inventory folder - have not yet received inventory for {0}",
                     senderId);

                return null;
            }

            InventoryFolderBase folder = senderUserInfo.GetFolder(folderId);

            if (null == folder)
            {
                m_log.ErrorFormat(
                     "[AGENT INVENTORY]: Could not find inventory folder {0} to give", folderId);

                return null;
            }

            CachedUserInfo recipientUserInfo
                = CommsManager.UserService.GetUserDetails(recipientId);

            if (null == recipientUserInfo)
            {
                m_log.ErrorFormat(
                     "[AGENT INVENTORY]: Failed to find receiving user {0} for folder {1}", recipientId, folderId);

                return null;
            }

            if (recipientParentFolderId == UUID.Zero)
            {
                InventoryFolderBase rootFolder = recipientUserInfo.FindFolderForType((int)FolderType.Root);
                recipientParentFolderId = rootFolder.ID;
            }

            UUID newFolderId = UUID.Random();
            InventoryFolderBase copiedFolder = recipientUserInfo.CreateFolder(folder.Name, newFolderId, (short)folder.Type, recipientParentFolderId);

            // Give all the subfolders
            List<InventoryFolderBase> newSubFolders = new List<InventoryFolderBase>();
            List<InventorySubFolderBase> subFolders = folder.SubFolders;
            foreach (InventorySubFolderBase childFolder in subFolders)
            {
                newSubFolders.Add(GiveInventoryFolder(recipientId, senderId, childFolder.ID, copiedFolder.ID));
            }

            // Give all the items
            List<InventoryItemBase> newItems = new List<InventoryItemBase>();
            List<InventoryItemBase> items = folder.Items;
            foreach (InventoryItemBase item in items)
            {
                InventoryItemBase newItem = GiveInventoryItem(recipientId, senderId, item.ID, copiedFolder.ID);
                if (newItem != null) newItems.Add(newItem);
            }

            //copy the collected folders back to the original
            foreach (InventoryFolderBase nfolder in newSubFolders)
            {
                copiedFolder.SubFolders.Add(new InventorySubFolderBase { ID = nfolder.ID, Name = nfolder.Name, Owner = nfolder.Owner, Type = nfolder.Type } );
            }

            //copy collected items back to the original
            foreach (InventoryItemBase nitem in newItems)
            {
                copiedFolder.Items.Add(nitem);
            }

            return copiedFolder;
        }

        public void CopyInventoryItem(IClientAPI remoteClient, uint callbackID, UUID oldAgentID, UUID oldItemID,
                                      UUID newFolderID, string newName)
        {
            InventoryItemBase item = CommsManager.LibraryRoot.FindItem(oldItemID);

            if (item == null)
            {
                CachedUserInfo userInfo = CommsManager.UserService.GetUserDetails(oldAgentID);
                if (userInfo == null)
                {
                    m_log.Error("[AGENT INVENTORY]: Failed to find user " + oldAgentID.ToString());
                    return;
                }

                item = userInfo.FindItem(oldItemID);

                if (item == null)
                {
                    m_log.Error("[AGENT INVENTORY]: Failed to find item " + oldItemID.ToString());
                    return;
                }
            }

            if (!Permissions.CanCreateObjectInventory(
                    item.InvType, item.ID, remoteClient.AgentId))
            {
                m_log.ErrorFormat("[AGENT INVENTORY]: User {0} not permitted to create inventory item for {1}.", remoteClient.AgentId.ToString(), oldItemID.ToString());
                return;
            }
            if ((item.CurrentPermissions & (uint)PermissionMask.Copy) == 0)
            {
                // In this case, this should have been prevented by the viewer, so it is likely that the caller here is using a hacked viewer.
                m_log.ErrorFormat("[AGENT INVENTORY]: User {0} not permitted to copy inventory item for {1}.", remoteClient.AgentId.ToString(), oldItemID.ToString());
                return;
            }

            m_log.DebugFormat("[AGENT INVENTORY]: CopyInventoryItem {0} '{1}' asset {2}", oldItemID, item.Name, item.AssetID);
            if (String.IsNullOrEmpty(newName))
            {
                newName = item.Name;
            }

            if (remoteClient.AgentId == oldAgentID)
            {
                CreateNewInventoryItem(
                    remoteClient, newFolderID, newName, item.Flags, callbackID, item.AssetID, (AssetType)item.AssetType, item.Description, (sbyte)item.InvType,
                    item.BasePermissions, item.CurrentPermissions, item.EveryonePermissions, item.NextPermissions, item.GroupPermissions, Util.UnixTimeSinceEpoch(), item.CreatorId.ToString());
            }
            else
            {
                CreateNewInventoryItem(
                    remoteClient, newFolderID, newName, item.Flags, callbackID, item.AssetID, (AssetType)item.AssetType, item.Description, (sbyte)item.InvType,
                    item.NextPermissions, item.NextPermissions, item.EveryonePermissions & item.NextPermissions, item.NextPermissions, item.GroupPermissions, Util.UnixTimeSinceEpoch(), item.CreatorId.ToString());
            }
        }

        /// <summary>
        /// Create a new asset data structure.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="description"></param>
        /// <param name="invType"></param>
        /// <param name="assetType"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        private AssetBase CreateAsset(string name, string description, sbyte assetType, byte[] data)
        {
            AssetBase asset = new AssetBase();
            asset.Name = name;
            asset.Description = description;
            asset.Type = assetType;
            asset.FullID = UUID.Random();
            asset.Data = (data == null) ? new byte[1] : data;

            return asset;
        }

        /// <summary>
        /// Move an item within the agent's inventory.
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="folderID"></param>
        /// <param name="itemID"></param>
        /// <param name="length"></param>
        /// <param name="newName"></param>
        public void MoveInventoryItem(IClientAPI remoteClient, UUID folderID, UUID itemID, int length,
                                      string newName)
        {
            try
            {
                m_log.DebugFormat(
                    "[AGENT INVENTORY]: Moving item {0} to {1} for {2}", itemID, folderID, remoteClient.AgentId);

                CachedUserInfo userInfo = CommsManager.UserService.GetUserDetails(remoteClient.AgentId);

                if (userInfo == null)
                {
                    m_log.Error("[AGENT INVENTORY]: Failed to find user " + remoteClient.AgentId.ToString());

                    return;
                }

                if (!String.IsNullOrEmpty(newName))
                {
                    InventoryItemBase item = userInfo.FindItem(itemID);

                    if (item != null)
                    {
                        item.Name = newName;
                        userInfo.ModifyAndMoveItem(item, folderID);
                    }
                    else
                    {
                        m_log.ErrorFormat("[AGENT INVENTORY]: Failed to find item {0} to move", itemID);
                    }
                }
                else
                {
                    userInfo.MoveItem(itemID, folderID);
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[AGENT INVENTORY]: Could not move item {0} to folder {1} for user {2}: {3}",
                    itemID, folderID, remoteClient.AgentId, e);
            }
        }

        /// <summary>
        /// Create a new inventory item.
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="folderID"></param>
        /// <param name="callbackID"></param>
        /// <param name="asset"></param>
        /// <param name="invType"></param>
        /// <param name="nextOwnerMask"></param>
        private void CreateNewInventoryItem(IClientAPI remoteClient, UUID folderID, string name, uint flags, uint callbackID,
                                            UUID assetId, AssetType assetType, string description, sbyte invType, uint nextOwnerMask,
                                            int creationDate, string creatorID)
        {
            uint baseMask = (uint)(PermissionMask.All | PermissionMask.Export);
            uint currentMask = (uint)(PermissionMask.All | PermissionMask.Export);
            uint groupMask = (uint)PermissionMask.None;
            uint everyoneMask = (uint)PermissionMask.None;
            CreateNewInventoryItem(
                remoteClient, folderID, name, flags, callbackID, assetId, assetType, description, invType,
                baseMask, currentMask, everyoneMask, nextOwnerMask, groupMask, creationDate, creatorID);
        }
        
        /// <summary>
        /// Create a new Inventory Item
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="folderID"></param>
        /// <param name="callbackID"></param>
        /// <param name="asset"></param>
        /// <param name="invType"></param>
        /// <param name="nextOwnerMask"></param>
        /// <param name="creationDate"></param>
        private void CreateNewInventoryItem(
            IClientAPI remoteClient, UUID folderID, string name, uint flags, uint callbackID, UUID assetId, AssetType assetType, string description,
            sbyte invType, uint baseMask, uint currentMask, uint everyoneMask, uint nextOwnerMask, uint groupMask, int creationDate, string creatorID)
        {
            CachedUserInfo userInfo
                = CommsManager.UserService.GetUserDetails(remoteClient.AgentId);

            if (userInfo != null)
            {
                if(assetType == AssetType.CallingCard && invType == (int)OpenMetaverse.InventoryType.CallingCard)
                {
                    //On 2015-12-15, a problem with calling cards occurred such that all calling cards
                    // would be duplicated by the viewer when logging in, which caused users to not
                    // display themselves and in extreme cases, would block them from doing anything
                    // along with generating 65000 calling cards for one user
                    //To address this issue, we make sure that the viewer cannot add calling cards
                    // that already exist for that user in the "Calling Cards"/Friends/All folder. 
                    // We will ignore the requests.
                    //We will cache the calling card folder in the CachedUserInfo as we don't want to 
                    // call the inventory database repeatedly to get the data.
                    if (userInfo.CheckIfCallingCardAlreadyExistsForUser(folderID, name))
                    {
                        m_log.WarnFormat(
                            "[AGENT INVENTORY]: User requested to generate duplicate calling card for client {0} uuid {1} in CreateNewInventoryItem!",
                                remoteClient.Name, remoteClient.AgentId);
                        return;
                    }
                }

                // Make sure that if this is an outfit link that the old outfit link is gone.
                // However there may be more than one if this is is an old DB, so make sure all are gone.
                if (assetType == AssetType.LinkFolder)
                {
                    // Verify this user actually owns the item, AND that the current operation is in the COF - otherwise there's no reason to clean the COF!
                    InventoryFolderBase currentOutfitFolder = GetCurrentOutfitFolder(userInfo);
                    if (currentOutfitFolder != null && currentOutfitFolder.ID == folderID)
                    {
                        uint count = 0;

                        try
                        {
                            // Get rid of all folder links in the COF: there should only ever be one, and that's the one we are about to create.
                            foreach (InventoryItemBase cofItem in currentOutfitFolder.Items)
                            {
                                if (cofItem.AssetType == (int)AssetType.LinkFolder)
                                {
                                    userInfo.DeleteItem(cofItem);
                                    ++count;
                                }
                            }
                        }
                        catch (InventoryStorageException ise)
                        {
                            m_log.WarnFormat("[AGENT INVENTORY] Had InventoryStorageException while removing out of date folder links from the COF for avatar {0}: {1}", userInfo.UserProfile.ID, ise);
                        }

                        if (count > 1) // Only report if more than one was removed.
                            m_log.InfoFormat("[AGENT INVENTORY] Removed {0} out of date folder links from the COF for avatar {1}.", count, userInfo.UserProfile.ID);

                        userInfo.UpdateFolder(currentOutfitFolder);
                    }
                }

                InventoryItemBase item = new InventoryItemBase();
                item.Owner = remoteClient.AgentId;
                item.CreatorId = creatorID;
                item.ID = UUID.Random();
                item.AssetID = assetId;
                item.Description = description;
                item.Name = name;
                item.Flags = flags;
                item.AssetType = (int)assetType;
                item.InvType = invType;
                item.Folder = folderID;
                item.CurrentPermissions = currentMask;
                item.NextPermissions = nextOwnerMask;
                item.EveryonePermissions = everyoneMask;
                item.GroupPermissions = groupMask;
                item.BasePermissions = baseMask;
                item.CreationDate = creationDate;

                userInfo.AddItem(item);
                remoteClient.SendInventoryItemCreateUpdate(item, callbackID);

                if (item.AssetType == (int)AssetType.Link && item.InvType == (int)InventoryType.Wearable)
                {
                    //this is a link to a wearable. resolve it and make sure the wearable is not broken
                    InventoryItemBase wearable = userInfo.ResolveLink(item);
                    if (wearable != null)
                    {
                        FixupItemFlagsOnWearableTypeMismatch(wearable, userInfo, remoteClient);
                    }
                }
            }
            else
            {
                m_log.WarnFormat(
                    "No user details associated with client {0} uuid {1} in CreateNewInventoryItem!",
                     remoteClient.Name, remoteClient.AgentId);
            }
        }

        protected const string COF_NAME = "Current Outfit";
        // Clone of AvatarFactoryModule::BuildCOF::GetCurrentOutfitFolder
        private static InventoryFolderBase GetCurrentOutfitFolder(CachedUserInfo userInfo)
        {
            InventoryFolderBase currentOutfitFolder = null;

            try
            {
                currentOutfitFolder = userInfo.FindFolderForType((int)FolderType.CurrentOutfit);
            }
            catch (InventoryStorageException)
            {
                // could not find it by type. load root and try to find it by name.
                InventorySubFolderBase foundFolder = null;
                InventoryFolderBase rootFolder = userInfo.FindFolderForType((int)FolderType.Root);
                foreach (var subfolder in rootFolder.SubFolders)
                {
                    if (subfolder.Name == COF_NAME)
                    {
                        foundFolder = subfolder;
                        break;
                    }
                }
                if (foundFolder != null)
                {
                    currentOutfitFolder = userInfo.GetFolder(foundFolder.ID);
                    if (currentOutfitFolder != null)
                    {
                        currentOutfitFolder.Level = InventoryFolderBase.FolderLevel.TopLevel;
                        userInfo.UpdateFolder(currentOutfitFolder);
                    }
                }
            }
            if(currentOutfitFolder != null)
                currentOutfitFolder = userInfo.GetFolder(currentOutfitFolder.ID);
            return currentOutfitFolder;
        }

        /// <summary>
        /// Hot patches inventory items that may have the wrong flags set and thus will not wear in the viewer
        /// </summary>
        /// <param name="baseItem"></param>
        /// <param name="uinfo"></param>
        /// <param name="clientView"></param>
        /// <returns></returns>
        private bool FixupItemFlagsOnWearableTypeMismatch(InventoryItemBase baseItem, CachedUserInfo uinfo, IClientAPI clientView)
        {
            if (baseItem.InvType != (int)InventoryType.Wearable) return false;

            //download the asset and extract the type. make sure the type matches the lower byte
            //of the inventory item flags

            AssetBase asset;
            try
            {
                asset = CommsManager.AssetCache.GetAsset(baseItem.AssetID, AssetRequestInfo.InternalRequest());
                if (asset == null) return false;    // not found
            }
            catch
            {
                //we couldnt get the asset, cant apply fixups
                return false;
            }

            //do we have the correct asset type to parse?
            if (asset.Type != (sbyte)AssetType.Clothing && asset.Type != (sbyte)AssetType.Bodypart) return false;

            try
            {
                uint wearableTypeInAsset = this.ParseWearableAndGetType(asset);

                //do they match?
                if (wearableTypeInAsset != (baseItem.Flags & 0xFF))
                {
                    //no. we need a fixup
                    m_log.ErrorFormat("[APPEARANCE]: Hotpatching item {0} due to flags mismatch: Got {1}, should be {2}",
                        baseItem.ID, baseItem.Flags & 0xFF, wearableTypeInAsset);

                    //remove the first byte
                    baseItem.Flags = baseItem.Flags & 0xFFFFFF00;
                    //combine our type
                    baseItem.Flags = baseItem.Flags | (wearableTypeInAsset & 0xFF);

                    //save back the inventory item with updated flags
                    uinfo.UpdateItem(baseItem);

                    //inform viewer
                    clientView.SendBulkUpdateInventory(baseItem);

                    return true;
                }

                return false;
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[APPEARANCE]: Error when attempting to parse wearable and update item for Flags hotpatch {0}", e);

                //couldnt parse wearable
                return false;
            }
        }

        private uint ParseWearableAndGetType(AssetBase asset)
        {
            string wearableData = Encoding.UTF8.GetString(asset.Data);
            bool typeFound = false;
            uint type = 0;

            using (System.IO.StringReader reader = new System.IO.StringReader(wearableData))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("type "))
                    {
                        //extract type
                        type = Convert.ToUInt32(line.Substring(5));
                        typeFound = true;
                        break;
                    }
                }
            }

            if (!typeFound) throw new Exception("Did not find type in wearable asset");

            return type;
        }

        /// <summary>
        /// Create a new inventory item.  Called when the client creates a new item directly within their
        /// inventory (e.g. by selecting a context inventory menu option).
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="transactionID"></param>
        /// <param name="folderID"></param>
        /// <param name="callbackID"></param>
        /// <param name="description"></param>
        /// <param name="name"></param>
        /// <param name="invType"></param>
        /// <param name="type"></param>
        /// <param name="wearableType"></param>
        /// <param name="nextOwnerMask"></param>
        public void CreateNewInventoryItem(IClientAPI remoteClient, UUID transactionID, UUID folderID,
                                           uint callbackID, string description, string name, sbyte invType,
                                           sbyte assetType, byte wearableType, uint nextOwnerMask, int creationDate)
        {
            m_log.DebugFormat("[AGENT INVENTORY]: Received request to create inventory item {0} in folder {1}", name, folderID);

            if (!Permissions.CanCreateUserInventory(invType, remoteClient.AgentId))
                return;

            if (transactionID == UUID.Zero)
            {
                UserProfileData profile = CommsManager.UserService.GetUserProfile(remoteClient.AgentId);

                if (profile != null)
                {
                    ScenePresence presence;
                    TryGetAvatar(remoteClient.AgentId, out presence);
                    byte[] data = null;

                    if (invType == (sbyte)InventoryType.Landmark && presence != null)
                    {
                        Vector3 pos = presence.AbsolutePosition;
                        string strdata = String.Format(
                            "Landmark version 2\nregion_id {0}\nlocal_pos {1} {2} {3}\nregion_handle {4}\n",
                            presence.Scene.RegionInfo.RegionID,
                            pos.X, pos.Y, pos.Z,
                            presence.RegionHandle);
                        data = Encoding.UTF8.GetBytes(strdata);
                    }

                    AssetBase asset = CreateAsset(name, description, assetType, data);

                    try
                    {
                        CommsManager.AssetCache.AddAsset(asset, AssetRequestInfo.GenericNetRequest());
                    }
                    catch (AssetServerException e)
                    {
                        m_log.DebugFormat("[AGENT INVENTORY]: Create inventory item failed: {0}", e);
                        if (remoteClient != null) remoteClient.SendAgentAlertMessage("Unable to upload asset. Please try again later.", false);
                        return;
                    }

                    CreateNewInventoryItem(remoteClient, folderID, asset.Name, 0, callbackID, asset.FullID, (AssetType)asset.Type, description, 
                        invType, nextOwnerMask, creationDate, remoteClient.AgentId.ToString());
                }
                else
                {
                    m_log.ErrorFormat(
                        "CreateNewInventoryItem could not load profile for agent {0}",
                        remoteClient.AgentId);
                }
            }
            else
            {
                IAgentAssetTransactions agentTransactions = this.RequestModuleInterface<IAgentAssetTransactions>();
                if (agentTransactions != null)
                {
                    agentTransactions.HandleItemCreationFromTransaction(
                        remoteClient, transactionID, folderID, callbackID, description,
                        name, invType, assetType, wearableType, nextOwnerMask);
                }
            }
        }
        
        
                /// <summary>
        /// Link an inventory item to an existing item.
        /// </summary>
        /// <remarks>
        /// The linkee item id is placed in the asset id slot.  This appears to be what the viewer expects when
        /// it receives inventory information.
        /// </remarks>
        /// <param name="remoteClient"></param>
        /// <param name="transActionID"></param>
        /// <param name="folderID"></param>
        /// <param name="callbackID"></param>
        /// <param name="description"></param>
        /// <param name="name"></param>
        /// <param name="invType"></param>
        /// <param name="type">/param>
        /// <param name="olditemID"></param>
        private void HandleLinkInventoryItem(IClientAPI remoteClient, UUID transActionID, UUID folderID,
                                             uint callbackID, string description, string name,
                                             sbyte invType, sbyte type, UUID olditemID)
        {
//            m_log.DebugFormat(
//                "[AGENT INVENTORY]: Received request from {0} to create inventory item link {1} in folder {2} pointing to {3}",
//                remoteClient.Name, name, folderID, olditemID);

            if (!Permissions.CanCreateUserInventory(invType, remoteClient.AgentId))
                return;

            if (m_sceneGraph.GetScenePresence(remoteClient.AgentId) != null)
            {
                // Don't link to default items
                if ((olditemID == AvatarWearable.DEFAULT_EYES_ITEM) ||
                    (olditemID == AvatarWearable.DEFAULT_BODY_ITEM) ||
                    (olditemID == AvatarWearable.DEFAULT_HAIR_ITEM) ||
                    (olditemID == AvatarWearable.DEFAULT_PANTS_ITEM) ||
                    (olditemID == AvatarWearable.DEFAULT_SHIRT_ITEM) ||
                    (olditemID == AvatarWearable.DEFAULT_SKIN_ITEM))
                {
                    return;
                }

                AssetBase asset = new AssetBase();
                asset.FullID = olditemID;
                asset.Type = type;
                asset.Name = name;
                asset.Description = description;

                uint baseMask = (uint)(PermissionMask.All | PermissionMask.Export);
                uint currentMask = (uint)(PermissionMask.All | PermissionMask.Export);
                uint groupMask = (uint)PermissionMask.None;
                uint everyoneMask = (uint)PermissionMask.None;
                uint nextOwnerMask = (uint)PermissionMask.All;

                CreateNewInventoryItem(
                    remoteClient, folderID, name, 0, callbackID, olditemID, (AssetType)type, description, invType,
                    baseMask, currentMask, everyoneMask, nextOwnerMask, groupMask, Util.UnixTimeSinceEpoch(),
                    remoteClient.AgentId.ToString());
            }
            else
            {
                m_log.ErrorFormat(
                    "ScenePresence for agent uuid {0} unexpectedly not found in HandleLinkInventoryItem",
                    remoteClient.AgentId);
            }
        }


        
        
        /// <summary>
        /// Remove an inventory item for the client's inventory
        /// If "forceDelete" is true, this is an internal system operation, not a user operation
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="itemID"></param>
        private void RemoveInventoryItem(IClientAPI remoteClient, UUID itemID, bool forceDelete)
        {
            CachedUserInfo userInfo
                = CommsManager.UserService.GetUserDetails(remoteClient.AgentId);

            if (userInfo == null)
            {
                m_log.WarnFormat(
                    "[AGENT INVENTORY]: Failed to find user {0} {1} to delete inventory item {2}",
                    remoteClient.Name, remoteClient.AgentId, itemID);

                return;
            }

            //verify this user actually owns the item
            InventoryItemBase item = userInfo.FindItem(itemID, true); // find it quietly since this is a remove

            if (item == null)
            {
                return;
            }

            if (item.Owner != remoteClient.AgentId)
            {
                m_log.ErrorFormat(
                    "[AGENT INVENTORY]: Not deleting item {0}, user {1} does not own it",
                    itemID, remoteClient.AgentId);

                return;
            }

            userInfo.DeleteItem(item);
        }

        private SceneObjectGroup GetGroupByPrim(uint localID)
        {
            return m_sceneGraph.GetGroupByPrim(localID);
        }

        /// <summary>
        /// Send the details of a prim's inventory to the client.
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="primLocalID"></param>
        public void RequestTaskInventory(IClientAPI remoteClient, uint primLocalID)
        {
            if (XferManager == null)
                return;

            SceneObjectGroup group = GetGroupByPrim(primLocalID);
            if (group != null)
            {
                group.RequestInventoryFile(remoteClient, primLocalID, XferManager);
            }
            else
            {
                m_log.ErrorFormat(
                    "[PRIM INVENTORY]: Inventory requested of prim {0} which doesn't exist", primLocalID);
            }
        }

        /// <summary>
        /// Remove an item from a prim (task) inventory
        /// </summary>
        /// <param name="remoteClient">Unused at the moment but retained since the avatar ID might
        /// be necessary for a permissions check at some stage.</param>
        /// <param name="itemID"></param>
        /// <param name="localID"></param>
        public void RemoveTaskInventory(IClientAPI remoteClient, UUID itemID, uint localID)
        {
            SceneObjectPart part = GetSceneObjectPart(localID);
            SceneObjectGroup group = (part==null) ? null : part.ParentGroup;

            if (group != null)
            {
                if (!Permissions.CanEditObject(group.UUID, remoteClient.AgentId, (uint)PermissionMask.Modify))
                    return;

                // Object itself must be modify to delete items
                if ((group.GetEffectivePermissions(false) & (uint)PermissionMask.Modify) != (uint)PermissionMask.Modify)
                    return;

                group.RemoveInventoryItem(localID, itemID);
                part.GetProperties(remoteClient);
            }
            else
            {
                m_log.ErrorFormat(
                    "[PRIM INVENTORY]: " +
                    "Removal of item {0} requested of prim {1} but this prim does not exist",
                    itemID,
                    localID);
            }
        }

        private InventoryItemBase CreateAgentInventoryItemFromTask(UUID destAgent, SceneObjectPart part, UUID itemId, out string reason)
        {
            TaskInventoryItem taskItem = part.Inventory.GetInventoryItem(itemId);

            if (null == taskItem)
            {
                m_log.ErrorFormat(
                    "[PRIM INVENTORY]: Tried to retrieve item ID {0} from prim {1}, {2} for creating an avatar"
                        + " inventory item from a prim's inventory item "
                        + " but the required item does not exist in the prim's inventory",
                    itemId, part.Name, part.UUID);
                reason = "item";
                return null;
            }

            if ((destAgent != taskItem.OwnerID) && ((taskItem.CurrentPermissions & (uint)PermissionMask.Transfer) == 0))
            {
                reason = "perm";
                return null;
            }

            InventoryItemBase agentItem = new InventoryItemBase();

            agentItem.ID = UUID.Random();
            agentItem.CreatorId = taskItem.CreatorID.ToString();
            agentItem.Owner = destAgent;
            agentItem.AssetID = taskItem.AssetID;
            agentItem.Description = taskItem.Description;
            agentItem.Name = taskItem.Name;
            agentItem.AssetType = taskItem.Type;
            agentItem.InvType = taskItem.InvType;
            agentItem.Flags = taskItem.Flags;

            CalcItemPermsFromTaskItem(agentItem, taskItem, (part.OwnerID != destAgent));

            reason = "";
            return agentItem;
        }

        /// <summary>
        /// Move the given item in the given prim to a folder in the client's inventory
        /// Optional remoteClient will be used if provided.
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="folderID"></param>
        /// <param name="part"></param>
        /// <param name="itemID"></param>
        public InventoryItemBase MoveTaskInventoryItem(UUID AgentId, IClientAPI remoteClient, UUID folderId, SceneObjectPart part, UUID itemId,
            bool silent, out string reason)
        {
            reason = "";
            if (part == null)
            {
                reason = "part";
                return null;
            }

            TaskInventoryItem taskItem = part.Inventory.GetInventoryItem(itemId);
            if (taskItem == null)
            {
                reason = "item";
                return null;
            }

            // Normally either the taskItem must be copyable (copy operation), or the enclosing object must be modifyable (move operation).
            // But SL allows *moves* but not *deletes* in this case, and IW has creators depending on this in no-mod boxed deliverables.
            // See commit f5e488ba572cc0cd2ca22c01068a166bc8dcfabf now reverted.

            if (remoteClient == null)
            {
                UserProfileData profile = CommsManager.UserService.GetUserProfile(AgentId);
                if (profile == null)
                {
                    m_log.ErrorFormat(
                        "[PRIM INVENTORY]: " +
                        "Avatar {0} cannot be found to add item",
                        AgentId);
                    reason = "user";
                    return null;
                }
            }


            InventoryItemBase agentItem = CreateAgentInventoryItemFromTask(AgentId, part, itemId, out reason);
            if (agentItem == null)
            {
                // reason was set by the function call above
                return null;
            }

            agentItem.Folder = folderId;
            if (remoteClient == null || silent)
            {
                AddInventoryItem(AgentId, agentItem);
            }
            else
            {
                AddInventoryItem(remoteClient, agentItem);
            }
            

            // if the Contents item is no-copy, dragging it out removes the one there
            if ((taskItem.CurrentPermissions & (uint)PermissionMask.Copy) == 0)
                part.Inventory.RemoveInventoryItem(itemId);

            return agentItem;
        }

        /// <summary>
        /// <see>ClientMoveTaskInventoryItem</see>
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="folderID"></param>
        /// <param name="primLocalID"></param>
        /// <param name="itemID"></param>
        public void ClientMoveTaskInventoryItem(IClientAPI remoteClient, UUID folderId, uint primLocalId, UUID itemId)
        {
            SceneObjectPart part = GetSceneObjectPart(primLocalId);

            if (null == part)
            {
                m_log.WarnFormat(
                    "[PRIM INVENTORY]: " +
                    "Move of inventory item {0} from prim with local id {1} failed because the prim could not be found",
                    itemId, primLocalId);

                return;
            }

            TaskInventoryItem taskItem = part.Inventory.GetInventoryItem(itemId);

            if (null == taskItem)
            {
                m_log.WarnFormat("[PRIM INVENTORY]: Move of inventory item {0} from prim with local id {1} failed"
                    + " because the inventory item could not be found",
                    itemId, primLocalId);

                return;
            }

            // Only owner can copy
            if (remoteClient.AgentId != taskItem.OwnerID)
                return;

            string reason = "";
            MoveTaskInventoryItem(remoteClient.AgentId, remoteClient, folderId, part, itemId, false, out reason);
        }

        /// <summary>
        /// <see>MoveTaskInventoryItem</see>
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="folderID"></param>
        /// <param name="part"></param>
        /// <param name="itemID"></param>
        public InventoryItemBase MoveTaskInventoryItem(UUID avatarId, UUID folderId, SceneObjectPart part, UUID itemId)
        {
            ScenePresence avatar;
            IClientAPI remoteClient = null;
            if (TryGetAvatar(avatarId, out avatar))
            {
                remoteClient = avatar.ControllingClient;
            }

            string reason = "";
            return MoveTaskInventoryItem(avatarId, remoteClient, folderId, part, itemId, false, out reason);
        }

        /// <summary>
        /// Copy a task (prim) inventory item to another task (prim)
        /// </summary>
        /// <param name="destId"></param>
        /// <param name="part"></param>
        /// <param name="itemId"></param>
        public void MoveTaskInventoryItem(UUID destId, SceneObjectPart part, UUID itemId)
        {
            TaskInventoryItem srcTaskItem = part.Inventory.GetInventoryItem(itemId);

            if (srcTaskItem == null)
            {
                m_log.ErrorFormat(
                    "[PRIM INVENTORY]: Tried to retrieve item ID {0} from prim {1}, {2} for moving"
                        + " but the item does not exist in this inventory",
                    itemId, part.Name, part.UUID);

                return;
            }

            SceneObjectPart destPart = GetSceneObjectPart(destId);

            if (destPart == null)
            {
                m_log.ErrorFormat(
                        "[PRIM INVENTORY]: " +
                        "Could not find prim for ID {0}",
                        destId);
                return;
            }

            // Can't transfer this
            //
            if ((part.OwnerID != destPart.OwnerID) && ((srcTaskItem.CurrentPermissions & (uint)PermissionMask.Transfer) == 0))
                return;

            if (part.OwnerID != destPart.OwnerID && (destPart.GetEffectiveObjectFlags() & PrimFlags.AllowInventoryDrop) == 0)
            {
                // object cannot copy items to an object owned by a different owner
                // unless llAllowInventoryDrop has been called

                return;
            }

            // must have both move and modify permission to put an item in an object
            if ((destPart.OwnerMask & ((uint)PermissionMask.Move | (uint)PermissionMask.Modify)) == 0)
            {
                return;
            }

            TaskInventoryItem destTaskItem = new TaskInventoryItem();

            destTaskItem.ItemID = UUID.Random();
            destTaskItem.CreatorID = srcTaskItem.CreatorID;
            destTaskItem.AssetID = srcTaskItem.AssetID;
            destTaskItem.GroupID = destPart.GroupID;
            destTaskItem.OwnerID = destPart.OwnerID;
            destTaskItem.ParentID = destPart.UUID;
            destTaskItem.ParentPartID = destPart.UUID;

            destTaskItem.BasePermissions = srcTaskItem.BasePermissions;
            destTaskItem.EveryonePermissions = srcTaskItem.EveryonePermissions;
            destTaskItem.GroupPermissions = srcTaskItem.GroupPermissions;
            destTaskItem.CurrentPermissions = srcTaskItem.CurrentPermissions;
            destTaskItem.NextPermissions = srcTaskItem.NextPermissions;
            destTaskItem.Flags = srcTaskItem.Flags;

            if (destPart.OwnerID != part.OwnerID)
            {
                if (Permissions.PropagatePermissions())
                {
                    destTaskItem.CurrentPermissions = srcTaskItem.CurrentPermissions &
                            srcTaskItem.NextPermissions;
                    destTaskItem.GroupPermissions = srcTaskItem.GroupPermissions &
                            srcTaskItem.NextPermissions;
                    destTaskItem.EveryonePermissions = srcTaskItem.EveryonePermissions &
                            srcTaskItem.NextPermissions;
                    destTaskItem.BasePermissions = srcTaskItem.BasePermissions &
                            srcTaskItem.NextPermissions;
                    if (destTaskItem.InvType == (int)InventoryType.Object)
                    {
                        destTaskItem.CurrentPermissions |= ScenePermBits.SLAM;            // Slam!
                        destTaskItem.Flags |= (uint)InventoryItemFlags.ObjectSlamPerm;    // Tell the viewer we are going to slam this
                    }
                }
            }

            destTaskItem.Description = srcTaskItem.Description;
            destTaskItem.Name = srcTaskItem.Name;
            destTaskItem.InvType = srcTaskItem.InvType;
            destTaskItem.Type = srcTaskItem.Type;

            destPart.Inventory.AddInventoryItem(destTaskItem, part.OwnerID != destPart.OwnerID, true);

            if ((srcTaskItem.CurrentPermissions & (uint)PermissionMask.Copy) == 0)
                part.Inventory.RemoveInventoryItem(itemId);

            ScenePresence avatar;

            if (TryGetAvatar(srcTaskItem.OwnerID, out avatar))
            {
                destPart.GetProperties(avatar.ControllingClient);
            }
        }

        public UUID MoveTaskInventoryItems(UUID AgentID, string category, SceneObjectPart host, List<UUID> items, out string reason)
        {
            ScenePresence avatar;
            IClientAPI remoteClient = null;
            CachedUserInfo profile = CommsManager.UserService.GetUserDetails(AgentID);
            if (profile == null)
            {
                m_log.ErrorFormat(
                    "[PRIM INVENTORY]: " +
                    "Avatar {0} cannot be found to add items",
                    AgentID);
                reason = "user";
                return UUID.Zero;
            }
            if (TryGetAvatar(AgentID, out avatar))
                remoteClient = avatar.ControllingClient;
            else
                avatar = null;

            UUID newFolderID = UUID.Random();

            InventoryFolderBase rootFolder = profile.FindFolderForType((int)FolderType.Root);
            profile.CreateFolder(category, newFolderID, (short)AssetType.Unknown, rootFolder.ID);

            foreach (UUID itemID in items)
            {
                InventoryItemBase newItem = MoveTaskInventoryItem(AgentID, remoteClient, newFolderID, host, itemID, true, out reason);
                if (newItem == null)
                {
                    // reason was set by the function call above
                    m_log.WarnFormat("[PRIM INVENTORY]: Could not move item {0} to new folder {1} for user {2}: {3}", itemID, newFolderID, AgentID, reason);
                    return UUID.Zero;
                }
            }

            if (remoteClient != null)
            {
                InventoryFolderBase fullFolder = profile.GetFolder(newFolderID);

                //profile.SendInventoryDecendents(remoteClient, rootFolder.ID, true, false);
                remoteClient.SendBulkUpdateInventory(fullFolder);
                profile.SendInventoryDecendents(remoteClient, newFolderID, false, true);
            }

            reason = "";
            return newFolderID;
        }

        public UUID MoveTaskInventoryItems(UUID AgentID, string category, SceneObjectPart host, List<UUID> items)
        {
            string reason;
            return MoveTaskInventoryItems(AgentID, category, host, items, out reason);
        }

        // Limits itemInfo, applying any limits in currentItem
        private void LimitItemUpdate(TaskInventoryItem itemInfo, TaskInventoryItem currentItem)
        {
            itemInfo.BasePermissions        &= currentItem.BasePermissions;
            itemInfo.CurrentPermissions        &= currentItem.BasePermissions;
            itemInfo.NextPermissions        &= currentItem.BasePermissions;
            itemInfo.GroupPermissions        &= currentItem.BasePermissions;
            itemInfo.EveryonePermissions    &= currentItem.BasePermissions;

            itemInfo.OwnerID = currentItem.OwnerID;
            itemInfo.CreatorID = currentItem.CreatorID;
            itemInfo.CreationDate = currentItem.CreationDate;

            itemInfo.InvType = currentItem.InvType;
            itemInfo.Type = currentItem.Type;
        }

        /// <summary>
        /// Update an item in a prim (task) inventory.
        /// This method does not handle scripts, <see>RezScript(IClientAPI, UUID, unit)</see>
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="transactionID"></param>
        /// <param name="itemInfo"></param>
        /// <param name="primLocalID"></param>
        public void UpdateTaskInventory(IClientAPI remoteClient, UUID transactionID, TaskInventoryItem itemInfo,
                                        uint primLocalID)
        {
            UUID itemID = itemInfo.ItemID;

            // Find the prim we're dealing with
            SceneObjectPart part = GetSceneObjectPart(primLocalID);

            if (part != null)
            {
                TaskInventoryItem currentItem = part.Inventory.GetInventoryItem(itemID);
                bool allowInventoryDrop = (part.GetEffectiveObjectFlags()
                                           & PrimFlags.AllowInventoryDrop) != 0;

                // Explicity allow anyone to add to the inventory if the
                // AllowInventoryDrop flag has been set. Don't however let
                // them update an item unless they pass the external checks
                //
                if (!Permissions.CanEditObjectInventory(part.ParentGroup.UUID, remoteClient.AgentId)
                    && (currentItem != null || !allowInventoryDrop))
                    return;

                if (currentItem == null)
                {
                    UUID copyID = UUID.Random();
                    if (itemID != UUID.Zero)
                    {
                        CachedUserInfo userInfo = CommsManager.UserService.GetUserDetails(remoteClient.AgentId);

                        if (userInfo != null)
                        {
                            InventoryItemBase item = userInfo.FindItem(itemID);

                            // Try library
                            // XXX clumsy, possibly should be one call
                            if (null == item)
                            {
                                item = CommsManager.LibraryRoot.FindItem(itemID);
                            }

                            if (item != null)
                            {
                                if ((remoteClient.AgentId != part.OwnerID) && ((item.CurrentPermissions & (uint)PermissionMask.Transfer) == 0))
                                {
                                    InformClientOfInvChange(remoteClient, item); // if it was no-copy, tell viewer to restore it.
                                    remoteClient.SendAgentAlertMessage("That item cannot be transferred to an item you do not own.\nYou may want to report this to the developer of the viewer you are currently using as a serious permissions bug.", true);
                                    return;
                                }

                                part.ParentGroup.AddInventoryItem(remoteClient, primLocalID, item, copyID);
                                m_log.InfoFormat(
                                    "[PRIM INVENTORY]: Update with item {0} requested of prim {1} for {2}",
                                    item.Name, primLocalID, remoteClient.Name);
                                part.GetProperties(remoteClient);
                                if (!Permissions.BypassPermissions())
                                {
                                    if ((item.CurrentPermissions & (uint)PermissionMask.Copy) == 0)
                                        RemoveInventoryItem(remoteClient, itemID, true);
                                }
                            }
                            else
                            {
                                m_log.ErrorFormat(
                                    "[PRIM INVENTORY]: Could not find inventory item {0} to update for {1}!",
                                    itemID, remoteClient.Name);
                            }
                        }
                    }
                }
                else // Updating existing item with new perms etc
                {
                    // Enforce the item update contents (fixes exploit, Mantis #611)
                    LimitItemUpdate(itemInfo, currentItem);
                    if (transactionID != UUID.Zero)
                    {
                        IAgentAssetTransactions agentTransactions = this.RequestModuleInterface<IAgentAssetTransactions>();
                        if (agentTransactions != null)
                        {
                            agentTransactions.HandleTaskItemUpdateFromTransaction(
                                remoteClient, part, transactionID, currentItem);
                        }
                    }
                    if (part.Inventory.UpdateTaskInventoryItemFromItem(itemInfo))
                        part.GetProperties(remoteClient);
                }
            }
            else
            {
                m_log.WarnFormat(
                    "[PRIM INVENTORY]: " +
                    "Update with item {0} requested of prim {1} for {2} but this prim does not exist",
                    itemID, primLocalID, remoteClient.Name);
            }
        }

        /// <summary>
        /// Rez a script into a prim's inventory, either ex nihilo or from an existing avatar inventory
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="itemID"> </param>
        /// <param name="localID"></param>
        public void RezScript(IClientAPI remoteClient, InventoryItemBase itemBase, UUID transactionID, uint localID)
        {
            UUID itemID = itemBase.ID;
            UUID copyID = UUID.Random();

            if (itemID != UUID.Zero)  // transferred from an avatar inventory to the prim's inventory
            {
                CachedUserInfo userInfo = CommsManager.UserService.GetUserDetails(remoteClient.AgentId);

                if (userInfo != null)
                {
                    InventoryItemBase item = userInfo.FindItem(itemID);

                    // Try library
                    bool fromLibrary = false;
                    if (null == item)
                    {
                        item = CommsManager.LibraryRoot.FindItem(itemID);
                        fromLibrary = true;
                    }

                    if (item != null)
                    {
                        SceneObjectPart part = GetSceneObjectPart(localID);
                        if (part != null)
                        {
                            if (!Permissions.CanEditObjectInventory(part.ParentGroup.UUID, remoteClient.AgentId))
                                return;

                            part.ParentGroup.AddInventoryItem(remoteClient, localID, item, copyID);
                            part.Inventory.CreateScriptInstance(copyID, 0, ScriptStartFlags.None, DefaultScriptEngine, 0, null);

                            part.GetProperties(remoteClient);

                            //if the script is no copy, remove it from the avatar inventory
                            if (!fromLibrary && ((item.CurrentPermissions & (int)PermissionMask.Copy) == 0))
                            {
                                userInfo.DeleteItem(item);
                            }
                        }
                        else
                        {
                            m_log.ErrorFormat(
                                "[PRIM INVENTORY]: " +
                                "Could not rez script {0} into prim local ID {1} for user {2}"
                                + " because the prim could not be found in the region!",
                                item.Name, localID, remoteClient.Name);
                        }
                    }
                    else
                    {
                        m_log.ErrorFormat(
                            "[PRIM INVENTORY]: Could not find script inventory item {0} to rez for {1}!",
                            itemID, remoteClient.Name);
                    }
                }
            }
            else  // script has been rezzed directly into a prim's inventory
            {
                SceneObjectPart part = GetSceneObjectPart(itemBase.Folder);
                if (part == null)
                    return;

                if (part.OwnerID != remoteClient.AgentId)
                {
                    // Group permissions
                    if ( (part.GroupID == UUID.Zero) || (remoteClient.GetGroupPowers(part.GroupID) == 0) || ((part.GroupMask & (uint)PermissionMask.Modify) == 0) )
                    return;

                } else {
                    if ((part.OwnerMask & (uint)PermissionMask.Modify) == 0)
                    return;
                }

                if (!Permissions.CanCreateObjectInventory(
                    itemBase.InvType, part.ParentGroup.UUID, remoteClient.AgentId))
                    return;

                AssetBase asset = CreateAsset(itemBase.Name, itemBase.Description, (sbyte)itemBase.AssetType, Encoding.UTF8.GetBytes("default\n{\n    state_entry()\n    {\n        llSay(0, \"Script running\");\n    }\n}"));

                try
                {
                    CommsManager.AssetCache.AddAsset(asset, AssetRequestInfo.GenericNetRequest());
                }
                catch (AssetServerException e)
                {
                    m_log.ErrorFormat("[TASK INVENTORY] Unable to rez new script. Asset write failed: {0}", e);
                    if (remoteClient != null) remoteClient.SendAgentAlertMessage("Unable to create asset. Please try again later.", false);
                    return;
                }

                TaskInventoryItem taskItem = new TaskInventoryItem();

                taskItem.ResetIDs(itemBase.Folder);
                taskItem.ParentID = itemBase.Folder;
                taskItem.CreationDate = (uint)itemBase.CreationDate;
                taskItem.Name = itemBase.Name;
                taskItem.Description = itemBase.Description;
                taskItem.Type = itemBase.AssetType;
                taskItem.InvType = itemBase.InvType;
                taskItem.OwnerID = itemBase.Owner;
                taskItem.CreatorID = itemBase.CreatorIdAsUuid;
                taskItem.BasePermissions = itemBase.BasePermissions;
                taskItem.CurrentPermissions = itemBase.CurrentPermissions;
                taskItem.EveryonePermissions = itemBase.EveryonePermissions;
                taskItem.GroupPermissions = itemBase.GroupPermissions;
                taskItem.NextPermissions = itemBase.NextPermissions;
                taskItem.GroupID = itemBase.GroupID;
                taskItem.GroupPermissions = 0;
                taskItem.Flags = itemBase.Flags;
                taskItem.PermsGranter = UUID.Zero;
                taskItem.PermsMask = 0;
                taskItem.AssetID = asset.FullID;

                part.Inventory.AddInventoryItem(taskItem, false, true);
                part.GetProperties(remoteClient);

                part.Inventory.CreateScriptInstance(taskItem, 0, ScriptStartFlags.None, DefaultScriptEngine, 0, null);
            }
        }

        /// <summary>
        /// Rez a script into a prim's inventory from another prim
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="itemID"> </param>
        /// <param name="localID"></param>
        /// <returns>Script error text to be reported, or empty string on success.</returns>
        public string RezScript(UUID srcId, SceneObjectPart srcPart, UUID destId, int pin, int running, int start_param)
        {
            TaskInventoryItem srcTaskItem = srcPart.Inventory.GetInventoryItem(srcId);

            if (srcTaskItem == null)
            {
                m_log.ErrorFormat(
                    "[PRIM INVENTORY]: Tried to retrieve item ID {0} from prim {1}, {2} for rezzing a script but the "
                        + " item does not exist in this inventory",
                    srcId, srcPart.Name, srcPart.UUID);

                return "Could not find script to load.";
            }

            SceneObjectPart destPart = GetSceneObjectPart(destId);

            if (destPart == null)
            {
                m_log.ErrorFormat("[PRIM INVENTORY]: Could not find target part {0} to load script into.", destId);
                return "Could not find target prim to load script.";
            }
        
            // Must own the object, and have modify rights
            if (srcPart.OwnerID != destPart.OwnerID)
            {
                // Group permissions
                if ( (destPart.GroupID == UUID.Zero) || (destPart.GroupID != srcPart.GroupID) ||
                ((destPart.GroupMask & (uint)PermissionMask.Modify) == 0) )
                    return "Ownership mismatch or lack of Modify permission.";
            } else {
                if ((destPart.OwnerMask & (uint)PermissionMask.Modify) == 0)
                    return "Destination lacks Modify permission.";
            }

            if ((destPart.ScriptAccessPin == 0) || (destPart.ScriptAccessPin != pin))
            {
                m_log.WarnFormat(
                        "[PRIM INVENTORY]: " +
                        "Script in object {0} : {1}, attempted to load script {2} : {3} into object {4} : {5} with invalid pin {6}",
                        srcPart.Name, srcId, srcTaskItem.Name, srcTaskItem.ItemID, destPart.Name, destId, pin);
				if (destPart.ScriptAccessPin == 0) {
					return "NO PIN";
				}
                return "PIN";   // signal a different response to the caller
            }

            TaskInventoryItem destTaskItem = new TaskInventoryItem();

            destTaskItem.ItemID = UUID.Random();
            destTaskItem.CreatorID = srcTaskItem.CreatorID;
            destTaskItem.AssetID = srcTaskItem.AssetID;
            destTaskItem.GroupID = destPart.GroupID;
            destTaskItem.OwnerID = destPart.OwnerID;
            destTaskItem.ParentID = destPart.UUID;
            destTaskItem.ParentPartID = destPart.UUID;

            destTaskItem.BasePermissions = srcTaskItem.BasePermissions;
            destTaskItem.EveryonePermissions = srcTaskItem.EveryonePermissions;
            destTaskItem.GroupPermissions = srcTaskItem.GroupPermissions;
            destTaskItem.CurrentPermissions = srcTaskItem.CurrentPermissions;
            destTaskItem.NextPermissions = srcTaskItem.NextPermissions;
            destTaskItem.Flags = srcTaskItem.Flags;

            if (destPart.OwnerID != srcPart.OwnerID)
            {
                if (Permissions.PropagatePermissions())
                {
                    destTaskItem.CurrentPermissions = srcTaskItem.CurrentPermissions &
                            srcTaskItem.NextPermissions;
                    destTaskItem.GroupPermissions = srcTaskItem.GroupPermissions &
                            srcTaskItem.NextPermissions;
                    destTaskItem.EveryonePermissions = srcTaskItem.EveryonePermissions &
                            srcTaskItem.NextPermissions;
                    destTaskItem.BasePermissions = srcTaskItem.BasePermissions &
                            srcTaskItem.NextPermissions;
                    if (destTaskItem.InvType == (int)InventoryType.Object)
                    {
                        destTaskItem.CurrentPermissions |= ScenePermBits.SLAM;            // Slam!
                        destTaskItem.Flags |= (uint)InventoryItemFlags.ObjectSlamPerm;    // Tell the viewer we are going to slam this
                    }
                }
            }

            destTaskItem.Description = srcTaskItem.Description;
            destTaskItem.Name = srcTaskItem.Name;
            destTaskItem.InvType = srcTaskItem.InvType;
            destTaskItem.Type = srcTaskItem.Type;

            // WARNING: This operation is asynchronous if the item is a script! Careful what occurs after this.
            ReplaceItemArgs replaceArgs = new ReplaceItemArgs(destTaskItem, running, start_param);
            destPart.Inventory.AddReplaceInventoryItem(destTaskItem, false, true, replaceArgs);

            return String.Empty;    // success, no error
        }

        /// <summary>
        /// The result or intended result of an action
        /// </summary>
        enum DeRezActionResult
        {
            /// <summary>
            /// No action, usually means no permission
            /// </summary>
            None,

            /// <summary>
            /// Take the object into inventory but do not delete from scene
            /// </summary>
            Take,

            /// <summary>
            /// Delete the object from the scene but do not take into inventory (never happens so far)
            /// </summary>
            Delete,

            /// <summary>
            /// Delete the object from the scene and take into inventory
            /// </summary>
            Both
        }

        private DeRezActionResult FindDeRezPermissions(IClientAPI remoteClient, SceneObjectGroup grp, DeRezAction action)
        {
            bool permissionToTake = false;
            bool permissionToDelete = false;

            switch (action)
            {
                case DeRezAction.SaveToExistingUserInventoryItem:
                    if (grp.OwnerID == remoteClient.AgentId && grp.RootPart.FromUserInventoryItemID != UUID.Zero)
                    {
                        permissionToTake = true;
                        permissionToDelete = false;
                    }
                    break;

                case DeRezAction.TakeCopy:
                    permissionToTake =
                        Permissions.CanTakeCopyObject(
                        grp.UUID,
                        remoteClient.AgentId);
                    break;

                case DeRezAction.GodTakeCopy:
                    permissionToTake =
                        Permissions.IsGod(
                        remoteClient.AgentId);
                    break;

                case DeRezAction.Take:
                    permissionToTake =
                        Permissions.CanTakeObject(
                        grp.UUID,
                        remoteClient.AgentId);

                    //If they can take, they can delete!
                    permissionToDelete = permissionToTake;
                    break;

                case DeRezAction.Delete:
                    permissionToTake =
                        Permissions.CanDeleteObject(
                        grp.UUID,
                        remoteClient.AgentId);

                    permissionToDelete = permissionToTake;
                    break;

                case DeRezAction.Return:
                    if (remoteClient != null)
                    {
                        permissionToTake =
                                Permissions.CanReturnObject(
                                grp.UUID,
                                remoteClient.AgentId);
                        permissionToDelete = permissionToTake;

                        if (permissionToDelete)
                        {
                            UUID NotifyID = (grp.OwnerID == grp.GroupID) ? grp.RootPart.LastOwnerID : grp.OwnerID; 
                            AddReturn(NotifyID, grp.Name, grp.AbsolutePosition, "parcel owner return");
                        }
                    }
                    else // Auto return passes through here with null agent
                    {
                        permissionToTake = true;
                        permissionToDelete = true;
                    }
                    break;

                default:
                    m_log.DebugFormat(
                    "[AGENT INVENTORY]: Ignoring unexpected derez action {0} for {1}", action, remoteClient.Name);
                    return DeRezActionResult.None;
            }

            if (permissionToTake && permissionToDelete)
            {
                return DeRezActionResult.Both;
            }
            else if (permissionToTake)
            {
                return DeRezActionResult.Take;
            }
            else if (permissionToDelete)
            {
                return DeRezActionResult.Delete;
            }

            return DeRezActionResult.None;
        }

        private DeRezActionResult GetIntendedResult(DeRezAction action)
        {
            switch (action)
            {
                case DeRezAction.SaveToExistingUserInventoryItem:
                    return DeRezActionResult.Take;

                case DeRezAction.TakeCopy:
                    return DeRezActionResult.Take;

                case DeRezAction.GodTakeCopy:
                    return DeRezActionResult.Take;

                case DeRezAction.Take:
                    return DeRezActionResult.Both;

                case DeRezAction.Delete:
                    return DeRezActionResult.Both;

                case DeRezAction.Return:
                    return DeRezActionResult.Both;
            }

            return DeRezActionResult.None;
        }

        SceneObjectGroup FindGroupAppropriateForDeRez(uint localId)
        {
            SceneObjectPart part = GetSceneObjectPart(localId);
            if (part == null)
                return null;

            if (part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return null;

            // Can't delete child prims
            if (part != part.ParentGroup.RootPart)
                return null;

           return part.ParentGroup;
        }

        public virtual void DeRezObject(IClientAPI remoteClient, uint localId,
            UUID groupId, DeRezAction action, UUID destinationID)
        {
            List<uint> partInfo = new List<uint>();
            partInfo.Add(localId);

            this.DeRezObjects(remoteClient, partInfo, groupId, action, destinationID);
        }

        /// <summary>
        /// Called when an object is removed from the environment into inventory.
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="localID"></param>
        /// <param name="groupID"></param>
        /// <param name="action"></param>
        /// <param name="destinationID"></param>
        /// <returns>The number of items returned/removed.</returns>
        public virtual int DeRezObjects(IClientAPI remoteClient, ICollection<uint> objectParts, 
            UUID groupId, DeRezAction action, UUID destinationID)
        {
            int count = 0;
            //get the intended behavior of this action
            DeRezActionResult intendedResult = this.GetIntendedResult(action);

            //List for collecting found prims
            List<SceneObjectGroup> groupsToDerez = new List<SceneObjectGroup>();
            bool abort = false;
            bool reportErrorForNoGroupsReturned = true;

            using (SceneTransaction transaction = SceneGraph.BeginPrimTransaction(objectParts))
            {
                try
                {
                    //check to make sure each part can meet the intended result
                    //skip with null client
                    if (remoteClient != null)
                    {
                        foreach (uint partInfo in objectParts)
                        {
                            SceneObjectGroup grp = this.FindGroupAppropriateForDeRez(partInfo);
                            if (grp == null) return count; //couldn't find groups
                            if (grp.IsAttachment) continue; //none of this should be done on attachments

                            if (this.FindDeRezPermissions(remoteClient, grp, action) != this.GetIntendedResult(action))
                            {
                                //missing permissions for one or more of the objects
                                remoteClient.SendAlertMessage("Insuffient permissions on '" + grp.Name + "'.");
                                return count;
                            }
                            else
                            {
                                if (!this.AddToListIfDerezOk(intendedResult, grp, groupsToDerez))
                                {
                                    //could not derez
                                    abort = true;
                                    remoteClient.SendBlueBoxMessage(UUID.Zero, String.Empty, "Could not take/remove '" + grp.Name + "', operation aborted.");
                                    return count;
                                }
                            }
                        }
                    }
                    else
                    {
                        //delete called from parcel return
                        foreach (uint partInfo in objectParts)
                        {
                            SceneObjectGroup grp = this.FindGroupAppropriateForDeRez(partInfo);
                            if (grp == null) return count; //couldn't find groups
                            if (grp.IsAttachment) continue; //none of this should be done on attachments

                            //protect against a return on the same object happing multiple times
                            if (!this.AddToListIfDerezOk(intendedResult, grp, groupsToDerez))
                            {
                                //could not derez
                                reportErrorForNoGroupsReturned = false;
                                continue;
                            }
                        }
                    }

                    if (groupsToDerez.Count == 0)
                    {
                        //there must've been a problem locating the group(s), inform the user

                        if (reportErrorForNoGroupsReturned)
                        {
                            m_log.ErrorFormat("[Scene.Inventory] No groups found to derez after scene search, Action: {0}", action);
                        }

                        /*
                        if (remoteClient != null)
                        {
                            remoteClient.SendBlueBoxMessage(UUID.Zero, String.Empty, "Could not find objects to derez");
                        }
                        */
                        return count;
                    }

                    //if we got here, we can start completing the requested action
                    ForceBulkSceneObjectBackup(groupsToDerez);

                    if (intendedResult == DeRezActionResult.Both || intendedResult == DeRezActionResult.Take)
                    {
                        m_asyncSceneObjectDeleter.DeleteToInventory(
                                action, destinationID, groupsToDerez, remoteClient,
                                intendedResult == DeRezActionResult.Both //also delete?
                                );
                    }
                    else if (intendedResult == DeRezActionResult.Delete)
                    {
                        DeleteSceneObjects(groupsToDerez, false);
                    }
                    count += groupsToDerez.Count;
                }
                finally
                {
                    if (abort)
                    {
                        //clean up the derezzed flag 
                        foreach (var group in groupsToDerez)
                        {
                            group.IsBeingDerezzed = false;
                        }
                    }
                }
            }

            return count;
        }

        private bool AddToListIfDerezOk(DeRezActionResult intendedResult, SceneObjectGroup grp, List<SceneObjectGroup> groupsToDerez)
        {
            //protect against a return on the same object happing multiple times
            if (intendedResult == DeRezActionResult.Both || intendedResult == DeRezActionResult.Delete)
            {
                if (grp.IsBeingDerezzed)
                {
                    return false;
                }
                else
                {
                    grp.IsBeingDerezzed = true;
                }
            }

            groupsToDerez.Add(grp);

            return true;
        }

        /// <summary>
        /// Groups the given parts together by their owner
        /// </summary>
        /// <param name="objectGroups"></param>
        /// <returns></returns>
        private Dictionary<UUID, List<SceneObjectGroup>> CollectByOwner(IEnumerable<SceneObjectGroup> objectGroups, IClientAPI remoteClient)
        {
            Dictionary<UUID, List<SceneObjectGroup>> ownerList = new Dictionary<UUID, List<SceneObjectGroup>>();

            foreach (SceneObjectGroup part in objectGroups)
            {
                UUID ownerId = UUID.Zero;

                //make sure this is an actual person owning it and not a group
                UserProfileData profile = CommsManager.UserService.GetUserProfile(part.OwnerID);
                if (profile != null)
                {
                    //user exists and is not a group
                    ownerId = profile.ID;
                }
                else
                {
                    // the owner isn't a user (e.g. is a group), so find the approriate user to give the object to
                    profile = CommsManager.UserService.GetUserProfile(part.RootPart.LastOwnerID);
                    if (profile != null)
                    {
                        ownerId = profile.ID;   // normal return for a group-deeded object
                    }
                    else
                    {
                        if (remoteClient != null)
                        {
                            // can't find the last owner... so object now belongs to the deleter
                            ownerId = remoteClient.AgentId;
                            m_log.WarnFormat("Delete patched: Owner {0} not found for object '{1}' {2}, sending to deleter instead.", part.RootPart.LastOwnerID, part.Name, part.UUID);
                        }
                        else
                        {
                            m_log.WarnFormat("Delete FAILED: Owner {0} not found for object '{1}' {2}, and no remote client requesting deletion.", part.RootPart.LastOwnerID, part.Name, part.UUID);
                        }
                    }
                }

                if (ownerId != UUID.Zero)
                {
                    if (!ownerList.ContainsKey(ownerId))
                    {
                        ownerList.Add(ownerId, new List<SceneObjectGroup>());
                    }

                    ownerList[ownerId].Add(part);
                }
            }

            return ownerList;
        }

        private StopScriptReason SerializationFlagsToStopReason(SerializationFlags flags)
        {
            if ((flags & SerializationFlags.StopScripts) == 0)
                return StopScriptReason.None;
            if ((flags & SerializationFlags.FromCrossing) != 0)
                return StopScriptReason.Crossing;
            else
                return StopScriptReason.Derez;
        }

        private void CopyItemsToFolder(CachedUserInfo inventoryOwner, UUID folderId, IList<SceneObjectGroup> items,
            IClientAPI remoteClient, bool stopScripts)
        {
            SerializationFlags serFlags = stopScripts ? SerializationFlags.StopScripts : SerializationFlags.None;

            if (items.Count == 0)
            {
                throw new ArgumentOutOfRangeException("CopyItemsToFolder() Given 0 items to copy");
            }

            //if there is only one item, this will be the basis for it
            //if this a coalesced object, this will be the basis for the name etc
            InventoryItemBase newInvItem = new InventoryItemBase();
            newInvItem.CreatorId = items[0].RootPart.CreatorID.ToString();
            newInvItem.ID = UUID.Random();
            newInvItem.InvType = (int)InventoryType.Object;
            newInvItem.Folder = folderId;
            newInvItem.Owner = inventoryOwner.UserProfile.ID;

            if (items.Count == 1)
            {
                ItemPermissionBlock newPerms = items[0].GetNewItemPermissions(inventoryOwner.UserProfile.ID);
                newPerms.ApplyToOther(newInvItem);

                byte[] objAsset = this.DoSerializeSingleGroup(items[0], serFlags);
                
                //single item, store as single item asset
                AssetBase itemAsset = CreateAsset(
                    items[0].GetPartName(items[0].RootPart.LocalId),
                    items[0].GetPartDescription(items[0].RootPart.LocalId),
                    (sbyte)AssetType.Object,
                    objAsset);

                //exception storing asset will get thrown up and prevent the object from
                //vanishing from the scene
                try
                {
                    CommsManager.AssetCache.AddAsset(itemAsset, AssetRequestInfo.InternalRequest());
                }
                catch (AssetServerException)
                {
                    if (remoteClient != null) remoteClient.SendAgentAlertMessage("Unable to create asset. Please try again later.", false);
                    throw;
                }
                
                //store inventory object
                newInvItem.CreationDate = Util.UnixTimeSinceEpoch();
                newInvItem.Description = itemAsset.Description;
                newInvItem.Name = itemAsset.Name;
                newInvItem.AssetType = itemAsset.Type;
                newInvItem.AssetID = itemAsset.FullID;

                inventoryOwner.AddItem(newInvItem);

                this.InformClientOfInvChange(remoteClient, newInvItem);
            }
            else if (items.Count > 1)
            {
                //we have a coalesced object, this must be serialized differently and flags set

                //retrieve a set of the next permissions for each item
                List<ItemPermissionBlock> itemPermissions = new List<ItemPermissionBlock>();
                foreach (SceneObjectGroup item in items)
                {
                    ItemPermissionBlock permsBlock = item.GetNewItemPermissions(inventoryOwner.UserProfile.ID);
                    itemPermissions.Add(permsBlock);
                }

                //serialize coalesced
                byte[] objAsset = this.DoSerializeCoalesced(items, itemPermissions, serFlags);

                //grab the first item to set the name etc
                SceneObjectGroup modelItem = items[0];

                //multiple items, store as multi item asset
                AssetBase itemAsset = CreateAsset(
                    modelItem.GetPartName(modelItem.RootPart.LocalId),
                    modelItem.GetPartDescription(modelItem.RootPart.LocalId),
                    (sbyte)AssetType.Object,
                    objAsset);

                //exception storing asset will get thrown up and prevent the object from
                //vanishing from the scene
                try
                {
                    CommsManager.AssetCache.AddAsset(itemAsset, AssetRequestInfo.InternalRequest());
                }
                catch (AssetServerException)
                {
                    if (remoteClient != null) remoteClient.SendAgentAlertMessage("Unable to create asset. Please try again later.", false);
                    throw;
                }

                //calculate minimum permissions for the coalesced object
                ItemPermissionBlock.CalculateCoalescedPermissions(itemPermissions).ApplyToOther(newInvItem);

                //misc
                newInvItem.CreationDate = Util.UnixTimeSinceEpoch();
                newInvItem.Description = itemAsset.Description;
                newInvItem.Name = itemAsset.Name;
                newInvItem.AssetType = itemAsset.Type;
                newInvItem.AssetID = itemAsset.FullID;

                //mark as coalesced
                newInvItem.Flags |= (uint)InventoryItemBase.Flag.OBJECT_HAS_MULTIPLE_ITEMS;

                //store inventory object
                inventoryOwner.AddItem(newInvItem);

                this.InformClientOfInvChange(remoteClient, newInvItem);
            }
        }

        private byte[] DoSerializeCoalesced(IList<SceneObjectGroup> items, List<ItemPermissionBlock> itemPermissions, SerializationFlags flags)
        {
            ISerializationEngine engine;
            if (ProviderRegistry.Instance.TryGet<ISerializationEngine>(out engine))
            {
                Dictionary<UUID, ItemPermissionBlock> perms = new Dictionary<UUID,ItemPermissionBlock>();
                for (int i = 0; i < items.Count; i++)
                {
                    perms.Add(items[i].UUID, itemPermissions[i]);
                }

                CoalescedObject csog = new CoalescedObject(items, perms);
                return engine.InventoryObjectSerializer.SerializeCoalescedObjToInventoryBytes(csog, flags);
            }
            else
            {
                StopScriptReason stopScriptReason = SerializationFlagsToStopReason(flags);
                return Utils.StringToBytes(CoalescedSceneObjectSerializer.ToXmlFormat(items, itemPermissions, stopScriptReason));
            }
        }

        private byte[] DoSerializeSingleGroup(SceneObjectGroup sceneObjectGroup, SerializationFlags flags)
        {
            ISerializationEngine engine;
            if (ProviderRegistry.Instance.TryGet<ISerializationEngine>(out engine))
            {
                return engine.InventoryObjectSerializer.SerializeGroupToInventoryBytes(sceneObjectGroup, flags);
            }
            else
            {
                StopScriptReason reason = SerializationFlagsToStopReason(flags);
                return Utils.StringToBytes(SceneObjectSerializer.ToOriginalXmlFormat(sceneObjectGroup, reason));
            }
        }

        private void PerformInventoryDelete(UUID folderId, IEnumerable<SceneObjectGroup> objectGroups,
            IClientAPI remoteClient)
        {
            Dictionary<UUID, List<SceneObjectGroup>> owners = this.CollectByOwner(objectGroups, remoteClient);

            //for each owner, determine the correct folder for the item and serialize
            foreach (KeyValuePair<UUID, List<SceneObjectGroup>> ownerObjects in owners)
            {
                CachedUserInfo uInfo = CommsManager.UserService.GetUserDetails(ownerObjects.Key);

                InventoryFolderBase destinationFolder;

                //owner goes to trash, other goes to l&f
                if (remoteClient.AgentId == uInfo.UserProfile.ID)
                {
                    destinationFolder = uInfo.FindFolderForType((int)FolderType.Trash);
                }
                else
                {
                    destinationFolder = uInfo.FindFolderForType((int)FolderType.LostAndFound);
                }

                this.CopyItemsToFolder(uInfo, destinationFolder.ID, ownerObjects.Value, remoteClient, true);
            }
        }

        private void PerformInventoryReturn(UUID folderId, IEnumerable<SceneObjectGroup> objectGroups,
            IClientAPI remoteClient)
        {
            // NOTE: CollectByOwner handles group-owned objects, placing them in the entry for the LastOwner
            //       Just be careful to use the dictionary key rather than the SOG.OwnerID below.
            Dictionary<UUID, List<SceneObjectGroup>> owners = this.CollectByOwner(objectGroups, remoteClient);

            //for each owner, all items go to lost and founds
            foreach (KeyValuePair<UUID, List<SceneObjectGroup>> ownerObjects in owners)
            {
                // If the owner of ownerObjects is a group, the previous user owners may be different.
                foreach (SceneObjectGroup SOG in ownerObjects.Value)
                {
                    // Find the calculated owner based on the dictionary key value
                    CachedUserInfo uInfo = CommsManager.UserService.GetUserDetails(ownerObjects.Key);
                    if (uInfo == null)
                    {
                        // The problem here is this is an async function with no failure case, so we need to do something with the object.
                        // We can't fail it, skip/ignore it, unless the desired behavior is to lose the items.  Return it to the user doing the return.
                        uInfo = CommsManager.UserService.GetUserDetails(remoteClient.AgentId);
                        if (uInfo != null)
                            m_log.WarnFormat("Return patched: Owner {0} not found for object '{1}' {2}, returning to {3} instead.", SOG.RootPart.LastOwnerID, SOG.Name, SOG.UUID, remoteClient.Name);
                    }
                    if (uInfo == null)
                    {
                        // Complete failure to look up any users.
                        m_log.ErrorFormat("Return failed: Object lost. No owner {0} for object '{1}' {2}", SOG.RootPart.LastOwnerID, SOG.Name, SOG.UUID);
                    }
                    else
                    {
                        InventoryFolderBase destinationFolder = uInfo.FindFolderForType((int)FolderType.LostAndFound);
                        List<SceneObjectGroup> items = new List<SceneObjectGroup>();
                        items.Add(SOG);

                        // If the user cannot be found, we want the line below to throw an exception anyway,
                        // so that the m_scene.DeleteSceneObjects does not occur.  This could result in an extra copy in
                        // their L&F if multiple objects were selected, but at least no data loss.
                        this.CopyItemsToFolder(uInfo, destinationFolder.ID, items, remoteClient, true);
                    }
                }
            }
        }

        private void PerformGodTakeCopy(UUID folderId, IEnumerable<SceneObjectGroup> objectGroups,
            IClientAPI remoteClient)
        {
            Dictionary<UUID, List<SceneObjectGroup>> owners = this.CollectByOwner(objectGroups, remoteClient);

            //for each owner, all items go to the object folder for this client who has been authenticated as a god
            foreach (KeyValuePair<UUID, List<SceneObjectGroup>> ownerObjects in owners)
            {
                CachedUserInfo uInfo = CommsManager.UserService.GetUserDetails(remoteClient.AgentId);
                InventoryFolderBase destinationFolder = uInfo.FindFolderForType((int)AssetType.Object);

                this.CopyItemsToFolder(uInfo, destinationFolder.ID, ownerObjects.Value, remoteClient, false);
            }
        }

        private void PerformSaveToExistingUserInventoryItem(UUID folderId, IEnumerable<SceneObjectGroup> objectGroups,
            IClientAPI remoteClient)
        {
            CachedUserInfo uInfo = CommsManager.UserService.GetUserDetails(remoteClient.AgentId);

            //there should only be one item
            foreach (SceneObjectGroup group in objectGroups)
            {
                //find the item
                InventoryItemBase item = uInfo.FindItem(group.RootPart.FromUserInventoryItemID);

                if (item != null)
                {
                    byte[] attachmentBytes = this.DoSerializeSingleGroup(group, SerializationFlags.StopScripts);

                    AssetBase asset = CreateAsset(
                        group.GetPartName(group.RootPart.LocalId),
                        group.GetPartDescription(group.RootPart.LocalId),
                        (sbyte)AssetType.Object,
                        attachmentBytes);

                    try
                    {
                        //exception storing asset will get thrown up and prevent the object from
                        //vanishing from the scene
                        CommsManager.AssetCache.AddAsset(asset, AssetRequestInfo.InternalRequest());
                    }
                    catch (AssetServerException)
                    {
                        if (remoteClient != null) remoteClient.SendAgentAlertMessage("Unable to create asset. Please try again later.", false);
                        throw;
                    }

                    item.AssetID = asset.FullID;
                    uInfo.UpdateItem(item);

                    this.InformClientOfInvChange(remoteClient, item);
                }
                else
                {
                    m_log.DebugFormat(
                           "[AGENT INVENTORY]: Object {0} {1} scheduled for save to inventory does not exist.",
                           group.Name, group.UUID);
                }
            }
        }

        private void InformClientOfInvChange(IClientAPI remoteClient, InventoryItemBase item)
        {
            if (remoteClient != null && item.Owner == remoteClient.AgentId)
            {
                remoteClient.SendInventoryItemCreateUpdate(item, 0);
            }
            else
            {
                ScenePresence notifyUser = GetScenePresence(item.Owner);
                if (notifyUser != null)
                {
                    notifyUser.ControllingClient.SendInventoryItemCreateUpdate(item, 0);
                }
            }
        }

        InventoryFolderBase GetFolderIfValidAndNotInTrash(UUID folderId, CachedUserInfo uInfo)
        {
            try
            {
                //first make sure the folder exists at all
                InventoryFolderBase desiredFolder = uInfo.GetFolderAttributesChecked(folderId); //this will throw if the user doesnt own the folder or if it doesnt exist
                if (desiredFolder == null) return null;

                InventoryFolderBase topLevelFolder = uInfo.FindTopLevelFolderFor(desiredFolder.ID);

                if ((topLevelFolder != null) && (topLevelFolder.Type != (int)FolderType.Trash))
                {
                    return desiredFolder;
                }
            }
            catch (Exception e)
            {
                m_log.InfoFormat("[AGENT INVENTORY] Unable to check heirarchy for {0}. {1}", folderId, e);
            }

            return null;
        }
        
        private void PerformTake(UUID clientPreferredFolderId, IEnumerable<SceneObjectGroup> objectGroups,
            IClientAPI remoteClient, bool stopScripts)
        {
            CachedUserInfo uInfo = CommsManager.UserService.GetUserDetails(remoteClient.AgentId);

            //creating this objectGroups list sucks, but no way to fix it as everything else
            //comes in enumerable
            List<SceneObjectGroup> listOfGroups = new List<SceneObjectGroup>(objectGroups);

            InventoryFolderBase folder = null;

            //if there is only one item, and we own it, we can take it into its original
            //folder if it exists
            if (listOfGroups.Count == 1 &&
                listOfGroups[0].OwnerID == remoteClient.AgentId &&
                listOfGroups[0].RezzedFromFolderId != UUID.Zero)
            {
                folder = this.GetFolderIfValidAndNotInTrash(listOfGroups[0].RezzedFromFolderId, uInfo);
            }

            //otherwise, try the client preferred folder
            if (folder == null && clientPreferredFolderId != UUID.Zero)
            {
                folder = this.GetFolderIfValidAndNotInTrash(clientPreferredFolderId, uInfo);
            }

            //maybe the objects folder?
            if (folder == null)
            {
                try
                {
                    folder = uInfo.FindFolderForType((int)AssetType.Object);
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("[AGENT INVENTORY] Exception trying to find objects folder for {0}: {1}", remoteClient.AgentId, e);
                }
            }

            //fall back to root folder
            if (folder == null)
            {
                try
                {
                    folder = uInfo.FindFolderForType((int)FolderType.Root);
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("[AGENT INVENTORY] Exception trying to find root folder for {0}: {1}", remoteClient.AgentId, e);
                }

                if (folder == null)
                {
                    m_log.ErrorFormat("[AGENT INVENTORY] Root folder not found for {0}, can not take item", remoteClient.AgentId);
                    throw new InventoryObjectMissingException(String.Format("Root folder not found for {0}, can not take item", remoteClient.AgentId));
                }
            }

            this.CopyItemsToFolder(uInfo, folder.ID, listOfGroups, remoteClient, stopScripts);
        }

        /// <summary>
        /// Delete a scene object from a scene and place in the given avatar's inventory.
        /// Returns the UUID of the newly created asset.
        /// </summary>
        /// <param name="action"></param>
        /// <param name="folderID"></param>
        /// <param name="objectGroup"></param>
        /// <param name="remoteClient"> </param>
        public virtual UUID DeleteToInventory(DeRezAction action, UUID folderID,
                IEnumerable<SceneObjectGroup> objectGroups, IClientAPI remoteClient)
        {
            StringBuilder objectNames = new StringBuilder();
            foreach (var obj in objectGroups)
            {
                objectNames.Append(obj.Name);
                objectNames.Append(", ");
            }

            string agentText = "(unknown/internal)";
            if (remoteClient != null)
                agentText = remoteClient.AgentId.ToString();
            m_log.InfoFormat("[AGENT INVENTORY] About to DeRezAction.{0} for client {1}. Groups: {2}", action, agentText, objectNames.ToString());

            //now lets deal with the individual cases..
            switch (action)
            {
                case DeRezAction.Delete:
                    this.PerformInventoryDelete(folderID, objectGroups, remoteClient);
                    break;

                case DeRezAction.Return:
                    this.PerformInventoryReturn(folderID, objectGroups, remoteClient);
                    break;

                case DeRezAction.GodTakeCopy:
                    this.PerformGodTakeCopy(folderID, objectGroups, remoteClient);
                    break;

                case DeRezAction.SaveToExistingUserInventoryItem:
                    this.PerformSaveToExistingUserInventoryItem(folderID, objectGroups, remoteClient);
                    break;

                case DeRezAction.Take:
                    this.PerformTake(folderID, objectGroups, remoteClient, true);
                    break;

                case DeRezAction.TakeCopy:
                    this.PerformTake(folderID, objectGroups, remoteClient, false);
                    break;
            }

            return UUID.Zero;
        }

        struct KnownAssetUpdateRequest
        {
            public IClientAPI remoteClient;
            public SceneObjectGroup attachment;
            public UUID assetID;
            public UUID agentID;
            public bool forDeletion;
        }

        private Dictionary<UUID, KnownAssetUpdateRequest> _pendingAssetUpdates = new Dictionary<UUID, KnownAssetUpdateRequest>();

        public void DeleteAttachment(SceneObjectGroup grp)
        {
            if (grp != null)
            {
                grp.DetachToInventoryPrep();
                DeleteSceneObject(grp, false);
                return;
            }
        }

        public void SaveAndDeleteAttachment(IClientAPI remoteClient, SceneObjectGroup grp, UUID assetID, UUID agentID)
        {
            if (grp != null)
            {
                m_log.DebugFormat("[DETACH]: Saving attachpoint {0}: [{1}] {2}", grp.GetCurrentAttachmentPoint(), grp.UUID, grp.Name);

                if (!grp.HasGroupChanged && grp.GroupScriptEvents == 0)
                {
                    m_log.InfoFormat("[ATTACHMENT]: Save request for {0} which is unchanged", grp.UUID);
                    DeleteAttachment(grp);
                    return;
                }

                if (grp.IsTempAttachment)
                {
                    m_log.InfoFormat("[ATTACHMENT]: Ignored save request for {0} which is temporary", grp.UUID);
                    DeleteAttachment(grp);
                    return;
                }

                grp.DetachToInventoryPrep();

                KnownAssetUpdateRequest updateReq = new KnownAssetUpdateRequest
                {
                    remoteClient = remoteClient,
                    attachment = grp,
                    assetID = assetID,
                    agentID = agentID,
                    forDeletion = true
                };

                lock (_pendingAssetUpdates)
                {
                    _pendingAssetUpdates[grp.UUID] = updateReq;
                }
            }
        }

        private void DoStoreKnownAsset(IClientAPI remoteClient, SceneObjectGroup objectGroup, UUID assetID, UUID agentID, bool forDeletion)
        {
            m_log.InfoFormat(
                "[ATTACHMENT]: Updating asset for attachment {0}, attachpoint {1}",
                objectGroup.UUID, objectGroup.AttachmentPoint);

            byte[] sceneObjectXml = this.DoSerializeSingleGroup(objectGroup, SerializationFlags.StopScripts);

            CachedUserInfo userInfo = CommsManager.UserService.GetUserDetails(agentID);

            if (userInfo != null)
            {
                InventoryItemBase item = userInfo.FindItem(assetID);

                if (item != null)
                {
                    AssetBase asset = CreateAsset(
                        objectGroup.GetPartName(objectGroup.LocalId),
                        objectGroup.GetPartDescription(objectGroup.LocalId),
                        (sbyte)AssetType.Object,
                        sceneObjectXml);

                    try
                    {
                        CommsManager.AssetCache.AddAsset(asset, AssetRequestInfo.InternalRequest());
                    }
                    catch (AssetServerException e)
                    {
                        m_log.ErrorFormat("[ATTACHMENT] Unable to store asset to save attachment: {0}", e);
                        if (remoteClient != null) remoteClient.SendAgentAlertMessage("Unable to create asset. Please try again later.", false);
                        return;
                    }

                    item.AssetID = asset.FullID;
                    item.Description = asset.Description;
                    item.Name = asset.Name;
                    item.AssetType = asset.Type;
                    item.InvType = (int)InventoryType.Object;
                    item.Folder = item.Folder;

                    userInfo.UpdateItem(item);

                    if (forDeletion)
                    {
                        DeleteSceneObject(objectGroup, false);
                    }

                    // this gets called when the agent loggs off!
                    if (remoteClient != null)
                    {
                        remoteClient.SendInventoryItemCreateUpdate(item, 0);
                    }
                }
            }
            else if (forDeletion)
                DeleteSceneObject(objectGroup, false);
        }

        public UUID AttachObjectAssetStore(IClientAPI remoteClient, SceneObjectGroup grp, UUID AgentId, UUID destFolderID, out UUID itemID)
        {
            itemID = UUID.Zero;
            if (grp != null)
            {
                byte[] sceneObjectData = this.DoSerializeSingleGroup(grp, SerializationFlags.None);

                CachedUserInfo userInfo =
                    CommsManager.UserService.GetUserDetails(AgentId);
                
                if (userInfo != null)
                {
                    AssetBase asset = CreateAsset(
                        grp.GetPartName(grp.LocalId),
                        grp.GetPartDescription(grp.LocalId),
                        (sbyte)AssetType.Object,
                        sceneObjectData);

                    try
                    {
                        CommsManager.AssetCache.AddAsset(asset, AssetRequestInfo.InternalRequest());
                    }
                    catch (AssetServerException e)
                    {
                        m_log.ErrorFormat("[ATTACHMENT] Unable to attach object. Storing asset failed: {0}", e);
                        return UUID.Zero;
                    }

                    if (destFolderID != UUID.Zero)
                    {
                        //make sure we own the folder this item is headed to and that it exists
                        try
                        {
                            //will throw InventorySecurityException, or InventoryObjectMissingException
                            //if something is wrong
                            userInfo.GetFolderAttributesChecked(destFolderID);
                        }
                        catch (Exception)
                        {
                            destFolderID = UUID.Zero;
                        }
                    }

                    if (destFolderID == UUID.Zero)
                    {
                        InventoryFolderBase objFolder = userInfo.FindFolderForType((int)InventoryType.Object);
                        destFolderID = objFolder.ID;
                    }

                    InventoryItemBase item = new InventoryItemBase();
                    item.CreatorId = grp.RootPart.CreatorID.ToString();
                    item.Owner = remoteClient.AgentId;
                    item.ID = UUID.Random();
                    item.AssetID = asset.FullID;
                    item.Description = asset.Description;
                    item.Name = asset.Name;
                    item.AssetType = asset.Type;
                    item.InvType = (int)InventoryType.Object;

                    item.Folder = destFolderID;
                    item.CreationDate = Util.UnixTimeSinceEpoch();

                    ItemPermissionBlock newPerms = grp.GetNewItemPermissions(remoteClient.AgentId);
                    newPerms.ApplyToOther(item);

                    // sets assetID so client can show asset as 'attached' in inventory
                    grp.SetFromItemID(item.ID);

                    userInfo.AddItem(item);
                    remoteClient.SendInventoryItemCreateUpdate(item, 0);

                    itemID = item.ID;
                    return item.AssetID;
                }
                return UUID.Zero;
            }
            return UUID.Zero;
        }

        private Vector3 ValidLocation(Vector3 pos)
        {
            float x = pos.X;
            float y = pos.Y;
            float z = pos.Z;
            float xmax = (float)this.Heightmap.Width - 0.01f;
            float ymax = (float)this.Heightmap.Height - 0.01f;
            float zmin;

            // Clamp to valid position
            if (x < 0.0f)
                x = 0.0f;
            else if (x >= xmax)
                x = xmax;
            if (y < 0.0f)
                y = 0.0f;
            else if (y >= ymax)
                y = ymax;

            zmin = (float)this.Heightmap.CalculateHeightAt(x, y) + 0.01f;
            if (z < zmin)
                z = zmin;

            return new Vector3(x, y, z);
        }

        /// <summary>
        /// Event Handler Rez an object into a scene
        /// Calls the non-void event handler
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="itemID"></param>
        /// <param name="RayEnd"></param>
        /// <param name="RayStart"></param>
        /// <param name="RayTargetID"></param>
        /// <param name="BypassRayCast"></param>
        /// <param name="RayEndIsIntersection"></param>
        /// <param name="EveryoneMask"></param>
        /// <param name="GroupMask"></param>
        /// <param name="RezSelected"></param>
        /// <param name="RemoveItem"></param>
        /// <param name="fromTaskID"></param>
        public virtual void RezObject(IClientAPI remoteClient, UUID groupID, UUID itemID, Vector3 RayEnd, Vector3 RayStart,
                                    UUID RayTargetID, byte BypassRayCast, bool RayEndIsIntersection,
                                    bool RezSelected, bool RemoveItem, UUID fromTaskID)
        {
            RezObject(
                remoteClient, groupID, itemID, RayEnd, RayStart, RayTargetID, BypassRayCast, RayEndIsIntersection,
                RezSelected, RemoveItem, fromTaskID, false, 0, 0);
        }

        private bool AddObjectToSceneIfPermitted(SceneObjectGroup group, IClientAPI remoteClient, Vector3 pos, bool attachment,
            UUID? rezzedByObjectUUID)
        {
            group.ResetIDs();

            if (this.IsBadUser(group.OwnerID))
            {
                if (remoteClient != null) remoteClient.SendAlertMessage("Can't rez object '" + group.Name + "'. Your ability to rez objects has been disabled.");
                return false;
            }

            if (attachment)
            {
                AddNewSceneObject(group, false);
                return true;
            }

            string reason = String.Empty;
            bool allowed = true;
            int landImpactNeeded = group.LandImpact;

            ILandObject parcel = LandChannel.GetLandObject(pos.X, pos.Y);
            if (parcel == null)
            {
                Vector3 newpos = ValidLocation(pos);
                if ((newpos.X != pos.X) || (newpos.Y != pos.Y))
                {
                    // Previous rez position was off-world, try again forced within.
                    parcel = LandChannel.GetLandObject(newpos.X, newpos.Y);
                    if (parcel != null)
                    {
                        string spos = Convert.ToInt16(pos.X).ToString() + "," + Convert.ToInt16(pos.Y).ToString() + "," + Convert.ToInt16(pos.Z).ToString();
                        if (remoteClient != null) remoteClient.SendAlertMessage("Attempt to rez object '" + group.Name + "' (" + landImpactNeeded.ToString() + " prims) at <" + spos + "> moved within region.");
//                        m_log.Warn("[SCENE]: Attempt to rez object '" + group.Name + "' (" + landImpactNeeded.ToString() + " prims) at <" + spos + "> moved within region.");
                        // success, found new valid position to rez - update it
                        pos = newpos;   
                        group.RootPart.SetGroupPositionDirect(pos);
                    }
                }
            }

            bool isTemp = (group.RootPart.GetEffectiveObjectFlags() & PrimFlags.TemporaryOnRez) != 0;
            if (parcel == null)
            {
                reason = ". Cannot determine land parcel.";
                allowed = false;
            }
            else
            {
                // Pass 0 for landImpact here so that it can be tested separately.
                if (!Permissions.CanRezObject(0, group.OwnerID, rezzedByObjectUUID.HasValue ? rezzedByObjectUUID.Value : UUID.Zero, pos, isTemp))
                {
                    if (RegionInfo.Product == ProductRulesUse.ScenicUse)
                        reason = ". Only the Estate Owner or partner can create objects on a scenic region.";
                    else
                        reason = " because the owner of this land parcel does not allow it.  Use About Land to see land ownership.";
                    allowed = false;
                }
                else
                if (!CheckLandImpact(parcel, landImpactNeeded, out reason))
                {
                    if (reason == "region")
                        reason = " because it would exceed the region prim limit.";
                    else
                        reason = " because it would exceed the prim limit.";
                    allowed = false;
                }
            }

            if (allowed) {
                AddNewSceneObject(group, !isTemp);
            } else {
                string spos = Convert.ToInt16(pos.X).ToString() + "," + Convert.ToInt16(pos.Y).ToString() + "," + Convert.ToInt16(pos.Z).ToString();
//                m_log.WarnFormat("[SCENE]: Attempt to rez object '" + group.Name + "' (" + landImpactNeeded.ToString() + " prims) at <" + spos + "> failed. {0}", reason);
                if (remoteClient != null) remoteClient.SendAlertMessage("Can't rez object '" + group.Name + "' (" + landImpactNeeded.ToString() + " prims) at <" + spos + "> " + reason);
            }
            return allowed;
        }

        private SceneObjectGroup RezSingleObjectToWorld(IClientAPI remoteClient, UUID itemID, 
            SceneObjectGroup group, Vector3 RayEnd, Vector3 RayStart,
            UUID RayTargetID, byte BypassRayCast, byte bRayEndIsIntersection,
            bool RezSelected, bool attachment, Vector3 pos, string name,
            string description, IInventoryItem item, ItemPermissionBlock itemPermissions,
            int? startParam, UUID? newAvatarGroupId, UUID? rezzedByObjectUUID)
        {
            bool ownerChanged = false;  // record this for the CHANGED_OWNER changed event

            if (IsBadUserLoad(group))
            {
                if (remoteClient != null)
                    remoteClient.SendAgentAlertMessage("You are currently not allowed to rez objects in this region.", false);
                return null;   // already reported above
            }
            if (IsBlacklistedLoad(group))
            {
                if (remoteClient != null)
                    remoteClient.SendAgentAlertMessage("Cannot rez blacklisted object '" + group.Name + "'.", false);
                return null;   // already reported above
            }

            //set the group here so that positioning in world will enable/disable the
            //script correctly based on the group the use is currently in

            // Initialize the server weight (LI)
            group.RecalcPrimWeights();

            group.RezzedFromFolderId = item.Folder;
            //group.FromAssetId = item.AssetID; //not needed yet

            if (newAvatarGroupId.HasValue)
            {
                //set the object's land group
                group.SetGroup(newAvatarGroupId.Value, null);
            }

            if (attachment)
            {
                group.SetFromItemID(itemID);
            }
            else
            {
                group.RootPart.SetGroupPositionDirect(pos);

                if (RezSelected)
                {
                    //also tell the client there is a new object being rezzed
                    foreach (SceneObjectPart part in group.GetParts())
                    {
                        part.AddFlag(PrimFlags.CreateSelected);
                    }
                }
            }

            SceneObjectPart rootPart = group.GetChildPart(group.UUID);
            if (rootPart == null) {
                string what = "object ";
                if (attachment)
                    what = " attachment ";
                m_log.Error("[AGENT INVENTORY]: Error rezzing ItemID: " + itemID + what + " root part not found.");
                return null;
            }

            // Since renaming the item in the inventory does not affect the name stored
            // in the serialization, transfer the correct name from the inventory to the
            // object itself before we rez.
            rootPart.Name = name;
            rootPart.Description = description;

            var partList = group.GetParts();

            foreach (SceneObjectPart part in partList)
            {
                /// This fixes inconsistencies between this part and the root part.
                /// In the past, there was a bug in Link operations that did not force 
                /// these permissions on child prims when linking.
                part.SyncChildPermsWithRoot();
            }

            if (rootPart.OwnerID != item.Owner)
            {
                if (Permissions.PropagatePermissions())
                {
                    if ((itemPermissions.CurrentPermissions & ScenePermBits.SLAM) != 0)
                    {    // enforce slam bit, apply item perms to the group parts
                        foreach (SceneObjectPart part in partList)
                        {
                            part.EveryoneMask = item.EveryonePermissions;
                            part.NextOwnerMask = item.NextPermissions;
                            part.GroupMask = 0; // DO NOT propagate here
                        }
                    }
                    group.ApplyNextOwnerPermissions();
                }
            }

            ownerChanged |= group.Rationalize(item.Owner, false);

            foreach (SceneObjectPart part in partList)
            {
                if (part.OwnerID != item.Owner)
                {
                    part.LastOwnerID = part.OwnerID;
                    part.OwnerID = item.Owner;
                    part.Inventory.ChangeInventoryOwner(item.Owner);
                    ownerChanged = true;
                }
                else if (((itemPermissions.CurrentPermissions & ScenePermBits.SLAM) != 0) && (!attachment)) // Slam!
                {
                    part.EveryoneMask = itemPermissions.EveryonePermissions;
                    part.NextOwnerMask = itemPermissions.NextPermissions;

                    part.GroupMask = 0; // DO NOT propagate here
                }
            }

            rootPart.TrimPermissions();

            if (!attachment)
            {
                if (group.RootPart.IsPrim)
                {
                    group.ClearPartAttachmentData();
                }
            }

            if (this.AddObjectToSceneIfPermitted(group, remoteClient, pos, attachment, rezzedByObjectUUID))
            {
                if (ownerChanged)
                {
                    foreach (SceneObjectPart part in partList)
                        part.TriggerScriptChangedEvent(Changed.OWNER);
                }

                if (!attachment)
                {
                    // Fire on_rez
                    group.CreateScriptInstances(startParam, ScriptStartFlags.PostOnRez, DefaultScriptEngine, (int)ScriptStateSource.PrimData, null);

                    rootPart.ScheduleFullUpdate(PrimUpdateFlags.ForcedFullUpdate);
                }

            } 
            else 
            {
                // The viewer automatically removes no-copy items from inventory on a rez attempt.
                // Since this one did not rez, it's still in inventory so let's "put it back".
                if (remoteClient != null)
                {
                    InventoryItemBase ib = item as InventoryItemBase;

                    if (item != null)
                    {
                        //this is a folder item, not a task item. update the user
                        remoteClient.SendInventoryItemCreateUpdate(ib, 0);
                    }
                }
                return null;
            }

            return rootPart.ParentGroup;
        }

        /// <summary>
        /// Rez an object into the scene from the user's inventory
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="itemID"></param>
        /// <param name="RayEnd"></param>
        /// <param name="RayStart"></param>
        /// <param name="RayTargetID"></param>
        /// <param name="BypassRayCast"></param>
        /// <param name="RayEndIsIntersection"></param>
        /// <param name="RezSelected"></param>
        /// <param name="RemoveItem"></param>
        /// <param name="fromTaskID"></param>
        /// <param name="attachment"></param>
        /// <returns>The SceneObjectGroup rezzed or null if rez was unsuccessful.</returns>
        public virtual SceneObjectGroup RezObject(IClientAPI remoteClient, UUID groupID, UUID itemID, Vector3 RayEnd, Vector3 RayStart,
                                    UUID RayTargetID, byte BypassRayCast, bool RayEndIsIntersection,
                                    bool RezSelected, bool RemoveItem, UUID fromTaskID, bool attachment, uint attachmentPt,
                                    int startParam)
        {
            // Rez object
            CachedUserInfo userInfo = CommsManager.UserService.GetUserDetails(remoteClient.AgentId);
            if (userInfo != null)
            {
                InventoryItemBase item = userInfo.FindItem(itemID);

                if (item != null)
                {
                    return RezObject(remoteClient, groupID, itemID, RayEnd, RayStart, RayTargetID, BypassRayCast, RayEndIsIntersection, RezSelected, RemoveItem,
                        fromTaskID, attachment, attachmentPt, startParam, userInfo, item, true);
                }
                
            }
            else
                m_log.WarnFormat("[AGENT INVENTORY]: User profile not found in {0}", RegionInfo.RegionName);

            return null;
        }

        public virtual void RestoreObject(IClientAPI remoteClient, UUID groupID, UUID itemID)
        {
            // Rez object
            CachedUserInfo userInfo = CommsManager.UserService.GetUserDetails(remoteClient.AgentId);
            if (userInfo != null)
            {
                InventoryItemBase item = userInfo.FindItem(itemID);

                if (item != null)
                {
                    RestoreObject(remoteClient, userInfo, itemID, item, null, groupID);
                }
            }
            else
                m_log.WarnFormat("[AGENT INVENTORY]: User profile not found during restore object: {0}", RegionInfo.RegionName);
        }

        public virtual SceneObjectGroup GetObjectFromItem(InventoryItemBase item)
        {
            AssetBase rezAsset = CommsManager.AssetCache.GetAsset(item.AssetID, AssetRequestInfo.InternalRequest());

            if (rezAsset != null)
            {
                UUID itemId = UUID.Zero;

                // If we have permission to copy then link the rezzed object back to the user inventory
                // item that it came from.  This allows us to enable 'save object to inventory'
                if (!Permissions.BypassPermissions())
                {
                    if ((item.CurrentPermissions & (uint)PermissionMask.Copy) == (uint)PermissionMask.Copy)
                    {
                        itemId = item.ID;
                    }
                }
                else
                {
                    // Brave new fullperm world
                    //
                    itemId = item.ID;
                }
                return this.DoDeserializeGroup(item.ID, rezAsset.Data);
            }

            // else asset could not be loaded
            return null;
        }

        public virtual SceneObjectGroup RezObject(IClientAPI remoteClient, UUID groupID, UUID itemID, Vector3 RayEnd, Vector3 RayStart,
                                    UUID RayTargetID, byte BypassRayCast, bool RayEndIsIntersection,
                                    bool RezSelected, bool RemoveItem, UUID fromTaskID, bool attachment, uint attachPoint,
                                    int? startParam, CachedUserInfo userInfo, InventoryItemBase item, bool allowUpdates)
        {
            // Work out position details
            byte bRayEndIsIntersection = (byte)0;

            if (RayEndIsIntersection)
            {
                bRayEndIsIntersection = (byte)1;
            }
            else
            {
                bRayEndIsIntersection = (byte)0;
            }

            Vector3 scale = new Vector3(0.5f, 0.5f, 0.5f);


            Vector3 pos = GetNewRezLocation(
                      RayStart, RayEnd, RayTargetID, Quaternion.Identity,
                      BypassRayCast, bRayEndIsIntersection, true, scale, false, remoteClient.AgentId);

            AssetBase rezAsset = CommsManager.AssetCache.GetAsset(item.AssetID, AssetRequestInfo.InternalRequest());

            if (rezAsset != null)
            {
                UUID itemId = UUID.Zero;

                // If we have permission to copy then link the rezzed object back to the user inventory
                // item that it came from.  This allows us to enable 'save object to inventory'
                if (!Permissions.BypassPermissions())
                {
                    if ((item.CurrentPermissions & (uint)PermissionMask.Copy) == (uint)PermissionMask.Copy)
                    {
                        itemId = item.ID;
                    }
                }
                else
                {
                    // Brave new fullperm world
                    //
                    itemId = item.ID;
                }

                string xmlData = Utils.BytesToString(rezAsset.Data);
                bool success = false;
                SceneObjectGroup retGroup = null;

                if (item.ContainsMultipleItems)
                {
                    if (!attachment)
                    {
                        CoalescedObject obj = this.DoDeserializeCoalesced(itemId, rezAsset.Data);

                        //rez coalesced object
                        success = this.RezCoalescedObject(remoteClient, itemID, obj, RayEnd,
                                    RayStart, RayTargetID, BypassRayCast, bRayEndIsIntersection,
                                    RezSelected, false, pos, item, startParam, groupID,
                                    null);
                    }
                    else
                    {
                        return null; //rezzing a coalesced as an attachment doesnt make sense
                    }
                }
                else
                {
                    //rez single group
                    SceneObjectGroup group = this.DoDeserializeGroup(itemId, rezAsset.Data);

                    group.DisableUpdates = !allowUpdates;

                    bool shouldTaint = false;

                    // if attachment we set it's asset id so object updates can reflect that
                    // if not, we set it's position in world.
                    if (!attachment)
                    {
                        // Save the previous attachment params if they've never been saved or default.
                        if (group.RootPart.SavedAttachmentPos == Vector3.Zero)
                            group.RootPart.SavedAttachmentPos = group.RootPart.RawGroupPosition;
                        if (group.RootPart.SavedAttachmentRot == Quaternion.Identity)
                            group.RootPart.SavedAttachmentRot = group.RootPart.RotationOffset;

                        //after this, the group is no longer an attachment as it is being rezzed as a normal group.
                        //this prevents an issue where the BB calculation tries to get the worldpos for the
                        //attachment that is not yet attached and creates a null reference exception
                        //when trying to access the scene to look up the wearer
                        group.SetAttachmentPoint(0);

                        Box bbox = group.BoundingBox();
                        scale = bbox.Size; ; // update the 0.5 cube with the actual size
                        pos = GetNewRezLocation(
                            RayStart, RayEnd, RayTargetID, Quaternion.Identity,
                            BypassRayCast, bRayEndIsIntersection, true, scale, false, remoteClient.AgentId);
                        float zCorrection = group.RootPart.GroupPosition.Z - bbox.Center.Z;
                        pos.Z += zCorrection;
                    }
                    else
                    {
                        //this is an attachment, prep the object for rez. particularly important so that it is not 
                        //first rezzed in world with physics in tact
                        if (!group.PrepareForRezAsAttachment(attachPoint, out shouldTaint, false))
                        {
                            return null;
                        }

                        //extract the saved position for sending to the rez method
                        pos = group.RawGroupPosition;
                    }

                    retGroup =
                        this.RezSingleObjectToWorld(remoteClient, itemID,
                                group, RayEnd, RayStart,
                                RayTargetID, BypassRayCast, bRayEndIsIntersection,
                                RezSelected, attachment, pos, item.Name,
                                item.Description, item, ItemPermissionBlock.FromOther(item),
                                startParam, groupID, null);

                    if (retGroup != null)
                    {
                        success = true;

                        if (shouldTaint)
                        {
                            retGroup.HasGroupChanged = true;
                            retGroup.TaintedAttachment = true;
                        }
                    }
                }

                if (success && !Permissions.BypassPermissions())
                {
                    //we check the inventory item permissions here instead of the prim permissions
                    //if the group or item is no copy, it should be removed
                    if ((item.CurrentPermissions & (uint)PermissionMask.Copy) == 0)
                    {
                        // If this is done on attachments, no
                        // copy ones will be lost, so avoid it
                        //
                        if (!attachment)
                        {
                            if (userInfo != null)
                                userInfo.DeleteItem(item);
                        }
                    }
                }

                return retGroup;
            }
            return null;
        }

        // unpackedGroup is null for an actual user Inventory item. non-null for group from a coalesced inventory item.
        public virtual bool RestoreObject(IClientAPI remoteClient, CachedUserInfo userInfo, UUID itemID, InventoryItemBase item, SceneObjectGroup unpackedGroup, UUID groupID)
        {
            Vector3 scale = new Vector3(0.5f, 0.5f, 0.5f);

            AssetBase rezAsset = CommsManager.AssetCache.GetAsset(item.AssetID, AssetRequestInfo.InternalRequest());
            if (rezAsset == null)
                return false;

            UUID itemId = UUID.Zero;
            bool success = false;

            // If we have permission to copy then link the rezzed object back to the user inventory
            // item that it came from.  This allows us to enable 'save object to inventory'
            if (!Permissions.BypassPermissions())
            {
                if ((item.CurrentPermissions & (uint)PermissionMask.Copy) == (uint)PermissionMask.Copy)
                {
                    itemId = item.ID;
                }
            }
            else
            {
                // Brave new fullperm world
                //
                itemId = item.ID;
            }

            if ((unpackedGroup==null) && item.ContainsMultipleItems)
            {
                CoalescedObject obj = this.DoDeserializeCoalesced(itemId, rezAsset.Data);
                //restore (rez) coalesced object
                success = this.RestoreCoalescedObject(remoteClient, userInfo, obj, itemID, item, groupID);
            }
            else
            {
                //rez single group
                SceneObjectGroup group = (unpackedGroup == null) ? this.DoDeserializeGroup(itemId, rezAsset.Data) : unpackedGroup;
                Vector3 pos = group.RootPart.GroupPositionNoUpdate;
                bool attachment = group.IsAttachment;
                uint attachPoint = group.AttachmentPoint;
                group.DisableUpdates = false;

                bool shouldTaint = false;

                if (attachment)
                {
                    remoteClient.SendAlertMessage("Inventory item is an attachment, use Wear or Add instead.");
                    return false;
                }

                // Save the previous attachment params if they've never been saved or default.
                if (group.RootPart.SavedAttachmentPos == Vector3.Zero)
                    group.RootPart.SavedAttachmentPos = group.RootPart.RawGroupPosition;
                if (group.RootPart.SavedAttachmentRot == Quaternion.Identity)
                    group.RootPart.SavedAttachmentRot = group.RootPart.RotationOffset;

                //after this, the group is no longer an attachment as it is being rezzed as a normal group.
                //this prevents an issue where the BB calculation tries to get the worldpos for the
                //attachment that is not yet attached and creates a null reference exception
                //when trying to access the scene to look up the wearer
                group.SetAttachmentPoint(0);

                Box bbox = group.BoundingBox();
                scale = bbox.Size; ; // update the 0.5 cube with the actual size
                pos = group.AbsolutePosition;
                float zCorrection = group.RootPart.GroupPosition.Z - bbox.Center.Z;
                pos.Z += zCorrection;

                SceneObjectGroup rezGroup =
                    this.RezSingleObjectToWorld(remoteClient, itemID,
                            group, pos, Vector3.Zero, UUID.Zero, 1, 1, false,
                            attachment, pos, group.Name, group.RootPart.Description,
                            item, ItemPermissionBlock.FromOther(item), 0, groupID, null);

                if (rezGroup != null)
                {
                    success = true;

                    if (shouldTaint)
                    {
                        rezGroup.HasGroupChanged = true;
                        rezGroup.TaintedAttachment = true;
                    }
                }
            }

            if (success && !Permissions.BypassPermissions())
            {
                //we check the inventory item permissions here instead of the prim permissions
                //if the group or item is no copy, it should be removed
                if ((item.CurrentPermissions & (uint)PermissionMask.Copy) == 0)
                {
                    // Not an attachment, so remove inventory copy, if no-copy.
                    if (userInfo != null)
                        userInfo.DeleteItem(item);
                }
            }

            return success;
        }

        private bool RestoreCoalescedObject(IClientAPI remoteClient, CachedUserInfo userInfo, CoalescedObject obj, UUID itemID, InventoryItemBase item, UUID groupId)
        {
            //determine the bounding box of the entire set
            Box coBoundingBox = obj.GetBoundingBox();
            Vector3 rezAtRootOffset = Vector3.Zero;

            Vector3 newPos = coBoundingBox.Center;

            //rez each group
            bool success = true;
            foreach (SceneObjectGroup group in obj.Groups)
            {
                group.ResetInstance(true, false, UUID.Zero);
                success &= RestoreObject(remoteClient, userInfo, itemID, item, group, groupId);
            }

            return success;
        }

        private SceneObjectGroup DoDeserializeGroup(UUID itemId, byte[] bytes)
        {
            SceneObjectGroup grp;
            ISerializationEngine engine;

            if (ProviderRegistry.Instance.TryGet<ISerializationEngine>(out engine) && engine.InventoryObjectSerializer.CanDeserialize(bytes))
            {
                grp = engine.InventoryObjectSerializer.DeserializeGroupFromInventoryBytes(bytes);
            }
            else
            {
                string xmlData = Utils.BytesToString(bytes);
                grp = SceneObjectSerializer.FromOriginalXmlFormat(itemId, xmlData);
            }

            ResetGroupAfterDeserialization(itemId, grp);

            return grp;
        }

        private CoalescedObject DoDeserializeCoalesced(UUID itemId, byte[] bytes)
        {
            CoalescedObject cobj;
            ISerializationEngine engine;

            if (ProviderRegistry.Instance.TryGet<ISerializationEngine>(out engine) && engine.InventoryObjectSerializer.CanDeserialize(bytes))
            {
                cobj = engine.InventoryObjectSerializer.DeserializeCoalescedObjFromInventoryBytes(bytes);
            }
            else
            {
                string xmlData = Utils.BytesToString(bytes);
                cobj = CoalescedSceneObjectSerializer.FromXmlFormat(itemId, xmlData);
            }

            foreach (var grp in cobj.Groups)
            {
                ResetGroupAfterDeserialization(itemId, grp);
            }

            return cobj;
        }

        private static void ResetGroupAfterDeserialization(UUID itemId, SceneObjectGroup grp)
        {
            grp.ResetInstance(true, false, UUID.Zero);
            foreach (var part in grp.GetParts())
            {
                part.DoPostDeserializationCleanups(itemId);
                part.TrimPermissions();
            }
        }

        private bool RezCoalescedObject(IClientAPI remoteClient, UUID itemID, 
            CoalescedObject obj, Vector3 RayEnd, Vector3 RayStart, UUID RayTargetID, 
            byte BypassRayCast, byte bRayEndIsIntersection, 
            bool RezSelected, bool rezAtRoot, Vector3 pos, 
            IInventoryItem item, int? startParam, UUID? newAvatarGroupId,
            UUID? rezzedByObjectUUID)
        {
            //determine the bounding box of the entire set
            Box coBoundingBox = obj.GetBoundingBox();
            Vector3 rezAtRootOffset = Vector3.Zero;

            pos = GetNewRezLocation(
                  RayStart, RayEnd, RayTargetID, Quaternion.Identity,
                  BypassRayCast, bRayEndIsIntersection, true, coBoundingBox.Size, false, 
                  remoteClient != null ? remoteClient.AgentId : UUID.Zero);

            Vector3 newPos = coBoundingBox.Center;
            if (rezAtRoot)
            {
                // Use the root prim position of the last object (matches SL).
                SceneObjectGroup lastGroup = null;
                foreach (SceneObjectGroup group in obj.Groups)
                    lastGroup = group;

                if (lastGroup != null)
                    rezAtRootOffset = (coBoundingBox.Center - lastGroup.RootPart.AbsolutePosition);
            }

            //reset the positions of all parts to be an offset from 0
            SceneObjectGroup.TranslateToNewCenterPosition(coBoundingBox.Center, pos, obj.Groups);
            
            //rez each group
            bool success = true;
            foreach (SceneObjectGroup group in obj.Groups)
            {
                group.ResetInstance(true, false, UUID.Zero);
                success = success &&
                (this.RezSingleObjectToWorld(remoteClient, itemID, group, RayEnd, RayStart, RayTargetID,
                    BypassRayCast, bRayEndIsIntersection, RezSelected, false, group.AbsolutePosition+rezAtRootOffset,
                    group.RootPart.Name, group.RootPart.Description, item, obj.FindPermissions(group.UUID),
                    startParam, newAvatarGroupId, rezzedByObjectUUID)) != null;
            }

            return success;
        }

        private bool CheckGreyGoo(SceneObjectPart sourcePart, SceneObjectGroup newGroup)
        {
            // Allow cooldown if the object isn't in a scenic region, or isn't a temp object, or is owned by the EO
            bool isTemp = (sourcePart.GetEffectiveObjectFlags() & PrimFlags.TemporaryOnRez) != 0;
            bool allowCooldown = true;
            if (isTemp)                                                                 // if temp-rez object
                if (RegionInfo.AllowRezzers == ProductRulesWho.OnlyEO)                  // if scenic region
                    if (!IsEstateOwner(sourcePart.OwnerID))                             // if guest user (not EO)
                        if (!(RegionInfo.AllowPartnerRez && IsEstateOwnerPartner(sourcePart.OwnerID))) // if guest user (not EO partner)
                            allowCooldown = false;                                      // hard-limit generations

            // First adjust the current generation based on possible cooldown rules.
            if (allowCooldown)
            {
                uint secsSinceRezzed = (uint)Util.UnixTimeSinceEpoch() - sourcePart.TimeStamp;
                uint secsSinceLastRez = (uint)Util.UnixTimeSinceEpoch() - sourcePart.TimeStampLastRez;
                if ((secsSinceRezzed > SceneObjectPart.GENERATION_COOLDOWN) || (sourcePart.TimeStampLastRez != 0))
                {
                    uint gooPeriod = 0; // seconds since recent significant event

                    // Check if this isn't the first time this thing has rezzed an object
                    if (sourcePart.TimeStampLastRez != 0)
                        gooPeriod = secsSinceLastRez;
                    else
                        if (secsSinceRezzed > SceneObjectPart.GENERATION_COOLDOWN)
                            gooPeriod = secsSinceRezzed;

                    if (gooPeriod > SceneObjectPart.GENERATION_COOLDOWN)
                    {
                        int cooldownFactor = (int)(gooPeriod / SceneObjectPart.GENERATION_COOLDOWN);
                        if (sourcePart.Generation < cooldownFactor)
                            sourcePart.ParentGroup.SetGeneration(0);
                        else
                            sourcePart.ParentGroup.SetGeneration(sourcePart.Generation - cooldownFactor);
                    }
                }
            }

            // Now regardless of cooldown, this part is rezzing something so apply the normal generation
            newGroup.RootPart.Generation = sourcePart.Generation + 1;
            return (newGroup.RootPart.Generation < SceneObjectPart.MAX_GENERATION);
        }

        /// <summary>
        /// Rez an object into the scene from a prim's inventory.
        /// </summary>
        /// <param name="sourcePart"></param>
        /// <param name="item"></param>
        /// <param name="pos"></param>
        /// <param name="rot"></param>
        /// <param name="vel"></param>
        /// <param name="param"></param>
        /// <returns>The SceneObjectGroup rezzed or null if rez was unsuccessful</returns>
        /// 
        public virtual IEnumerable<SceneObjectGroup> RezObject(
            SceneObjectPart sourcePart, TaskInventoryItem item, bool rezAtRoot,
            Vector3 pos, Quaternion rot, Vector3 vel, int? param, out string reason)
        {
            // Rez object
            if (item != null)
            {
                UUID ownerID = item.OwnerID;

                AssetBase rezAsset = CommsManager.AssetCache.GetAsset(item.AssetID, AssetRequestInfo.InternalRequest());

                if (rezAsset != null)
                {
                    CoalescedObject cobj = null;
                    SceneObjectGroup grp = null;
                    List<SceneObjectGroup> allGroups = new List<SceneObjectGroup>();

                    int totalLandImpact = 0;
                    bool isTemp = true;

                    if (item.ContainsMultipleItems)
                    {
                        cobj = this.DoDeserializeCoalesced(item.ItemID, rezAsset.Data);
                        foreach (var subGrp in cobj.Groups)
                        {
                            totalLandImpact += subGrp.LandImpact;
                            //for a coalesced, we set istemp to false if any parts are not temp
                            if ((subGrp.RootPart.GetEffectiveObjectFlags() & PrimFlags.TemporaryOnRez) == 0)
                            {
                                isTemp = false;
                            }

                            allGroups.Add(subGrp);
                        }
                    }
                    else
                    {
                        grp = this.DoDeserializeGroup(item.ItemID, rezAsset.Data);
                        totalLandImpact = grp.LandImpact;
                        isTemp = (grp.RootPart.GetEffectiveObjectFlags() & PrimFlags.TemporaryOnRez) != 0;
                        allGroups.Add(grp);
                    }

                    ILandObject parcel = LandChannel.GetLandObject(pos.X, pos.Y);
                    if (parcel == null)
                    {
                        reason = "land";
                        return null;
                    }

                    // Pass 0 for landImpact here so that it can be tested separately.
                    if (!Permissions.CanRezObject(0, ownerID, sourcePart.UUID, pos, isTemp))
                    {
                        reason = "permission";
                        return null;
                    }

                    if (!CheckLandImpact(parcel, totalLandImpact, out reason))
                    {
                        return null;
                    }

                    foreach (var subGrp in allGroups)
                    {
                        // Check for grey goo fence
                        if (!CheckGreyGoo(sourcePart, subGrp))
                        {
                            reason = "fence";
                            return null;
                        }

                        Vector3 actualPos = pos;
                        if (!rezAtRoot)
                            actualPos += (subGrp.RootPart.AbsolutePosition - subGrp.BoundingBox().Center);

                        // update the last rez time and the new group fields
                        sourcePart.StampLastRez();
                        subGrp.CurrentParcel = parcel;  // initialize _currentParcel (and auto-return)
                        subGrp.SetGeneration(subGrp.RootPart.Generation); // now update the rest of the parts

                        //change the velocity for rez
                        subGrp.RootPart.ReplaceSerializedVelocity(vel);
                    }

                    bool rezSucceeded;
                    if (!item.ContainsMultipleItems)
                    {
                        // Since renaming the item in the inventory does not affect the name stored
                        // in the serialization, transfer the correct name from the inventory to the
                        // object itself before we rez.
                        grp.Name = item.Name;
                        grp.RootPart.Description = item.Description;

                        //reset the rotation
                        grp.RootPart.RotationOffset = rot;

                        //rez the single group
                        Vector3 actualPos = pos;
                        if (!rezAtRoot)
                            actualPos += (grp.RootPart.AbsolutePosition - grp.BoundingBox().Center);
                        rezSucceeded = this.RezSingleObjectToWorld(null, item.ItemID, grp, actualPos, actualPos, UUID.Zero, (byte)1, (byte)1, false, false, actualPos,
                            grp.Name, grp.RootPart.Description, item, ItemPermissionBlock.FromOther(item), param, 
                            sourcePart.ParentGroup.RootPart.GroupID, sourcePart.UUID) != null;
                    }
                    else
                    {
                        //rez the coalesced
                        rezSucceeded = this.RezCoalescedObject(null, item.ItemID, cobj, pos, pos, UUID.Zero, (byte)1, (byte)1, 
                            false, rezAtRoot, pos, item, param, sourcePart.ParentGroup.RootPart.GroupID, sourcePart.UUID);
                    }

                    if (!rezSucceeded)
                    {
                        reason = "rez";
                        return null;
                    }
                    else
                    {
                        if (!Permissions.BypassPermissions())
                        {
                            //if the item is no copy, it should be removed
                            if ((item.CurrentPermissions & (uint)PermissionMask.Copy) != (uint)PermissionMask.Copy)
                            {
                                sourcePart.Inventory.RemoveInventoryItem(item.ItemID);
                            }
                        }

                        reason = "success";
                        return allGroups;
                    }
                } else
                {
                    m_log.ErrorFormat("[SCENE]: RezObject could not fetch asset {0} for '{1}' in '{2}' owned by {3}", item.AssetID, item.Name, sourcePart.ParentGroup.Name, sourcePart.OwnerID);
                }

            } else
            {
                m_log.ErrorFormat("[SCENE]: Missing item for '{0}' owned by {1}", sourcePart.ParentGroup.Name, sourcePart.OwnerID);
            }

            reason = "item";
            return null;
        }

        public virtual SceneObjectGroup RezPrim(SceneObjectPart sourcePart, SceneObjectPart newPart, int param, out string reason)
        {
            // Rez object
            Vector3 pos = newPart.AbsolutePosition;
            SceneObjectGroup group = new SceneObjectGroup(newPart);
            bool isTemp = (group.RootPart.GetEffectiveObjectFlags() & PrimFlags.TemporaryOnRez) != 0;

            ILandObject parcel = LandChannel.GetLandObject(pos.X, pos.Y);
            if (parcel == null)
            {
                reason = "land";
                return null;
            }

            // Pass 0 for landImpact here so that it can be tested separately.
            if (!Permissions.CanRezObject(0, newPart.OwnerID, sourcePart.UUID, pos, isTemp))
            {
                reason = "permission";
                return null;
            }

            if (!CheckLandImpact(parcel, group.LandImpact, out reason))
            {
                return null;
            }

            // Check for grey goo fence
            if (!CheckGreyGoo(sourcePart, group))
            {
                reason = "fence";
                return null;
            }

            // Allowing the rez... update the last rez time and the new group fields
            sourcePart.StampLastRez();
            group.CurrentParcel = parcel;   // initialize _currentParcel (and auto-return)
            group.SetGeneration(group.RootPart.Generation); // now update the rest of the parts
            group.ResetIDs();

            //set the group's group before setting the object's position.
            //this will make sure that the group id is correct during the script
            //engine's group check
            group.SetGroup(sourcePart.ParentGroup.RootPart.GroupID, null);

            AddNewSceneObject(group, !isTemp);

            SceneObjectPart rootPart = group.GetChildPart(group.UUID);

            rootPart.TrimPermissions();

            if (group.RootPart.IsPrim)
            {
                group.ClearPartAttachmentData();
            }

            group.CreateScriptInstances(param, ScriptStartFlags.PostOnRez, DefaultScriptEngine, (int)ScriptStateSource.PrimData, null);
            rootPart.ScheduleFullUpdate(PrimUpdateFlags.ForcedFullUpdate);

            reason = "success";
            return rootPart.ParentGroup;
        }

        public virtual int returnObjects(SceneObjectGroup[] returnobjects)
        {
            return returnObjects(returnobjects, "parcel owner return");
        }

        public int returnObjects(SceneObjectGroup[] returnobjects, string reason)
        {
            List<uint> groupIds = new List<uint>();
            foreach (SceneObjectGroup grp in returnobjects)
            {
                UUID NotifyID = (grp.OwnerID == grp.GroupID) ? grp.RootPart.LastOwnerID : grp.OwnerID;
                AddReturn(NotifyID, grp.Name, grp.AbsolutePosition, reason);
                groupIds.Add(grp.RootPart.LocalId);
            }

            return DeRezObjects(null, groupIds, UUID.Zero, DeRezAction.Return, UUID.Zero);
        }

        public void SetScriptRunning(IClientAPI controllingClient, UUID objectID, UUID itemID, bool running)
        {
            SceneObjectPart part = GetSceneObjectPart(objectID);
            if (part == null)
                return;

            if (running && !Permissions.CanStartScript(part, itemID, controllingClient.AgentId))
            {
                return;
            }

            if (!running && !Permissions.CanStopScript(part, itemID, controllingClient.AgentId))
            {
                return;
            }

            if (running)
                EventManager.TriggerStartScript(part.LocalId, itemID);
            else
                EventManager.TriggerStopScript(part.LocalId, itemID);
        }

        /// <summary>
        /// Rezzes a single attachment and returns UUID.Zero. Can not be called if you need the ID
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="itemID"></param>
        /// <param name="AttachmentPt"></param>
        /// <param name="append"></param>
        /// <returns></returns>
        public UUID RezSingleAttachment(IClientAPI client, UUID itemID, uint AttachmentPt, bool append)
        {
            ScenePresence presence;
            if (!TryGetAvatar(client.AgentId, out presence))
                return UUID.Zero;

            client.RunAttachmentOperation(() =>
                {
                    SceneObjectGroup sog = RezSingleAttachmentSync(client, itemID, AttachmentPt, append);
                    if (sog != null)
                    {
                        IAvatarFactory ava = RequestModuleInterface<IAvatarFactory>();
                        if (ava != null)
                            ava.UpdateDatabase(client.AgentId, presence.Appearance, null, null);
                    }

                });

            return UUID.Zero;
        }

        /// <summary>
        /// Rezzes the given attachment in the current thread. This should only be called
        /// from the avatar's attachment management thread
        /// </summary>
        /// <param name="client"></param>
        /// <param name="itemID"></param>
        /// <param name="AttachmentPt"></param>
        /// <param name="appendMode">Are we adding/appending or wear/replacing it?</param>
        /// <returns>The SceneObjectGroup rezzed</returns>
        public SceneObjectGroup RezSingleAttachmentSync(IClientAPI client, UUID itemID, uint AttachmentPt, bool appendMode)
        {
            ScenePresence presence;
            if (!TryGetAvatar(client.AgentId, out presence))
                return null;

            SceneObjectGroup attachment = null;

            // Are we already attached?
            if (presence.Appearance.GetAttachmentForItem(itemID) == null)
            {
                attachment = m_sceneGraph.RezSingleAttachment(client, itemID, AttachmentPt, appendMode);

                if (attachment == null)
                {
                    DetachSingleAttachmentToInvSync(itemID, client);
                }
                else
                {
                    AttachmentPt = attachment.RootPart.AttachmentPoint;
                    UUID assetId = attachment.UUID;
                    presence.Appearance.SetAttachment((int)AttachmentPt, appendMode, itemID, assetId);
                }
            }

            return attachment;
        }

        private void ClearAllAttachments(ScenePresence presence)
        {
            presence.Appearance.ClearAttachments();
        }

        public void RezMultipleAttachments(IClientAPI remoteClient, RezMultipleAttachmentsFromInvPacket.HeaderDataBlock header,
                                       RezMultipleAttachmentsFromInvPacket.ObjectDataBlock[] objects)
        {
            remoteClient.RunAttachmentOperation(() =>
            {
                ScenePresence presence;
                if (!TryGetAvatar(remoteClient.AgentId, out presence))
                    return;

                if (header.FirstDetachAll)
                {
                    foreach (UUID itemId in presence.CollectAttachmentItemIds())
                        this.DetachSingleAttachmentToInvSync(itemId, remoteClient);

                    this.ClearAllAttachments(presence);
                }

                bool updated = false;

                foreach (RezMultipleAttachmentsFromInvPacket.ObjectDataBlock obj in objects)
                {
                    bool append = (((uint)obj.AttachmentPt & 0x80) != 0);         // Are we appending?
                    uint attachPoint = ((uint)obj.AttachmentPt & 0x7f);           // And strip the append bit

                    // Are we already attached?
                    if (presence.Appearance.GetAttachmentForItem(obj.ItemID) == null)
                    {
                        SceneObjectGroup sog = RezSingleAttachmentSync(remoteClient, obj.ItemID, attachPoint, append);
                        if (sog != null)
                            updated = true;
                    }
                }

                if (updated)
                {
                    IAvatarFactory ava = RequestModuleInterface<IAvatarFactory>();
                    if (ava != null)
                        ava.UpdateDatabase(remoteClient.AgentId, presence.Appearance, null, null);
                }
            });
        }

        public void AttachObject(IClientAPI controllingClient, uint localID, uint attachPoint, bool append, bool silent,
            AttachFlags flags)
        {
            controllingClient.RunAttachmentOperation(() =>
                {
                    m_sceneGraph.AttachObject(controllingClient, localID, attachPoint, append, silent, flags);
                });
        }

        public void DetachSingleAttachmentToGround(UUID itemID, IClientAPI remoteClient)
        {
            remoteClient.RunAttachmentOperation(() =>
                {
                    SceneObjectPart part = GetSceneObjectPart(itemID);
                    if (part == null || part.ParentGroup == null)
                        return;

                    UUID inventoryID = part.ParentGroup.GetFromItemID();

                    ScenePresence presence;
                    if (TryGetAvatar(remoteClient.AgentId, out presence))
                    {
                        int landImpact = part.ParentGroup.LandImpact;
                        bool isTemp = (part.GetEffectiveObjectFlags() & PrimFlags.TemporaryOnRez) != 0;
                        if (!Permissions.CanRezObject(landImpact, remoteClient.AgentId, UUID.Zero, presence.AbsolutePosition, isTemp))
                            return;

                        this.EventManager.TriggerOnDetachObject(part.ParentGroup.LocalId);
                        presence.Appearance.DetachAttachment(itemID);
                        IAvatarFactory ava = RequestModuleInterface<IAvatarFactory>();
                        if (ava != null)
                        {
                            ava.UpdateDatabase(remoteClient.AgentId, presence.Appearance, null, null);
                        }
                        part.ParentGroup.DetachToGround();
                        CachedUserInfo userInfo =
                            CommsManager.UserService.GetUserDetails(remoteClient.AgentId);
                        if (userInfo != null)
                        {
                            userInfo.CheckedDeleteItem(remoteClient.AgentId, inventoryID);
                            remoteClient.SendRemoveInventoryItem(inventoryID);
                        }
                    }
                });
        }

        public void DetachSingleAttachmentToInv(UUID itemID, IClientAPI remoteClient)
        {
            remoteClient.RunAttachmentOperation(() =>
            {
                ScenePresence sp = this.GetScenePresence(remoteClient.AgentId);
                if (sp == null)
                    return;
                SceneObjectGroup group =  sp.GetAttachmentByItemID(itemID);
                if (group == null)
                    return;
                if (group.OwnerID != remoteClient.AgentId) 
                    return;

                this.EventManager.TriggerOnDetachObject(group.LocalId);
                DetachSingleAttachmentToInvSync(itemID, remoteClient);
            });
        }

        /// <summary>
        /// Detaches the given item running in the current thread (which must be the client attachment thread)
        /// </summary>
        /// <param name="itemID"></param>
        /// <param name="remoteClient"></param>
        /// <returns></returns>
        private void DetachSingleAttachmentToInvSync(UUID itemID, IClientAPI remoteClient)
        {
            UUID groupId = UUID.Zero;
            ScenePresence presence;
            bool isBot = false;

            if (TryGetAvatar(remoteClient.AgentId, out presence))
            {
                isBot = presence.IsBot;
                if (isBot)
                    itemID ^= presence.UUID;    // use original itemID from owner's inventory
                groupId = presence.Appearance.DetachAttachment(itemID);
                IAvatarFactory ava = RequestModuleInterface<IAvatarFactory>();
                if (ava != null)
                {
                    if (!isBot)
                        ava.UpdateDatabase(remoteClient.AgentId, presence.Appearance, null, null);
                }
            }

            if (isBot)
                m_sceneGraph.DetachSingleBotAttachment(itemID, groupId, remoteClient);
            else
                m_sceneGraph.DetachSingleAttachmentToInv(itemID, groupId, remoteClient);
        }

        public void GetScriptRunning(IClientAPI controllingClient, UUID objectID, UUID itemID)
        {
            EventManager.TriggerGetScriptRunning(controllingClient, objectID, itemID);
        }

        void ObjectOwner(IClientAPI remoteClient, UUID ownerID, UUID groupID, List<uint> localIDs)
        {
            if (!Permissions.IsGod(remoteClient.AgentId))
            {
                if (ownerID != UUID.Zero)
                    return;
                
                if (!Permissions.CanDeedObject(remoteClient.AgentId, groupID))
                    return;
            }

            List<SceneObjectGroup> groups = new List<SceneObjectGroup>();

            foreach (uint localID in localIDs)
            {
                SceneObjectPart part = GetSceneObjectPart(localID);
                if (!groups.Contains(part.ParentGroup))
                    groups.Add(part.ParentGroup);
            }

            foreach (SceneObjectGroup sog in groups)
            {
                if (ownerID != UUID.Zero)
                {
                    sog.SetOwnerId(ownerID);
                    sog.SetGroup(groupID, remoteClient);

                    foreach (SceneObjectPart child in sog.GetParts())
                        child.Inventory.ChangeInventoryOwner(ownerID);
                }
                else
                {   
                    // This is the deed-to-group case.
                    if (!Permissions.CanEditObject(sog.UUID, remoteClient.AgentId, (uint)PermissionMask.Transfer))
                        continue;

                    if (sog.GroupID != groupID)
                        continue;   // Must deed to the *current* object's group.

                    foreach (SceneObjectPart child in sog.GetParts())
                    {
                        child.LastOwnerID = child.OwnerID;
                        child.Inventory.ChangeInventoryOwner(groupID);
                    }

                    sog.SetOwnerId(groupID);
                    sog.ApplyNextOwnerPermissions();
                }

                InspectForAutoReturn(sog);
            }

            foreach (uint localID in localIDs)
            {
                SceneObjectPart part = GetSceneObjectPart(localID);
                part.GetProperties(remoteClient);
            }
        }
    }
}
