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
using log4net;
using System.Reflection;

using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using System.IO;
using Amib.Threading;

namespace Halcyon.Phlox.Engine
{
    /// <summary>
    /// Schedules the execution of all the scripts running in a simulator
    /// </summary>
    internal class ExecutionScheduler
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// How many script Tick() operations total to run vs other duties such as loading
        /// </summary>
        private const int INSTRUCTION_FREQUENCY = 48;

        /// <summary>
        /// How many instructions to run for each script, this is to try and amortize the 
        /// cost of other duties the engine has to perform such as state saves
        /// </summary>
        private const int SCRIPT_TIMESLICE = 8;


        /// <summary>
        /// Holds a list of all scripts regardless of their run state
        /// </summary>
        private Dictionary<UUID, VM.Interpreter> _allScripts = new Dictionary<UUID, VM.Interpreter>();


        /// <summary>
        /// Scripts that are ready to run now
        /// </summary>
        private LinkedList<VM.Interpreter> _runQueue = new LinkedList<VM.Interpreter>();

        /// <summary>
        /// The next script that will run
        /// </summary>
        private LinkedListNode<VM.Interpreter> _nextScheduledScript;

        /// <summary>
        /// An index to running scripts for fast removal
        /// </summary>
        private Dictionary<UUID, LinkedListNode<VM.Interpreter>> _runningScriptsIndex
            = new Dictionary<UUID, LinkedListNode<VM.Interpreter>>();


        private struct SleepingScript : IComparable<SleepingScript>
        {
            public UUID ItemId;
            public UInt64 ReadyOn;

            /// <summary>
            /// The event to post when the script wakes up
            /// </summary>
            public enum WakingEvent
            {
                /// <summary>
                /// Do not post any event
                /// </summary>
                None,

                /// <summary>
                /// Post a new timer event
                /// </summary>
                Timer,

                /// <summary>
                /// Post a touch event with the information from the current state
                /// </summary>
                Touch
            }

            public WakingEvent EventToPost;

            #region IComparable<SleepingScript> Members

            public int CompareTo(SleepingScript other)
            {
                if (ReadyOn < other.ReadyOn)
                {
                    return -1;
                }
                else if (ReadyOn > other.ReadyOn)
                {
                    return 1;
                }
                else
                {
                    return 0;
                }
            }

            #endregion
        }

        /// <summary>
        /// A list of all scripts that are currently sleeping sorted by wakeup time asc
        /// </summary>
        private C5.IntervalHeap<SleepingScript> _sleepingScripts = new C5.IntervalHeap<SleepingScript>();

        /// <summary>
        /// Priority queue handles for each script in a sleep state waiting on a timer
        /// </summary>
        private Dictionary<UUID, C5.IPriorityQueueHandle<SleepingScript>> _timerSleepHandles
            = new Dictionary<UUID,C5.IPriorityQueueHandle<SleepingScript>>();

        /// <summary>
        /// Priority queue handles of all scripts sleeping due to a script sleep
        /// </summary>
        private Dictionary<UUID, C5.IPriorityQueueHandle<SleepingScript>> _stdSleepHandles
            = new Dictionary<UUID, C5.IPriorityQueueHandle<SleepingScript>>();

        /// <summary>
        /// Priority queue handles of all scripts sleeping due to a touch delay
        /// </summary>
        private Dictionary<UUID, C5.IPriorityQueueHandle<SleepingScript>> _touchSleepHandles
            = new Dictionary<UUID, C5.IPriorityQueueHandle<SleepingScript>>();

        /// <summary>
        /// Pending request for state data
        /// </summary>
        private Queue<StateDataRequest> _stateDataRequest = new Queue<StateDataRequest>();

        private struct PendingEvent
        {
            public UUID ItemId;
            public VM.PostedEvent EventInfo;
        }

        /// <summary>
        /// Pending events that need to be posted or added to the event queue
        /// </summary>
        private Queue<PendingEvent> _pendingEvents = new Queue<PendingEvent>();

        /// <summary>
        /// Pending requests for script information
        /// </summary>
        private Queue<ScriptInfoRequest> _infoRequests = new Queue<ScriptInfoRequest>();

        private struct PendingEnableDisable
        {
            public UUID ItemId;
            public EnableDisableFlag Flag;
        }

        /// <summary>
        /// Scripts waiting to be enabled or disabled
        /// </summary>
        private Queue<PendingEnableDisable> _pendingEnableDisableRequests = new Queue<PendingEnableDisable>();

        /// <summary>
        /// Scripts waiting to be reset
        /// </summary>
        private Queue<UUID> _pendingScriptResets = new Queue<UUID>();

        private struct SyscallReturn
        {
            public UUID ItemId;
            public object ReturnValue;
            public int Delay;
        }

        /// <summary>
        /// Returns from a long syscall waiting to be processed
        /// </summary>
        private Queue<SyscallReturn> _pendingSyscallReturns = new Queue<SyscallReturn>();

        /// <summary>
        /// Call to signal the master scheduler when work arrives
        /// </summary>
        WorkArrivedDelegate _workArrived;

        /// <summary>
        /// Reverse pointer to the engine this scheduler is running on
        /// </summary>
        EngineInterface _engine;

        /// <summary>
        /// For llListen message queueing and delivery
        /// </summary>
        IWorldComm _worldComm;

        /// <summary>
        /// Holds events from scripts that are not yet available
        /// </summary>
        DeferredEventManager _deferredEvents = new DeferredEventManager();

        /// <summary>
        /// Manages the saving and retrieval of state data for the region
        /// </summary>
        StateManager _stateManager;
        public StateManager StateManager
        {
            get
            {
                return _stateManager;
            }

            set
            {
                _stateManager = value;
            }
        }

        /// <summary>
        /// Scripts that need notification of a sim crossed avatar's readyness
        /// Tuple[ScriptId, UserId] 
        /// </summary>
        private List<Tuple<UUID, UUID>> _scriptsAwaitingDeferredEvents = new List<Tuple<UUID,UUID>>();

        /// <summary>
        /// Touch information updates waiting to be passed to their host scripts in applicable
        /// </summary>
        private Dictionary<UUID, VM.DetectVariables[]> _pendingTouchInfoUpdates = new Dictionary<UUID,VM.DetectVariables[]>();

        /// <summary>
        /// Threadpool to service async calls
        /// </summary>
        private SmartThreadPool _asyncCallPool;

        /// <summary>
        /// State data requests that were stalled by a syscall awaiting completion
        /// </summary>
        private Dictionary<UUID, List<StateDataRequest>> _stateRequestsWaitingOnSyscall = new Dictionary<UUID, List<StateDataRequest>>();

        private readonly TimeSpan SYSCALL_TIMEOUT = TimeSpan.FromMinutes(10);

        private DateTime _lastSyscallCleanupCheck = DateTime.Now;

        public bool TraceExecution { get; set; }

        private Queue<PendingCommand> _pendingCommands = new Queue<PendingCommand>();



        public ExecutionScheduler(WorkArrivedDelegate workArrived, EngineInterface engine, IWorldComm worldComm)
        {
            TraceExecution = false;

            STPStartInfo START_INFO = new STPStartInfo
            {
                WorkItemPriority = Amib.Threading.WorkItemPriority.Lowest,
                MinWorkerThreads = 0,
                MaxWorkerThreads = 6,
                IdleTimeout = 60 * 1000
            };

            _asyncCallPool = new SmartThreadPool(START_INFO);
            _asyncCallPool.Name = "Phlox Async";

            _workArrived = workArrived;
            _engine = engine;
            _worldComm = worldComm;
        }

        /// <summary>
        /// Posts a new event to a script
        /// </summary>
        /// <param name="itemId"></param>
        /// <param name="eventInfo"></param>
        public void PostEvent(UUID itemId, VM.PostedEvent eventInfo)
        {
            lock (_pendingEvents)
            {
                _pendingEvents.Enqueue(new PendingEvent { ItemId = itemId, EventInfo = eventInfo });
            }

            _workArrived();
        }
        
