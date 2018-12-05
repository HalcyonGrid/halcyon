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

namespace Halcyon.Phlox.Engine
{
    /// <summary>
    /// A submitted request for script information
    /// </summary>
    internal class ScriptInfoRequest
    {
        public delegate void InfoRetrieved(ScriptInfoRequest req);
        private InfoRetrieved _retrievedCallback;
        
        public enum Type
        {
            ScriptRunningRequest,
            ScriptEnabledDetailsRequest
        }

        private UUID _itemId;
        public UUID ItemId
        {
            get
            {
                return _itemId;
            }
        }

        private Type _reqType;
        public Type ReqType
        {
            get
            {
                return _reqType;
            }
        }

        private bool _isRunning;
        public bool IsRunning
        {
            get
            {
                return _isRunning;
            }

            set
            {
                _isRunning = value;
            }
        }

        public List<Tuple<UUID, bool, VM.RuntimeState.LocalDisableFlag>> DetailedEnabledInfo { get; set; }

        private IEnumerable<UUID> _scriptItemsToCheck;
        public IEnumerable<UUID> ScriptItemList
        {
            get { return _scriptItemsToCheck; }
        }

        public ScriptInfoRequest(UUID itemId, Type reqType, InfoRetrieved retrievedCallback)
        {
            _itemId = itemId;
            _reqType = reqType;
            _retrievedCallback = retrievedCallback;
        }

        public ScriptInfoRequest(Type reqType, IEnumerable<UUID> scriptItemIds, InfoRetrieved retrievedCallback)
        {
            _reqType = reqType;
            _retrievedCallback = retrievedCallback;
            _scriptItemsToCheck = scriptItemIds;
        }

        public void FireRetrievedCallBack()
        {
            _retrievedCallback(this);
        }
    }
}
