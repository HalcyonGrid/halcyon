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
using OpenSim.Framework;
using System.IO;

using OpenMetaverse;
using Halcyon.Phlox.VM;
using log4net;
using System.Reflection;
using System.Diagnostics;

using Halcyon.Phlox.Serialization;
using OpenSim.Region.ScriptEngine.Shared.Api;

namespace Halcyon.Phlox.Engine
{
    /// <summary>
    /// Loads a script. Searches for it from the most efficient source (bytecode already in memory)
    /// to the least efficient source (asset request and compilation)
    /// </summary>
    internal class ScriptLoader
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        
        private IAssetCache _assetCache;

        private ExecutionScheduler _exeScheduler;

        private StateManager _stateManager;
        public StateManager StateManager
        {
            get
            {
                return _stateManager;
            }

            set
            {
                _stateManager = value;
            }
        }
        
        /// <summary>
        /// A script that is loaded into memory with a reference count
        /// </summary>
        private class LoadedScript
        {
            public CompiledScript Script;
            public int RefCount;
        }

        /// <summary>
        /// Scripts loaded with the script asset UUID as the key
        /// </summary>
        private Dictionary<UUID, LoadedScript> _loadedScripts = new Dictionary<UUID, LoadedScript>();

        /// <summary>
        /// Scripts waiting to be loaded or unloaded, accessing this list must be done with a lock as requests
        /// come from outside of the engine thread
        /// </summary>
        private LinkedList<LoadUnloadRequest> _outstandingLoadUnloadRequests = new LinkedList<LoadUnloadRequest>();

        /// <summary>
        /// Locks the _waitingForAssetServer list and the _waitingForCompilation queue
        /// </summary>
        private object _assetAndCompileLock = new object();

        /// <summary>
        /// Load requests waiting on the asset server for the script
        /// </summary>
        private Dictionary<UUID, List<LoadUnloadRequest>> _waitingForAssetServer = new Dictionary<UUID, List<LoadUnloadRequest>>();
        
        /// <summary>
        /// Called after an asset has been retrieved from the asset server to wake up the master scheduler
        /// </summary>
        private WorkArrivedDelegate _workArrived;

        private class LoadedAsset
        {
            public uint LocalId;
            public UUID ItemId;
            public UUID AssetId;
            public string ScriptText;
            public List<LoadUnloadRequest> Requests;
        }

        /// <summary>
        /// List of assets waiting for compilation
        /// </summary>
        private Queue<LoadedAsset> _waitingForCompilation = new Queue<LoadedAsset>();

        /// <summary>
        /// Used to measure compilation time
        /// </summary>
        private Stopwatch _stopwatch = new Stopwatch();

        /// <summary>
        /// The parent engine interface
        /// </summary>
        private EngineInterface _engineInterface;

        /// <summary>
        /// Requests for loaded script bytecode get queued here. This list must use locking because
        /// the requests come from outside of the engine thread
        /// </summary>
        private Queue<RetrieveBytecodeRequest> _outstandingBytecodeRequests = new Queue<RetrieveBytecodeRequest>();

        /// <summary>
        /// The number of scripts that have been unloaded that we keep in the cache
        /// </summary>
        private const int MAX_CACHED_UNLOADED_SCRIPTS = 128;

        /// <summary>
        /// Cache that is utilized for scripts that have been unloaded to limit the 
        /// amount of disk thrasing that can happen when scripted objects are repeatedly 
        /// added and deleted from the scene
        /// </summary>
        private LRUCache<UUID, CompiledScript> _unloadedScriptCache = new LRUCache<UUID, CompiledScript>(MAX_CACHED_UNLOADED_SCRIPTS);


        public ScriptLoader(IAssetCache assetCache, ExecutionScheduler exeScheduler, 
            WorkArrivedDelegate workArrived, EngineInterface engineInterface)
        {
            _assetCache = assetCache;
            _exeScheduler = exeScheduler;
            _workArrived = workArrived;
            _engineInterface = engineInterface;
            this.CreateDirectoryStructure();
        }

        private void CreateDirectoryStructure()
        {
            Directory.CreateDirectory(PhloxConstants.COMPILE_CACHE_DIR);
        }

