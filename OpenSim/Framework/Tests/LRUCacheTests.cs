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
using System.Threading;
using NUnit.Framework;
using OpenSim.Framework;
using OpenMetaverse;

namespace OpenSim.Framework.Tests
{
    [TestFixture]
    class LRUCacheTests
    {
        private const int MAX_CACHE_SIZE = 250;

        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void TestSimpleCacheAndRetrieval()
        {
            var cache = new LRUCache<UUID, String>(MAX_CACHE_SIZE);
            String testData = "Test Data";
            UUID id = UUID.Random();

            cache.Add(id, testData);
            Assert.IsTrue(cache.Contains(id));
            Assert.AreEqual(1, cache.Size);

            String entry;
            Assert.IsTrue(cache.TryGetValue(id, out entry));
            Assert.AreEqual(testData.Length, entry.Length);
            Assert.AreEqual(1, cache.Size);
        }

        [Test]
        public void TestRetrieveItemThatDoesntExist()
        {
            var cache = new LRUCache<UUID, String>(MAX_CACHE_SIZE);
            String testData = "Test Data";
            UUID id = UUID.Random();

            cache.Add(id, testData);

            String entry = null;
            Assert.AreEqual(1, cache.Size);
            Assert.AreEqual(1, cache.Count);
            Assert.IsFalse(cache.TryGetValue(UUID.Random(), out entry));
            Assert.IsNull(entry);
        }

        [Test]
        public void TestFillCache()
        {
            var cache = new LRUCache<UUID, String>(10);
            String testData = "Test Data";

            for (int i = 0; i < 10; i++)
            {
                var id = UUID.Random();
                cache.Add(id, testData);
            }

            Assert.AreEqual(10, cache.Count);
            Assert.AreEqual(10, cache.Size);
        }

        [Test]
        public void TestOverflowCacheWithSmallerItem()
        {
            var cache = new LRUCache<UUID, String>(10);
            String testData = "Test Data";

            for (int i = 0; i < 10; i++)
            {
                var id = UUID.Random();
                cache.Add(id, testData);
            }

            Assert.AreEqual(10, cache.Count);
            Assert.AreEqual(10, cache.Size);

            UUID overflowId = UUID.Random();
            String overflowData = "OverFlow";
            cache.Add(overflowId, overflowData);

            Assert.AreEqual(10, cache.Count);
            Assert.AreEqual(10, cache.Size);

            String lastInsertedValue;
            Assert.IsTrue(cache.TryGetValue(overflowId, out lastInsertedValue));
            Assert.AreEqual(overflowData, lastInsertedValue);
        }

        [Test]
        public void TestOverflowReplacesFirstEntryAdded()
        {
            var cache = new LRUCache<UUID, String>(10);

            UUID firstEntryId = UUID.Random();
            String firstEntryData = "First Entry";
            cache.Add(firstEntryId, firstEntryData);

            String testData = "Test Data";
            for (int i = 0; i < 10; i++)
            {
                var id = UUID.Random();
                cache.Add(id, testData);
            }

            Assert.AreEqual(10, cache.Count);
            Assert.AreEqual(10, cache.Size);

            String lastInsertedValue;
            Assert.IsFalse(cache.TryGetValue(firstEntryId, out lastInsertedValue));
            Assert.IsNull(lastInsertedValue);
        }

        [Test]
        public void TestAgingRemovesEntriesPastExpirationInterval()
        {
            var cache = new LRUCache<UUID, String>(10, maxAge : 1000);

            UUID firstEntryId = UUID.Random();
            String firstEntryData = "First Entry";
            cache.Add(firstEntryId, firstEntryData);

            Thread.Sleep(2 * 1000);
            cache.Maintain();

            Assert.AreEqual(0, cache.Count);
            Assert.AreEqual(0, cache.Size);

            String lastInsertedValue;
            Assert.IsFalse(cache.TryGetValue(firstEntryId, out lastInsertedValue));
            Assert.IsNull(lastInsertedValue);
        }

        [Test]
        public void TestAgingRemovesEntriesButPreservesReservedEntries()
        {
            var cache = new LRUCache<UUID, String>(10, minSize : 1, maxAge : 1000);

            UUID firstEntryId = UUID.Random();
            String firstEntryData = "First Entry";
            cache.Add(firstEntryId, firstEntryData);

            UUID secondEntryId = UUID.Random();
            String secondEntryData = "Second Entry";
            cache.Add(secondEntryId, secondEntryData);

            Thread.Sleep(5 * 1000);
            cache.Maintain();

            Assert.AreEqual(1, cache.Count);
            Assert.AreEqual(1, cache.Size);

            String lastInsertedValue;
            Assert.IsFalse(cache.TryGetValue(firstEntryId, out lastInsertedValue));
            Assert.IsNull(lastInsertedValue);

            Assert.IsTrue(cache.TryGetValue(secondEntryId, out lastInsertedValue));
            Assert.AreEqual(secondEntryData, lastInsertedValue);
        }

        [Test]
        public void TestAgingRemovesEntriesUsingBytesForReservedSize()
        {
            UUID firstEntryId = UUID.Random();
            String firstEntryData = "First Entry";

            UUID secondEntryId = UUID.Random();
            String secondEntryData = "Second Entry";

            var cache = new LRUCache<UUID, String>(capacity: 250, useSizing: true, minSize: secondEntryData.Length, maxAge: 1000);
            cache.Add(firstEntryId, firstEntryData, firstEntryData.Length);
            cache.Add(secondEntryId, secondEntryData, secondEntryData.Length);

            Thread.Sleep(5 * 1000);
            cache.Maintain();

            Assert.AreEqual(1, cache.Count);
            Assert.AreEqual(secondEntryData.Length, cache.Size);

            String lastInsertedValue;
            Assert.IsFalse(cache.TryGetValue(firstEntryId, out lastInsertedValue));
            Assert.IsNull(lastInsertedValue);

            Assert.IsTrue(cache.TryGetValue(secondEntryId, out lastInsertedValue));
            Assert.AreEqual(secondEntryData, lastInsertedValue);
        }
    }
}
