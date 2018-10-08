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
using ProtoBuf;

namespace InWorldz.Region.Data.Thoosa.Serialization
{
    /// <summary>
    /// Snashot of an item permissions block for a coalesced object
    /// </summary>
    [ProtoContract]
    public class ItemPermissionBlockSnapshot
    {
        [ProtoMember(1)]
        public uint BasePermissions;

        [ProtoMember(2)]
        public uint NextPermissions;

        [ProtoMember(3)]
        public uint EveryonePermissions;

        [ProtoMember(4)]
        public uint GroupPermissions;

        [ProtoMember(5)]
        public uint CurrentPermissions;

        internal static ItemPermissionBlockSnapshot FromItemPermissionBlock(OpenSim.Framework.ItemPermissionBlock itemPermissionBlock)
        {
            return new ItemPermissionBlockSnapshot
            {
                BasePermissions = itemPermissionBlock.BasePermissions,
                NextPermissions = itemPermissionBlock.NextPermissions,
                EveryonePermissions = itemPermissionBlock.EveryonePermissions,
                GroupPermissions = itemPermissionBlock.GroupPermissions,
                CurrentPermissions = itemPermissionBlock.CurrentPermissions
            };
        }

        internal OpenSim.Framework.ItemPermissionBlock ToItemPermissionBlock(OpenMetaverse.UUID itemId)
        {
            return new OpenSim.Framework.ItemPermissionBlock
            {
                BasePermissions = this.BasePermissions,
                CurrentPermissions = this.CurrentPermissions,
                EveryonePermissions = this.EveryonePermissions,
                GroupPermissions = this.GroupPermissions,
                ItemId = itemId,
                NextPermissions = this.NextPermissions
            };
        }
    }
}