        public void PostLoadUnloadRequest(LoadUnloadRequest req)
        {
            lock (_outstandingLoadUnloadRequests)
            {
                _outstandingLoadUnloadRequests.AddFirst(req);
            }

            _workArrived();
        }

        public WorkStatus DoWork()
        {
            //do we have outstanding unload requests?
            bool lrqStatus = CheckAndPerformLoadUnloadRequest();

            //do we have outstanding asset responses?
            bool compStatus = CheckAndCompileScript();

            //how about requests for compiled script assets?
            bool byteCodeReqStatus = CheckAndRetrieveBytecodes();

            return new WorkStatus
            {
                WorkWasDone = lrqStatus || compStatus || byteCodeReqStatus,
                WorkIsPending = this.WorkIsPending(), 
                NextWakeUpTime = UInt64.MaxValue
            };
        }

        // Returns true if it found the script loaded and decremented the refcount.
        private bool PerformUnloadRequest(LoadUnloadRequest unloadReq)
        {
            bool rc = false;

            // Find based on the item ID
            VM.Interpreter loadedScript = _exeScheduler.FindScript(unloadReq.ItemId);

            if (loadedScript != null)
            {
                LoadedScript reffedScript;

                //tell the scheduler the script needs to be pulled
                _exeScheduler.DoUnload(unloadReq.ItemId, unloadReq);

                //tell the async command manager that it needs to remove this script
                AsyncCommandManager.RemoveScript(_engineInterface, unloadReq.LocalId, unloadReq.ItemId);

                //tell the state manager to remove this script
                _stateManager.ScriptUnloaded(unloadReq.ItemId);

                //decref and unload if refcount is 0 based on the Asset ID
                if (_loadedScripts.TryGetValue(loadedScript.Script.AssetId, out reffedScript))
                {
                    if (--reffedScript.RefCount == 0)
                    {
                        _loadedScripts.Remove(loadedScript.Script.AssetId);
                        _unloadedScriptCache.Add(loadedScript.Script.AssetId, loadedScript.Script);
                    }

                    rc = true;
                }
            }

            // Callback here because if the Item ID was not found, the callback would be meaningless
            if (unloadReq.PostUnloadCallback != null)
            {
                // Now call the completion callback (e.g. now that it is safe for the script to be removed in the delete case).
                unloadReq.PostUnloadCallback(unloadReq.Prim, unloadReq.ItemId, unloadReq.CallbackParams.AllowedDrop, unloadReq.CallbackParams.FireEvents, unloadReq.CallbackParams.ReplaceArgs);
            }

            return rc;
        }

        private bool CheckAndCompileScript()
        {
            LoadedAsset loadedScript;
            lock (_assetAndCompileLock)
            {
                if (_waitingForCompilation.Count == 0)
                {
                    return false;
                }

                loadedScript = _waitingForCompilation.Dequeue();
            }


            List<Types.ILSLListener> subListeners = new List<Types.ILSLListener>();
            foreach (LoadUnloadRequest request in loadedScript.Requests)
            {
                if (request.Listener != null)
                {
                    subListeners.Add(new CompilationListenerAdaptor(request.Listener));
                }
            }

            //perform compilation
            Glue.CompilerFrontend frontend;
            if (subListeners.Count == 0)
            {
                frontend = new Glue.CompilerFrontend(new LogOutputListener(loadedScript.Requests), ".");
            }
            else
            {
                frontend = new Glue.CompilerFrontend(new MulticastCompilerListener(subListeners), ".");
            }

            try
            {
                _stopwatch.Start();
                CompiledScript comp = frontend.Compile(loadedScript.ScriptText);
                _stopwatch.Stop();

                if (comp != null)
                {
                    comp.AssetId = loadedScript.AssetId;

                    //save the script
                    this.CacheCompiledScript(comp);

                    _log.InfoFormat("[Phlox]: Compiled script {0} ({1}ms) in {2}", loadedScript.AssetId, _stopwatch.ElapsedMilliseconds, loadedScript.LocalId);

                    foreach (LoadUnloadRequest request in loadedScript.Requests)
                    {
                        this.BeginScriptRun(request, comp);
                    }

                    _loadedScripts[comp.AssetId] = new LoadedScript { Script = comp, RefCount = loadedScript.Requests.Count };
                }
                else
                {
                    _log.ErrorFormat("[Phlox]: Compilation failed for {0} item {1} in {2}", loadedScript.AssetId, loadedScript.ItemId, loadedScript.LocalId);
                }

                return true;
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Phlox]: Exception while compiling {0} item {1} in {2}: {3}", loadedScript.AssetId, loadedScript.ItemId, loadedScript.LocalId, e);
            }
            finally
            {
                frontend.Listener.CompilationFinished();
                _stopwatch.Reset();
            }

