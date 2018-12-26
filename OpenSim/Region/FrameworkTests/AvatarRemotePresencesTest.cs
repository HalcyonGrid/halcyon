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
using System.Threading.Tasks;
using NUnit.Framework;
using OpenSim.Region.Framework.Scenes;
using InWorldz.Testing;
using OpenSim.Framework;
using System.Threading;
using OpenSim.Region.Framework.Interfaces;
using System.Collections;

namespace OpenSim.Region.FrameworkTests
{
    [TestFixture]
    public class AvatarRemotePresencesTest
    {
        private Scene mockScene;
        private Scene neighbor1left;
        private Scene neighbor2left;
        private Scene neighbor3left;
        private ScenePresence presence;
        private const ushort REGION_PORT_BASE = 9021;

        [SetUp]
        public void Setup()
        {
            mockScene = SceneHelper.CreateScene(REGION_PORT_BASE, 1000, 1000);
            mockScene.RegisterModuleInterface<IEventQueue>(new InWorldz.Testing.MockEventQueue());

            neighbor1left = SceneHelper.CreateScene(REGION_PORT_BASE+1, 999, 1000);
            neighbor2left = SceneHelper.CreateScene(REGION_PORT_BASE+2, 998, 1000);
            neighbor3left = SceneHelper.CreateScene(REGION_PORT_BASE+3, 997, 1000);

            presence = new ScenePresence(mockScene, 256f, new MockClientAPI());

            neighbor1left.CommsManager.HttpServer.AddHTTPHandler("/agent/", HandleAgentStuff);
            neighbor1left.CommsManager.HttpServer.AddHTTPHandler("/agent2/", HandleAgentStuff);
            neighbor2left.CommsManager.HttpServer.AddHTTPHandler("/agent/", HandleAgentStuff);
            neighbor2left.CommsManager.HttpServer.AddHTTPHandler("/agent2/", HandleAgentStuff);
        }

        private Hashtable HandleAgentStuff(Hashtable request)
        {
            Hashtable reply = new Hashtable();

            reply["str_response_string"] = "true";
            reply["int_response_code"] = 200;
            reply["content_type"] = "text/plain";

            return reply;
        }

        [TearDown]
        public void Teardown()
        {
            SceneHelper.TearDownScene(mockScene);
            SceneHelper.TearDownScene(neighbor1left);
            SceneHelper.TearDownScene(neighbor2left);
            SceneHelper.TearDownScene(neighbor3left);
        }

    
        [Test]
        public void TestMakeRootAgentEventRegistration()
        {
            mockScene.EventManager.TriggerOnMakeRootAgent(presence);

            //tell the mock scene that neighbor1left is up
            SurroundingRegionManagerTests.SendCreateRegionMessage(999, 1000, REGION_PORT_BASE, REGION_PORT_BASE+1);

            Thread.Sleep(1000); //sleeps are needed here because the calls are async

            Assert.IsTrue(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(999, 1000)));


            //tell the mock scene that neighbor1 has left
            SurroundingRegionManagerTests.SendRegionDownMessage(999, 1000, REGION_PORT_BASE, REGION_PORT_BASE+1);

            Thread.Sleep(500);

            Assert.IsFalse(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(999, 1000)));