        private void PerformAsyncCall(Glue.SyscallShim.LongRunSyscallDelegate syscallDelegate)
        {
            //OpenSim.Framework.Util.FireAndForget(delegate(object obj) { syscallDelegate(); });
            _asyncCallPool.QueueWorkItem(
                delegate() 
                {
                    try
                    {
                        syscallDelegate();
                    }
                    catch (Exception e)
                    {
                        _log.ErrorFormat("[Phlox]: Exception thrown from async syscall: {0}", e);
                    }
                }
                );
        }

        internal void FinishedLoading(LoadUnloadRequest loadRequest, VM.CompiledScript script, VM.RuntimeState state)
        {
            if (_allScripts.ContainsKey(loadRequest.ItemId))
            {
                _log.ErrorFormat("[Phlox]: Refusing to load item {0}, it is already running", loadRequest.ItemId);
                return;
            }

            if (loadRequest.Prim == null)
            {
                _log.ErrorFormat("[Phlox]: Refusing to load item {0}, prim was not found", loadRequest.ItemId);
                return;
            }

            VM.Interpreter interp = null;
            Halcyon.Phlox.Glue.SyscallShim shim = new Glue.SyscallShim(this.PerformAsyncCall);
            LSLSystemAPI sysApi = new LSLSystemAPI(_engine, loadRequest.Prim, loadRequest.Prim.LocalId, loadRequest.ItemId);
            shim.SystemAPI = sysApi;

            try
            {
                if (state != null)
                {
                    interp = new VM.Interpreter(script, state, shim);
                    interp.ItemId = loadRequest.ItemId;
                    shim.Interpreter = interp;
                    sysApi.Script = interp;
                }
                else
                {
                    interp = new VM.Interpreter(script, shim);
                    interp.ItemId = loadRequest.ItemId;
                    shim.Interpreter = interp;
                    sysApi.Script = interp;

                    //no state means we need to fire state entry
                    interp.ScriptState.RunState = VM.RuntimeState.Status.Running;
                    sysApi.OnScriptReset();    // there is no state, initialize LSL (reset) as a new script (e.g. runtime perms)
                    this.PostEvent(interp.ItemId, new VM.PostedEvent { EventType = Types.SupportedEventList.Events.STATE_ENTRY, Args = new object[0] });
                }
            }
            catch (VM.VMException e)
            {
                //an out of memory exception can be thrown here from loading the script
                //bytecode
                _log.ErrorFormat("[Phlox]: Error while starting script {0}, asset {1}: {2}", loadRequest.ItemId, script.AssetId, e);
                return;
            }

            //for debugging will output a trace file
            if (TraceExecution)
            {
                interp.TraceExecution = true;
            }



            //hook up to state changes
            interp.OnStateChg += new VM.Interpreter.StateChgDelegate(interp_OnStateChg);

            if (loadRequest.StartParam.HasValue)
            {
                interp.ScriptState.StartParameter = loadRequest.StartParam.Value; // for llGetStartParameter()
            }

            if (loadRequest.PostOnRez)
            {
                this.PostEvent(interp.ItemId, new VM.PostedEvent { EventType = Types.SupportedEventList.Events.ON_REZ, Args = new object[] { interp.ScriptState.StartParameter } });
            }

            interp.SetScriptEventFlags();

            //Send the CHANGED_REGION_START if the script supports it and it is requested
            if (loadRequest.ChangedRegionStart &&
                interp.Script.FindEvent(interp.ScriptState.LSLState, (int)Types.SupportedEventList.Events.CHANGED) != null)
            {
                const int CHANGED_REGION_START = 0x400;
                this.PostEvent(interp.ItemId, 
                    new VM.PostedEvent { 
                        EventType = Types.SupportedEventList.Events.CHANGED, 
                        Args = new object[] { CHANGED_REGION_START } });
            }

            _allScripts.Add(interp.ItemId, interp);

            //if this script is loaded disabled, set the flag for parcel disable
            if (loadRequest.StartLocalDisabled && !loadRequest.Prim.ParentGroup.HasAvatarControls)
            {
                interp.ScriptState.LocalDisable |= VM.RuntimeState.LocalDisableFlag.Parcel;
                _log.InfoFormat("[Phlox]: Script {0}, asset {1} disabled on load (parcel scripts disabled)", loadRequest.ItemId, script.AssetId);
            }

            if (loadRequest.StartGlobalDisabled)
            {
                interp.ScriptState.GeneralEnable = false;
                _log.InfoFormat("[Phlox]: Script {0}, asset {1} disabled on load (global disable)", loadRequest.ItemId, script.AssetId);
            }

            // We may have altered the ScriptState flags above. Do this again to update the flags.
            interp.SetScriptEventFlags();

            this.InjectScript(interp, loadRequest.FromCrossing);
            this.InjectDeferredEvents(interp);

            _workArrived();
        }

        /// <summary>
        /// Schedules events or puts the script into the proper runstate 
        /// </summary>
        /// <param name="interp"></param>
        private void InjectScript(VM.Interpreter interp, bool fromCrossing)
        {
            if (interp.ScriptState.Enabled)
            {
                switch (interp.ScriptState.RunState)
                {
                    case VM.RuntimeState.Status.Running:
                        this.AddToRunQueue(interp);
                        break;

                    case VM.RuntimeState.Status.Sleeping:
                        UInt64 readyOn = interp.ScriptState.NextWakeup;
                        this.SetAndTrackSleep(interp, readyOn);
                        break;

                    case VM.RuntimeState.Status.Waiting:
                        //we were waiting, now we're injected.. do we have events?
                        //this can happen when events are posted to a script in crossing wait
                        while (interp.ScriptState.EventQueue.Count > 0)
                        {
                            VM.PostedEvent nextEvent = interp.ScriptState.EventQueue.Dequeue();

                            VM.EventInfo eventCallInfo = this.DoStateTransitionAndFindEventHandler(nextEvent, interp);
                            if (eventCallInfo != null)
                            {
                                StartEvent(nextEvent, interp, eventCallInfo);
                                break; //break to run this event
                            }
                        }
                        break;
                }

                //check for an active timer and schedule a wakeup
                if (interp.ScriptState.TimerInterval > 0)
                {
                    //compare the time the state was captured on to the time the timer was scheduled
                    //subtract the difference from the sleeper and add a new wakeup
                    Int64 preSaveWaitAmount = (Int64)interp.ScriptState.StateCapturedOn - (Int64)interp.ScriptState.TimerLastScheduledOn;
                    UInt64 readyOn = (UInt64)((Int64)OpenSim.Framework.Util.GetLongTickCount() + ((Int64)interp.ScriptState.TimerInterval - preSaveWaitAmount));
                    this.SetAndTrackTimer(interp, readyOn, true);
                }

                //this will reset listeners and other stored attributes
                interp.OnScriptInjected(fromCrossing);
            }
        }

        /// <summary>
        /// Injects events that were sent before a script was loaded into the target script
        /// </summary>
        /// <param name="interp"></param>
        private void InjectDeferredEvents(VM.Interpreter script)
        {
            DeferredEventManager.DefferredEvents deferredEvents = _deferredEvents.FindEvents(script.ItemId);
            if (deferredEvents != null)
            {
                foreach (VM.PostedEvent evt in deferredEvents.EventList)
                {
                    this.PostEvent(script.ItemId, evt);
                }

                foreach (EnableDisableFlag enableDisable in deferredEvents.EnableDisableList)
                {
                    this.EnableScript(script, enableDisable);
                }

                foreach (UUID avId in deferredEvents.GroupCrossedAvatarsReadyList)
                {
                    script.OnGroupCrossedAvatarReady(avId);
                }
            }
        }

