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
using System.Linq;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Communications.Cache;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using System.Collections.Generic;
using System.Timers;
using System.Text;
using System.IO;

namespace OpenSim.Region.CoreModules.Avatar.AvatarFactory
{
    public class AvatarFactoryModule : IAvatarFactory, IRegionModule
    {
        public delegate void DatabaseUpdatingCallback();

        private struct AppearanceUpdateRequest
        {
            public UUID UserId;
            public AvatarAppearance Appearance;
            public DateTime RequestTime;
            public Dictionary<UUID, UUID> BakedTextures;
            public Action CallBack;
        }

        private class BuildCOF
        {
            private System.Threading.Timer _timer;
            private bool _cofHasBeenCreated = false;
            private bool _wearableHasBeenSet = false;
            private UUID _wearableInventoryItemID = UUID.Zero;
            private Scene _scene;
            private UUID _avatarID = UUID.Zero;
            private bool _addLink = true;
            private Action<UUID, UUID> _clear;

            public BuildCOF(Action<UUID, UUID> clear)
            {
                _clear = clear;
            }

            public void SetWearableToLookFor(UUID wearable, Scene scene, UUID avatarID, bool addLink)
            {
                _wearableInventoryItemID = wearable;
                _avatarID = avatarID;
                _scene = scene;
                _wearableHasBeenSet = true;
                _addLink = addLink;
                if (_cofHasBeenCreated)
                    return;
                else if (addLink && CheckIfCOFExists(_wearableInventoryItemID))
                {
                    _cofHasBeenCreated = true;
                    _clear(_avatarID, _wearableInventoryItemID);
                    return;
                }
                if(_timer == null)
                    _timer = new System.Threading.Timer(TimerExpired, wearable, 5000, System.Threading.Timeout.Infinite);
            }

            private bool CheckIfCOFExists(UUID _wearableInventoryItemID)
            {
                CachedUserInfo userInfo = _scene.CommsManager.UserService.GetUserDetails(_avatarID);

                if (userInfo == null)
                    return false;

                //verify this user actually owns the item
                InventoryFolderBase CurrentOutfitFolder = GetCurrentOutfitFolder(userInfo);
                if (CurrentOutfitFolder == null) return false; //No COF, just ignore

                foreach (InventoryItemBase cofItem in CurrentOutfitFolder.Items)
                {
                    if (cofItem.AssetID == _wearableInventoryItemID)
                        return true;
                }

                return false;
            }

