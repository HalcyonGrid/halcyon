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
using OpenSim.Framework;

namespace OpenSim.Region.CoreModules.Agent.BotManager
{
    public class WanderingAction : MovementAction
    {
        private WanderingDescription m_description;

        public class WanderingDescription : MovementDescription
        {
            public Vector3 Origin { get; private set; }
            public Vector3 Distances { get; private set; }
            public TravelMode TravelMode { get; private set; }
            public float TimeBetweenNodeMovement { get; private set; }

            public WanderingDescription(Vector3 origin, Vector3 distances, Dictionary<int, object> options)
            {
                this.Origin = origin;
                this.Distances = distances;
                TravelMode = Region.Framework.Interfaces.TravelMode.Walk;
                TimeBetweenNodeMovement = 0;

                const int BOT_WANDER_MOVEMENT_TYPE = 1;
                const int BOT_WANDER_TIME_BETWEEN_NODES = 2;

                foreach (KeyValuePair<int, object> kvp in options)
                {
                    switch (kvp.Key)
                    {
                        case BOT_WANDER_MOVEMENT_TYPE:
                            if(kvp.Value is int)
                                TravelMode = (TravelMode)(int)kvp.Value;
                            break;
                        case BOT_WANDER_TIME_BETWEEN_NODES:
                            if (kvp.Value is float)
                                TimeBetweenNodeMovement = (float)kvp.Value;
                            else if (kvp.Value is int)
                                TimeBetweenNodeMovement = (float)(int)kvp.Value;
                            break;
                    }
                }
            }
        }

        public WanderingAction(MovementDescription desc, BotMovementController controller)
            : base(desc, controller)
        {
            m_description = (WanderingDescription)desc;
        }

        public override void Start()
        {
            GenerateNodeGraph();
        }

        private void GenerateNodeGraph()
        {
            m_nodeGraph.Clear();

            m_nodeGraph.FollowIndefinitely = false;

            float xPos = (float)(Util.RandomClass.NextDouble() * m_description.Distances.X * 2) - m_description.Distances.X;
            float yPos = (float)(Util.RandomClass.NextDouble() * m_description.Distances.Y * 2) - m_description.Distances.Y;
            xPos += m_description.Origin.X;
            yPos += m_description.Origin.Y;
            
            ScenePresence botPresence = m_controller.Scene.GetScenePresence(m_controller.Bot.AgentID);
            if (botPresence != null)
            {
                Vector3 nodepos = new Vector3(xPos, yPos, botPresence.AbsolutePosition.Z);
                Util.ForceValidRegionXYZ(ref nodepos);
                float zmin = (float)botPresence.Scene.Heightmap.CalculateHeightAt(nodepos.X, nodepos.Y);
                if (nodepos.Z < zmin)
                    nodepos.Z = zmin;
                List<Vector3> nodes = new List<Vector3>() { nodepos };
                List<TravelMode> travelModes = new List<TravelMode>() { m_description.TravelMode };
                if (m_description.TimeBetweenNodeMovement != 0)
                {
                    nodes.Add(new Vector3(m_description.TimeBetweenNodeMovement, 0, 0));
                    travelModes.Add(TravelMode.Wait);
                }
                m_nodeGraph.AddRange(nodes, travelModes);
            }
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

        public override void TriggerFinishedMovement(ScenePresence botPresence)
        {
            GenerateNodeGraph();
        }

        public override void CheckInformationBeforeMove()
        {
        }

        public override void UpdateInformation()
        {
        }
    }
}