        void interp_OnStateChg(VM.Interpreter script, int newState)
        {
            //remove all pending timers and sleep waits
            UnregisterScriptFromNotifications(script);

            //immediate halt, clear callstack, clear event queue
            script.ScriptState.StateChangePrep();

            //post state change events
            //lock events so that these two are always one following the other
            lock (_pendingEvents)
            {
                this.PostEvent(script.ItemId, new VM.PostedEvent { EventType = Types.SupportedEventList.Events.STATE_EXIT, Args = new object[0] });
                
                this.PostEvent(script.ItemId, new VM.PostedEvent
                {
                    EventType = Types.SupportedEventList.Events.STATE_ENTRY,
                    Args = new object[0],
                    TransitionToState = newState
                });
            }
        }

        public void RequestStateData(StateDataRequest stateDataReq)
        {
            lock (_stateDataRequest)
            {
                _stateDataRequest.Enqueue(stateDataReq);
            }

            _workArrived();
        }

        private bool DoNextStateRequest()
        {
            StateDataRequest req;
            lock (_stateDataRequest)
            {
                if (_stateDataRequest.Count == 0)
                {
                    return false;
                }

                req = _stateDataRequest.Dequeue();
            }

            return PerformStateRequest(req);
        }

        private bool PerformStateRequest(StateDataRequest req)
        {
            bool triggerDataReady = true;

            try
            {
                //find the script and grab the state the caller is looking for
                VM.Interpreter script = null;
                if (_allScripts.TryGetValue(req.ItemId, out script))
                {
                    //if the script is in a long syscall, we cant process it yet
                    //when the syscall returns we will handle it then 
                    if (script.ScriptState.RunState == VM.RuntimeState.Status.Syscall)
                    {
                        List<StateDataRequest> existingRequests;

                        if (_stateRequestsWaitingOnSyscall.TryGetValue(req.ItemId, out existingRequests))
                        {
                            existingRequests.Add(req);
                        }
                        else
                        {
                            _stateRequestsWaitingOnSyscall.Add(req.ItemId, new List<StateDataRequest> { req });
                        }

                        triggerDataReady = false;
                        return true;
                    }


                    try
                    {
                        Serialization.SerializedRuntimeState serState = Serialization.SerializedRuntimeState.FromRuntimeState(script.ScriptState);

                        if (req.PreSerialize)
                        {
                            //the caller has requested that we serialize the data to a byte array
                            //this usually means the call is coming from a take request or a script
                            //crossing
                            using (MemoryStream serializedData = new MemoryStream())
                            {
                                ProtoBuf.Serializer.Serialize(serializedData, serState);
                                req.SerializedStateData = serializedData.ToArray();
                            }
                        }
                        else
                        {
                            //the more common case, state saves for region objects, doesnt need the byte array
                            req.RawStateData = serState;
                            req.CurrentLocalEnableState = script.ScriptState.LocalDisable;
                        }
                    }
                    catch (Serialization.SerializationException e)
                    {
                        _log.ErrorFormat("[Phlox]: Unable to request state for {0} due to exception: {1}", script.ItemId, e);
                        req.RawStateData = null;
                        req.SerializedStateData = null;
                    }

                    //also the caller may have requested we also stop the script, this is only used for region
                    //crossing and is a special case currently. This is because crossing can not release controls
                    //but a disable of a controlled script should
                    if (req.DisableScriptReason != StopScriptReason.None)
                    {
                        if (req.DisableScriptReason == StopScriptReason.Crossing)
                            this.DisableScript(script, EnableDisableFlag.CrossingWaitDisable);
                        else
                            this.DisableScript(script, EnableDisableFlag.DerezDisable);
                    }
                }
            }
            finally
            {
                if (triggerDataReady)
                {
                    //in any case the waiter needs signaling as long as the request hasn't been deferred by a syscall
                    req.TriggerDataReady();
                }
            }


            return true;
        }

        private void SetAndTrackTimer(VM.Interpreter script, UInt64 readyOn, bool fromStateRestore)
        {
            //reset the timer
            C5.IPriorityQueueHandle<SleepingScript> newHandle = null;
            _sleepingScripts.Add(ref newHandle,
                new SleepingScript
                {
                    ItemId = script.ItemId,
                    EventToPost = SleepingScript.WakingEvent.Timer,
                    ReadyOn = readyOn
                });

            _timerSleepHandles.Add(script.ItemId, newHandle);

            if (!fromStateRestore)
            {
                script.ScriptState.TimerLastScheduledOn = OpenSim.Framework.Util.GetLongTickCount();
            }

            //we've reset the timer, so we need to pull any events that were part
            //of the previous schedule
            script.ScriptState.RemovePendingTimerEvent();
        }

        private void SetAndTrackSleep(VM.Interpreter script, UInt64 readyOn)
        {
            //set the sleep wakeup
            C5.IPriorityQueueHandle<SleepingScript> newHandle = null;
            _sleepingScripts.Add(ref newHandle,
                new SleepingScript
                {
                    ItemId = script.ItemId,
                    EventToPost = SleepingScript.WakingEvent.None,
                    ReadyOn = readyOn
                });

            _stdSleepHandles.Add(script.ItemId, newHandle);
            script.ScriptState.NextWakeup = readyOn;
        }

        private bool ProcessSyscallReturns()
        {
            List<SyscallReturn> returns;
            lock (_pendingSyscallReturns)
            {
                if (_pendingSyscallReturns.Count == 0)
                {
                    return false;
                }

                returns = new List<SyscallReturn>(_pendingSyscallReturns);
                _pendingSyscallReturns.Clear();
            }

            foreach (SyscallReturn ret in returns)
            {
                VM.Interpreter script;

                if (_allScripts.TryGetValue(ret.ItemId, out script))
                {
                    //check if someone was waiting on this syscall to get state information
                    List<StateDataRequest> requests;
                    if (_stateRequestsWaitingOnSyscall.TryGetValue(ret.ItemId, out requests))
                    {
                        _stateRequestsWaitingOnSyscall.Remove(ret.ItemId);
                    }

                    if (script.ScriptState.RunState != VM.RuntimeState.Status.Syscall)
                    {
                        //this script has been reset, post any requests and continue
                        if (requests != null)
                        {
                            foreach (StateDataRequest req in requests)
                            {
                                PerformStateRequest(req);
                            }
                        }

                        continue;
                    }

                    if (ret.ReturnValue != null)
                    {
                        script.ScriptState.Operands.Push(ret.ReturnValue);
                    }

                    if (ret.Delay == 0)
                    {
                        script.ScriptState.RunState = VM.RuntimeState.Status.Running;
                        this.AddToRunQueue(script);
                    }
                    else
                    {
                        script.ScriptState.RunState = VM.RuntimeState.Status.Sleeping;
                        script.ScriptState.NextWakeup = OpenSim.Framework.Util.GetLongTickCount() + (UInt64)ret.Delay;
                        this.SetAndTrackSleep(script, script.ScriptState.NextWakeup);
                    }

                    //finally see if we need to post and responses to state data requests
                    if (requests != null)
                    {
                        foreach (StateDataRequest req in requests)
                        {
                            PerformStateRequest(req);
                        }
                    }
                }
                else
                {
                    _log.ErrorFormat("[Phlox]: Syscall returned for script {0} but script was unloaded", ret.ItemId);
                }
            }

            //if it's been at least 10 minutes, check through the list of requests waiting on syscalls and clean it up
            if (DateTime.Now - _lastSyscallCleanupCheck > SYSCALL_TIMEOUT)
            {
                List<UUID> removals = new List<UUID>();
                foreach (KeyValuePair<UUID, List<StateDataRequest>> reqs in _stateRequestsWaitingOnSyscall)
                {
                    for (int i = reqs.Value.Count - 1; i >= 0; i--)
                    {
                        if (DateTime.Now - reqs.Value[i].TimeIssued >= SYSCALL_TIMEOUT)
                        {
                            reqs.Value.RemoveAt(i);
                        }
                    }

                    if (reqs.Value.Count == 0)
                    {
                        removals.Add(reqs.Key);
                    }
                }

                foreach (UUID id in removals)
                {
                    _stateRequestsWaitingOnSyscall.Remove(id);
                }

                _lastSyscallCleanupCheck = DateTime.Now;
            }
            

            return true;
        }

