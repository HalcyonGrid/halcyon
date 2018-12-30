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
using System.Text.RegularExpressions;
using System.Threading;
using GlynnTucker.Cache;
using log4net;
using OpenMetaverse;
using OpenMetaverse.Packets;
using OpenSim.Framework.Statistics;
using System.Text;
using System.Linq;
using OpenSim.Framework;
using Amib.Threading;

namespace OpenSim.Framework.Communications.Cache
{
    /// <summary>
    /// Manages local cache of assets and their sending to viewers.
    /// </summary>
    ///
    /// This class actually encapsulates two largely separate mechanisms.  One mechanism fetches assets either
    /// synchronously or async and passes the data back to the requester.  The second mechanism fetches assets and
    /// sends packetised data directly back to the client.  The only point where they meet is AssetReceived() and
    /// AssetNotFound(), which means they do share the same asset and texture caches.
    /// 
    /// Heavily modified by inworldz llc to allow asset loading from a sane source
    /// 
    public class AssetCache : IAssetCache
    {
        private static readonly ILog m_log
            = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        #region IPlugin

        /// <summary>
        /// The methods and properties in this section are needed to
        /// support the IPlugin interface. They cann all be overridden
        /// as needed by a derived class.
        /// </summary>

        public virtual string Name
        {
            get { return "OpenSim.Framework.Communications.Cache.AssetCache"; }
        }

        public virtual string Version
        {
            get { return "1.0"; }
        }

        public virtual void Initialize()
        {
            m_log.Debug("[ASSET CACHE]: Asset cache null initialization");
        }

        public virtual void Initialize(IAssetServer assetServer)
        {
            m_log.InfoFormat("[ASSET CACHE]: Asset cache initialization [{0}/{1}]", Name, Version);
         
            m_assetServer = assetServer;
            m_assetServer.SetReceiver(this);
        }

        public virtual void Initialize(ConfigSettings settings, IAssetServer assetServer)
        {
            m_log.Debug("[ASSET CACHE]: Asset cache configured initialization");
            Initialize(assetServer);
        }

        public void Dispose()
        {
            m_assetServer.Dispose();
        }

        #endregion     

        private const int NEGATIVE_CACHE_SIZE = 1000;       // maximum negative cache size (in items)
        private const int NEGATIVE_CACHE_TIMEOUT = 300;     // maximum negative cache TTL: 5 minutes (in seconds)
        private LRUCache<UUID, DateTime> _negativeCache;

        public IAssetServer AssetServer
        {
            get { return m_assetServer; }
        }
        private IAssetServer m_assetServer;

        /// <summary>
        /// Threads from this pool will execute the async callbacks after assets are returned
        /// </summary>
        private SmartThreadPool _pool = new SmartThreadPool(5 * 60 * 1000, 16, 8);

        public AssetCache()
        {
            _pool.Name = "Asset Cache";
            _negativeCache = new LRUCache<UUID, DateTime>(NEGATIVE_CACHE_SIZE);

            m_log.Debug("[ASSET CACHE]: Asset cache (plugin constructor)");
            ProviderRegistry.Instance.RegisterInterface<IAssetCache>(this);
        }        
        
        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="assetServer"></param>
        public AssetCache(IAssetServer assetServer)
        {
            Initialize(assetServer);
            _negativeCache = new LRUCache<UUID, DateTime>(NEGATIVE_CACHE_SIZE);
            ProviderRegistry.Instance.RegisterInterface<IAssetCache>(this);
        }        

        private string GetMinAvgMax(float[] data, string format)
        {
            if (data.Length == 0)
                return String.Format(format, 0.0f, 0.0f, 0.0f);

            return String.Format(format, data.Min(), data.Average(), data.Max());
        }

        public void ShowState(bool resetStats)
        {
            AssetStats stats = m_assetServer.GetStats(resetStats);
            if (resetStats)
                return;

            float RHits = (stats.nGet > 0) ? ((float)stats.nGetHit / (float)stats.nGet) : 1.0f;
            m_log.InfoFormat("[ASSET_STATS]: reads={0} hits/fetched/missing={1}/{2}/{3} ({4}%), {5}",
                stats.nGet, stats.nGetHit, stats.nGetFetches, stats.nGetNotFound, (int)(RHits*100),
                GetMinAvgMax(stats.allGets, "min/avg/max={0}/{1}/{2}")
                );
            float WHits = (stats.nPut > 0) ? ((float)stats.nPutCached / (float)stats.nPut) : 1.0f;
            m_log.InfoFormat("[ASSET_STATS]: writes={0}, cached={1} ({2}%), uncached exists/size/stream/dupe={3}/{4}/{5}/{6} {7}",
                stats.nPut, stats.nPutCached, (int)(WHits*100),
                stats.nPutExists, stats.nBigAsset, stats.nBigStream, stats.nDupUpdate,
                GetMinAvgMax(stats.allPuts, "min/avg/max={0}/{1}/{2}")
                );
            m_log.InfoFormat("[ASSET_STATS]: Total={0}, readErr init={1}, writeErr TO/NTO/ex/web/io={2}/{3}/{4}/{5}/{6}", 
                stats.nTotal, stats.nGetInit,
                stats.nPutTO, stats.nPutNTO, stats.nPutExcept, stats.nPutExceptWeb, stats.nPutExceptIO);
        }

