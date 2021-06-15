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

using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace OpenSim.Region.Framework.AvatarTransit.SendStates
{
    internal class SendAvatarState : TransitStateBase
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private InTransitAvatar _avatar;

        public SendAvatarState(InTransitAvatar _avatar, TransitStateBase progenitor)
        {
            this._avatar = _avatar;
            this.Progenitor = progenitor;
        }

        public override async Task StateEntry()
        {
            await _avatar.TriggerOnTransitStageChanged(TransitStage.SendEstablishChildPresence, _avatar.RideOnPrims);

            SimpleRegionInfo destination = _avatar.TransitArgs.DestinationRegion;

            //do we have a presence on the destination?
            if (!_avatar.ScenePresence.RemotePresences.HasPresenceOnRegion(destination.RegionHandle))
            {
                //no, we need to establish a new presence
                Tuple<EstablishPresenceResult, string> result = 
                    await _avatar.ScenePresence.RemotePresences.EstablishPresenceOnRegionLocked(destination, false, true);

                if (result.Item1 != EstablishPresenceResult.Success)
                {
                    //something broke 
                    _avatar.ScenePresence.ControllingClient.SendAlertMessage(
                        "Unable to complete transfer to new region: " + result.Item2);

                    throw new SendAvatarException(
                        String.Format("Could not establish presence on remote region: {0}", result.Item2));
                }

                RollbackActions.Push(async () => await _avatar.ScenePresence.RemotePresences.DropRemotePresenceLocked(destination, true));
            }

            AvatarRemotePresence remotePresence = null;
            _avatar.ScenePresence.RemotePresences.TryGetRemotePresenceLocked(destination.RegionHandle, 
                (AvatarRemotePresence pres) => 
                    {
                        remotePresence = pres;
                    }
            );

            if (remotePresence == null)
            {
                //something is horked
                throw new SendAvatarException(
                    String.Format("Presence could not be established on new region for {0}", _avatar.ScenePresence.Name));
            }

            //we have a presence now, we can send the child agent update
            await _avatar.TriggerOnTransitStageChanged(TransitStage.SendAvatarHandoff, _avatar.RideOnPrims);

            //the ChildAgentUpdate below will always stop attachment scripts to transmit their state
            //if anything from this point on fails, we need to start the scripts running again
            RollbackActions.Push(() =>
                {
                    List<SceneObjectGroup> attachments = _avatar.ScenePresence.GetAttachments();
                    foreach (var att in attachments)
                    {
                        att.EndTransit(false);
                    }
                }
            );

            // Invoke the agent2 entry point
            ChildAgentUpdate2Response rc = this.SendChildAgentUpdate2();
            switch (rc)
            {
                case ChildAgentUpdate2Response.Ok:
                    break;  // continue normally
                case ChildAgentUpdate2Response.AccessDenied:
                    throw new SendAvatarException(
                        String.Format("Region entry denied for {0}", _avatar.ScenePresence.Name));
                case ChildAgentUpdate2Response.MethodNotAvailalble:
                    throw new SendAvatarException(
                        String.Format("Region change not available for {0}", _avatar.ScenePresence.Name));
                case ChildAgentUpdate2Response.Error:
                default:
                    throw new SendAvatarException(
                        String.Format("Region change failed for {0}", _avatar.ScenePresence.Name));
            }

            //this avatar is now considered a child agent
            _avatar.ScenePresence.MakeChildAgent(_avatar.TransitArgs.DestinationRegion.RegionHandle);

            //if there is a failure, we will need to restore the user as a root agent
            Vector3 restorePos = _avatar.ScenePresence.AbsolutePosition;
            Util.ForceValidRegionXY(ref restorePos);

            RollbackActions.Push(() => { _avatar.ScenePresence.MakeRootAgent(restorePos); });

            //the user is ready to be transfered
            IEventQueue eq = _avatar.ScenePresence.Scene.RequestModuleInterface<IEventQueue>();

            bool eventWasQueued = false;
            switch (_avatar.TransitArgs.Type)
            {
                case TransitType.OutboundCrossing:
                    eventWasQueued = eq.CrossRegion(_avatar.TransitArgs.DestinationRegion.RegionHandle,
                        _avatar.TransitArgs.LocationInDestination,
                        _avatar.ScenePresence.Velocity,
                        _avatar.TransitArgs.DestinationRegion.ExternalEndPoint,
                        remotePresence.PresenceInfo.FullCapsSeedURL,
                        _avatar.ScenePresence.UUID,
                        _avatar.ScenePresence.ControllingClient.SessionId);
                    break;

                case TransitType.OutboundTeleport:
                    eventWasQueued = eq.TeleportFinishEvent(_avatar.TransitArgs.DestinationRegion.RegionHandle,
                        13,
                        _avatar.TransitArgs.DestinationRegion.ExternalEndPoint,
                        4,
                        (uint)_avatar.TransitArgs.TeleportFlags,
                        remotePresence.PresenceInfo.FullCapsSeedURL,
                        _avatar.ScenePresence.UUID);
                    break;

                default:
                    throw new SendAvatarException(String.Format("Invalid transit type {0} for sending avatar {1}",
                        _avatar.TransitArgs.Type));
            }

            if (!eventWasQueued)
            {
                throw new SendAvatarException(String.Format("Unable to enqueue transfer event for {0}",
                        _avatar.ScenePresence.Name));
            }

            //wait for confirmation of avatar on the other side
            await _avatar.WaitForRelease();

            //matching endtransit for all attachments
            List<SceneObjectGroup> sentAttachments = _avatar.ScenePresence.GetAttachments();
            foreach (var att in sentAttachments)
            {
                att.EndTransit(true);
            }

            _avatar.ScenePresence.AttachmentsCrossedToNewRegion();
            
            //unsit the SP if appropriate
            if (_avatar.TransitArgs.RideOnPart != null)
            {
                _avatar.TransitArgs.RideOnPart.RemoveSeatedAvatar(_avatar.ScenePresence, false);
            }

            //this avatar is history.
            _avatar.ScenePresence.Reset(_avatar.TransitArgs.DestinationRegion);

            _avatar.ScenePresence.Scene.EventManager.TriggerAvatarLeavingRegion(_avatar.ScenePresence, 
                _avatar.TransitArgs.DestinationRegion);
        }

        private ChildAgentUpdate2Response SendChildAgentUpdate2()
        {
            ScenePresence agent = _avatar.ScenePresence;
            SceneObjectGroup sceneObjectGroup = _avatar.TransitArgs.RideOnGroup;
            SceneObjectPart part = _avatar.TransitArgs.RideOnPart;
            ulong newRegionHandle = _avatar.TransitArgs.DestinationRegion.RegionHandle;
            SimpleRegionInfo neighbourRegion = _avatar.TransitArgs.DestinationRegion;
            Vector3 pos = _avatar.TransitArgs.LocationInDestination;
            AgentLocomotionFlags locomotionFlags = 0;

            if (_avatar.TransitArgs.Type == TransitType.OutboundCrossing)
            {
                locomotionFlags = AgentLocomotionFlags.Crossing;
            }
            else if (_avatar.TransitArgs.Type == TransitType.OutboundTeleport)
            {
                locomotionFlags = AgentLocomotionFlags.Teleport;
            }

            AgentData cAgent = new AgentData();
            agent.CopyToForRootAgent(cAgent);

            if (part == null)
                cAgent.Position = pos;

            cAgent.LocomotionState = 1;
            cAgent.LocomotionFlags = locomotionFlags;

            List<SceneObjectGroup> attachments = agent.CollectAttachmentsForCrossing();

            //try the new comms first
            var engine = ProviderRegistry.Instance.Get<ISerializationEngine>();

            if (engine == null)
            {
                _log.ErrorFormat("[SCENE COMM]: Cannot send child agent update to {0}, Serialization engine is missing!", 
                    neighbourRegion.RegionHandle);
                return ChildAgentUpdate2Response.Error;
            }

            List<byte[]> serializedAttachments = new List<byte[]>();
            foreach (var att in attachments)
            {
                //mark the SOG in-transit. this along with the serialization below sends a disable to the script engine, but they are not cumulative 
                att.StartTransit();

                //we are stopping the scripts as part of the serialization process here
                //this means that later on, should the remote creation call fail, we need to re-enable them
                //reenabling is done via EndTransit with success==false
                byte[] sogBytes = engine.SceneObjectSerializer.SerializeGroupToBytes(att, SerializationFlags.FromCrossing | SerializationFlags.StopScripts | SerializationFlags.SerializeScriptBytecode);
                serializedAttachments.Add(sogBytes);
            }

            cAgent.SerializedAttachments = serializedAttachments;
            
            var scene = agent.Scene;

            cAgent.CallbackURI = scene.RegionInfo.InsecurePublicHTTPServerURI +
                "/agent/" + agent.UUID.ToString() + "/" + agent.Scene.RegionInfo.RegionHandle.ToString() + "/release/";

            ChildAgentUpdate2Response resp = scene.InterregionComms.SendChildAgentUpdate2(neighbourRegion, cAgent);

            if (resp == ChildAgentUpdate2Response.Error)
            {
                _log.ErrorFormat("[SCENE COMM]: Error sending child agent update to {0}", neighbourRegion.RegionHandle);
            }
            else if (resp == ChildAgentUpdate2Response.MethodNotAvailalble)
            {
                _log.ErrorFormat("[SCENE COMM]: Error sending child agent update to {0}, ChildAgentUpdate2 not available. Falling back to old method", neighbourRegion.RegionHandle);
            }

            return resp;
        }
    }
}