        public bool CheckForRunstateChange()
        {
            //most common case, the script is still running
            if (_nextScheduledScript.Value.ScriptState.RunState == VM.RuntimeState.Status.Running)
            {
                return false;
            }

            switch (_nextScheduledScript.Value.ScriptState.RunState)
            {
                case VM.RuntimeState.Status.Sleeping:
                    TransitionToSleep();
                    break;

                case VM.RuntimeState.Status.Waiting:
                    TransitionToWait();
                    break;

                case VM.RuntimeState.Status.Killed:
                    TransitionToKilled();
                    break;

                case VM.RuntimeState.Status.Syscall:
                    TransitionToSyscall();
                    break;
            }

            return true;
        }

        private void TransitionToSyscall()
        {
            //for a syscall we wait for the syscall message to come in to reenable the script
            _runQueue.Remove(_nextScheduledScript);
            _runningScriptsIndex.Remove(_nextScheduledScript.Value.ItemId);
        }

        private void TransitionToKilled()
        {
            //the script did something bad and is being forcibly removed
            _runningScriptsIndex.Remove(_nextScheduledScript.Value.ItemId);
            _runQueue.Remove(_nextScheduledScript);
        }

        private void TransitionToWait()
        {
            //this script has transitioned to a halt, if there are events, post them and leave this script
            //on the run queue. if there are no events, remove from the run queue. If we are returning 
            //from a sleep and the script still has an active timer, repost the sleep timeout
            VM.Interpreter movedScript = _nextScheduledScript.Value;

            //if there was an event, it's finished now, unref it
            movedScript.ScriptState.RunningEvent = null;

            //look for an event on the queue that this script can service
            while (movedScript.ScriptState.EventQueue.Count > 0)
            {
                VM.PostedEvent nextEvent = movedScript.ScriptState.EventQueue.Dequeue();

                VM.EventInfo eventCallInfo = this.DoStateTransitionAndFindEventHandler(nextEvent, movedScript);
                this.CheckAndResetTouchWait(movedScript, nextEvent);

                if (eventCallInfo != null)
                {
                    try
                    {
                        //we found an event to handle
                        //push the stack and reposition the instruction pointer
                        movedScript.ScriptState.DoEvent(eventCallInfo, nextEvent, nextEvent.Args);
                    }
                    catch (VM.VMException e)
                    {
                        //an out of memory exception can be thrown here
                        _log.ErrorFormat("[Phlox]: Error while starting event for script {0}, asset {1}: {2}", movedScript.ItemId, movedScript.Script.AssetId, e);
                        TerminateScriptWithError(e, movedScript);

                        _runningScriptsIndex.Remove(_nextScheduledScript.Value.ItemId);
                        _runQueue.Remove(_nextScheduledScript);

                        return;
                    }

                    this.CheckAndResetTimerWait(movedScript, eventCallInfo);

                    //we have a good call, return here so the script is left on the run queue
                    return;
                }
            }

            _runningScriptsIndex.Remove(_nextScheduledScript.Value.ItemId);
            _runQueue.Remove(_nextScheduledScript);
            return;
        }

        /// <summary>
        /// This is called at the end of an event
        /// if this is a timer event and it's still enabled, repush it to the wait list
        /// but only if it wasn't reset from inside the timer event and only if the interval
        /// wasn't already expired while we were inside the timer which caused a timer event
        /// to be queued
        /// </summary>
        /// <param name="movedScript"></param>
        /// <param name="eventCallInfo"></param>
        private void CheckAndResetTimerWait(VM.Interpreter movedScript, VM.EventInfo eventCallInfo)
        {
            VM.PostedEvent timerEventUnused;
            if (eventCallInfo.EventType == (int)Types.SupportedEventList.Events.TIMER &&
                movedScript.ScriptState.TimerInterval > 0 &&
                !_timerSleepHandles.ContainsKey(movedScript.ItemId) &&
                !movedScript.ScriptState.EventQueue.Find(
                    (evt) => {return evt.EventType == Types.SupportedEventList.Events.TIMER;}, 
                    out timerEventUnused))
            {
                this.ScriptSetTimer(movedScript);
            }
        }

        /// <summary>
        /// if this is a touch event and it's still enabled, repush it to the wait list.
        /// </summary>
        /// <param name="movedScript">The script</param>
        /// <param name="potentialNextEvent">The next event that could POTENTIALLY be run, the script does not necessarily have to have a handler for the event</param>
        private void CheckAndResetTouchWait(VM.Interpreter movedScript, VM.PostedEvent potentialNextEvent)
        {
            //if we got a touch start and this script supports it we should also start a repeat
            //of touch()
            if (potentialNextEvent.EventType == Types.SupportedEventList.Events.TOUCH_START &&
                movedScript.Script.FindEvent(movedScript.ScriptState.LSLState, (int)Types.SupportedEventList.Events.TOUCH) != null &&
                movedScript.ScriptState.TouchActive == false)
            {
                movedScript.ScriptState.TouchActive = true;
                movedScript.ScriptState.CurrentTouchDetectVars = potentialNextEvent.DetectVars;
                ResetTouchInterval(movedScript);
            } 
            else if (potentialNextEvent.EventType == Types.SupportedEventList.Events.TOUCH_END &&
                movedScript.Script.FindEvent(movedScript.ScriptState.LSLState, (int)Types.SupportedEventList.Events.TOUCH) != null &&
                movedScript.ScriptState.TouchActive == true)
            {
                movedScript.ScriptState.TouchActive = false;
                movedScript.ScriptState.CurrentTouchDetectVars = null;

                //also pull any pending touch activations (prevents an issue with touch reindexing)
                C5.IPriorityQueueHandle<SleepingScript> sleepHandle;
                if (_touchSleepHandles.TryGetValue(movedScript.ItemId, out sleepHandle))
                {
                    _touchSleepHandles.Remove(movedScript.ItemId);
                    _sleepingScripts.Delete(sleepHandle);
                }
            }
            else if (potentialNextEvent.EventType == Types.SupportedEventList.Events.TOUCH &&
                movedScript.ScriptState.TouchActive)
            {
                potentialNextEvent.DetectVars = movedScript.ScriptState.CurrentTouchDetectVars;
                ResetTouchInterval(movedScript);
            }
        }

        private const UInt64 TOUCH_REPEAT_INTERVAL = 100;
        private void ResetTouchInterval(VM.Interpreter movedScript)
        {
            // Touch can be queued at any point, so we make sure we dont already have one waiting
            // to ensure only one touch event on the queue at once
            if (!_touchSleepHandles.ContainsKey(movedScript.ItemId))
            {
                //reset the wait
                C5.IPriorityQueueHandle<SleepingScript> newHandle = null;
                _sleepingScripts.Add(ref newHandle,
                    new SleepingScript
                    {
                        ItemId = movedScript.ItemId,
                        EventToPost = SleepingScript.WakingEvent.Touch,
                        ReadyOn = OpenSim.Framework.Util.GetLongTickCount() + TOUCH_REPEAT_INTERVAL
                    });

                _touchSleepHandles.Add(movedScript.ItemId, newHandle);
            }
        }

        private void TransitionToSleep()
        {
            VM.Interpreter movedScript = _nextScheduledScript.Value;
            _runQueue.Remove(_nextScheduledScript);
            _runningScriptsIndex.Remove(_nextScheduledScript.Value.ItemId);

            this.SetAndTrackSleep(movedScript, movedScript.ScriptState.NextWakeup);
        }

