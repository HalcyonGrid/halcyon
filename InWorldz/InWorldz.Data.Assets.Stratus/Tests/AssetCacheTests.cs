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


namespace InWorldz.Data.Assets.Stratus.Tests
{
    [TestFixture]
    class AssetCacheTests
    {
        [SetUp]
        public void Setup()
        {
            Config.Settings.Instance.CFCacheSize = Config.Constants.MAX_CACHEABLE_ASSET_SIZE * 10;
        }

        [Test]
        public void TestSimpleCacheAndRetrieval()
        {
            Cache.Cache cache = new Cache.Cache();

            byte[] assetData = new byte[] {0x1, 0x2, 0x3, 0x4};
            OpenMetaverse.UUID id = OpenMetaverse.UUID.Random();

            cache.CacheAssetData(id, assetData);

            Assert.IsTrue(cache.HasAsset(id));
            Assert.AreEqual(1, cache.ItemCount);
            
            Cache.CacheEntry entry;
            Assert.IsTrue(cache.TryGetAsset(id, out entry));
            Assert.AreEqual(assetData.Length, entry.Size);
            Assert.AreEqual(entry.Data.Length, Cache.Cache.BUFFER_SIZES[0]);
            Assert.AreEqual(entry.Data.Length, cache.Size);
        }

        [Test]
        public void TestRetrieveItemThatDoesntExist()
        {
            Cache.Cache cache = new Cache.Cache();

            byte[] assetData = new byte[] { 0x1, 0x2, 0x3, 0x4 };
            OpenMetaverse.UUID id = OpenMetaverse.UUID.Random();

            cache.CacheAssetData(id, assetData);

            Cache.CacheEntry entry;
            Assert.IsFalse(cache.TryGetAsset(OpenMetaverse.UUID.Random(), out entry));
            Assert.IsNull(entry);
        }

        [Test]
        public void TestTryStoreOversizedAsset()
        {
            Cache.Cache cache = new Cache.Cache();

            byte[] assetData = new byte[Config.Constants.MAX_CACHEABLE_ASSET_SIZE + 1];
            OpenMetaverse.UUID id = OpenMetaverse.UUID.Random();

            cache.CacheAssetData(id, assetData);

            Cache.CacheEntry entry;
            Assert.IsFalse(cache.TryGetAsset(OpenMetaverse.UUID.Random(), out entry));
            Assert.IsNull(entry);
        }

        [Test]
        public void TestFillCache()
        {
            Cache.Cache cache = new Cache.Cache();

            var assets = new Dictionary<OpenMetaverse.UUID, byte[]>();

            for (int i = 0; i < 10; i++)
            {
                var id = OpenMetaverse.UUID.Random();
                var data = new byte[Config.Constants.MAX_CACHEABLE_ASSET_SIZE];
                assets.Add(id, data);
                cache.CacheAssetData(id, data);
            }

            Assert.AreEqual(10, cache.ItemCount);
            Assert.AreEqual(Config.Constants.MAX_CACHEABLE_ASSET_SIZE * 10, cache.Size);

            foreach (var kvp in assets)
            {
                Cache.CacheEntry entry;
                Assert.IsTrue(cache.TryGetAsset(kvp.Key, out entry));
                Assert.AreEqual(kvp.Value.Length, entry.Size);
                Assert.AreEqual(entry.Data.Length, Config.Constants.MAX_CACHEABLE_ASSET_SIZE);
            }
        }

        [Test]
        public void TestOverflowCacheWithSmallerItem()
        {
            Cache.Cache cache = new Cache.Cache();

            for (int i = 0; i < 10; i++)
            {
                var id = OpenMetaverse.UUID.Random();
                var data = new byte[Config.Constants.MAX_CACHEABLE_ASSET_SIZE];
                cache.CacheAssetData(id, data);
            }

            cache.CacheAssetData(OpenMetaverse.UUID.Random(), new byte[4]);

            Assert.AreEqual(10, cache.ItemCount);
            Assert.AreEqual((Config.Constants.MAX_CACHEABLE_ASSET_SIZE * 9) + Cache.Cache.BUFFER_SIZES[0], cache.Size);
        }

        [Test]
        public void TestOverflowCacheWithLargerItem()
        {
            Cache.Cache cache = new Cache.Cache();

            for (int i = 0; i < 20; i++)
            {
                var id = OpenMetaverse.UUID.Random();
                var data = new byte[Config.Constants.MAX_CACHEABLE_ASSET_SIZE / 2];
                cache.CacheAssetData(id, data);
            }

            Assert.AreEqual(20, cache.ItemCount);
            Assert.AreEqual((Config.Constants.MAX_CACHEABLE_ASSET_SIZE / 2) * 20, cache.Size);

            cache.CacheAssetData(OpenMetaverse.UUID.Random(), new byte[Config.Constants.MAX_CACHEABLE_ASSET_SIZE]);

            Assert.AreEqual(19, cache.ItemCount);
            Assert.AreEqual(((Config.Constants.MAX_CACHEABLE_ASSET_SIZE / 2) * 18) + Config.Constants.MAX_CACHEABLE_ASSET_SIZE, cache.Size);
        }

        [Test]
        public void TestRawAssetCaching()
        {
            Cache.Cache cache = new Cache.Cache();

            OpenMetaverse.UUID id = OpenMetaverse.UUID.Random();

            StratusAsset sa = new StratusAsset {Id = id.Guid, Data = new byte[Config.Constants.MAX_STREAM_CACHE_SIZE * 2] };
            cache.CacheAssetData(id, sa);

            Assert.AreEqual(1, cache.ItemCount);
            Assert.AreEqual(Config.Constants.MAX_STREAM_CACHE_SIZE * 2, cache.Size);

            Cache.CacheEntry cachedAsset;
            Assert.IsTrue(cache.TryGetAsset(id, out cachedAsset));
            Assert.AreEqual(id.Guid, cachedAsset.FullAsset.Id);
        }
    }
}
