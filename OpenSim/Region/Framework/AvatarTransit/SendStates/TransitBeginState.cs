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
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.Framework.AvatarTransit.SendStates
{
    /// <summary>
    /// State that runs when an avatar is beginning a send-transit
    /// </summary>
    internal class TransitBeginState : TransitStateBase
    {
        private InTransitAvatar _avatar;

        public TransitBeginState(InTransitAvatar avatar)
        {
            _avatar = avatar;
        }

        public override async Task StateEntry()
        {
            //no matter what happens at this point we want to make sure the avatar doesnt try to immediately recross should we have
            //a failure. bounce them off the border
            Vector3 pos = _avatar.ScenePresence.AbsolutePosition;
            if (_avatar.TransitArgs.Type == TransitType.OutboundCrossing && _avatar.TransitArgs.RideOnGroup == null)
            {
                Vector3 backVel = -_avatar.ScenePresence.Velocity;
                RollbackActions.Push(() =>
                {
                    Vector3 newpos = pos + (backVel * 2.0f);
                    // Check if "bounce back leaves region". If so, don't apply bounce.
                    if (!Util.IsValidRegionXY(newpos))
                        newpos = pos;    // just limit current pos to valid.
                    Util.ForceValidRegionXYZ(ref newpos);
                    _avatar.ScenePresence.AbsolutePosition = newpos;
                });
            }

            //assert that this avatar is fully in this region before beginning a send
            if (_avatar.ScenePresence.Connection.State != OpenSim.Framework.AvatarConnectionState.Established)
            {
                throw new InvalidOperationException("An avatar can not begin transition to a new region while already in transit");
            }

            //assert that this avatar is ready to leave the region
            if (!_avatar.ScenePresence.CanExitRegion)
            {
//                _avatar.ScenePresence.ControllingClient.SendAlertMessage("Can not move to a new region, until established in the current region");
                throw new InvalidOperationException("An avatar can not begin transition to a new region until established in the current region");
            }

            //assert that the dest region is available and this avatar has an established connection to that region
            if (_avatar.ScenePresence.RemotePresences.HasConnectionsEstablishing())
            {
//                _avatar.ScenePresence.ControllingClient.SendAlertMessage("Can not move to a new region, connections are still being established");
                throw new InvalidOperationException("An avatar can not begin transition to a neighbor region while the connections are still being established");
            }

            //if we're riding on a prim, wait for the all clear before moving on
            if (_avatar.TransitArgs.RideOnPart != null)
            {
                bool success = await _avatar.WaitForRemoteObjectCreation();
                if (!success)
                {
                    _avatar.ScenePresence.ControllingClient.SendAlertMessage("Unable to create object in remote region");
                    throw new SendAvatarException("Remote object creation failed");
                }
            }

            //listeners to this event will stop all traffic and suspend physics for the avatar
            //there is nothing else we need to do in this state
            await _avatar.TriggerOnTransitStageChanged(TransitStage.SendBegin, _avatar.RideOnPrims);

            RollbackActions.Push(async () =>  await _avatar.TriggerOnTransitStageChanged(TransitStage.SendError, _avatar.RideOnPrims));

            var nextState = new SendAvatarState(_avatar, this);
            await _avatar.SetNewState(nextState);
        }
    }
}