            private static InventoryFolderBase GetCurrentOutfitFolder(CachedUserInfo userInfo)
            {
                // Duplicate method exists at Scene.Inventory.cs::Scene::GetCurrentOutfitFolder

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

            public void COFHasBeenSet()
            {
                _cofHasBeenCreated = true;
                if (_timer != null)
                {
                    _timer.Dispose();
                    _timer = null;
                }
            }

            public bool Finished()
            {
                return _wearableHasBeenSet && _cofHasBeenCreated;
            }

            protected const string COF_NAME = "Current Outfit";
            private void TimerExpired(object o)
            {
                //The COF has not been added, we need to add it now
                m_log.WarnFormat("[BuildCOF]: Need to {1} COF item {0}", o.ToString(), _addLink ? "add" : "remove");

                if (_addLink)
                    CreateLink();
                else
                    RemoveLink();


                _clear(_avatarID, _wearableInventoryItemID);
            }

            private void RemoveLink()
            {
                CachedUserInfo userInfo = _scene.CommsManager.UserService.GetUserDetails(_avatarID);

                if (userInfo == null)
                    return;

                InventoryFolderBase CurrentOutfitFolder = GetCurrentOutfitFolder(userInfo);
                if (CurrentOutfitFolder == null) return; //No COF, just ignore
                userInfo.UpdateFolder(CurrentOutfitFolder);//Update the versionID

                foreach (InventoryItemBase cofItem in CurrentOutfitFolder.Items)
                {
                    if (cofItem.AssetID == _wearableInventoryItemID)
                        userInfo.DeleteItem(cofItem);
                }
                ScenePresence presence;
                if ((presence = _scene.GetScenePresence(_avatarID)) != null)
                    presence.ControllingClient.SendInventoryFolderDetails(presence.UUID, CurrentOutfitFolder, GetCurrentOutfitFolder(userInfo).Items, new List<InventoryFolderBase>(), false, true);
            }

            private void CreateLink()
            {
                if (!_scene.Permissions.CanCreateUserInventory((int)InventoryType.Wearable, _avatarID))
                    return;

                ScenePresence presence;
                if ((presence = _scene.GetScenePresence(_avatarID)) != null)
                {
                    // Don't link to default items
                    if ((_wearableInventoryItemID == AvatarWearable.DEFAULT_EYES_ITEM) ||
                        (_wearableInventoryItemID == AvatarWearable.DEFAULT_BODY_ITEM) ||
                        (_wearableInventoryItemID == AvatarWearable.DEFAULT_HAIR_ITEM) ||
                        (_wearableInventoryItemID == AvatarWearable.DEFAULT_PANTS_ITEM) ||
                        (_wearableInventoryItemID == AvatarWearable.DEFAULT_SHIRT_ITEM) ||
                        (_wearableInventoryItemID == AvatarWearable.DEFAULT_SKIN_ITEM))
                    {
                        return;
                    }

                    CachedUserInfo userInfo = _scene.CommsManager.UserService.GetUserDetails(_avatarID);

                    if (userInfo != null)
                    {
                        InventoryItemBase oldItem = null;
                        try
                        {
                            oldItem = userInfo.FindItem(_wearableInventoryItemID);
                        }
                        catch { }
                        if (oldItem == null)
                            return;//Item doesn't exist?

                        InventoryFolderBase CurrentOutfitFolder = GetCurrentOutfitFolder(userInfo);
                        if (CurrentOutfitFolder == null) return; //No COF, just ignore
                        CurrentOutfitFolder.Version++;
                        userInfo.UpdateFolder(CurrentOutfitFolder);

                        InventoryItemBase item = new InventoryItemBase();
                        item.Owner = _avatarID;
                        item.CreatorId = _avatarID.ToString();
                        item.ID = UUID.Random();
                        item.AssetID = _wearableInventoryItemID;
                        item.Description = oldItem.Description;
                        item.Name = oldItem.Name;
                        item.Flags = oldItem.Flags;
                        item.AssetType = (int)AssetType.Link;
                        item.InvType = (int)InventoryType.Wearable;
                        item.Folder = CurrentOutfitFolder.ID;
                        item.CreationDate = Util.UnixTimeSinceEpoch();

                        item.BasePermissions = (uint)(PermissionMask.All | PermissionMask.Export);
                        item.CurrentPermissions = (uint)(PermissionMask.All | PermissionMask.Export);
                        item.GroupPermissions = (uint)PermissionMask.None;
                        item.EveryonePermissions = (uint)PermissionMask.None;
                        item.NextPermissions = (uint)PermissionMask.All;

                        userInfo.AddItem(item); 
                        presence.ControllingClient.SendInventoryItemCreateUpdate(item, 0);
                    }
                    else
                    {
                        m_log.WarnFormat(
                            "No user details associated with client {0} uuid {1} in CreateNewInventoryItem!",
                             presence.Name, presence.UUID);
                    }
                }
                else
                {
                    m_log.ErrorFormat(
                        "ScenePresence for agent uuid {0} unexpectedly not found in HandleLinkInventoryItem",
                        _avatarID);
                }
            }

            private void PrintCOF()
            {
                CachedUserInfo userInfo = _scene.CommsManager.UserService.GetUserDetails(_avatarID);

                if (userInfo == null)
                    return;

                InventoryFolderBase CurrentOutfitFolder = GetCurrentOutfitFolder(userInfo);
                if (CurrentOutfitFolder == null) return; //No COF, just ignore

                foreach (InventoryItemBase item in CurrentOutfitFolder.Items)
                    m_log.Info("[COF]: " + item.Name);
            }
        }

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private Scene m_scene = null;
        private const int APPEARANCE_UPDATE_INTERVAL = 1500;
        private Timer _appearanceUpdateTimer = new Timer(APPEARANCE_UPDATE_INTERVAL);
        private Dictionary<UUID, AppearanceUpdateRequest> _pendingUpdates = new Dictionary<UUID, AppearanceUpdateRequest>();
        private Dictionary<UUID, Dictionary<UUID, BuildCOF>> _currentlyWaitingCOFBuilds = new Dictionary<UUID, Dictionary<UUID, BuildCOF>>();
        private List<UUID> _viewer2Users = new List<UUID>();
        private bool _cacheBakedTexturesEnabled = true;
        private bool _cofSyncEnabled = false;

