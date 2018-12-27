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
using NUnit.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenMetaverse;
using KellermanSoftware.CompareNetObjects;
using OpenSim.Framework;
using System.IO;
using System.Xml.Serialization;

namespace OpenSim.Region.FrameworkTests
{
    [TestFixture]
    public class XMLSerializerTests
    {
        private readonly List<string> PrimCompareIgnoreList = new List<string> { "ParentGroup", "FullUpdateCounter", "TerseUpdateCounter", 
                "TimeStamp", "SerializedVelocity", "InventorySerial", "Rezzed" };

        [Test]
        public void TestGroupSerializationDeserialization()
        {
            var sop1 = SceneUtil.RandomSOP("Root", 1);
            var sop2 = SceneUtil.RandomSOP("Child1", 2);
            var sop3 = SceneUtil.RandomSOP("Child2", 3);

            SceneObjectGroup group = new SceneObjectGroup(sop1);
            group.AddPart(sop2);
            group.AddPart(sop3);

            SceneObjectGroup deserGroup = null;
            string grpBytes = null;

            Assert.DoesNotThrow(() =>
            {
                grpBytes = SceneObjectSerializer.ToXml2Format(group, true);
            });

            Assert.NotNull(grpBytes);

            Assert.DoesNotThrow(() =>
            {
                deserGroup = SceneObjectSerializer.FromXml2Format(grpBytes);
            });

            CompareObjects comp = new CompareObjects();
            comp.CompareStaticFields = false;
            comp.CompareStaticProperties = false;
            comp.ElementsToIgnore = PrimCompareIgnoreList;

            Assert.IsTrue(comp.Compare(group, deserGroup), comp.DifferencesString);
        }

        [Test]
        public void TestPrimitiveBaseShapeDeserialization()
        {
            var shape = SceneUtil.MockPrmitiveBaseShape();;
            var serializer = new XmlSerializer(shape.GetType());

            Assert.NotNull(serializer);

            string shapeBytes = null;

            Assert.DoesNotThrow(() =>
            {
                using (StringWriter stringwriter = new System.IO.StringWriter())
                {
                    serializer.Serialize(stringwriter, shape);
                    shapeBytes = stringwriter.ToString();
                }
            });

            Assert.NotNull(shapeBytes);

            PrimitiveBaseShape deserShape = null;

            Assert.DoesNotThrow(() =>
            {
                using (StringReader stringReader = new System.IO.StringReader(shapeBytes))
                {
                    deserShape =  (PrimitiveBaseShape)serializer.Deserialize(stringReader);
                }
            });

            CompareObjects comp = new CompareObjects();
            comp.CompareStaticFields = false;
            comp.CompareStaticProperties = false;
            Assert.IsTrue(comp.Compare(shape, deserShape), comp.DifferencesString);
        }

        [Test]
        public void TestNullMediaListIsNotAnError()
        {
            var sop1 = SceneUtil.RandomSOP("Root", 1);
            var sop2 = SceneUtil.RandomSOP("Child1", 2);
            var sop3 = SceneUtil.RandomSOP("Child2", 3);

            SceneObjectGroup group = new SceneObjectGroup(sop1);
            group.AddPart(sop2);
            group.AddPart(sop3);

            sop1.Shape.Media = null;

            SceneObjectGroup deserGroup = null;
            string grpBytes = null;

            Assert.DoesNotThrow(() =>
            {
                grpBytes = SceneObjectSerializer.ToXml2Format(group, true);

            });

            Assert.NotNull(grpBytes);

            Assert.DoesNotThrow(() =>
            {
                deserGroup = SceneObjectSerializer.FromXml2Format(grpBytes);
            });
        }

        [Test]
        public void TestNullMediaEntryIsNotAnError()
        {
            var sop1 = SceneUtil.RandomSOP("Root", 1);
            var sop2 = SceneUtil.RandomSOP("Child1", 2);
            var sop3 = SceneUtil.RandomSOP("Child2", 3);

            sop1.Shape.Media = new PrimitiveBaseShape.PrimMedia(3);
            sop1.Shape.Media[0] = null;
            sop1.Shape.Media[1] = new MediaEntry();
            sop1.Shape.Media[2] = null;

            SceneObjectGroup group = new SceneObjectGroup(sop1);
            group.AddPart(sop2);
            group.AddPart(sop3);

            SceneObjectGroup deserGroup = null;
            string grpBytes = null;

            Assert.DoesNotThrow(() =>
            {
                grpBytes = SceneObjectSerializer.ToXml2Format(group, true);
            });

            Assert.NotNull(grpBytes);

            Assert.DoesNotThrow(() =>
            {
                deserGroup = SceneObjectSerializer.FromXml2Format(grpBytes);
            });
        }

        [Test]
        public void TestRenderMaterialsSerialization()
        {
            var sop1 = SceneUtil.RandomSOP("Root", 1);
            var sop2 = SceneUtil.RandomSOP("Child1", 2);
            var sop3 = SceneUtil.RandomSOP("Child2", 3);

            var mat1 = new RenderMaterial(UUID.Random(), UUID.Random());
            var mat2 = new RenderMaterial(UUID.Random(), UUID.Random());

            sop1.Shape.RenderMaterials.AddMaterial(mat1);
            sop2.Shape.RenderMaterials.AddMaterial(mat2);

            SceneObjectGroup group = new SceneObjectGroup(sop1);
            group.AddPart(sop2);
            group.AddPart(sop3);

            SceneObjectGroup deserGroup = null;
            string grpBytes = null;

            Assert.DoesNotThrow(() =>
            {
                grpBytes = SceneObjectSerializer.ToXml2Format(group, true);
            });

            Assert.NotNull(grpBytes);

            Assert.DoesNotThrow(() =>
            {
                deserGroup = SceneObjectSerializer.FromXml2Format(grpBytes);
            });

            var newsop1 = deserGroup.GetChildPart(1);
            var newsop2 = deserGroup.GetChildPart(2);
            var newsop3 = deserGroup.GetChildPart(3);

            Assert.That(sop1.Shape.RenderMaterials, Is.EqualTo(newsop1.Shape.RenderMaterials));
            Assert.That(sop2.Shape.RenderMaterials, Is.EqualTo(newsop2.Shape.RenderMaterials));
            Assert.That(sop3.Shape.RenderMaterials, Is.EqualTo(newsop3.Shape.RenderMaterials));
        }

    }
}
