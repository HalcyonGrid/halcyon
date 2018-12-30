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
using System.Net;
using System.Reflection;
using System.Threading;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using log4net;
using OpenSim.Framework;
using OpenSim.Framework.Communications;
using OpenSim.Framework.Communications.Cache;
using OpenSim.Framework.Communications.Capabilities;
using OpenSim.Region.Framework.Interfaces;
using OSD = OpenMetaverse.StructuredData.OSD;
using OpenSim.Region.Framework.Scenes.Serialization;
using System.Threading.Tasks;

namespace OpenSim.Region.Framework.Scenes
{
    public delegate void KiPrimitiveDelegate(uint localID, SimpleRegionInfo movingToRegion);

    public delegate void RemoveKnownRegionsFromAvatarList(UUID avatarID, List<ulong> regionlst);

    public class LandingType
    {
        public const byte Blocked = 0;
        public const byte LandingPoint = 1;
        public const byte Anywhere = 2;
    }

    public class SceneCommunicationService //one instance per region
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected CommunicationsManager m_commsProvider;
        protected IInterregionCommsOut m_interregionCommsOut;
        protected RegionInfo m_regionInfo;

        protected RegionCommsListener regionCommsHost;

        protected List<UUID> m_agentsInTransit;

        public event AgentCrossing OnAvatarCrossingIntoRegion;
        public event ExpectUserDelegate OnExpectUser;
        public event ExpectPrimDelegate OnExpectPrim;
        public event CloseAgentConnection OnCloseAgentConnection;
        public event PrimCrossing OnPrimCrossingIntoRegion;
        public event RegionUp OnRegionUp;
        public event ChildAgentUpdate OnChildAgentUpdate;
        //public event RemoveKnownRegionsFromAvatarList OnRemoveKnownRegionFromAvatar;
        public event LogOffUser OnLogOffUser;
        public event GetLandData OnGetLandData;
        public event GetLandDataByID OnGetLandDataByID;

        private AgentCrossing handlerAvatarCrossingIntoRegion = null; // OnAvatarCrossingIntoRegion;
        private ExpectUserDelegate handlerExpectUser = null; // OnExpectUser;
        private ExpectPrimDelegate handlerExpectPrim = null; // OnExpectPrim;
        private CloseAgentConnection handlerCloseAgentConnection = null; // OnCloseAgentConnection;
        private PrimCrossing handlerPrimCrossingIntoRegion = null; // OnPrimCrossingIntoRegion;
        private RegionUp handlerRegionUp = null; // OnRegionUp;
        private ChildAgentUpdate handlerChildAgentUpdate = null; // OnChildAgentUpdate;
        //private RemoveKnownRegionsFromAvatarList handlerRemoveKnownRegionFromAvatar = null; // OnRemoveKnownRegionFromAvatar;
        private LogOffUser handlerLogOffUser = null;
        private GetLandData handlerGetLandData = null; // OnGetLandData
        private GetLandDataByID handlerGetLandDataByID = null; // OnGetLandDataByID

        public KiPrimitiveDelegate KiPrimitive;

        public SceneCommunicationService(CommunicationsManager commsMan)
        {
            m_commsProvider = commsMan;
            m_agentsInTransit = new List<UUID>();
        }

        public void UnitTest_SetCommsOut(IInterregionCommsOut comms_out)
        {
            m_interregionCommsOut = comms_out;
        }

        /// <summary>
        /// Register a region with the grid
        /// </summary>
        /// <param name="regionInfos"></param>
        /// <exception cref="System.Exception">Thrown if region registration fails.</exception>
        public void RegisterRegion(IInterregionCommsOut comms_out, RegionInfo regionInfos)
        {
            m_interregionCommsOut = comms_out;

            m_regionInfo = regionInfos;
            m_commsProvider.GridService.gdebugRegionName = regionInfos.RegionName;
            regionCommsHost = m_commsProvider.GridService.RegisterRegion(m_regionInfo);

            if (regionCommsHost != null)
            {
                //m_log.Info("[INTER]: " + debugRegionName + ": SceneCommunicationService: registered with gridservice and got" + regionCommsHost.ToString());

                regionCommsHost.debugRegionName = regionInfos.RegionName;
                regionCommsHost.OnExpectPrim += IncomingPrimCrossing;
                regionCommsHost.OnExpectUser += NewUserConnection;
                regionCommsHost.OnAvatarCrossingIntoRegion += AgentCrossing;
                regionCommsHost.OnCloseAgentConnection += CloseConnection;
                regionCommsHost.OnRegionUp += newRegionUp;
                regionCommsHost.OnChildAgentUpdate += ChildAgentUpdate;
                regionCommsHost.OnLogOffUser += GridLogOffUser;
                regionCommsHost.OnGetLandData += FetchLandData;
                regionCommsHost.OnGetLandDataByID += FetchLandDataByID;
            }
            else
            {
                //m_log.Info("[INTER]: " + debugRegionName + ": SceneCommunicationService: registered with gridservice and got null");
            }
        }

