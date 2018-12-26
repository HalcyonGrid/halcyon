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
    public class SerializerTests
    {
        private Engines.SerializationEngine serEngine;

        private readonly List<string> PrimCompareIgnoreList = new List<string> { "ParentGroup", "FullUpdateCounter", "TerseUpdateCounter", 
                "TimeStamp", "SerializedVelocity", "InventorySerial", "Rezzed" };

        [SetUp]
        public void Setup()
        {
            serEngine = new Engines.SerializationEngine();
        }

        [Test]
        public void TestPartSerialzationDeserialization()
        {
            SceneObjectPart rootPart = new SceneObjectPart(UUID.Zero, new OpenSim.Framework.PrimitiveBaseShape(), new Vector3(1, 2, 3), Quaternion.Identity, Vector3.Zero, false);
            rootPart.Name = "RootPart";
            SceneObjectPart part = Util.RandomSOP("ChildPart", 2);

            var pgrp1 = new SceneObjectGroup(rootPart);
            pgrp1.AddPart(part);

            part.InventorySerial = 1;
            part.Rezzed = DateTime.Now;
            part.TextColor = System.Drawing.Color.FromArgb(1,2,3,4);
            
            byte[] bytes = serEngine.SceneObjectSerializer.SerializePartToBytes(part, OpenSim.Region.Framework.Scenes.Serialization.SerializationFlags.None);

            SceneObjectPart rootPart2 = new SceneObjectPart(UUID.Zero, new OpenSim.Framework.PrimitiveBaseShape(), new Vector3(1, 2, 3), Quaternion.Identity, Vector3.Zero, false);
            rootPart2.Name = "RootPart2";
            SceneObjectPart deserPart = serEngine.SceneObjectSerializer.DeserializePartFromBytes(bytes);
            var pgrp2 = new SceneObjectGroup(rootPart2);
            pgrp2.AddPart(deserPart);
            deserPart.Rezzed = part.Rezzed;

            CompareObjects comp = new CompareObjects();
            comp.CompareStaticFields = false;
            comp.CompareStaticProperties = false;
            comp.ElementsToIgnore = PrimCompareIgnoreList;

            Assert.IsTrue(comp.Compare(part, deserPart), comp.DifferencesString);
        }

        [Test]
        public void TestGroupSerializationDeserialization()
        {
            var sop1 = Util.RandomSOP("Root", 1);
            var sop2 = Util.RandomSOP("Child1", 2);
            var sop3 = Util.RandomSOP("Child2", 3);

            SceneObjectGroup group = new SceneObjectGroup(sop1);
            group.AddPart(sop2);
            group.AddPart(sop3);

            var grpBytes = serEngine.SceneObjectSerializer.SerializeGroupToBytes(group, OpenSim.Region.Framework.Scenes.Serialization.SerializationFlags.SerializeScriptBytecode);

            SceneObjectGroup deserGroup = serEngine.SceneObjectSerializer.DeserializeGroupFromBytes(grpBytes);

            CompareObjects comp = new CompareObjects();
            comp.CompareStaticFields = false;
            comp.CompareStaticProperties = false;
            comp.ElementsToIgnore = PrimCompareIgnoreList;

            Assert.IsTrue(comp.Compare(group, deserGroup), comp.DifferencesString);
        }

        [Test]
        public void TestCoalescedSerializationDeserialization()
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

            var colbytes = serEngine.CoalescedObjectSerializer.SerializeObjectToBytes(cobj, SerializationFlags.None);

            var deserColObj = serEngine.CoalescedObjectSerializer.DeserializeObjectFromBytes(colbytes);

            CompareObjects comp = new CompareObjects();
            comp.CompareStaticFields = false;
            comp.CompareStaticProperties = false;
            comp.ElementsToIgnore = PrimCompareIgnoreList;

            Assert.IsTrue(comp.Compare(cobj, deserColObj), comp.DifferencesString);
        }

        [Test]
        public void TestNullMediaListIsNotAnError()
        {
            var sop1 = Util.RandomSOP("Root", 1);
            var sop2 = Util.RandomSOP("Child1", 2);
            var sop3 = Util.RandomSOP("Child2", 3);

            SceneObjectGroup group = new SceneObjectGroup(sop1);
            group.AddPart(sop2);
            group.AddPart(sop3);

            sop1.Shape.Media = null;

            byte[] serBytes = null;
            Assert.DoesNotThrow(() =>
            {
                serBytes = serEngine.SceneObjectSerializer.SerializeGroupToBytes(group, SerializationFlags.None);
            });

            Assert.NotNull(serBytes);

            var deserObj = serEngine.SceneObjectSerializer.DeserializeGroupFromBytes(serBytes);
        }

        [Test]
        public void TestNullMediaEntryIsNotAnError()
        {
            var sop1 = Util.RandomSOP("Root", 1);
            var sop2 = Util.RandomSOP("Child1", 2);
            var sop3 = Util.RandomSOP("Child2", 3);

            sop1.Shape.Media = new PrimitiveBaseShape.PrimMedia(3);
            sop1.Shape.Media[0] = null;
            sop1.Shape.Media[1] = new MediaEntry();
            sop1.Shape.Media[2] = null;

            SceneObjectGroup group = new SceneObjectGroup(sop1);
            group.AddPart(sop2);
            group.AddPart(sop3);


            byte[] serBytes = null;
            Assert.DoesNotThrow(() =>
            {
                serBytes = serEngine.SceneObjectSerializer.SerializeGroupToBytes(group, SerializationFlags.None);
            });

            Assert.NotNull(serBytes);

            Assert.DoesNotThrow(() =>
            {
                var deserObj = serEngine.SceneObjectSerializer.DeserializeGroupFromBytes(serBytes);
            });
        }

        [Test]
        public void TestRenderMaterialsSerialization()
        {
            var sop1 = Util.RandomSOP("Root", 1);
            var sop2 = Util.RandomSOP("Child1", 2);
            var sop3 = Util.RandomSOP("Child2", 3);

            var mat1 = new RenderMaterial(UUID.Random(), UUID.Random());
            var mat2 = new RenderMaterial(UUID.Random(), UUID.Random());

            sop1.Shape.RenderMaterials.AddMaterial(mat1);
            sop2.Shape.RenderMaterials.AddMaterial(mat2);

            SceneObjectGroup group = new SceneObjectGroup(sop1);
            group.AddPart(sop2);
            group.AddPart(sop3);

            byte[] serBytes = null;
            Assert.DoesNotThrow(() =>
            {
                serBytes = serEngine.SceneObjectSerializer.SerializeGroupToBytes(group, SerializationFlags.None);
            });

            Assert.NotNull(serBytes);

            SceneObjectGroup deserObj = null;
            Assert.DoesNotThrow(() =>
            {
                deserObj = serEngine.SceneObjectSerializer.DeserializeGroupFromBytes(serBytes);
            });

            var newsop1 = deserObj.GetChildPart(1);
            var newsop2 = deserObj.GetChildPart(2);
            var newsop3 = deserObj.GetChildPart(3);

            Assert.That(sop1.Shape.RenderMaterials, Is.EqualTo(newsop1.Shape.RenderMaterials));
            Assert.That(sop2.Shape.RenderMaterials, Is.EqualTo(newsop2.Shape.RenderMaterials));
            Assert.That(sop3.Shape.RenderMaterials, Is.EqualTo(newsop3.Shape.RenderMaterials));
        }

        [Test]
        public void TestKeyframeAnimationSerialization()
        {
            KeyframeAnimation anim = new KeyframeAnimation()
            {
                CurrentAnimationPosition = 5,
                CurrentCommand = KeyframeAnimation.Commands.Stop,
                CurrentMode = KeyframeAnimation.Modes.PingPong,
                InitialPosition = new Vector3(128,128,128),
                InitialRotation = new Quaternion(0.25f, 0.5f, 0.75f, 1.0f),
                PingPongForwardMotion = true,
                PositionList = new Vector3[3] 
                { 
                    new Vector3(10,0,0),
                    new Vector3(-10,0,0),
                    new Vector3(0,0,10)
                },
                RotationList = new Quaternion[3] 
                { 
                    new Quaternion(0.4f, 0.6f, 0.8f, 1),
                    new Quaternion(0.2f, 0.5f, 0.8f, 1),
                    new Quaternion(0.1f, 0.5f, 0.9f, 1)
                },
                TimeElapsed = 678,
                TimeLastTick = Environment.TickCount,
                TimeList = new TimeSpan[3]
                { 
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(3)
                }
            };

            byte[] byteFormat = serEngine.MiscObjectSerializer.SerializeKeyframeAnimationToBytes(anim);
            KeyframeAnimation deserializedAnim = serEngine.MiscObjectSerializer.DeserializeKeyframeAnimationFromBytes(byteFormat);

            CompareObjects comp = new CompareObjects();
            comp.CompareStaticFields = false;
            comp.CompareStaticProperties = false;
            comp.ElementsToIgnore = PrimCompareIgnoreList;

            Assert.IsTrue(comp.Compare(anim, deserializedAnim), comp.DifferencesString);
        }
    }
}
