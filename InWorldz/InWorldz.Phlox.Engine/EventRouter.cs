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
 * 
 * (PORTED FROM DotNetEngine)
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules;
using OpenSim.Region;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using log4net;
using OpenSim.Region.ScriptEngine.Interfaces;

namespace Halcyon.Phlox.Engine
{

    internal class EventRouter
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private IScriptEngine myScriptEngine;

        public EventRouter(IScriptEngine scriptEngine)
        {
            myScriptEngine = scriptEngine;
            this.HookUpEvents();
        }

        public void HookUpEvents()
        {
            m_log.Info("[" + myScriptEngine.ScriptEngineName + "]: Attaching to object events");

            myScriptEngine.World.EventManager.OnObjectGrab += touch_start;
            myScriptEngine.World.EventManager.OnObjectDeGrab += touch_end;

            myScriptEngine.World.EventManager.OnScriptChangedEvent += changed;

            myScriptEngine.World.EventManager.OnScriptAtTargetEvent += at_target;
            myScriptEngine.World.EventManager.OnScriptNotAtTargetEvent += not_at_target;
            myScriptEngine.World.EventManager.OnBotPathUpdateEvent += bot_update;

            myScriptEngine.World.EventManager.OnScriptControlEvent += control;

            myScriptEngine.World.EventManager.OnScriptColliderStart += collision_start;
            myScriptEngine.World.EventManager.OnScriptColliding += collision;
            myScriptEngine.World.EventManager.OnScriptCollidingEnd += collision_end;

            myScriptEngine.World.EventManager.OnScriptLandCollidingStart += land_collision_start;
            myScriptEngine.World.EventManager.OnScriptLandCollidingEnd += land_collision_end;
            myScriptEngine.World.EventManager.OnScriptLandColliding += land_collision;

            myScriptEngine.World.EventManager.OnObjectGrabUpdate += new EventManager.ObjectGrabUpdateDelegate(EventManager_OnObjectGrabUpdate);

            myScriptEngine.World.EventManager.OnAttachObject += new EventManager.AttachObject(EventManager_OnAttachObject);
            myScriptEngine.World.EventManager.OnDetachObject += new EventManager.DetachObject(EventManager_OnDetachObject);

            myScriptEngine.World.EventManager.OnScriptAtRotTargetEvent += EventManager_OnScriptAtRotTargetEvent;
            myScriptEngine.World.EventManager.OnScriptNotAtRotTargetEvent += EventManager_OnScriptNotAtRotTargetEvent;

            IMoneyModule money =
                    myScriptEngine.World.RequestModuleInterface<IMoneyModule>();
            if (money != null)
                money.OnObjectPaid += HandleObjectPaid;
        }

