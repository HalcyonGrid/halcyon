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
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.Framework.Scenes;
using log4net;
using System.Reflection;

using Halcyon.Phlox.Glue;
using Halcyon.Phlox.Types;
using OpenSim.Framework;
using OpenMetaverse;
using Nini.Config;
using System.Threading;
using OpenSim.Region.Framework;
using OpenSim.Region.CoreModules.Capabilities;

namespace Halcyon.Phlox.Engine
{
    public delegate void WorkArrivedDelegate();

    public class EngineInterface : INonSharedRegionModule, IScriptEngine, IScriptModule
    {
        public const System.Threading.ThreadPriority SUBTASK_PRIORITY = System.Threading.ThreadPriority.Lowest;

        /// <summary>
        /// Amount of time to wait for state data from the script engine before we give up
        /// </summary>
        private const int STATE_REQUEST_TIMEOUT = 10 * 1000;

        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        Scene _scene;
        ScriptLoader _scriptLoader;
        ExecutionScheduler _exeScheduler;
        MasterScheduler _masterScheduler;
        SupportedEventList _eventList = new SupportedEventList();
        EventRouter _eventRouter;
        StateManager _stateManager;

        private IConfigSource _configSource;
        public IConfig _scriptConfigSource;
        
        private bool _enabled;