        public RegionInfo RequestClosestRegion(string name)
        {
            return m_commsProvider.GridService.RequestClosestRegion(name);
        }

        public void Close()
        {
            if (regionCommsHost != null)
            {
                regionCommsHost.OnLogOffUser -= GridLogOffUser;
                regionCommsHost.OnChildAgentUpdate -= ChildAgentUpdate;
                regionCommsHost.OnRegionUp -= newRegionUp;
                regionCommsHost.OnExpectUser -= NewUserConnection;
                regionCommsHost.OnExpectPrim -= IncomingPrimCrossing;
                regionCommsHost.OnAvatarCrossingIntoRegion -= AgentCrossing;
                regionCommsHost.OnCloseAgentConnection -= CloseConnection;
                regionCommsHost.OnGetLandData -= FetchLandData;
                regionCommsHost.OnGetLandDataByID -= FetchLandDataByID;
                
                try
                {
                    m_commsProvider.GridService.DeregisterRegion(m_regionInfo);
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat(
                        "[GRID]: Deregistration of region {0} from the grid failed - {1}.  Continuing", 
                        m_regionInfo.RegionName, e);
                }
                
                regionCommsHost = null;
            }
        }

        #region CommsManager Event handlers

        /// <summary>
        ///
        /// </summary>
        /// <param name="regionHandle"></param>
        /// <param name="agent"></param>
        ///
        protected void NewUserConnection(AgentCircuitData agent)
        {
            handlerExpectUser = OnExpectUser;
            if (handlerExpectUser != null)
            {
                //m_log.Info("[INTER]: " + debugRegionName + ": SceneCommunicationService: OnExpectUser Fired for User:" + agent.firstname + " " + agent.lastname);
                handlerExpectUser(agent);
            }
        }

        protected void GridLogOffUser(UUID AgentID, UUID RegionSecret, string message)
        {
            handlerLogOffUser = OnLogOffUser;
            if (handlerLogOffUser != null)
            {
                handlerLogOffUser(AgentID, RegionSecret, message);
            }
        }

        protected bool newRegionUp(RegionInfo region)
        {
            handlerRegionUp = OnRegionUp;
            if (handlerRegionUp != null)
            {
                //m_log.Info("[INTER]: " + debugRegionName + ": SceneCommunicationService: newRegionUp Fired for User:" + region.RegionName);
                handlerRegionUp(region);
            }
            return true;
        }

        protected bool ChildAgentUpdate(ChildAgentDataUpdate cAgentData)
        {
            handlerChildAgentUpdate = OnChildAgentUpdate;
            if (handlerChildAgentUpdate != null)
                handlerChildAgentUpdate(cAgentData);


            return true;
        }

        protected void AgentCrossing(UUID agentID, Vector3 position, bool isFlying)
        {
            handlerAvatarCrossingIntoRegion = OnAvatarCrossingIntoRegion;
            if (handlerAvatarCrossingIntoRegion != null)
            {
                handlerAvatarCrossingIntoRegion(agentID, position, isFlying);
            }
        }

        protected bool IncomingPrimCrossing(UUID primID, String objXMLData, int XMLMethod)
        {
            handlerExpectPrim = OnExpectPrim;
            if (handlerExpectPrim != null)
            {
                return handlerExpectPrim(primID, objXMLData, XMLMethod);
            }
            else
            {
                return false;
            }

        }

        protected void PrimCrossing(UUID primID, Vector3 position, bool isPhysical)
        {
            handlerPrimCrossingIntoRegion = OnPrimCrossingIntoRegion;
            if (handlerPrimCrossingIntoRegion != null)
            {
                handlerPrimCrossingIntoRegion(primID, position, isPhysical);
            }
        }

        protected bool CloseConnection(UUID agentID)
        {
            m_log.Debug("[INTERREGION]: Incoming Agent Close Request for agent: " + agentID);

            handlerCloseAgentConnection = OnCloseAgentConnection;
            if (handlerCloseAgentConnection != null)
            {
                return handlerCloseAgentConnection(agentID);
            }
            
            return false;
        }

        protected LandData FetchLandData(uint x, uint y)
        {
            handlerGetLandData = OnGetLandData;
            if (handlerGetLandData != null)
            {
                return handlerGetLandData(x, y);
            }
            return null;
        }

