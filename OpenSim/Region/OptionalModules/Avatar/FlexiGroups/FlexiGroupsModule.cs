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
 * 
 * 2009.9.12 - Heavily modified version of XmlRpcGroups by D. Daeschler for inworldz 
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Timers;

using log4net;
using Nini.Config;

using OpenMetaverse;
using OpenMetaverse.StructuredData;

using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Framework.Communications.Cache;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.CoreModules.Capabilities;

using DirFindFlags = OpenMetaverse.DirectoryManager.DirFindFlags;
using System.Text.RegularExpressions;


namespace OpenSim.Region.OptionalModules.Avatar.FlexiGroups
{
    public class FlexiGroupsModule : ISharedRegionModule, IGroupsModule
    {
        /// <summary>
        /// ; To use this module, you must specify the following in your Halcyon.ini
        /// [GROUPS]
        /// Enabled = true
        /// Module  = XmlRpcGroups
        /// XmlRpcServiceURL = http://osflotsam.org/xmlrpc.php
        /// XmlRpcMessagingEnabled = true
        /// XmlRpcNoticesEnabled = true
        /// XmlRpcDebugEnabled = true
        /// XmlRpcServiceReadKey = 1234
        /// XmlRpcServiceWriteKey = 1234
        /// 
        /// ; Disables HTTP Keep-Alive for Groups Module HTTP Requests, work around for
        /// ; a problem discovered on some Windows based region servers.  Only disable
        /// ; if you see a large number (dozens) of the following Exceptions:
        /// ; System.Net.WebException: The request was aborted: The request was canceled.
        ///
        /// XmlRpcDisableKeepAlive = false
        /// </summary>

        private const int ATTACH_NAME_OFFSET = 52;
        private const byte OFFLINE_SIGN = 0xFF;

        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private List<Scene> m_sceneList = new List<Scene>();

        private IMessageTransferModule m_msgTransferModule = null;
        private IMuteListModule m_muteListModule = null;

        private IGroupDataProvider m_groupData = null;

        // Configuration settings
        //private const string m_defaultXmlRpcServiceURL = "http://osflotsam.org/xmlrpc.php";
        private bool m_groupsEnabled = false;
        private bool m_groupNoticesEnabled = true;
        private bool m_debugEnabled = true;

        private Dictionary<Guid, UUID> GroupAttachmentCache = new Dictionary<Guid, UUID>();

        #region IRegionModuleBase Members

        public void Initialize(IConfigSource config)
        {
            IConfig groupsConfig = config.Configs["Groups"];

            if (groupsConfig == null)
            {
                // Do not run this module by default.
                return;
            }
            else
            {
                m_groupsEnabled = groupsConfig.GetBoolean("Enabled", false);
                if (!m_groupsEnabled)
                {
                    return;
                }

                if (groupsConfig.GetString("Module", "Default") != "FlexiGroups")
                {
                    m_groupsEnabled = false;

                    return;
                }

                m_log.Info("[GROUPS]: Initializing FlexiGroups");

                m_groupData = ProviderFactory.GetProviderFromConfigName(m_log, groupsConfig, groupsConfig.GetString("Provider"));


                m_groupNoticesEnabled   = groupsConfig.GetBoolean("XmlRpcNoticesEnabled", true);
                m_debugEnabled          = groupsConfig.GetBoolean("XmlRpcDebugEnabled", true);
            }
        }

        public void AddRegion(Scene scene)
        {
            if (m_groupsEnabled)
                scene.RegisterModuleInterface<IGroupsModule>(this);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_groupsEnabled)
                return;

            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            if (m_msgTransferModule == null)
            {
                m_msgTransferModule = scene.RequestModuleInterface<IMessageTransferModule>();

                // No message transfer module, no notices, group invites, rejects, ejects, etc
                if (m_msgTransferModule == null)
                {
                    m_groupsEnabled = false;
                    m_log.Error("[GROUPS]: Could not get MessageTransferModule");
                    Close();
                    return;
                }
            }

            lock (m_sceneList)
            {
                m_sceneList.Add(scene);
            }

            scene.EventManager.OnNewClient += OnNewClient;
            scene.EventManager.OnIncomingInstantMessage += OnGridInstantMessage;
            scene.EventManager.OnCompleteMovementToRegion += OnCompleteMovementToRegion;