        public bool TryGetAvatarAppearance(UUID avatarId, out AvatarAppearance appearance)
        {
            CachedUserInfo profile = m_scene.CommsManager.UserService.GetUserDetails(avatarId);
            //if ((profile != null) && (profile.RootFolder != null))
            if (profile != null)
            {
                appearance = m_scene.CommsManager.AvatarService.GetUserAppearance(avatarId);
                if (appearance != null)
                {
                    //SetAppearanceAssets(profile, ref appearance);
                    //m_log.DebugFormat("[APPEARANCE]: Found : {0}", appearance.ToString());
                    return true;
                }
            }

            appearance = CreateDefault(avatarId);
            m_log.ErrorFormat("[APPEARANCE]: Appearance not found for {0}, creating default", avatarId);
            return false;
        }

        private AvatarAppearance CreateDefault(UUID avatarId)
        {
            return (new AvatarAppearance(avatarId));
        }

        public void Initialize(Scene scene, IConfigSource source)
        {
            scene.RegisterModuleInterface<IAvatarFactory>(this);
            scene.EventManager.OnNewClient += NewClient;
            scene.EventManager.OnRemovePresence += RemoveClient;

            if (m_scene == null)
                m_scene = scene;

            IConfig config = m_scene.Config.Configs["AvatarFactory"];
            if (config != null)
            {
                _cacheBakedTexturesEnabled = config.GetBoolean("EnableCachedBakedTextures", _cacheBakedTexturesEnabled);
                _cofSyncEnabled = config.GetBoolean("EnableCOFSync", _cofSyncEnabled);
            }

            _appearanceUpdateTimer.AutoReset = true;
            _appearanceUpdateTimer.Elapsed += OnAppearanceUpdateTimer;
            _appearanceUpdateTimer.Start();
        }

        public void PostInitialize()
        {
        }

        public void Close()
        {
        }

        public string Name
        {
            get { return "Default Avatar Factory"; }
        }

        public bool IsSharedModule
        {
            get { return false; }
        }

        public void NewClient(IClientAPI client)
        {
            client.OnAvatarNowWearing += AvatarIsWearing;
            client.OnLinkInventoryItem += LinkInventoryItem;
            if (_cacheBakedTexturesEnabled)
                client.OnAgentCachedTextureRequest += AgentCachedTextureRequest;
            client.OnPreRemoveInventoryItem += RemoveInventoryItem;
        }

        public void RemoveClient(UUID id)
        {
            //presence.ControllingClient.OnAvatarNowWearing -= AvatarIsWearing;
            //presence.ControllingClient.OnLinkInventoryItem -= LinkInventoryItem;
            //presence.ControllingClient.OnAgentCachedTextureRequest -= AgentCachedTextureRequest;
            //presence.ControllingClient.OnPreRemoveInventoryItem -= RemoveInventoryItem;
            lock(_currentlyWaitingCOFBuilds)
            {
                _viewer2Users.Remove(id);
                _currentlyWaitingCOFBuilds.Remove(id);
            }
        }

