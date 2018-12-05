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
using System.Reflection;
using System.Text;
using System.Threading;
using log4net;
using OpenMetaverse;
using Halcyon.Phlox.Serialization;
using OpenSim.Region.Framework.Interfaces;

namespace Halcyon.Phlox.Engine
{
    /// <summary>
    /// A request for state data from a running script
    /// </summary>
    public class StateDataRequest
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public delegate void StateDataReadyDelegate(StateDataRequest req);

        private ManualResetEventSlim _dataReady;
        private StateDataReadyDelegate _dataReadyCallback;

        private UUID _itemId;
        public UUID ItemId
        {
            get
            {
                return _itemId;
            }

            set
            {
                _itemId = value;
            }
        }

        private byte[] _stateData;
        public byte[] SerializedStateData
        {
            get
            {
                return _stateData;
            }

            set
            {
                _stateData = value;
            }
        }

        /// <summary>
        /// The raw state data from the script engine 
        /// (acually is a SerializedRuntimeState object)
        /// made opaque to break circular refs
        /// </summary>
        private object _rawStateData;
        public object RawStateData
        {
            get
            {
                return _rawStateData;
            }

            set
            {
                _rawStateData = value;
            }
        }

        private bool _preSerialize;
        public bool PreSerialize
        {
            get
            {
                return _preSerialize;
            }
        }

        public VM.RuntimeState.LocalDisableFlag CurrentLocalEnableState { get; set; }

        /// <summary>
        /// Whether or not to disable the script after collecting the
        /// script state to prevent the state from changing
        /// </summary>
        private StopScriptReason _disableScriptReason;
        public StopScriptReason DisableScriptReason
        {
            get
            {
                return _disableScriptReason;
            }

            set
            {
                _disableScriptReason = value;
            }
        }

        /// <summary>
        /// When this request was created. Used to expire syscalls that failed to return
        /// for any reason
        /// </summary>
        private DateTime _requestIssuedOn = DateTime.Now;
        public DateTime TimeIssued
        {
            get
            {
                return _requestIssuedOn;
            }
        }

        public StateDataRequest()
        {
        }

        public StateDataRequest(UUID itemId, bool preSerialize)
        {
            _itemId = itemId;
            _preSerialize = preSerialize;
            _dataReady = new ManualResetEventSlim(false);
        }

        public StateDataRequest(UUID itemId, StateDataReadyDelegate dataReadyCallback)
        {
            _itemId = itemId;
            _preSerialize = false;
            _dataReadyCallback = dataReadyCallback;
        }

        public bool WaitForData(int milliTimeout)
        {
            if (_dataReady.Wait(milliTimeout))
            {
                _dataReady.Dispose();
                _dataReady = null;
                return true;
            }
            else
            {
                return false;
            }
        }

        public void TriggerDataReady()
        {
            if (_dataReady != null)
            {
                try
                {
                    _dataReady.Set();
                }
                catch (Exception)
                {
                    //have seen System.IO.IOException: The handle is invalid 
                    //this was happening before we were manually disposing _dataReady so something
                    //is odd here
                }
            }
            else
            {
                if (_dataReadyCallback != null)
                {
                    _dataReadyCallback(this);
                }
                else
                {
                    _log.Error("[Halcyon.Phlox]: StateDataRequest.TriggerDataReady() called, but no callback or event could be fired");
                }
            }
        }
    }
}
