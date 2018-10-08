/*
 * Copyright (c) 2015, InWorldz Halcyon Developers
 * All rights reserved.
 * 
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 * 
 *   * Redistributions of source code must retain the above copyright notice, this
 *     list of conditions and the following disclaimer.
 * 
 *   * Redistributions in binary form must reproduce the above copyright notice,
 *     this list of conditions and the following disclaimer in the documentation
 *     and/or other materials provided with the distribution.
 * 
 *   * Neither the name of halcyon nor the names of its
 *     contributors may be used to endorse or promote products derived from
 *     this software without specific prior written permission.
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
 * FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
 * DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
 * CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
 * OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenSim.Framework;
using OpenMetaverse;
using System.Net;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using Nini.Config;
using System.Xml;
using OpenSim.Framework.Communications.Cache;
using OpenSim.Region.Framework;
using OpenSim.Data;
using System.Text.RegularExpressions;
using System.Threading;

namespace OpenSim.Region.CoreModules.Agent.BotManager
{
    public class BotManager : IRegionModule, IBotManager
    {
        #region Declares

        private Scene m_scene;
        private Dictionary<UUID, IBot> m_bots = new Dictionary<UUID, IBot>();
        private int m_maxNumberOfBots = 40;
        private int m_maxLandImpactAllowedByOutfitAttachments = 5000;

        #endregion

        #region IRegionModule Members

        public void Initialize(Scene scene, IConfigSource source)
        {
            scene.RegisterModuleInterface<IBotManager>(this);
            m_scene = scene;

            IConfig conf = source.Configs["BotSettings"];
            if (conf != null)
            {
                m_maxNumberOfBots = conf.GetInt("MaximumNumberOfBots", m_maxNumberOfBots);
                m_maxLandImpactAllowedByOutfitAttachments = conf.GetInt("MaxLandImpactAllowedByBotOutfitAttachments", m_maxLandImpactAllowedByOutfitAttachments);
            }
        }

        public void PostInitialize()
        {
        }

        public void Close()
        {
        }

        public string Name
        {
            get { return "BotManager"; }
        }

        public bool IsSharedModule
        {
            get { return false; }
        }

        #endregion

        #region Bot Management

        public UUID CreateBot(string firstName, string lastName, Vector3 startPos, string outfitName, UUID itemID, UUID owner, out string reason)
        {
            try
            {
                // do simple limit/string tests first before profile lookups
                if ((firstName.Trim() == String.Empty) || (lastName.Trim() == String.Empty))
                {
                    reason = "Invalid name: Bots require both first and last name.";
                    return UUID.Zero;
                }
                if (lastName.ToLower().Contains("inworldz") || firstName.ToLower().Contains("inworldz"))
                {
                    reason = "Invalid name: This name has already been taken.";
                    return UUID.Zero;//You cannot put inworldz in the name
                }
                var regex = new Regex(@"\s");
                if (regex.IsMatch(firstName) || regex.IsMatch(lastName))
                {
                    reason = "Invalid name: The name cannot contain whitespace.";
                    return UUID.Zero;
                }

                lock (m_bots)
                {
                    if (m_bots.Count + 1 > m_maxNumberOfBots)
                    {
                        reason = "The maximum number of bots has been reached.";
                        return UUID.Zero;
                    }
                }

                UserProfileData userCheckData = m_scene.CommsManager.UserService.GetUserProfile(firstName, lastName);
                if (userCheckData != null)
                {
                    reason = "Invalid name: This name has already been taken.";
                    return UUID.Zero;//You cannot use the same name as another user.
                }
                if (!m_scene.Permissions.CanRezObject(0, owner, UUID.Zero, startPos, false))
                {
                    //Cannot create bot on a parcel that does not allow for rezzing an object
                    reason = "You do not have permissions to create a bot on this parcel.";
                    return UUID.Zero;
                }

                ILandObject parcel = m_scene.LandChannel.GetLandObject(startPos.X, startPos.Y);
                if (parcel == null)
                {
                    reason = "Land parcel could not be found at "+ ((int)startPos.X).ToString() + "," + ((int)startPos.Y).ToString();
                    return UUID.Zero;
                }

                ParcelPropertiesStatus status;
                if (parcel.DenyParcelAccess(owner, out status))
                {
                    reason = "You do not have permissions to create a bot on this parcel.";
                    return UUID.Zero;
                }

                AvatarAppearance appearance;
                UUID originalOwner = UUID.Zero;
                string ownerName = String.Empty;
                bool isSavedOutfit = !string.IsNullOrEmpty(outfitName);

                if (!isSavedOutfit)
                {
                    ScenePresence ownerSP = m_scene.GetScenePresence(owner);
                    if (ownerSP == null)
                    {
                        appearance = m_scene.CommsManager.AvatarService.GetUserAppearance(owner);
                        if (appearance == null)
                        {
                            reason = "No appearance could be found for the owner.";
                            return UUID.Zero;
                        }
                        ownerName = m_scene.CommsManager.UserService.Key2Name(owner,false);
                        if (String.IsNullOrEmpty(ownerName))
                        {
                            reason = "Owner could not be found.";
                            return UUID.Zero;
                        }
                    }
                    else
                    {
                        //Do checks here to see whether the appearance can be saved
                        if (!CheckAppearanceForAttachmentCount(ownerSP))
                        {
                            reason = "The outfit has too many attachments and cannot be used.";
                            return UUID.Zero;
                        }

                        appearance = new AvatarAppearance(ownerSP.Appearance);
                        ownerName = ownerSP.Name;
                    }
                }
                else
                {
                    appearance = m_scene.CommsManager.AvatarService.GetBotOutfit(owner, outfitName);
                    if (appearance == null)
                    {
                        reason = "No such outfit could be found.";
                        return UUID.Zero;
                    }
                    ownerName = m_scene.CommsManager.UserService.Key2Name(owner, false);
                    if (String.IsNullOrEmpty(ownerName))
                    {
                        reason = "Owner could not be found.";
                        return UUID.Zero;
                    }
                }

                BotClient client = new BotClient(firstName, lastName, m_scene, startPos, owner);

                // Replace all wearables and attachments item IDs with new ones so that they cannot be found in the
                // owner's avatar appearance in case the user is in the same region, wearing some of the same items.
                RemapWornItems(client.AgentID, appearance);

                if (!CheckAttachmentCount(client.AgentID, appearance, parcel, appearance.Owner, out reason))
                {
                    //Too many objects already on this parcel/region
                    return UUID.Zero;
                }

                originalOwner = appearance.Owner;
                appearance.Owner = client.AgentID;
                appearance.IsBotAppearance = true;
                string defaultAbout = "I am a 'bot' (a scripted avatar), owned by " + ownerName + ".";

                AgentCircuitData data = new AgentCircuitData()
                {
                    AgentID = client.AgentId,
                    Appearance = appearance,
                    BaseFolder = UUID.Zero,
                    child = false,
                    CircuitCode = client.CircuitCode,
                    ClientVersion = String.Empty,
                    FirstName = client.FirstName,
                    InventoryFolder = UUID.Zero,
                    LastName = client.LastName,
                    SecureSessionID = client.SecureSessionId,
                    SessionID = client.SessionId,
                    startpos = startPos
                };

                // Now that we're ready to add this bot, one last name check inside the lock.
                lock (m_bots)
                {
                    if (GetBot(firstName, lastName) != null)
                    {
                        reason = "Invalid name: A bot with this name already exists.";
                        return UUID.Zero;//You cannot use the same name as another bot.
                    }
                    m_bots.Add(client.AgentId, client);
                }

                m_scene.ConnectionManager.NewConnection(data, Region.Framework.Connection.EstablishedBy.Login);
                m_scene.AddNewClient(client, true);
                m_scene.ConnectionManager.TryAttachUdpCircuit(client);

                ScenePresence sp;
                if (m_scene.TryGetAvatar(client.AgentId, out sp))
                {
                    sp.IsBot = true;
                    sp.OwnerID = owner;

                    m_scene.CommsManager.UserService.AddTemporaryUserProfile(new UserProfileData()
                    {
                        AboutText = defaultAbout,
                        Created = Util.ToUnixTime(client.TimeCreated),
                        CustomType = "Bot",
                        Email = String.Empty,
                        FirstLifeAboutText = defaultAbout,
                        FirstLifeImage = UUID.Zero,
                        FirstName = client.FirstName,
                        GodLevel = 0,
                        ID = client.AgentID,
                        Image = UUID.Zero,
                        ProfileURL = String.Empty,
                        SurName = client.LastName,
                    });

                    sp.CompleteMovement();

                    new Thread(() =>
                    {
                        InitialAttachmentRez(sp, appearance.GetAttachments(), originalOwner, isSavedOutfit);
                    }).Start();

                    BotRegisterForPathUpdateEvents(client.AgentID, itemID, owner);

                    reason = null;
                    return client.AgentId;
                }
                reason = "Something went wrong.";
            }
            catch (Exception ex)
            {
                reason = "Something went wrong: " + ex.ToString();
            }
            return UUID.Zero;
        }

        private void RemapWornItems(UUID botID, AvatarAppearance appearance)
        {
            // save before Clear calls
            List<AvatarWearable> wearables = appearance.GetWearables();
            List<AvatarAttachment> attachments = appearance.GetAttachments();
            appearance.ClearWearables();
            appearance.ClearAttachments();

            // Remap bot outfit with new item IDs
            foreach (AvatarWearable w in wearables)
            {
                AvatarWearable newWearable = new AvatarWearable(w);
                // store a reversible back-link to the original inventory item ID.
                newWearable.ItemID = w.ItemID ^ botID;
                appearance.SetWearable(newWearable);
            }

            foreach (AvatarAttachment a in attachments)
            {
                // store a reversible back-link to the original inventory item ID.
                UUID itemID = a.ItemID ^ botID;
                appearance.SetAttachment(a.AttachPoint, true, itemID, a.AssetID);
            }
        }

        public bool CheckAttachmentCount(UUID botID, AvatarAppearance appearance, ILandObject parcel, UUID originalOwner, out string reason)
        {
            int landImpact = 0;
            foreach (AvatarAttachment attachment in appearance.GetAttachments())
            {
                // get original itemID
                UUID origItemID = attachment.ItemID ^ botID;
                if (origItemID == UUID.Zero)
                    continue;

                IInventoryProviderSelector inventorySelect = ProviderRegistry.Instance.Get<IInventoryProviderSelector>();
                var provider = inventorySelect.GetProvider(originalOwner);
                InventoryItemBase item = provider.GetItem(origItemID);

                SceneObjectGroup grp = m_scene.GetObjectFromItem(item);
                if (grp != null)
                {
                    if ((grp.GetEffectivePermissions(true) & (uint)PermissionMask.Copy) != (uint)PermissionMask.Copy)
                        continue;//No copy objects cannot be attached
                    landImpact += grp.LandImpact;
                }
            }

            if (!m_scene.CheckLandImpact(parcel, landImpact, out reason))
            {
                //Cannot create bot on a parcel that does not allow for rezzing an object
                reason = "Attachments exceed the land impact limit for this " + reason + ".";
                return false;
            }

            reason = null;
            return true;
        }

        public void InitialAttachmentRez(ScenePresence sp, List<AvatarAttachment> attachments, UUID originalOwner, bool isSavedOutfit)
        {
            ScenePresence ownerSP = null;

            //retrieve all attachments
            sp.Appearance.ClearAttachments();

            foreach (AvatarAttachment attachment in attachments)
            {
                UUID origItemID = attachment.ItemID ^ sp.UUID;
                if (origItemID == UUID.Zero)
                    continue;

                // Are we already attached?
                if (sp.Appearance.GetAttachmentForItem(origItemID) == null)
                {
                    IInventoryProviderSelector inventorySelect = ProviderRegistry.Instance.Get<IInventoryProviderSelector>();
                    var provider = inventorySelect.GetProvider(originalOwner);
                    InventoryItemBase item = provider.GetItem(origItemID);
                    if ((item.CurrentPermissions & (uint)PermissionMask.Copy) != (uint)PermissionMask.Copy)
                    {
                        if (ownerSP == null)
                            ownerSP = m_scene.GetScenePresence(originalOwner);
                        if (ownerSP != null)
                            ownerSP.ControllingClient.SendAgentAlertMessage("Bot cannot wear no-copy attachment: '" + item.Name + "'.", true);
                        continue;//No copy objects cannot be attached
                    }

                    // sp.ControllingClient can go null on botRemoveBot from another script
                    IClientAPI remoteClient = sp.ControllingClient; // take a reference and use
                    if (remoteClient == null)
                        return;

                    SceneObjectGroup grp = m_scene.RezObject(remoteClient, remoteClient.ActiveGroupId,
                        origItemID, Vector3.Zero, Vector3.Zero, UUID.Zero, (byte)1, true,
                        false, false, sp.UUID, true, (uint)attachment.AttachPoint, 0, null, item, false);

                    if (grp != null)
                    {
                        grp.OwnerID = sp.UUID; //Force UUID to botID, this should probably update the parts too, but right now they are separate
                        grp.SetOwnerId(grp.OwnerID);  // let's do this for safety and consistency. In case childpart.OwnerID is referenced, or rootpart.OwnerID
                        bool tainted = false;
                        if (attachment.AttachPoint != 0 && attachment.AttachPoint != grp.GetBestAttachmentPoint())
                            tainted = true;

                        m_scene.SceneGraph.AttachObject(remoteClient, grp.LocalId, (uint)attachment.AttachPoint, true, false, AttachFlags.None);

                        if (tainted)
                            grp.HasGroupChanged = true;

                        // Fire after attach, so we don't get messy perms dialogs
                        //
                        grp.CreateScriptInstances(0, ScriptStartFlags.PostOnRez, m_scene.DefaultScriptEngine, (int)ScriptStateSource.PrimData, null);

                        attachment.AttachPoint = grp.RootPart.AttachmentPoint;
                        UUID assetId = grp.UUID;
                        sp.Appearance.SetAttachment((int)attachment.AttachPoint, true, attachment.ItemID, assetId);
                        grp.DisableUpdates = false;
                        grp.ScheduleGroupForFullUpdate(PrimUpdateFlags.ForcedFullUpdate);
                    }
                }
            }
        }

        private IBot GetBot(UUID botID)
        {
            IBot bot;
            lock (m_bots)
            {
                if (!m_bots.TryGetValue(botID, out bot))
                    return null;
                return bot;
            }
        }

        public IBot GetBot(string first, string last)
        {
            string testName = String.Format("{0} {1}", first, last).ToLower();
            List<UUID> ownedBots = new List<UUID>();
            lock (m_bots)
            {
                foreach (IBot bot in m_bots.Values)
                {
                    if (bot.Name.ToLower() == testName)
                        return bot;
                }
            }
            return null;
        }

        private IBot GetBotWithPermission(UUID botID, UUID attemptingUser)
        {
            IBot bot;
            if ((bot = GetBot(botID)) == null || !CheckPermission(bot, attemptingUser))
                return null;
            return bot;
        }

        public bool RemoveBot(UUID botID, UUID attemptingUser)
        {
            lock (m_bots)
            {
                if (GetBotWithPermission(botID, attemptingUser) == null)
                    return false;
                m_bots.Remove(botID);
            }

            m_scene.IncomingCloseAgent(botID);
            m_scene.CommsManager.UserService.RemoveTemporaryUserProfile(botID);
            return true;
        }

        public UUID GetBotOwner(UUID botID)
        {
            IBot bot;
            if ((bot = GetBot(botID)) == null)
                return UUID.Zero;

            return bot.OwnerID;
        }

        public bool IsBot(UUID userID)
        {
            return GetBot(userID) != null;
        }

        public string GetBotName(UUID botID)
        {
            IBot bot;
            if ((bot = GetBot(botID)) == null)
                return String.Empty;

            return bot.Name;
        }

        public bool ChangeBotOwner(UUID botID, UUID newOwnerID, UUID attemptingUser)
        {
            //This method is now unsupported
            return false;
            /*IBot bot;
            if (!m_bots.TryGetValue(botID, out bot) || !CheckPermission(bot, attemptingUser))
                return false;

            bot.OwnerID = newOwnerID;
            return true;*/
        }

        public bool SetBotProfile(UUID botID, string aboutText, string email, UUID? imageUUID, string profileURL, UUID attemptingUser)
        {
            IBot bot;
            if ((bot = GetBotWithPermission(botID, attemptingUser)) == null)
                return false;

            UserProfileData profile = m_scene.CommsManager.UserService.GetUserProfile(botID);
            if (profile == null)
                return false;

            m_scene.CommsManager.UserService.AddTemporaryUserProfile(new UserProfileData() 
            { 
                AboutText = aboutText ?? profile.AboutText,
                Created = Util.ToUnixTime(bot.TimeCreated),
                CustomType = "Bot",
                Email = email ?? profile.Email,
                FirstLifeAboutText = profile.FirstLifeAboutText,
                FirstLifeImage = UUID.Zero,
                FirstName = bot.FirstName,
                GodLevel = 0,
                ID = botID,
                Image = imageUUID.HasValue ? imageUUID.Value : profile.Image,
                ProfileURL = profileURL ?? profile.ProfileURL,
                SurName = bot.LastName,
            });

            return true;
        }

        public List<UUID> GetAllBots()
        {
            lock (m_bots)
                return m_bots.Values.Select((b) => b.AgentID).ToList();
        }

        public List<UUID> GetAllOwnedBots(UUID attemptingUser)
        {
            List<UUID> ownedBots = new List<UUID>();
            lock (m_bots)
            {
                foreach (IBot bot in m_bots.Values)
                {
                    if (CheckPermission(bot, attemptingUser))
                        ownedBots.Add(bot.AgentID);
                }
            }
            return ownedBots;
        }

        #endregion

        #region Bot Outfit Management

        public bool SaveOutfitToDatabase(UUID userID, string outfitName, out string reason)
        {
            ScenePresence sp = m_scene.GetScenePresence(userID);
            if (sp == null)
            {
                reason = "The owner of this script must be in the sim to create an outfit.";
                return false;
            }

            //Do checks here to see whether the appearance can be saved
            if (!CheckAppearanceForAttachmentCount(sp))
            {
                reason = "The outfit has too many attachments and cannot be saved.";
                return false;
            }

            reason = null;

            m_scene.CommsManager.AvatarService.AddOrUpdateBotOutfit(userID, outfitName, sp.Appearance);
            return true;
        }

        /// <summary>
        /// Check whether the attachments of the given presence are too heavy
        /// to be allowed to be used in an outfit
        /// </summary>
        /// <param name="presence"></param>
        /// <returns></returns>
        private bool CheckAppearanceForAttachmentCount(ScenePresence presence)
        {
            int totalAttachmentLandImpact = 0;
            foreach(SceneObjectGroup grp in presence.GetAttachments())
            {
                totalAttachmentLandImpact += grp.LandImpact;
            }
            return totalAttachmentLandImpact < m_maxLandImpactAllowedByOutfitAttachments;
        }

        public void RemoveOutfitFromDatabase(UUID userID, string outfitName)
        {
            m_scene.CommsManager.AvatarService.RemoveBotOutfit(userID, outfitName);
        }

        public bool ChangeBotOutfit(UUID botID, string outfitName, UUID attemptingUser, out string reason)
        {
            IBot bot;
            if ((bot = GetBotWithPermission(botID, attemptingUser)) == null)
            {
                reason = "Could not find bot with the given UUID.";
                return false;
            }

            ScenePresence sp;
            if (m_scene.TryGetAvatar(bot.AgentID, out sp))
            {
                AvatarAppearance appearance;
                UUID originalOwner = UUID.Zero;
                if (string.IsNullOrEmpty(outfitName))
                {
                    ScenePresence ownerSP = m_scene.GetScenePresence(attemptingUser);
                    if (ownerSP == null)
                    {
                        reason = "No appearance could be found for the owner.";
                        return false;
                    }

                    //Do checks here to see whether the appearance can be saved
                    if (!CheckAppearanceForAttachmentCount(ownerSP))
                    {
                        reason = "The outfit has too many attachments and cannot be used.";
                        return false;
                    }

                    appearance = new AvatarAppearance(ownerSP.Appearance);
                }
                else
                {
                    appearance = m_scene.CommsManager.AvatarService.GetBotOutfit(attemptingUser, outfitName);
                    if (appearance == null)
                    {
                        reason = "No such outfit could be found.";
                        return false;
                    }
                }

                Vector3 pos = sp.AbsolutePosition;
                ILandObject parcel = m_scene.LandChannel.GetLandObject(pos.X, pos.Y);
                if (parcel == null)
                {
                    reason = "Land parcel could not be found at " + ((int)pos.X).ToString() + "," + ((int)pos.Y).ToString();
                    return false;
                }

                // Replace all wearables and attachments item IDs with new ones so that they cannot be found in the
                // owner's avatar appearance in case the user is in the same region, wearing some of the same items.
                RemapWornItems(bot.AgentID, appearance);     // allocate temp IDs for the new outfit.

                if (!CheckAttachmentCount(bot.AgentID, appearance, parcel, appearance.Owner, out reason))
                {
                    //Too many objects already on this parcel/region
                    return false;
                }
                reason = null;

                originalOwner = appearance.Owner;
                appearance.Owner = bot.AgentID;
                appearance.IsBotAppearance = true;

                sp.Appearance = appearance;
                if (appearance.AvatarHeight > 0)
                    sp.SetHeight(appearance.AvatarHeight);
                sp.SendInitialData();

                List<AvatarAttachment> attachments = sp.Appearance.GetAttachments();
                foreach (SceneObjectGroup group in sp.GetAttachments())
                {
                    sp.Appearance.DetachAttachment(group.GetFromItemID());
                    group.DetachToInventoryPrep();
                    m_scene.DeleteSceneObject(group, false);
                }

                new Thread(() =>
                {
                    InitialAttachmentRez(sp, attachments, originalOwner, true);
                }).Start();

                return true;
            }

            reason = "Could not find bot with the given UUID.";
            return false;
        }

        public List<string> GetBotOutfitsByOwner(UUID userID)
        {
            return m_scene.CommsManager.AvatarService.GetBotOutfitsByOwner(userID);
        }

        #endregion

        #region Bot Movement

        public BotMovementResult StartFollowingAvatar(UUID botID, UUID avatarID, Dictionary<int, object> options, UUID attemptingUser)
        {
            IBot bot;
            if ((bot = GetBotWithPermission(botID, attemptingUser)) == null)
                return BotMovementResult.BotNotFound;

            if (m_scene.GetScenePresence(avatarID) == null)
                return BotMovementResult.UserNotFound;

            bot.MovementController.StartFollowingAvatar(avatarID, options);
            return BotMovementResult.Success;
        }

        public BotMovementResult SetBotNavigationPoints(UUID botID, List<Vector3> positions, List<TravelMode> modes, Dictionary<int, object> options, UUID attemptingUser)
        {
            IBot bot;
            if ((bot = GetBotWithPermission(botID, attemptingUser)) == null)
                return BotMovementResult.BotNotFound;

            bot.MovementController.StartNavigationPath(positions, modes, options);
            return BotMovementResult.Success;
        }

        public BotMovementResult WanderWithin(UUID botID, Vector3 origin, Vector3 distances, Dictionary<int, object> options, UUID attemptingUser)
        {
            IBot bot;
            if ((bot = GetBotWithPermission(botID, attemptingUser)) == null)
                return BotMovementResult.BotNotFound;

            bot.MovementController.StartWandering(origin, distances, options);
            return BotMovementResult.Success;
        }

        public bool StopMovement(UUID botID, UUID attemptingUser)
        {
            IBot bot;
            if ((bot = GetBotWithPermission(botID, attemptingUser)) == null)
                return false;

            if (!bot.MovementController.MovementInProgress)
                return false;

            bot.MovementController.StopMovement();
            return true;
        }

        public bool PauseBotMovement(UUID botID, UUID attemptingUser)
        {
            IBot bot;
            if ((bot = GetBotWithPermission(botID, attemptingUser)) == null)
                return false;

            if (!bot.MovementController.MovementInProgress)
                return false;

            bot.MovementController.PauseMovement();
            return true;
        }

        public bool ResumeBotMovement(UUID botID, UUID attemptingUser)
        {
            IBot bot;
            if ((bot = GetBotWithPermission(botID, attemptingUser)) == null)
                return false;

            if (!bot.MovementController.MovementInProgress)
                return false;

            bot.MovementController.ResumeMovement();
            return true;
        }

        public bool SetBotSpeed(UUID botID, float speed, UUID attemptingUser)
        {
            IBot bot;
            if ((bot = GetBotWithPermission(botID, attemptingUser)) == null)
                return false;

            return bot.SetSpeed(speed);
        }

        public Vector3 GetBotPosition(UUID botID, UUID attemptingUser)
        {
            if (GetBot(botID) == null)
                return Vector3.Zero;

            ScenePresence sp = m_scene.GetScenePresence(botID);
            if (sp == null)
                return Vector3.Zero;
            return sp.AbsolutePosition;
        }

        public bool SetBotPosition(UUID botID, Vector3 position, UUID attemptingUser)
        {
            if (GetBotWithPermission(botID, attemptingUser) == null)
                return false;

            ScenePresence sp = m_scene.GetScenePresence(botID);
            if (sp == null)
                return false;

            sp.StandUp(false, true);
            sp.Teleport(position);
            return true;
        }

        public bool SetBotRotation(UUID botID, Quaternion rotation, UUID attemptingUser)
        {
            if (GetBotWithPermission(botID, attemptingUser) == null)
                return false;

            ScenePresence sp = m_scene.GetScenePresence(botID);
            if (sp == null)
                return false;

            if (sp.PhysicsActor != null)
            {
                EntityBase.PositionInfo posInfo = sp.GetPosInfo();
                posInfo.m_pos.Z += 0.001f;
                sp.SetAgentPositionInfo(null, false, posInfo.m_pos, posInfo.m_parent, Vector3.Zero, sp.Velocity);
            }
            sp.Rotation = rotation;
            sp.SendTerseUpdateToAllClients();
            return true;
        }

        #endregion

        #region Tag/Remove bots

        private readonly Dictionary<string, List<UUID>> m_botTags = new Dictionary<string, List<UUID>>();

        public bool AddTagToBot(UUID botID, string tag, UUID attemptingUser)
        {
            if (GetBotWithPermission(botID, attemptingUser) == null)
                return false;

            if (!m_botTags.ContainsKey(tag))
                m_botTags.Add(tag, new List<UUID>());
            m_botTags[tag].Add(botID);
            return true;
        }

        public bool BotHasTag(UUID botID, string tag)
        {
            if (m_botTags.ContainsKey(tag))
                return m_botTags[tag].Contains(botID);
            return false;
        }

        public List<string> GetBotTags(UUID botID)
        {
            List<string> ret = new List<string>();
            foreach (KeyValuePair<string, List<UUID>> tag in m_botTags)
                if (tag.Value.Contains(botID))
                    ret.Add(tag.Key);
            return ret;
        }

        public List<UUID> GetBotsWithTag(string tag)
        {
            if (!m_botTags.ContainsKey(tag))
                return new List<UUID>();
            return new List<UUID>(m_botTags[tag]);
        }

        public bool RemoveBotsWithTag(string tag, UUID attemptingUser)
        {
            List<UUID> bots = GetBotsWithTag(tag);
            bool success = true;
            foreach (UUID botID in bots)
            {
                if (GetBotWithPermission(botID, attemptingUser) == null)
                {
                    success = false;
                    continue;
                }

                RemoveTagFromBot(botID, tag, attemptingUser);
                RemoveBot(botID, attemptingUser);
            }
            return success;
        }

        public bool RemoveTagFromBot(UUID botID, string tag, UUID attemptingUser)
        {
            if (GetBotWithPermission(botID, attemptingUser) == null)
                return false;

            if (m_botTags.ContainsKey(tag))
                m_botTags[tag].Remove(botID);
            return true;
        }

        public bool RemoveAllTagsFromBot(UUID botID, UUID attemptingUser)
        {
            if (GetBotWithPermission(botID, attemptingUser) == null)
                return false;

            List<string> tagsToRemove = new List<string>();
            foreach (KeyValuePair<string, List<UUID>> kvp in m_botTags)
            {
                if (kvp.Value.Contains(botID))
                    tagsToRemove.Add(kvp.Key);
            }

            foreach (string tag in tagsToRemove)
                m_botTags[tag].Remove(botID);

            return true;
        }

        #endregion

        #region Region Interaction Methods

        public bool SitBotOnObject(UUID botID, UUID objectID, UUID attemptingUser)
        {
            IBot bot;
            if ((bot = GetBotWithPermission(botID, attemptingUser)) == null)
                return false;

            return bot.SitOnObject(objectID);
        }

        public bool StandBotUp(UUID botID, UUID attemptingUser)
        {
            IBot bot;
            if ((bot = GetBotWithPermission(botID, attemptingUser)) == null)
                return false;

            return bot.StandUp();
        }

        public bool BotTouchObject(UUID botID, UUID objectID, UUID attemptingUser)
        {
            IBot bot;
            if ((bot = GetBotWithPermission(botID, attemptingUser)) == null)
                return false;

            return bot.TouchObject(objectID);
        }

        public bool GiveInventoryObject(UUID botID, SceneObjectPart part, string objName, UUID objId, byte assetType, UUID destId, UUID attemptingUser)
        {
            IBot bot;
            if ((bot = GetBotWithPermission(botID, attemptingUser)) == null)
                return false;

            bot.GiveInventoryObject(part, objName, objId, assetType, destId);
            return true;
        }

        #endregion

        #region Bot Animation

        public bool StartBotAnimation(UUID botID, UUID animID, string anim, UUID objectID, UUID attemptingUser)
        {
            IBot bot;
            if ((bot = GetBotWithPermission(botID, attemptingUser)) == null)
                return false;

            return bot.StartAnimation(animID, anim, objectID);
        }

        public bool StopBotAnimation(UUID botID, UUID animID, string animation, UUID attemptingUser)
        {
            IBot bot;
            if ((bot = GetBotWithPermission(botID, attemptingUser)) == null)
                return false;

            return bot.StopAnimation(animID, animation);
        }

        #endregion

        #region Event Registration Methods

        public bool BotRegisterForPathUpdateEvents(UUID botID, UUID itemID, UUID attemptingUser)
        {
            IBot bot;
            if ((bot = GetBotWithPermission(botID, attemptingUser)) == null)
                return false;

            lock (bot.RegisteredScriptsForPathUpdateEvents)
            {
                if (!bot.RegisteredScriptsForPathUpdateEvents.Contains(itemID))
                {
                    bot.RegisteredScriptsForPathUpdateEvents.Add(itemID);
                    return true;
                }
            }
            return false;
        }

        public bool BotDeregisterFromPathUpdateEvents(UUID botID, UUID itemID, UUID attemptingUser)
        {
            IBot bot;
            if ((bot = GetBotWithPermission(botID, attemptingUser)) == null)
                return false;

            lock (bot.RegisteredScriptsForPathUpdateEvents)
            {
                if (bot.RegisteredScriptsForPathUpdateEvents.Contains(itemID))
                {
                    bot.RegisteredScriptsForPathUpdateEvents.Remove(itemID);
                    return true;
                }
            }
            return false;
        }

        public bool BotRegisterForCollisionEvents(UUID botID, SceneObjectGroup group, UUID attemptingUser)
        {
            if (GetBotWithPermission(botID, attemptingUser) == null)
                return false;

            ScenePresence botSP = m_scene.GetScenePresence(botID);
            if (botSP == null || group == null)
                return false;

            botSP.RegisterGroupToCollisionUpdates(group);
            return true;
        }

        public bool BotDeregisterFromCollisionEvents(UUID botID, SceneObjectGroup group, UUID attemptingUser)
        {
            if (GetBotWithPermission(botID, attemptingUser) == null)
                return false;

            ScenePresence botSP = m_scene.GetScenePresence(botID);
            if (botSP == null || group == null)
                return false;

            botSP.DeregisterGroupFromCollisionUpdates(group);
            return true;
        }

        #endregion

        #region Chat Methods

        public bool BotChat(UUID botID, int channel, string message, ChatTypeEnum sourceType, UUID attemptingUser)
        {
            IBot bot;
            if ((bot = GetBotWithPermission(botID, attemptingUser)) == null)
                return false;

            bot.Say(channel, message, sourceType);
            return true;
        }

        public bool SendInstantMessageForBot(UUID botID, UUID userID, string message, UUID attemptingUser)
        {
            IBot bot;
            if ((bot = GetBotWithPermission(botID, attemptingUser)) == null)
                return false;

            bot.SendInstantMessage(userID, message);
            return true;
        }

        #endregion

        #region Permissions Check

        public bool CheckPermission(UUID botID, UUID attemptingUser)
        {
            if (GetBotWithPermission(botID, attemptingUser) == null)
                return false;
            return true;
        }

        private bool CheckPermission(IBot bot, UUID attemptingUser)
        {
            if (attemptingUser == UUID.Zero)
                return true; //Forced override

            if (bot != null)
            {
                if (bot.OwnerID == attemptingUser || bot.OwnerID == UUID.Zero)
                    return true;
            }
            return false;
        }

        #endregion
    }
}
