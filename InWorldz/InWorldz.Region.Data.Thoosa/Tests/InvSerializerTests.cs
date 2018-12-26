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
using NUnit.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenMetaverse;
using KellermanSoftware.CompareNetObjects;
using OpenSim.Framework;

namespace InWorldz.Region.Data.Thoosa.Tests
{
    [TestFixture]
    class InvSerializerTests
    {
        private Engines.SerializationEngine serEngine;

        private readonly List<string> PrimCompareIgnoreList = new List<string> { "ParentGroup", "FullUpdateCounter", "TerseUpdateCounter", 
                "TimeStamp", "SerializedVelocity", "InventorySerial", "Rezzed", "SculptData" };

        [SetUp]
        public void Setup()
        {
            serEngine = new Engines.SerializationEngine();
        }

        [Test]
        public void TestGroupInventorySerializationDeserialization()
        {
            var sop1 = Util.RandomSOP("Root", 1);
            var sop2 = Util.RandomSOP("Child1", 2);
            var sop3 = Util.RandomSOP("Child2", 3);

            SceneObjectGroup group = new SceneObjectGroup(sop1);
            group.AddPart(sop2);
            group.AddPart(sop3);

            var grpBytes = serEngine.InventoryObjectSerializer.SerializeGroupToInventoryBytes(group, SerializationFlags.None);

            SceneObjectGroup deserGroup = serEngine.InventoryObjectSerializer.DeserializeGroupFromInventoryBytes(grpBytes);

            CompareObjects comp = new CompareObjects();
            comp.CompareStaticFields = false;
            comp.CompareStaticProperties = false;
            comp.ElementsToIgnore = PrimCompareIgnoreList;

            Assert.IsTrue(comp.Compare(group, deserGroup), comp.DifferencesString);
        }

        [Test]
        public void TestCoalescedInventorySerializationDeserialization()
        {
            var sop1 = Util.RandomSOP("Root", 1);
            var sop2 = Util.RandomSOP("Child1", 2);
            var sop3 = Util.RandomSOP("Child2", 3);

            SceneObjectGroup group = new SceneObjectGroup(sop1);
            group.AddPart(sop2);
            group.AddPart(sop3);


            var sop4 = Util.RandomSOP("Root2", 1);
            var sop5 = Util.RandomSOP("Child12", 2);
            var sop6 = Util.RandomSOP("Child22", 3);

            SceneObjectGroup group2 = new SceneObjectGroup(sop4);
            group2.AddPart(sop5);
            group2.AddPart(sop6);

            var gp1perms = group.GetNewItemPermissions(UUID.Random());
            var gp2perms = group2.GetNewItemPermissions(UUID.Random());

            var perms = new Dictionary<UUID, ItemPermissionBlock>();
            perms[group.UUID] = gp1perms;
            perms[group2.UUID] = gp2perms;

            CoalescedObject cobj = new CoalescedObject(
                new List<SceneObjectGroup> { group, group2 },
                perms
                );

            var colbytes = serEngine.InventoryObjectSerializer.SerializeCoalescedObjToInventoryBytes(cobj, SerializationFlags.None);

            var deserColObj = serEngine.InventoryObjectSerializer.DeserializeCoalescedObjFromInventoryBytes(colbytes);

            CompareObjects comp = new CompareObjects();
            comp.CompareStaticFields = false;
            comp.CompareStaticProperties = false;
            comp.ElementsToIgnore = PrimCompareIgnoreList;

            Assert.IsTrue(comp.Compare(cobj, deserColObj), comp.DifferencesString);
        }

        [Test]
        public void TestBadHeaderContent()
        {
            byte[] badHeader = new byte[] { 0x1, 0x2, 0x3, 0x4 };
            Assert.IsFalse(serEngine.InventoryObjectSerializer.CanDeserialize(badHeader));
            Assert.Throws<ArgumentException>(() => { serEngine.InventoryObjectSerializer.DeserializeGroupFromInventoryBytes(badHeader); });
        }

        [Test]
        public void TestBadHeaderLength()
        {
            byte[] badHeader = new byte[3];
            Assert.IsFalse(serEngine.InventoryObjectSerializer.CanDeserialize(badHeader));
            Assert.Throws<ArgumentException>(() => { serEngine.InventoryObjectSerializer.DeserializeGroupFromInventoryBytes(badHeader); });
        }
    }
}
