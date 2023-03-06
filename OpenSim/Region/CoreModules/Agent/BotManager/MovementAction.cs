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
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Framework;

namespace OpenSim.Region.CoreModules.Agent.BotManager
{
    public abstract class MovementAction
    {
        protected readonly NodeGraph m_nodeGraph = new NodeGraph();
        protected int m_frame;
        protected bool m_toAvatar;
        protected BotMovementController m_controller;
        protected bool m_paused;
        private bool m_hasStoppedMoving = false;
        protected MovementDescription m_baseDescription;
        protected bool m_hasFiredFinishedMovingEvent = false;
        protected DateTime m_timeOfLastStep = DateTime.MinValue;
        private int _amountOfTimesLeftToJump = 0;

        public MovementAction(MovementDescription desc, BotMovementController controller)
        {
            m_baseDescription = desc;
            m_controller = controller;
        }

        public abstract void Start();
        public abstract void Stop();
        public abstract void UpdateInformation();
        public abstract void CheckInformationBeforeMove();

        public virtual bool Frame()
        {
            ScenePresence botPresence = m_controller.Scene.GetScenePresence(m_controller.Bot.AgentID);
            if (botPresence == null)
                return false;

            m_frame++;
            GetNextDestination();
            SetBeginningOfMovementFrame();

            if (m_frame % 10 == 0) //Only every 10 frames
            {
                m_frame = 0;
                UpdateInformation();
            }
            return true;
        }

        public void PauseMovement()
        {
            m_paused = true;
        }

        public void ResumeMovement()
        {
            m_paused = false;
            SetBeginningOfMovementFrame();
        }

        public void SetBeginningOfMovementFrame()
        {
            m_timeOfLastStep = DateTime.Now;
        }

        private void GetNextDestination()
        {
            //Fire the move event
            CheckInformationBeforeMove();

            ScenePresence botPresence = m_controller.Scene.GetScenePresence(m_controller.Bot.AgentID);

            var physActor = botPresence.PhysicsActor;

            if (m_controller == null || physActor == null)
                return;
            if (m_paused)
            {
                StopMoving(botPresence, LastFlying, false);
                return;
            }

            Vector3 pos;
            TravelMode state;
            bool teleport;
            bool changingNodes;

            float closeToPoint = physActor.Flying ? 1.5f : 1.0f;

            TimeSpan diffFromLastFrame = (DateTime.Now - m_timeOfLastStep);

            if (m_nodeGraph.GetNextPosition(botPresence, closeToPoint, diffFromLastFrame, m_baseDescription.TimeBeforeTeleportToNextPositionOccurs, out pos, out state,
                                            out teleport, out changingNodes))
            {
                if (changingNodes)
                    _amountOfTimesLeftToJump = 0;
                m_hasFiredFinishedMovingEvent = false;
                if (teleport)
                {
                    //We're forced to teleport to the next location
                    Teleport(botPresence, pos);
                    m_nodeGraph.CurrentPos++;
                    changingNodes = true;
                    //Trigger the update to tell the user that the move failed
                    TriggerFailedToMoveToNextNode(botPresence, m_nodeGraph.CurrentPos == 0 ? m_nodeGraph.NumberOfNodes : m_nodeGraph.CurrentPos);
                }
                else
                {
                    switch (state)
                    {
                        case TravelMode.Fly:
                            FlyTo(botPresence, pos);
                            break;
                        case TravelMode.Run:
                            botPresence.SetAlwaysRun = true;
                            WalkTo(botPresence, pos);
                            break;
                        case TravelMode.Walk:
                            botPresence.SetAlwaysRun = false;
                            WalkTo(botPresence, pos);
                            break;
                        case TravelMode.Teleport:
                            //We have to do this here as if there is a wait before the teleport, we won't get the wait event fired
                            if (changingNodes)
                                TriggerChangingNodes(botPresence, m_nodeGraph.CurrentPos == 0 ? m_nodeGraph.NumberOfNodes : m_nodeGraph.CurrentPos);
                            Teleport(botPresence, pos);
                            m_nodeGraph.CurrentPos++;
                            changingNodes = true;
                            break;
                        case TravelMode.Wait:
                            StopMoving(botPresence, LastFlying, false);
                            break;
                    }
                }
                //Tell the user that we've switched nodes
                if (changingNodes)
                    TriggerChangingNodes(botPresence, m_nodeGraph.CurrentPos == 0 ? m_nodeGraph.NumberOfNodes : m_nodeGraph.CurrentPos);
            }
            else
            {
                StopMoving(botPresence, LastFlying, true);
                if (!m_hasFiredFinishedMovingEvent)
                {
                    m_hasFiredFinishedMovingEvent = true;
                    TriggerFinishedMovement(botPresence);
                }
            }
        }

