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
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Physics.Manager;
using OpenSim.Framework;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using System.Threading.Tasks;

namespace OpenSim.Region.CoreModules.Agent.BotManager
{
    public class AvatarFollower : MovementAction
    {
        public class AvatarFollowerDescription : MovementDescription
        {
            private const float DEFAULT_STOP_FOLLOW_DISTANCE = 2f;
            private const float DEFAULT_START_FOLLOW_DISTANCE = 3f;
            private const float DEFAULT_LOST_AVATAR_DISTANCE = 1000f;

            public bool AllowRunning { get; set; }
            public bool AllowJumping { get; set; }
            public bool AllowFlying { get; set; }
            public Vector3 FollowOffset { get; set; }
            public bool FollowRequiresLineOfSight { get; set; }
            public UUID FollowUUID { get; set; }
            public float StopFollowingDistance { get; set; }
            public float StartFollowingDistance { get; set; }
            public float LostAvatarDistance { get; set; }
            public bool LostAvatar { get; set; }
            public bool Paused { get; set; }
            public int NumberOfTimesJumpAttempted { get; set; }

            public AvatarFollowerDescription(UUID avatarID, Dictionary<int, object> options)
            {
                AllowRunning = AllowJumping = AllowFlying = true;
                FollowUUID = avatarID;
                StartFollowingDistance = DEFAULT_START_FOLLOW_DISTANCE;
                StopFollowingDistance = DEFAULT_STOP_FOLLOW_DISTANCE;
                LostAvatarDistance = DEFAULT_LOST_AVATAR_DISTANCE;

                const int BOT_ALLOW_RUNNING = 1;
                const int BOT_ALLOW_FLYING = 2;
                const int BOT_ALLOW_JUMPING = 3;
                const int BOT_FOLLOW_OFFSET = 4;
                const int BOT_REQUIRES_LINE_OF_SIGHT = 5;
                const int BOT_START_FOLLOWING_DISTANCE = 6;
                const int BOT_STOP_FOLLOWING_DISTANCE = 7;
                const int BOT_LOST_AVATAR_DISTANCE = 8;

                foreach (KeyValuePair<int, object> kvp in options)
                {
                    switch (kvp.Key)
                    {
                        case BOT_ALLOW_RUNNING:
                            if (kvp.Value is int)
                                AllowRunning = ((int)kvp.Value) == 1 ? true : false;
                            break;
                        case BOT_ALLOW_FLYING:
                            if (kvp.Value is int)
                                AllowFlying = ((int)kvp.Value) == 1 ? true : false;
                            break;
                        case BOT_ALLOW_JUMPING:
                            if (kvp.Value is int)
                                AllowJumping = ((int)kvp.Value) == 1 ? true : false;
                            break;
                        case BOT_FOLLOW_OFFSET:
                            if (kvp.Value is Vector3)
                                FollowOffset = (Vector3)kvp.Value;
                            break;
                        case BOT_REQUIRES_LINE_OF_SIGHT:
                            if (kvp.Value is int)
                                FollowRequiresLineOfSight = ((int)kvp.Value) == 1 ? true : false;
                            break;
                        case BOT_START_FOLLOWING_DISTANCE:
                            if (kvp.Value is float || kvp.Value is int)
                                StartFollowingDistance = (float)kvp.Value;
                            break;
                        case BOT_STOP_FOLLOWING_DISTANCE:
                            if (kvp.Value is float || kvp.Value is int)
                                StopFollowingDistance = (float)kvp.Value;
                            break;
                        case BOT_LOST_AVATAR_DISTANCE:
                            if (kvp.Value is float || kvp.Value is int)
                                LostAvatarDistance = (float)kvp.Value;
                            break;
                    }
                }
            }
        }

        private List<Vector3> m_significantAvatarPositions = new List<Vector3>();
        private int m_currentPos;
        private AvatarFollowerDescription m_description;

        public AvatarFollower(MovementDescription desc, BotMovementController controller) :
            base(desc, controller)
        {
            m_description = (AvatarFollowerDescription)desc;
        }

        //We don't use either of these events as they don't make sense for avatar following
        public override void TriggerChangingNodes(ScenePresence botPresence, int nextNode) { }
        public override void TriggerFailedToMoveToNextNode(ScenePresence botPresence, int nextNode) { }

        public override void Start()
        {
            ScenePresence presence = m_controller.Scene.GetScenePresence(m_description.FollowUUID);
            if (presence != null)
            {
                var pa = presence.PhysicsActor;
                if (pa != null)
                    pa.OnRequestTerseUpdate += EventManager_OnClientMovement;
            }
        }

