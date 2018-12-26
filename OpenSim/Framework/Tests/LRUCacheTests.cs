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

using System.Threading;

using NUnit.Framework;

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
            var cache = new LRUCache<UUID, string>(MAX_CACHE_SIZE);
            string testData = "Test Data";
            UUID id = UUID.Random();

            cache.Add(id, testData);
            Assert.IsTrue(cache.Contains(id));
            Assert.AreEqual(1, cache.Size, "Size property was wrong");

            Assert.IsTrue(cache.TryGetValue(id, out string entry));
            Assert.AreEqual(testData.Length, entry.Length);
            Assert.AreEqual(1, cache.Size, "Size property was wrong");
        }

        [Test]
        public void TestRetrieveItemThatDoesntExist()
        {
            var cache = new LRUCache<UUID, string>(MAX_CACHE_SIZE);
            string testData = "Test Data";
            UUID id = UUID.Random();

            cache.Add(id, testData);

            Assert.AreEqual(1, cache.Size, "Size property was wrong");
            Assert.AreEqual(1, cache.Count, "Count property was wrong");
            Assert.IsFalse(cache.TryGetValue(UUID.Random(), out string entry));
            Assert.IsNull(entry);
        }

        [Test]
        public void TestFillCache()
        {
            var cache = new LRUCache<UUID, string>(10);
            string testData = "Test Data";

            for (int i = 0; i < 10; i++)
            {
                var id = UUID.Random();
                cache.Add(id, testData);
            }

            Assert.AreEqual(10, cache.Count, "Count property was wrong");
            Assert.AreEqual(10, cache.Size, "Size property was wrong");
        }

        [Test]
        public void TestFillCacheLiteral()
        {
            string firstEntryData = "First Entry";
            string secondEntryData = "Second Entry";

            var cache = new LRUCache<UUID, string>(2)
            {
                { UUID.Random(), firstEntryData, firstEntryData.Length },
                { UUID.Random(), secondEntryData, secondEntryData.Length }
            };

            Assert.AreEqual(2, cache.Count, "Count property was wrong");
            Assert.AreEqual(2, cache.Size, "Size property was wrong");
        }

        [Test]
        public void TestOverflowCacheWithSmallerItem()
        {
            var cache = new LRUCache<UUID, string>(10);
            string testData = "Test Data";

            for (int i = 0; i < 10; i++)
            {
                var id = UUID.Random();
                cache.Add(id, testData);
            }

            Assert.AreEqual(10, cache.Count, "Count property was wrong");
            Assert.AreEqual(10, cache.Size, "Size property was wrong");

            UUID overflowId = UUID.Random();
            string overflowData = "OverFlow";
            cache.Add(overflowId, overflowData);

            Assert.AreEqual(10, cache.Count, "Count property was wrong");
            Assert.AreEqual(10, cache.Size, "Size property was wrong");

            Assert.IsTrue(cache.TryGetValue(overflowId, out string lastInsertedValue));
            Assert.AreEqual(overflowData, lastInsertedValue);
        }

        [Test]
        public void TestOverflowReplacesFirstEntryAdded()
        {
            var cache = new LRUCache<UUID, string>(10);

            UUID firstEntryId = UUID.Random();
            string firstEntryData = "First Entry";
            cache.Add(firstEntryId, firstEntryData);

            string testData = "Test Data";
            for (int i = 0; i < 10; i++)
            {
                var id = UUID.Random();
                cache.Add(id, testData);
            }

            Assert.AreEqual(10, cache.Count, "Count property was wrong");
            Assert.AreEqual(10, cache.Size, "Size property was wrong");

            Assert.IsFalse(cache.TryGetValue(firstEntryId, out string lastInsertedValue));
            Assert.IsNull(lastInsertedValue);
        }

        [Test]
        public void TestAgingRemovesEntriesPastExpirationInterval()
        {
            var cache = new LRUCache<UUID, string>(10, maxAge : 1000);

            UUID firstEntryId = UUID.Random();
            string firstEntryData = "First Entry";
            cache.Add(firstEntryId, firstEntryData);

            Thread.Sleep(2 * 1000);
            cache.Maintain();

            Assert.AreEqual(0, cache.Count, "Count property was wrong");
            Assert.AreEqual(0, cache.Size, "Size property was wrong");

            Assert.IsFalse(cache.TryGetValue(firstEntryId, out string lastInsertedValue));
            Assert.IsNull(lastInsertedValue);
        }

        [Test]
        public void TestAgingRemovesEntriesButPreservesReservedEntries()
        {
            var cache = new LRUCache<UUID, string>(10, minSize : 1, maxAge : 1000);

            UUID firstEntryId = UUID.Random();
            string firstEntryData = "First Entry";
            cache.Add(firstEntryId, firstEntryData);

            UUID secondEntryId = UUID.Random();
            string secondEntryData = "Second Entry";
            cache.Add(secondEntryId, secondEntryData);

            Thread.Sleep(5 * 1000);
            cache.Maintain();

            Assert.AreEqual(1, cache.Count, "Count property was wrong");
            Assert.AreEqual(1, cache.Size, "Size property was wrong");

            Assert.IsFalse(cache.TryGetValue(firstEntryId, out string lastInsertedValue));
            Assert.IsNull(lastInsertedValue);

            Assert.IsTrue(cache.TryGetValue(secondEntryId, out lastInsertedValue));
            Assert.AreEqual(secondEntryData, lastInsertedValue);
        }

        [Test]
        public void TestAgingRemovesEntriesUsingBytesForReservedSize()
        {
            UUID firstEntryId = UUID.Random();
            string firstEntryData = "First Entry";

            UUID secondEntryId = UUID.Random();
            string secondEntryData = "Second Entry";

            var cache = new LRUCache<UUID, string>(capacity: 250, useSizing: true, minSize: secondEntryData.Length, maxAge: 1000)
            {
                { firstEntryId, firstEntryData, firstEntryData.Length },
                { secondEntryId, secondEntryData, secondEntryData.Length }
            };

            Thread.Sleep(5 * 1000);
            cache.Maintain();

            Assert.AreEqual(1, cache.Count, "Count property was wrong");
            Assert.AreEqual(secondEntryData.Length, cache.Size, "Size property was wrong");

            Assert.IsFalse(cache.TryGetValue(firstEntryId, out string lastInsertedValue));
            Assert.IsNull(lastInsertedValue);

            Assert.IsTrue(cache.TryGetValue(secondEntryId, out lastInsertedValue));
            Assert.AreEqual(secondEntryData, lastInsertedValue);
        }
    }
}
