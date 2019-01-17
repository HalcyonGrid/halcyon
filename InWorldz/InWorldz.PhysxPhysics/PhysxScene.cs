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
using OpenSim.Region.Physics.Manager;
using log4net;
using System.Reflection;
using System.Threading;
using System.Diagnostics;
using OpenSim.Framework;
using System.Runtime.InteropServices;
using System.IO;
using OpenSim.Region.Interfaces;

namespace InWorldz.PhysxPhysics
{
    internal class PhysxScene : PhysicsScene
    {
        [DllImport("kernel32.dll")]
        static extern bool SetThreadPriority(IntPtr hThread, ThreadPriorityLevel nPriority);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentThread();

        public const int TIMESTEP = 15;
        public const float TIMESTEP_IN_SECONDS = TIMESTEP / 1000.0f;
        public const float DILATED_TIMESTEP_IN_SECONDS = TIMESTEP_IN_SECONDS * 2.0f;
        public const int SIMULATE_DELAY_TO_BEGIN_DILATION = (int)(TIMESTEP * 1.9f);

        private const int UPDATE_WATCHDOG_FRAMES = 200;
        private const int CHECK_EXPIRED_KINEMATIC_FRAMES = (int) ((1.0f / TIMESTEP_IN_SECONDS) * 60.0f);
        private const int UPDATE_FPS_FRAMES = 30;


        private static readonly ILog m_log
            = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);


        private static Debugging.PhysxErrorCallback s_ErrorCallback = new Debugging.PhysxErrorCallback();

        public PhysX.Material DEFAULT_MATERIAL;

        private static PhysX.Foundation _foundation;
        private static PhysX.Physics _physics;
        private PhysX.SceneDesc _sceneDesc;
        private PhysX.Scene _scene;

        private Meshing.TerrainMesher _terrainMesher;
        private TerrainManager _terrainMgr;
        private Meshing.MeshingStage _meshingStage;

        private Thread HeartbeatThread;
        private Thread TimingThread;
        private ManualResetEventSlim _timingSignal = new ManualResetEventSlim(true);
        private uint _lastSimulate;
        private int _frameNum;

        private OpenSim.Framework.LocklessQueue<Commands.ICommand> _currentCommandQueue = new OpenSim.Framework.LocklessQueue<Commands.ICommand>();
        private OpenSim.Framework.LocklessQueue<Commands.ICommand> _waitingCommandQueue = new OpenSim.Framework.LocklessQueue<Commands.ICommand>();

        private C5.HashSet<PhysxPrim> _allPrims = new C5.HashSet<PhysxPrim>();
        private C5.HashSet<PhysxPrim> _dynPrims = new C5.HashSet<PhysxPrim>();
        private C5.HashSet<PhysxPrim> _collisionRepeatPrims = new C5.HashSet<PhysxPrim>();

        private delegate void TaintHandler(PhysxPrim prim, TaintType taint);
        private readonly Dictionary<TaintType, TaintHandler> _taintHandlers;

        private volatile bool _stop = false;
        private bool _simulating = false;

        private KinematicManager _kinematicManager = new KinematicManager();

        private uint _lastFpsCalc = (uint)Environment.TickCount;
        private short _framesSinceLastFpsCalc = 0;
        private volatile float _currFps = 60.0f;

        private bool _gridmode = true;

        private class DelayedCommandInfo
        {
            public Commands.ICommand Initiator;
            public Dictionary<Type, LinkedListNode<Commands.ICommand>> TopCullables;
            public LinkedList<Commands.ICommand> Commands;
        }

        /// <summary>
        /// Stores commands that are being delayed pending execution of an async operation on a prim (such as meshing)
        /// This ensures a proper order of execution for physics commands
        /// </summary>
        private Dictionary<PhysxPrim, DelayedCommandInfo> _delayedCommands = new Dictionary<PhysxPrim, DelayedCommandInfo>();

        /// <summary>
        /// Manages the agent actors in this scene
        /// </summary>
        private PhysX.ControllerManager _controllerManager;

        /// <summary>
        /// Holds the avatar/character actors in the scene
        /// </summary>
        private C5.HashSet<PhysxCharacter> _charActors = new C5.HashSet<PhysxCharacter>();

        private SimulationEventCallbackDelegator _simEventDelegator;

        private MovingIntegerAverage _frameTimeAvg = new MovingIntegerAverage(10);

        private OpenMetaverse.UUID _regionId;

        internal OpenMetaverse.UUID RegionID
        {
            get
            {
                return _regionId;
            }
        }

        private Queue<Commands.ICommand> _freedCommands = new Queue<Commands.ICommand>();

        public override float SimulationFPS
        {
            get
            {
                return _currFps;
            }
        }

