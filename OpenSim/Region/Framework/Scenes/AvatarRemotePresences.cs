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
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Communications.Capabilities;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.Framework.Scenes
{
    /// <summary>
    /// Tracks the presences an avatar has on our known neighbor regions
    /// </summary>
    public class AvatarRemotePresences
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        
        /// <summary>
        /// Collection of the currently established remote presences for this user
        /// </summary>
        private Dictionary<ulong, AvatarRemotePresence> _remotePresences = new Dictionary<ulong, AvatarRemotePresence>();

        /// <summary>
        /// The current scene we're managing presences for this user on
        /// </summary>
        private Scene _scene;

        /// <summary>
        /// The local presence we're managing
        /// </summary>
        private ScenePresence _sp;

        /// <summary>
        /// The last number of regions we were able to see into given the user's draw distance
        /// </summary>
        private uint _lastDrawDistanceFactor;
        /// <summary>
        /// Similar to _lastDrawDistanceFactor, the previous value for the neighbors range
        /// </summary>
        private uint _lastNeighborsRange;

        /// <summary>
        /// Semaphore held during large ops to ensure only one major change happens at a time
        /// </summary>
        private SemaphoreSlim _operationSemaphore = new SemaphoreSlim(1, 1);


        public AvatarRemotePresences(Scene currentRegion, ScenePresence sp)
        {
            _scene = currentRegion;
            _sp = sp;
            _lastDrawDistanceFactor = Util.GetRegionUnitsFromDD((uint)sp.DrawDistance);
            _lastNeighborsRange = 0;

            _scene.EventManager.OnMakeRootAgent += EventManager_OnMakeRootAgent;
            _scene.EventManager.OnMakeChildAgent += EventManager_OnMakeChildAgent;
        }

        /// <summary>
        /// Called when a root SP is being constructed and we need to copy presences 
        /// from a previous region
        /// </summary>
        /// <param name="currentPresences"></param>
        public void SetInitialPresences(IEnumerable<RemotePresenceInfo> currentPresences)
        {
            if (_sp.IsBot) return;

            foreach (var pres in currentPresences)
            {
                //don't include our handle in the remote presences. this may be coming
                //from a neighbor region which had us as a remote pres
                if (pres.RegionInfo.RegionHandle != _scene.RegionInfo.RegionHandle)
                {
                    _remotePresences[pres.RegionInfo.RegionHandle] =
                        new AvatarRemotePresence
                        {
                            PresenceInfo = pres,
                            IsFarPresence = false,
                            State = RemotePresenceState.Established
                        };
                }
            }
        }

        /// <summary>
        /// Called when this presence is being changed to a root agent
        /// </summary>
        /// <param name="presence"></param>
        void EventManager_OnMakeRootAgent(ScenePresence presence)
        {
            if (presence.IsBot) return;

            if (presence == _sp)
            {
                //subscribe to changes in the surrounding region config
                _scene.SurroundingRegions.OnNeighborStateChange += SurroundingRegions_OnNeighborStateChange;

                //set up our initial connections to neighbors
                //let the task run async in the background
                const int CROSSING_RESYNC_DELAY = 500;
                this.CalculateAndResyncNeighbors((uint)presence.DrawDistance, presence.ControllingClient.NeighborsRange, CROSSING_RESYNC_DELAY);
            }
        }

        /// <summary>
        /// Called when this presence is leaving this region
        /// </summary>
        /// <param name="presence"></param>
        void EventManager_OnMakeChildAgent(ScenePresence presence)
        {
            if (presence.IsBot) return;

            //is it our presence that has been made into a child?
            if (presence == _sp)
            {
                StopManagingPresences();
            }
        }

        /// <summary>
        /// Unsubscribe to region changes and clear out presences, it is no longer up to us to manage this data
        /// </summary>
        private void StopManagingPresences()
        {
            _scene.SurroundingRegions.OnNeighborStateChange -= SurroundingRegions_OnNeighborStateChange;
            lock (_remotePresences)
            {
                _remotePresences.Clear();
            }
        }

        /// <summary>
        /// Called by our SP when it is closing
        /// </summary>
        internal void OnScenePresenceClosed()
        {
            if (_sp.IsBot) return;

            StopManagingPresences();

            _sp = null; // release the SP reference

            _scene.EventManager.OnMakeRootAgent -= EventManager_OnMakeRootAgent;
            _scene.EventManager.OnMakeChildAgent -= EventManager_OnMakeChildAgent;
        }

        /// <summary>
        /// Called by the surrounding region manager when there is a region state change
        /// </summary>
        /// <param name="neighbor"></param>
        /// <param name="changeType"></param>
        void SurroundingRegions_OnNeighborStateChange(SimpleRegionInfo neighbor, NeighborStateChangeType changeType)
        {
            switch (changeType)
            {
                case NeighborStateChangeType.NeighborUp:
                    this.HandleNeighborUp(neighbor);
                    break;

                case NeighborStateChangeType.NeighborDown:
                    this.HandleNeighborDown(neighbor);
                    break;
            }
        }

        /// <summary>
        /// Called when a neighbor has declared that it is down or a ping timed out. We should tell the client
        /// to remove these regions from its view and we remove the region presence from our collection
        /// </summary>
        /// <param name="neighbor"></param>
        private async Task HandleNeighborDown(SimpleRegionInfo neighbor)
        {
            // on any neighbor change, we need to recalculate all neighbors because 
            // visibility rules may have resulted in more than one new neighbor.
            await CalculateAndResyncNeighbors((uint)_sp.DrawDistance, _sp.ControllingClient.NeighborsRange, 0);
        }

        /// <summary>
        /// Drops the given presence taking the operation semaphore
        /// </summary>
        /// <param name="neighbor">The presence to drop</param>
        /// <param name="onlyIfFar">Only drop if the presence is a far presence</param>
        public async Task DropRemotePresenceLocked(SimpleRegionInfo neighbor, bool onlyIfFar)
        {
            try
            {
                await _operationSemaphore.WaitAsync();
                await DropRemotePresence(neighbor, onlyIfFar);
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Drops the given presence
        /// </summary>
        /// <param name="neighbor">The presence to drop</param>
        /// <param name="onlyIfFar">Only drop if the presence is a far presence</param>
        private async Task DropRemotePresence(SimpleRegionInfo neighbor, bool onlyIfFar)
        {
            AvatarRemotePresence foundPresence = null;

            TryGetRemotePresenceLocked(neighbor.RegionHandle, (AvatarRemotePresence presence) =>
            {
                foundPresence = presence;

                if (presence != null)
                {
                    if (onlyIfFar && !presence.IsFarPresence)
                    {
                        return;
                    }

                    //You can not send a disablesimulator for the remote region
                    //the viewer processes all disablesimulator messages coming from this
                    //region as a disablesimulator message FOR this region
                    //therefore, we remove the neighbor from our known regions list 
                    //and ask the sim on the other end to tear down the connection 
                    _remotePresences.Remove(neighbor.RegionHandle);
                }
            });

            if (foundPresence != null)
            {
                //send a close to the neighbor
                var intercom = _scene.InterregionComms;
                if (intercom != null)
                {
                    await intercom.SendCloseAgentAsync(neighbor, _sp.UUID);
                }
            }
        }

        private async Task HandleNeighborUp(SimpleRegionInfo neighbor)
        {
            // on any neighbor change, we need to recalculate all neighbors because 
            // visibility rules may have resulted in more than one new neighbor.
            await CalculateAndResyncNeighbors((uint)_sp.DrawDistance, _sp.ControllingClient.NeighborsRange, 0);
        }

        /// <summary>
        /// Does this user have an established presence on the given region?
        /// </summary>
        /// <param name="regionHandle"></param>
        /// <returns></returns>
        public bool HasPresenceOnRegion(ulong regionHandle)
        {
            bool hasPresence = false;

            TryGetRemotePresenceLocked(regionHandle, (AvatarRemotePresence presence) =>
            {
                if (presence != null)
                {
                    hasPresence = (presence.State == RemotePresenceState.Established);
                }
            });

            return hasPresence;
        }

        public void TryGetRemotePresenceLocked(ulong regionHandle, Action<AvatarRemotePresence> callback)
        {
            lock (_remotePresences)
            {
                AvatarRemotePresence presence;
                _remotePresences.TryGetValue(regionHandle, out presence);

                callback(presence);
            }
        }

        /// <summary>
        /// Attempts to establish a presence on the given region. Does this while waiting for other major tasks to complete
        /// </summary>
        /// <param name="region">The region we want to establish a child presence on</param>
        /// <param name="forceReestablish">Whether to force a reestablishment even if we already think we have a remote presence</param>
        /// <param name="isFarPresence">Is this presence intentionally far away? Eg. The beginning of a remote teleport</param>
        /// <returns></returns>
        public async Task<Tuple<EstablishPresenceResult, string>> EstablishPresenceOnRegionLocked(SimpleRegionInfo region, bool forceReestablish, bool isFarPresence)
        {
            try
            {
                await _operationSemaphore.WaitAsync();
                return await this.EstablishPresenceOnRegion(region, forceReestablish, isFarPresence);
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Attempts to establish a presence on the given region
        /// </summary>
        /// <param name="region">The region we want to establish a child presence on</param>
        /// <param name="forceReestablish">Whether to force a reestablishment even if we already think we have a remote presence</param>
        /// <param name="isFarPresence">Is this presence intentionally far away? Eg. The beginning of a remote teleport</param>
        /// <returns></returns>
        private async Task<Tuple<EstablishPresenceResult, string>> EstablishPresenceOnRegion(SimpleRegionInfo region, bool forceReestablish, bool isFarPresence)
        {
            Task<Tuple<EstablishPresenceResult, string>> establishTask = null;
            bool presenceExisted = false;

            //check if we already have or are waiting on an establish
            TryGetRemotePresenceLocked(region.RegionHandle, (AvatarRemotePresence presence) =>
            {
                if (presence != null && !forceReestablish)
                {
                    //we have a presence
                    //if it is established just return
                    if (presence.State == RemotePresenceState.Established)
                    {
                        presenceExisted = true;
                    }
                    else
                    {
                        //if not, we can await the existing callback
                        establishTask = presence.EstablishTask;
                    }
                }
                else
                {
                    //we have no presence and we're not waiting for a callback
                    //begin an async establish and await a callback

                    presence = new AvatarRemotePresence
                    {
                        PresenceInfo = new RemotePresenceInfo { RegionInfo = region, CapsPath = CapsUtil.GetRandomCapsObjectPath() },
                        IsFarPresence = isFarPresence,
                        State = RemotePresenceState.Establishing
                    };

                    if (_remotePresences.ContainsKey(region.RegionHandle))
                        _remotePresences.Remove(region.RegionHandle);
                    _remotePresences.Add(region.RegionHandle, presence);

                    establishTask = DoEstablishPresenceOnRegion(region, presence);
                    presence.EstablishTask = establishTask;
                }
            });

            //nothing to do, we're already established
            if (presenceExisted) return Tuple.Create(EstablishPresenceResult.Success, String.Empty);

            return await establishTask;
        }

        private async Task<Tuple<EstablishPresenceResult, string>> DoEstablishPresenceOnRegion(SimpleRegionInfo region, AvatarRemotePresence initPresence)
        {
            Tuple<EstablishPresenceResult, string> establishResult;

            try
            {
                establishResult = await this.LaunchNewEstablishChildTask(initPresence, region);
            }
            catch (Exception e)
            {
                establishResult = new Tuple<EstablishPresenceResult, string>(EstablishPresenceResult.ErrorInformingRegion, e.Message);
            }

            bool failure = false;

            TryGetRemotePresenceLocked(region.RegionHandle, (AvatarRemotePresence presence) =>
            {
                //success, change the status of the task
                if (presence != null)
                {
                    if (establishResult.Item1 == EstablishPresenceResult.Success)
                    {
                        presence.State = RemotePresenceState.ViewerWait;
                    }
                    else
                    {
                        //failure contacting other region
                        _remotePresences.Remove(region.RegionHandle);
                        failure = true;
                    }
                }
                else
                {
                    string context = (_sp == null) ? String.Empty : " for "+_sp.Name;
                    failure = true;

                    //hmm, someone stole this presence from us
                    _log.ErrorFormat("[REMOTEPRESENCE]: Unable to update child presence established to {0}{1}. Child presence missing.", establishResult, context);
                    establishResult = Tuple.Create(EstablishPresenceResult.ConnectionAborted, "Connection was aborted");
                }
            });

            if (failure)
            {
                return establishResult;
            }

            //now we need to call out to the remote region to wait for the SP to be set up
            bool waitSuccess = await WaitForScenePresenceEstablished(region);

            Tuple<EstablishPresenceResult, string> result = null;

            TryGetRemotePresenceLocked(region.RegionHandle, (AvatarRemotePresence presence) =>
            {
                //success, change the status of the task
                if (presence != null)
                {
                    if (waitSuccess)
                    {
                        presence.State = RemotePresenceState.Established;
                        result = Tuple.Create(EstablishPresenceResult.Success, String.Empty);
                    }
                    else
                    {
                        //failure waiting for SP
                        _remotePresences.Remove(region.RegionHandle);
                        result = Tuple.Create(EstablishPresenceResult.ClientWaitTimeout, "Destination region never received a connection from the viewer");
                    }
                }
                else
                {
                    //hmm, someone stole this presence from us
                    _log.ErrorFormat("[REMOTEPRESENCE]: Unable to update child presence established to {0} for {1}. Child presence missing.", establishResult, _sp.Name);
                    result = Tuple.Create(EstablishPresenceResult.ConnectionAborted, "Connection was aborted");
                }
            });

            return result;
        }

        private async Task<bool> WaitForScenePresenceEstablished(SimpleRegionInfo region)
        {
            //this is the HTTP timeout, however the actual wait timeout on the receiving side is 10 seconds
            //so that timeout should be triggered first
            const int HTTP_SP_WAIT_TIMEOUT = 15000;

            var req = (HttpWebRequest)HttpWebRequest.Create(region.InsecurePublicHTTPServerURI + String.Format("/agent2/{0}/{1}", _sp.UUID, region.RegionHandle));
            req.Headers["authorization"] = Util.GenerateHttpAuthorization(_scene.GridSendKey);
            req.Timeout = HTTP_SP_WAIT_TIMEOUT;
            req.ReadWriteTimeout = HTTP_SP_WAIT_TIMEOUT;
            req.Method = "GET";

            try
            {
                using (WebResponse response = await req.GetResponseAsync(HTTP_SP_WAIT_TIMEOUT))
                {
                    //we do nothing besides dispose on success. a 200 response means all is well
                }

                return true;
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[REMOTEPRESENCE]: Waiting for viewer connection from {0} to {1} failed: {2}", _sp.Name, region.RegionHandle, e);
                return false;
            }
        }

        private Task<Tuple<EstablishPresenceResult, string>> LaunchNewEstablishChildTask(AvatarRemotePresence presence, SimpleRegionInfo region)
        {
            AgentCircuitData agent = _sp.ControllingClient.RequestClientInfo();
            agent.BaseFolder = UUID.Zero;
            agent.InventoryFolder = UUID.Zero;
            agent.startpos = Scene.DEFAULT_CHILD_AGENT_POS;
            agent.child = true;
            agent.CapsPath = presence.PresenceInfo.CapsPath;

            return _scene.SceneGridService.EstablishChildConnectionToRegionAsync(_sp, agent, region);
        }

        /// <summary>
        /// The user's draw distance has changed, we may need to drop or add some regions
        /// </summary>
        /// <param name="newDrawDistance"></param>
        public async Task HandleDrawDistanceChanged(uint newDrawDistance)
        {
            if (_sp.IsBot) return;

            uint factor = Util.GetRegionUnitsFromDD(newDrawDistance);
            uint maxRange = _sp.ControllingClient.NeighborsRange;

            if ((_lastDrawDistanceFactor == factor) && (_lastNeighborsRange == maxRange))
            {
                //nothing to do
                return;
            }

            _lastDrawDistanceFactor = factor;
            _lastNeighborsRange = maxRange;
            await CalculateAndResyncNeighbors(newDrawDistance, maxRange, 0);
        }

        /// <summary>
        /// Resyncs the user with our view of the neighbors
        /// </summary>
        /// <param name="newDrawDistance">The new DD for the user</param>
        /// <param name="resyncDelay">Delay before executing the resync. We  delay on a region crossing because the viewer locks up sometimes when freeing memory</param>
        /// <returns></returns>
        private async Task CalculateAndResyncNeighbors(uint newDrawDistance, uint maxRange, int resyncDelay)
        {
            uint xmin, xmax, ymin, ymax;

            Util.GetDrawDistanceBasedRegionRectangle((uint)newDrawDistance, maxRange, _scene.RegionInfo.RegionLocX,
                _scene.RegionInfo.RegionLocY, out xmin, out xmax, out ymin, out ymax);

            //get our current neighbor list
            List<SimpleRegionInfo> knownNeighborsList = _scene.SurroundingRegions.GetKnownNeighborsWithinClientDD(newDrawDistance, maxRange);
            Dictionary<ulong, SimpleRegionInfo> knownNeighborsDict = new Dictionary<ulong, SimpleRegionInfo>();
            foreach (var neighbor in knownNeighborsList)
            {
                knownNeighborsDict.Add(neighbor.RegionHandle, neighbor);
            }

            HashSet<ulong> knownNeighbors = new HashSet<ulong>(knownNeighborsList.Select(x => x.RegionHandle));

            List<ulong> deadRegions;
            List<ulong> newRegions;

            lock (_remotePresences)
            {
                //check the list of what we have vs what we should have
                HashSet<ulong> usersRegions = new HashSet<ulong>();

                //add all regions from the presence
                foreach (var presence in _remotePresences.Values)
                {
                    knownNeighborsDict[presence.PresenceInfo.RegionInfo.RegionHandle] = presence.PresenceInfo.RegionInfo;

                    //dont put far regions into this update, they shouldnt be dropped by DD changes
                    if (!presence.IsFarPresence)
                    {
                        usersRegions.Add(presence.PresenceInfo.RegionInfo.RegionHandle);
                    }
                }

                // regions that we have but that we shouldnt have anymore
                deadRegions = new List<ulong>(usersRegions.Except(knownNeighbors));
                // regions that we don't have that we need to add
                newRegions = new List<ulong>(knownNeighbors.Except(usersRegions));
            }

            try
            {
                await _operationSemaphore.WaitAsync();
                if (resyncDelay > 0) await Task.Delay(resyncDelay);
                await this.ResyncRegions(knownNeighborsDict, deadRegions, newRegions);
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Resynchronizes our remote presences with the given lists
        /// </summary>
        /// <param name="knownNeighborsDict">All known regions from both lists</param>
        /// <param name="deadRegions">Regions that we should not longer have presences on</param>
        /// <param name="newRegions">Regions that we should have presences on but do not</param>
        /// <returns></returns>
        private async Task ResyncRegions(Dictionary<ulong, SimpleRegionInfo> knownNeighborsDict, List<ulong> deadRegions, List<ulong> newRegions)
        {
            List<Task<Tuple<EstablishPresenceResult, string>>> connectionTasks = new List<Task<Tuple<EstablishPresenceResult, string>>>(newRegions.Count);
            List<Task> disconnectTasks = new List<Task>();

            foreach (var region in deadRegions)
            {
                disconnectTasks.Add(this.DropRemotePresence(knownNeighborsDict[region], false));
            }

            foreach (var region in newRegions)
            {
                connectionTasks.Add(this.EstablishPresenceOnRegion(knownNeighborsDict[region], false, false));
            }

            await Task.WhenAll(connectionTasks);
            await Task.WhenAll(disconnectTasks);

            if (connectionTasks.Count > 0)
            {
                _sp?.SendChildAgentUpdate(); // send the other regions the intial avatar info (pos, draw distance) for culling
            }
        }

        /// <summary>
        /// Returns all remote presences we know about, including presences still establishing
        /// </summary>
        /// <returns></returns>
        public List<AvatarRemotePresence> GetRemotePresenceList()
        {
            lock (_remotePresences)
            {
                return new List<AvatarRemotePresence>(_remotePresences.Values);
            }
        }

        /// <summary>
        /// Returns only established presences
        /// </summary>
        /// <returns></returns>
        public List<AvatarRemotePresence> GetEstablishedRemotePresenceList()
        {
            List<AvatarRemotePresence> ret = new List<AvatarRemotePresence>();

            lock (_remotePresences)
            {
                foreach (var pres in _remotePresences.Values)
                {
                    if (pres.State == RemotePresenceState.Established)
                    {
                        ret.Add(pres);
                    }
                }
            }

            return ret;
        }

        /// <summary>
        /// Returns whether or not there are any connections currently being established
        /// </summary>
        /// <returns></returns>
        public bool HasConnectionsEstablishing()
        {
            if (_operationSemaphore.CurrentCount == 0) return true;

            lock (_remotePresences)
            {
                foreach (AvatarRemotePresence pres in _remotePresences.Values)
                {
                    if (pres.State != RemotePresenceState.Established)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public bool HasEstablishedConnection(SimpleRegionInfo region)
        {
            AvatarRemotePresence pres;
            lock (_remotePresences)
            {
                if (_remotePresences.TryGetValue(region.RegionHandle, out pres))
                {
                    return (pres.State == RemotePresenceState.Established);
                }
            }

            return false;
        }

        /// <summary>
        /// Requests that all remote regions we have children on terminate the child agents
        /// </summary>
        internal void TerminateAllNeighbors()
        {
            if (_sp.IsBot) return;

            List<AvatarRemotePresence> remotes = this.GetRemotePresenceList();
            foreach (var remote in remotes)
            {
                if (!remote.IsFarPresence)
                {
                    _scene.InterregionComms.SendCloseAgent(remote.PresenceInfo.RegionInfo.RegionHandle, _sp.UUID);
                }
            }
        }

        /// <summary>
        /// Returns the number of remote presences we have on other regions
        /// </summary>
        /// <returns></returns>
        public int GetRemotePresenceCount()
        {
            lock (_remotePresences)
            {
                return _remotePresences.Count;
            }
        }
    }
}
