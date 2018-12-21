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
using log4net;
using System.Reflection;
using System.Net;

namespace OpenSim.Framework
{
    /// <summary>
    /// Manages lists of trusted resources
    /// </summary>
    public class TrustManager
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const string IP_TRUST_FILE = "trustednetworks.txt";

        private object _lock = new object();
        private List<string> _goodIPMasks = new List<string>();
        private bool _trustAll = false;

        private static readonly string[] DEFAULT_TRUSTED_NETWORKS = new string[] 
        {
            "127.0.0.1",
            "192.168.",
            "10.0.",
            "198.61.240.",
            "198.61.243.",
            "198.61.156.",
            "198.61.135.",
            "166.78.72.",
            "166.78.66."
        };

        private static readonly TrustManager instance = new TrustManager();

        public TrustManager()
        {
            this.ReloadTrustLists();
        }

        public static TrustManager Instance
        {
            get
            {
                return instance;
            }
        }

        public void ReloadTrustLists()
        {
            lock (_lock)
            {
                _log.InfoFormat("[TRUSTMGR]: Reloading trust lists");
                this.ReloadNetworkTrustList();
            }
        }

        private void ReloadNetworkTrustList()
        {
            if (!System.IO.File.Exists(IP_TRUST_FILE))
            {
                this.CreateDefaultIPTrustFile();
            }

            // Read the file and display it line by line.
            using (System.IO.StreamReader file = new System.IO.StreamReader(IP_TRUST_FILE))
            {
                _goodIPMasks.Clear();
                _trustAll = false;

                string line;
                while ((line = file.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Equals("*"))
                    {
                        _log.InfoFormat("[TRUSTMGR]: Enabling trust wildcard for all IPs.");
                        _goodIPMasks.Clear();
                        _trustAll = true;
                        break;
                    }

                    if (!String.IsNullOrWhiteSpace(line))
                    {
                        _log.InfoFormat("[TRUSTMGR]: Added whitelist IP {0}", line);
                        _goodIPMasks.Add(line);
                    }
                }
            }
        }

        private void CreateDefaultIPTrustFile()
        {
            using (System.IO.StreamWriter file = new System.IO.StreamWriter(IP_TRUST_FILE))
            {
                foreach (string net in DEFAULT_TRUSTED_NETWORKS)
                {
                    file.WriteLine(net);
                }
            }
        }

        public bool IsTrustedPeer(IPEndPoint endPoint)
        {
            lock (_lock)
            {
                if (_trustAll) return true;
                string addr = endPoint.Address.ToString();
                foreach (string ipMask in _goodIPMasks)
                {
                    if (addr.StartsWith(ipMask))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public bool IsTrustedPeer(string peer)
        {
            lock (_lock)
            {
                if (_trustAll) return true;
                foreach (string ipMask in _goodIPMasks)
                {
                    if (peer.StartsWith(ipMask))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
