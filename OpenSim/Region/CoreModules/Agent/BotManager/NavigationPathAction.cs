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
using OpenSim.Region.Framework.Interfaces;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.Agent.BotManager
{
    public class NavigationPathAction : MovementAction
    {
        private NavigationPathDescription m_description;

        public class NavigationPathDescription : MovementDescription
        {
            public List<Vector3> Nodes { get; private set; }
            public List<TravelMode> TravelModes { get; private set; }
            public bool ContinueIndefinitely { get; private set; }

            public NavigationPathDescription(List<Vector3> nodes, List<TravelMode> travelModes, Dictionary<int, object> options)
            {
                ContinueIndefinitely = false;
                this.Nodes = nodes;
                this.TravelModes = travelModes;

                const int BOT_MOVEMENT_TYPE = 0;
                const int BOT_MOVEMENT_TELEPORT_AFTER = 1;
                //const int BOT_MOVEMENT_FLAG_NONE = 0;
                const int BOT_MOVEMENT_FLAG_FOLLOW_INDEFINITELY = 1;

                foreach (KeyValuePair<int, object> kvp in options)
                {
                    switch (kvp.Key)
                    {
                        case BOT_MOVEMENT_TYPE:
                            if (kvp.Value is int)
                                ContinueIndefinitely = ((int)kvp.Value) == BOT_MOVEMENT_FLAG_FOLLOW_INDEFINITELY ? true : false;
                            break;
                        case BOT_MOVEMENT_TELEPORT_AFTER:
                            if (kvp.Value is float)
                                TimeBeforeTeleportToNextPositionOccurs = (float)kvp.Value;
                            else if (kvp.Value is int)
                                TimeBeforeTeleportToNextPositionOccurs = (float)(int)kvp.Value;
                            break;
                    }
                }
            }
        }

        public NavigationPathAction(MovementDescription desc, BotMovementController controller)
            : base(desc, controller)
        {
            m_description = (NavigationPathDescription)desc;
        }

        public override void Start()
        {
            m_nodeGraph.Clear();

            m_nodeGraph.FollowIndefinitely = m_description.ContinueIndefinitely;
            m_nodeGraph.AddRange(m_description.Nodes, m_description.TravelModes);
        }

        public override void Stop()
        {
            m_nodeGraph.Clear();

            ScenePresence botPresence = m_controller.Scene.GetScenePresence(m_controller.Bot.AgentID);
            if (botPresence != null)
            {
                var pa = botPresence.PhysicsActor;
                StopMoving(botPresence, pa != null && pa.Flying, true);
            }
        }

        public override void CheckInformationBeforeMove()
        {
        }

        public override void UpdateInformation()
        {
        }
    }
}