        private void CheckForRunnableSleepingScripts()
        {
            UInt64 now = OpenSim.Framework.Util.GetLongTickCount();

            while (_sleepingScripts.Count > 0)
            {
                SleepingScript sleeper = _sleepingScripts.FindMin();
                if (now >= sleeper.ReadyOn)
                {
                    _sleepingScripts.DeleteMin();

                    if (sleeper.EventToPost == SleepingScript.WakingEvent.None)
                    {
                        //remove the script sleep tracking
                        _stdSleepHandles.Remove(sleeper.ItemId);
                    }
                    else if (sleeper.EventToPost == SleepingScript.WakingEvent.Timer)
                    {
                        //remove the timer sleep
                        _timerSleepHandles.Remove(sleeper.ItemId);
                    }
                    else
                    {
                        //this is a touch sleep. remove that
                        _touchSleepHandles.Remove(sleeper.ItemId);
                    }

                    VM.Interpreter script;
                    if (_allScripts.TryGetValue(sleeper.ItemId, out script))
                    {
                        if (script.ScriptState.RunState == VM.RuntimeState.Status.Killed ||
                            script.ScriptState.Enabled == false)
                        {
                            continue;
                        }

                        
                        if (sleeper.EventToPost == SleepingScript.WakingEvent.None)
                        {
                            //there are three scenarios here. first this might just be a script sleep
                            this.AddToRunQueue(script);
                        }
                        else if (sleeper.EventToPost == SleepingScript.WakingEvent.Timer)
                        {
                            //secondly this could be a timer signal
                            this.PostEvent(sleeper.ItemId,
                                new VM.PostedEvent
                                {
                                    EventType = Types.SupportedEventList.Events.TIMER,
                                    Args = new object[0]
                                });
                        }
                        else if (sleeper.EventToPost == SleepingScript.WakingEvent.Touch)
                        {
                            //make sure we're still waiting for touches
                            if (script.ScriptState.TouchActive)
                            {
                                //finally this can be a repeating touch event
                                this.PostEvent(sleeper.ItemId,
                                    new VM.PostedEvent
                                    {
                                        EventType = Types.SupportedEventList.Events.TOUCH,
                                        Args = new object[] { 1 }, //this will need to change if we end up ever doing touch* right
                                        DetectVars = script.ScriptState.CurrentTouchDetectVars
                                    });
                            }
                        }
                    }
                }
                else
                {
                    break;
                }
            }
        }

        private bool WorkIsPending()
        {
            if (_runQueue.Count > 0) return true;

            lock (_stateDataRequest)
            {
                if (_stateDataRequest.Count > 0) return true;
            }

            lock (_pendingEvents)
            {
                if (_pendingEvents.Count > 0) return true;
            }

            lock (_infoRequests)
            {
                if (_infoRequests.Count > 0) return true;
            }

            lock (_pendingEnableDisableRequests)
            {
                if (_pendingEnableDisableRequests.Count > 0) return true;
            }

            lock (_pendingScriptResets)
            {
                if (_pendingScriptResets.Count > 0) return true;
            }

            lock (_pendingSyscallReturns)
            {
                if (_pendingSyscallReturns.Count > 0) return true;
            }

            lock (_scriptsAwaitingDeferredEvents)
            {
                if (_scriptsAwaitingDeferredEvents.Count > 0) return true;
            }

            lock (_pendingTouchInfoUpdates)
            {
                if (_pendingTouchInfoUpdates.Count > 0) return true;
            }

            lock (_pendingCommands)
            {
                if (_pendingCommands.Count > 0) return true;
            }

            return false;
        }

        private void AddToScriptQueue(VM.Interpreter script, VM.PostedEvent evt)
        {
            script.ScriptState.QueueEvent(evt);
        }

        private void ProcessEventQueue()
        {
            List<PendingEvent> pendingEvents;
            //we have to make a copy because holding a lock for performing these 
            //actions can cause a deadlock
            lock (_pendingEvents)
            {
                pendingEvents = new List<PendingEvent>(_pendingEvents);
                _pendingEvents.Clear();
            }


            foreach (PendingEvent pendingEvt in pendingEvents)
            {
                //make sure the script exists
                VM.Interpreter script;
                if (_allScripts.TryGetValue(pendingEvt.ItemId, out script))
                {
                    if (script.ScriptState.RunState == VM.RuntimeState.Status.Killed ||
                        !script.ScriptState.Enabled)
                    {
                        if (pendingEvt.EventInfo.EventType == Types.SupportedEventList.Events.STATE_ENTRY)
                        {
                            //killed and disabled scripts should no longer respond to outside stimuli
                            //however, when a script is saved with the enabled checkbox off, we must allow
                            //state entry to get queued
                            this.AddToScriptQueue(script, pendingEvt.EventInfo);
                            continue;
                        }
                        else if (script.ScriptState.GeneralEnable && script.ScriptState.LocalDisable == VM.RuntimeState.LocalDisableFlag.CrossingWait)
                        {
                            //CrossingWait scripts should not drop events. They will be enabled after a few ms and everything
                            //should be queued
                            this.AddToScriptQueue(script, pendingEvt.EventInfo);
                            continue;
                        }
                        else
                        {
                            continue;
                        }
                    }

                    //transitioning here seems wrong, but a state entry event means the script must already
                    //be in wait
                    VM.EventInfo eventCallInfo = DoStateTransitionAndFindEventHandler(pendingEvt.EventInfo, script);
                    this.CheckAndResetTouchWait(script, pendingEvt.EventInfo);

                    if (eventCallInfo != null)
                    {
                        if (script.ScriptState.RunState == VM.RuntimeState.Status.Waiting)
                        {
                            //this is the butter state, we can start this event running right away
                            StartEvent(pendingEvt.EventInfo, script, eventCallInfo);
                        }
                        else
                        {
                            //in other states we need to queue this event
                            
                            //one special case. if this is a control event, and it is a null change
                            //do not add it to the script queue if there is already a control event 
                            //in the queue. this is because sometimes the client will spam the
                            //server with control events, this appears to be some kind of race on the
                            //viewer

                            if (pendingEvt.EventInfo.EventType == Types.SupportedEventList.Events.CONTROL &&
                                (int)pendingEvt.EventInfo.Args[2] == 0)
                            {
                                if (!script.ScriptState.IsEventQueued(Types.SupportedEventList.Events.CONTROL))
                                {
                                    this.AddToScriptQueue(script, pendingEvt.EventInfo);
                                }
                            }
                            else
                            {
                                this.AddToScriptQueue(script, pendingEvt.EventInfo);
                            }
                        }
                    }
                }
                else
                {
                    //_log.DebugFormat("[Phlox]: Deferring {0} for script {1}", pendingEvt.EventInfo.EventType, pendingEvt.ItemId);
                    _deferredEvents.AddEvent(pendingEvt.ItemId, pendingEvt.EventInfo);
                }
            }
            
        }

        private void StartEvent(VM.PostedEvent eventInfo, VM.Interpreter script, VM.EventInfo eventCallInfo)
        {
            try
            {
                script.ScriptState.DoEvent(eventCallInfo, eventInfo, eventInfo.Args);
                this.CheckAndResetTimerWait(script, eventCallInfo);

                this.AddToRunQueue(script);
            }
            catch (VM.VMException e)
            {
                //we can get a memory error here when trying to set up the event
                //stack frames
                TerminateScriptWithError(e, script);
            }
        }

