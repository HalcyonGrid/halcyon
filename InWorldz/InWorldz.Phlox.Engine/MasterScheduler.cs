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

using System.Threading;
using log4net;
using System.Reflection;

namespace Halcyon.Phlox.Engine
{
    /// <summary>
    /// The scheduler ticks the DoWork methods on the loader and the ExecutionScheduler
    /// </summary>
    internal class MasterScheduler
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private ExecutionScheduler _exeScheduler;
        private ScriptLoader _scriptLoader;
        private StateManager _stateManager;
        private ManualResetEvent _actionEvent = new ManualResetEvent(false);
        private Thread _executionThread;
        

        private volatile bool _stop = false;
        private bool _terminated = false;

        public bool IsRunning
        {
            get
            {
                return !_stop;
            }
        }

        public bool IsTerminated
        {
            get
            {
                return _terminated;
            }
        }

        public MasterScheduler(ExecutionScheduler exeScheduler, ScriptLoader scriptLoader, StateManager stateManger)
        {
            _exeScheduler = exeScheduler;
            _scriptLoader = scriptLoader;
            _stateManager = stateManger;
        }

        /// <summary>
        /// Starts the script engine thread. This thread schedules all subwork for the 
        /// rest of the engine
        /// </summary>
        public void Start()
        {
            if (_executionThread == null)
            {
                _executionThread = new Thread(WorkLoop);
                _executionThread.Priority = EngineInterface.SUBTASK_PRIORITY;
                _executionThread.Start();
            }

            _stateManager.Start();
        }

        /// <summary>
        /// Stops the execution thread and tells the subprocesses we're stopping
        /// </summary>
        public void Stop()
        {
            if (_executionThread != null && _executionThread.IsAlive)
            {
                _stop = true;
                

                _scriptLoader.Stop();
                _exeScheduler.Stop();

                WorkArrived();
                _executionThread.Join();

                //have the state manager do a complete backup
                _stateManager.Stop();
            }
        }

        /// <summary>
        /// The primary loop of the script engine
        /// </summary>
        private void WorkLoop()
        {
            try
            {
                while (!_stop)
                {
                    WorkStatus exeWorkStatus = _exeScheduler.DoWork();
                    WorkStatus loadWorkStatus = _scriptLoader.DoWork();

                    if (!exeWorkStatus.WorkIsPending && !loadWorkStatus.WorkIsPending)
                    {
                        //we have no work to do, wait for the earliest available work
                        UInt64 minDate = exeWorkStatus.NextWakeUpTime;
                        if (loadWorkStatus.NextWakeUpTime < minDate)
                        {
                            minDate = loadWorkStatus.NextWakeUpTime;
                        }

                        if (minDate != UInt64.MaxValue)
                        {
                            Int64 waitSpan = (Int64)minDate - (Int64)OpenSim.Framework.Util.GetLongTickCount();
                            if (waitSpan > 0)
                            {
                                if (waitSpan > int.MaxValue)
                                {
                                    waitSpan = int.MaxValue;
                                }

                                _actionEvent.WaitOne((int)waitSpan);
                                _actionEvent.Reset();
                            }
                        }
                        else
                        {
                            //no work to be done, wait forever
                            _actionEvent.WaitOne();
                            _actionEvent.Reset();
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Phlox]: CRITICAL ERROR. The master scheduler has caught an exception " +
                    "from the execution environment. This indicates a bug in the script engine. The script engine " +
                    "has been disabled. {0}", e);

                _terminated = true;
                this.Stop();
            }
        }

        /// <summary>
        /// Signals to the scheduler that work has arrived
        /// </summary>
        public void WorkArrived()
        {
            _actionEvent.Set();
        }


    }
}
