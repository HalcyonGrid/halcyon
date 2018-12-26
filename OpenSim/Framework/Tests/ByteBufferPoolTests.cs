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
    class ByteBufferPoolTests
    {
        private const int IDLE_BUFFER_MAX_AGE = 1;

        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void TestSimpleLeaseAndReturn()
        {
            var buffer = new ByteBufferPool(10, new int[] { 1, 2, 3, 4 });
            byte[] a = buffer.LeaseBytes(1);
            byte[] b = buffer.LeaseBytes(2);
            byte[] c = buffer.LeaseBytes(3);
            byte[] d = buffer.LeaseBytes(4);

            Assert.AreEqual(0, buffer.AllocatedBytes);

            buffer.ReturnBytes(a);

            Assert.AreEqual(1, buffer.AllocatedBytes);

            buffer.ReturnBytes(b);

            Assert.AreEqual(3, buffer.AllocatedBytes);

            buffer.ReturnBytes(c);

            Assert.AreEqual(6, buffer.AllocatedBytes);

            buffer.ReturnBytes(d);

            Assert.AreEqual(10, buffer.AllocatedBytes);
        }

        [Test]
        public void TestOverflow()
        {
            var buffer = new ByteBufferPool(10, new int[] { 1, 2, 3, 4 });
            byte[] a = buffer.LeaseBytes(1);
            byte[] b = buffer.LeaseBytes(2);
            byte[] c = buffer.LeaseBytes(3);
            byte[] d = buffer.LeaseBytes(4);

            Assert.AreEqual(0, buffer.AllocatedBytes);

            buffer.ReturnBytes(a);

            Assert.AreEqual(1, buffer.AllocatedBytes);

            buffer.ReturnBytes(b);

            Assert.AreEqual(3, buffer.AllocatedBytes);

            buffer.ReturnBytes(c);

            Assert.AreEqual(6, buffer.AllocatedBytes);

            buffer.ReturnBytes(d);

            Assert.AreEqual(10, buffer.AllocatedBytes);

            byte[] e = buffer.LeaseBytes(4);
            buffer.ReturnBytes(e);

            Assert.AreEqual(10, buffer.AllocatedBytes);
        }

        [Test]
        public void TestInvalidByteReturnSize()
        {
            var buffer = new ByteBufferPool(10, new int[] { 10, 20, 30 });

            Assert.AreEqual(0, buffer.AllocatedBytes);

            buffer.ReturnBytes(new byte[40]);

            Assert.AreEqual(0, buffer.AllocatedBytes);

            buffer.ReturnBytes(new byte[5]);

            Assert.AreEqual(0, buffer.AllocatedBytes);
        }

        [Test]
        public void TestServiceLargeBytes()
        {
            var buffer = new ByteBufferPool(10, new int[] { 10, 20, 30 });

            Assert.AreEqual(0, buffer.AllocatedBytes);

            var bytes = buffer.LeaseBytes(400);

            Assert.AreEqual(400, bytes.Length);

            buffer.ReturnBytes(bytes);

            Assert.AreEqual(0, buffer.AllocatedBytes);
        }

        [Test]
        public void TestAging()
        {
            var buffer = new ByteBufferPool(10, new int[] { 1 }, 1);

            Assert.AreEqual(0, buffer.AllocatedBytes);

            var bytes = buffer.LeaseBytes(1);
            buffer.ReturnBytes(bytes);

            Assert.AreEqual(1, buffer.AllocatedBytes);

            System.Threading.Thread.Sleep(1000);

            buffer.Maintain();

            Assert.AreEqual(0, buffer.AllocatedBytes);
        }
    }
}