        public virtual void TriggerFinishedMovement(ScenePresence botPresence)
        {
            List<object> parameters = new List<object>() { botPresence.AbsolutePosition };
            const int BOT_MOVE_COMPLETE = 1;
            TriggerBotUpdate(BOT_MOVE_COMPLETE, parameters);
        }

        public virtual void TriggerChangingNodes(ScenePresence botPresence, int nextNode)
        {
            List<object> parameters = new List<object>() { nextNode, botPresence.AbsolutePosition };
            const int BOT_MOVE_UPDATE = 2;
            TriggerBotUpdate(BOT_MOVE_UPDATE, parameters);
        }

        public virtual void TriggerFailedToMoveToNextNode(ScenePresence botPresence, int nextNode)
        {
            List<object> parameters = new List<object>() { nextNode, botPresence.AbsolutePosition };
            const int BOT_MOVE_FAILED = 3;
            TriggerBotUpdate(BOT_MOVE_FAILED, parameters);
        }

        public virtual void TriggerAvatarLost(ScenePresence botPresence, ScenePresence followPresence, float distance)
        {
            List<object> parameters = new List<object>() { followPresence == null ? Vector3.Zero : followPresence.AbsolutePosition,
                distance, botPresence.AbsolutePosition };
            const int BOT_MOVE_AVATAR_LOST = 4;
            TriggerBotUpdate(BOT_MOVE_AVATAR_LOST, parameters);
        }

        public virtual void TriggerBotUpdate(int flag, List<object> parameters)
        {
            lock (m_controller.Bot.RegisteredScriptsForPathUpdateEvents)
            {
                foreach (UUID itemID in m_controller.Bot.RegisteredScriptsForPathUpdateEvents)
                {
                    m_controller.Scene.EventManager.TriggerBotPathUpdateEvent(itemID, m_controller.Bot.AgentID, flag, parameters);
                }
            }
        }

        public void Teleport(ScenePresence botPresence, Vector3 pos)
        {
            botPresence.StandUp(false, true);
            botPresence.Teleport(pos);
        }

        public void UpdateMovementAnimations(bool p)
        {
            ScenePresence presence = m_controller.Scene.GetScenePresence(m_controller.Bot.AgentID);
            presence.UpdateMovementAnimations();
        }

        // Makes the bot fly to the specified destination
        public void StopMoving(ScenePresence botPresence, bool fly, bool clearPath)
        {
            if (m_hasStoppedMoving)
                return;
            m_hasStoppedMoving = true;
            State = BotState.Idle;
            //Clear out any nodes
            if (clearPath)
                m_nodeGraph.Clear();
            //Send the stop message
            m_movementFlag = (uint)AgentManager.ControlFlags.NONE;
            if (fly)
            {
                m_movementFlag |= (uint)AgentManager.ControlFlags.AGENT_CONTROL_FLY;
            }
            OnBotAgentUpdate(botPresence, Vector3.Zero, m_movementFlag, m_bodyDirection, false);
            //botPresence.CollisionPlane = Vector4.UnitW;
            var pa = botPresence.PhysicsActor;
            if (pa != null)
                pa.SetVelocity(Vector3.Zero, false);
        }


        #region Move/Rotate the bot

        private uint m_movementFlag;
        private Quaternion m_bodyDirection = Quaternion.Identity;

        public BotState m_currentState = BotState.Idle;
        public BotState m_previousState = BotState.Idle;

        public bool LastFlying { get; set; }

        public BotState State
        {
            get { return m_currentState; }
            set
            {
                if (m_currentState != value)
                {
                    m_previousState = m_currentState;
                    m_currentState = value;
                }
            }
        }

        // Makes the bot walk to the specified destination
        public void WalkTo(ScenePresence presence, Vector3 destination)
        {
            if (!Util.IsZeroVector(destination - presence.AbsolutePosition))
            {
                WalkToInternal(presence, destination);
                State = BotState.Walking;
                LastFlying = false;
            }
        }

