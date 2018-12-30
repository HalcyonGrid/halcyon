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
using System.IO;
using System.Text;
using OpenSim.Framework;
using log4net;
using System.Reflection;
using OpenSim.Framework.AssetLoader.Filesystem;

namespace InWorldz.Whip.Client
{
    /// <summary>
    /// Implements the IAssetServer interface for the InWorldz WHIP asset server
    /// </summary>
    public class AssetClient : IAssetServer
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private RemoteServer _readWhipServer;
        private RemoteServer _writeWhipServer;
        private IAssetReceiver _receiver;

        protected IAssetLoader assetLoader = new AssetLoaderFileSystem();

        ConfigSettings _settings;

        private bool _loadingDefaultAssets = false;

        #region IAssetServer Members

        public AssetClient()
        {
        }

        public AssetClient(string url)
        {
            _log.Info("[WHIP.AssetClient] Direct constructor");

            this.SetupConnections(url);
        }

        public void Initialize(ConfigSettings settings)
        {
            _settings = settings;

            Nini.Config.IConfig netConfig = settings.SettingsFile["Network"];
            if (netConfig != null && netConfig.Contains("asset_server_url"))
            {
                string url = netConfig.GetString("asset_server_url");
                this.SetupConnections(url);
            }
            else
            {
                throw new Exception("Network/asset_server_url is a required setting");
            }
        }

        private void SetupConnections(string url)
        {
            if (url.Contains(","))
            {
                List<string[]> rwServers = this.ParseReadWriteWhipURL(url);
                _readWhipServer = new RemoteServer(rwServers[0][1], Convert.ToUInt16(rwServers[0][2]), rwServers[0][0]);

                //if the port or hosts are different the write server is separate
                if (rwServers[0][1] != rwServers[1][1] || rwServers[0][2] != rwServers[1][2])
                {
                    _writeWhipServer = new RemoteServer(rwServers[1][1], Convert.ToUInt16(rwServers[1][2]), rwServers[1][0]);
                }
                else
                {
                    //else the servers are the same
                    _writeWhipServer = _readWhipServer;
                }
            }
            else
            {
                string[] urlParts = this.ParseSingularWhipURL(url);
                _readWhipServer = new RemoteServer(urlParts[1], Convert.ToUInt16(urlParts[2]), urlParts[0]);
                _writeWhipServer = _readWhipServer;
            }
        }

        public virtual void LoadDefaultAssets(string pAssetSetsXml)
        {
            _log.Info("[ASSET SERVER]: Setting up asset database");
            var signpostMarkerFilename = $"{pAssetSetsXml}.loaded";

            if (File.Exists(signpostMarkerFilename))
            {
                _log.Info("[ASSET SERVER]: Asset database marked as already set up. Not reloading default assets.");
            }
            else
            {
                _loadingDefaultAssets = true;
                assetLoader.ForEachDefaultXmlAsset(pAssetSetsXml, StoreAsset);
                _loadingDefaultAssets = false;
                try
                {
                    File.CreateText(signpostMarkerFilename).Close();
                }
                catch(Exception e)
                {
                    _log.Error($"Unable to create file '{signpostMarkerFilename}' to mark default assets as having been already loaded.", e);
                }
            }
        }

        private List<string[]> ParseReadWriteWhipURL(string url)
        {
            // R:whip://pass@127.0.0.1:32700,W:whip://pass@127.0.0.1:32700
            
            //split by , this will give us the two WHIP URLs prefixed by their
            //usage
            string[] readAndWriteServers = url.Split(new char[1] { ',' });
            string[] readURL = null;
            string[] writeURL = null;

            string firstServerType = readAndWriteServers[0].Substring(0, 2);
            if (firstServerType == "R:")
            {
                readURL = this.ParseSingularWhipURL(readAndWriteServers[0].Substring(2));
            }
            else if (firstServerType == "W:")
            {
                writeURL = this.ParseSingularWhipURL(readAndWriteServers[0].Substring(2));
            }
            else
            {
                throw new Exception("Invalid whip URL. For first server R: or W: type not specified");
            }


            string secondServerType = readAndWriteServers[1].Substring(0, 2);
            if (secondServerType == "R:")
            {
                if (readURL != null)
                {
                    throw new Exception("Invalid whip URL. Read URL specified twice");
                }

                readURL = this.ParseSingularWhipURL(readAndWriteServers[1].Substring(2));
            }
            else if (secondServerType == "W:")
            {
                if (writeURL != null)
                {
                    throw new Exception("Invalid whip URL. Write URL specified twice");
                }

                writeURL = this.ParseSingularWhipURL(readAndWriteServers[1].Substring(2));
            }
            else
            {
                throw new Exception("Invalid whip URL. For second server R: or W: type not specified");
            }

            return new List<string[]>(new[] { readURL, writeURL});
        }

        /// <summary>
        /// Returns an array with [user, password, host, port]
        /// </summary>
        /// <param name="url"></param>
        private string[] ParseSingularWhipURL(string url)
        {
            // whip://pass@127.0.0.1:32700

            //url must start with whip
            if (url.Substring(0, 7) != "whip://") throw new Exception("Invaid whip URL.  Must start with whip://");
            //strip the Resource ID portion
            url = url.Substring(7);
            //split by @ this will give us     username:password   ip:port
            string[] userAndHost = url.Split(new char[1]{'@'});
            if (userAndHost.Length != 2) throw new Exception("Invalid whip URL, missing @");

            //get the user and pass
            string pass = userAndHost[0];
           
            //get the host and port
            string[] hostAndPort = userAndHost[1].Split(new char[1]{':'});
            if (hostAndPort.Length != 2) throw new Exception("Invalid whip URL, missing : between host and port");

            string[] ret = new string[4];
            ret[0] = pass;
            ret[1] = hostAndPort[0];
            ret[2] = hostAndPort[1];

            return ret;
        }