        public override async Task Stop()
        {
            ScenePresence presence = m_controller.Scene.GetScenePresence(m_description.FollowUUID);
            if (presence != null)
            {
                var pa = presence.PhysicsActor;
                if (pa != null)
                    pa.OnRequestTerseUpdate -= EventManager_OnClientMovement;
            }

            ScenePresence botPresence = m_controller.Scene.GetScenePresence(m_controller.Bot.AgentID);
            if (botPresence != null)
            {
                var pa = botPresence.PhysicsActor;
                await StopMoving(botPresence, pa != null && pa.Flying, true);
            }
        }

        public override async Task UpdateInformation()
        {
            // FOLLOW an avatar - this is looking for an avatar UUID so wont follow a prim here  - yet
            //Call this each iteration so that if the av leaves, we don't get stuck following a null person
            ScenePresence presence = m_controller.Scene.GetScenePresence(m_description.FollowUUID);
            ScenePresence botPresence = m_controller.Scene.GetScenePresence(m_controller.Bot.AgentID);
            //If its still null, the person doesn't exist, cancel the follow and return
            if (presence == null)
            {
                if (!m_description.LostAvatar)
                {
                    m_description.LostAvatar = true;
                    TriggerAvatarLost(botPresence, presence, 0.0f);
                    //We stopped, fix the animation
                    UpdateMovementAnimations(false);
                }
                m_description.Paused = true;
                return;
            }

            Vector3 targetPos = presence.AbsolutePosition + m_description.FollowOffset;
            Vector3 ourCurrentPos = botPresence.AbsolutePosition;
            Util.ForceValidRegionXYZ(ref targetPos);

            double distance = Util.GetDistanceTo(targetPos, ourCurrentPos);
            float closeToPoint = m_toAvatar ? m_description.StartFollowingDistance : m_description.StopFollowingDistance;

            List<SceneObjectPart> raycastEntities = llCastRay(ourCurrentPos,
                                                                    targetPos);

            if (m_description.FollowRequiresLineOfSight)
            {
                if (raycastEntities.Count > 0)
                {
                    //Lost the avatar, fire the event
                    if (!m_description.LostAvatar)
                    {
                        m_description.LostAvatar = true;
                        TriggerAvatarLost(botPresence, presence, (float)distance);
                        //We stopped, fix the animation
                        UpdateMovementAnimations(false);
                    }
                    m_description.Paused = true;
                    return;
                }
            }
            if (distance > 10) //Greater than 10 meters, give up
            {
                //Try direct then, since it is way out of range
                DirectFollowing(presence, botPresence);
            }
            else if (distance < closeToPoint && raycastEntities.Count == 0)
            //If the raycastEntities isn't zero, there is something between us and the avatar, don't stop on the other side of walls, etc
            {
                //We're here!
                //If we were having to fly to here, stop flying
                if (m_description.NumberOfTimesJumpAttempted > 0)
                {
                    botPresence.PhysicsActor.Flying = false;
                    await WalkToInternal(botPresence, botPresence.AbsolutePosition);
                    //Fix the animation from flying > walking
                    UpdateMovementAnimations(false);
                }
                m_description.NumberOfTimesJumpAttempted = 0;
            }
            else
            {
                if (raycastEntities.Count == 0)
                    //Nothing between us and the target, go for it!
                    DirectFollowing(presence, botPresence);
                else
                    //if (!BestFitPathFollowing (raycastEntities))//If this doesn't work, try significant positions
                    SignificantPositionFollowing(presence, botPresence);
            }
            ClearOutInSignificantPositions(botPresence, false);
        }

        public override async Task CheckInformationBeforeMove()
        {
            ScenePresence presence = m_controller.Scene.GetScenePresence(m_description.FollowUUID);
            //If its still null, the person doesn't exist, cancel the follow and return
            if (presence == null)
                return;

            //Check to see whether we are close to our avatar, and fire the event if needed
            Vector3 targetPos = presence.AbsolutePosition + m_description.FollowOffset;
            Util.ForceValidRegionXYZ(ref targetPos);
            ScenePresence botPresence = m_controller.Scene.GetScenePresence(m_controller.Bot.AgentID);
            Vector3 ourCurrentPos = botPresence.AbsolutePosition;

            double distance = Util.GetDistanceTo(targetPos, ourCurrentPos);
            float closeToPoint = m_toAvatar ? m_description.StartFollowingDistance : m_description.StopFollowingDistance;

            //Fix how we are running
            botPresence.SetAlwaysRun = presence.SetAlwaysRun;

            if (distance < closeToPoint)
            {
                //Fire our event once
                if (!m_toAvatar) //Changed
                {
                    //Fix the animation
                    UpdateMovementAnimations(false);
                }
                m_toAvatar = true;
                var physActor = presence.PhysicsActor;
                bool fly = physActor == null ? m_description.AllowFlying : (m_description.AllowFlying && physActor.Flying);

                await StopMoving(botPresence, fly, true);
                return;
            }
            if (distance > m_description.LostAvatarDistance)
            {
                //Lost the avatar, fire the event
                if (!m_description.LostAvatar)
                {
                    m_description.LostAvatar = true;
                    TriggerAvatarLost(botPresence, presence, 0.0f);
                    //We stopped, fix the animation
                    UpdateMovementAnimations(false);
                }
                m_description.Paused = true;
            }
            else if (m_description.LostAvatar)
            {
                m_description.LostAvatar = false;
                m_paused = false; //Fixed pause status, avatar entered our range again
            }
            m_toAvatar = false;
        }

