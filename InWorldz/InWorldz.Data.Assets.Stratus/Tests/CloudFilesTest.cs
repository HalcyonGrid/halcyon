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
using KellermanSoftware.CompareNetObjects;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace InWorldz.Data.Assets.Stratus.Tests
{
    [TestFixture]
    class CloudFilesTest
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
        [Repeat(3)]
        public void TestWriteAndRetrieveAssets()
        {
            if (!_runTests) return;

            AssetBase baseAsset = new AssetBase();

            baseAsset.Data = new byte[] { 0xA, 0x9, 0x8, 0x7, 0x6, 0x5, 0xA, 0x9, 0x8, 0x7, 0x6, 0x5 };
            baseAsset.Name = "Name1234567 ΏΏ";
            baseAsset.Description = "Description TEstttt ΏΏΏÿÿ";
            baseAsset.FullID = UUID.Random();
            baseAsset.Local = true;
            baseAsset.Temporary = true;
            baseAsset.Type = 5;
            baseAsset.Metadata.CreationDate = DateTime.Now;

            int start = Environment.TickCount;
            _client.StoreAsset(baseAsset);
            Console.WriteLine("Time to store: {0}", Environment.TickCount - start);

            start = Environment.TickCount;
            AssetBase cfAsset = _client.RequestAssetSync(baseAsset.FullID);
            Console.WriteLine("Time to read: {0}", Environment.TickCount - start);

            CompareObjects comp = new CompareObjects();
            comp.CompareStaticFields = false;
            comp.CompareStaticProperties = false;

            Assert.IsTrue(comp.Compare(baseAsset, cfAsset), comp.DifferencesString);
            CollectionAssert.AreEqual(baseAsset.Data, cfAsset.Data);

            start = Environment.TickCount;
            //cleanup
            _client.PurgeAssetSync(baseAsset.FullID);
            Console.WriteLine("Time to purge: {0}", Environment.TickCount - start);
        }

        [Test]
        [Repeat(3)]
        public void TestReadNonexistentAsset()
        {
            if (!_runTests) return;

            var start = Environment.TickCount;
            AssetBase cfAsset = _client.RequestAssetSync(UUID.Random());
            Assert.IsNull(cfAsset);
            Console.WriteLine("Time to read: {0}", Environment.TickCount - start);
        }

        [Test]
        [Repeat(3)]
        public void TestReadNonexistentAssetMetadata()
        {
            if (!_runTests) return;

            var start = Environment.TickCount;
            var meta = _client.RequestAssetMetadataSync(UUID.Random());
            Assert.IsNull(meta);
            Console.WriteLine("Time to read metadata: {0}", Environment.TickCount - start);
        }

        [Test]
        [Repeat(3)]
        public void TestWriteSameAssetRepeat()
        {
            if (!_runTests) return;

            AssetBase baseAsset = new AssetBase();

            var id = UUID.Random();

            baseAsset.Data = new byte[] { 0xA, 0x9, 0x8, 0x7, 0x6, 0x5, 0xA, 0x9, 0x8, 0x7, 0x6, 0x5 };
            baseAsset.Name = "Name1234567 ΏΏ";
            baseAsset.Description = "Description TEstttt ΏΏΏÿÿ";
            baseAsset.FullID = id;
            baseAsset.Local = true;
            baseAsset.Temporary = true;
            baseAsset.Type = 5;
            baseAsset.Metadata.CreationDate = DateTime.Now;

            int start = Environment.TickCount;
            _client.StoreAsset(baseAsset);
            Console.WriteLine("Time to store: {0}", Environment.TickCount - start);

            start = Environment.TickCount;
            AssetBase cfAsset = _client.RequestAssetSync(baseAsset.FullID);
            Console.WriteLine("Time to read: {0}", Environment.TickCount - start);

            CompareObjects comp = new CompareObjects();
            comp.CompareStaticFields = false;
            comp.CompareStaticProperties = false;

            Assert.IsTrue(comp.Compare(baseAsset, cfAsset), comp.DifferencesString);
            CollectionAssert.AreEqual(baseAsset.Data, cfAsset.Data);

            start = Environment.TickCount;
            //cleanup
            _client.PurgeAssetSync(baseAsset.FullID);
            Console.WriteLine("Time to purge: {0}", Environment.TickCount - start);
        }

        [Test]
        [Repeat(3)]
        public void TestOverwriteAssetFails()
        {
            if (!_runTests) return;

            AssetBase baseAsset = new AssetBase();

            var id = UUID.Random();

            baseAsset.Data = new byte[] { 0xA, 0x9, 0x8, 0x7, 0x6, 0x5, 0xA, 0x9, 0x8, 0x7, 0x6, 0x5 };
            baseAsset.Name = "Name1234567 ΏΏ";
            baseAsset.Description = "Description TEstttt ΏΏΏÿÿ";
            baseAsset.FullID = id;
            baseAsset.Local = true;
            baseAsset.Temporary = true;
            baseAsset.Type = 5;
            baseAsset.Metadata.CreationDate = DateTime.Now;

            int start = Environment.TickCount;
            _client.StoreAsset(baseAsset);
            Console.WriteLine("Time to store: {0}", Environment.TickCount - start);

            start = Environment.TickCount;
            Assert.Throws<AssetAlreadyExistsException>(() => _client.StoreAsset(baseAsset));
            Console.WriteLine("Time to store: {0}", Environment.TickCount - start);

            start = Environment.TickCount;
            AssetBase cfAsset = _client.RequestAssetSync(baseAsset.FullID);
            Console.WriteLine("Time to read: {0}", Environment.TickCount - start);

            //cleanup
            _client.PurgeAssetSync(baseAsset.FullID);
            Console.WriteLine("Time to purge: {0}", Environment.TickCount - start);
        }

        [Test]
        [Repeat(3)]
        public void TestWriteAndRetrieveLargeAssets()
        {
            if (!_runTests) return;

            Random random = new Random();
            AssetBase baseAsset = new AssetBase();

            var arr = new byte[1 * 1024 * 1024];
            random.NextBytes(arr);

            baseAsset.Data = arr;
            baseAsset.Name = "Name1234567 ΏΏ";
            baseAsset.Description = "Description TEstttt ΏΏΏÿÿ";
            baseAsset.FullID = UUID.Random();
            baseAsset.Local = true;
            baseAsset.Temporary = true;
            baseAsset.Type = 5;
            baseAsset.Metadata.CreationDate = DateTime.Now;

            int start = Environment.TickCount;
            _client.StoreAsset(baseAsset);
            Console.WriteLine("Time to store: {0}", Environment.TickCount - start);

            start = Environment.TickCount;
            AssetBase cfAsset = _client.RequestAssetSync(baseAsset.FullID);
            Console.WriteLine("Time to read: {0}", Environment.TickCount - start);

            CompareObjects comp = new CompareObjects();
            comp.CompareStaticFields = false;
            comp.CompareStaticProperties = false;

            Assert.IsTrue(comp.Compare(baseAsset, cfAsset), comp.DifferencesString);
            CollectionAssert.AreEqual(baseAsset.Data, cfAsset.Data);

            start = Environment.TickCount;
            //cleanup
            _client.PurgeAssetSync(baseAsset.FullID);
            Console.WriteLine("Time to purge: {0}", Environment.TickCount - start);
        }

        [Test]
        public void TestWriteTHENRetrieveLargeAssets()
        {
            if (!_runTests) return;

            Random random = new Random();
            List<AssetBase> assets = new List<AssetBase>();

            int start;

            try
            {
                for (int i = 0; i < 25; i++)
                {
                    AssetBase baseAsset = new AssetBase();

                    var arr = new byte[1 * 1024 * 1024];
                    random.NextBytes(arr);

                    baseAsset.Data = arr;
                    baseAsset.Name = "Name1234567 ΏΏ";
                    baseAsset.Description = "Description TEstttt ΏΏΏÿÿ";
                    baseAsset.FullID = UUID.Random();
                    baseAsset.Local = true;
                    baseAsset.Temporary = true;
                    baseAsset.Type = 5;
                    baseAsset.Metadata.CreationDate = DateTime.Now;

                    start = Environment.TickCount;
                    _client.StoreAsset(baseAsset);
                    Console.WriteLine("Time to store: {0}", Environment.TickCount - start);

                    assets.Add(baseAsset);
                }


                foreach (var baseAsset in assets)
                {
                    start = Environment.TickCount;
                    AssetBase cfAsset = _client.RequestAssetSync(baseAsset.FullID);
                    Console.WriteLine("Time to read: {0}", Environment.TickCount - start);

                    CompareObjects comp = new CompareObjects();
                    comp.CompareStaticFields = false;
                    comp.CompareStaticProperties = false;

                    Assert.IsTrue(comp.Compare(baseAsset, cfAsset), comp.DifferencesString);
                    CollectionAssert.AreEqual(baseAsset.Data, cfAsset.Data);
                }

            }
            finally
            {
                foreach (var baseAsset in assets)
                {
                    start = Environment.TickCount;
                    //cleanup
                    _client.PurgeAssetSync(baseAsset.FullID);
                    Console.WriteLine("Time to purge: {0}", Environment.TickCount - start);
                }
            }
        }

        [Test]
        public void TestWriteThenReadManyTimes()
        {
            if (!_runTests) return;

            Random random = new Random();
            List<AssetBase> assets = new List<AssetBase>();

            int start;

            try
            {
                for (int i = 0; i < 25; i++)
                {
                    AssetBase baseAsset = new AssetBase();

                    var arr = new byte[512 * 1024];
                    random.NextBytes(arr);

                    baseAsset.Data = arr;
                    baseAsset.Name = "Name1234567 ΏΏ";
                    baseAsset.Description = "Description TEstttt ΏΏΏÿÿ";
                    baseAsset.FullID = UUID.Random();
                    baseAsset.Local = true;
                    baseAsset.Temporary = true;
                    baseAsset.Type = 5;
                    baseAsset.Metadata.CreationDate = DateTime.Now;

                    start = Environment.TickCount;
                    _client.StoreAsset(baseAsset);
                    Console.WriteLine("Time to store: {0}", Environment.TickCount - start);

                    assets.Add(baseAsset);
                }

                // Use ConcurrentQueue to enable safe enqueueing from multiple threads. 
                var exceptions = new ConcurrentQueue<Exception>();

                System.Threading.Tasks.Parallel.For(0, 10, i =>
                {
                    try
                    {
                        foreach (var baseAsset in assets)
                        {
                            start = Environment.TickCount;
                            AssetBase cfAsset = _client.RequestAssetSync(baseAsset.FullID);
                            Console.WriteLine("Time to read: {0}", Environment.TickCount - start);

                            CompareObjects comp = new CompareObjects();
                            comp.CompareStaticFields = false;
                            comp.CompareStaticProperties = false;

                            Assert.IsTrue(comp.Compare(baseAsset, cfAsset), comp.DifferencesString);
                            CollectionAssert.AreEqual(baseAsset.Data, cfAsset.Data);
                        }
                    }
                    catch (Exception e)
                    {
                        exceptions.Enqueue(e);
                    }
                });

                if (exceptions.Count > 0) throw new AggregateException(exceptions);
            }
            finally
            {
                foreach (var baseAsset in assets)
                {
                    start = Environment.TickCount;
                    //cleanup
                    _client.PurgeAssetSync(baseAsset.FullID);
                    Console.WriteLine("Time to purge: {0}", Environment.TickCount - start);
                }
            }
        }
    }
}
