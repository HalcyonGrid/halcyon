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
using System.IO;
using System.Linq;
using System.Text;
using OpenSim.Region.Framework.Scenes.Serialization;

namespace InWorldz.Region.Data.Thoosa.Engines
{
    /// <summary>
    /// Engine that is capable of serializing objects in a format that is appropriate for inventory storage
    /// </summary>
    public class InventoryObjectSerializer : IInventoryObjectSerializer
    {
        /// <summary>
        /// The byte length of inventory headers
        /// </summary>
        private const int HEADER_LEN = 4;

        /// <summary>
        /// Magic header that is expected at the beginning of the bytestream to identify the serialized data
        /// as being created by Thoosa. The header is (ASCII) "TIG[0x1]" (Thoosa Inventory Group 1). 
        /// The major version "1" would only be changed if Thoosa discontinued use of protobuf in the future
        /// </summary>
        private readonly byte[] GROUP_HEADER = new byte[HEADER_LEN] { 0x49, 0x54, 0x47, 0x1 };

        /// <summary>
        /// Magic header that is expected at the beginning of the bytestream to identify the serialized data
        /// as being created by Thoosa. The header is (ASCII) "TIC[0x1]" (Thoosa Inventory Coalesced 1). 
        /// The major version "1" would only be changed if Thoosa discontinued use of protobuf in the future
        /// </summary>
        private readonly byte[] COALESCED_HEADER = new byte[HEADER_LEN] { 0x49, 0x54, 0x43, 0x1 };

        /// <summary>
        /// Flag indicating how the header check function should behave
        /// </summary>
        [Flags]
        private enum HeaderTestFlag
        {
            None = 0,
            ThrowOnFailedCheck = (1 << 0),
            CheckValidGroup = (1 << 1),
            CheckValidCoalesced = (1 << 2),
            CheckValidEither = (1 << 3)
        }

        /// <summary>
        /// Serializes a group into a byte array suitable for storage and retrieval from inventory
        /// </summary>
        /// <param name="group"></param>
        /// <returns></returns>
        public byte[] SerializeGroupToInventoryBytes(OpenSim.Region.Framework.Scenes.SceneObjectGroup group, SerializationFlags flags)
        {
            Serialization.SceneObjectGroupSnapshot snap = Serialization.SceneObjectGroupSnapshot.FromSceneObjectGroup(group, flags);

            using (MemoryStream ms = new MemoryStream())
            {
                ms.Write(GROUP_HEADER, 0, GROUP_HEADER.Length);
                snap.SerializeToStream(ms);

                return ms.ToArray();
            }
        }

        /// <summary>
        /// Attempts to extract a SceneObjectGroup from the given byte array
        /// </summary>
        /// <param name="bytes"></param>
        /// <returns></returns>
        public OpenSim.Region.Framework.Scenes.SceneObjectGroup DeserializeGroupFromInventoryBytes(byte[] bytes)
        {
            CheckHeader(bytes, HeaderTestFlag.CheckValidGroup | HeaderTestFlag.ThrowOnFailedCheck);

            //we should be able to proceed
            Serialization.SceneObjectGroupSnapshot snap = Serialization.SceneObjectGroupSnapshot.FromBytes(bytes, GROUP_HEADER.Length);

            return snap.ToSceneObjectGroup();
        }

        public byte[] SerializeCoalescedObjToInventoryBytes(OpenSim.Region.Framework.Scenes.CoalescedObject csog, SerializationFlags flags)
        {
            Serialization.CoalescedObjectSnapshot snap = Serialization.CoalescedObjectSnapshot.FromCoalescedObject(csog, flags);

            using (MemoryStream ms = new MemoryStream())
            {
                ms.Write(COALESCED_HEADER, 0, COALESCED_HEADER.Length);
                snap.SerializeToStream(ms);

                return ms.ToArray();
            }
        }

        public OpenSim.Region.Framework.Scenes.CoalescedObject DeserializeCoalescedObjFromInventoryBytes(byte[] bytes)
        {
            this.CheckHeader(bytes, HeaderTestFlag.CheckValidCoalesced | HeaderTestFlag.ThrowOnFailedCheck);

            //we should be able to proceed
            Serialization.CoalescedObjectSnapshot snap = Serialization.CoalescedObjectSnapshot.FromBytes(bytes, GROUP_HEADER.Length);

            return snap.ToCoalescedObject();
        }

        public bool CanDeserialize(byte[] bytes)
        {
            return CheckHeader(bytes, HeaderTestFlag.CheckValidEither);
        }

        /// <summary>
        /// Returns whether or not this asset data represents a coalesced object
        /// </summary>
        /// <param name="bytes">A serialized object stream</param>
        /// <returns></returns>
        public bool IsValidCoalesced(byte[] bytes)
        {
            return CheckHeader(bytes, HeaderTestFlag.CheckValidCoalesced);
        }

        /// <summary>
        /// Returns whether or not this asset data represents a single scene object group
        /// </summary>
        /// <param name="bytes">A serialized object stream</param>
        /// <returns></returns>
        public bool IsValidGroup(byte[] bytes)
        {
            return CheckHeader(bytes, HeaderTestFlag.CheckValidGroup);
        }

        /// <summary>
        /// Checks the given bytes to make sure the header is valid
        /// </summary>
        /// <param name="bytes"></param>
        /// <param name="flag"></param>
        /// <returns></returns>
        private bool CheckHeader(byte[] bytes, HeaderTestFlag flag)
        {
            bool hasBadGroupHeaderContent = false;
            bool hasBadCoalescedHeaderContent = false;

            if (bytes.Length < HEADER_LEN)
            {
                //missing the header
                if ((flag & HeaderTestFlag.ThrowOnFailedCheck) != 0)
                {
                    throw new ArgumentException("Given byte array is too short to be a serialized SOG/CSOG");
                }
                else
                {
                    return false;
                }
            }

            if ((flag & HeaderTestFlag.CheckValidGroup) != 0 || (flag & HeaderTestFlag.CheckValidEither) != 0)
            {
                //test group header
                if (!Enumerable.SequenceEqual(GROUP_HEADER, bytes.Take(HEADER_LEN)))
                {
                    hasBadGroupHeaderContent = true;
                }
            }

            if ((flag & HeaderTestFlag.CheckValidCoalesced) != 0 || (flag & HeaderTestFlag.CheckValidEither) != 0)
            {
                //test coalesced header
                if (!Enumerable.SequenceEqual(COALESCED_HEADER, bytes.Take(HEADER_LEN)))
                {
                    hasBadCoalescedHeaderContent = true;
                }
            }

            if ((flag & HeaderTestFlag.CheckValidGroup) != 0)
            {
                if (hasBadGroupHeaderContent)
                {
                    if ((flag & HeaderTestFlag.ThrowOnFailedCheck) != 0)
                    {
                        throw new ArgumentException("Given bytes do not contain the correct header to be a serialized SOG");
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            if ((flag & HeaderTestFlag.CheckValidCoalesced) != 0)
            {
                if (hasBadCoalescedHeaderContent)
                {
                    if ((flag & HeaderTestFlag.ThrowOnFailedCheck) != 0)
                    {
                        throw new ArgumentException("Given bytes do not contain the correct header to be a serialized CSOG");
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            if ((flag & HeaderTestFlag.CheckValidEither) != 0)
            {
                if (hasBadGroupHeaderContent && hasBadCoalescedHeaderContent)
                {
                    if ((flag & HeaderTestFlag.ThrowOnFailedCheck) != 0)
                    {
                        throw new ArgumentException("Given bytes do not contain the correct header to be a serialized SOG/CSOG");
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