        public string Name
        {
            get 
            {
                return "Halcyon.Phlox";
            }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void Initialize(Nini.Config.IConfigSource source)
        {
            _configSource = source;

            Preload();
        }

        private static void PreloadMethods(Type type)
        {
            foreach (var method in type.GetMethods(BindingFlags.DeclaredOnly |
                                BindingFlags.NonPublic |
                                BindingFlags.Public | BindingFlags.Instance |
                                BindingFlags.Static))
            {
                if (method.IsAbstract)
                    continue;
                if (method.ContainsGenericParameters || method.IsGenericMethod || method.IsGenericMethodDefinition)
                    continue;
                if ((method.Attributes & MethodAttributes.PinvokeImpl) > 0)
                    continue;

                try
                {
                    System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(method.MethodHandle);
                }
                catch
                {
                }
            }
        }

        private static void Preload()
        {
            foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
            {
                PreloadMethods(type);
            }

            foreach (var type in Assembly.GetAssembly(typeof(Halcyon.Phlox.VM.Interpreter)).GetTypes())
            {
                PreloadMethods(type);
            }
        }

        public void Close()
        {
            
        }

        private void WorkArrived()
        {
            _masterScheduler.WorkArrived();
        }

        public void AddRegion(Scene scene)
        {
            if (ConfigSource.Configs[ScriptEngineName] == null)
                ConfigSource.AddConfig(ScriptEngineName);

            _scriptConfigSource = ConfigSource.Configs[ScriptEngineName];

            _enabled = _scriptConfigSource.GetBoolean("Enabled", true);
            
            if (!_enabled) return;

            IWorldComm comms = scene.RequestModuleInterface<IWorldComm>();
            if (comms == null)
            {
                _log.Error("[Phlox]: Script engine can not start, no worldcomm module found");
                return;
            }

            comms.SetWorkArrivedDelegate(this.WorkArrived);

            _scene = scene;
            
            _exeScheduler = new ExecutionScheduler(this.WorkArrived, this, comms);
            _stateManager = new StateManager(_exeScheduler);
            _exeScheduler.StateManager = _stateManager;

            _scriptLoader = new ScriptLoader(scene.CommsManager.AssetCache, _exeScheduler, this.WorkArrived, this);
            _scriptLoader.StateManager = _stateManager;

            _masterScheduler = new MasterScheduler(_exeScheduler, _scriptLoader, _stateManager);
            _stateManager.MMasterScheduler = _masterScheduler;
            _eventRouter = new EventRouter(this);

            _scene.EventManager.OnRezScript += new EventManager.NewRezScript(EventManager_OnRezScript);
            _scene.EventManager.OnRemoveScript += new EventManager.RemoveScript(EventManager_OnRemoveScript);
            _scene.EventManager.OnReloadScript += new EventManager.ReloadScript(EventManager_OnReloadScript);
            _scene.EventManager.OnScriptReset += new EventManager.ScriptResetDelegate(EventManager_OnScriptReset);
            _scene.EventManager.OnGetScriptRunning += new EventManager.GetScriptRunning(EventManager_OnGetScriptRunning);
            _scene.EventManager.OnStartScript += new EventManager.StartScript(EventManager_OnStartScript);
            _scene.EventManager.OnStopScript += new EventManager.StopScript(EventManager_OnStopScript);
            _scene.EventManager.OnCompileScript += new EventManager.CompileScript(EventManager_OnCompileScript);
            _scene.EventManager.OnGroupCrossedToNewParcel += new EventManager.GroupCrossedToNewParcelDelegate(EventManager_OnGroupCrossedToNewParcel);
            _scene.EventManager.OnSOGOwnerGroupChanged += new EventManager.SOGOwnerGroupChangedDelegate(EventManager_OnSOGOwnerGroupChanged);
            _scene.EventManager.OnCrossedAvatarReady += OnCrossedAvatarReady;
            _scene.EventManager.OnGroupBeginInTransit += EventManager_OnGroupBeginInTransit;
            _scene.EventManager.OnGroupEndInTransit += EventManager_OnGroupEndInTransit;

            _masterScheduler.Start();

            _scene.StackModuleInterface<IScriptModule>(this);

            Phlox.Util.Preloader.Preload();
        }

        void EventManager_OnGroupEndInTransit(SceneObjectGroup sog, bool transitSuccess)
        {
            if (!transitSuccess)
            {
                sog.ForEachPart(delegate(SceneObjectPart part)
                {
                    foreach (TaskInventoryItem script in part.Inventory.GetScripts())
                    {
                        _exeScheduler.ChangeEnabledStatus(script.ItemID, EnableDisableFlag.CrossingWaitEnable);
                    }
                });
            }
        }

        void EventManager_OnGroupBeginInTransit(SceneObjectGroup sog)
        {
            sog.ForEachPart(delegate(SceneObjectPart part)
            {
                foreach (TaskInventoryItem script in part.Inventory.GetScripts())
                {
                    _exeScheduler.ChangeEnabledStatus(script.ItemID, EnableDisableFlag.CrossingWaitDisable);
                }
            });
        }

        void EventManager_OnSOGOwnerGroupChanged(SceneObjectGroup sog, UUID oldGroup, UUID newGroup)
        {
            ILandObject parcel = _scene.LandChannel.GetLandObject(sog.RootPart.GroupPosition.X, sog.RootPart.GroupPosition.Y);
            bool scriptsCanRun = ScriptsCanRun(parcel, sog.RootPart);

            EnableDisableFlag flag =
                    scriptsCanRun ? EnableDisableFlag.ParcelEnable : EnableDisableFlag.ParcelDisable;

            sog.ForEachPart(delegate(SceneObjectPart part)
            {
                foreach (TaskInventoryItem script in part.Inventory.GetScripts())
                {
                    _exeScheduler.ChangeEnabledStatus(script.ItemID, flag);
                }
            });
        }

        public bool ScriptsCanRun(ILandObject parcel, SceneObjectPart hostPart)
        {
            if (hostPart.ParentGroup.IsAttachment)
                return true;

            if (parcel == null)
                return false;

            bool parcelAllowsOtherScripts = (parcel.landData.Flags & (uint)ParcelFlags.AllowOtherScripts) != 0;
            bool parcelAllowsGroupScripts = (parcel.landData.Flags & (uint)ParcelFlags.AllowGroupScripts) != 0;
            bool parcelMatchesObjectGroup = parcel.landData.GroupID == hostPart.GroupID;
            bool ownerOwnsParcel = parcel.landData.OwnerID == hostPart.OwnerID;

            if (ownerOwnsParcel ||
                parcelAllowsOtherScripts ||
                (parcelAllowsGroupScripts && parcelMatchesObjectGroup))
            {
                return true;
            }

            return false;
        }

        void EventManager_OnGroupCrossedToNewParcel(SceneObjectGroup group, ILandObject oldParcel, ILandObject newParcel)
        {
            if (group.IsAttachment)
            {
                //attachment scripts always run and are unaffected by crossings
                return;
            }

            bool scriptsCouldRun = false;
            bool scriptsCanRun = false;

            bool oldParcelAllowedOtherScripts = oldParcel != null && (oldParcel.landData.Flags & (uint)ParcelFlags.AllowOtherScripts) != 0;
            bool oldParcelAllowedGroupScripts = oldParcel != null && (oldParcel.landData.Flags & (uint)ParcelFlags.AllowGroupScripts) != 0;
            bool oldParcelMatchesObjectGroup = oldParcel != null && oldParcel.landData.GroupID == group.GroupID;
            bool ownerOwnedOldParcel = oldParcel != null && oldParcel.landData.OwnerID == group.OwnerID;

            bool newParcelAllowsOtherScripts = newParcel != null && (newParcel.landData.Flags & (uint)ParcelFlags.AllowOtherScripts) != 0;
            bool newParcelAllowsGroupScripts = newParcel != null && (newParcel.landData.Flags & (uint)ParcelFlags.AllowGroupScripts) != 0;
            bool newParcelMatchesObjectGroup = newParcel != null && newParcel.landData.GroupID == group.GroupID;
            bool ownerOwnsNewParcel = newParcel != null && newParcel.landData.OwnerID == group.OwnerID;

            if (oldParcel == null ||
                ownerOwnedOldParcel ||
                oldParcelAllowedOtherScripts ||
                (oldParcelAllowedGroupScripts && oldParcelMatchesObjectGroup))
            {
                scriptsCouldRun = true;
            }

            if (ownerOwnsNewParcel ||
                newParcelAllowsOtherScripts ||
                (newParcelAllowsGroupScripts && newParcelMatchesObjectGroup))
            {
                scriptsCanRun = true;
            }

            List<TaskInventoryItem> scripts = new List<TaskInventoryItem>();
            if (scriptsCanRun != scriptsCouldRun)
            {
                EnableDisableFlag flag = 
                    scriptsCanRun ? EnableDisableFlag.ParcelEnable : EnableDisableFlag.ParcelDisable;

                if (flag == EnableDisableFlag.ParcelDisable)
                {
                    //do not parcel disable any scripted group that is holding avatar controls
                    if (group.HasAvatarControls)
                    {
                        return;
                    }
                }

                group.ForEachPart(delegate(SceneObjectPart part)
                {
                    foreach (TaskInventoryItem script in part.Inventory.GetScripts())
                    {
                        _exeScheduler.ChangeEnabledStatus(script.ItemID, flag);
                    }
                });
            }
        }

        void EventManager_OnCompileScript(string scriptSource, OpenSim.Region.Framework.Interfaces.ICompilationListener compListener)
        {
            //compile script to get error output only, do not start a run
            CompilerFrontend frontEnd = new CompilerFrontend(new CompilationListenerAdaptor(compListener), ".");
            frontEnd.Compile(scriptSource);
        }

        void EventManager_OnStopScript(uint localID, OpenMetaverse.UUID itemID)
        {
            _exeScheduler.ChangeEnabledStatus(itemID, EnableDisableFlag.GeneralDisable);
        }

        void EventManager_OnStartScript(uint localID, OpenMetaverse.UUID itemID)
        {
            _exeScheduler.ChangeEnabledStatus(itemID, EnableDisableFlag.GeneralEnable);
        }

        void EventManager_OnGetScriptRunning(OpenSim.Framework.IClientAPI controllingClient, 
            OpenMetaverse.UUID objectID, OpenMetaverse.UUID itemID)
        {
            _exeScheduler.PostScriptInfoRequest(new ScriptInfoRequest(itemID, ScriptInfoRequest.Type.ScriptRunningRequest,
                delegate(ScriptInfoRequest req)
                {
                    IEventQueue eq = World.RequestModuleInterface<IEventQueue>();
                    if (eq != null)
                    {
                        eq.Enqueue(EventQueueHelper.ScriptRunningReplyEvent(objectID, itemID, req.IsRunning, false),
                           controllingClient.AgentId);
                    }
                }
            ));
        }

        void EventManager_OnScriptReset(uint localID, OpenMetaverse.UUID itemID)
        {
            _exeScheduler.ResetScript(itemID);
        }

        void EventManager_OnRemoveScript(uint localID, OpenMetaverse.UUID itemID, SceneObjectPart part, EventManager.ScriptPostUnloadDelegate callback, bool allowedDrop, bool fireEvents, ReplaceItemArgs replaceArgs)
        {
            _scriptLoader.PostLoadUnloadRequest(
                new LoadUnloadRequest
                {
                    RequestType = LoadUnloadRequest.LUType.Unload,
                    LocalId = localID,
                    PostUnloadCallback = callback,
                    CallbackParams = new LoadUnloadRequest.UnloadCallbackParams
                    {
                        Prim = part,
                        ItemId = itemID,
                        AllowedDrop = allowedDrop,
                        FireEvents = fireEvents,
                        ReplaceArgs = replaceArgs,
                    }
                });
        }

        void EventManager_OnRezScript(uint localID, TaskInventoryItem item, string script,
            int? startParam, ScriptStartFlags startFlags, string engine, int stateSource, ICompilationListener listener)
        {
            //try to find the prim associated with the localid
            SceneObjectPart part = _scene.SceneGraph.GetPrimByLocalId(localID);
            if (part == null)
            {
                _log.ErrorFormat("[Phlox]: Unable to load script {0}. Prim {1} no longer exists",
                    item.ItemID, localID);
                return;
            }

            ILandObject parcel = _scene.LandChannel.GetLandObject(part.GroupPosition.X, part.GroupPosition.Y);
            bool startDisabled = !ScriptsCanRun(parcel, part);

            _scriptLoader.PostLoadUnloadRequest(new LoadUnloadRequest
            {
                RequestType = LoadUnloadRequest.LUType.Load,
                OldItemId = item.OldItemID,
                PostOnRez = (startFlags & ScriptStartFlags.PostOnRez) != 0,
                ChangedRegionStart = (startFlags & ScriptStartFlags.ChangedRegionStart) != 0,
                StartParam = startParam,
                StateSource = (OpenSim.Region.Framework.ScriptStateSource)stateSource,
                Listener = listener,
                StartLocalDisabled = startDisabled,
                StartGlobalDisabled = (startFlags & ScriptStartFlags.StartGloballyDisabled) != 0,
                FromCrossing = (startFlags & ScriptStartFlags.FromCrossing) != 0,
                CallbackParams = new LoadUnloadRequest.UnloadCallbackParams
                {
                    Prim = part,
                    ItemId = item.ItemID,
                    ReplaceArgs = null,     // signals that it is not used
                    AllowedDrop = false,    // not used
                    FireEvents = false,     // not used
                }
            });
        }

        void EventManager_OnReloadScript(uint localID, OpenMetaverse.UUID itemID, string script,
            int? startParam, ScriptStartFlags startFlags, string engine, int stateSource, ICompilationListener listener)
        {
            //try to find the prim associated with the localid
            SceneObjectPart part = _scene.SceneGraph.GetPrimByLocalId(localID);
            if (part == null)
            {
                _log.ErrorFormat("[Phlox]: Unable to reload script {0}. Prim {1} no longer exists",
                    itemID, localID);
                return;
            }

            ILandObject parcel = _scene.LandChannel.GetLandObject(part.GroupPosition.X, part.GroupPosition.Y);
            bool startDisabled = !ScriptsCanRun(parcel, part);

            _scriptLoader.PostLoadUnloadRequest(new LoadUnloadRequest
            {
                RequestType = LoadUnloadRequest.LUType.Reload,
                PostOnRez = (startFlags & ScriptStartFlags.PostOnRez) != 0,
                ChangedRegionStart = (startFlags & ScriptStartFlags.ChangedRegionStart) != 0,
                StartParam = startParam,
                StateSource = (OpenSim.Region.Framework.ScriptStateSource)stateSource,
                Listener = listener,
                StartLocalDisabled = startDisabled,
                StartGlobalDisabled = (startFlags & ScriptStartFlags.StartGloballyDisabled) != 0,
                CallbackParams = new LoadUnloadRequest.UnloadCallbackParams
                {
                    Prim = part,
                    ItemId = itemID,
                    ReplaceArgs = null,     // signals that it is not used
                    AllowedDrop = false,    // not used
                    FireEvents = false,     // not used
                }
            });
        }

        public void RemoveRegion(Scene scene)
        {
            if (_masterScheduler == null)
                return; // happens under Phlox if disabled
            _masterScheduler.Stop();
        }

        public void RegionLoaded(Scene scene)
        {
            
        }


        #region IScriptEngine Members

        public IScriptWorkItem QueueEventHandler(object parms)
        {
            throw new NotImplementedException();
        }

        public OpenSim.Region.Framework.Scenes.Scene World
        {
            get { return _scene; }
        }

        public IScriptModule ScriptModule
        {
            get { return this; }
        }

        VM.DetectVariables DetectParamsToDetectVariables(OpenSim.Region.ScriptEngine.Shared.DetectParams parm)
        {
            return new VM.DetectVariables
            {
                Grab = parm.OffsetPos,
                Key = parm.Key.ToString(),
                BotID = parm.BotID.ToString(),
                Group = parm.Group.ToString(),
                LinkNumber = parm.LinkNum,
                Name = parm.Name,
                Owner = parm.Owner.ToString(),
                Pos = parm.Position,
                Rot = parm.Rotation,
                Type = parm.Type,
                Vel = parm.Velocity,
                TouchBinormal = parm.TouchBinormal,
                TouchFace = parm.TouchFace,
                TouchNormal = parm.TouchNormal,
                TouchPos = parm.TouchPos,
                TouchST = parm.TouchST,
                TouchUV = parm.TouchUV
            };

        }

        VM.DetectVariables[] DetectParamsArrayToDetectVariablesArray(OpenSim.Region.ScriptEngine.Shared.DetectParams[] parms)
        {
            if (parms == null)
            {
                return new VM.DetectVariables[0];
            }

            VM.DetectVariables[] retVars = new VM.DetectVariables[parms.Length];
            for (int i = 0; i < parms.Length; i++)
            {
                retVars[i] = this.DetectParamsToDetectVariables(parms[i]);
            }

            return retVars;
        }

        public bool PostScriptEvent(OpenMetaverse.UUID itemID, OpenSim.Region.ScriptEngine.Shared.EventParams parms)
        {
            FunctionSig eventInfo = _eventList.GetEventByName(parms.EventName);
            VM.DetectVariables[] detectVars = this.DetectParamsArrayToDetectVariablesArray(parms.DetectParams);

            VM.PostedEvent evt = 
                new VM.PostedEvent { 
                    EventType = (SupportedEventList.Events) eventInfo.TableIndex, 
                    Args = parms.Params,
                    DetectVars = detectVars
                };

            evt.Normalize();

            _exeScheduler.PostEvent(itemID, evt);

            return true;
        }

        public bool PostObjectEvent(uint localID, OpenSim.Region.ScriptEngine.Shared.EventParams parms)
        {
            SceneObjectPart part = World.SceneGraph.GetPrimByLocalId(localID);
            if (part != null)
            {
                //grab the local ids of all the scripts and send the event to each script
                IList<TaskInventoryItem> scripts = part.Inventory.GetScripts();
                foreach (TaskInventoryItem script in scripts)
                {
                    this.PostScriptEvent(script.ItemID, parms);
                }
            }

            return false;
        }

        public OpenSim.Region.ScriptEngine.Shared.DetectParams GetDetectParams(OpenMetaverse.UUID item, int number)
        {
            throw new NotImplementedException();
        }

        public void SetMinEventDelay(OpenMetaverse.UUID itemID, double delay)
        {
            throw new NotImplementedException();
        }

        public int GetStartParameter(OpenMetaverse.UUID itemID)
        {
            throw new NotImplementedException();
        }

        public void SetScriptState(OpenMetaverse.UUID itemID, bool state)
        {
            EnableDisableFlag flag = state ? EnableDisableFlag.GeneralEnable : EnableDisableFlag.GeneralDisable;
            _exeScheduler.ChangeEnabledStatus(itemID, flag);
        }

        /// <summary>
        /// Should only be called by the LSL api as it directly accesses the scripts
        /// </summary>
        /// <param name="itemID"></param>
        /// <returns></returns>
        public bool GetScriptState(OpenMetaverse.UUID itemID)
        {
            VM.Interpreter script = _exeScheduler.FindScript(itemID);
            if (script == null)
                return false;
            return script.ScriptState.Enabled;
        }

        public void SetState(OpenMetaverse.UUID itemID, string newState)
        {
            throw new NotImplementedException();
        }

        public void ApiResetScript(OpenMetaverse.UUID itemID)
        {
            _exeScheduler.ResetNow(itemID);
        }

        public void ResetScript(OpenMetaverse.UUID itemID)
        {
            _exeScheduler.ResetScript(itemID);
        }

        public Nini.Config.IConfig Config
        {
            get { return _scriptConfigSource; }
        }

        public Nini.Config.IConfigSource ConfigSource
        {
            get { return _configSource; }
        }

        public string ScriptEngineName
        {
            get { return "Halcyon.Phlox";  }
        }

        public IScriptApi GetApi(OpenMetaverse.UUID itemID, string name)
        {
            throw new NotImplementedException();
        }

        public void UpdateTouchData(uint localId, OpenSim.Region.ScriptEngine.Shared.DetectParams[] det)
        {
            SceneObjectPart part = World.SceneGraph.GetPrimByLocalId(localId);
            if (part != null)
            {
                //grab the local ids of all the scripts and send the event to each script
                IList<TaskInventoryItem> scripts = part.Inventory.GetScripts();
                foreach (TaskInventoryItem script in scripts)
                {
                    VM.DetectVariables[] detectVars = this.DetectParamsArrayToDetectVariablesArray(det);
                    _exeScheduler.UpdateTouchData(script.ItemID, detectVars);
                }
            }
        }

        #endregion

        #region IScriptModule Members


        public string GetAssemblyName(OpenMetaverse.UUID itemID)
        {
            throw new NotImplementedException();
        }

        public string GetXMLState(OpenMetaverse.UUID itemID, StopScriptReason stopScriptReason)
        {
            if (!_masterScheduler.IsRunning)
            {
                _log.ErrorFormat("[Phlox]: Unable to retrieve state data for {0} master scheduler has died", itemID);
                return String.Empty;
            }

            StateDataRequest req = new StateDataRequest(itemID, true);
            req.DisableScriptReason = stopScriptReason;

            _exeScheduler.RequestStateData(req);
            bool success = req.WaitForData(STATE_REQUEST_TIMEOUT);

            if (req.SerializedStateData != null)
            {
                return Convert.ToBase64String(req.SerializedStateData);
            }
            else
            {
                _log.ErrorFormat("[Phlox]: Unable to retrieve state data for {0}, timeout: {1}", itemID,
                    !success);

                return String.Empty;
            }
        }

        public byte[] GetBinaryStateSnapshot(OpenMetaverse.UUID itemID, StopScriptReason stopScriptReason)
        {
            if (!_masterScheduler.IsRunning)
            {
                _log.ErrorFormat("[Phlox]: Unable to retrieve state data for {0} master scheduler has died", itemID);
                return null;
            }

            StateDataRequest req = new StateDataRequest(itemID, true);
            req.DisableScriptReason = stopScriptReason;

            _exeScheduler.RequestStateData(req);
            bool success = req.WaitForData(STATE_REQUEST_TIMEOUT);

            if (req.SerializedStateData != null)
            {
                return req.SerializedStateData;
            }
            else
            {
                _log.ErrorFormat("[Phlox]: Unable to retrieve state data for {0}, timeout: {1}", itemID,
                    !success);

                return null;
            }
        }

        public ScriptRuntimeInformation GetScriptInformation(UUID itemId)
        {
            if (!_masterScheduler.IsRunning)
            {
                _log.ErrorFormat("[Phlox]: Unable to retrieve state data for {0} master scheduler has died", itemId);
                return null;
            }

            StateDataRequest req = new StateDataRequest(itemId, false);
            req.DisableScriptReason = StopScriptReason.None;

            _exeScheduler.RequestStateData(req);
            bool success = req.WaitForData(STATE_REQUEST_TIMEOUT);

            if (!success) return null;

            Serialization.SerializedRuntimeState state = (Serialization.SerializedRuntimeState)req.RawStateData;

            ScriptRuntimeInformation info = new ScriptRuntimeInformation
            {
                CurrentEvent = state.RunningEvent == null ? "none" : state.RunningEvent.EventType.ToString(),
                CurrentState = state.RunState.ToString() + " | GlobalEnable: " + state.Enabled + " | " + req.CurrentLocalEnableState.ToString(),
                MemoryUsed = state.MemInfo.MemoryUsed,
                TotalRuntime = state.TotalRuntime,
                NextWakeup = state.NextWakeup,
                StackFrameFunctionName = state.TopFrame == null ? "none" : state.TopFrame.FunctionInfo.Name,
                TimerInterval = state.TimerInterval,
                TimerLastScheduledOn = state.TimerLastScheduledOn
            };

            return info;
        }

        public bool PostScriptEvent(OpenMetaverse.UUID itemID, string name, object[] args)
        {
            return this.PostScriptEvent(itemID, new OpenSim.Region.ScriptEngine.Shared.EventParams(name, args, null));
        }

        public bool PostObjectEvent(uint localId, string name, object[] args)
        {
            return this.PostObjectEvent(localId, new OpenSim.Region.ScriptEngine.Shared.EventParams(name, args, null));
        }

        #endregion

        /// <summary>
        /// Can only be called from inside the script run thread as it directly accesses script data
        /// </summary>
        /// <param name="itemID"></param>
        /// <returns></returns>
        public float GetTotalRuntime(UUID itemID)
        {
            VM.Interpreter script = _exeScheduler.FindScript(itemID);
            if (script != null)
            {
                return script.ScriptState.TotalRuntime;
            }

            return 0.0f;
        }
        public int GetFreeMemory(UUID itemID)
        {
            VM.Interpreter script = _exeScheduler.FindScript(itemID);
            if (script != null)
            {
                return script.ScriptState.MemInfo.MemoryFree;
            }

            return 0;
        }
        public int GetUsedMemory(UUID itemID)
        {
            VM.Interpreter script = _exeScheduler.FindScript(itemID);
            if (script != null)
            {
                return script.ScriptState.MemInfo.MemoryUsed;
            }

            return 0;
        }
        public int GetMaxMemory()
        {
            // Same for all scripts.
            return VM.MemoryInfo.MAX_MEMORY;
        }

        public float GetAverageScriptTime(UUID itemID)
        {
            VM.Interpreter script = _exeScheduler.FindScript(itemID);
            if (script != null)
            {
                return script.GetAverageScriptTime();
            }

            return 0.0f;
        }    

        /// <summary>
        /// Can only be called from inside the script run thread as it directly accesses script data
        /// Returns the amount of free space in the event queue (0 - 1.0)
        /// </summary>
        /// <param name="itemID"></param>
        /// <returns></returns>
        public float GetEventQueueFreeSpacePercentage(UUID itemID)
        {
            VM.Interpreter script = _exeScheduler.FindScript(itemID);
            if (script == null)
                return 1.0f;

            if (script.ScriptState.EventQueue.Count >= VM.RuntimeState.MAX_EVENT_QUEUE_SIZE)
                return 0.0f; // No space
            else
                return 1.0f - (float)script.ScriptState.EventQueue.Count / VM.RuntimeState.MAX_EVENT_QUEUE_SIZE;
        }

        /// <summary>
        /// Sets a timer. Can only be called from inside the script run thread as it directly accesses script data
        /// </summary>
        /// <param name="itemID"></param>
        /// <returns></returns>
        public void SetTimerEvent(uint localID, UUID itemID, float sec)
        {
            _exeScheduler.SetTimer(itemID, sec);
        }

        public void SysReturn(UUID itemId, object retValue, int delay)
        {
            _exeScheduler.PostSyscallReturn(itemId, retValue, delay);
        }

        public void ChangeScriptEnabledLandStatus(SceneObjectGroup group, bool enabled)
        {
            group.ForEachPart(delegate(SceneObjectPart part)
            {
                foreach (TaskInventoryItem item in part.Inventory.GetScripts())
                {
                    if (enabled)
                    {
                        _exeScheduler.ChangeEnabledStatus(item.ItemID, EnableDisableFlag.ParcelEnable);
                    }
                    else
                    {   
                        _exeScheduler.ChangeEnabledStatus(item.ItemID, EnableDisableFlag.ParcelDisable);
                    }
                }
            });
        }

        /// <summary>
        /// Continues scripted controls of a seated avatar after crossing
        /// </summary>
        /// <param name="satOnPart"></param>
        /// <param name="client"></param>
        public void OnCrossedAvatarReady(SceneObjectPart satPart, UUID agentId)
        {
            if (satPart == null) return;

            SceneObjectGroup satOnGroup = satPart.ParentGroup;

            List<TaskInventoryItem> allScripts = new List<TaskInventoryItem>();
            if (satOnGroup != null)
            {
                satOnGroup.ForEachPart(delegate(SceneObjectPart part)
                {
                    allScripts.AddRange(part.Inventory.GetScripts());
                });
            }

            foreach (TaskInventoryItem item in allScripts)
            {
                _exeScheduler.QueueCrossedAvatarReady(item.ItemID, agentId);
            }
        }

        public void DisableScriptTraces()
        {
            _exeScheduler.QueueCommand(new PendingCommand { CommandType = PendingCommand.PCType.StopAllTraces });
        }

        /// <summary>
        /// Returns serialized compiled script instances given a set of script asset ids
        /// </summary>
        /// <param name="assetIds"></param>
        /// <returns></returns>
        public Dictionary<UUID, byte[]> GetBytecodeForAssets(IEnumerable<UUID> assetIds)
        {
            if (!_masterScheduler.IsRunning)
            {
                _log.Error("[Phlox]: Unable to retrieve bytecode data for scripts, master scheduler has died");
                return new Dictionary<UUID,byte[]>();
            }


            const int DATA_WAIT_TIMEOUT = 3000;

            RetrieveBytecodeRequest rbRequest = new RetrieveBytecodeRequest { ScriptIds = assetIds };
            _scriptLoader.PostRetrieveByteCodeRequest(rbRequest);

            rbRequest.WaitForData(DATA_WAIT_TIMEOUT);

            return rbRequest.Bytecodes;
        }
    }
}
