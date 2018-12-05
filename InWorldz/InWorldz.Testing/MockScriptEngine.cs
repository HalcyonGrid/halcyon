using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;

namespace InWorldz.Testing
{
    public class MockScriptEngine : IScriptEngine
    {
        public MockScriptEngine(Scene world, IConfigSource configSource)
        {
            World = world;
            ConfigSource = configSource;
            Config = ConfigSource.Configs["Halcyon.Phlox"];
        }

        public IScriptWorkItem QueueEventHandler(object parms)
        {
            throw new System.NotImplementedException();
        }

        public Scene World { get; }

        public IScriptModule ScriptModule { get; }

        public bool PostScriptEvent(UUID itemID, EventParams parms)
        {
            throw new System.NotImplementedException();
        }

        public bool PostObjectEvent(uint localID, EventParams parms)
        {
            throw new System.NotImplementedException();
        }

        public DetectParams GetDetectParams(UUID item, int number)
        {
            throw new System.NotImplementedException();
        }

        public void SetMinEventDelay(UUID itemID, double delay)
        {
            throw new System.NotImplementedException();
        }

        public int GetStartParameter(UUID itemID)
        {
            throw new System.NotImplementedException();
        }

        public void SetScriptState(UUID itemID, bool state)
        {
            throw new System.NotImplementedException();
        }

        public bool GetScriptState(UUID itemID)
        {
            throw new System.NotImplementedException();
        }

        public void SetState(UUID itemID, string newState)
        {
            throw new System.NotImplementedException();
        }

        public void ApiResetScript(UUID itemID)
        {
            throw new System.NotImplementedException();
        }

        public void ResetScript(UUID itemID)
        {
            throw new System.NotImplementedException();
        }

        public IConfig Config { get; }

        public IConfigSource ConfigSource { get; }

        public string ScriptEngineName { get; }

        public IScriptApi GetApi(UUID itemID, string name)
        {
            throw new System.NotImplementedException();
        }

        public float GetTotalRuntime(UUID itemID)
        {
            throw new System.NotImplementedException();
        }

        public float GetAverageScriptTime(UUID itemID)
        {
            throw new System.NotImplementedException();
        }

        public int GetFreeMemory(UUID itemID)
        {
            throw new System.NotImplementedException();
        }

        public int GetUsedMemory(UUID itemID)
        {
            throw new System.NotImplementedException();
        }

        public int GetMaxMemory()
        {
            throw new System.NotImplementedException();
        }

        public float GetEventQueueFreeSpacePercentage(UUID itemID)
        {
            throw new System.NotImplementedException();
        }

        public void SetTimerEvent(uint localID, UUID itemID, float sec)
        {
            throw new System.NotImplementedException();
        }

        public void SysReturn(UUID itemId, object returnVal, int delay)
        {
            throw new System.NotImplementedException();
        }

        public void UpdateTouchData(uint localID, DetectParams[] det)
        {
            throw new System.NotImplementedException();
        }

        public void ChangeScriptEnabledLandStatus(SceneObjectGroup @group, bool enabled)
        {
            throw new System.NotImplementedException();
        }

        public bool ScriptsCanRun(ILandObject parcel, SceneObjectPart hostPart)
        {
            throw new System.NotImplementedException();
        }
    }
}