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
using System.Threading.Tasks;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.Avatar.Chat
{
    public class ChatModule : IRegionModule, IChatModule
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private const int DEBUG_CHANNEL = 2147483647;

        private bool m_enabled = true;
        private int m_saydistance = 30;
        private int m_shoutdistance = 100;
        private int m_whisperdistance = 10;
        private List<Scene> m_scenes = new List<Scene>();

        internal object m_syncInit = new object();

        #region IRegionModule Members
        public virtual void Initialize(Scene scene, IConfigSource config)
        {
            // wrap this in a try block so that defaults will work if
            // the config file doesn't specify otherwise.
            try
            {
                m_enabled = config.Configs["Chat"].GetBoolean("enabled", m_enabled);
                if (!m_enabled) return;

                m_whisperdistance = config.Configs["Chat"].GetInt("whisper_distance", m_whisperdistance);
                m_saydistance = config.Configs["Chat"].GetInt("say_distance", m_saydistance);
                m_shoutdistance = config.Configs["Chat"].GetInt("shout_distance", m_shoutdistance);
            }
            catch (Exception)
            {
            }

            lock (m_syncInit)
            {
                if (!m_scenes.Contains(scene))
                {
                    m_scenes.Add(scene);
                    scene.EventManager.OnNewClient += OnNewClient;
                    scene.EventManager.OnChatFromWorld += OnChatFromWorld;
                    scene.EventManager.OnChatBroadcast += OnChatBroadcast;
                    scene.RegisterModuleInterface<IChatModule>(this);
                }
            }

            m_log.InfoFormat("[CHAT]: Initialized for {0} w:{1} s:{2} S:{3}", scene.RegionInfo.RegionName,
                             m_whisperdistance, m_saydistance, m_shoutdistance);
        }
        
        public virtual void PostInitialize()
        {
        }

        public virtual void Close()
        {
        }

        public virtual string Name
        {
            get { return "ChatModule"; }
        }

        public virtual bool IsSharedModule
        {
            get { return true; }
        }

        #endregion


        public virtual void OnNewClient(IClientAPI client)
        {
            client.OnChatFromClient += OnChatFromClient;
        }

        private async Task<bool> PrefilterChat(Object sender, OSChatMessage c)
        {
            if (c.Type != ChatTypeEnum.Say)
                return false;

            string[] separators = {" ", "\t"};
            string[] args = c.Message.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            if (args.Length < 1)
                return false;

            if (args[0].Length < 3)
                return false;
            if (args[0].Substring(0, 2) != "/!")
                return false;

            if (sender is IClientAPI)
            {
                await HandleChatCommand((IClientAPI)sender, c, args);
                return true;
            }

            return false;
        }

        public virtual async void OnChatFromClient(object sender, OSChatMessage c)
        {
            await OnChatFromClientInternal(sender, c);
        }

        public virtual async Task OnChatFromClientInternal(object sender, OSChatMessage c)
        {
            if (await PrefilterChat(sender, c))
                return;

            // redistribute to interested subscribers
            Scene scene = (Scene)c.Scene;
            scene.EventManager.TriggerOnChatFromClient(sender, c);

            // early return if not on public or debug channel
            if (c.Channel != 0 && c.Channel != DEBUG_CHANNEL) return;

            // sanity check:
            if (c.SenderUUID == UUID.Zero)
            {
                m_log.ErrorFormat("[CHAT] OnChatFromClient from {0} has empty Sender field!", sender);
                return;
            }

            DeliverChatToAvatars(ChatSourceType.Agent, c);
        }

        public virtual void OnChatFromWorld(Object sender, OSChatMessage c)
        {
            // early return if not on public or debug channel
            if (c.Channel != 0 && c.Channel != DEBUG_CHANNEL) return;

            DeliverChatToAvatars(ChatSourceType.Object, c);
        }

        protected virtual void DeliverChatToAvatars(ChatSourceType sourceType, OSChatMessage c)
        {
            string fromName = c.From;
            UUID fromID = UUID.Zero;
            UUID ownerID = c.GeneratingAvatarID;
            UUID destID = c.DestinationUUID;
            string message = c.Message;
            IScene scene = c.Scene;
            Vector3 fromPos = c.Position;
            Vector3 regionPos = new Vector3(scene.RegionInfo.RegionLocX * Constants.RegionSize,
                                            scene.RegionInfo.RegionLocY * Constants.RegionSize, 0);

            if (c.Channel == DEBUG_CHANNEL) c.Type = ChatTypeEnum.DebugChannel;

            switch (sourceType) 
            {
                case ChatSourceType.Agent:
                    if (!(scene is Scene))
                    {
                        m_log.WarnFormat("[CHAT]: scene {0} is not a Scene object, cannot obtain scene presence for {1}",
                            scene.RegionInfo.RegionName, c.SenderUUID);
                        return;
                    }
                    ScenePresence avatar;
                    if((scene as Scene).TryGetAvatar(c.SenderUUID, out avatar))
                    {
                        fromPos = avatar.AbsolutePosition;
                        fromName = avatar.Name;
                        fromID = c.SenderUUID;
                        ownerID = c.SenderUUID;
                    }
                    else
                    {
                        m_log.WarnFormat("[CHAT]: cannot obtain scene presence for {0}",
                            c.SenderUUID);
                        return;
                    }

                    break;

                case ChatSourceType.Object:
                    fromID = c.SenderUUID;
                break;
            }

            // TODO: iterate over message
            if (message.Length >= 1000) // libomv limit
                message = message.Substring(0, 1000);

            // m_log.DebugFormat("[CHAT]: DCTA: fromID {0} fromName {1}, cType {2}, sType {3}", fromID, fromName, c.Type, sourceType);

            foreach (Scene s in m_scenes)
            {
                s.ForEachScenePresence(delegate(ScenePresence presence)
                    {
                                           
                        if (!presence.IsChildAgent)
                            if ((destID == UUID.Zero) || (destID == presence.UUID))
                                TrySendChatMessage(presence, fromPos, regionPos, fromID, fromName, ownerID,
                                    c.Type, message, sourceType);
                    });
            }
        }

        static private Vector3 CenterOfRegion = new Vector3(128, 128, 30);
        
        public virtual void OnChatBroadcast(Object sender, OSChatMessage c)
        {
            // unless the chat to be broadcast is of type Region, we
            // drop it if its channel is neither 0 nor DEBUG_CHANNEL
            if (c.Channel != 0 && c.Channel != DEBUG_CHANNEL && c.Type != ChatTypeEnum.Region) return;

            ChatTypeEnum cType = c.Type;
            if (c.Channel == DEBUG_CHANNEL)
                cType = ChatTypeEnum.DebugChannel;

            if (cType == ChatTypeEnum.Region)
                cType = ChatTypeEnum.Say;

            if (c.Message.Length > 1100)
                c.Message = c.Message.Substring(0, 1000);

            // broadcast chat works by redistributing every incoming chat
            // message to each avatar in the scene.
            string fromName = c.From;
            
            UUID fromID = UUID.Zero;
            UUID ownerID = c.GeneratingAvatarID;
            ChatSourceType sourceType = ChatSourceType.Object;
            ScenePresence avatar;
            if ((c.Scene as Scene).TryGetAvatar(c.SenderUUID, out avatar))
            {
                fromID = c.SenderUUID;
                fromName = avatar.Name;
                ownerID = c.SenderUUID;
                sourceType = ChatSourceType.Agent;
            } 
            else if (c.SenderUUID != UUID.Zero)
            {
                fromID = c.SenderUUID; 
            }

            // m_log.DebugFormat("[CHAT] Broadcast: fromID {0} fromName {1}, cType {2}, sType {3}", fromID, fromName, cType, sourceType);

            ((Scene)c.Scene).ForEachScenePresence(
                delegate(ScenePresence presence)
                {
                    // ignore chat from child agents
                    if (presence.IsChildAgent) return;
                    
                    IClientAPI client = presence.ControllingClient;
                    
                    // don't forward SayOwner chat from objects to
                    // non-owner agents
                    if ((c.Type == ChatTypeEnum.Owner) &&
                        (ownerID != client.AgentId))
                        return;

                    presence.Scene.EventManager.TriggerOnChatToClient(c.Message, fromID,
                        client.AgentId, presence.Scene.RegionInfo.RegionID, (uint)Util.UnixTimeSinceEpoch(), 
                        ChatToClientType.InworldChat);

                    client.SendChatMessage(c.Message, (byte)cType, CenterOfRegion, fromName, fromID, ownerID,
                                           (byte)sourceType, (byte)ChatAudibleLevel.Fully);
                });
        }


        public bool ShouldChatCrossRegions(ChatTypeEnum type)
        {
            switch (type)
            {
                case ChatTypeEnum.Whisper:      return true;
                case ChatTypeEnum.Say:          return true;
                case ChatTypeEnum.Shout:        return true;
                case (ChatTypeEnum)3:           return true;    // 3 is an obsolete version of Say
                case ChatTypeEnum.StartTyping:  return false;
                case ChatTypeEnum.StopTyping:   return false;
                case ChatTypeEnum.DebugChannel: return false;
                case ChatTypeEnum.Region:       return false;
                case ChatTypeEnum.Owner:        return false;
                case ChatTypeEnum.Direct:       return false;   // llRegionSayTo
                case ChatTypeEnum.Broadcast:    return true;
            }
            return false;   // by default, new types will stay within the region
        }

        protected virtual void TrySendChatMessage(ScenePresence presence, Vector3 fromPos, Vector3 regionPos,
                                                  UUID fromAgentID, string fromName, UUID ownerID, ChatTypeEnum type,
                                                  string message, ChatSourceType src)
        {
            if ((presence.Scene.RegionInfo.RegionLocX != ((uint)regionPos.X) / Constants.RegionSize) ||
                (presence.Scene.RegionInfo.RegionLocY != ((uint)regionPos.Y) / Constants.RegionSize))
            {   // Different region?
                if (!ShouldChatCrossRegions(type))
                    return;
            }

            Vector3 fromRegionPos = fromPos + regionPos;
            Vector3 toRegionPos = presence.AbsolutePosition +
                new Vector3(presence.Scene.RegionInfo.RegionLocX * Constants.RegionSize,
                            presence.Scene.RegionInfo.RegionLocY * Constants.RegionSize, 0);

            // fix the exception that happens if 
            double fdist = Util.GetDistanceTo(toRegionPos, fromRegionPos);
            if (fdist > (double)Int32.MaxValue)
                return;
            if (fdist < -(double)Int32.MaxValue)
                return;
            int dis = Math.Abs((int) fdist);    // throws an exception on the cast if out of range
            if (type == ChatTypeEnum.Whisper && dis > m_whisperdistance ||
                type == ChatTypeEnum.Say && dis > m_saydistance ||
                type == ChatTypeEnum.Shout && dis > m_shoutdistance)
            {
                return;
            }

            presence.Scene.EventManager.TriggerOnChatToClient(message, fromAgentID,
                presence.UUID, presence.Scene.RegionInfo.RegionID, (uint)Util.UnixTimeSinceEpoch(), 
                ChatToClientType.InworldChat);
            // TODO: should change so the message is sent through the avatar rather than direct to the ClientView
            presence.ControllingClient.SendChatMessage(message, (byte) type, fromPos, fromName, fromAgentID,
                                                       ownerID, (byte)src, (byte)ChatAudibleLevel.Fully);
        }
        
        private SceneObjectPart FindObject(uint localID)
        {
            SceneObjectPart part = null;
            foreach (Scene scene in m_scenes)
            {
                part = scene.GetSceneObjectPart(localID);
                if (part != null)
                    break;
            }
            return part;
        }

        private SceneObjectPart FindObject(UUID id)
        {
            SceneObjectPart part = null;
            foreach (Scene scene in m_scenes)
            {
                part = scene.GetSceneObjectPart(id);
                if (part != null)
                    break;
            }
            return part;
        }

        private List<SceneObjectGroup> FindMatchingObjects(string name, UUID agentId)
        {
            List<SceneObjectGroup> results = new List<SceneObjectGroup>();

            string target = String.Empty;
            UUID targetID;
            if (!UUID.TryParse(name, out targetID))
            {
                targetID = UUID.Zero;
                target = name.Trim().ToLower();
                if (String.IsNullOrEmpty(target))
                    return results;
            }

            int count = 0;
            foreach (Scene scene in m_scenes)
            {
                bool includeAttachments = (agentId == UUID.Zero) ? false : scene.Permissions.IsGod(agentId);
                foreach (EntityBase ent in scene.Entities)
                {
                    if (ent is SceneObjectGroup)
                    {
                        SceneObjectGroup group = (SceneObjectGroup)ent;
                        if ((group.IsAttachment) && (!includeAttachments))
                                continue;
                        if (targetID != UUID.Zero)
                        {   // search by user
                            if (group.OwnerID == targetID)
                            {
                                results.Add(group);
                                count++;
                            }
                        }
                        else
                        {
                            // search by object name
                            if (group.Name.ToLower().Contains(target))
                            {
                                results.Add(group);
                                count++;
                            }
                        }
                        // Don't let this list build up to huge counts if someone searches for "e"...
                        if (count >= 100)
                            return results;
                    }
                }
            }

            return results;
        }

        private void SendClientChat(IClientAPI client, string format, params Object[] args)
        {
            Vector3 Pos = new Vector3(128.0f, 128.0f, 128.0f);  // close enough, we're sending to the viewer no matter what
            string msg = String.Format(format, args);
            ((Scene)client.Scene).EventManager.TriggerOnChatToClient(msg, UUID.Zero,
                client.AgentId, client.Scene.RegionInfo.RegionID, (uint)Util.UnixTimeSinceEpoch(),
                ChatToClientType.InworldChat);
            client.SendChatMessage(msg, (byte)ChatTypeEnum.Say, Pos, client.Name, UUID.Zero, UUID.Zero, 
                (byte)ChatSourceType.System, (byte)ChatAudibleLevel.Fully);
        }

        private void SendSystemChat(IClientAPI client, string format, params Object[] args)
        {
            Vector3 GodPos = new Vector3(128.0f, 128.0f, 128.0f);
            string msg = String.Format(format, args);
            ((Scene)client.Scene).EventManager.TriggerOnChatToClient(msg, UUID.Zero,
                client.AgentId, client.Scene.RegionInfo.RegionID, (uint)Util.UnixTimeSinceEpoch(),
                ChatToClientType.InworldChat);
            client.SendChatMessage(msg, (byte)ChatTypeEnum.Say, GodPos, "System", UUID.Zero, UUID.Zero, 
                (byte)ChatSourceType.System, (byte)ChatAudibleLevel.Fully);
        }

        private void DumpPart(IClientAPI client, Scene scene, SceneObjectPart part)
        {
            if (part == part.ParentGroup.RootPart)
            {
                SendSystemChat(client, "Object {0} [{1}] '{2}' at {3}{4}", part.LocalId.ToString(), part.UUID.ToString(), part.Name, Util.LocationShortCode(scene.RegionInfo.RegionName, part.AbsolutePosition), part.ParentGroup.IsAttachment ? " (attached)" : "");
            }
            else
            {
                SceneObjectPart root = part.ParentGroup.RootPart;
                SendSystemChat(client, "Child prim {0} [{1}] '{2}' at {3}", part.LocalId.ToString(), part.UUID.ToString(), part.Name, Util.LocationShortCode(scene.RegionInfo.RegionName, part.AbsolutePosition));
                SendSystemChat(client, "Root prim {0} [{1}] '{2}' at {3}{4}", root.LocalId.ToString(), root.UUID.ToString(), root.Name, Util.LocationShortCode(scene.RegionInfo.RegionName, part.AbsolutePosition), part.ParentGroup.IsAttachment ? " (attached)" : "");
            }
        }

        private void FindObjects(IClientAPI client, string[] args)
        {
            if (args.Length <= 1)
            {
                SendSystemChat(client, "Error: expected either a UUID or a local ID for an object or user.");
                return;
            }

            string arg = args[1];
            UUID uuid = UUID.Zero;
            uint localID = 0;

            if (!UUID.TryParse(arg, out uuid))
                uuid = UUID.Zero;

            if (uuid != UUID.Zero)  // see if it's a prim
            {
                foreach (Scene scene in m_scenes)
                {
                    SceneObjectPart part = scene.GetSceneObjectPart(uuid);
                    if (part != null)
                    {
                        // Allow the user to dump any part (including an attachment) if they specify the UUID.
                        DumpPart(client, scene, part);
                        return;
                    }
                }
            }

            if ((uuid==UUID.Zero) && uint.TryParse(arg, out localID))
            {
                foreach (Scene scene in m_scenes)
                {
                    SceneObjectPart part = scene.GetSceneObjectPart(localID);
                    if (part != null)
                    {
                        // Allow the user to dump any part (including an attachment) if they are the owner of the attachment.
                        if (part.IsAttachment && part.OwnerID != client.AgentId && !scene.Permissions.IsGod(client.AgentId))
                            SendSystemChat(client, "That ID specifies an attachment.");
                        else
                            DumpPart(client, scene, part);
                        return;
                    }
                }
                SendSystemChat(client, "Error: Could not find an part with local ID: " + localID.ToString());
                return;
            }

            List<SceneObjectGroup> results;
            if (uuid != UUID.Zero)  // find by user
            {
                results = FindMatchingObjects(uuid.ToString(), client.AgentId);
            }
            else
            {
                string objName = string.Join(" ", args, 1, args.Length - 1);
                results = FindMatchingObjects(objName, client.AgentId);
            }

            if (results.Count < 1)
            {
                SendSystemChat(client, "No matching objects found.");
            }
            else
            {
                SendSystemChat(client, "{0} matching objects found:", results.Count);
                foreach (SceneObjectGroup group in results)
                {
                    DumpPart(client, group.Scene, group.RootPart);
                }
            }
        }

        private void NukeObject(IClientAPI client, string[] args)
        {
            if (args.Length > 1)
            {
                string arg = args[1];
                UUID uuid = UUID.Zero;
                uint localID = 0;

                if (UUID.TryParse(arg, out uuid))
                {
                    foreach (Scene scene in m_scenes)
                    {
                        SceneObjectPart part = scene.GetSceneObjectPart(uuid);
                        if (part != null)
                        {
                            if (part.IsAttachment)
                                SendSystemChat(client, "That ID specifies an attachment.");
                            else
                            {
                                SceneObjectPart rootPart = part.ParentGroup.RootPart;
                                scene.DeRezObject(client, rootPart.LocalId, rootPart.GroupID, DeRezAction.Return, UUID.Zero);
                            }
                            return;
                        }
                    }
                    SendSystemChat(client, "Error: Could not find an part with UUID: {0}", uuid.ToString());
                    return;
                }
                if (uint.TryParse(arg, out localID))
                {
                    foreach (Scene scene in m_scenes)
                    {
                        SceneObjectPart part = scene.GetSceneObjectPart(localID);
                        if (part != null)
                        {
                            if (part.IsAttachment)
                                SendSystemChat(client, "That ID specifies an attachment.");
                            else
                            {
                                SceneObjectPart rootPart = part.ParentGroup.RootPart;
                                scene.DeRezObject(client, rootPart.LocalId, rootPart.GroupID, DeRezAction.Return, UUID.Zero);
                            }
                            return;
                        }
                    }
                    SendSystemChat(client, "Error: Could not find an part with local ID: " + localID.ToString());
                    return;
                }
                // fall thru since the error at the end fits here too.
            }

            SendSystemChat(client, "Error: expected either a UUID or a local ID for an object.");
        }

        private void ShowOffsimObjects(IClientAPI client)
        {
            uint count = 0;
            foreach (Scene scene in m_scenes)
            {
                foreach (EntityBase ent in scene.Entities)
                {
                    if (ent is SceneObjectGroup)
                    {
                        SceneObjectGroup group = (SceneObjectGroup)ent;
                        if (group.IsAttachment)
                            continue;

                        Vector3 pos = group.AbsolutePosition;
                        if ((pos.X < 0) || (pos.Y < 0) || (pos.X >= Constants.RegionSize) || (pos.Y >= Constants.RegionSize))
                        {
                            count++;
                            DumpPart(client, scene, group.RootPart);
                        }
                    }
                }
            }

            SendSystemChat(client, "{0} off-sim objects found:", count);
        }

        private void DebugCrossings(IClientAPI client)
        {
            bool newValue = !client.DebugCrossings;
            client.DebugCrossings = newValue;
            SendSystemChat(client, "Crossing debug messages are now: {0}", newValue ? "enabled" : "disabled");
        }

        private void ShowUpdates(IClientAPI client, string[] args)
        {
            string[] inArgs = { String.Empty, args[2] };
            foreach (Scene scene in m_scenes)
            {
                string output = scene.GetTopUpdatesOutput(inArgs);
                SendSystemChat(client, "{0}", output);
            }
            
        }

        private void ShowNeighborsRange(IClientAPI client, bool updated)
        {
            foreach (Scene scene in m_scenes)
            {
                SendSystemChat(client, "Neighbors range from {0} is {1}{2}.",
                    scene.RegionInfo.RegionName, updated?"now ":"", client.NeighborsRange);
            }
        }

        private bool NeighborsRange(IClientAPI client, string arg)
        {
            foreach (Scene scene in m_scenes)
            {
                try
                {
                    uint range = Convert.ToUInt32(arg);
                    if ((range == 1) || (range == 2))
                    {
                        client.NeighborsRange = range;
                        return true;
                    }
                }
                catch (Exception) {
                    SendClientChat(client, "Expected a neighbors range of 1 or 2.");
                }
            }
            return false;   // if we get here, it wasn't updated.
        }

        private async Task HandleChatCommand(IClientAPI client, OSChatMessage c, string[] args)
        {
            SendClientChat(client, "{0}", string.Join(" ",args));
            switch (args[0].ToLower())
            {
                case "/!find":
                    FindObjects(client, args);
                    break;

                case "/!nuke":
                    NukeObject(client, args);
                    break;

                case "/!show":
                    if (args.Length > 1)
                    {
                        if ((args.Length > 2) && (args[1] == "updates"))
                        {
                            ShowUpdates(client, args);
                        }
                        else
                        if ((args[1] == "offsim") || (args[1] == "off-sim"))
                        {
                            ShowOffsimObjects(client);
                        }
                    }
                    break;

                case "/!debug":
                    if (args.Length > 1)
                    {
                        if ((args[1] == "crossings") || (args[1] == "cross"))
                        {
                            DebugCrossings(client);
                            return;
                        }
                    }
                    SendSystemChat(client, "Region chat command was not recognized. Did you intend? /!debug crossings");
                    break;

                case "/!neighbor":  // lets allow some variations on the spelling etc. for 'muricans :p
                case "/!neighbors":
                case "/!neighbour":
                case "/!neighbours":
                    if ((args.Length > 1) && ((args[1] == "range") || (args[1] == "show")))
                    {
                        if (args.Length > 2)
                        {
                            if (NeighborsRange(client, args[2]))    // if changed
                            {
                                if ((client.Scene as Scene).TryGetAvatar(c.SenderUUID, out ScenePresence avatar))
                                {
                                    await avatar.UpdateForDrawDistanceChange();   // update the visible neighbors
                                }
                                ShowNeighborsRange(client, true);
                            }
                            break;
                        }
                    }
                    // else show the current value
                    ShowNeighborsRange(client, false);
                    break;

                default:
                    SendSystemChat(client, "Region chat command was not recognized.");
                    break;
            }
        }
    }
}
