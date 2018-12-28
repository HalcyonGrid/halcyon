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
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Physics.Manager;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.CoreModules.Agent.BotManager
{
    public enum BotState
    {
        Idle,
        Walking,
        Flying,
        Unknown
    }

    public class BotMovementController
    {
        #region Declares

        private Scene m_scene;
        private IBot m_bot;
        private object m_movementLock = new object();
        private MovementDescription m_currentMovement = null;
        private MovementAction m_movementAction = null;

        public Scene Scene { get { return m_scene; } }
        public IBot Bot { get { return m_bot; } }

        #endregion

        #region Properties

        public bool MovementInProgress
        {
            get
            {
                lock (m_movementLock)
                {
                    return m_currentMovement != null;
                }
            }
        }

        #endregion

        #region Constructor

        public BotMovementController(IBot bot)
        {
            m_bot = bot;
            m_scene = (Scene)bot.Scene;
        }

        #endregion

        #region Public Methods

        public void StartFollowingAvatar(UUID avatarID, Dictionary<int, object> options)
        {
            lock (m_movementLock)
            {
                if (MovementInProgress)
                    StopMovement();

                AvatarFollower.AvatarFollowerDescription desc = 
                    new AvatarFollower.AvatarFollowerDescription(avatarID, options);

                BeginTrackingFrames(desc);
            }
        }

        public void StartNavigationPath(List<Vector3> nodes, List<TravelMode> travelModes, Dictionary<int, object> options)
        {
            lock (m_movementLock)
            {
                if (MovementInProgress)
                    StopMovement();

                NavigationPathAction.NavigationPathDescription desc =
                    new NavigationPathAction.NavigationPathDescription(nodes, travelModes, options);

                BeginTrackingFrames(desc);
            }
        }

        public void StartWandering(Vector3 origin, Vector3 distance, Dictionary<int, object> options)
        {
            lock (m_movementLock)
            {
                if (MovementInProgress)
                    StopMovement();

                WanderingAction.WanderingDescription desc =
                    new WanderingAction.WanderingDescription(origin, distance, options);

                BeginTrackingFrames(desc);
            }
        }

        public void StopMovement()
        {
            lock (m_movementLock)
            {
                if (!MovementInProgress)
                    return;

                StopTrackingFrames();
            }
        }

        public void PauseMovement()
        {
            lock (m_movementLock)
            {
                if (!MovementInProgress)
                    return;

                m_movementAction.PauseMovement();
            }
        }

        public void ResumeMovement()
        {
            lock (m_movementLock)
            {
                if (!MovementInProgress)
                    return;

                m_movementAction.ResumeMovement();
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// MUST BE CALLED FROM WITHIN m_movementLock!
        /// </summary>
        /// <param name="movement"></param>
        private void BeginTrackingFrames(MovementDescription movement)
        {
            if (m_currentMovement != null)
                return;

            m_currentMovement = movement;

            if (movement is AvatarFollower.AvatarFollowerDescription)
                m_movementAction = new AvatarFollower(movement, this);
            else if (movement is WanderingAction.WanderingDescription)
                m_movementAction = new WanderingAction(movement, this);
            else
                m_movementAction = new NavigationPathAction(movement, this);

            m_movementAction.Start();
            m_movementAction.SetBeginningOfMovementFrame();
            m_scene.EventManager.OnFrame += Scene_OnFrame;
        }

        /// <summary>
        /// MUST BE CALLED FROM WITHIN m_movementLock!
        /// </summary>
        /// <param name="movement"></param>
        private void StopTrackingFrames()
        {
            if (m_currentMovement == null)
                return;

            m_movementAction.Stop();
            m_movementAction = null;
            m_currentMovement = null;
            m_scene.EventManager.OnFrame -= Scene_OnFrame;
        }

        private void Scene_OnFrame()
        {
            if (m_movementAction == null)
                return;

            if (!m_movementAction.Frame())
            {
                lock (m_movementLock)
                    StopTrackingFrames();
            }
        }

        #endregion
    }
}
