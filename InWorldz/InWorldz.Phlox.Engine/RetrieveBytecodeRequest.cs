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
using System.Threading;

namespace Halcyon.Phlox.Engine
{
    /// <summary>
    /// A request to the script loader to serialize a copy of the compiled
    /// script that it may have loaded for the given asset ids
    /// </summary>
    internal class RetrieveBytecodeRequest
    {
        /// <summary>
        /// An enumerable specifying the script IDs for which we would like to get a serialized
        /// copy of the compiled script with bytecode data
        /// </summary>
        public IEnumerable<UUID> ScriptIds;

        /// <summary>
        /// A collection of serialized scripts we were able to retrieve
        /// </summary>
        public Dictionary<UUID, byte[]> Bytecodes;

        /// <summary>
        /// Note that we're letting this object's finalizer Dispose() the instance. In the case of a 
        /// timeout we wont have any other choice
        /// </summary>
        private ManualResetEventSlim _signalEvent = new ManualResetEventSlim();

        /// <summary>
        /// Signals that the serialized data is available
        /// </summary>
        internal void SignalDataReady()
        {
            _signalEvent.Set();
        }

        /// <summary>
        /// Waits for the serialized data to become available
        /// </summary>
        /// <param name="timeout">Timeout in millis</param>
        internal bool WaitForData(int timeout)
        {
            return _signalEvent.Wait(timeout);
        }
    }
}