        private bool HasRWSplit()
        {
            return _readWhipServer != _writeWhipServer;
        }

        public void Start()
        {
            _readWhipServer.Start();

            if (this.HasRWSplit())
            {
                _writeWhipServer.Start();
            }

            if (_settings != null) this.LoadDefaultAssets(_settings.AssetSetsXMLFile);
        }

        public void Stop()
        {
            _readWhipServer.Stop();

            if (this.HasRWSplit())
            {
                _writeWhipServer.Stop();
            }
        }

        public void SetReceiver(IAssetReceiver receiver)
        {
            _receiver = receiver;
        }

        /// <summary>
        /// Converts a whip asset to an opensim asset
        /// </summary>
        /// <param name="whipAsset"></param>
        /// <returns></returns>
        private AssetBase WhipAssetToOpensim(Asset whipAsset)
        {
            AssetBase osAsset = new AssetBase();
            osAsset.Data = whipAsset.Data;
            osAsset.Name = whipAsset.Name;
            osAsset.Description = whipAsset.Description;
            osAsset.Type = (sbyte)whipAsset.Type;
            osAsset.Local = whipAsset.Local;
            osAsset.Temporary = whipAsset.Temporary;
            osAsset.FullID = new OpenMetaverse.UUID(whipAsset.Uuid);

            return osAsset;
        }

        public void RequestAsset(OpenMetaverse.UUID assetID, AssetRequestInfo args)
        {
            try
            {
                _readWhipServer.GetAssetAsync(assetID.ToString(),
                    delegate(Asset asset, AssetServerError e)
                    {
                        if (e == null)
                        {
                            //no error, pass asset to caller
                            _receiver.AssetReceived(WhipAssetToOpensim(asset), args);
                        }
                        else
                        {
                            string errorString = e.ToString();
                            if (!errorString.Contains("not found"))
                            {
                                //there is an error, log it, and then tell the caller we have no asset to give
                                _log.ErrorFormat(
                                    "[WHIP.AssetClient]: Failure fetching asset {0}" + Environment.NewLine + errorString
                                    + Environment.NewLine, assetID);
                            }

                            _receiver.AssetNotFound(assetID, args);
                        }
                    }
                );
            }
            catch (AssetServerError e)
            {
                //there is an error, log it, and then tell the caller we have no asset to give
                string errorString = e.ToString();
                if (!errorString.Contains("not found"))
                {
                    _log.ErrorFormat(
                        "[WHIP.AssetClient]: Failure fetching asset {0}" + Environment.NewLine + errorString
                        + Environment.NewLine, assetID);
                }

                _receiver.AssetNotFound(assetID, args);
            }
        }

        public AssetBase RequestAssetSync(OpenMetaverse.UUID assetID)
        {
            try
            {
                AssetBase asset = this.WhipAssetToOpensim(_readWhipServer.GetAsset(assetID.ToString()));
                return asset;
            }
            catch (AssetServerError e)
            {
                string errorString = e.ToString();
                if (!errorString.Contains("not found"))
                {
                    //there is an error, log it, and then tell the caller we have no asset to give
                    _log.ErrorFormat(
                        "[WHIP.AssetClient]: Failure fetching asset {0}" + Environment.NewLine + errorString
                        + Environment.NewLine, assetID);
                }
                return null;
            }
        }

        private Asset OpensimAssetToWhip(AssetBase osAsset)
        {
            Asset whipAsset = new Asset(osAsset.FullID.ToString(), (byte)osAsset.Type,
                osAsset.Local, osAsset.Temporary, OpenSim.Framework.Util.UnixTimeSinceEpoch(),
                osAsset.Name, osAsset.Description, osAsset.Data);

            return whipAsset;
        }

        public void StoreAsset(AssetBase asset)
        {
            try
            {
                _writeWhipServer.PutAsset(OpensimAssetToWhip(asset));
            }
            catch (AssetServerError e)
            {
                //there is an error, log it, and then tell the caller we have no asset to give
                if (!_loadingDefaultAssets)
                {
                    if (e.Message.Contains("already exists")) 
                    {
                        //this is hacky, but I dont want to edit the whip client this
                        //late in the game when we're going to be phasing it out
                        throw new AssetAlreadyExistsException(e.Message, e);
                    }
                    else
                    {
                        _log.ErrorFormat(
                            "[WHIP.AssetClient]: Failure storing asset {0}" + Environment.NewLine + e.ToString()
                            + Environment.NewLine, asset.FullID);
                        throw new AssetServerException(e.Message, e);
                    }
                }
            }
        }

        /// <summary>
        /// Okay... assets are immutable so im not even sure what place this has here..
        /// </summary>
        /// <param name="asset"></param>
        public void UpdateAsset(AssetBase asset)
        {
            _log.WarnFormat("[WHIP.AssetClient]: UpdateAsset called for {0}  Assets are immutable.", asset.FullID);
            this.StoreAsset(asset);
        }

        public AssetStats GetStats(bool resetStats)
        {
            return new AssetStats("WHIP");
        }

        #endregion

        #region IPlugin Members

        public string Version
        {
            get { return "1.0"; }
        }

        public string Name
        {
            get { return "InWorldz WHIP Asset Client"; }
        }

        public void Initialize()
        {
            
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            this.Stop();
        }

        #endregion
    }
}