        private VM.EventInfo DoStateTransitionAndFindEventHandler(VM.PostedEvent eventInfo, VM.Interpreter script)
        {
            //if this event includes a state transition we need to check that state for
            //the event def
            if (eventInfo.TransitionToState != VM.PostedEvent.NO_TRANSITION)
            {
                script.ScriptState.LSLState = eventInfo.TransitionToState;
                script.SetScriptEventFlags();

                //also reset timer if the timer event exists
                VM.EventInfo evtInfo = script.Script.FindEvent(eventInfo.TransitionToState, (int)Types.SupportedEventList.Events.TIMER);
                if (evtInfo != null)
                {
                    this.CheckAndResetTimerWait(script, evtInfo);
                }
            }

            VM.EventInfo eventCallInfo = script.Script.FindEvent(script.ScriptState.LSLState, (int)eventInfo.EventType);


            return eventCallInfo;
        }

        private void AddToRunQueue(VM.Interpreter script)
        {
            LinkedListNode<VM.Interpreter> entry = _runQueue.AddLast(script);

            //am I also the first one on the queue?!
            if (_runQueue.Count == 1)
            {
                //put me in the queue
                _nextScheduledScript = entry;
            }

            //index me
            _runningScriptsIndex.Add(script.ItemId, entry);

            //im running
            script.ScriptState.RunState = VM.RuntimeState.Status.Running;
        }

        private void ProcessWaitingListen()
        {
            if (_worldComm.HasMessages())
            {
                IWorldCommListenerInfo info = _worldComm.GetNextMessage();
                if (info != null)
                {
                    VM.DetectVariables[] detectVariables = new VM.DetectVariables[0];
                    if (!_worldComm.UUIDIsPrim(info.GetHostID()))
                    {
                        //It's a botListen, give it the detect param for iwDetectedBot
                        detectVariables = new VM.DetectVariables[1]
                        {
                            new VM.DetectVariables() { BotID = info.GetHostID().ToString() }
                        };
                    }

                    //post event to the proper script
                    this.PostEvent(info.GetItemID(),
                        new VM.PostedEvent
                        {
                            EventType = Types.SupportedEventList.Events.LISTEN,
                            Args = new object[4] {
                            info.GetChannel(),
                            info.GetName(),
                            info.GetID().ToString(),
                            info.GetMessage()
                            },
                            DetectVars = detectVariables
                        });
                }
            }
        }

        public WorkStatus DoWork()
        {
            //do unloads and state saves first
            bool stateRequestDidWork = DoNextStateRequest();
            this.CheckForRunnableSleepingScripts();
            this.ProcessWaitingListen();
            this.ProcessEventQueue();
            bool infoRequestDidWork = ProcessScriptInfoRequest();
            bool enableDisableDidWork = DoEnableDisable();
            bool resetDidWork = DoScriptReset();
            bool sysReturnDidWork = ProcessSyscallReturns();
            _deferredEvents.DoExpirations();
            ProcessScriptsAwaitingDeferredEvents();
            ProcessTouchInfoUpdates();
            RunPendingCommands();

            bool didWork = _nextScheduledScript != null;

            DoTimeslices();
            
            return new WorkStatus
            {
                WorkWasDone = stateRequestDidWork || didWork || infoRequestDidWork || resetDidWork || sysReturnDidWork,
                WorkIsPending = this.WorkIsPending(),
                NextWakeUpTime = _sleepingScripts.Count > 0 ? _sleepingScripts.FindMin().ReadyOn : UInt64.MaxValue
            };
        }

        private void ProcessTouchInfoUpdates()
        {
            List<KeyValuePair<UUID, VM.DetectVariables[]>> updates = null;
            lock (_pendingTouchInfoUpdates)
            {
                if (_pendingTouchInfoUpdates.Count == 0)
                {
                    return;
                }

                updates = new List<KeyValuePair<UUID, VM.DetectVariables[]>>(_pendingTouchInfoUpdates);
                _pendingTouchInfoUpdates.Clear();
            }

            foreach (var update in updates)
            {
                //try to find the script
                VM.Interpreter script;
                if (_allScripts.TryGetValue(update.Key, out script))
                {
                    //make sure the script has a touch active
                    if (script.ScriptState.TouchActive)
                    {
                        //update the params
                        script.ScriptState.CurrentTouchDetectVars = update.Value;
                    }
                }
            }
        }


        private System.Diagnostics.Stopwatch _sliceWatch = new System.Diagnostics.Stopwatch();

        /// <summary>
        /// At 500 ms the script tick is considered slow and a warning will be reported
        /// </summary>
        private const int SCRIPT_TIMESLICE_SLOW_THRESH_MS = 250;
        private void DoTimeslices()
        {
            int iterations = 0;
            while (_nextScheduledScript != null && iterations < INSTRUCTION_FREQUENCY)
            {
                LinkedListNode<VM.Interpreter> followingScript = _nextScheduledScript.Next;

                int timesliceInstructions = 0;

                _sliceWatch.Start();
                while (timesliceInstructions < SCRIPT_TIMESLICE)
                {
                    try
                    {
                        _nextScheduledScript.Value.Tick();
                    }
                    catch (Exception e)
                    {
                        VM.Interpreter script = _nextScheduledScript.Value;

                        TerminateScriptWithError(e, script);
                    }

                    iterations++;
                    timesliceInstructions++;

                    //check for state transition
                    if (this.CheckForRunstateChange())
                    {
                        break;
                    }
                }

                _sliceWatch.Stop();

                //profiling
                _nextScheduledScript.Value.AddExecutionTime(_sliceWatch.Elapsed.TotalMilliseconds);

                if (_sliceWatch.Elapsed.TotalMilliseconds >= SCRIPT_TIMESLICE_SLOW_THRESH_MS)
                {
                    this.ReportSlowTimeslice(_nextScheduledScript.Value, _sliceWatch.Elapsed.TotalMilliseconds);
                }

                _sliceWatch.Reset();

                //tell our state manager that we changed and need saving
                _stateManager.ScriptChanged(_nextScheduledScript.Value);

                _nextScheduledScript = followingScript;
                if (_nextScheduledScript == null)
                {
                    _nextScheduledScript = _runQueue.First;
                }
            }
        }

        private void ReportSlowTimeslice(VM.Interpreter script, double msTaken)
        {
            string bytecodeDisplay = this.GenerateBacktrace(script, 32);
            _log.WarnFormat("[Phlox]: Slow timeslice for asset {0} ({1} ms). Backtrace leading up to IP at {2}: [{3}]",
                script.Script.AssetId, msTaken, script.ScriptState.IP.ToString("X") + "h", bytecodeDisplay);
        }

        private string GenerateBacktrace(VM.Interpreter script, int numBytes)
        {
            int startAt = script.ScriptState.IP - numBytes;
            if (startAt < 0) startAt = 0;

            int endAt = startAt + 32;
            if (endAt >= script.Script.ByteCode.Length) endAt = script.Script.ByteCode.Length - 1;

            int len = endAt - startAt;

            string bitString = BitConverter.ToString(script.Script.ByteCode, startAt, len);
            return bitString.Replace('-', ' ');
        }

        private void TerminateScriptWithError(Exception e, VM.Interpreter script)
        {
            script.ScriptState.RunState = VM.RuntimeState.Status.Killed;

            script.TraceExecution = false;

            _log.ErrorFormat("[Phlox]: The script asset {0} item {1} threw an exception {2} script stopped",
                script.Script.AssetId, script.ItemId, e);

            script.ShoutError(String.Format("Script {0} encountered a problem and was stopped: {1}",
                script.Script.AssetId, e.Message));
        }
        
        private bool DoScriptReset()
        {
            UUID resetId;
            lock (_pendingScriptResets)
            {
                if (_pendingScriptResets.Count == 0)
                {
                    return false;
                }

                resetId = _pendingScriptResets.Dequeue();
            }

            return ResetNow(resetId);
        }

