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

namespace Halcyon.Phlox.Engine
{
    internal class PhloxConstants
    {
        /// <summary>
        /// Where compiles scripts are stored locally
        /// </summary>
        public const string COMPILE_CACHE_DIR = "./ScriptEngines/Phlox/cache";

        /// <summary>
        /// Where the states for each script in each item are stored. This is stored under the itemid /states/{itemid}.state
        /// </summary>
        public const string STATE_SAVE_DIR = "./ScriptEngines/Phlox/states";

        /// <summary>
        /// How many characters of an item or asset id are used to create subfolders for cached scripts
        /// </summary>
        public const int CACHE_PREFIX_LEN = 3;

        /// <summary>
        /// The file extension for a compiled script
        /// </summary>
        public const string COMPILED_SCRIPT_EXTENSION = ".plx";

        /// <summary>
        /// The file extension for saved state data
        /// </summary>
        public const string STATE_DATA_EXTENSION = ".state";
    }
}