        void EventManager_OnScriptNotAtRotTargetEvent(uint localID)
        {
            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "not_at_rot_target", new object[0],
                    new DetectParams[0]));
        }

        void EventManager_OnScriptAtRotTargetEvent(uint localID, int handle, Quaternion targetRot, Quaternion atRot)
        {
            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "at_rot_target", new object[] {
                    handle,
                    targetRot,
                    atRot},
                    new DetectParams[0]));
        }

        void EventManager_OnObjectGrabUpdate(uint localID, uint originalID, Vector3 offsetPos, IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            // Add to queue for all scripts in ObjectID object
            DetectParams[] det = new DetectParams[1];
            det[0] = new DetectParams();
            det[0].Key = remoteClient.AgentId;
            det[0].Populate(myScriptEngine.World);
            det[0].OffsetPos = offsetPos;

            if (originalID == 0)
            {
                SceneObjectPart part =
                        myScriptEngine.World.GetSceneObjectPart(localID);

                if (part == null)
                    return;

                det[0].LinkNum = part.LinkNum;
            }
            else
            {
                SceneObjectPart originalPart =
                        myScriptEngine.World.GetSceneObjectPart(originalID);
                det[0].LinkNum = originalPart.LinkNum;
            }
            if (surfaceArgs != null)
            {
                det[0].SurfaceTouchArgs = surfaceArgs;
            }

            myScriptEngine.UpdateTouchData(localID, det);
        }

        void EventManager_OnDetachObject(uint localId)
        {
            myScriptEngine.PostObjectEvent(localId, new EventParams(
                    "attach", new object[] { UUID.Zero.ToString() },
                    new DetectParams[0]));
        }

        void EventManager_OnAttachObject(UUID avatarId, uint localId)
        {
            myScriptEngine.PostObjectEvent(localId, new EventParams(
                    "attach", new object[] { avatarId.ToString() },
                    new DetectParams[0]));
        }

        public void ReadConfig()
        {
        }

        private void HandleObjectPaid(UUID objectID, UUID agentID, int amount)
        {
            SceneObjectPart part =
                    myScriptEngine.World.GetSceneObjectPart(objectID);
            if (part == null)
                return;

            int paidLink = part.LinkNum;

            // Check if this part has a money handler, if not, pass it to the root part
            if ((part.ScriptEvents & ScriptEvents.money) == 0)
                if (!part.IsRootPart())
                    part = part.ParentGroup.RootPart;
            if (part == null)
                return;

            // Okay now send the money event to the appropriate/selected part
            money(part, agentID, amount, paidLink);
        }

        public void changed(uint localID, uint change)
        {
            // Add to queue for all scripts in localID, Object pass change.
            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "changed", new object[] { (int)change },
                    new DetectParams[0]));
        }

        public void state_entry(uint localID, UUID itemID)
        {
            // Add to queue for all scripts in ObjectID object
            /*myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "state_entry",new object[] { },
                    new DetectParams[0]));*/
            //state changes are NOT object events
            myScriptEngine.PostScriptEvent(itemID, new EventParams(
                    "state_entry", new object[] { },
                    new DetectParams[0]));
        }

        public void touch_start(uint localID, uint originalID,
                Vector3 offsetPos, IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            // Add to queue for all scripts in ObjectID object
            DetectParams[] det = new DetectParams[1];
            det[0] = new DetectParams();
            det[0].Key = remoteClient.AgentId;
            det[0].Populate(myScriptEngine.World);

            if (originalID == 0)
            {
                SceneObjectPart part =
                        myScriptEngine.World.GetSceneObjectPart(localID);

                if (part == null)
                    return;

                det[0].LinkNum = part.LinkNum;
            }
            else
            {
                SceneObjectPart originalPart =
                        myScriptEngine.World.GetSceneObjectPart(originalID);
                det[0].LinkNum = originalPart.LinkNum;
            }
            if (surfaceArgs != null)
            {
                det[0].SurfaceTouchArgs = surfaceArgs;
            }

            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "touch_start", new Object[] { 1 },
                    det));
        }

        public void touch(uint localID, uint originalID, Vector3 offsetPos,
                IClientAPI remoteClient)
        {
            // Add to queue for all scripts in ObjectID object
            DetectParams[] det = new DetectParams[1];
            det[0] = new DetectParams();
            det[0].Key = remoteClient.AgentId;
            det[0].Populate(myScriptEngine.World);
            det[0].OffsetPos = offsetPos;

            if (originalID == 0)
            {
                SceneObjectPart part = myScriptEngine.World.GetSceneObjectPart(localID);
                if (part == null)
                    return;

                det[0].LinkNum = part.LinkNum;
            }
            else
            {
                SceneObjectPart originalPart = myScriptEngine.World.GetSceneObjectPart(originalID);
                det[0].LinkNum = originalPart.LinkNum;
            }

            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "touch", new Object[] { 1 },
                    det));
        }

        public void touch_end(uint localID, uint originalID, IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            // Add to queue for all scripts in ObjectID object
            DetectParams[] det = new DetectParams[1];
            det[0] = new DetectParams();
            det[0].Key = remoteClient.AgentId;
            det[0].Populate(myScriptEngine.World);

            if (originalID == 0)
            {
                SceneObjectPart part =
                        myScriptEngine.World.GetSceneObjectPart(localID);
                if (part == null)
                    return;

                det[0].LinkNum = part.LinkNum;
            }
            else
            {
                SceneObjectPart originalPart =
                        myScriptEngine.World.GetSceneObjectPart(originalID);
                if (originalPart == null)
                    return;
                det[0].LinkNum = originalPart.LinkNum;
            }

            if (surfaceArgs != null)
            {
                det[0].SurfaceTouchArgs = surfaceArgs;
            }

            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "touch_end", new Object[] { 1 },
                    det));
        }

        public void money(SceneObjectPart part, UUID agentID, int amount, int paidLinkNum)
        {
            DetectParams[] det = new DetectParams[1];
            det[0] = new DetectParams();
            det[0].Key = agentID;
            det[0].LinkNum = paidLinkNum;

            myScriptEngine.PostObjectEvent(part.LocalId, new EventParams(
                    "money", new object[] {
                    agentID.ToString(),
                    amount },
                    det));
        }

        // TODO: Replace placeholders below
        // NOTE! THE PARAMETERS FOR THESE FUNCTIONS ARE NOT CORRECT!
        //  These needs to be hooked up to OpenSim during init of this class
        //   then queued in EventQueueManager.
        // When queued in EventQueueManager they need to be LSL compatible (name and params)

        public void state_exit(uint localID, UUID itemID)
        {
            myScriptEngine.PostScriptEvent(itemID, new EventParams(
                    "state_exit", new object[] { },
                    new DetectParams[0]));
        }

        public void collision_start(uint localID, ColliderArgs col)
        {
            // Add to queue for all scripts in ObjectID object
            List<DetectParams> det = new List<DetectParams>();

            foreach (DetectedObject detobj in col.Colliders)
            {
                DetectParams d = DetectParams.FromDetectedObject(detobj);
                det.Add(d);
            }

            if (det.Count > 0)
                myScriptEngine.PostObjectEvent(localID, new EventParams(
                        "collision_start",
                        new Object[] { det.Count },
                        det.ToArray()));
        }

        public void collision(uint localID, ColliderArgs col)
        {
            // Add to queue for all scripts in ObjectID object
            List<DetectParams> det = new List<DetectParams>();

            foreach (DetectedObject detobj in col.Colliders)
            {
                DetectParams d = DetectParams.FromDetectedObject(detobj);
                det.Add(d);
            }

            if (det.Count > 0)
            {
                myScriptEngine.PostObjectEvent(localID, new EventParams(
                        "collision", new Object[] { det.Count },
                        det.ToArray()));
            }
        }

        public void collision_end(uint localID, ColliderArgs col)
        {
            // Add to queue for all scripts in ObjectID object
            List<DetectParams> det = new List<DetectParams>();

            foreach (DetectedObject detobj in col.Colliders)
            {
                DetectParams d = DetectParams.FromDetectedObject(detobj);
                det.Add(d);
            }

            if (det.Count > 0)
                myScriptEngine.PostObjectEvent(localID, new EventParams(
                        "collision_end",
                        new Object[] { det.Count },
                        det.ToArray()));
        }

        public void land_collision_start(uint localID, Vector3 pos)
        {
            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "land_collision_start",
                    new object[] { pos },
                    new DetectParams[0]));
        }

        public void land_collision(uint localID, Vector3 pos)
        {
            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "land_collision",
                    new object[] { pos },
                    new DetectParams[0]));
        }

        public void land_collision_end(uint localID, Vector3 pos)
        {
            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "land_collision_end",
                    new object[] { pos },
                    new DetectParams[0]));
        }

        // Handled by long commands
        public void timer(uint localID, UUID itemID)
        {
        }

        public void listen(uint localID, UUID itemID)
        {
        }

        public void control(uint localID, UUID itemID, UUID agentID, uint held, uint change)
        {
            //if ((change == 0) && (myScriptEngine.m_EventQueueManager.CheckEeventQueueForEvent(localID, "control"))) return;
           
            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "control", new object[] {
                        agentID.ToString(),
                        (int)held,
                        (int)change},
                    new DetectParams[0]));
        }

        public void email(uint localID, UUID itemID, string timeSent,
                string address, string subject, string message, int numLeft)
        {
            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "email", new object[] {
                    timeSent,
                    address,
                    subject,
                    message,
                    numLeft},
                    new DetectParams[0]));
        }

        public void at_target(uint localID, int handle, Vector3 targetpos,
                Vector3 atpos)
        {
            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "at_target", new object[] {
                    handle,
                    targetpos,
                    atpos },
                    new DetectParams[0]));
        }

        public void not_at_target(uint localID)
        {
            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "not_at_target", new object[0],
                    new DetectParams[0]));
        }

        public void bot_update(UUID itemID, UUID botID, int flag, List<object> parameters)
        {
            myScriptEngine.PostScriptEvent(itemID, new EventParams(
                    "bot_update", new object[] {
                    botID.ToString(),
                    flag,
                    new Halcyon.Phlox.Types.LSLList(parameters) },
                    new DetectParams[0]));
        }

        public void at_rot_target(uint localID, UUID itemID)
        {
            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "at_rot_target", new object[0],
                    new DetectParams[0]));
        }

        public void not_at_rot_target(uint localID, UUID itemID)
        {
            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "not_at_rot_target", new object[0],
                    new DetectParams[0]));
        }

        public void attach(uint localID, UUID itemID)
        {
        }

        public void dataserver(uint localID, UUID itemID)
        {
        }

        public void link_message(uint localID, UUID itemID)
        {
        }

        public void moving_start(uint localID, UUID itemID)
        {
            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "moving_start", new object[0],
                    new DetectParams[0]));
        }

        public void moving_end(uint localID, UUID itemID)
        {
            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "moving_end", new object[0],
                    new DetectParams[0]));
        }

        public void object_rez(uint localID, UUID itemID)
        {
        }

        public void remote_data(uint localID, UUID itemID)
        {
        }

        // Handled by long commands
        public void http_response(uint localID, UUID itemID)
        {
        }

        /// <summary>
        /// If set to true then threads and stuff should try to make a graceful exit
        /// </summary>
        public bool PleaseShutdown
        {
            get { return _PleaseShutdown; }
            set { _PleaseShutdown = value; }
        }
        private bool _PleaseShutdown = false;
    }
}
