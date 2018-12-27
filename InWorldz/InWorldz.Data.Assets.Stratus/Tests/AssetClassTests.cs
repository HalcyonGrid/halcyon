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
using OpenSim.Framework;
using OpenMetaverse;
using System.IO;
using KellermanSoftware.CompareNetObjects;

namespace InWorldz.Data.Assets.Stratus.Tests
{
    [TestFixture]
    class AssetClassTests
    {
        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void TestSimpleConversionFromBaseAsset()
        {
            AssetBase baseAsset = new AssetBase();

            baseAsset.Data = new byte[] { 0xA, 0x9, 0x8, 0x7, 0x6, 0x5, 0xA, 0x9, 0x8, 0x7, 0x6, 0x5 };
            baseAsset.Name = "Name";
            baseAsset.Description = "Description";
            baseAsset.FullID = UUID.Random();
            baseAsset.Local = true;
            baseAsset.Temporary = true;
            baseAsset.Type = 5;
            baseAsset.Metadata.CreationDate = DateTime.Now;

            var stAsset = StratusAsset.FromAssetBase(baseAsset);

            Assert.AreEqual(baseAsset.FullID.Guid, stAsset.Id);
            CollectionAssert.AreEqual(baseAsset.Data, stAsset.Data);
            Assert.AreEqual(baseAsset.Description, stAsset.Description);
            Assert.AreEqual(baseAsset.Local, stAsset.Local);
            Assert.AreEqual(baseAsset.Name, stAsset.Name);
            Assert.AreEqual(baseAsset.Temporary, stAsset.Temporary);
            Assert.AreEqual(baseAsset.Type, stAsset.Type);
            Assert.AreEqual(baseAsset.Metadata.CreationDate, stAsset.CreateTime);
        }

        [Test]
        public void TestSimpleConversionToBaseAsset()
        {
            StratusAsset stAsset = new StratusAsset();

            stAsset.Data = new byte[] { 0xA, 0x9, 0x8, 0x7, 0x6, 0x5, 0xA, 0x9, 0x8, 0x7, 0x6, 0x5 };
            stAsset.Name = "Name";
            stAsset.Description = "Description";
            stAsset.Id = UUID.Random().Guid;
            stAsset.Local = true;
            stAsset.Temporary = true;
            stAsset.Type = 5;
            stAsset.CreateTime = DateTime.Now;

            var baseAsset = stAsset.ToAssetBase();

            Assert.AreEqual(stAsset.Id, baseAsset.FullID.Guid);
            CollectionAssert.AreEqual(stAsset.Data, baseAsset.Data);
            Assert.AreEqual(stAsset.Description, baseAsset.Description);
            Assert.AreEqual(stAsset.Local, baseAsset.Local);
            Assert.AreEqual(stAsset.Name, baseAsset.Name);
            Assert.AreEqual(stAsset.Temporary, baseAsset.Temporary);
            Assert.AreEqual(stAsset.Type, baseAsset.Type);
            Assert.AreEqual(stAsset.CreateTime, baseAsset.Metadata.CreationDate);
        }

        [Test]
        public void TestSimpleSerializeDeserialize()
        {
            StratusAsset stAsset1 = new StratusAsset();

            stAsset1.Data = new byte[] { 0xA, 0x9, 0x8, 0x7, 0x6, 0x5, 0xA, 0x9, 0x8, 0x7, 0x6, 0x5 };
            stAsset1.Name = "Name";
            stAsset1.Description = "Description";
            stAsset1.Id = UUID.Random().Guid;
            stAsset1.Local = true;
            stAsset1.Temporary = true;
            stAsset1.Type = 5;
            stAsset1.CreateTime = DateTime.Now;

            CompareObjects comp = new CompareObjects();
            comp.CompareStaticFields = false;
            comp.CompareStaticProperties = false;

            StratusAsset stAsset2;
            using (MemoryStream ms = new MemoryStream())
            {
                ProtoBuf.Serializer.Serialize<StratusAsset>(ms, stAsset1);

                ms.Position = 0;

                stAsset2 = ProtoBuf.Serializer.Deserialize<StratusAsset>(ms);

                Assert.IsTrue(comp.Compare(stAsset1, stAsset2), comp.DifferencesString);
            }

            
        }

        [Test]
        public void TestBaseSerializeDeserialize()
        {
            AssetBase baseAsset = new AssetBase();

            baseAsset.Data = new byte[] { 0xA, 0x9, 0x8, 0x7, 0x6, 0x5, 0xA, 0x9, 0x8, 0x7, 0x6, 0x5 };
            baseAsset.Name = "Name";
            baseAsset.Description = "Description";
            baseAsset.FullID = UUID.Random();
            baseAsset.Local = true;
            baseAsset.Temporary = true;
            baseAsset.Type = 5;
            baseAsset.Metadata.CreationDate = DateTime.Now;

            StratusAsset stAsset2;
            using (MemoryStream ms = new MemoryStream())
            {
                ProtoBuf.Serializer.Serialize<StratusAsset>(ms, StratusAsset.FromAssetBase(baseAsset));

                ms.Position = 0;

                stAsset2 = ProtoBuf.Serializer.Deserialize<StratusAsset>(ms);
            }

            var deserBase = stAsset2.ToAssetBase();

            CompareObjects comp = new CompareObjects();
            comp.CompareStaticFields = false;
            comp.CompareStaticProperties = false;
            Assert.IsTrue(comp.Compare(deserBase, baseAsset), comp.DifferencesString);

        }
    }
}