        // Shortcut test to see if we can return null for the asset without fetching.
        // Returns true if the asset is cached as a null and not expired, or NULL_KEY.
        private bool IsNegativeCached(UUID assetId)
        {
            if (assetId == UUID.Zero)
                return true;    // NULL_KEY requests are permanently "negative cached", just skip.

            DateTime stamp;
            bool found;
            lock (_negativeCache)
            {
                found = _negativeCache.TryGetValue(assetId, out stamp);
            }

            if (found)
            {
                if ((DateTime.Now - stamp).TotalSeconds < NEGATIVE_CACHE_TIMEOUT)
                    return true;    // cached and still valid

                // otherwise we need to check again so just remove this one and fall through
                lock (_negativeCache)
                {
                    _negativeCache.Remove(assetId);
                }
                // m_log.InfoFormat("[ASSET CACHE]: Removed {0} from negative cache on lookup.",assetId);
            }
            // m_log.InfoFormat("[ASSET CACHE]: Unknown {0} not in negative cache, fetching...", assetId);
            return false;
        }

        private void StampNegativeCache(UUID assetId)
        {
            lock (_negativeCache)
            {
                if (_negativeCache.Contains(assetId))
                    _negativeCache.Remove(assetId);
                _negativeCache.Add(assetId, DateTime.Now);
            }
            // m_log.InfoFormat("[ASSET CACHE]: Add/update {0} to negative cache.", assetId);
        }

        private void RemoveFromNegativeCache(UUID assetId)
        {
            lock (_negativeCache)
            {
                if (_negativeCache.Contains(assetId))
                    _negativeCache.Remove(assetId);
            }
            // m_log.InfoFormat("[ASSET CACHE]: Removed {0} from negative cache.", assetId);
        }

        public void GetAsset(UUID assetId, AssetRequestCallback callback, AssetRequestInfo requestInfo)
        {
            requestInfo.AssetId = assetId;
            requestInfo.Callback = callback;
            if (IsNegativeCached(assetId))
                callback(assetId, null);
            else
                m_assetServer.RequestAsset(assetId, requestInfo);
        }

        private bool IsAssetRetrievalAllowed(AssetBase asset, AssetRequestInfo requestInfo)
        {
            if (asset == null)
            {
                return true;
            }

            //do not pass back object assets to the net
            if (asset.Type == (sbyte)AssetType.Object &&
                requestInfo.Origin == AssetRequestInfo.RequestOrigin.SRC_NET)
            {
                m_log.WarnFormat("Not allowing access to OBJECT asset {0} from SRC_NET", requestInfo.AssetId);
                return false;
            }

            //dont pass back full script text via LLSTS_ASSET
            if ((asset.Type == (sbyte)AssetType.LSLText || asset.Type == (sbyte)AssetType.Notecard) &&
                requestInfo.Origin == AssetRequestInfo.RequestOrigin.SRC_NET &&
                (requestInfo.NetSource != AssetRequestInfo.NetSourceType.LLTST_SIM_INV_ITEM &&
                requestInfo.NetSource != AssetRequestInfo.NetSourceType.LLTST_SIM_ESTATE))
            {
                m_log.WarnFormat("Not allowing access to LSLText or asset {0} from SRC_NET and LLTST_ASSET", requestInfo.AssetId);
                return false;
            }

            return true;
        }

        public AssetBase GetAsset(UUID assetID, AssetRequestInfo reqInfo)
        {
            // This function returns true if it's cached as a null and not expired.
            if (IsNegativeCached(assetID))
                return null;

            // Else, we need to fetch the asset.
            reqInfo.AssetId = assetID;
            AssetBase asset = m_assetServer.RequestAssetSync(assetID);

            if (asset == null)
                StampNegativeCache(assetID);

            if (this.IsAssetRetrievalAllowed(asset, reqInfo))
            {
                StatsManager.SimExtraStats.AddAssetRequestTime((long)reqInfo.RequestDuration);
                return asset;
            }

            return null;
        }