            // The InstantMessageModule itself doesn't do this, 
            // so lets see if things explode if we don't do it
            // scene.EventManager.OnClientClosed += OnClientClosed;

        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_groupsEnabled)
                return;

            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            lock (m_sceneList)
            {
                m_sceneList.Remove(scene);
            }
        }

        public void Close()
        {
            if (!m_groupsEnabled)
                return;

            if (m_debugEnabled) m_log.Debug("[GROUPS]: Shutting down XmlRpcGroups module.");
        }

        public string Name
        {
            get { return "XmlRpcGroupsModule"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        #endregion

        #region ISharedRegionModule Members

        public void PostInitialize()
        {
            // NoOp
        }

        #endregion

        #region EventHandlers
        private void OnNewClient(IClientAPI client)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            client.OnUUIDGroupNameRequest += HandleUUIDGroupNameRequest;
            client.OnAgentDataUpdateRequest += OnAgentDataUpdateRequest;
            client.OnDirFindQuery += OnDirFindQuery;
            client.OnRequestAvatarProperties += OnRequestAvatarProperties;

            // Used for Notices and Group Invites/Accept/Reject
            client.OnInstantMessage += OnInstantMessage;

            //client.OnRegionHandShakeReply += OnRegionHandShakeReply;
//            SendAgentGroupDataUpdate(client, client.AgentId); // should NOT be called for child agents, so let logins handle this via IGroupsModule interface
        }

        /*private void OnRegionHandShakeReply(IClientAPI client)
        {
            SendAgentGroupDataUpdate(client, client.AgentId);
        }*/

        private void OnRequestAvatarProperties(IClientAPI remoteClient, UUID avatarID)
        {
//            m_log.WarnFormat("[GROUPS]: RequestAvatarProperties for [{0}] by user {1}...", avatarID, remoteClient.AgentId);
            GroupMembershipData[] avatarGroups = m_groupData.GetAgentGroupMemberships(GetClientGroupRequestID(remoteClient), avatarID).ToArray();
            remoteClient.SendAvatarGroupsReply(avatarID, avatarGroups);
        }

        /*
         * This becomes very problematic in a shared module.  In a shared module you may have more then one
         * reference to IClientAPI's, one for 0 or 1 root connections, and 0 or more child connections.
         * The OnClientClosed event does not provide anything to indicate which one of those should be closed
         * nor does it provide what scene it was from so that the specific reference can be looked up.
         * The InstantMessageModule.cs does not currently worry about unregistering the handles, 
         * and it should be an issue, since it's the client that references us not the other way around
         * , so as long as we don't keep a reference to the client laying around, the client can still be GC'ed
        private void OnClientClosed(UUID AgentId)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            lock (m_ActiveClients)
            {
                if (m_ActiveClients.ContainsKey(AgentId))
                {
                    IClientAPI client = m_ActiveClients[AgentId];
                    client.OnUUIDGroupNameRequest -= HandleUUIDGroupNameRequest;
                    client.OnAgentDataUpdateRequest -= OnAgentDataUpdateRequest;
                    client.OnDirFindQuery -= OnDirFindQuery;
                    client.OnInstantMessage -= OnInstantMessage;

                    m_ActiveClients.Remove(AgentId);
                }
                else
                {
                    if (m_debugEnabled) m_log.WarnFormat("[GROUPS]: Client closed that wasn't registered here.");
                }

                
            }
        }
        */


        void OnDirFindQuery(IClientAPI remoteClient, UUID queryID, string queryText, uint queryFlags, int queryStart)
        {
            if (((DirFindFlags)queryFlags & DirFindFlags.Groups) == DirFindFlags.Groups)
            {
                if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called with queryText({1}) queryFlags({2}) queryStart({3})", System.Reflection.MethodBase.GetCurrentMethod().Name, queryText, (DirFindFlags)queryFlags, queryStart);
                queryText = queryText.Trim();   // newer viewers sometimes append a space

                // TODO: This currently ignores pretty much all the query flags including Mature and sort order
                remoteClient.SendDirGroupsReply(queryID, m_groupData.FindGroups(GetClientGroupRequestID(remoteClient), queryText).ToArray());
            }
            
        }

        private void OnAgentDataUpdateRequest(IClientAPI remoteClient, UUID dataForAgentID, UUID sessionID)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            UUID activeGroupID = UUID.Zero;
            string activeGroupTitle = String.Empty;
            string activeGroupName = String.Empty;
            ulong activeGroupPowers  = (ulong)0;

            GroupMembershipData membership = m_groupData.GetAgentActiveMembership(GetClientGroupRequestID(remoteClient), dataForAgentID);
            if (membership != null)
            {
                activeGroupID = membership.GroupID;
                activeGroupTitle = membership.GroupTitle;
                activeGroupPowers = membership.GroupPowers;
            }

            SendAgentDataUpdate(remoteClient, dataForAgentID, activeGroupID, activeGroupName, activeGroupPowers, activeGroupTitle);

            SendScenePresenceUpdate(dataForAgentID, activeGroupTitle);
        }

        private void HandleUUIDGroupNameRequest(UUID GroupID,IClientAPI remoteClient)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            string GroupName;
            
            GroupRecord group = m_groupData.GetGroupRecord(GetClientGroupRequestID(remoteClient), GroupID, null);
            if (group != null)
            {
                GroupName = group.GroupName;
            }
            else
            {
                GroupName = "Unknown";
            }

            remoteClient.SendGroupNameReply(GroupID, GroupName);
        }

        private void SendGroupNoticeIM(UUID NoticeID, UUID AgentID, OpenMetaverse.InstantMessageDialog dialog, bool checkMuted)
        {
            // Build notice IIM
            GridInstantMessage msg = CreateGroupNoticeIM(UUID.Zero, NoticeID, (byte)dialog);
            if (msg == null)
                return;     // old bad one stored from offlines?
            UUID sender = new UUID(msg.fromAgentID);

            if (checkMuted)
            {
                m_muteListModule = m_sceneList[0].RequestModuleInterface<IMuteListModule>();
                if (m_muteListModule != null)
                    if (m_muteListModule.IsMuted(sender, AgentID))
                        return;
            }

            bool HasAttachment = (msg.binaryBucket[0] != 0);
            if (HasAttachment)  // save the notice with the session ID
            {
                lock (GroupAttachmentCache) {
                    GroupAttachmentCache[msg.imSessionID] = NoticeID;
                }
            }

            msg.toAgentID = AgentID.Guid;

            IClientAPI localClient = GetActiveClient(AgentID);
            if (localClient != null)
            {
                if (m_debugEnabled) m_log.InfoFormat("[GROUPS]: Recipient ({0}) is local, delivering group notice directly", localClient.Name);
                localClient.SendInstantMessage(msg);
            }
            else
            {
                // send for offline storage
                if (HasAttachment)  // clean up the cache item added above
                {
                    lock (GroupAttachmentCache) {
                        GroupAttachmentCache.Remove(msg.imSessionID);
                    }
                }
                // need to reformat this with db storage bucket, not the viewer IM bucket
                // format for database storage
                int bucketLen = msg.binaryBucket.Length;
                byte[] OfflineBucket = new byte[bucketLen + 4 + 16];
                OfflineBucket[0] = 0xFF; // sign, would be 00 or 01 here in a normal viewer IM bucket
                OfflineBucket[1] = 0x00; OfflineBucket[2] = 0x00; OfflineBucket[3] = 0x00; // Spare bytes
                NoticeID.ToBytes(OfflineBucket, 4);    // 16-byte UUID
                msg.binaryBucket.CopyTo(OfflineBucket, 20);
                msg.binaryBucket = OfflineBucket;
                if (m_debugEnabled) m_log.InfoFormat("[GROUPS]: Recipient ({0}) is not local, delivering group notice via TransferModule", msg.toAgentID);
                m_msgTransferModule.SendInstantMessage(msg, delegate(bool success) { if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: Message Sent: {0}", success ? "Succeeded" : "Failed"); });
            }
        }

        private InventoryFolderBase FindRootFolder(ICheckedInventoryStorage storage, UUID groupId)
        {
            try
            {
                return storage.FindFolderForType(groupId, (AssetType)FolderType.Root);
            }
            catch (InventoryStorageException)
            {
            }
            return null;
        }

        private InventoryFolderBase FindCreateGroupStorage(ICheckedInventoryStorage storage, UUID groupId)
        {
            InventoryFolderBase rootFolder = null;
            try
            {
                rootFolder = FindRootFolder(storage, groupId);
                if (rootFolder == null)
                {
                    rootFolder = new InventoryFolderBase();
                    rootFolder.Level = InventoryFolderBase.FolderLevel.Root;
                    rootFolder.Type = (short)FolderType.Root;
                    rootFolder.Owner = groupId;
                    rootFolder.ID = UUID.Random();
                    storage.CreateFolder(groupId, rootFolder);
                }
                return rootFolder;
            }
            catch (InventoryStorageException e)
            {
                m_log.ErrorFormat("[INVENTORY] Exception finding or creating inventory storage for group {0}: {1}\n{2}", groupId.ToString(), e.Message, e.StackTrace);
            }
            return null;
        }

        private  void StoreGroupItem(UUID groupId, InventoryItemBase item)
        {
            try
            {
                ICheckedInventoryStorage storage = ProviderRegistry.Instance.Get<IInventoryProviderSelector>().GetGroupsProvider();
                InventoryFolderBase rootFolder = FindCreateGroupStorage(storage, groupId);
                if (rootFolder != null)
                {
                    // Tell storage to store it in the folder we found above.
                    item.Folder = rootFolder.ID;

                    // In case it's useful for reporting etc, store the original owner.
                    // Since there's no LastOwner field here, store it in the unused group ID field.
                    item.GroupID = item.Owner;
                    item.Owner = groupId;   // required by the Cassandra group storage
                    storage.CreateItem(groupId, item);
                }
                return;
            }
            catch (InventoryStorageException e)
            {
                m_log.ErrorFormat("[INVENTORY] Exception adding new group inventory item for group {0}: {1}\n{2}", groupId.ToString(), e.Message, e.StackTrace);
            }
        }

        private InventoryItemBase FetchGroupItem(UUID groupId, UUID itemId)
        {
            InventoryItemBase item;
            try
            {
                ICheckedInventoryStorage storage = ProviderRegistry.Instance.Get<IInventoryProviderSelector>().GetGroupsProvider();
                item = storage.GetItem(groupId, itemId, UUID.Zero);
                // owner field is the group, let's restore the owner ID we stored in the group field
                item.Owner = item.GroupID;
                item.GroupID = UUID.Zero;
            }
            catch (InventoryStorageException)
            {
//                m_log.ErrorFormat("[INVENTORY] Exception fetching inventory item {0} for group {1}: {2}\n{3}", itemId.ToString(), groupId.ToString(), e.Message, e.StackTrace);
                item = null;
            }
            return item;
        }

        private InventoryItemBase AddToGroupInventory(IClientAPI remoteClient, GroupRecord group, InventoryItemBase item)
        {
            Scene scene = (Scene)remoteClient.Scene;
            InventoryItemBase itemCopy = scene.InventoryItemForGroup(group.GroupID, remoteClient.AgentId, item.ID);
            if (itemCopy != null)
                StoreGroupItem(group.GroupID, itemCopy);

            return itemCopy;
        }

        private InventoryItemBase DeliverGroupInventory(IClientAPI remoteClient, UUID groupId, InventoryItemBase item)
        {
            Scene scene = (Scene)remoteClient.Scene;
            // When delivering from a group notice, senderID is the group ID and senderUserInfo is null. Item is delivered to user.
            return scene.CheckDeliverGroupItem(item, remoteClient.AgentId, groupId, null);
        }

        private InventoryItemBase FindInventoryItem(UUID ownerId, UUID itemId)
        {
            try
            {
                ICheckedInventoryStorage provider = ProviderRegistry.Instance.Get<IInventoryProviderSelector>().GetGroupsProvider();
                return provider.GetItem(ownerId, itemId, UUID.Zero);
            }
            catch (InventoryStorageException)
            {
                // don't report "not found" errors here.
            }

            return null;
        }

        private void OnInstantMessage(IClientAPI remoteClient, GridInstantMessage im)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            // Group invitations
            if ((im.dialog == (byte)InstantMessageDialog.GroupInvitationAccept) || (im.dialog == (byte)InstantMessageDialog.GroupInvitationDecline))
            {
                UUID inviteID = new UUID(im.imSessionID);
                GroupInviteInfo inviteInfo = m_groupData.GetAgentToGroupInvite(GetClientGroupRequestID(remoteClient), inviteID);

                if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: Invite is for Agent {0} to Group {1}.", inviteInfo.AgentID, inviteInfo.GroupID);

                UUID fromAgentID = new UUID(im.fromAgentID);
                if ((inviteInfo != null) && (fromAgentID == inviteInfo.AgentID))
                {
                    // Accept
                    if (im.dialog == (byte)InstantMessageDialog.GroupInvitationAccept)
                    {
                        if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: Received an accept invite notice.");

                        // and the sessionid is the role
                        m_groupData.AddAgentToGroup(GetClientGroupRequestID(remoteClient), inviteInfo.AgentID, inviteInfo.GroupID, inviteInfo.RoleID, false);

                        GridInstantMessage msg = new GridInstantMessage();
                        msg.imSessionID = UUID.Zero.Guid;
                        msg.fromAgentID = UUID.Zero.Guid;
                        msg.toAgentID = inviteInfo.AgentID.Guid;
                        msg.timestamp = (uint)Util.UnixTimeSinceEpoch();
                        msg.fromAgentName = "Groups";
                        msg.message = string.Format("You have been added to the group.");
                        msg.dialog = (byte)OpenMetaverse.InstantMessageDialog.MessageBox;
                        msg.fromGroup = false;
                        msg.offline = (byte)0;  // don't bother storing this one to fetch on login if user offline
                        msg.ParentEstateID = 0;
                        msg.Position = Vector3.Zero;
                        msg.RegionID = UUID.Zero.Guid;
                        msg.binaryBucket = new byte[0];

                        OutgoingInstantMessage(msg, inviteInfo.AgentID);

                        UpdateAllClientsWithGroupInfo(inviteInfo.AgentID);

                        // TODO: If the inviter is still online, they need an agent dataupdate 
                        // and maybe group membership updates for the invitee

                        m_groupData.RemoveAgentToGroupInvite(GetClientGroupRequestID(remoteClient), inviteID);
                    }

                    // Reject
                    if (im.dialog == (byte)InstantMessageDialog.GroupInvitationDecline)
                    {
                        if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: Received a reject invite notice.");
                        m_groupData.RemoveAgentToGroupInvite(GetClientGroupRequestID(remoteClient), inviteID);
                    }
                }
            }

            // Group notices
            if ((im.dialog == (byte)InstantMessageDialog.GroupNotice))
            {
                if (!m_groupNoticesEnabled)
                {
                    return;
                }
                if (im.offline != 0)
                {
                    // Delivery of stored (offline) IMs is handled by the caller
                    return;
                }

                UUID GroupID = new UUID(im.toAgentID);
                GroupRecord group = m_groupData.GetGroupRecord(GetClientGroupRequestID(remoteClient), GroupID, null);
                if (group == null)
                {
                    m_log.ErrorFormat("[GROUPS]: Failed to find notice group {0}", GroupID);
                    return;
                }

                UUID NoticeID = UUID.Random();
                string Subject = im.message.Substring(0, im.message.IndexOf('|'));
                string Message = im.message.Substring(Subject.Length + 1);

                byte[] bucket;

                if ((im.binaryBucket.Length == 1) && (im.binaryBucket[0] == 0))
                {
                    // Sending a notice without an attachment
                    bucket = new byte[19];
                    bucket[0] = 0; // HasAttachment boolean
                    bucket[1] = 0; // attachment type
                    GroupID.ToBytes(bucket, 2);
                    bucket[18] = 0; // attachment name
                }
                else
                {
                    // Sending a notice with an attachment
                    string binBucket = OpenMetaverse.Utils.BytesToString(im.binaryBucket);
                    binBucket = binBucket.Remove(0, 14).Trim();
                    OSDMap binBucketOSD = (OSDMap)OSDParser.DeserializeLLSDXml(binBucket);
                    UUID itemId = binBucketOSD["item_id"].AsUUID();
                    UUID ownerID = binBucketOSD["owner_id"].AsUUID();
                    if (ownerID != remoteClient.AgentId)
                    {
                        m_log.ErrorFormat("[GROUPS]: Notice attachment in group {0} not owned {1} by the user sending it {2}", GroupID.ToString(), ownerID.ToString(), remoteClient.AgentId);
                        return;
                    }

                    CachedUserInfo ownerUserInfo = m_sceneList[0].CommsManager.UserService.GetUserDetails(ownerID);
                    if (ownerUserInfo == null)
                    {
                        m_log.ErrorFormat("[GROUPS]: Failed to find notice sender {0} for item {1}", ownerID, itemId);
                        return;
                    }

                    InventoryItemBase item = ownerUserInfo.FindItem(itemId);
                    if (item == null)
                    {
                        m_log.ErrorFormat("[GROUPS]: Item {0} not found for notice sender {1}", itemId, ownerID);
                        return;
                    }

                    if (!m_sceneList[0].Permissions.BypassPermissions())
                    {
                        if (((item.CurrentPermissions & (uint)PermissionMask.Transfer) == 0) ||
                            ((item.CurrentPermissions & (uint)PermissionMask.Copy) == 0))
                        {
                            remoteClient.SendAgentAlertMessage("Notice attachments must be copyable and transferable.", false);
                            return;
                        }
                    }

                    // Clone the item for storage as a group-owned item in the db that is NOT in any user's inventory.
                    InventoryItemBase groupItem = AddToGroupInventory(remoteClient, group, item);

                    // format for database storage
                    bucket = FormatBucketForStorage(true, (byte)groupItem.AssetType, GroupID, groupItem.ID, NoticeID, item.Name);
                }
                if (m_groupData.AddGroupNotice(GetClientGroupRequestID(remoteClient), GroupID, NoticeID, im.fromAgentName, Subject, Message, bucket))
                {
                    // Once added to the DB above with owner ID, replace that with the GroupID that the viewers need
                    GroupID.ToBytes(bucket, 2);         // 16-byte UUID
                    if (OnNewGroupNotice != null)
                    {
                        OnNewGroupNotice(GroupID, NoticeID);
                    }

                    // Get the list of users who have this sender muted.
                    List<UUID> muters;
                    m_muteListModule = m_sceneList[0].RequestModuleInterface<IMuteListModule>();
                    if (m_muteListModule != null)
                        muters = m_muteListModule.GetInverseMuteList(remoteClient.AgentId);
                    else
                        muters = new List<UUID>();
                    // Send notice out to everyone that wants notices
                    foreach (GroupMembersData member in m_groupData.GetGroupMembers(GetClientGroupRequestID(remoteClient), GroupID, false))
                    {
                        if (member.AcceptNotices && !muters.Contains(member.AgentID))
                        {
                            SendGroupNoticeIM(NoticeID, member.AgentID, OpenMetaverse.InstantMessageDialog.GroupNotice, false);
                        }
                    }
                }
            }
            if (im.dialog == (byte)InstantMessageDialog.GroupNoticeInventoryDeclined)
            {
                if (m_debugEnabled) m_log.InfoFormat("[GROUPS]: Declined - removing session {0}", im.imSessionID);
                lock (GroupAttachmentCache) {
                    GroupAttachmentCache.Remove(im.imSessionID);
                }
            }
            if (im.dialog == (byte)InstantMessageDialog.GroupNoticeInventoryAccepted)
            {
                UUID NoticeID = UUID.Zero;
                lock (GroupAttachmentCache) {
                    if (GroupAttachmentCache.ContainsKey(im.imSessionID))
                    {
                        // Retrieve the notice ID and remove it from the cache.
                        NoticeID = GroupAttachmentCache[im.imSessionID];
                        GroupAttachmentCache.Remove(im.imSessionID);
                    }
                }
                if (NoticeID == UUID.Zero)
                {
                    m_log.ErrorFormat("[GROUPS]: Accepted attachment for session {0} - NOT FOUND", im.imSessionID);
                    remoteClient.SendAgentAlertMessage("Attachment not saved - specified group notice not found.", false);
                    return;
                }

                if (m_debugEnabled) m_log.InfoFormat("[GROUPS]: Accepted attachment for session {0} notice {1}", im.imSessionID, NoticeID);

                GroupNoticeInfo notice = m_groupData.GetGroupNotice(GetClientGroupRequestID(remoteClient), NoticeID);
                if (notice == null)
                {
                    remoteClient.SendAgentAlertMessage("Could not find the required group notice.", false);
                    return;
                }
                InitializeNoticeFromBucket(notice);

                UUID groupId = notice.GroupID;
                UUID itemId = notice.noticeData.ItemID;

                // we need a userInfo structure to get the sessionID to use in case the inventory service needs a secure service connection
                CachedUserInfo userInfo = m_sceneList[0].CommsManager.UserService.GetUserDetails(remoteClient.AgentId);
                if (userInfo == null)
                {
                    m_log.ErrorFormat("[GROUPS]: Failed to find notice recipient {0} for item {1}", remoteClient.AgentId, itemId);
                    remoteClient.SendAgentAlertMessage("Attachment not saved - user profile not found.", false);
                    return;
                }
                
                InventoryItemBase groupItem = FetchGroupItem(groupId, itemId);
                if (groupItem == null)  // For now support fallback to the legacy inventory system.
                    groupItem = FindInventoryItem(groupId, itemId);

                if (groupItem != null) {
                    InventoryItemBase deliveredItem = DeliverGroupInventory(remoteClient, groupId, groupItem);
                    if (deliveredItem != null)
                    {
                        remoteClient.SendInventoryItemCreateUpdate(deliveredItem, 0);
                        remoteClient.SendAgentAlertMessage("Group notice attachment '"+groupItem.Name+"' saved to inventory.", false);
                    }
                    else
                    {
                        remoteClient.SendAgentAlertMessage("Attachment not saved - delivery failed.", false);
                        m_log.ErrorFormat("[GROUPS]: Failed to deliver notice attachment {0} for {1}", itemId, remoteClient.AgentId);
                    }
                }
                else
                {
                    remoteClient.SendAgentAlertMessage("Attachment not saved - item not found.", false);
                    m_log.ErrorFormat("[GROUPS]: Missing notice attachment {0} for {1}", itemId, remoteClient.AgentId);
                }
                if (m_debugEnabled) m_log.InfoFormat("[GROUPS]: Accepted and completed for session {0}", im.imSessionID);
            }
            
            // Interop, received special 210 code for ejecting a group member
            // this only works within the comms servers domain, and won't work hypergrid
            // TODO:FIXME: Use a presense server of some kind to find out where the 
            // client actually is, and try contacting that region directly to notify them,
            // or provide the notification via xmlrpc update queue
            if ((im.dialog == 210))
            {
                // This is sent from the region that the ejectee was ejected from
                // if it's being delivered here, then the ejectee is here
                // so we need to send local updates to the agent.

                UUID ejecteeID = new UUID(im.toAgentID);

                im.dialog = (byte)InstantMessageDialog.MessageFromAgent;
                OutgoingInstantMessage(im, ejecteeID);

                IClientAPI ejectee = GetActiveClient(ejecteeID);
                if (ejectee != null)
                {
                    UUID groupID = new UUID(im.fromAgentID);
                    ejectee.SendAgentDropGroup(groupID);
                }
            }
        }

        private const int OFFLINE_BUCKET_BYTES = (4+16);
        private void OnGridInstantMessage(GridInstantMessage msg)
        {
            if (m_debugEnabled) m_log.InfoFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            // Trigger the above event handler
            OnInstantMessage(null, msg);

            // If a message from a group arrives here, it may need to be forwarded to a local client
            if (msg.fromGroup == true)
            {
                switch (msg.dialog)
                {
                    case (byte)InstantMessageDialog.GroupInvitation:
                    case (byte)InstantMessageDialog.GroupNotice:
                        UUID toAgentID = new UUID(msg.toAgentID);
                        IClientAPI localClient = GetActiveClient(toAgentID);
                        if (localClient != null)
                        {
                            if (msg.dialog == (byte)InstantMessageDialog.GroupInvitation)
                            {
                                localClient.SendInstantMessage(msg);    // send it directly
                            }
                            if (msg.dialog == (byte)InstantMessageDialog.GroupNotice)
                            {
                                // offline format has 0xFF000000 and noticeID before the viewer IM bucket
                                if ((msg.binaryBucket.Length < OFFLINE_BUCKET_BYTES) || (msg.binaryBucket[0] != OFFLINE_SIGN))
                                    localClient.SendInstantMessage(msg);    // send it directly
                                else
                                {   // convert from offline bucket to viewer IM bucket
                                    // skip FF signature byte and 3 spare bytes
                                    UUID NoticeID = new UUID(msg.binaryBucket, 4);  // 16-byte UUID
                                    int bucketLen = msg.binaryBucket.Length - OFFLINE_BUCKET_BYTES;
                                    byte[] bucket = new byte[bucketLen];
                                    Array.Copy(msg.binaryBucket, OFFLINE_BUCKET_BYTES, bucket, 0, bucketLen);
                                    msg.binaryBucket = bucket;
                                    SendGroupNoticeIM(NoticeID, localClient.AgentId, OpenMetaverse.InstantMessageDialog.GroupNotice, true);
                                }
                            }
                        }
                        break;
                }
            }
        }

        private void OnCompleteMovementToRegion(ScenePresence SP)
        {
            SendAgentGroupDataUpdate(SP.ControllingClient, SP.UUID);
        }

        #endregion

        #region IGroupsModule Members

        public event NewGroupNotice OnNewGroupNotice;

        public GroupRecord GetGroupRecord(UUID GroupID)
        {
            return m_groupData.GetGroupRecord(null, GroupID, null);
        }

        public void ActivateGroup(IClientAPI remoteClient, UUID groupID)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            if (groupID != UUID.Zero)
                if (!m_groupData.IsAgentInGroup(groupID, remoteClient.AgentId))
                    return;

            m_groupData.SetAgentActiveGroup(GetClientGroupRequestID(remoteClient), remoteClient.AgentId, groupID);

            // Changing active group changes title, active powers, all kinds of things
            // anyone who is in any region that can see this client, should probably be 
            // updated with new group info.  At a minimum, they should get ScenePresence
            // updated with new title.
            UpdateAllClientsWithGroupInfo(remoteClient.AgentId);
        }

        public bool IsAgentInGroup(UUID agentID, UUID groupID)
        {
            return m_groupData.IsAgentInGroup(groupID, agentID);
        }

        public bool IsAgentInGroup(IClientAPI remoteClient, UUID groupID)
        {
            if (remoteClient == null)
                return false;   // we don't know who to check

            // Use the known in-memory group membership data if available before going to db.
            return remoteClient.IsGroupMember(groupID);
        }

        /// <summary>
        /// Get the Role Titles for an Agent, for a specific group
        /// </summary>
        public List<GroupTitlesData> GroupTitlesRequest(IClientAPI remoteClient, UUID groupID)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            GroupRequestID grID = GetClientGroupRequestID(remoteClient);

            List<GroupRolesData> agentRoles = m_groupData.GetAgentGroupRoles(grID, remoteClient.AgentId, groupID);
            GroupMembershipData agentMembership = m_groupData.GetAgentGroupMembership(grID, remoteClient.AgentId, groupID);

            List<GroupTitlesData> titles = new List<GroupTitlesData>();
            foreach (GroupRolesData role in agentRoles)
            {
                GroupTitlesData title = new GroupTitlesData();
                title.Name = role.Name;
                if (agentMembership != null)
                {
                    title.Selected = agentMembership.ActiveRole == role.RoleID;
                }
                title.UUID = role.RoleID;

                titles.Add(title);
            }

            return titles;
        }

        // Internal system calls to this will pass null, null, UUID.Zero, groupID
        public List<GroupMembersData> GroupMembersRequest(IClientAPI remoteClient, Scene scene, UUID agentID, UUID groupID)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            GroupRequestID requestID = new GroupRequestID();

            bool ownersOnly = false; // assume it's a system request unless a specific client was passed in
            if (remoteClient != null)
            {
                requestID = GetClientGroupRequestID(remoteClient);
                if (scene == null)
                    scene = (Scene)remoteClient.Scene;
                agentID = remoteClient.AgentId;
            }
            if (scene != null)
                ownersOnly = !(scene.Permissions.IsGod(agentID) || IsAgentInGroup(agentID, groupID));

            List<GroupMembersData> data = m_groupData.GetGroupMembers(requestID, groupID, ownersOnly);
            if (m_debugEnabled)
            {
                foreach (GroupMembersData member in data)
                {
                    m_log.DebugFormat("[GROUPS]: {0} {1}", member.AgentID, member.Title);
                }
            }

            return data;
        }

        public List<GroupRolesData> GroupRoleDataRequest(IClientAPI remoteClient, UUID groupID)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            List<GroupRolesData> data = m_groupData.GetGroupRoles(GetClientGroupRequestID(remoteClient), groupID);

            if (m_debugEnabled)
            {
                foreach (GroupRolesData member in data)
                {
                    m_log.DebugFormat("[GROUPS]: {0} {1}", member.Title, member.Members);
                }
            }

            return data;

        }

        public List<GroupRoleMembersData> GroupRoleMembersRequest(IClientAPI remoteClient, UUID groupID)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            List<GroupRoleMembersData> data = m_groupData.GetGroupRoleMembers(GetClientGroupRequestID(remoteClient), groupID);

            if (m_debugEnabled)
            {
                foreach (GroupRoleMembersData member in data)
                {
                    m_log.DebugFormat("[GROUPS]: Av: {0}  Role: {1}", member.MemberID, member.RoleID);
                }
            }
            
            return data;


        }

        public GroupProfileData GroupProfileRequest(IClientAPI remoteClient, UUID groupID)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            GroupProfileData profile = new GroupProfileData();

            GroupRequestID grID = GetClientGroupRequestID(remoteClient);

            GroupRecord groupInfo = m_groupData.GetGroupRecord(GetClientGroupRequestID(remoteClient), groupID, null);
            if (groupInfo != null)
            {
                profile.AllowPublish = groupInfo.AllowPublish;
                profile.Charter = groupInfo.Charter;
                profile.FounderID = groupInfo.FounderID;
                profile.GroupID = groupID;
                profile.GroupMembershipCount = m_groupData.GetGroupMembers(grID, groupID, false).Count;
                profile.GroupRolesCount = m_groupData.GetGroupRoles(grID, groupID).Count;
                profile.InsigniaID = groupInfo.GroupPicture;
                profile.MaturePublish = groupInfo.MaturePublish;
                profile.MembershipFee = groupInfo.MembershipFee;
                profile.Money = 0; // TODO: Get this from the currency server?
                profile.Name = groupInfo.GroupName;
                profile.OpenEnrollment = groupInfo.OpenEnrollment;
                profile.OwnerRole = groupInfo.OwnerRoleID;
                profile.ShowInList = groupInfo.ShowInList;
            }

            GroupMembershipData memberInfo = m_groupData.GetAgentGroupMembership(grID, remoteClient.AgentId, groupID);
            if (memberInfo != null)
            {
                profile.MemberTitle = memberInfo.GroupTitle;
                profile.PowersMask = memberInfo.GroupPowers;
            }

            return profile;
        }

        public GroupMembershipData[] GetMembershipData(UUID agentID)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            return m_groupData.GetAgentGroupMemberships(null, agentID).ToArray();
        }

        public GroupMembershipData GetMembershipData(UUID groupID, UUID agentID)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            return m_groupData.GetAgentGroupMembership(null, agentID, groupID);
        }

        // Returns null on error, empty list if not in any groups.
        public List<UUID> GetAgentGroupList(UUID agentID)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            return m_groupData.GetAgentGroupList(null, agentID);
        }

        public void UpdateGroupInfo(IClientAPI remoteClient, UUID groupID, string charter, bool showInList, UUID insigniaID, int membershipFee, bool openEnrollment, bool allowPublish, bool maturePublish)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            // TODO: Security Check?

            m_groupData.UpdateGroup(GetClientGroupRequestID(remoteClient), groupID, charter, showInList, insigniaID, membershipFee, openEnrollment, allowPublish, maturePublish);
        }

        public void SetGroupAcceptNotices(IClientAPI remoteClient, UUID groupID, bool acceptNotices, bool listInProfile)
        {
            // TODO: Security Check?
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            m_groupData.SetAgentGroupInfo(GetClientGroupRequestID(remoteClient), remoteClient.AgentId, groupID, acceptNotices, listInProfile);
        }

        public UUID CreateGroup(IClientAPI remoteClient, string name, string charter, bool showInList, UUID insigniaID, int membershipFee, bool openEnrollment, bool allowPublish, bool maturePublish)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            GroupRequestID grID = GetClientGroupRequestID(remoteClient);

            if (m_groupData.GetGroupRecord(grID, UUID.Zero, name) != null)
            {
                remoteClient.SendCreateGroupReply(UUID.Zero, false, "A group with the same name already exists.");
                return UUID.Zero;
            }

            IScene scene = (remoteClient == null) ? m_sceneList[0] : remoteClient.Scene;
            IMoneyModule mm = scene.RequestModuleInterface<IMoneyModule>();
            if (mm == null)
            {
                remoteClient.SendAgentAlertMessage("Server configuration error - cannot create group without money module.", false);
                return UUID.Zero;
            }

            if (!mm.GroupCreationCovered(remoteClient.AgentId)) {
                remoteClient.SendAgentAlertMessage("Unable to create group. Insufficient funds.", false);
                return UUID.Zero;
            }

            UUID groupID = m_groupData.CreateGroup(grID, name, charter, showInList, insigniaID, membershipFee, openEnrollment, allowPublish, maturePublish, remoteClient.AgentId);
            if (groupID == UUID.Zero)
            {
                remoteClient.SendCreateGroupReply(groupID, true, "Server error attempting to create group (try again later).");
                return UUID.Zero;
            }

            mm.ApplyGroupCreationCharge(remoteClient.AgentId);
            remoteClient.SendCreateGroupReply(groupID, true, "Group created successfully.");

            // Update the founder with new group information.
            SendAgentGroupDataUpdate(remoteClient, remoteClient.AgentId);
            return groupID;
        }

        public GroupNoticeData[] GroupNoticesListRequest(IClientAPI remoteClient, UUID groupID)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            // ToDo: check if agent is a member of group and is allowed to see notices?

            return m_groupData.GetGroupNotices(GetClientGroupRequestID(remoteClient), groupID).ToArray();
        }

        /// <summary>
        /// Get the title of the agent's current role.
        /// </summary>
        public string GetGroupTitle(UUID avatarID)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            GroupMembershipData membership = m_groupData.GetAgentActiveMembership(null, avatarID);
            if (membership != null)
            {
                return membership.GroupTitle;
            } 
            return String.Empty;
        }

        /// <summary>
        /// Change the current Active Group Role for Agent
        /// </summary>
        public void GroupTitleUpdate(IClientAPI remoteClient, UUID groupID, UUID titleRoleID)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            m_groupData.SetAgentActiveGroupRole(GetClientGroupRequestID(remoteClient), remoteClient.AgentId, groupID, titleRoleID);

            // TODO: Not sure what all is needed here, but if the active group role change is for the group
            // the client currently has set active, then we need to do a scene presence update too
            // if (m_groupData.GetAgentActiveMembership(remoteClient.AgentId).GroupID == GroupID)
                
            UpdateAllClientsWithGroupInfo(remoteClient.AgentId);
        }


        public void GroupRoleUpdate(IClientAPI remoteClient, UUID groupID, UUID roleID, string name, string description, string title, ulong powers, byte updateType)
        {
            name = Regex.Replace(name, @"[\u000A]", String.Empty);
            description = Regex.Replace(description, @"[\u000A]", String.Empty);
            title = Regex.Replace(title, @"[\u000A]", String.Empty);

            name = Regex.Replace(name, @"[\u000B]", String.Empty);
            description = Regex.Replace(description, @"[\u000B]", String.Empty);
            title = Regex.Replace(title, @"[\u000B]", String.Empty);

            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            // TODO: Security Checks?

            GroupRequestID grID = GetClientGroupRequestID(remoteClient);

            switch ((OpenMetaverse.GroupRoleUpdate)updateType)
            {
                case OpenMetaverse.GroupRoleUpdate.Create:
                    m_groupData.AddGroupRole(grID, groupID, UUID.Random(), name, description, title, powers, false);
                    break;

                case OpenMetaverse.GroupRoleUpdate.Delete:
                    m_groupData.RemoveGroupRole(grID, groupID, roleID);
                    break;

                case OpenMetaverse.GroupRoleUpdate.UpdateAll:
                case OpenMetaverse.GroupRoleUpdate.UpdateData:
                case OpenMetaverse.GroupRoleUpdate.UpdatePowers:
                    m_groupData.UpdateGroupRole(grID, groupID, roleID, name, description, title, powers);
                    break;

                case OpenMetaverse.GroupRoleUpdate.NoUpdate:
                default:
                    // No Op
                    break;

            }

            // TODO: This update really should send out updates for everyone in the role that just got changed.
            SendAgentGroupDataUpdate(remoteClient, remoteClient.AgentId);
        }

        public void GroupRoleChanges(IClientAPI remoteClient, UUID groupID, UUID roleID, UUID memberID, uint changes)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);
            // Todo: Security check

            GroupRequestID grID = GetClientGroupRequestID(remoteClient);

            switch (changes)
            {
                case 0:
                    // Add
                    m_groupData.AddAgentToGroupRole(grID, grID.AgentID, memberID, groupID, roleID, false);

                    break;
                case 1:
                    // Remove
                    m_groupData.RemoveAgentFromGroupRole(grID, memberID, groupID, roleID);
                    
                    break;
                default:
                    m_log.ErrorFormat("[GROUPS]: {0} does not understand changes == {1}", System.Reflection.MethodBase.GetCurrentMethod().Name, changes);
                    break;
            }

            // TODO: This update really should send out updates for everyone in the role that just got changed.
            SendAgentGroupDataUpdate(remoteClient, remoteClient.AgentId);
        }

        public void GroupNoticeRequest(IClientAPI remoteClient, UUID groupNoticeID)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            GroupRequestID grID = GetClientGroupRequestID(remoteClient);

            GroupNoticeInfo notice = m_groupData.GetGroupNotice(grID, groupNoticeID);
            if (notice != null)
            {
                if (!IsAgentInGroup(remoteClient.AgentId, notice.GroupID))
                    return;

                InitializeNoticeFromBucket(notice);

                SendGroupNoticeIM(groupNoticeID, remoteClient.AgentId, OpenMetaverse.InstantMessageDialog.GroupNoticeRequested, true);
            }

        }

        // This bucket format is the database-stored format for binary notice data.
        private void InitializeNoticeFromBucket(GroupNoticeInfo notice)
        {
            int bucketLen = notice.BinaryBucket.Length;

            // Initialize the remaining notice fields from bucket data.
            if (bucketLen < ATTACH_NAME_OFFSET + 1)
            {
                // no attachment data
                notice.noticeData.HasAttachment = false;
                notice.noticeData.AssetType = 0;
                notice.noticeData.OwnerID = UUID.Zero;
                notice.noticeData.ItemID = UUID.Zero;
                notice.noticeData.Attachment = String.Empty;
            }
            else
            {   // we have enough attachment data
                notice.noticeData.AssetType = notice.BinaryBucket[1];
                notice.noticeData.OwnerID = new UUID(notice.BinaryBucket, 2);
                notice.noticeData.ItemID = new UUID(notice.BinaryBucket, 18);
                notice.noticeData.HasAttachment = true;
                notice.noticeData.Attachment = OpenMetaverse.Utils.BytesToString(notice.BinaryBucket, ATTACH_NAME_OFFSET, bucketLen - ATTACH_NAME_OFFSET);
            }
        }

        // This bucket format is the longer database-stored format for binary notice data.
        private byte[] FormatBucketForStorage(bool HasAttachment, byte AssetType, UUID GroupID, UUID ItemID, UUID NoticeID, string AttachmentName)
        {
            byte[] bucket = null;
            if (HasAttachment)
            {
                byte[] nameBytes = OpenMetaverse.Utils.StringToBytes(AttachmentName);
                int nameLen = nameBytes.GetLength(0);

                // old bucket byte buffer is bool,invtype,[2]ownerID,[18]itemID,[34]name
                // new bucket byte buffer is bool,invtype,[2]ownerID,[18]itemID,[34]0x01,[35]version,[36]noticeID,[52]name
                bucket = new byte[1 + 1 + 16 + 16 + 16 + 1 + 1 + nameLen];
                bucket[0] = 1;                          // HasAttachment boolean
                bucket[1] = AssetType;                  // attachment asset/inventory type
                GroupID.ToBytes(bucket, 2);             // 16-byte UUID
                ItemID.ToBytes(bucket, 18);             // 16-byte UUID
                bucket[34] = (byte)1;                   // sign of new extended format
                bucket[35] = (byte)0;                   // version number of new format
                NoticeID.ToBytes(bucket, 36);           // 16-byte UUID
                Array.Copy(nameBytes, 0, bucket, ATTACH_NAME_OFFSET, nameLen);  //  name
            }
            else
            {
                bucket = new byte[1 + 1 + 16 + 16 + 16 + 1 + 1 + 1];
                bucket[0] = 0;                          // HasAttachment boolean
                bucket[1] = 0;                          // attachment type
                UUID.Zero.ToBytes(bucket, 2);           // 16-byte UUID
                UUID.Zero.ToBytes(bucket, 18);          // 16-byte UUID
                bucket[34] = (byte)1;                   // sign of new extended format
                bucket[35] = (byte)0;                   // version number of new format
                NoticeID.ToBytes(bucket, 36);           // 16-byte UUID
                bucket[ATTACH_NAME_OFFSET] = 0;         //  name
            }
            return bucket;
        }

        // This bucket format is the NOT database-stored format for binary notice data, but rather the data sent to the viewer in the IM packet.
        public byte[] FormatBucketForIM(bool HasAttachment, byte AttType, UUID GroupID, string AttName)
        {
            byte[] nameBytes = OpenMetaverse.Utils.StringToBytes(" "+AttName);
            int nameLen = nameBytes.GetLength(0);
            byte[] bucket = new byte[18+nameLen];

            // bucket byte buffer is HasAttachment, invtype, ownerID, name
            bucket[0] = HasAttachment ? (byte)0x01 : (byte)0x00;
            bucket[1] = AttType;
            GroupID.ToBytes(bucket, 2); // 16-byte UUID
            Array.Copy(nameBytes, 0, bucket, 18, nameLen);  //  name
            return bucket;
        }

        public GridInstantMessage CreateGroupNoticeIM(UUID agentID, UUID groupNoticeID, byte dialog)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            GridInstantMessage msg = new GridInstantMessage();
            msg.imSessionID = UUID.Random().Guid;
            msg.toAgentID = agentID.Guid;
            msg.dialog = dialog;
            msg.fromGroup = true;
            msg.offline = (byte)1; // Allow this message to be stored offline for fetch on login
            msg.ParentEstateID = 0;
            msg.Position = Vector3.Zero;
            msg.RegionID = UUID.Zero.Guid;
    
            GroupNoticeInfo notice = m_groupData.GetGroupNotice(null, groupNoticeID);
            if (notice == null)
                return null;
            InitializeNoticeFromBucket(notice);

            msg.fromAgentID = notice.GroupID.Guid;
            msg.timestamp = notice.noticeData.Timestamp;
            msg.fromAgentName = notice.noticeData.FromName;
            msg.message = notice.noticeData.Subject + "|" + notice.Message;

            bool HasAttachment = notice.noticeData.HasAttachment;
            int nameLen = notice.noticeData.Attachment.Length;
            if (nameLen < 1)
            {
                HasAttachment = false;
                nameLen = 0;
            }

            msg.binaryBucket = FormatBucketForIM(HasAttachment, notice.noticeData.AssetType, notice.GroupID, notice.noticeData.Attachment);

            return msg;
        }

        public void SendAgentGroupDataUpdate(IClientAPI remoteClient)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            // Send agent information about his groups
            SendAgentGroupDataUpdate(remoteClient, remoteClient.AgentId);
        }

        public void JoinGroupRequest(IClientAPI remoteClient, UUID groupID)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            // Should check to see if OpenEnrollment, or if there's an outstanding invitation
            if (m_groupData.AddAgentToGroup(GetClientGroupRequestID(remoteClient), remoteClient.AgentId, groupID, UUID.Zero, false))
            {

                remoteClient.SendJoinGroupReply(groupID, true);

                // Should this send updates to everyone in the group?
                SendAgentGroupDataUpdate(remoteClient, remoteClient.AgentId);
            }
            else
            {
                remoteClient.SendJoinGroupReply(groupID, false);
            }
        }

        public void LeaveGroupRequest(IClientAPI remoteClient, UUID groupID)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            int rc = m_groupData.RemoveAgentFromGroup(GetClientGroupRequestID(remoteClient), remoteClient.AgentId, remoteClient.AgentId, groupID);
            if (rc == 0)
            {

                remoteClient.SendLeaveGroupReply(groupID, true);

                remoteClient.SendAgentDropGroup(groupID);

                // SL sends out notifcations to the group messaging session that the person has left
                // Should this also update everyone who is in the group?
                SendAgentGroupDataUpdate(remoteClient, remoteClient.AgentId);
            }
        }

        // agentID and agentName are only used if remoteClient is null.
        // agentID/agentName is the requesting user, typically owner of the script requesting it.
        public int EjectGroupMemberRequest(IClientAPI remoteClient, UUID agentID, string agentName, UUID groupID, UUID ejecteeID)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            GroupRequestID grID = GetClientGroupRequestID(remoteClient);
            IScene scene = m_sceneList[0];
            if (remoteClient != null)
            {
                agentID = remoteClient.AgentId;
                agentName = remoteClient.Name;
                scene = remoteClient.Scene;
            }

            int rc = m_groupData.RemoveAgentFromGroup(grID, agentID, ejecteeID, groupID);
            if (rc != 0)
                return rc;
            // After this point, always return SUCCESS.

            if (remoteClient != null)
                remoteClient.SendEjectGroupMemberReply(remoteClient.AgentId, groupID, true);

            GroupRecord groupInfo = m_groupData.GetGroupRecord(grID, groupID, null);
            UserProfileData userProfile = m_sceneList[0].CommsManager.UserService.GetUserProfile(ejecteeID);

            if ((groupInfo == null) || (userProfile == null))
            {
                // The user was removed from the group but we don't have the data to notify them.
                // This is not likely to ever happen in practice or the RemoveAgentFromGroup call 
                // would have returned false, however, this is here for safety and to indicate to
                // the calling script that the actual eject succeeded.
                return (int)Constants.GenericReturnCodes.SUCCESS;
            }

            // Send Message to Ejectee
            GridInstantMessage msg = new GridInstantMessage();

            msg.imSessionID = UUID.Zero.Guid;
            msg.fromAgentID = agentID.Guid;
            // msg.fromAgentID = info.GroupID;
            msg.toAgentID = ejecteeID.Guid;
            //msg.timestamp = (uint)Util.UnixTimeSinceEpoch();
            msg.timestamp = 0;
            msg.fromAgentName = agentName;
            msg.message = string.Format("You have been ejected from '{1}' by {0}.", agentName, groupInfo.GroupName);
            msg.dialog = (byte)OpenMetaverse.InstantMessageDialog.MessageFromAgent;
            msg.fromGroup = false;
            msg.offline = (byte)1; // Allow this message to be stored offline for fetch on login
            msg.ParentEstateID = 0;
            msg.Position = Vector3.Zero;
            msg.RegionID = scene.RegionInfo.RegionID.Guid;
            msg.binaryBucket = new byte[0];
            OutgoingInstantMessage(msg, ejecteeID);


            // Message to ejector
            // Interop, received special 210 code for ejecting a group member
            // this only works within the comms servers domain, and won't work hypergrid
            // TODO:FIXME: Use a presense server of some kind to find out where the 
            // client actually is, and try contacting that region directly to notify them,
            // or provide the notification via xmlrpc update queue

            msg = new GridInstantMessage();
            msg.imSessionID = UUID.Zero.Guid;
            msg.fromAgentID = agentID.Guid;
            msg.toAgentID = agentID.Guid;
            msg.timestamp = 0;
            msg.fromAgentName = agentName;
            if (userProfile != null)
            {
                msg.message = string.Format("{2} has been ejected from '{1}' by {0}.", agentName, groupInfo.GroupName, userProfile.Name);
            }
            else
            {
                msg.message = string.Format("{2} has been ejected from '{1}' by {0}.", agentName, groupInfo.GroupName, "Unknown member");
            }
            msg.dialog = (byte)210; //interop
            msg.fromGroup = false;
            msg.offline = (byte)0;
            msg.ParentEstateID = 0;
            msg.Position = Vector3.Zero;
            msg.RegionID = scene.RegionInfo.RegionID.Guid;
            msg.binaryBucket = new byte[0];
            OutgoingInstantMessage(msg, agentID);

            // SL sends out messages to everyone in the group
            // Who all should receive updates and what should they be updated with?
            UpdateAllClientsWithGroupInfo(ejecteeID);
            return (int)Constants.GenericReturnCodes.SUCCESS;
        }

        // agentID and agentName are only used if remoteClient is null.
        // agentID/agentName is the requesting user, typically owner of the script requesting it.
        public int InviteGroupRequest(IClientAPI remoteClient, UUID agentID, string agentName, UUID groupID, UUID invitedAgentID, UUID roleID)
        {
            GroupRequestID grID = GetClientGroupRequestID(remoteClient);
            GroupRecord groupInfo = m_groupData.GetGroupRecord(grID, groupID, null);
            IScene scene = m_sceneList[0];

            if (remoteClient != null)
            {
                agentID = remoteClient.AgentId;
                agentName = remoteClient.Name;
                scene = remoteClient.Scene;
            }

            string groupName;
            if (groupInfo != null)
                groupName = groupInfo.GroupName;
            else
                groupName = "(unknown)";

            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            // Get the list of users who have this sender muted.
            m_muteListModule = m_sceneList[0].RequestModuleInterface<IMuteListModule>();
            if (m_muteListModule != null)
            {
                if (m_muteListModule.IsMuted(agentID, invitedAgentID))
                {
                    if (remoteClient != null)
                        remoteClient.SendAlertMessage("You cannot invite someone who has you muted into a group.");
                    return (int)Constants.GenericReturnCodes.MUTED;
                }
            }

            // Send notice out to everyone that wants notices

            // Todo: Security check, probably also want to send some kind of notification
            UUID InviteID = UUID.Random();
            string reason = String.Empty;
            int rc = m_groupData.AddAgentToGroupInvite(grID, agentID, InviteID, groupID, roleID, invitedAgentID, out reason);
            if (rc != 0)
            {
                if (!(remoteClient == null || String.IsNullOrEmpty(reason)))
                    remoteClient.SendAlertMessage(reason);
                return rc;
            }

            if (m_msgTransferModule == null)
            {
                if (remoteClient != null)
                    remoteClient.SendAlertMessage("Error sending group invitation.");
                return (int)Constants.GenericReturnCodes.ERROR;
            }

            Guid inviteUUID = InviteID.Guid;

            GridInstantMessage msg = new GridInstantMessage();

            msg.imSessionID = inviteUUID;

            // msg.fromAgentID = agentId.Guid;
            msg.fromAgentID = groupID.Guid;
            msg.toAgentID = invitedAgentID.Guid;
            //msg.timestamp = (uint)Util.UnixTimeSinceEpoch();
            msg.timestamp = 0;
            msg.fromAgentName = agentName;
            msg.message = string.Format("{0} has invited you to join a group: {1}. There is no cost to join this group.", agentName, groupName);
            msg.dialog = (byte)OpenMetaverse.InstantMessageDialog.GroupInvitation;
            msg.fromGroup = true;
            msg.offline = (byte)1; //yes, store for fetching missed IMs on login
            msg.ParentEstateID = 0;
            msg.Position = Vector3.Zero;
            msg.RegionID = scene.RegionInfo.RegionID.Guid;
            msg.binaryBucket = new byte[20];

            OutgoingInstantMessage(msg, invitedAgentID);
            if (!(remoteClient == null || String.IsNullOrEmpty(reason)))
                remoteClient.SendAlertMessage(reason);
            return (int)Constants.GenericReturnCodes.SUCCESS;
        }
        #endregion

        #region Client/Update Tools

        /// <summary>
        /// Try to find an active IClientAPI reference for agentID giving preference to root connections
        /// </summary>
        private IClientAPI GetActiveClient(UUID agentID)
        {
            IClientAPI child = null;

            // Try root avatar first
            foreach (Scene scene in m_sceneList)
            {
                if (scene.Entities.ContainsKey(agentID) &&
                        scene.Entities[agentID] is ScenePresence)
                {
                    ScenePresence user = (ScenePresence)scene.Entities[agentID];
                    if (!user.IsChildAgent)
                    {
                        return user.ControllingClient;
                    }
                    else
                    {
                        child = user.ControllingClient;
                    }
                }
            }

            // If we didn't find a root, then just return whichever child we found, or null if none
            return child;
        }

        private GroupRequestID GetClientGroupRequestID(IClientAPI client)
        {
            if (client == null)
            {
                return new GroupRequestID();
            }

            GroupRequestID reqID = new GroupRequestID();
            reqID.AgentID = client.AgentId;
            reqID.SessionID = client.SessionId;
            reqID.UserServiceURL = m_sceneList[0].CommsManager.NetworkServersInfo.UserURL;
            return reqID;
        }

        /// <summary>
        /// Send 'remoteClient' the group membership 'data' for agent 'dataForAgentID'.
        /// </summary>
        private void SendGroupMembershipInfoViaCaps(IClientAPI remoteClient, UUID dataForAgentID, GroupMembershipData[] data)
        {
            if (m_debugEnabled) m_log.InfoFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            OSDArray AgentData = new OSDArray(1);
            OSDMap AgentDataMap = new OSDMap(1);
            AgentDataMap.Add("AgentID", OSD.FromUUID(dataForAgentID));
            AgentData.Add(AgentDataMap);


            OSDArray GroupData = new OSDArray(data.Length);
            OSDArray NewGroupData = new OSDArray(data.Length);

            foreach (GroupMembershipData membership in data)
            {
                OSDMap GroupDataMap = new OSDMap(6);
                OSDMap NewGroupDataMap = new OSDMap(1);

                GroupDataMap.Add("GroupID", OSD.FromUUID(membership.GroupID));
                GroupDataMap.Add("GroupPowers", OSD.FromULong(membership.GroupPowers));
                GroupDataMap.Add("AcceptNotices", OSD.FromBoolean(membership.AcceptNotices));
                GroupDataMap.Add("GroupInsigniaID", OSD.FromUUID(membership.GroupPicture));
                GroupDataMap.Add("Contribution", OSD.FromInteger(membership.Contribution));
                GroupDataMap.Add("GroupName", OSD.FromString(membership.GroupName));
                NewGroupDataMap.Add("ListInProfile", OSD.FromBoolean(membership.ListInProfile));

                GroupData.Add(GroupDataMap);
                NewGroupData.Add(NewGroupDataMap);
            }

            OSDMap llDataStruct = new OSDMap(3);
            llDataStruct.Add("AgentData", AgentData);
            llDataStruct.Add("GroupData", GroupData);
            llDataStruct.Add("NewGroupData", NewGroupData);

            IEventQueue queue = remoteClient.Scene.RequestModuleInterface<IEventQueue>();

            if (queue != null)
            {
                queue.Enqueue(EventQueueHelper.BuildEvent("AgentGroupDataUpdate", llDataStruct), remoteClient.AgentId);
            }
            
        }

        private void SendScenePresenceUpdate(UUID AgentID, string Title)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: Updating scene title for {0} with title: {1}", AgentID, Title);

            ScenePresence presence = null;
            lock (m_sceneList)
            {
                foreach (Scene scene in m_sceneList)
                {
                    presence = scene.GetScenePresence(AgentID);
                    if (presence != null)
                    {
                        if (presence.Grouptitle != Title)
                        {
                            presence.Grouptitle = Title;
                            presence.SceneView.SendFullUpdateToAllClients();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Send updates to all clients who might be interested in groups data for dataForClientID
        /// </summary>
        private void UpdateAllClientsWithGroupInfo(UUID dataForClientID)
        {
            if (m_debugEnabled) m_log.InfoFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            // TODO: Probably isn't nessesary to update every client in every scene.  
            // Need to examine client updates and do only what's nessesary.
            lock (m_sceneList)
            {
                foreach (Scene scene in m_sceneList)
                {
                    scene.ForEachClient(delegate(IClientAPI client) { SendAgentGroupDataUpdate(client, dataForClientID); });
                }
            }
        }

        /// <summary>
        /// Update remoteClient with group information about dataForAgentID
        /// </summary>
        private void SendAgentGroupDataUpdate(IClientAPI remoteClient, UUID dataForAgentID)
        {
            if (m_debugEnabled) m_log.InfoFormat("[GROUPS]: {0} called for {1}", System.Reflection.MethodBase.GetCurrentMethod().Name, remoteClient.Name);

            // TODO: All the client update functions need to be reexamined because most do too much and send too much stuff

            OnAgentDataUpdateRequest(remoteClient, dataForAgentID, UUID.Zero);


            // Need to send a group membership update to the client
            // UDP version doesn't seem to behave nicely.  But we're going to send it out here
            // with an empty group membership to hopefully remove groups being displayed due
            // to the core Groups Stub
            remoteClient.SendGroupMembership( new GroupMembershipData[0] );

            GroupMembershipData[] membershipData = m_groupData.GetAgentGroupMemberships(GetClientGroupRequestID(remoteClient), dataForAgentID).ToArray();

            SendGroupMembershipInfoViaCaps(remoteClient, dataForAgentID, membershipData);
            remoteClient.SendAvatarGroupsReply(dataForAgentID, membershipData);
        }

        private void SendAgentDataUpdate(IClientAPI remoteClient, UUID dataForAgentID, UUID activeGroupID, string activeGroupName, ulong activeGroupPowers, string activeGroupTitle)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            // TODO: All the client update functions need to be reexamined because most do too much and send too much stuff
            UserProfileData userProfile = m_sceneList[0].CommsManager.UserService.GetUserProfile(dataForAgentID);
            string firstname, lastname;
            if (userProfile != null)
            {
                firstname = userProfile.FirstName;
                lastname = userProfile.SurName;
            }
            else
            {
                firstname = "Unknown";
                lastname = "Unknown";
            }

            remoteClient.SendAgentDataUpdate(dataForAgentID, activeGroupID, firstname,
                    lastname, activeGroupPowers, activeGroupName,
                    activeGroupTitle);
        }

        #endregion

        #region IM Backed Processes

        // returns true if the message was sent to a local active user.
        private bool OutgoingInstantMessage(GridInstantMessage msg, UUID msgTo)
        {
            if (m_debugEnabled) m_log.InfoFormat("[GROUPS]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            IClientAPI localClient = GetActiveClient(msgTo);
            if (localClient != null)
            {
                if (m_debugEnabled) m_log.InfoFormat("[GROUPS]: MsgTo ({0}) is local, delivering directly", localClient.Name);
                localClient.SendInstantMessage(msg);
            }
            else
            {
                if (m_debugEnabled) m_log.InfoFormat("[GROUPS]: MsgTo ({0}) is not local, delivering via TransferModule", msgTo);
                m_msgTransferModule.SendInstantMessage(msg, delegate(bool success) { if (m_debugEnabled) m_log.DebugFormat("[GROUPS]: Message Sent: {0}", success?"Succeeded":"Failed"); });
            }
            return (localClient != null);
        }

        public void NotifyChange(UUID groupID)
        {
            // Notify all group members of a chnge in group roles and/or
            // permissions
            //
        }

        #endregion
    }

}
