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
using System.IO;

namespace InWorldz.ApplicationPlugins.ChatFilterModule
{
    /// <summary>
    /// Implements filtering of bad words in the text stream for IMs and chat
    /// 
    /// To enable, add 
    /// 
    /// [ChatFilterModule]
    ///     Enabled = true
    /// 
    /// into Halcyon.ini.
    /// </summary>
    public class ChatFilterModule : INonSharedRegionModule
    {
        #region Declares

        private static readonly log4net.ILog m_log
            = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private const string WORDFILTER_FILE = "wordfilterDictionary.txt";


        private bool m_enabled = false;

        /// <summary>
        /// The smallest word that was found in the list
        /// </summary>
        private int m_minWordLength = int.MaxValue;

        /// <summary>
        /// The bad words
        /// </summary>
        private string[] m_words;


        #endregion

        #region INonSharedRegionModule Members

        public string Name
        {
            get { return "ChatFilterModule"; }
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
                //load up words
                if (File.Exists(WORDFILTER_FILE))
                {
                    m_words = File.ReadAllLines(WORDFILTER_FILE);
                    foreach (var word in m_words)
                    {
                        m_minWordLength = Math.Min(m_minWordLength, word.Length);
                    }
                }
                else
                {
                    m_log.ErrorFormat("[WORDFILTER]: Word file {0} is missing! No words to load!", WORDFILTER_FILE);
                    m_enabled = false;
                }
            }

        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled) return;

            scene.EventManager.OnBeforeSendInstantMessage += EventManager_OnBeforeSendInstantMessage;
            scene.EventManager.OnChatFromClient += EventManager_OnChatFromClient;
        }

        void EventManager_OnChatFromClient(object sender, OSChatMessage chat)
        {
            if (!m_enabled) return;
            if (String.IsNullOrEmpty(chat.Message) || chat.SenderUUID == chat.DestinationUUID) return;

            if (chat.Message.Length < m_minWordLength) return; //too small, nothing to filter, dont waste time

            StringBuilder result = new StringBuilder(chat.Message);
            DoFilteringOnStatement(result);

            chat.Message = result.ToString();
        }

        private void DoFilteringOnStatement(StringBuilder result)
        {
            for (int i = 0; i < result.Length && i + m_minWordLength < result.Length; i += m_minWordLength)
            {
                ApplyWordFilterAtIndex(i, result);
            }
        }

        private void ApplyWordFilterAtIndex(int i, StringBuilder chat)
        {
            foreach (string word in m_words)
            {
                if (i + word.Length > chat.Length) continue;

                bool found = WordMatchesAtIndex(i, chat, word);

                if (found)
                {
                    ScrubWord(word.Length, i, chat);
                }
            }
        }

        private void ScrubWord(int wordLen, int idx, StringBuilder chat)
        {
            for (int j = 0; j < wordLen; j++)
            {
                chat[j + idx] = '*';
            }
        }

        private static bool WordMatchesAtIndex(int i, StringBuilder chat, string word)
        {
            bool found = true;
            for (int j = 0; j < word.Length; j++)
            {
                if (word[j] != chat[j + i])
                {
                    found = false;
                    break;
                }
            }
            return found;
        }

        /// <summary>
        /// Guests are not allowed to send instant messages
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        bool EventManager_OnBeforeSendInstantMessage(GridInstantMessage message)
        {
            if (!m_enabled) return true;
            if (String.IsNullOrEmpty(message.message) || message.fromAgentID == message.toAgentID) return true;
            if (message.message.Length < m_minWordLength) return true; //too small, nothing to filter, dont waste time

            StringBuilder result = new StringBuilder(message.message);
            DoFilteringOnStatement(result);

            message.message = result.ToString();

            return true;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled) return;

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