        void AgentCachedTextureRequest(IClientAPI client, List<CachedAgentArgs> args)
        {
            ScenePresence sp = m_scene.GetScenePresence(client.AgentId);
            // Look up hashes to make sure that the request is valid
            AvatarAppearance app = sp.Appearance;
            List<CachedAgentArgs> cachedTextures = m_scene.CommsManager.AvatarService.GetCachedBakedTextures(args);
            if (cachedTextures == null || cachedTextures.Count == 0)
            {
                cachedTextures = new List<CachedAgentArgs>();
                foreach (CachedAgentArgs arg in args)
                    cachedTextures.Add(new CachedAgentArgs() { ID = UUID.Zero, TextureIndex = arg.TextureIndex });
            }
            else if (cachedTextures.Count != args.Count)
            {
                //This happens if we don't have all of the textures in the database, 
                //  so we need to re-add UUID.Zero to tell the client to rebake
                foreach (CachedAgentArgs arg in args)
                {
                    if (cachedTextures.Find((a) => a.TextureIndex == arg.TextureIndex) == null)
                        cachedTextures.Add(new CachedAgentArgs() { ID = UUID.Zero, TextureIndex = arg.TextureIndex });
                }
            }

            client.SendAgentCachedTexture(cachedTextures);
        }

        void LinkInventoryItem(IClientAPI remoteClient, UUID transActionID, UUID folderID,
            uint callbackID, string description, string name, sbyte invType, sbyte type, UUID olditemID)
        {
            if (!_cofSyncEnabled) return;

            lock (_currentlyWaitingCOFBuilds)
            {
                if (!_viewer2Users.Contains(remoteClient.AgentId))
                    _viewer2Users.Add(remoteClient.AgentId);
                bool add = false;
                Dictionary<UUID, BuildCOF> waitingCOFs = new Dictionary<UUID, BuildCOF>();
                if ((add = !_currentlyWaitingCOFBuilds.TryGetValue(remoteClient.AgentId, out waitingCOFs)))
                    waitingCOFs = new Dictionary<UUID, BuildCOF>();
                if (waitingCOFs.ContainsKey(olditemID))
                {
                    BuildCOF cof = waitingCOFs[olditemID];
                    cof.COFHasBeenSet();
                    if (cof.Finished())
                        waitingCOFs.Remove(olditemID);
                }
                else
                {
                    BuildCOF cof = new BuildCOF(ClearWaitingCOF);
                    cof.COFHasBeenSet();
                    waitingCOFs.Add(olditemID, cof);
                }
                if (add)
                    _currentlyWaitingCOFBuilds.Add(remoteClient.AgentId, waitingCOFs);
            }
            
        }

        void RemoveInventoryItem(IClientAPI remoteClient, UUID itemID, bool forceDelete)
        {
            if (!_cofSyncEnabled) return;

            CachedUserInfo userInfo
                = m_scene.CommsManager.UserService.GetUserDetails(remoteClient.AgentId);

            if (userInfo == null)
                return;

            InventoryItemBase item = userInfo.FindItem(itemID);

            if (item != null && item.AssetType == (int)AssetType.Link)
            {
                lock (_currentlyWaitingCOFBuilds)
                {
                    if(!_viewer2Users.Contains(remoteClient.AgentId))
                        _viewer2Users.Add(remoteClient.AgentId);
                    bool add = false;
                    Dictionary<UUID, BuildCOF> waitingCOFs = new Dictionary<UUID, BuildCOF>();
                    if ((add = !_currentlyWaitingCOFBuilds.TryGetValue(remoteClient.AgentId, out waitingCOFs)))
                        waitingCOFs = new Dictionary<UUID, BuildCOF>();
                    if (waitingCOFs.ContainsKey(itemID))
                    {
                        BuildCOF cof = waitingCOFs[itemID];
                        cof.COFHasBeenSet();
                        if (cof.Finished())
                            waitingCOFs.Remove(itemID);
                    }
                    else
                    {
                        BuildCOF cof = new BuildCOF(ClearWaitingCOF);
                        cof.COFHasBeenSet();
                        waitingCOFs.Add(itemID, cof);
                    }
                    if (add)
                        _currentlyWaitingCOFBuilds.Add(remoteClient.AgentId, waitingCOFs);
                }
            }
        }