            mockScene.EventManager.TriggerOnMakeChildAgent(presence);
        }

        [Test]
        public void TestMakeChildAgentDeregistration()
        {
            mockScene.EventManager.TriggerOnMakeRootAgent(presence);

            mockScene.EventManager.TriggerOnMakeChildAgent(presence);

            //tell the mock scene that neighbor1left is up
            SurroundingRegionManagerTests.SendCreateRegionMessage(999, 1000, REGION_PORT_BASE, REGION_PORT_BASE+1);

            Thread.Sleep(500); //sleeps are needed here because the calls are async

            Assert.IsFalse(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(999, 1000)));
        }

        [Test]
        public void TestFarRegionUpDoesntNotifyPresence()
        {
            mockScene.EventManager.TriggerOnMakeRootAgent(presence);

            //tell the mock scene that neighbor1left is up
            SurroundingRegionManagerTests.SendCreateRegionMessage(997, 1000, REGION_PORT_BASE, REGION_PORT_BASE+1);

            Thread.Sleep(500); //sleeps are needed here because the calls are async

            Assert.IsFalse(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(999, 1000)));

            mockScene.EventManager.TriggerOnMakeChildAgent(presence);
        }

        [Test]
        public void TestDrawDistanceChangesRegionVisibility()
        {
            //tell the mock scene that all neighbors are up
            SurroundingRegionManagerTests.SendCreateRegionMessage(999, 1000, REGION_PORT_BASE, REGION_PORT_BASE+1);
            SurroundingRegionManagerTests.SendCreateRegionMessage(998, 1000, REGION_PORT_BASE, REGION_PORT_BASE+2);
            SurroundingRegionManagerTests.SendCreateRegionMessage(997, 1000, REGION_PORT_BASE, REGION_PORT_BASE+3);

            //make sure our presence has no connections to neighbors since we should be child
            var presences = presence.RemotePresences.GetRemotePresenceList();
            Assert.AreEqual(0, presences.Count);

            mockScene.EventManager.TriggerOnMakeRootAgent(presence);

            Thread.Sleep(1000);

            presences = presence.RemotePresences.GetRemotePresenceList();
            Assert.AreEqual(1, presences.Count);

            Assert.IsTrue(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(999, 1000)));
            Assert.IsFalse(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(998, 1000)));
            Assert.IsFalse(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(997, 1000)));

            presence.RemotePresences.HandleDrawDistanceChanged(512)?.Wait();
            Thread.Sleep(1000);
            presences = presence.RemotePresences.GetRemotePresenceList();
            Assert.AreEqual(2, presences.Count);

            Assert.IsTrue(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(999, 1000)));
            Assert.IsTrue(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(998, 1000)));
            Assert.IsFalse(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(997, 1000)));

            presence.RemotePresences.HandleDrawDistanceChanged(256)?.Wait();
            Thread.Sleep(1000);
            presences = presence.RemotePresences.GetRemotePresenceList();
            Assert.AreEqual(1, presences.Count);

            Assert.IsTrue(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(999, 1000)));
            Assert.IsFalse(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(998, 1000)));
            Assert.IsFalse(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(997, 1000)));

            mockScene.EventManager.TriggerOnMakeChildAgent(presence);
        }

        private const int HOME = 1000;
        [Test]
        public void TestRegionVisibilityConnected2of3()
        {
            //make sure our presence has no connections to neighbors since we should be child
            var presences = presence.RemotePresences.GetRemotePresenceList();
            Assert.AreEqual(0, presences.Count);

            //tell the mock scene that all neighbors are up

            // Add some neighbors (2 of 3 should be visible at long draw distance)
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 1, HOME, REGION_PORT_BASE, REGION_PORT_BASE + 1);
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 2, HOME, REGION_PORT_BASE, REGION_PORT_BASE + 2);
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 3, HOME, REGION_PORT_BASE, REGION_PORT_BASE + 3);
            Thread.Sleep(500);

            mockScene.EventManager.TriggerOnMakeRootAgent(presence);
            Thread.Sleep(500);
            presence.RemotePresences.HandleDrawDistanceChanged(1024)?.Wait();
            Thread.Sleep(250);

            presences = presence.RemotePresences.GetRemotePresenceList();
            Assert.AreEqual(2, presences.Count);

            Assert.IsTrue(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 1, HOME)));
            Assert.IsTrue(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 2, HOME)));
            Assert.IsFalse(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 3, HOME)));

            // Clean up
            mockScene.EventManager.TriggerOnMakeChildAgent(presence);
        }
        [Test]
        public void TestRegionVisibilityConnected2of7()
        {
            //make sure our presence has no connections to neighbors since we should be child
            var presences = presence.RemotePresences.GetRemotePresenceList();
            Assert.AreEqual(0, presences.Count);

            //tell the mock scene that all neighbors are up

            // Add some neighbors (2 of 3 should be visible at long draw distance)
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 1, HOME, REGION_PORT_BASE, REGION_PORT_BASE + 1);
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 2, HOME, REGION_PORT_BASE, REGION_PORT_BASE + 2);
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 3, HOME, REGION_PORT_BASE, REGION_PORT_BASE + 3);
            // Now add 4 more unconnected distant regions (3 of 4 should be visible if connected)
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 0, HOME + 2, REGION_PORT_BASE, REGION_PORT_BASE + 5);
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 1, HOME + 2, REGION_PORT_BASE, REGION_PORT_BASE + 6);
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 2, HOME + 2, REGION_PORT_BASE, REGION_PORT_BASE + 7);
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 3, HOME + 2, REGION_PORT_BASE, REGION_PORT_BASE + 8);
            Thread.Sleep(500);

            mockScene.EventManager.TriggerOnMakeRootAgent(presence);
            Thread.Sleep(500);
            presence.RemotePresences.HandleDrawDistanceChanged(1024)?.Wait();
            Thread.Sleep(250);

            presences = presence.RemotePresences.GetRemotePresenceList();
            Assert.AreEqual(2, presences.Count);

            Assert.IsTrue(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 1, HOME)));
            Assert.IsTrue(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 2, HOME)));
            Assert.IsFalse(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 3, HOME)));

            Assert.IsFalse(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 0, HOME + 2)));
            Assert.IsFalse(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 1, HOME + 2)));
            Assert.IsFalse(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 2, HOME + 2)));
            Assert.IsFalse(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 3, HOME + 2)));

            // Clean up
            mockScene.EventManager.TriggerOnMakeChildAgent(presence);
        }
        [Test]
        public void TestRegionVisibilityConnected6of8()
        {
            //make sure our presence has no connections to neighbors since we should be child
            var presences = presence.RemotePresences.GetRemotePresenceList();
            Assert.AreEqual(0, presences.Count);

            //tell the mock scene that all neighbors are up

            // Add some neighbors (2 of 3 should be visible at long draw distance)
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 1, HOME, REGION_PORT_BASE, REGION_PORT_BASE + 1);
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 2, HOME, REGION_PORT_BASE, REGION_PORT_BASE + 2);
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 3, HOME, REGION_PORT_BASE, REGION_PORT_BASE + 3);
            // Now add 4 more unconnected distant regions (3 of 4 should be visible if connected)
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 0, HOME + 2, REGION_PORT_BASE, REGION_PORT_BASE + 5);
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 1, HOME + 2, REGION_PORT_BASE, REGION_PORT_BASE + 6);
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 2, HOME + 2, REGION_PORT_BASE, REGION_PORT_BASE + 7);
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 3, HOME + 2, REGION_PORT_BASE, REGION_PORT_BASE + 8);
            // Now add the connector (visible)
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 2, HOME + 1, REGION_PORT_BASE, REGION_PORT_BASE + 4);
            Thread.Sleep(500);

            mockScene.EventManager.TriggerOnMakeRootAgent(presence);
            Thread.Sleep(500);
            presence.RemotePresences.HandleDrawDistanceChanged(1024)?.Wait();
            Thread.Sleep(250);

            presences = presence.RemotePresences.GetRemotePresenceList();
            Assert.AreEqual(6, presences.Count);

            Assert.IsTrue(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 1, HOME)));
            Assert.IsTrue(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 2, HOME)));
            Assert.IsFalse(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 3, HOME)));

            Assert.IsTrue(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 2, HOME + 1)));

            Assert.IsTrue(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 0, HOME + 2)));
            Assert.IsTrue(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 1, HOME + 2)));
            Assert.IsTrue(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 2, HOME + 2)));
            Assert.IsFalse(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 3, HOME + 2)));

            // Clean up
            mockScene.EventManager.TriggerOnMakeChildAgent(presence);
        }
        [Test]
        public void TestRegionVisibilityConnected2of8()
        {
            //make sure our presence has no connections to neighbors since we should be child
            var presences = presence.RemotePresences.GetRemotePresenceList();
            Assert.AreEqual(0, presences.Count);

            //tell the mock scene that all neighbors are up

            // Add some neighbors (2 of 3 should be visible at long draw distance)
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 1, HOME, REGION_PORT_BASE, REGION_PORT_BASE + 1);
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 2, HOME, REGION_PORT_BASE, REGION_PORT_BASE + 2);
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 3, HOME, REGION_PORT_BASE, REGION_PORT_BASE + 3);
            // Now add 4 more unconnected distant regions (3 of 4 should be visible if connected)
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 0, HOME + 2, REGION_PORT_BASE, REGION_PORT_BASE + 5);
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 1, HOME + 2, REGION_PORT_BASE, REGION_PORT_BASE + 6);
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 2, HOME + 2, REGION_PORT_BASE, REGION_PORT_BASE + 7);
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 3, HOME + 2, REGION_PORT_BASE, REGION_PORT_BASE + 8);
            // Now add the connector (NOT visible)
            SurroundingRegionManagerTests.SendCreateRegionMessage(HOME - 3, HOME + 1, REGION_PORT_BASE, REGION_PORT_BASE + 4);
            Thread.Sleep(500);

            mockScene.EventManager.TriggerOnMakeRootAgent(presence);
            Thread.Sleep(500);
            presence.RemotePresences.HandleDrawDistanceChanged(1024)?.Wait();
            Thread.Sleep(250);

            presences = presence.RemotePresences.GetRemotePresenceList();
            Assert.AreEqual(2, presences.Count);

            Assert.IsTrue(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 1, HOME)));
            Assert.IsTrue(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 2, HOME)));
            Assert.IsFalse(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 3, HOME)));

            Assert.IsFalse(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 3, HOME + 1)));

            Assert.IsFalse(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 0, HOME + 2)));
            Assert.IsFalse(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 1, HOME + 2)));
            Assert.IsFalse(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 2, HOME + 2)));
            Assert.IsFalse(presence.RemotePresences.HasPresenceOnRegion(Util.RegionHandleFromLocation(HOME - 3, HOME + 2)));

            // Clean up
            mockScene.EventManager.TriggerOnMakeChildAgent(presence);
        }
    }
}