        protected LandData FetchLandDataByID(int localLandID)
        {
            handlerGetLandDataByID = OnGetLandDataByID;
            if (handlerGetLandDataByID != null)
            {
                return handlerGetLandDataByID(localLandID);
            }
            return null;
        }

        #endregion

        #region Inform Client of Neighbours

        public async Task<Tuple<EstablishPresenceResult, string>> EstablishChildConnectionToRegionAsync(ScenePresence avatar, AgentCircuitData a, SimpleRegionInfo reg)
        {
            string capsPath = CapsUtil.GetFullCapsSeedURL(reg.InsecurePublicHTTPServerURI, a.CapsPath);

            Tuple<bool, string> createResult = await m_interregionCommsOut.SendCreateRemoteChildAgentAsync(reg, a);

            if (createResult.Item1)
            {
                IPEndPoint endPoint = reg.ExternalEndPoint;

                IEventQueue eq = avatar.Scene.RequestModuleInterface<IEventQueue>();
                if (!eq.EnableSimulator(reg.RegionHandle, endPoint, avatar.UUID))
                {
                    m_log.ErrorFormat("[INTERGRID]: Could not enqueue eq.EnableSimulator for {0}", avatar.Name);
                    return Tuple.Create(EstablishPresenceResult.ClientSignallingFailed, "Could not enqueue EnableSimulator");
                }

                if (! eq.EstablishAgentCommunication(avatar.UUID, endPoint, capsPath))
                {
                    m_log.ErrorFormat("[INTERGRID]: Could not enqueue eq.EstablishAgentCommunication for {0}", avatar.Name);
                    return Tuple.Create(EstablishPresenceResult.ClientSignallingFailed, "Could not enqueue EstablishAgentCommunication");
                }

                return Tuple.Create(EstablishPresenceResult.Success, String.Empty);
            }
            else
            {
                return Tuple.Create(EstablishPresenceResult.ErrorInformingRegion, createResult.Item2);
            }
        }

        /// <summary>
        /// This Closes child agents on neighboring regions
        /// Calls an asynchronous method to do so..  so it doesn't lag the sim.
        /// </summary>
        protected void SendCloseChildAgentAsync(UUID agentID, ulong regionHandle)
        {

            m_log.Debug("[INTERGRID]: Sending close agent to " + regionHandle);
            // let's do our best, but there's not much we can do if the neighbour doesn't accept.

            //m_commsProvider.InterRegion.TellRegionToCloseChildConnection(regionHandle, agentID);
            m_interregionCommsOut.SendCloseAgent(regionHandle, agentID);
        }

        #endregion

        public void SendUpdateEstateInfo(UUID regionID)
        {
            m_interregionCommsOut.SendUpdateEstateInfo(regionID);
        }

        public delegate void SendChildAgentDataUpdateDelegate(AgentPosition cAgentData, ulong regionHandle);

        /// <summary>
        /// This informs all neighboring regions about the settings of it's child agent.
        /// Calls an asynchronous method to do so..  so it doesn't lag the sim.
        ///
        /// This contains information, such as, Draw Distance, Camera location, Current Position, Current throttle settings, etc.
        ///
        /// </summary>
        private void SendChildAgentDataUpdateAsync(AgentPosition cAgentData, ulong regionHandle)
        {
            //m_log.Info("[INTERGRID]: Informing neighbors about my agent in " + m_regionInfo.RegionName);
            try
            {
                //m_commsProvider.InterRegion.ChildAgentUpdate(regionHandle, cAgentData);
                m_interregionCommsOut.SendChildAgentUpdate(regionHandle, cAgentData);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[INTERGRID]: Exception in SendChildAgentDataUpdateAsync. {0}", e);
                // Ignore; we did our best
            }
        }

        private void SendChildAgentDataUpdateCompleted(IAsyncResult iar)
        {
            SendChildAgentDataUpdateDelegate icon = (SendChildAgentDataUpdateDelegate) iar.AsyncState;
            icon.EndInvoke(iar);
        }

        public void SendChildAgentDataUpdate(AgentPosition cAgentData, ScenePresence presence)
        {
            foreach (ulong regionHandle in presence.KnownChildRegionHandles)
            {
                if (regionHandle != m_regionInfo?.RegionHandle)
                {
                    SendChildAgentDataUpdateDelegate d = SendChildAgentDataUpdateAsync;
                    d.BeginInvoke(cAgentData, regionHandle,
                                  SendChildAgentDataUpdateCompleted,
                                  d);
                }
            }
        }

       
        /// <summary>
        /// Helper function to request neighbors from grid-comms
        /// </summary>
        /// <param name="regionHandle"></param>
        /// <returns></returns>
        public virtual RegionInfo RequestNeighbouringRegionInfo(ulong regionHandle)
        {
            //m_log.Info("[INTER]: " + debugRegionName + ": SceneCommunicationService: Sending Grid Services Request about neighbor " + regionHandle.ToString());
            return m_commsProvider.GridService.RequestNeighbourInfo(regionHandle);
        }

