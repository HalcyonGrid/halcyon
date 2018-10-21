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
using Cassandra;

namespace InWorldz.ApplicationPlugins.ChatLog
{
    /// <summary>
    /// Implements a logging frontend that logs all messages to a cassandra 1.2 cluster
    /// 
    /// To enable, add 
    /// 
    /// [ChatLogModule]
    ///     Backend = Cassandra12Backend
    ///     SeedNode1 = x.host.com
    ///     SeedNode2 = x.host.com
    ///     SeedNode3 = x.host.com
    ///     SeedNode4 = x.host.com
    ///     
    /// into Halcyon.ini.
    /// </summary>
    public class InworldzChatLogMessageCassandra12Backend : IApplicationPlugin, IChatMessageLogBackend
    {
        #region Declares

        private static readonly log4net.ILog m_log
            = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private const string KEYSPACE = "chatlog";
        private const Cassandra.ConsistencyLevel CONSISTENCY = ConsistencyLevel.Quorum;
        private const string Q_INSERT = "INSERT INTO messages(message_id, from_agent, to_agent, region_id, chat_type, sent_on, sent_on_day, message) VALUES(?,?,?,?,?,?,?,?) USING TTL ";
        private string Q_INS_TTL;

        private bool m_enabled = false;
        private bool m_debug = false;

        private List<string> m_seedNodes = new List<string>(4);
        
        private Cluster m_cluster;
        private Session m_session;

        private int m_ttl = 2592000; //1 month

        #endregion

        #region IApplicationPlugin Members

        public void Initialize(OpenSimBase openSim)
        {
            IConfig config = openSim.ConfigSource.Source.Configs["ChatLogModule"];
            if (config == null) return;

            m_enabled = config.GetBoolean("Enabled", false) && config.GetString("Backend", String.Empty) == "Cassandra12Backend";
            m_debug = config.GetBoolean("Debug", m_debug);
            m_ttl = config.GetInt("TTL", m_ttl);

            Q_INS_TTL = Q_INSERT + m_ttl.ToString() + ";";

            if (m_enabled)
            {
                ExtractSeedNodesFromConfig(config);

                var clusterb = Cluster.Builder();

                foreach (string node in m_seedNodes)
                {
                    if (m_debug) m_log.DebugFormat("[CHATLOG.Cassandra]: Adding seed node {0}", node);
                    clusterb.AddContactPoint(node);
                }

                clusterb.WithDefaultKeyspace(KEYSPACE);
                
                m_cluster = clusterb.Build();
                m_session = m_cluster.Connect();

                ProviderRegistry.Instance.RegisterInterface<IChatMessageLogBackend>(this);
            }
        }

        private void ExtractSeedNodesFromConfig(IConfig config)
        {
            const int MAX_SEED_NODES = 4;
            for (int i = 1; i < MAX_SEED_NODES + 1; i++)
            {
                string seed = config.GetString("SeedNode" + i.ToString(), String.Empty);
                if (!String.IsNullOrEmpty(seed))
                {
                    m_seedNodes.Add(seed);
                }
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
            get { return "InworldzChatLogMessageCassandra12Backend"; }
        }

        public void Initialize()
        {
        }

        public void Dispose()
        {
            if (!m_enabled) return;

            m_session.Dispose();
            m_cluster.Dispose();
        }

        #endregion

        #region IChatMessageLogBackend Members

        public void LogChatMessage(ChatMessageLog log)
        {
            /*
             *  CREATE TABLE messages (
             *       message_id uuid PRIMARY KEY,
             *       from_agent uuid,
             *       to_agent uuid,
             *       region_id uuid,
             *       chat_type int,
             *       sent_on int,
             *       sent_on_day timestamp, 
             *       message text
             *   );
             */
            

            try
            {
                var prep = m_session.Prepare(Q_INS_TTL);
                var bound = prep.Bind(new object[] { Guid.NewGuid(), log.FromAgentID.Guid, log.ToAgentID.Guid, log.RegionID.Guid, 
                        (int)log.ChatType, new DateTimeOffset(DateTime.Now), new DateTimeOffset(DateTime.Today), log.Message});

                var prepQuery = bound.SetConsistencyLevel(CONSISTENCY);

                if (m_debug) m_log.DebugFormat("[CHATLOG.Cassandra]: Executing {0}", prepQuery.ToString());
                m_session.Execute(prepQuery);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[CHATLOG]: Unable to write message to cluster: {0}", e);
            }
        }

        #endregion
    }
}
