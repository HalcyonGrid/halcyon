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
using Nini.Config;
using log4net;
using System.Reflection;

namespace InWorldz.Data.Assets.Stratus
{
    /// <summary>
    /// The asset client that is loaded by the plugin loader. This client then intelligently selects how
    /// to query the asset service(s) based on its configuration
    /// </summary>
    /// <remarks>
    ///    [InWorldz.Data.Assets.Stratus]
    ///        CFSupport = true
    ///        LegacySupport = true
    ///        WriteTarget = "WHIP"
    ///        WhipURL = "whip://password@localhost:32700"
    ///        CFDefaultRegion = "ORD"
    ///        CFUsername = "username"
    ///        CFAPIKey = "key"
    ///        CFContainerPrefix = "PREFIX_"
    ///        CFWorkerThreads = 8
    ///        CFUseInternalURL = true
    ///        CFCacheSize = 20971520
    /// </remarks>
    public class StratusAssetClient : IAssetServer, IAssetReceiver
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private CloudFilesAssetClient _cfAssetClient;
        private Whip.Client.AssetClient _whipAssetClient;

        private IAssetServer _firstReadServer;
        private IAssetServer _secondReadServer = null;
        private IAssetServer _writeServer;

        private IAssetReceiver _assetReceiver;

        public void Initialize(ConfigSettings settings)
        {
            IConfig stratusConfig = settings.SettingsFile["InWorldz.Data.Assets.Stratus"];
            if (stratusConfig != null && stratusConfig.GetBoolean("enabled", true))
            {
                Config.Settings.Instance.Enabled = true;
                Config.Settings.Instance.CFSupport = stratusConfig.GetBoolean("CFSupport", true);
                Config.Settings.Instance.LegacySupport = stratusConfig.GetBoolean("LegacySupport", false);
                Config.Settings.Instance.WhipURL = stratusConfig.GetString("WhipURL", null);
                Config.Settings.Instance.UseAsyncStore = stratusConfig.GetBoolean("UseAsyncStore", false);

                if (Config.Settings.Instance.LegacySupport == true && Config.Settings.Instance.WhipURL == null)
                {
                    //not allowed, we need a whip URL
                    throw new Exception("To enable stratus legacy asset support, you must include the WhipURL setting");
                }

                //used for testing. should be disabled except for unit tests
                Config.Settings.Instance.UnitTest_ThrowTimeout = false;
                Config.Settings.Instance.UnitTest_DeleteOldCacheFilesImmediately = false;

                Config.Settings.Instance.CFDefaultRegion = stratusConfig.GetString("CFDefaultRegion", null);
                Config.Settings.Instance.CFUsername = stratusConfig.GetString("CFUsername", null);
                Config.Settings.Instance.CFApiKey = stratusConfig.GetString("CFAPIKey", null);
                Config.Settings.Instance.CFContainerPrefix = stratusConfig.GetString("CFContainerPrefix", null);
                Config.Settings.Instance.CFWorkerThreads = stratusConfig.GetInt("CFWorkerThreads", 8);
                Config.Settings.Instance.CFUseInternalURL = stratusConfig.GetBoolean("CFUseInternalURL", true);
                Config.Settings.Instance.CFUseCache = stratusConfig.GetBoolean("CFUseCache", true);
                Config.Settings.Instance.CFCacheSize = stratusConfig.GetInt("CFCacheSize", 20 * 1024 * 1024);

                if (Config.Settings.Instance.CFSupport == true && 
                    (Config.Settings.Instance.CFUsername == null || Config.Settings.Instance.CFApiKey == null || 
                    Config.Settings.Instance.CFContainerPrefix == null || Config.Settings.Instance.CFDefaultRegion == null))
                {
                    //not allowed, we need the full cloudfiles auth information
                    throw new Exception("To enable stratus Cloud Files support, you must include the CFDefaultRegion, CFUsername, CFAPIKey, and CFContainerPrefix settings");
                }

                Config.Settings.Instance.WriteTarget = stratusConfig.GetString("WriteTarget", null);
                if (Config.Settings.Instance.CFSupport && Config.Settings.Instance.LegacySupport && Config.Settings.Instance.WriteTarget == null)
                {
                    throw new Exception("If both legacy and Cloud Files support is enabled, you must specify 'whip' or 'cf' in the WriteTarget setting");
                }

                Config.Settings.Instance.WriteTarget = Config.Settings.Instance.WriteTarget.ToLower();

                m_log.InfoFormat("[InWorldz.Stratus] Plugin is enabled");

                if (Config.Settings.Instance.LegacySupport)
                {
                    _whipAssetClient = new Whip.Client.AssetClient(Config.Settings.Instance.WhipURL);
                    _whipAssetClient.Initialize(settings);

                    if (Config.Settings.Instance.CFSupport)
                    {
                        //legacy and CF support.
                        _firstReadServer = _whipAssetClient; //whip is the first read server with legacy/cf since it has lower latency
                        if (Config.Settings.Instance.WriteTarget == "whip")
                        {
                            _writeServer = _whipAssetClient;
                        }
                    }
                    else
                    {
                        //just legacy
                        _firstReadServer = _whipAssetClient;
                        _writeServer = _whipAssetClient;
                    }
                }

                if (Config.Settings.Instance.CFSupport)
                {
                    _cfAssetClient = new CloudFilesAssetClient();
                    
                    if (Config.Settings.Instance.LegacySupport)
                    {
                        _secondReadServer = _cfAssetClient; //cf is the second read server when whip is enabled

                        //legacy and CF support
                        if (Config.Settings.Instance.WriteTarget == "cf")
                        {
                            _writeServer = _cfAssetClient;
                        }
                    }
                    else
                    {
                        _firstReadServer = _cfAssetClient; //first read server when only CF is enabled

                        //just CF
                        _writeServer = _cfAssetClient;
                    }
                }

                _firstReadServer.SetReceiver(this);
                if (_secondReadServer != null) _secondReadServer.SetReceiver(this);
            }
            else
            {
                Config.Settings.Instance.Enabled = false;
                m_log.InfoFormat("[InWorldz.Stratus] Plugin is disabled");
            }
        }

