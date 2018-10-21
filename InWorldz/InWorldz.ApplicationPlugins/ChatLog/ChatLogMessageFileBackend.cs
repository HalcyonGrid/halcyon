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
using OpenSim;
using Nini.Config;
using System.IO;

namespace InWorldz.ApplicationPlugins.ChatLog
{
    /// <summary>
    /// Implements a logging frontend that logs all messages to a file
    /// 
    /// To enable, add 
    /// 
    /// [ChatLogModule]
    ///     Backend = FileBackend
    ///     File    = Chat.log
    /// 
    /// into Halcyon.ini.
    /// </summary>
    public class InworldzChatLogMessageFileBackend : IApplicationPlugin, IChatMessageLogBackend
    {
        #region Declares

        //private static readonly log4net.ILog m_log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private bool m_enabled = false;
        private string m_fileName = "Chat.log";
        private TextWriter m_fileWriter = null;



        #endregion

        #region IApplicationPlugin Members

        public void Initialize(OpenSimBase openSim)
        {
            IConfig config = openSim.ConfigSource.Source.Configs["ChatLogModule"];
            if (config == null) return;

            m_enabled = config.GetString("Backend", String.Empty) == "FileBackend";
            m_fileName = config.GetString("File", m_fileName);

            if (m_enabled)
            {
                m_fileWriter = TextWriter.Synchronized(new StreamWriter(m_fileName, true));
                ProviderRegistry.Instance.RegisterInterface<IChatMessageLogBackend>(this);
            }
        }

        public void PostInitialize()
        {
        }

        public string Version
        {
            get { return "1.0.0"; }
        }

        public string Name
        {
            get { return "InworldzChatLogMessageFileBackend"; }
        }

        public void Initialize()
        {
        }

        public void Dispose()
        {
            if (!m_enabled) return;
            if (m_fileWriter != null)
                m_fileWriter.Close();
        }

        #endregion

        #region IChatMessageLogBackend Members

        public void LogChatMessage(ChatMessageLog log)
        {
            m_fileWriter.WriteLine(log.ToString());
            m_fileWriter.Flush();
        }

        #endregion
    }
}