        private void EventManager_OnClientMovement()
        {
            ScenePresence presence = m_controller.Scene.GetScenePresence(m_description.FollowUUID);
            if (presence == null)
                return;

            Vector3 pos = presence.AbsolutePosition;
            lock (m_significantAvatarPositions)
            {
                m_significantAvatarPositions.Add(pos);
            }
        }

        #region Significant Position Following Code

        private void ClearOutInSignificantPositions(ScenePresence botPresence, bool checkPositions)
        {
            int closestPosition = 0;
            double closestDistance = 0;
            Vector3[] sigPos;
            lock (m_significantAvatarPositions)
            {
                sigPos = new Vector3[m_significantAvatarPositions.Count];
                m_significantAvatarPositions.CopyTo(sigPos);
            }

            for (int i = 0; i < sigPos.Length; i++)
            {
                double val = Util.GetDistanceTo(botPresence.AbsolutePosition, sigPos[i]);
                if (closestDistance == 0 || closestDistance > val)
                {
                    closestDistance = val;
                    closestPosition = i;
                }
            }
            if (m_currentPos > closestPosition)
            {
                m_currentPos = closestPosition + 2;
                //Going backwards? We must have no idea where we are
            }
            else //Going forwards in the line, all good
                m_currentPos = closestPosition + 2;

            //Remove all insignificant
            List<Vector3> vectors = new List<Vector3>();
            for (int i = sigPos.Length - 50; i < sigPos.Length; i++)
            {
                if (i < 0)
                    continue;
                vectors.Add(sigPos[i]);
            }
            m_significantAvatarPositions = vectors;
        }

        private void SignificantPositionFollowing(ScenePresence presence, ScenePresence botPresence)
        {
            //Do this first
            ClearOutInSignificantPositions(botPresence, true);

            var physActor = presence.PhysicsActor;
            bool fly = physActor == null ? m_description.AllowFlying : (m_description.AllowFlying && physActor.Flying);
            if (m_significantAvatarPositions.Count > 0 && m_currentPos + 1 < m_significantAvatarPositions.Count)
            {
                m_nodeGraph.Clear();

                Vector3 targetPos = m_significantAvatarPositions[m_currentPos + 1];
                Util.ForceValidRegionXYZ(ref targetPos);
                Vector3 diffAbsPos = targetPos - botPresence.AbsolutePosition;
                if (!fly && (diffAbsPos.Z < -0.25 || m_description.NumberOfTimesJumpAttempted > 5))
                {
                    if (m_description.NumberOfTimesJumpAttempted > 5 || diffAbsPos.Z < -3)
                    {
                        if (m_description.NumberOfTimesJumpAttempted <= 5)
                            m_description.NumberOfTimesJumpAttempted = 6;
                        if (m_description.AllowFlying)
                            fly = true;
                    }
                    else
                    {
                        if (!m_description.AllowJumping)
                        {
                            m_description.NumberOfTimesJumpAttempted--;
                            targetPos.Z = botPresence.AbsolutePosition.Z + 0.15f;
                        }
                        else
                        {
                            if (!JumpDecisionTree(botPresence, targetPos))
                            {
                                m_description.NumberOfTimesJumpAttempted--;
                                targetPos.Z = botPresence.AbsolutePosition.Z + 0.15f;
                            }
                            else
                            {
                                if (m_description.NumberOfTimesJumpAttempted < 0)
                                    m_description.NumberOfTimesJumpAttempted = 0;
                                m_description.NumberOfTimesJumpAttempted++;
                            }
                        }
                    }
                }
                else if (!fly)
                {
                    if (diffAbsPos.Z > 3)
                    {
                        //We should fly down to the avatar, rather than fall
                        if (m_description.AllowFlying)
                            fly = true;
                    }
                    m_description.NumberOfTimesJumpAttempted--;
                    if (m_description.NumberOfTimesJumpAttempted < 0)
                        m_description.NumberOfTimesJumpAttempted = 0;
                }
                bool run = m_description.AllowRunning ? presence.SetAlwaysRun : false;
                m_nodeGraph.Add(targetPos, fly ? TravelMode.Fly : (run ? TravelMode.Run : TravelMode.Walk));
            }
        }