        public void Start()
        {
            if (_whipAssetClient != null)
            {
                _whipAssetClient.Start();
            }

            if (_cfAssetClient != null)
            {
                _cfAssetClient.Start();
            }
        }

        public void Stop()
        {
            if (_whipAssetClient != null)
            {
                _whipAssetClient.Stop();
            }

            if (_cfAssetClient != null)
            {
                _cfAssetClient.Stop();
            }
        }

        public void SetReceiver(IAssetReceiver receiver)
        {
            _assetReceiver = receiver;
        }

        public void RequestAsset(OpenMetaverse.UUID assetID, AssetRequestInfo args)
        {
            _firstReadServer.RequestAsset(assetID, args);
        }

        public AssetBase RequestAssetSync(OpenMetaverse.UUID assetID)
        {
            AssetBase asset;
            if (TryRequestAsset(assetID, _firstReadServer, out asset))
            {
                return asset;
            }

            if ((_secondReadServer != null) && TryRequestAsset(assetID, _secondReadServer, out asset))
            {
                return asset;
            }

            return null;
        }

        /// <summary>
        /// Tries to retrieve the asset with the given id
        /// </summary>
        /// <param name="assetID"></param>
        /// <param name="server"></param>
        /// <param name="asset"></param>
        /// <returns></returns>
        private bool TryRequestAsset(OpenMetaverse.UUID assetID, IAssetServer server, out AssetBase asset)
        {
            try
            {
                asset = server.RequestAssetSync(assetID);
            }
            catch (AssetServerException e)
            {
                asset = null;
                m_log.ErrorFormat("[InWorldz.Stratus]: Unable to retrieve asset {0}: {1}", assetID, e);
            }

            return asset != null;
        }

        public void StoreAsset(AssetBase asset)
        {
            _writeServer.StoreAsset(asset);
        }

        public void UpdateAsset(AssetBase asset)
        {
            m_log.WarnFormat("[InWorldz.Stratus]: UpdateAsset called for {0}  Assets are immutable.", asset.FullID);
            this.StoreAsset(asset);
        }

        public string Version
        {
            get { return "1.0"; }
        }

        public string Name
        {
            get { return "InWorldz.Data.Assets.Stratus.StratusAssetClient"; }
        }

        public void Initialize()
        {
        }

        public void Dispose()
        {
            this.Stop();
        }

        //see IAssetReceiver
        public void AssetReceived(AssetBase asset, AssetRequestInfo data)
        {
            _assetReceiver.AssetReceived(asset, data);
        }

        //see IAssetReceiver
        public void AssetNotFound(OpenMetaverse.UUID assetID, AssetRequestInfo data)
        {
            this.HandleAssetCallback(assetID, data, null);
        }

        public void AssetError(OpenMetaverse.UUID assetID, Exception error, AssetRequestInfo data)
        {
            m_log.ErrorFormat("[InWorldz.Stratus]: Error while requesting asset {0}: {1}", assetID, error);
            this.HandleAssetCallback(assetID, data, error);
        }

        public void HandleAssetCallback(OpenMetaverse.UUID assetID, AssetRequestInfo data, Exception error)
        {
            //if not found and this is the first try, try the second server
            if (_secondReadServer != null && data.ServerNumber == 0)
            {
                data.ServerNumber++;
                _secondReadServer.RequestAsset(assetID, data);
            }
            else
            {
                if (error == null)
                {
                    _assetReceiver.AssetNotFound(assetID, data);
                }
                else
                {
                    _assetReceiver.AssetError(assetID, error, data);
                }
            }
        }

        public AssetStats GetStats(bool resetStats)
        {
            // This only supports CF stats (otherwise we could return _whipAssetClient.GetStats() instead)
            return _cfAssetClient.GetStats(resetStats);
        }
    }
}