        public bool ResetNow(UUID resetId)
        {
            VM.Interpreter script;
            if (_allScripts.TryGetValue(resetId, out script))
            {
                //remove all pending events
                UnregisterScriptFromNotifications(script);

                script.Reset();
                script.SetScriptEventFlags();

                //reset also fires state entry
                this.PostEvent(resetId, new VM.PostedEvent { EventType = Types.SupportedEventList.Events.STATE_ENTRY, Args = new object[0] });

                if (!_runningScriptsIndex.ContainsKey(resetId) && script.ScriptState.Enabled)
                {
                    this.AddToRunQueue(script);
                }

                return true;
            }

            return false;
        }

        private void AfterDisable(VM.Interpreter script, Types.ScriptUnloadReason unloadReason, VM.RuntimeState.LocalDisableFlag ldFlag, StopScriptReason stopScriptReason)
        {
            this.RemoveFromRunQueue(script.ItemId);

            this.UnregisterScriptFromNotifications(script);
            script.OnUnload(unloadReason, ldFlag);
            script.ScriptState.StateCapturedOn = OpenSim.Framework.Util.GetLongTickCount();
        }

        private void DisableScript(VM.Interpreter script, EnableDisableFlag flag)
        {
            bool wasEnabled = script.ScriptState.Enabled;

            switch (flag)
            {
                case EnableDisableFlag.GeneralDisable:
                    if (script.ScriptState.GeneralEnable == true)
                    {
                        script.ScriptState.GeneralEnable = false;
                        _log.InfoFormat("[Phlox]: Disabling script {0} globally", script.ItemId);

                        if (wasEnabled)
                        {
                            this.AfterDisable(script, Types.ScriptUnloadReason.GloballyDisabled, VM.RuntimeState.LocalDisableFlag.None, StopScriptReason.None);
                        }
                    }
                    break;

                case EnableDisableFlag.ParcelDisable:
                    if ((script.ScriptState.LocalDisable & VM.RuntimeState.LocalDisableFlag.Parcel) == 0)
                    {
                        script.ScriptState.LocalDisable |= VM.RuntimeState.LocalDisableFlag.Parcel;
                        _log.InfoFormat("[Phlox]: Disabling script {0} locally", script.ItemId);

                        if (wasEnabled)
                        {
                            this.AfterDisable(script, Types.ScriptUnloadReason.LocallyDisabled, VM.RuntimeState.LocalDisableFlag.Parcel, StopScriptReason.None);
                        }
                    }
                    break;

                case EnableDisableFlag.CrossingWaitDisable:
                    if ((script.ScriptState.LocalDisable & VM.RuntimeState.LocalDisableFlag.CrossingWait) == 0)
                    {
                        script.ScriptState.LocalDisable |= VM.RuntimeState.LocalDisableFlag.CrossingWait;
                        //_log.InfoFormat("[Phlox]: Disabling script {0} locally (crossing wait)", script.ItemId);

                        if (wasEnabled)
                        {
                            this.AfterDisable(script, Types.ScriptUnloadReason.LocallyDisabled, VM.RuntimeState.LocalDisableFlag.CrossingWait, StopScriptReason.Crossing);
                        }
                    }
                    break;

                case EnableDisableFlag.DerezDisable:
                    if ((script.ScriptState.LocalDisable & VM.RuntimeState.LocalDisableFlag.CrossingWait) == 0)
                    {
                        //_log.InfoFormat("[Phlox]: Disabling script {0} locally (derez)", script.ItemId);
                        if (wasEnabled)
                        {
                            this.AfterDisable(script, Types.ScriptUnloadReason.LocallyDisabled, VM.RuntimeState.LocalDisableFlag.None, StopScriptReason.Derez);
                        }
                    }
                    break;
            }
        }

        private void EnableScript(VM.Interpreter script, EnableDisableFlag flag)
        {
            switch (flag)
            {
                case EnableDisableFlag.GeneralEnable:
                    if (script.ScriptState.GeneralEnable == false)
                    {
                        script.ScriptState.GeneralEnable = true;
                        _log.InfoFormat("[Phlox]: Enabling script {0} globally", script.ItemId);
                        this.InjectScript(script, false);
                    }
                    break;

                case EnableDisableFlag.ParcelEnable:
                    if ((script.ScriptState.LocalDisable & VM.RuntimeState.LocalDisableFlag.Parcel) != 0)
                    {
                        script.ScriptState.LocalDisable &= ~VM.RuntimeState.LocalDisableFlag.Parcel;
                        _log.InfoFormat("[Phlox]: Enabling script {0} locally", script.ItemId);
                        this.InjectScript(script, false);
                    }
                    break;

                case EnableDisableFlag.CrossingWaitEnable:
                    if ((script.ScriptState.LocalDisable & VM.RuntimeState.LocalDisableFlag.CrossingWait) != 0)
                    {
                        script.ScriptState.LocalDisable &= ~VM.RuntimeState.LocalDisableFlag.CrossingWait;
                        _log.InfoFormat("[Phlox]: Enabling script {0} locally (crossing wait)", script.ItemId);
                        this.InjectScript(script, false);
                    }
                    break;
            }
        }

        private bool DoEnableDisable()
        {
            PendingEnableDisable enableDisable;
            lock (_pendingEnableDisableRequests)
            {
                if (_pendingEnableDisableRequests.Count == 0)
                {
                    return false;
                }

                enableDisable = _pendingEnableDisableRequests.Dequeue();
            }

            VM.Interpreter script;
            if (_allScripts.TryGetValue(enableDisable.ItemId, out script))
            {
                switch (enableDisable.Flag)
                {
                    case EnableDisableFlag.GeneralEnable:
                    case EnableDisableFlag.ParcelEnable:
                    case EnableDisableFlag.CrossingWaitEnable:
                        this.EnableScript(script, enableDisable.Flag);
                        break;

                    case EnableDisableFlag.GeneralDisable:
                    case EnableDisableFlag.ParcelDisable:
                    case EnableDisableFlag.CrossingWaitDisable:
                        this.DisableScript(script, enableDisable.Flag);
                        break;
                }
                script.SetScriptEventFlags();
                return true;
            }
            else
            {
                _deferredEvents.AddEnableDisableEvent(enableDisable.ItemId, enableDisable.Flag);
            }

            return false;
        }

        internal VM.Interpreter FindScript(UUID itemId)
        {
            VM.Interpreter script;
            if (_allScripts.TryGetValue(itemId, out script))
            {
                return script;
            }

            return null;
        }

        private void RemoveFromRunQueue(UUID itemId)
        {
            LinkedListNode<VM.Interpreter> runningScript;

            if (_nextScheduledScript != null)
            {
                //this case, when the next script is being unloaded
                if (_nextScheduledScript.Value.ItemId == itemId)
                {
                    _nextScheduledScript = _nextScheduledScript.Next;
                    if (_nextScheduledScript == null)
                    {
                        _nextScheduledScript = _runQueue.First;
                        if (_nextScheduledScript != null && _nextScheduledScript.Value.ItemId == itemId)
                        {
                            //the unloaded script is the one and only script in the queue
                            _nextScheduledScript = null;
                        }
                    }
                }
            }

            if (_runningScriptsIndex.TryGetValue(itemId, out runningScript))
            {
                //remove from the runqueue
                _runningScriptsIndex.Remove(itemId);
                _runQueue.Remove(runningScript);
            }
        }

        /// <summary>
        /// Called by the script loader when an unload request has come through
        /// </summary>
        /// <param name="itemId"></param>
        internal void DoUnload(UUID itemId, LoadUnloadRequest unloadReq)
        {
            VM.Interpreter script;
            if (_allScripts.TryGetValue(itemId, out script))
            {
                this.RemoveFromRunQueue(itemId);

                UnregisterScriptFromNotifications(script);

                //tell the api we're unloading
                script.OnUnload(Types.ScriptUnloadReason.Unloaded, VM.RuntimeState.LocalDisableFlag.None);

                //finally remove from all scripts
                _allScripts.Remove(itemId);
            }
            else
            {
                _log.ErrorFormat("[Phlox]: Unmatched unload request for {0}", itemId);
            }
        }