        #endregion

        #region Direct Following code

        private void DirectFollowing(ScenePresence presence, ScenePresence botPresence)
        {
            Vector3 targetPos = presence.AbsolutePosition + m_description.FollowOffset;
            Util.ForceValidRegionXYZ(ref targetPos);
            Vector3 ourPos = botPresence.AbsolutePosition;
            Vector3 diffAbsPos = targetPos - ourPos;

            var physActor = presence.PhysicsActor;
            bool fly = physActor == null ? m_description.AllowFlying : (m_description.AllowFlying && physActor.Flying);
            if (!fly && (diffAbsPos.Z > 0.25 || m_description.NumberOfTimesJumpAttempted > 5))
            {
                if (m_description.NumberOfTimesJumpAttempted > 5 || diffAbsPos.Z > 3)
                {
                    if (m_description.NumberOfTimesJumpAttempted <= 5)
                        m_description.NumberOfTimesJumpAttempted = 6;

                    if (m_description.AllowFlying)
                        fly = true;
                }
                else
                {
                    if (!m_description.AllowJumping)
                    {
                        m_description.NumberOfTimesJumpAttempted--;
                        targetPos.Z = ourPos.Z + 0.15f;
                    }
                    else
                    {
                        if (!JumpDecisionTree(botPresence, targetPos))
                        {
                            m_description.NumberOfTimesJumpAttempted--;
                            targetPos.Z = ourPos.Z + 0.15f;
                        }
                        else
                        {
                            if (m_description.NumberOfTimesJumpAttempted < 0)
                                m_description.NumberOfTimesJumpAttempted = 0;
                            m_description.NumberOfTimesJumpAttempted++;
                        }
                    }
                }
            }
            else if (!fly)
            {
                if (diffAbsPos.Z < -3)
                {
                    //We should fly down to the avatar, rather than fall
                    //We also know that because this is the old, we have no entities in our way
                    //(unless this is > 10m, but that case is messed up anyway, needs dealt with later)
                    //so we can assume that it is safe to fly
                    if (m_description.AllowFlying)
                        fly = true;
                }
                m_description.NumberOfTimesJumpAttempted--;
            }
            m_nodeGraph.Clear();
            bool run = m_description.AllowRunning ? presence.SetAlwaysRun : false;
            m_nodeGraph.Add(targetPos, fly ? TravelMode.Fly : (run ? TravelMode.Run : TravelMode.Walk));
        }

        /// <summary>
        ///     See whether we should jump based on the start and end positions given
        /// </summary>
        /// <param name="start"></param>
        /// <param name="end"></param>
        /// <returns></returns>
        private bool JumpDecisionTree(ScenePresence start, Vector3 end)
        {
            //Cast a ray in the direction that we are going
            List<SceneObjectPart> entities = llCastRay(start.AbsolutePosition, end);
            foreach (SceneObjectPart entity in entities)
            {
                if (!entity.IsAttachment)
                {
                    if (entity.Scale.Z > start.PhysicsActor.Size.Z) return true;
                }
            }
            return false;
        }

        public List<SceneObjectPart> llCastRay(Vector3 start, Vector3 end)
        {
            Vector3 dir = new Vector3((end - start).X, (end - start).Y, (end - start).Z);
            Vector3 startvector = new Vector3(start.X, start.Y, start.Z);
            Vector3 endvector = new Vector3(end.X, end.Y, end.Z);

            List<SceneObjectPart> entities = new List<SceneObjectPart>();
            List<ContactResult> results = m_controller.Scene.PhysicsScene.RayCastWorld(startvector, dir, dir.Length(), 5);

            double distance = Util.GetDistanceTo(startvector, endvector);
            if (distance == 0)
                distance = 0.001;
            foreach (ContactResult result in results)
            {
                if (result.CollisionActor != null && result.CollisionActor.PhysicsActorType == ActorType.Prim)
                {
                    SceneObjectPart child = m_controller.Scene.GetSceneObjectPart(result.CollisionActor.Uuid);
                    if (!entities.Contains(child))
                    {
                        entities.Add(child);
                    }
                }
            }
            return entities;
        }

        #endregion
    }
}