        // Makes the bot fly to the specified destination
        public void FlyTo(ScenePresence presence, Vector3 destination)
        {
            if (Util.IsZeroVector(destination - presence.AbsolutePosition) == false)
            {
                FlyToInternal(presence, destination);
                State = BotState.Flying;
                LastFlying = true;
            }
            else
            {
                m_movementFlag = (uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_POS;

                OnBotAgentUpdate(presence, Vector3.Zero, m_movementFlag, m_bodyDirection);
                m_movementFlag = (uint)AgentManager.ControlFlags.NONE;
            }
        }

        private void RotateTo(ScenePresence presence, Vector3 destination)
        {
            Vector3 bot_forward = new Vector3(1, 0, 0);
            if (destination - presence.AbsolutePosition != Vector3.Zero)
            {
                Vector3 bot_toward = Util.GetNormalizedVector(destination - presence.AbsolutePosition);
                Quaternion rot_result = llRotBetween(bot_forward, bot_toward);
                m_bodyDirection = rot_result;
            }
            m_movementFlag = (uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_POS;

            OnBotAgentUpdate(presence, Vector3.Zero, m_movementFlag, m_bodyDirection);
            m_movementFlag = (uint)AgentManager.ControlFlags.NONE;
        }

        #region rotation helper functions

        private Vector3 llRot2Fwd(Quaternion r)
        {
            return (new Vector3(1, 0, 0) * r);
        }

        private Quaternion llRotBetween(Vector3 a, Vector3 b)
        {
            //A and B should both be normalized
            double dotProduct = Vector3.Dot(a, b);
            Vector3 crossProduct = Vector3.Cross(a, b);
            double magProduct = Vector3.Distance(Vector3.Zero, a) * Vector3.Distance(Vector3.Zero, b);
            double angle = Math.Acos(dotProduct / magProduct);
            Vector3 axis = Vector3.Normalize(crossProduct);
            float s = (float)Math.Sin(angle / 2);

            return new Quaternion(axis.X * s, axis.Y * s, axis.Z * s, (float)Math.Cos(angle / 2));
        }

        #endregion

        #region Move / fly bot

        /// <summary>
        ///     Does the actual movement of the bot
        /// </summary>
        /// <param name="pos"></param>
        protected void WalkToInternal(ScenePresence presence, Vector3 pos)
        {
            Vector3 bot_forward = new Vector3(2, 0, 0);
            Vector3 bot_toward = Vector3.Zero;
            Vector3 dif = pos - presence.AbsolutePosition;

            bool isJumping = (Math.Abs(dif.X) < 2 && Math.Abs(dif.Y) < 2 && Math.Abs(dif.Z) > 1.5f) || _amountOfTimesLeftToJump > 0;

            if (dif != Vector3.Zero)
            {
                try
                {
                    bot_toward = Util.GetNormalizedVector(dif);
                    Quaternion rot_result = llRotBetween(bot_forward, bot_toward);
                    m_bodyDirection = rot_result;
                }
                catch (ArgumentException)
                {
                }
            }

            //if the bot is being forced to turn around 180 degrees, it won't be able to and it will get stuck 
            // and eventually teleport (mantis 2898). This detects when the vector is too close to zero and 
            // needs to have help being turned around so that it can then go in the correct direction
            if(m_bodyDirection.ApproxEquals(new Quaternion(), 0.01f))
            {
                //Turn to the right until we move far enough away from zero that we will turn around on our own
                bot_toward = new Vector3(0, 1, 0);
                Quaternion rot_result = llRotBetween(bot_forward, bot_toward);
                m_bodyDirection = rot_result;
            }

            if (isJumping)
            {
                //Add UP_POS as well (meaning that we need to be able to move freely up as well as along the ground)
                m_movementFlag = (uint)(AgentManager.ControlFlags.AGENT_CONTROL_AT_POS | AgentManager.ControlFlags.AGENT_CONTROL_UP_POS);
                presence.ShouldJump = true;
                if (_amountOfTimesLeftToJump == 0)
                    _amountOfTimesLeftToJump = 10;//Because we need the bot to apply force over multiple frames (just like agents do)
                _amountOfTimesLeftToJump--;

                //Make sure that we aren't jumping too far (apply a constant to make sure of this)
                const double JUMP_CONST = 0.3;
                bot_toward = Util.GetNormalizedVector(dif);
                if (Math.Abs(bot_toward.X) < JUMP_CONST)
                    bot_toward.X = (float)JUMP_CONST * (bot_toward.X < 0 ? -1 : 1);
                if (Math.Abs(bot_toward.Y) < JUMP_CONST)
                    bot_toward.Y = (float)JUMP_CONST * (bot_toward.Y < 0 ? -1 : 1);

                if(presence.PhysicsActor.IsColliding)//After they leave the ground, don't use as much force so we don't send the bot flying into the air
                    bot_forward = new Vector3(4, 0, 4);
                else
                    bot_forward = new Vector3(2, 0, 2);

                Quaternion rot_result = llRotBetween(bot_forward, bot_toward);
                m_bodyDirection = rot_result;
            }
            else
                m_movementFlag = (uint)(AgentManager.ControlFlags.AGENT_CONTROL_AT_POS);

            if (presence.AllowMovement)
            {
                OnBotAgentUpdate(presence, bot_toward, m_movementFlag, m_bodyDirection);
            }
            else
            {
                OnBotAgentUpdate(presence, Vector3.Zero, (uint) AgentManager.ControlFlags.AGENT_CONTROL_STOP, Quaternion.Identity);
            }

            if (!isJumping)
                m_movementFlag = (uint)AgentManager.ControlFlags.NONE;
        }

        /// <summary>
        ///     Does the actual movement of the bot
        /// </summary>
        /// <param name="pos"></param>
        protected void FlyToInternal(ScenePresence presence, Vector3 pos)
        {
            Vector3 bot_forward = new Vector3(1, 0, 0), bot_toward = Vector3.Zero;
            if (pos - presence.AbsolutePosition != Vector3.Zero)
            {
                try
                {
                    bot_toward = Util.GetNormalizedVector(pos - presence.AbsolutePosition);
                    Quaternion rot_result = llRotBetween(bot_forward, bot_toward);
                    m_bodyDirection = rot_result;
                }
                catch (ArgumentException)
                {
                }
            }

            //if the bot is being forced to turn around 180 degrees, it won't be able to and it will get stuck 
            // and eventually teleport (mantis 2898). This detects when the vector is too close to zero and 
            // needs to have help being turned around so that it can then go in the correct direction
            if (m_bodyDirection.ApproxEquals(new Quaternion(), 0.01f))
            {
                //Turn to the right until we move far enough away from zero that we will turn around on our own
                bot_toward = new Vector3(0, 1, 0);
                Quaternion rot_result = llRotBetween(bot_forward, bot_toward);
                m_bodyDirection = rot_result;
            }

            m_movementFlag = (uint)AgentManager.ControlFlags.AGENT_CONTROL_FLY;

            Vector3 diffPos = pos - presence.AbsolutePosition;
            if (Math.Abs(diffPos.X) > 1.5 || Math.Abs(diffPos.Y) > 1.5)
            {
                m_movementFlag |= (uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_POS;
            }

            if (presence.AbsolutePosition.Z < pos.Z - 1)
            {
                m_movementFlag |= (uint)AgentManager.ControlFlags.AGENT_CONTROL_UP_POS;
            }
            else if (presence.AbsolutePosition.Z > pos.Z + 1)
            {
                m_movementFlag |= (uint)AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG;
            }

            if (bot_forward.X > 0)
            {
                m_movementFlag |= (uint)AgentManager.ControlFlags.AGENT_CONTROL_TURN_LEFT;
                m_movementFlag |= (uint)AgentManager.ControlFlags.AGENT_CONTROL_YAW_POS;
            }
            if (bot_forward.X < 0)
            {
                m_movementFlag |= (uint)AgentManager.ControlFlags.AGENT_CONTROL_TURN_RIGHT;
                m_movementFlag |= (uint)AgentManager.ControlFlags.AGENT_CONTROL_YAW_NEG;
            }

            if (presence.AllowMovement)
            {
                OnBotAgentUpdate(presence, bot_toward, m_movementFlag, m_bodyDirection);
            }
            m_movementFlag = (uint)AgentManager.ControlFlags.AGENT_CONTROL_FLY;
        }

        #endregion

        public void OnBotAgentUpdate(ScenePresence presence, Vector3 toward, uint controlFlag, Quaternion bodyRotation)
        {
            OnBotAgentUpdate(presence, toward, controlFlag, bodyRotation, true);
        }

        public void OnBotAgentUpdate(ScenePresence presence, Vector3 toward, uint controlFlag, Quaternion bodyRotation, bool isMoving)
        {
            if (m_controller.Bot.Frozen && isMoving)
            {
                var pa = presence.PhysicsActor;
                bool fly = pa != null && pa.Flying;
                StopMoving(presence, fly, false);
                return;
            }

            if (isMoving)
                m_hasStoppedMoving = false;
            AgentUpdateArgs pack = new AgentUpdateArgs { ControlFlags = controlFlag, BodyRotation = bodyRotation };
            presence.HandleAgentUpdate(presence.ControllingClient, pack);
        }

        #endregion
    }

    public class MovementDescription
    {
        /// <summary>
        /// The time (in seconds) before the bot teleports to the next position
        /// </summary>
        public float TimeBeforeTeleportToNextPositionOccurs = 60;
    }
}