        public override bool Simulating
        {
            get
            {
                return _simulating;
            }
            set
            {
                _simulating = value;
            }
        }

        internal PhysX.Scene SceneImpl
        {
            get
            {
                return _scene;
            }
        }

        internal Meshing.MeshingStage MeshingStageImpl
        {
            get
            {
                return _meshingStage;
            }
        }

        internal PhysX.ControllerManager ControllerManager
        {
            get
            {
                return _controllerManager;
            }
        }

        public override int SimulationFrameTimeAvg
        {
            get 
            {
                return _frameTimeAvg.CalculateAverage();
            }
        }

        public uint CurrentFrameNum
        {
            get
            {
                return (uint)_frameNum;
            }
        }

        public override OpenSim.Region.Interfaces.ITerrainChannel TerrainChannel { get; set; }

        public override RegionSettings RegionSettings { get; set; }

        public Debugging.ContactDebugManager ContactDebug { get; set; }

        public IEnumerable<PhysxPrim> DynamicPrims
        {
            get
            {
                return _dynPrims;
            }
        }

        IMesher _mesher;
        public override IMesher Mesher 
        {
            get
            {
                return _mesher;
            }
        }

        internal OpenMetaverse.Vector2[] RegionWaterCurrents = null;
        internal OpenMetaverse.Vector2[] RegionWindGround = null;
        internal OpenMetaverse.Vector2[] RegionWindAloft = null;
        internal float[]                 RegionTerrainRanges = null;
        internal float[]                 RegionTerrainMaxHeights = null;

        public PhysxScene()
        {
            _taintHandlers = new Dictionary<TaintType, TaintHandler>()
            {
                {TaintType.MadeDynamic, HandlePrimMadeDynamic},
                {TaintType.MadeStatic, HandlePrimMadeStatic},
                {TaintType.ChangedScale, HandlePrimChangedShape},
                {TaintType.ChangedShape, HandlePrimChangedShape}
            };

            ContactDebug = new Debugging.ContactDebugManager(this);
        }

        private void CreateDefaults()
        {
            DEFAULT_MATERIAL = _physics.CreateMaterial(0.5f, 0.5f, 0.15f);
        }
        
        public override void Initialize(IMesher meshmerizer, Nini.Config.IConfigSource config, OpenMetaverse.UUID regionId)
        {
            _regionId = regionId;
            _mesher = meshmerizer;

            m_log.Info("[InWorldz.PhysxPhysics] Creating PhysX scene");

            if (config.Configs["InWorldz.PhysxPhysics"] != null)
            {
                Settings.Instance.UseVisualDebugger = config.Configs["InWorldz.PhysxPhysics"].GetBoolean("use_visual_debugger", false);
                Settings.Instance.UseCCD = config.Configs["InWorldz.PhysxPhysics"].GetBoolean("use_ccd", true);
                Settings.Instance.Gravity = config.Configs["InWorldz.PhysxPhysics"].GetFloat("gravity", -9.8f);
                Settings.Instance.ThrowOnSdkError = config.Configs["InWorldz.PhysxPhysics"].GetBoolean("throw_on_sdk_error", false);
                Settings.Instance.InstrumentMeshing = config.Configs["InWorldz.PhysxPhysics"].GetBoolean("instrument_meshing", false);
            }
            else
            {
                Settings.Instance.UseVisualDebugger = false;
                Settings.Instance.UseCCD = true;
                Settings.Instance.Gravity = -9.8f;
                Settings.Instance.ThrowOnSdkError = false;
                Settings.Instance.InstrumentMeshing = false;
            }

            Nini.Config.IConfig startupcfg = config.Configs["Startup"];
            if (startupcfg != null)
                _gridmode = startupcfg.GetBoolean("gridmode", false);

            if (_foundation == null)
            {
                _foundation = new PhysX.Foundation(s_ErrorCallback);
                _physics = new PhysX.Physics(_foundation);

                Material.BuiltinMaterialInit(_physics);
            }

            _sceneDesc = new PhysX.SceneDesc(null, Settings.Instance.UseCCD);
            _sceneDesc.Gravity = new PhysX.Math.Vector3(0f, 0f, Settings.Instance.Gravity);


            _simEventDelegator = new SimulationEventCallbackDelegator();
            _simEventDelegator.OnContactCallback += this.OnContact;
            _simEventDelegator.OnTriggerCallback += this.OnTrigger;
            _sceneDesc.SimulationEventCallback = _simEventDelegator;

            _scene = _physics.CreateScene(_sceneDesc);
            Preload();

            if (Settings.Instance.UseCCD)
            {
                _scene.SetFlag(PhysX.SceneFlag.SweptIntegration, true);
            }

            if (Settings.Instance.UseVisualDebugger && _physics.RemoteDebugger != null)
            {
                _physics.RemoteDebugger.Connect("localhost", null, null, PhysX.VisualDebugger.VisualDebuggerConnectionFlag.Debug, null);
            }

            _controllerManager = _scene.CreateControllerManager();

            CreateDefaults();

            _terrainMesher = new Meshing.TerrainMesher(_scene);
            _terrainMgr = new TerrainManager(_scene, _terrainMesher, regionId);
            _meshingStage = new Meshing.MeshingStage(_scene, meshmerizer, _terrainMesher);
            _meshingStage.OnShapeNeedsFreeing += new Meshing.MeshingStage.ShapeNeedsFreeingDelegate(_meshingStage_OnShapeNeedsFreeing);

            _kinematicManager = new KinematicManager();

            //fire up our work loop
            HeartbeatThread = Watchdog.StartThread(new ThreadStart(Heartbeat), "Physics Heartbeat",
                ThreadPriority.Normal, false);

            TimingThread = Watchdog.StartThread(new ThreadStart(DoTiming), string.Format("Physics Timing"),
                ThreadPriority.Highest, false);
        }