            return false;
        }

        private void CacheCompiledScript(CompiledScript comp)
        {
            string scriptCacheDir = this.LookupDirectoryFromId(PhloxConstants.COMPILE_CACHE_DIR, comp.AssetId);
            Directory.CreateDirectory(scriptCacheDir);
            string scriptPath = Path.Combine(scriptCacheDir, comp.AssetId.ToString() + PhloxConstants.COMPILED_SCRIPT_EXTENSION);

            SerializedScript script = SerializedScript.FromCompiledScript(comp);
            using (FileStream f = File.Open(scriptPath, FileMode.Create))
            {
                ProtoBuf.Serializer.Serialize(f, script);
                f.Close();
            }
        }

        private bool CheckAndPerformLoadUnloadRequest()
        {
            LinkedListNode<LoadUnloadRequest> lrq;
            lock (_outstandingLoadUnloadRequests)
            {
                if (_outstandingLoadUnloadRequests.Count > 0)
                {
                    lrq = _outstandingLoadUnloadRequests.First;
                    _outstandingLoadUnloadRequests.RemoveFirst();
                }
                else
                {
                    return false;
                }
            }

            switch (lrq.Value.RequestType)
            {
                case LoadUnloadRequest.LUType.Load:
                    return this.PerformLoadRequest(lrq.Value);

                case LoadUnloadRequest.LUType.Unload:
                    return this.PerformUnloadRequest(lrq.Value);

                case LoadUnloadRequest.LUType.Reload:
                    return this.PerformReloadRequest(lrq.Value);
            }

            return false;
        }

        private bool PerformReloadRequest(LoadUnloadRequest loadUnloadRequest)
        {
            return this.PerformLoadRequest(loadUnloadRequest);
        }

        private bool PerformLoadRequest(LoadUnloadRequest lrq)
        {
            //look up the asset id
            UUID scriptAssetId = this.FindAssetId(lrq);

            if (scriptAssetId != UUID.Zero)
            {
                try
                {
                    //we try to load a script the most efficient way possible.
                    //these if statements are ordered from most efficient to least

                    if (TryStartSharedScript(scriptAssetId, lrq))
                    {
                        return true;
                    }
                    else if (TryStartScriptFromUnloadedCache(scriptAssetId, lrq))
                    {
                        return true;
                    }
                    else if (TryStartScriptFromSerializedData(scriptAssetId, lrq))
                    {
                        return true;
                    }
                    else if (TryStartCachedScript(scriptAssetId, lrq))
                    {
                        return true;
                    }
                    else
                    {
                        SubmitAssetLoadRequest(lrq);
                        return true;
                    }
                }
                catch (LoaderException e)
                {
                    _log.ErrorFormat("[Phlox]: Could not load script: " + e.Message);
                }
            }

            return false;
        }

        /// <summary>
        /// This function attempts to load a script from the unloaded script cache
        /// </summary>
        /// <param name="scriptAssetId">The asset ID for the script to be loaded</param>
        /// <param name="lrq">The request that is causing the load</param>
        /// <returns></returns>
        private bool TryStartScriptFromUnloadedCache(UUID scriptAssetId, LoadUnloadRequest lrq)
        {
            CompiledScript compiledScript;

            if (_unloadedScriptCache.TryGetValue(scriptAssetId, out compiledScript))
            {
                _log.InfoFormat("[Phlox]: Starting recovered script {0} in item {1} group {2} part {3}", scriptAssetId, lrq.ItemId, lrq.Prim.ParentGroup.LocalId, lrq.Prim.LocalId);

                //remove the script from the unloaded cache for good measure since it is now loaded again
                _unloadedScriptCache.Remove(scriptAssetId);

                //check the part in the load request for this script.
                //even though we're not using the passed in script asset, 
                //we should still do cleanup
                ClearSerializedScriptData(lrq, scriptAssetId);

                BeginScriptRun(lrq, compiledScript);
                _loadedScripts[scriptAssetId] = new LoadedScript { Script = compiledScript, RefCount = 1 };
                return true;
            }

            return false;
        }

        private bool TryStartScriptFromSerializedData(UUID scriptAssetId, LoadUnloadRequest lrq)
        {
            if (lrq.Prim.SerializedScriptByteCode == null) return false;

            byte[] serializedCompiledScript;
            if (lrq.Prim.SerializedScriptByteCode.TryGetValue(scriptAssetId, out serializedCompiledScript))
            {
                //deserialize and load
                using (MemoryStream ms = new MemoryStream(serializedCompiledScript))
                {
                    Serialization.SerializedScript script = ProtoBuf.Serializer.Deserialize<Serialization.SerializedScript>(ms);
                    if (script == null)
                    {
                        _log.ErrorFormat("[Phlox]: LOADER: Script data contained in prim failed to deserialize");
                        ClearSerializedScriptData(lrq, scriptAssetId);
                        return false;
                    }
                    else
                    {
                        CompiledScript compiledScript = script.ToCompiledScript();

                        _log.InfoFormat("[Phlox]: Starting contained script {0} in item {1} group {2} part {3}", scriptAssetId, lrq.ItemId, lrq.Prim.ParentGroup.LocalId, lrq.Prim.LocalId);

                        BeginScriptRun(lrq, compiledScript);
                        _loadedScripts[scriptAssetId] = new LoadedScript { Script = compiledScript, RefCount = 1 };
                        return true;
                    }
                }
            }

            return false;
        }

        private bool SubmitAssetLoadRequest(LoadUnloadRequest lrq)
        {
            UUID scriptAssetId = this.FindAssetId(lrq);

            if (scriptAssetId != UUID.Zero)
            {
                if (AddAssetWait(scriptAssetId, lrq))
                {
                    _assetCache.GetAsset(scriptAssetId, delegate(UUID i, AssetBase a) { this.AssetReceived(lrq.Prim.LocalId, lrq.ItemId, i, a); }, AssetRequestInfo.InternalRequest());
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Tracks a wait on the asset server
        /// </summary>
        /// <param name="scriptAssetId"></param>
        /// <param name="lrq"></param>
        /// <returns>True if a request should be sent, false if not (already in progress)</returns>
        private bool AddAssetWait(UUID scriptAssetId, LoadUnloadRequest lrq)
        {
            lock (_assetAndCompileLock)
            {
                List<LoadUnloadRequest> waitingRequests;
                if (_waitingForAssetServer.TryGetValue(scriptAssetId, out waitingRequests))
                {
                    //we already have another script waiting for load with the same UUID,
                    //add this one to the waiting list
                    waitingRequests.Add(lrq);
                    return false;
                }
                else
                {
                    //no one waiting for this asset yet, create a new entry
                    _waitingForAssetServer.Add(scriptAssetId, new List<LoadUnloadRequest>() { lrq });
                    return true;
                }
            }
        }

        private void AssetReceived(uint localId, UUID itemId, UUID assetId, AssetBase asset)
        {
            lock (_assetAndCompileLock)
            {
                List<LoadUnloadRequest> waitingRequests;
                if (_waitingForAssetServer.TryGetValue(assetId, out waitingRequests))
                {
                    _waitingForAssetServer.Remove(assetId);

                    if (asset == null)
                    {
                        _log.ErrorFormat("[Phlox]: Asset not found for script {0}", assetId);
                        return;
                    }

                    //we have the asset, verify it, and signal that work has arrived
                    if (asset.Type != (sbyte)AssetType.LSLText)
                    {
                        _log.ErrorFormat("[Phlox]: Invalid asset type received from asset server. " +
                            "Expected LSLText, got {0}", asset.Type);
                        return;
                    }

                    string scriptText = OpenMetaverse.Utils.BytesToString(asset.Data);
                    _waitingForCompilation.Enqueue(new LoadedAsset { LocalId = localId, ItemId = itemId, AssetId = assetId, Requests = waitingRequests, ScriptText = scriptText });
                    _workArrived();
                }
                else
                {
                    _log.ErrorFormat("[Phlox]: Received an asset for a script im not waiting for {0}", assetId);
                }
            }
        }

        /// <summary>
        /// Try to load a script from disk and start it up
        /// </summary>
        /// <param name="scriptAssetId"></param>
        /// <param name="lrq"></param>
        /// <returns></returns>
        private bool TryStartCachedScript(UUID scriptAssetId, LoadUnloadRequest lrq)
        {
            //check in the cache directory for compiled scripts
            if (ScriptIsCached(scriptAssetId))
            {
                CompiledScript script = LoadScriptFromDisk(scriptAssetId);

                _log.InfoFormat("[Phlox]: Starting cached script {0} in item {1} owner {2} part {3}", scriptAssetId, lrq.ItemId, lrq.Prim.ParentGroup.OwnerID, lrq.Prim.LocalId);

                BeginScriptRun(lrq, script);
                _loadedScripts[scriptAssetId] = new LoadedScript { Script = script, RefCount = 1 };
                return true;
            }

            return false;
        }

        private void BeginScriptRun(LoadUnloadRequest lrq, CompiledScript script)
        {
            RuntimeState state = this.TryLoadState(lrq);

            //if this is a reload, we unload first
            if (lrq.RequestType == LoadUnloadRequest.LUType.Reload)
            {
                this.PerformUnloadRequest(lrq);
            }

            try
            {
                _exeScheduler.FinishedLoading(lrq, script, state);
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Phlox]: Error when informing scheduler of script load. Script: {0} Item: {1} Group: {2} Part: {3}. {4}", script.AssetId, lrq.ItemId, lrq.Prim.ParentGroup.LocalId, lrq.Prim.LocalId, e);
                throw;
            }
        }

        private CompiledScript LoadScriptFromDisk(UUID scriptAssetId)
        {
            string cacheDir = this.LookupDirectoryFromId(PhloxConstants.COMPILE_CACHE_DIR, scriptAssetId);
            string scriptPath = Path.Combine(cacheDir, scriptAssetId.ToString() + PhloxConstants.COMPILED_SCRIPT_EXTENSION);
            if (File.Exists(scriptPath))
            {
                SerializedScript serScript;
                using (var file = File.OpenRead(scriptPath))
                {
                    serScript = ProtoBuf.Serializer.Deserialize<SerializedScript>(file);
                    if (serScript == null)
                    {
                        throw new LoaderException(String.Format("Script {0} failed to deserialize from source at {1}", scriptAssetId,
                            scriptPath));
                    }
                }

                return serScript.ToCompiledScript();
            }
            else
            {
                throw new LoaderException(String.Format("Script {0} could not be found at {1}", scriptAssetId, scriptPath));
            }
        }

        private string LookupDirectoryFromId(string baseDir, UUID id)
        {
            return Path.Combine(baseDir, id.ToString().Substring(0, PhloxConstants.CACHE_PREFIX_LEN));
        }

        private bool ScriptIsCached(UUID scriptAssetId)
        {
            string cacheDir = this.LookupDirectoryFromId(PhloxConstants.COMPILE_CACHE_DIR, scriptAssetId);
            string scriptPath = Path.Combine(cacheDir, scriptAssetId.ToString() + PhloxConstants.COMPILED_SCRIPT_EXTENSION);
            if (File.Exists(scriptPath))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// If the script asset is found cached, we start a new instance of it
        /// </summary>
        /// <param name="scriptAssetId"></param>
        /// <returns></returns>
        private bool TryStartSharedScript(UUID scriptAssetId, LoadUnloadRequest loadRequest)
        {
            LoadedScript script;
            if (_loadedScripts.TryGetValue(scriptAssetId, out script))
            {
                //only adjust ref counts if this is not a reload
                if (loadRequest.RequestType != LoadUnloadRequest.LUType.Reload)
                {
                    script.RefCount++;
                }

                //check the part in the load request for this script.
                //even though we're not using the passed in script asset, 
                //we should still do cleanup
                ClearSerializedScriptData(loadRequest, scriptAssetId);

                _log.InfoFormat("[Phlox]: Starting shared script {0} in item {1} owner {2} part {3}", scriptAssetId, loadRequest.ItemId, loadRequest.Prim.ParentGroup.OwnerID, loadRequest.Prim.LocalId);
                BeginScriptRun(loadRequest, script.Script);

                return true;
            }

            return false;
        }

        private void ClearSerializedScriptData(LoadUnloadRequest loadRequest, UUID scriptAssetId)
        {
            Dictionary<UUID, byte[]> dictionary = loadRequest.Prim.SerializedScriptByteCode;
            if (dictionary == null) return;

            if (dictionary.ContainsKey(scriptAssetId))
            {
                dictionary.Remove(scriptAssetId);
            }

            if (dictionary.Count == 0)
            {
                loadRequest.Prim.SerializedScriptByteCode = null;
            }
        }

        /// <summary>
        /// Attempt to load state from the correct source
        /// </summary>
        /// <param name="loadRequest">The request that sparked the script load</param>
        /// <returns>The runtime state or null</returns>
        private RuntimeState TryLoadState(LoadUnloadRequest loadRequest)
        {
            try
            {
                switch (loadRequest.StateSource)
                {
                    case OpenSim.Region.Framework.ScriptStateSource.RegionLocalDisk:
                        return _stateManager.LoadStateFromDisk(loadRequest.ItemId);

                    case OpenSim.Region.Framework.ScriptStateSource.PrimData:
                        return _stateManager.LoadStateFromPrim(loadRequest.ItemId, loadRequest.OldItemId, loadRequest.Prim);
                }

                return null;
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Phlox]: Loading script state failed for {0}, {1}",
                    loadRequest.ItemId, e);
            }

            return null;
        }

        private bool WorkIsPending()
        {
            lock (_outstandingLoadUnloadRequests)
            {
                if (_outstandingLoadUnloadRequests.Count > 0 || _waitingForCompilation.Count > 0)
                {
                    return true;
                }
            }

            lock (_outstandingBytecodeRequests)
            {
                if (_outstandingBytecodeRequests.Count > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private UUID FindAssetId(LoadUnloadRequest lrq)
        {
            TaskInventoryItem item = lrq.Prim.Inventory.GetInventoryItem(lrq.ItemId);
            if (item == null)
            {
                _log.ErrorFormat("[Phlox]: Could not find inventory item {0} in primitive {1} ({2}) to start script",
                    lrq.ItemId, lrq.Prim.Name, lrq.Prim.UUID);
                return UUID.Zero;
            }
            else
            {
                return item.AssetID;
            }
        }

        internal void Stop()
        {
            
        }

        internal void PostRetrieveByteCodeRequest(RetrieveBytecodeRequest rbRequest)
        {
            lock (_outstandingBytecodeRequests)
            {
                _outstandingBytecodeRequests.Enqueue(rbRequest);
            }

            _workArrived();
        }

        private bool CheckAndRetrieveBytecodes()
        {
            List<RetrieveBytecodeRequest> reqs;
            lock (_outstandingBytecodeRequests)
            {
                if (_outstandingBytecodeRequests.Count == 0) return false;

                reqs = new List<RetrieveBytecodeRequest>(_outstandingBytecodeRequests);
                _outstandingBytecodeRequests.Clear();
            }

            foreach (var req in reqs)
            {
                Dictionary<UUID, byte[]> bytecodes = new Dictionary<UUID, byte[]>();

                foreach (UUID id in req.ScriptIds)
                {
                    if (!bytecodes.ContainsKey(id))
                    {
                        LoadedScript script;
                        if (_loadedScripts.TryGetValue(id, out script))
                        {
                            byte[] serializedScript = ReserializeScript(script.Script);
                            bytecodes.Add(id, serializedScript);
                        }
                    }
                }

                req.Bytecodes = bytecodes;
                req.SignalDataReady();
            }

            return true;
        }

        /// <summary>
        /// Reserializes a compiled script into a form again usable to pass over the wire or write to disk
        /// </summary>
        /// <param name="compiledScript"></param>
        /// <returns></returns>
        private byte[] ReserializeScript(CompiledScript compiledScript)
        {
            SerializedScript script = SerializedScript.FromCompiledScript(compiledScript);
            using (MemoryStream memStream = new MemoryStream())
            {
                ProtoBuf.Serializer.Serialize(memStream, script);
                return memStream.ToArray();
            }
        }
    }
}
