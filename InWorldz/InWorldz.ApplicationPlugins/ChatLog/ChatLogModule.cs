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

namespace InWorldz.ApplicationPlugins.ChatLog
{
    /// <summary>
    /// Implements a logging frontend that logs all outgoing messages to clients
    /// 
    /// To enable, add 
    /// 
    /// [ChatLogModule]
    ///     Enabled = true
    /// 
    /// into Halcyon.ini.
    /// </summary>
    public class InWorldzChatLogModule : INonSharedRegionModule
    {
        #region Declares

        //private static readonly log4net.ILog m_log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private bool m_enabled = false;
        private IChatMessageLogBackend m_backend = null;

        #endregion

        #region INonSharedRegionModule Members

        public string Name
        {
            get { return "ChatLogModule"; }
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
        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled) return;

            m_backend = ProviderRegistry.Instance.Get<IChatMessageLogBackend>();
            scene.EventManager.OnChatToClient += EventManager_OnChatToClient;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled) return;

            scene.EventManager.OnChatToClient -= EventManager_OnChatToClient;
        }

        public void RegionLoaded(Scene scene)
        {
        }

        #endregion

        #region Functionality

        void EventManager_OnChatToClient(string message, UUID fromID, UUID toID, UUID regionID, 
            uint timestamp, ChatToClientType type)
        {
            if (String.IsNullOrEmpty(message) || toID == fromID) return;

            //Create the log, then pass it to the backend
            ChatMessageLog log = new ChatMessageLog()
            {
                Message = message,
                ChatType = type,
                ToAgentID = toID,
                FromAgentID = fromID,
                RegionID = regionID,
                Timestamp = timestamp
            };

            m_backend.LogChatMessage(log);
        }

        #endregion
    }
}