        private void ClearWaitingCOF(UUID avatarID, UUID itemID)
        {
            lock (_currentlyWaitingCOFBuilds)
                if(_currentlyWaitingCOFBuilds.ContainsKey(avatarID))
                    _currentlyWaitingCOFBuilds[avatarID].Remove(itemID);
        }

        private void SetAppearanceAssets(CachedUserInfo profile, ref List<AvatarWearable> wearables, IClientAPI clientView)
        {
            foreach (AvatarWearable wearable in wearables)
            {
                // Skip it if its empty
                if (wearable.ItemID == UUID.Zero)
                    continue;

                // Ignore ruth's assets
                if (((wearable.WearableType == AvatarWearable.BODY) && (wearable.ItemID == AvatarWearable.DEFAULT_BODY_ITEM)) ||
                    ((wearable.WearableType == AvatarWearable.SKIN) && (wearable.ItemID == AvatarWearable.DEFAULT_SKIN_ITEM)) ||
                    ((wearable.WearableType == AvatarWearable.HAIR) && (wearable.ItemID == AvatarWearable.DEFAULT_HAIR_ITEM)) ||
                    ((wearable.WearableType == AvatarWearable.EYES) && (wearable.ItemID == AvatarWearable.DEFAULT_EYES_ITEM)) ||
                    ((wearable.WearableType == AvatarWearable.SHIRT) && (wearable.ItemID == AvatarWearable.DEFAULT_SHIRT_ITEM)) ||
                    ((wearable.WearableType == AvatarWearable.PANTS) && (wearable.ItemID == AvatarWearable.DEFAULT_PANTS_ITEM)))
                {
                    continue;
                }

                // Otherwise look up the asset and store the translated value
                // XXX dont look up stuff we already know about.. save a trip to the asset server

                InventoryItemBase baseItem = profile.FindItem(wearable.ItemID);
                baseItem = profile.ResolveLink(baseItem);
                

                if (baseItem != null)
                {
                    wearable.AssetID = baseItem.AssetID;
                }
                else
                {
                    m_log.ErrorFormat("[APPEARANCE]: Can't find inventory item {0}", wearable.ItemID);
                }
            }
        }