        private void UnregisterScriptFromNotifications(VM.Interpreter script)
        {
            //also remove all other references
            C5.IPriorityQueueHandle<SleepingScript> sleepHandle;
            if (_stdSleepHandles.TryGetValue(script.ItemId, out sleepHandle))
            {
                _stdSleepHandles.Remove(script.ItemId);
                _sleepingScripts.Delete(sleepHandle);
            }

            if (_timerSleepHandles.TryGetValue(script.ItemId, out sleepHandle))
            {
                _timerSleepHandles.Remove(script.ItemId);
                _sleepingScripts.Delete(sleepHandle);
            }

            if (_touchSleepHandles.TryGetValue(script.ItemId, out sleepHandle))
            {
                _touchSleepHandles.Remove(script.ItemId);
                _sleepingScripts.Delete(sleepHandle);
            }

            //tell worldcomm it lost this script
            _worldComm.DeleteListener(script.ItemId);
        }

        /// <summary>
        /// Request for script information from a client
        /// </summary>
        /// <param name="scriptInfoRequest"></param>
        internal void PostScriptInfoRequest(ScriptInfoRequest scriptInfoRequest)
        {
            lock (_infoRequests)
            {
                _infoRequests.Enqueue(scriptInfoRequest);
            }

            _workArrived();
        }

        private bool ProcessScriptInfoRequest()
        {
            ScriptInfoRequest req;
            lock (_infoRequests)
            {
                if (_infoRequests.Count == 0)
                {
                    return false;
                }

                req = _infoRequests.Dequeue();
            }

            if (req.ReqType == ScriptInfoRequest.Type.ScriptRunningRequest)
            {
                VM.Interpreter script;
                if (_allScripts.TryGetValue(req.ItemId, out script))
                {
                    req.IsRunning = script.ScriptState.Enabled;
                }
                else
                {
                    req.IsRunning = false;
                }
            }
            else if (req.ReqType == ScriptInfoRequest.Type.ScriptEnabledDetailsRequest)
            {
                req.DetailedEnabledInfo = new List<Tuple<UUID, bool, VM.RuntimeState.LocalDisableFlag>>();
                foreach (UUID id in req.ScriptItemList)
                {
                    VM.Interpreter script;
                    if (_allScripts.TryGetValue(id, out script))
                    {
                        req.DetailedEnabledInfo.Add(new Tuple<UUID, bool, VM.RuntimeState.LocalDisableFlag>(id, script.ScriptState.GeneralEnable, script.ScriptState.LocalDisable));
                    }
                    else
                    {
                        req.DetailedEnabledInfo.Add(new Tuple<UUID, bool, VM.RuntimeState.LocalDisableFlag>(id, false, VM.RuntimeState.LocalDisableFlag.None));
                    }
                }
            }

            req.FireRetrievedCallBack();
            return true;
        }

        internal void ChangeEnabledStatus(UUID itemID, EnableDisableFlag flag)
        {
            lock (_pendingEnableDisableRequests)
            {
                _pendingEnableDisableRequests.Enqueue(new PendingEnableDisable { ItemId = itemID, Flag = flag });
            }

            _workArrived();
        }

        internal void ResetScript(UUID itemId)
        {
            lock (_pendingScriptResets)
            {
                _pendingScriptResets.Enqueue(itemId);
            }

            _workArrived();
        }

        private void ScriptSetTimer(VM.Interpreter script)
        {
            if (script.ScriptState.TimerInterval > 0)
            {
                UInt64 tickCountNow = OpenSim.Framework.Util.GetLongTickCount();
                UInt64 readyOn = tickCountNow + (UInt64)script.ScriptState.TimerInterval;

                C5.IPriorityQueueHandle<SleepingScript> existingHandle;
                if (_timerSleepHandles.TryGetValue(script.ItemId, out existingHandle))
                {
                    _sleepingScripts.Replace(existingHandle,
                        new SleepingScript
                        {
                            ItemId = script.ItemId,
                            EventToPost = SleepingScript.WakingEvent.Timer,
                            ReadyOn = readyOn
                        });

                    script.ScriptState.TimerLastScheduledOn = tickCountNow;
                }
                else
                {
                    this.SetAndTrackTimer(script, readyOn, false);
                }
            }
            else
            {
                C5.IPriorityQueueHandle<SleepingScript> existingHandle;
                if (_timerSleepHandles.TryGetValue(script.ItemId, out existingHandle))
                {
                    _sleepingScripts.Delete(existingHandle);
                    _timerSleepHandles.Remove(script.ItemId);
                }
                else
                {
                    //if it wasnt in the sleep handles it might be in the script queue
                    script.ScriptState.RemovePendingTimerEvent();
                }
            }
        }

        /// <summary>
        /// Called when llSetTimerEvent is called. Replaces or sets a new timer
        /// </summary>
        /// <param name="itemID"></param>
        /// <param name="sec"></param>
        internal void SetTimer(UUID itemID, float sec)
        {
            VM.Interpreter script;
            if (_allScripts.TryGetValue(itemID, out script))
            {
                script.ScriptState.TimerInterval = (int)(sec * 1000);
                this.ScriptSetTimer(script);
            }
        }

        internal void PostSyscallReturn(UUID itemId, object retValue, int delay)
        {
            SyscallReturn sysReturn = new SyscallReturn { ItemId = itemId, ReturnValue = retValue, Delay = delay };
            lock (_pendingSyscallReturns)
            {
                _pendingSyscallReturns.Enqueue(sysReturn);
            }

            _workArrived();
        }

        internal void Stop()
        {
            //intentionally left blank
        }

        internal void QueueCrossedAvatarReady(UUID uuid, UUID avatarId)
        {
            lock (_scriptsAwaitingDeferredEvents)
            {
                _scriptsAwaitingDeferredEvents.Add(Tuple.Create(uuid, avatarId));
            }

            _workArrived();
        }

        private void ProcessScriptsAwaitingDeferredEvents()
        {
            List<Tuple<UUID,UUID>> waitingScripts;
            lock (_scriptsAwaitingDeferredEvents)
            {
                if (_scriptsAwaitingDeferredEvents.Count == 0) return;

                waitingScripts = new List<Tuple<UUID,UUID>>(_scriptsAwaitingDeferredEvents);
                _scriptsAwaitingDeferredEvents.Clear();
            }

            foreach (var scriptIdUserId in waitingScripts)
            {
                VM.Interpreter script;
                if (_allScripts.TryGetValue(scriptIdUserId.Item1, out script))
                {
                    script.OnGroupCrossedAvatarReady(scriptIdUserId.Item2);
                }
                else
                {
                    //defer this message
                    _deferredEvents.AddGroupCrossedAvatarsReadyEvent(scriptIdUserId);
                }
            }
        }

        internal void UpdateTouchData(UUID itemId, VM.DetectVariables[] det)
        {
            lock (_pendingTouchInfoUpdates)
            {
                _pendingTouchInfoUpdates[itemId] = det;
            }
        }

        public void QueueCommand(PendingCommand cmd)
        {
            lock (_pendingCommands)
            {
                _pendingCommands.Enqueue(cmd);
            }

            _workArrived();
        }

        public void RunPendingCommands()
        {
            List<PendingCommand> waitingComamnds;
            lock (_pendingCommands)
            {
                if (_pendingCommands.Count == 0) return;

                waitingComamnds = new List<PendingCommand>(_pendingCommands);
                _pendingCommands.Clear();
            }

            foreach (PendingCommand cmd in waitingComamnds)
            {
                if (cmd.CommandType == PendingCommand.PCType.StopAllTraces)
                {
                    foreach (VM.Interpreter script in _allScripts.Values)
                    {
                        script.TraceExecution = false;
                    }
                }
            }
        }
    }
}
