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
using System.Text;
using System.Xml;
using OpenMetaverse;
using System.Xml.Serialization;

namespace OpenSim.Framework
{
    public class ItemPermissionBlock
    {
        private static XmlSerializer serializer = new XmlSerializer(typeof(ItemPermissionBlock));

        public UUID ItemId;
        public uint BasePermissions;
        public uint NextPermissions;
        public uint EveryonePermissions;
        public uint GroupPermissions;
        public uint CurrentPermissions;

        public ItemPermissionBlock()
        {

        }

        public void ApplyToOther(InventoryItemBase other)
        {
            other.BasePermissions = this.BasePermissions;
            other.NextPermissions = this.NextPermissions;
            other.EveryonePermissions = this.EveryonePermissions;
            other.GroupPermissions = this.GroupPermissions;
            other.CurrentPermissions = this.CurrentPermissions;
        }

        public void ToXml(XmlWriter writer)
        {
            serializer.Serialize(writer, this);
        }

        public static ItemPermissionBlock FromXml(XmlReader xmlReader)
        {
            ItemPermissionBlock perm = (ItemPermissionBlock)serializer.Deserialize(xmlReader);
            return perm;
        }

        public static ItemPermissionBlock CalculateCoalescedPermissions(IEnumerable<ItemPermissionBlock> allPermissions)
        {
            // Include support for the Export permission.
            ItemPermissionBlock resultPermissions = new ItemPermissionBlock();
            resultPermissions.BasePermissions = (uint)(PermissionMask.All | PermissionMask.Export);
            resultPermissions.CurrentPermissions = (uint)(PermissionMask.All | PermissionMask.Export);
            resultPermissions.GroupPermissions = (uint)PermissionMask.All;
            resultPermissions.EveryonePermissions = (uint)(PermissionMask.All | PermissionMask.Export);
            resultPermissions.NextPermissions = (uint)(PermissionMask.All | PermissionMask.Export);

            //we want to grab the minimum permissions for each object for each permission class
            foreach (ItemPermissionBlock permBlock in allPermissions)
            {
                resultPermissions.BasePermissions &= permBlock.BasePermissions;
                resultPermissions.CurrentPermissions &= permBlock.CurrentPermissions;
                resultPermissions.GroupPermissions &= permBlock.GroupPermissions;
                resultPermissions.EveryonePermissions &= permBlock.EveryonePermissions;
                resultPermissions.NextPermissions &= permBlock.NextPermissions;
            }

            // Now don't let any of these exceed the calculated BasePermissions.
            resultPermissions.CurrentPermissions &= resultPermissions.BasePermissions;
            resultPermissions.GroupPermissions &= resultPermissions.BasePermissions;
            resultPermissions.EveryonePermissions &= resultPermissions.BasePermissions;
            resultPermissions.NextPermissions &= resultPermissions.BasePermissions;

            return resultPermissions;
        }

        public static ItemPermissionBlock FromOther(IInventoryItem other)
        {
            ItemPermissionBlock newBlock = new ItemPermissionBlock();
            newBlock.BasePermissions = other.BasePermissions;
            newBlock.NextPermissions = other.NextPermissions;
            newBlock.EveryonePermissions = other.EveryonePermissions;
            newBlock.GroupPermissions = other.GroupPermissions;
            newBlock.CurrentPermissions = other.CurrentPermissions;

            return newBlock;
        }
    }
}