        /// <summary>
        /// Update what the avatar is wearing using an item from their inventory.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public void AvatarIsWearing(Object sender, AvatarWearingArgs e)
        {
            IClientAPI clientView = (IClientAPI)sender;
            ScenePresence avatar = m_scene.GetScenePresence(clientView.AgentId);
            
            if (avatar == null) 
            {
                m_log.Error("[APPEARANCE]: Avatar is child agent, ignoring AvatarIsWearing event");
                return;
            }

            CachedUserInfo profile = m_scene.CommsManager.UserService.GetUserDetails(clientView.AgentId);
            if (profile != null)
            {
                AvatarAppearance appearance = avatar.Appearance;

                // we need to clean out the existing textures
                appearance.Texture = AvatarAppearance.GetDefaultTexture();

                List<AvatarWearable> wearables = new List<AvatarWearable>();
                lock (_currentlyWaitingCOFBuilds)
                {
                    //Check to see whether the client can manage itself
                    if (_cofSyncEnabled && !_viewer2Users.Contains(clientView.AgentId))
                    {
                        foreach (AvatarWearingArgs.Wearable wear in e.NowWearing)
                        {
                            wearables.Add(new AvatarWearable(wear.Type, wear.ItemID, UUID.Zero));
                            AvatarWearable oldWearable = appearance.GetWearableOfType(wear.Type);
                            if (wear.ItemID != UUID.Zero)
                            {
                                if (oldWearable == null || oldWearable.ItemID == UUID.Zero || wear.ItemID != oldWearable.ItemID)
                                {
                                    bool add = false;
                                    Dictionary<UUID, BuildCOF> waitingCOFs = new Dictionary<UUID, BuildCOF>();
                                    if ((add = !_currentlyWaitingCOFBuilds.TryGetValue(clientView.AgentId, out waitingCOFs)))
                                        waitingCOFs = new Dictionary<UUID, BuildCOF>();
                                    //Make sure that the new item is added
                                    if (waitingCOFs.ContainsKey(wear.ItemID))
                                    {
                                        BuildCOF cof = waitingCOFs[wear.ItemID];
                                        cof.SetWearableToLookFor(wear.ItemID, m_scene, clientView.AgentId, true);
                                        if (cof.Finished())
                                            waitingCOFs.Remove(wear.ItemID);
                                    }
                                    else
                                    {
                                        BuildCOF cof = new BuildCOF(ClearWaitingCOF);
                                        cof.SetWearableToLookFor(wear.ItemID, m_scene, clientView.AgentId, true);
                                        waitingCOFs.Add(wear.ItemID, cof);
                                    }
                                    if (add)
                                        _currentlyWaitingCOFBuilds.Add(clientView.AgentId, waitingCOFs);
                                }
                                if (oldWearable != null && oldWearable.ItemID != UUID.Zero && wear.ItemID != oldWearable.ItemID)
                                {
                                    bool add = false;
                                    Dictionary<UUID, BuildCOF> waitingCOFs = new Dictionary<UUID, BuildCOF>();
                                    if ((add = !_currentlyWaitingCOFBuilds.TryGetValue(clientView.AgentId, out waitingCOFs)))
                                        waitingCOFs = new Dictionary<UUID, BuildCOF>();
                                    //Check for removal of old item
                                    if (waitingCOFs.ContainsKey(oldWearable.ItemID))
                                    {
                                        BuildCOF cof = waitingCOFs[oldWearable.ItemID];
                                        cof.SetWearableToLookFor(oldWearable.ItemID, m_scene, clientView.AgentId, false);
                                        if (cof.Finished())
                                            waitingCOFs.Remove(oldWearable.ItemID);
                                    }
                                    else
                                    {
                                        BuildCOF cof = new BuildCOF(ClearWaitingCOF);
                                        cof.SetWearableToLookFor(oldWearable.ItemID, m_scene, clientView.AgentId, false);
                                        waitingCOFs.Add(oldWearable.ItemID, cof);
                                    }
                                    if (add)
                                        _currentlyWaitingCOFBuilds.Add(clientView.AgentId, waitingCOFs);
                                }
                            }
                            else if (oldWearable != null && oldWearable.ItemID != UUID.Zero)
                            {
                                bool add = false;
                                Dictionary<UUID, BuildCOF> waitingCOFs = new Dictionary<UUID, BuildCOF>();
                                if ((add = !_currentlyWaitingCOFBuilds.TryGetValue(clientView.AgentId, out waitingCOFs)))
                                    waitingCOFs = new Dictionary<UUID, BuildCOF>();
                                //Remove the item if it was just removed
                                if (waitingCOFs.ContainsKey(oldWearable.ItemID))
                                {
                                    BuildCOF cof = waitingCOFs[oldWearable.ItemID];
                                    cof.SetWearableToLookFor(oldWearable.ItemID, m_scene, clientView.AgentId, false);
                                    if (cof.Finished())
                                        waitingCOFs.Remove(oldWearable.ItemID);
                                }
                                else
                                {
                                    BuildCOF cof = new BuildCOF(ClearWaitingCOF);
                                    cof.SetWearableToLookFor(oldWearable.ItemID, m_scene, clientView.AgentId, false);
                                    waitingCOFs.Add(oldWearable.ItemID, cof);
                                }
                                if (add)
                                    _currentlyWaitingCOFBuilds.Add(clientView.AgentId, waitingCOFs);
                            }
                        }
                    }
                    else
                    {
                        foreach (AvatarWearingArgs.Wearable wear in e.NowWearing)
                            wearables.Add(new AvatarWearable(wear.Type, wear.ItemID, UUID.Zero));
                    }
                }
                // Wearables are a stack. The entries we have represent the current "top" stack state.  Apply them

                SetAppearanceAssets(profile, ref wearables, clientView);
                avatar.Appearance.SetWearables(wearables);
                this.UpdateDatabase(clientView.AgentId, avatar.Appearance, null, null);
            }
            else
            {
                m_log.WarnFormat("[APPEARANCE]: Cannot set wearables for {0}, no user profile found", clientView.Name);
            }
        }

