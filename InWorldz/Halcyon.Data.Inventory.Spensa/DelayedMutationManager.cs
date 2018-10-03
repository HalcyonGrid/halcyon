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

namespace Halcyon.Data.Inventory.Spensa
{
    /// <summary>
    /// Manages retries for batches of mutations. This is to conver cases of transient down time
    /// </summary>
    internal class DelayedMutationManager
    {
        private const int WORK_LOOP_SLEEP_TIME = 15 * 1000;
        private const int MAX_RETRIES = 4;

        private static readonly TimeSpan[] RETRY_DELAYS = new TimeSpan[] {
            TimeSpan.FromMinutes(1.0),
            TimeSpan.FromMinutes(5.0),
            TimeSpan.FromMinutes(20.0),
            TimeSpan.FromMinutes(60.0)
        };

        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private C5.IntervalHeap<DelayedMutation> _delayedMutations = new C5.IntervalHeap<DelayedMutation>();

        private Thread _executionThread;
        private volatile bool _stop = true;

        public DelayedMutationManager()
        {

        }

        /// <summary>
        /// Starts the thread that manages this mutation manager
        /// </summary>
        public void Start()
        {
            if (_executionThread == null)
            {
                _stop = false;
                _executionThread = new Thread(WorkLoop);
                _executionThread.Start();
            }
        }

        /// <summary>
        /// Stops the mutation manager thread and waits for it to exit
        /// </summary>
        public void Stop()
        {
            if (!_stop)
            {
                _stop = true;
                _executionThread.Join();
            }
        }

        public void WorkLoop()
        {
            while (!_stop)
            {
                CheckForReadyRetries();
                Thread.Sleep(WORK_LOOP_SLEEP_TIME);
            }
        }

        /// <summary>
        /// Checks for retries that are ready to be applied
        /// </summary>
        private void CheckForReadyRetries()
        {
            List<DelayedMutation> needsRetry = new List<DelayedMutation>();

            while (_delayedMutations.Count > 0)
            {
                DelayedMutation mut;
                lock (_delayedMutations)
                {
                    mut = _delayedMutations.FindMin();
                    if (mut.ReadyOn > DateTime.Now) break;

                    _delayedMutations.DeleteMin();
                }

                try
                {
                    mut.Execute();
                }
                catch (Exception e)
                {
                    _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa] Error while applying mutation {0} on retry {1}: {2}", 
                        mut.Identifier, mut.RetryCount + 1, e);

                    mut.RetryCount++;

                    if (mut.RetryCount == MAX_RETRIES)
                    {
                        _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa] CRITICAL: Retry limit reached, discarding mutation {0}",
                            mut.Identifier);
                    }
                    else
                    {
                        needsRetry.Add(mut);
                    }
                }
            }

            foreach (DelayedMutation delayedMutation in needsRetry)
            {
                ScheduleMutationRetry(delayedMutation);
            }
        }

        /// <summary>
        /// Schedules the given mutation to be re-executed at a time in the future based on its retry count
        /// </summary>
        /// <param name="mut">The mutation to reschedule</param>
        private void ScheduleMutationRetry(DelayedMutation mut)
        {
            mut.ReadyOn = DateTime.Now + RETRY_DELAYS[mut.RetryCount];
            _log.InfoFormat("[Halcyon.Data.Inventory.Spensa] Mutation {0} will be retried at {1}", mut.Identifier, mut.ReadyOn);

            lock (_delayedMutations)
            {
                _delayedMutations.Add(mut);
            }
        }

        /// <summary>
        /// Adds a mutation to be retried by this manager
        /// </summary>
        /// <param name="retryDelegate"></param>
        /// <param name="identifier"></param>
        public void AddMutationForRetry(DelayedMutation.DelayedMutationDelegate retryDelegate, string identifier)
        {
            this.ScheduleMutationRetry(new DelayedMutation(retryDelegate, identifier)); 
        }
    }
}
