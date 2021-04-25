/*
 * Copyright (c) InWorldz Halcyon Developers
 * Copyright (c) Contributors, http://opensimulator.org/
 *
 * Ported from LSL_Api.cs
 * Massively adapted by InWorldz, LLC in March 2011
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Remoting.Lifetime;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;
using Nini.Config;
using log4net;
using OpenMetaverse;
using OpenMetaverse.Packets;
using OpenMetaverse.StructuredData;
using OpenSim;
using OpenSim.Framework;
using OpenSim.Framework.Communications.Cache;
using OpenSim.Framework.Geom;
using OpenSim.Framework.Servers;
using OpenSim.Region.CoreModules;
using OpenSim.Region.CoreModules.World.Land;
using OpenSim.Region.CoreModules.World.Terrain;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Physics.Manager;
using OpenSim.Region.Physics.Manager.Vehicle;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.Api.Plugins;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Region.ScriptEngine.Interfaces;

using LSL_List = InWorldz.Phlox.Types.LSLList;
using LSL_Rotation = OpenMetaverse.Quaternion;
using LSL_Vector = OpenMetaverse.Vector3;

using System.Reflection;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.Interfaces;

namespace InWorldz.Phlox.Engine
{
    using Util = OpenSim.Framework.Util;

    public class LSLSystemAPI : InWorldz.Phlox.Glue.ISystemAPI
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private const int MAX_PHYSICS_TIME_BEFORE_DILATION = 30;

        private const int MAX_OBJ_NAME = 63;
        private const int MAX_OBJ_DESC = 127;

        protected IScriptEngine m_ScriptEngine;
        protected SceneObjectPart m_host;
        protected uint m_localID;
        protected UUID m_itemID;
        protected bool throwErrorOnNotImplemented = false;
        protected AsyncCommandManager AsyncCommands = null;
        protected float m_ScriptDelayFactor = 1.0f;
        protected float m_MinTimerInterval = 0.01f;
        
        private DateTime m_timer = DateTime.Now;
        private IClientAPI m_waitingForScriptAnswer = null; // we need to know more than if there's an outstanding request, also WHICH LLCV has an answer handler set.
        private bool m_automaticLinkPermission = false;
        private IMessageTransferModule m_TransferModule = null;
        private int m_notecardLineReadCharsMax = 255;
        private IUrlModule m_UrlModule = null;

        private const int MAX_RESETS_PER_SECOND = 5;
        private int m_resetSecond = 0;
        private int m_resetCount = 0;
        private DateTime m_resetWarned = DateTime.Now;

        /// <summary>
        /// When a script is restored with state the listen handle it previously had
        /// may not be available. This collection remaps the previous handle with a new
        /// one
        /// </summary>
        private Dictionary<int, int> _listenRemap = new Dictionary<int, int>();

        public VM.Interpreter Script
        {
            get
            {
                return _thisScript;
            }

            set
            {
                _thisScript = value;
            }
        }

        private VM.Interpreter _thisScript;

        /// <summary>
        /// Random number generator used for all scripts. This does not have to be 
        /// locked before use because all scripts run inside the script engine thread
        /// </summary>
        private static Random s_random = new Random();

        public LSLSystemAPI(IScriptEngine ScriptEngine, SceneObjectPart host, uint localID, UUID itemID)
        {
            m_ScriptEngine = ScriptEngine;
            m_host = host;
            m_localID = localID;
            m_itemID = itemID;

            Initialize();
        }

        public void Initialize()
        {
            m_ScriptDelayFactor =
                m_ScriptEngine.Config.GetFloat("ScriptDelayFactor", 1.0f);
            m_MinTimerInterval =
                m_ScriptEngine.Config.GetFloat("MinTimerInterval", m_MinTimerInterval);
            m_automaticLinkPermission =
                m_ScriptEngine.Config.GetBoolean("AutomaticLinkPermission", false);
            m_notecardLineReadCharsMax =
                m_ScriptEngine.Config.GetInt("NotecardLineReadCharsMax", 255);
            if (m_notecardLineReadCharsMax > 65535)
                m_notecardLineReadCharsMax = 65535;

            m_TransferModule =
                    m_ScriptEngine.World.RequestModuleInterface<IMessageTransferModule>();
            m_UrlModule = m_ScriptEngine.World.RequestModuleInterface<IUrlModule>();

            AsyncCommands = new AsyncCommandManager(m_ScriptEngine);
        }


        protected void ScriptSleep(int delay)   // in milliseconds
        {
            if (_thisScript.ScriptState.RunState != VM.RuntimeState.Status.Syscall)
            {
                //delay = (int)((float)delay * m_ScriptDelayFactor);
                if (delay == 0)
                    return;

                _thisScript.ScriptState.NextWakeup = OpenSim.Framework.Util.GetLongTickCount() + (UInt64)delay;
                _thisScript.ScriptState.RunState = VM.RuntimeState.Status.Sleeping;
            }
        }

        public Scene World
        {
            get { return m_ScriptEngine.World; }
        }

        public void state(string newState)
        {
            m_ScriptEngine.SetState(m_itemID, newState);
        }

        private void RemoveAllAsyncHandlers()
        {
            AsyncCommands.DataserverPlugin.RemoveEvents(m_host.LocalId, m_itemID);
            AsyncCommands.SensorRepeatPlugin.UnSetSenseRepeaterEvents(m_host.LocalId, m_itemID);
            AsyncCommands.HttpRequestPlugin.RemoveEvents(m_host.LocalId, m_itemID);
            AsyncCommands.XmlRequestPlugin.RemoveEvents(m_host.LocalId, m_itemID);
        }

        private void ThrottleScriptResets()
        {
            // Throttle resets to no more than 5 per second (MAX_RESETS_PER_SECOND)
            int now = Util.UnixTimeSinceEpoch();
            if (m_resetSecond == now)
            {
                if (++m_resetCount > MAX_RESETS_PER_SECOND)
                {
                    if (DateTime.Now > m_resetWarned.AddMinutes(60))    // warn once per hour
                    {
                        Vector3 pos = m_host.AbsolutePosition;
                        string thePos = string.Format("at {0}/{1}/{2}", (int)pos.X, (int)pos.Y, (int)pos.Z);
                        string theObject = " in '" + m_host.ParentGroup.Name + "'";
                        string theLink = (m_host.LinkNum < 2) ? String.Empty : " link #" + m_host.LinkNum.ToString();
                        string context = string.Format("{0}{1} {2}", theObject, theLink, thePos);
                        m_log.WarnFormat("[Phlox]: Script '{0}' calling llResetScript too frequently: {1}",
                            llGetScriptName(), context);
                        if (m_host.LinkNum < 2)
                            context = thePos;
                        ScriptShoutError("Script '" + llGetScriptName() + "' calling llResetScript too frequently: " + context);
                        m_resetWarned = DateTime.Now;
                    }
                    ScriptSleep(5000);  // punish the script for 5 seconds after too many resets in the same period
                }
            }
            else
                m_resetCount = 1;
            m_resetSecond = now;
        }

        public void OnScriptReset()
        {
            int oldControls, oldPassOn;
            RemoveAllAsyncHandlers();
            ReleaseControlsInternal(false, true, false, false, out oldControls, out oldPassOn);

            //remove http handlers
            m_UrlModule.ScriptRemoved(m_itemID);

            TaskInventoryItem item;
            lock (m_host.TaskInventory)
            {
                if (!m_host.TaskInventory.ContainsKey(InventorySelf()))
                    return;
                else
                    item = m_host.TaskInventory[InventorySelf()];
            }

            PermsChange(item, UUID.Zero, 0);

            ThrottleScriptResets();
        }

        public void OnStateChange()
        {
            RemoveAllAsyncHandlers();
        }

        public void OnScriptUnloaded(Phlox.Types.ScriptUnloadReason reason, Phlox.VM.RuntimeState.LocalDisableFlag localFlag)
        {
            RemoveAllAsyncHandlers();
            m_host.RemoveScriptEvents(m_itemID);

            //silently release controls IF this is not the result of a crossing wait disable

            //The reason for this is that the controls structures need to be maintained as the
            //actual serialization of the script data will happen later. If we disable now
            //for a crossing wait when the object gets to the other side it wont have the
            //correct controls flags to reenable controls on the next region
            // m_log.WarnFormat("[LSL]: OnScriptUnloaded {0} reason={1}, localFlag={2}", m_itemID, reason, localFlag);
            if (reason != Types.ScriptUnloadReason.LocallyDisabled || localFlag != VM.RuntimeState.LocalDisableFlag.CrossingWait)
            {
                if (reason != Types.ScriptUnloadReason.Unloaded)    // called a second time with this reason after already disabling
                    this.ReleaseControlsInternal(true, false, false, false);
            }

            //remove http handlers
            m_UrlModule.ScriptRemoved(m_itemID);
        }

        private const int NUM_RUN_SAMPLES = 16;
        private double[] _samples = new double[NUM_RUN_SAMPLES];
        private int _numSamples = 0;
        private int _currSample = 0;
        public void AddExecutionTime(double ms)
        {
            m_host.ParentGroup.AddScriptLPS(ms);

            _samples[_currSample++] = ms;
            if (_currSample > _numSamples)
                _numSamples = _currSample;
            if (_currSample >= NUM_RUN_SAMPLES)
                _currSample = 0;
        }
        // This tracks the average of the last N script execution times, and
        // is called indirectly from engine.GetAverageScriptTime(itemID).
        public float GetAverageScriptTime()
        {
            if (_numSamples < 1)
                return 0.0f;

            double total = 0.0f;
            foreach (double sample in _samples)
                total += sample;
            return (float) (total / _numSamples);
        }

        public void OnScriptInjected(bool fromCrossing)
        {
            //add current listeners back
            foreach (VM.ActiveListen activeListen in _thisScript.ScriptState.ActiveListens.Values)
            {
                if (activeListen != null)
                {
                    this.Relisten(activeListen.Handle, activeListen.Channel, activeListen.Name, activeListen.Key, activeListen.Message);
                }
            }

            //check other misc attributes
            foreach (KeyValuePair<int, object[]> kvp in new List<KeyValuePair<int, object[]>>(_thisScript.ScriptState.MiscAttributes))
            {
                TaskInventoryItem item = null;
                switch ((VM.RuntimeState.MiscAttr)kvp.Key)
                {
                    case VM.RuntimeState.MiscAttr.SensorRepeat:
                        this.llSensorRepeat((string)kvp.Value[0], (string)kvp.Value[1], (int)kvp.Value[2], (float)kvp.Value[3],
                            (float)kvp.Value[4], (float)kvp.Value[5]);
                        break;

                    case VM.RuntimeState.MiscAttr.VolumeDetect:
                        this.llVolumeDetect((int)kvp.Value[0]);
                        break;
                    case VM.RuntimeState.MiscAttr.SilentEstateManagement:
                        bool silentEstate = false;
                        lock (m_host.TaskInventory)
                        {
                            if (m_host.TaskInventory.ContainsKey(InventorySelf()))
                            {
                                item = m_host.TaskInventory[InventorySelf()];
                                if ((int)kvp.Value[0] != 0)
                                    silentEstate = true;
                            }
                            if (item != null)
                            {
                                if (silentEstate)
                                    item.PermsMask |= ScriptBaseClass.PERMISSION_SILENT_ESTATE_MANAGEMENT;
                                else
                                    item.PermsMask &= ~ScriptBaseClass.PERMISSION_SILENT_ESTATE_MANAGEMENT;
                                if (item.PermsMask == 0)
                                    item.PermsGranter = UUID.Zero;
                            }
                        }
                        if (item != null)
                            PermsChange(item, item.PermsGranter, item.PermsMask);
                        break;
                    case VM.RuntimeState.MiscAttr.Control:
                        if (m_host.IsAttachment || !fromCrossing)
                        {
                            lock (m_host.TaskInventory)
                            {
                                UUID self = InventorySelf();
                                if (!m_host.TaskInventory.ContainsKey(self))
                                    return;
                                item = m_host.TaskInventory[self];
                            }

                            int controls = (int)kvp.Value[0];
                            int accept = (int)kvp.Value[1];
                            int pass_on = (int)kvp.Value[2];

                            // m_log.WarnFormat("[LSL]: InjectScript [{0}] LocalDisable={1}", m_itemID, _thisScript.ScriptState.LocalDisable);
                            TakeControlsInternal(-1, 0, controls, accept, pass_on, fromCrossing, item.PermsGranter);
                        }
                        break;
                }
            }
        }

        public void SetScriptEventFlags()
        {
            int eventFlags = 0;

            if (_thisScript.ScriptState.GeneralEnable && _thisScript.ScriptState.Enabled            // Scripts enabled, and Running checked?
                && (_thisScript.ScriptState.LocalDisable == VM.RuntimeState.LocalDisableFlag.None)) // not parcel disabled or crossing wait
            {
                foreach (VM.EventInfo evt in _thisScript.Script.StateEvents[_thisScript.ScriptState.LSLState])
                {
                    eventFlags |= this.MapToEventFlag((Types.SupportedEventList.Events)evt.EventType);
                }
            }

            m_host.SetScriptEvents(_thisScript.ItemId, eventFlags);
        }

        private int MapToEventFlag(Types.SupportedEventList.Events evt)
        {
            switch (evt)
            {
                case Types.SupportedEventList.Events.AT_ROT_TARGET:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.at_rot_target;

                case Types.SupportedEventList.Events.AT_TARGET:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.at_target;

                case Types.SupportedEventList.Events.ATTACH:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.attach;

                case Types.SupportedEventList.Events.BOT_UPDATE:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.bot_update;

                case Types.SupportedEventList.Events.COLLISION:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.collision;

                case Types.SupportedEventList.Events.COLLISION_END:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.collision_end;

                case Types.SupportedEventList.Events.COLLISION_START:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.collision_start;

                case Types.SupportedEventList.Events.CONTROL:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.control;

                case Types.SupportedEventList.Events.DATASERVER:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.dataserver;

                case Types.SupportedEventList.Events.EMAIL:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.email;

                case Types.SupportedEventList.Events.HTTP_RESPONSE:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.http_response;

                case Types.SupportedEventList.Events.LAND_COLLISION:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.land_collision;

                case Types.SupportedEventList.Events.LAND_COLLISION_END:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.land_collision_end;

                case Types.SupportedEventList.Events.LAND_COLLISION_START:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.land_collision_start;

                case Types.SupportedEventList.Events.LISTEN:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.listen;

                case Types.SupportedEventList.Events.MONEY:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.money;

                case Types.SupportedEventList.Events.MOVING_END:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.moving_end;

                case Types.SupportedEventList.Events.MOVING_START:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.moving_start;

                case Types.SupportedEventList.Events.NOT_AT_ROT_TARGET:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.not_at_rot_target;

                case Types.SupportedEventList.Events.NOT_AT_TARGET:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.not_at_target;

                case Types.SupportedEventList.Events.OBJECT_REZ:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.object_rez;

                case Types.SupportedEventList.Events.REMOTE_DATA:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.remote_data;

                case Types.SupportedEventList.Events.RUN_TIME_PERMISSIONS:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.run_time_permissions;

                case Types.SupportedEventList.Events.STATE_ENTRY:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.state_entry;

                case Types.SupportedEventList.Events.STATE_EXIT:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.state_exit;

                case Types.SupportedEventList.Events.TIMER:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.timer;

                case Types.SupportedEventList.Events.TOUCH:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.touch;

                case Types.SupportedEventList.Events.TOUCH_END:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.touch_end;

                case Types.SupportedEventList.Events.TOUCH_START:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.touch_start;

                case Types.SupportedEventList.Events.CHANGED:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.changed;

                case Types.SupportedEventList.Events.TRANSACTION_RESULT:
                    return (int)OpenSim.Region.Framework.Scenes.ScriptEvents.transaction_result;
            }

            return 0;
        }

        /// <summary>
        /// Called by the script engine when the script is ready after a crossing, teleport, or relog and the avatar or seated prim is ready.
        /// </summary>
        public void OnGroupCrossedAvatarReady(UUID avatarId)
        {
            object[] controlParams;
            if (_thisScript.ScriptState.MiscAttributes.TryGetValue((int)VM.RuntimeState.MiscAttr.Control, out controlParams))
            {
                lock (m_host.TaskInventory)
                {
                    if (!m_host.TaskInventory.ContainsKey(InventorySelf()))
                        return;
                }

                // m_log.WarnFormat("[LSL]: OnGroupCrossedAvatarReady [{0}] LocalDisable={1} avatar={2}", m_itemID, _thisScript.ScriptState.LocalDisable, avatarId);
                TakeControlsInternal(-1, 0, (int)controlParams[0], (int)controlParams[1], (int)controlParams[2], true, avatarId);
            }
        }


        public void Relisten(int oldHandle, int channelID, string name, string ID, string msg)
        {
            int newHandle = this.ListenInternal(channelID, name, ID, msg);
            _listenRemap[oldHandle] = newHandle;
        }


        /// <summary>
        /// Reset the named script. The script must be present
        /// in the same prim.
        /// </summary>
        public void llResetScript()
        {
            // now reset the script itself
            m_ScriptEngine.ApiResetScript(m_itemID);
        }

        public void llResetOtherScript(string name)
        {
            UUID itemID;

            if ((itemID = ScriptByName(name)) != UUID.Zero)
            {
                // now reset the script itself
                m_ScriptEngine.ApiResetScript(itemID);
            }
            else
                ScriptShoutError("llResetOtherScript: script " + name + " not found");
        }

        public int llGetScriptState(string name)
        {
            UUID item;

            if ((item = ScriptByName(name)) != UUID.Zero)
            {
                return m_ScriptEngine.GetScriptState(item) ? 1 : 0;
            }

            ScriptShoutError("llGetScriptState: script " + name + " not found");

            // If we didn't find it, then it's safe to
            // assume it is not running.

            return 0;
        }

        public void llSetScriptState(string name, int run)
        {
            UUID item;

            // These functions are supposed to be robust,
            // so get the state one step at a time.

            if ((item = ScriptByName(name)) != UUID.Zero)
            {
                m_ScriptEngine.SetScriptState(item, run == 0 ? false : true);
            }
            else
            {
                ScriptShoutError("llSetScriptState: script " + name + " not found");
            }
        }

        private List<object> GetLinkParts(int linkType, bool includeAvatars)
        {
            List<object> ret = new List<object>();

            switch (linkType)
            {
                case ScriptBaseClass.LINK_SET:
                    if (m_host.ParentGroup != null)
                        ret.AddRange(m_host.ParentGroup.GetAllLinksAsList(includeAvatars));
                    else
                        ret.Add(m_host);
                    break;

                case ScriptBaseClass.LINK_ROOT:
                case 0: // the other LINK_ROOT linknum
                    if (m_host.ParentGroup != null)
                        ret.Add(m_host.ParentGroup.RootPart);
                    else
                        ret.Add(m_host);
                    break;

                case ScriptBaseClass.LINK_ALL_OTHERS:
                    if (m_host.ParentGroup != null)
                        ret.AddRange(m_host.ParentGroup.GetAllLinksAsListExcept(m_host, includeAvatars));
                    else
                        ret.Add(m_host);
                    break;

                case ScriptBaseClass.LINK_ALL_CHILDREN:
                    if (m_host.ParentGroup != null)
                        ret.AddRange(m_host.ParentGroup.GetAllLinksAsListExcept(m_host.ParentGroup.RootPart, includeAvatars));
                    else
                        ret.Add(m_host);
                    break;

                case ScriptBaseClass.LINK_THIS:
                    ret.Add(m_host);
                    break;

                default:
                    if (m_host.ParentGroup != null)
                    {
                        if (linkType > m_host.ParentGroup.PartCount)
                        {
                            if (includeAvatars)
                            {
                                ScenePresence targetSP = m_host.ParentGroup.GetSeatedAvatarByLink(linkType);
                                if (targetSP != null)
                                    ret.Add(targetSP);
                            }
                        }
                        else
                        if (linkType > 1)   // 0, 1 handled above
                        {
                            SceneObjectPart target = m_host.ParentGroup.GetLinkNumPart(linkType);
                            if (target != null)
                                ret.Add(target);
                        }
                    }
                    break;
            }

            return ret;
        }

        private List<object> GetLinkPrimsOnly(int linknum)
        {
            return GetLinkParts(linknum, false);
        }

        private SceneObjectPart GetLinkOnePrimOnly(int linknum)
        {
            SceneObjectPart part = null;
            List<object> links = GetLinkParts(linknum, false);
            if (links.First() is SceneObjectPart)
                part = links.First() as SceneObjectPart;
            return part;
        }

        private UUID InventorySelf()
        {
            UUID invItemID = new UUID();

            lock (m_host.TaskInventory)
            {
                foreach (KeyValuePair<UUID, TaskInventoryItem> inv in m_host.TaskInventory)
                {
                    if (inv.Value.Type == 10 && inv.Value.ItemID == m_itemID)
                    {
                        invItemID = inv.Key;
                        break;
                    }
                }
            }

            return invItemID;
        }

        private UUID InventoryKey(SceneObjectPart part, string name, int type)
        {
            lock (part.TaskInventory)
            {
                foreach (KeyValuePair<UUID, TaskInventoryItem> inv in part.TaskInventory)
                {
                    if (inv.Value.Name == name)
                    {
                        if ((inv.Value.Type != type) && (type != -1))
                            return UUID.Zero;

                        return inv.Value.AssetID;
                    }
                }
            }

            return UUID.Zero;

        }

        private UUID InventoryKey(string name, int type)
        {
            return InventoryKey(m_host, name, type);
        }

        private UUID InventoryKey(string name)
        {
            return InventoryKey(m_host, name, -1);
        }

        // returns a null string if asset ID not found (NOT a null ID as a string!)
        private string InventoryName(UUID assetID)
        {
            lock (m_host.TaskInventory)
            {
                foreach (KeyValuePair<UUID, TaskInventoryItem> inv in m_host.TaskInventory)
                {
                    if (inv.Value.AssetID == assetID)
                    {
                        return inv.Value.Name;
                    }
                }
            }

            return String.Empty;
        }

        /// <summary>
        /// Accepts a valid UUID, -or- a name of an inventory item.
        /// Returns a valid UUID or UUID.Zero if the item was not found in prim inventory and the key is invalid.
        ///
        /// Checks the inventory first as otherwise someone could exploit the system by naming their inventory object a UUID.
        /// </summary>
        /// <param name="k">The name of an inventory item or a UUID string.</param>
        /// <returns>either the UUID of the inventory item, UUID parsed from the string, or UUID.Zero.</returns>
        private UUID KeyOrName(string k)
        {
            UUID key = InventoryKey(k);

            // Try to locate the name in inventory of object first. If found return the key.
            // Doing this first means that no-one can name an inventory item with a UUID string and have this code return the UUID in the name instead of the inventory item's UUID!
            // If not, then if we can parse the string as a key, use it instead.
            if (key != UUID.Zero || UUID.TryParse(k, out key))
            {
                return key;
            }

            // Not found returns UUID.Zero which will translate to the default particle texture if this was a call about textures.
            return UUID.Zero;
        }

        // convert a LSL_Rotation to a Quaternion
        private Quaternion Rot2Quaternion(LSL_Rotation r)
        {
            Quaternion q = r;   // LSL_Rotation and Quaternion are the same type
            q.Normalize();
            return q;
        }

        //These are the implementations of the various ll-functions used by the LSL scripts.
        public float llSin(float f)
        {
            
            return (float)Math.Sin(f);
        }

        public float llCos(float f)
        {

            return (float)Math.Cos(f);
        }

        public float llTan(float f)
        {

            return (float)Math.Tan(f);
        }

        public float llAtan2(float x, float y)
        {

            return (float)Math.Atan2(x, y);
        }

        public float llSqrt(float f)
        {

            return (float)Math.Sqrt(f);
        }

        public float llPow(float fbase, float fexponent)
        {

            return (float)Math.Pow(fbase, fexponent);
        }

        public int llAbs(int i)
        {
            // changed to replicate LSL behaviour whereby minimum int value is returned untouched.
            
            if (i == Int32.MinValue)
                return i;
            else
                return (int)Math.Abs(i);
        }

        public float llFabs(float f)
        {

            return (float)Math.Abs(f);
        }

        public int iwIntRandRange(int min, int max)
        {
            if (min == max) return min;
            if (max < min) return s_random.Next(max, min + 1); //max needs to be more than minimum, swapping min and max
            return s_random.Next(min, max + 1);
        }

        public int iwIntRand(int max)
        {
            if (max < 0) return -1 * s_random.Next(Math.Abs(max) + 1);
            return s_random.Next(max + 1);
        }

        public float iwFrandRange(float min, float max)
        {
            if (min == max) return min;
            if (max < min) return (float)(s_random.NextDouble() * ((min) - max) + max); //max needs to be more than minimum, swapping min and max.
            return (float)(s_random.NextDouble() * ((max) - min) + min);
        }

        public float llFrand(float mag)
        {
            return (float)(s_random.NextDouble() * mag);
        }

        public int llFloor(float f)
        {
            
            return (int)Math.Floor(f);
        }

        public int llCeil(float f)
        {
            
            return (int)Math.Ceiling(f);
        }

        // Xantor 01/May/2008 fixed midpointrounding (2.5 becomes 3.0 instead of 2.0, default = ToEven)
        public int llRound(float f)
        {
            
            return (int)Math.Round(f, MidpointRounding.AwayFromZero);
        }

        //This next group are vector operations involving squaring and square root. ckrinke
        public float llVecMag(LSL_Vector v)
        {
            return LSL_Vector.Mag(v);
        }

        public LSL_Vector llVecNorm(LSL_Vector v)
        {
            return LSL_Vector.Normalize(v);
        }

        public float llVecDist(LSL_Vector a, LSL_Vector b)
        {
            
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            float dz = a.Z - b.Z;
            return (float) Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        //Now we start getting into quaternions which means sin/cos, matrices and vectors. ckrinke

        // Utility function for llRot2Euler

        // Old implementation of llRot2Euler, now normalized

        public Vector3 llRot2Euler(Quaternion r)
        {
            m_host.AddScriptLPS(1);

            Vector3 v = new Vector3(0.0f, 0.0f, 1.0f) * r;   // Z axis unit vector unaffected by Z rotation component of r.
            double m = Vector3.Mag(v);                       // Just in case v isn't normalized, need magnitude for Asin() operation later.
            if (m == 0.0) return new Vector3();
            double x = Math.Atan2(-v.Y, v.Z);
            double sin = v.X / m;
            if (sin < -0.999999 || sin > 0.999999) x = 0.0;     // Force X rotation to 0 at the singularities.
            double y = Math.Asin(sin);
            // Rotate X axis unit vector by r and unwind the X and Y rotations leaving only the Z rotation
            v = new Vector3(1.0f, 0.0f, 0.0f) * r * new Quaternion((float)Math.Sin(-x / 2.0), 0.0f, 0.0f, (float)Math.Cos(-x / 2.0)) * new Quaternion(0.0f, (float)Math.Sin(-y / 2.0), 0.0f, (float)Math.Cos(-y / 2.0));
            double z = Math.Atan2(v.Y, v.X);

            return new Vector3((float)x, (float)y, (float)z);
        }

        /* From wiki:
        The Euler angle vector (in radians) is converted to a rotation by doing the rotations around the 3 axes
        in Z, Y, X order. So llEuler2Rot(<1.0, 2.0, 3.0> * DEG_TO_RAD) generates a rotation by taking the zero rotation,
        a vector pointing along the X axis, first rotating it 3 degrees around the global Z axis, then rotating the resulting
        vector 2 degrees around the global Y axis, and finally rotating that 1 degree around the global X axis.
        */

        /* How we arrived at this llEuler2Rot
         *
         * Experiment in SL to determine conventions:
         *   llEuler2Rot(<PI,0,0>)=<1,0,0,0>
         *   llEuler2Rot(<0,PI,0>)=<0,1,0,0>
         *   llEuler2Rot(<0,0,PI>)=<0,0,1,0>
         *
         * Important facts about Quaternions
         *  - multiplication is non-commutative (a*b != b*a)
         *  - http://en.wikipedia.org/wiki/Quaternion#Basis_multiplication
         *
         * Above SL experiment gives (c1,c2,c3,s1,s2,s3 as defined in our llEuler2Rot):
         *   Qx = c1+i*s1
         *   Qy = c2+j*s2;
         *   Qz = c3+k*s3;
         *
         * Rotations applied in order (from above) Z, Y, X
         * Q = (Qz * Qy) * Qx
         * ((c1+i*s1)*(c2+j*s2))*(c3+k*s3)
         * (c1*c2+i*s1*c2+j*c1*s2+ij*s1*s2)*(c3+k*s3)
         * (c1*c2+i*s1*c2+j*c1*s2+k*s1*s2)*(c3+k*s3)
         * c1*c2*c3+i*s1*c2*c3+j*c1*s2*c3+k*s1*s2*c3+k*c1*c2*s3+ik*s1*c2*s3+jk*c1*s2*s3+kk*s1*s2*s3
         * c1*c2*c3+i*s1*c2*c3+j*c1*s2*c3+k*s1*s2*c3+k*c1*c2*s3 -j*s1*c2*s3 +i*c1*s2*s3   -s1*s2*s3
         * regroup: x=i*(s1*c2*c3+c1*s2*s3)
         *          y=j*(c1*s2*c3-s1*c2*s3)
         *          z=k*(s1*s2*c3+c1*c2*s3)
         *          s=   c1*c2*c3-s1*s2*s3
         *
         * This implementation agrees with the functions found here:
         * http://lslwiki.net/lslwiki/wakka.php?wakka=LibraryRotationFunctions
         * And with the results in SL.
         *
         * It's also possible to calculate llEuler2Rot by direct multiplication of
         * the Qz, Qy, and Qx vectors (as above - and done in the "accurate" function
         * from the wiki).
         * Apparently in some cases this is better from a numerical precision perspective?
         */

        public LSL_Rotation llEuler2Rot(LSL_Vector v)
        {
            float x, y, z, s;

            double c1 = Math.Cos(v.X / 2.0);
            double c2 = Math.Cos(v.Y / 2.0);
            double c3 = Math.Cos(v.Z / 2.0);
            double s1 = Math.Sin(v.X / 2.0);
            double s2 = Math.Sin(v.Y / 2.0);
            double s3 = Math.Sin(v.Z / 2.0);

            x = (float)(s1 * c2 * c3 + c1 * s2 * s3);
            y = (float)(c1 * s2 * c3 - s1 * c2 * s3);
            z = (float)(s1 * s2 * c3 + c1 * c2 * s3);
            s = (float)(c1 * c2 * c3 - s1 * s2 * s3);

            LSL_Rotation rot = LSL_Rotation.Normalize(new LSL_Rotation(x, y, z, s));

            return rot;
        }

        public LSL_Rotation llAxes2Rot(LSL_Vector fwd, LSL_Vector left, LSL_Vector up)
        {
            
            float s;
            double tr = fwd.X + left.Y + up.Z + 1.0;

            if (tr >= 1.0)
            {
                s = 0.5f / (float)Math.Sqrt(tr);
                return new LSL_Rotation(
                        (left.Z - up.Y) * s,
                        (up.X - fwd.Z) * s,
                        (fwd.Y - left.X) * s,
                        0.25f / s);
            }
            else
            {
                double max = (left.Y > up.Z) ? left.Y : up.Z;

                if (max < fwd.X)
                {
                    s = (float) Math.Sqrt(fwd.X - (left.Y + up.Z) + 1.0);
                    float x = s * 0.5f;
                    s = 0.5f / s;
                    return new LSL_Rotation(
                            x,
                            (fwd.Y + left.X) * s,
                            (up.X + fwd.Z) * s,
                            (left.Z - up.Y) * s);
                }
                else if (max == left.Y)
                {
                    s = (float) Math.Sqrt(left.Y - (up.Z + fwd.X) + 1.0);
                    float y = s * 0.5f;
                    s = 0.5f / s;
                    return new LSL_Rotation(
                            (fwd.Y + left.X) * s,
                            y,
                            (left.Z + up.Y) * s,
                            (up.X - fwd.Z) * s);
                }
                else
                {
                    s = (float)Math.Sqrt(up.Z - (fwd.X + left.Y) + 1.0);
                    float z = s * 0.5f;
                    s = 0.5f / s;
                    return new LSL_Rotation(
                            (up.X + fwd.Z) * s,
                            (left.Z + up.Y) * s,
                            z,
                            (fwd.Y - left.X) * s);
                }
            }
        }

        public LSL_Vector llRot2Fwd(LSL_Rotation r)
        {
            return LSL_Vector.Normalize(new LSL_Vector(1.0f, 0.0f, 0.0f) * r);
        }

        public LSL_Vector llRot2Left(LSL_Rotation r)
        {
            return LSL_Vector.Normalize(new LSL_Vector(0.0f, 1.0f, 0.0f) * r);
        }

        public LSL_Vector llRot2Up(LSL_Rotation r)
        {
            return LSL_Vector.Normalize(new LSL_Vector(0.0f, 0.0f, 1.0f) * r);
        }

        public LSL_Rotation llRotBetween(LSL_Vector a, LSL_Vector b)
        {
            //A and B should both be normalized
            
            double dotProduct = LSL_Vector.Dot(a, b);
            LSL_Vector crossProduct = LSL_Vector.Cross(a, b);
            double magProduct = LSL_Vector.Mag(a) * LSL_Vector.Mag(b);
            double angle = Math.Acos(dotProduct / magProduct);
            LSL_Vector axis = LSL_Vector.Normalize(crossProduct);
            double s = Math.Sin(angle / 2);

            double x = axis.X * s;
            double y = axis.Y * s;
            double z = axis.Z * s;
            double w = Math.Cos(angle / 2);

            if (Double.IsNaN(x) || Double.IsNaN(y) || Double.IsNaN(z) || Double.IsNaN(w))
                return new LSL_Rotation(0.0f, 0.0f, 0.0f, 1.0f);

            return LSL_Rotation.Normalize(new LSL_Rotation((float)x, (float)y, (float)z, (float)w));
        }

        public static void ChatFromObject(int channelID, string text, ChatTypeEnum type, Scene world, SceneObjectPart part, UUID destID)
        {
            if (text.Length > 1023)
                text = text.Substring(0, 1023);

            world.SimChat(text, type, channelID, part, destID);

            IWorldComm wComm = world.RequestModuleInterface<IWorldComm>();
            wComm.DeliverMessage(type, channelID, part.Name, part.UUID, text, destID);
        }

        private void SimChat(int channelID, string text, ChatTypeEnum type, UUID destID)
        {
            ChatFromObject(channelID, text, type, World, m_host, destID);
            ScriptSleep(15);
        }

        private void SimChat(int channelID, string text, ChatTypeEnum type)
        {
            ChatFromObject(channelID, text, type, World, m_host, UUID.Zero);
            ScriptSleep(15);
        }

        public void llWhisper(int channelID, string text)
        {
            this.SimChat(channelID, text, ChatTypeEnum.Whisper);
        }

        public void llSay(int channelID, string text)
        {
            this.SimChat(channelID, text, ChatTypeEnum.Say);
        }

        public void llShout(int channelID, string text)
        {
            this.SimChat(channelID, text, ChatTypeEnum.Shout);
        }

        public void llRegionSay(int channelID, string text)
        {
            if (channelID == 0)
            {
                LSLError("Cannot use llRegionSay() on channel 0");
                return;
            }

            if (text.Length > 1023)
                text = text.Substring(0, 1023);

            
            IWorldComm wComm = m_ScriptEngine.World.RequestModuleInterface<IWorldComm>();
            wComm.DeliverMessage(ChatTypeEnum.Region, channelID, m_host.Name, m_host.UUID, text, UUID.Zero);

            ScriptSleep(15);
        }

        public void llRegionSayTo(string destination, int channelID, string text)
        {
            if (channelID == ScriptBaseClass.DEBUG_CHANNEL)
            {
                LSLError("Cannot use llRegionSayTo() on DEBUG_CHANNEL.");
                return;
            }

            UUID destKey;
            UUID.TryParse(destination, out destKey);
            if (destKey == UUID.Zero) return;

            this.SimChat(channelID, text, ChatTypeEnum.Direct, destKey);
        }

        public int ListenInternal(int channelID, string name, string ID, string msg)
        {
            UUID keyID;
            UUID.TryParse(ID, out keyID);
            IWorldComm wComm = m_ScriptEngine.World.RequestModuleInterface<IWorldComm>();
            return wComm.Listen(m_localID, m_itemID, m_host.UUID, channelID, name, keyID, msg);
        }

        public int llListen(int channelID, string name, string ID, string msg)
        {
            int handle = this.ListenInternal(channelID, name, ID, msg);

            _thisScript.ScriptState.AddActiveListen(
                new VM.ActiveListen { Handle = handle, Channel = channelID, Name = name, Key = ID, Message = msg });

            return handle;
        }

        public void llListenControl(int number, int active)
        {
            
            IWorldComm wComm = m_ScriptEngine.World.RequestModuleInterface<IWorldComm>();
            wComm.ListenControl(m_itemID, number, active);
        }

        public void llListenRemove(int number)
        {
            IWorldComm wComm = m_ScriptEngine.World.RequestModuleInterface<IWorldComm>();

            int remap;
            if (_listenRemap.TryGetValue(number, out remap))
            {
                wComm.ListenRemove(m_itemID, remap);
                _listenRemap.Remove(number);
            }
            else
            {
                wComm.ListenRemove(m_itemID, number);
            }

            _thisScript.ScriptState.RemoveListen(number);
        }

        public void llSensor(string name, string id, int type, float range, float arc)
        {
            
            UUID keyID = UUID.Zero;
            UUID.TryParse(id, out keyID);

            AsyncCommands.SensorRepeatPlugin.SenseOnce(m_localID, m_itemID, name, keyID, type, range, arc, m_host);
        }

        public void llSensorRepeat(string name, string id, int type, float range, float arc, float rate)
        {
            
            UUID keyID = UUID.Zero;
            UUID.TryParse(id, out keyID);

            AsyncCommands.SensorRepeatPlugin.SetSenseRepeatEvent(m_localID, m_itemID, name, keyID, type, range, arc, rate, m_host);
            _thisScript.ScriptState.MiscAttributes[(int)VM.RuntimeState.MiscAttr.SensorRepeat] =
                new object[6] { name, id, type, range, arc, rate };
        }

        public void llSensorRemove()
        {
            
            AsyncCommands.SensorRepeatPlugin.UnSetSenseRepeaterEvents(m_localID, m_itemID);
            _thisScript.ScriptState.MiscAttributes.Remove((int)VM.RuntimeState.MiscAttr.SensorRepeat);
        }

        public string resolveName(UUID objecUUID)
        {
            // try avatar username surname
            string name = World.CommsManager.UserService.Key2Name(objecUUID, false);
            if (!String.IsNullOrEmpty(name))
            {
                return name;
            }

            // try an scene object
            SceneObjectPart SOP = World.GetSceneObjectPart(objecUUID);
            if (SOP != null)
            {
                string objectname = SOP.Name;
                return objectname;
            }

            EntityBase SensedObject;
            World.Entities.TryGetValue(objecUUID, out SensedObject);

            if (SensedObject == null)
                return String.Empty;
            return SensedObject.Name;
        }

        public string llDetectedName(int number)
        {
            
            VM.DetectVariables detectedParams = _thisScript.ScriptState.GetDetectVariables(number);
            if (detectedParams == null)
                return String.Empty;
            return detectedParams.Name;
        }

        public string llDetectedKey(int number)
        {
            
            VM.DetectVariables detectedParams = _thisScript.ScriptState.GetDetectVariables(number);
            if (detectedParams == null)
                return String.Empty;
            return detectedParams.Key.ToString();
        }

        public string llDetectedOwner(int number)
        {
            
            VM.DetectVariables detectedParams = _thisScript.ScriptState.GetDetectVariables(number);
            if (detectedParams == null)
                return String.Empty;
            return detectedParams.Owner.ToString();
        }

        public int llDetectedType(int number)
        {
            
            VM.DetectVariables detectedParams = _thisScript.ScriptState.GetDetectVariables(number);
            if (detectedParams == null)
                return 0;
            return (int)(detectedParams.Type);
        }

        public LSL_Vector llDetectedPos(int number)
        {
            
            VM.DetectVariables detectedParams = _thisScript.ScriptState.GetDetectVariables(number);
            if (detectedParams == null)
                return new LSL_Vector();
            return detectedParams.Pos;
        }

        public LSL_Vector llDetectedVel(int number)
        {
            
            VM.DetectVariables detectedParams = _thisScript.ScriptState.GetDetectVariables(number);
            if (detectedParams == null)
                return new LSL_Vector();
            return detectedParams.Vel;
        }

        public LSL_Vector llDetectedGrab(int number)
        {

            VM.DetectVariables detectedParams = _thisScript.ScriptState.GetDetectVariables(number);
            if (detectedParams == null)
                return Vector3.Zero;

            return detectedParams.Grab;
        }

        public LSL_Rotation llDetectedRot(int number)
        {
            
            VM.DetectVariables detectedParams = _thisScript.ScriptState.GetDetectVariables(number);
            if (detectedParams == null)
                return new LSL_Rotation();
            return detectedParams.Rot;
        }

        public int llDetectedGroup(int number)
        {
            
            VM.DetectVariables detectedParams = _thisScript.ScriptState.GetDetectVariables(number);
            if (detectedParams == null)
                return 0;
            if (m_host.GroupID.ToString() == detectedParams.Group)
                return 1;
            return 0;
        }

        public int llDetectedLinkNumber(int number)
        {

            VM.DetectVariables detectedParams = _thisScript.ScriptState.GetDetectVariables(number);
            if (detectedParams == null)
                return 0;

            return detectedParams.LinkNumber;
        }

        /// <summary>
        /// See http://wiki.secondlife.com/wiki/LlDetectedTouchBinormal for details
        /// </summary>
        public LSL_Vector llDetectedTouchBinormal(int index)
        {

            VM.DetectVariables detectedParams = _thisScript.ScriptState.GetDetectVariables(index);
            if (detectedParams == null)
                return new LSL_Vector();
            return detectedParams.TouchBinormal;
        }

        /// <summary>
        /// See http://wiki.secondlife.com/wiki/LlDetectedTouchFace for details
        /// </summary>
        public int llDetectedTouchFace(int index)
        {

            VM.DetectVariables detectedParams = _thisScript.ScriptState.GetDetectVariables(index);
            if (detectedParams == null)
                return -1;
            return detectedParams.TouchFace;
        }

        /// <summary>
        /// See http://wiki.secondlife.com/wiki/LlDetectedTouchNormal for details
        /// </summary>
        public LSL_Vector llDetectedTouchNormal(int index)
        {

            VM.DetectVariables detectedParams = _thisScript.ScriptState.GetDetectVariables(index);
            if (detectedParams == null)
                return new LSL_Vector();
            return detectedParams.TouchNormal;
        }

        /// <summary>
        /// See http://wiki.secondlife.com/wiki/LlDetectedTouchPos for details
        /// </summary>
        public LSL_Vector llDetectedTouchPos(int index)
        {

            VM.DetectVariables detectedParams = _thisScript.ScriptState.GetDetectVariables(index);
            if (detectedParams == null)
                return new LSL_Vector();
            return detectedParams.TouchPos;
        }

        /// <summary>
        /// See http://wiki.secondlife.com/wiki/LlDetectedTouchST for details
        /// </summary>
        public LSL_Vector llDetectedTouchST(int index)
        {

            VM.DetectVariables detectedParams = _thisScript.ScriptState.GetDetectVariables(index);
            if (detectedParams == null)
                return new LSL_Vector(-1.0f, -1.0f, 0.0f);
            return detectedParams.TouchST;
        }

        /// <summary>
        /// See http://wiki.secondlife.com/wiki/LlDetectedTouchUV for details
        /// </summary>
        public LSL_Vector llDetectedTouchUV(int index)
        {

            VM.DetectVariables detectedParams = _thisScript.ScriptState.GetDetectVariables(index);
            if (detectedParams == null)
                return new LSL_Vector(-1.0f, -1.0f, 0.0f);
            return detectedParams.TouchUV;
        }

        public void llDie()
        {
            if (m_host.ParentGroup.IsAttachment)
                return; // silently fail. See http://wiki.secondlife.com/wiki/LlDie
            m_ScriptEngine.World.DeleteSceneObject(m_host.ParentGroup, false);
            _thisScript.ScriptState.RunState = VM.RuntimeState.Status.Killed;
        }

        private LSL_Vector ValidLocation(float argx, float argy, float argz, bool limitGround)
        {
            float x = argx;
            float y = argy;
            float z = argz;
            float xmax = (float)World.Heightmap.Width - 0.01f;
            float ymax = (float)World.Heightmap.Height - 0.01f;
            float zmin;

            // Clamp to valid position
            if (x < 0.0f)
                x = 0.0f;
            else if (x >= xmax)
                x = xmax;
            if (y < 0.0f)
                y = 0.0f;
            else if (y >= ymax)
                y = ymax;
            if (limitGround)
            {
                zmin = (float)World.Heightmap.CalculateHeightAt(x, y);
                if (z < zmin)
                    z = zmin;
            }

            return new LSL_Vector(x, y, z);
        }

        public float llGround(LSL_Vector offset)
        {
            Vector3 pos = m_host.GetWorldPosition();
            float x = pos.X + offset.X;
            float y = pos.Y + offset.Y;

            LSL_Vector validated = ValidLocation(x, y, float.MinValue, true);
            return validated.Z;
        }

        public float llCloud(LSL_Vector offset)
        {
            
            float cloudCover = 0f;
            ICloudModule module = World.RequestModuleInterface<ICloudModule>();
            if (module != null)
            {
                Vector3 pos = m_host.GetWorldPosition();
                int x = (int)(pos.X + offset.X);
                int y = (int)(pos.Y + offset.Y);

                cloudCover = module.CloudCover(x, y, 0);

            }
            return cloudCover;
        }

        public LSL_Vector llWind(LSL_Vector offset)
        {
            
            LSL_Vector wind = iwWind(offset);

            // Maintain compatibility. llWind's legacy behavior is
            // that is does not return any z-data.
            wind.Z = 0.0f;

            return wind;
        }

        public LSL_Vector iwWind(LSL_Vector offset)
        {
            
            LSL_Vector wind = new LSL_Vector(0, 0, 0);
            IWindModule module = World.RequestModuleInterface<IWindModule>();
            if (module != null)
            {
                Vector3 pos = m_host.GetWorldPosition();
                int x = (int)(pos.X + offset.X);
                int y = (int)(pos.Y + offset.Y);
                int z = (int)(pos.Z + offset.Z);

                Vector3 windSpeed = module.WindSpeed(x, y, z);

                wind.X = windSpeed.X;
                wind.Y = windSpeed.Y;
                wind.Z = windSpeed.Z;
            }
            return wind;
        }

        public void iwSetWind(int type, LSL_Vector offset, LSL_Vector speed)
        {
            Scene scene = m_host.ParentGroup.Scene;
            if (scene.Permissions.CanIssueEstateCommand(m_host.OwnerID, false) || World.Permissions.IsGod(m_host.OwnerID))
            {
                IWindModule module = World.RequestModuleInterface<IWindModule>();
                if (module != null)
                {
                    Vector3 pos = m_host.GetWorldPosition();
                    pos += offset;
                    module.WindSet(type, pos, speed);
                }
            }
            return;
        }

        public void llSetStatus(int status, int value)
        {
            int statusrotationaxis = 0;

            if ((status & ScriptBaseClass.STATUS_PHYSICS) == ScriptBaseClass.STATUS_PHYSICS)
            {
                if (value != 0)
                {
                    SceneObjectGroup group = m_host.ParentGroup;
                    if (group == null)
                        return;
                    bool allow = true;
                    foreach (SceneObjectPart part in group.GetParts())
                    {
                        if (part.Scale.X > World.m_maxPhys || part.Scale.Y > World.m_maxPhys || part.Scale.Z > World.m_maxPhys)
                        {
                            allow = false;
                            break;
                        }
                    }

                    if (!allow)
                        return;

                    m_host.ScriptSetPhysicsStatus(true);
                }
                else
                {
                    m_host.ScriptSetPhysicsStatus(false);
                }

                PhySleep();
            }

            if ((status & ScriptBaseClass.STATUS_PHANTOM) == ScriptBaseClass.STATUS_PHANTOM)
            {
                if (value != 0)
                    m_host.ScriptSetPhantomStatus(true);
                else
                    m_host.ScriptSetPhantomStatus(false);

                PhySleep();
            }

            if ((status & ScriptBaseClass.STATUS_CAST_SHADOWS) == ScriptBaseClass.STATUS_CAST_SHADOWS)
            {
                m_host.AddFlag(PrimFlags.CastShadows);
            }

            if ((status & ScriptBaseClass.STATUS_ROTATE_X) == ScriptBaseClass.STATUS_ROTATE_X)
            {
                statusrotationaxis |= ScriptBaseClass.STATUS_ROTATE_X;
            }

            if ((status & ScriptBaseClass.STATUS_ROTATE_Y) == ScriptBaseClass.STATUS_ROTATE_Y)
            {
                statusrotationaxis |= ScriptBaseClass.STATUS_ROTATE_Y;
            }

            if ((status & ScriptBaseClass.STATUS_ROTATE_Z) == ScriptBaseClass.STATUS_ROTATE_Z)
            {
                statusrotationaxis |= ScriptBaseClass.STATUS_ROTATE_Z;
            }

            if ((status & ScriptBaseClass.STATUS_BLOCK_GRAB) == ScriptBaseClass.STATUS_BLOCK_GRAB)
            {
                m_host.SetBlockGrab(value != 0);
            }

            if ((status & ScriptBaseClass.STATUS_BLOCK_GRAB_OBJECT) == ScriptBaseClass.STATUS_BLOCK_GRAB_OBJECT)
            {
                m_host.SetBlockGrab(value != 0);
            }

            if ((status & ScriptBaseClass.STATUS_DIE_AT_EDGE) == ScriptBaseClass.STATUS_DIE_AT_EDGE)
            {
                if (value != 0)
                    m_host.SetDieAtEdge(true);
                else
                    m_host.SetDieAtEdge(false);
            }

            if ((status & ScriptBaseClass.STATUS_RETURN_AT_EDGE) == ScriptBaseClass.STATUS_RETURN_AT_EDGE)
            {
                NotImplemented("llSetStatus - STATUS_RETURN_AT_EDGE");
            }

            if ((status & ScriptBaseClass.STATUS_SANDBOX) == ScriptBaseClass.STATUS_SANDBOX)
            {
                NotImplemented("llSetStatus - STATUS_SANDBOX");
            }

            if (statusrotationaxis != 0)
            {
                float locked = (value != 0) ? 0 : 1;

                PhysicsActor physActor = m_host.PhysActor;
                if (physActor != null)
                {
                    // Get current locked axis settings.
                    OpenMetaverse.Vector3 lockedaxis = physActor.GetLockedAngularMotion();

                    // Adjust them based on the flags.
                    if ((statusrotationaxis & ScriptBaseClass.STATUS_ROTATE_X) != 0)
                        lockedaxis.X = locked;
                    if ((statusrotationaxis & ScriptBaseClass.STATUS_ROTATE_Y) != 0)
                        lockedaxis.Y = locked;
                    if ((statusrotationaxis & ScriptBaseClass.STATUS_ROTATE_Z) != 0)
                        lockedaxis.Z = locked;

                    // Convert the rotation axes to locked axes.
                    physActor.LockAngularMotion(lockedaxis);
                }
            }
        }

        bool lastWasSleep = false;
        private void PhySleep()
        {
            int cmdTime = m_host.ParentGroup.Scene.PhysicsScene.SimulationFrameTimeAvg;
            if (cmdTime > MAX_PHYSICS_TIME_BEFORE_DILATION)
            {
                ScriptSleep(cmdTime);

                if (!lastWasSleep)
                {
                    //m_log.InfoFormat("sleep {0}", cmdTime);
                    lastWasSleep = !lastWasSleep;
                }
            }
            else
            {
                if (lastWasSleep)
                {
                    //m_log.InfoFormat("nosleep");
                    lastWasSleep = !lastWasSleep;
                }
            }
        }

        public int llGetStatus(int status)
        {
            // For the rotation queries
            OpenMetaverse.Vector3 lockedaxis = OpenMetaverse.Vector3.Zero;
            PhysicsActor physActor = m_host.PhysActor;
            if (physActor != null)
                lockedaxis = physActor.GetLockedAngularMotion();
            
            // m_log.Debug(m_host.ToString() + " status is " + m_host.GetEffectiveObjectFlags().ToString());
            switch (status)
            {
                case ScriptBaseClass.STATUS_PHYSICS:
                    if ((m_host.GetEffectiveObjectFlags() & PrimFlags.Physics) == PrimFlags.Physics)
                    {
                        return 1;
                    }
                    return 0;

                case ScriptBaseClass.STATUS_PHANTOM:
                    if ((m_host.GetEffectiveObjectFlags() & PrimFlags.Phantom) == PrimFlags.Phantom)
                    {
                        return 1;
                    }
                    return 0;

                case ScriptBaseClass.STATUS_CAST_SHADOWS:
                    if ((m_host.GetEffectiveObjectFlags() & PrimFlags.CastShadows) == PrimFlags.CastShadows)
                    {
                        return 1;
                    }
                    return 0;

                case ScriptBaseClass.STATUS_BLOCK_GRAB:
                    return m_host.GetBlockGrab() ? 1 : 0;

                case ScriptBaseClass.STATUS_BLOCK_GRAB_OBJECT:
                    return m_host.GetBlockGrab() ? 1 : 0;

                case ScriptBaseClass.STATUS_DIE_AT_EDGE:
                    if (m_host.GetDieAtEdge())
                        return 1;
                    else
                        return 0;

                case ScriptBaseClass.STATUS_RETURN_AT_EDGE:
                    NotImplemented("llGetStatus - STATUS_RETURN_AT_EDGE");
                    return 0;

                case ScriptBaseClass.STATUS_ROTATE_X:
                    return (int)(1.0f - lockedaxis.X);

                case ScriptBaseClass.STATUS_ROTATE_Y:
                    return (int)(1.0f - lockedaxis.Y);

                case ScriptBaseClass.STATUS_ROTATE_Z:
                    return (int)(1.0f - lockedaxis.Z);

                case ScriptBaseClass.STATUS_SANDBOX:
                    NotImplemented("llGetStatus - STATUS_SANDBOX");
                    return 0;
            }
            return 0;
        }

        public void llSetScale(LSL_Vector scale)
        {
            SetScale(m_host, scale);
        }

        private void SetScale(SceneObjectPart part, LSL_Vector scale)
        {
            // TODO: this needs to trigger a persistance save as well

            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return;

            if ((scale.X < SceneObjectPart.MIN_PART_SCALE) || (scale.Y < SceneObjectPart.MIN_PART_SCALE) || (scale.Z < SceneObjectPart.MIN_PART_SCALE))
                return;

            PhysicsActor physActor = part.ParentGroup.RootPart.PhysActor;
            if (physActor != null && physActor.IsPhysical)
            {
                if (scale.X > World.m_maxPhys)
                    scale.X = World.m_maxPhys;
                if (scale.Y > World.m_maxPhys)
                    scale.Y = World.m_maxPhys;
                if (scale.Z > World.m_maxPhys)
                    scale.Z = World.m_maxPhys;
            }
            if (scale.X > World.m_maxNonphys)
                scale.X = World.m_maxNonphys;
            if (scale.Y > World.m_maxNonphys)
                scale.Y = World.m_maxNonphys;
            if (scale.Z > World.m_maxNonphys)
                scale.Z = World.m_maxNonphys;
            Vector3 tmp = part.Scale;
            tmp.X = (float)scale.X;
            tmp.Y = (float)scale.Y;
            tmp.Z = (float)scale.Z;
            part.Scale = tmp;
            part.ScheduleFullUpdate(PrimUpdateFlags.FindBest);

            PhySleep();
        }

        public LSL_Vector llGetScale()
        {
            return new LSL_Vector(m_host.Scale.X, m_host.Scale.Y, m_host.Scale.Z);
        }

        public void llSetClickAction(int action)
        {
            m_host.ClickAction = (byte)action;
            if (m_host.ParentGroup != null) m_host.ParentGroup.HasGroupChanged = true;
            m_host.ScheduleFullUpdate(PrimUpdateFlags.FindBest);
            return;
        }

        public void llSetColor(LSL_Vector color, int face)
        {
            SetColor(m_host, color, face);
        }

        private void SetColor(SceneObjectPart part, LSL_Vector color, int face)
        {
            Primitive.TextureEntry tex = part.Shape.Textures;
            Color4 texcolor;
            if (face >= 0 && face < part.GetNumberOfSides())
            {
                texcolor = tex.CreateFace((uint)face).RGBA;
                texcolor.R = Util.Clip((float)color.X, 0.0f, 1.0f);
                texcolor.G = Util.Clip((float)color.Y, 0.0f, 1.0f);
                texcolor.B = Util.Clip((float)color.Z, 0.0f, 1.0f);
                tex.FaceTextures[face].RGBA = texcolor;
                part.UpdateTexture(tex, Changed.COLOR);
                return;
            }
            else if (face == ScriptBaseClass.ALL_SIDES)
            {
                for (uint i = 0; i < part.GetNumberOfSides(); i++)
                {
                    if (tex.FaceTextures[i] != null)
                    {
                        texcolor = tex.FaceTextures[i].RGBA;
                        texcolor.R = Util.Clip((float)color.X, 0.0f, 1.0f);
                        texcolor.G = Util.Clip((float)color.Y, 0.0f, 1.0f);
                        texcolor.B = Util.Clip((float)color.Z, 0.0f, 1.0f);
                        tex.FaceTextures[i].RGBA = texcolor;
                    }
                    texcolor = tex.DefaultTexture.RGBA;
                    texcolor.R = Util.Clip((float)color.X, 0.0f, 1.0f);
                    texcolor.G = Util.Clip((float)color.Y, 0.0f, 1.0f);
                    texcolor.B = Util.Clip((float)color.Z, 0.0f, 1.0f);
                    tex.DefaultTexture.RGBA = texcolor;
                }
                part.UpdateTexture(tex, Changed.COLOR);
                return;
            }
        }

        public void SetTexGen(SceneObjectPart part, int face, int style)
        {
            Primitive.TextureEntry tex = part.Shape.Textures;
            MappingType textype;
            textype = MappingType.Default;
            if (style == (int)ScriptBaseClass.PRIM_TEXGEN_PLANAR)
                textype = MappingType.Planar;

            if (face >= 0 && face < part.GetNumberOfSides())
            {
                tex.CreateFace((uint)face);
                tex.FaceTextures[face].TexMapType = textype;
                part.UpdateTexture(tex, 0); // no changed notification for this
                return;
            }
            else if (face == ScriptBaseClass.ALL_SIDES)
            {
                for (uint i = 0; i < part.GetNumberOfSides(); i++)
                {
                    if (tex.FaceTextures[i] != null)
                    {
                        tex.FaceTextures[i].TexMapType = textype;
                    }
                    tex.DefaultTexture.TexMapType = textype;
                }
                part.UpdateTexture(tex, 0); // no changed notification for this
                return;
            }
        }

        public void SetGlow(SceneObjectPart part, int face, float glow)
        {
            Primitive.TextureEntry tex = part.Shape.Textures;
            if (face >= 0 && face < part.GetNumberOfSides())
            {
                tex.CreateFace((uint)face);
                tex.FaceTextures[face].Glow = glow;
                part.UpdateTexture(tex);
                return;
            }
            else if (face == ScriptBaseClass.ALL_SIDES)
            {
                for (uint i = 0; i < part.GetNumberOfSides(); i++)
                {
                    if (tex.FaceTextures[i] != null)
                    {
                        tex.FaceTextures[i].Glow = glow;
                    }
                    tex.DefaultTexture.Glow = glow;
                }
                part.UpdateTexture(tex);
                return;
            }
        }

        public void SetShiny(SceneObjectPart part, int face, int shiny, Bumpiness bump)
        {
            Shininess sval = new Shininess();

            switch (shiny)
            {
                case 0:
                    sval = Shininess.None;
                    break;
                case 1:
                    sval = Shininess.Low;
                    break;
                case 2:
                    sval = Shininess.Medium;
                    break;
                case 3:
                    sval = Shininess.High;
                    break;
                default:
                    sval = Shininess.None;
                    break;
            }

            Primitive.TextureEntry tex = part.Shape.Textures;
            if (face >= 0 && face < part.GetNumberOfSides())
            {
                tex.CreateFace((uint)face);
                tex.FaceTextures[face].Shiny = sval;
                tex.FaceTextures[face].Bump = bump;
                part.UpdateTexture(tex);
                return;
            }
            else if (face == ScriptBaseClass.ALL_SIDES)
            {
                for (uint i = 0; i < part.GetNumberOfSides(); i++)
                {
                    if (tex.FaceTextures[i] != null)
                    {
                        tex.FaceTextures[i].Shiny = sval;
                        tex.FaceTextures[i].Bump = bump; ;
                    }
                    tex.DefaultTexture.Shiny = sval;
                    tex.DefaultTexture.Bump = bump;
                }
                part.UpdateTexture(tex);
                return;
            }
        }

        public void SetFullBright(SceneObjectPart part, int face, bool bright)
        {
            Primitive.TextureEntry tex = part.Shape.Textures;
            if (face >= 0 && face < part.GetNumberOfSides())
            {
                tex.CreateFace((uint)face);
                tex.FaceTextures[face].Fullbright = bright;
                part.UpdateTexture(tex);
                return;
            }
            else if (face == ScriptBaseClass.ALL_SIDES)
            {
                for (uint i = 0; i < part.GetNumberOfSides(); i++)
                {
                    if (tex.FaceTextures[i] != null)
                    {
                        tex.FaceTextures[i].Fullbright = bright;
                    }
                }
                tex.DefaultTexture.Fullbright = bright;
                part.UpdateTexture(tex);
                return;
            }
        }

        public float llGetAlpha(int face)
        {
            return GetAlpha(m_host, face);
        }

        private float GetAlpha(SceneObjectPart part, int face)
        {
            Primitive.TextureEntry tex = part.Shape.Textures;
            if (face == ScriptBaseClass.ALL_SIDES)
            {
                int i;
                double sum = 0.0;
                for (i = 0; i < part.GetNumberOfSides(); i++)
                    sum += (double)tex.GetFace((uint)i).RGBA.A;
                return (float)sum;
            }
            if (face >= 0 && face < part.GetNumberOfSides())
            {
                return (float)tex.GetFace((uint)face).RGBA.A;
            }
            return 0.0f;
        }

        public void llSetAlpha(float alpha, int face)
        {
            SetAlpha(m_host, alpha, face);
        }

        public void llSetLinkAlpha(int linknumber, float alpha, int face)
        {
            var parts = GetLinkPrimsOnly(linknumber);

            foreach (SceneObjectPart part in parts)
                SetAlpha(part, alpha, face);
        }

        private void SetAlpha(SceneObjectPart part, double alpha, int face)
        {
            Primitive.TextureEntry tex = part.Shape.Textures;
            Color4 texcolor;
            if (face >= 0 && face < part.GetNumberOfSides())
            {
                texcolor = tex.CreateFace((uint)face).RGBA;
                texcolor.A = Util.Clip((float)alpha, 0.0f, 1.0f);
                tex.FaceTextures[face].RGBA = texcolor;
                part.UpdateTexture(tex, Changed.COLOR);
                return;
            }
            else if (face == ScriptBaseClass.ALL_SIDES)
            {
                for (int i = 0; i < part.GetNumberOfSides(); i++)
                {
                    if (tex.FaceTextures[i] != null)
                    {
                        texcolor = tex.FaceTextures[i].RGBA;
                        texcolor.A = Util.Clip((float)alpha, 0.0f, 1.0f);
                        tex.FaceTextures[i].RGBA = texcolor;
                    }
                }
                texcolor = tex.DefaultTexture.RGBA;
                texcolor.A = Util.Clip((float)alpha, 0.0f, 1.0f);
                tex.DefaultTexture.RGBA = texcolor;
                part.UpdateTexture(tex, Changed.COLOR);
                return;
            }
        }

        /// <summary>
        /// Set flexi parameters of a part.
        ///
        /// FIXME: Much of this code should probably be within the part itself.
        /// </summary>
        /// <param name="part"></param>
        /// <param name="flexi"></param>
        /// <param name="softness"></param>
        /// <param name="gravity"></param>
        /// <param name="friction"></param>
        /// <param name="wind"></param>
        /// <param name="tension"></param>
        /// <param name="Force"></param>
        private void SetFlexi(SceneObjectPart part, bool flexi, int softness, float gravity, float friction,
            float wind, float tension, LSL_Vector Force)
        {
            if (part == null)
                return;

            if (flexi)
            {
                if (part.IsRootPart())
                {
                    //this is a hack. We need to set the status first so that the comparison of current
                    //vs desired physics state doesnt return no changes since flexi == phantom
                    part.ScriptSetPhantomStatus(true); 
                    SetPhantomPropertiesOnPart(part, softness, gravity, friction, wind, tension, Force);
                }
                else
                {
                    SetPhantomPropertiesOnPart(part, softness, gravity, friction, wind, tension, Force);
                    part.PhysicsShapeChanged();
                }
            }
            else
            {
                int curve = part.Shape.PathCurve;
                curve &= (int)(~(Extrusion.Flexible));  // Flexible is NOT a bit mask... hmm. This needed? Should always already be off here.
                if (curve == 0)
                    curve = (byte)Extrusion.Straight;         // make sure it's valid.  Zero is not.
                part.Shape.PathCurve = (byte)curve;
                part.Shape.FlexiEntry = false;

                if (! part.IsRootPart())
                {
                    //restores physics for this part
                    part.PhysicsShapeChanged();
                }
            }

            part.ParentGroup.HasGroupChanged = true;
            part.ScheduleFullUpdate(PrimUpdateFlags.FullUpdate);
        }

        private static void SetPhantomPropertiesOnPart(SceneObjectPart part, int softness, float gravity, float friction, float wind, float tension, LSL_Vector Force)
        {
            part.Shape.FlexiEntry = true;   // this setting flexi true isn't working, but the below parameters do
            // work once the prim is already flexi
            part.Shape.FlexiSoftness = softness;
            part.Shape.FlexiGravity = gravity;
            part.Shape.FlexiDrag = friction;
            part.Shape.FlexiWind = wind;
            part.Shape.FlexiTension = tension;
            part.Shape.FlexiForceX = (float)Force.X;
            part.Shape.FlexiForceY = (float)Force.Y;
            part.Shape.FlexiForceZ = (float)Force.Z;
            part.Shape.PathCurve = (byte)Extrusion.Flexible;
        }

        /// <summary>
        /// Set a light point on a part
        /// </summary>
        /// FIXME: Much of this code should probably be in SceneObjectGroup
        /// 
        /// <param name="part"></param>
        /// <param name="light"></param>
        /// <param name="color"></param>
        /// <param name="intensity"></param>
        /// <param name="radius"></param>
        /// <param name="falloff"></param>
        private void SetPointLight(SceneObjectPart part, bool light, LSL_Vector color, float intensity, float radius, float falloff)
        {
            if (part == null)
                return;

            if (light)
            {
                part.Shape.LightEntry = true;
                part.Shape.LightColorR = Util.Clip((float)color.X, 0.0f, 1.0f);
                part.Shape.LightColorG = Util.Clip((float)color.Y, 0.0f, 1.0f);
                part.Shape.LightColorB = Util.Clip((float)color.Z, 0.0f, 1.0f);
                part.Shape.LightIntensity = intensity;
                part.Shape.LightRadius = radius;
                part.Shape.LightFalloff = falloff;
            }
            else
            {
                part.Shape.LightEntry = false;
            }

            part.ParentGroup.HasGroupChanged = true;
            part.ScheduleFullUpdate(PrimUpdateFlags.FindBest);
        }

        public LSL_Vector llGetColor(int face)
        {
            
            return GetColor(m_host, face);
        }

        private LSL_Vector GetColor(SceneObjectPart part, int face)
        {
            Primitive.TextureEntry tex = part.Shape.Textures;
            Color4 texcolor;
            LSL_Vector rgb = new LSL_Vector();
            if (face == ScriptBaseClass.ALL_SIDES)
            {
                int i;

                for (i = 0; i < part.GetNumberOfSides(); i++)
                {
                    texcolor = tex.GetFace((uint)i).RGBA;
                    rgb.X += texcolor.R;
                    rgb.Y += texcolor.G;
                    rgb.Z += texcolor.B;
                }

                rgb.X /= (float)part.GetNumberOfSides();
                rgb.Y /= (float)part.GetNumberOfSides();
                rgb.Z /= (float)part.GetNumberOfSides();

                return rgb;
            }
            if (face >= 0 && face < part.GetNumberOfSides())
            {
                texcolor = tex.GetFace((uint)face).RGBA;
                rgb.X = texcolor.R;
                rgb.Y = texcolor.G;
                rgb.Z = texcolor.B;
                return rgb;
            }
            else
            {
                return new LSL_Vector();
            }
        }

        public void llSetTexture(string texture, int face)
        {
            SetTexture(m_host, texture, face);
            ScriptSleep(200);
        }

        public void llSetLinkTexture(int linknumber, string texture, int face)
        {
            var parts = GetLinkPrimsOnly(linknumber);

            foreach (SceneObjectPart part in parts)
                SetTexture(part, texture, face);

            ScriptSleep(200);
        }

        private void SetTexture(SceneObjectPart part, string texture, int face)
        {
            UUID textureID = KeyOrName(texture);

            if (textureID == UUID.Zero)
                return;

            Primitive.TextureEntry tex = part.Shape.Textures;

            if (face >= 0 && face < part.GetNumberOfSides())
            {
                Primitive.TextureEntryFace texface = tex.CreateFace((uint)face);
                if (texface.TextureID != textureID)
                {
                    texface.TextureID = textureID;
                    tex.FaceTextures[face] = texface;
                    part.UpdateTexture(tex);
                }
                return;
            }
            else if (face == ScriptBaseClass.ALL_SIDES)
            {
                bool textureChanged = false;
                for (uint i = 0; i < part.GetNumberOfSides(); i++)
                {
                    if (tex.FaceTextures[i] != null)
                    {
                        if (tex.FaceTextures[i].TextureID != textureID)
                        {
                            tex.FaceTextures[i].TextureID = textureID;
                            textureChanged = true;
                        }
                    }
                }
                if (tex.DefaultTexture.TextureID != textureID) {
                    tex.DefaultTexture.TextureID = textureID;
                    textureChanged = true;
                }
                if (textureChanged)
                    part.UpdateTexture(tex);
                return;
            }
        }

        public void llScaleTexture(float u, float v, int face)
        {
            ScaleTexture(m_host, u, v, face);
            ScriptSleep(200);
        }

        private void ScaleTexture(SceneObjectPart part, float u, float v, int face)
        {
            Primitive.TextureEntry tex = part.Shape.Textures;
            if (face >= 0 && face < part.GetNumberOfSides())
            {
                Primitive.TextureEntryFace texface = tex.CreateFace((uint)face);
                texface.RepeatU = (float)u;
                texface.RepeatV = (float)v;
                tex.FaceTextures[face] = texface;
                part.UpdateTexture(tex);
                return;
            }
            if (face == ScriptBaseClass.ALL_SIDES)
            {
                for (int i = 0; i < part.GetNumberOfSides(); i++)
                {
                    if (tex.FaceTextures[i] != null)
                    {
                        tex.FaceTextures[i].RepeatU = (float)u;
                        tex.FaceTextures[i].RepeatV = (float)v;
                    }
                }
                tex.DefaultTexture.RepeatU = (float)u;
                tex.DefaultTexture.RepeatV = (float)v;
                part.UpdateTexture(tex);
                return;
            }
        }

        public void llOffsetTexture(float u, float v, int face)
        {
            OffsetTexture(m_host, u, v, face);
            ScriptSleep(200);
        }

        private void OffsetTexture(SceneObjectPart part, double u, double v, int face)
        {
            Primitive.TextureEntry tex = part.Shape.Textures;
            if (face >= 0 && face < part.GetNumberOfSides())
            {
                Primitive.TextureEntryFace texface = tex.CreateFace((uint)face);
                texface.OffsetU = (float)u;
                texface.OffsetV = (float)v;
                tex.FaceTextures[face] = texface;
                part.UpdateTexture(tex);
                return;
            }
            if (face == ScriptBaseClass.ALL_SIDES)
            {
                for (int i = 0; i < part.GetNumberOfSides(); i++)
                {
                    if (tex.FaceTextures[i] != null)
                    {
                        tex.FaceTextures[i].OffsetU = (float)u;
                        tex.FaceTextures[i].OffsetV = (float)v;
                    }
                }
                tex.DefaultTexture.OffsetU = (float)u;
                tex.DefaultTexture.OffsetV = (float)v;
                part.UpdateTexture(tex);
                return;
            }
        }

        public void llRotateTexture(float rotation, int face)
        {
            RotateTexture(m_host, rotation, face);
            ScriptSleep(200);
        }

        private void RotateTexture(SceneObjectPart part, double rotation, int face)
        {
            Primitive.TextureEntry tex = part.Shape.Textures;
            if (face >= 0 && face < part.GetNumberOfSides())
            {
                Primitive.TextureEntryFace texface = tex.CreateFace((uint)face);
                texface.Rotation = (float)rotation;
                tex.FaceTextures[face] = texface;
                part.UpdateTexture(tex);
                return;
            }
            if (face == ScriptBaseClass.ALL_SIDES)
            {
                for (int i = 0; i < part.GetNumberOfSides(); i++)
                {
                    if (tex.FaceTextures[i] != null)
                    {
                        tex.FaceTextures[i].Rotation = (float)rotation;
                    }
                }
                tex.DefaultTexture.Rotation = (float)rotation;
                part.UpdateTexture(tex);
                return;
            }
        }

        // This function does no permissions validation. Caller is responsible.
        private UUID GetTextureInfo(SceneObjectPart part, int face)
        {
            UUID assetID = UUID.Zero;

            Primitive.TextureEntry tex = part.Shape.Textures;
            if (face == ScriptBaseClass.ALL_SIDES)
            {
                face = 0;
            }
            if (face >= 0 && face < part.GetNumberOfSides())
            {
                Primitive.TextureEntryFace texface;
                texface = tex.GetFace((uint)face);
                assetID = texface.TextureID;
            }

            return assetID;
        }

        private string ConditionalTextureNameOrUUID(SceneObjectPart part, UUID assetID)
        {
            if (assetID == UUID.Zero)
                return UUID.Zero.ToString();

            // We need to provide the name if present in the prim.
            string result = InventoryName(assetID);
            if (!String.IsNullOrEmpty(result))
                return result;

            // Not present in the prim, if full-perm object, return the UUID.
            if (IsFullPerm(part.OwnerMask))
                return assetID.ToString();

            // Not a full-perm object, and the texture was not found
            // in the prim, so hide the texture ID to the caller.
            return UUID.Zero.ToString();
        }

        public string llGetTexture(int face)
        {
            UUID assetID = GetTextureInfo(m_host, face);
            return ConditionalTextureNameOrUUID(m_host, assetID);
        }

        public void llSetPos(LSL_Vector pos)
        {
            SetPos(m_host, pos, true);

            ScriptSleep(200);
        }

        // Capped movemment if distance > 10m (http://wiki.secondlife.com/wiki/LlSetPos)
        // note linked setpos is capped "differently"
        // Newer rules cap only unattached root prims at 10m.  Attachments and child prims can move up to 54m.
        private LSL_Vector SetPosAdjust(LSL_Vector anchor, LSL_Vector target, float limit)
        {
            float dist = Vector3.Mag(target - anchor);
            if (dist <= limit)
                return target;  // just return it unchanged

            // Clamp new position at limit
            return (target - anchor) * limit / dist + anchor;
        }

        private void SetPos(SceneObjectPart part, LSL_Vector target, bool isLimited)
        {
            SceneObjectGroup group = part.ParentGroup;
            LSL_Vector anchor = Vector3.Zero;
            float limit;

            // Capped movement limits
            // Attached root prims: 3.5m
            // Unattached root prims: 10m
            // Child prims: 54m

            // See http://inworldz.com/mantis/view.php?id=1929
            if (part.IsRootPart())
            {
                if (part.IsAttachment)
                {
                    limit = 3.5f;   // attached root prim
                }
                else
                {
                    limit = 10f;    // unattached root prim
                    anchor = part.AbsolutePosition; // relative to current position
                }
            }
            else
            {
                if (part.IsAttachment)
                {
                    limit = 54.0f;  // child prim of attachment
                }
                else
                {
                    // For now, let's make this mostly unlimited, 
                    // except for invalid / out of range requests.
                    limit = 256.0f;  // child prim of rezzed object
                }
            }

            LSL_Vector real_vec = target;
            if (isLimited)
                real_vec = SetPosAdjust(anchor, target, limit);

            if (group == null)
            {
                part.UpdateOffSet(new Vector3((float)real_vec.X, (float)real_vec.Y, (float)real_vec.Z));
                return;
            }

            if (group.InTransit)
                return;

            if (group.RootPart == part)
            {
                SceneObjectGroup parent = group;
                parent.UpdateGroupPosition(new Vector3((float)real_vec.X, (float)real_vec.Y, (float)real_vec.Z), false);
            }
            else
            {
                part.OffsetPosition = new Vector3((float)real_vec.X, (float)real_vec.Y, (float)real_vec.Z);
                SceneObjectGroup parent = group;
                parent.HasGroupChanged = true;
                part.ScheduleTerseUpdate();
                //parent.ScheduleGroupForTerseUpdate();
            }
        }

        public LSL_Vector llGetPos()
        {
            Vector3 pos = m_host.GetSLCompatiblePosition();
            return new LSL_Vector(pos.X, pos.Y, pos.Z);
        }

        public LSL_Vector llGetLocalPos()
        {
            return GetPartLocalPos(m_host);
        }

        private LSL_Vector GetPartPos(SceneObjectPart part)
        {
            LSL_Vector RootPos_v = new LSL_Vector(part.ParentGroup.AbsolutePosition);
            LSL_Vector Offset_v = new LSL_Vector(part.OffsetPosition);
            LSL_Rotation RootRot_r = new LSL_Rotation(part.ParentGroup.GroupRotation);
            return (RootPos_v + (Offset_v * RootRot_r));
        }

        private LSL_Vector GetPartLocalPos(SceneObjectPart part)
        {
            if (part.ParentID == 0)
                return new LSL_Vector(part.AbsolutePosition);

            if (part.LinkNum == 0 || part.LinkNum == 1)
                return new LSL_Vector(part.AttachedPos);

            return new LSL_Vector(part.OffsetPosition);
        }

        // llSetRot without the sleep
        public void SetRot(LSL_Rotation rot)
        {
            // try to let this work as in SL...
            if (m_host.IsRootPart())
            {
                // special case: If we are root, rotate complete SOG to new rotation
                SetRot(m_host, Rot2Quaternion(rot));
            }
            else
            {
                // we are a child. The rotation values will be set to the one of root modified by rot, as in SL. Don't ask.
                SceneObjectGroup group = m_host.ParentGroup;
                if (group != null) // a bit paranoid, maybe
                {
                    SceneObjectPart rootPart = group.RootPart;
                    if (rootPart != null) // again, better safe than sorry
                    {
                        SetRot(m_host, rootPart.RotationOffset * Rot2Quaternion(rot));
                    }
                }
            }
        }

        public void llSetRot(LSL_Rotation rot)
        {
            SetRot(rot);
            ScriptSleep(200);
        }

        public void llSetLocalRot(LSL_Rotation rot)
        {
            
            SetRot(m_host, Rot2Quaternion(rot));
            ScriptSleep(200);
        }

        private void SetRot(SceneObjectPart part, Quaternion rot)
        {
            if (part.ParentGroup.InTransit)
                return;

            part.UpdateRotation(rot);
        }

        /// <summary>
        /// See http://lslwiki.net/lslwiki/wakka.php?wakka=ChildRotation
        /// </summary>
        public LSL_Rotation llGetRot()
        {
            // unlinked or root prim then use llRootRotation
            // see llRootRotaion for references.
            if (m_host.IsRootPart())
            {
                return llGetRootRotation();
            }
            
            Quaternion q = m_host.GetWorldRotation();
            return new LSL_Rotation(q.X, q.Y, q.Z, q.W);
        }

        private LSL_Rotation GetPartRot(SceneObjectPart part)
        {
            Quaternion q;
            if (part.LinkNum == 0 || part.LinkNum == 1) // unlinked or root prim
            {
                if (part.ParentGroup.RootPart.AttachmentPoint != 0)
                {
                    ScenePresence avatar = World.GetScenePresence(part.AttachedAvatar);
                    if (avatar != null)
                    {
                        if ((avatar.AgentControlFlags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_MOUSELOOK) != 0)
                            q = avatar.CameraRotation; // Mouselook
                        else
                            q = avatar.Rotation; // Currently infrequently updated so may be inaccurate
                    }
                    else
                        q = part.ParentGroup.GroupRotation; // Likely never get here but just in case
                }
                else
                    q = part.ParentGroup.GroupRotation; // just the group rotation
                return new LSL_Rotation(q.X, q.Y, q.Z, q.W);
            }
            q = part.GetWorldRotation();
            return new LSL_Rotation(q.X, q.Y, q.Z, q.W);
        }

        private LSL_Rotation GetPartLocalRot(SceneObjectPart part)
        {
            return new LSL_Rotation(part.RotationOffset.X, part.RotationOffset.Y, part.RotationOffset.Z, part.RotationOffset.W);
        }

        public LSL_Rotation llGetLocalRot()
        {
            return GetPartLocalRot(m_host);
        }

        public void llSetForce(LSL_Vector force, int local)
        {
            m_host.SetForce(force, local != 0);

            PhySleep();
        }

        public LSL_Vector llGetForce()
        {
            if (m_host.ParentGroup != null)
            {
                if (!m_host.ParentGroup.IsDeleted)
                {
                    return m_host.ParentGroup.RootPart.GetForce();
                }
            }

            return OpenMetaverse.Vector3.Zero;
        }

        public int llTarget(LSL_Vector position, float range)
        {
            return m_host.registerTargetWaypoint(new Vector3((float)position.X, (float)position.Y, (float)position.Z), (float)range);
        }

        public void llTargetRemove(int number)
        {
            m_host.unregisterTargetWaypoint(number);
        }

        public int llRotTarget(LSL_Rotation rot, float error)
        {
            return m_host.RegisterRotationTarget(rot, error);
        }

        public void llRotTargetRemove(int number)
        {
            m_host.UnregisterRotationTarget(number);
        }

        public void llMoveToTarget(LSL_Vector target, float tau)
        {
            // Check for edge of world. If the target goes into a void
            // limit the target to stay in this region.
            if (!Util.IsValidRegionXYZ(target) && !World.HasNeighborAtPosition(target.X, target.Y))
            {
                Util.ForceValidRegionXYZ(ref target);
            }
            
            m_host.MoveToTarget(new Vector3(target.X, target.Y, target.Z), tau);
        }

        public void llStopMoveToTarget()
        {
            
            m_host.StopMoveToTarget();
        }

        public void llApplyImpulse(LSL_Vector force, int local)
        {
            if (force.Length() > 20000.0f)
            {
                force.Normalize();
                force = force * 20000.0f;
            }
            m_host.ApplyImpulse(force, local != 0);

            PhySleep();
        }

        public void llApplyRotationalImpulse(LSL_Vector force, int local)
        {
            m_host.ApplyAngularImpulse(force, local != 0);
            PhySleep();
        }

        public void llSetTorque(LSL_Vector torque, int local)
        {
            m_host.SetAngularImpulse(torque, local != 0);
            PhySleep();
        }

        public LSL_Vector llGetTorque()
        {
            return m_host.GetTorque();
        }

        public void llSetForceAndTorque(LSL_Vector force, LSL_Vector torque, int local)
        {
            llSetForce(force, local);
            llSetTorque(torque, local);
        }

        public LSL_Vector llGetVel()
        {
            return m_host.ParentGroup.RootPart.Velocity;
        }

        public LSL_Vector iwGetAngularVelocity()
        {
            return m_host.ParentGroup.RootPart.PhysicalAngularVelocity;
        }

        public LSL_Vector llGetAccel()
        {
            return m_host.ParentGroup.RootPart.Acceleration;
        }

        public LSL_Vector llGetOmega()
        {
            return m_host.AngularVelocity;
        }

        public float llGetTimeOfDay()
        {
            return (float)((DateTime.Now.TimeOfDay.TotalMilliseconds / 1000) % (3600 * 4));
        }

        public float llGetWallclock()
        {
            return (float)DateTime.Now.TimeOfDay.TotalSeconds;
        }

        public int llGetUnixTime()
        {
            return Util.UnixTimeSinceEpoch();
        }

        public int iwGetLocalTime()
        {
            return Util.LocalUnixTimeSinceEpoch();
        }

        public int iwGetLocalTimeOffset()
        {
            return Util.LocalTimeOffset();
        }

        public string iwFormatTime(int unixtime, int isUTC, string format)
        {
            DateTime date = Util.UnixToUTCDateTime(unixtime);
            if (isUTC == 0) {
                // local time zone
                date.AddSeconds(Util.LocalTimeOffset());
                date = DateTime.SpecifyKind(date, DateTimeKind.Local);
            }

            if (String.IsNullOrEmpty(format))
                format = "yyyy'-'MM'-'dd' 'HH':'mm':'ss";
            return date.ToString(format);
        }

        public float llGetTime()
        {
            return _thisScript.ScriptState.TotalRuntime;
        }

        public void llResetTime()
        {
            _thisScript.ScriptState.ResetRuntime();
        }

        public float llGetAndResetTime()
        {
            float runtime = _thisScript.ScriptState.TotalRuntime;
            llResetTime();
            return runtime;
        }

        public void llPreloadSound(string sound)
        {
            m_host.PreloadSound(KeyOrName(sound).ToString());
            ScriptSleep(1000);
        }

        public void llPlaySound(string sound, float volume)
        {
            m_host.UpdateSound(KeyOrName(sound), volume, SoundFlags.None);
        }

        public void llLoopSound(string sound, float volume)
        {
            m_host.UpdateSound(KeyOrName(sound), volume, SoundFlags.Loop);
        }

        public void llStopSound()
        {
            m_host.UpdateSound(UUID.Zero, 0.0f, SoundFlags.Stop);
        }

        public void llTriggerSound(string sound, float volume)
        {
            m_host.SendSound(KeyOrName(sound).ToString(), volume, true, (byte)SoundFlags.None);
        }

        public void llLoopSoundMaster(string sound, float volume)
        {
            NotImplemented("llLoopSoundMaster");
        }

        public void llLoopSoundSlave(string sound, float volume)
        {
            NotImplemented("llLoopSoundSlave");
        }

        public void llPlaySoundSlave(string sound, float volume)
        {
            NotImplemented("llPlaySoundSlave");
        }

        public void llSound(string sound, float volume, int queue, int loop)
        {
            // This function has been deprecated
            // see http://www.lslwiki.net/lslwiki/wakka.php?wakka=llSound
            Deprecated("llSound");
        }

        /// <summary>
        /// Return a portion of the designated string bounded by
        /// inclusive indices (start and end). As usual, the negative
        /// indices, and the tolerance for out-of-bound values, makes
        /// this more complicated than it might otherwise seem.
        /// </summary>
        public string llGetSubString(string src, int start, int end)
        {
            // Normalize indices (if negative).
            // After normlaization they may still be
            // negative, but that is now relative to
            // the start, rather than the end, of the
            // sequence.

            if (start < 0)
            {
                start = src.Length + start;
            }
            if (end < 0)
            {
                end = src.Length + end;
            }

            // Conventional substring
            if (start <= end)
            {
                // Implies both bounds are out-of-range.
                if (end < 0 || start >= src.Length)
                {
                    return String.Empty;
                }
                // If end is positive, then it directly
                // corresponds to the lengt of the substring
                // needed (plus one of course). BUT, it
                // must be within bounds.
                if (end >= src.Length)
                {
                    end = src.Length - 1;
                }

                if (start < 0)
                {
                    return src.Substring(0, end + 1);
                }
                // Both indices are positive
                return src.Substring(start, (end + 1) - start);
            }

            // Inverted substring (end < start)
            else
            {
                // Implies both indices are below the
                // lower bound. In the inverted case, that
                // means the entire string will be returned
                // unchanged.
                if (start < 0)
                {
                    return src;
                }
                // If both indices are greater than the upper
                // bound the result may seem initially counter
                // intuitive.
                if (end >= src.Length)
                {
                    return src;
                }

                if (end < 0)
                {
                    if (start < src.Length)
                    {
                        return src.Substring(start);
                    }
                    else
                    {
                        return String.Empty;
                    }
                }
                else
                {
                    if (start < src.Length)
                    {
                        return src.Substring(0, end + 1) + src.Substring(start);
                    }
                    else
                    {
                        return src.Substring(0, end + 1);
                    }
                }
            }
        }

        /// <summary>
        /// Delete substring removes the specified substring bounded
        /// by the inclusive indices start and end. Indices may be
        /// negative (indicating end-relative) and may be inverted,
        /// i.e. end < start.
        /// </summary>

        public string llDeleteSubString(string src, int start, int end)
        {
            // Normalize indices (if negative).
            // After normlaization they may still be
            // negative, but that is now relative to
            // the start, rather than the end, of the
            // sequence.
            if (start < 0)
            {
                start = src.Length + start;
            }
            if (end < 0)
            {
                end = src.Length + end;
            }
            // Conventionally delimited substring
            if (start <= end)
            {
                // If both bounds are outside of the existing
                // string, then return unchanges.
                if (end < 0 || start >= src.Length)
                {
                    return src;
                }
                // At least one bound is in-range, so we
                // need to clip the out-of-bound argument.
                if (start < 0)
                {
                    start = 0;
                }

                if (end >= src.Length)
                {
                    end = src.Length - 1;
                }

                return src.Remove(start, end - start + 1);
            }
            // Inverted substring
            else
            {
                // In this case, out of bounds means that
                // the existing string is part of the cut.
                if (start < 0 || end >= src.Length)
                {
                    return String.Empty;
                }

                if (end > 0)
                {
                    if (start < src.Length)
                    {
                        return src.Remove(start).Remove(0, end + 1);
                    }
                    else
                    {
                        return src.Remove(0, end + 1);
                    }
                }
                else
                {
                    if (start < src.Length)
                    {
                        return src.Remove(start);
                    }
                    else
                    {
                        return src;
                    }
                }
            }
        }

        /// <summary>
        /// Insert string inserts the specified string identified by src
        /// at the index indicated by index. Index may be negative, in
        /// which case it is end-relative. The index may exceed either
        /// string bound, with the result being a concatenation.
        /// </summary>

        public string llInsertString(string dest, int index, string src)
        {
            // Normalize indices (if negative).
            // After normlaization they may still be
            // negative, but that is now relative to
            // the start, rather than the end, of the
            // sequence.
            if (index < 0)
            {
                index = dest.Length + index;

                // Negative now means it is less than the lower
                // bound of the string.

                if (index < 0)
                {
                    return src + dest;
                }

            }

            if (index >= dest.Length)
            {
                return dest + src;
            }

            // The index is in bounds.
            // In this case the index refers to the index that will
            // be assigned to the first character of the inserted string.
            // So unlike the other string operations, we do not add one
            // to get the correct string length.
            return dest.Substring(0, index) + src + dest.Substring(index);
        }

        public string llToUpper(string src)
        {
            return src.ToUpper();
        }

        public string llToLower(string src)
        {
            return src.ToLower();
        }

        public int llGiveMoney(string destination, int amount)
        {
            UUID invItemID = InventorySelf();
            if (invItemID == UUID.Zero)
            {
                LSLError("No item found from which to give money");
                return 0;
            }

            TaskInventoryItem item;
            lock (m_host.TaskInventory)
            {
                item = m_host.TaskInventory[invItemID];
            }

            if (!CheckRuntimePerms(item, item.OwnerID, ScriptBaseClass.PERMISSION_DEBIT))
            {
                LSLError("No permissions to give money");
                return 0;
            }

            UUID toID = new UUID();
            if (!UUID.TryParse(destination, out toID))
            {
                LSLError("Bad key in llGiveMoney");
                return 0;
            }

            IMoneyModule money = World.RequestModuleInterface<IMoneyModule>();
            if (money == null)
            {
                NotImplemented("llGiveMoney");
                return 0;
            }

            string reason;
            string transactionID = money.ObjectGiveMoney(m_host.ParentGroup.RootPart.UUID, m_host.ParentGroup.RootPart.OwnerID, toID, amount, out reason);
            return String.IsNullOrEmpty(transactionID) ? 0 : 1;
        }

        // If sendEvent is true, returns the transaction_result event key, event has error status.
        // If sendEvent is false, no event, no key, it returns the actual transaction ID, or error tag on error.
        private string GiveMoney(string destination, int amount, bool sendEvent)
        {
            UUID eventID = UUID.Random();           // transaction_result event key
            string transactionID = String.Empty;    // actual currency transaction ID
            UUID invItemID = InventorySelf();
            int success = 0;
            string data = String.Empty;
            try
            {
                if (invItemID == UUID.Zero)
                {
                    LSLError("No item found from which to give money");
                    data = "SERVICE_ERROR";
                    return sendEvent ? eventID.ToString() : data;
                }

                TaskInventoryItem item;
                lock (m_host.TaskInventory)
                {
                    item = m_host.TaskInventory[invItemID];
                }

                if (!CheckRuntimePerms(item, item.OwnerID, ScriptBaseClass.PERMISSION_DEBIT))
                {
                    LSLError("No permissions to give money");
                    data = "MISSING_PERMISSION_DEBIT";
                    return sendEvent ? eventID.ToString() : data;
                }

                UUID toID = new UUID();
                if (!UUID.TryParse(destination, out toID))
                {
                    LSLError("Bad key in llGiveMoney");
                    data = "INVALID_DESTINATION";
                    return sendEvent ? eventID.ToString() : data;
                }

                IMoneyModule money = World.RequestModuleInterface<IMoneyModule>();
                if (money == null)
                {
                    NotImplemented("llGiveMoney");
                    data = "SERVICE_ERROR";
                    return sendEvent ? eventID.ToString() : data;
                }

                string reason;
                transactionID = money.ObjectGiveMoney(m_host.ParentGroup.RootPart.UUID, m_host.ParentGroup.RootPart.OwnerID, toID, amount, out reason);
                success = String.IsNullOrEmpty(transactionID) ? 0 : 1;
                if (success != 0)
                    data = String.Format("{0},{1}", transactionID, amount);
                else
                    data = reason;
                return sendEvent ? eventID.ToString() : transactionID;
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[SCRIPT]: llTransferLindenDollars exception: {0}", e.ToString());
                data = "SERVICE_ERROR";
                return sendEvent ? eventID.ToString() : data;
            }
            finally
            {
                if (sendEvent)
                {
                    object[] resobj = new object[] { eventID.ToString(), success, data };
                    m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams("transaction_result", resobj, new DetectParams[0]));
                }
            }
        }

        // Returns the key for the transaction_result event and triggers the event (either way).
        public string llTransferLindenDollars(string destination, int amount)
        {
            return GiveMoney(destination, amount, true);
        }

        // There is no event, so no event key, instead it returns the actual transaction ID, or error tag on error.
        public string iwGiveMoney(string destination, int amount)
        {
            return GiveMoney(destination, amount, false);
        }

        public void llMakeExplosion(int particles, float scale, float vel, float lifetime, float arc, string texture, LSL_Vector offset)
        {
            Deprecated("llMakeExplosion");
            // ScriptSleep(100);
        }

        public void llMakeFountain(int particles, float scale, float vel, float lifetime, float arc, int bounce, string texture, LSL_Vector offset, float bounce_offset)
        {
            Deprecated("llMakeFountain");
            // ScriptSleep(100);
        }

        public void llMakeSmoke(int particles, float scale, float vel, float lifetime, float arc, string texture, LSL_Vector offset)
        {
            Deprecated("llMakeSmoke");
            // ScriptSleep(100);
        }

        public void llMakeFire(int particles, float scale, float vel, float lifetime, float arc, string texture, LSL_Vector offset)
        {
            Deprecated("llMakeFire");
            // ScriptSleep(100);
        }

        public int iwClampInt(int value, int min, int max)
        {
            //Min and max are equal, nothing to do
            if (min == max) return min;

            //max is less than min, switch the math inputs
            if (max < min) return Math.Min(min, Math.Max(value, max));

            //inputs are good, clamp the values
            return Math.Min(max, Math.Max(value, min));
        }

        public float iwClampFloat(float value, float min, float max)
        {
            //Min and max are equal, nothing to do
            if (min == max) return min;

            //max is less than min, switch the math inputs
            if (max < min) return Math.Min(min, Math.Max(value, max));

            //inputs are good, clamp the values
            return Math.Min(max, Math.Max(value, min));
        }

        public int iwCheckRezError(Vector3 pos, int isTemp, int landImpact)
        {
            return m_host.ParentGroup.Scene.CheckRezError(m_host.OwnerID, m_host.UUID, pos, isTemp != 0, landImpact);
        }

        public string iwRezAt(string inventory, bool rezAtRoot, LSL_Vector pos, LSL_Vector vel, LSL_Rotation rot, int param,
            out int sleepTime)
        {
            string result = UUID.Zero.ToString();

            // Anti-griefer (nuke console command) check
            if (m_host.ParentGroup.Scene.IsBadUser(m_host.OwnerID))
            {
                // fail silently
                sleepTime = 100;
                return UUID.Zero.ToString();
            }

            if (Double.IsNaN(rot.X) || Double.IsNaN(rot.Y) || Double.IsNaN(rot.Z) || Double.IsNaN(rot.W))
            {
                ScriptShoutError("Unable to create requested object. Position is invalid.");
                sleepTime = 0;
                return result;
            }

            float dist = (float)llVecDist(llGetPos(), pos);
            if (dist > 10.0f)
            {
                ScriptShoutError("Unable to create requested object. Position exceeds 10m distance limit.");
                sleepTime = 0;
                return result;
            }

            TaskInventoryDictionary partInventory = (TaskInventoryDictionary)m_host.TaskInventory.Clone();

            foreach (KeyValuePair<UUID, TaskInventoryItem> inv in partInventory)
            {
                if (inv.Value.Name == inventory)
                {
                    // make sure we're an object.
                    if (inv.Value.InvType != (int)InventoryType.Object)
                    {
                        ScriptShoutError("Unable to create requested object. Inventory item '"+inv.Value.Name+"' is something other than an object.");
                        sleepTime = 0;
                        return result;
                    }

                    Vector3 llpos = new Vector3((float)pos.X, (float)pos.Y, (float)pos.Z);

                    // test if we're further away then 10m
                    if (Util.GetDistanceTo(llpos, m_host.AbsolutePosition) > 10)
                    {
                        ScriptShoutError("Unable to create requested object. Relative position exceeds 10m distance limit.");
                        sleepTime = 0;
                        return result;
                    }

                    string reason = "success";
                    IEnumerable<SceneObjectGroup> new_group = World.RezObject(m_host, inv.Value, rezAtRoot, llpos, Rot2Quaternion(rot), vel, param, out reason);
                    // If either of these are null, then there was an unknown error.
                    if (new_group == null)
                    {
                        string errtext = String.Empty;
                        switch (reason)
                        {
                            case "permission":
                                errtext = "Insufficient permissions to rez objects in this parcel.";
                                break;
                            case "parcel":
                                errtext = "Insufficient prims available; parcel is full.";
                                break;
                            case "region":
                                errtext = "Insufficient prims available; region is full.";
                                break;
                            case "fence":
                                errtext = "Object rezzing too fast or nested too deep (grey goo fence).";
                                break;
                            case "item":
                                errtext = "Item not found in Contents.";
                                break;
                            case "land":
                                errtext = "Cannot determine land parcel.";
                                break;
                            case "rez":
                                errtext = "The object failed to rez";
                                break;
                        }
                        string spos = Convert.ToInt16(pos.X).ToString() + "," + Convert.ToInt16(pos.Y).ToString() + "," + Convert.ToInt16(pos.Z).ToString();
                        ScriptShoutError("Object '" + m_host.ParentGroup.Name + "' is unable to create object '" + inventory + "' at <" + spos + ">. " + errtext);
                        sleepTime = 0;
                        return result;
                    }

                    foreach (var grp in new_group)
                    {
                        result = grp.RootPart.UUID.ToString();
                        m_ScriptEngine.PostObjectEvent(m_host.LocalId, 
                            new EventParams("object_rez", new Object[] {result}, new DetectParams[0])
                        );
                    }

                    break;  // out of for loop to return
                }
            }

            // Variable script delay? (see (http://wiki.secondlife.com/wiki/LSL_Delay)
            //ScriptSleep((int)((groupmass * velmag) / 10));
            sleepTime = 100;
            return result;
        }

        public void iwRezAtRoot(string inventory, LSL_Vector pos, LSL_Vector vel, LSL_Rotation rot, int param)
        {
            string ret = String.Empty;
            int sleepTime = 0;

            try
            {
                ret = iwRezAt(inventory, true, pos, vel, rot, param, out sleepTime);
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, ret, sleepTime);
            }
        }

        public void llRezAtRoot(string inventory, LSL_Vector pos, LSL_Vector vel, LSL_Rotation rot, int param)
        {
            int sleepTime = 0;

            try
            {
                iwRezAt(inventory, true, pos, vel, rot, param, out sleepTime);
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, sleepTime);
            }
        }

        public void iwRezObject(string inventory, LSL_Vector pos, LSL_Vector vel, LSL_Rotation rot, int param)
        {
            string ret = String.Empty;
            int sleepTime = 0;

            try
            {
                ret = iwRezAt(inventory, false, pos, vel, rot, param, out sleepTime);
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, ret, sleepTime);
            }
        }

        public void llRezObject(string inventory, LSL_Vector pos, LSL_Vector vel, LSL_Rotation rot, int param)
        {
            int sleepTime = 0;

            try
            {
                iwRezAt(inventory, false, pos, vel, rot, param, out sleepTime);
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, sleepTime);
            }
        }

        private void ReportRezError(Vector3 pos, string errtext)
        {
            string spos = Convert.ToInt16(pos.X).ToString() + "," + Convert.ToInt16(pos.Y).ToString() + "," + Convert.ToInt16(pos.Z).ToString();
            ScriptShoutError("Unable to create new object at <" + spos + ">. " + errtext);
        }

        public string iwRezPrim(LSL_List primParams, LSL_List particleSystem, LSL_List inventory,  LSL_Vector lslpos, LSL_Vector lslvel, LSL_Rotation lslrot, int param)
        {
            Vector3 pos = new Vector3(lslpos);
            Quaternion rot = new Quaternion(lslrot);
            Vector3 vel = new Vector3(lslvel);
            SceneObjectPart part = null;
            SceneObjectGroup new_group = null;
            List<TaskInventoryItem> itemsToCopy = new List<TaskInventoryItem>();
            bool includesScripts = false;
            string reason = "success";

            // Anti-griefer (nuke console command) check
            if (m_host.ParentGroup.Scene.IsBadUser(m_host.OwnerID))
            {
                ScriptSleep(1000);
                return UUID.Zero.ToString();
            }

            int idx = 0;
            while (idx < inventory.Length)
            {
                string itemName = inventory.Data[idx++].ToString();
                if (!String.IsNullOrEmpty(itemName))
                {
                    bool found = false;
                    lock (m_host.TaskInventory)
                    {
                        foreach (KeyValuePair<UUID, TaskInventoryItem> inv in m_host.TaskInventory)
                        {
                            if (inv.Value.Name == itemName)
                            {
                                if ((inv.Value.CurrentPermissions & (uint)PermissionMask.Copy) == 0)
                                {
                                    ReportRezError(pos, "'" + itemName + "' is not copyable.");
                                    return UUID.Zero.ToString();
                                }

                                found = true;
                                if (inv.Value.Type == ScriptBaseClass.INVENTORY_SCRIPT)
                                    includesScripts = true;
                                itemsToCopy.Add(inv.Value);
                                break;
                            }
                        }
                    }
                    if (!found)
                    {
                        ReportRezError(pos, "'" + itemName + "' not found in Contents.");
                        return UUID.Zero.ToString();
                    }
                }
            }

            part = new SceneObjectPart(m_host.OwnerID, PrimitiveBaseShape.Default, pos, rot, Vector3.Zero, false);
            if (part == null)
            {
                ReportRezError(pos, "Cannot allocate new object.");
                return UUID.Zero.ToString();
            }

            new_group = World.RezPrim(m_host, part, param, out reason);
            if (new_group == null)
            {
                string errtext = reason;    // in case we miss one in the switch
                switch (reason)
                {
                    case "permission":
                        errtext = "Insufficient permissions to rez objects in this parcel.";
                        break;
                    case "parcel":
                        errtext = "Insufficient prims available; parcel is full.";
                        break;
                    case "region":
                        errtext = "Insufficient prims available; region is full.";
                        break;
                    case "fence":
                        errtext = "Object rezzing too fast or nested too deep (grey goo fence).";
                        break;
                    case "land":
                        errtext = "Cannot determine land parcel.";
                        break;
                }
                ReportRezError(pos, errtext);
                return UUID.Zero.ToString();
            }

            // successfully rezzed it

            // if a script was specified, insert the specified script in the new object
            foreach (TaskInventoryItem item in itemsToCopy)
            {
                m_host.ParentGroup.Scene.MoveTaskInventoryItem(part.UUID, m_host, item.ItemID);
            }

            // Now set the rest of the prim properties
            SetPrimParams(0, primParams, part);
            PrimParticleSystem(part, particleSystem);

            if (vel != Vector3.Zero)
                new_group.SetVelocity(vel, false);

            part.ScheduleFullUpdate(PrimUpdateFlags.FindBest);

            // if a script was specified, start it now
            if (includesScripts)
                part.ParentGroup.CreateScriptInstances(param, OpenSim.Region.Framework.ScriptStartFlags.PostOnRez, m_host.ParentGroup.Scene.DefaultScriptEngine, 0, null);

            // Notify the rezzer part that the new group was created
            m_ScriptEngine.PostObjectEvent(m_host.LocalId, new EventParams(
                    "object_rez",
                    new Object[] { new_group.RootPart.UUID },
                    new DetectParams[0]));

            return new_group.RootPart.UUID.ToString();
        }

        public void llLookAt(LSL_Vector target, float strength, float damping)
        {
            // Determine where we are looking from
            LSL_Rotation rotstart = LSL_Rotation.Normalize(llGetRot());
            LSL_Vector from = llGetPos();

            //translate the target vector to a new origin based on our current from vector
            LSL_Vector targetNewOrigin = target - from;
 
            //the forward vector
            LSL_Vector forward = LSL_Vector.UnitZ;

            LSL_Rotation newRot;

            //determine the amount of rotation needed to point our unrotated FWD axis to the target.
            //the actual amount will be the arccos of the dot product
            float angle = LSL_Vector.Dot(forward, LSL_Vector.Normalize(targetNewOrigin));
            if (angle > 0.999999)
            {
                LSL_Vector localFwd = forward * rotstart;

                //this object is exactly on our forward axis. we want to point straight up.
                //unfortunately we can't just rotate to identity. we want to maintain the +X
                //direction. so we need to use our left axis to rotate our fwd to global fwd
                float angleToFwd = LSL_Vector.Dot(localFwd, forward);
                if (angleToFwd > 0.999999)
                {
                    //already at the rotation we want
                    newRot = rotstart;
                }
                else if (angleToFwd < -0.999999)
                {
                    //180 degree flip
                    newRot = LSL_Rotation.CreateFromAxisAngle(LSL_Vector.UnitY * rotstart, (float)Math.PI) * rotstart;
                }
                else
                {
                    //calculate a flip
                    LSL_Vector rotAxis = LSL_Vector.Normalize(LSL_Vector.Cross(localFwd, forward));
                    angleToFwd = (float)Math.Acos(angleToFwd);
                    newRot = LSL_Rotation.CreateFromAxisAngle(rotAxis, angleToFwd) * rotstart;
                }
            }
            else if (angle < -0.999999)
            {
                //the object is exactly on our rear axis. we want to rotate to match
                LSL_Vector localFwd = forward * rotstart;

                //this object is exactly on our rear axis. we want to point straight down.
                //unfortunately we can't just rotate to identity * PI. we want to maintain the +X
                //direction. so we need to use our left axis to rotate our -fwd to global -fwd
                float angleToRear = LSL_Vector.Dot(localFwd, -forward);
                if (angleToRear > 0.999999)
                {
                    //already at the rotation we want
                    newRot = rotstart;
                }
                else if (angleToRear < -0.999999)
                {
                    //180 degree flip
                    newRot = LSL_Rotation.CreateFromAxisAngle(LSL_Vector.UnitY * rotstart, (float)Math.PI) * rotstart;
                }
                else
                {
                    //calculate a flip
                    LSL_Vector rotAxis = LSL_Vector.Normalize(LSL_Vector.Cross(localFwd, -forward));
                    angleToRear = (float)Math.Acos(angleToRear);
                    newRot = LSL_Rotation.CreateFromAxisAngle(rotAxis, angleToRear) * rotstart;
                }
            }
            else
            {
                //neither of the special cases, so we actually want to determine where to rotate
                angle = (float)Math.Acos(angle);

                //we are the origin, calculate a vector perpendicular to our forward
                //axis and the new target. this will form a new basis for rotation
                LSL_Vector rotAxis = LSL_Vector.Normalize(LSL_Vector.Cross(forward, targetNewOrigin));

                //apply the rotation angle against this axis
                newRot = LSL_Rotation.CreateFromAxisAngle(rotAxis, angle);
                newRot.Normalize();
                
                //cross our new forward direction with world Z to get a local level RIGHT/LEFT vector
                LSL_Vector newFwd = LSL_Vector.Normalize(forward * newRot);

                LSL_Vector left = LSL_Vector.UnitY;
                if (newFwd.Y < 0)
                {
                    left = -left;
                }

                LSL_Vector objectLevelLeft = LSL_Vector.Normalize(LSL_Vector.Cross(LSL_Vector.UnitZ, newFwd));
                //rotate us by the difference of our left vs level left
                float leftAdjustment = LSL_Vector.Dot(objectLevelLeft, left * newRot);
                leftAdjustment = (float)Math.Acos(leftAdjustment);
                if (newFwd.Y < 0)
                {
                    leftAdjustment = leftAdjustment + (float)Math.PI;
                }

                LSL_Rotation levelingRotation = LSL_Rotation.CreateFromAxisAngle(LSL_Vector.UnitZ, leftAdjustment);

                //perform a local rotation to level our left vector
                newRot = newRot * levelingRotation;
            }

            
            // Orient the object to the angle calculated. Use
            // llRotLookAt because it handles both nonphysical and physical rotations.
            llRotLookAt(newRot, strength, damping);
        }

        public void llStopLookAt()
        {
            m_host.ParentGroup.StopRotLookAt();
        }

        public void llSetTimerEvent(float sec)
        {
            if (sec < 0.0f)
                sec = 0.0f;

            if (sec != 0.0f && sec < m_MinTimerInterval)
                sec = m_MinTimerInterval;
            
            // Setting timer repeat
            m_ScriptEngine.SetTimerEvent(m_localID, m_itemID, sec);
        }

        public void llSleep(float sec)
        {
            int msSleep = (int)(sec * 1000);

            this.ScriptSleep(msSleep);
        }

        public void llCollisionFilter(string name, string id, int accept)
        {
            NotImplemented("llCollisionFilter");
        }

        private void TakeControlsInternal(int oldControls, int oldPassOn, int controls, int accept, int pass_on, bool fromCrossing, UUID requiredAvatar)
        {
            TaskInventoryItem item;

            lock (m_host.TaskInventory)
            {
                if (!m_host.TaskInventory.ContainsKey(InventorySelf()))
                    return;
                else
                    item = m_host.TaskInventory[InventorySelf()];
            }

            //m_log.WarnFormat("[LSL]: '{0}' TakeControlsInternal controls={1}, accept={2}, pass_on={3}, fromCrossing={4} requiredAvatar={5}",
            //    item.Name, controls, accept, pass_on, fromCrossing, requiredAvatar);

            SceneObjectGroup parentGroup = m_host.ParentGroup;
            if (parentGroup == null)
            {
                m_log.Warn("[LSL]: Could not take controls: Parent group not found");
                return;
            }

            if (item.PermsGranter != UUID.Zero)
            {
                //requiredAvatar is sent by the API if this is a control re-take from a crossing.
                //we only run this code for the current avatar that has just hit this object
                if (requiredAvatar != UUID.Zero && item.PermsGranter != requiredAvatar)
                {
                    //not looking for this avatar
                    return;
                }

                ScenePresence presence = World.GetScenePresence(item.PermsGranter);
                if (presence != null)
                {
                    if (CheckRuntimePerms(item, item.PermsGranter, ScriptBaseClass.PERMISSION_TAKE_CONTROLS))
                    {
                        // Check for a redundant late request (e.g. attachment on crossing).
                        if (!_thisScript.ScriptState.MiscAttributes.ContainsKey((int)VM.RuntimeState.MiscAttr.Control))
                        {
                            // We don't already have controls, take them.
                            _thisScript.ScriptState.MiscAttributes[(int)VM.RuntimeState.MiscAttr.Control] = new object[] { controls, accept, pass_on };
                        }
                        item.HasScriptedControls = true;
                        m_host.ParentGroup.RecalcAvatarControls();

                        // We need to deregister controls on a stand or viewer Release Keys.
                        presence.OnMustReleaseControls += handleMustReleaseControls;
                        presence.RegisterControlEventsToScript(oldControls, oldPassOn, controls, accept, pass_on, m_localID, m_itemID, fromCrossing);

                        m_ScriptEngine.ChangeScriptEnabledLandStatus(parentGroup, true);

                        if (pass_on != 0)
                            presence.ResendVehicleControls();   // ensure that true,false cases are sent after others (like true, true)
                    }
                }
                else
                {
                    m_log.Warn("[LSL]: Could not take controls: PermsGranter SP not found");
                }
            }
        }

        // returns the old controls, or -1 if none/unknown
        // if (accept == false), returns ~controls ready for sending to viewer.
        private void ReleaseControlsInternal(bool isUnloading, bool releasePerms, bool silent, bool checkPerms, out int oldControls, out int oldPassOn)
        {
            object[] controlParams;
            if (_thisScript.ScriptState.MiscAttributes.TryGetValue((int)VM.RuntimeState.MiscAttr.Control, out controlParams))
            {
                oldControls = (int)controlParams[0];
                oldPassOn = (int)controlParams[2];
                if ((int)controlParams[1] == 0) // if accept == false
                    oldControls = ~oldControls;
            }
            else
            {
                oldControls = -1;
                oldPassOn = 0;
            }

            TaskInventoryItem item;
            lock (m_host.TaskInventory)
            {
                if (!m_host.TaskInventory.ContainsKey(InventorySelf()))
                    return;

                item = m_host.TaskInventory[InventorySelf()];
            }
            // m_log.WarnFormat("[LSL]: '{0}' ReleaseControlsInternal isUnloading={1}, releasePerms={2}, silent={3}, checkPerms={4}",
            //     item.Name, isUnloading, releasePerms, silent, checkPerms);

            if (checkPerms & (item.PermsGranter == UUID.Zero))
                return;

            SceneObjectGroup parentGroup = m_host.ParentGroup;
            if (parentGroup == null)
            {
                m_log.Warn("[LSL]: Could not release controls: Parent group not found");
                return;
            }

            if (checkPerms && !CheckRuntimePerms(item, item.PermsGranter, ScriptBaseClass.PERMISSION_TAKE_CONTROLS))
                return; // no permission to release controls

            bool hadControls = item.HasScriptedControls;

            // All prechecks complete. After this point we must fully release controls.
            if (_thisScript.ScriptState.MiscAttributes.ContainsKey((int)VM.RuntimeState.MiscAttr.Control))
            {
                _thisScript.ScriptState.MiscAttributes.Remove((int)VM.RuntimeState.MiscAttr.Control);
            }
            item.HasScriptedControls = false;
            m_host.ParentGroup.RecalcAvatarControls();

            //the scripts in this group may only be running due to the fact that the group
            //had the avatar's controls. if this is the case we need to re-disable all the
            //scripts in the group
            if (!isUnloading && parentGroup.CurrentParcel != null && !parentGroup.HasAvatarControls)
            {
                if (!m_ScriptEngine.ScriptsCanRun(parentGroup.CurrentParcel, m_host))
                {
                    m_ScriptEngine.ChangeScriptEnabledLandStatus(parentGroup, false);
                }
            }

            if (hadControls)
            {
                ScenePresence presence = World.GetScenePresence(item.PermsGranter);
                if (presence != null)
                {
                    // Unregister controls from Presence
                    presence.OnMustReleaseControls -= handleMustReleaseControls;
                    presence.UnRegisterControlEventsToScript(m_localID, m_itemID, silent);
                }
            }

            if (releasePerms)
            {
                // Remove Take Control permission.
                item.PermsMask &= ~ScriptBaseClass.PERMISSION_TAKE_CONTROLS;
                if (item.PermsMask == 0)
                    item.PermsGranter = UUID.Zero;
                PermsChange(item, item.PermsGranter, item.PermsMask);
            }
        }

        private void ReleaseControlsInternal(bool isUnloading, bool releasePerms, bool silent, bool checkPerms)
        {
            int oldControls, oldPassOn; // don't care
            ReleaseControlsInternal(isUnloading, releasePerms, silent, checkPerms, out oldControls, out oldPassOn);
        }

        public void llTakeControls(int controls, int accept, int pass_on)
        {
            TaskInventoryItem item;
            lock (m_host.TaskInventory)
            {
                if (!m_host.TaskInventory.ContainsKey(InventorySelf()))
                {
                    m_log.ErrorFormat("[LSL]: '{0}' ***llTakeControls*** controls={1}, accept={2}, pass_on={3}", "*UNKNOWN*", controls, accept, pass_on);
                    return;
                }
                item = m_host.TaskInventory[InventorySelf()];
            }

            // m_log.ErrorFormat("[LSL]: '{0}' ***llTakeControls*** controls={1}, accept={2}, pass_on={3}", item.Name, controls, accept, pass_on);

            int oldControls, oldPassOn;
            ReleaseControlsInternal(false, false, true, true, out oldControls, out oldPassOn);  // implicitly silent
            TakeControlsInternal(oldControls, oldPassOn, controls, accept, pass_on, false, UUID.Zero);
        }

        public void llReleaseControls()
        {
            ReleaseControlsInternal(false, true, false, true);
        }

        public void llReleaseURL(string url)
        {
            
            if (m_UrlModule != null)
                m_UrlModule.ReleaseURL(url);
        }

        public void llAttachToAvatar(int attachment)
        {
            AttachInternal(attachment, AttachFlags.FromInWorld);
        }

        public void llAttachToAvatarTemp(int attachment)
        {
            AttachInternal(attachment, AttachFlags.FromInWorld | AttachFlags.Temp);
        }

        private void AttachInternal(int attachment, AttachFlags flags)
        {
            if (m_host.ParentGroup.IsAttachment)
                return;

            TaskInventoryItem item;

            lock (m_host.TaskInventory)
            {
                if (!m_host.TaskInventory.ContainsKey(InventorySelf()))
                    return;
                else
                    item = m_host.TaskInventory[InventorySelf()];
            }

            if (item.PermsGranter != m_host.OwnerID)
            {
                if ((flags & AttachFlags.Temp) == 0)
                {
                    //not allowed to change owner unless it's temp
                    return;
                }
            }

            if (CheckRuntimePerms(item, item.PermsGranter, ScriptBaseClass.PERMISSION_ATTACH))
            {
                ScenePresence presence = World.GetScenePresence(item.PermsGranter);
                SceneObjectGroup grp = m_host.ParentGroup;

                if (presence == null)
                    return; // don't crash the script

                if ((flags & AttachFlags.Temp) != 0 && item.PermsGranter != m_host.OwnerID)
                {
                    if (!ChangeSOGOwnershipForTempAttach(presence, grp))
                    {
                        ScriptShoutError("llAttachToAvatarTemp: No permission to transfer");
                        return;
                    }
                }

                // Behaviour on SL is to always append.
                // https://jira.secondlife.com/browse/SCR-277?
                bool append = true;
                m_ScriptEngine.World.AttachObject(
                    presence.ControllingClient, grp.LocalId, (uint)attachment, append, false, flags);
            }
        }

        private bool ChangeSOGOwnershipForTempAttach(ScenePresence newOwner, SceneObjectGroup sog)
        {
            return World.ChangeLiveSOGOwner(newOwner.ControllingClient, sog.RootPart, sog);
        }

        public void llDetachFromAvatar()
        {
            if (m_host.ParentGroup.RootPart.AttachmentPoint == 0)
                return;

            TaskInventoryItem item;

            lock (m_host.TaskInventory)
            {
                if (!m_host.TaskInventory.ContainsKey(InventorySelf()))
                    return;
                else
                    item = m_host.TaskInventory[InventorySelf()];
            }

            if (item.PermsGranter != m_host.OwnerID)
                return;

            if (CheckRuntimePerms(item, m_host.OwnerID, ScriptBaseClass.PERMISSION_ATTACH))
            {
                SceneObjectGroup grp = m_host.ParentGroup;
                UUID itemID = grp.GetFromItemID();

                ScenePresence presence = World.GetScenePresence(item.PermsGranter);
                if (presence == null)
                    return; // don't crash the script

                m_ScriptEngine.World.DetachSingleAttachmentToInv(itemID,
                        presence.ControllingClient);
            }
        }

        public void llTakeCamera(string avatar)
        {
            
            Deprecated("llTakeCamera");
        }

        public void llReleaseCamera(string avatar)
        {
            
            Deprecated("llReleaseCamera");
        }

        public string llGetOwner()
        {
            

            return m_host.ObjectOwner.ToString();
        }

        private void SendIM(UUID agentId, string message)
        {
            // We may be able to use ClientView.SendInstantMessage here, but we need a client instance.
            // InstantMessageModule.OnInstantMessage searches through a list of scenes for a client matching the toAgent,
            // but I don't think we have a list of scenes available from here.
            // (We also don't want to duplicate the code in OnInstantMessage if we can avoid it.)

            // user is a UUID

            // TODO: figure out values for client, fromSession, and imSessionID
            // client.SendInstantMessage(m_host.UUID, fromSession, message, user, imSessionID, m_host.Name, AgentManager.InstantMessageDialog.MessageFromAgent, (uint)Util.UnixTimeSinceEpoch());

            //m_pendingFriendRequests.Add(friendTransactionID, fromAgentID);

            GridInstantMessage msg = new GridInstantMessage();
			msg.fromAgentID = new Guid(m_host.OwnerID.ToString()); // fromAgentID.Guid;
            msg.toAgentID = agentId.Guid;
			msg.imSessionID = new Guid(m_host.UUID.ToString()); // This is the item we're mucking with here
            msg.timestamp = (uint)Util.UnixTimeSinceEpoch();// timestamp;
            msg.fromAgentName = m_host.Name;
            // Cap the message length at 1024.
            if (message != null && message.Length > 1024)
                msg.message = message.Substring(0, 1024);
            else
                msg.message = message;
            msg.dialog = (byte)InstantMessageDialog.MessageFromObject;
            msg.fromGroup = false;// fromGroup;
            msg.offline = (byte)1; //yes, store for fetching missed IMs on login
            msg.ParentEstateID = 0; //ParentEstateID;
            msg.Position = m_host.AbsolutePosition;
            msg.RegionID = World.RegionInfo.RegionID.Guid;//RegionID.Guid;
            // binaryBucket is the SL URL without the prefix, e.g. "Region/x/y/z"
            string url = Util.LocationShortCode(World.RegionInfo.RegionName, msg.Position);
            byte[] bucket = Utils.StringToBytes(url);
            msg.binaryBucket = new byte[bucket.Length];// binaryBucket;
            bucket.CopyTo(msg.binaryBucket, 0);

            if (m_TransferModule != null)
            {
                m_TransferModule.SendInstantMessage(msg, delegate(bool success) { });
            }
        }

        public void llInstantMessage(string user, string message)
        {
            const int delay = 2000;

            try
            {
                UUID agentId = UUID.Zero;
                if (UUID.TryParse(user, out agentId) && (agentId != UUID.Zero))
                    SendIM(agentId, message);
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }

        public void llEmail(string address, string subject, string message)
        {
            const int delay = 20000;

            try
            {
                IEmailModule emailModule = m_ScriptEngine.World.RequestModuleInterface<IEmailModule>();
                if (emailModule == null)
                    return;

                emailModule.SendEmail(m_host.UUID, address, subject, message);
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }

        public void llGetNextEmail(string address, string subject)
        {
            int delay = 0;

            try
            {
                IGetEmailModule emailModule = m_ScriptEngine.World.RequestModuleInterface<IGetEmailModule>();
                if (emailModule == null)
                    return;

                Email email = emailModule.GetNextEmail(m_host.UUID, address, subject);
                if (email == null)
                    return;

                m_ScriptEngine.PostObjectEvent(m_host.LocalId,
                        new EventParams("email",
                        new Object[] {
                        (string)(email.time),
                        (string)(email.sender),
                        (string)(email.subject),
                        (string)(email.message),
                        (int)(email.numLeft)},
                        new DetectParams[0]));
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }

        public string llGetKey()
        {
            
            return m_host.UUID.ToString();
        }

        public void llSetBuoyancy(float buoyancy)
        {
            if (m_host.ParentGroup != null)
            {
                if (!m_host.ParentGroup.IsDeleted)
                {
                    m_host.ParentGroup.RootPart.SetBuoyancy((float)buoyancy);
                }
            }
        }

        /// <summary>
        /// Attempt to clamp the object on the Z axis at the given height over tau seconds.
        /// </summary>
        /// <param name="height">Height to hover.  Height of zero disables hover.</param>
        /// <param name="water">False if height is calculated just from ground, otherwise uses ground or water depending on whichever is higher</param>
        /// <param name="tau">Number of seconds over which to reach target</param>
        public void llSetHoverHeight(float height, int water, float tau)
        {
            if (m_host.PhysActor != null)
            {
                PIDHoverFlag hoverType = PIDHoverFlag.Ground;
                if (water != 0)
                {
                    hoverType = PIDHoverFlag.Ground | PIDHoverFlag.Water;
                }

                m_host.SetHoverHeight(height, hoverType, tau);
            }
        }

        public void llStopHover()
        {
            
            if (m_host.PhysActor != null)
            {
                m_host.StopHover();
            }
        }

        public void llMinEventDelay(float delay)
        {
            
            try
            {
                m_ScriptEngine.SetMinEventDelay(m_itemID, delay);
            }
            catch (NotImplementedException)
            {
                // Currently not implemented in DotNetEngine only XEngine
                NotImplemented("llMinEventDelay");
            }
        }

        /// <summary>
        /// llSoundPreload is deprecated. In SL this appears to do absolutely nothing
        /// and is documented to have no delay.
        /// </summary>
        public void llSoundPreload(string sound)
        {
            
        }

        public void llRotLookAt(LSL_Rotation target, float strength, float damping)
        {
            // When physical, enable a rotlookat
            if ((m_host.GetEffectiveObjectFlags() & PrimFlags.Physics) == PrimFlags.Physics)
            {
                m_host.ParentGroup.BeginRotLookAt(target, strength, damping);
            }

            // RotLookAt acts as a SetRot call when the object is not physical and it
            // clears any active rotlookat
            else
            {
                m_host.ParentGroup.BeginRotLookAt(LSL_Rotation.CreateFromEulers(0, 0, 0), 0, 0);
                SetRot(m_host, Rot2Quaternion(target)); // this is like llSetLocalRot (vs llSetRot) on a child prim.
            }
        }

        public int llStringLength(string str)
        {
            return str.Length;
        }

        private void StartAnimation(SceneObjectPart part, string anim)
        {
            UUID invItemID = InventorySelf();
            if (invItemID == UUID.Zero)
                return;

            TaskInventoryItem item;

            lock (m_host.TaskInventory)
            {
                if (!m_host.TaskInventory.ContainsKey(InventorySelf()))
                    return;
                else
                    item = m_host.TaskInventory[InventorySelf()];
            }

            if (item.PermsGranter == UUID.Zero)
                return;

            if (CheckRuntimePerms(item, item.PermsGranter, ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION))
            {
                ScenePresence presence = World.GetScenePresence(item.PermsGranter);

                if ((presence != null) && (!presence.IsChildAgent))
                {
                    string result;
                    // Do NOT try to parse UUID, animations cannot be triggered by ID
                    UUID animID = InventoryKey(part, anim, (int)AssetType.Animation);
                    //UUID animID = GetInventoryKey(part, anim);
                    if (animID == UUID.Zero)
                        result = presence.AddAnimation(anim, m_host.UUID);
                    else
                        result = presence.AddAnimation(animID, m_host.UUID);
                    if (!String.IsNullOrEmpty(result))
                        ScriptShoutError(result);
                }
                else
                {
                    // Emulate SL's behavior of clearing this permission when this is called for an agent outside this region.
                    // See Mantis #2798 http://bugs.inworldz.com/mantis/view.php?id=2798
                    item.PermsMask &= ~ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION;
                    if (item.PermsMask == 0)
                        item.PermsGranter = UUID.Zero;
                    PermsChange(item, item.PermsGranter, item.PermsMask);
                    m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                            "run_time_permissions", new Object[] { (int)(item.PermsMask) }, new DetectParams[0]));
                    // SL displays "llStopAnimation: Script trying to stop animations but agent not found" in the first call
                    // and then "llStopAnimation: Unable to find agent, releasing animation permissions" on the second call.
                    // However this is pointless and disruptive to role-play and other users in general. Let's not emulate that.
                    // ScriptShoutError("llStartAnimation: Unable to find agent, releasing animation permissions");
                }
            }
        }

        public void llStartAnimation(string anim)
        {
            StartAnimation(m_host, anim);
        }

        public void iwStartLinkAnimation(int linknumber, string anim)
        {
            if (linknumber < 0) return;
            var part = GetLinkOnePrimOnly(linknumber);
            if (part != null)
                StartAnimation(part, anim);
        }

        /*
        public void llStartAnimation(string anim)
        {
            UUID invItemID = InventorySelf();
            if (invItemID == UUID.Zero)
                return;

            TaskInventoryItem item;

            lock (m_host.TaskInventory)
            {
                if (!m_host.TaskInventory.ContainsKey(InventorySelf()))
                    return;
                else
                    item = m_host.TaskInventory[InventorySelf()];
            }

            if (item.PermsGranter == UUID.Zero)
                return;

            if (CheckRuntimePerms(item, item.PermsGranter, ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION))
            {
                ScenePresence presence = World.GetScenePresence(item.PermsGranter);

                if ((presence != null) && (!presence.IsChildAgent))
                {
                    string result;
                    // Do NOT try to parse UUID, animations cannot be triggered by ID
                    UUID animID = InventoryKey(anim, (int)AssetType.Animation);
                    if (animID == UUID.Zero)
                        result = presence.AddAnimation(anim, m_host.UUID);
                    else
                        result = presence.AddAnimation(animID, m_host.UUID);
                    if (!String.IsNullOrEmpty(result))
                        ScriptShoutError(result);
                }
                else
                {
                    // Emulate SL's behavior of clearing this permission when this is called for an agent outside this region.
                    // See Mantis #2798 http://bugs.inworldz.com/mantis/view.php?id=2798
                    item.PermsMask &= ~ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION;
                    if (item.PermsMask == 0)
                        item.PermsGranter = UUID.Zero;
                    PermsChange(item, item.PermsGranter, item.PermsMask);
                    m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                            "run_time_permissions", new Object[] { (int)(item.PermsMask) }, new DetectParams[0]));
                    // SL displays "llStopAnimation: Script trying to stop animations but agent not found" in the first call
                    // and then "llStopAnimation: Unable to find agent, releasing animation permissions" on the second call.
                    // However this is pointless and disruptive to role-play and other users in general. Let's not emulate that.
                    // ScriptShoutError("llStartAnimation: Unable to find agent, releasing animation permissions");
                }
            }
        }
        */

        private void StopAnimation(SceneObjectPart part, string anim)
        {
            UUID invItemID = InventorySelf();
            if (invItemID == UUID.Zero)
                return;

            TaskInventoryItem item;

            lock (m_host.TaskInventory)
            {
                if (!m_host.TaskInventory.ContainsKey(InventorySelf()))
                    return;
                else
                    item = m_host.TaskInventory[InventorySelf()];
            }

            if (item.PermsGranter == UUID.Zero)
                return;

            if (CheckRuntimePerms(item, item.PermsGranter, ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION))
            {
                UUID animID = new UUID();

                if (!UUID.TryParse(anim, out animID))
                {
                    animID = InventoryKey(part, anim, (int)AssetType.Animation);
                }

                ScenePresence presence = World.GetScenePresence(item.PermsGranter);

                if ((presence != null) && (!presence.IsChildAgent))
                {
                    if (animID == UUID.Zero)
                        presence.RemoveAnimation(anim);
                    else
                        presence.RemoveAnimation(animID);
                }
                else
                {
                    // Emulate SL's behavior of clearing this permission when this is called for an agent outside this region.
                    // See Mantis #2798 http://bugs.inworldz.com/mantis/view.php?id=2798
                    item.PermsMask &= ~ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION;
                    if (item.PermsMask == 0)
                        item.PermsGranter = UUID.Zero;
                    PermsChange(item, item.PermsGranter, item.PermsMask);
                    m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                            "run_time_permissions", new Object[] { (int)(item.PermsMask) }, new DetectParams[0]));
                    // SL displays "llStopAnimation: Script trying to stop animations but agent not found" in the first call
                    // and then "llStopAnimation: Unable to find agent, releasing animation permissions" on the second call.
                    // However this is pointless and disruptive to role-play and other users in general. Let's not emulate that.
                    // ScriptShoutError("llStopAnimation: Unable to find agent, releasing animation permissions");
                }
            }
        }

        public void llStopAnimation(string anim)
        {
            StopAnimation(m_host, anim);
        }

        public void iwStopLinkAnimation(int linknumber, string anim)
        {
            if (linknumber < 0) return;

            var part = GetLinkOnePrimOnly(linknumber);
            if (part != null)
                StopAnimation(part, anim);
        }
        
        /*
        public void llStopAnimation(string anim)
        {
            UUID invItemID = InventorySelf();
            if (invItemID == UUID.Zero)
                return;

            TaskInventoryItem item;

            lock (m_host.TaskInventory)
            {
                if (!m_host.TaskInventory.ContainsKey(InventorySelf()))
                    return;
                else
                    item = m_host.TaskInventory[InventorySelf()];
            }

            if (item.PermsGranter == UUID.Zero)
                return;

            if (CheckRuntimePerms(item, item.PermsGranter, ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION))
            {
                UUID animID = new UUID();

                if (!UUID.TryParse(anim, out animID))
                {
                    animID = InventoryKey(anim);
                }

                ScenePresence presence = World.GetScenePresence(item.PermsGranter);

                if ((presence != null) && (!presence.IsChildAgent))
                {
                    if (animID == UUID.Zero)
                        presence.RemoveAnimation(anim);
                    else
                        presence.RemoveAnimation(animID);
                }
                else
                {
                    // Emulate SL's behavior of clearing this permission when this is called for an agent outside this region.
                    // See Mantis #2798 http://bugs.inworldz.com/mantis/view.php?id=2798
                    item.PermsMask &= ~ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION;
                    if (item.PermsMask == 0)
                        item.PermsGranter = UUID.Zero;
                    PermsChange(item, item.PermsGranter, item.PermsMask);
                    m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                            "run_time_permissions", new Object[] { (int)(item.PermsMask) }, new DetectParams[0]));
                    // SL displays "llStopAnimation: Script trying to stop animations but agent not found" in the first call
                    // and then "llStopAnimation: Unable to find agent, releasing animation permissions" on the second call.
                    // However this is pointless and disruptive to role-play and other users in general. Let's not emulate that.
                    // ScriptShoutError("llStopAnimation: Unable to find agent, releasing animation permissions");
                }
            }
        }
        */

        public void llPointAt(LSL_Vector pos)
        {
            
            NotImplemented("llPointAt");
        }

        public void llStopPointAt()
        {
            
            NotImplemented("llStopPointAt");
        }

        public void PrimTargetOmega(SceneObjectPart part, LSL_Vector axis, double spinrate, double gain)
        {
            PhysicsActor physActor = part.PhysActor;
            if (physActor != null && physActor.IsPhysical)
            {
                part.AngularVelocity = new Vector3((float)(axis.X * spinrate * gain), (float)(axis.Y * spinrate * gain), (float)(axis.Z * spinrate * gain));
            }
            else
            {
                if (gain == 0.0)
                {
                    part.AngularVelocity = Vector3.Zero;
                }
                else
                {
                    part.AngularVelocity = new Vector3((float)(axis.X * spinrate), (float)(axis.Y * spinrate), (float)(axis.Z * spinrate));
                }
            }

            part.ScheduleTerseUpdate();
            part.ParentGroup.HasGroupChanged = true;
        }

        public void llTargetOmega(LSL_Vector axis, float spinrate, float gain)
        {
            PrimTargetOmega(m_host, axis, spinrate, gain);
        }

        public void iwLinkTargetOmega(int linknumber, LSL_Vector axis, float spinrate, float gain)
        {
            var parts = GetLinkPrimsOnly(linknumber);

            foreach (SceneObjectPart part in parts)
                PrimTargetOmega(part, axis, spinrate, gain);
        }

        public int llGetStartParameter()
        {
            return _thisScript.ScriptState.StartParameter;
        }

        public void llGodLikeRezObject(string inventory, LSL_Vector pos)
        {
            NotImplemented("llGodLikeRezObject");
        }

        private IMuteListModule m_muteListModule = null;
        private bool IsScriptMuted(UUID targetUser)
        {
            // For performance reasons, don't make this call if it's a dialog to yourself.
            if (targetUser == m_host.OwnerID)
                return false;

            if (m_muteListModule == null)
                m_muteListModule = m_host.ParentGroup.Scene.RequestModuleInterface<IMuteListModule>();
            if (m_muteListModule != null)
            {
                if (m_muteListModule.IsMuted(m_host.OwnerID, targetUser))
                    return true; // recipient has object owner muted
                if (m_muteListModule.IsMuted(m_host.ParentGroup.UUID, targetUser))
                    return true; // recipient has this object muted
            }
            return false;
        }

        // Called with item != null when inventory needs to be persisted.
        // Don't call while task inventory is locked.
        private void PermsChange(TaskInventoryItem item, UUID granter, int mask)
        {
            int silentEstateManagement = (mask & ScriptBaseClass.PERMISSION_SILENT_ESTATE_MANAGEMENT) != 0 ? 1 : 0;
            _thisScript.ScriptState.MiscAttributes[(int)VM.RuntimeState.MiscAttr.SilentEstateManagement] = new object[1] { silentEstateManagement };

            if (item != null)
            {
                item.PermsGranter = granter;
                item.PermsMask = mask;

                m_host.Inventory.ForceInventoryPersistence();
                m_host.ParentGroup.HasGroupChanged = true;
            }
        }

        private int GetImplicitPermissions(TaskInventoryItem item, UUID agentID)
        {
            int implicitPerms = 0;

            if (m_host.ParentGroup.IsAttachment && agentID == m_host.ParentGroup.RootPart.AttachedAvatar)
            {
                // When attached, certain permissions are implicit if requested from owner
                implicitPerms = ScriptBaseClass.PERMISSION_TAKE_CONTROLS |
                                ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION |
                                ScriptBaseClass.PERMISSION_CONTROL_CAMERA |
                                ScriptBaseClass.PERMISSION_TRACK_CAMERA |
                                ScriptBaseClass.PERMISSION_ATTACH;
            }
            else if (m_host.ParentGroup.IsSeatedAnywhere(agentID)) // Sitting avatar
            {
                // When agent is sitting, certain permissions are implicit if requested from sitting agent
                implicitPerms = ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION |
                                ScriptBaseClass.PERMISSION_CONTROL_CAMERA |
                                ScriptBaseClass.PERMISSION_TRACK_CAMERA |
                                ScriptBaseClass.PERMISSION_TAKE_CONTROLS;
            }
            else
            {
                ScenePresence presence = World.GetScenePresence(agentID);
                if ((presence != null) && (presence.IsBot) && (!presence.IsChildAgent))
                {
                    if (presence.OwnerID == m_host.OwnerID)
                    {
                        implicitPerms = ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION;
                    }
                    else
                    {
                        SceneObjectPart parent = presence.GetSitTargetPart();
                        if(parent != null && parent.ObjectOwner == m_host.OwnerID)
                        {
                            implicitPerms = ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION;
                        }
                    }
                }
            }

            return implicitPerms;
        }

        private bool CheckRuntimePerms(TaskInventoryItem item, UUID agentID, int needs)
        {
            if (item.PermsGranter != agentID)
                return false;

            return ((item.PermsMask & needs) == needs);
        }

        private bool RequestImplicitPermissions(int perm, TaskInventoryItem item, UUID agentID)
        {
            int implicitPerms = GetImplicitPermissions(item, agentID);
            if (implicitPerms == 0)
                return false;

            if ((perm & (~implicitPerms)) != 0) // requesting more than implicit perms
                return false;   // needs user prompt

            lock (m_host.TaskInventory)
            {
                item.PermsGranter = agentID;
                item.PermsMask = perm;
            }
            PermsChange(item, item.PermsGranter, item.PermsMask);

            m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                "run_time_permissions", new Object[] {
                    (int)(item.PermsMask) },
                    new DetectParams[0]));

            return true;    // that's enough, all done
        }

        public void llRequestPermissions(string agent, int perm)
        {
            UUID agentID = new UUID();

            if (!UUID.TryParse(agent, out agentID))
                return;

            UUID invItemID = InventorySelf();

            if (invItemID == UUID.Zero)
                return; // Not in a prim? How??

            TaskInventoryItem item;
            lock (m_host.TaskInventory)
            {
                item = m_host.TaskInventory[invItemID];
            }
            // m_log.WarnFormat("[LSL]: '{0}' llRequestPermissions agent={1} perm={2}", item.Name, agent, perm);

            if ((perm & ScriptBaseClass.PERMISSION_TELEPORT) != 0)
            {
                // Temp attachments cannot request PERMISSION_TELEPORT.
                // See http://wiki.secondlife.com/wiki/LlAttachToAvatarTemp
                if (m_host.ParentGroup.IsTempAttachment)
                {
                    ScriptShoutError("Temporary attachments cannot request runtime permissions to teleport.");
                    perm &= ~ScriptBaseClass.PERMISSION_TELEPORT;
                    // now let it proceed with other perms, possibly releasing them all if only TELEPORT requested.
                }
            }

            if (agentID == UUID.Zero || perm == 0) // Releasing permissions
            {
                ReleaseControlsInternal(false, true, false, false);

                PermsChange(item, UUID.Zero, 0);

                m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                        "run_time_permissions", new Object[] {
                        (int)(0) },
                        new DetectParams[0]));

                return;
            }

            if (item.PermsGranter != agentID || (perm & ScriptBaseClass.PERMISSION_TAKE_CONTROLS) == 0)
                ReleaseControlsInternal(false, true, false, false);

            if (RequestImplicitPermissions(perm, item, agentID))
                return; // all done, only implicit perms were requested

            // Otherwise we need to prompt the user for permission.
            ScenePresence presence = World.GetScenePresence(agentID);
            if (presence == null)
            {
                // We have a new perms request, not answered, and no one to answer it.
                PermsChange(item, UUID.Zero, 0);

                // Requested agent is not in range, refuse perms, or muted
                ScriptSleep(200);
                m_ScriptEngine.PostScriptEvent(
                    m_itemID, 
                    new EventParams("run_time_permissions",  new Object[] { (int)(0) },
                    new DetectParams[0]));
                return;
            }
            // Only do the mute check if we're going to present the permissions dialog.
            if (IsScriptMuted(agentID))
            {
                // We have a new perms request, not answered, and no one to answer it.
                PermsChange(item, UUID.Zero, 0);
                return;
            }

            // Okay, now we need to ask the user for permission.
            string ownerName = resolveName(m_host.ParentGroup.RootPart.OwnerID);
            if (String.IsNullOrEmpty(ownerName))
                ownerName = "(hippos)";

            lock (m_host.TaskInventory)
            {
                item = m_host.TaskInventory[invItemID];
            }

            if (m_waitingForScriptAnswer != presence.ControllingClient)
            {
                ClearWaitingForScriptAnswer(m_waitingForScriptAnswer);
                presence.ControllingClient.OnScriptAnswer += handleScriptAnswer;
                presence.ControllingClient.OnConnectionClosed += handleConnectionClosed;
                m_waitingForScriptAnswer = presence.ControllingClient;
            }

            presence.ControllingClient.SendScriptQuestion(
                m_host.UUID, m_host.ParentGroup.RootPart.Name, ownerName, invItemID, perm);
        }

        private void ClearWaitingForScriptAnswer(IClientAPI client)
        {
            if ((m_waitingForScriptAnswer == null) || (client != m_waitingForScriptAnswer))
                return;
            client.OnScriptAnswer -= handleScriptAnswer;
            m_waitingForScriptAnswer = null;
        }

        public void handleConnectionClosed(IClientAPI client)
        {
            ClearWaitingForScriptAnswer(client);
        }

        void handleScriptAnswer(IClientAPI client, UUID taskID, UUID itemID, int answer)
        {
            if (taskID != m_host.UUID)
                return;
            if ((m_waitingForScriptAnswer == null) || (client != m_waitingForScriptAnswer))
                return;

            ClearWaitingForScriptAnswer(client);

            UUID invItemID = InventorySelf();
            if (invItemID == UUID.Zero)
                return;

            if ((answer & ScriptBaseClass.PERMISSION_TAKE_CONTROLS) == 0)
                ReleaseControlsInternal(false, true, false, false);

            TaskInventoryItem item;
            lock (m_host.TaskInventory)
            {
                item = m_host.TaskInventory[invItemID];
            }
            PermsChange(item, client.AgentId, answer);

            m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                    "run_time_permissions", new Object[] {
                    (int)(item.PermsMask) },
                    new DetectParams[0]));
        }

        // Must only release if presence matches perms holder.
        void handleMustReleaseControls(ScenePresence presence, SceneObjectPart part, TaskInventoryItem item, bool silent)
        {
            if (part != m_host)
                return; // not for me
            if (item == null)
                return; // no item to release from
            if (item.PermsGranter != UUID.Zero)
                if (item.PermsGranter != presence.UUID)
                    return; // not for me

            UUID invItemID = InventorySelf();
            if (invItemID != UUID.Zero)
            {
                lock (m_host.TaskInventory)
                {
                    if (!m_host.TaskInventory.ContainsKey(item.ItemID))
                        return; // item not in the contents of this prim

                    item = m_host.TaskInventory[invItemID];
                }
            }
            if (!IsMyScript(part, item)) return;

            // m_log.WarnFormat("[LSL]: handleMustReleaseControls {0}: releasing controls for {1}", part.Name, item.Name);

            // Call this before perms changes (it needs PERMISSION_TAKE_CONTROLS to work).
            ReleaseControlsInternal(false, true, silent, false);

            // On stand, lose both PERMISSION_CONTROL_CAMERA and PERMISSION_TAKE_CONTROLS
            if (item != null)
            {
                item.PermsMask &= ~ScriptBaseClass.PERMISSION_TAKE_CONTROLS;
                item.PermsMask &= ~ScriptBaseClass.PERMISSION_CONTROL_CAMERA;
                if (item.PermsMask == 0)
                    item.PermsGranter = UUID.Zero;
                PermsChange(item, item.PermsGranter, item.PermsMask);
            }
        }

        public string llGetPermissionsKey()
        {
            lock (m_host.TaskInventory)
            {
                foreach (TaskInventoryItem item in m_host.TaskInventory.Values)
                {
                    if (item.Type == 10 && item.ItemID == m_itemID)
                    {
                        return item.PermsGranter.ToString();
                    }
                }
            }

            return UUID.Zero.ToString();
        }

        public int llGetPermissions()
        {
            lock (m_host.TaskInventory)
            {
                foreach (TaskInventoryItem item in m_host.TaskInventory.Values)
                {
                    if (item.Type == 10 && item.ItemID == m_itemID)
                    {
                        int perms = item.PermsMask;
                        if (m_automaticLinkPermission)
                            perms |= ScriptBaseClass.PERMISSION_CHANGE_LINKS;
                        return perms;
                    }
                }
            }

            return 0;
        }

        public int llGetLinkNumber()
        {
            

            if (m_host.ParentGroup.PartCount > 1)
            {
                return m_host.LinkNum;
            }
            else
            {
                return 0;
            }
        }

        public void llSetLinkColor(int linknumber, LSL_Vector color, int face)
        {
            var parts = GetLinkPrimsOnly(linknumber);

            foreach (SceneObjectPart part in parts)
                SetColor(part, color, face);
        }

        public void llCreateLink(string target, int parent)
        {
            if (m_host.ParentGroup.IsAttachment)
                return; // Fail silently if attached

            UUID invItemID = InventorySelf();

            TaskInventoryItem item;
            lock (m_host.TaskInventory)
            {
                item = m_host.TaskInventory[invItemID];
            }

            if (!(m_automaticLinkPermission || CheckRuntimePerms(item, item.PermsGranter, ScriptBaseClass.PERMISSION_CHANGE_LINKS)))
            {
                ScriptShoutError("Script trying to link but PERMISSION_CHANGE_LINKS permission not set!");
                ScriptSleep(1000);
                return;
            }

            IClientAPI client = null;
            ScenePresence sp = World.GetScenePresence(item.PermsGranter);
            if (sp != null)
                client = sp.ControllingClient;

            if (!World.Permissions.CanEditObject(m_host.ParentGroup.UUID, client.AgentId, (uint)PermissionMask.Modify))
                return; // host object is not editable

            SceneObjectPart targetPart = World.GetSceneObjectPart((UUID)target);

            if (targetPart == null)
                return; // target UUID does not exist here

            if (targetPart.ParentGroup.RootPart.AttachmentPoint != 0)
                return; // Fail silently if attached

            if (!World.Permissions.CanEditObject(targetPart.ParentGroup.UUID, client.AgentId, (uint)PermissionMask.Modify))
                return; // target object is not editable
            if ((targetPart.ParentGroup.RootPart.OwnerMask & (uint)PermissionMask.Modify) != (uint)PermissionMask.Modify)
                return; // target is no-mod

            // Group 1 will have the new root prim, group 2 is being added.
            SceneObjectGroup group1 = null, group2 = null;
            if (parent != 0)
            {
                group1 = m_host.ParentGroup;
                group2 = targetPart.ParentGroup;
            }
            else
            {
                group1 = targetPart.ParentGroup;
                group2 = m_host.ParentGroup;
            }

            if (group1.OwnerID != group2.OwnerID)
                return; // Fail silently two different owners
            if (group1.RootPart == group2.RootPart)
                return; // Fail silently, target is already in the link set

            // after this, group2 doesn't really exist anymore (no prims)
            group1.LinkOtherGroupPrimsToThisGroup(group2); 

            group1.TriggerScriptChangedEvent(Changed.LINK);
            group1.RootPart.AddFlag(PrimFlags.CreateSelected);
            group1.HasGroupChanged = true;
            group1.ScheduleGroupForFullUpdate(PrimUpdateFlags.ForcedFullUpdate);

            if (client != null)
                group1.GetProperties(client);

            ScriptSleep(1000);
        }

        public void llBreakLink(int linknum)
        {
            
            UUID invItemID = InventorySelf();

            TaskInventoryItem item;
            lock (m_host.TaskInventory)
            {
                item = m_host.TaskInventory[invItemID];
            }

            if (!(m_automaticLinkPermission || CheckRuntimePerms(item, item.PermsGranter, ScriptBaseClass.PERMISSION_CHANGE_LINKS)))
            {
                ScriptShoutError("Script trying to link but PERMISSION_CHANGE_LINKS permission not set!");
                ScriptSleep(1000);
                return;
            }

            if (linknum < ScriptBaseClass.LINK_THIS)
                return;

            SceneObjectGroup parentPrim = m_host.ParentGroup;

            if (parentPrim.RootPart.AttachmentPoint != 0)
                return; // Fail silently if attached
            SceneObjectPart childPrim = null;

            switch (linknum)
            {
                case ScriptBaseClass.LINK_ROOT:
                    break;
                case ScriptBaseClass.LINK_SET:
                case ScriptBaseClass.LINK_ALL_OTHERS:
                case ScriptBaseClass.LINK_ALL_CHILDREN:
                case ScriptBaseClass.LINK_THIS:
                    foreach (SceneObjectPart part in parentPrim.GetParts())
                    {
                        if (part.UUID != m_host.UUID)
                        {
                            childPrim = part;
                            break;
                        }
                    }
                    break;
                default:
                    childPrim = parentPrim.GetLinkNumPart(linknum);
                    if (childPrim.UUID == m_host.UUID)
                        childPrim = null;
                    break;
            }

            if (linknum == ScriptBaseClass.LINK_ROOT)
            {
                // Restructuring Multiple Prims.
                var parts = new List<SceneObjectPart>(parentPrim.GetParts());
                parts.Remove(parentPrim.RootPart);
                foreach (SceneObjectPart part in parts)
                {
                    parentPrim.DelinkFromGroup(part.LocalId, true);
                }
                parentPrim.TriggerScriptChangedEvent(Changed.LINK);

                if (parts.Count > 0)
                {
                    SceneObjectPart newRoot = parts[0];
                    parts.Remove(newRoot);
                    foreach (SceneObjectPart part in parts)
                    {
                        newRoot.ParentGroup.LinkOtherGroupPrimsToThisGroup(part.ParentGroup);
                    }
                }
            }
            else
            {
                if (childPrim == null)
                    return;

                parentPrim.DelinkFromGroup(childPrim.LocalId, true);
                parentPrim.TriggerScriptChangedEvent(Changed.LINK);
            }
        }

        public void llBreakAllLinks()
        {
            
            SceneObjectGroup parentPrim = m_host.ParentGroup;
            if (parentPrim.RootPart.AttachmentPoint != 0)
                return; // Fail silently if attached

            foreach (SceneObjectPart part in parentPrim.GetPartsExcluding(parentPrim.RootPart))
            {
                parentPrim.DelinkFromGroup(part.LocalId, true);
                parentPrim.TriggerScriptChangedEvent(Changed.LINK);
            }
        }

        public string llGetLinkKey(int linknum)
        {
            List<object> links = GetLinkParts(linknum, true);
            if (links.Count == 1)
            {
                object o = links.First();
                if (o is ScenePresence)
                {
                    ScenePresence sp = o as ScenePresence;
                    return sp.UUID.ToString();
                }
                if (o is SceneObjectPart)
                {
                    SceneObjectPart part = o as SceneObjectPart;
                    return part.UUID.ToString();
                }
            }

            return UUID.Zero.ToString();
        }

        /// <summary>
        /// The rules governing the returned name are not simple. The only
        /// time a blank name is returned is if the target prim has a blank
        /// name. If no prim with the given link number can be found then
        /// usually NULL_KEY is returned but there are exceptions.
        /// 
        /// In a single unlinked prim, a call with 0 or LINK_THIS returns the name, all 
        /// other values for link number return NULL_KEY
        ///
        /// In link sets it is more complicated.
        /// 
        /// If the script is in the root prim:-
        ///     A link number returns NULL_KEY.
        ///     Positive link numbers return the name of the prim, 
        ///     or NULL_KEY if a prim does not exist at that position.
        ///     Negative link numbers (even out of range values) return the name 
        ///     of the first child prim (link 2), except for LINK_THIS (-4) which
        ///     returns the current link's name.
        /// 
        /// If the script is in a child prim:-
        ///     Link numbers 0 or 1 return the name of the root prim.
        ///     Positive link numbers return the name of the prim,
        ///     or NULL_KEY if a prim does not exist at that position.
        ///     Negative numbers (even out of range values) return the name of the root prim,
        ///     except for LINK_THIS (-4) which returns the current link's name.
        /// </summary>
        public string llGetLinkName(int linknum)
        {
            // returns a string version of NULL_KEY for not found or invalid linknum.
            string result = UUID.Zero.ToString();

            if (linknum > m_host.ParentGroup.LinkCount)
                return result;

            if (linknum > m_host.ParentGroup.PartCount)
            {
                // It's a seated avatar:
                List<object> res = new List<object>();
                GetAvatarAsPrimParam(linknum, ref res, ScriptBaseClass.PRIM_NAME);
                return (string)res[0];
            }

            if (m_host.LinkNum == 0)
            {
                // Single prim, only one non-NULL_KEY return
                if ((linknum == 0) || (linknum == ScriptBaseClass.LINK_THIS))
                    result = m_host.Name;
            }
            else
            {
                // Link set
                SceneObjectPart part = null;
                if (m_host.LinkNum == ScriptBaseClass.LINK_ROOT)
                {
                    // this is the Root prim
                    switch (linknum)
                    {
                        case ScriptBaseClass.LINK_THIS:
                        case ScriptBaseClass.LINK_ROOT:
                            part = m_host;
                            break;
                        default:
                            if (linknum >= 0)
                                part = m_host.ParentGroup.GetLinkNumPart(linknum);
                            else
                                part = m_host.ParentGroup.GetLinkNumPart(2);
                            break;
                    }
                }
                else
                {
                    // this is a child prim
                    switch (linknum)
                    {
                        case ScriptBaseClass.LINK_THIS:
                            part = m_host;
                            break;
                        case ScriptBaseClass.LINK_SET:
                        case ScriptBaseClass.LINK_ALL_OTHERS:
                        case ScriptBaseClass.LINK_ALL_CHILDREN:
                        case ScriptBaseClass.LINK_ROOT:
                        case 0:
                            part = m_host.ParentGroup.RootPart;
                            break;
                        default:
                            if (linknum < 0)
                                part = m_host.ParentGroup.RootPart;
                            else
                            if (linknum > 0)
                                part = m_host.ParentGroup.GetLinkNumPart(linknum);
                            break;
                    }
                }
                if (part != null)
                    result = part.Name;
            }

            return result;
        }

        public int GetInventoryNumber(SceneObjectPart part, int type)
        {
            int count = 0;

            lock (part.TaskInventory)
            {
                foreach (KeyValuePair<UUID, TaskInventoryItem> inv in part.TaskInventory)
                {
                    if (inv.Value.Type == type || type == -1)
                    {
                        count = count + 1;
                    }
                }
            }

            return count;
        }
        public int llGetInventoryNumber(int type)
        {
            return GetInventoryNumber(m_host, type);
        }
        public int iwGetLinkInventoryNumber(int linknumber, int type)
        {
            int count = 0;
            var parts = GetLinkPrimsOnly(linknumber);

            foreach (SceneObjectPart part in parts)
            {
                count += GetInventoryNumber(part, type);
            }
            return count;
        }

        public class InvNameComparer : IComparer
        {
            static string OrderLSL = " !\"#$%&'()*+,-./0123456789:;<=>?@[\\]^_`ABCDEFGHIJKLMNOPQRSTUVWXYZ{|}~";
            private int CompareChars(char c1, char c2)
            {
                int val1 = OrderLSL.IndexOf(char.ToUpper(c1));
                int val2 = OrderLSL.IndexOf(char.ToUpper(c2));
                if ((val1 != -1) && (val2 != -1))   // both in range above
                    return val1 - val2;     // normal comparison

                // any value outside the range will do, so long as it's consistently calculated
                if ((val1 == -1) && (val2 == -1))
                    return c1 - c2; // both are outside range
                if (val1 == val2)
                    return 0;
                if (val1 == -1)
                    return 1;   // only val1 outside range, sort after val2
                return -1;      // only val2 outside range, sort after val1
            }

            // Compares by Height, Length, and Width.
            public int Compare(object o1, object o2)
            {
                string name1 = (string)o1;
                string name2 = (string)o2;
                int max = Math.Min(name1.Length, name2.Length);
                for (int x = 0; x < max; x++)
                {
                    int cmp = CompareChars(name1[x], name2[x]);
                    if (cmp != 0)
                        return cmp;
                }
                if (name1.Length < name2.Length)
                    return -1;
                return 1;
            }
        }
        public static IComparer InvNameSorter()
        {
            return (IComparer)new InvNameComparer();
        }

        public string GetInventoryName(SceneObjectPart part, int type, int number)
        {
            ArrayList keys = new ArrayList();

            lock (part.TaskInventory)
            {
                foreach (KeyValuePair<UUID, TaskInventoryItem> inv in part.TaskInventory)
                {
                    if (inv.Value.Type == type || type == -1)
                    {
                        keys.Add(inv.Value.Name);
                    }
                }
            }

            if (keys.Count == 0)
            {
                return String.Empty;
            }

            keys.Sort(InvNameSorter());
            if (keys.Count > number)
            {
                return (string)keys[number];
            }
            return String.Empty;
        }
        public string llGetInventoryName(int type, int number)
        {
            return GetInventoryName(m_host, type, number);
        }
        public string iwGetLinkInventoryName(int linknumber, int type, int number)
        {
            string name = String.Empty;
            var part = GetLinkOnePrimOnly(linknumber);
            if (part != null)
                name = GetInventoryName(part, type, number);

            return name;
        }

        private LSL_List SearchInventory(SceneObjectPart part, int type, string pattern, int matchType)
        {
            if(matchType > 2)
            {
                if (matchType == 3) LSLError("IW_MATCH_COUNT is not a valid matching type for iwSearchInventory or iwSearchLinkInventory.");
                else if (matchType == 4) LSLError("IW_MATCH_COUNT_REGEX is not a valid matching type for iwSearchInventory or iwSearchLinkInventory.");
                return new LSL_List();
            }
            ArrayList keys = new ArrayList();
            lock(part.TaskInventory)
            {
                foreach(KeyValuePair<UUID, TaskInventoryItem> inv in part.TaskInventory)
                {
                    if(inv.Value.Type == type || type == -1)
                    {
                        if (String.IsNullOrEmpty(pattern) || iwMatchString(inv.Value.Name, pattern, matchType) == 1)
                            keys.Add(inv.Value.Name);
                    }
                }
            }

            if(keys.Count > 0)
            {
                keys.Sort(InvNameSorter());
                return new LSL_List(keys.ToArray());
            }


            return new LSL_List();
        }

        public LSL_List iwSearchInventory(int type, string pattern, int matchType)
        {
            return SearchInventory(m_host, type, pattern, matchType);
        }

        public LSL_List iwSearchLinkInventory(int link, int type, string pattern, int matchtype)
        {
            if (link < 0) return new LSL_List();
            var part = GetLinkOnePrimOnly(link);
            if (part != null)
                return SearchInventory(part, type, pattern, matchtype);
            else
                return new LSL_List();
        }

        public int GetPartScriptTotal(SceneObjectPart part, int which)
        {
            IScriptEngine engine = m_ScriptEngine;

            int total = 0;
            lock (part.TaskInventory)
            {
                foreach (KeyValuePair<UUID, TaskInventoryItem> inv in part.TaskInventory)
                {
                    if (inv.Value.Type == ScriptBaseClass.INVENTORY_SCRIPT)
                    {
                        switch (which)
                        {
                            case ScriptBaseClass.OBJECT_TOTAL_SCRIPT_COUNT:
                                total++;
                                break;
                            case ScriptBaseClass.OBJECT_RUNNING_SCRIPT_COUNT:
                                if (engine.GetScriptState(inv.Value.ItemID))
                                    total++;
                                break;
                            case ScriptBaseClass.OBJECT_SCRIPT_MEMORY:  // total mem possible (128K)
                                total += engine.GetMaxMemory();
                                break;
                            case ScriptBaseClass.IW_OBJECT_SCRIPT_MEMORY_USED:
                                total += m_ScriptEngine.GetUsedMemory(inv.Value.ItemID);
                                break;
                        }
                    }
                }
            }
            return total;
        }
        public float GetPartScriptTotalFloat(SceneObjectPart part, int which)
        {
            IScriptEngine engine = m_ScriptEngine;
            List<float> scriptTimes = new List<float>();
            float total = 0.0f;
            lock (part.TaskInventory)
            {
                foreach (KeyValuePair<UUID, TaskInventoryItem> inv in part.TaskInventory)
                {
                    if (inv.Value.Type == ScriptBaseClass.INVENTORY_SCRIPT)
                    {
                        switch (which)
                        {
                            case ScriptBaseClass.OBJECT_SCRIPT_TIME:
                                scriptTimes.Add(engine.GetAverageScriptTime(inv.Value.ItemID));
                                break;
                        }
                    }
                }
            }

            if (which != ScriptBaseClass.OBJECT_SCRIPT_TIME)
                return total;

            if (scriptTimes.Count < 1)
                return 0.0f;

            // OBJECT_SCRIPT_TIME is seconds not ms
            return (float)(scriptTimes.Average() / 1000.0);
        }

        public int GetObjectScriptTotal(SceneObjectGroup group, int which)
        {
            int total = 0;
            group.ForEachPart(delegate(SceneObjectPart part)
            {
                total += GetPartScriptTotal(part, which);
            });
            return total;
        }

        public float GetObjectScriptTotalFloat(SceneObjectGroup group, int which)
        {
            float total = 0;
            group.ForEachPart(delegate(SceneObjectPart part)
            {
                total += GetPartScriptTotalFloat(part, which);
            });
            return total;
        }

        private int GetAgentTotals(ScenePresence sp, int which)
        {
            int total = 0;
            List<SceneObjectGroup> attachments = sp.GetAttachments();
            foreach (SceneObjectGroup group in attachments)
            {
                switch (which) {
                    case ScriptBaseClass.OBJECT_RUNNING_SCRIPT_COUNT:
                    case ScriptBaseClass.OBJECT_TOTAL_SCRIPT_COUNT:
                    case ScriptBaseClass.OBJECT_SCRIPT_MEMORY:
                    case ScriptBaseClass.IW_OBJECT_SCRIPT_MEMORY_USED:
                        total += GetObjectScriptTotal(group, which);
                        break;
                }
            }
            return total;
        }

        private float GetAgentTotalsFloat(ScenePresence sp, int which)
        {
            List<float> scriptTimes = new List<float>();
            float total = 0;

            List<SceneObjectGroup> attachments = sp.GetAttachments();
            foreach (SceneObjectGroup group in attachments)
            {
                switch (which) {
                    case ScriptBaseClass.OBJECT_SCRIPT_TIME:
                        scriptTimes.Add(GetObjectScriptTotalFloat(group, which));
                        break;
                }
            }

            if (which != ScriptBaseClass.OBJECT_SCRIPT_TIME)
                return total;

            if (scriptTimes.Count < 1)
                return 0.0f;

            // OBJECT_SCRIPT_TIME is seconds not ms
            return (float)(scriptTimes.Average() / 1000.0);
        }

        public float llGetEnergy()
        {
            // TODO: figure out real energy value
            return 1.0f;
        }

        int DeliverReasonToResult(string reason)
        {
            int rc = -1;
            switch (reason)
            {
                case "":
                    rc = ScriptBaseClass.IW_DELIVER_OK;
                    break;
                case "uuid":
                    rc = ScriptBaseClass.IW_DELIVER_BADKEY;
                    break;
                case "muted":
                    rc = ScriptBaseClass.IW_DELIVER_MUTED;
                    break;
                case "item":
                    rc = ScriptBaseClass.IW_DELIVER_ITEM;
                    break;
                case "prim":
                    rc = ScriptBaseClass.IW_DELIVER_PRIM;
                    break;
                case "user":
                    rc = ScriptBaseClass.IW_DELIVER_USER;
                    break;
                case "perm":
                    rc = ScriptBaseClass.IW_DELIVER_PERM;
                    break;
                default:
                    m_log.ErrorFormat("[LSL]: Unknown delivery failure reason: {0}", reason);   // for testing and safety
                    break;
            }
            return rc;
        }

        // deliveries to objects do not get a delay, deliveries to avatars get a delay (also errors)
        private int _GiveInventory(SceneObjectPart part, string destination, string inventory, out bool needsDelay)
        {
            bool found = false;
            UUID destId = UUID.Zero;
            UUID objId = UUID.Zero;
            byte assetType = 0;
            string objName = String.Empty;

            needsDelay = true;

            if (!UUID.TryParse(destination, out destId))
            {
                llSay(0, "Could not parse key " + destination);
                return ScriptBaseClass.IW_DELIVER_BADKEY;
            }

            if (IsScriptMuted(destId))
            {
                m_log.InfoFormat("[LSL]: Not offering inventory from muted {0} to {1}", m_host.ParentGroup.UUID, destId);
                return ScriptBaseClass.IW_DELIVER_MUTED; // recipient has sender muted
            }

            // move the first object found with this inventory name
            lock (part.TaskInventory)
            {
                foreach (KeyValuePair<UUID, TaskInventoryItem> inv in part.TaskInventory)
                {
                    if (inv.Value.Name == inventory)
                    {
                        found = true;
                        objId = inv.Key;
                        assetType = (byte)inv.Value.Type;
                        objName = inv.Value.Name;
                        break;
                    }
                }
            }

            if (!found)
            {
                ScriptShoutError(String.Format("Could not find item '{0}'", inventory));
                return ScriptBaseClass.IW_DELIVER_NONE;
            }

            // check if destination is a part (much faster than checking if it's an avatar)
            SceneObjectPart destPart = m_host.ParentGroup.Scene.GetSceneObjectPart(destId);
            if (destPart == null)
            {
                // destination must be an avatar
                string reason = "";
                Scene scene = m_host.ParentGroup.Scene;
                ScenePresence avatar;
                IClientAPI remoteClient = null;
                if (scene.TryGetAvatar(destId, out avatar))
                {
                    remoteClient = avatar.ControllingClient;
                }

                InventoryItemBase agentItem =
                        World.MoveTaskInventoryItem(destId, remoteClient, UUID.Zero, part, objId, false, out reason);

                if (agentItem == null)
                    return DeliverReasonToResult(reason);

                byte dialog = (byte)InstantMessageDialog.TaskInventoryOffered;
                byte[] bucket = new byte[1];
                bucket[0] = assetType;
                SceneObjectPart rootPart = part.ParentGroup.RootPart;
                Vector3 pos = rootPart.AbsolutePosition;
                string URL = Util.LocationURL(World.RegionInfo.RegionName, pos);

                GridInstantMessage msg = new GridInstantMessage(World,
                        rootPart.OwnerID, rootPart.Name, destId,
                            dialog, false, "'"+objName+"'  ( "+URL+" )",
                        agentItem.ID, true, rootPart.AbsolutePosition,
                        bucket);

                if (m_TransferModule != null)
                {
                        m_TransferModule.SendInstantMessage(msg, delegate(bool success) { });
                }
            }
            else
            {
                needsDelay = false;
                // destination is an object
                World.MoveTaskInventoryItem(destId, part, objId);
                // we don't support delivery return codes to objects (yet)
            }
            return ScriptBaseClass.IW_DELIVER_OK;
        }

        private void GiveLinkInventory(int linknumber, string destination, string inventory, int delay, bool includeRC)
        {
            bool needsDelay = true;
            int rc = ScriptBaseClass.IW_DELIVER_PRIM;   // part not found
            try
            {
                var parts = GetLinkPrimsOnly(linknumber);
                foreach (SceneObjectPart part in parts)
                {
                    rc = _GiveInventory(part, destination, inventory, out needsDelay);
                    if (rc != ScriptBaseClass.IW_DELIVER_NONE)
                        return; // give results from the first matching prim only
                }
            }
            finally
            {
                // C# cannot handle: includeRC ? rc : null
                object result = null;
                if (includeRC) result = rc;
                if (!needsDelay) delay = 0;
                m_ScriptEngine.SysReturn(m_itemID, result, delay);
            }
        }
        public void llGiveInventory(string destination, string inventory)
        {
            GiveLinkInventory(ScriptBaseClass.LINK_THIS, destination, inventory, 2000, false);
        }
        public void iwGiveLinkInventory(int linknumber, string destination, string inventory)
        {
            GiveLinkInventory(linknumber, destination, inventory, 2000, false);
        }
        public void iwDeliverInventory(int linknumber, string destination, string inventory)
        {
            GiveLinkInventory(linknumber, destination, inventory, 100, true);
        }

        private void RemoveLinkInventory(SceneObjectPart part, string name)
        {
            lock (part.TaskInventory)
            {
                foreach (TaskInventoryItem item in part.TaskInventory.Values)
                {
                    if (item.Name == name)
                    {
                        part.Inventory.RemoveInventoryItem(item.ItemID);
                        return;
                    }
                }
            }
        }
        public void llRemoveInventory(string name)
        {
            RemoveLinkInventory(m_host, name);
        }
        public void iwRemoveLinkInventory(int linknumber, string name)
        {
            var parts = GetLinkPrimsOnly(linknumber);
            foreach (SceneObjectPart part in parts)
                RemoveLinkInventory(part, name);
        }


        private void PrimSetText(SceneObjectPart part, string text, LSL_Vector color, double alpha)
        {
            
            Vector3 av3 = new Vector3(Util.Clip((float)color.X, 0.0f, 1.0f),
                                      Util.Clip((float)color.Y, 0.0f, 1.0f),
                                      Util.Clip((float)color.Z, 0.0f, 1.0f));
            part.SetText(text, av3, Util.Clip((float)alpha, 0.0f, 1.0f));
            part.ParentGroup.HasGroupChanged = true;
        }

        public void llSetText(string text, LSL_Vector color, float alpha)
        {
            
            PrimSetText(m_host, text, color, alpha);
        }

        public float llWater(LSL_Vector offset)
        {
            
            return (float)World.RegionInfo.RegionSettings.WaterHeight;
        }

        public void llPassTouches(int pass)
        {
            m_host.PassTouches = (pass != 0);
        }

        private bool IsScriptOwnerFriendOf(UUID uuid)
        {
            List<FriendListItem> friends = World.CommsManager.UserService.GetUserFriendList(uuid);
            FriendListItem friend = 
                friends.Find(delegate(FriendListItem f)
                {
                    return f.Friend == m_host.OwnerID;
                });
            return friend != null;
        }

        private bool GetAgentData(UUID uuid, int data, out string reply)
        {
            // Grab the profile even if only CurrentAgent is needed so that it uses the profile cache with timeout.
            UserProfileData userProfile = World.CommsManager.UserService.GetUserProfile(uuid);

            reply = String.Empty;

            switch (data)
            {
                case ScriptBaseClass.DATA_ONLINE:
                    Scene scene = m_host.ParentGroup.Scene;
                    ScenePresence SP = scene.GetScenePresence(uuid);
                    if (SP != null) // user is here, always allow this
                        reply = "1";
                    else
                    if ((userProfile == null) || (userProfile.CurrentAgent == null) || (!userProfile.CurrentAgent.AgentOnline))
                    {
                        reply = "0";
                    }
                    else if (uuid == m_host.OwnerID)
                    {
                        // script is owned by the target UUID, checking on their own status
                        reply = "1";
                    }
                    else
                    {   // user is online... are they in this region?
                        if (IsScriptOwnerFriendOf(uuid))
                            reply = "1";
                        else
                        {
                            UserPreferencesData prefs = scene.CommsManager.UserService.RetrieveUserPreferences(uuid);
                            reply = "0";
                            if (prefs != null)
                            {
                                // This is where we check the "Only friend and groups know I'm online" option.
                                // Only applies to friends (not groups) in InWorldz (for now at least).
                                if (prefs.ListedInDirectory)
                                    reply = "1";
                            }
                        }
                    }
                    break;

                case ScriptBaseClass.DATA_NAME: // "First Last"
                    reply = (userProfile == null) ? String.Empty : userProfile.FirstName + " " + userProfile.SurName;
                    break;

                case ScriptBaseClass.DATA_BORN: // "YYYY-MM-DD"
                    DateTime born = new DateTime(1970, 1, 1, 0, 0, 0, 0);
                    if (userProfile != null)
                        born = born.AddSeconds(userProfile.Created);
                    reply = born.ToString("yyyy-MM-dd");
                    break;

                case ScriptBaseClass.DATA_RATING: // Deprecated
                    reply = "0,0,0,0,0,0";
                    break;

                case ScriptBaseClass.DATA_PAYINFO: // 0|1|2|3
                    reply = "0";    // PAYMENT_INFO_ON_FILE=1, PAYMENT_INFO_USED=2
                    break;

                case ScriptBaseClass.DATA_ACCOUNT_TYPE:
                    if (userProfile != null)
                        reply = userProfile.CustomType; // e.g. "Plus"
                    break;

                default:
                    return false; // Raise no event
            }
            return true;
        }

        public string iwGetAgentData(string id, int data)
        {
            string reply;
            UUID uuid;

            if (UUID.TryParse(id, out uuid))
                if (GetAgentData(uuid, data, out reply))
                    return reply;

            return String.Empty;
        }

        public int iwIsPlusUser(string id)
        {
            string accountType = iwGetAgentData(id, ScriptBaseClass.DATA_ACCOUNT_TYPE);
            return m_host.ParentGroup.Scene.RegionInfo.IsPlusUser(accountType.ToLower()) ? 1 : 0;
        }

        public void llRequestAgentData(string id, int data)
        {
            const int DELAY = 100;

            UUID rq = UUID.Random();
            UUID uuid;

            m_ScriptEngine.SysReturn(m_itemID, rq.ToString(), DELAY);

            string reply = String.Empty;
            if (UUID.TryParse(id, out uuid))
                if (!GetAgentData(uuid, data, out reply))
                    reply = String.Empty;

            m_ScriptEngine.PostObjectEvent(m_localID,
                    new EventParams("dataserver", new Object[] { rq.ToString(), reply },
                    new DetectParams[0]));
        }

        public string llRequestInventoryData(string name)
        {
            TaskInventoryDictionary itemDictionary = (TaskInventoryDictionary)m_host.TaskInventory.Clone();

            foreach (TaskInventoryItem item in itemDictionary.Values)
            {
                if (item.Type == ScriptBaseClass.INVENTORY_LANDMARK && item.Name == name)
                {
                    UUID tid = AsyncCommands.
                        DataserverPlugin.RegisterRequest(m_localID,
                                                     m_itemID, item.AssetID.ToString());

                    Vector3 region = new Vector3(
                        World.RegionInfo.RegionLocX * Constants.RegionSize,
                        World.RegionInfo.RegionLocY * Constants.RegionSize,
                        0);

                    World.CommsManager.AssetCache.GetAsset(item.AssetID,
                        delegate(UUID i, AssetBase a)
                        {
                            AssetLandmark lm = new AssetLandmark(a);

                            float rx = (uint)(lm.RegionHandle >> 32);
                            float ry = (uint)lm.RegionHandle;
                            region = lm.Position + new Vector3(rx, ry, 0) - region;

                            string reply = region.ToString();
                            AsyncCommands.
                                DataserverPlugin.DataserverReply(i.ToString(),
                                                             reply);
                        }, AssetRequestInfo.InternalRequest());

                    ScriptSleep(1000);
                    return tid.ToString();
                }
            }
            
            ScriptSleep(1000);
            return String.Empty;
        }

        public string iwRequestAnimationData(string name)
        {
            TaskInventoryDictionary itemDictionary = (TaskInventoryDictionary)m_host.TaskInventory.Clone();
            foreach (TaskInventoryItem item in itemDictionary.Values)
            {
                if (item.Type == ScriptBaseClass.INVENTORY_ANIMATION && item.Name == name)
                {
                    UUID tid = AsyncCommands.
                        DataserverPlugin.RegisterRequest(m_localID,
                                                     m_itemID, item.AssetID.ToString());

                    World.CommsManager.AssetCache.GetAsset(item.AssetID,
                        delegate(UUID i, AssetBase a)
                        {
                            string reply;           
                            try
                            {
                                BinBVHAnimation anim = new BinBVHAnimation(a.Data);
                                reply = String.Format("{0} {1} {2} {3} {4} {5} {6} {7} {8}", 
                                                (int)anim.Priority,
                                                (int)(anim.Loop ? 1 : 0), 
                                                ((float)anim.Length).ToString("F4"),
                                                ((float)anim.InPoint).ToString("F4"),
                                                ((float)anim.OutPoint).ToString("F4"),
                                                ((float)anim.EaseInTime).ToString("F4"),
                                                ((float)anim.EaseOutTime).ToString("F4"),
                                                (int)anim.HandPose,
                                                (string)anim.ExpressionName
                                                );
                            }
                            catch (Exception)
                            {
                                reply = String.Empty;
                            }

                            AsyncCommands.DataserverPlugin.DataserverReply(i.ToString(), reply);
                        }, AssetRequestInfo.InternalRequest());

                    ScriptSleep(1000);
                    return tid.ToString();
                }
            }

            ScriptSleep(1000);
            return String.Empty;
        }

        public void llSetDamage(float damage)
        {
            NotImplemented("llSetDamage");
        }

        public void llTeleportAgentHome(string agent)
        {
            const int delay = 5000;
            try
            {
                UUID agentId = new UUID();
                if (UUID.TryParse(agent, out agentId))
                {
                    // agent must be known to this region
                    ScenePresence presence = World.GetScenePresence(agentId);
                    if (presence == null)
                        return;

                    if (!IsTeleportAuthorized(presence))
                        return;

                    presence.ControllingClient.SendTeleportLocationStart();
                    World.TeleportClientHome(agentId, presence.ControllingClient);
                }
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }

        public void llTextBox(string avatar, string message, int chat_channel)
        {
            IDialogModule dm = World.RequestModuleInterface<IDialogModule>();

            if (dm == null)
                return;

            UUID av = new UUID();
            if (!UUID.TryParse(avatar, out av))
            {
                LSLError("First parameter to llDialog needs to be a key");
                return;
            }

            if ((message != null) && (message.Length > 1024))
                message = message.Substring(0, 1024);

            dm.SendTextBoxToUser(av, message, chat_channel, m_host.Name, m_host.UUID, m_host.OwnerID);
            ScriptSleep(1000);
        }

        public void llModifyLand(int action, int brush)
        {
            ITerrainModule tm = m_ScriptEngine.World.RequestModuleInterface<ITerrainModule>();
            if (tm != null)
            {
                tm.ModifyTerrain(m_host.OwnerID, m_host.AbsolutePosition, (byte)brush, (byte)action, m_host.OwnerID);
            }
        }

        public void iwSetGround(int x1, int y1, int x2, int y2, float height)
        {
            ITerrainModule tm = m_ScriptEngine.World.RequestModuleInterface<ITerrainModule>();
            if (tm != null)
            {
                tm.SetTerrain(m_host.OwnerID, x1, y1, x2, y2, height);
            }
        }

        public void llCollisionSound(string impact_sound, float impact_volume)
        {
            UUID soundId = UUID.Zero;
            if (!UUID.TryParse(impact_sound, out soundId))
            {
                lock (m_host.TaskInventory)
                {
                    foreach (TaskInventoryItem item in m_host.TaskInventory.Values)
                    {
                        if (item.Type == (int)AssetType.Sound && item.Name == impact_sound)
                        {
                            soundId = item.AssetID;
                            break;
                        }
                    }
                }
            }
            m_host.CollisionSound = soundId;
            m_host.CollisionSoundVolume = impact_volume;
        }

        public void llCollisionSprite(string impact_sprite)
        {
            NotImplemented("llCollisionSprite");
        }

        public string llGetAnimation(string id)
        {
            // This should only return a value if the avatar is in the same region
            
            UUID avatar = (UUID)id;
            ScenePresence presence = World.GetScenePresence(avatar);
            if (presence == null)
                return String.Empty;

            if (m_host.RegionHandle == presence.RegionHandle)
            {
                Dictionary<UUID, string> animationstateNames = AnimationSet.Animations.AnimStateNames;

                if (presence != null)
                {
                    AnimationSet currentAnims = presence.Animations;
                    string currentAnimationState = String.Empty;
                    if (animationstateNames.TryGetValue(currentAnims.DefaultAnimation.AnimID, out currentAnimationState))
                        return currentAnimationState;
                }
            }
            return String.Empty;
        }

        public void llMessageLinked(int linknumber, int num, string msg, string id)
        {
            int DELAY = 0;

            var parts = GetLinkPrimsOnly(linknumber);

            UUID partItemID;
            foreach (SceneObjectPart part in parts)
            {
                TaskInventoryDictionary itemsDictionary = (TaskInventoryDictionary)part.TaskInventory.Clone();

                foreach (TaskInventoryItem item in itemsDictionary.Values)
                {
                    if (item.Type == ScriptBaseClass.INVENTORY_SCRIPT)
                    {
                        partItemID = item.ItemID;
                        int linkNumber = m_host.LinkNum;
                        if (m_host.ParentGroup.LinkCount == 1)  // if there are seated avatars, single-prim becomes multi, else 0
                            linkNumber = 0;

                        object[] resobj = new object[] { linkNumber, num, msg, id };

                        m_ScriptEngine.PostScriptEvent(partItemID,
                                new EventParams("link_message",
                                resobj, new DetectParams[0]));

                        //now, determine a variable delay based on the current size of the script queue
                        const float LOW_SPACE_THRESHOLD = 0.2f;
                        const int LOW_SPACE_DELAY = 50;
                        if (m_ScriptEngine.GetEventQueueFreeSpacePercentage(partItemID) <= LOW_SPACE_THRESHOLD)
                        {
                            DELAY = LOW_SPACE_DELAY;
                        }
                    }
                }
            }

            ScriptSleep(DELAY);
        }

        public void llPushObject(string target, LSL_Vector impulse, LSL_Vector ang_impulse, int local)
        {
            bool pushrestricted = World.RegionInfo.RegionSettings.RestrictPushing;
            bool pushAllowed = false;

            bool pusheeIsAvatar = false;
            UUID targetID = UUID.Zero;

            if (!UUID.TryParse(target, out targetID))
                return;

            ScenePresence pusheeav = null;
            Vector3 PusheePos = Vector3.Zero;
            SceneObjectPart pusheeob = null;

            ScenePresence avatar = World.GetScenePresence(targetID);
            if (avatar != null)
            {
                pusheeIsAvatar = true;

                // Pushee doesn't have a physics actor
                if (avatar.PhysicsActor == null)
                    return;

                // Pushee is in GodMode this pushing object isn't owned by them
                if (avatar.GodLevel > 0 && m_host.OwnerID != targetID)
                    return;

                pusheeav = avatar;

                // Find pushee position
                // Pushee Linked?
                SceneObjectPart parentobj = pusheeav.GetSitTargetPart();
                if (parentobj != null)
                {
                    PusheePos = parentobj.AbsolutePosition;
                }
                else
                {
                    PusheePos = pusheeav.AbsolutePosition;
                }
            }

            if (!pusheeIsAvatar)
            {
                // not an avatar so push is not affected by parcel flags
                pusheeob = World.GetSceneObjectPart((UUID)target);

                // We can't find object
                if (pusheeob == null)
                    return;

                // Object not pushable.  Not an attachment and has no physics component
                if (!pusheeob.ParentGroup.IsAttachment && pusheeob.PhysActor == null)
                    return;

                PusheePos = pusheeob.AbsolutePosition;
                pushAllowed = true;
            }
            else
            {
                if (pushrestricted)
                {
                    ILandObject targetlandObj = World.LandChannel.GetLandObject(PusheePos.X, PusheePos.Y);

                    // We didn't find the parcel but region is push restricted so assume it is NOT ok
                    if (targetlandObj == null)
                        return;

                    // if push disabled, script must be owned by the land owner.  That includes group-deeded land (script object must be deeded too).  It will also work if the script is owned by an estate owner or manager.
                    if (m_host.OwnerID == targetlandObj.landData.OwnerID || World.IsEstateManager(m_host.OwnerID))
                    {
                        pushAllowed = true;
                    }
                }
                else
                {
                    ILandObject targetlandObj = World.LandChannel.GetLandObject(PusheePos.X, PusheePos.Y);
                    if (targetlandObj == null)
                    {
                        // We didn't find the parcel but region isn't push restricted so assume it's ok
                        pushAllowed = true;
                    }
                    else
                    {
                        // Parcel push restriction
                        if ((targetlandObj.landData.Flags & (uint)ParcelFlags.RestrictPushObject) == (uint)ParcelFlags.RestrictPushObject)
                        {
                            // if push disabled, script must be owned by the land owner.  That includes group-deeded land (script object must be deeded too).  It will also work if the script is owned by an estate owner or manager.
                            if (m_host.OwnerID == targetlandObj.landData.OwnerID || World.IsEstateManager(m_host.OwnerID))
                            {
                                pushAllowed = true;
                            }
                        }
                        else
                        {
                            // Parcel isn't push restricted
                            pushAllowed = true;
                        }
                    }
                }
            }
            if (pushAllowed)
            {
                float distance = (PusheePos - m_host.AbsolutePosition).Length();
                float distance_term = distance * distance * distance; // Script Energy
                float pusher_mass = m_host.GetMass();

                float PUSH_ATTENUATION_DISTANCE = 17f;
                float PUSH_ATTENUATION_SCALE = 5f;
                float distance_attenuation = 1f;
                if (distance > PUSH_ATTENUATION_DISTANCE)
                {
                    float normalized_units = 1f + (distance - PUSH_ATTENUATION_DISTANCE) / PUSH_ATTENUATION_SCALE;
                    distance_attenuation = 1f / normalized_units;
                }

                Vector3 applied_linear_impulse = new Vector3((float)impulse.X, (float)impulse.Y, (float)impulse.Z);
                {
                    float impulse_length = applied_linear_impulse.Length();

                    float desired_energy = impulse_length * pusher_mass;
                    if (desired_energy > 0f)
                        desired_energy += distance_term;

                    float scaling_factor = 1f;
                    scaling_factor *= distance_attenuation;
                    applied_linear_impulse *= scaling_factor;

                }
                if (pusheeIsAvatar)
                {
                    if (pusheeav != null)
                    {
                        PhysicsActor pa = pusheeav.PhysicsActor;
                        if (pa != null)
                        {
                            if (local != 0)
                            {
                                applied_linear_impulse *= m_host.GetWorldRotation();
                            }
                            pa.AddForce(applied_linear_impulse, (local!=0) ? ForceType.LocalLinearImpulse : ForceType.GlobalLinearImpulse);
                        }
                    }
                }
                else
                {
                    if (pusheeob != null)
                    {
                        pusheeob.ApplyImpulse(applied_linear_impulse, local != 0);
                    }
                }

                PhySleep();
            }
        }

        public void llPassCollisions(int pass)
        {
            PhysicsActor phyActor = m_host.PhysActor;
            if (phyActor != null)
            {
                phyActor.Properties.PassCollisions = (pass == 0) ? false : true;
            }
        }

        public string llGetScriptName()
        {
            string result = String.Empty;

            lock (m_host.TaskInventory)
            {
                foreach (TaskInventoryItem item in m_host.TaskInventory.Values)
                {
                    if (item.Type == 10 && item.ItemID == m_itemID)
                    {
                        result = item.Name != null ? item.Name : String.Empty;
                        break;
                    }
                }
            }

            return result;
        }

        // this function to understand which shape it is (taken from meshmerizer)
        // quite useful can be used by meshmerizer to have a centralized point of understanding the shape
        // except that it refers to scripting constants
        private int getScriptPrimType(PrimitiveBaseShape primShape)
        {
            if (primShape.SculptEntry)
                return ScriptBaseClass.PRIM_TYPE_SCULPT;

            // ProfileCurve seems to combine hole shape and profile curve so we need to only compare against the lower 3 bits
            byte profileCurve = primShape.ProfileCurve;
            profileCurve &= (byte)0x07;

            if (profileCurve == (byte)ProfileShape.Square)
            {
                if ((primShape.PathCurve == (byte)Extrusion.Straight) || (primShape.PathCurve == (byte)(Extrusion.Flexible)))
                    return ScriptBaseClass.PRIM_TYPE_BOX;
                else if (primShape.PathCurve == (byte)Extrusion.Curve1)
                    return ScriptBaseClass.PRIM_TYPE_TUBE;
            }
            else if (profileCurve == (byte)ProfileShape.Circle)
            {
                if ((primShape.PathCurve == (byte)Extrusion.Straight) || (primShape.PathCurve == (byte)(Extrusion.Flexible)))
                    return ScriptBaseClass.PRIM_TYPE_CYLINDER;
                else if (primShape.PathCurve == (byte)Extrusion.Curve1)
                    return ScriptBaseClass.PRIM_TYPE_TORUS;
            }
            else if (profileCurve == (byte)ProfileShape.HalfCircle)
            {
                if (primShape.PathCurve == (byte)Extrusion.Curve1 || primShape.PathCurve == (byte)Extrusion.Curve2)
                    return ScriptBaseClass.PRIM_TYPE_SPHERE;
            }
            else if (profileCurve == (byte)ProfileShape.EquilateralTriangle)
            {
                if ((primShape.PathCurve == (byte)Extrusion.Straight) || (primShape.PathCurve == (byte)(Extrusion.Flexible)))
                    return ScriptBaseClass.PRIM_TYPE_PRISM;
                else if (primShape.PathCurve == (byte)Extrusion.Curve1)
                    return ScriptBaseClass.PRIM_TYPE_RING;
            }
            return ScriptBaseClass.PRIM_TYPE_BOX;
        }

        // Helper functions to understand if object has cut, hollow, dimple, and other affecting number of faces
        private void hasCutHollowDimpleProfileCut(int primType, PrimitiveBaseShape shape, out bool hasCut, out bool hasHollow,
            out bool hasDimple, out bool hasProfileCut)
        {
            if (primType == ScriptBaseClass.PRIM_TYPE_BOX
                ||
                primType == ScriptBaseClass.PRIM_TYPE_CYLINDER
                ||
                primType == ScriptBaseClass.PRIM_TYPE_PRISM)

                hasCut = (shape.ProfileBegin > 0) || (shape.ProfileEnd > 0);
            else
                hasCut = (shape.PathBegin > 0) || (shape.PathEnd > 0);

            hasHollow = shape.ProfileHollow > 0;
            hasDimple = (shape.ProfileBegin > 0) || (shape.ProfileEnd > 0); // taken from llSetPrimitiveParms
            hasProfileCut = hasDimple; // is it the same thing?

        }

        public int llGetNumberOfSides()
        {
            return m_host.GetNumberOfSides();
        }

        public int llGetLinkNumberOfSides(int linknumber)
        {
            int sides = 0;

            var parts = GetLinkPrimsOnly(linknumber);

            foreach (SceneObjectPart part in parts)
                sides += part.GetNumberOfSides();

            return sides;
        }

        /* The new / changed functions were tested with the following LSL script:
        default
        {
            state_entry()
            {
                rotation rot = llEuler2Rot(<0,70,0> * DEG_TO_RAD);

                llOwnerSay("to get here, we rotate over: "+ (string) llRot2Axis(rot));
                llOwnerSay("and we rotate for: "+ (llRot2Angle(rot) * RAD_TO_DEG));

                // convert back and forth between quaternion <-> vector and angle

                rotation newrot = llAxisAngle2Rot(llRot2Axis(rot),llRot2Angle(rot));

                llOwnerSay("Old rotation was: "+(string) rot);
                llOwnerSay("re-converted rotation is: "+(string) newrot);

                llSetRot(rot);  // to check the parameters in the prim
            }
        }
        */

        // Xantor 29/apr/2008
        // Returns rotation described by rotating angle radians about axis.
        // q = cos(a/2) + i (x * sin(a/2)) + j (y * sin(a/2)) + k (z * sin(a/2))
        public LSL_Rotation llAxisAngle2Rot(LSL_Vector axis, float angle)
        {
            double x, y, z, s, t;

            s = Math.Cos(angle / 2);
            t = Math.Sin(angle / 2); // temp value to avoid 2 more sin() calcs
            x = axis.X * t;
            y = axis.Y * t;
            z = axis.Z * t;

            return new LSL_Rotation((float)x, (float)y, (float)z, (float)s);
        }


        // Xantor 29/apr/2008
        // converts a Quaternion to X,Y,Z axis rotations
        public LSL_Vector llRot2Axis(LSL_Rotation rot)
        {
            double x, y, z;

            if (rot.W < 0f)
            {
                //negate to prevent NaN in sqrt after normalization is applied
                rot = Quaternion.Negate(rot);
            }

            if (rot.W > 1) // normalization needed
            {
                float length = (float)Math.Sqrt(rot.X * rot.X + rot.Y * rot.Y +
                        rot.Z * rot.Z + rot.W * rot.W);

                rot.X /= length;
                rot.Y /= length;
                rot.Z /= length;
                rot.W /= length;

            }

            // double angle = 2 * Math.Acos(rot.s);
            double s = Math.Sqrt(1 - rot.W * rot.W);
            if (s < 0.001)
            {
                x = 1;
                y = z = 0;
            }
            else
            {
                x = rot.X / s; // normalize axis
                y = rot.Y / s;
                z = rot.Z / s;
            }

            return new LSL_Vector((float)x, (float)y, (float)z);
        }

        // Returns the angle of a quaternion (see llRot2Axis for the axis)
        public float llRot2Angle(LSL_Rotation rot)
        {
            if (rot.W > 1) // normalization needed
            {
                float length = (float)Math.Sqrt(rot.X * rot.X + rot.Y * rot.Y +
                        rot.Z * rot.Z + rot.W * rot.W);

                rot.X /= length;
                rot.Y /= length;
                rot.Z /= length;
                rot.W /= length;
            }

            float angle = (float)(2 * Math.Acos(rot.W));

            return angle;
        }

        public float llAcos(float val)
        {
            return (float)Math.Acos(val);
        }

        public float llAsin(float val)
        {
            return (float)Math.Asin(val);
        }

        public float llAngleBetween(LSL_Rotation a, LSL_Rotation b)
        {
            double aa = (a.X * a.X + a.Y * a.Y + a.Z * a.Z + a.W * a.W);
            double bb = (b.X * b.X + b.Y * b.Y + b.Z * b.Z + b.W * b.W);
            double aa_bb = aa * bb;
            if (aa_bb == 0) return 0.0f;
            double ab = (a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);
            double quotient = (ab * ab) / aa_bb;
            if (quotient >= 1.0) return 0.0f;
            return (float)Math.Acos(2 * quotient - 1);
        }

        // This function refers to inventory/object permissions, not LSL script permissions.
        const uint FULL_PERM = (uint)(PermissionMask.Copy | PermissionMask.Transfer | PermissionMask.Modify);
        private bool IsFullPerm(uint permsMask)
        {
            return ((permsMask & FULL_PERM) == FULL_PERM);
        }

        private bool IsMyScript(SceneObjectPart part, TaskInventoryItem item)
        {
            return (part == m_host) && (item.Type == 10) && (item.ItemID == m_itemID);
        }

        // If name isn't empty or targetID isn't a null ID, returns the asset ID of the matching inventory item.
        // WARNING: Always returns null ID if the matching inventory item is not full-perm.
        private UUID GetInventoryKey(SceneObjectPart part, string name)
        {
            UUID assetID = UUID.Zero;
            lock (part.TaskInventory)
            {
                foreach (TaskInventoryItem item in part.TaskInventory.Values)
                {
                    if (item.Name == name)
                    {
                        // Only return the asset ID if full perm or asking for is THIS script.
                        if (IsFullPerm(item.CurrentPermissions) || IsMyScript(part, item))
                            assetID = item.AssetID;
                        break;
                    }
                }
            }

            return assetID;
        }

        public string llGetInventoryKey(string name)
        {
            return GetInventoryKey(m_host, name).ToString(); ;
        }

        public string iwGetLinkInventoryKey(int linknumber, string name)
        {
            var part = GetLinkOnePrimOnly(linknumber);
            if (part == null)
                return UUID.Zero.ToString();

            return GetInventoryKey(part, name).ToString();
        }

        public string GetInventoryDesc(SceneObjectPart part, string name)
        {
            lock (part.TaskInventory)
            {
                foreach (KeyValuePair<UUID, TaskInventoryItem> inv in part.TaskInventory)
                {
                    if (inv.Value.Name == name)
                    {
                        return inv.Value.Description;
                    }
                }
            }
            return String.Empty;
        }
        public string iwGetLinkInventoryDesc(int linknumber, string name)
        {
            var part = GetLinkOnePrimOnly(linknumber);
            if (part == null)
                return String.Empty;

            return GetInventoryDesc(part, name);
        }

        public string GetInventoryLastOwner(SceneObjectPart part, string name)
        {
            lock (part.TaskInventory)
            {
                foreach (KeyValuePair<UUID, TaskInventoryItem> inv in part.TaskInventory)
                {
                    if (inv.Value.Name == name)
                    {
                        return inv.Value.LastOwnerID.ToString();
                    }
                }
            }
            return UUID.Zero.ToString();
        }
        public string iwGetLinkInventoryLastOwner(int linknumber, string name)
        {
            var part = GetLinkOnePrimOnly(linknumber);
            if (part == null)
                return UUID.Zero.ToString();

            return GetInventoryLastOwner(part, name);
        }

        public void llAllowInventoryDrop(int add)
        {
            if (add != 0)
                m_host.ParentGroup.RootPart.AllowedDrop = true;
            else
                m_host.ParentGroup.RootPart.AllowedDrop = false;

            // Update the object flags
            m_host.ParentGroup.RootPart.DoAggregateScriptEvents();
        }

        public LSL_Vector llGetSunDirection()
        {
            LSL_Vector SunDoubleVector3;
            Vector3 SunFloatVector3;

            // sunPosition estate setting is set in OpenSim.Region.CoreModules.SunModule
            // have to convert from Vector3 (float) to LSL_Vector (double)
            SunFloatVector3 = World.RegionInfo.RegionSettings.SunVector;
            SunDoubleVector3.X = (float)SunFloatVector3.X;
            SunDoubleVector3.Y = (float)SunFloatVector3.Y;
            SunDoubleVector3.Z = (float)SunFloatVector3.Z;

            return SunDoubleVector3;
        }

        public LSL_Vector llGetTextureOffset(int face)
        {
            return GetTextureOffset(m_host, face);
        }

        private LSL_Vector GetTextureOffset(SceneObjectPart part, int face)
        {
            Primitive.TextureEntry tex = part.Shape.Textures;
            LSL_Vector offset = new LSL_Vector();
            if (face == ScriptBaseClass.ALL_SIDES)
            {
                face = 0;
            }
            if (face >= 0 && face < part.GetNumberOfSides())
            {
                offset.X = tex.GetFace((uint)face).OffsetU;
                offset.Y = tex.GetFace((uint)face).OffsetV;
                offset.Z = 0.0f;
                return offset;
            }
            else
            {
                return offset;
            }
        }

        public LSL_Vector llGetTextureScale(int side)
        {
            Primitive.TextureEntry tex = m_host.Shape.Textures;
            LSL_Vector scale;
            if (side == -1)
            {
                side = 0;
            }
            scale.X = tex.GetFace((uint)side).RepeatU;
            scale.Y = tex.GetFace((uint)side).RepeatV;
            scale.Z = 0.0f;
            return scale;
        }

        public float llGetTextureRot(int face)
        {
            
            return GetTextureRot(m_host, face);
        }

        private float GetTextureRot(SceneObjectPart part, int face)
        {
            Primitive.TextureEntry tex = part.Shape.Textures;
            if (face == -1)
            {
                face = 0;
            }
            if (face >= 0 && face < part.GetNumberOfSides())
            {
                return tex.GetFace((uint)face).Rotation;
            }
            else
            {
                return 0.0f;
            }
        }

        public int llSubStringIndex(string source, string pattern)
        {
            return source.IndexOf(pattern);
        }

        public int iwSubStringIndex(string source, string pattern, int offset, int isCaseSensitive)
        {
            if (isCaseSensitive!=0)
                return source.IndexOf(pattern, offset);

            return source.ToUpper().IndexOf(pattern.ToUpper(), offset);
        }

        public string llGetOwnerKey(string id)
        {
            UUID key = new UUID();
            if (UUID.TryParse(id, out key))
            {
                try
                {
                    SceneObjectPart obj = World.GetSceneObjectPart(key);
                    if (obj == null)
                        return id; // the key is for an agent so just return the key
                    else
                        return obj.OwnerID.ToString();
                }
                catch (KeyNotFoundException)
                {
                    return id; // The Object/Agent not in the region so just return the key
                }
            }
            else
            {
                return UUID.Zero.ToString();
            }
        }

        public LSL_Vector llGetCenterOfMass()
        {
            NotImplemented("llGetCenterOfMass");
            return new LSL_Vector();
        }

        public LSL_List llListSort(LSL_List src, int stride, int ascending)
        {
            if (stride <= 0)
            {
                stride = 1;
            }
            return src.Sort(stride, ascending);
        }

        public int llGetListLength(LSL_List src)
        {
            if (src == null)
            {
                return 0;
            }
            else
            {
                return src.Length;
            }
        }

        public int llList2Integer(LSL_List src, int index)
        {
            if (index < 0)
            {
                index = src.Length + index;
            }

            return src.GetLSLIntegerItem(index);
        }

        public float llList2Float(LSL_List src, int index)
        {
            if (index < 0)
            {
                index = src.Length + index;
            }

            return src.GetLSLFloatItem(index);
        }

        public string llList2String(LSL_List src, int index)
        {
            if (index < 0)
            {
                index = src.Length + index;
            }

            return src.GetLSLStringItem(index);
        }

        public string llList2Key(LSL_List src, int index)
        {
            if (index < 0)
            {
                index = src.Length + index;
            }

            //LL returns strings here and doesnt seem to test if 
            //this is in fact a valid UUID
            if (src.GetItemType(index) == Types.VarType.String)
            {
                return src.GetLSLStringItem(index);
            }
            else
            {
                return String.Empty;
            }
        }

        public LSL_Vector llList2Vector(LSL_List src, int index)
        {
            if (index < 0)
            {
                index = src.Length + index;
            }

            return src.GetVector3Item(index);
        }

        public LSL_Rotation llList2Rot(LSL_List src, int index)
        {
            if (index < 0)
            {
                index = src.Length + index;
            }

            return src.GetQuaternionItem(index);
        }

        public LSL_List llList2List(LSL_List src, int start, int end)
        {
            return src.GetSublist(start, end);
        }

        public LSL_List llDeleteSubList(LSL_List src, int start, int end)
        {
            return src.DeleteSublist(start, end);
        }

        public int llGetListEntryType(LSL_List src, int index)
        {
            if (index < 0)
            {
                index = src.Length + index;
            }
            if (index >= src.Length)
            {
                return 0;
            }

            if (src.Data[index] is int || src.Data[index] is Int32)
                return 1;
            if (src.Data[index] is float || src.Data[index] is Single || src.Data[index] is Double)
                return 2;
            if (src.Data[index] is string || src.Data[index] is String)
            {
                UUID tuuid;
                if (UUID.TryParse(src.Data[index].ToString(), out tuuid))
                {
                    return 4;
                }
                else
                {
                    return 3;
                }
            }
            if (src.Data[index] is LSL_Vector)
                return 5;
            if (src.Data[index] is LSL_Rotation)
                return 6;
            if (src.Data[index] is LSL_List)
                return 7;
            return 0;

        }

        /// <summary>
        /// Process the supplied list and return the
        /// content of the list formatted as a comma
        /// separated list. There is a space after
        /// each comma.
        /// </summary>
        public string llList2CSV(LSL_List src)
        {
            //string ret = String.Empty;
            StringBuilder ret = new StringBuilder();
            int x = 0;

            if (src.Data.Length > 0)
            {
                ret.Append(src.Data[x++].ToString());
                for (; x < src.Data.Length; x++)
                {
                    ret.Append(", ");
                    ret.Append(src.Data[x].ToString());
                }
            }

            return ret.ToString();
        }

        /// <summary>
        /// The supplied string is scanned for commas
        /// and converted into a list. Commas are only
        /// effective if they are encountered outside
        /// of '<' '>' delimiters. Any whitespace
        /// before or after an element is trimmed.
        /// </summary>
        public LSL_List llCSV2List(string src)
        {
            List<object> result = new List<object>();
            string item;
            int parens = 0;
            int start = 0;
            int length = 0;

            for (int i = 0; i < src.Length; i++)
            {
                switch (src[i])
                {
                    case '<':
                        parens++;
                        length++;
                        break;
                    case '>':
                        if (parens > 0)
                            parens--;
                        length++;
                        break;
                    case ',':
                        if (parens == 0)
                        {
                            item = src.Substring(start, length).Trim();
                            result.Add(item);
                            start += length + 1;
                            length = 0;
                        }
                        else
                        {
                            length++;
                        }
                        break;
                    default:
                        length++;
                        break;
                }
            }

            item = src.Substring(start, length).Trim();
            result.Add(item);
            return new LSL_List(result);
        }

        ///  <summary>
        ///  Randomizes the list, be arbitrarily reordering
        ///  sublists of stride elements. As the stride approaches
        ///  the size of the list, the options become very
        ///  limited.
        ///  </summary>
        ///  <remarks>
        ///  This could take a while for very large list
        ///  sizes.
        ///  </remarks>
        public LSL_List llListRandomize(LSL_List src, int stride)
        {
            List<object> result;
            Random rand = new Random();

            int chunkk;
            int[] chunks;

            if (stride <= 0)
            {
                stride = 1;
            }

            // Stride MUST be a factor of the list length
            // If not, then return the src list. This also
            // traps those cases where stride > length.
            if (src.Length != stride && src.Length % stride == 0)
            {
                chunkk = src.Length / stride;

                chunks = new int[chunkk];

                for (int i = 0; i < chunkk; i++)
                    chunks[i] = i;

                // Knuth shuffle the chunkk index
                for (int i = chunkk - 1; i >= 1; i--)
                {
                    // Elect an unrandomized chunk to swap
                    int index = rand.Next(i + 1);
                    int tmp;

                    // and swap position with first unrandomized chunk
                    tmp = chunks[i];
                    chunks[i] = chunks[index];
                    chunks[index] = tmp;
                }

                // Construct the randomized list
                result = new List<object>();

                for (int i = 0; i < chunkk; i++)
                {
                    for (int j = 0; j < stride; j++)
                    {
                        result.Add(src.Data[chunks[i] * stride + j]);
                    }
                }
            }
            else
            {
                object[] array = new object[src.Length];
                Array.Copy(src.Data, 0, array, 0, src.Length);
                result = new List<object>(array);
            }

            return new LSL_List(result);
        }

        /// <summary>
        /// Elements in the source list starting with 0 and then
        /// every i+stride. If the stride is negative then the scan
        /// is backwards producing an inverted result.
        /// Only those elements that are also in the specified
        /// range are included in the result.
        /// </summary>
        public LSL_List llList2ListStrided(LSL_List src, int start, int end, int stride)
        {
            List<object> result = new List<object>();
            int[] si = new int[2];
            int[] ei = new int[2];
            bool twopass = false;

            //  First step is always to deal with negative indices
            if (start < 0)
                start = src.Length + start;
            if (end < 0)
                end = src.Length + end;

            //  Out of bounds indices are OK, just trim them
            //  accordingly
            if (start > src.Length)
                start = src.Length;
            if (end > src.Length)
                end = src.Length;

            //  There may be one or two ranges to be considered
            if (start != end)
            {
                if (start <= end)
                {
                    si[0] = start;
                    ei[0] = end;
                }
                else
                {
                    si[1] = start;
                    ei[1] = src.Length;
                    si[0] = 0;
                    ei[0] = end;
                    twopass = true;
                }

                //  The scan always starts from the beginning of the
                //  source list, but members are only selected if they
                //  fall within the specified sub-range. The specified
                //  range values are inclusive.
                //  A negative stride reverses the direction of the
                //  scan producing an inverted list as a result.
                if (stride == 0)
                    stride = 1;

                if (stride > 0)
                {
                    for (int i = 0; i < src.Length; i += stride)
                    {
                        if (i <= ei[0] && i >= si[0])
                            result.Add(src.Data[i]);
                        if (twopass && i >= si[1] && i <= ei[1])
                            result.Add(src.Data[i]);
                    }
                }
                else if (stride < 0)
                {
                    for (int i = src.Length - 1; i >= 0; i += stride)
                    {
                        if (i <= ei[0] && i >= si[0])
                            result.Add(src.Data[i]);
                        if (twopass && i >= si[1] && i <= ei[1])
                            result.Add(src.Data[i]);
                    }
                }
            }

            return new LSL_List(result);
        }

        public int llGetRegionAgentCount()
        {
            return (int)(World.GetAvatars().Count);
        }

        public LSL_Vector llGetRegionCorner()
        {
            return new LSL_Vector(World.RegionInfo.RegionLocX * Constants.RegionSize, World.RegionInfo.RegionLocY * Constants.RegionSize, 0);
        }

        /// <summary>
        /// Insert the list identified by <src> into the
        /// list designated by <dest> such that the first
        /// new element has the index specified by <index>
        /// </summary>
        public LSL_List llListInsertList(LSL_List dest, LSL_List src, int index)
        {
            LSL_List pref = null;
            LSL_List suff = null;

            if (index < 0)
            {
                index = index + dest.Length;
                if (index < 0)
                {
                    index = 0;
                }
            }

            if (index != 0)
            {
                pref = dest.GetSublist(0, index - 1);
                if (index < dest.Length)
                {
                    suff = dest.GetSublist(index, -1);
                    return pref + src + suff;
                }
                else
                {
                    return pref + src;
                }
            }
            else
            {
                if (index < dest.Length)
                {
                    suff = dest.GetSublist(index, -1);
                    return src + suff;
                }
                else
                {
                    return src;
                }
            }

        }

        /// <summary>
        /// Returns the index of the first occurrence of test
        /// in src.
        /// </summary>
        public int llListFindList(LSL_List src, LSL_List test)
        {
            int index = -1;
            int length = src.Length - test.Length + 1;

            // If either list is empty, do not match
            if (src.Length != 0 && test.Length != 0)
            {
                for (int i = 0; i < length; i++)
                {
                    if (src.Data[i].Equals(test.Data[0]))
                    {
                        int j;
                        for (j = 1; j < test.Length; j++)
                            if (!src.Data[i + j].Equals(test.Data[j]))
                                break;
                        if (j == test.Length)
                        {
                            index = i;
                            break;
                        }
                    }
                }
            }

            return index;
        }

        public string llGetObjectName()
        {
            return m_host.Name != null ? m_host.Name : String.Empty;
        }

        private string LimitLength(string text, int maxlen)
        {
            if (text == null)
                return String.Empty;

            int len = text.Length;
            if (len <= maxlen)
                return text;

            return text.Substring(0, maxlen);
        }

        public void llSetObjectName(string name)
        {
            m_host.Name = LimitLength(name, MAX_OBJ_NAME);
        }

        public string llGetDate()
        {
            DateTime date = DateTime.Now.ToUniversalTime();
            string result = date.ToString("yyyy-MM-dd");
            return result;
        }

        public int llEdgeOfWorld(LSL_Vector pos, LSL_Vector dir)
        {
            double RegionSize = (double)Constants.RegionSize;
            int nx, ny; // relative neighbour position

            // The rounded values are just to avoid inaccuracies, like 90 degrees has dir.x of 0.000024 or something
            double dirx = Math.Round(dir.X, 3, MidpointRounding.AwayFromZero);   // convert dir to integers with 3 digits accuracy
            double diry = Math.Round(dir.Y, 3, MidpointRounding.AwayFromZero);   // convert dir to integers with 3 digits accuracy
            // dirx and diry are only used for zero comparison and the ratio between them (slope)

            if ((dirx == 0.0) && (diry == 0.0))
            {
                // Direction vector is 0,0 so return
                // false since we're staying in the sim
                return 0;
            }
            if (dirx == 0.0)
            {
                nx = 0;
                if (diry > 0.0)
                    ny = 1;
                else
                    ny = -1;
            }
            else
                if (diry == 0.0)
                {
                    ny = 0;
                    if (dirx > 0.0)
                        nx = 1;
                    else
                        nx = -1;
                }
                else
                {
                    // need to find which edge it will pass through
                    // both dirx and diry are non-zero
                    // Abs(dirx) as we divide the tests into up or down
                    double slope = diry / Math.Abs(dirx);
                    double aslope = Math.Abs(slope);    // we know the direction, just need the ratio
                    double normx, normy;
                    if (diry > 0.0)   // up
                    {
                        if (dirx > 0.0)
                        {
                            // North-East quadrant
                            normx = RegionSize - pos.X;
                            normy = (normx * slope) + pos.Y;
                            if ((pos.X == pos.Y) && (aslope == 1.0))
                                return 1;   // reject exact corners so things don't get stuck trying to cross
                            if (normy <= RegionSize)
                            {
                                nx = 1;
                                ny = 0;
                            }
                            else
                            {
                                nx = 0;
                                ny = 1;
                            }
                        }
                        else
                        {
                            // North-West quadrant
                            normx = pos.X;
                            normy = (normx * slope) + pos.Y;
                            if ((RegionSize - pos.X == pos.Y) && (aslope == 1.0))
                                return 1;   // reject exact corners so things don't get stuck trying to cross
                            if (normy <= RegionSize)
                            {
                                nx = -1;
                                ny = 0;
                            }
                            else
                            {
                                nx = 0;
                                ny = 1;
                            }
                        }
                    }
                    else
                    {   // down
                        if (dirx > 0.0)
                        {
                            // South-East quadrant
                            normx = RegionSize - pos.X;
                            normy = (normx * slope) + pos.Y;
                            if ((RegionSize - pos.X == pos.Y) && (aslope == 1.0))
                                return 1;   // reject exact corners so things don't get stuck trying to cross
                            if (normy >= 0.0)
                            {
                                nx = 1;
                                ny = 0;
                            }
                            else
                            {
                                nx = 0;
                                ny = -1;
                            }
                        }
                        else
                        {
                            // South-West quadrant
                            normx = pos.X;
                            normy = (normx * slope) + pos.Y;
                            if ((pos.X == pos.Y) && (aslope == 1.0))
                                return 1;   // reject exact corners so things don't get stuck trying to cross
                            if (normy >= 0.0)
                            {
                                nx = -1;
                                ny = 0;
                            }
                            else
                            {
                                nx = 0;
                                ny = -1;
                            }
                        }
                    }
                }

            int neighborX = (int)World.RegionInfo.RegionLocX + nx;
            int neighborY = (int)World.RegionInfo.RegionLocY + ny;

            if (neighborX < 0 || neighborY < 0)
            {
                return 1;
            }

            if (World.HasNeighbor((uint)neighborX, (uint)neighborY))
            {
                return 0;
            }
            else
            {
                return 1;
            }
        }

        public int llGetAgentInfo(string id)
        {
            UUID key = new UUID();
            if (!UUID.TryParse(id, out key))
            {
                return 0;
            }

            int flags = 0;

            ScenePresence agent = World.GetScenePresence(key);
            if (agent == null)
            {
                return 0;
            }

            if (agent.IsChildAgent)
                return 0; // Fail if they are not in the same region

            // note: in OpenSim, sitting seems to cancel AGENT_ALWAYS_RUN, unlike SL
            if (agent.SetAlwaysRun)
            {
                flags |= ScriptBaseClass.AGENT_ALWAYS_RUN;
            }

            if (agent.HasAttachments())
            {
                flags |= ScriptBaseClass.AGENT_ATTACHMENTS;
                if (agent.HasScriptedAttachments())
                    flags |= ScriptBaseClass.AGENT_SCRIPTED;
            }

            if ((agent.AgentControlFlags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_AWAY) != 0)
            {
                flags |= ScriptBaseClass.AGENT_AWAY;
            }

            // seems to get unset, even if in mouselook, when avatar is sitting on a prim???
            if ((agent.AgentControlFlags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_MOUSELOOK) != 0)
            {
                flags |= ScriptBaseClass.AGENT_MOUSELOOK;
            }

            if ((agent.State & AgentState.Typing) != 0)
            {
                flags |= ScriptBaseClass.AGENT_TYPING;
            }

            if (agent.Animations.HasAnimation(Animations.BUSY))
            {
                // There's no server-side busy mode, only whether the "BUSY" animation is currently active.
                flags |= ScriptBaseClass.AGENT_BUSY;
            }

            string agentMovementAnimation = agent.GetMovementAnimation();

            if (agentMovementAnimation == "CROUCH")
            {
                flags |= ScriptBaseClass.AGENT_CROUCHING;
            }

            if (agentMovementAnimation == "WALK" || agentMovementAnimation == "CROUCHWALK")
            {
                flags |= ScriptBaseClass.AGENT_WALKING;
            }

            if (agentMovementAnimation == "RUN")
            {
                // There's no way to detect running with these status flags, make this work for running too.
                flags |= ScriptBaseClass.AGENT_WALKING;
            }

            SceneObjectPart parent = agent.GetSitTargetPart();
            if (parent != null)
            {
                flags |= ScriptBaseClass.AGENT_ON_OBJECT;
                flags |= ScriptBaseClass.AGENT_SITTING;
            }

            if (agent.Animations.DefaultAnimation.AnimID == AnimationSet.Animations.AnimsUUID["SIT_GROUND_CONSTRAINED"])
            {
                flags |= ScriptBaseClass.AGENT_SITTING;
            }

            //Workaround for issue where AGENT_FLYING is sometimes set while sitting
            //Since a seated avatar cannot be flying or in the air, do not set the flags if agent is seated
            if((flags & ScriptBaseClass.AGENT_SITTING) == 0)
            {
                if ((agent.AgentControlFlags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_FLY) != 0)
                {
                    flags |= ScriptBaseClass.AGENT_FLYING;
                    flags |= ScriptBaseClass.AGENT_IN_AIR; // flying always implies in-air, even if colliding with e.g. a wall
                }
            }

            // not colliding implies in air. Note: flying also implies in-air, even if colliding (see above)

            // note: AGENT_IN_AIR and AGENT_WALKING/AGENT_SITTING seem to be mutually exclusive states in SL.

            // note: this may need some tweaking when walking downhill. you "fall down" for a brief instant
            // and don't collide when walking downhill, which instantly registers as in-air, briefly. should
            // there be some minimum non-collision threshold time before claiming the avatar is in-air?
            PhysicsActor pa = agent.PhysicsActor;
            if ((flags & ScriptBaseClass.AGENT_WALKING) == 0 && 
                (flags & ScriptBaseClass.AGENT_SITTING) == 0 &&
                (flags & ScriptBaseClass.AGENT_CROUCHING) == 0 &&
                pa != null && !pa.IsColliding)
            {
                flags |= ScriptBaseClass.AGENT_IN_AIR;
            }

            //NotImplemented("llGetAgentInfo");

            return flags;
        }

        public string llGetAgentLanguage(string id)
        {
            UUID agent = new UUID();
            if (!UUID.TryParse(id, out agent)) return String.Empty;

            ScenePresence presence = World.GetScenePresence(agent);
            if (presence == null) return String.Empty;
            if (presence.IsChildAgent) return String.Empty;

            AgentPreferencesData prefs = presence.AgentPrefs;
            if (prefs == null) return String.Empty;
            if (!prefs.LanguageIsPublic) return String.Empty;

            return prefs.Language;
        }

        public void llAdjustSoundVolume(float volume)
        {
            m_host.AdjustSoundGain(volume);
            ScriptSleep(100);
        }

        public void llSetSoundQueueing(int queue)
        {
            if (queue == 0)
                m_host.SoundOptions &= (byte)~SoundFlags.Queue;
            else
                m_host.SoundOptions |= (byte)SoundFlags.Queue;
            m_host.ParentGroup.HasGroupChanged = true;
            m_host.ScheduleFullUpdate(PrimUpdateFlags.Sound);
        }

        public void llSetSoundRadius(float radius)
        {
            m_host.SoundRadius = radius;
            m_host.ScheduleFullUpdate(PrimUpdateFlags.Sound);
        }

        public string llKey2Name(string id)
        {
            UUID key = new UUID();
            if (UUID.TryParse(id, out key))
            {
                ScenePresence presence = World.GetScenePresence(key);

                if (presence != null)
                {
                    return presence.ControllingClient.Name;
                    //return presence.Name;
                }

                if (World.GetSceneObjectPart(key) != null)
                {
                    return World.GetSceneObjectPart(key).Name;
                }
            }
            return String.Empty;
        }

        public void SetPrimTextureAnim(SceneObjectPart part, int mode, int face, int sizex, int sizey, double start, double length, double rate)
        {
            Primitive.TextureAnimation pTexAnim = new Primitive.TextureAnimation();
            pTexAnim.Flags = (Primitive.TextureAnimMode)mode;

            //ALL_SIDES
            if (face == ScriptBaseClass.ALL_SIDES)
                face = 255;

            pTexAnim.Face = (uint)face;
            pTexAnim.Length = (float)length;
            pTexAnim.Rate = (float)rate;
            pTexAnim.SizeX = (uint)sizex;
            pTexAnim.SizeY = (uint)sizey;
            pTexAnim.Start = (float)start;

            part.AddTextureAnimation(pTexAnim);
            part.ScheduleFullUpdate(PrimUpdateFlags.TextureAnim);
            part.ParentGroup.HasGroupChanged = true;
        }

        public void llSetTextureAnim(int mode, int face, int sizex, int sizey, float start, float length, float rate)
        {
            
            SetPrimTextureAnim(m_host, mode, face, sizex, sizey, start, length, rate);
        }

        public void llSetLinkTextureAnim(int linknumber, int mode, int face, int sizex, int sizey, float start, float length, float rate)
        {

            var parts = GetLinkPrimsOnly(linknumber);

            foreach (SceneObjectPart part in parts)
                SetPrimTextureAnim(part, mode, face, sizex, sizey, start, length, rate);
        }

        public void llTriggerSoundLimited(string sound, float volume, LSL_Vector top_north_east,
                                          LSL_Vector bottom_south_west)
        {
            
            NotImplemented("llTriggerSoundLimited");
        }

        public void llEjectFromLand(string pest)
        {
            llTeleportAgentHome(pest);
        }

        private float H2RGB(float v1, float v2, float vH)
        {
            if (vH < 0) vH += 1;
            if (vH > 1) vH -= 1;
            if ((6f * vH) < 1) return (v1 + (v2 - v1) * 6f * vH);
            if ((2f * vH) < 1) return (v2);
            if ((3f * vH) < 2) return (v1 + (v2 - v1) * ((2f / 3f) - vH) * 6f);
            return v1;
        }

        private Vector3 HSL_TO_RGB(Vector3 input)
        {
            float H = input.X;
            float S = input.Y;
            float L = input.Z;

            if (S == 0) return new Vector3(L, L, L);

            float v1=0;
            float v2=0;

            if (L < 0.5) v2 = L * (1 + S);
            else v2 = (L + S) - (S * L);
            v1 = 2 * L - v2;

            return new Vector3(
                H2RGB(v1, v2, H + (1f / 3f)),
                H2RGB(v1, v2, H),
                H2RGB(v1, v2, H - (1f / 3f))
            );
        }

        private Vector3 HSV_TO_RGB(Vector3 input)
        {
            float H = input.X;
            float S = input.Y;
            float V = input.Z;

            if (S == 0)
            {
                return new Vector3(V, V, V);
            }

            float vH = H * 6f;
            if (vH == 6) vH = 0;
            int i = (int)vH;
            float v1 = V * (1 - S);
            float v2 = V * (1 - S * (vH - i));
            float v3 = V * (1 - S * (1 - (vH - i)));

            if (i == 0) return new Vector3(V, v3, v1);
            else if (i == 1) return new Vector3(v2, V, v1);
            else if (i == 2) return new Vector3(v1, V, v3);
            else if (i == 3) return new Vector3(v1, v2, V);
            else if (i == 4) return new Vector3(v3, v1, V);
            else return new Vector3(V, v1, v2);
        }

        private Vector3 RGB_TO_HSV(Vector3 input)
        {
            float R = input.X;
            float G = input.Y;
            float B = input.Z;

            float min = Math.Min(Math.Min(R, G), B);
            float max = Math.Max(Math.Max(R, G), B);
            float delta = max - min;

            float H = 0;
            float S = 0;
            float V = max;

            if (delta != 0)
            {
                S = delta / max;
                float vR = (((max - R) / 6f) + (max / 2f)) / delta;
                float vG = (((max - G) / 6f) + (max / 2f)) / delta;
                float vB = (((max - B) / 6f) + (max / 2f)) / delta;

                if (R == max) H = vB - vG;
                else if (G == max) H = (1f / 3f) + vR - vB;
                else if (B == max) H = (2f / 3f) + vG - vR;

                if (H < 0) H += 1f;
                if (H > 1) H -= 1f;
            }
            return new Vector3(H, S, V);
        }

        private Vector3 RGB_TO_HSL(Vector3 input)
        {
            float R = input.X;
            float G = input.Y;
            float B = input.Z;

            float min = Math.Min(Math.Min(R, G), B);
            float max = Math.Max(Math.Max(R, G), B);
            float delta = max - min;

            float H = 0;
            float S = 0;
            float L = (max + min) / 2f;
            if(delta != 0) {
                if(L < 0.5) S = delta / (max + min);
                else S = delta / (2 - max - min);

                float vR = (((max - R) / 6f) + (max / 2f)) / delta;
                float vG = (((max - G) / 6f) + (max / 2f)) / delta;
                float vB = (((max - B) / 6f) + (max / 2f)) / delta;

                if(max == R) H = vB - vG;
                else if(max == G) H = (1f / 3f) + vR - vB;
                else if(max == B) H = (2f / 3f) + vG - vR;

                if(H < 0) H += 1f;
                if(H > 1) H -= 1f;
            }
            return new Vector3(H, S, L);
        }

        public Vector3 iwColorConvert(Vector3 input, int color1, int color2)
        {
            if (color1 == color2) return input;
            
            if (color1 == 1) input = HSL_TO_RGB(input);
            else if (color1 == 2) input = HSV_TO_RGB(input);

            if (color2 == 1) return RGB_TO_HSL(input);
            else if (color2 == 2) return RGB_TO_HSV(input);
            return input;
        }

        public Vector3 iwNameToColor(string name)
        {
            Color c = Color.FromName(name.Replace(" ", null));
            return new Vector3(c.R/255f, c.G/255f, c.B/255f);
        }

        public int iwVerifyType(string str, int type)
        {
            switch (type)
            {
                case 0:  // No Type
                    foreach (var index in new int[] { 1, 2, 4, 5, 6 })
                    {
                        if (iwVerifyType(str, index) == 1) return index;
                    }
                    return 3;
                case 1:  // TYPE_INTEGER
                    int i;
                    return int.TryParse(str, out i) ? 1 : 0;
                case 2:  // TYPE_FLOAT
                    float f;
                    return float.TryParse(str, out f) ? 1 : 0;
                case 4:  // TYPE_KEY
                    UUID k;
                    return UUID.TryParse(str, out k) ? 1 : 0;
                case 5:  // TYPE_VECTOR
                    if (Regex.Matches(str, ",").Count != 2) return 0;
                    Vector3 v;
                    return Vector3.TryParse(str, out v) ? 1 : 0;
                case 6:  // TYPE_ROTATION
                    if (Regex.Matches(str, ",").Count != 3) return 0;
                    Quaternion q;
                    return Quaternion.TryParse(str, out q) ? 1 : 0;
                case 3:  // TYPE_STRING
                    return 1;
                default:
                    return -1;
            }
        }

        private Object AutoCastString(string str)
        {
            if(String.IsNullOrEmpty(str)) return str;

            int c = str.Length - str.Replace(".", String.Empty).Length;

            if (c == 1)
            {
                float f;
                if (float.TryParse(str, out f)) return f;
            }
            else if (c == 0)
            {
                int i;
                if (int.TryParse(str, out i)) return i;
            }

            if (str.StartsWith("<") && str.EndsWith(">"))
            {
                c = Regex.Matches(str, ",").Count;
                if (c == 2)
                {
                    Vector3 vec;
                    if (Vector3.TryParse(str, out vec)) return vec;
                }
                else if (c == 3)
                {
                    Quaternion quat;
                    if (Quaternion.TryParse(str, out quat)) return quat;
                }
            }
            
            if(str.Length == 36) {
                UUID k;
                if(UUID.TryParse(str, out k)) {
                    return k.ToString();
                }
            }

            return str;
        }

        private string iwParseString2ListSub(string str, int trimString, int doCapitalize)
        {
            if (String.IsNullOrEmpty(str)) return str;
            if (trimString != 0)
            {
                str = llStringTrim(str, trimString);
            }
            if (doCapitalize != 0)
            {
                switch (doCapitalize)
                {
                    case 1: //Upper Case
                        str = str.ToUpper();
                        break;
                    case 2: //Lower Case
                        str = str.ToLower();
                        break;
                    default: //Dummy Case
                        break;
                }
            }
            return str;
        }

        public LSL_List iwParseString2List(string str, LSL_List separators, LSL_List in_spacers, LSL_List args) {
            if(String.IsNullOrEmpty(str)) return new LSL_List();
            List<object> ret = new List<object>();
            List<object> spacers = new List<object>();

            //List<object> argList = new List<object>();
            //Handling for optional parser arguments
            bool keepNulls=false;
            int trimString = 0;
            int maxSplits = 0;
            int totalSplits = 0;
            int doCapitalize = 0;
            int autoCast = 0;
            if (args.Length > 0)
            {
                int argLen = args.Length;
                for (int i = 0; i < argLen; i += 2)
                {
                    if ((args.Data[i] is string) == false) continue;
                    string argName = args.GetLSLStringItem(i).ToLower();

                    switch (argName)
                    {
                        case "keepnulls":
                            keepNulls = (args.GetLSLIntegerItem(i+1) == 1);
                            break;
                        case "trimstrings":
                            trimString = args.GetLSLIntegerItem(i + 1);
                            if (trimString < 0 || trimString > 3)
                            {
                                trimString = 0;
                            }
                            break;
                        case "maxsplits":
                            maxSplits = args.GetLSLIntegerItem(i + 1);
                            if (maxSplits < 0)
                            {
                                maxSplits = 0;
                            }
                            break;
                        case "capitalize":
                            doCapitalize = args.GetLSLIntegerItem(i + 1);
                            if (doCapitalize < 0 || doCapitalize > 2)
                            {
                                doCapitalize = 0;
                            }
                            break;
                        case "autocast":
                            autoCast = args.GetLSLIntegerItem(i + 1);
                            if (autoCast < 1 || autoCast > 2) autoCast = 0;
                            break;
                        default:
                            break;
                    }
                }
            }

            if (in_spacers.Length > 0 && separators.Length > 0)
            {
                foreach (var spacer in in_spacers.Data)
                {
                    bool found = false;
                    foreach (var separator in separators.Data)
                    {
                        if (separator.ToString() == spacer.ToString())
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        spacers.Add(spacer);
                    }
                }
            }
            else if (in_spacers.Length > 0 && separators.Length == 0)
            {
                foreach (var in_spacer in in_spacers.Data)
                {
                    spacers.Add(in_spacer);
                }
            }
            else if (in_spacers.Length == 0 && separators.Length == 0)
            {
                return new LSL_List(ret);
            }

            object[] delimiters = new object[separators.Length + spacers.Count];
            separators.Data.CopyTo(delimiters, 0);
            spacers.CopyTo(delimiters, separators.Length);
            bool dfound = false;
            do
            {
                dfound = false;
                int cindex = -1;
                string cdeli = String.Empty;
                foreach (var delimiter in delimiters)
                {
                    int index = str.IndexOf(delimiter.ToString());
                    bool found = index != -1;
                    if (found && !String.IsNullOrEmpty(delimiter.ToString()))
                    {
                        if ((cindex > index) || (cindex == -1))
                        {
                            cindex = index;
                            cdeli = delimiter.ToString();
                        }
                        dfound = dfound || found;
                    }
                }
                if (cindex != -1)
                {
                    if (cindex > 0)
                    {
                        string temp = iwParseString2ListSub((string)(str.Substring(0, cindex)), trimString, doCapitalize);
                        if (!String.IsNullOrEmpty(temp) || keepNulls == true)
                        {
                            totalSplits++;
                            if(autoCast > 0) ret.Add(AutoCastString(temp));
                            else ret.Add(temp);
                        }
                    }
                    else if (cindex == 0 || keepNulls == true)
                    {
                        totalSplits++;
                        ret.Add(String.Empty);
                    }
                    if (maxSplits > 0 && totalSplits >= maxSplits)
                    {
                        str = str.Substring(cindex);
                        break;
                    }

                    // Cannot use spacers.Contains() because spacers may be either type String or LSLString
                    foreach (var spacer in spacers)
                    {
                        if (spacer.ToString() == cdeli)
                        {
                            string temp = iwParseString2ListSub((string)cdeli, trimString, doCapitalize);
                            if (!String.IsNullOrEmpty(temp) || keepNulls == true)
                            {
                                totalSplits++;
                                if (autoCast == 2) ret.Add(AutoCastString((string)cdeli));
                                else ret.Add((string)(cdeli));
                            }
                            break;
                        }
                    }
                    if (maxSplits > 0 && totalSplits >= maxSplits)
                    {
                        str = str.Substring(cindex + cdeli.Length);
                        break;
                    }

                    /*
                    if (maxSplits > 0 && totalSplits >= maxSplits)
                    {
                        
                        //int lenSplit = cindex + cdeli.Length + 1;
                        //if (lenSplit >= str.Length) str = str.Substring(lenSplit-1);
                        //else str = str.Substring(lenSplit);
                        //if (!String.IsNullOrEmpty(str.Length)) str = str.Substring(1, str.Length);
                        //else str = str.Substring(0, str.Length);
                        
                        str = str.Substring(cindex);
                        break;
                    }
                    */

                    str = str.Substring(cindex + cdeli.Length);
                    if (maxSplits > 0 && totalSplits >= maxSplits) break;
                }
            } while (dfound);

            str = iwParseString2ListSub(str, trimString, doCapitalize);

            if (!String.IsNullOrEmpty(str) || keepNulls == true)
            {
                if (autoCast > 1) ret.Add(AutoCastString((string)str));
                else ret.Add((string)(str));
            }

            return new LSL_List(ret);
        }

        public LSL_List llParseString2List(string str, LSL_List separators, LSL_List in_spacers)
        {

            List<object> ret = new List<object>();
            List<object> spacers = new List<object>();
            if (in_spacers.Length > 0 && separators.Length > 0)
            {
                foreach (var spacer in in_spacers.Data)
                {
                    bool found = false;
                    foreach (var separator  in separators.Data)
                    {
                        if (separator.ToString() == spacer.ToString())
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        spacers.Add(spacer);
                    }
                }
            }
            else if (in_spacers.Length > 0 && separators.Length == 0)
            {
                foreach (var in_spacer in in_spacers.Data)
                {
                    spacers.Add(in_spacer);
                }
            }
            else if (in_spacers.Length == 0 && separators.Length == 0)
            {
                return new LSL_List(ret);
            }

            object[] delimiters = new object[separators.Length + spacers.Count];
            separators.Data.CopyTo(delimiters, 0);
            spacers.CopyTo(delimiters, separators.Length);
            bool dfound = false;
            do
            {
                dfound = false;
                int cindex = -1;
                string cdeli = String.Empty;
                foreach (var delimiter in delimiters)
                {
                    int index = str.IndexOf(delimiter.ToString());
                    bool found = index != -1;
                    if (found && !String.IsNullOrEmpty(delimiter.ToString()))
                    {
                        if ((cindex > index) || (cindex == -1))
                        {
                            cindex = index;
                            cdeli = delimiter.ToString();
                        }
                        dfound = dfound || found;
                    }
                }
                if (cindex != -1)
                {
                    if (cindex > 0)
                    {
                        ret.Add((string)(str.Substring(0, cindex)));
                    }
                    // Cannot use spacers.Contains() because spacers may be either type String or LSLString
                    foreach (var spacer in spacers)
                    {
                        if (spacer.ToString() == cdeli)
                        {
                            ret.Add((string)(cdeli));
                            break;
                        }
                    }
                    str = str.Substring(cindex + cdeli.Length);
                }
            } while (dfound);
            if (!String.IsNullOrEmpty(str))
            {
                ret.Add((string)(str));
            }

            return new LSL_List(ret);
        }

        public int llOverMyLand(string id)
        {
            ILandObject parcel = null;
            UUID key = new UUID();
            if (UUID.TryParse(id, out key))
            {
                Vector3 pos;    // take a copy to avoid double calc
                ScenePresence presence = World.GetScenePresence(key);
                if (presence != null) // object is an avatar
                {
                    pos = presence.AbsolutePosition;
                    parcel = World.LandChannel.GetLandObject(pos.X, pos.Y);
                }
                else // object is not an avatar
                {
                    SceneObjectPart obj = World.GetSceneObjectPart(key);
                    if (obj != null)
                    {
                        pos = obj.AbsolutePosition;
                        parcel = World.LandChannel.GetLandObject(pos.X, pos.Y);
                    }
                }
            }

            return (parcel != null) && (m_host.OwnerID == parcel.landData.OwnerID) ? 1 : 0;
        }

        public string llGetLandOwnerAt(LSL_Vector pos)
        {
            UUID owner = UUID.Zero;
            ILandObject parcel = World.LandChannel.GetLandObject((float)pos.X, (float)pos.Y);
            if (parcel != null)
                owner = parcel.landData.OwnerID;
            return owner.ToString();
        }

        /// <summary>
        /// According to http://lslwiki.net/lslwiki/wakka.php?wakka=llGetAgentSize
        /// only the height of avatars vary and that says:- 
        /// Width (x) and depth (y) are constant. (0.45m and 0.6m respectively).
        /// </summary>
        public LSL_Vector llGetAgentSize(string id)
        {
            
            ScenePresence avatar = World.GetScenePresence((UUID)id);
            LSL_Vector agentSize;
            if (avatar == null || avatar.IsChildAgent) // Fail if not in the same region
            {
                agentSize = Vector3.Zero;
            }
            else
            {
                agentSize = new LSL_Vector(0.45f, 0.6f, avatar.Appearance.AvatarHeight);
            }
            return agentSize;
        }

        private bool HasMatchingGroup(UUID targetId, UUID groupId)
        {
            ScenePresence presence = World.GetScenePresence(targetId);
            if (presence != null)
            {
                // It's an avatar.
                if (presence.IsChildAgent) // Return flase for child agents
                    return false;
                return (presence.ControllingClient.ActiveGroupId == groupId);
            }
            // Not a user, let's try an object part.
            SceneObjectPart SOP = World.GetSceneObjectPart(targetId);
            if (SOP == null)
                return false;

            return (SOP.GroupID == groupId);
        }

        public int iwActiveGroup(string target, string group)
        {
            UUID targetId = new UUID();
            UUID groupId = new UUID();
            if (!UUID.TryParse(target, out targetId))
                return 0;
            if (!UUID.TryParse(group, out groupId))
                return 0;
            return HasMatchingGroup(targetId, groupId) ? 1 : 0;
        }

        public int llSameGroup(string target)
        {
            UUID targetId = new UUID();
            if (!UUID.TryParse(target, out targetId))
                return 0;
            if (targetId == UUID.Zero && m_host.GroupID == UUID.Zero) return 1;
            return HasMatchingGroup(targetId, m_host.GroupID) ? 1 : 0;
        }

        public void llUnSit(string id)
        {
            UUID key;
            if (UUID.TryParse(id, out key))
            {
                ScenePresence av = World.GetScenePresence(key);

                if (av != null)
                {
                    SceneObjectPart part = av.GetSitTargetPart();
                    if ((part != null) && (part.ParentGroup == m_host.ParentGroup))
                    {
                        // if the avatar is sitting on this object, then
                        // we can unsit them.  We don't want random scripts unsitting random people
                        // Lets avoid the popcorn avatar scenario.
                        av.StandUp(false, true);
                    }
                    else
                    {
                        // If the object owner also owns the parcel
                        // or
                        // if the land is group owned and the object is group owned by the same group
                        // or
                        // if the object is owned by a person with estate access.
                        Vector3 pos = av.AbsolutePosition;  // take a copy to avoid double calc
                        ILandObject parcel = World.LandChannel.GetLandObject(pos.X, pos.Y);
                        if (parcel != null)
                        {
                            Scene scene = m_host.ParentGroup.Scene;
                            if (m_host.ObjectOwner == parcel.landData.OwnerID ||
                                (m_host.OwnerID == m_host.GroupID && m_host.GroupID == parcel.landData.GroupID && parcel.landData.IsGroupOwned) ||
                                scene.Permissions.CanIssueEstateCommand(m_host.OwnerID, false) || World.Permissions.IsGod(m_host.OwnerID))
                            {
                                av.StandUp(false, true);
                            }
                        }
                    }
                }
            }
        }

        private LSL_Vector GroundVector(LSL_Vector offset, bool wantSlope)
        {
            Vector3 pos = m_host.AbsolutePosition + new Vector3(offset);
            LSL_Vector validated = ValidLocation(pos.X, pos.Y, pos.Z, false);

            // This uses the simple 3-point triangle normal, which will result in a
            // hinge effect as an object passes over the triangles in a terrain square.
            Vector3 result = World.Heightmap.CalculateNormalAt(validated.X, validated.Y);
            if (wantSlope)
                result = World.Heightmap.NormalToSlope(result);
            return new LSL_Vector(result);
        }

        public LSL_Vector llGroundSlope(LSL_Vector offset)
        {
            return GroundVector(offset, true);
        }

        public LSL_Vector llGroundNormal(LSL_Vector offset)
        {
//            return GroundVector(offset, false);
            // Instead of the above, let's return the falue as defined by the LSL wiki
            LSL_Vector x = llGroundSlope(offset);
            return new LSL_Vector(x.X, x.Y, 1.0f);
        }

        public LSL_Vector iwGroundSurfaceNormal(LSL_Vector offset)
        {
            Vector3 pos = m_host.AbsolutePosition + new Vector3(offset);
            LSL_Vector validated = ValidLocation(pos.X, pos.Y, pos.Z, false);
            Vector3 normal = World.Heightmap.Calculate4PointNormalAt(validated.X, validated.Y);
            return new LSL_Vector(normal);
        }

        public LSL_Vector llGroundContour(LSL_Vector offset)
        {
            
            LSL_Vector x = llGroundSlope(offset);
            return new LSL_Vector(-x.Y, x.X, 0.0f);
        }

        public int llGetAttached()
        {
            
            return (int)m_host.ParentGroup.RootPart.AttachmentPoint;
        }

        public int llGetFreeMemory()
        {
            return m_ScriptEngine.GetFreeMemory(m_itemID);
        }

        public int llGetUsedMemory()
        {
            return m_ScriptEngine.GetUsedMemory(m_itemID);
        }

        public int LIMIT_128K = 128 * 1024;
        public int llGetMemoryLimit()
        {
			return LIMIT_128K;
        }

        public int llSetMemoryLimit(int limit)
        {
			if (limit == LIMIT_128K)
                return 1;

            return 0;
        }

        public int llGetFreeURLs()
        {
            
            if (m_UrlModule != null)
                return (int)(m_UrlModule.GetFreeUrls());
            return (int)(0);
        }


        public string llGetRegionName()
        {
            
            return World.RegionInfo.RegionName;
        }

        public float llGetRegionTimeDilation()
        {
            
            return (float)World.TimeDilation;
        }

        /// <summary>
        /// Returns the value reported in the client Statistics window
        /// </summary>
        public float llGetRegionFPS()
        {
            
            return World.SimulatorFPS;
        }

        /* particle system rules should be coming into this routine as doubles, that is
        rule[0] should be an integer from this list and rule[1] should be the arg
        for the same integer. wiki.secondlife.com has most of this mapping, but some
        came from http://www.caligari-designs.com/p4u2

        We iterate through the list for 'Count' elements, incrementing by two for each
        iteration and set the members of Primitive.ParticleSystem, one at a time.
        */

        public enum PrimitiveRule : int
        {
            PSYS_PART_FLAGS = 0,
            PSYS_PART_START_COLOR = 1,
            PSYS_PART_START_ALPHA = 2,
            PSYS_PART_END_COLOR = 3,
            PSYS_PART_END_ALPHA = 4,
            PSYS_PART_START_SCALE = 5,
            PSYS_PART_END_SCALE = 6,
            PSYS_PART_MAX_AGE = 7,
            PSYS_SRC_ACCEL = 8,
            PSYS_SRC_PATTERN = 9,
            PSYS_SRC_INNERANGLE = 10,
            PSYS_SRC_OUTERANGLE = 11,
            PSYS_SRC_TEXTURE = 12,
            PSYS_SRC_BURST_RATE = 13,
            PSYS_SRC_BURST_PART_COUNT = 15,
            PSYS_SRC_BURST_RADIUS = 16,
            PSYS_SRC_BURST_SPEED_MIN = 17,
            PSYS_SRC_BURST_SPEED_MAX = 18,
            PSYS_SRC_MAX_AGE = 19,
            PSYS_SRC_TARGET_KEY = 20,
            PSYS_SRC_OMEGA = 21,
            PSYS_SRC_ANGLE_BEGIN = 22,
            PSYS_SRC_ANGLE_END = 23,
            PSYS_PART_BLEND_FUNC_SOURCE = 24,
            PSYS_PART_BLEND_FUNC_DEST = 25,
            PSYS_PART_START_GLOW = 26,
            PSYS_PART_END_GLOW = 27
        }

        internal Primitive.ParticleSystem.ParticleDataFlags ConvertUINTtoFlags(uint flags)
        {
            Primitive.ParticleSystem.ParticleDataFlags returnval = Primitive.ParticleSystem.ParticleDataFlags.None;

            return returnval;
        }

        private Primitive.ParticleSystem getNewParticleSystemWithSLDefaultValues()
        {
            Primitive.ParticleSystem ps = new Primitive.ParticleSystem();

            // TODO find out about the other defaults and add them here
            ps.PartStartColor = new Color4(1.0f, 1.0f, 1.0f, 1.0f);
            ps.PartEndColor = new Color4(1.0f, 1.0f, 1.0f, 1.0f);
            ps.PartStartScaleX = 1.0f;
            ps.PartStartScaleY = 1.0f;
            ps.PartEndScaleX = 1.0f;
            ps.PartEndScaleY = 1.0f;
            ps.BurstSpeedMin = 1.0f;
            ps.BurstSpeedMax = 1.0f;
            ps.BurstRate = 0.1f;
            ps.PartMaxAge = 10.0f;
            ps.BurstPartCount = 1;
            ps.BlendFuncSource = ScriptBaseClass.PSYS_PART_BF_SOURCE_ALPHA;
            ps.BlendFuncDest = ScriptBaseClass.PSYS_PART_BF_ONE_MINUS_SOURCE_ALPHA;
            ps.PartStartGlow = 0.0f;
            ps.PartEndGlow = 0.0f;
            return ps;
        }

        private float LimitFloat(float value, float limit, bool allowNegative)
        {
            if (value > limit)
                value = limit;

            if (allowNegative)
            {
                if (value < -limit)
                    value = -limit;
            }
            else
            {
                if (value < 0.0f)
                    value = 0.0f;
            }

            return value;
        }

        // scale start/end values are encoded into a single byte, unsigned.
        // Encoding uses 3 bits for the integer part, 5 bits for the fractional part.
        // Legal range: 0.000, 0.03125, 0.06250, ... to the largest possible value of 7.96875
        // This results in values from 0 to 255 when incoded this way. (7.96875 -> 0xFF)
        // We'll limit the values here since the OMV encoder does not. (8.0 -> 0x00)
        // See LLPartData::pack() in llpartdata.cpp in the viewer source.
        const float MAX_SRC_SCALE = 7.96875f; // See Mantis http://inworldz.com/mantis/view.php?id=955
        private float LimitScaleForByteEncoding(float value)
        {
            return LimitFloat(value, MAX_SRC_SCALE, false);
        }

        const float MIN_SRC_BURST_RATE = 0.05f; // See Mantis http://inworldz.com/mantis/view.php?id=955
        public void PrimParticleSystem(SceneObjectPart part, LSL_List rules)
        {
            
            if (rules.Length == 0)
            {
                part.RemoveParticleSystem();
                part.ParentGroup.HasGroupChanged = true;
            }
            else
            {
                Primitive.ParticleSystem prules = getNewParticleSystemWithSLDefaultValues();
                LSL_Vector tempv = new LSL_Vector();

                float tempf = 0;
                int tempi = 0;

                for (int i = 0; i < rules.Length; i += 2)
                {
                    switch ((int)rules.Data[i])
                    {
                        case (int)ScriptBaseClass.PSYS_PART_FLAGS:
                            prules.PartDataFlags = (Primitive.ParticleSystem.ParticleDataFlags)(uint)rules.GetLSLIntegerItem(i + 1);
                            break;

                        case (int)ScriptBaseClass.PSYS_PART_START_COLOR:
                            tempv = rules.GetVector3Item(i + 1);
                            prules.PartStartColor.R = (float)tempv.X;
                            prules.PartStartColor.G = (float)tempv.Y;
                            prules.PartStartColor.B = (float)tempv.Z;
                            break;

                        case (int)ScriptBaseClass.PSYS_PART_START_ALPHA:
                            tempf = (float)rules.GetLSLFloatItem(i + 1);
                            prules.PartStartColor.A = tempf;
                            break;

                        case (int)ScriptBaseClass.PSYS_PART_END_COLOR:
                            tempv = rules.GetVector3Item(i + 1);
                            prules.PartEndColor.R = (float)tempv.X;
                            prules.PartEndColor.G = (float)tempv.Y;
                            prules.PartEndColor.B = (float)tempv.Z;
                            break;

                        case (int)ScriptBaseClass.PSYS_PART_END_ALPHA:
                            tempf = (float)rules.GetLSLFloatItem(i + 1);
                            prules.PartEndColor.A = tempf;
                            break;

                        case (int)ScriptBaseClass.PSYS_PART_START_SCALE:
                            tempv = rules.GetVector3Item(i + 1);
                            prules.PartStartScaleX = LimitScaleForByteEncoding(tempv.X);
                            prules.PartStartScaleY = LimitScaleForByteEncoding(tempv.Y);
                            break;

                        case (int)ScriptBaseClass.PSYS_PART_END_SCALE:
                            tempv = rules.GetVector3Item(i + 1);
                            prules.PartEndScaleX = LimitScaleForByteEncoding(tempv.X);
                            prules.PartEndScaleY = LimitScaleForByteEncoding(tempv.Y);
                            break;

                        case (int)ScriptBaseClass.PSYS_PART_MAX_AGE:
                            tempf = (float)rules.GetLSLFloatItem(i + 1);
                            prules.PartMaxAge = LimitFloat(tempf, 30.0f, false);
                            break;

                        case (int)ScriptBaseClass.PSYS_SRC_ACCEL:
                            tempv = rules.GetVector3Item(i + 1);
                            prules.PartAcceleration.X = LimitFloat(tempv.X, 100.0f, true);
                            prules.PartAcceleration.Y = LimitFloat(tempv.Y, 100.0f, true);
                            prules.PartAcceleration.Z = LimitFloat(tempv.Z, 100.0f, true);
                            break;

                        case (int)ScriptBaseClass.PSYS_SRC_PATTERN:
                            tempi = (int)rules.GetLSLIntegerItem(i + 1);
                            prules.Pattern = (Primitive.ParticleSystem.SourcePattern)tempi;
                            break;

                        // PSYS_SRC_INNERANGLE and PSYS_SRC_ANGLE_BEGIN use the same variables. The
                        // PSYS_SRC_OUTERANGLE and PSYS_SRC_ANGLE_END also use the same variable. The
                        // client tells the difference between the two by looking at the 0x02 bit in
                        // the PartFlags variable.
                        case (int)ScriptBaseClass.PSYS_SRC_INNERANGLE:
                            tempf = (float)rules.GetLSLFloatItem(i + 1);
                            prules.InnerAngle = (float)tempf;
                            prules.PartFlags &= 0xFFFFFFFD; // Make sure new angle format is off.
                            break;

                        case (int)ScriptBaseClass.PSYS_SRC_OUTERANGLE:
                            tempf = (float)rules.GetLSLFloatItem(i + 1);
                            prules.OuterAngle = (float)tempf;
                            prules.PartFlags &= 0xFFFFFFFD; // Make sure new angle format is off.
                            break;

                        case (int)ScriptBaseClass.PSYS_SRC_TEXTURE:
                            prules.Texture = KeyOrName(rules.GetLSLStringItem(i + 1));
                            break;

                        case (int)ScriptBaseClass.PSYS_SRC_BURST_RATE:
                            tempf = (float)rules.GetLSLFloatItem(i + 1);
                            if (tempf < MIN_SRC_BURST_RATE)
                                tempf = MIN_SRC_BURST_RATE;
                            prules.BurstRate = (float)tempf;
                            break;

                        case (int)ScriptBaseClass.PSYS_SRC_BURST_PART_COUNT:
                            prules.BurstPartCount = (byte)(int)rules.GetLSLIntegerItem(i + 1);
                            break;

                        case (int)ScriptBaseClass.PSYS_SRC_BURST_RADIUS:
                            tempf = (float)rules.GetLSLFloatItem(i + 1);
                            prules.BurstRadius = LimitFloat(tempf, 50.0f, false);
                            break;

                        case (int)ScriptBaseClass.PSYS_SRC_BURST_SPEED_MIN:
                            tempf = (float)rules.GetLSLFloatItem(i + 1);
                            prules.BurstSpeedMin = (float)tempf;
                            break;

                        case (int)ScriptBaseClass.PSYS_SRC_BURST_SPEED_MAX:
                            tempf = (float)rules.GetLSLFloatItem(i + 1);
                            prules.BurstSpeedMax = (float)tempf;
                            break;

                        case (int)ScriptBaseClass.PSYS_SRC_MAX_AGE:
                            tempf = (float)rules.GetLSLFloatItem(i + 1);
                            prules.MaxAge = (float)tempf;
                            break;

                        case (int)ScriptBaseClass.PSYS_SRC_TARGET_KEY:
                            UUID key = UUID.Zero;
                            if (UUID.TryParse(rules.Data[i + 1].ToString(), out key))
                            {
                                prules.Target = key;
                            }
                            else
                            {
                                prules.Target = part.UUID;
                            }
                            break;

                        case (int)ScriptBaseClass.PSYS_SRC_OMEGA:
                            // AL: This is an assumption, since it is the only thing that would match.
                            tempv = rules.GetVector3Item(i + 1);
                            prules.AngularVelocity.X = (float)tempv.X;
                            prules.AngularVelocity.Y = (float)tempv.Y;
                            prules.AngularVelocity.Z = (float)tempv.Z;
                            break;

                        case (int)ScriptBaseClass.PSYS_SRC_ANGLE_BEGIN:
                            tempf = (float)rules.GetLSLFloatItem(i + 1);
                            prules.InnerAngle = (float)tempf;
                            prules.PartFlags |= 0x02; // Set new angle format.
                            break;

                        case (int)ScriptBaseClass.PSYS_SRC_ANGLE_END:
                            tempf = (float)rules.GetLSLFloatItem(i + 1);
                            prules.OuterAngle = (float)tempf;
                            prules.PartFlags |= 0x02; // Set new angle format.
                            break;

                        case (int)ScriptBaseClass.PSYS_PART_START_GLOW:
                            tempf = (float)rules.GetLSLFloatItem(i + 1);
                            prules.PartStartGlow = tempf;
                            break;

                        case (int)ScriptBaseClass.PSYS_PART_END_GLOW:
                            tempf = (float)rules.GetLSLFloatItem(i + 1);
                            prules.PartEndGlow = tempf;
                            break;

                        case (int)ScriptBaseClass.PSYS_PART_BLEND_FUNC_SOURCE:
                            tempi = (int)rules.GetLSLIntegerItem(i + 1);
                            prules.BlendFuncSource = (byte)tempi;
                            break;

                        case (int)ScriptBaseClass.PSYS_PART_BLEND_FUNC_DEST:
                            tempi = (int)rules.GetLSLIntegerItem(i + 1);
                            prules.BlendFuncDest = (byte)tempi;
                            break;
                    }

                }
                prules.CRC = 1;

                part.AddNewParticleSystem(prules);
                part.ParentGroup.HasGroupChanged = true;
            }
            part.ScheduleFullUpdate(PrimUpdateFlags.Particles);
        }

        public void llParticleSystem(LSL_List rules)
        {
            PrimParticleSystem(m_host, rules);
        }

        public void llLinkParticleSystem(int linknumber, LSL_List rules)
        {

            var parts = GetLinkPrimsOnly(linknumber);

            foreach (SceneObjectPart part in parts)
                PrimParticleSystem(part, rules);
        }

        public void llGroundRepel(float height, int water, float tau)
        {
            if (m_host.PhysActor != null)
            {
                PIDHoverFlag hoverType = PIDHoverFlag.Ground | PIDHoverFlag.Repel;
                if (water != 0)
                {
                    hoverType = PIDHoverFlag.Ground  | PIDHoverFlag.Water | PIDHoverFlag.Repel;
                }

                m_host.SetHoverHeight(height, hoverType, tau);
            }
        }

        private UUID GetTaskInventoryItem(SceneObjectPart part, string name)
        {
            lock (part.TaskInventory)
            {
                foreach (KeyValuePair<UUID, TaskInventoryItem> inv in part.TaskInventory)
                {
                    if (inv.Value.Name == name)
                        return inv.Key;
                }
            }

            return UUID.Zero;
        }

        private int _GiveLinkInventoryList(SceneObjectPart part, string destination, string category, LSL_List inventory, bool includeRC, out bool needsDelay)
        {
            needsDelay = true;

            UUID destID;
            if (!UUID.TryParse(destination, out destID))
            {
                return ScriptBaseClass.IW_DELIVER_BADKEY;
            }

            if (IsScriptMuted(destID))
            {
                m_log.InfoFormat("[LSL]: Not offering inventory list from muted {0} to {1}", m_host.ParentGroup.UUID, destID);
                return ScriptBaseClass.IW_DELIVER_MUTED; // recipient has sender muted
            }

            List<UUID> itemList = null;
            // check if destination is a part (much faster than checking if it's an avatar)
            SceneObjectPart destPart = m_host.ParentGroup.Scene.GetSceneObjectPart(destID);
            if (destPart == null)
                itemList = new List<UUID>();
            foreach (Object item in inventory.Data)
            {
                UUID itemID = UUID.Zero;
                if (!UUID.TryParse(item.ToString(), out itemID))
                    itemID = GetTaskInventoryItem(part, item.ToString());

                // If being called from iwDeliver...(), we need to 
                // abort any delivery with missing items and return error code.
                if (includeRC && (itemID == UUID.Zero))
                    return ScriptBaseClass.IW_DELIVER_ITEM; // item is missing

                if (destPart != null)   // give to another prim
                    World.MoveTaskInventoryItem(destID, part, itemID);
                else   // give to a user inventory
                    itemList.Add(itemID);
            }

            if (destPart != null)
            {   // Items were given to another prim above.
                needsDelay = false;
                return ScriptBaseClass.IW_DELIVER_OK;
            }

            if (itemList.Count == 0)
            {   // Nothing to give.
                return ScriptBaseClass.IW_DELIVER_NONE;
            }

            string reason;
            UUID folderID = m_ScriptEngine.World.MoveTaskInventoryItems(destID, category, part, itemList, out reason);
            if (folderID == UUID.Zero)
                return DeliverReasonToResult(reason);

            byte dialog = (byte)InstantMessageDialog.TaskInventoryOffered;
            byte[] bucket = new byte[1];
            bucket[0] = (byte)AssetType.Folder;
            SceneObjectPart rootPart = part.ParentGroup.RootPart;
            Vector3 pos = rootPart.AbsolutePosition;
            string URL = Util.LocationURL(World.RegionInfo.RegionName, pos);

            GridInstantMessage msg = new GridInstantMessage(World,
                    rootPart.OwnerID, rootPart.Name, destID,
                    dialog, false, "'"+category+"'  ( "+URL+" )",
                    folderID, true, rootPart.AbsolutePosition,
                    bucket);

            if (m_TransferModule != null)
                m_TransferModule.SendInstantMessage(msg, delegate(bool success) { });
            return ScriptBaseClass.IW_DELIVER_OK;
        }

        private void GiveInventoryList(int linknumber, string destination, string category, LSL_List inventory, int delay, bool includeRC)
        {
            bool needsDelay = true;
            int rc = ScriptBaseClass.IW_DELIVER_PRIM;   // part not found
            try
            {
                var parts = GetLinkPrimsOnly(linknumber);
                foreach (SceneObjectPart part in parts)
                {
                    rc = _GiveLinkInventoryList(part, destination, category, inventory, includeRC, out needsDelay);
                    if (rc != ScriptBaseClass.IW_DELIVER_NONE)
                        return; // give results from the first matching prim only
                }
            }
            finally
            {
                // C# cannot handle: includeRC ? rc : null
                object result = null;
                if (includeRC) result = rc;
                if (!needsDelay) delay = 0;
                m_ScriptEngine.SysReturn(m_itemID, result, delay);
            }
        }
        public void llGiveInventoryList(string destination, string category, LSL_List inventory)
        {
            GiveInventoryList(ScriptBaseClass.LINK_THIS, destination, category, inventory, 3000, false);
        }
        public void iwGiveLinkInventoryList(int linknumber, string destination, string category, LSL_List inventory)
        {
            GiveInventoryList(linknumber, destination, category, inventory, 3000, false);
        }
        public void iwDeliverInventoryList(int linknumber, string destination, string category, LSL_List inventory)
        {
            GiveInventoryList(linknumber, destination, category, inventory, 100, true);
        }

        public void llSetVehicleType(int type)
        {
            if (m_host.ParentGroup != null)
            {
                if (!m_host.ParentGroup.IsDeleted)
                {
                    if (!VehicleTypeValidator.IsValid(type))
                    {
                        LSLError("llSetVehicleType(" + type.ToString() + ") is not valid.");
                        return;
                    }

                    m_host.ParentGroup.RootPart.SetVehicleType((VehicleType)type);
                }
            }

            PhySleep();
        }

        public void llSetVehicleFloatParam(int param, float value)
        {
            if (m_host.ParentGroup != null)
            {
                if (!m_host.ParentGroup.IsDeleted)
                {
                    // Formal float params
                    if (!float.IsNaN(value) && FloatParamsValidator.IsValid(param))
                    {
                        m_host.ParentGroup.RootPart.SetVehicleFloatParam((FloatParams)param, value);
                    }

                    // The float param setter permits setting vector parameters, just because type punning is exotic and cool.
                    else if (!float.IsNaN(value) && VectorParamsValidator.IsValid(param))
                    {
                        m_host.ParentGroup.RootPart.SetVehicleVectorParam((VectorParams)param, new OpenMetaverse.Vector3(value, value, value));
                    }

                    else
                    {
                        LSLError("llSetVehicleFloatParam(" + param.ToString() + ", " + value.ToString() + ") is not valid.");
                        return;
                    }
                }
            }

            PhySleep();
        }

        public void llSetVehicleVectorParam(int param, LSL_Vector vec)
        {
            if (m_host.ParentGroup != null)
            {
                if (!m_host.ParentGroup.IsDeleted)
                {
                    if (float.IsNaN(vec.X) || float.IsNaN(vec.Y) || float.IsNaN(vec.Z) || !VectorParamsValidator.IsValid(param))
                    {
                        LSLError("llSetVehicleVectorParam(" + param.ToString() + ", " + vec.ToString() + ") is not valid.");
                        return;
                    }

                    m_host.ParentGroup.RootPart.SetVehicleVectorParam((VectorParams)param, vec);
                }
            }

            PhySleep();
        }

        public void llSetVehicleRotationParam(int param, LSL_Rotation rot)
        {
            if (m_host.ParentGroup != null)
            {
                if (!m_host.ParentGroup.IsDeleted)
                {
                    if (float.IsNaN(rot.X) || float.IsNaN(rot.Y) || float.IsNaN(rot.Z) || float.IsNaN(rot.W) ||  !RotationParamsValidator.IsValid(param))
                    {
                        LSLError("llSetVehicleRotationParam(" + param.ToString() + ", " + rot.ToString() + ") is not valid.");
                        return;
                    }

                    m_host.ParentGroup.RootPart.SetVehicleRotationParam((RotationParams)param,
                        Rot2Quaternion(rot));
                }
            }
        }

        public void llSetVehicleFlags(int flags)
        {
            if (m_host.ParentGroup != null)
            {
                if (!m_host.ParentGroup.IsDeleted)
                {
                    m_host.ParentGroup.RootPart.SetVehicleFlags((VehicleFlags)flags);
                }

                // Not implemented
                if ((flags & (int)VehicleFlags.CameraDecoupled) != 0)
                    LSLError("VEHICLE_FLAG_CAMERA_DECOUPLED is not implemented.");
            }
        }

        public void llRemoveVehicleFlags(int flags)
        {
            if (m_host.ParentGroup != null)
            {
                if (!m_host.ParentGroup.IsDeleted)
                {
                    m_host.ParentGroup.RootPart.RemoveVehicleFlags((VehicleFlags)flags);
                }
            }
        }

        public void llLinkSitTarget(int linknumber, LSL_Vector offset, LSL_Rotation rot)
        {
            Vector3 sitPos = new Vector3((float)offset.X, (float)offset.Y, (float)offset.Z);
            Quaternion sitRot = Rot2Quaternion(rot);
            bool isActive = (sitPos != Vector3.Zero) || (sitRot != Quaternion.Identity);

            var parts = GetLinkPrimsOnly(linknumber);
            foreach (SceneObjectPart part in parts)
            {
                part.SetSitTarget(isActive, sitPos, sitRot, true);
            }
        }

        public void llSitTarget(LSL_Vector offset, LSL_Rotation rot)
        {
            llLinkSitTarget(m_host.LinkNum, offset, rot);
        }

        private string AvatarOnSitTarget(int linknumber, bool IncludeSitTargetOnly)
        {
            var parts = GetLinkPrimsOnly(linknumber);   // only matching parts, not all parts
            UUID seatedAvatar = UUID.Zero;

            // We'll spin through the "list" but return the values from the first match with a sit target.
            // Should only be one match, but if the script specified LINK_ALL_OTHERS or another wildcard,
            // then this function will return the first match.
            foreach (SceneObjectPart part in parts) {
                SitTargetInfo sitInfo = part.ParentGroup.SitTargetForPart(part.UUID);
                if (IncludeSitTargetOnly)
                {
                    if (sitInfo.IsActive && sitInfo.HasSitter)
                    {
                        seatedAvatar = sitInfo.Sitter.UUID;
                        break;
                    }
                }
                else
                {
                    part.ForEachSittingAvatar((ScenePresence sp) =>
                    {
                        if (seatedAvatar == UUID.Zero)
                            seatedAvatar = sp.UUID;
                    });
                    if (seatedAvatar != UUID.Zero)
                        break;
                }
            }
            return seatedAvatar.ToString();
        }
        public string llAvatarOnSitTarget()
        {
            return (AvatarOnSitTarget(m_host.LinkNum, true));
        }
        public string llAvatarOnLinkSitTarget(int linknum)
        {
            return (AvatarOnSitTarget(linknum, true));
        }
        // Same as above but does not require a non-zero sit target
        public string iwAvatarOnLink(int linknum)
        {
            return (AvatarOnSitTarget(linknum, false));
        }

        public string iwGetLastOwner()
        {
            return m_host.LastOwnerID.ToString();
        }

        public void llAddToLandPassList(string avatar, float hours)
        {
            UUID key;
            Vector3 landpos = m_host.AbsolutePosition;
            ILandObject landObject = World.LandChannel.GetLandObject(landpos.X, landpos.Y);
            if (World.Permissions.CanEditParcel(m_host.OwnerID, landObject, GroupPowers.LandManageAllowed))
            {
                ParcelManager.ParcelAccessEntry entry = new ParcelManager.ParcelAccessEntry();
                if (UUID.TryParse(avatar, out key))
                {
                    entry.AgentID = key;
                    entry.Flags = AccessList.Access;
                    entry.Time = DateTime.Now.AddHours(hours);
                    landObject.landData.ParcelAccessList.Add(entry);
                }
            }
            
            ScriptSleep(100);
        }

        public void llSetTouchText(string text)
        {
            
            m_host.TouchName = text;
        }

        public void llSetSitText(string text)
        {
            
            m_host.SitName = text;
        }

        public void llSetLinkCamera(int linknumber, LSL_Vector eyeOffset, LSL_Vector cameraAt)
        {
            var parts = GetLinkPrimsOnly(linknumber);
            foreach (SceneObjectPart part in parts)
            {
                Vector3 localPos = part.IsRootPart() ? Vector3.Zero : part.OffsetPosition;
                part.SetCameraEyeOffset(new Vector3(localPos + eyeOffset));
                part.SetCameraAtOffset(new Vector3(localPos + cameraAt));
            }
        }

        public void llSetCameraEyeOffset(LSL_Vector offset)
        {
            
            m_host.SetCameraEyeOffset(new Vector3((float)offset.X, (float)offset.Y, (float)offset.Z));
        }

        public void llSetCameraAtOffset(LSL_Vector offset)
        {
            
            m_host.SetCameraAtOffset(new Vector3((float)offset.X, (float)offset.Y, (float)offset.Z));
        }

        public string llDumpList2String(LSL_List src, string seperator)
        {
            
            if (src.Length == 0)
            {
                return String.Empty;
            }
            StringBuilder ret = new StringBuilder();
            for (int index = 0; index < src.Data.Length; ++index)
            {
                ret.Append(src.GetLSLStringItem(index));
                ret.Append(seperator);
            }
            ret.Length -= seperator.Length;
            return ret.ToString();
        }

        public int llScriptDanger(LSL_Vector pos)
        {
            
            bool result = World.ScriptDanger(m_host.LocalId, new Vector3((float)pos.X, (float)pos.Y, (float)pos.Z));
            if (result)
            {
                return 1;
            }
            else
            {
                return 0;
            }

        }

        public void llDialog(string avatar, string message, LSL_List buttons, int chat_channel)
        {
            IDialogModule dm = World.RequestModuleInterface<IDialogModule>();

            if (dm == null)
                return;

            
            UUID av = new UUID();
            if (!UUID.TryParse(avatar, out av))
            {
                LSLError("First parameter to llDialog needs to be a key");
                return;
            }
            if (buttons.Length > 12)
            {
                LSLError("No more than 12 buttons can be shown");
                return;
            }

            string[] buts;
            if (buttons.Length == 0)
            {
                buts = new string[1];
                buts[0] = "OK";
            }
            else
            {
                buts = new string[buttons.Length];

                for (int i = 0; i < buttons.Length; i++)
                {
                    if (String.IsNullOrEmpty(buttons.Data[i].ToString()))
                    {
                        LSLError("button label cannot be blank");
                        return;
                    }
                    if (buttons.Data[i].ToString().Length > 24)
                    {
                        LSLError("button label cannot be longer than 24 characters");
                        return;
                    }
                    buts[i] = buttons.Data[i].ToString();
                }
            }

            dm.SendDialogToUser(
                av, m_host.Name, m_host.UUID, m_host.OwnerID,
                message, new UUID("00000000-0000-2222-3333-100000001000"), chat_channel, buts);

            ScriptSleep(1000);
        }

        public void llVolumeDetect(int detect)
        {
            if (m_host.ParentGroup != null)
            {
                if (!m_host.ParentGroup.IsDeleted)
                {
                    m_host.ParentGroup.RootPart.ScriptSetVolumeDetect(detect != 0);

                    if (detect == 1)
                    {
                        _thisScript.ScriptState.MiscAttributes[(int)VM.RuntimeState.MiscAttr.VolumeDetect]
                                = new object[1] { 1 };
                    }
                    else
                    {
                        _thisScript.ScriptState.MiscAttributes.Remove((int)VM.RuntimeState.MiscAttr.VolumeDetect);
                    }
                }
            }
        }

        /// <summary>
        /// This is a depecated function so this just replicates the result of
        /// invoking it in SL
        /// </summary>

        public void llRemoteLoadScript(string target, string name, int running, int start_param)
        {
            
            // Report an error as it does in SL
            ScriptShoutError("Deprecated. Please use llRemoteLoadScriptPin instead.");
            // ScriptSleep(3000);
        }

        public void llSetRemoteScriptAccessPin(int pin)
        {
            
            m_host.ScriptAccessPin = pin;
        }

		//Functions same as llRemoteLoadScriptPin, but returns an integer flag to indicate success,
		//  instead of calling ScriptShoutError
		public int iwRemoteLoadScriptPin(string target, string name, int pin, int running, int start_param) {
			return RemoteLoadScriptPin(target, name, pin, running, start_param, false);
		}

		public void llRemoteLoadScriptPin(string target, string name, int pin, int running, int start_param) {
			RemoteLoadScriptPin (target, name, pin, running, start_param, true);
		}

		//A proxy for llRemoteLoadScriptPin and iwRemoteLoadScriptPin
		//Uses integer constants as return codes to indicate success or failure
		//  IW_REMOTELOAD_SUCCESS =  1	Function succeeded
		//  IW_REMOTELOAD_FAILURE =  0	Function failed, for various reasons
		//  IW_REMOTELOAD_BAD_PIN = -1	The pin did not match the target's pin
		//  IW_REMOTELOAD_NO_PIN  = -2	The target did not have a pin set
		public int RemoteLoadScriptPin(string target, string name, int pin, int running, int start_param, bool doShout)
        {
			if (pin == 0) {
				ScriptShoutError ("llRemoteLoadScriptPin: PIN cannot be zero.");
				ScriptSleep(3000);
				return 0;
			}
            
            bool found = false;
            UUID destId = UUID.Zero;
            UUID srcId = UUID.Zero;

            if (!UUID.TryParse(target, out destId))
            {
				llSay(0, "Could not parse key " + target);
                return 0;
            }

            // Target must be a different prim than the one containing the script, owned by the same user.
            SceneObjectPart part = m_host.ParentGroup.Scene.GetSceneObjectPart(destId);
            if (part == null)
            {
				ScriptShoutError("llRemoteLoadScriptPin: Target prim ["+destId.ToString()+"] not found.");
                return 0;
            }
            if (m_host.OwnerID != part.OwnerID)
            {
				ScriptShoutError("llRemoteLoadScriptPin: Target prim ownership does not match.");
                return 0;
            }
            if (m_host.UUID == destId)
            {
				ScriptShoutError("llRemoteLoadScriptPin: Target prim cannot be the source prim.");
                return 0;
            }

            // copy the first script found with this inventory name
            lock (m_host.TaskInventory)
            {
                foreach (KeyValuePair<UUID, TaskInventoryItem> inv in m_host.TaskInventory)
                {
                    if (inv.Value.Name == name)
                    {
                        // make sure the object is a script
                        if (10 == inv.Value.Type)
                        {
                            found = true;
                            srcId = inv.Key;
                            break;
                        }
                    }
                }
            }

            if (!found)
            {
				llSay(0, "Could not find script " + name);
                return 0;
            }

            // the rest of the permission checks are done in RezScript, so check the pin there as well
            string result = World.RezScript(srcId, m_host, destId, pin, running, start_param);
			int ret = 1;
            if (!String.IsNullOrEmpty(result))
            {
                // validation error updating script
				if (result == "PIN") {    // special case for public error (let's not match the silly SL "illegal" text)
					if (doShout)
						ShoutError ("llRemoteLoadScriptPin: Script update denied - PIN mismatch.");
					ret = -1;
				} else if (result == "NO PIN") {
					if(doShout)
						ShoutError ("llRemoteLoadScriptPin: Script update denied - PIN not set.");
					ret = -2;
				} else {
					ScriptShoutError ("llRemoteLoadScriptPin: " + result);
					ret = 0;
				}
            }
            // this will cause the delay even if the script pin or permissions were wrong - seems ok
            ScriptSleep(3000);
			return ret;
        }

        public void llOpenRemoteDataChannel()
        {
            
            IXMLRPC xmlrpcMod = m_ScriptEngine.World.RequestModuleInterface<IXMLRPC>();
            if (xmlrpcMod.IsEnabled())
            {
                UUID channelID = xmlrpcMod.OpenXMLRPCChannel(m_localID, m_itemID, UUID.Zero);
                IXmlRpcRouter xmlRpcRouter = m_ScriptEngine.World.RequestModuleInterface<IXmlRpcRouter>();
                if (xmlRpcRouter != null)
                    xmlRpcRouter.RegisterNewReceiver(m_ScriptEngine.ScriptModule, channelID, m_host.UUID, m_itemID, "http://" + System.Environment.MachineName + ":" + xmlrpcMod.Port.ToString() + "/");
                object[] resobj = new object[] { (int)(1), channelID.ToString(), UUID.Zero.ToString(), String.Empty, (int)(0), String.Empty };
                m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                        "remote_data", resobj,
                        new DetectParams[0]));
            }
            
            ScriptSleep(1000);
        }

        public string llSendRemoteData(string channel, string dest, int idata, string sdata)
        {
            
            IXMLRPC xmlrpcMod = m_ScriptEngine.World.RequestModuleInterface<IXMLRPC>();
            ScriptSleep(3000);
            return (xmlrpcMod.SendRemoteData(m_localID, m_itemID, channel, dest, idata, sdata)).ToString();
        }

        public void llRemoteDataReply(string channel, string message_id, string sdata, int idata)
        {
            
            IXMLRPC xmlrpcMod = m_ScriptEngine.World.RequestModuleInterface<IXMLRPC>();
            xmlrpcMod.RemoteDataReply(channel, message_id, sdata, idata);
            ScriptSleep(3000);
        }

        public void llCloseRemoteDataChannel(string channel)
        {
            
            IXMLRPC xmlrpcMod = m_ScriptEngine.World.RequestModuleInterface<IXMLRPC>();
            xmlrpcMod.CloseXMLRPCChannel((UUID)channel);
            ScriptSleep(1000);
        }

        public string llMD5String(string src, int nonce)
        {
            return Util.Md5Hash(src + ":" + nonce.ToString());
        }

        public string llSHA1String(string src)
        {
            
            return Util.SHA1Hash(src).ToLower();
        }

        public string iwSHA256String(string src)
        {
            return OpenMetaverse.Utils.SHA256String(src).ToLower();
        }

        private ObjectShapePacket.ObjectDataBlock SetPrimitiveBlockShapeParams(SceneObjectPart part, int holeshape, LSL_Vector cut, float hollow, LSL_Vector twist)
        {
            ObjectShapePacket.ObjectDataBlock shapeBlock = new ObjectShapePacket.ObjectDataBlock();

            if (holeshape != (int)ScriptBaseClass.PRIM_HOLE_DEFAULT &&
                holeshape != (int)ScriptBaseClass.PRIM_HOLE_CIRCLE &&
                holeshape != (int)ScriptBaseClass.PRIM_HOLE_SQUARE &&
                holeshape != (int)ScriptBaseClass.PRIM_HOLE_TRIANGLE)
            {
                holeshape = (int)ScriptBaseClass.PRIM_HOLE_DEFAULT;
            }
            shapeBlock.ProfileCurve = (byte)holeshape;
            if (cut.X < 0f)
            {
                cut.X = 0f;
            }
            if (cut.X > 1f)
            {
                cut.X = 1f;
            }
            if (cut.Y < 0f)
            {
                cut.Y = 0f;
            }
            if (cut.Y > 1f)
            {
                cut.Y = 1f;
            }
            if (cut.Y - cut.X < 0.02f)
            {
                cut.X = cut.Y - 0.02f;
            }
            shapeBlock.ProfileBegin = (ushort)(50000 * cut.X);
            shapeBlock.ProfileEnd = (ushort)(50000 * (1 - cut.Y));
            if (hollow < 0f)
            {
                hollow = 0f;
            }
            if (hollow > 0.99f)
            {
                hollow = 0.99f;
            }
            shapeBlock.ProfileHollow = (ushort)(50000 * hollow);
            if (twist.X < -1.0f)
            {
                twist.X = -1.0f;
            }
            if (twist.X > 1.0f)
            {
                twist.X = 1.0f;
            }
            if (twist.Y < -1.0f)
            {
                twist.Y = -1.0f;
            }
            if (twist.Y > 1.0f)
            {
                twist.Y = 1.0f;
            }
            shapeBlock.PathTwistBegin = (sbyte)(100 * twist.X);
            shapeBlock.PathTwist = (sbyte)(100 * twist.Y);

            shapeBlock.ObjectLocalID = part.LocalId;

            // retain pathcurve
            shapeBlock.PathCurve = part.Shape.PathCurve;

            return shapeBlock;
        }

        private void SetPrimitiveShapeParamsCommon(SceneObjectPart part, int holeshape, LSL_Vector cut, float hollow, LSL_Vector twist, LSL_Vector taper_b, LSL_Vector topshear, LSL_Vector slice, byte fudge)
        {
            ObjectShapePacket.ObjectDataBlock shapeBlock;

            shapeBlock = SetPrimitiveBlockShapeParams(part, holeshape, cut, hollow, twist);

            shapeBlock.ProfileCurve += fudge;

            if (taper_b.X < 0f)
            {
                taper_b.X = 0f;
            }
            if (taper_b.X > 2f)
            {
                taper_b.X = 2f;
            }
            if (taper_b.Y < 0f)
            {
                taper_b.Y = 0f;
            }
            if (taper_b.Y > 2f)
            {
                taper_b.Y = 2f;
            }
            shapeBlock.PathScaleX = (byte)(100 * (2.0 - taper_b.X));
            shapeBlock.PathScaleY = (byte)(100 * (2.0 - taper_b.Y));
            if (topshear.X < -0.5f)
            {
                topshear.X = -0.5f;
            }
            if (topshear.X > 0.5f)
            {
                topshear.X = 0.5f;
            }
            if (topshear.Y < -0.5f)
            {
                topshear.Y = -0.5f;
            }
            if (topshear.Y > 0.5f)
            {
                topshear.Y = 0.5f;
            }
            shapeBlock.PathShearX = (byte)Primitive.PackPathShear(topshear.X);
            shapeBlock.PathShearY = (byte)Primitive.PackPathShear(topshear.Y);

            if (slice.X < 0f)
            {
                slice.X = 0.0f;
            }
            if (slice.X > 0.98f)
            {
                slice.X = 0.98f;
            }
            if (slice.Y < 0.02f)
            {
                slice.Y = 0.02f;
            }
            if (slice.Y > 1.0f)
            {
                slice.Y = 1.0f;
            }
            if (slice.Y - slice.X < 0.02f)
            {
                slice.X = slice.Y - 0.02f;
            }
            shapeBlock.PathBegin = (ushort)(50000 * slice.X);
            shapeBlock.PathEnd = (ushort)(50000 * (1 - slice.Y));

            part.Shape.SculptEntry = false;
            part.UpdateShape(shapeBlock);
        }

        private void SetPrimitiveShapeParamsSphere(SceneObjectPart part, int holeshape, LSL_Vector cut, float hollow, LSL_Vector twist, LSL_Vector dimple, byte fudge)
        {
            ObjectShapePacket.ObjectDataBlock shapeBlock;

            shapeBlock = SetPrimitiveBlockShapeParams(part, holeshape, cut, hollow, twist);

            // profile/path swapped for a sphere
            shapeBlock.PathBegin = shapeBlock.ProfileBegin;
            shapeBlock.PathEnd = shapeBlock.ProfileEnd;

            shapeBlock.ProfileCurve += fudge;

            shapeBlock.PathScaleX = 100;
            shapeBlock.PathScaleY = 100;

            if (dimple.X < 0f)
            {
                dimple.X = 0f;
            }
            if (dimple.X > 1f)
            {
                dimple.X = 1f;
            }
            if (dimple.Y < 0f)
            {
                dimple.Y = 0f;
            }
            if (dimple.Y > 1f)
            {
                dimple.Y = 1f;
            }
            if (dimple.Y - cut.X < 0.02f)
            {
                dimple.X = cut.Y - 0.02f;
            }
            shapeBlock.ProfileBegin = (ushort)(50000 * dimple.X);
            shapeBlock.ProfileEnd = (ushort)(50000 * (1 - dimple.Y));

            part.Shape.SculptEntry = false;

            part.UpdateShape(shapeBlock);
        }

        private void SetPrimitiveShapeParamsTorus(SceneObjectPart part, int holeshape, LSL_Vector cut, float hollow, LSL_Vector twist, LSL_Vector holesize, LSL_Vector topshear, LSL_Vector profilecut, LSL_Vector taper_a, float revolutions, float radiusoffset, float skew, byte fudge)
        {
            ObjectShapePacket.ObjectDataBlock shapeBlock;

            shapeBlock = SetPrimitiveBlockShapeParams(part, holeshape, cut, hollow, twist);

            shapeBlock.ProfileCurve += fudge;

            // profile/path swapped for a torrus, tube, ring
            shapeBlock.PathBegin = shapeBlock.ProfileBegin;
            shapeBlock.PathEnd = shapeBlock.ProfileEnd;

            if (holesize.X < 0.01f)
            {
                holesize.X = 0.01f;
            }
            if (holesize.X > 1f)
            {
                holesize.X = 1f;
            }
            if (holesize.Y < 0.05f)
            {
                holesize.Y = 0.05f;
            }
            if (holesize.Y > 0.5f)
            {
                holesize.Y = 0.5f;
            }
            shapeBlock.PathScaleX = (byte)(100 * (2 - holesize.X));
            shapeBlock.PathScaleY = (byte)(100 * (2 - holesize.Y));
            if (topshear.X < -0.5f)
            {
                topshear.X = -0.5f;
            }
            if (topshear.X > 0.5f)
            {
                topshear.X = 0.5f;
            }
            if (topshear.Y < -0.5f)
            {
                topshear.Y = -0.5f;
            }
            if (topshear.Y > 0.5f)
            {
                topshear.Y = 0.5f;
            }
            shapeBlock.PathShearX = (byte)Primitive.PackPathShear(topshear.X);
            shapeBlock.PathShearY = (byte)Primitive.PackPathShear(topshear.Y);
            if (profilecut.X < 0f)
            {
                profilecut.X = 0f;
            }
            if (profilecut.X > 1f)
            {
                profilecut.X = 1f;
            }
            if (profilecut.Y < 0f)
            {
                profilecut.Y = 0f;
            }
            if (profilecut.Y > 1f)
            {
                profilecut.Y = 1f;
            }
            if (profilecut.Y - profilecut.X < 0.05f)
            {
                profilecut.X = profilecut.Y - 0.05f;
                if (profilecut.X < 0.0f)
                {
                    profilecut.X = 0.0f;
                    profilecut.Y = 0.05f;
                }
            }
            shapeBlock.ProfileBegin = (ushort)(50000 * profilecut.X);
            shapeBlock.ProfileEnd = (ushort)(50000 * (1 - profilecut.Y));
            if (taper_a.X < -1f)
            {
                taper_a.X = -1f;
            }
            if (taper_a.X > 1f)
            {
                taper_a.X = 1f;
            }
            if (taper_a.Y < -1f)
            {
                taper_a.Y = -1f;
            }
            if (taper_a.Y > 1f)
            {
                taper_a.Y = 1f;
            }
            shapeBlock.PathTaperX = (sbyte)(100 * taper_a.X);
            shapeBlock.PathTaperY = (sbyte)(100 * taper_a.Y);
            if (revolutions < 1f)
            {
                revolutions = 1f;
            }
            if (revolutions > 4f)
            {
                revolutions = 4f;
            }
            shapeBlock.PathRevolutions = (byte)(66.666667 * (revolutions - 1.0));
            // limits on radiusoffset depend on revolutions and hole size (how?) seems like the maximum range is 0 to 1
            if (radiusoffset < 0f)
            {
                radiusoffset = 0f;
            }
            if (radiusoffset > 1f)
            {
                radiusoffset = 1f;
            }
            shapeBlock.PathRadiusOffset = (sbyte)(100 * radiusoffset);
            if (skew < -0.95f)
            {
                skew = -0.95f;
            }
            if (skew > 0.95f)
            {
                skew = 0.95f;
            }
            shapeBlock.PathSkew = (sbyte)(100 * skew);

            part.Shape.SculptEntry = false;

            part.UpdateShape(shapeBlock);
        }

        private void SetPrimitiveShapeParamsSculpt(SceneObjectPart part, string map, int typeBits)
        {
            ObjectShapePacket.ObjectDataBlock shapeBlock = new ObjectShapePacket.ObjectDataBlock();
            UUID sculptId;
            int sculptTypeMask = 0x0F;
            int sculptType = typeBits & sculptTypeMask;
            int sculptOptions = typeBits & ~sculptTypeMask;

            if (!UUID.TryParse(map, out sculptId))
            {
                sculptId = InventoryKey(map, (int)AssetType.Texture);
            }

            if (sculptId == UUID.Zero)
                return;

            shapeBlock.ObjectLocalID = part.LocalId;
            shapeBlock.PathScaleX = 100;
            shapeBlock.PathScaleY = 150;

            if (sculptType != (int)ScriptBaseClass.PRIM_SCULPT_TYPE_CYLINDER &&
                sculptType != (int)ScriptBaseClass.PRIM_SCULPT_TYPE_PLANE &&
                sculptType != (int)ScriptBaseClass.PRIM_SCULPT_TYPE_SPHERE &&
                sculptType != (int)ScriptBaseClass.PRIM_SCULPT_TYPE_TORUS)
            {
                // default
                sculptType = (int)ScriptBaseClass.PRIM_SCULPT_TYPE_SPHERE;
                typeBits = sculptOptions | sculptType;
            }

            // retain pathcurve
            shapeBlock.PathCurve = part.Shape.PathCurve;

            part.Shape.SetSculptProperties((byte)typeBits, sculptId);
            part.Shape.SculptEntry = true;
            part.UpdateShape(shapeBlock);
        }

        public void llSetPrimitiveParams(LSL_List rules)
        {
            SetPrimParams(m_host.LinkNum, rules, null);
            this.ScriptSleep(200);
        }

        public void llSetLinkPrimitiveParamsFast(int linknumber, LSL_List rules)
        {
            SetPrimParams(linknumber, rules, null);
        }

        public void llSetLinkPrimitiveParams(int linknumber, LSL_List rules)
        {
            llSetLinkPrimitiveParamsFast(linknumber, rules);
            this.ScriptSleep(200);
        }

        public readonly LSL_Vector DEFAULT_SLICE = new LSL_Vector(0.0f, 1.0f, 0.0f);

        private void SetPrimParams(int linknumber, LSL_List rules, SceneObjectPart newPart)
        {
            IReadOnlyCollection<object> links;

            if (newPart == null) // normal SetPrimParams
                links = GetLinkParts(linknumber, true);
            else                // iwRezPart call
            {
                // force it to use newPart
                List<SceneObjectPart> initial = new List<SceneObjectPart>();
                initial.Add(newPart);
                links = initial;
            }

            int idx = 0;
            while (idx < rules.Length)
            {
                int code = rules.GetLSLIntegerItem(idx++);
                int remain = rules.Length - idx;
                int face;
                LSL_Vector v;
                LSL_Rotation r;

                if (code == (int)ScriptBaseClass.PRIM_LINK_TARGET)
                {
                    if (remain < 1)
                        return;
                    linknumber = (int)rules.GetLSLIntegerItem(idx++);
                    remain = rules.Length - idx;

                    links = GetLinkParts(linknumber, true);
                }
                
                switch (code)
                {
                    case (int)ScriptBaseClass.PRIM_POSITION:
                    case (int)ScriptBaseClass.PRIM_POS_LOCAL:   // same as PRIM_POSITION on a SET operation
                        if (remain < 1)
                            return;

                        v = rules.GetVector3Item(idx++);
                        foreach (var o in links)
                        {
                            if (o is ScenePresence)     // avatar-as-a-prim 'part'?
                            {
                                var sp = o as ScenePresence;
                                sp.SetAvatarAsAPrimMode();
                                sp.UpdateSeatedPosition(v);
                            }
                            else
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                SetPos(part, v, true);
                            }
                        }
                        break;
                    case (int)ScriptBaseClass.PRIM_SIZE:
                        if (remain < 1)
                            return;

                        v = rules.GetVector3Item(idx++);
                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                SetScale(part, v);
                            }
                        }
                        break;
                    case (int)ScriptBaseClass.PRIM_ROT_LOCAL:
                        if (remain < 1)
                            return;

                        LSL_Rotation lq = rules.GetQuaternionItem(idx++);
                        foreach (var o in links)
                        {
                            if (o is ScenePresence)     // avatar-as-a-prim 'part'?
                            {
                                var sp = o as ScenePresence;
                                sp.SetAvatarAsAPrimMode();
                                sp.Rotation = Rot2Quaternion(lq);
                            }
                            else
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                SetRot(part, Rot2Quaternion(lq));
                            }
                        }
                        break;
                    case (int)ScriptBaseClass.PRIM_PHYSICS_SHAPE_TYPE:
                        if (remain < 1)
                            return;

                        int pshape_type = rules.GetLSLIntegerItem(idx++);

                        if (Enum.IsDefined(typeof(PhysicsShapeType), (byte)pshape_type))
                        {
                            foreach (var o in links)
                            {
                                if (o is SceneObjectPart)
                                {
                                    var part = o as SceneObjectPart;
                                    part.Shape.PreferredPhysicsShape = (PhysicsShapeType)pshape_type;
                                    part.PhysicsShapeChanged();
                                }
                            }
                        }

                        break;
                    case (int)ScriptBaseClass.PRIM_ROTATION:
                        if (remain < 1)
                            return;

                        LSL_Rotation q = rules.GetQuaternionItem(idx++);
                        // try to let this work as in SL...
                        foreach (var o in links)
                        {
                            if (o is ScenePresence)     // avatar-as-a-prim 'part'?
                            {
                                var sp = o as ScenePresence;
                                sp.SetAvatarAsAPrimMode();
                                sp.Rotation = m_host.ParentGroup.RootPart.RotationOffset * Rot2Quaternion(q);
                            }
                            else
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                if (part.IsRootPart())
                                {
                                    // special case: If we are root, rotate complete SOG to new rotation
                                    SetRot(part, Rot2Quaternion(q));
                                }
                                else
                                {
                                    // we are a child. The rotation values will be set to the one of root modified by rot, as in SL. Don't ask.
                                    SceneObjectGroup group = part.ParentGroup;
                                    if (group != null) // a bit paranoid, maybe
                                    {
                                        SceneObjectPart rootPart = group.RootPart;
                                        if (rootPart != null) // again, better safe than sorry
                                        {
                                            SetRot(part, rootPart.RotationOffset * Rot2Quaternion(q));
                                        }
                                    }
                                }
                            }
                        }

                        break;

                    case (int)ScriptBaseClass.PRIM_OMEGA:
                        if (remain < 3)
                            return;
                        LSL_Vector axis = rules.GetVector3Item(idx++);
                        double spinrate = (float)rules.GetLSLFloatItem(idx++);
                        double gain = (float)rules.GetLSLFloatItem(idx++);

                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                PrimTargetOmega(part, axis, spinrate, gain);
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_TYPE:
                        if (remain < 3)
                            return;

                        code = (int)rules.GetLSLIntegerItem(idx++);

                        remain = rules.Length - idx;
                        float hollow;
                        LSL_Vector twist;
                        LSL_Vector taper_b;
                        LSL_Vector topshear;
                        float revolutions;
                        float radiusoffset;
                        float skew;
                        LSL_Vector holesize;
                        LSL_Vector profilecut;

                        switch (code)
                        {
                            case (int)ScriptBaseClass.PRIM_TYPE_BOX:
                                if (remain < 6)
                                    return;

                                face = (int)rules.GetLSLIntegerItem(idx++);
                                v = rules.GetVector3Item(idx++); // cut
                                hollow = (float)rules.GetLSLFloatItem(idx++);
                                twist = rules.GetVector3Item(idx++);
                                taper_b = rules.GetVector3Item(idx++);
                                topshear = rules.GetVector3Item(idx++);
                                foreach (var o in links)
                                {
                                    if (o is SceneObjectPart)
                                    {
                                        var part = o as SceneObjectPart;
                                        part.Shape.PathCurve = (byte)Extrusion.Straight;
                                        SetPrimitiveShapeParamsCommon(part, face, v, hollow, twist, taper_b, topshear, DEFAULT_SLICE, 1);
                                    }
                                }
                                break;

                            case (int)ScriptBaseClass.PRIM_TYPE_CYLINDER:
                                if (remain < 6)
                                    return;

                                face = (int)rules.GetLSLIntegerItem(idx++); // holeshape
                                v = rules.GetVector3Item(idx++); // cut
                                hollow = (float)rules.GetLSLFloatItem(idx++);
                                twist = rules.GetVector3Item(idx++);
                                taper_b = rules.GetVector3Item(idx++);
                                topshear = rules.GetVector3Item(idx++);
                                foreach (var o in links)
                                {
                                    if (o is SceneObjectPart)
                                    {
                                        var part = o as SceneObjectPart;
                                        part.Shape.ProfileShape = ProfileShape.Circle;
                                        part.Shape.PathCurve = (byte)Extrusion.Straight;
                                        SetPrimitiveShapeParamsCommon(part, face, v, hollow, twist, taper_b, topshear, DEFAULT_SLICE, 0);
                                    }
                                }
                                break;

                            case (int)ScriptBaseClass.PRIM_TYPE_PRISM:
                                if (remain < 6)
                                    return;

                                face = (int)rules.GetLSLIntegerItem(idx++); // holeshape
                                v = rules.GetVector3Item(idx++); //cut
                                hollow = (float)rules.GetLSLFloatItem(idx++);
                                twist = rules.GetVector3Item(idx++);
                                taper_b = rules.GetVector3Item(idx++);
                                topshear = rules.GetVector3Item(idx++);
                                foreach (var o in links)
                                {
                                    if (o is SceneObjectPart)
                                    {
                                        var part = o as SceneObjectPart;
                                        part.Shape.PathCurve = (byte)Extrusion.Straight;
                                        SetPrimitiveShapeParamsCommon(part, face, v, hollow, twist, taper_b, topshear, DEFAULT_SLICE, 3);
                                    }
                                }
                                break;

                            case (int)ScriptBaseClass.PRIM_TYPE_SPHERE:
                                if (remain < 5)
                                    return;

                                face = (int)rules.GetLSLIntegerItem(idx++); // holeshape
                                v = rules.GetVector3Item(idx++); // cut
                                hollow = (float)rules.GetLSLFloatItem(idx++);
                                twist = rules.GetVector3Item(idx++);
                                taper_b = rules.GetVector3Item(idx++); // dimple
                                foreach (var o in links)
                                {
                                    if (o is SceneObjectPart)
                                    {
                                        var part = o as SceneObjectPart;
                                        part.Shape.PathCurve = (byte)Extrusion.Curve1;
                                        SetPrimitiveShapeParamsSphere(part, face, v, hollow, twist, taper_b, 5);
                                    }
                                }
                                break;

                            case (int)ScriptBaseClass.PRIM_TYPE_TORUS:
                                if (remain < 11)
                                    return;

                                face = (int)rules.GetLSLIntegerItem(idx++); // holeshape
                                v = rules.GetVector3Item(idx++); //cut
                                hollow = (float)rules.GetLSLFloatItem(idx++);
                                twist = rules.GetVector3Item(idx++);
                                holesize = rules.GetVector3Item(idx++);
                                topshear = rules.GetVector3Item(idx++);
                                profilecut = rules.GetVector3Item(idx++);
                                taper_b = rules.GetVector3Item(idx++); // taper_a
                                revolutions = (float)rules.GetLSLFloatItem(idx++);
                                radiusoffset = (float)rules.GetLSLFloatItem(idx++);
                                skew = (float)rules.GetLSLFloatItem(idx++);
                                foreach (var o in links)
                                {
                                    if (o is SceneObjectPart)
                                    {
                                        var part = o as SceneObjectPart;
                                        part.Shape.PathCurve = (byte)Extrusion.Curve1;
                                        SetPrimitiveShapeParamsTorus(part, face, v, hollow, twist, holesize, topshear, profilecut, taper_b, revolutions, radiusoffset, skew, 0);
                                    }
                                }
                                break;

                            case (int)ScriptBaseClass.PRIM_TYPE_TUBE:
                                if (remain < 11)
                                    return;

                                face = (int)rules.GetLSLIntegerItem(idx++); // holeshape
                                v = rules.GetVector3Item(idx++); //cut
                                hollow = (float)rules.GetLSLFloatItem(idx++);
                                twist = rules.GetVector3Item(idx++);
                                holesize = rules.GetVector3Item(idx++);
                                topshear = rules.GetVector3Item(idx++);
                                profilecut = rules.GetVector3Item(idx++);
                                taper_b = rules.GetVector3Item(idx++); // taper_a
                                revolutions = (float)rules.GetLSLFloatItem(idx++);
                                radiusoffset = (float)rules.GetLSLFloatItem(idx++);
                                skew = (float)rules.GetLSLFloatItem(idx++);
                                foreach (var o in links)
                                {
                                    if (o is SceneObjectPart)
                                    {
                                        var part = o as SceneObjectPart;
                                        part.Shape.PathCurve = (byte)Extrusion.Curve1;
                                        SetPrimitiveShapeParamsTorus(part, face, v, hollow, twist, holesize, topshear, profilecut, taper_b, revolutions, radiusoffset, skew, 1);
                                    }
                                }
                                break;

                            case (int)ScriptBaseClass.PRIM_TYPE_RING:
                                if (remain < 11)
                                    return;

                                face = (int)rules.GetLSLIntegerItem(idx++); // holeshape
                                v = rules.GetVector3Item(idx++); //cut
                                hollow = (float)rules.GetLSLFloatItem(idx++);
                                twist = rules.GetVector3Item(idx++);
                                holesize = rules.GetVector3Item(idx++);
                                topshear = rules.GetVector3Item(idx++);
                                profilecut = rules.GetVector3Item(idx++);
                                taper_b = rules.GetVector3Item(idx++); // taper_a
                                revolutions = (float)rules.GetLSLFloatItem(idx++);
                                radiusoffset = (float)rules.GetLSLFloatItem(idx++);
                                skew = (float)rules.GetLSLFloatItem(idx++);
                                foreach (var o in links)
                                {
                                    if (o is SceneObjectPart)
                                    {
                                        var part = o as SceneObjectPart;
                                        part.Shape.PathCurve = (byte)Extrusion.Curve1;
                                        SetPrimitiveShapeParamsTorus(part, face, v, hollow, twist, holesize, topshear, profilecut, taper_b, revolutions, radiusoffset, skew, 3);
                                    }
                                }
                                break;

                            case (int)ScriptBaseClass.PRIM_TYPE_SCULPT:
                                if (remain < 2)
                                    return;

                                string map = rules.Data[idx++].ToString();
                                face = (int)rules.GetLSLIntegerItem(idx++); // type
                                foreach (var o in links)
                                {
                                    if (o is SceneObjectPart)
                                    {
                                        var part = o as SceneObjectPart;
                                        part.Shape.PathCurve = (byte)Extrusion.Curve1;
                                        SetPrimitiveShapeParamsSculpt(part, map, face);
                                    }
                                }
                                break;
                        }

                        break;  // PRIM_TYPE

                    case (int)ScriptBaseClass.PRIM_SLICE:
                        if (remain < 1)
                            return;
                        LSL_Vector slice = rules.GetVector3Item(idx++);

                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                PrimitiveBaseShape Shape = part.Shape;
                                int primType = getScriptPrimType(part.Shape);

                                face = (int)(Shape.HollowShape);
                                v = new LSL_Vector(Shape.ProfileBegin / 50000.0f, 1 - Shape.ProfileEnd / 50000.0f, 0);
                                hollow = (float)(Shape.ProfileHollow / 50000.0);
                                twist = new LSL_Vector(Shape.PathTwistBegin / 100.0f, Shape.PathTwist / 100.0f, 0);
                                taper_b = new LSL_Vector(1 - (Shape.PathScaleX / 100.0f - 1), 1 - (Shape.PathScaleY / 100.0f - 1), 0);
                                topshear = new LSL_Vector(Primitive.UnpackPathShear((sbyte)Shape.PathShearX), Primitive.UnpackPathShear((sbyte)Shape.PathShearY), 0);

                                switch (primType)
                                {
                                    case ScriptBaseClass.PRIM_TYPE_BOX:
                                        part.Shape.PathCurve = (byte)Extrusion.Straight;
                                        SetPrimitiveShapeParamsCommon(part, face, v, hollow, twist, taper_b, topshear, slice, 1);
                                        break;
                                    case ScriptBaseClass.PRIM_TYPE_CYLINDER:
                                        part.Shape.ProfileShape = ProfileShape.Circle;
                                        part.Shape.PathCurve = (byte)Extrusion.Straight;
                                        SetPrimitiveShapeParamsCommon(part, face, v, hollow, twist, taper_b, topshear, slice, 0);
                                        break;
                                    case ScriptBaseClass.PRIM_TYPE_PRISM:
                                        part.Shape.PathCurve = (byte)Extrusion.Straight;
                                        SetPrimitiveShapeParamsCommon(part, face, v, hollow, twist, taper_b, topshear, slice, 3);
                                        break;
                                }
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_TEXTURE:
                        if (remain < 5)
                            return;

                        face = (int)rules.GetLSLIntegerItem(idx++);
                        string tex = rules.Data[idx++].ToString();
                        LSL_Vector repeats = rules.GetVector3Item(idx++);
                        LSL_Vector offsets = rules.GetVector3Item(idx++);
                        double rotation = (double)rules.GetLSLFloatItem(idx++);

                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                SetTexture(part, tex, face);
                                ScaleTexture(part, repeats.X, repeats.Y, face);
                                OffsetTexture(part, offsets.X, offsets.Y, face);
                                RotateTexture(part, rotation, face);
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.IW_PRIM_ALPHA:
                        if (remain < 2)
                            return;

                        face = (int)rules.GetLSLIntegerItem(idx++);
                        double alphaValue = (double)rules.GetLSLFloatItem(idx++);

                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                SetAlpha(part, alphaValue, face);
                            }
                        }

                        break;

                    case (int)ScriptBaseClass.PRIM_COLOR:
                        if (remain < 3)
                            return;

                        face = (int)rules.GetLSLIntegerItem(idx++);
                        LSL_Vector color = rules.GetVector3Item(idx++);
                        double alpha = (double)rules.GetLSLFloatItem(idx++);

                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                SetColor(part, color, face);
                                SetAlpha(part, alpha, face);
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_FLEXIBLE:
                        if (remain < 7)
                            return;

                        bool flexi = rules.GetLSLIntegerItem(idx++) == 1;
                        int softness = rules.GetLSLIntegerItem(idx++);
                        float gravity = (float)rules.GetLSLFloatItem(idx++);
                        float friction = (float)rules.GetLSLFloatItem(idx++);
                        float wind = (float)rules.GetLSLFloatItem(idx++);
                        float tension = (float)rules.GetLSLFloatItem(idx++);
                        LSL_Vector force = rules.GetVector3Item(idx++);

                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                SetFlexi(part, flexi, softness, gravity, friction, wind, tension, force);
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_POINT_LIGHT:
                        if (remain < 5)
                            return;
                        bool light = rules.GetLSLIntegerItem(idx++) == 1;
                        LSL_Vector lightcolor = rules.GetVector3Item(idx++);
                        float intensity = (float)rules.GetLSLFloatItem(idx++);
                        float radius = (float)rules.GetLSLFloatItem(idx++);
                        float falloff = (float)rules.GetLSLFloatItem(idx++);
                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                SetPointLight(part, light, lightcolor, intensity, radius, falloff);
                            }
                        }
                        break;

                    case ScriptBaseClass.IW_PRIM_PROJECTOR:
                        if (remain < 5)
                            return;
                        bool enabled = rules.GetLSLIntegerItem(idx++) == 1;
                        UUID texID = KeyOrName(rules.Data[idx++].ToString());
                        float field_of_view = rules.GetLSLFloatItem(idx++);
                        float ford = rules.GetLSLFloatItem(idx++);
                        float ambience = rules.GetLSLFloatItem(idx++);

                        if (texID == UUID.Zero)
                        {
                            ScriptShoutError("The second argument of IW_PRIM_PROJECTOR must not be NULL_KEY.");
                        }
                        else
                        {
                            foreach (var o in links)
                            {
                                if (o is SceneObjectPart)
                                {
                                    var part = o as SceneObjectPart;
                                    PrimitiveBaseShape shape = part.Shape;
                                    shape.ProjectionEntry = enabled;
                                    shape.ProjectionTextureUUID = texID;
                                    shape.ProjectionFOV = field_of_view;
                                    shape.ProjectionFocus = ford;
                                    shape.ProjectionAmbiance = ambience;
                                    part.ParentGroup.HasGroupChanged = true;
                                    part.ScheduleFullUpdate(PrimUpdateFlags.FindBest);
                                }
                            }
                        }
                        break;

                    case ScriptBaseClass.IW_PRIM_PROJECTOR_ENABLED:
                        if (remain < 1)
                            return;
                        bool projector = rules.GetLSLIntegerItem(idx++) == 1;
                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                PrimitiveBaseShape shape = part.Shape;
                                shape.ProjectionEntry = projector;
                                part.ParentGroup.HasGroupChanged = true;
                                part.ScheduleFullUpdate(PrimUpdateFlags.Shape);
                            }
                        }
                        break;

                    case ScriptBaseClass.IW_PRIM_PROJECTOR_TEXTURE:
                        if (remain < 1)
                            return;
                        tex = rules.Data[idx++].ToString();
                        UUID textureID = KeyOrName(tex);

                        if (textureID == UUID.Zero)
                        {
                            ScriptShoutError("The argument of IW_PRIM_PROJECTOR_TEXTURE must not be NULL_KEY.");
                        }
                        else
                        {
                            foreach (var o in links)
                            {
                                if (o is SceneObjectPart)
                                {
                                    var part = o as SceneObjectPart;
                                    PrimitiveBaseShape shape = part.Shape;
                                    shape.ProjectionTextureUUID = textureID;
                                    part.ParentGroup.HasGroupChanged = true;
                                    part.ScheduleFullUpdate(PrimUpdateFlags.Shape);
                                }
                            }
                        }
                        break;

                    case ScriptBaseClass.IW_PRIM_PROJECTOR_FOV:
                        if (remain < 1)
                            return;
                        float fov = rules.GetLSLFloatItem(idx++);
                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                PrimitiveBaseShape shape = part.Shape;
                                shape.ProjectionFOV = fov;
                                part.ParentGroup.HasGroupChanged = true;
                                part.ScheduleFullUpdate(PrimUpdateFlags.Shape);
                            }
                        }
                        break;

                    case ScriptBaseClass.IW_PRIM_PROJECTOR_FOCUS:
                        if (remain < 1)
                            return;
                        float focus = rules.GetLSLFloatItem(idx++);
                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                PrimitiveBaseShape shape = part.Shape;
                                shape.ProjectionFocus = focus;
                                part.ParentGroup.HasGroupChanged = true;
                                part.ScheduleFullUpdate(PrimUpdateFlags.Shape);
                            }
                        }
                        break;

                    case ScriptBaseClass.IW_PRIM_PROJECTOR_AMBIENCE:
                        if (remain < 1)
                            return;
                        float amb = rules.GetLSLFloatItem(idx++);
                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                PrimitiveBaseShape shape = part.Shape;
                                shape.ProjectionAmbiance = amb;
                                part.ParentGroup.HasGroupChanged = true;
                                part.ScheduleFullUpdate(PrimUpdateFlags.Shape);
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_GLOW:
                        if (remain < 2)
                            return;
                        face = rules.GetLSLIntegerItem(idx++);
                        float glow = (float)rules.GetLSLFloatItem(idx++);
                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                SetGlow(part, face, glow);
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_BUMP_SHINY:
                        if (remain < 3)
                            return;
                        face = (int)rules.GetLSLIntegerItem(idx++);
                        int shiny = (int)rules.GetLSLIntegerItem(idx++);
                        Bumpiness bump = (Bumpiness)Convert.ToByte((int)rules.GetLSLIntegerItem(idx++));
                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                SetShiny(part, face, shiny, bump);
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_FULLBRIGHT:
                        if (remain < 2)
                            return;
                        face = rules.GetLSLIntegerItem(idx++);
                        bool st = rules.GetLSLIntegerItem(idx++) == 1;
                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                SetFullBright(part, face, st);
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_MATERIAL:
                        if (remain < 1)
                            return;
                        int mat = rules.GetLSLIntegerItem(idx++);
                        if (mat < 0 || mat > 7)
                            return;

                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                part.Material = Convert.ToByte(mat);
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_PHANTOM:
                        if (remain < 1)
                            return;

                        string ph = rules.Data[idx++].ToString();
                        bool phantom;

                        if (ph.Equals("1"))
                            phantom = true;
                        else
                            phantom = false;

                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                //no matter how many parts are selected, this physics change
                                //is applied to the group, so dont apply in a loop
                                part.ParentGroup.ScriptSetPhantomStatus(phantom);
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_PHYSICS:
                        if (remain < 1)
                            return;
                        string phy = rules.Data[idx++].ToString();
                        bool physics;

                        if (phy.Equals("1"))
                            physics = true;
                        else
                            physics = false;

                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                //no matter how many parts are selected, this physics change
                                //is applied to the group, so dont apply in a loop
                                part.ParentGroup.ScriptSetPhysicsStatus(physics);
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_TEMP_ON_REZ:
                        if (remain < 1)
                            return;
                        string temp = rules.Data[idx++].ToString();
                        bool tempOnRez;

                        if (temp.Equals("1"))
                            tempOnRez = true;
                        else
                            tempOnRez = false;

                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                part.ScriptSetTemporaryStatus(tempOnRez);
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_TEXGEN:
                        if (remain < 2)
                            return;
                        //face,type
                        face = rules.GetLSLIntegerItem(idx++);
                        int style = rules.GetLSLIntegerItem(idx++);
                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                SetTexGen(part, face, style);
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_TEXT:
                        if (remain < 3)
                            return;
                        string primtext = rules.Data[idx++].ToString();
                        LSL_Vector primcolor = rules.GetVector3Item(idx++);
                        double primalpha = (double)rules.GetLSLFloatItem(idx++);
                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                PrimSetText(part, primtext, primcolor, primalpha);
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_NAME:
                        if (remain < 1)
                            return;
                        string primname = rules.Data[idx++].ToString();
                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                part.Name = LimitLength(primname, MAX_OBJ_NAME);
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_DESC:
                        if (remain < 1)
                            return;
                        string primdesc = rules.Data[idx++].ToString();
                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                part.Description = LimitLength(primdesc, MAX_OBJ_DESC);
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_SPECULAR:
                        if (remain < 8)
                            return;

                        face = rules.GetLSLIntegerItem(idx++);
                        string specular_tex = rules.Data[idx++].ToString();

                        // First see if its the name of a texture in inventory
                        UUID SpecularTextureID = InventoryKey(specular_tex, (int)AssetType.Texture);

                        // If we got UUID.Zero back its not a texture name.  Try and parse it as a UUID
                        // If that fails we'll return an error (should shout one according to the specs)
                        if (SpecularTextureID == UUID.Zero)
                        {
                            if (UUID.TryParse(specular_tex, out SpecularTextureID) == false)
                                return;

                            // UUID.Zero here is valid.  It means to clear the normal settings.
                            specular_tex = SpecularTextureID.ToString();
                        }

                        LSL_Vector specular_repeats = rules.GetVector3Item(idx++);
                        LSL_Vector specular_offsets = rules.GetVector3Item(idx++);
                        float specular_rotation = rules.GetLSLFloatItem(idx++);
                        LSL_Vector specular_color = rules.GetVector3Item(idx++);
                        int specular_glossiness = rules.GetLSLIntegerItem(idx++);
                        int specular_environment = rules.GetLSLIntegerItem(idx++);

                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                if (face == ScriptBaseClass.ALL_SIDES)
                                {
                                    for (face = 0; face < part.GetNumberOfSides(); face++)
                                    {
                                        SetRenderMaterialSpecularData(part, face, specular_tex, specular_repeats, specular_offsets, specular_rotation, specular_color, specular_glossiness, specular_environment);
                                    }
                                }
                                else
                                {
                                    if (face >= 0 && face < part.GetNumberOfSides())
                                    {
                                        SetRenderMaterialSpecularData(part, face, specular_tex, specular_repeats, specular_offsets, specular_rotation, specular_color, specular_glossiness, specular_environment);
                                    }
                                }
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_NORMAL:
                        if (remain < 5)
                            return;

                        face = rules.GetLSLIntegerItem(idx++);
                        string normal_tex = rules.Data[idx++].ToString();

                        // First see if its the name of a texture in inventory
                        UUID NormalTextureID = InventoryKey(normal_tex, (int)AssetType.Texture);

                        // If we got UUID.Zero back its not a texture name.  Try and parse it as a UUID
                        // If that fails we'll return an error (should shout one according to the specs)
                        if (NormalTextureID == UUID.Zero)
                        {
                            if (UUID.TryParse(normal_tex, out NormalTextureID) == false)
                                return;

                            // UUID.Zero here is valid.  It means to clear the normal settings.
                            normal_tex = NormalTextureID.ToString();
                        }

                        LSL_Vector normal_repeats = rules.GetVector3Item(idx++);
                        LSL_Vector normal_offsets = rules.GetVector3Item(idx++);
                        float normal_rotation = rules.GetLSLFloatItem(idx++);

                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                if (face == ScriptBaseClass.ALL_SIDES)
                                {
                                    for (face = 0; face < part.GetNumberOfSides(); face++)
                                    {
                                        SetRenderMaterialNormalData(part, face, normal_tex, normal_repeats, normal_offsets, normal_rotation);
                                    }
                                }
                                else
                                {
                                    if (face >= 0 && face < part.GetNumberOfSides())
                                    {
                                        SetRenderMaterialNormalData(part, face, normal_tex, normal_repeats, normal_offsets, normal_rotation);
                                    }
                                }
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_ALPHA_MODE:
                        if (remain < 3)
                            return;

                        face = rules.GetLSLIntegerItem(idx++);
                        int alpha_mode = rules.GetLSLIntegerItem(idx++);
                        int alpha_mask_cutoff = rules.GetLSLIntegerItem(idx++);

                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                if (face == ScriptBaseClass.ALL_SIDES)
                                {
                                    for (face = 0; face < part.GetNumberOfSides(); face++)
                                    {
                                        SetRenderMaterialAlphaModeData(part, face, alpha_mode, alpha_mask_cutoff);
                                    }
                                }
                                else
                                {
                                    if (face >= 0 && face < part.GetNumberOfSides())
                                    {
                                        SetRenderMaterialAlphaModeData(part, face, alpha_mode, alpha_mask_cutoff);
                                    }
                                }
                            }
                        }
                        break;
                    case (int)ScriptBaseClass.PRIM_SIT_TARGET:
                        // [ PRIM_SIT_TARGET, integer active, vector offset, rotation rot ] 
                        if (remain < 3)
                            return;
                        bool isActive = rules.GetLSLIntegerItem(idx++) != 0;
                        v = rules.GetVector3Item(idx++);
                        r = rules.GetQuaternionItem(idx++);
                        foreach (var o in links)
                        {
                            if (o is SceneObjectPart)
                            {
                                var part = o as SceneObjectPart;
                                if (isActive)
                                    part.SetSitTarget(isActive, v, r, true);
                                else
                                    part.RemoveSitTarget();
                            }
                        }
                        break;
                }
            }
        }

        private void SetRenderMaterialSpecularData(
            SceneObjectPart part, 
            int face, 
            string specular_tex, 
            LSL_Vector specular_repeats, 
            LSL_Vector specular_offsets, 
            float specular_rotation, 
            LSL_Vector specular_color, 
            int specular_glossiness, 
            int specular_environment
            )
        {
            UUID id = part.Shape.GetMaterialID(face);
            RenderMaterial material;

            if (id == UUID.Zero)
                material = (RenderMaterial)RenderMaterial.DefaultMaterial.Clone();
            else
                material = part.Shape.RenderMaterials.GetMaterial(id);

            material.SpecularID = new UUID(specular_tex);
            material.SpecularRepeatX = specular_repeats.X;
            material.SpecularRepeatY = specular_repeats.Y;
            material.SpecularOffsetX = specular_offsets.X;
            material.SpecularOffsetY = specular_offsets.Y;
            material.SpecularRotation = specular_rotation;
            material.SpecularLightColorR = OpenMetaverse.Utils.FloatToByte(specular_color.X, 0f, 1f);
            material.SpecularLightColorG = OpenMetaverse.Utils.FloatToByte(specular_color.Y, 0f, 1f);
            material.SpecularLightColorB = OpenMetaverse.Utils.FloatToByte(specular_color.Z, 0f, 1f);
            material.SpecularLightColorA = 0;
            material.SpecularLightExponent = (byte)specular_glossiness;
            material.EnvironmentIntensity = (byte)specular_environment;

            AssignMaterial(part, face, material);
        }

        private void SetRenderMaterialNormalData(
            SceneObjectPart part, 
            int face, 
            string normal_tex, 
            LSL_Vector normal_repeats, 
            LSL_Vector normal_offsets, 
            float normal_rotation
            )
        {
            UUID id = part.Shape.GetMaterialID(face);
            RenderMaterial material;

            if (id == UUID.Zero)
                material = (RenderMaterial)RenderMaterial.DefaultMaterial.Clone();
            else
                material = part.Shape.RenderMaterials.GetMaterial(id);

            material.NormalID = new UUID(normal_tex);
            material.NormalRepeatX = normal_repeats.X;
            material.NormalRepeatY = normal_repeats.Y;
            material.NormalOffsetX = normal_offsets.X;
            material.NormalOffsetY = normal_offsets.Y;
            material.NormalRotation = normal_rotation;

            AssignMaterial(part, face, material);
        }

        private void SetRenderMaterialAlphaModeData(
            SceneObjectPart part, 
            int face, 
            int alpha_mode, 
            int alpha_mask_cutoff
            )
        {
            UUID id = part.Shape.GetMaterialID(face);
            RenderMaterial material;

            if (id == UUID.Zero)
                material = (RenderMaterial)RenderMaterial.DefaultMaterial.Clone();
            else
                material = part.Shape.RenderMaterials.GetMaterial(id);

            material.DiffuseAlphaMode = (byte)alpha_mode;
            material.AlphaMaskCutoff = (byte)alpha_mask_cutoff;

            AssignMaterial(part, face, material);
        }

        /// <summary>
        /// Assign a single material value.  Based on the values passed we'll either set (or clear) the materials for a SOP.
        /// </summary>
        /// <param name="sop">The SOP being affected.</param>
        /// <param name="face">The face to assign, or -1 if the default texture is being set.</param>
        /// <param name="id">The ID assigned to this material.  Setting a Zero UUID clears it.</param>
        /// <param name="material">If not null, the material to set.  Otherwise we are clearing.</param>
        private void AssignMaterial(SceneObjectPart sop, int face, RenderMaterial material)
        {
            // Add the new material to the SOP Shape.  We get an ID back
            UUID id = sop.Shape.RenderMaterials.AddMaterial(material);

            // Signal the change so the region cache gets updated
            if (sop.ParentGroup.Scene != null)
                sop.ParentGroup.Scene.EventManager.TriggerRenderMaterialAddedToPrim(sop, id, material);

            // If the new material is replacing one lets record it so we can clean up
            UUID oldMaterialID = UUID.Zero;

            /// Get a copy of the texture entry so we can make changes.
            var te = new Primitive.TextureEntry(sop.Shape.TextureEntry, 0, sop.Shape.TextureEntry.Length);

            // Set the Material ID in the TextureEntry. If face is ALL_SIDES then
            // set the default entry, otherwise fetch the face and set it there.
            if (face < 0)
            {
                oldMaterialID = te.DefaultTexture.MaterialID;
                te.DefaultTexture.MaterialID = id;
            }
            else
            {
                var faceEntry = te.CreateFace((uint)face);
                oldMaterialID = faceEntry.MaterialID;
                faceEntry.MaterialID = id;
            }

            // Update the texture entry which will force an update to connected clients
            sop.UpdateTexture(te);

            // If the material has changed and it wasn't previously Zero 
            // Deallocate the old value if its not in use and signal the change
            if ((oldMaterialID != id) &&
                (oldMaterialID != UUID.Zero))
            {
                var currentMaterialIDs = sop.Shape.GetMaterialIDs();
                if (currentMaterialIDs.Contains(oldMaterialID) == false)
                {
                    if (sop.Shape.RenderMaterials.ContainsMaterial(oldMaterialID) == true)
                        sop.Shape.RenderMaterials.RemoveMaterial(oldMaterialID);

                    if (sop.ParentGroup.Scene != null)
                        sop.ParentGroup.Scene.EventManager.TriggerRenderMaterialRemovedFromPrim(sop, oldMaterialID);
                }
            }
        }

        public string llStringToBase64(string str)
        {
            try
            {
                byte[] encData_byte = new byte[str.Length];
                encData_byte = Encoding.UTF8.GetBytes(str);
                string encodedData = Convert.ToBase64String(encData_byte);
                return encodedData;
            }
            catch (Exception e)
            {
                LSLError("Error in base64Encode" + e.Message);
                return String.Empty;
            }
        }

        public string llBase64ToString(string str)
        {
            try
            {
                return Util.Base64ToString(str).Replace("�", "?");
            }
            catch
            {
                return String.Empty;
            }
        }

        public string llXorBase64Strings(string str1, string str2)
        {
            Deprecated("llXorBase64Strings");
            // ScriptSleep(300);
            return String.Empty;
        }

        public void llRemoteDataSetRegion()
        {
            NotImplemented("llRemoteDataSetRegion");
        }

        public float llLog10(float val)
        {
            return (float)Math.Log10(val);
        }

        public float llLog(float val)
        {
            return (float)Math.Log(val);
        }

        public LSL_List llGetAnimationList(string id)
        {
            List<object> l = new List<object>();
            ScenePresence av = World.GetScenePresence((UUID)id);
            if (av == null || av.IsChildAgent) // only if in the region
                return new LSL_List(l);
            UUID[] anims;
            anims = av.GetAnimationArray();
            foreach (UUID foo in anims)
                l.Add(foo.ToString());
            return new LSL_List(l);
        }

        public void llSetParcelMusicURL(string url)
        {
            ILandObject landObject = World.LandChannel.GetLandObject(m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y);

            if (!World.Permissions.CanEditParcel(m_host.OwnerID, landObject, GroupPowers.ChangeMedia)) 
                return;

            landObject.SetMusicUrl(url);

            ScriptSleep(2000);
        }

        public string llGetParcelMusicURL()
        {
            ILandObject land = World.LandChannel.GetLandObject(m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y);
            if (land == null)
                return String.Empty;

            return land.GetMusicUrl();
        }

        public int iwHasParcelPowers(int operation)
        {
            ILandObject landObject = World.LandChannel.GetLandObject(m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y);
            GroupPowers powers = (GroupPowers)operation;
            return World.Permissions.CanEditParcel(m_host.OwnerID, landObject, powers) ? 1 : 0;
        }

        public LSL_Vector llGetRootPosition()
        {
            
            return new LSL_Vector(m_host.ParentGroup.AbsolutePosition.X, m_host.ParentGroup.AbsolutePosition.Y, m_host.ParentGroup.AbsolutePosition.Z);
        }

        /// <summary>
        /// http://lslwiki.net/lslwiki/wakka.php?wakka=llGetRot
        /// http://lslwiki.net/lslwiki/wakka.php?wakka=ChildRotation
        /// Also tested in sl in regards to the behaviour in attachments/mouselook
        /// In the root prim:-
        ///     Returns the object rotation if not attached
        ///     Returns the avatars rotation if attached
        ///     Returns the camera rotation if attached and the avatar is in mouselook
        /// </summary>
        public LSL_Rotation llGetRootRotation()
        {
            
            Quaternion q;
            if (m_host.ParentGroup.RootPart.AttachmentPoint != 0)
            {
                ScenePresence avatar = World.GetScenePresence(m_host.AttachedAvatar);
                if (avatar != null)
                    if ((avatar.AgentControlFlags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_MOUSELOOK) != 0)
                        q = avatar.CameraRotation; // Mouselook
                    else
                        q = avatar.Rotation; // Currently infrequently updated so may be inaccurate
                else
                    q = m_host.ParentGroup.GroupRotation; // Likely never get here but just in case
            }
            else
                q = m_host.ParentGroup.GroupRotation; // just the group rotation
            return new LSL_Rotation(q.X, q.Y, q.Z, q.W);
        }

        public string llGetObjectDesc()
        {
            return m_host.Description != null ? m_host.Description : String.Empty;
        }

        public void llSetObjectDesc(string desc)
        {
            m_host.Description = LimitLength(desc, MAX_OBJ_DESC);
        }

        public string llGetCreator()
        {
            
            return m_host.ObjectCreator.ToString();
        }

        public string llGetTimestamp()
        {
            
            return DateTime.Now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
        }

        public int llGetNumberOfPrims()
        {
            return m_host.ParentGroup.LinkCount;
        }

        public LSL_List GetBoundingBox(string obj, bool isRelative)
        {

            UUID objID = UUID.Zero;
            List<object> result = new List<object>();
            if (!UUID.TryParse(obj, out objID))
            {
                result.Add(new LSL_Vector());
                result.Add(new LSL_Vector());
                return new LSL_List(result);
            }
            ScenePresence presence = World.GetScenePresence(objID);
            if (presence != null)
            {
                ScenePresence.PositionInfo info = presence.GetPosInfo();
                if (info.Parent == null) // not sat on an object
                {
                    LSL_Vector lower;
                    LSL_Vector upper;
                    if (presence.Animations.DefaultAnimation.AnimID == AnimationSet.Animations.AnimsUUID["SIT_GROUND_CONSTRAINED"])
                    {
                        // This is for ground sitting avatars
                        float height = presence.Appearance.AvatarHeight / 2.66666667f;
                        lower = new LSL_Vector(-0.3375f, -0.45f, height * -1.0f);
                        upper = new LSL_Vector(0.3375f, 0.45f, 0.0f);
                    }
                    else
                    {
                        // This is for standing/flying avatars
                        float height = presence.Appearance.AvatarHeight / 2.0f;
                        lower = new LSL_Vector(-0.225f, -0.3f, height * -1.0f);
                        upper = new LSL_Vector(0.225f, 0.3f, height + 0.05f);
                    }
                    if (!isRelative)
                    {
                        lower -= info.Position;
                        upper += info.Position;
                    }
                    result.Add(lower);
                    result.Add(upper);
                    return new LSL_List(result);
                }
                // sitting on an object so we need the bounding box of that
                // which should include the avatar so set the UUID to the
                // UUID of the object the avatar is sat on and allow it to fall through
                // to processing an object
                objID = info.Parent.UUID;
            }
            SceneObjectPart part = World.GetSceneObjectPart(objID);
            // Currently only works for single prims without a sitting avatar
            if (part != null)
            {
                Box bbox;
                if (isRelative)
                    bbox = part.ParentGroup.RelativeBoundingBox(true);
                else
                    bbox = part.ParentGroup.BoundingBox();

                Vector3 halfSize = bbox.Size / 2.0f;
                LSL_Vector lower = new LSL_Vector(halfSize.X * -1.0f, halfSize.Y * -1.0f, halfSize.Z * -1.0f);
                LSL_Vector upper = new LSL_Vector(halfSize.X, halfSize.Y, halfSize.Z);
                lower += bbox.Center;
                upper += bbox.Center;
                result.Add(lower);
                result.Add(upper);
                return new LSL_List(result);
            }

            // Not found so return empty values
            result.Add(new LSL_Vector());
            result.Add(new LSL_Vector());
            return new LSL_List(result);
        }

        /// <summary>
        /// A partial implementation.
        /// http://lslwiki.net/lslwiki/wakka.php?wakka=llGetBoundingBox
        /// So far only valid for standing/flying/ground sitting avatars and single prim objects.
        /// If the object has multiple prims and/or a sitting avatar then the bounding
        /// box is for the root prim only.
        /// </summary>
        public LSL_List llGetBoundingBox(string obj)
        {
            return GetBoundingBox(obj, true);
        }
        public LSL_List iwGetWorldBoundingBox(string obj)
        {
            return GetBoundingBox(obj, false);
        }

        public LSL_Vector llGetGeometricCenter()
        {
            return new LSL_Vector(m_host.GetGeometricCenter().X, m_host.GetGeometricCenter().Y, m_host.GetGeometricCenter().Z);
        }

        private int GetAvatarAsPrimParam(ScenePresence sp, ref List<object> res, int rule)
        {
            int paramCount = 0;
            switch (rule)
            {
                case ScriptBaseClass.PRIM_POSITION:
                    res.Add(new LSL_Vector(sp.AbsolutePosition));
                    break;
                case ScriptBaseClass.PRIM_POS_LOCAL:
                    res.Add(new LSL_Vector(sp.AbsolutePosition-m_host.ParentGroup.RootPart.AbsolutePosition));
                    break;
                case ScriptBaseClass.PRIM_NAME: // will return the avatar's legacy name.
                    res.Add((sp != null) ? sp.Name : String.Empty);
                    break;
                case ScriptBaseClass.PRIM_DESC:
                    res.Add(String.Empty);
                    break;
                case ScriptBaseClass.PRIM_TYPE:
                    res.Add(ScriptBaseClass.PRIM_TYPE_BOX);
                    res.Add(ScriptBaseClass.PRIM_HOLE_DEFAULT);
                    res.Add(new LSL_Vector(0.0f, 1.0f, 0.0f));
                    res.Add((float)0.0f);
                    res.Add(new LSL_Vector(Vector3.Zero));
                    res.Add(new LSL_Vector(1.0f, 1.0f, 0.0f));
                    res.Add(new LSL_Vector(Vector3.Zero));
                    break;
                case ScriptBaseClass.PRIM_SLICE:
                    res.Add(new LSL_Vector(0.0f, 1.0f, 0.0f));
                    break;
                case ScriptBaseClass.PRIM_MATERIAL:
                    res.Add((int)ScriptBaseClass.PRIM_MATERIAL_FLESH);
                    break;
                case ScriptBaseClass.PRIM_TEMP_ON_REZ:
                    res.Add(ScriptBaseClass.FALSE);
                    break;
                case ScriptBaseClass.PRIM_PHANTOM:
                    res.Add(ScriptBaseClass.FALSE);
                    break;
                case ScriptBaseClass.PRIM_SIZE:
                    res.Add(new LSL_Vector(llGetAgentSize(sp.UUID.ToString())));
                    break;
                case ScriptBaseClass.PRIM_TEXT:
                    res.Add(String.Empty);
                    res.Add(new LSL_Vector(Vector3.Zero));
                    res.Add((float)1.0f);
                    break;
                case ScriptBaseClass.PRIM_POINT_LIGHT:
                    res.Add(ScriptBaseClass.FALSE);
                    res.Add(new LSL_Vector(Vector3.Zero));
                    res.Add((float)0.0f);
                    res.Add((float)0.0f);
                    res.Add((float)0.0f);
                    break;
                case ScriptBaseClass.PRIM_FLEXIBLE:
                    res.Add(ScriptBaseClass.FALSE);
                    res.Add((int)0);
                    res.Add((float)0.0f);
                    res.Add((float)0.0f);
                    res.Add((float)0.0f);
                    res.Add((float)0.0f);
                    res.Add(new LSL_Vector(Vector3.Zero));
                    break;

                // These all expect a single parameter in the params list:
                case (int)ScriptBaseClass.PRIM_TEXTURE:
                case (int)ScriptBaseClass.IW_PRIM_ALPHA:
				case (int)ScriptBaseClass.PRIM_COLOR:
				case (int)ScriptBaseClass.PRIM_BUMP_SHINY:
				case (int)ScriptBaseClass.PRIM_FULLBRIGHT:
				case (int)ScriptBaseClass.PRIM_TEXGEN:
				case (int)ScriptBaseClass.PRIM_GLOW:
				case (int)ScriptBaseClass.PRIM_SPECULAR:
				case (int)ScriptBaseClass.PRIM_NORMAL:
				case (int)ScriptBaseClass.PRIM_ALPHA_MODE:
                    paramCount = 1;
                    ScriptShoutError("texture info cannot be accessed for avatars.");
                    break;
            }

            return paramCount;
        }

        private int GetAvatarAsPrimParam(int linknum, ref List<object> res, int rule)
        {
            ScenePresence sp = m_host.ParentGroup.GetSeatedAvatarByLink(linknum);
            if (sp == null)
                return 0;

            return GetAvatarAsPrimParam(sp, ref res, rule);
        }

        private LSL_List GetPrimParams(int linknumber, LSL_List rules)
        {
            List<object> res = new List<object>();
            int idx = 0;
            int face = 0;
            Primitive.TextureEntry tex;
            List<object> parts = null;

            // Support avatar-as-a-prim link number.
            parts = GetLinkParts(linknumber, true);

            while (idx < rules.Length)
            {
                int code = (int)rules.GetLSLIntegerItem(idx++);
                int remain = rules.Length - idx;

                // Handle changing target prim(s) and/or avatars.
                if (code == (int)ScriptBaseClass.PRIM_LINK_TARGET)
                {
                    if (remain < 1)
                        return new LSL_List(res);
                    linknumber = (int)rules.GetLSLIntegerItem(idx++);
                    remain = rules.Length - idx;

                    parts = GetLinkParts(linknumber, true);
                    continue;
                }

                // Neither of the above, fall through to normal prim properties.
                switch (code)
                {
                    case (int)ScriptBaseClass.PRIM_MATERIAL:
                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                                res.Add((int)((o as SceneObjectPart).Material));
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_PHYSICS:
                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            if (((o as SceneObjectPart).GetEffectiveObjectFlags() & PrimFlags.Physics) != 0)
                                res.Add((int)(1));
                            else
                                res.Add((int)(0));
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_TEMP_ON_REZ:
                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            if (((o as SceneObjectPart).GetEffectiveObjectFlags() & PrimFlags.TemporaryOnRez) != 0)
                                res.Add((int)(1));
                            else
                                res.Add((int)(0));
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_PHANTOM:
                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            if (((o as SceneObjectPart).GetEffectiveObjectFlags() & PrimFlags.Phantom) != 0)
                                res.Add((int)(1));
                            else
                                res.Add((int)(0));
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_POSITION:
                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                                res.Add(new LSL_Vector((o as SceneObjectPart).GetSLCompatiblePosition()));
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_SIZE:
                        foreach (object o in parts)
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                                res.Add(new LSL_Vector((o as SceneObjectPart).Scale));
                        break;

                    case (int)ScriptBaseClass.PRIM_ROTATION:
                        foreach (object o in parts)
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                                res.Add(GetPartRot(o as SceneObjectPart));
                        break;

                    case (int)ScriptBaseClass.PRIM_ROT_LOCAL:
                        foreach (object o in parts)
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                                res.Add(GetPartLocalRot(o as SceneObjectPart));
                        break;

                    case (int)ScriptBaseClass.PRIM_PHYSICS_SHAPE_TYPE:
                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            if ((o as SceneObjectPart).Shape.PreferredPhysicsShape == PhysicsShapeType.Prim)
                                res.Add((int)ScriptBaseClass.PRIM_PHYSICS_SHAPE_PRIM);
                            else if ((o as SceneObjectPart).Shape.PreferredPhysicsShape == PhysicsShapeType.None)
                                res.Add((int)ScriptBaseClass.PRIM_PHYSICS_SHAPE_NONE);
                            else
                                res.Add((int)ScriptBaseClass.PRIM_PHYSICS_SHAPE_CONVEX);
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_OMEGA:
                        // This is probably correct in the 99% case.  It's stored in axis*spinrate (gain is ignored)
                        // and spinrate is often TWO_PI. So divide by that and hope for the best (for now).
                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                res.Add(new LSL_Vector((float)(part.AngularVelocity.X / ScriptBaseClass.TWO_PI), (float)(part.AngularVelocity.Y / ScriptBaseClass.TWO_PI), (float)(part.AngularVelocity.Z / ScriptBaseClass.TWO_PI)));
                                res.Add((float)ScriptBaseClass.TWO_PI);
                                res.Add((float)1.0f);
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_POS_LOCAL:
                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                                res.Add(new LSL_Vector(GetPartLocalPos(o as SceneObjectPart)));
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_TYPE:
                        // implementing box
                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                PrimitiveBaseShape Shape = part.Shape;
                                int primType = getScriptPrimType(part.Shape);
                                res.Add((int)(primType));
                                switch (primType)
                                {
                                    case ScriptBaseClass.PRIM_TYPE_BOX:
                                    case ScriptBaseClass.PRIM_TYPE_CYLINDER:
                                    case ScriptBaseClass.PRIM_TYPE_PRISM:
                                        res.Add((int)(Shape.HollowShape));
                                        res.Add(new LSL_Vector(Shape.ProfileBegin / 50000.0f, 1 - Shape.ProfileEnd / 50000.0f, 0));
                                        res.Add((float)(Shape.ProfileHollow / 50000.0));
                                        res.Add(new LSL_Vector(Shape.PathTwistBegin / 100.0f, Shape.PathTwist / 100.0f, 0));
                                        res.Add(new LSL_Vector(1 - (Shape.PathScaleX / 100.0f - 1), 1 - (Shape.PathScaleY / 100.0f - 1), 0));
                                        res.Add(new LSL_Vector(Primitive.UnpackPathShear((sbyte)Shape.PathShearX), Primitive.UnpackPathShear((sbyte)Shape.PathShearY), 0));
                                        break;

                                    case ScriptBaseClass.PRIM_TYPE_SPHERE:
                                        res.Add((int)(Shape.HollowShape));
                                        res.Add(new LSL_Vector(Shape.PathBegin / 50000.0f, 1 - Shape.PathEnd / 50000.0f, 0));
                                        res.Add((float)(Shape.ProfileHollow / 50000.0f));
                                        res.Add(new LSL_Vector(Shape.PathTwistBegin / 100.0f, Shape.PathTwist / 100.0f, 0));
                                        res.Add(new LSL_Vector(Shape.ProfileBegin / 50000.0f, 1 - Shape.ProfileEnd / 50000.0f, 0));
                                        break;

                                    case ScriptBaseClass.PRIM_TYPE_SCULPT:
                                        res.Add(ConditionalTextureNameOrUUID(part, Shape.SculptTexture));
                                        res.Add((int)(Shape.SculptType));
                                        break;

                                    case ScriptBaseClass.PRIM_TYPE_RING:
                                    case ScriptBaseClass.PRIM_TYPE_TUBE:
                                    case ScriptBaseClass.PRIM_TYPE_TORUS:
                                        // holeshape
                                        res.Add((int)(Shape.HollowShape));

                                        // cut
                                        res.Add(new LSL_Vector(Shape.PathBegin / 50000.0f, 1 - Shape.PathEnd / 50000.0f, 0));

                                        // hollow
                                        res.Add((float)(Shape.ProfileHollow / 50000.0));

                                        // twist
                                        res.Add(new LSL_Vector(Shape.PathTwistBegin / 100.0f, Shape.PathTwist / 100.0f, 0));

                                        // vector holesize
                                        res.Add(new LSL_Vector(1 - (Shape.PathScaleX / 100.0f - 1), 1 - (Shape.PathScaleY / 100.0f - 1), 0));

                                        // vector topshear
                                        res.Add(new LSL_Vector(Primitive.UnpackPathShear((sbyte)Shape.PathShearX), Primitive.UnpackPathShear((sbyte)Shape.PathShearY), 0));

                                        // vector profilecut
                                        res.Add(new LSL_Vector(Shape.ProfileBegin / 50000.0f, 1 - Shape.ProfileEnd / 50000.0f, 0));

                                        // vector tapera
                                        res.Add(new LSL_Vector(Shape.PathTaperX / 100.0f, Shape.PathTaperY / 100.0f, 0));

                                        // float revolutions, 
                                        res.Add((float)(Shape.PathRevolutions / 50.0)); // needs fixing :(

                                        // float radiusoffset, 
                                        res.Add((float)(Shape.PathRadiusOffset / 100.0));

                                        // float skew
                                        res.Add((float)(Shape.PathSkew / 100.0));
                                        break;
                                }
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_TEXTURE:
                        if (remain < 1)
                            return new LSL_List(res);
                        face = (int)rules.GetLSLIntegerItem(idx++);

                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                tex = part.Shape.Textures;
                                if (face == ScriptBaseClass.ALL_SIDES)
                                {
                                    for (face = 0; face < part.GetNumberOfSides(); face++)
                                    {
                                        Primitive.TextureEntryFace texface = tex.GetFace((uint)face);
                                        res.Add(ConditionalTextureNameOrUUID(part, texface.TextureID));
                                        res.Add(new LSL_Vector(texface.RepeatU,
                                                               texface.RepeatV,
                                                               0));
                                        res.Add(new LSL_Vector(texface.OffsetU,
                                                               texface.OffsetV,
                                                               0));
                                        res.Add((float)(texface.Rotation));
                                    }
                                }
                                else
                                {
                                    if (face >= 0 && face < part.GetNumberOfSides())
                                    {
                                        Primitive.TextureEntryFace texface = tex.GetFace((uint)face);
                                        res.Add(ConditionalTextureNameOrUUID(part, texface.TextureID).ToString());
                                        res.Add(new LSL_Vector(texface.RepeatU,
                                                               texface.RepeatV,
                                                               0));
                                        res.Add(new LSL_Vector(texface.OffsetU,
                                                               texface.OffsetV,
                                                               0));
                                        res.Add((float)(texface.Rotation));
                                    }
                                }
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.IW_PRIM_ALPHA:
                        if (remain < 1)
                            return new LSL_List(res);
                        face = (int)rules.GetLSLIntegerItem(idx++);

                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            {
                                double alphaValue = GetAlpha(o as SceneObjectPart, face);
                                res.Add((float)alphaValue);
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_COLOR:
                        if (remain < 1)
                            return new LSL_List(res);
                        face = (int)rules.GetLSLIntegerItem(idx++);

                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                tex = part.Shape.Textures;
                                Color4 texcolor;
                                if (face == ScriptBaseClass.ALL_SIDES)
                                {
                                    for (face = 0; face < part.GetNumberOfSides(); face++)
                                    {
                                        texcolor = tex.GetFace((uint)face).RGBA;
                                        res.Add(new LSL_Vector(texcolor.R,
                                                               texcolor.G,
                                                               texcolor.B));
                                        res.Add((float)(texcolor.A));
                                    }
                                }
                                else
                                {
                                    texcolor = tex.GetFace((uint)face).RGBA;
                                    res.Add(new LSL_Vector(texcolor.R,
                                                           texcolor.G,
                                                           texcolor.B));
                                    res.Add((float)(texcolor.A));
                                }
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_BUMP_SHINY:
                        if (remain < 1)
                            return new LSL_List(res);
                        face = (int)rules.GetLSLIntegerItem(idx++);

                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                tex = part.Shape.Textures;
                                Primitive.TextureEntryFace texface;

                                if (face == ScriptBaseClass.ALL_SIDES)
                                {
                                    for (face = 0; face < part.GetNumberOfSides(); face++)
                                    {
                                        texface = tex.GetFace((uint)face);
                                        // Convert Shininess to PRIM_SHINY_*
                                        res.Add((int)((uint)texface.Shiny >> 6));
                                        // PRIM_BUMP_*
                                        res.Add((int)texface.Bump);
                                    }
                                }
                                else
                                {
                                    if (face >= 0 && face < part.GetNumberOfSides())
                                    {
                                        texface = tex.GetFace((uint)face);
                                        // Convert Shininess to PRIM_SHINY_*
                                        res.Add((int)((uint)texface.Shiny >> 6));
                                        // PRIM_BUMP_*
                                        res.Add((int)((int)texface.Bump));
                                    }
                                }
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_FULLBRIGHT:
                        if (remain < 1)
                            return new LSL_List(res);
                        face = (int)rules.GetLSLIntegerItem(idx++);

                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                tex = part.Shape.Textures;
                                if (face == ScriptBaseClass.ALL_SIDES)
                                {
                                    for (face = 0; face < part.GetNumberOfSides(); face++)
                                    {
                                        Primitive.TextureEntryFace texface = tex.GetFace((uint)face);
                                        res.Add(texface.Fullbright ? 1 : 0);
                                    }
                                }
                                else
                                {
                                    if (face >= 0 && face < part.GetNumberOfSides())
                                    {
                                        Primitive.TextureEntryFace texface = tex.GetFace((uint)face);
                                        res.Add(texface.Fullbright ? 1 : 0);
                                    }
                                }
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_FLEXIBLE:
                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                PrimitiveBaseShape shape = part.Shape;

                                if (shape.FlexiEntry)
                                    res.Add((int)(1));              // active
                                else
                                    res.Add((int)(0));
                                res.Add((int)(shape.FlexiSoftness));// softness
                                res.Add((float)(shape.FlexiGravity));   // gravity
                                res.Add((float)(shape.FlexiDrag));      // friction
                                res.Add((float)(shape.FlexiWind));      // wind
                                res.Add((float)(shape.FlexiTension));   // tension
                                res.Add(new LSL_Vector(shape.FlexiForceX,       // force
                                                       shape.FlexiForceY,
                                                       shape.FlexiForceZ));
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_TEXGEN:
                        if (remain < 1)
                            return new LSL_List(res);
                        face = (int)rules.GetLSLIntegerItem(idx++);

                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                tex = part.Shape.Textures;
                                if (face == ScriptBaseClass.ALL_SIDES)
                                {
                                    for (face = 0; face < part.GetNumberOfSides(); face++)
                                    {
                                        MappingType texgen = tex.GetFace((uint)face).TexMapType;
                                        // Convert MappingType to PRIM_TEXGEN_DEFAULT, PRIM_TEXGEN_PLANAR etc.
                                        res.Add((int)((uint)texgen >> 1));
                                    }
                                }
                                else
                                {
                                    if (face >= 0 && face < part.GetNumberOfSides())
                                    {
                                        MappingType texgen = tex.GetFace((uint)face).TexMapType;
                                        res.Add((int)((uint)texgen >> 1));
                                    }
                                }
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_POINT_LIGHT:
                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                PrimitiveBaseShape shape = part.Shape;
                                if (shape.LightEntry)
                                    res.Add((int)(1));              // active
                                else
                                    res.Add((int)(0));
                                res.Add(new LSL_Vector(shape.LightColorR,       // color
                                                       shape.LightColorG,
                                                       shape.LightColorB));
                                res.Add((float)(shape.LightIntensity)); // intensity
                                res.Add((float)(shape.LightRadius));    // radius
                                res.Add((float)(shape.LightFalloff));   // falloff
                            }
                        }
                        break;

                    case ScriptBaseClass.IW_PRIM_PROJECTOR:
                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                PrimitiveBaseShape shape = part.Shape;
                                if (shape.ProjectionEntry)
                                    res.Add((int)(1));              // active
                                else
                                    res.Add((int)(0));
                                res.Add(ConditionalTextureNameOrUUID(part, shape.ProjectionTextureUUID));
                                res.Add((float)(shape.ProjectionFOV));
                                res.Add((float)(shape.ProjectionFocus));
                                res.Add((float)(shape.ProjectionAmbiance));
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.IW_PRIM_PROJECTOR_ENABLED:
                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                PrimitiveBaseShape shape = part.Shape;
                                if (shape.ProjectionEntry)
                                    res.Add((int)(1));              // active
                                else
                                    res.Add((int)(0));
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.IW_PRIM_PROJECTOR_TEXTURE:
                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                PrimitiveBaseShape shape = part.Shape;
                                res.Add(ConditionalTextureNameOrUUID(part, shape.ProjectionTextureUUID));
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.IW_PRIM_PROJECTOR_FOV:
                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                PrimitiveBaseShape shape = part.Shape;
                                res.Add((float)(shape.ProjectionFOV));
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.IW_PRIM_PROJECTOR_FOCUS:
                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                PrimitiveBaseShape shape = part.Shape;
                                res.Add((float)(shape.ProjectionFocus));
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.IW_PRIM_PROJECTOR_AMBIENCE:
                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                PrimitiveBaseShape shape = part.Shape;
                                res.Add((float)(shape.ProjectionAmbiance));
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_GLOW:
                        if (remain < 1)
                            return new LSL_List(res);
                        face = (int)rules.GetLSLIntegerItem(idx++);

                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                tex = part.Shape.Textures;
                                if (face == ScriptBaseClass.ALL_SIDES)
                                {
                                    for (face = 0; face < part.GetNumberOfSides(); face++)
                                    {
                                        Primitive.TextureEntryFace texface = tex.GetFace((uint)face);
                                        res.Add(texface.Glow);
                                    }
                                }
                                else
                                {
                                    if (face >= 0 && face < part.GetNumberOfSides())
                                    {
                                        Primitive.TextureEntryFace texface = tex.GetFace((uint)face);
                                        res.Add(texface.Glow);
                                    }
                                }
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_TEXT:
                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                res.Add((string)(part.Text));
                                res.Add(new LSL_Vector(part.TextColor.R,
                                                        part.TextColor.G,
                                                        part.TextColor.B));
                                res.Add((float)((255.0 - part.TextColor.A) / 255)); // see part.SetText()
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_NAME:
                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                                res.Add((string)((o as SceneObjectPart).Name));
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_DESC:
                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                                res.Add((string)((o as SceneObjectPart).Description));
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_SLICE:
                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                PrimitiveBaseShape Shape = part.Shape;
                                int primType = getScriptPrimType(part.Shape);

                                int hollowShape = (int)(Shape.HollowShape);
                                LSL_Vector profileCut = new LSL_Vector(Shape.ProfileBegin / 50000.0f, 1 - Shape.ProfileEnd / 50000.0f, 0.0f);
                                float profileHollow = (float)(Shape.ProfileHollow / 50000.0);
                                LSL_Vector pathTwist = new LSL_Vector(Shape.PathTwistBegin / 100.0f, Shape.PathTwist / 100.0f, 0);
                                LSL_Vector pathScale = new LSL_Vector(1 - (Shape.PathScaleX / 100.0f - 1), 1 - (Shape.PathScaleY / 100.0f - 1), 0);
                                LSL_Vector pathShear = new LSL_Vector(Primitive.UnpackPathShear((sbyte)Shape.PathShearX), Primitive.UnpackPathShear((sbyte)Shape.PathShearY), 0);

                                float sliceBegin = Shape.PathBegin / 50000.0f;
                                float sliceEnd = 1 - Shape.PathEnd / 50000.0f;

                                switch (primType)
                                {
                                    case ScriptBaseClass.PRIM_TYPE_BOX:
                                    case ScriptBaseClass.PRIM_TYPE_CYLINDER:
                                    case ScriptBaseClass.PRIM_TYPE_PRISM:
                                        res.Add(new LSL_Vector(sliceBegin, sliceEnd, 0.0f));
                                        break;
                                    default:    // anything else has full slice
                                        res.Add(new LSL_Vector(0.0f, 1.0f, 0.0f));
                                        break;
                                }
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_SPECULAR:
                        if (remain < 1)
                            return new LSL_List(res);

                        face = (int)rules.GetLSLIntegerItem(idx++);

                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                tex = part.Shape.Textures;
                                if (face == ScriptBaseClass.ALL_SIDES)
                                {
                                    for (face = 0; face < part.GetNumberOfSides(); face++)
                                    {
                                        res.AddRange(GetRenderMaterialSpecularData(part, face));
                                    }
                                }
                                else
                                {
                                    if (face >= 0 && face < part.GetNumberOfSides())
                                    {
                                        res.AddRange(GetRenderMaterialSpecularData(part, face));
                                    }
                                }
                            }
                        }
                        break;


                    case (int)ScriptBaseClass.PRIM_NORMAL:
                        if (remain < 1)
                            return new LSL_List(res);

                        face = (int)rules.GetLSLIntegerItem(idx++);

                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                tex = part.Shape.Textures;
                                if (face == ScriptBaseClass.ALL_SIDES)
                                {
                                    for (face = 0; face < part.GetNumberOfSides(); face++)
                                    {
                                        res.AddRange(GetRenderMaterialNormalData(part, face));
                                    }
                                }
                                else
                                {
                                    if (face >= 0 && face < part.GetNumberOfSides())
                                    {
                                        res.AddRange(GetRenderMaterialNormalData(part, face));
                                    }
                                }
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_ALPHA_MODE:
                        if (remain < 1)
                            return new LSL_List(res);

                        face = (int)rules.GetLSLIntegerItem(idx++);

                        foreach (object o in parts)
                        {
                            if (o is ScenePresence)
                                GetAvatarAsPrimParam(o as ScenePresence, ref res, code);
                            else
                            {
                                SceneObjectPart part = o as SceneObjectPart;
                                tex = part.Shape.Textures;
                                if (face == ScriptBaseClass.ALL_SIDES)
                                {
                                    for (face = 0; face < part.GetNumberOfSides(); face++)
                                    {
                                        res.AddRange(GetRenderMaterialAlphaModeData(part, face));
                                    }
                                }
                                else
                                {
                                    if (face >= 0 && face < part.GetNumberOfSides())
                                    {
                                        res.AddRange(GetRenderMaterialAlphaModeData(part, face));
                                    }
                                }
                            }
                        }
                        break;

                    case (int)ScriptBaseClass.PRIM_SIT_TARGET:
                        foreach (object o in parts)
                        {
                            if (o is SceneObjectPart)
                            {
                                SceneObjectPart part = (o as SceneObjectPart);
                                SitTargetInfo sitInfo = part.ParentGroup.SitTargetForPart(part.UUID);
                                if (sitInfo != null)
                                {
                                    res.Add((int)(sitInfo.IsActive ? 1 : 0));
                                    res.Add(new LSL_Vector(sitInfo.Offset));
                                    res.Add(new LSL_Rotation(sitInfo.Rotation));
                                    continue;
                                }
                            }

                            // No sit target info, or it's a ScenePresence.
                            res.Add((int)0);
                            res.Add(new LSL_Vector(Vector3.Zero));
                            res.Add(new LSL_Rotation(Quaternion.Identity));
                        }
                        break;
                }
            }

            return new LSL_List(res);
        }

        // Returns the list: [string texture, vector repeats, vector offsets, float rot, vector specular_color, integer glossiness, integer environment]
        private List<object> GetRenderMaterialSpecularData(SceneObjectPart part, int face)
        {
            List<object> res = new List<object>();

            Primitive.TextureEntry tex = part.Shape.Textures;
            Primitive.TextureEntryFace texface = tex.GetFace((uint)face);
            RenderMaterial mat;

            if ((texface.MaterialID == UUID.Zero) ||
                (part.Shape.RenderMaterials.ContainsMaterial(texface.MaterialID) == false))
                mat = RenderMaterial.DefaultMaterial;
            else
                mat = part.Shape.RenderMaterials.GetMaterial(texface.MaterialID);

            res.Add(ConditionalTextureNameOrUUID(part, mat.SpecularID).ToString());
            res.Add(new LSL_Vector(mat.SpecularRepeatX, mat.SpecularRepeatY, 0));
            res.Add(new LSL_Vector(mat.SpecularOffsetX, mat.SpecularOffsetY, 0));
            res.Add(mat.SpecularRotation);
            res.Add(new LSL_Vector(
                Utils.ByteToFloat(mat.SpecularLightColorR, 0f, 1f),
                Utils.ByteToFloat(mat.SpecularLightColorG, 0f, 1f),
                Utils.ByteToFloat(mat.SpecularLightColorB, 0f, 1f)));
            res.Add((int)mat.SpecularLightExponent);
            res.Add((int)mat.EnvironmentIntensity);

            return res;
        }

        //  Returns the list: [ string texture, vector repeats, vector offsets, float rot ]
        private List<object> GetRenderMaterialNormalData(SceneObjectPart part, int face)
        {

            List<object> res = new List<object>();

            Primitive.TextureEntry tex = part.Shape.Textures;
            Primitive.TextureEntryFace texface = tex.GetFace((uint)face);
            RenderMaterial mat;

            if ((texface.MaterialID == UUID.Zero) ||
                (part.Shape.RenderMaterials.ContainsMaterial(texface.MaterialID) == false))
                mat = RenderMaterial.DefaultMaterial;
            else
                mat = part.Shape.RenderMaterials.GetMaterial(texface.MaterialID);

            res.Add(ConditionalTextureNameOrUUID(part, mat.NormalID).ToString());
            res.Add(new LSL_Vector(mat.NormalRepeatX, mat.NormalRepeatY, 0));
            res.Add(new LSL_Vector(mat.NormalOffsetX, mat.NormalOffsetY, 0));
            res.Add(mat.NormalRotation);

            return res;
        }

        // Returns the list: [ integer alpha_mode, integer mask_cutoff ]
        private static List<object> GetRenderMaterialAlphaModeData(SceneObjectPart part, int face)
        {

            List<object> res = new List<object>();

            Primitive.TextureEntry tex = part.Shape.Textures;
            Primitive.TextureEntryFace texface = tex.GetFace((uint)face);
            RenderMaterial mat;

            if ((texface.MaterialID == UUID.Zero) ||
                (part.Shape.RenderMaterials.ContainsMaterial(texface.MaterialID) == false))
                mat = RenderMaterial.DefaultMaterial;
            else
                mat = part.Shape.RenderMaterials.GetMaterial(texface.MaterialID);

            res.Add((int)mat.DiffuseAlphaMode);
            res.Add((int)mat.AlphaMaskCutoff);

            return res;
        }

        public LSL_List llGetPrimitiveParams(LSL_List rules)
        {
            return GetPrimParams(m_host.LinkNum, rules);
        }

        public LSL_List llGetLinkPrimitiveParams(int linknumber, LSL_List rules)
        {
            return GetPrimParams(linknumber, rules);
        }


        public LSL_List llGetPrimMediaParams(int face, LSL_List rules)
        {
            m_host.AddScriptLPS(1);
            ScriptSleep(1000);
            return GetPrimMediaParams(m_host, face, rules);
        }

        // Prim Media
        public LSL_List llGetLinkMedia(int link, int face, LSL_List rules)
        {
            m_host.AddScriptLPS(1);
            ScriptSleep(1000);
            if (link == ScriptBaseClass.LINK_ROOT)
                return GetPrimMediaParams(m_host.ParentGroup.RootPart, face, rules);
            else if (link == ScriptBaseClass.LINK_THIS)
                return GetPrimMediaParams(m_host, face, rules);
            else
            {
                SceneObjectPart part = m_host.ParentGroup.GetLinkNumPart(link);
                if (null != part)
                    return GetPrimMediaParams(part, face, rules);
            }

            return new LSL_List();
        }

        private LSL_List GetPrimMediaParams(SceneObjectPart part, int face, LSL_List rules)
        {
            // LSL Spec http://wiki.secondlife.com/wiki/LlGetPrimMediaParams says to fail silently if face is invalid
            // TODO: Need to correctly handle case where a face has no media (which gives back an empty list).
            // Assuming silently fail means give back an empty list.  Ideally, need to check this.
            if (face < 0 || face > part.GetNumberOfSides() - 1)
                return new LSL_List();

            IMoapModule module = m_ScriptEngine.World.RequestModuleInterface<IMoapModule>();
            if (null == module)
                return new LSL_List();

            MediaEntry me = module.GetMediaEntry(part, face);

            // As per http://wiki.secondlife.com/wiki/LlGetPrimMediaParams
            if (null == me)
                return new LSL_List();

            List<object> res = new List<object>();

            for (int i = 0; i < rules.Length; i++)
            {
                int code = (int)rules.GetLSLIntegerItem(i);

                switch (code)
                {
                    case ScriptBaseClass.PRIM_MEDIA_ALT_IMAGE_ENABLE:
                        // Not implemented
                        res.Add((int)0);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_CONTROLS:
                        if (me.Controls == MediaControls.Standard)
                            res.Add((int)ScriptBaseClass.PRIM_MEDIA_CONTROLS_STANDARD);
                        else
                            res.Add((int)ScriptBaseClass.PRIM_MEDIA_CONTROLS_MINI);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_CURRENT_URL:
                        res.Add((string)(me.CurrentURL));
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_HOME_URL:
                        res.Add((string)(me.HomeURL));
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_AUTO_LOOP:
                        res.Add(me.AutoLoop ? ScriptBaseClass.TRUE : ScriptBaseClass.FALSE);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_AUTO_PLAY:
                        res.Add(me.AutoPlay ? ScriptBaseClass.TRUE : ScriptBaseClass.FALSE);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_AUTO_SCALE:
                        res.Add(me.AutoScale ? ScriptBaseClass.TRUE : ScriptBaseClass.FALSE);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_AUTO_ZOOM:
                        res.Add(me.AutoZoom ? ScriptBaseClass.TRUE : ScriptBaseClass.FALSE);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_FIRST_CLICK_INTERACT:
                        res.Add(me.InteractOnFirstClick ? ScriptBaseClass.TRUE : ScriptBaseClass.FALSE);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_WIDTH_PIXELS:
                        res.Add((int)(me.Width));
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_HEIGHT_PIXELS:
                        res.Add((int)(me.Height));
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_WHITELIST_ENABLE:
                        res.Add(me.EnableWhiteList ? ScriptBaseClass.TRUE : ScriptBaseClass.FALSE);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_WHITELIST:
                        if (me.WhiteList == null)
                        {
                            res.Add("");
                            break;
                        }
                        string[] urls = (string[])me.WhiteList.Clone();

                        for (int j = 0; j < urls.Length; j++)
                            urls[j] = Uri.EscapeDataString(urls[j]);

                        res.Add((string)(string.Join(", ", urls)));
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_PERMS_INTERACT:
                        res.Add((int)(me.InteractPermissions));
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_PERMS_CONTROL:
                        res.Add((int)(me.ControlPermissions));
                        break;

                    default: 
                        return new LSL_List();          // ScriptBaseClass.LSL_STATUS_MALFORMED_PARAMS;
                }
            }

            return new LSL_List(res);
        }

        public int llSetPrimMediaParams(int face, LSL_List rules)
        {
            m_host.AddScriptLPS(1);
            ScriptSleep(1000);
            return SetPrimMediaParams(m_host, face, rules);
        }

        public int llSetLinkMedia(int link, int face, LSL_List rules)
        {
            m_host.AddScriptLPS(1);
            ScriptSleep(1000);
            if (link == ScriptBaseClass.LINK_ROOT)
                return SetPrimMediaParams(m_host.ParentGroup.RootPart, face, rules);
            else if (link == ScriptBaseClass.LINK_THIS)
                return SetPrimMediaParams(m_host, face, rules);
            else
            {
                SceneObjectPart part = m_host.ParentGroup.GetLinkNumPart(link);
                if (null != part)
                    return SetPrimMediaParams(part, face, rules);
            }

            return ScriptBaseClass.LSL_STATUS_NOT_FOUND;
        }

        private int SetPrimMediaParams(SceneObjectPart part, int face, LSL_List rules)
        {
            // LSL Spec http://wiki.secondlife.com/wiki/LlSetPrimMediaParams says to fail silently if face is invalid
            // Assuming silently fail means sending back LSL_STATUS_OK.  Ideally, need to check this.
            // Don't perform the media check directly
            if (face < 0 || face > part.GetNumberOfSides() - 1)
                return ScriptBaseClass.LSL_STATUS_NOT_FOUND;

            IMoapModule module = m_ScriptEngine.World.RequestModuleInterface<IMoapModule>();
            if (null == module)
                return ScriptBaseClass.LSL_STATUS_NOT_SUPPORTED;

            MediaEntry me = module.GetMediaEntry(part, face);
            if (null == me)
                me = new MediaEntry();

            int i = 0;

            while (i < rules.Length - 1)
            {
                int code = rules.GetLSLIntegerItem(i++);

                switch (code)
                {
                    case ScriptBaseClass.PRIM_MEDIA_ALT_IMAGE_ENABLE:
                        me.EnableAlterntiveImage = (rules.GetLSLIntegerItem(i++) != 0 ? true : false);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_CONTROLS:
                        int v = rules.GetLSLIntegerItem(i++);
                        if (ScriptBaseClass.PRIM_MEDIA_CONTROLS_STANDARD == v)
                            me.Controls = MediaControls.Standard;
                        else
                            me.Controls = MediaControls.Mini;
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_CURRENT_URL:
                        me.CurrentURL = rules.GetLSLStringItem(i++);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_HOME_URL:
                        me.HomeURL = rules.GetLSLStringItem(i++);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_AUTO_LOOP:
                        me.AutoLoop = (ScriptBaseClass.TRUE == rules.GetLSLIntegerItem(i++) ? true : false);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_AUTO_PLAY:
                        me.AutoPlay = (ScriptBaseClass.TRUE == rules.GetLSLIntegerItem(i++) ? true : false);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_AUTO_SCALE:
                        me.AutoScale = (ScriptBaseClass.TRUE == rules.GetLSLIntegerItem(i++) ? true : false);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_AUTO_ZOOM:
                        me.AutoZoom = (ScriptBaseClass.TRUE == rules.GetLSLIntegerItem(i++) ? true : false);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_FIRST_CLICK_INTERACT:
                        me.InteractOnFirstClick = (ScriptBaseClass.TRUE == rules.GetLSLIntegerItem(i++) ? true : false);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_WIDTH_PIXELS:
                        me.Width = (int)rules.GetLSLIntegerItem(i++);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_HEIGHT_PIXELS:
                        me.Height = (int)rules.GetLSLIntegerItem(i++);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_WHITELIST_ENABLE:
                        me.EnableWhiteList = (ScriptBaseClass.TRUE == rules.GetLSLIntegerItem(i++) ? true : false);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_WHITELIST:
                        string[] rawWhiteListUrls = rules.GetLSLStringItem(i++).ToString().Split(new char[] { ',' });
                        List<string> whiteListUrls = new List<string>();
                        Array.ForEach(
                            rawWhiteListUrls, delegate(string rawUrl) { whiteListUrls.Add(rawUrl.Trim()); });
                        me.WhiteList = whiteListUrls.ToArray();
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_PERMS_INTERACT:
                        me.InteractPermissions = (MediaPermission)(byte)(int)rules.GetLSLIntegerItem(i++);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_PERMS_CONTROL:
                        me.ControlPermissions = (MediaPermission)(byte)(int)rules.GetLSLIntegerItem(i++);
                        break;

                    default: return ScriptBaseClass.LSL_STATUS_MALFORMED_PARAMS;
                }
            }

            module.SetMediaEntry(part, face, me);

            return ScriptBaseClass.LSL_STATUS_OK;
        }

        public int llClearPrimMedia(int face)
        {
            m_host.AddScriptLPS(1);
            ScriptSleep(1000);
            return ClearPrimMedia(m_host, face);
        }

        public int llClearLinkMedia(int link, int face)
        {
            m_host.AddScriptLPS(1);
            ScriptSleep(1000);
            if (link == ScriptBaseClass.LINK_ROOT)
                return ClearPrimMedia(m_host.ParentGroup.RootPart, face);
            else if (link == ScriptBaseClass.LINK_THIS)
                return ClearPrimMedia(m_host, face);
            else
            {
                SceneObjectPart part = m_host.ParentGroup.GetLinkNumPart(link);
                if (null != part)
                    return ClearPrimMedia(part, face);
            }

            return ScriptBaseClass.LSL_STATUS_NOT_FOUND;
        }

        private int ClearPrimMedia(SceneObjectPart part, int face)
        {
            IMoapModule module = m_ScriptEngine.World.RequestModuleInterface<IMoapModule>();
            if (null == module)
                return ScriptBaseClass.LSL_STATUS_NOT_SUPPORTED;

            if (face == ScriptBaseClass.ALL_SIDES)
            {
                for (uint i = 0; i < part.GetNumberOfSides(); i++)
                {
                    module.ClearMediaEntry(part, face);
                }
                return ScriptBaseClass.LSL_STATUS_OK;
            }

            // LSL Spec http://wiki.secondlife.com/wiki/LlClearPrimMedia says to fail silently if face is invalid
            // Assuming silently fail means sending back LSL_STATUS_OK.  Ideally, need to check this.
            // FIXME: Don't perform the media check directly
            if (face < 0 || face > part.GetNumberOfSides() - 1)
                return ScriptBaseClass.LSL_STATUS_NOT_FOUND;
            
            module.ClearMediaEntry(part, face);

            return ScriptBaseClass.LSL_STATUS_OK;
        }

        //  <remarks>
        //  <para>
        //  The .NET definition of base 64 is:
        //  <list>
        //  <item>
        //  Significant: A-Z a-z 0-9 + -
        //  </item>
        //  <item>
        //  Whitespace: \t \n \r ' '
        //  </item>
        //  <item>
        //  Valueless: =
        //  </item>
        //  <item>
        //  End-of-string: \0 or '=='
        //  </item>
        //  </list>
        //  </para>
        //  <para>
        //  Each point in a base-64 string represents
        //  a 6 bit value. A 32-bit integer can be
        //  represented using 6 characters (with some
        //  redundancy).
        //  </para>
        //  <para>
        //  LSL requires a base64 string to be 8
        //  characters in length. LSL also uses '/'
        //  rather than '-' (MIME compliant).
        //  </para>
        //  <para>
        //  RFC 1341 used as a reference (as specified
        //  by the SecondLife Wiki).
        //  </para>
        //  <para>
        //  SL do not record any kind of exception for
        //  these functions, so the string to integer
        //  conversion returns '0' if an invalid
        //  character is encountered during conversion.
        //  </para>
        //  <para>
        //  References
        //  <list>
        //  <item>
        //  http://lslwiki.net/lslwiki/wakka.php?wakka=Base64
        //  </item>
        //  <item>
        //  </item>
        //  </list>
        //  </para>
        //  </remarks>

        //  <summary>
        //  Table for converting 6-bit integers into
        //  base-64 characters
        //  </summary>

        private static readonly char[] i2ctable =
        {
            'A','B','C','D','E','F','G','H',
            'I','J','K','L','M','N','O','P',
            'Q','R','S','T','U','V','W','X',
            'Y','Z',
            'a','b','c','d','e','f','g','h',
            'i','j','k','l','m','n','o','p',
            'q','r','s','t','u','v','w','x',
            'y','z',
            '0','1','2','3','4','5','6','7',
            '8','9',
            '+','/'
        };

        //  <summary>
        //  Table for converting base-64 characters
        //  into 6-bit integers.
        //  </summary>

        private static readonly int[] c2itable =
        {
            -1,-1,-1,-1,-1,-1,-1,-1,    // 0x
            -1,-1,-1,-1,-1,-1,-1,-1,
            -1,-1,-1,-1,-1,-1,-1,-1,    // 1x
            -1,-1,-1,-1,-1,-1,-1,-1,
            -1,-1,-1,-1,-1,-1,-1,-1,    // 2x
            -1,-1,-1,63,-1,-1,-1,64,
            53,54,55,56,57,58,59,60,    // 3x
            61,62,-1,-1,-1,0,-1,-1,
            -1,1,2,3,4,5,6,7,           // 4x
            8,9,10,11,12,13,14,15,
            16,17,18,19,20,21,22,23,    // 5x
            24,25,26,-1,-1,-1,-1,-1,
            -1,27,28,29,30,31,32,33,    // 6x
            34,35,36,37,38,39,40,41,
            42,43,44,45,46,47,48,49,    // 7x
            50,51,52,-1,-1,-1,-1,-1,
            -1,-1,-1,-1,-1,-1,-1,-1,    // 8x
            -1,-1,-1,-1,-1,-1,-1,-1,
            -1,-1,-1,-1,-1,-1,-1,-1,    // 9x
            -1,-1,-1,-1,-1,-1,-1,-1,
            -1,-1,-1,-1,-1,-1,-1,-1,    // Ax
            -1,-1,-1,-1,-1,-1,-1,-1,
            -1,-1,-1,-1,-1,-1,-1,-1,    // Bx
            -1,-1,-1,-1,-1,-1,-1,-1,
            -1,-1,-1,-1,-1,-1,-1,-1,    // Cx
            -1,-1,-1,-1,-1,-1,-1,-1,
            -1,-1,-1,-1,-1,-1,-1,-1,    // Dx
            -1,-1,-1,-1,-1,-1,-1,-1,
            -1,-1,-1,-1,-1,-1,-1,-1,    // Ex
            -1,-1,-1,-1,-1,-1,-1,-1,
            -1,-1,-1,-1,-1,-1,-1,-1,    // Fx
            -1,-1,-1,-1,-1,-1,-1,-1
        };

        //  <summary>
        //  Converts a 32-bit integer into a Base64
        //  character string. Base64 character strings
        //  are always 8 characters long. All iinteger
        //  values are acceptable.
        //  </summary>
        //  <param name="number">
        //  32-bit integer to be converted.
        //  </param>
        //  <returns>
        //  8 character string. The 1st six characters
        //  contain the encoded number, the last two
        //  characters are padded with "=".
        //  </returns>

        public string llIntegerToBase64(int number)
        {
            // uninitialized string
            char[] imdt = new char[8];

            // Manually unroll the loop

            imdt[7] = '=';
            imdt[6] = '=';
            imdt[5] = i2ctable[number << 4 & 0x3F];
            imdt[4] = i2ctable[number >> 2 & 0x3F];
            imdt[3] = i2ctable[number >> 8 & 0x3F];
            imdt[2] = i2ctable[number >> 14 & 0x3F];
            imdt[1] = i2ctable[number >> 20 & 0x3F];
            imdt[0] = i2ctable[number >> 26 & 0x3F];

            return new string(imdt);
        }

        //  <summary>
        //  Converts an eight character base-64 string
        //  into a 32-bit integer.
        //  </summary>
        //  <param name="str">
        //  8 characters string to be converted. Other
        //  length strings return zero.
        //  </param>
        //  <returns>
        //  Returns an integer representing the
        //  encoded value providedint he 1st 6
        //  characters of the string.
        //  </returns>
        //  <remarks>
        //  This is coded to behave like LSL's
        //  implementation (I think), based upon the
        //  information available at the Wiki.
        //  If more than 8 characters are supplied,
        //  zero is returned.
        //  If a NULL string is supplied, zero will
        //  be returned.
        //  If fewer than 6 characters are supplied, then
        //  the answer will reflect a partial
        //  accumulation.
        //  <para>
        //  The 6-bit segments are
        //  extracted left-to-right in big-endian mode,
        //  which means that segment 6 only contains the
        //  two low-order bits of the 32 bit integer as
        //  its high order 2 bits. A short string therefore
        //  means loss of low-order information. E.g.
        //
        //  |<---------------------- 32-bit integer ----------------------->|<-Pad->|
        //  |<--Byte 0----->|<--Byte 1----->|<--Byte 2----->|<--Byte 3----->|<-Pad->|
        //  |3|3|2|2|2|2|2|2|2|2|2|2|1|1|1|1|1|1|1|1|1|1| | | | | | | | | | |P|P|P|P|
        //  |1|0|9|8|7|6|5|4|3|2|1|0|9|8|7|6|5|4|3|2|1|0|9|8|7|6|5|4|3|2|1|0|P|P|P|P|
        //  |  str[0]   |  str[1]   |  str[2]   |  str[3]   |  str[4]   |  str[6]   |
        //
        //  </para>
        //  </remarks>

        public int llBase64ToInteger(string str)
        {
            int number = 0;
            int digit;

            

            //    Require a well-fromed base64 string

            if (str.Length > 8)
                return 0;

            //    The loop is unrolled in the interests
            //    of performance and simple necessity.
            //
            //    MUST find 6 digits to be well formed
            //      -1 == invalid
            //       0 == padding

            if ((digit = c2itable[str[0]]) <= 0)
            {
                return digit < 0 ? (int)0 : number;
            }
            number += --digit << 26;

            if ((digit = c2itable[str[1]]) <= 0)
            {
                return digit < 0 ? (int)0 : number;
            }
            number += --digit << 20;

            if ((digit = c2itable[str[2]]) <= 0)
            {
                return digit < 0 ? (int)0 : number;
            }
            number += --digit << 14;

            if ((digit = c2itable[str[3]]) <= 0)
            {
                return digit < 0 ? (int)0 : number;
            }
            number += --digit << 8;

            if ((digit = c2itable[str[4]]) <= 0)
            {
                return digit < 0 ? (int)0 : number;
            }
            number += --digit << 2;

            if ((digit = c2itable[str[5]]) <= 0)
            {
                return digit < 0 ? (int)0 : number;
            }
            number += --digit >> 4;

            // ignore trailing padding

            return number;
        }

        public float llGetGMTclock()
        {
            return (float)DateTime.UtcNow.TimeOfDay.TotalSeconds;
        }

        public string llGetHTTPHeader(string request_id, string header)
        {
            UUID requestIdAsUuid;

            if (!UUID.TryParse(request_id, out requestIdAsUuid))
            {
                return String.Empty;
            }

            if (m_UrlModule != null)
                return m_UrlModule.GetHttpHeader(requestIdAsUuid, header);

            return String.Empty;
        }


        public string llGetSimulatorHostname()
        {
            return llGetEnv("simulator_hostname");
        }

        //  <summary>
        //  Scan the string supplied in 'src' and
        //  tokenize it based upon two sets of
        //  tokenizers provided in two lists,
        //  separators and spacers.
        //  </summary>
        //
        //  <remarks>
        //  Separators demarcate tokens and are
        //  elided as they are encountered. Spacers
        //  also demarcate tokens, but are themselves
        //  retained as tokens.
        //
        //  Both separators and spacers may be arbitrarily
        //  long strings. i.e. ":::".
        //
        //  The function returns an ordered list
        //  representing the tokens found in the supplied
        //  sources string. If two successive tokenizers
        //  are encountered, then a NULL entry is added
        //  to the list.
        //
        //  It is a precondition that the source and
        //  toekizer lisst are non-null. If they are null,
        //  then a null pointer exception will be thrown
        //  while their lengths are being determined.
        //
        //  A small amount of working memoryis required
        //  of approximately 8*#tokenizers.
        //
        //  There are many ways in which this function
        //  can be implemented, this implementation is
        //  fairly naive and assumes that when the
        //  function is invooked with a short source
        //  string and/or short lists of tokenizers, then
        //  performance will not be an issue.
        //
        //  In order to minimize the perofrmance
        //  effects of long strings, or large numbers
        //  of tokeizers, the function skips as far as
        //  possible whenever a toekenizer is found,
        //  and eliminates redundant tokenizers as soon
        //  as is possible.
        //
        //  The implementation tries to avoid any copying
        //  of arrays or other objects.
        //  </remarks>
        //TODO:  This function sucks.  Also the LSL_List add implementation sucks
        public LSL_List llParseStringKeepNulls(string src, LSL_List separators, LSL_List spacers)
        {
            List<Object> separray = new List<object>(separators.Data);
            List<Object> spcarray = new List<Object>(spacers.Data);

            //empty strings need to be removed
            List<Object> newSepArray = new List<object>();
            foreach (object obj in separray)
            {
                string str = (string)obj;
                if (!String.IsNullOrEmpty(str))
                {
                    newSepArray.Add(obj);
                }
            }

            separray = newSepArray;

            //empty strings need to be removed
            List<Object> newSpcArray = new List<object>();
            foreach (object obj in spcarray)
            {
                string str = (string)obj;
                if (!String.IsNullOrEmpty(str))
                {
                    newSpcArray.Add(obj);
                }
            }

            spcarray = newSpcArray;

            int beginning = 0;
            int srclen = src.Length;
            int seplen = separray.Count;
            int spclen = spcarray.Count;

            int mlen = seplen + spclen;

            int[] offset = new int[mlen + 1];
            bool[] active = new bool[mlen];

            int best;
            int j;


            //    Initial capacity reduces resize cost

            List<object> tokens = new List<object>();

            

            //    All entries are initially valid

            for (int i = 0; i < mlen; i++)
                active[i] = true;

            offset[mlen] = srclen;

            while (beginning < srclen)
            {

                best = mlen;    // as bad as it gets

                //    Scan for separators

                for (j = 0; j < seplen; j++)
                {
                    if (active[j])
                    {
                        // scan all of the markers
                        if ((offset[j] = src.IndexOf(separray[j].ToString(), beginning)) == -1)
                        {
                            // not present at all
                            active[j] = false;
                        }
                        else
                        {
                            // present and correct
                            if (offset[j] < offset[best])
                            {
                                // closest so far
                                best = j;
                                if (offset[best] == beginning)
                                    break;
                            }
                        }
                    }
                }

                //    Scan for spacers

                if (offset[best] != beginning)
                {
                    for (j = seplen; (j < mlen) && (offset[best] > beginning); j++)
                    {
                        if (active[j])
                        {
                            // scan all of the markers
                            if ((offset[j] = src.IndexOf(spcarray[j - seplen].ToString(), beginning)) == -1)
                            {
                                // not present at all
                                active[j] = false;
                            }
                            else
                            {
                                // present and correct
                                if (offset[j] < offset[best])
                                {
                                    // closest so far
                                    best = j;
                                }
                            }
                        }
                    }
                }

                //    This is the normal exit from the scanning loop

                if (best == mlen)
                {
                    // no markers were found on this pass
                    // so we're pretty much done
                    tokens.Add((string)(src.Substring(beginning, srclen - beginning)));
                    break;
                }

                //    Otherwise we just add the newly delimited token
                //    and recalculate where the search should continue.

                tokens.Add((string)(src.Substring(beginning, offset[best] - beginning)));

                if (best < seplen)
                {
                    beginning = offset[best] + (separray[best].ToString()).Length;
                }
                else
                {
                    beginning = offset[best] + (spcarray[best - seplen].ToString()).Length;
                    tokens.Add((string)(spcarray[best - seplen].ToString()));
                }
            }

            //    This an awkward an not very intuitive boundary case. If the
            //    last substring is a tokenizer, then there is an implied trailing
            //    null list entry. Hopefully the single comparison will not be too
            //    arduous. Alternatively the 'break' could be replced with a return
            //    but that's shabby programming.

            if (beginning == srclen)
            {
                if (srclen != 0)
                    tokens.Add((string)(String.Empty));
            }

            return new LSL_List(tokens);
        }

        public int llGetObjectPermMask(int mask)
        {
            int permmask = 0;

            if (mask == ScriptBaseClass.MASK_BASE)//0
            {
                permmask = (int)m_host.BaseMask;
            }

            else if (mask == ScriptBaseClass.MASK_OWNER)//1
            {
                permmask = (int)m_host.OwnerMask;
            }

            else if (mask == ScriptBaseClass.MASK_GROUP)//2
            {
                permmask = (int)m_host.GroupMask;
            }

            else if (mask == ScriptBaseClass.MASK_EVERYONE)//3
            {
                permmask = (int)m_host.EveryoneMask;
            }

            else if (mask == ScriptBaseClass.MASK_NEXT)//4
            {
                permmask = (int)m_host.NextOwnerMask;
            }

            return permmask;
        }

        public void llSetObjectPermMask(int mask, int value)
        {
            if (m_ScriptEngine.Config.GetBoolean("AllowGodFunctions", false))
            {
                if (World.Permissions.CanRunConsoleCommand(m_host.OwnerID))
                {
                    if (mask == ScriptBaseClass.MASK_BASE)//0
                    {
                        m_host.BaseMask = (uint)value;
                    }

                    else if (mask == ScriptBaseClass.MASK_OWNER)//1
                    {
                        m_host.OwnerMask = (uint)value;
                    }

                    else if (mask == ScriptBaseClass.MASK_GROUP)//2
                    {
                        m_host.GroupMask = (uint)value;
                    }

                    else if (mask == ScriptBaseClass.MASK_EVERYONE)//3
                    {
                        m_host.EveryoneMask = (uint)value;
                    }

                    else if (mask == ScriptBaseClass.MASK_NEXT)//4
                    {
                        m_host.NextOwnerMask = (uint)value;
                    }
                }
            }
        }

        public int GetInventoryPermMask(SceneObjectPart part, string item, int mask)
        {
            lock (part.TaskInventory)
            {
                foreach (KeyValuePair<UUID, TaskInventoryItem> inv in part.TaskInventory)
                {
                    if (inv.Value.Name == item)
                    {
                        switch (mask)
                        {
                            case 0:
                                return (int)inv.Value.BasePermissions;
                            case 1:
                                return (int)inv.Value.CurrentPermissions;
                            case 2:
                                return (int)inv.Value.GroupPermissions;
                            case 3:
                                return (int)inv.Value.EveryonePermissions;
                            case 4:
                                return (int)inv.Value.NextPermissions;
                        }
                    }
                }
            }

            return -1;
        }
        public int llGetInventoryPermMask(string item, int mask)
        {
            return GetInventoryPermMask(m_host, item, mask);
        }
        public int iwGetLinkInventoryPermMask(int linknumber, string item, int mask)
        {
            int rc = -1;

            var parts = GetLinkPrimsOnly(linknumber);
            foreach (SceneObjectPart part in parts)
                rc &= GetInventoryPermMask(part, item, mask);

            return rc;
        }

        public void llSetInventoryPermMask(string item, int mask, int value)
        {
            NotImplemented("llSetInventoryPermMask");
        }

        public string GetInventoryCreator(SceneObjectPart part, string item)
        {
            lock (part.TaskInventory)
            {
                foreach (KeyValuePair<UUID, TaskInventoryItem> inv in part.TaskInventory)
                {
                    if (inv.Value.Name == item)
                    {
                        return inv.Value.CreatorID.ToString();
                    }
                }
            }

            if (part.LinkNum == 0)
                llSay(0, "No item named '" + item + "'");
            else
                llSay(0, "No item named '" + item + "' in link "+part.LinkNum.ToString());

            return String.Empty;
        }
        public string llGetInventoryCreator(string item)
        {
            return GetInventoryCreator(m_host, item);
        }
        public string iwGetLinkInventoryCreator(int linknumber, string item)
        {
            var part = GetLinkOnePrimOnly(linknumber);
            if (part == null)
                return UUID.Zero.ToString();

            return GetInventoryCreator(part, item);
        }

        public void llOwnerSay(string msg)
        {
            World.SimChat(msg, ChatTypeEnum.Owner, 0, m_host, UUID.Zero, true);

            ScriptSleep(15);
        }

        public string llRequestSecureURL()
        {
            
            if (m_UrlModule != null)
                return m_UrlModule.RequestSecureURL(m_ScriptEngine.ScriptModule, m_host, m_itemID).ToString();
            return UUID.Zero.ToString();
        }

        public string llRequestSimulatorData(string simulator, int data)
        {
            try
            {
                string reply = String.Empty;

                RegionInfo info = m_ScriptEngine.World.RequestClosestRegion(simulator);

                switch (data)
                {
                    case ScriptBaseClass.DATA_SIM_POS:
                        if (info == null)
                        {
                            //ScriptSleep(1000);
                            return UUID.Zero.ToString();
                        }
                        reply = new LSL_Vector(
                            info.RegionLocX * Constants.RegionSize,
                            info.RegionLocY * Constants.RegionSize,
                            0).ToString();
                        break;
                    case ScriptBaseClass.DATA_SIM_STATUS:
                        if (info != null)
                            reply = "up"; // Duh!
                        else
                            reply = "unknown";
                        break;
                    case ScriptBaseClass.DATA_SIM_RATING:
                        if (info == null)
                        {
                            // ScriptSleep(1000);
                            return UUID.Zero.ToString();
                        }
                        int access = info.RegionSettings.Maturity;
                        if (access == 0)
                            reply = "PG";
                        else if (access == 1)
                            reply = "MATURE";
                        else if (access == 2)
                            reply = "ADULT";
                        else
                            reply = "UNKNOWN";
                        break;
                    case ScriptBaseClass.DATA_SIM_RELEASE:
                        if (simulator == World.RegionInfo.RegionName)
                            reply = m_host.ParentGroup.Scene.GetSimulatorVersion();
                        else
                            reply = VersionInfo.SoftwareName;
                        break;
                    default:
                        // ScriptSleep(1000);
                        return UUID.Zero.ToString(); // Raise no event
                }
                UUID rq = UUID.Random();

                UUID tid = AsyncCommands.
                    DataserverPlugin.RegisterRequest(m_localID, m_itemID, rq.ToString());

                AsyncCommands.
                    DataserverPlugin.DataserverReply(rq.ToString(), reply);

                ScriptSleep(1000);
                return tid.ToString();
            }
            catch (Exception e)
            {
                m_log.Error("[LSL]: llRequestSimulatorData" + e.ToString());
                return UUID.Zero.ToString();
            }
        }
        public string llRequestURL()
        {
            
            if (m_UrlModule != null)
                return m_UrlModule.RequestURL(m_ScriptEngine.ScriptModule, m_host, m_itemID).ToString();

            return UUID.Zero.ToString();
        }

        public void llForceMouselook(int mouselook)
        {
            
            m_host.SetForceMouselook(mouselook != 0);
        }

        private float Kilograms2Lindograms(float kg)
        {
            // This is supposed to convert kilograms (kg) to "Lindograms" (Lg).
            // Observed behavior in SL matches the LSL wiki reports of 1Lg == 100kb
            // Returning kg/100.0 would break existing content in subtle ways 
            // (things that moved would barely move or stop moving).
            // For now, return the same number until we know how to
            // do this correctly and in a backwards-compatible way.
            return kg;      // kg / 100.0;
        }

        public float GetObjectMassKG(string id)
        {
            UUID key = new UUID();
            if (UUID.TryParse(id, out key))
            {
                try
                {
                    SceneObjectPart obj = World.GetSceneObjectPart(key);
                    if (obj != null)
                        return (float)obj.GetMass();
                    // the object is null so the key is for an avatar
                    ScenePresence avatar = World.GetScenePresence(key);
                    if (avatar != null)
                    {
                        if (avatar.IsChildAgent)
                            return 0.01f;   // as observed in SL
                        else
                        {
                            // We don't have a mass without a PhysActor, so we need to come up something.
                            // For now fix the null reference crash and use same as child agent.
                            PhysicsActor pa = avatar.PhysicsActor;
                            if (pa == null)
                                return 0.01f;
                            return (float)pa.Mass;
                        }
                    }
                }
                catch (KeyNotFoundException)
                {
                    return 0; // The Object/Agent not in the region so just return zero
                }
            }
            return 0;
        }

        public float llGetObjectMass(string id)
        {
            float kg = GetObjectMassKG(id);
            return Kilograms2Lindograms(kg);
        }

        public float iwGetObjectMassMKS(string id)
        {
            return GetObjectMassKG(id);
        }

        public float llGetMass()
        {
            return Kilograms2Lindograms(llGetMassMKS());
        }

        public float llGetMassMKS()
        {
            // returns kilograms, same as physActor.Mass via part.GetMass()
            return m_host.GetMass();
        }

        /// <summary>
        /// illListReplaceList removes the sub-list defined by the inclusive indices
        /// start and end and inserts the src list in its place. The inclusive
        /// nature of the indices means that at least one element must be deleted
        /// if the indices are within the bounds of the existing list. I.e. 2,2
        /// will remove the element at index 2 and replace it with the source
        /// list. Both indices may be negative, with the usual interpretation. An
        /// interesting case is where end is lower than start. As these indices
        /// bound the list to be removed, then 0->end, and start->lim are removed
        /// and the source list is added as a suffix.
        /// </summary>

        public LSL_List llListReplaceList(LSL_List dest, LSL_List src, int start, int end)
        {
            LSL_List pref = null;

            

            // Note that although we have normalized, both
            // indices could still be negative.
            if (start < 0)
            {
                start = start + dest.Length;
            }

            if (end < 0)
            {
                end = end + dest.Length;
            }
            // The comventional case, remove a sequence starting with
            // start and ending with end. And then insert the source
            // list.
            if (start <= end)
            {
                // If greater than zero, then there is going to be a
                // surviving prefix. Otherwise the inclusive nature
                // of the indices mean that we're going to add the
                // source list as a prefix.
                if (start > 0)
                {
                    pref = dest.GetSublist(0, start - 1);
                    // Only add a suffix if there is something
                    // beyond the end index (it's inclusive too).
                    if (end + 1 < dest.Length)
                    {
                        return pref + src + dest.GetSublist(end + 1, -1);
                    }
                    else
                    {
                        return pref + src;
                    }
                }
                // If start is less than or equal to zero, then
                // the new list is simply a prefix. We still need to
                // figure out any necessary surgery to the destination
                // based upon end. Note that if end exceeds the upper
                // bound in this case, the entire destination list
                // is removed.
                else
                {
                    if (end + 1 < dest.Length)
                    {
                        return src + dest.GetSublist(end + 1, -1);
                    }
                    else
                    {
                        return src;
                    }
                }
            }
            // Finally, if start > end, we strip away a prefix and
            // a suffix, to leave the list that sits <between> ens
            // and start, and then tag on the src list. AT least
            // that's my interpretation. We can get sublist to do
            // this for us. Note that one, or both of the indices
            // might have been negative.
            else
            {
                return dest.GetSublist(end + 1, start - 1) + src;
            }
        }

        public void llLoadURL(string avatar_id, string message, string url)
        {
            UUID avatar;
            IDialogModule dm = World.RequestModuleInterface<IDialogModule>();
            if (null != dm)
                if (UUID.TryParse(avatar_id, out avatar))
                    dm.SendUrlToUser(
                        avatar, m_host.Name, m_host.UUID, m_host.ObjectOwner, false, message, url);

            ScriptSleep(100);
        }

        public void llParcelMediaCommandList(LSL_List commandList)
        {
            // TODO: Not implemented yet (missing in libomv?):
            //  PARCEL_MEDIA_COMMAND_LOOP_SET    float loop      Use this to get or set the parcel's media loop duration. (1.19.1 RC0 or later)

            // according to the docs, this command only works if script owner and land owner are the same
            // lets add estate owners and gods, too, and use the generic permission check.
            ILandObject landObject = World.LandChannel.GetLandObject(m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y);
            if (!World.Permissions.CanEditParcel(m_host.ObjectOwner, landObject, GroupPowers.ChangeMedia)) return;

            bool update = false; // send a ParcelMediaUpdate (and possibly change the land's media URL)? 
            byte loop = 0;

            LandData landData = landObject.landData;
            string url = landData.MediaURL;
            UUID textureID = landData.MediaID;
            bool autoAlign = landData.MediaAutoScale != 0;

            string mediaType = landData.MediaType;
            int width = landData.MediaWidth;
            int height = landData.MediaHeight;
            string description = landData.MediaDescription;

            ParcelMediaCommandEnum? commandToSend = null;
            float time = 0.0f; // default is from start

            ScenePresence presence = null;

            for (int i = 0; i < commandList.Data.Length; i++)
            {
                ParcelMediaCommandEnum command = (ParcelMediaCommandEnum)commandList.GetLSLIntegerItem(i);
                switch (command)
                {
                    case ParcelMediaCommandEnum.Agent:
                        // we send only to one agent
                        if ((i + 1) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is string)
                            {
                                UUID agentID;
                                if (UUID.TryParse((string)commandList.Data[i + 1], out agentID))
                                {
                                    presence = World.GetScenePresence(agentID);
                                }
                            }
                            else ScriptShoutError("The argument of PARCEL_MEDIA_COMMAND_AGENT must be a key");
                            ++i;
                        }
                        break;

                    case ParcelMediaCommandEnum.Loop:
                        loop = 1;
                        commandToSend = command;
                        update = true; //need to send the media update packet to set looping
                        break;

                    case ParcelMediaCommandEnum.Play:
                        loop = 0;
                        commandToSend = command;
                        update = true; //need to send the media update packet to make sure it doesn't loop
                        break;

                    case ParcelMediaCommandEnum.Pause:
                    case ParcelMediaCommandEnum.Stop:
                    case ParcelMediaCommandEnum.Unload:
                        commandToSend = command;
                        break;

                    case ParcelMediaCommandEnum.Url:
                        if ((i + 1) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is string)
                            {
                                url = (string)commandList.Data[i + 1];
                                update = true;
                            }
                            else ScriptShoutError("The argument of PARCEL_MEDIA_COMMAND_URL must be a string.");
                            ++i;
                        }
                        break;

                    case ParcelMediaCommandEnum.Texture:
                        if ((i + 1) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is string)
                            {
                                string texture = (string)commandList.Data[i + 1];
                                if (!UUID.TryParse(texture, out textureID))
                                    textureID = UUID.Zero;
                                update = true;
                            }
                            else ScriptShoutError("The argument of PARCEL_MEDIA_COMMAND_TEXTURE must be a string or key.");
                            ++i;
                        }
                        break;

                    case ParcelMediaCommandEnum.Time:
                        if ((i + 1) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is float)
                            {
                                time = (float)commandList.Data[i + 1];
                            }
                            else ScriptShoutError("The argument of PARCEL_MEDIA_COMMAND_TIME must be a float.");
                            ++i;
                        }
                        break;

                    case ParcelMediaCommandEnum.AutoAlign:
                        if ((i + 1) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is int)
                            {
                                autoAlign = (int)commandList.Data[i + 1] == 1;
                                update = true;
                            }

                            else ScriptShoutError("The argument of PARCEL_MEDIA_COMMAND_AUTO_ALIGN must be an integer.");
                            ++i;
                        }
                        break;

                    case ParcelMediaCommandEnum.Type:
                        if ((i + 1) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is string)
                            {
                                mediaType = (string)commandList.Data[i + 1];
                                update = true;
                            }
                            else ScriptShoutError("The argument of PARCEL_MEDIA_COMMAND_TYPE must be a string.");
                            ++i;
                        }
                        break;

                    case ParcelMediaCommandEnum.Desc:
                        if ((i + 1) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is string)
                            {
                                description = (string)commandList.Data[i + 1];
                                update = true;
                            }
                            else ScriptShoutError("The argument of PARCEL_MEDIA_COMMAND_DESC must be a string.");
                            ++i;
                        }
                        break;

                    case ParcelMediaCommandEnum.Size:
                        if ((i + 2) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is int)
                            {
                                if (commandList.Data[i + 2] is int)
                                {
                                    width = (int)commandList.Data[i + 1];
                                    height = (int)commandList.Data[i + 2];
                                    update = true;
                                }
                                else ScriptShoutError("The second argument of PARCEL_MEDIA_COMMAND_SIZE must be an integer.");
                            }
                            else ScriptShoutError("The first argument of PARCEL_MEDIA_COMMAND_SIZE must be an integer.");
                            i += 2;
                        }
                        break;

                    default:
                        NotImplemented("llParcelMediaCommandList parameter not supported yet: " + Enum.Parse(typeof(ParcelMediaCommandEnum), commandList.Data[i].ToString()).ToString());
                        break;
                }//end switch
            }//end for

            // if we didn't get a presence, we send to all and change the url
            // if we did get a presence, we only send to the agent specified, and *don't change the land settings*!

            // did something important change or do we only start/stop/pause?
            if (update)
            {
                if (presence == null)
                {
                    // we send to all
                    landData.MediaID = textureID;
                    landData.MediaAutoScale = autoAlign ? (byte)1 : (byte)0;
                    landData.MediaDescription = description;
                    landData.MediaWidth = width;
                    landData.MediaHeight = height;
                    landData.MediaType = mediaType;

                    // do that one last, it will cause a ParcelPropertiesUpdate
                    landObject.SetMediaUrl(url);

                    // now send to all (non-child) agents
                    List<ScenePresence> agents = World.GetAvatars();
                    foreach (ScenePresence agent in agents)
                    {
                        if (agent.IsDeleted || agent.IsInTransit)
                            continue;
                        if (agent.currentParcelUUID == landData.GlobalID)
                            agent.ControllingClient.SendParcelMediaUpdate(landData.MediaURL,
                                                                      landData.MediaID,
                                                                      landData.MediaAutoScale,
                                                                      mediaType,
                                                                      description,
                                                                      width, height,
                                                                      loop);
                    }
                }
                else if (!presence.IsChildAgent)
                {
                    // we only send to one (root) agent
                    if ((!presence.IsDeleted) && (!presence.IsInTransit))
                        if (presence.currentParcelUUID == landData.GlobalID)
                            presence.ControllingClient.SendParcelMediaUpdate(url,
                                                                     textureID,
                                                                     autoAlign ? (byte)1 : (byte)0,
                                                                     mediaType,
                                                                     description,
                                                                     width, height,
                                                                     loop);
                }
            }

            if (commandToSend != null)
            {
                // the commandList contained a start/stop/... command, too
                if (presence == null)
                {
                    // send to all (non-child) agents
                    List<ScenePresence> agents = World.GetAvatars();
                    foreach (ScenePresence agent in agents)
                    {
                        if (agent.IsDeleted || agent.IsInTransit)
                            continue;
                        if (agent.currentParcelUUID == landData.GlobalID)
                            agent.ControllingClient.SendParcelMediaCommand(0x4, // TODO what is this?
                                                                       (ParcelMediaCommandEnum)commandToSend,
                                                                       time);
                    }
                }
                else if (!presence.IsChildAgent)
                {
                    if ((!presence.IsDeleted) && (!presence.IsInTransit))
                        if (presence.currentParcelUUID == landData.GlobalID)
                            presence.ControllingClient.SendParcelMediaCommand(0x4, // TODO what is this?
                                                                      (ParcelMediaCommandEnum)commandToSend,
                                                                      time);
                }
            }
            
            ScriptSleep(2000);
        }

        public LSL_List llParcelMediaQuery(LSL_List aList)
        {
            List<object> list = new List<object>();

            Vector3 pos = m_host.AbsolutePosition;
            ILandObject landObject = World.LandChannel.GetLandObject(pos.X, pos.Y);
            // according to the docs, this command only works if script owner and land owner are the same
            // lets add estate owners and gods, too, and use the generic permission check.
            if (!World.Permissions.CanEditParcel(m_host.OwnerID, landObject, GroupPowers.ChangeMedia))
                return new LSL_List(list);

            //TO DO: make the implementation for the missing commands
            //PARCEL_MEDIA_COMMAND_TEXTURE     key uuid        Use this to get or set the parcel's media texture.
            //PARCEL_MEDIA_COMMAND_URL         string url      Used to get or set the parcel's media url.
            //PARCEL_MEDIA_COMMAND_TYPE        string mime_type        Use this to get or set the parcel media MIME type (e.g. "text/html"). (1.19.1 RC0 or later)
            //PARCEL_MEDIA_COMMAND_SIZE        integer x, integer y    Use this to get or set the parcel media pixel resolution. (1.19.1 RC0 or later)
            //PARCEL_MEDIA_COMMAND_DESC        string desc     Use this to get or set the parcel media description. (1.19.1 RC0 or later)
            //PARCEL_MEDIA_COMMAND_LOOP_SET    float loop      Use this to get or set the parcel's media loop duration. (1.19.1 RC0 or later)
            for (int i = 0; i < aList.Data.Length; i++)
            {

                if (aList.Data[i] != null)
                {
                    switch ((ParcelMediaCommandEnum)aList.GetLSLIntegerItem(i))
                    {
                        case ParcelMediaCommandEnum.Url:
                            list.Add((string)(World.GetLandData(m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y).MediaURL));
                            break;
                        case ParcelMediaCommandEnum.Desc:
                            list.Add((string)(World.GetLandData(m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y).MediaDescription));
                            break;
                        case ParcelMediaCommandEnum.Texture:
                            list.Add((string)(World.GetLandData(m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y).MediaID.ToString()));
                            break;
                        case ParcelMediaCommandEnum.Type:
                            list.Add((string)(World.GetLandData(m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y).MediaType));
                            break;
                        case ParcelMediaCommandEnum.Size:
                            list.Add(World.GetLandData(m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y).MediaWidth);
                            list.Add(World.GetLandData(m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y).MediaHeight);
                            break;
                        default:
                            ParcelMediaCommandEnum mediaCommandEnum = ParcelMediaCommandEnum.Url;
                            NotImplemented("llParcelMediaQuery parameter do not supported yet: " + Enum.Parse(mediaCommandEnum.GetType(), aList.Data[i].ToString()).ToString());
                            break;
                    }

                }
            }
            ScriptSleep(2000);
            return new LSL_List(list);
        }

        public int llModPow(int a, int b, int c)
        {
            
            Int64 tmp = 0;
            Math.DivRem(Convert.ToInt64(Math.Pow(a, b)), c, out tmp);
            // ScriptSleep(1000);
            return Convert.ToInt32(tmp);
        }

        public int GetInventoryPrice(SceneObjectPart part, string name)
        {
            lock (part.TaskInventory)
            {
                foreach(KeyValuePair<UUID, TaskInventoryItem> inv in part.TaskInventory)
                {
                    if(inv.Value.Name == name)
                    {
                        //return inv.Value.SalePrice
                    }
                }
            }
            return 0;
        }

        public int GetInventoryType(SceneObjectPart part, string name)
        {
            lock (part.TaskInventory)
            {
                foreach (KeyValuePair<UUID, TaskInventoryItem> inv in part.TaskInventory)
                {
                    if (inv.Value.Name == name)
                    {
                        return inv.Value.Type;
                    }
                }
            }

            return ScriptBaseClass.INVENTORY_NONE;
        }
        public int llGetInventoryType(string name)
        {
            return GetInventoryType(m_host, name);
        }
        public int iwGetLinkInventoryType(int linknumber, string name)
        {
            var part = GetLinkOnePrimOnly(linknumber);
            if (part == null)
                return ScriptBaseClass.INVENTORY_NONE;

            return GetInventoryType(part, name);
        }

        public void llSetPayPrice(int price, LSL_List quick_pay_buttons)
        {
            m_host.ParentGroup.RootPart.PayPrice[0] = price;

            for (int i = 0; i < 4; i++)
            {
                if (quick_pay_buttons.Length > i)
                {
                    m_host.ParentGroup.RootPart.PayPrice[i + 1] = quick_pay_buttons.GetLSLIntegerItem(i);
                }
                else
                {
                    m_host.ParentGroup.RootPart.PayPrice[i + 1] = SceneObjectPart.PAY_HIDE;
                }
            }

            m_host.ParentGroup.HasGroupChanged = true;
        }

        public LSL_Vector llGetCameraPos()
        {
            UUID invItemID = InventorySelf();

            if (invItemID == UUID.Zero)
                return new LSL_Vector();

            TaskInventoryItem item;
            lock (m_host.TaskInventory)
            {
                if (!m_host.TaskInventory.ContainsKey(invItemID))
                    return new LSL_Vector();
                item = m_host.TaskInventory[invItemID];
            }

            if (item.PermsGranter == UUID.Zero)
                return new LSL_Vector();

            if (!CheckRuntimePerms(item, item.PermsGranter, ScriptBaseClass.PERMISSION_TRACK_CAMERA))
            {
                ScriptShoutError("No permissions to track the camera");
                return new LSL_Vector();
            }

            ScenePresence presence = World.GetScenePresence(item.PermsGranter);
            if (presence != null)
            {
                LSL_Vector pos = new LSL_Vector(presence.CameraPosition.X, presence.CameraPosition.Y, presence.CameraPosition.Z);
                return pos;
            }
            return new LSL_Vector();
        }

        public LSL_Rotation llGetCameraRot()
        {
            UUID invItemID = InventorySelf();
            if (invItemID == UUID.Zero)
                return new LSL_Rotation();

            TaskInventoryItem item;
            lock (m_host.TaskInventory)
            {
                if (!m_host.TaskInventory.ContainsKey(invItemID))
                    return new LSL_Rotation();
                item = m_host.TaskInventory[invItemID];
            }

            if (item.PermsGranter == UUID.Zero)
                return new LSL_Rotation();

            if (!CheckRuntimePerms(item, item.PermsGranter, ScriptBaseClass.PERMISSION_TRACK_CAMERA))
            {
                ScriptShoutError("No permissions to track the camera");
                return new LSL_Rotation();
            }

            ScenePresence presence = World.GetScenePresence(item.PermsGranter);
            if (presence != null)
            {
                return new LSL_Rotation(presence.CameraRotation.X, presence.CameraRotation.Y, presence.CameraRotation.Z, presence.CameraRotation.W);
            }

            return new LSL_Rotation();
        }

        /// <summary>
        /// The SL implementation does nothing, it is deprecated
        /// This duplicates SL
        /// </summary>
        public void llSetPrimURL(string url)
        {
            
            // ScriptSleep(2000);
        }

        /// <summary>
        /// The SL implementation shouts an error, it is deprecated
        /// This duplicates SL
        /// </summary>
        public void llRefreshPrimURL()
        {
            
            ScriptShoutError("llRefreshPrimURL - not yet supported");
            // ScriptSleep(20000);
        }

        //Returns true if the URL's format is valid.
        public int iwValidateURL(string url)
        {
            Uri uriResult;
            bool ret = Uri.TryCreate(url, UriKind.Absolute, out uriResult) && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
            return Convert.ToInt32(ret);
        }

        public string llEscapeURL(string url)
        {
            try
            {
                return Util.EscapeUriDataStringRfc3986(url);
            }
            catch (Exception ex)
            {
                return "llEscapeURL: " + ex.ToString();
            }
        }

        public string llUnescapeURL(string url)
        {
            try
            {
                return Uri.UnescapeDataString(url);
            }
            catch (Exception ex)
            {
                return "llUnescapeURL: " + ex.ToString();
            }
        }

        public void llMapDestination(string simname, LSL_Vector pos, LSL_Vector lookAt)
        {
            UUID targetAvatar = UUID.Zero;
            VM.DetectVariables detectedParams = _thisScript.ScriptState.GetDetectVariables(0);

            // Figure out who to apply this to.
            if (detectedParams != null) // e.g. touch_start, someone clicked
            {
                targetAvatar = UUID.Parse(detectedParams.Key);
            }
            else
            {
                if (m_host.IsAttachment)
                    targetAvatar = m_host.OwnerID;
            }

            if (targetAvatar != UUID.Zero)
            {
                ScenePresence avatar = World.GetScenePresence(targetAvatar);
                if (avatar != null)
                {
                    avatar.ControllingClient.SendScriptTeleportRequest(m_host.Name, simname,
                        new Vector3(pos.X, pos.Y, pos.Z),
                        new Vector3(lookAt.X, lookAt.Y, lookAt.Z));
                }
            }

            ScriptSleep(1000);
        }

        public void llAddToLandBanList(string avatar, float hours)
        {
            
            UUID key;
            Vector3 landpos = m_host.AbsolutePosition;
            ILandObject landObject = World.LandChannel.GetLandObject(landpos.X, landpos.Y);
            if (World.Permissions.CanEditParcel(m_host.OwnerID, landObject, GroupPowers.LandManageAllowed))
            {
                ParcelManager.ParcelAccessEntry entry = new ParcelManager.ParcelAccessEntry();
                if (UUID.TryParse(avatar, out key))
                {
                    entry.AgentID = key;
                    entry.Flags = AccessList.Ban;
                    entry.Time = DateTime.Now.AddHours(hours);
                    landObject.landData.ParcelAccessList.Add(entry);
                }
            }
            
            ScriptSleep(100);
        }

        public void llRemoveFromLandPassList(string avatar)
        {
            
            UUID key;
            Vector3 landpos = m_host.AbsolutePosition;
            ILandObject landObject = World.LandChannel.GetLandObject(landpos.X, landpos.Y);
            if (World.Permissions.CanEditParcel(m_host.OwnerID, landObject, GroupPowers.LandManageAllowed))
            {
                if (UUID.TryParse(avatar, out key))
                {
                    foreach (ParcelManager.ParcelAccessEntry entry in landObject.landData.ParcelAccessList)
                    {
                        if (entry.AgentID == key && entry.Flags == AccessList.Access)
                        {
                            landObject.landData.ParcelAccessList.Remove(entry);
                            break;
                        }
                    }
                }
            }
            
            ScriptSleep(100);
        }

        public void llRemoveFromLandBanList(string avatar)
        {
            
            UUID key;
            Vector3 landpos = m_host.AbsolutePosition;
            ILandObject landObject = World.LandChannel.GetLandObject(landpos.X, landpos.Y);
            if (World.Permissions.CanEditParcel(m_host.OwnerID, landObject, GroupPowers.LandManageAllowed))
            {
                if (UUID.TryParse(avatar, out key))
                {
                    foreach (ParcelManager.ParcelAccessEntry entry in landObject.landData.ParcelAccessList)
                    {
                        if (entry.AgentID == key && entry.Flags == AccessList.Ban)
                        {
                            landObject.landData.ParcelAccessList.Remove(entry);
                            break;
                        }
                    }
                }
            }
            
            ScriptSleep(100);
        }

        public void llSetCameraParams(LSL_List rules)
        {
            // our key in the object we are in
            UUID invItemID = InventorySelf();
            if (invItemID == UUID.Zero) return;

            // check the prim we are in
            if (m_host.UUID == UUID.Zero) return;

            TaskInventoryItem item;
            UUID agentID;
            lock (m_host.TaskInventory)
            {
                item = m_host.TaskInventory[invItemID];
                // we need the permission first, to know which avatar we want to set the camera for
                agentID = item.PermsGranter;
                if (agentID == UUID.Zero) return;
            }

            if (!CheckRuntimePerms(item, agentID, ScriptBaseClass.PERMISSION_CONTROL_CAMERA))
                return;

            ScenePresence presence = World.GetScenePresence(agentID);

            // we are not interested in child-agents
            if (presence == null || presence.IsChildAgent) return;

            Dictionary<int, float> parameters = new Dictionary<int, float>();
            object[] data = rules.Data;
            for (int i = 0; i < data.Length; ++i)
            {
                int type = Convert.ToInt32(data[i++].ToString());
                if (i >= data.Length) break; // odd number of entries => ignore the last

                // some special cases: Vector parameters are split into 3 float parameters (with type+1, type+2, type+3)
                switch (type)
                {
                    case ScriptBaseClass.CAMERA_FOCUS:
                    case ScriptBaseClass.CAMERA_FOCUS_OFFSET:
                    case ScriptBaseClass.CAMERA_POSITION:
                        LSL_Vector v = rules.GetVector3Item(i);//data[i];
                        parameters.Add(type + 1, (float)v.X);
                        parameters.Add(type + 2, (float)v.Y);
                        parameters.Add(type + 3, (float)v.Z);
                        break;
                    default:
                        // TODO: clean that up as soon as the implicit casts are in
                        if (data[i] is float)
                            parameters.Add(type, (float)((float)data[i]));
                        else if (data[i] is int)
                            parameters.Add(type, (float)((int)data[i]));
                        else parameters.Add(type, Convert.ToSingle(data[i]));
                        break;
                }
            }
            if (parameters.Count > 0) presence.ControllingClient.SendSetFollowCamProperties(m_host.UUID, parameters);
        }

        public void llClearCameraParams()
        {
            // our key in the object we are in
            UUID invItemID = InventorySelf();
            if (invItemID == UUID.Zero) return;

            // check the prim we are in
            if (m_host.UUID == UUID.Zero) return;

            // we need the permission first, to know which avatar we want to clear the camera for
            TaskInventoryItem item;
            UUID agentID;
            lock (m_host.TaskInventory)
            {
                item = m_host.TaskInventory[invItemID];
                // we need the permission first, to know which avatar we want to set the camera for
                agentID = item.PermsGranter;
                if (agentID == UUID.Zero) return;
            }

            if (!CheckRuntimePerms(item, agentID, ScriptBaseClass.PERMISSION_CONTROL_CAMERA))
                return; 
            
            ScenePresence presence = World.GetScenePresence(agentID);
            if (presence == null)
                return; // don't crash the script

            // we are not interested in child-agents
            if (presence.IsChildAgent) return;

            presence.ControllingClient.SendClearFollowCamProperties(m_host.UUID);
        }

        public float llListStatistics(int operation, LSL_List src)
        {
            
            LSL_List nums = LSL_List.ToFloatList(src);
            switch (operation)
            {
                case ScriptBaseClass.LIST_STAT_RANGE:
                    return nums.Range();
                case ScriptBaseClass.LIST_STAT_MIN:
                    return nums.Min();
                case ScriptBaseClass.LIST_STAT_MAX:
                    return nums.Max();
                case ScriptBaseClass.LIST_STAT_MEAN:
                    return nums.Mean();
                case ScriptBaseClass.LIST_STAT_MEDIAN:
                    return nums.Median();
                case ScriptBaseClass.LIST_STAT_NUM_COUNT:
                    return nums.NumericLength();
                case ScriptBaseClass.LIST_STAT_STD_DEV:
                    return nums.StdDev();
                case ScriptBaseClass.LIST_STAT_SUM:
                    return nums.Sum();
                case ScriptBaseClass.LIST_STAT_SUM_SQUARES:
                    return nums.SumSqrs();
                case ScriptBaseClass.LIST_STAT_GEOMETRIC_MEAN:
                    return nums.GeometricMean();
                case ScriptBaseClass.LIST_STAT_HARMONIC_MEAN:
                    return nums.HarmonicMean();
                default:
                    return 0.0f;
            }
        }

        public int llGetParcelFlags(LSL_Vector pos)
        {
            ILandObject land = World.LandChannel.GetLandObject((float)pos.X, (float)pos.Y);

            if (land == null) return 0;

            LandData landData = land.landData;

            if (landData == null) return 0;

            return (int)landData.Flags;
        }

        public int llGetRegionFlags()
        {
            
            IEstateModule estate = World.RequestModuleInterface<IEstateModule>();
            if (estate == null)
                return 67108864;
            return (int)estate.GetRegionFlags();
        }

        public string llXorBase64StringsCorrect(string str1, string str2)
        {
            if (String.IsNullOrEmpty(str1) || String.IsNullOrEmpty(str2))
                return str1;
            string ret = String.Empty;
            string src1 = llBase64ToString(str1);
            string src2 = llBase64ToString(str2);
            int c = 0;
            for (int i = 0; i < src1.Length; i++)
            {
                ret += (char)(src1[i] ^ src2[c]);

                c++;
                if (c >= src2.Length)
                    c = 0;
            }
            return llStringToBase64(ret);
        }

        public string llHTTPRequest(string url, LSL_List parameters, string body)
        {
            // Partial implementation: support for parameter flags needed
            //   see http://wiki.secondlife.com/wiki/LlHTTPRequest
            // parameter flags support are implemented in ScriptsHttpRequests.cs
            //   in StartHttpRequest
            
            IHttpRequestModule httpScriptMod =
                m_ScriptEngine.World.RequestModuleInterface<IHttpRequestModule>();

            //now, determine a variable delay based on the current size of the script queue
            const float EVENT_LOW_SPACE_THRESHOLD = 0.4f;             // 60% full
            const float REQUEST_LOW_SPACE_THRESHOLD = 0.4f;           // 60% full
            const int LOW_SPACE_DELAY = 50;
            const int ERROR_DELAY = 80;

            float eventQueueFreeSpace = m_ScriptEngine.GetEventQueueFreeSpacePercentage(m_itemID);
            float requestQueueFreeSpace = httpScriptMod.RequestQueueFreeSpacePercentage;
            float DELAY = 0;

            if (eventQueueFreeSpace <= EVENT_LOW_SPACE_THRESHOLD)
                DELAY += ((1.0f - (eventQueueFreeSpace / EVENT_LOW_SPACE_THRESHOLD)) * LOW_SPACE_DELAY);

            if (requestQueueFreeSpace <= REQUEST_LOW_SPACE_THRESHOLD)
                DELAY += ((1.0f - (requestQueueFreeSpace / REQUEST_LOW_SPACE_THRESHOLD)) * LOW_SPACE_DELAY);

            if (DELAY > 0)
                ScriptSleep((int)DELAY);

            // StartHttpRequest below really needs an array, so instead of building a new list, build the array.
            string[] param = new string[parameters.Length];
            int i = 0;
            while (i < parameters.Length)
            {
                string arg1 = parameters.GetLSLStringItem(i);
                param[i++] = arg1;

                if (i >= parameters.Length)
                {
                    ScriptShoutError("Invalid number of parameters in options list for llHTTPRequest.");
                    return UUID.Zero.ToString();
                }
                string arg2 = parameters.GetLSLStringItem(i);
                param[i++] = arg2;

                // We can do whatever forward-checking we want on the parameters now.
                // For now lets just validate the option is numeric and HTTP_METHOD strings valid.
                int option;
                if (!Int32.TryParse(arg1, out option))
                {
                    ScriptShoutError("Invalid flag passed in parameters list of llHTTPRequest.");
                    return UUID.Zero.ToString();
                }

                switch (option)
                {
                    // All parameters are in pairs except for HTTP_CUSTOM_HEADER which has an extra param.
                    case (int)HttpRequestConstants.HTTP_CUSTOM_HEADER:
                        if (i >= parameters.Length)
                        {
                            ScriptShoutError("Invalid number of parameters in the HTTP_CUSTOM_HEADER options for llHTTPRequest.");
                            return UUID.Zero.ToString();
                        }
                        string arg3 = parameters.GetLSLStringItem(i);
                        param[i++] = arg3;
                        break;
                    default:
                        // just consume the value for this parameter
                        break;
                }
            }

            Vector3 position = m_host.AbsolutePosition;
            Vector3 velocity = m_host.Velocity;
            Quaternion rotation = m_host.RotationOffset;
            string ownerName = String.Empty;
            ScenePresence scenePresence = World.GetScenePresence(m_host.ObjectOwner);
            if (scenePresence == null)
                ownerName = resolveName(m_host.ObjectOwner);
            else
                ownerName = scenePresence.Name;

            RegionInfo regionInfo = World.RegionInfo;

            Dictionary<string, string> httpHeaders = new Dictionary<string, string>();

            string shard = "Production";
            IConfigSource config = m_ScriptEngine.ConfigSource;
            if (config.Configs["Network"] != null)
            {
                shard = config.Configs["Network"].GetString("shard", shard);
            }

            httpHeaders["X-SecondLife-Shard"] = shard;
            httpHeaders["X-SecondLife-Object-Name"] = m_host.Name;
            httpHeaders["X-SecondLife-Object-Key"] = m_host.UUID.ToString();
            httpHeaders["X-SecondLife-Region"] = string.Format("{0} ({1}, {2})", regionInfo.RegionName, regionInfo.RegionLocX, regionInfo.RegionLocY);
            httpHeaders["X-SecondLife-Local-Position"] = string.Format("({0:0.000000}, {1:0.000000}, {2:0.000000})", position.X, position.Y, position.Z);
            httpHeaders["X-SecondLife-Local-Velocity"] = string.Format("({0:0.000000}, {1:0.000000}, {2:0.000000})", velocity.X, velocity.Y, velocity.Z);
            httpHeaders["X-SecondLife-Local-Rotation"] = string.Format("({0:0.000000}, {1:0.000000}, {2:0.000000}, {3:0.000000})", rotation.X, rotation.Y, rotation.Z, rotation.W);
            httpHeaders["X-SecondLife-Owner-Name"] = ownerName;
            httpHeaders["X-SecondLife-Owner-Key"] = m_host.ObjectOwner.ToString();
            string userAgent = config.Configs["Network"].GetString("user_agent", null);
            if (userAgent == null)
                userAgent = "InWorldz LSL/"+ VersionInfo.Version+ " (Mozilla Compatible)";

            httpHeaders["User-Agent"] = userAgent;

            UUID reqId = httpScriptMod.StartHttpRequest(m_host.ParentGroup.UUID, m_localID, m_itemID, url, param, httpHeaders, body);
            if (reqId == UUID.Zero) ScriptSleep(ERROR_DELAY);

            return reqId.ToString();
        }

        public void llSetContentType(string id, int type)
        {
            m_host.AddScriptLPS(1);

            if (m_UrlModule == null)
                return;

            UUID key;
            if (UUID.TryParse(id, out key) == false)
                return;

            // Make sure the content type is text/plain to start with
            m_UrlModule.HttpContentType(key, "text/plain");

            // Is the object owner online and in the region
            ScenePresence agent = World.GetScenePresence(m_host.ParentGroup.OwnerID);
            if (agent == null || agent.IsChildAgent)
                return;  // Fail if the owner is not in the same region

            // Is it the embeded browser?
            string userAgent = m_UrlModule.GetHttpHeader(key, "user-agent");
            if (userAgent.IndexOf("SecondLife", StringComparison.CurrentCultureIgnoreCase) < 0 && 
                userAgent.IndexOf("InWorldz", StringComparison.CurrentCultureIgnoreCase) < 0)
                return; // Not the embedded browser. Is this check good enough?  

            switch (type)
            {
                case ScriptBaseClass.CONTENT_TYPE_HTML:
                    m_UrlModule.HttpContentType(key, "text/html");
                    break;
                case ScriptBaseClass.CONTENT_TYPE_XML:
                    m_UrlModule.HttpContentType(key, "application/xml");
                    break;
                case ScriptBaseClass.CONTENT_TYPE_XHTML:
                    m_UrlModule.HttpContentType(key, "application/xhtml+xml");
                    break;
                case ScriptBaseClass.CONTENT_TYPE_ATOM:
                    m_UrlModule.HttpContentType(key, "application/atom+xml");
                    break;
                case ScriptBaseClass.CONTENT_TYPE_JSON:
                    m_UrlModule.HttpContentType(key, "application/json");
                    break;
                case ScriptBaseClass.CONTENT_TYPE_LLSD:
                    m_UrlModule.HttpContentType(key, "application/llsd+xml");
                    break;
                case ScriptBaseClass.CONTENT_TYPE_FORM:
                    m_UrlModule.HttpContentType(key, "application/x-www-form-urlencoded");
                    break;
                case ScriptBaseClass.CONTENT_TYPE_RSS:
                    m_UrlModule.HttpContentType(key, "application/rss+xml");
                    break;
                default:
                    m_UrlModule.HttpContentType(key, "text/plain");
                    break;
            }
        }

        public void llHTTPResponse(string url, int status, string body)
        {
            // Partial implementation: support for parameter flags needed
            //   see http://wiki.secondlife.com/wiki/llHTTPResponse

            if (m_UrlModule != null)
                m_UrlModule.HttpResponse(UUID.Parse(url), status, body);
        }

        public void llResetLandBanList()
        {
            Vector3 landpos = m_host.AbsolutePosition;
            ILandObject landObject = World.LandChannel.GetLandObject(landpos.X, landpos.Y);
            if (World.Permissions.CanEditParcel(m_host.OwnerID, landObject, GroupPowers.LandManageAllowed))
            {
                foreach (ParcelManager.ParcelAccessEntry entry in landObject.landData.ParcelAccessList)
                {
                    if (entry.Flags == AccessList.Ban)
                    {
                        landObject.landData.ParcelAccessList.Remove(entry);
                    }
                }
            }
            
            ScriptSleep(100);
        }

        public void llResetLandPassList()
        {
            Vector3 landpos = m_host.AbsolutePosition;
            ILandObject landObject = World.LandChannel.GetLandObject(landpos.X, landpos.Y);
            if (World.Permissions.CanEditParcel(m_host.OwnerID, landObject, GroupPowers.LandManageAllowed))
            {
                foreach (ParcelManager.ParcelAccessEntry entry in landObject.landData.ParcelAccessList)
                {
                    if (entry.Flags == AccessList.Access)
                    {
                        landObject.landData.ParcelAccessList.Remove(entry);
                    }
                }
            }
            
            ScriptSleep(100);
        }

        public int llGetParcelPrimCount(LSL_Vector pos, int category, int sim_wide)
        {
            ILandObject parcel = World.LandChannel.GetLandObject(pos.X, pos.Y);
            LandData land = (parcel == null) ? null : parcel.landData;

            if (land == null)
            {
                return 0;
            }
            else
            {
                if (sim_wide != 0)
                {
                    if (category == ScriptBaseClass.PARCEL_COUNT_TOTAL)
                    {
                        return land.SimwidePrims;
                    }

                    else
                    {
                        return parcel.getSimulatorMaxPrimCount(parcel);
                    }
                }

                else
                {
                    if (category == ScriptBaseClass.PARCEL_COUNT_TOTAL)
                    {
                        return land.OwnerPrims + land.GroupPrims + land.OtherPrims + land.SelectedPrims;
                    }

                    else if (category == ScriptBaseClass.PARCEL_COUNT_OWNER)
                    {
                        return land.OwnerPrims;
                    }

                    else if (category == ScriptBaseClass.PARCEL_COUNT_GROUP)
                    {
                        return land.GroupPrims;
                    }

                    else if (category == ScriptBaseClass.PARCEL_COUNT_OTHER)
                    {
                        return land.OtherPrims;
                    }

                    else if (category == ScriptBaseClass.PARCEL_COUNT_SELECTED)
                    {
                        return land.SelectedPrims;
                    }

                    else if (category == ScriptBaseClass.PARCEL_COUNT_TEMP)
                    {
                        return 0;//land.
                    }
                }
            }
            return 0;
        }

        public LSL_List llGetParcelPrimOwners(LSL_Vector pos)
        {
            
            LandObject land = (LandObject)World.LandChannel.GetLandObject((float)pos.X, (float)pos.Y);
            List<object> ret = new List<object>();
            if (land != null)
            {
                foreach (KeyValuePair<UUID, int> detectedParams in land.getLandObjectOwners())
                {
                    ret.Add(detectedParams.Key.ToString());
                    ret.Add(detectedParams.Value);
                }
            }

            ScriptSleep(2000);
            return new LSL_List(ret);
        }

        public int llGetObjectPrimCount(string object_id)
        {
            UUID object_uuid;
            if (UUID.TryParse(object_id, out object_uuid))
            {
                SceneObjectPart part = World.GetSceneObjectPart(object_uuid);
                if (part != null)
                    return part.ParentGroup.PartCount;
            }

            return 0;
        }

        public int llGetParcelMaxPrims(LSL_Vector pos, int sim_wide)
        {
            ILandObject parcel = World.LandChannel.GetLandObject(pos.X, pos.Y);
            if (parcel == null)
                return 0;

            if (sim_wide == 0)
                return parcel.getParcelMaxPrimCount(parcel, true);

            return parcel.getSimulatorMaxPrimCount(parcel);
        }

        public LSL_List llGetParcelDetails(LSL_Vector pos, LSL_List args)
        {
            LandData land = World.GetLandData((float)pos.X, (float)pos.Y);
            if (land == null)
            {
                return new LSL_List(0);
            }
            LSL_List ret = new LSL_List();
            for (int idx=0; idx<args.Length; idx++)
            {
                int param = (int)args.GetLSLIntegerItem(idx);

                switch (param)
                {
                    case ScriptBaseClass.PARCEL_DETAILS_NAME:
                        ret = ret.Append(land.Name);
                        break;
                    case ScriptBaseClass.PARCEL_DETAILS_DESC:
                        ret = ret.Append(land.Description);
                        break;
                    case ScriptBaseClass.PARCEL_DETAILS_OWNER:
                        ret = ret.Append(land.OwnerID.ToString());
                        break;
                    case ScriptBaseClass.PARCEL_DETAILS_GROUP:
                        ret = ret.Append(land.GroupID.ToString());
                        break;
                    case ScriptBaseClass.PARCEL_DETAILS_AREA:
                        ret = ret.Append(land.Area);
                        break;
                    case ScriptBaseClass.PARCEL_DETAILS_ID:
                        ret = ret.Append(land.GlobalID.ToString());
                        break;
                    case ScriptBaseClass.PARCEL_DETAILS_SEE_AVATARS:
                        ret = ret.Append(1); // currently always true in InWorldz
                        break;
                    default:
                        ret = ret.Append(0);
                        break;
                }
            }
            return ret;
        }

        public string llStringTrim(string src, int type)
        {
            if (type == (int)ScriptBaseClass.STRING_TRIM_HEAD) { return src.TrimStart(); }
            if (type == (int)ScriptBaseClass.STRING_TRIM_TAIL) { return src.TrimEnd(); }
            if (type == (int)ScriptBaseClass.STRING_TRIM) { return src.Trim(); }
            return src;
        }

        private bool GetAgentDetails(List<object> ret, ScenePresence av, LSL_List args)
        {
            int idx = 0;
            for (idx = 0; idx < args.Length; idx++)
            {
                int param = (int)args.GetLSLIntegerItem(idx);
                switch (param)
                {
                    case ScriptBaseClass.OBJECT_NAME:
                        ret.Add(av.Firstname + " " + av.Lastname);
                        break;
                    case ScriptBaseClass.OBJECT_DESC:
                        ret.Add(String.Empty);
                        break;
                    case ScriptBaseClass.OBJECT_POS:
                        Vector3 pos = av.AbsolutePosition;
                        ret.Add(new LSL_Vector(pos));
                        break;
                    case ScriptBaseClass.OBJECT_ROT:
                        Quaternion rot = av.Rotation;
                        ret.Add(new LSL_Rotation(rot));
                        break;
                    case ScriptBaseClass.OBJECT_VELOCITY:
                        Vector3 vel = av.Velocity;
                        ret.Add(new LSL_Vector(vel));
                        break;
                    case ScriptBaseClass.OBJECT_OWNER:
                        ret.Add(av.UUID.ToString());
                        break;
                    case ScriptBaseClass.OBJECT_GROUP:
                        ret.Add(UUID.Zero.ToString());  // documented to return a null key for avatars
                        break;
                    case ScriptBaseClass.OBJECT_CREATOR:
                        ret.Add(UUID.Zero.ToString());  // documented to return a null key for avatars
                        break;
                    case ScriptBaseClass.OBJECT_RUNNING_SCRIPT_COUNT:
                    case ScriptBaseClass.OBJECT_TOTAL_SCRIPT_COUNT:
                    case ScriptBaseClass.OBJECT_SCRIPT_MEMORY:
                    case ScriptBaseClass.IW_OBJECT_SCRIPT_MEMORY_USED:
                        ret.Add(GetAgentTotals(av, param));
                        break;
                    case ScriptBaseClass.OBJECT_SCRIPT_TIME:
                        ret.Add(GetAgentTotalsFloat(av, param));
                        break;
                    case ScriptBaseClass.OBJECT_PRIM_EQUIVALENCE:
                        ret.Add((int)(0));
                        break;
                    case ScriptBaseClass.OBJECT_SERVER_COST:
                        ret.Add((float)(0.0f));
                        break;
                    case ScriptBaseClass.OBJECT_STREAMING_COST:
                        ret.Add((float)(0.0f));
                        break;
                    case ScriptBaseClass.OBJECT_PHYSICS_COST:
                        PhysicsActor physActor = m_host.PhysActor;
                        if (physActor != null)
                        {
                            ret.Add((float)physActor.TotalComplexity);
                        }
                        else
                        {
                            ret.Add((float)(0.0f));
                        }
                        break;
                    case ScriptBaseClass.OBJECT_CHARACTER_TIME:
                        ret.Add((float)(0.0f));
                        break;
                    case ScriptBaseClass.OBJECT_ROOT:
                        ScenePresence.PositionInfo info = av.GetPosInfo();
                        if (info.Parent != null)
                        {
                            SceneObjectPart parentobj = info.Parent;
                            if (parentobj != null)
                                ret.Add(parentobj.GetRootPartUUID().ToString());  // documented to return a null key for avatars
                            else
                                ret.Add(av.UUID.ToString());    // ParentID not found, so use unseated avatars return instead
                        }
                        else
                        {
                            ret.Add(av.UUID.ToString());    // unseated avatars return their own ID for OBJECT_ROOT
                        }
                        break;
                    case ScriptBaseClass.OBJECT_ATTACHED_POINT:
                        ret.Add(0);  // avatar cannot be attached
                        break;
                    case ScriptBaseClass.OBJECT_PATHFINDING_TYPE:
                        ret.Add(ScriptBaseClass.OPT_AVATAR);
                        break;
                    case ScriptBaseClass.OBJECT_PHYSICS:
                        ret.Add(0);  // always 0 for avatars
                        break;
                    case ScriptBaseClass.OBJECT_PHANTOM:
                        ret.Add(0);  // always 0 for avatars
                        break;
                    case ScriptBaseClass.OBJECT_TEMP_ON_REZ:
                        ret.Add(0);  // always 0 for avatars
                        break;
                    case ScriptBaseClass.OBJECT_RENDER_WEIGHT:
                        ret.Add(0);
                        break;
                    case ScriptBaseClass.OBJECT_HOVER_HEIGHT:
                        ret.Add(0);
                        break;
                    case ScriptBaseClass.OBJECT_BODY_SHAPE_TYPE:
                        int value = iwGetAppearanceParam(av.UUID.ToString(), 31);
                        if (value == 255) ret.Add(1f);
                        else ret.Add(0f);
                        break;
                    case ScriptBaseClass.OBJECT_LAST_OWNER_ID:
                        ret.Add(UUID.Zero);
                        break;
                    case ScriptBaseClass.OBJECT_CLICK_ACTION:
                        ret.Add(0);
                        break;
                }
            }

            return true;    // success
        }

        public LSL_List llGetObjectDetails(string id, LSL_List args)
        {
            List<object> ret = new List<object>();
            UUID key = new UUID();
            int idx = 0;

            if (UUID.TryParse(id, out key))
            {
                // First, the avatar-based versions of these.
                ScenePresence av = World.GetScenePresence(key);
                if (av != null)
                {
                    GetAgentDetails(ret, av, args);
                    return new LSL_List(ret);
                }

                // Now the object-based versions of these.
                SceneObjectPart part = World.GetSceneObjectPart(key);
                if (part != null)
                {
                    for (idx = 0; idx < args.Length; idx++)
                    {
                        int param = (int)args.GetLSLIntegerItem(idx);
                        switch (param)
                        {
                            case ScriptBaseClass.OBJECT_NAME:
                                ret.Add(part.Name);
                                break;
                            case ScriptBaseClass.OBJECT_DESC:
                                ret.Add(part.Description);
                                break;
                            case ScriptBaseClass.OBJECT_POS:
                                ret.Add(GetPartPos(part));
                                break;
                            case ScriptBaseClass.OBJECT_ROT:
                                ret.Add(new LSL_Rotation(part.RotationOffset.X, part.RotationOffset.Y, part.RotationOffset.Z, part.RotationOffset.W));
                                break;
                            case ScriptBaseClass.OBJECT_VELOCITY:
                                ret.Add(new LSL_Vector(part.Velocity.X, part.Velocity.Y, part.Velocity.Z));
                                break;
                            case ScriptBaseClass.OBJECT_OWNER:
                                if ((part.GroupID != UUID.Zero) && (part.OwnerID == part.GroupID))  // group-deeded
                                    ret.Add(UUID.Zero.ToString());  // documented to return NULL_KEY for group-deeded
                                else
                                    ret.Add(part.OwnerID.ToString());
                                break;
                            case ScriptBaseClass.OBJECT_GROUP:
                                ret.Add(part.GroupID.ToString());
                                break;
                            case ScriptBaseClass.OBJECT_CREATOR:
                                ret.Add(part.CreatorID.ToString());
                                break;
                            case ScriptBaseClass.OBJECT_RUNNING_SCRIPT_COUNT:
                            case ScriptBaseClass.OBJECT_TOTAL_SCRIPT_COUNT:
                            case ScriptBaseClass.OBJECT_SCRIPT_MEMORY:
                            case ScriptBaseClass.IW_OBJECT_SCRIPT_MEMORY_USED:
                                ret.Add(GetObjectScriptTotal(part.ParentGroup, param));
                                break;
                            case ScriptBaseClass.OBJECT_SCRIPT_TIME:
                                ret.Add(GetObjectScriptTotalFloat(part.ParentGroup, param));
                                break;
                            case ScriptBaseClass.OBJECT_PRIM_EQUIVALENCE:
                                ret.Add((int)(part.ParentGroup.LandImpact));
                                break;
                            case ScriptBaseClass.OBJECT_SERVER_COST:
                                ret.Add((float)(part.ParentGroup.ServerWeight));
                                break;
                            case ScriptBaseClass.OBJECT_STREAMING_COST:
                                ret.Add((float)(part.ParentGroup.StreamingCost));
                                break;
                            case ScriptBaseClass.OBJECT_PHYSICS_COST:
                                PhysicsActor physActor = part.ParentGroup.RootPart.PhysActor;
                                if (physActor == null)
                                    ret.Add((float)(0.0f));
                                else
                                    ret.Add((float)(physActor.TotalComplexity));
                                break;
                            case ScriptBaseClass.OBJECT_CHARACTER_TIME:
                                ret.Add((float)(0.0f));
                                break;
                            case ScriptBaseClass.OBJECT_ROOT:
                                ret.Add(part.GetRootPartUUID().ToString());
                                break;
                            case ScriptBaseClass.OBJECT_ATTACHED_POINT:
                                ret.Add(part.AttachmentPoint);
                                break;
                            case ScriptBaseClass.OBJECT_PATHFINDING_TYPE:
                                if (part.IsLindenPlant())
                                    ret.Add(ScriptBaseClass.OPT_OTHER);
                                else
                                    ret.Add(ScriptBaseClass.OPT_LEGACY_LINKSET);
                                break;
                            case ScriptBaseClass.OBJECT_PHYSICS:
                                if ((part.GetEffectiveObjectFlags() & PrimFlags.Physics) == PrimFlags.Physics)
                                    ret.Add(1);
                                else
                                    ret.Add(0);
                                break;
                            case ScriptBaseClass.OBJECT_PHANTOM:
                                if ((part.GetEffectiveObjectFlags() & PrimFlags.Phantom) == PrimFlags.Phantom) 
                                    ret.Add(1);
                                else
                                    ret.Add(0);
                                break;
                            case ScriptBaseClass.OBJECT_TEMP_ON_REZ:
                                if ((part.GetEffectiveObjectFlags() & PrimFlags.TemporaryOnRez) == PrimFlags.TemporaryOnRez)
                                    ret.Add(1);
                                else
                                    ret.Add(0);
                                break;
                            case ScriptBaseClass.OBJECT_RENDER_WEIGHT:
                                ret.Add(0);
                                break;
                            case ScriptBaseClass.OBJECT_HOVER_HEIGHT:
                                ret.Add(0f);
                                break;
                            case ScriptBaseClass.OBJECT_BODY_SHAPE_TYPE:
                                ret.Add(-1);
                                break;
                            case ScriptBaseClass.OBJECT_LAST_OWNER_ID:
                                ret.Add(part.LastOwnerID);
                                break;
                            case ScriptBaseClass.OBJECT_CLICK_ACTION:
                                ret.Add(Convert.ToInt32(part.ClickAction));
                                break;

                        }
                    }
                    return new LSL_List(ret);
                }
            }
            return new LSL_List();
        }

        private bool IsInRange(float value, float target1, float target2)
        {
            if ((target1 == 0.0f) && (target2 == 0.0f))
                return true;    // wildcard, accept any value

            float minValue = target1;
            float maxValue = target2;
            if (target1 > target2)
            {
                minValue = target2;
                maxValue = target1;
            }
            return ((value >= minValue) && (value <= maxValue));
        }

        private bool AddToAgentResults(List<object> results, ScenePresence av, int scope, LandData thisParcel, Vector3 minPos, Vector3 maxPos, LSL_List args)
        {
            LandData targetParcel = null;
            if (scope != ScriptBaseClass.AGENT_LIST_REGION)
            {
                Vector3 pos = av.AbsolutePosition;
                ILandObject land = World.LandChannel.GetLandObject(pos.X, pos.Y);
                if (land != null)
                    targetParcel = land.landData;
            }

            if ((minPos != Vector3.Zero) && (maxPos != Vector3.Zero))
            {
                Vector3 pos = av.AbsolutePosition;
                if (!IsInRange(pos.X, minPos.X, maxPos.X))
                    return true;    // success but not a match
                if (!IsInRange(pos.Y, minPos.Y, maxPos.Y))
                    return true;    // success but not a match
                if (!IsInRange(pos.Z, minPos.Z, maxPos.Z))
                    return true;    // success but not a match
                // within the specified bounding box or wildcard specified
            }

            switch (scope)
            {
                case ScriptBaseClass.AGENT_LIST_PARCEL:
                    if ((targetParcel == null) || (thisParcel == null))
                        return false;   // error
                    if (targetParcel.GlobalID != thisParcel.GlobalID)
                        return true;    // success but not a match
                    break;
                case ScriptBaseClass.AGENT_LIST_PARCEL_OWNER:
                    if ((targetParcel == null) || (thisParcel == null))
                        return false;   // error
                    if (targetParcel.OwnerID != thisParcel.OwnerID)
                        return true;    // success but not a match
                    break;
                case ScriptBaseClass.AGENT_LIST_REGION:
                    // don't filter any parcels
                    break;
                default:
                    return false; // unrecognized scope
            }

            return GetAgentDetails(results, av, args);
        }

        public LSL_List iwGetAgentList(int scope, Vector3 minPos, Vector3 maxPos, LSL_List args)
        {
            List<object> results = new List<object>();
            List<ScenePresence> agentList = World.GetAvatars();
            if (agentList == null)
                return new LSL_List();

            LandData thisParcel = null;
            if (scope != ScriptBaseClass.AGENT_LIST_REGION)
            {
                Vector3 pos = m_host.AbsolutePosition;
                ILandObject land = World.LandChannel.GetLandObject(pos.X, pos.Y);
                if (land != null)
                    thisParcel = land.landData;
                if (thisParcel == null)
                    return new LSL_List();  // failed to find the required current parcel
            }

            // First, the avatar-based versions of these.
            foreach (ScenePresence av in agentList)
            {
                if ((av.IsDeleted) || (av.IsInTransit))
                    continue;
                if (!AddToAgentResults(results, av, scope, thisParcel, minPos, maxPos, args))
                    break;
            }

            return new LSL_List(results);
        }

        public LSL_List llGetAgentList(int scope, LSL_List args)
        {
            // ignore 'args', LL does not use it in the SL implementation
            List<object> paramList = new List<object>();
            // Substitute a new list with only OBJECT_OWNER (which is the user UUIDs).
            paramList.Add((int)ScriptBaseClass.OBJECT_OWNER);
            return iwGetAgentList(scope, Vector3.Zero, Vector3.Zero, new LSL_List(paramList));
        }

        internal UUID ScriptByName(string name)
        {
            lock (m_host.TaskInventory)
            {
                foreach (TaskInventoryItem item in m_host.TaskInventory.Values)
                {
                    if (item.Type == 10 && item.Name == name)
                        return item.ItemID;
                }
            }

            return UUID.Zero;
        }

        public void ScriptShoutError(string msg)
        {
            this.SimChat(ScriptBaseClass.DEBUG_CHANNEL, msg, ChatTypeEnum.Direct, m_host.OwnerID);
        }

        /// <summary>
        /// Called by the script engine for internal errors
        /// </summary>
        /// <param name="msg"></param>
        public void ShoutError(string msg)
        {
            ChatFromObject(ScriptBaseClass.DEBUG_CHANNEL, msg, ChatTypeEnum.Shout, World, m_host, UUID.Zero);
        }

        internal void NotImplemented(string command)
        {
            if (throwErrorOnNotImplemented)
                throw new NotImplementedException("Command not implemented: " + command);
            else
                ScriptShoutError("Command not implemented: " + command);
        }

        internal void Deprecated(string command)
        {
            ScriptShoutError("Command deprecated: " + command);
        }

        internal void LSLError(string msg)
        {
            ScriptShoutError("LSL Runtime Error: " + msg);
        }

        public delegate void AssetRequestCallback(UUID assetID, AssetBase asset);
        private void WithNotecard(UUID assetID, AssetRequestCallback cb)
        {
            World.CommsManager.AssetCache.GetAsset(assetID, delegate(UUID i, AssetBase a) { cb(i, a); }, AssetRequestInfo.InternalRequest());
        }

        public string GetNumberOfNotecardLines(SceneObjectPart part, string name)
        {
            const int ERROR_DELAY = 100;
            const int LONG_DELAY = 50;
            const int FAST_DELAY = 25;
            TaskInventoryDictionary itemsDictionary = (TaskInventoryDictionary)part.TaskInventory.Clone();

            UUID assetID = UUID.Zero;

            if (!UUID.TryParse(name, out assetID))
            {
                foreach (TaskInventoryItem item in itemsDictionary.Values)
                {
                    if (item.Type == 7 && item.Name == name)
                    {
                        assetID = item.AssetID;
                        break;
                    }
                }
            }

            if (assetID == UUID.Zero)
            {
                // => complain loudly, as specified by the LSL docs
                ScriptShoutError("Notecard '" + name + "' could not be found.");
                ScriptSleep(ERROR_DELAY);
                return UUID.Zero.ToString();
            }

            string reqIdentifier = UUID.Random().ToString();

            // was: UUID tid = tid = AsyncCommands.
            UUID tid = AsyncCommands.DataserverPlugin.RegisterRequest(m_localID, m_itemID, reqIdentifier);

            if (NotecardCache.IsCached(assetID))
            {
                AsyncCommands.
                DataserverPlugin.DataserverReply(reqIdentifier, NotecardCache.GetLines(assetID).ToString());
                ScriptSleep(FAST_DELAY);
                return tid.ToString();
            }

            WithNotecard(assetID, delegate(UUID id, AssetBase a)
            {
                if (a == null || a.Type != 7)
                {
                    ScriptShoutError("Notecard '" + name + "' could not be found.");
                    ScriptSleep(ERROR_DELAY);
                    return;
                }

                //m_log.Debug(data);
                NotecardCache.Cache(id, a.Data);
                AsyncCommands.DataserverPlugin.DataserverReply(reqIdentifier, NotecardCache.GetLines(id).ToString());
            });

            NotecardCache.CacheCheck(); //this must be done in the script engine thread to avoid race conditions

            ScriptSleep(LONG_DELAY);
            return tid.ToString();
        }
        public string llGetNumberOfNotecardLines(string name)
        {
            return GetNumberOfNotecardLines(m_host, name);
        }
        public string iwGetLinkNumberOfNotecardLines(int linknumber, string name)
        {
            var parts = GetLinkPrimsOnly(linknumber);
            if (parts.Count == 1)
            {
                foreach (SceneObjectPart part in parts)
                {
                    // just return the first match
                    return GetNumberOfNotecardLines(part, name);
                }
            }

            ScriptShoutError("llGetLinkNumberOfNotecardLines: Link number " + linknumber.ToString() + " does not resolve to a single prim.");
            return UUID.Zero.ToString();
        }

        private string GetNotecardSegment(SceneObjectPart part, string name, int line, int startOffset, int maxLength)
        {
            const int LONG_DELAY = 25;
            const int FAST_DELAY = 1;
            const int LINES_PER_DELAY = 16; // every 16 lines, delay FAST_DELAY

            TaskInventoryDictionary itemsDictionary = (TaskInventoryDictionary)part.TaskInventory.Clone();

            UUID assetID = UUID.Zero;

            if (!UUID.TryParse(name, out assetID))
            {
                foreach (TaskInventoryItem item in itemsDictionary.Values)
                {
                    if (item.Type == 7 && item.Name == name)
                    {
                        assetID = item.AssetID;
                        break;
                    }
                }
            }

            if (assetID == UUID.Zero)
            {
                // => complain loudly, as specified by the LSL docs
                ScriptShoutError("Notecard '" + name + "' could not be found.");

                return UUID.Zero.ToString();
            }

            string reqIdentifier = UUID.Random().ToString();

            // was: UUID tid = tid = AsyncCommands.
            UUID tid = AsyncCommands.DataserverPlugin.RegisterRequest(m_localID, m_itemID, reqIdentifier);

            if (NotecardCache.IsCached(assetID))
            {
                AsyncCommands.
                DataserverPlugin.DataserverReply(reqIdentifier, NotecardCache.GetLine(assetID, line, startOffset, maxLength));
                if (((line % LINES_PER_DELAY) == 0) && (startOffset == 0))
                    ScriptSleep(FAST_DELAY);
                return tid.ToString();
            }

            WithNotecard(assetID, delegate(UUID id, AssetBase a)
            {
                if (a == null || a.Type != 7)
                {
                    ScriptShoutError("Notecard '" + name + "' could not be found.");
                    return;
                }
                //m_log.Debug(data);
                NotecardCache.Cache(id, a.Data);
                AsyncCommands.DataserverPlugin.DataserverReply(reqIdentifier, NotecardCache.GetLine(id, line, startOffset, maxLength));
            });

            NotecardCache.CacheCheck(); //this must be done in the script engine thread to avoid race conditions

            ScriptSleep(LONG_DELAY);
            return tid.ToString();
        }

        public string llGetNotecardLine(string name, int line)
        {
            return GetNotecardSegment(m_host, name, line, 0, m_notecardLineReadCharsMax);
        }

        public string iwGetNotecardSegment(string name, int line, int startOffset, int maxLength)
        {
            return GetNotecardSegment(m_host, name, line, startOffset, maxLength);
        }

        public string iwGetLinkNotecardLine(int linknumber, string name, int line)
        {
            var parts = GetLinkPrimsOnly(linknumber);
            foreach (SceneObjectPart part in parts)
            {
                // just return the first match
                return GetNotecardSegment(part, name, line, 0, m_notecardLineReadCharsMax);
            }

            return UUID.Zero.ToString();
        }

        public string iwGetLinkNotecardSegment(int linknumber, string name, int line, int startOffset, int maxLength)
        {
            var parts = GetLinkPrimsOnly(linknumber);
            foreach (SceneObjectPart part in parts)
            {
                // just return the first match
                return GetNotecardSegment(part, name, line, startOffset, maxLength);
            }

            return UUID.Zero.ToString();
        }

        /// <summary>
        /// Does a lookup of the given avatar name and returns the user UUID if it exists,
        /// this is defined as a long system call so results are returned through the sysreturn
        /// interface
        /// </summary>
        /// <param name="firstname">Avatar first name</param>
        /// <param name="lastname">Avatar last name</param>
        public void iwAvatarName2Key(string firstname, string lastname)
        {
            const int LONG_DELAY = 1000;
            const int SHORT_DELAY = 100;
            int delay = LONG_DELAY;
            UUID agentID = UUID.Zero;

            try
            {
                if (!String.IsNullOrWhiteSpace(firstname))
                {
                    if (String.IsNullOrWhiteSpace(lastname))
                        lastname = "Resident";
                    else
                        lastname = lastname.Trim();
                    firstname = firstname.Trim();

                    ScenePresence avatar = World.GetScenePresence(firstname, lastname);
                    if (avatar != null)
                    {
                        agentID = avatar.UUID;
                        delay = SHORT_DELAY;    // see Mantis #2263
                    }
                    else
                    {
                        agentID = World.CommsManager.UserService.Name2Key(firstname, lastname);
                    }
                }
                m_ScriptEngine.SysReturn(this.m_itemID, agentID.ToString(), delay);
            }
            catch
            {
                m_ScriptEngine.SysReturn(m_itemID, UUID.Zero.ToString(), delay);
                throw;
            }
        }

        /// <summary>
        /// Creates a new notecard in the inventory of the given prim
        /// </summary>
        /// <param name="notecardName">Name of the inventory note card</param>
        /// <param name="contents">A string to contain in the notecard</param>
        public void iwMakeNotecard(string notecardName, LSL_List contents)
        {
            const int DELAY = 5000;
            const int MAX_LENGTH = 65536;   // 64K characters (including newlines)

            try
            {
                // Create new asset
                AssetBase asset = new AssetBase();
                asset.Name = notecardName;
                asset.Description = "Script Generated Notecard";
                asset.Type = 7;
                asset.FullID = UUID.Random();
                StringBuilder notecardData = new StringBuilder();

                for (int i = 0; i < contents.Length; i++)
                {
                    if (i > 0)
                        notecardData.Append("\n");
                    notecardData.Append(contents.GetLSLStringItem(i));
                    if (notecardData.Length > MAX_LENGTH)
                    {
                        return;
                    }
                }

                int textLength = Encoding.UTF8.GetByteCount(notecardData.ToString());
                string sNotecardData = "Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length "
                    + textLength.ToString() + "\n" + notecardData.ToString() + "}\n";

                asset.Data = Encoding.UTF8.GetBytes(sNotecardData);

                try
                {
                    World.CommsManager.AssetCache.AddAsset(asset, AssetRequestInfo.InternalRequest());
                }
                catch (AssetServerException e)
                {
                    ScriptShoutError("Notecard asset storage failed!");
                    m_log.ErrorFormat("[LSL]: Notecard asset storage failed {0}", e);
                    return;
                }

                // Create Task Entry
                TaskInventoryItem taskItem = new TaskInventoryItem();

                taskItem.ResetIDs(m_host.UUID);
                taskItem.ParentID = m_host.UUID;
                taskItem.CreationDate = (uint)Util.UnixTimeSinceEpoch();
                taskItem.Name = asset.Name;
                taskItem.Description = asset.Description;
                taskItem.Type = (int)AssetType.Notecard;
                taskItem.InvType = (int)InventoryType.Notecard;
                taskItem.OwnerID = m_host.OwnerID;
                taskItem.CreatorID = m_host.OwnerID;
                taskItem.BasePermissions = (uint)PermissionMask.All;
                taskItem.CurrentPermissions = (uint)PermissionMask.All;
                taskItem.EveryonePermissions = 0;
                taskItem.NextPermissions = (uint)PermissionMask.All;
                taskItem.GroupID = m_host.GroupID;
                taskItem.GroupPermissions = 0;
                taskItem.Flags = 0;
                taskItem.PermsGranter = UUID.Zero;
                taskItem.PermsMask = 0;
                taskItem.AssetID = asset.FullID;

                m_host.Inventory.AddInventoryItem(taskItem, false, true);
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, DELAY);
            }
        }

        public string llGetDisplayName(string id)
        {
            return ReturnUserFirstLastIfOnSim(id);
        }

        private string ReturnUserFirstLastIfOnSim(string id)
        {
            UUID presenceId;

            if (UUID.TryParse(id, out presenceId))
            {
                ScenePresence presence = World.GetScenePresence(presenceId);
                if (presence != null)
                    return presence.Name;
            }

            return String.Empty;
        }

        public string llGetUsername(string id)
        {
            return ReturnUserFirstLastIfOnSim(id);
        }

        public void llRequestDisplayName(string id)
        {
            llRequestUsername(id);
        }

        public void llRequestUsername(string id)
        {
            const int DELAY = 100;

            UUID uuid = (UUID)id;
            UUID rq = UUID.Random();

            m_ScriptEngine.SysReturn(m_itemID, rq.ToString(), DELAY);

            UserProfileData userProfile = World.CommsManager.UserService.GetUserProfile(uuid);

            string reply = String.Empty;

            if (userProfile != null)
                reply = userProfile.Name;

            m_ScriptEngine.PostObjectEvent(m_localID,
                    new EventParams("dataserver", new Object[] { rq.ToString(), reply },
                    new DetectParams[0]));
        }

        // Gender is VisualParams[31]. 0 for female, 255 for male. >128 is male. 127 or 128 may be "both"
        public int iwGetAppearanceParam(string who, int which)
        {
            UUID agentId = UUID.Zero;
            if (!UUID.TryParse(who, out agentId))
                return -1;

            ScenePresence presence = World.GetScenePresence(agentId);
            if (presence == null)
                return -1;
            if (presence.Appearance == null)
                return -1;

            // Special case which == -1 meaning as a way to find out the upper limit
            if (which == -1)
                return presence.Appearance.VisualParams.Length;

            // Other than that, range validate and return if valid
            if ((which < 0) || (which >= presence.Appearance.VisualParams.Length))
                return -1;
            return presence.Appearance.VisualParams[which];
        }

        public int llSetRegionPos(LSL_Vector args)
        {
            LSL_Vector position = ValidLocation(args.X, args.Y, args.Z, true);

            if (m_host.ParentGroup.RootPart.AttachmentPoint != 0)
            {
                ScenePresence avatar = World.GetScenePresence(m_host.AttachedAvatar);
                if (avatar == null)
                    return 0;
                avatar.StandUp(false, true);
                avatar.Teleport(position);
            }
            else
                SetPos(m_host.ParentGroup.RootPart, position, false);
            return 1;
        }

        private bool HasLandPrivileges(ILandObject parcel)
        {
            // Agent must be over the script owner's land.  Also includes EO, EM, etc.
            if (World.IsLandOwner(m_host.OwnerID, parcel))
                return true;

            // Also support case where script owner is a group member that has Eject perms on group land.
            if (!parcel.landData.IsGroupOwned)   // group-deed land parcel
                return false;
            return World.Permissions.CanEditParcel(m_host.OwnerID, parcel, GroupPowers.LandEjectAndFreeze);
        }

        private bool IsTeleportAuthorized(ScenePresence targetSP)
        {
            // Agent must be in the same region or code farther down will fail.
            if (targetSP.IsChildAgent)
                return false;

            // Otherwise, always allow HUDs, attachments and objects owned by the same user.
            if (targetSP.UUID == m_host.OwnerID)
                return true;

            // scripts cannot force-TP gods, unless the god is the owner of the script (above)
            if (targetSP.GodLevel > 0)
                return false;

            // Otherwise check the land ownership override situations
            if (World.IsEstateManager(m_host.OwnerID))
                return true;

            // If it gets this far it's not EO, EM, or owner match, use land privileges.
            Vector3 objectPos = m_host.ParentGroup.AbsolutePosition;   // calculate this only once
            ILandObject objectLand = World.LandChannel.GetLandObject(objectPos.X, objectPos.Y);
            if (objectLand == null)
                return false;   // can't determine land priviledges.

            Vector3 agentPos = targetSP.AbsolutePosition;
            ILandObject agentLand = World.LandChannel.GetLandObject(agentPos.X, agentPos.Y);
            // See comment below, don't return on null here.

            // Agent must be over the script owner's land.  Also includes EO, EM, etc.
            if (HasLandPrivileges(objectLand))
            {
                // Error recovery, or anti-hacker handling... 
                // If the avatar is in this region, and the script has land privileges, but
                // the parcel under the avatar can NOT be determined, authorize the teleport.
                if (agentLand == null)
                    return true;

                // Agent is over a land parcel, see if we have land privileges
                if (HasLandPrivileges(agentLand))
                    return true;
            }

            return false;
        }

        public void iwTeleportAgent(string agent, string region, Vector3 pos, Vector3 lookAt)
        {
            const int delay = 0;

            try
            {
                UUID agentId = new UUID();

                if (UUID.TryParse(agent, out agentId))
                {
                    // agent must be known to this region
                    ScenePresence targetSP = World.GetScenePresence(agentId);
                    if (targetSP == null)
                        return;

                    if (!IsTeleportAuthorized(targetSP))
                        return;

                    if (String.IsNullOrEmpty(region))
                        region = targetSP.Scene.RegionInfo.RegionName;
                    else
                    if (region != targetSP.Scene.RegionInfo.RegionName) // diff region?
                        targetSP.ControllingClient.SendTeleportLocationStart();
                    World.RequestTeleportLocation(targetSP.ControllingClient,
                        region, pos, lookAt, (uint)TeleportFlags.ViaLocation);
                }
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }
        //        string llGenerateKey();

        public string llGenerateKey()
        {
            return UUID.Random().ToString();
        }

        public string llGetEnv(string name)
        {
            return m_host.ParentGroup.Scene.GetEnv(name);
        }

        public void llSetAngularVelocity(LSL_Vector force, int local)
        {
            m_host.ParentGroup.SetAngularVelocity(force, local != 0);
        }

        public LSL_List llGetPhysicsMaterial()
        {
            PhysicsActor physActor = m_host.PhysActor;
            if (physActor != null)
            {
                List<object> results = new List<object>();
                IMaterial mat = physActor.GetMaterial();

                results.Add((float)mat.GravityMultiplier);
                results.Add((float)mat.Restitution);
                results.Add((float)mat.StaticFriction);
                results.Add((float)mat.Density);
                return new LSL_List(results);
            }
            else
            {
                return new LSL_List();
            }
        }

        public void llSetPhysicsMaterial(int mask, float gravityMultiplier, float restitution, float friction, float density)
        {
            PhysicsActor physActor = m_host.PhysActor;
            if (physActor != null)
            {
                MaterialChanges changes = (MaterialChanges)mask;
                physActor.SetMaterial(
                    new MaterialDesc
                    {
                        Restitution = restitution,
                        StaticFriction = friction,
                        DynamicFriction = friction / 1.75f,
                        Density = density,
                        GravityMultiplier = gravityMultiplier,
                        MaterialPreset = MaterialDesc.NO_PRESET
                    },
                    true,
                    changes
                );
                PhySleep();
            }
            else
            {
                string msg = String.Format("llSetPhysicsMaterial cannot be called for phantom prim #{0} '{1} in '{2}.", m_host.LinkNum, m_host.Name, m_host.ParentGroup.Name);
                m_log.DebugFormat("[LSL]: {0}", msg);
                ScriptShoutError(msg);
            }
        }

        public void llSetVelocity(LSL_Vector force, int local)
        {
            m_host.ParentGroup.SetVelocity(force, local == 0 ? false : true);
        }

        private int ManageEstateAccess(int action, UUID targetId)
        {
            int rc = 0;
            Scene scene = m_host.ParentGroup.Scene;
            EstateResult result = EstateResult.Success;
            IEstateModule estate = World.RequestModuleInterface<IEstateModule>();
            if (estate == null)
                return 0;

            bool canmanage = scene.Permissions.CanIssueEstateCommand(m_host.OwnerID, false);
            if (scene.Permissions.BypassPermissions() || (action == ScriptBaseClass.ESTATE_ACCESS_QUERY_CAN_MANAGE) || canmanage)
            {
                // m_log.DebugFormat("[ManageEstate] action={0} id={1} canmanage={2}", action, targetId, canmanage);
                switch (action)
                {
                    // A script needs to be able to determine silently whether it can manage an estate.
                    // This returns that capability.
                    case ScriptBaseClass.ESTATE_ACCESS_QUERY_CAN_MANAGE:
                        result = (canmanage) ? EstateResult.Success : EstateResult.InvalidReq;
                        break;

                    case ScriptBaseClass.ESTATE_ACCESS_ALLOWED_AGENT_ADD:
                        result = estate.EstateAllowUser(targetId, true);
                        break;

                    case ScriptBaseClass.ESTATE_ACCESS_ALLOWED_AGENT_REMOVE:
                        result = estate.EstateAllowUser(targetId, false);
                        break;

                    case ScriptBaseClass.ESTATE_ACCESS_QUERY_ALLOWED_AGENT:
                        result = estate.EstateQueryAllowedUser(targetId);
                        break;

                    case ScriptBaseClass.ESTATE_ACCESS_ALLOWED_GROUP_ADD:
                        result = estate.EstateAllowGroup(targetId, true);
                        break;

                    case ScriptBaseClass.ESTATE_ACCESS_ALLOWED_GROUP_REMOVE:
                        result = estate.EstateAllowGroup(targetId, false);
                        break;

                    case ScriptBaseClass.ESTATE_ACCESS_QUERY_ALLOWED_GROUP:
                        result = estate.EstateQueryAllowedGroup(targetId);
                        break;

                    case ScriptBaseClass.ESTATE_ACCESS_BANNED_AGENT_ADD:
                        result = estate.EstateBanUser(targetId, true);
                        break;

                    case ScriptBaseClass.ESTATE_ACCESS_BANNED_AGENT_REMOVE:
                        result = estate.EstateBanUser(targetId, false);
                        break;

                    case ScriptBaseClass.ESTATE_ACCESS_QUERY_BANNED_AGENT:
                        result = estate.EstateQueryBannedUser(targetId);
                        break;

                    default:
                        // Invalid action, return FALSE
                        result = EstateResult.InvalidReq;
                        break;
                }

                if (result == EstateResult.Success)
                    rc = 1;
            }
            else
            {
                ScriptShoutError("llManageEstateAccess object owner must manage estate.");
            }

            return rc;
        }

        public string UserNameToReport(UUID agentId, bool onlyIfCached)
        {
            string name = World.CommsManager.UserService.Key2Name(agentId, onlyIfCached);
            if (!String.IsNullOrEmpty(name))
                return name;

            return agentId.ToString();
        }

        public string GroupNameToReport(UUID groupId)
        {
            Scene scene = m_host.ParentGroup.Scene;
            IGroupsModule groupsModule = scene.RequestModuleInterface<IGroupsModule>();
            GroupRecord group = groupsModule.GetGroupRecord(groupId);
            if (group == null)
                return groupId.ToString();

            return group.GroupName;
        }

        public void llManageEstateAccess(int action, string avatarIdStr)
        {
            int delay = 0;
            int rc = 0;

            try
            {
                UUID targetId = UUID.Zero;
                if (UUID.TryParse(avatarIdStr, out targetId))
                {
                    rc = ManageEstateAccess(action, targetId);
                    if (rc != 0)
                    {
                        string regionName = m_host.ParentGroup.Scene.RegionInfo.RegionName;
                        string msg = null;
                        switch (action) {
                        case ScriptBaseClass.ESTATE_ACCESS_ALLOWED_AGENT_ADD:
                                msg = UserNameToReport(targetId, false) + " has been added to the allowed user list for " + regionName;
                            break;
                        case ScriptBaseClass.ESTATE_ACCESS_ALLOWED_AGENT_REMOVE:
                            msg = UserNameToReport(targetId, false) + " has been removed from the allowed user list for " + regionName;
                            break;
                        case ScriptBaseClass.ESTATE_ACCESS_ALLOWED_GROUP_ADD:
                            msg = GroupNameToReport(targetId) + " has been added to the allowed group list for " + regionName;
                            break;
                        case ScriptBaseClass.ESTATE_ACCESS_ALLOWED_GROUP_REMOVE:
                            msg = GroupNameToReport(targetId) + " has been removed from the allowed group list for " + regionName;
                            break;
                        case ScriptBaseClass.ESTATE_ACCESS_BANNED_AGENT_ADD:
                            msg = UserNameToReport(targetId, false) + " has been banned from " + regionName;
                            break;
                        case ScriptBaseClass.ESTATE_ACCESS_BANNED_AGENT_REMOVE:
                            msg = UserNameToReport(targetId, false) + " has been removed from the banned list for " + regionName;
                            break;
                        default:
                            break;
                        }
                        if (msg != null)
                        {
                            bool silentEstateManagement = false;
                            lock (m_host.TaskInventory)
                            {
                                if (m_host.TaskInventory.ContainsKey(InventorySelf()))
                                {
                                    TaskInventoryItem item = m_host.TaskInventory[InventorySelf()];
                                    silentEstateManagement = ((item.PermsMask & ScriptBaseClass.PERMISSION_SILENT_ESTATE_MANAGEMENT) == ScriptBaseClass.PERMISSION_SILENT_ESTATE_MANAGEMENT);
                                }
                            }
                            if (!silentEstateManagement)
                            {
                                SendIM(m_host.OwnerID, msg);
                                delay = 200;
                            }
                        }
                    }
                }
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, rc, delay);
            }
        }

        private string StripOneBOM(string str, ref bool stripped)
        {
            stripped = true;

                 if (str.StartsWith("\xFF\xFE\x00\x00")) return str.Substring(4);
            else if (str.StartsWith("\x00\x00\xFF\xFF")) return str.Substring(4);
            else if (str.StartsWith("\xEF\xBB\xBF")) return str.Substring(3);
            else if (str.StartsWith("\xFF\xFE")) return str.Substring(2);
            else if (str.StartsWith("\xFE\xFF")) return str.Substring(2);

            stripped = false;
            return str;
        }

        private string StripBOM(string str)
        {
            bool stripped = false;
            do
            {
                str = StripOneBOM(str, ref stripped);
            } while (stripped);
            return str;
        }

        private string OSDToJsonStringValue(OSD specVal)
        {
            string ret;
            switch (specVal.Type)
            {
                case OSDType.Boolean:
                    ret = specVal.AsBoolean() ? ScriptBaseClass.JSON_TRUE : ScriptBaseClass.JSON_FALSE;
                    break;
                case OSDType.Real:
                    ret = specVal.AsReal().ToString("0.0#####");
                    break;
                default:
                    ret = specVal.ToString();
                    break;
            }
            return ret;
        }

        public string llJsonGetValue(string json, LSL_List specifiers)
        {
            json = StripBOM(json);
            try
            {
                OSD o = OSDParser.DeserializeJson(json);
                OSD specVal = JsonGetSpecific(o, specifiers, 0);
                if (specVal == null) return ScriptBaseClass.JSON_INVALID;
                string ret = OSDToJsonStringValue(specVal);
                if (String.IsNullOrEmpty(ret)) return ScriptBaseClass.JSON_NULL;
                return ret;
            }
            catch (Exception)
            {
                return ScriptBaseClass.JSON_INVALID;
            }
        }

        public LSL_List llJson2List(string text)
        {
            text = StripBOM(text);
            try
            {
                // Special case an empty string as meaning empty list
                if (String.IsNullOrEmpty(text))
                    return new LSL_List();
                LitJson.JsonData json = DetectJson(text);
                if (json == null)
                    json = new LitJson.JsonData(text);  // interpret non-json as a string
                OSD o = OSDParser.DeserializeJson(json);
                return JsonNode2List(o);
            }
            catch (Exception)
            {
                return new LSL_List(ScriptBaseClass.JSON_INVALID);
            }
        }

        private LSL_List JsonNode2List(OSD node)
        {
            if (node.Type == OSDType.Integer)
            {
                return new LSL_List(node.AsInteger());
            }
            else if (node.Type == OSDType.Boolean)
            {
                return new LSL_List(node.AsBoolean() ? "true" : "false");
            }
            else if (node.Type == OSDType.Real)
            {
                return new LSL_List((float)node.AsReal());
            }
            else if ((node.Type == OSDType.UUID) || (node.Type == OSDType.String))
            {
                return new LSL_List(node.AsString());
            }
            else if (node.Type == OSDType.Unknown)
            {
                return new LSL_List(ScriptBaseClass.JSON_NULL);
            }
            else if (node.Type == OSDType.Array)
            {
                // JSON arrays are stored in LSL lists as strings
                List<object> resp = new List<object>();
                OSDArray ar = node as OSDArray;
                foreach (OSD o in ar)
                    resp.Add(JsonNode2ListElement(o, false));

                return new LSL_List(resp);
            }
            else if (node.Type == OSDType.Map)
            {
                // JSON objects are stored in LSL lists as strings
                List<object> resp = new List<object>();
                OSDMap ar = node as OSDMap;
                foreach (KeyValuePair<string, OSD> o in ar)
                {
                    resp.Add(o.Key.ToString());
                    resp.Add(JsonNode2ListElement(o.Value, false));
                }
                return new LSL_List(resp);
            }

            throw new Exception(ScriptBaseClass.JSON_INVALID);
        }

        // This function returns the stringified version of a JSON array/object element
        private object JsonNode2ListElement(OSD node, bool nested)
        {
            if (node.Type == OSDType.Integer)
            {
                return node.AsInteger();
            }
            else if (node.Type == OSDType.Boolean)
            {
                if (nested)
                    return (node.AsBoolean() ? "true" : "false");
                else
                    return (node.AsBoolean() ? ScriptBaseClass.JSON_TRUE : ScriptBaseClass.JSON_FALSE);
            }
            else if (node.Type == OSDType.Real)
            {
                if (nested)
                    return ((float)node.AsReal()).ToString("0.0#####");
                else
                    return (float)node.AsReal();
            }
            else if (node.Type == OSDType.UUID)
            {
                return node.AsString();
            }
            else if (node.Type == OSDType.String)
            {
                if (nested)
                    return "\""+node.AsString()+"\"";
                else
                    return node.AsString();
            }
            else if (node.Type == OSDType.Unknown)
            {
                if (nested)
                    return "null";
                else
                    return ScriptBaseClass.JSON_NULL;
            }
            else if (node.Type == OSDType.Array)
            {
                string resp = String.Empty;
                OSDArray ar = node as OSDArray;
                foreach (OSD o in ar)
                {
                    if (!String.IsNullOrEmpty(resp)) resp += ",";
                    resp += JsonNode2ListElement(o, true);
                }

                return "[" + resp + "]";
            }
            else if (node.Type == OSDType.Map)
            {
                string resp = String.Empty;
                OSDMap ar = node as OSDMap;
                foreach (KeyValuePair<string, OSD> o in ar)
                {
                    if (!String.IsNullOrEmpty(resp)) resp += ",";
                    resp += "\""+o.Key.ToString() + "\":" + JsonNode2ListElement(o.Value, true).ToString();
                }
                return "{" + resp + "}";
            }

            throw new Exception(ScriptBaseClass.JSON_INVALID);
        }

        public string llList2Json(string type, LSL_List values)
        {
            try
            {
                if (type == ScriptBaseClass.JSON_ARRAY)
                {
                    OSDArray array = new OSDArray();
                    foreach (object o in values.Data)
                    {
                        array.Add(ListToJson(o));
                    }
                    return OSDParser.SerializeJsonString(array);
                }
                else if (type == ScriptBaseClass.JSON_OBJECT)
                {
                    OSDMap map = new OSDMap();
                    for (int i = 0; i < values.Data.Length; i += 2)
                    {
                        if (!(values.Data[i] is string))
                            return ScriptBaseClass.JSON_INVALID;
                        if (i + 1 >= values.Data.Length)
                            return ScriptBaseClass.JSON_INVALID;
                        map.Add((string)values.Data[i], ListToJson(values.Data[i + 1]));
                    }
                    return OSDParser.SerializeJsonString(map);
                }
                return ScriptBaseClass.JSON_INVALID;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        double FloatAsDouble(float val)
        {
            // force accurate to 6 digits only, but double form
            return ((double)(int)(((float)val * 100000.0) + 0.5)) / 100000.0;
        }

        private bool IsJsonFramed(string str, char start, char end)
        {
            return ((str.Length >= 2) && (str[0] == start) && (str[str.Length - 1] == end));
        }

        // This function returns null for JSON_INVALID cases.
        private LitJson.JsonData DetectJson(string orig)
        {
            string trimmed = orig.Trim();

            try
            {
                if (trimmed == String.Empty)
                    return null;

                if (IsJsonFramed(trimmed, '[', ']'))
                    return LitJson.JsonMapper.ToObject(trimmed);

                if (IsJsonFramed(trimmed, '{', '}'))
                    return LitJson.JsonMapper.ToObject(trimmed);

                if (trimmed == ScriptBaseClass.JSON_FALSE)
                    return new LitJson.JsonData(false);

                if (trimmed == ScriptBaseClass.JSON_TRUE)
                    return new LitJson.JsonData(true);

                if (IsJsonFramed(trimmed, '"', '"'))
                    return new LitJson.JsonData(trimmed.Substring(1, trimmed.Length - 2));

                // If none of the above, it must be numeric.
                if (trimmed.All(c => "0123456789".Contains(c)))
                {
                    long lval = Convert.ToInt64(trimmed);
                    int ival = (int)lval;
                    return (lval == (long)ival) ? new LitJson.JsonData(ival) : new LitJson.JsonData(lval);
                }

                if (trimmed.All(c => "-0123456789.eE+".Contains(c)))
                {
                    bool isDouble = true;
                    double val;
                    try {
                        val = Convert.ToDouble(trimmed);
                    }
                    catch (Exception)
                    {
                        isDouble = false;
                        val = 0.0;
                    }
                    if (isDouble)
                    {
                        return new LitJson.JsonData(val);
                    }
                }

                if (trimmed == "true")
                    return new LitJson.JsonData(true);
                if (trimmed == "false")
                    return new LitJson.JsonData(false);
                if (trimmed == "null")
                    return new LitJson.JsonData(null);
            }
            catch (LitJson.JsonException)
            {
                // Invalid JSON passed to this function (return null)
            }

            // JSON strings must be quoted (LL testcase expects JSON_INVALID)
            return null;    // treated as JSON_INVALID
        }

        private OSD ListToJson(object o)
        {
            if (o is float)
                return OSD.FromReal(FloatAsDouble((float)o));

            if (o is int)
                return OSD.FromInteger((int)o);

            if (o is LSL_Rotation)
                return OSD.FromString(((LSL_Rotation)o).ToString());

            if (o is LSL_Vector)
                return OSD.FromString(((LSL_Vector)o).ToString());

            if (o is string)
            {
                string str = (string)o;
                if (str == ScriptBaseClass.JSON_NULL)
                    return new OSD();
                if (str == ScriptBaseClass.JSON_FALSE)
                    return OSD.FromBoolean(false);
                if (str == ScriptBaseClass.JSON_TRUE)
                    return OSD.FromBoolean(true);

                LitJson.JsonData json;
                if (IsJsonFramed(str, '[', ']') || IsJsonFramed(str, '{', '}'))
                {
                    json = LitJson.JsonMapper.ToObject(str);
                }
                else
                {
                    if (IsJsonFramed(str, '"', '"'))
                        str = str.Substring(1, str.Length - 2);
                    json = new LitJson.JsonData(str);
                }
                return OSDParser.DeserializeJson(json);
            }

            throw new Exception(ScriptBaseClass.JSON_INVALID);
        }

        private OSD JsonGetSpecific(OSD o, LSL_List specifiers, int i)
        {
            if (specifiers.Length == 0)
                return o;

            object spec = specifiers.Data[i];
            OSD nextVal = null;
            if (o is OSDArray)
            {
                OSDArray array = (OSDArray)o;
                if (spec is int)
                    if (((int)spec >= 0) && ((int)spec < array.Count))
                        nextVal = ((OSDArray)o)[(int)spec];
            }
            if (o is OSDMap)
            {
                OSDMap map = (OSDMap)o;
                if (spec is string)
                    if (map.ContainsKey((string)spec))
                        nextVal = map[(string)spec];
            }
            if (nextVal != null)
            {
                if (specifiers.Data.Length - 1 > i)
                    return JsonGetSpecific(nextVal, specifiers, i + 1);
            }
            return nextVal;
        }

        public string llJsonSetValue(string str, LSL_List specifiers, string value)
        {
            str = StripBOM(str);
            try
            {
                LitJson.JsonData json = LitJson.JsonMapper.ToObject(str);
                LitJson.JsonType jtype = json.GetJsonType();

                OSD o;
                if (jtype == LitJson.JsonType.None)
                    o = new OSDArray();
                else
                    o= OSDParser.DeserializeJson(str);
                JsonSetSpecific(null, o, specifiers, 0, value);
                return OSDToJsonStringValue(o);
            }
            catch (Exception)
            {
            }
            return ScriptBaseClass.JSON_INVALID;
        }

        private void JsonSetSpecific(OSD parent, OSD o, LSL_List specifiers, int i, string val)
        {
            object spec = specifiers.Data[i];
            object specNext = i + 1 == specifiers.Data.Length ? null : specifiers.Data[i + 1];
            OSD nextVal = null;
            if (o is OSDArray)
            {
                OSDArray array = ((OSDArray)o);
                if (spec is int)
                {
                    if ((int)spec == ScriptBaseClass.JSON_APPEND)
                    {
                        array.Add(JsonBuildRestOfSpec(specifiers, i + 1, val));
                    }
                    else
                    {
                        int v = (int)spec;
                        if (v > array.Count)
                            throw new Exception(ScriptBaseClass.JSON_INVALID);
                        else if (v == array.Count)
                        {
                            if (val == ScriptBaseClass.JSON_DELETE)
                                throw new Exception(ScriptBaseClass.JSON_INVALID);
                            else
                                array.Add(JsonBuildRestOfSpec(specifiers, i + 1, val));
                        }
                        else
                        {
                            if (specNext == null)
                            {
                                // no more specifiers, this is the final one
                                if (val == ScriptBaseClass.JSON_DELETE)
                                    array.RemoveAt(v);
                                else
                                    array[v] = JsonBuildRestOfSpec(specifiers, i + 1, val);
                            }
                            else
                                nextVal = array[v];
                        }
                    }
                }
            }
            else
            if (o is OSDMap)
            {
                if (spec is string)
                {
                    OSDMap map = ((OSDMap)o);
                    if (map.ContainsKey((string)spec))
                    {
                        if (specNext == null)
                        {
                            // no more specifiers, this is the final one
                            if (val == ScriptBaseClass.JSON_DELETE)
                                map.Remove((string)spec);
                            else
                                map[(string)spec] = JsonBuildRestOfSpec(specifiers, i + 1, val);
                        }
                        else
                            nextVal = map[(string)spec];
                    }
                    else
                    {
                        if (val == ScriptBaseClass.JSON_DELETE)
                            throw new Exception(ScriptBaseClass.JSON_INVALID);
                        else
                            map.Add((string)spec, JsonBuildRestOfSpec(specifiers, i + 1, val));
                    }
                }

                // This is the case where we need to completely replace a whole node tree with something that doesn't match at all
                if ((parent != null) && (spec is int))
                {
                    if (parent is OSDArray)
                    {
                        OSDArray parentArray = (OSDArray)parent;
                        object parentSpec = specifiers.Data[i - 1];
                        if (parentSpec is int)
                            parentArray[(int)parentSpec] = JsonBuildRestOfSpec(specifiers, i, val);
                    }
                }
            }
            else
            {
                // spec is trying to index into something other than an array or object
                throw new Exception(ScriptBaseClass.JSON_INVALID);
            }

            if (nextVal != null)
            {
                if (specifiers.Data.Length - 1 > i)
                {
                    JsonSetSpecific(o, nextVal, specifiers, i + 1, val);
                    return;
                }
            }
        }

        private OSD JsonBuildRestOfSpec(LSL_List specifiers, int i, string val)
        {
            object spec = i >= specifiers.Data.Length ? null : specifiers.Data[i];
            object specNext = i + 1 >= specifiers.Data.Length ? null : specifiers.Data[i + 1];

            if (spec == null)
            {
                LitJson.JsonData json = DetectJson(val);
                return (json == null) ? OSD.FromString(val) : OSDParser.DeserializeJson(json);
            }

            if (spec is int)
            {
                OSDArray array = new OSDArray();
                array.Add(JsonBuildRestOfSpec(specifiers, i + 1, val));
                return array;
            }
            else if (spec is string)
            {
                OSDMap map = new OSDMap();
                map.Add((string)spec, JsonBuildRestOfSpec(specifiers, i + 1, val));
                return map;
            }
            return new OSD();
        }

        public string llJsonValueType(string str, LSL_List specifiers)
        {
            str = StripBOM(str);
            LitJson.JsonData json = DetectJson(str);
            if (json == null)
                return ScriptBaseClass.JSON_INVALID;

            OSD o = OSDParser.DeserializeJson(json);
            OSD specVal = JsonGetSpecific(o, specifiers, 0);
            if (specVal == null)
                return ScriptBaseClass.JSON_INVALID;

            switch (specVal.Type)
            {
                case OSDType.Array:
                    return ScriptBaseClass.JSON_ARRAY;
                case OSDType.Boolean:
                    return specVal.AsBoolean() ? ScriptBaseClass.JSON_TRUE : ScriptBaseClass.JSON_FALSE;
                case OSDType.Integer:
                case OSDType.Real:
                    return ScriptBaseClass.JSON_NUMBER;
                case OSDType.Map:
                    return ScriptBaseClass.JSON_OBJECT;
                case OSDType.String:
                case OSDType.UUID:
                    return ScriptBaseClass.JSON_STRING;
                case OSDType.Unknown:
                    return ScriptBaseClass.JSON_NULL;
            }

            return ScriptBaseClass.JSON_INVALID;
        }

        public LSL_List iwReverseList(LSL_List src, int stride)
        {
            if (src.Length <= 1) return src;
            if (stride < 1) return new LSL_List(src.Data.Reverse());
            if(src.Length % stride != 0)
            {
                LSLError(string.Format("Error: stride argument is {0}, but source list length is not divisible by {0}", stride));
                return new LSL_List();
            }
            
            List<object> ret = new List<object>();
            for(int a = src.Length - 1; a >= 0; a -= stride)
            {
                ret.AddRange(src.GetSublist(a - (stride - 1), a).Data.ToList());
            }

            return new LSL_List(ret);
        }

        public string iwReverseString(string src)
        {
            if (src.Length <= 1) return src;
            return new string(src.Reverse().ToArray());
        }

        public LSL_List iwListRemoveDuplicates(LSL_List src)
        {
            if(src.Length <= 1) return src;
            //yarrr...
            return new LSL_List(  src.Data.Distinct().ToList()  );
        }

        public LSL_List iwListRemoveElements(LSL_List src, LSL_List elements, int count, int mode)
        {
            if (src.Length == 0 || elements.Length == 0) return src;
            if (count == 0) count = -1;
            int counted = 0;

            List<object> ret = new List<object>();

            if (mode == 0)
            {
                int len = src.Length - elements.Length + 1;
                for (int i = 0; i < len; i++)
                {
                    if (src.Data[i].Equals(elements.Data[0]))
                    {
                        if (count == -1 || counted < count)
                        {
                            int x;
                            for (x = 1; x < elements.Length; x++)
                                if (!src.Data[i + x].Equals(elements.Data[x]))
                                    break;
                            if (x == elements.Length)
                            {
                                counted++;
                                i += elements.Length - 1;
                                continue;
                            }
                        }
                    }
                    ret.Add(src.Data[i]);
                }
            }
            else
            {
                int len = src.Length;
                for (int i = 0; i < len; i++)
                {
                    if(elements.Data.Contains<object>(src.Data[i]) == false)
                    {
                        if(count == -1 || counted < count)
                        {
                            ret.Add(src.Data[i]);
                            counted++;
                        }
                    }
                }
            }

            return new LSL_List(ret);
        }

        public int iwListIncludesElements(LSL_List src, LSL_List elements, int any)
        {
            if (elements.Length == 0 || src.Length == 0) return 0;
            for(int a=0; a < elements.Length; a++)
            {
                bool found = false;
                for(int b=0; b < src.Length; b++)
                {
                    //src.Data[i].Equals(test.Data[0])
                    if (src.Data[b].Equals(elements.Data[a]))
                    {
                        found = true;
                        break;
                    }
                }
                if (any == 1)
                {
                    if(found == true)
                    {
                        return 1;
                    }
                } else
                {
                    if(found == false)
                    {
                        return 0;
                    }
                }
            }
            if (any == 1) return 0;
            else return 1;
        }

        public int iwChar2Int(string src, int index)
        {
            if (index < 0) index = src.Length + index;
            if (Math.Abs(index) >= src.Length) return 0;
            char c = src[index];
            return (int)c;
        }

        public string iwInt2Char(int num)
        {
            if (num < 0 || num > 0xffff) return String.Empty;
            return Convert.ToChar(num).ToString();
        }

        public string iwReplaceString(string str, string pattern, string replacement)
        {
            if (String.IsNullOrEmpty(str) || String.IsNullOrEmpty(pattern)) return str;
            if (String.IsNullOrEmpty(replacement)) return str.Replace(pattern, null);
            if (replacement.Length > 1024 || pattern.Length > 1024) return str;
            return str.Replace(pattern, replacement);
        }

        public string iwFormatString(string str, LSL_List values)
        {
            if (String.IsNullOrEmpty(str)) return str;

            int len = values.Length;

            //var clock = new System.Diagnostics.Stopwatch();
            //var freq = System.Diagnostics.Stopwatch.Frequency;

            float time1 = Util.GetLongTickCount();

            for (int i = 0; i < len; i++)
            {
                string pattern = "{" + Convert.ToString(i) + "}";
                string val = values.GetLSLStringItem(i);
                if (val.Length > 1024)
                {
                    val = val.Substring(0, 1023);
                }

                if (str.Contains(pattern))
                {
                    if (!String.IsNullOrEmpty(val))
                    {
                        str = str.Replace(pattern, val);
                    }
                }
                else
                {
                    break;
                }

                if (str.Length > 32768)
                {
                    LSLError("Return value from iwFormatString is greater than 64kb");
                    return String.Empty;
                }

                float time2 = Util.GetLongTickCount();
                if (time2 - time1 > 0)
                {
                    this.ScriptSleep(100);
                    time1 = time2;
                }
            }

            return str;
        }

        public int iwMatchString(string str, string pattern, int matchType)
        {
            int len1 = str.Length;
            int len2 = pattern.Length;
            if (len1 == 0 || len2 == 0)
            {
                if (matchType <= 1 && (len1 == 0 && len2 == 0)) return 1;
                else return 0;
            }
            switch (matchType)
            {
                case -2: //IW_MATCH_INCLUDE
                    return (str.IndexOf(pattern) != -1) ? 1 : 0;
                case -1: //IW_MATCH_EQUAL
                    return (str == pattern) ? 1 : 0;
                case 0: //IW_MATCH_HEAD
                    return str.StartsWith(pattern) ? 1 : 0;
                case 1: //IW_MATCH_TAIL
                    return str.EndsWith(pattern) ? 1 : 0;
                case 2: //IW_MATCH_REGEX
                    Regex r = new Regex("^" + pattern + "$");
                    return (r.Match(str).Length != 0) ? 1 : 0;
                case 3: //IW_MATCH_COUNT
                    return Regex.Matches(str, Regex.Escape(pattern)).Count;
                case 4: //IW_MATCH_COUNT_REGEX
                    return Regex.Matches(str, pattern).Count;
            }
            return 0;
        }

        private int listCompare(LSL_List list1, LSL_List list2)
        {
            if (list1.Length != list2.Length) return 0;
            int len = list1.Length;

            for (int i = 0; i < len; i++)
            {
                int t1 = llGetListEntryType(list1, i);
                int t2 = llGetListEntryType(list2, i);
                if (t1 != t2 || (t1==0 && t2==0)) return 0;
                if (t1 == 1 && (list1.GetLSLIntegerItem(i) != list2.GetLSLIntegerItem(i))) return 0;
                if (t1 == 2 && (list1.GetLSLFloatItem(i) != list2.GetLSLFloatItem(i))) return 0;
                else if ((t1 == 3 || t1 == 4) && (list1.GetLSLStringItem(i) != list2.GetLSLStringItem(i))) return 0;
                else if (t1 == 5 && (list1.GetVector3Item(i) != list2.GetVector3Item(i))) return 0;
                else if (t1 == 6 && (list1.GetQuaternionItem(i) != list2.GetQuaternionItem(i))) return 0;
            }
            return 1;
        }

        public int iwMatchList(LSL_List list1, LSL_List list2, int matchType)
        {
            int len1=list1.Length;
            int len2 = list2.Length;
            if (len1 == 0 || len1 == 0)
            {
                if (matchType <= 1) return (len1 == 0 && len2 == 0) ? 1 : 0;
                else return 0;
            }
            switch (matchType)
            {
                case -1: //IW_MATCH_EQUAL
                    if (len1 == 0 || len2 == 0 || len1 != len2) return 0;
                    return listCompare(list1, list2);
                case 0: //IW_MATCH_HEAD
                    if (len1 == 0 || len2 == 0 || len1 < len2) return 0;
                    return listCompare(list1.GetSublist(0, len2-1),list2);
                case 1: //IW_MATCH_TAIL
                    if (len1 == 0 || len2 == 0 || len1 < len2) return 0;
                    return listCompare(list1.GetSublist(len2, len1-1), list2);
                case 2: //IW_MATCH_REGEX
                    LSLError("IW_MATCH_REGEX not implemented for iwMatchList.");
                    break;
                case 3: //IW_MATCH_COUNT
                    LSLError("IW_MATCH_COUNT not implemented for iwMatchList.");
                    break;
                case 4: //IW_MATCH_COUNT_REGEX
                    LSLError("IW_MATCH_COUNT_REGEX not implemented for iwMatchList.");
                    break;
            }
            return 0;
        }


        //Static utility class for use with iwStringCodec
        private static class CodecUtil
        {

            //Returns true if a codec is available for conversions
            public static bool HasCodec(string codec)
            {
                switch (codec.ToLower())
                {
                    case "base16":
                    case "uuid":
                    case "base64":
                    case "base64-safe":
                    case "base4096":
                    case "base4k":
                        return true;
                }
                return false;
            }

            //
            // Encode the input to a specified codec.
            //
            public static string Encode(string str, string codec)
            {
                return Encode(Encoding.Unicode.GetBytes(str), codec);
            }

            //
            // Decode an input string from a specified codec
            //
            public static string DecodeToString(string str, string codec)
            {
                byte[] bytes = null;
                string s;
                if (codec == "uuid")
                {
                    bytes = Decode(str, "base16");
                    if (bytes == null) return null;
                    s = pad_dashes(Encoding.Unicode.GetString(bytes));
                }
                else
                {

                    bytes = Decode(str, codec);
                    if (bytes == null) return null;
                    s = Encoding.Unicode.GetString(bytes);
                }
                bytes = null;
                return s;
            }

            //
            // Encode byte data to a specified codec
            //
            public static string Encode(byte[] bytes, string codec)
            {
                switch (codec.ToLower())
                {
                    case "base16":
                        return EncodeBase16(bytes);
                    case "uuid":
                        string str = EncodeBase16(bytes);
                        if (str.Length == 32) return pad_dashes(str);
                        else return str;
                    case "base64":
                        try
                        {
                            return Convert.ToBase64String(bytes);
                        }
                        catch
                        {
                            return null;
                        }
                    case "base64-safe":
                        try
                        {
                            return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_");
                            //return HttpServerUtility.UrlTokenEncode(bytes);

                        }
                        catch
                        {
                            return null;
                        }
                    case "base4k":
                    case "base4096":
                        return EncodeBase4k(bytes);
                    default:
                        return null;
                }
            }

            //
            // Decode string data to bytes from a specified codec.
            //
            public static byte[] Decode(string str, string codec)
            {
                switch (codec.ToLower())
                {
                    case "base16":
                        return DecodeBase16(str);
                    case "uuid":
                        return DecodeBase16(str.Replace("-", String.Empty));
                    case "base64":
                        try
                        {
                            return Convert.FromBase64String(str);
                        }
                        catch
                        {
                            return null;
                        }
                    case "base64-safe":
                        try
                        {
                            return Convert.FromBase64String(str.Replace("-", "+").Replace("_", "/"));
                            //return HttpServerUtility.UrlTokenDecode(str);
                        }
                        catch
                        {
                            return null;
                        }
                    case "base4k":
                    case "base4096":
                        return DecodeBase4k(str);
                    default:
                        return null;
                }
            }

            //
            // Returns 1, 0, or -1, depending on whether or not the input string conforms to the specified codec.
            // 1 : Valid
            // 0 : Invalid
            // -1: Codec is not valid or validation is not implemented
            //
            public static int Validate(string str, string codec)
            {
                switch (codec.ToLower())
                {
                    case "base16":
                        return ValidateBase16(str);
                    case "uuid":
                        UUID u = UUID.Zero;
                        return UUID.TryParse(str, out u) ? 1 : 0;
                    case "base64":
                        return ValidateBase64(str, '+', '/');
                    case "base64-safe":
                        return ValidateBase64(str, '-', '_');
                    case "base4k":
                    case "base4096":
                        return ValidateBase4k(str);
                    default:
                        return -1;
                }
            }

            //Inserts the dashes into a UUID.
            private static string pad_dashes(string str)
            {
                return string.Format("{0}-{1}-{2}-{3}-{4}",
                    str.Substring(0, 8),
                    str.Substring(8, 4),
                    str.Substring(12, 4),
                    str.Substring(16, 4),
                    str.Substring(20, 12)
                );
            }


            //Convert a base16 string to byte data.
            private static byte[] DecodeBase16(string str)
            {
                int len = str.Length / 2;
                byte[] bytes = new byte[len];
                using (var sr = new StringReader(str))
                {
                    for (int i = 0; i < len; i++)
                    {
                        bytes[i] = Convert.ToByte(new string(new char[2] { (char)sr.Read(), (char)sr.Read() }), 16);
                    }
                }
                return bytes;
            }

            //Convert byte data to base16 encoding
            private static string EncodeBase16(byte[] bytes)
            {
                StringBuilder output = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes)
                {
                    output.AppendFormat("{0:X2}", b);
                }
                string ret = output.ToString().ToLower();
                output.Clear();
                return ret;
            }

            //Check if the input string is a valid base16 string
            private static int ValidateBase16(string str)
            {
                int i = 0;
                if (str.StartsWith("0x")) i = 2;
                int len = str.Length;
                if (len % 2 == 1) return 0;
                for (; i < len; i++)
                {
                    char c = str[i];
                    if (!Char.IsDigit(c) || (c >= 'a' && c <= 'f'))
                    {
                        return 0;
                    }
                }
                return 1;
            }

            //Check if the input string is a valid base64 string
            private static int ValidateBase64(string str, char token1, char token2)
            {
                int len = str.Length;
                for(int i=0; i< len; i++) {
                    char c = str[i];
                    if (!Char.IsLetterOrDigit(c) && c != token1 && c != token2)
                    {
                        if(len % 4 != 0 && (i==len-1 || i==len-2) && c != '=') return 0;
                    }
                }
                return 1;
            }

            //Convert byte data to base4096 encoding.
            // Adapted from public domain code by Adam Wozniak and Doran Zemlja
            // http://wiki.secondlife.com/w/index.php?title=Key_Compression#Base_4096_Script_.28Reduced_Code_Size.29
            private static string EncodeBase4k(byte[] inBytes)
            {
                StringBuilder ret = new StringBuilder();
                int len = inBytes.Length;
                int[] bytes = new int[len*2];
                for (int i = 0; i < (len*2); i+=2)
                {
                    bytes[i] = (inBytes[i / 2] & 0xf0) >> 4;
                    bytes[i + 1] = inBytes[i / 2] & 0xf;
                }
                len = bytes.Length;
                int extra = 0;
                for (int i = 0; i < len; i+=3)
                {
                    int A = bytes[i];
                    int B = 0;
                    int C = 0;
                    if (i + 1 < len)
                    {
                        B = bytes[i + 1];
                        if (i + 2 < len) C = bytes[i + 2];
                        else extra = 1;
                    }
                    else extra = 2;
                    
                    int D = 0xB;
                    
                    if (A == 0)
                    {
                        A = 0xE;
                        D = 8;
                    }
                    else if (A == 0xD)
                    {
                        A = 0xE;
                        D = 9;
                    }
                    else if (A == 0xF)
                    {
                        A = 0xE;
                        D = 0xA;
                    }

                    ret.Append("%E");
                    ret.Append(A.ToString("X"));
                    ret.Append("%");
                    ret.Append(D.ToString("X"));
                    ret.Append(B.ToString("X"));
                    ret.Append("%B");
                    ret.Append(C.ToString("X"));
                }

                if (extra >= 1) ret.Append("%3D");
                if (extra == 2) ret.Append("%3D");

                string output = Uri.UnescapeDataString(ret.ToString());
                ret = null;
                bytes = null;

                return output;
            }

            //Convert a base4096 string to byte data
            // Adapted from public domain code by Adam Wozniak and Doran Zemlja
            // http://wiki.secondlife.com/w/index.php?title=Key_Compression#Base_4096_Script_.28Reduced_Code_Size.29
            private static byte[] DecodeBase4k(string str)
            {
                int extra = 0;
                if (str.EndsWith("==")) extra = 2;
                else if (str.EndsWith("=")) extra = 1;

                str = Uri.EscapeDataString(str.Replace("=", String.Empty));
                //byte[] bytes = Encoding.Unicode.GetBytes(str.Replace("%", String.Empty).ToLower());
                byte[] inBytes = DecodeBase16(str.Replace("%", String.Empty));
                StringBuilder ret = new StringBuilder();


                int len = inBytes.Length;
                int[] bytes = new int[len*2];
                for (int i = 0; i < len; i++)
                {
                    bytes[i * 2] = (inBytes[i] >> 4) & 0xF;
                    bytes[(i*2)+1] = inBytes[i] & 0xF;
                }

                len *= 2;
                for (int i = 0; i+5 < len; i += 6)
                {
                    int A = bytes[i + 1];
                    int B = bytes[i + 3];
                    int C = bytes[i + 5];
                    int D = bytes[i + 2];
                    if (D == 0x8) A = 0;
                    else if (D == 0x9) A = 0xD;
                    else if (D == 0xA) A = 0xF;

                    ret.Append(A.ToString("X"));
                    ret.Append(B.ToString("X"));
                    ret.Append(C.ToString("X"));
                }
                if (extra > 0) ret.Length -= extra;
                string output = ret.ToString();
                ret = null;
                return DecodeBase16(output);
            }

            //Check if a base4096 string is valid
            //TODO
            private static int ValidateBase4k(string str)
            {
                return -1;
            }

            //Compress string data with gzip compression.
            public static string gzipCompress(string str, string codec = "base4096")
            {
                var bytes = Encoding.Unicode.GetBytes(str);
                using (var msi = new MemoryStream(bytes))
                using (var mso = new MemoryStream())
                {
                    using (var gs = new GZipStream(mso, CompressionMode.Compress))
                    {
                        msi.CopyTo(gs);
                    }
                    return Encode(mso.ToArray(), codec);
                }
            }

            //Decompress string data with gzip compression;
            public static string gzipDecompress(string str, string codec = "base4096")
            {
                var bytes = CodecUtil.Decode(str, codec);
                using (var msi = new MemoryStream(bytes))
                using (var mso = new MemoryStream())
                {
                    using (var gs = new GZipStream(msi, CompressionMode.Decompress))
                    {
                        gs.CopyTo(mso);
                    }
                    return Encoding.Unicode.GetString(mso.ToArray());
                }
            }

            
            //Encrypt Data with AES encryption
            public static byte[] AesEncrypt(byte[] valueBytes, byte[] keyBytes, string _vector)
            {
            //    return AesEncrypt<AesManaged>(valueBytes, keyBytes, _vector);
            //}
            //public static byte[] AesEncrypt<T>(byte[] valueBytes, byte[] keyBytes, string _vector)
            //        where T : SymmetricAlgorithm, new()
            //{
                byte[] vectorBytes = DecodeBase16(_vector);

                byte[] encrypted;
                using (AesManaged cipher = new AesManaged())
                {

                    cipher.Mode = CipherMode.CBC;

                    using (ICryptoTransform encryptor = cipher.CreateEncryptor(keyBytes, vectorBytes))
                    {
                        using (MemoryStream to = new MemoryStream())
                        {
                            using (CryptoStream writer = new CryptoStream(to, encryptor, CryptoStreamMode.Write))
                            {
                                writer.Write(valueBytes, 0, valueBytes.Length);
                                writer.FlushFinalBlock();
                                encrypted = to.ToArray();
                            }
                        }
                    }
                    cipher.Clear();
                }
                vectorBytes = null;
                return encrypted;
            }

            //Decrypt data with AES decryption
            public static string AesDecrypt(byte[] valueBytes, byte[] keyBytes, string _vector)
            {
            //    return AesDecrypt<AesManaged>(value, keyBytes, _vector);
            //}
            //public static string AesDecrypt<T>(byte[] valueBytes, byte[] keyBytes, string _vector) where T : SymmetricAlgorithm, new()
            //{
                byte[] vectorBytes = DecodeBase16(_vector);

                byte[] decrypted;
                int decryptedByteCount = 0;

                using (AesManaged cipher = new AesManaged())
                {

                    cipher.Mode = CipherMode.CBC;

                    try
                    {
                        using (ICryptoTransform decryptor = cipher.CreateDecryptor(keyBytes, vectorBytes))
                        {
                            using (MemoryStream from = new MemoryStream(valueBytes))
                            {
                                using (CryptoStream reader = new CryptoStream(from, decryptor, CryptoStreamMode.Read))
                                {
                                    decrypted = new byte[valueBytes.Length];
                                    decryptedByteCount = reader.Read(decrypted, 0, decrypted.Length);
                                }
                            }
                        }
                    }
                    catch
                    {
                        return String.Empty;
                    }

                    cipher.Clear();
                }
                vectorBytes = null;
                return Encoding.Unicode.GetString(decrypted, 0, decryptedByteCount);
            }

            //Escape non-ascii characters in a string
            public static string StringToAscii(string str)
            {
                StringBuilder sb = new StringBuilder();
                foreach (char c in str)
                {
                    if (c > 127) sb.Append("\\u" + ((int)c).ToString("x4"));
                    else sb.Append(c);
                }

                return sb.ToString();
            }

            //Unescape non-ascii characters from a string
            public static string AsciiToString(string str)
            {
                return Regex.Replace(
                    str,
                    @"\\u(?<Value>[a-zA-Z0-9]{4})",
                    m =>
                    {
                        return ((char)int.Parse(m.Groups["Value"].Value, System.Globalization.NumberStyles.HexNumber)).ToString();
                    });
            }

            //Helper function for Ascii Compression
            // Adapted from public domain code by Becky Pippen
            // http://wiki.secondlife.com/wiki/User:Becky_Pippen/Text_Storage
            private static string encode15BitsToChar(int num) {
                if (num < 0 || num >= 0x8000) return "�";
                num += 0x1000;
                return Uri.UnescapeDataString(
                    string.Format("%{0}%{1}%{2}",
                    (0xE0 + (num >> 12)).ToString("X"),
                    (0x80 + ((num >> 6) & 0x3F)).ToString("X"),
                    (0x80 + (num & 0x3F)).ToString("X")
                ));
            }

            //Helper function for Ascii Compression
            // Adapted from public domain code by Becky Pippen
            // http://wiki.secondlife.com/wiki/User:Becky_Pippen/Text_Storage
            private static int charToInt(string src, int index) {
                if (index < 0) index = src.Length + index;
                if (Math.Abs(index) >= src.Length) return 0;
                char c = src[index];
                return (int)c;
            }
            
            //Helper function for Ascii Compression
            // Adapted from public domain code by Becky Pippen
            // http://wiki.secondlife.com/wiki/User:Becky_Pippen/Text_Storage
            private static int decodeCharTo15Bits(string ch)
            {
                int t = Convert.ToChar(ch);
                return ((((t >> 12) & 0xFF) & 0x1F) << 12) +
                    ((((t >> 6) & 0xFF) & 0x3F) << 6) +
                    ((t & 0xFF) & 0x3F) - 0x1000;
            }

            //Compress an ascii string by encoding two characters into a single 15bit character.
            // Adapted from public domain code by Becky Pippen
            // http://wiki.secondlife.com/wiki/User:Becky_Pippen/Text_Storage
            public static string AsciiCompress(string str)
            {
                if (String.IsNullOrEmpty(str)) return str;
                str = StringToAscii(str);
                int len = str.Length;
                bool emptyEnd = false;
                if (len % 2 == 1)
                {
                    str += " ";
                    len++;
                    emptyEnd = true;
                }

                StringBuilder encoded = new StringBuilder();
                for (int i = 0; i < len; i += 2)
                {
                    encoded.Append(encode15BitsToChar(
                        charToInt(str, i) << 7 | charToInt(str, i+1)
                    ));
                }

                if (emptyEnd) encoded.Append("=");

                return encoded.ToString();
            }

            //Decompress an ascii string from 15bit encoding.
            // Adapted from public domain code by Becky Pippen
            // http://wiki.secondlife.com/wiki/User:Becky_Pippen/Text_Storage
            public static string AsciiDecompress(string str)
            {
                if (String.IsNullOrEmpty(str)) return str;
                int len = str.Length;
                StringBuilder result = new StringBuilder(len * 2);
                for (int i = 0; i < len; i++)
                {
                    if (i == (len - 1) && str.Substring(i) == "=")
                    {
                        result.Length--;
                        break;
                    }
                    else
                    {
                        int cInt15 = decodeCharTo15Bits(str.Substring(i,1));
                        result.Append((char)(cInt15 >> 7));
                        result.Append((char)(cInt15 & 0x7f));
                    }
                }
                return result.ToString();
            }

            //Cryptographic hash providers
            private static MD5CryptoServiceProvider md5util = new MD5CryptoServiceProvider();
            private static SHA1CryptoServiceProvider sha1util = new SHA1CryptoServiceProvider();
            private static SHA256Managed sha2util = new SHA256Managed();
            private static SHA384Managed sha384util = new SHA384Managed();
            private static SHA512Managed sha512util = new SHA512Managed();
            //Return a cryptographic hash
            public static string Hash(string str, string nonce, string inCodec, string outCodec)
            {
                if (!String.IsNullOrEmpty(nonce)) str = str + ":" + nonce;
                byte[] bytes = null;
                byte[] inBytes = Encoding.UTF8.GetBytes(str);
                switch (inCodec)
                {
                    case "md5":
                        lock(md5util)
                            bytes = md5util.ComputeHash(inBytes);
                        break;
                    case "sha1":
                    case "sha128":
                        lock (sha1util)
                            bytes = sha1util.ComputeHash(inBytes);
                        break;
                    case "sha2":
                    case "sha256":
                        lock (sha2util)
                            bytes = sha2util.ComputeHash(inBytes);
                        break;
                    case "sha384":
                        lock (sha384util)
                            bytes = sha384util.ComputeHash(inBytes);
                        break;
                    case "sha512":
                        lock (sha512util)
                            bytes = sha512util.ComputeHash(inBytes);
                        break;
                    default:
                        return String.Empty;
                }
                return Encode(bytes, outCodec);
            }
        }



        public string iwStringCodec(string str, string codec, int operation, LSL_List extraParams)
        {
            const int OP_DECODE = 0;
            const int OP_ENCODE = 1;
            const int OP_VALIDATE = 2;

                 if (str.Length >= 16000)this.ScriptSleep((str.Length / 100) * 20);
            else if (str.Length >=  8000)this.ScriptSleep((str.Length / 100) * 10);
            else if (str.Length >=  1000)this.ScriptSleep((str.Length / 100) *  5);

            int pLen = extraParams.Length;
            byte[] cBytes = null;
            bool useBytes = false;
            codec = codec.ToLower();
            for (int i = 0; i < pLen; i += 2)
            {
                string k = extraParams.GetLSLStringItem(i);
                string v = extraParams.GetLSLStringItem(i+1);
                k = k.ToLower(); v = v.ToLower();
                if (k == "input codec")
                {
                    if (CodecUtil.HasCodec(v) == false)
                    {
                        LSLError("Invalid input codec: " + v);
                        return String.Empty;
                    }
                    cBytes = CodecUtil.Decode(str, v.ToLower());
                    useBytes = true;
                }
            }

            switch (codec)
            {
                case "ascii":
                    if (String.IsNullOrEmpty(str)) return str;
                    if(operation == OP_ENCODE) {
                        return CodecUtil.StringToAscii(str);
                    } else if(operation == OP_DECODE) {
                        try {
                            return CodecUtil.AsciiToString(str);
                        } catch (Exception e) {
                            LSLError("Error in ascii decoding: " + e.Message);
                        }
                    } else if(operation == OP_VALIDATE) {
                        try {
                            CodecUtil.AsciiToString(str);
                            return "VALID";
                        }
                        catch (Exception) {
                            return "INVALID";
                        }
                    }
                    break;
                case "gzip":
                    if (String.IsNullOrEmpty(str)) return str;
                    string outputCodec = "base4096";
                    if (extraParams.Length >= 2 && extraParams.Length % 2 == 0)
                    {
                        int len = extraParams.Length;
                        for (int i = 0; i < len; i += 2)
                        {
                            if (extraParams.GetLSLStringItem(i).ToLower() == "output codec")
                            {
                                outputCodec = extraParams.GetLSLStringItem(i + 1);
                                break;
                            }
                        }
                    }
                    if (!CodecUtil.HasCodec(outputCodec))
                    {
                        LSLError("Bad codec for gzip compression: " + outputCodec);
                        return String.Empty;
                    }
                    if (operation == OP_ENCODE) return CodecUtil.gzipCompress(str, outputCodec);
                    else if (operation == OP_DECODE) return CodecUtil.gzipDecompress(str, outputCodec);
                    break;
                case "ascii-zip":
                    if (String.IsNullOrEmpty(str)) return str;
                    if (operation == OP_ENCODE) return CodecUtil.AsciiCompress(str);
                    else if (operation == OP_DECODE) return CodecUtil.AsciiDecompress(str);
                    break;
                case "md5":
                case "sha1":
                case "sha-1":
                case "sha128":
                case "sha-128":
                case "sha2":
                case "sha-2":
                case "sha256":
                case "sha-256":
                case "sha384":
                case "sha-384":
                case "sha512":
                case "sha-512":
                    string outCodec = "base16";
                    string nonce = String.Empty;
                    if (extraParams.Length >= 0 && extraParams.Length % 2 == 0)
                    {
                        int len = extraParams.Length;
                        for (int i = 0; i < len; i += 2)
                        {
                            string k = extraParams.GetLSLStringItem(i).ToLower();
                            string val = extraParams.GetLSLStringItem(i + 1);
                            switch (k)
                            {
                                case "output codec":
                                    outCodec = val;
                                    break;
                                case "nonce":
                                    nonce = val;
                                    break;
                                default:
                                    break;
                            }
                        }
                    }
                    if (!CodecUtil.HasCodec(outCodec))
                    {
                        LSLError(string.Format("Error: invalid codec for {0} hash: {1}", codec, outCodec));
                    }
                    return CodecUtil.Hash(str, nonce, codec, outCodec);
                case "aes-key":
                    if (String.IsNullOrEmpty(str))
                    {
                        LSLError(string.Format("Error: using a blank password to generate an AES encryption key is not allowed."));
                        return String.Empty;
                    }
                    byte[] _aesKeySalt = null;
                    string _aesKeyCodec = "base16";
                    int _aesKeyIter = 8;
                    if (extraParams.Length >= 0)
                    {
                        int len = extraParams.Length;
                        for (int i = 0; i < len; i += 2)
                        {
                            string k = extraParams.GetLSLStringItem(i).ToLower();
                            string val = extraParams.GetLSLStringItem(i + 1);
                            switch (k)
                            {
                                case "salt":
                                    if(String.IsNullOrEmpty(val)) {
                                        LSLError("Salt for AES encryption key cannot be blank.");
                                        return String.Empty;
                                    }
                                    _aesKeySalt = Encoding.UTF8.GetBytes(val);
                                    break;
                                case "output codec":
                                    if (CodecUtil.HasCodec(val) == false)
                                    {
                                        LSLError("Error: invalid codec for AES encryption key: " + val);
                                        return String.Empty;
                                    }
                                    _aesKeyCodec = val;
                                    break;
                                case "rounds":
                                    int iterTest = Convert.ToInt32(val);
                                    if (iterTest < 1 || iterTest > 1024) {
                                        LSLError("Rounds for AES encryption key cannot be more than 1024 or less than 1");
                                        return String.Empty;
                                    }
                                    _aesKeyIter = iterTest;
                                    break;
                                default:
                                    break;
                            }
                        }
                    }

                    using (var PB = new Rfc2898DeriveBytes(str, _aesKeySalt, _aesKeyIter))
                    {
                        string aesKeyNew = CodecUtil.Encode(PB.GetBytes(32), _aesKeyCodec);
                        this.ScriptSleep(Math.Max(100, _aesKeyIter));
                        return aesKeyNew;
                    }
                case "aes":
                    if (String.IsNullOrEmpty(str)) return str;
                    string aesCodec = "base4096";
                    string aesKeyCodec = "base16";
                    string aesKey = String.Empty;
                    string aesVector = String.Empty;
                    
                    if (extraParams.Length >= 0)
                    {
                        int len = extraParams.Length;
                        for (int i = 0; i < len; i+=2)
                        {
                            string k = extraParams.GetLSLStringItem(i).ToLower();
                            string val = extraParams.GetLSLStringItem(i+1);
                            switch (k)
                            {
                                case "key":
                                    aesKey = val;
                                    break;
                                case "vector":
                                    if (!String.IsNullOrEmpty(val)) aesVector = val.Replace("-",null);
                                    break;
                                case "key codec":
                                    aesKeyCodec = val;
                                    break;
                                case "output codec":
                                    aesCodec = val;
                                    break;
                                default:
                                    break;
                            }
                        }
                    }
                    if (String.IsNullOrEmpty(aesKey) || String.IsNullOrEmpty(aesVector) || String.IsNullOrEmpty(aesCodec))
                    {
                        LSLError("Error: some parameters for AES encryption are blank or missing!");
                        return String.Empty;
                    }
                    if (aesVector.Length != 32)
                    {
                        LSLError("AES vectors require a 32-character hexadecimal string.");
                        return String.Empty;
                    }
                    if (CodecUtil.HasCodec(aesCodec) == false)
                    {
                        LSLError("Error: invalid codec for AES encryption: " + aesCodec);
                        return String.Empty;
                    }
                    if(CodecUtil.HasCodec(aesKeyCodec) == false) {
                        LSLError("Error: invalid input codec for AES encryption key: " + aesKeyCodec);
                        return String.Empty;
                    }
                    byte[] aesKeyBytes = CodecUtil.Decode(aesKey, aesKeyCodec);

                    if (operation == OP_ENCODE)
                    {
                        if (!useBytes)
                        {
                            return CodecUtil.Encode(
                                CodecUtil.AesEncrypt(Encoding.Unicode.GetBytes(str), aesKeyBytes, aesVector),
                                aesCodec
                            );
                        }
                        else
                        {
                            //bytes = CodecUtil.AesEncrypt(cBytes, aesKeyBytes, aesVector);
                            return CodecUtil.Encode(
                                CodecUtil.AesEncrypt(cBytes, aesKeyBytes, aesVector),
                                aesCodec
                            );
                        }
                        //string ret = CodecUtil.Encode(bytes, aesCodec);

                    }
                    else if (operation == OP_DECODE)
                    {
                        if (!useBytes)
                        {
                            return CodecUtil.AesDecrypt(CodecUtil.Decode(str, aesCodec), aesKeyBytes, aesVector);
                        }
                        else
                        {
                            return CodecUtil.AesDecrypt(cBytes, aesKeyBytes, aesVector);
                        }
                        
                    }

                    break;
                case "":
                    LSLError("Error: No codec specified for iwStringCodec!");
                    break;
                default:
                    if (String.IsNullOrEmpty(str)) return str;
                    string ret = null;
                    if (operation == OP_ENCODE)
                    {
                        if (!useBytes) ret = CodecUtil.Encode(str, codec);
                        else ret = CodecUtil.Encode(cBytes, codec);
                    }
                    else if (operation == OP_DECODE) ret = CodecUtil.DecodeToString(str, codec);
                    else if (operation == OP_VALIDATE)
                    {
                        int v = CodecUtil.Validate(str, codec);
                        if (v == 1) return "PASS";
                        else if (v == 0) return "FAIL";
                        else return "INVALID CODEC";
                    }
                    if (ret == null)
                    {
                        LSLError("Error: \"X\" is not a valid codec for iwStringCodec!".Replace("X", codec));
                        return String.Empty;
                    }
                    return ret;
            }
            return String.Empty;
        }

        public LSL_List llCastRay(Vector3 start, Vector3 end, LSL_List options)
        {
            List<object> results = new List<object>();
            Vector3 dir = end - start;

            float dist = Vector3.Mag(dir);

            int count = 1;
            //bool detectPhantom = false;
            int dataFlags = 0;
            int rejectTypes = 0;

            for (int i = 0; i < options.Length; i += 2)
            {
                if (options.GetLSLIntegerItem(i) == ScriptBaseClass.RC_MAX_HITS)
                    count = options.GetLSLIntegerItem(i + 1);
                //else if (options.GetLSLIntegerItem(i) == ScriptBaseClass.RC_DETECT_PHANTOM)
                //    detectPhantom = (options.GetLSLIntegerItem(i + 1) > 0);
                else if (options.GetLSLIntegerItem(i) == ScriptBaseClass.RC_DATA_FLAGS)
                    dataFlags = options.GetLSLIntegerItem(i + 1);
                else if (options.GetLSLIntegerItem(i) == ScriptBaseClass.RC_REJECT_TYPES)
                    rejectTypes = options.GetLSLIntegerItem(i + 1);
            }

            if (count > 16)
                count = 16;
            else if (count <= 0)
            {
                LSLError("You must request at least one result from llCastRay.");
                return new LSL_List();
            }

            bool rejectTerrain = (rejectTypes & ScriptBaseClass.RC_REJECT_LAND) == ScriptBaseClass.RC_REJECT_LAND;
            bool rejectAgents = (rejectTypes & ScriptBaseClass.RC_REJECT_AGENTS) == ScriptBaseClass.RC_REJECT_AGENTS;
            bool rejectNonPhysical = (rejectTypes & ScriptBaseClass.RC_REJECT_NONPHYSICAL) == ScriptBaseClass.RC_REJECT_NONPHYSICAL;
            bool rejectPhysical = (rejectTypes & ScriptBaseClass.RC_REJECT_PHYSICAL) == ScriptBaseClass.RC_REJECT_PHYSICAL;

            List<ContactResult> contactResults = m_host.ParentGroup.Scene.PhysicsScene.RayCastWorld(start, dir, dist, count);

            contactResults.Sort(new ContactResultComparer());
            int values = 0;
            foreach (ContactResult result in contactResults)
            {
                if (result.CollisionActor != null && m_host.LocalId == result.CollisionActor.LocalID)
                    continue;

                //Check to see if this actor type is rejected by the filter
                if (rejectTerrain && result.CollisionActor == null)//If the actor is null, then it is the terrain
                    continue;
                if (rejectAgents && result.CollisionActor != null && result.CollisionActor.PhysicsActorType == ActorType.Agent)
                    continue;
                if ((rejectNonPhysical || rejectPhysical) && result.CollisionActor != null)
                {
                    if(result.CollisionActor.PhysicsActorType == ActorType.Prim)
                    {
                        if (rejectPhysical && result.CollisionActor.IsPhysical)
                            continue;

                        if (rejectNonPhysical && !result.CollisionActor.IsPhysical)
                            continue;
                    }
                }

                UUID itemID = UUID.Zero;
                int linkNum = 0;

                if (result.CollisionActor != null)//If the actor is null, then it is the terrain
                {
                    SceneObjectPart part = World.GetSceneObjectPart(result.CollisionActor.LocalID);
                    if (part != null)
                    {
                        if (part.ParentGroup == m_host.ParentGroup)
                            continue;

                        if ((dataFlags & ScriptBaseClass.RC_GET_ROOT_KEY) == ScriptBaseClass.RC_GET_ROOT_KEY)
                            itemID = part.ParentGroup.UUID;
                        else
                            itemID = part.UUID;

                        linkNum = part.LinkNum;
                    }
                    else
                    {
                        ScenePresence sp = World.GetScenePresence(result.CollisionActor.LocalID);
                        /// It it a boy? a girl?
                        if (sp != null)
                            itemID = sp.UUID;
                    }
                }

                results.Add(itemID.ToString());

                if ((dataFlags & ScriptBaseClass.RC_GET_LINK_NUM) == ScriptBaseClass.RC_GET_LINK_NUM)
                    results.Add((int)linkNum);

                results.Add(result.Position);

                if ((dataFlags & ScriptBaseClass.RC_GET_NORMAL) == ScriptBaseClass.RC_GET_NORMAL)
                    results.Add(result.Normal);

                values++;
                if (values >= count)
                    break;
            }

            results.Add((int)values);

            return new LSL_List(results);
        }

        public class ContactResultComparer : Comparer<ContactResult>
        {
            public override int Compare(ContactResult x, ContactResult y)
            {
                return x.Distance.CompareTo(y.Distance);
            }
        }



        public void llSetKeyframedMotion(LSL_List keyframes, LSL_List options)
        {
            if (!m_host.IsRootPart())
            {
                ShoutError("Must be used in the root object!");
                return;
            }

            if(keyframes.Length == 0 &&
                options.Length == 0)
            {
                //Basically, we just need to stop the motion and clear the animation
                m_host.ParentGroup.RootPart.KeyframeAnimation = null;
                m_host.ParentGroup.AddKeyframedMotion(null, KeyframeAnimation.Commands.Stop);
                return;
            }

            KeyframeAnimation.Data dataType = KeyframeAnimation.Data.Both;
            KeyframeAnimation.Modes currentMode = KeyframeAnimation.Modes.Forward;
            for (int i = 0; i < options.Length; i += 2)
            {
                int option = options.GetLSLIntegerItem(i);
                int value = options.GetLSLIntegerItem(i + 1);
                if (option == ScriptBaseClass.KFM_COMMAND)
                {
                    m_host.ParentGroup.AddKeyframedMotion(null, (KeyframeAnimation.Commands)value);
                    return; //Its supposed to be the only option in the list
                }
                if (option == ScriptBaseClass.KFM_MODE)
                {
                    currentMode = (KeyframeAnimation.Modes)value;
                }
                else if (option == ScriptBaseClass.KFM_DATA)
                {
                    dataType = (KeyframeAnimation.Data)value;
                }
            }

            List<Vector3> positions = new List<Vector3>();
            List<Quaternion> rotations = new List<Quaternion>();
            List<TimeSpan> times = new List<TimeSpan>();
            for (int i = 0; i < keyframes.Length; i += (dataType == KeyframeAnimation.Data.Both ? 3 : 2))
            {
                if (dataType == KeyframeAnimation.Data.Both ||
                    dataType == KeyframeAnimation.Data.Translation)
                {
                    positions.Add(keyframes.GetVector3Item(i));
                }
                if (dataType == KeyframeAnimation.Data.Both ||
                    dataType == KeyframeAnimation.Data.Rotation)
                {
                    LSL_Rotation rot = keyframes.GetQuaternionItem(i + (dataType == KeyframeAnimation.Data.Both ? 1 : 0));
                    rot.Normalize();
                    rotations.Add(rot);
                }
                float time = keyframes.GetLSLFloatItem(i + (dataType == KeyframeAnimation.Data.Both ? 2 : 1));
                times.Add(TimeSpan.FromSeconds(time));
            }
            KeyframeAnimation animation = new KeyframeAnimation()
            {
                CurrentMode = currentMode,
                PositionList = positions.Count == 0 ? null : positions.ToArray(),
                RotationList = rotations.Count == 0 ? null : rotations.ToArray(),
                TimeList = times.ToArray(),
                CurrentAnimationPosition = currentMode == KeyframeAnimation.Modes.Reverse ? times.Count - 1 : 0,
                InitialPosition = m_host.AbsolutePosition,
                InitialRotation = m_host.RotationOffset
            };
            m_host.ParentGroup.AddKeyframedMotion(animation, KeyframeAnimation.Commands.Play);
        }

        private bool ScriptOwnerIsCreator()
        {
            lock (m_host.TaskInventory)
            {
                foreach (KeyValuePair<UUID, TaskInventoryItem> inv in m_host.TaskInventory)
                {
                    TaskInventoryItem item = inv.Value;
                    if (item.Type == 10 && item.ItemID == m_itemID)
                    {
                        return item.CreatorID == item.OwnerID;
                    }
                }
            }

            // should never get here, a script should always be able to find itself
            return false;
        }

        // iwGroupInvite(key group, key user, string role)
        public int iwGroupInvite(string group, string user, string roleName)
        {
            UUID roleID = UUID.Zero;
            UUID groupID = UUID.Zero;
            if ((!UUID.TryParse(group, out groupID)) || (groupID == UUID.Zero))
                return (int)Constants.GenericReturnCodes.PARAMETER;
            UUID userID = UUID.Zero;
            if ((!UUID.TryParse(user, out userID)) || (userID == UUID.Zero))
                return (int)Constants.GenericReturnCodes.PARAMETER;
            if (String.IsNullOrEmpty(roleName))
                roleName = "Everyone";

            if (!ScriptOwnerIsCreator())
            {
                LSLError("iwGroupInvite requires the owner of the calling script to be the creator of the script.");
                return (int)Constants.GenericReturnCodes.PERMISSION;
            }

            IGroupsModule groupsModule = m_ScriptEngine.World.RequestModuleInterface<IGroupsModule>();
            List<GroupRolesData> roles = groupsModule.GroupRoleDataRequest(null, groupID);
            if (roles == null) return (int)Constants.GenericReturnCodes.PARAMETER;      // groupID bad, or internal/system error (should not happen)

            int roleIndex = roles.FindIndex(role => role.Name.Equals(roleName, StringComparison.InvariantCultureIgnoreCase));
            if (roleIndex == -1) return (int)Constants.GenericReturnCodes.PARAMETER;   // unknown role

            return groupsModule.InviteGroupRequest(null, m_host.ObjectOwner, m_host.Name, groupID, userID, roles[roleIndex].RoleID);
        }

        // iwGroupEject(key group, key user)
        public int iwGroupEject(string group, string user)
        {
            UUID groupID = UUID.Zero;
            if ((!UUID.TryParse(group, out groupID)) || (groupID == UUID.Zero))
                return (int)Constants.GenericReturnCodes.PARAMETER;
            UUID userID = UUID.Zero;
            if ((!UUID.TryParse(user, out userID)) || (userID == UUID.Zero))
                return (int)Constants.GenericReturnCodes.PARAMETER;

            if (!ScriptOwnerIsCreator())
            {
                LSLError("iwGroupEject requires the owner of the calling script to be the creator of the script.");
                return (int)Constants.GenericReturnCodes.PERMISSION;
            }

            IGroupsModule groupsModule = m_ScriptEngine.World.RequestModuleInterface<IGroupsModule>();
            return groupsModule.EjectGroupMemberRequest(null, m_host.OwnerID, m_host.Name, groupID, userID);
        }

        #region Bot Management Methods

        public void botCreateBot(string FirstName, string LastName, string outfitName, Vector3 startPos, int options)
        {
            const int delay = 2000;
            object retVal = UUID.Zero.ToString();

            try
            {
                IBotManager manager = World.RequestModuleInterface<IBotManager>();
                if (manager != null)
                {
                    string reason;
                    string botID = manager.CreateBot(FirstName, LastName, startPos, outfitName, m_itemID, m_host.OwnerID, out reason).ToString();
                    if (reason == null)
                    {
                        retVal = botID;
                        return;
                    }
                    ScriptShoutError(reason);
                }
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, retVal, delay);
            }
        }

        public void botRemoveBot(string botID)
        {
            const int delay = 1000;

            try
            {
                UUID botUUID = UUID.Zero;
                if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                    return;

                IBotManager manager = World.RequestModuleInterface<IBotManager>();
                if (manager != null)
                    manager.RemoveBot(botUUID, m_host.OwnerID);
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }

        public string botGetOwner(string botID)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return String.Empty;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                return manager.GetBotOwner(botUUID).ToString();
            return String.Empty;
        }

        public int botIsBot(string userID)
        {
            UUID userUUID = UUID.Zero;
            if (!UUID.TryParse(userID, out userUUID) || (userUUID == UUID.Zero))
                return 0;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                return manager.IsBot(userUUID) ? 1 : 0;
            return 0;
        }

        public string botGetName(string botID)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return String.Empty;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                return manager.GetBotName(botUUID);
            return String.Empty;
        }

        public void botChangeOwner(string botID, string newOwnerID)
        {
            NotImplemented("botChangeOwner");
            /*UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.ChangeBotOwner(botUUID, UUID.Parse(newOwnerID), m_host.OwnerID);*/
        }

        public LSL_List botGetAllBotsInRegion()
        {
            List<UUID> bots = new List<UUID>();
            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                bots = manager.GetAllBots();
            return new LSL_List(bots.ConvertAll<object>((o) => o.ToString()));
        }

        public LSL_List botGetAllMyBotsInRegion()
        {
            List<UUID> bots = new List<UUID>();
            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                bots = manager.GetAllOwnedBots(m_host.OwnerID);
            return new LSL_List(bots.ConvertAll<object>((o) => o.ToString()));
        }

        public void botSetProfile(string botID, string aboutText, string email, string firstLifeAboutText,
            string firstLifeImageUUID, string imageUUID, string profileURL)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            UUID imageID = UUID.Zero;
            UUID.TryParse(imageUUID, out imageID);

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.SetBotProfile(botUUID, aboutText, email, imageID, profileURL, m_host.OwnerID);
        }

        public void botSetProfileParams(string botID, LSL_List profileInformation)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            string aboutText = null, email = null, profileURL = null;
            UUID? imageUUID = null;

            for(int i = 0; i < profileInformation.Length; i += 2)
            {
                int param = profileInformation.GetLSLIntegerItem(i);
                string value = profileInformation.GetLSLStringItem(i + 1);

                switch(param)
                {
                    case ScriptBaseClass.BOT_ABOUT_TEXT:
                        aboutText = value;
                        break;
                    case ScriptBaseClass.BOT_EMAIL:
                        email = value;
                        break;
                    case ScriptBaseClass.BOT_IMAGE_UUID:
                        UUID id;
                        if (UUID.TryParse(value, out id))
                            imageUUID = id;
                        break;
                    case ScriptBaseClass.BOT_PROFILE_URL:
                        profileURL = value;
                        break;
                }
            }

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.SetBotProfile(botUUID, aboutText, email, imageUUID, profileURL, m_host.OwnerID);
        }

        public LSL_List botGetProfileParams(string botID, LSL_List profileInformation)
        {
            if (botIsBot(botID) == 0)   // do not allow this to work on regular user accounts
                return new LSL_List();
            
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return new LSL_List();

            UserProfileData profile = World.CommsManager.UserService.GetUserProfile(botUUID);
            if (profile == null)
                return new LSL_List();

            List<object> list = new List<object>();
            for (int i = 0; i < profileInformation.Length; i++)
            {
                int param = profileInformation.GetLSLIntegerItem(i);

                switch (param)
                {
                    case ScriptBaseClass.BOT_ABOUT_TEXT:
                        list.Add((string)profile.AboutText);
                        break;
                    case ScriptBaseClass.BOT_EMAIL:
                        list.Add((string)profile.Email);
                        break;
                    case ScriptBaseClass.BOT_IMAGE_UUID:
                        list.Add((string)profile.Image.ToString());
                        break;
                    case ScriptBaseClass.BOT_PROFILE_URL:
                        list.Add((string)profile.ProfileURL);
                        break;
                }
            }

            return new LSL_List(list);
        }

        #endregion

        #region Bot Appearance Methods

        public void botSetOutfit(string outfitName)
        {
            const int delay = 1000;

            try
            {
                IBotManager manager = World.RequestModuleInterface<IBotManager>();
                if (manager != null)
                {
                    string reason;
                    manager.SaveOutfitToDatabase(m_host.OwnerID, outfitName, out reason);
                    if (reason != null)
                        ScriptShoutError(reason);
                }
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }

        public void botRemoveOutfit(string outfitName)
        {
            const int delay = 1000;

            try
            {
                IBotManager manager = World.RequestModuleInterface<IBotManager>();
                if (manager != null)
                    manager.RemoveOutfitFromDatabase(m_host.OwnerID, outfitName);
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }

        public void botChangeOutfit(string botID, string outfitName)
        {
            const int delay = 1000;

            try
            {
                UUID botUUID = UUID.Zero;
                if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                    return;

                IBotManager manager = World.RequestModuleInterface<IBotManager>();
                if (manager != null)
                {
                    string reason;
                    manager.ChangeBotOutfit(botUUID, outfitName, m_host.OwnerID, out reason);
                    if (reason != null)
                        ScriptShoutError(reason);
                }
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }

        public void botSearchBotOutfits(string pattern, int matchType, int start, int end)
        {
            List<object> retVal = new List<object>();
            const int delay = 1000;

            try
            {
                
                if(matchType > 2)
                {
                    if (matchType == 3) LSLError("IW_MATCH_COUNT is not a valid matching type for botSearchBotOutfits");
                    else if (matchType == 4) LSLError("IW_MATCH_COUNT_REGEX is not a valid matching type for botSearchBotOutfits");
                }
                else 
                {
                    IBotManager manager = World.RequestModuleInterface<IBotManager>();
                    if (manager != null)
                    {
                        List<string> itms = manager.GetBotOutfitsByOwner(m_host.OwnerID);
                        int count=0;
                        foreach(string outfit in itms)
                        {
                            if(String.IsNullOrEmpty(pattern) || iwMatchString(outfit, pattern, matchType) == 1)
                            {
                                if (count >= start && (end == -1 || count <= end))
                                {
                                    retVal.Add(outfit);
                                }
                                count++;
                                if (end != -1 && count > end)
                                    break;
                            }
                        }
                    }
                }
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, new LSL_List(retVal), delay);
            }
        }

        public void botGetBotOutfits()
        {
            object retVal = new LSL_List();

            const int delay = 1000;

            try
            {
                IBotManager manager = World.RequestModuleInterface<IBotManager>();
                if (manager != null)
                {
                    List<string> itms = manager.GetBotOutfitsByOwner(m_host.OwnerID);
                    retVal = new LSL_List(itms.ConvertAll<object>((s) => s));
                }
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, retVal, delay);
            }
        }

        #endregion

        #region Bot Event Registration Methods

        public void botRegisterForNavigationEvents(string botID)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.BotRegisterForPathUpdateEvents(botUUID, m_itemID, m_host.OwnerID);
        }

        public void botDeregisterFromNavigationEvents(string botID)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.BotDeregisterFromPathUpdateEvents(botUUID, m_itemID, m_host.OwnerID);
        }

        public void botRegisterForCollisionEvents(string botID)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.BotRegisterForCollisionEvents(botUUID, m_host.ParentGroup, m_host.OwnerID);
        }

        public void botDeregisterFromCollisionEvents(string botID)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.BotDeregisterFromCollisionEvents(botUUID, m_host.ParentGroup, m_host.OwnerID);
        }

        #endregion

        #region Bot Movement Methods

        public void botPauseMovement(string botID)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.PauseBotMovement(botUUID, m_host.OwnerID);
        }

        public void botResumeMovement(string botID)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.ResumeBotMovement(botUUID, m_host.OwnerID);
        }

        public void botSetMovementSpeed(string botID, float speed)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.SetBotSpeed(botUUID, speed, m_host.OwnerID);
        }

        public Vector3 botGetPos(string botID)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return Vector3.Zero;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                return manager.GetBotPosition(botUUID, m_host.OwnerID);

            return Vector3.Zero;
        }

        public void botTeleportTo(string botID, Vector3 position)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.SetBotPosition(botUUID, position, m_host.OwnerID);
        }

        public void botSetRotation(string botID, Quaternion rotation)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.SetBotRotation(botUUID, rotation, m_host.OwnerID);
        }

        private int CheckBotNavOptions(Dictionary<int, object> dictOptions, string context, int option)
        {
            // The caller uses a Dictionary so avoid exception adding the same key ...
            if (dictOptions.ContainsKey(option))
            {
                // This indicates a bug in the script, the parameters are ambiguous and/or redundant.
                ScriptShoutError(String.Format("{0}: options list already includes option {1}", context, option));
                // Replacing the matching option that is already there seems better than aborting the operation.
                dictOptions.Remove(option); 
            }
            return option;
        }

        public int botFollowAvatar(string botID, string avatar, LSL_List options)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return ScriptBaseClass.BOT_ERROR;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
            {
                UUID userID;
                if (!UUID.TryParse(avatar, out userID))
                {
                    if (World.CommsManager.UserService.Name2Key(avatar) == UUID.Zero)
                        return ScriptBaseClass.BOT_USER_NOT_FOUND;
                }

                if (options.Length % 2 != 0)
                    return ScriptBaseClass.BOT_ERROR;//bad data

                Dictionary<int, object> dictOptions = new Dictionary<int, object>();
                for (int i = 0; i < options.Length; i += 2)
                {
                    if (options.GetItemType(i) != Types.VarType.Integer)
                        return ScriptBaseClass.BOT_ERROR;//Bad value
                    if (options.GetItemType(i + 1) != Types.VarType.Integer &&
                        options.GetItemType(i + 1) != Types.VarType.Float &&
                        options.GetItemType(i + 1) != Types.VarType.Vector)
                        return ScriptBaseClass.BOT_ERROR;//bad

                    // This uses a Dictionary so avoid exception adding the same key ...
                    int option = CheckBotNavOptions(dictOptions, "botFollowAvatar", options.GetLSLIntegerItem(i));
                    dictOptions.Add(option, options.Data[i + 1]);
                }
                BotMovementResult result = manager.StartFollowingAvatar(botUUID, userID, dictOptions, m_host.OwnerID);

                switch (result)
                {
                    case BotMovementResult.BotNotFound:
                        return ScriptBaseClass.BOT_NOT_FOUND;
                    case BotMovementResult.UserNotFound:
                        return ScriptBaseClass.BOT_USER_NOT_FOUND;
                    case BotMovementResult.Success:
                        return ScriptBaseClass.BOT_SUCCESS;
                    default:
                        return ScriptBaseClass.BOT_ERROR;
                }
            }
            return ScriptBaseClass.BOT_NOT_FOUND;
        }

        public void botStopMovement(string botID)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.StopMovement(botUUID, m_host.OwnerID);
        }

        public void botSetNavigationPoints(string botID, LSL_List positions, LSL_List movementType, LSL_List options)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            List<Vector3> PositionsMap = new List<Vector3>();
            for (int i = 0; i < positions.Length; i++)
            {
                LSL_Vector pos;
                if (positions.GetItemType(i) == Types.VarType.Vector)
                    pos = positions.GetVector3Item(i);
                else if(positions.GetItemType(i) == Types.VarType.Float ||
                    positions.GetItemType(i) == Types.VarType.Integer)
                    pos = new LSL_Vector(positions.GetLSLFloatItem(i), 0, 0);
                else
                {
                    return;//Bad value
                }
                Util.ForceValidRegionXYZ(ref pos);
                float zmin = (float)World.Heightmap.CalculateHeightAt(pos.X, pos.Y);
                if (pos.Z < zmin)
                    pos.Z = zmin;                
                PositionsMap.Add(pos);
            }

            List<TravelMode> TravelMap = new List<TravelMode>();
            for (int i = 0; i < movementType.Length; i++)
            {
                if (movementType.GetItemType(i) != Types.VarType.Integer)
                    return;//Bad value
                int travel = movementType.GetLSLIntegerItem(i);
                TravelMap.Add((TravelMode)travel);
            }

            if (options.Length % 2 != 0)
                return;//bad data

            Dictionary<int, object> dictOptions = new Dictionary<int, object>();
            for (int i = 0; i < options.Length; i += 2)
            {
                if (options.GetItemType(i) != Types.VarType.Integer)
                    return;//Bad value
                if (options.GetItemType(i + 1) != Types.VarType.Integer &&
                    options.GetItemType(i + 1) != Types.VarType.Float)
                    return;//bad

                // This uses a Dictionary so avoid exception adding the same key ...
                int option = CheckBotNavOptions(dictOptions, "botSetNavigationPoints", options.GetLSLIntegerItem(i));
                dictOptions.Add(option, options.Data[i + 1]);
            }

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.SetBotNavigationPoints(botUUID, PositionsMap, TravelMap, dictOptions, m_host.OwnerID);
        }

        public void botWanderWithin(string botID, Vector3 origin, float xDistance, float yDistance, LSL_List options)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            if (options.Length % 2 != 0)
                return;//bad data

            Dictionary<int, object> dictOptions = new Dictionary<int, object>();
            for (int i = 0; i < options.Length; i += 2)
            {
                if (options.GetItemType(i) != Types.VarType.Integer)
                    return;//bad
                if (options.GetItemType(i + 1) != Types.VarType.Integer &&
                    options.GetItemType(i + 1) != Types.VarType.Float)
                    return;//bad

                // This uses a Dictionary so avoid exception adding the same key ...
                int option = CheckBotNavOptions(dictOptions, "botWanderWithin", options.GetLSLIntegerItem(i));
                dictOptions.Add(option, options.Data[i + 1]);
            }

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.WanderWithin(botUUID, origin, new Vector3(xDistance, yDistance, 0), dictOptions, m_host.OwnerID);
        }

        #endregion

        #region Bot Animations

        public void botStartAnimation(string botID, string animation)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
            {
                UUID animID = InventoryKey(animation, (int)AssetType.Animation);
                manager.StartBotAnimation(botUUID, animID, animation, m_host.UUID, m_host.OwnerID);
            }
        }

        public void botStopAnimation(string botID, string animation)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
            {
                UUID animID;
                if (!UUID.TryParse(animation, out animID))
                    animID = InventoryKey(animation, (int)AssetType.Animation);

                manager.StopBotAnimation(botUUID, animID, animation, m_host.OwnerID);
            }
        }

        #endregion

        #region Chat Methods

        public void botWhisper(string botID, int channel, string message)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.BotChat(botUUID, channel, message, ChatTypeEnum.Whisper, m_host.OwnerID);
            ScriptSleep(15);
        }

        public void botSay(string botID, int channel, string message)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.BotChat(botUUID, channel, message, ChatTypeEnum.Say, m_host.OwnerID);
            ScriptSleep(15);
        }

        public void botShout(string botID, int channel, string message)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.BotChat(botUUID, channel, message, ChatTypeEnum.Shout, m_host.OwnerID);
            ScriptSleep(15);
        }

        public void botStartTyping(string botID)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.BotChat(botUUID, 0, String.Empty, ChatTypeEnum.StartTyping, m_host.OwnerID);
            ScriptSleep(15);
        }

        public void botStopTyping(string botID)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.BotChat(botUUID, 0, String.Empty, ChatTypeEnum.StopTyping, m_host.OwnerID);
            ScriptSleep(15);
        }

        public void botSendInstantMessage(string botID, string userID, string message)
        {
            const int delay = 2000;

            try
            {
                UUID botUUID = UUID.Zero;
                if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                    return;

                UUID userUUID = UUID.Zero;
                if (!UUID.TryParse(userID, out userUUID) || (userUUID == UUID.Zero))
                    return;

                IBotManager manager = World.RequestModuleInterface<IBotManager>();
                if (manager != null)
                    manager.SendInstantMessageForBot(botUUID, userUUID, message, m_host.OwnerID);
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }

        #endregion

        #region Region Interaction

        public void botSitObject(string botID, string objectID)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            UUID objectUUID = UUID.Zero;
            if (!UUID.TryParse(objectID, out objectUUID) || (objectUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.SitBotOnObject(botUUID, objectUUID, m_host.OwnerID);
            ScriptSleep(15);
        }

        public void botStandUp(string botID)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.StandBotUp(botUUID, m_host.OwnerID);
            ScriptSleep(15);
        }

        public void botTouchObject(string botID, string objectID)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            UUID objectUUID = UUID.Zero;
            if (!UUID.TryParse(objectID, out objectUUID) || (objectUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.BotTouchObject(botUUID, objectUUID, m_host.OwnerID);
            ScriptSleep(15);
        }

        public void botGiveInventory(string botID, string destination, string inventory)
        {
            int delay = 0;

            try
            {
                UUID botUUID = UUID.Zero;
                if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                    return;

                bool found = false;
                UUID destId = UUID.Zero;
                UUID objId = UUID.Zero;
                byte assetType = 0;
                string objName = String.Empty;

                if (!UUID.TryParse(destination, out destId))
                {
                    llSay(0, "Could not parse key " + destination);
                    return;
                }

                // move the first object found with this inventory name
                lock (m_host.TaskInventory)
                {
                    foreach (KeyValuePair<UUID, TaskInventoryItem> inv in m_host.TaskInventory)
                    {
                        if (inv.Value.Name == inventory)
                        {
                            found = true;
                            objId = inv.Key;
                            assetType = (byte)inv.Value.Type;
                            objName = inv.Value.Name;
                            break;
                        }
                    }
                }

                if (!found)
                {
                    ScriptShoutError(String.Format("Could not find object '{0}'", inventory));
                    return;
                }
    
                IBotManager manager = World.RequestModuleInterface<IBotManager>();
                if (manager != null)
                    manager.GiveInventoryObject(botUUID, m_host, objName, objId, assetType, destId, m_host.OwnerID);

                delay = 2000;
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }

        public void botSensor(string botID, string name, string id, int type, float range, float arc)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            UUID keyID = UUID.Zero;
            UUID.TryParse(id, out keyID);
            
            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager == null || !manager.CheckPermission(botUUID, m_host.OwnerID))
                return;

            ScenePresence botSP = World.GetScenePresence(botUUID);
            if (botSP == null)
                return;

            AsyncCommands.SensorRepeatPlugin.SenseOnce(m_localID, m_itemID, name, keyID, type, range, arc, botSP);
        }

        public void botSensorRepeat(string botID, string name, string id, int type, float range, float arc, float rate)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            UUID keyID = UUID.Zero;
            UUID.TryParse(id, out keyID);

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager == null || !manager.CheckPermission(botUUID, m_host.OwnerID))
                return;

            ScenePresence botSP = World.GetScenePresence(botUUID);
            if (botSP == null)
                return;

            AsyncCommands.SensorRepeatPlugin.SetSenseRepeatEvent(m_localID, m_itemID, name, keyID, type, range, arc, rate, botSP);
        }

        public void botSensorRemove()
        {
            AsyncCommands.SensorRepeatPlugin.UnSetSenseRepeaterEvents(m_localID, m_itemID);
        }

        public string iwDetectedBot()
        {
            VM.DetectVariables detectedParams = _thisScript.ScriptState.GetDetectVariables(0);
            if (detectedParams == null)
                return String.Empty;
            return detectedParams.BotID;
        }

        public int botListen(string botID, int channelID, string name, string ID, string msg)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return -1;

            UUID keyID;
            UUID.TryParse(ID, out keyID);

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager == null || !manager.CheckPermission(botUUID, m_host.OwnerID))
                return -1;

            ScenePresence botSP = World.GetScenePresence(botUUID);
            if (botSP == null)
                return -1;

            IWorldComm wComm = m_ScriptEngine.World.RequestModuleInterface<IWorldComm>();
            return wComm.Listen(m_localID, m_itemID, botSP.UUID, channelID, name, keyID, msg);
        }

        public void botMessageLinked(string botID, int num, string msg, string id)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager == null || !manager.CheckPermission(botUUID, m_host.OwnerID))
                return;

            ScenePresence botSP = World.GetScenePresence(botUUID);
            if (botSP == null)
                return;

            int DELAY = 0;

            SceneObjectGroup[] groups = botSP.GetAttachments().ToArray();

            UUID partItemID;
            foreach (SceneObjectGroup group in groups)
            {
                foreach (SceneObjectPart part in group.GetParts())
                {
                    TaskInventoryDictionary itemsDictionary = (TaskInventoryDictionary)part.TaskInventory.Clone();

                    foreach (TaskInventoryItem item in itemsDictionary.Values)
                    {
                        if (item.Type == ScriptBaseClass.INVENTORY_SCRIPT)
                        {
                            partItemID = item.ItemID;
                            int linkNumber = m_host.LinkNum;
                            if (m_host.ParentGroup.LinkCount == 1)
                                linkNumber = 0;

                            object[] resobj = new object[] { linkNumber, num, msg, id };

                            m_ScriptEngine.PostScriptEvent(partItemID,
                                    new EventParams("link_message",
                                    resobj, new DetectParams[0]));

                            //now, determine a variable delay based on the current size of the script queue
                            const float LOW_SPACE_THRESHOLD = 0.2f;
                            const int LOW_SPACE_DELAY = 50;
                            if (m_ScriptEngine.GetEventQueueFreeSpacePercentage(partItemID) <= LOW_SPACE_THRESHOLD)
                            {
                                DELAY = LOW_SPACE_DELAY;
                            }
                        }
                    }
                }
            }

            ScriptSleep(DELAY);
        }

        #endregion

        #region Bot Tagging

        public void botAddTag(string botID, string tag)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.AddTagToBot(botUUID, tag, m_host.OwnerID);
        }

        public void botRemoveTag(string botID, string tag)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                manager.RemoveTagFromBot(botUUID, tag, m_host.OwnerID);
        }

        public int botHasTag(string botID, string tag)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return 0;

            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                return manager.BotHasTag(botUUID, tag) ? 1 : 0;
            return 0;
        }

        public LSL_List botGetBotTags(string botID)
        {
            UUID botUUID = UUID.Zero;
            if (!UUID.TryParse(botID, out botUUID) || (botUUID == UUID.Zero))
                return new LSL_List();


            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            if (manager != null)
                return new LSL_List(manager.GetBotTags(botUUID).ToList<object>());
            return new LSL_List();
        }

        public LSL_List botGetBotsWithTag(string tag)
        {
            IBotManager manager = World.RequestModuleInterface<IBotManager>();
            List<UUID> bots = new List<UUID>();
            if (manager != null)
                bots = manager.GetBotsWithTag(tag);
            List<Object> botList = new List<object>();
            foreach (UUID bot in bots)
                botList.Add(bot.ToString());

            return new LSL_List(botList);
        }

        public void botRemoveBotsWithTag(string tag)
        {
            const int delay = 1000;

            try
            {
                IBotManager manager = World.RequestModuleInterface<IBotManager>();
                if (manager != null)
                    manager.RemoveBotsWithTag(tag, m_host.OwnerID);
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }
        #endregion

        public LSL_List iwSearchLinksByName(string pattern, int matchType, int linksOnly)
        {
            if(matchType > 2)
            {
                if (matchType == 3) LSLError("IW_MATCH_COUNT is not a valid matching type for iwSearchLinksByName()");
                else if (matchType == 4) LSLError("IW_MATCH_COUNT_REGEX is not a valid matching type for iwSearchLinksByName()");
                return new LSL_List();
            }
            List<object> ret = new List<object>();
            List<SceneObjectPart> parts = new List<SceneObjectPart>();

            foreach(SceneObjectPart part in m_host.ParentGroup.GetParts())
            {
                if (String.IsNullOrEmpty(pattern) || iwMatchString(part.Name, pattern, matchType) == 1)
                    parts.Add(part);
            }

            if(parts.Count > 0)
            {
                parts.Sort((x, y) => x.LinkNum.CompareTo(y.LinkNum));
                foreach(SceneObjectPart part in parts)
                {
                    ret.Add(part.LinkNum);
                    if (linksOnly == 0)
                        ret.Add(part.Name);
                }
            }

            return new LSL_List(ret);
        }

        public LSL_List iwSearchLinksByDesc(string pattern, int matchType, int linksOnly)
        {
            if(matchType > 2)
            {
                if (matchType == 3) LSLError("IW_MATCH_COUNT is not a valid matching type for iwSearchLinksByDesc()");
                else if (matchType == 4) LSLError("IW_MATCH_COUNT_REGEX is not a valid matching type for iwSearchLinksByDesc()");
                return new LSL_List();
            }
            
            List<object> ret = new List<object>();
            List<SceneObjectPart> parts = new List<SceneObjectPart>();

            foreach (SceneObjectPart part in m_host.ParentGroup.GetParts())
            {
                if (String.IsNullOrEmpty(pattern) || iwMatchString(part.Description, pattern, matchType) == 1)
                    parts.Add(part);
            }

            if (parts.Count > 0)
            {
                parts.Sort((x, y) => x.LinkNum.CompareTo(y.LinkNum));
                foreach (SceneObjectPart part in parts)
                {
                    ret.Add(part.LinkNum);
                    if (linksOnly == 0)
                        ret.Add(part.Description);
                }
            }

            return new LSL_List(ret);
        }

        public LSL_List llGetAttachedList(string avatar)
        {
            LSL_List ret = new LSL_List();

            UUID agentID;
            if (UUID.TryParse(avatar, out agentID))
            {
                ScenePresence sp = World.GetScenePresence(agentID);
                if ((sp != null) && (!sp.IsChildAgent))
                {
                    foreach (UUID id in sp.CollectVisibleAttachmentIds())
                        ret = ret.Append(id.ToString());
                    return ret;
                }
            }

            ret = ret.Append("NOT FOUND");
            return ret;
        }

        public int llReturnObjectsByOwner(string owner, int scope)
        {
            UUID targetAgentID;
            if (!UUID.TryParse(owner, out targetAgentID))
                return ScriptBaseClass.ERR_MALFORMED_PARAMS;

            if (targetAgentID == UUID.Zero)
                return 0;

            UUID invItemID = InventorySelf();
            if (invItemID == UUID.Zero)
            {
                LSLError("No item found from which to run script");
                return ScriptBaseClass.ERR_GENERIC;
            }

            // After this, set rc for error code.
            int rc = 0;

            try {

                TaskInventoryItem item;
                lock (m_host.TaskInventory) {
                    item = m_host.TaskInventory[invItemID];
                }

                // First, just check if anyone has ERR_RUNTIME_PERMISSIONS ...
                if (!CheckRuntimePerms(item, item.PermsGranter, ScriptBaseClass.PERMISSION_RETURN_OBJECTS)) {
                    rc = ScriptBaseClass.ERR_RUNTIME_PERMISSIONS;
                    return rc;
                }

                // We need the land parcel for everything after this.
                Vector3 currentPos = m_host.ParentGroup.AbsolutePosition;
                ILandObject currentParcel = World.LandChannel.GetLandObject(currentPos.X, currentPos.Y);
                if ((currentParcel == null) && (scope == ScriptBaseClass.OBJECT_RETURN_REGION))
                {
                    rc = ScriptBaseClass.ERR_GENERIC;
                    return rc;
                }

                LandData patternParcel = null;
                bool sameOwner;
                switch (scope) {
                    case ScriptBaseClass.OBJECT_RETURN_PARCEL:
                        patternParcel = currentParcel.landData;
                        sameOwner = false;
                        break;
                    case ScriptBaseClass.OBJECT_RETURN_PARCEL_OWNER:
                        patternParcel = currentParcel.landData;
                        sameOwner = true;
                        break;
                    case ScriptBaseClass.OBJECT_RETURN_REGION:
                        patternParcel = null; // wildcard for all parcels
                        sameOwner = false;
                        break;
                    default:
                        return ScriptBaseClass.ERR_MALFORMED_PARAMS;
                }

                rc = World.LandChannel.ScriptedReturnObjectsInParcelByOwner(item, targetAgentID, patternParcel, sameOwner);
                if (rc > 0)
                    m_log.InfoFormat("[LAND]: Scripted object return of {0} objects owned by {1} by {2} for {3}", rc, targetAgentID, item.PermsGranter, item.OwnerID);
            }
            catch (Exception e) {
                return ScriptBaseClass.ERR_GENERIC;
            }
            finally
            {
                switch (rc) {
                    case ScriptBaseClass.ERR_GENERIC:
                        LSLError("No parcel found for permissions to return objects");
                        break;
                    case ScriptBaseClass.ERR_PARCEL_PERMISSIONS:
                        LSLError("No parcel/region permission to return objects");
                        break;
                    case ScriptBaseClass.ERR_RUNTIME_PERMISSIONS:
                        LSLError("No permissions to return objects");
                        break;
                    case ScriptBaseClass.ERR_MALFORMED_PARAMS:
                        LSLError("Bad parameters on scripted call to return objects");
                        break;
                    default:
                        // do nothing for other 0, other errors, or >0 counts
                        break;
                }
            }
            return rc;
        }

        public int llReturnObjectsByID(LSL_List objects)
        {
            try
            {
                UUID invItemID = InventorySelf();
                if (invItemID == UUID.Zero)
                {
                    LSLError("No item found from which to run script");
                    return ScriptBaseClass.ERR_GENERIC;
                }

                TaskInventoryItem item;
                lock (m_host.TaskInventory)
                {
                    item = m_host.TaskInventory[invItemID];
                }

                if (!CheckRuntimePerms(item, item.PermsGranter, ScriptBaseClass.PERMISSION_RETURN_OBJECTS))
                {
                    LSLError("No permissions to return objects");
                    return ScriptBaseClass.ERR_RUNTIME_PERMISSIONS;
                }

                // Validate the list of IDs and sort into parcel buckets.
                Dictionary<int, List<UUID>> objectsByParcel = new Dictionary<int, List<UUID>>();
                foreach (object o in objects.Data)
                {
                    UUID targetId = UUID.Zero;
                    if (!UUID.TryParse(o.ToString(), out targetId))
                        return ScriptBaseClass.ERR_MALFORMED_PARAMS;

                    if (targetId != UUID.Zero)
                    {
                        SceneObjectPart part = World.GetSceneObjectPart(targetId);
                        if (part == null) continue; // invalid ID

                        Vector3 pos = part.AbsolutePosition;
                        ILandObject parcel = World.LandChannel.GetNearestLandObjectInRegion(pos.X, pos.Y);
                        if (parcel == null) continue;

                        if (part.OwnerID == parcel.landData.OwnerID)
                            continue;   // cannot return the parcel owner's stuff with this
                        if (World.IsEstateManager(part.OwnerID))
                            continue;   // cannot return EO or EM objects with this either

                        // Now added it to the bucket for that parcel.
                        if (!objectsByParcel.ContainsKey(parcel.landData.LocalID))
                            objectsByParcel[parcel.landData.LocalID] = new List<UUID>();
                        objectsByParcel[parcel.landData.LocalID].Add(part.UUID);
                    }
                }

                // Now check and return the objects in each bucket.
                int count = 0;
                foreach (KeyValuePair<int, List<UUID>> bucket in objectsByParcel)
                {
                    count += World.LandChannel.ScriptedReturnObjectsInParcelByIDs(m_host, item, bucket.Value, bucket.Key);
                }
                if (count > 0)
                    m_log.InfoFormat("[LAND]: Scripted object list returned {0} objects by {1} for {2}", count, item.PermsGranter, item.OwnerID);
                return count;
            }
            catch (Exception e)
            {
                return ScriptBaseClass.ERR_GENERIC;
            }
        }

        public void iwStandTarget(Vector3 offset, Quaternion rot)
        {
            iwLinkStandTarget(m_host.LinkNum, offset, rot);
        }

        public void iwLinkStandTarget(int linknumber, Vector3 offset, Quaternion rot)
        {
            Quaternion qrot = Rot2Quaternion(rot);

            var parts = GetLinkPrimsOnly(linknumber);
            foreach (SceneObjectPart part in parts)
            {
                part.StandTargetPos = offset;
                part.StandTargetRot = qrot;
            }
        }

        public string llGetAnimationOverride(string anim_state)
        {
            NotImplemented("llGetAnimationOverride - NOT IMPLEMENTED");
            return String.Empty;
        }

        public void llSetAnimationOverride(string anim_state, string anim)
        {
            NotImplemented("llSetAnimationOverride - NOT IMPLEMENTED");
        }

        public void llResetAnimationOverride(string anim_state)
        {
            NotImplemented("llResetAnimationOverride - NOT IMPLEMENTED");
        }
    }


    public class NotecardCache
    {
        /// <summary>
        /// Timeout in seconds before we purge notecards from the cache
        /// </summary>
        public const int NOTECARD_CACHE_TIMEOUT = 60;

        private class Notecard
        {
            public string[] text;
            public DateTime lastRef;
        }

        private static Dictionary<UUID, Notecard> m_Notecards =
                new Dictionary<UUID, Notecard>();

        public static void Cache(UUID assetID, byte[] data)
        {
            lock (m_Notecards)
            {
                if (m_Notecards.ContainsKey(assetID))
                    return;

                Notecard nc = new Notecard();
                nc.lastRef = DateTime.Now;
                nc.text = ParseText(OpenSim.Framework.Util.SplitArray(data, (byte)'\n'));
                m_Notecards[assetID] = nc;
            }
        }

        private static string[] ParseText(List<ArraySegment<byte>> input)
        {
            int idx = 0;
            int level = 0;
            List<string> output = new List<string>();
            List<ArraySegment<byte>> words;

            while (idx < input.Count)
            {
                byte[] segmentArray = input[idx].Array;
                int segmentCount = input[idx].Count;
                int segmentOffset = input[idx].Offset;

                if (segmentCount == 1 && segmentArray[segmentOffset] == (byte)'{')
                {
                    level++;
                    idx++;
                    continue;
                }

                if (segmentCount == 1 && segmentArray[segmentOffset] == (byte)'}')
                {
                    level--;
                    idx++;
                    continue;
                }

                switch (level)
                {
                    case 0:
                        words = Util.SplitArraySegment(input[idx], (byte)' ');
                        // Notecards are created *really* empty. Treat that as "no text" (just like after saving an empty notecard)
                        if (words.Count < 3)
                            return new String[0];

                        int version = int.Parse(Util.ArraySegmentToString(words[3], Encoding.UTF8));//int.Parse(words[3]);
                        if (version != 2)
                            return new String[0];
                        break;

                    case 1:
                        words = Util.SplitArraySegment(input[idx], (byte)' ');
                        if (Util.ArraySegmentToString(words[0], Encoding.UTF8) == "LLEmbeddedItems")
                            break;

                        if (Util.ArraySegmentToString(words[0], Encoding.UTF8) == "Text")
                        {
                            int len = int.Parse(Util.ArraySegmentToString(words[2], Encoding.UTF8));
                            idx++;

                            int count = -1;

                            while (count < len)
                            {
                                int need = len - count - 1;
                                if (input[idx].Count > need)
                                {
                                    input[idx] = new ArraySegment<byte>(input[idx].Array, input[idx].Offset, need);
                                }

                                string ln = Util.ArraySegmentToString(input[idx], Encoding.UTF8);
                                if (ln.Contains("\r")) ln = ln.Replace("\r", String.Empty);

                                output.Add(ln);
                                count += input[idx].Count + 1;
                                idx++;
                            }

                            return output.ToArray();
                        }
                        break;
                    case 2:
                        words = Util.SplitArraySegment(input[idx], (byte)' ');
                        if (Util.ArraySegmentToString(words[0], Encoding.UTF8) == "count")
                        {
                            int c = int.Parse(Util.ArraySegmentToString(words[1], Encoding.UTF8));
                            if (c > 0)
                                return new String[0];
                            break;
                        }
                        break;
                }
                idx++;
            }
            return output.ToArray();
        }

        public static bool IsCached(UUID assetID)
        {
            lock (m_Notecards)
            {
                return IsCachedNoLock(assetID);
            }
        }

        private static bool IsCachedNoLock(UUID assetID)
        {
            return m_Notecards.ContainsKey(assetID);
        }

        public static int GetLines(UUID assetID)
        {
            lock (m_Notecards)
            {
                if (!IsCachedNoLock(assetID))
                    return -1;

                m_Notecards[assetID].lastRef = DateTime.Now;
                return m_Notecards[assetID].text.Length;
            }
        }

        public static string GetLine(UUID assetID, int line, int startOffset, int maxLength)
        {
            if ((line < 0) || (maxLength < 0))
                return String.Empty;

            string data;

            lock (m_Notecards)
            {
                if (!IsCachedNoLock(assetID))
                    return String.Empty;

                m_Notecards[assetID].lastRef = DateTime.Now;

                if (maxLength == 0)  // 0 is valid so update lastRef before this check.
                    return String.Empty;

                if (line >= m_Notecards[assetID].text.Length)
                    return "\n\n\n";

                data = m_Notecards[assetID].text[line];
                if (startOffset >= data.Length)
                    return String.Empty;  // no more data
                if (startOffset + maxLength > data.Length)
                    maxLength = data.Length - startOffset;  // last bit of data on the line

                return data.Substring(startOffset, maxLength);
            }
        }

        /// <summary>
        /// this must be done in the script engine thread to avoid race conditions
        /// </summary>
        public static void CacheCheck()
        {
            lock (m_Notecards)
            {
                foreach (UUID key in new List<UUID>(m_Notecards.Keys))
                {
                    Notecard nc = m_Notecards[key];
                    if (nc.lastRef.AddSeconds(NOTECARD_CACHE_TIMEOUT) < DateTime.Now)
                        m_Notecards.Remove(key);
                }
            }
        }

    }
}

/** @page lslfunc LSL Functions
 * @brief A listing of most, if not all, functions available in Halcyon.
 * 
 * Please note that this is a work in progress, and not every function may be listed,
 * nor is it likely that each has a solid description.
 * In fact it's likely that most of what you'll find here are Halcyon-specific extentions to what LL has defined.
 * 
 * Please reference the <a href="http://wiki.secondlife.com/wiki/Category:LSL_Functions">SecondLife® Wiki's Functions listing</a> for the details on functions that may not be defined here.
 */


/** @page lslevent LSL Events
 * @brief A listing of most, if not all, events available in Halcyon.
 * 
 * Please note that this is a work in progress, and not every event may be listed,
 * nor is it likely that each has a solid description.
 * In fact it's likely that most of what you'll find here are Halcyon-specific extentions to what LL has defined.
 * 
 * Please reference the <a href="http://wiki.secondlife.com/wiki/Category:LSL_Events">SecondLife® Wiki's Events listing</a> for the details on events that may not be defined here.
 */


/** @page lslerr LSL Errors
 * @brief A listing of most, if not all, errors produced during the execution of LSL in Halcyon.
 * 
 * Please note that this is a work in progress, and not every error may be listed,
 * nor is it likely that each has a solid description.
 * In fact it's likely that most of what you'll find here are Halcyon-specific extentions to what LL has defined.
 * 
 * Please reference the <a href="http://wiki.secondlife.com/wiki/LSL_Errors">SecondLife® Wiki's Errors listing</a> for the details on erros that may not be defined here.
 */