        private void Preload()
        {
            using (PhysX.Collection coll = _scene.Physics.CreateCollection())
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    coll.Deserialize(ms);
                }
            }

            
        }

        void _meshingStage_OnShapeNeedsFreeing(PhysicsShape shape)
        {
            this.QueueCommand(new Commands.DestroyShapeCmd { Shape = shape });
        }

        void DoTiming()
        {
            IntPtr thrdHandle = GetCurrentThread();
            SetThreadPriority(thrdHandle, ThreadPriorityLevel.TimeCritical);

            while (!_stop)
            {
                _timingSignal.Set();

                if (_frameNum % 100 == 0)
                {
                    Watchdog.UpdateThread();
                }

                Thread.Sleep(TIMESTEP);
                Interlocked.Increment(ref _frameNum);
            }
        }

        private void Heartbeat()
        {
            uint lastSimulateFrame = 0;
            while (!_stop)
            {
                uint startingFrame = (uint)Environment.TickCount;

                bool processedCommandsThisIteration = ProcessQueueCommands();
                uint uframe = (uint)_frameNum;

                if (_simulating && (uframe > lastSimulateFrame))
                {
                    uint tickCount = (uint)Environment.TickCount;
                    uint ticksSinceLastSimulate = Math.Max(tickCount - _lastSimulate, TIMESTEP);

                    _lastSimulate = (uint)Environment.TickCount;
                    lastSimulateFrame = uframe;

                    if (ticksSinceLastSimulate >= SIMULATE_DELAY_TO_BEGIN_DILATION)
                    {
                        Simulate(DILATED_TIMESTEP_IN_SECONDS, ticksSinceLastSimulate, uframe, true);
                        //m_log.DebugFormat("[PHYSICS]: Dilated simulate {0}", ticksSinceLastSimulate);
                    }
                    else
                    {
                        Simulate(ticksSinceLastSimulate * 0.001f, ticksSinceLastSimulate, uframe, false);
                    }

                    ++_framesSinceLastFpsCalc;
                    if (uframe % UPDATE_WATCHDOG_FRAMES == 0)
                    {
                        Watchdog.UpdateThread();
                    }
                    if (uframe % CHECK_EXPIRED_KINEMATIC_FRAMES == 0)
                    {
                        this.CheckForExpiredKinematics();
                    }
                    if (uframe % UPDATE_FPS_FRAMES == 0)
                    {
                        this.UpdateFpsCalc();
                        //CheckForPhysicsLongFramesAndDebug();
                    }
                }

                _frameTimeAvg.AddValue((uint)Environment.TickCount - startingFrame);
                ContactDebug.OnFramePassed();

                if (_currentCommandQueue.Count == 0)
                {
                    _timingSignal.Wait();
                }

                _timingSignal.Reset();
            }
        }

        private void CheckForPhysicsLongFramesAndDebug()
        {
             throw new NotImplementedException();
        }

        //Stopwatch sw = new Stopwatch();
        public override float Simulate(float timeStep, uint ticksSinceLastSimulate, uint frameNum, bool dilated)
        {
            //sw.Start();
            _scene.Simulate(timeStep);
            _scene.FetchResults(true);
            //sw.Stop();

            //m_log.DebugFormat("Simulate took: {0}", sw.Elapsed);
            //sw.Reset();

            ProcessDynamicPrimChanges(timeStep, ticksSinceLastSimulate, frameNum);
            ProcessCollisionRepeats(timeStep, ticksSinceLastSimulate, frameNum);

            //run avatar dynamics at 1/2 simulation speed (30fps nominal)
            if (frameNum % 2 == 0)
            {
                ProcessAvatarDynamics(timeStep, ticksSinceLastSimulate, frameNum);
            }

            return 0.0f;
        }

        private void UpdateFpsCalc()
        {
            uint msSinceLastCalc = (uint)Environment.TickCount - _lastFpsCalc;
            _currFps = _framesSinceLastFpsCalc / (msSinceLastCalc * 0.001f);

            _framesSinceLastFpsCalc = 0;
            _lastFpsCalc = (uint)Environment.TickCount;
            //Console.WriteLine("FPS: {0}", _currFps);

            //const float LOW_FPS_THRESHOLD = 54.0f;
            const float LOW_FPS_THRESHOLD = 25.0f;
            if (_currFps < LOW_FPS_THRESHOLD)
            {
                m_log.WarnFormat("[InWorldz.PhysxPhysics] Low physics FPS {0}", _currFps);
            }
        }

        private void CheckForExpiredKinematics()
        {
            _kinematicManager.CheckForExipiredKinematics();
        }

        public override PhysicsActor AddAvatar(string avName, OpenMetaverse.Vector3 position, OpenMetaverse.Quaternion rotation, OpenMetaverse.Vector3 size, bool isFlying, OpenMetaverse.Vector3 initialVelocity)
        {
            Commands.CreateCharacterCmd cmd = new Commands.CreateCharacterCmd(size.Z, size.X, position, rotation, isFlying, initialVelocity);
            this.QueueCommand(cmd);

            cmd.FinshedEvent.Wait();
            cmd.Dispose();

            return cmd.FinalActor;
        }

        public override void RemoveAvatar(PhysicsActor actor)
        {
            this.QueueCommand(new Commands.RemoveCharacterCmd((PhysxCharacter)actor));
        }

        public override void RemovePrim(PhysicsActor prim)
        {
            this.QueueCommand(new Commands.RemoveObjectCmd((PhysxPrim)prim));
        }

        /// <summary>
        /// The AddPrimShape calls are pseudo synchronous by default.
        /// </summary>
        /// <param name="primName"></param>
        /// <param name="pbs"></param>
        /// <param name="position"></param>
        /// <param name="size"></param>
        /// <param name="rotation"></param>
        /// <returns></returns>
        public override PhysicsActor AddPrimShape(string primName, AddPrimShapeFlags flags, BulkShapeData shapeData)
        {
            Commands.CreateObjectCmd createObj = new Commands.CreateObjectCmd(
                null, primName, shapeData.Pbs, shapeData.Position, shapeData.Size, shapeData.Rotation, shapeData.Velocity, 
                shapeData.AngularVelocity, Meshing.MeshingStage.SCULPT_MESH_LOD, flags, (Material)shapeData.Material, 
                shapeData.PhysicsProperties, shapeData.SerializedShapes, shapeData.ObjectReceivedOn);

            this.QueueCommand(createObj);

            createObj.FinshedEvent.Wait(); //wait for meshing and all prerequisites to complete
            createObj.Dispose();

            return createObj.FinalPrim;
        }

        public override void BulkAddPrimShapes(ICollection<BulkShapeData> shapeData, AddPrimShapeFlags flags)
        {
            Commands.BulkCreateObjectCmd createObjs = new Commands.BulkCreateObjectCmd(flags, shapeData);
            this.QueueCommand(createObjs);

            createObjs.FinishedEvent.Wait(); //wait for meshing and all prerequisites to complete

            createObjs.Dispose();
        }

        public override void AddPhysicsActorTaint(PhysicsActor prim)
        {
            //throw new NotSupportedException("AddPhysicsActorTaint must be called with a taint type");
        }

        public override void AddPhysicsActorTaint(PhysicsActor prim, TaintType taint)
        {
            TaintHandler handler;
            if (_taintHandlers.TryGetValue(taint, out handler))
            {
                handler((PhysxPrim)prim, taint);
            }
        }

        private void HandlePrimMadeStatic(PhysxPrim prim, TaintType taint)
        {
            this.QueueCommand(new Commands.SetPhysicalityCmd(prim, false));
        }

        private void HandlePrimMadeDynamic(PhysxPrim prim, TaintType taint)
        {
            this.QueueCommand(new Commands.SetPhysicalityCmd(prim, true));
        }

        private void HandlePrimChangedShape(PhysxPrim prim, TaintType taint)
        {
            this.QueueCommand(new Commands.ChangedShapeCmd(prim));
        }

        private void ProcessAvatarDynamics(float timeStep, uint ticksSinceLastSimulate, uint frameNum)
        {
            _controllerManager.ComputeInteractions(TimeSpan.FromMilliseconds(ticksSinceLastSimulate));

            foreach (PhysxCharacter character in _charActors)
            {
                character.SyncWithPhysics(timeStep, ticksSinceLastSimulate, frameNum);
            }
        }

        private void ProcessDynamicPrimChanges(float timeStep, uint ticksSinceLastSimulate, uint frameNum)
        {
            foreach (PhysicsActor actor in _dynPrims)
            {
                actor.SyncWithPhysics(timeStep, ticksSinceLastSimulate, frameNum);
            }
        }

        private void ProcessCollisionRepeats(float timeStep, uint ticksSinceLastSimulate, uint frameNum)
        {
            //repeat collision notifications every 4 frames (7.5 fps nominal)
            if (ticksSinceLastSimulate > 0 && frameNum % 4 == 0)
            {
                foreach (PhysicsActor actor in _collisionRepeatPrims)
                {
                    actor.DoCollisionRepeats(timeStep, ticksSinceLastSimulate, frameNum);
                }
            }
        }

        private bool ProcessQueueCommands()
        {
            OpenSim.Framework.LocklessQueue<Commands.ICommand> oldCurrentQueue = Interlocked.Exchange<OpenSim.Framework.LocklessQueue<Commands.ICommand>>(ref _currentCommandQueue, _waitingCommandQueue);

            try
            {
                if (oldCurrentQueue.Count == 0)
                {
                    _waitingCommandQueue = oldCurrentQueue;
                    return false;
                }

                while (oldCurrentQueue.Count > 0)
                {
                    Commands.ICommand cmd;
                    if (oldCurrentQueue.Dequeue(out cmd))
                    {
                        //remember, each command that is executed from the queue may free other
                        //commands that are waiting on that command to complete. therefore, after executing
                        //each command from the current queue, we must check to see if new commands
                        //have been put into the freed queue, and execute those. this ensures proper
                        //ordering of commands relative to each object
                        DelayOrExecuteCommand(cmd);
                        ExecuteFreedCommands();
                    }
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[PhysxScene]: ProcessQueueCommands exception:\n   {0}", e);
            }

            _waitingCommandQueue = oldCurrentQueue;


            return true;
        }

        private void ExecuteFreedCommands()
        {
            while (_freedCommands.Count > 0)
            {
                DelayOrExecuteCommand(_freedCommands.Dequeue());
            }
        }

        private void DelayOrExecuteCommand(Commands.ICommand cmd)
        {
            if (!this.CheckDelayedCommand(cmd))
            {
                //Util.DebugOut(cmd.ToString());
                cmd.Execute(this);
            }
        }

        private bool CheckDelayedCommand(Commands.ICommand cmd)
        {
            if (cmd.AffectsMultiplePrims())
            {
                bool delay = false;

                Commands.IMultiPrimCommand mpCommand = (Commands.IMultiPrimCommand)cmd;

                IEnumerable<PhysxPrim> targets = mpCommand.GetTargetPrims();
                foreach (PhysxPrim target in targets)
                {
                    delay |= CheckAddDelay(cmd, target);
                }

                //if (delay) m_log.DebugFormat("[InWorldz.PhysX] Delaying physics command pending command completion");
                return delay;
            }
            else
            {
                PhysxPrim target = cmd.GetTargetPrim();
                if (target == null)
                {
                    return false;
                }

                return CheckAddDelay(cmd, target);
            }
        }

        private bool CheckAddDelay(Commands.ICommand cmd, PhysxPrim target)
        {
            DelayedCommandInfo delayInfo;
            if (_delayedCommands.TryGetValue(target, out delayInfo) && delayInfo.Initiator != cmd)
            {
                //if we're already the last delayed command delayed behind the other command
                //for the given prim, we only need to be added once per command so we can safely
                //just return
                if (delayInfo.Commands.Count > 0 && delayInfo.Commands.Last.Value == cmd)
                {
                    return true;
                }

                //before adding this new command to wait, check to see if it is cullable.
                //if the command is cullable, and has the same targets, we replace it with this command
                //maintaining its position in the queue
                LinkedListNode<Commands.ICommand> cmdNode;
                if (cmd.IsCullable
                    && delayInfo.TopCullables != null && delayInfo.TopCullables.TryGetValue(cmd.GetType(), out cmdNode)
                    && HasSameTargets(cmdNode.Value, cmd))
                {
                    cmdNode.Value = cmd;
                    if (cmd.AffectsMultiplePrims()) ((Commands.IMultiPrimCommand)cmd).AddDelay();

                    return true;
                }
                else
                {
                    cmdNode = delayInfo.Commands.AddLast(cmd);
                    if (cmd.AffectsMultiplePrims()) ((Commands.IMultiPrimCommand)cmd).AddDelay();

                    if (cmd.IsCullable)
                    {
                        if (delayInfo.TopCullables == null)
                        {
                            delayInfo.TopCullables = new Dictionary<Type, LinkedListNode<Commands.ICommand>>();
                        }

                        delayInfo.TopCullables.Add(cmd.GetType(), cmdNode);
                    }

                    return true;
                }
            }

            return false;
        }

        private bool HasSameTargets(Commands.ICommand cmd1, Commands.ICommand cmd2)
        {
            if (cmd1.AffectsMultiplePrims() != cmd2.AffectsMultiplePrims())
            {
                m_log.ErrorFormat("[InWorldz.PhysxPhysics] Asked to check command targets for different command types!");
                return false;
            }

            if (cmd1.AffectsMultiplePrims())
            {
                IEnumerator<PhysxPrim> cmd1prims = ((Commands.IMultiPrimCommand)cmd1).GetTargetPrims().GetEnumerator();
                IEnumerator<PhysxPrim> cmd2prims = ((Commands.IMultiPrimCommand)cmd2).GetTargetPrims().GetEnumerator();

                bool cmd1end = false;
                bool cmd2end = false;

                while (true)
                {
                    cmd1end = cmd1prims.MoveNext();
                    cmd2end = cmd2prims.MoveNext();

                    if (cmd1end || cmd2end) break;

                    if (cmd1prims.Current != cmd2prims.Current) return false;
                }

                return cmd1end == cmd2end;
            }
            else
            {
                return cmd1.GetTargetPrim() == cmd2.GetTargetPrim();
            }
        }

        public override void GetResults()
        {
            
        }

        public override void SetTerrain(float[] heightMap, int revision)
        {
            this._meshingStage.MeshHeightfield(heightMap, 
                delegate(Tuple<PhysX.TriangleMesh, MemoryStream> meshedHeightfield)
                {
                    this.QueueCommand(new Commands.SetTerrainCmd(meshedHeightfield, revision));
                });
        }

        /// <summary>
        /// Called by the loader before the scene loop is running
        /// </summary>
        /// <param name="heightMap"></param>
        public override void SetStartupTerrain(float[] heightMap, int revision)
        {
            m_log.Info("[InWorldz.PhysxPhysics] Setting starup terrain");
            this.QueueCommand(new Commands.SetTerrainCmd(heightMap, true, revision));
        }

        public void SetTerrainSync(float[] heightMap, bool canLoadFromCache, int revision)
        {
            _terrainMgr.SetTerrainSync(heightMap, canLoadFromCache, revision);
        }

        internal void SetPremeshedTerrainSync(Tuple<PhysX.TriangleMesh, MemoryStream> premeshedTerrainData, int revision)
        {
            _terrainMgr.SetTerrainPremeshedSync(premeshedTerrainData, revision);
        }

        public override void SetWaterLevel(float baseheight)
        {
            //NOP
        }

        public override void Dispose()
        {
            _stop = true;
            TimingThread.Join();

            _timingSignal.Set();
            HeartbeatThread.Join();

            _meshingStage.InformCachesToPerformDirectDeletes();

            foreach (PhysxPrim actor in _allPrims)
            {
                actor.Dispose();
            }

            _meshingStage.Stop();

            _meshingStage.Dispose();
            _terrainMgr.Dispose();
        }

        public override Dictionary<uint, float> GetTopColliders()
        {
            return new Dictionary<uint, float>();
        }

        public override bool IsThreaded
        {
            get { return false; }
        }

        public void QueueCommand(Commands.ICommand command)
        {
            _currentCommandQueue.Enqueue(command);
            _timingSignal.Set();
        }

        internal void AddPrimSync(PhysxPrim prim, bool physical, bool kinematicStatic)
        {
            _allPrims.Add(prim);
            if (physical) _dynPrims.Add(prim);
            if (kinematicStatic) _kinematicManager.KinematicChanged(prim);
        }

        internal void PrimMadeDynamic(PhysxPrim prim)
        {
            _dynPrims.Add(prim);
            _kinematicManager.KinematicRemoved(prim);
        }

        internal void PrimMadeStaticKinematic(PhysxPrim actor)
        {
            _dynPrims.Remove(actor);
            _kinematicManager.KinematicChanged(actor);
        }

        internal void UpdateKinematic(PhysxPrim actor)
        {
            _kinematicManager.KinematicChanged(actor);
        }

        internal void RemovePrim(PhysxPrim prim)
        {
            _dynPrims.Remove(prim);
            _allPrims.Remove(prim);
            _collisionRepeatPrims.Remove(prim);
            _kinematicManager.KinematicRemoved(prim);
            _delayedCommands.Remove(prim);
            prim.Dispose();
        }

        internal void PrimBecameChild(PhysxPrim prim)
        {
            _dynPrims.Remove(prim);
            _allPrims.Remove(prim);
            _kinematicManager.KinematicRemoved(prim);
            _delayedCommands.Remove(prim);
        }

        internal void BeginDelayCommands(PhysxPrim prim, Commands.ICommand initiator)
        {
            DelayedCommandInfo info = new DelayedCommandInfo { Commands = new LinkedList<Commands.ICommand>(), Initiator = initiator };
            _delayedCommands.Add(prim, info);
        }

        internal void EndDelayCommands(PhysxPrim prim)
        {
            DelayedCommandInfo delayedCmds;
            if (_delayedCommands.TryGetValue(prim, out delayedCmds))
            {
                _delayedCommands.Remove(prim);

                foreach (Commands.ICommand cmd in delayedCmds.Commands)
                {
                    if (cmd.RemoveWaitAndCheckReady())
                    {
                        this.EnqueueFreedCommand(cmd);
                    }
                }
            }
        }

        /// <summary>
        /// Enqueues commands to be processed FIRST on the next physics spin
        /// This ensures that commands that were blocked and delayed for a specific
        /// object run first before other commands that may have gotten in just after 
        /// the delay was released
        /// </summary>
        /// <param name="cmd"></param>
        private void EnqueueFreedCommand(Commands.ICommand cmd)
        {
            _freedCommands.Enqueue(cmd);
        }

        internal void AddCharacterSync(PhysxCharacter newChar)
        {
            _charActors.Add(newChar);
        }

        internal void RemoveCharacterSync(PhysxCharacter physxCharacter)
        {
            _charActors.Remove(physxCharacter);
        }

        private void OnContact(PhysX.ContactPairHeader contactPairHeader, PhysX.ContactPair[] pairs)
        {
            if ((contactPairHeader.Flags & PhysX.ContactPairHeaderFlag.DeletedActor0) == 0)
            {
                bool wasPrim = TryInformPrimOfContactChange(contactPairHeader, pairs, 0);
                if (! wasPrim)
                {
                    TryInformCharacterOfContactChange(contactPairHeader, pairs, 0);
                }
            }

            if ((contactPairHeader.Flags & PhysX.ContactPairHeaderFlag.DeletedActor1) == 0)
            {
                bool wasPrim = TryInformPrimOfContactChange(contactPairHeader, pairs, 1);
                if (!wasPrim)
                {
                    TryInformCharacterOfContactChange(contactPairHeader, pairs, 1);
                }
            }
        }

        private bool TryInformCharacterOfContactChange(PhysX.ContactPairHeader contactPairHeader, PhysX.ContactPair[] pairs, int actorIndex)
        {
            PhysxCharacter character = contactPairHeader.Actors[actorIndex].UserData as PhysxCharacter;
            if (character != null)
            {
                character.OnContactChangeSync(contactPairHeader, pairs, actorIndex);
                return true;
            }
            else
            {
                return false;
            }
        }

        private bool TryInformPrimOfContactChange(PhysX.ContactPairHeader contactPairHeader, PhysX.ContactPair[] pairs, int actorIndex)
        {
            PhysxPrim prim = contactPairHeader.Actors[actorIndex].UserData as PhysxPrim;
            if (prim != null)
            {
                prim.OnContactChangeSync(contactPairHeader, pairs, actorIndex);
                return true;
            }
            else
            {
                return false;
            }
        }

        void OnTrigger(PhysX.TriggerPair[] pairs)
        {
            foreach (var pair in pairs)
            {
                if (pair.TriggerShape != null)
                {
                    PhysxPrim triggerPrim = pair.TriggerShape.Actor.UserData as PhysxPrim;
                    if (triggerPrim != null)
                    {
                        triggerPrim.OnTrigger(pair);
                    }
                }
            }
        }

        internal void WakeAllDynamics()
        {
            foreach (PhysxPrim prim in _dynPrims)
            {
                prim.WakeUp();
            }
        }

        public override IMaterial FindMaterialImpl(OpenMetaverse.Material materialEnum)
        {
            return Material.FindImpl(materialEnum);
        }

        public void PrimWantsCollisionRepeat(PhysxPrim prim)
        {
            _collisionRepeatPrims.Add(prim);
        }

        public void PrimDisabledCollisionRepeat(PhysxPrim prim)
        {
            _collisionRepeatPrims.Remove(prim);
        }

        public void ForEachCharacter(Action<PhysxCharacter> eachCallback)
        {
            foreach (PhysxCharacter character in _charActors)
            {
                eachCallback(character);
            }
        }

        public override void DumpCollisionInfo()
        {
            ContactDebug.OnDataReady += new Debugging.ContactDebugManager.DataCallback(ContactDebug_OnDataReady);
            ContactDebug.BeginCollectingContactData();
        }

        void ContactDebug_OnDataReady(IEnumerable<KeyValuePair<PhysX.Actor, int>> data)
        {
            m_log.InfoFormat("[InWorldz.PhysX.Debugging] Contact Dump --");

            foreach (var kvp in data)
            {
                if (kvp.Key.UserData == null) continue;

                PhysxPrim prim = kvp.Key.UserData as PhysxPrim;
                if (prim == null)
                {
                    m_log.DebugFormat("[InWorldz.PhysX.Debugging]: (object) {0}", kvp.Value);
                }
                else
                {
                    OpenMetaverse.Vector3 pos = prim.Position;
                    m_log.DebugFormat("[InWorldz.PhysX.Debugging]: {0} {1} at {2}/{3}/{4}", prim.SOPName, kvp.Value,
                        (int)(pos.X + 0.5), (int)(pos.Y + 0.5), (int)(pos.Z + 0.5));
                }
            }

            ContactDebug.OnDataReady -= new Debugging.ContactDebugManager.DataCallback(ContactDebug_OnDataReady);
        }

        internal void DisableKinematicTransitionTracking(PhysxPrim physxPrim)
        {
            _kinematicManager.KinematicRemoved(physxPrim);
        }

        internal void ChildPrimDeleted(PhysxPrim childPrim)
        {
            _collisionRepeatPrims.Remove(childPrim);
        }

        public override void SendPhysicsWindData(OpenMetaverse.Vector2[] sea, OpenMetaverse.Vector2[] gnd, OpenMetaverse.Vector2[] air,
                                                 float[] ranges, float[] maxheights)
        {
            QueueCommand(
                new Commands.GenericSyncCmd( 
                    (PhysxScene scene) => 
                    {
                        scene.RegionWaterCurrents   = sea;
                        scene.RegionWindGround      = gnd;
                        scene.RegionWindAloft       = air;
                        scene.RegionTerrainRanges   = ranges;
                        scene.RegionTerrainMaxHeights = maxheights;
                        
                    }
            ));
        }

        public override List<ContactResult> RayCastWorld(OpenMetaverse.Vector3 start, OpenMetaverse.Vector3 direction, float distance,
            int hitAmounts)
        {
            List<ContactResult> contactResults = new List<ContactResult>();
            AutoResetEvent ev = new AutoResetEvent(false);
            RayCastWorld(start, direction, distance, hitAmounts, (r) =>
                {
                    contactResults = r;
                    ev.Set();
                });
            ev.WaitOne(1000);
            return contactResults;
        }

        public override void RayCastWorld(OpenMetaverse.Vector3 start, OpenMetaverse.Vector3 direction, float distance,
            int hitAmounts, Action<List<ContactResult>> result)
        {
            QueueCommand(
                new Commands.GenericSyncCmd(
                    (PhysxScene scene) =>
                    {
                        GetRayCastResults(start, direction, distance, hitAmounts, result, scene);
                    }
            ));
        }

        private void GetRayCastResults(OpenMetaverse.Vector3 start, OpenMetaverse.Vector3 direction,
            float distance, int hitAmounts, Action<List<ContactResult>> result, PhysxScene scene)
        {
            int buffercount = 16;
            int maxbuffercount = 1024;
            PhysX.RaycastHit[] hits = null;
            direction = OpenMetaverse.Vector3.Normalize(direction);

            //Increase the buffer count if the call indicates overflow. Prevent infinite loops.
            while (hits == null && buffercount <= maxbuffercount)
            {
                hits = SceneImpl.RaycastMultiple(PhysUtil.OmvVectorToPhysx(start),
                                                        PhysUtil.OmvVectorToPhysx(direction),
                                                        distance, PhysX.SceneQueryFlags.All,
                                                        buffercount,
                                                        null);
                buffercount *= 2;
            }

            List<ContactResult> contactResults = new List<ContactResult>();
            if (hits != null)
            {
                List<PhysX.RaycastHit> hitsSorted = new List<PhysX.RaycastHit>(hits);
                hitsSorted.Sort((a, b) => a.Distance.CompareTo(b.Distance));

                int count = 0;
                foreach (PhysX.RaycastHit hit in hitsSorted)
                {
                    contactResults.Add(new ContactResult()
                    {
                        Distance = hit.Distance,
                        FaceIndex = hit.FaceIndex,
                        CollisionActor = hit.Shape.Actor.UserData as PhysicsActor,
                        Position = PhysUtil.PhysxVectorToOmv(hit.Impact),
                        Normal = PhysUtil.PhysxVectorToOmv(hit.Normal),
                    });
                    if (++count >= hitAmounts)
                        break;
                }
            }
            result(contactResults);
        }
    }
}