        /// <summary>
        /// Helper function to request neighbors from grid-comms
        /// </summary>
        /// <param name="regionID"></param>
        /// <returns></returns>
        public virtual RegionInfo RequestNeighbouringRegionInfo(UUID regionID)
        {
            //m_log.Info("[INTER]: " + debugRegionName + ": SceneCommunicationService: Sending Grid Services Request about neighbor " + regionID);
            return m_commsProvider.GridService.RequestNeighbourInfo(regionID);
        }

        /// <summary>
        /// Requests map blocks in area of minX, maxX, minY, MaxY in world cordinates
        /// </summary>
        /// <param name="minX"></param>
        /// <param name="minY"></param>
        /// <param name="maxX"></param>
        /// <param name="maxY"></param>
        public virtual void RequestMapBlocks(IClientAPI remoteClient, int minX, int minY, int maxX, int maxY)
        {
            List<MapBlockData> mapBlocks;
            mapBlocks = m_commsProvider.GridService.RequestNeighbourMapBlocks(minX - 4, minY - 4, minX + 4, minY + 4);
            remoteClient.SendMapBlock(mapBlocks, 0);
        }

        

        public bool WaitForCallback(UUID id)
        {
//            return false;   // for testing failures
            int count = 100;

            int now = NextTickCheck(0, String.Empty); 
            lock (m_agentsInTransit)
            {
                now = NextTickCheck(now, "[SCENE COMM]: WaitForCallback - lock (m_agentsInTransit)");
                while (m_agentsInTransit.Contains(id) && count-- > 0)
                {
                    Monitor.Wait(m_agentsInTransit, 100);
                }
            }
            now = NextTickCheck(now, "[SCENE COMM]: WaitForCallback Wait");
            if (count > 0)
                return true;
            else
                return false;
        }

        private int NextTickCheck(int then, string msg)
        {
            int now = Environment.TickCount;
            if (then != 0)
            {
                int elapsed = now - then;
                if (elapsed >= 1000)
                    m_log.WarnFormat("{0} {1}ms", msg, elapsed);
            }
            return now;
        }

        public Dictionary<string, string> GetGridSettings()
        {
            return m_commsProvider.GridService.GetGridSettings();
        }

        public void LogOffUser(UUID userid, UUID regionid, ulong regionhandle, Vector3 position, Vector3 lookat)
        {
            m_commsProvider.LogOffUser(userid, regionid, regionhandle, position, lookat);
        }

        // deprecated as of 2008-08-27
        public void LogOffUser(UUID userid, UUID regionid, ulong regionhandle, float posx, float posy, float posz)
        {
             m_commsProvider.LogOffUser(userid, regionid, regionhandle, posx, posy, posz);
        }

        public void ClearUserAgent(UUID avatarID)
        {
            m_commsProvider.UserService.ClearUserAgent(avatarID);
        }

        public void AddNewUserFriend(UUID friendlistowner, UUID friend, uint perms)
        {
            m_commsProvider.AddNewUserFriend(friendlistowner, friend, perms);
        }

        public void UpdateUserFriendPerms(UUID friendlistowner, UUID friend, uint perms)
        {
            m_commsProvider.UpdateUserFriendPerms(friendlistowner, friend, perms);
        }

        public void RemoveUserFriend(UUID friendlistowner, UUID friend)
        {
            m_commsProvider.RemoveUserFriend(friendlistowner, friend);
        }

        public List<FriendListItem> GetUserFriendList(UUID friendlistowner)
        {
            return m_commsProvider.GetUserFriendList(friendlistowner);
        }

        public  List<MapBlockData> RequestNeighbourMapBlocks(int minX, int minY, int maxX, int maxY)
        {
            return m_commsProvider.GridService.RequestNeighbourMapBlocks(minX, minY, maxX, maxY);
        }

        public List<AvatarPickerAvatar> GenerateAgentPickerRequestResponse(UUID queryID, string query)
        {
            return m_commsProvider.GenerateAgentPickerRequestResponse(queryID, query);
        }
        
        public List<RegionInfo> RequestNamedRegions(string name, int maxNumber)
        {
            return m_commsProvider.GridService.RequestNamedRegions(name, maxNumber);
        }
    }
}
