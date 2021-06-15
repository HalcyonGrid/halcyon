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
using System.Threading.Tasks;
using OpenSim.Framework;
using log4net;
using System.Reflection;
using System.Threading;
using System.Net;
using OpenMetaverse.StructuredData;
using OpenSim.Framework.Servers.HttpServer;
using System.IO;

namespace OpenSim.Region.Framework.Scenes
{
    /// <summary>
    /// Manages communications, monitoring, and events from regions surrounding this one
    /// 
    /// "Surrounding" doesnt necessarily mean touching. We want to have information about regions
    /// that are potentially very distant, but that clients may want to be able to see into from
    /// their present location. This is done by managing a maximum draw distance and using that 
    /// to query neighboring regions.
    /// </summary>
    public class SurroundingRegionManager
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);


        /// <summary>
        /// The maximum draw distance we support for allowing child connections to our surrounding regions
        /// </summary>
        private const int MAX_DRAW_DISTANCE = 512;

        /// <summary>
        /// The number of seconds between sending heartbeats from this region to all neighbors
        /// </summary>
        private const int NEIGHBOR_PING_FREQUENCY_SECS = 300;

        /// <summary>
        /// The number of seconds that a region has to ping us before we consider it timed out
        /// </summary>
        private const int NEIGHBOR_PING_TIMEOUT_SECS = 900;

        /// <summary>
        /// In very rare cases, two neighbor regions may start/register at the exact same time, and the initial
        /// region query will be misssing the neighbors. After a random number of seconds between the min and max 
        /// in this range, a new reconciliation query will be performed adding region(s) to our list that may have
        /// been missing during the initial startup
        /// </summary>
        private readonly Tuple<int, int> NEIGHBOR_RECONCILIATION_TIME_RANGE_SECS = new Tuple<int, int>(300, 600);

        /// <summary>
        /// The type of message we're sending or receiving
        /// </summary>
        private enum MessageType
        {
            Heartbeat,
            Up,
            Down
        }

        /// <summary>
        /// The URL we use to send a heartbeat to our neighbors
        /// </summary>
        private const string HEARTBEAT_HANDLER_URL = "/region2/heartbeat";

        /// <summary>
        /// The URL we use to send an up notice to our neighbors
        /// </summary>
        private const string UP_HANDLER_URL = "/region2/regionup";

        /// <summary>
        /// The URL we use to send a down notice to our neighbors
        /// </summary>
        private const string DOWN_HANDLER_URL = "/region2/regiondown";

        /// <summary>
        /// The timeout in ms for calls to our neighbor regions
        /// </summary>
        private const int NEIGHBOR_HANDLER_TIMEOUT = 5000;


        /// <summary>
        /// The scene we're managing regions for
        /// </summary>
        private Scene _scene;

        /// <summary>
        /// The shared key used for region/region auth
        /// </summary>
        private string _gridSendKey;

        /// <summary>
        /// A dict of regions keyed by their handles of neighbors that we know and are reasonably sure are up
        /// </summary>
        private Dictionary<ulong, KnownNeighborRegion> _knownNeighbors = new Dictionary<ulong, KnownNeighborRegion>();

        /// <summary>
        /// Timer used to manage up/down checks for regions
        /// </summary>
        private Timer _regionHeartbeatTimer;

        /// <summary>
        /// The time in ticks that we initial queried the database for our neighbors
        /// </summary>
        private ulong _timeNeighborsInitiallyQueried;

        /// <summary>
        /// The number of seconds after which we should reconcile neighbor regions
        /// </summary>
        private ulong _reconcileAfterSecs;

        /// <summary>
        /// Regions that were not available when we sent our 'up' message. These regions should not be reconciled
        /// We will bring them up if we receieve a ping later
        /// </summary>
        private HashSet<ulong> _doaRegions = new HashSet<ulong>();



        /// <summary>
        /// Delegate for monitoring state changes
        /// </summary>
        /// <param name="neighbor">The neighbor that changed</param>
        /// <param name="changeType">Type of change that happened</param>
        public delegate void NeighborStateChange(SimpleRegionInfo neighbor, NeighborStateChangeType changeType);

        /// <summary>
        /// Event that is fired whenever there is a state change
        /// </summary>
        public event NeighborStateChange OnNeighborStateChange;




        public SurroundingRegionManager(Scene myRegion, string gridSendKey)
        {
            _scene = myRegion;
            _gridSendKey = gridSendKey;
            _reconcileAfterSecs = (ulong)Util.RandomClass.Next(NEIGHBOR_RECONCILIATION_TIME_RANGE_SECS.Item1, NEIGHBOR_RECONCILIATION_TIME_RANGE_SECS.Item2);
            _regionHeartbeatTimer = new Timer(async (object state) => await DoRegionHeartbeat(state), null, NEIGHBOR_PING_FREQUENCY_SECS * 1000, NEIGHBOR_PING_FREQUENCY_SECS * 1000);

            //register HTTP comms
            _scene.CommsManager.HttpServer.AddStreamHandler(new BinaryStreamHandler("POST", HEARTBEAT_HANDLER_URL, OnHeartbeatReceived));
            _scene.CommsManager.HttpServer.AddStreamHandler(new BinaryStreamHandler("POST", UP_HANDLER_URL, OnNeighborRegionUp));
            _scene.CommsManager.HttpServer.AddStreamHandler(new BinaryStreamHandler("POST", DOWN_HANDLER_URL, OnNeighborRegionDown));
        }

        private string OnHeartbeatReceived(Stream request, string path, string param, OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            if (CheckAuthorization(httpRequest, httpResponse))
            {
                return UnpackMessageAndCallHandler(request, httpResponse, this.HandleNeighborPing);
            }
            else
            {
                return "Unauthorized";
            }
        }

        private string OnNeighborRegionUp(Stream request, string path, string param, OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            if (CheckAuthorization(httpRequest, httpResponse))
            {
                return UnpackMessageAndCallHandler(request, httpResponse, this.HandleNeighborUp);
            }
            else
            {
                return "Unauthorized";
            }
        }

        private string OnNeighborRegionDown(Stream request, string path, string param, OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            if (CheckAuthorization(httpRequest, httpResponse))
            {
                return UnpackMessageAndCallHandler(request, httpResponse, this.HandleNeighborDown);
            }
            else
            {
                return "Unauthorized";
            }
        }

        private bool CheckAuthorization(OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            if (Util.CheckHttpAuthorization(_gridSendKey, httpRequest.Headers))
            {
                return true;
            }
            else
            {
                httpResponse.StatusCode = 401;
                return false;
            }
        }

        /// <summary>
        /// Unpacks the message sent from a neighboring region and calls the given handler
        /// </summary>
        /// <param name="request"></param>
        /// <param name="httpResponse"></param>
        /// <param name="handler"></param>
        /// <returns></returns>
        private string UnpackMessageAndCallHandler(Stream request, OSHttpResponse httpResponse, Action<SimpleRegionInfo> handler)
        {
            try
            {
                byte[] binaryLLSD;
                using (MemoryStream ms = new MemoryStream())
                {
                    request.CopyTo(ms);
                    binaryLLSD = ms.ToArray();
                }

                OSDMap regionInfoPackage = (OSDMap)OSDParser.DeserializeLLSDBinary(binaryLLSD);
                RegionInfo regionInfo = new RegionInfo();
                regionInfo.UnpackRegionInfoData(regionInfoPackage);

                handler(regionInfo);

                httpResponse.StatusCode = 200;
                return "OK";
            }
            catch (Exception e)
            {
                httpResponse.StatusCode = 500;
                return "OnHeartbeatReceived: Error: " + e.ToString();
            }
        }

        /// <summary>
        /// Sends the region up notification to our known neighbors
        /// </summary>
        public void SendRegionUpToNeighbors()
        {
            List<KnownNeighborRegion> neighborSnap = GetNeighborsSnapshot();

            SendMessageTo(neighborSnap, UP_HANDLER_URL, MessageType.Up);
        }

        /// <summary>
        /// Sends the region down notification to our known neighbors
        /// </summary>
        public void SendRegionDownToNeighbors()
        {
            List<KnownNeighborRegion> neighborSnap = GetNeighborsSnapshot();

            SendMessageTo(neighborSnap, DOWN_HANDLER_URL, MessageType.Down);
        }

        private async Task DoRegionHeartbeat(object state)
        {
            //do we need to reconcile?
            if (_timeNeighborsInitiallyQueried != 0 &&
                Util.GetLongTickCount() - _timeNeighborsInitiallyQueried >= _reconcileAfterSecs * 1000)
            {
                _timeNeighborsInitiallyQueried = 0;
                await ReconcileNeighborsFromStorage();
            }

            List<KnownNeighborRegion> neighborSnap = GetNeighborsSnapshot();

            //send our heartbeat to everyone else
            SendMessageTo(neighborSnap, HEARTBEAT_HANDLER_URL, MessageType.Heartbeat);

            //now prune regions that we haven't seen a heartbeat from
            List<KnownNeighborRegion> toPrune = new List<KnownNeighborRegion>();
            lock (_knownNeighbors)
            {
                foreach (var kvp in _knownNeighbors)
                {
                    if (Util.GetLongTickCount() - kvp.Value.LastPingReceivedOn > NEIGHBOR_PING_TIMEOUT_SECS * 1000)
                    {
                        toPrune.Add(kvp.Value);
                    }
                }

                foreach (var neighbor in toPrune)
                {
                    _knownNeighbors.Remove(neighbor.RegionInfo.RegionHandle);
                }
            }

            foreach (var neighbor in toPrune)
            {
                TriggerNeighborStateChange(neighbor.RegionInfo, NeighborStateChangeType.NeighborDown);
            }
        }

        /// <summary>
        /// Locks and copies the neighbors list into a new list 
        /// </summary>
        /// <returns></returns>
        public List<KnownNeighborRegion> GetNeighborsSnapshot()
        {
            List<KnownNeighborRegion> neighborSnap;
            lock (_knownNeighbors)
            {
                neighborSnap = new List<KnownNeighborRegion>(_knownNeighbors.Count);
                neighborSnap.AddRange(_knownNeighbors.Values);
            }

            return neighborSnap;
        }

        private void SendMessageTo(List<KnownNeighborRegion> neighborSnap, string url, MessageType messageType)
        {
            try
            {
                List<Task> waitingResults = new List<Task>();
                OSDMap regionInfo = _scene.RegionInfo.PackRegionInfoData();
                byte[] serializedRegionInfo = OSDParser.SerializeLLSDBinary(regionInfo);

                foreach (var neighbor in neighborSnap)
                {
                    var req = (HttpWebRequest)HttpWebRequest.Create(neighbor.RegionInfo.InsecurePublicHTTPServerURI + url);
                    req.Headers["authorization"] = Util.GenerateHttpAuthorization(_gridSendKey);
                    req.Timeout = NEIGHBOR_HANDLER_TIMEOUT;
                    req.ReadWriteTimeout = NEIGHBOR_HANDLER_TIMEOUT;
                    req.Method = "POST";

                    waitingResults.Add(this.DoRegionAsyncCall(req, neighbor, messageType, serializedRegionInfo));
                }

                Task.WaitAll(waitingResults.ToArray());
            }
            catch (AggregateException e) //we're catching exceptions in the call, so we really should never see this
            {
                for (int j = 0; j < e.InnerExceptions.Count; j++)
                {
                    _log.ErrorFormat("[REGIONMANAGER]: Error thrown from async call: {0}", e);
                }
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[REGIONMANAGER]: Error thrown while sending heartbeat: {0}", e);
            }
        }

        private async Task<bool> DoRegionAsyncCall(HttpWebRequest req, KnownNeighborRegion neighbor, MessageType type, byte[] body)
        {
            try
            {
                using (System.IO.Stream reqStream = await req.GetRequestStreamAsync())
                {
                    await reqStream.WriteAsync(body, 0, body.Length);
                }
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[REGIONMANAGER]: Could not post request for {0} to region {1} at {2}: {3}", 
                    type, neighbor.RegionInfo.RegionHandle, Util.RegionHandleToLocationString(neighbor.RegionInfo.RegionHandle), e.Message);
                KillNeighborOnUpMessageFailure(neighbor, type);
                
                return false;
            }

            try
            {
                using (WebResponse response = await req.GetResponseAsync(NEIGHBOR_HANDLER_TIMEOUT))
                {
                    //we dont do anything with the response
                }
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[REGIONMANAGER]: Unable to read response to {0} for region {1} at {2}: {3}", 
                    type, neighbor.RegionInfo.RegionHandle, Util.RegionHandleToLocationString(neighbor.RegionInfo.RegionHandle), e.Message);
                KillNeighborOnUpMessageFailure(neighbor, type);

                return false;
            }

            return true;
        }

        private void KillNeighborOnUpMessageFailure(KnownNeighborRegion neighbor, MessageType type)
        {
            //if this is an "up" message, consider the region crashed and remove it from neighbors
            if (type == MessageType.Up)
            {
                lock (_knownNeighbors)
                {
                    _knownNeighbors.Remove(neighbor.RegionInfo.RegionHandle);
                    _doaRegions.Add(neighbor.RegionInfo.RegionHandle);
                }
            }
        }

        /// <summary>
        /// Begins a series of async calls to refresh neighbor regions from storage. This should be
        /// called only when we're first coming on line
        /// </summary>
        /// <remarks>
        /// Will throw exceptions if the list can not be loaded
        /// </remarks>
        public async Task RefreshNeighborsFromStorage()
        {
            Task<List<SimpleRegionInfo>> neighborsTask = 
                _scene.CommsManager.GridService.RequestNeighbors2Async(
                    _scene.RegionInfo.RegionLocX, _scene.RegionInfo.RegionLocY, MAX_DRAW_DISTANCE);
            
            await neighborsTask;

            Dictionary<ulong, KnownNeighborRegion> knownNeighbors = new Dictionary<ulong, KnownNeighborRegion>();
            foreach (var neighbor in neighborsTask.Result)
            {
                var region = new KnownNeighborRegion { LastPingReceivedOn = Util.GetLongTickCount(), RegionInfo = neighbor };
                knownNeighbors.Add(region.RegionInfo.RegionHandle, region);
            }

            //atomic swapout
            _knownNeighbors = knownNeighbors;
            _timeNeighborsInitiallyQueried = Util.GetLongTickCount();

            return;
        }

        /// <summary>
        /// Begins a series of async calls to reconcile our neighbor regions against storage. This function is called
        /// once after startup to ensure that we should have a true list of all regions
        /// </summary>
        /// <remarks>
        /// Will throw exceptions if the list can not be loaded
        /// </remarks>
        private async Task ReconcileNeighborsFromStorage()
        {
            Task<List<SimpleRegionInfo>> neighborsTask =
                _scene.CommsManager.GridService.RequestNeighbors2Async(
                    _scene.RegionInfo.RegionLocX, _scene.RegionInfo.RegionLocY, MAX_DRAW_DISTANCE);

            List<SimpleRegionInfo> newRegions = await neighborsTask;
            List<SimpleRegionInfo> regionsNowUp = new List<SimpleRegionInfo>();

            lock (_knownNeighbors)
            {
                //add any regions we don't know about
                foreach (var region in newRegions)
                {
                    if (!_knownNeighbors.ContainsKey(region.RegionHandle) && !_doaRegions.Contains(region.RegionHandle))
                    {
                        _knownNeighbors.Add(region.RegionHandle,
                            new KnownNeighborRegion { RegionInfo = region, LastPingReceivedOn = Util.GetLongTickCount() });
                        regionsNowUp.Add(region);
                    }
                }

                _doaRegions.Clear();
            }

            foreach (var region in regionsNowUp)
            {
                TriggerNeighborStateChange(region, NeighborStateChangeType.NeighborUp);
            }
        }

        /// <summary>
        /// Signal that a neighbor has come up
        /// </summary>
        /// <param name="region">The region information</param>
        private void HandleNeighborUp(SimpleRegionInfo region)
        {
            var newNeighbor = new KnownNeighborRegion
            {
                LastPingReceivedOn = Util.GetLongTickCount(),
                RegionInfo = region
            };

            lock (_knownNeighbors)
            {
                _knownNeighbors[region.RegionHandle] = newNeighbor;
            }

            TriggerNeighborStateChange(region, NeighborStateChangeType.NeighborUp);
        }

        /// <summary>
        /// Signal that a neighbor has gone down
        /// </summary>
        /// <param name="region"></param>
        private void HandleNeighborDown(SimpleRegionInfo region)
        {
            lock (_knownNeighbors)
            {
                _knownNeighbors.Remove(region.RegionHandle);
            }

            TriggerNeighborStateChange(region, NeighborStateChangeType.NeighborDown);
        }

        /// <summary>
        /// Signal that we've received a ping from a neighbor
        /// </summary>
        /// <param name="region"></param>
        private void HandleNeighborPing(SimpleRegionInfo region)
        {
            bool newNeighbor = false;

            KnownNeighborRegion neighbor;
            lock (_knownNeighbors)
            {
                if (_knownNeighbors.TryGetValue(region.RegionHandle, out neighbor))
                {
                    neighbor.LastPingReceivedOn = Util.GetLongTickCount();
                }
                else
                {
                    newNeighbor = true;
                    neighbor = new KnownNeighborRegion { LastPingReceivedOn = Util.GetLongTickCount(), RegionInfo = region };
                    _knownNeighbors.Add(neighbor.RegionInfo.RegionHandle, neighbor);
                }
            }

            if (newNeighbor)
            {
                TriggerNeighborStateChange(region, NeighborStateChangeType.NeighborUp);
            }

            TriggerNeighborStateChange(region, NeighborStateChangeType.NeighborPing);
        }
        
        /// <summary>
        /// Triggers the state change event
        /// </summary>
        /// <param name="neighbor"></param>
        /// <param name="neighborStateChangeType"></param>
        private void TriggerNeighborStateChange(SimpleRegionInfo neighbor, NeighborStateChangeType neighborStateChangeType)
        {
            if (neighborStateChangeType != NeighborStateChangeType.NeighborPing)
            {
                _log.InfoFormat("[REGIONMANAGER]: Neighbor region {0} at {1} state change {2}",
                    neighbor.RegionHandle, Util.RegionHandleToLocationString(neighbor.RegionHandle), neighborStateChangeType);
            }

            var stateChangeHandler = this.OnNeighborStateChange;

            if (stateChangeHandler != null)
            {
                stateChangeHandler(neighbor, neighborStateChangeType);
            }
        }

        /// <summary>
        /// Returns a neighbor we know about at x,y or null if there is no known neighbor there
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        public SimpleRegionInfo GetKnownNeighborAt(uint x, uint y)
        {
            lock (_knownNeighbors)
            {
                KnownNeighborRegion foundRegion;
                if (_knownNeighbors.TryGetValue(Util.RegionHandleFromLocation(x, y), out foundRegion))
                {
                    return foundRegion.RegionInfo;
                }
                else
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Returns whether or not we know about a neighbor at the given coordinates
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        public bool HasKnownNeighborAt(uint x, uint y)
        {
            return GetKnownNeighborAt(x, y) != null;
        }

        /// <summary>
        /// Returns a neighbor we know about by its handle or null if there is no known neighbor there
        /// </summary>
        /// <param name="handle">The region handle</param>
        /// <returns></returns>
        public SimpleRegionInfo GetKnownNeighborByHandle(ulong handle)
        {
            lock (_knownNeighbors)
            {
                KnownNeighborRegion foundRegion;
                if (_knownNeighbors.TryGetValue(handle, out foundRegion))
                {
                    return foundRegion.RegionInfo;
                }
                else
                {
                    return null;
                }
            }
        }

        private SimpleRegionInfo FindKnownNeighbor(uint x, uint y)
        {
            foreach (var region in _knownNeighbors.Values)
            {
                if ((region.RegionInfo.RegionLocX == x) && (region.RegionInfo.RegionLocY == y))
                    return region.RegionInfo;
            }
            return null;
        }

        // Called with _knownNeighbors already locked.
        // Returns true if anything added to visibleNeighbors
        private void AddVisibleRegion(SimpleRegionInfo[,] visibleNeighbors, bool[,] inspected, uint x, uint y, uint xmin, uint xmax, uint ymin, uint ymax)
        {
            if ((x < xmin) || (x > xmax) || (y < ymin) || (y > ymax))
                return;     // off the grid, nothing to do

            // visibleNeighbors[] and inspected[] arrays use 0-based coordinates.
            uint xmap = x - xmin;
            uint ymap = y - ymin;
            if (inspected[xmap, ymap])
                return;     // already did this one

            inspected[xmap, ymap] = true;

            SimpleRegionInfo neighbor = FindKnownNeighbor(x, y);
            if (neighbor == null)
                return;     // region not present

            visibleNeighbors[xmap, ymap] = neighbor;

            AddVisibleNeighbors(visibleNeighbors, inspected, x, y, xmin, xmax, ymin, ymax);
        }

        // Called with _knownNeighbors already locked.
        // Returns true if anything added to visibleNeighbors
        private void AddVisibleNeighbors(SimpleRegionInfo[,] visibleNeighbors, bool[,] inspected, uint x, uint y, uint xmin, uint xmax, uint ymin, uint ymax)
        {
            // Visibility rules are view must pass through at least one horizontal or vertical neighbor.
            AddVisibleRegion(visibleNeighbors, inspected, x, y - 1, xmin, xmax, ymin, ymax);
            AddVisibleRegion(visibleNeighbors, inspected, x, y + 1, xmin, xmax, ymin, ymax);
            AddVisibleRegion(visibleNeighbors, inspected, x - 1, y, xmin, xmax, ymin, ymax);
            AddVisibleRegion(visibleNeighbors, inspected, x + 1, y, xmin, xmax, ymin, ymax);
        }

        /// <summary>
        /// Returns a list of neighbors we are aware of that are within the given client draw distance from any
        /// of our edges
        /// </summary>
        /// <param name="drawDistance">DD in meters</param>
        /// <returns>List of known neighbors</returns>
        public List<SimpleRegionInfo> GetKnownNeighborsWithinClientDD(uint drawDistance, uint maxRange)
        {
            drawDistance = Math.Max(drawDistance, 64);

            drawDistance = Math.Min(drawDistance, MAX_DRAW_DISTANCE);

            uint xmin, xmax, ymin, ymax;
            var regionInfo = _scene.RegionInfo;
            Util.GetDrawDistanceBasedRegionRectangle(drawDistance, maxRange, regionInfo.RegionLocX, regionInfo.RegionLocY, out xmin, out xmax, out ymin, out ymax);

            uint gridsize = xmax - xmin + 1;
            uint center = (xmax - xmin) / 2;
            List<SimpleRegionInfo> neighbors = new List<SimpleRegionInfo>();

            // visibleNeighbors[] and inspected[] arrays use 0-based coordinates.
            SimpleRegionInfo[,] visibleNeighbors = new SimpleRegionInfo[gridsize, gridsize];

            lock (_knownNeighbors)
            {
                foreach (KnownNeighborRegion neighbor in _knownNeighbors.Values)
                {
                    if (Util.IsWithinDDRectangle(neighbor.RegionInfo.RegionLocX, neighbor.RegionInfo.RegionLocY,
                        xmin, xmax, ymin, ymax))
                    {
                        //region within bounds
                        neighbors.Add(neighbor.RegionInfo);
                    }
                }

                // Apply per-presence region visibility filter
                bool[,] inspected = new bool[gridsize, gridsize];   // context for recursive call

                // The starting/center point in the visibility grid is always included.
                visibleNeighbors[center, center] = new SimpleRegionInfo(regionInfo);
                inspected[center, center] = true;

                // Recursively path-find all visible neighbors.
                AddVisibleNeighbors(visibleNeighbors, inspected, regionInfo.RegionLocX, regionInfo.RegionLocY, xmin, xmax, ymin, ymax);
            }

            // Now replace the full list of neighbors with the regions in the filtered visible array.
            neighbors.Clear();
            foreach (var region in visibleNeighbors)
                if ((region != null) && (region.RegionHandle != regionInfo.RegionHandle)) // got one and it's not this one
                    neighbors.Add(region);
            return neighbors;
        }

        /// <summary>
        /// Returns the number of regions that we know about surrounding this region within MAX_DRAW_DISTANCE
        /// </summary>
        /// <returns></returns>
        public int GetKnownNeighborCount()
        {
            lock (_knownNeighbors)
            {
                return _knownNeighbors.Count;
            }
        }

    }
}