        private bool IsAssetStorageAllowed(AssetBase asset, AssetRequestInfo requestInfo)
        {
            if (asset == null)
            {
                return false;
            }

            //do not accept object assets from the net
            if (asset.Type == (sbyte)AssetType.Object && 
                requestInfo.Origin == AssetRequestInfo.RequestOrigin.SRC_NET)
            {
                return false;
            }

            return true;
        }

        public void AddAsset(AssetBase asset, AssetRequestInfo requestInfo)
        {
            requestInfo.AssetId = asset.FullID;
            if (this.IsAssetStorageAllowed(asset, requestInfo))
            {
                ulong startTime = Util.GetLongTickCount();

                try
                {
                    RemoveFromNegativeCache(asset.FullID);
                    m_assetServer.StoreAsset(asset);
                }
                catch (AssetAlreadyExistsException e)
                {
                    //Don't rethrow this exception. AssetServerExceptions thrown from here
                    // Let's not report this as a warning since this isn't a problem.
                    // m_log.WarnFormat("[ASSET CACHE] Not storing asset that already exists: {0}", e.Message);
                }

                StatsManager.SimExtraStats.AddAssetWriteTime((long)(Util.GetLongTickCount() - startTime));
            }
            else
            {
                throw new NotSupportedException(String.Format("Not allowing asset storage ID:{0} Type:{1} Source:{2}",
                    asset.ID, asset.Type, requestInfo.Origin));
            }

            //we will save the local asset issue for later as its confusing as hell and even LL isnt
            //very clear on it.  It looks like baked textures, possibly body parts, etc are all
            //marked as local.  from what I can gather this means that the asset is stored locally
            //on this server, but, and this makes no sense, can be requested by other servers.
            //on this case why not put it on the asset cluster since it's central anyways?
            //i could see bakes getting stored locally and regenerated for every sim corssing (i guess)
            //but this isnt even the case.  so im leaving this for now and everything is going to
            //the asset server
        }

        // See IAssetReceiver
        public virtual void AssetReceived(AssetBase asset, AssetRequestInfo rdata)
        {
            StatsManager.SimExtraStats.AddAssetRequestTime((long)rdata.RequestDuration);
            this.RemoveFromNegativeCache(asset.FullID);
            this.StartAssetReceived(asset.FullID, asset, rdata);
        }

        // See IAssetReceiver
        public virtual void AssetNotFound(UUID assetId, AssetRequestInfo reqInfo)
        {
//            m_log.WarnFormat("[ASSET CACHE]: AssetNotFound or transfer otherwise blocked for {0}", assetId);
            this.StampNegativeCache(assetId);
            this.StartAssetReceived(assetId, null, reqInfo);
        }

        public virtual void AssetError(UUID assetId, Exception e, AssetRequestInfo reqInfo)
        {
            m_log.WarnFormat("[ASSET CACHE]: Error while retrieving asset {0}: {1}", assetId, e.Message);
            this.StartAssetReceived(assetId, null, reqInfo);
        }

        private void StartAssetReceived(UUID uUID, AssetBase asset, AssetRequestInfo reqInfo)
        {
            _pool.QueueWorkItem(
                new Action<UUID, AssetBase, AssetRequestInfo>(HandleAssetReceived),
                uUID, asset, reqInfo);
        }

        private void HandleAssetReceived(UUID assetId, AssetBase asset, AssetRequestInfo reqInfo)
        {
            // This must be done before the IsAssetRetrievalAllowed check which can turn it to null.
            if (asset == null)
                this.StampNegativeCache(reqInfo.AssetId);
            else
                this.RemoveFromNegativeCache(reqInfo.AssetId);

            if (this.IsAssetRetrievalAllowed(asset, reqInfo))
            {
                reqInfo.Callback(assetId, asset);
            }
            else
            {
                reqInfo.Callback(assetId, null);
            }
        }

        public void AddAssetRequest(IClientAPI userInfo, TransferRequestPacket transferRequest)
        {
            AssetRequestInfo reqInfo = new AssetRequestInfo(transferRequest, userInfo);
            reqInfo.Callback =
                delegate(UUID assetId, AssetBase asset)
                {
                    this.ProcessDirectAssetRequest(userInfo, asset, reqInfo);
                };

            m_assetServer.RequestAsset(reqInfo.AssetId, reqInfo);
        }

        /// <summary>
        /// Process the asset queue which sends packets directly back to the client.
        /// </summary>
        private void ProcessDirectAssetRequest(IClientAPI userInfo, AssetBase asset, AssetRequestInfo req)
        {
           userInfo.SendAsset(asset, req);
        }
    }
}