        public void UpdateDatabase(UUID user, AvatarAppearance appearance, Action callBack, Dictionary<UUID, UUID> bakedTextures)
        {
            lock (_pendingUpdates)
            {
                // m_log.InfoFormat("[LLCV]: Avatar database update ({0}) queued for user {1}", appearance.Serial, user);
                if (_pendingUpdates.ContainsKey(user))
                {
                    _pendingUpdates.Remove(user);
                }

                AppearanceUpdateRequest appearanceUpdate =
                    new AppearanceUpdateRequest
                    {
                        UserId = user,
                        Appearance = appearance,
                        RequestTime = DateTime.Now,
                        BakedTextures = bakedTextures,
                        CallBack = callBack
                    };

                _pendingUpdates.Add(user, appearanceUpdate);

                if (!_appearanceUpdateTimer.Enabled)
                {
                    _appearanceUpdateTimer.Enabled = true;
                }
            }
        }

        private void OnAppearanceUpdateTimer(object source, ElapsedEventArgs e)
        {
            List<AppearanceUpdateRequest> readyRequests = new List<AppearanceUpdateRequest>();
            lock (_pendingUpdates)
            {
                foreach (AppearanceUpdateRequest req in _pendingUpdates.Values)
                {
                    if (DateTime.Now - req.RequestTime >= TimeSpan.FromSeconds(3.0))
                    {
                        readyRequests.Add(req);
                    }
                }

                foreach (AppearanceUpdateRequest req in readyRequests)
                {
                    _pendingUpdates.Remove(req.UserId);
                }

                if (_pendingUpdates.Count == 0)
                {
                    _appearanceUpdateTimer.Enabled = false;
                }
            }

            foreach (AppearanceUpdateRequest upd in readyRequests)
            {
                bool appearanceValid = true;

                // Sanity checking the update
                List<AvatarWearable> wearables = upd.Appearance.GetWearables();
                foreach (AvatarWearable wearable in wearables)
                {
                    if (AvatarWearable.IsRequiredWearable(wearable.WearableType) && (wearable.ItemID == UUID.Zero))
                    {
                        m_log.ErrorFormat(
                            "[APPEARANCE]: Refusing to commit avatar appearance for user {0} because required wearable is zero'd", 
                            upd.UserId.ToString());
                        appearanceValid = false;
                        break;
                    }
                }

                //Don't allow saving of appearances to the avatarappearance table for bots
                if (upd.Appearance.IsBotAppearance)
                    appearanceValid = false;

                if (appearanceValid == true)
                {
                    if (upd.CallBack != null) 
                        upd.CallBack();
                    m_log.InfoFormat("[LLCV]: Avatar database update ({0}) committing for user {1}", upd.Appearance.Serial, upd.UserId);
                    m_scene.CommsManager.AvatarService.UpdateUserAppearance(upd.UserId, upd.Appearance);
                    if (upd.BakedTextures != null && upd.BakedTextures.Count > 0)
                        m_scene.CommsManager.AvatarService.SetCachedBakedTextures(upd.BakedTextures);
                }
            }
        }
    }
}
