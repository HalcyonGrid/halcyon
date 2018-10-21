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
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Communications.Cache;
using System.Text.RegularExpressions;

namespace InWorldz.ApplicationPlugins.GuestModule
{
    /// <summary>
    /// Implements a guest functionality that removes certain rights from avatars that are logging in as guests
    /// 
    /// To enable, add 
    /// 
    /// [GuestModule]
    ///     Enabled = true
    /// 
    /// into Halcyon.ini.
    /// </summary>
    public class GuestModule : INonSharedRegionModule
    {
        #region Declares

        private static readonly log4net.ILog m_log
            = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        
        private bool m_enabled = false;
        private string m_guestRegionName;
        private string m_limitViewerString;

        private Scene m_scene;

        #endregion

        #region INonSharedRegionModule Members

        public string Name
        {
            get { return "GuestModule"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void Initialize(IConfigSource source)
        {
            IConfig config = source.Configs[Name];
            if (config == null) return;

            m_enabled = config.GetBoolean("Enabled", m_enabled);

            if (m_enabled)
            {
                m_guestRegionName = config.GetString("GuestRegionName", m_guestRegionName);

                if (m_guestRegionName == null)
                {
                    m_log.ErrorFormat("[GUESTMOD]: Guest region name is required");
                    throw new Exception("Guest region name is required");
                }

                m_limitViewerString = config.GetString("ViewerString", m_limitViewerString);
                if (m_limitViewerString != null)
                {
                    m_limitViewerString = m_limitViewerString.ToLower();
                }
            }
        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled) return;

            m_scene = scene;

            scene.EventManager.OnAuthorizeUser += EventManager_OnAuthorizeUser;
            scene.EventManager.OnBeforeSendInstantMessage += EventManager_OnBeforeSendInstantMessage;
            scene.EventManager.OnChatFromClient += EventManager_OnChatFromClient;
        }

        void EventManager_OnChatFromClient(object sender, OSChatMessage chat)
        {
            if (!m_enabled) return;
            if (String.IsNullOrEmpty(chat.Message) || chat.SenderUUID == chat.DestinationUUID) return;

            string lastName = m_scene.CommsManager.UserService.GetLastName(chat.SenderUUID, false);
            if (lastName == "Guest")
            {
                //scan for and remove hyperlinks
                //v2 recognizes .com, .org, and .net links with or without HTTP/S in them..
                //so we need a few regexes to cover the cases

                string noProto = "(\\bwww\\.\\S+\\.\\S+|(?<!@)\\b[^[:space:]:@/>]+\\.(?:com|net|edu|org)([/:][^[:space:]<]*)?\\b)";
                string withProto = "https?://([-\\w\\.]+)+(:\\d+)?(:\\w+)?(@\\d+)?(@\\w+)?/?\\S*";

                string replaced = Regex.Replace(chat.Message, withProto, String.Empty, RegexOptions.IgnoreCase);
                replaced = Regex.Replace(replaced, noProto, String.Empty, RegexOptions.IgnoreCase);

                chat.Message = replaced;
            }
        }

        /// <summary>
        /// Guests are not allowed to send instant messages
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        bool EventManager_OnBeforeSendInstantMessage(GridInstantMessage message)
        {
            if (!m_enabled) return true;
            if (string.IsNullOrEmpty(message.fromAgentName)) return true;
            if (!message.fromAgentName.Contains(' ')) return true;

            string[] name = message.fromAgentName.Split(new char[] {' '});
            if (name[1] == "Guest") return false;

            return true;
        }

        /// <summary>
        /// Guests are only allowed to visit a guest region
        /// </summary>
        /// <param name="agent"></param>
        /// <param name="reason"></param>
        /// <returns></returns>
        bool EventManager_OnAuthorizeUser(UUID agentID, string firstName, string lastName, string clientVersion, ref string reason)
        {
            if (!m_enabled) return true;

            if (lastName != "Guest") return true;
            if (m_limitViewerString != null && clientVersion != null && !clientVersion.ToLower().Contains(m_limitViewerString)) return false; //only the demolay viewer
            if (m_scene.RegionInfo.RegionName.Contains(m_guestRegionName)) return true; //only the guest region
            
            return false;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled) return;

            m_scene = null;

            scene.EventManager.OnAuthorizeUser -= EventManager_OnAuthorizeUser;
            scene.EventManager.OnBeforeSendInstantMessage -= EventManager_OnBeforeSendInstantMessage;
            scene.EventManager.OnChatFromClient -= EventManager_OnChatFromClient;
        }

        public void RegionLoaded(Scene scene)
        {
        }

        #endregion

        #region Functionality

        #endregion

        
    }
}
