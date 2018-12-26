using NUnit.Framework;
using Nini.Config;
using InWorldz.Testing;
using InWorldz.Phlox.Types;
using Nini.Ini;
using OpenMetaverse;
using System.Collections.Generic;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Region.FrameworkTests;
using OpenSim.Region.Framework.Scenes;

namespace InWorldz.Phlox.Engine.Tests
{

    [TestFixture]
    public class MaterialsTests
    {
        Scene world;
        MockScriptEngine engine;

        [SetUp]
        public void Setup()
        {
            var iniDoc = new IniDocument();
            var configSource = new IniConfigSource(iniDoc);
            configSource.AddConfig("InWorldz.Phlox");
            world = SceneHelper.CreateScene(9000, 1000, 1000);
            engine = new MockScriptEngine(world, configSource);
        }

        [TearDown]
        public void Teardown()
        {
            SceneHelper.TearDownScene(world);
        }

        [Test]
        public void TestGetMaterialsForFace()
        {
            var sop = SceneUtil.RandomSOP("Root", 1);
            var group = new SceneObjectGroup(sop);
            var lslSystemApi = new LSLSystemAPI(engine, sop, 0, UUID.Zero);
            var expectedResult = new LSLList(new List<object> { UUID.Zero.ToString(), new Vector3(1, 1, 0), new Vector3(0, 0, 0), 0 });
            var rules = new LSLList(new List<object> { ScriptBaseClass.PRIM_NORMAL, 0 });
            LSLList asList = lslSystemApi.llGetPrimitiveParams(rules);
            Assert.That(asList.ToString(), Is.EqualTo(expectedResult.ToString()));
        }

        [Test]
        public void TestSetAndClearMaterialsForFaceClearsEntry()
        {
            var sop = SceneUtil.RandomSOP("Root", 1);
            sop.OwnerMask = (uint)(PermissionMask.Copy | PermissionMask.Transfer | PermissionMask.Modify);
            var group = new SceneObjectGroup(sop);
            var lslSystemApi = new LSLSystemAPI(engine, sop, 0, UUID.Zero);

            // Check that its Zeroed
            var emptyResult = new LSLList(new List<object> { UUID.Zero.ToString(), new Vector3(1, 1, 0), new Vector3(0, 0, 0), 0 });
            var rules = new LSLList(new List<object> { ScriptBaseClass.PRIM_NORMAL, 0 });
            LSLList asList = lslSystemApi.llGetPrimitiveParams(rules);
            Assert.That(asList.ToString(), Is.EqualTo(emptyResult.ToString()));

            // Set a value and check it
            var textureId = UUID.Random();
            var faceZeroData = new LSLList(new List<object> { textureId.ToString(), new Vector3(1, 1, 0), new Vector3(0, 0, 0), 0 });
            var setMaterialsRequest = rules.Append(faceZeroData);
            lslSystemApi.llSetLinkPrimitiveParamsFast(0, setMaterialsRequest);
            LSLList setMaterialsResult = lslSystemApi.llGetLinkPrimitiveParams(0, rules);
            Assert.That(setMaterialsResult.ToString(), Is.EqualTo(faceZeroData.ToString()));

            // Clear it and Check thats its zeroed
            var clearMaterialsRequest = rules.Append(emptyResult);
            lslSystemApi.llSetLinkPrimitiveParamsFast(0, clearMaterialsRequest);
            LSLList clearMaterialsResult = lslSystemApi.llGetLinkPrimitiveParams(0, clearMaterialsRequest);
            Assert.That(clearMaterialsResult.ToString(), Is.EqualTo(emptyResult.ToString()));
        }

        [Test]
        public void TestSetAndGetMaterialsForFaceForFullPermSOP()
        {
            var sop = SceneUtil.RandomSOP("Root", 1);
            sop.OwnerMask = (uint)(PermissionMask.Copy | PermissionMask.Transfer | PermissionMask.Modify);
            var group = new SceneObjectGroup(sop);
            var lslSystemApi = new LSLSystemAPI(engine, sop, 0, UUID.Zero);

            var textureId = UUID.Random();
            var faceZeroData = new LSLList(new List<object> { textureId.ToString(), new Vector3(1, 1, 0), new Vector3(0, 0, 0), 0 });
            var rules = new LSLList(new List<object> { ScriptBaseClass.PRIM_NORMAL, 0 });
            var request = rules.Append(faceZeroData);

            lslSystemApi.llSetLinkPrimitiveParamsFast(0, request);
            LSLList result = lslSystemApi.llGetLinkPrimitiveParams(0, rules);
            Assert.That(result.ToString(), Is.EqualTo(faceZeroData.ToString()));
        }

        [Test]
        public void TestSetAndGetMaterialsForFaceForNoModSOP()
        {
            var sop = SceneUtil.RandomSOP("Root", 1);
            sop.OwnerMask = (uint)(PermissionMask.Copy | PermissionMask.Transfer);
            var group = new SceneObjectGroup(sop);
            var lslSystemApi = new LSLSystemAPI(engine, sop, 0, UUID.Zero);

            var textureId = UUID.Random();
            var faceZeroData = new LSLList(new List<object> { textureId.ToString(), new Vector3(1, 1, 0), new Vector3(0, 0, 0), 0 });
            var rules = new LSLList(new List<object> { ScriptBaseClass.PRIM_NORMAL, 0 });
            var request = rules.Append(faceZeroData);

            lslSystemApi.llSetLinkPrimitiveParamsFast(0, request);
            LSLList result = lslSystemApi.llGetLinkPrimitiveParams(0, rules);
            Assert.That(result.ToString(), !Is.EqualTo(faceZeroData.ToString()));
        }
    }
}
