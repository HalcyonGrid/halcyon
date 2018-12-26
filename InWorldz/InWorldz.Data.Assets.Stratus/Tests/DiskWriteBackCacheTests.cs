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
using System.Threading.Tasks;
using KellermanSoftware.CompareNetObjects;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework;

namespace InWorldz.Data.Assets.Stratus.Tests
{
    [TestFixture]
    class DiskWriteBackCacheTests
    {
        private CloudFilesAssetClient _client;
        private bool _runTests;

        [SetUp]
        public void Setup()
        {
            if (Environment.GetEnvironmentVariable("CFUsername") == null)
            {
                _runTests = false;
                return;
            }

            _runTests = true;

            Config.Settings.Instance.CFUseInternalURL = false;
            Config.Settings.Instance.CFUsername = Environment.GetEnvironmentVariable("CFUsername");
            Config.Settings.Instance.CFApiKey = Environment.GetEnvironmentVariable("CFAPIKey");
            Config.Settings.Instance.CFUseCache = false;
            Config.Settings.Instance.CFContainerPrefix = Environment.GetEnvironmentVariable("CFContainerPrefix");
            Config.Settings.Instance.CFWorkerThreads = 8;
            Config.Settings.Instance.CFDefaultRegion = Environment.GetEnvironmentVariable("CFDefaultRegion");

            _client = new CloudFilesAssetClient();
            _client.Start();
        }

        [TearDown]
        public void Stop()
        {
            if (!_runTests) return;

            _client.Stop();
        }

        [Test]
        public void TestBasicCacheRetrieval()
        {
            Cache.DiskWriteBackCache wbc = new Cache.DiskWriteBackCache();

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

            wbc.StoreAsset(stAsset);

            CompareObjects comp = new CompareObjects();
            comp.CompareStaticFields = false;
            comp.CompareStaticProperties = false;

            var cacheAsset = wbc.GetAsset(baseAsset.FullID.Guid);

            Assert.IsTrue(comp.Compare(cacheAsset, stAsset), comp.DifferencesString);
            CollectionAssert.AreEqual(cacheAsset.Data, stAsset.Data);
        }

        [Test]
        public void TestCachePersists()
        {
            Cache.DiskWriteBackCache wbc = new Cache.DiskWriteBackCache();

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

            wbc.StoreAsset(stAsset);

            wbc = null;

            wbc = new Cache.DiskWriteBackCache();

            CompareObjects comp = new CompareObjects();
            comp.CompareStaticFields = false;
            comp.CompareStaticProperties = false;

            var cacheAsset = wbc.GetAsset(baseAsset.FullID.Guid);

            Assert.IsTrue(comp.Compare(cacheAsset, stAsset), comp.DifferencesString);
            CollectionAssert.AreEqual(cacheAsset.Data, stAsset.Data);
        }

        [Test]
        public void TestWriteToCF()
        {
            if (!_runTests) return;

            //delete any leftover files in the writeback cache
            foreach (var file in Directory.EnumerateFiles("cache/cf_writeback"))
            {
                File.Delete(file);
            }

            Cache.DiskWriteBackCache wbc = new Cache.DiskWriteBackCache();

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

            wbc.StoreAsset(stAsset);

            wbc.DoWriteCycle();

            //the asset should still be in the WB cache
            Assert.IsNotNull(wbc.GetAsset(baseAsset.FullID.Guid));

            //... but we should now be able to get the asset from CF
            AssetBase cfAsset = _client.RequestAssetSync(baseAsset.FullID);

            CompareObjects comp = new CompareObjects();
            comp.CompareStaticFields = false;
            comp.CompareStaticProperties = false;

            Assert.IsTrue(comp.Compare(baseAsset, cfAsset), comp.DifferencesString);
            CollectionAssert.AreEqual(baseAsset.Data, cfAsset.Data);
        }

        [Test]
        [Repeat(1)]
        public void TestCFTimeoutAndWritePath()
        {
            if (!_runTests) return;

            //delete any leftover files in the writeback cache
            foreach (var file in Directory.EnumerateFiles("cache/cf_writeback"))
            {
                File.Delete(file);
            }

            Cache.DiskWriteBackCache wbc = new Cache.DiskWriteBackCache();

            AssetBase baseAsset = new AssetBase();

            baseAsset.Data = new byte[] { 0xA, 0x9, 0x8, 0x7, 0x6, 0x5, 0xA, 0x9, 0x8, 0x7, 0x6, 0x5 };
            baseAsset.Name = "Name";
            baseAsset.Description = "Description";
            baseAsset.FullID = UUID.Random();
            baseAsset.Local = true;
            baseAsset.Temporary = true;
            baseAsset.Type = 5;
            baseAsset.Metadata.CreationDate = DateTime.Now;

            try
            {
                Config.Settings.Instance.UnitTest_ThrowTimeout = true;

                _client.StoreAsset(baseAsset);
            }
            finally
            {
                Config.Settings.Instance.UnitTest_ThrowTimeout = false;
            }

            System.Threading.Thread.Sleep(5000);

            //we should now be able to get the asset from CF since it should've been written back
            //by the write back recovery code
            AssetBase cfAsset = _client.RequestAssetSync(baseAsset.FullID);

            CompareObjects comp = new CompareObjects();
            comp.CompareStaticFields = false;
            comp.CompareStaticProperties = false;

            Assert.IsTrue(comp.Compare(baseAsset, cfAsset), comp.DifferencesString);
            CollectionAssert.AreEqual(baseAsset.Data, cfAsset.Data);
        }

        [Test]
        public void TestCFTimeoutAndWritePathWithImmediateStaleDeletes()
        {
            if (!_runTests) return;

            //delete any leftover files in the writeback cache
            foreach (var file in Directory.EnumerateFiles("cache/cf_writeback"))
            {
                File.Delete(file);
            }

            AssetBase baseAsset = new AssetBase();

            baseAsset.Data = new byte[] { 0xA, 0x9, 0x8, 0x7, 0x6, 0x5, 0xA, 0x9, 0x8, 0x7, 0x6, 0x5 };
            baseAsset.Name = "Name";
            baseAsset.Description = "Description";
            baseAsset.FullID = UUID.Random();
            baseAsset.Local = true;
            baseAsset.Temporary = true;
            baseAsset.Type = 5;
            baseAsset.Metadata.CreationDate = DateTime.Now;

            try
            {
                Config.Settings.Instance.UnitTest_ThrowTimeout = true;
                Config.Settings.Instance.UnitTest_DeleteOldCacheFilesImmediately = true;

                _client.StoreAsset(baseAsset);

                System.Threading.Thread.Sleep(5000);

                //confirm the asset is now missing from the writeback cache
                Assert.IsNull(_client.DiskWriteBackCache.GetAsset(baseAsset.FullID.Guid));
            }
            finally
            {
                Config.Settings.Instance.UnitTest_ThrowTimeout = false;
                Config.Settings.Instance.UnitTest_DeleteOldCacheFilesImmediately = false;
            }

            //we should now be able to get the asset from CF since it should've been written back
            //by the write back recovery code
            AssetBase cfAsset = _client.RequestAssetSync(baseAsset.FullID);

            CompareObjects comp = new CompareObjects();
            comp.CompareStaticFields = false;
            comp.CompareStaticProperties = false;

            Assert.IsTrue(comp.Compare(baseAsset, cfAsset), comp.DifferencesString);
            CollectionAssert.AreEqual(baseAsset.Data, cfAsset.Data);
        }
    }
}
