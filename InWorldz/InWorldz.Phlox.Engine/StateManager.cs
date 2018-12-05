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

using OpenMetaverse;
using System.Threading;
using Halcyon.Phlox.Serialization;
using System.IO;
using log4net;
using System.Reflection;
using System.Data.SQLite;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Framework;

namespace Halcyon.Phlox.Engine
{
    /// <summary>
    /// Manages the state of scripts and maintains a list of scripts that need 
    /// state storage to disk. Storage is performed on an time interval basis 
    /// </summary>
    internal class StateManager
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly TimeSpan REPORT_INTERVAL = TimeSpan.FromMinutes(5);
        private readonly TimeSpan SAVE_INTERVAL = TimeSpan.FromMinutes(4);
        private const int SLEEP_TIME_MS = 500;

        /// <summary>
        /// The connection to the databases is kept open for the duration of the application
        /// </summary>
        private SQLiteConnection _connection;


        private DateTime _lastReport = DateTime.Now;
        private int _stateSavesSinceLastReport = 0;
        private int _stateRemovalsSinceLastReport = 0;

        ExecutionScheduler _scheduler;

        /// <summary>
        /// Used to lock the collection of dirty scripts and the delay queue
        /// </summary>
        private object _scriptChangeLock = new object();

        /// <summary>
        /// All scripts that have changed since their last save
        /// </summary>
        private Dictionary<UUID, VM.Interpreter> _dirtyScripts = new Dictionary<UUID, VM.Interpreter>();

        /// <summary>
        /// Stores scripts that have been saved and are on the delay list prevnting them from saving again
        /// before SAVE_INTERVAL expires
        /// </summary>
        private IndexedPriorityQueue<UUID, DateTime> _delayQueue = new IndexedPriorityQueue<UUID, DateTime>();

        /// <summary>
        /// Scripts that we now have state for that needs to be written to disk on the next iteration
        /// </summary>
        private Dictionary<UUID, StateDataRequest> _needsSaving = new Dictionary<UUID, StateDataRequest>();



        /// <summary>
        /// Scripts that have been unloaded and thus no longer need to hold state on 
        /// region storage
        /// </summary>
        private List<UUID> _unloadedScripts = new List<UUID>();

        /// <summary>
        /// The thread we're running on
        /// </summary>
        private Thread _executionThread;

        /// <summary>
        /// Whether or not we're running
        /// </summary>
        private bool _running = true;

        /// <summary>
        /// Used by this class to know whether or not the scheduler is alive during shutdown
        /// </summary>
        private MasterScheduler _masterScheduler;

        public MasterScheduler MMasterScheduler
        {
            set
            {
                _masterScheduler = value;
            }
        }

        public StateManager(ExecutionScheduler scheduler)
        {
            _scheduler = scheduler;
            this.SetupStorage();
        }

        private void SetupStorage()
        {
            Directory.CreateDirectory(PhloxConstants.STATE_SAVE_DIR);
            SetupAndOpenDatabase();
        }

        private string GetDBFileName()
        {
            return Path.Combine(PhloxConstants.STATE_SAVE_DIR, "script_states.db3");
        }

        private void SetupAndOpenDatabase()
        {
            _connection = new SQLiteConnection(
                String.Format("Data Source={0}", this.GetDBFileName()));

            _connection.Open();

            const string INITIALIZED_QUERY =    "SELECT COUNT(*) AS tbl_count " +
                                                "FROM sqlite_master " +
                                                "WHERE type = 'table' AND tbl_name='StateData';";

            using (SQLiteCommand cmd = new SQLiteCommand(INITIALIZED_QUERY, _connection))
            {
                long count = (long)cmd.ExecuteScalar();

                if (count == 0)
                {
                    const string SETUP_QUERY = "CREATE TABLE StateData (" +
                                                    "   item_id CHARACTER(32) PRIMARY KEY NOT NULL, " +
                                                    "   state_data BLOB " +
                                                    ")";

                    using (SQLiteCommand createCmd = new SQLiteCommand(SETUP_QUERY, _connection))
                    {
                        createCmd.ExecuteNonQuery();
                    }
                }
            }
        }



        public void ScriptUnloaded(UUID scriptId)
        {
            lock (_unloadedScripts)
            {
                _unloadedScripts.Add(scriptId);
            }
        }

        public void ScriptChanged(VM.Interpreter script)
        {
            lock (_scriptChangeLock)
            {
                _dirtyScripts[script.ItemId] = script;
                if (!_delayQueue.ContainsKey(script.ItemId))
                {
                    _delayQueue.Add(script.ItemId, DateTime.Now + SAVE_INTERVAL);
                    _scheduler.RequestStateData(new StateDataRequest(script.ItemId, this.StateAvailable));
                }
            }
        }

        private void StateAvailable(StateDataRequest dataRequest)
        {
            lock (_scriptChangeLock)
            {
                _dirtyScripts.Remove(dataRequest.ItemId);

                if (dataRequest.RawStateData != null)
                {
                    _needsSaving[dataRequest.ItemId] = dataRequest;
                }
            }
        }

        /// <summary>
        /// Starts the state manager thread. This thread schedules region object state saves
        /// </summary>
        public void Start()
        {
            if (_executionThread == null)
            {
                _executionThread = new Thread(WorkLoop);
                _executionThread.Priority = EngineInterface.SUBTASK_PRIORITY;
                _executionThread.Start();
            }
        }

        private void WorkLoop()
        {
            try 
            {
                while (_running)
                {
                    DoSaveDirtyScripts();
                    CheckForExpiredScripts();
                    DeleteUnloadedScriptStates();

                    Thread.Sleep(SLEEP_TIME_MS);
                }
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Phlox]: State manager caught exception and is terminating: {0}", e);
                throw;
            }

        }

        private void DoSaveDirtyScripts()
        {
            List<StateDataRequest> data;
            lock (_scriptChangeLock)
            {
                if (_needsSaving.Count == 0)
                {
                    return;
                }

                data = new List<StateDataRequest>(_needsSaving.Values);
                _needsSaving.Clear();
            }

            _stateSavesSinceLastReport += data.Count;

            lock (_connection)
            {
                //write each state to disk
                SQLiteTransaction transaction = _connection.BeginTransaction();

                try
                {
                    foreach (StateDataRequest req in data)
                    {
                        this.SaveStateToDisk(req);
                    }

                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }

        private void CheckForExpiredScripts()
        {
            lock (_scriptChangeLock)
            {
                while (_delayQueue.Count > 0)
                {
                    KeyValuePair<UUID, DateTime> kvp = _delayQueue.FindMinItemAndIndex();
                    if (kvp.Value <= DateTime.Now)
                    {
                        _delayQueue.Remove(kvp.Key);

                        if (_dirtyScripts.ContainsKey(kvp.Key))
                        {
                            //if this script is dirty and its wait is expired, we can request a save for it now
                            _delayQueue.Add(kvp.Key, DateTime.Now + SAVE_INTERVAL);
                            _scheduler.RequestStateData(new StateDataRequest(kvp.Key, this.StateAvailable));
                        }

                        //else, this script is expired, but not dirty. just removing it is fine
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        private void DeleteUnloadedScriptStates()
        {
            List<UUID> unloadedScripts;
            lock (_unloadedScripts)
            {
                if (_unloadedScripts.Count == 0)
                {
                    return;
                }

                unloadedScripts = new List<UUID>(_unloadedScripts);
                _unloadedScripts.Clear();
            }

            lock (_scriptChangeLock)
            {
                foreach (UUID uuid in unloadedScripts)
                {
                    _delayQueue.Remove(uuid);
                    _dirtyScripts.Remove(uuid);
                    _needsSaving.Remove(uuid);
                }
            }

            _stateRemovalsSinceLastReport += unloadedScripts.Count;

            StringBuilder deleteQuery = new StringBuilder();
            deleteQuery.Append("DELETE FROM StateData WHERE item_id IN (");

            for (int i = 0; i < unloadedScripts.Count; i++)
            {
                if (i != 0) deleteQuery.Append(",");

                deleteQuery.Append("'");
                deleteQuery.Append(unloadedScripts[i].Guid.ToString("N"));
                deleteQuery.Append("'");
            }

            deleteQuery.Append(");");

            lock (_connection)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(deleteQuery.ToString(), _connection))
                {
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private void ReportStateSaves()
        {
            if (DateTime.Now - _lastReport >= REPORT_INTERVAL)
            {
                _log.DebugFormat("[Phlox]: {0} script states saved, {1} states removed in the past {2} minutes", 
                    _stateSavesSinceLastReport, _stateRemovalsSinceLastReport, REPORT_INTERVAL.TotalMinutes);

                _stateSavesSinceLastReport = 0;
                _stateRemovalsSinceLastReport = 0;
                _lastReport = DateTime.Now;
            }
        }

        /// <summary>
        /// Only called during startup, loads the state data for the given item id off the disk
        /// </summary>
        /// <param name="itemId"></param>
        /// <returns></returns>
        public VM.RuntimeState LoadStateFromDisk(UUID itemId)
        {
            try
            {
                lock (_connection)
                {
                    const string FIND_CMD = "SELECT state_data FROM StateData WHERE item_id = @itemId";
                    using (SQLiteCommand cmd = new SQLiteCommand(FIND_CMD, _connection))
                    {
                        SQLiteParameter itemIdParam = cmd.CreateParameter();
                        itemIdParam.ParameterName = "@itemId";
                        itemIdParam.Value = itemId.Guid.ToString("N");

                        cmd.Parameters.Add(itemIdParam);

                        using (SQLiteDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                //int bufSz = reader.GetBytes(0, 0, null, 0, 0);
                                byte[] buffer = (byte[])reader[0];//reader.GetBytes(

                                using (MemoryStream stateStream = new MemoryStream(buffer))
                                {
                                    SerializedRuntimeState runstate = ProtoBuf.Serializer.Deserialize<SerializedRuntimeState>(stateStream);
                                    if (runstate != null)
                                    {
                                        return runstate.ToRuntimeState();
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Phlox]: Could not load state for {0} script will be reset. Error was {1}",
                    itemId, e);
            }

            return null;
        }

        internal VM.RuntimeState LoadStateFromPrim(UUID currId, UUID oldId, SceneObjectPart sop)
        {
            string oldIdState = null;
            string newIdState = null;

            byte[] binaryState = null;

            //Old way, the script states are stored in the group
            if (sop.ParentGroup.m_savedScriptState != null &&
                (sop.ParentGroup.m_savedScriptState.TryGetValue(oldId, out oldIdState) ||
                sop.ParentGroup.m_savedScriptState.TryGetValue(currId, out newIdState)))
            {
                string state = oldIdState != null ? oldIdState : newIdState;

                sop.ParentGroup.m_savedScriptState.Remove(oldIdState != null ? oldId : currId);
                byte[] rawState = Convert.FromBase64String(state);

                return DeserializeScriptState(currId, rawState);
            }
            else if (sop.HasSavedScriptStates &&
                (sop.TryExtractSavedScriptState(oldId, out binaryState) ||
                sop.TryExtractSavedScriptState(currId, out binaryState)))
            {
                return DeserializeScriptState(sop.UUID, binaryState);
            }

            return null;
        }

        private static VM.RuntimeState DeserializeScriptState(UUID currId, byte[] rawState)
        {
            using (MemoryStream loadStream = new MemoryStream(rawState))
            {
                try
                {
                    SerializedRuntimeState runstate = ProtoBuf.Serializer.Deserialize<SerializedRuntimeState>(loadStream);
                    if (runstate != null)
                    {
                        return runstate.ToRuntimeState();
                    }
                }
                catch (Serialization.SerializationException e)
                {
                    _log.ErrorFormat("[Phlox]: Unable to load script state for {0} from prim data: {1}", currId, e);
                }
            }

            return null;
        }

        private void SaveStateToDisk(StateDataRequest req)
        {
            //serialize the state
            SerializedRuntimeState serState = (SerializedRuntimeState)req.RawStateData;

            using (MemoryStream ms = new MemoryStream())
            {
                ProtoBuf.Serializer.Serialize(ms, serState);
                WriteBlobRow(req.ItemId, ms);
            }
        }

        private void WriteBlobRow(UUID uuid, MemoryStream ms)
        {
            const string INSERT_CMD = "INSERT OR REPLACE INTO StateData(item_id, state_data) VALUES(@itemId, @stateBlob)";
            using (SQLiteCommand cmd = new SQLiteCommand(INSERT_CMD, _connection))
            {
                SQLiteParameter itemIdParam = cmd.CreateParameter();
                itemIdParam.ParameterName = "@itemId";
                itemIdParam.Value = uuid.Guid.ToString("N");

                SQLiteParameter stateBlobParam = cmd.CreateParameter();
                stateBlobParam.ParameterName = "@stateBlob";
                stateBlobParam.Value = ms.ToArray();

                cmd.Parameters.Add(itemIdParam);
                cmd.Parameters.Add(stateBlobParam);

                cmd.ExecuteNonQuery();
            }
        }

        internal void Stop()
        {
            _running = false;
            _executionThread.Join();

            _log.InfoFormat("[Phlox]: Finalizing {0} script states for shutdown", _dirtyScripts.Count + _needsSaving.Count);

            //write all pending script states with direct access since 
            //the execution scheduler is stopped
            foreach (KeyValuePair<UUID, VM.Interpreter> script in _dirtyScripts)
            {
                SerializedRuntimeState runstate = SerializedRuntimeState.FromRuntimeState(script.Value.ScriptState);
                _needsSaving[script.Key] = new StateDataRequest { ItemId = script.Key, RawStateData = runstate };
            }

            DoSaveDirtyScripts();
            DeleteUnloadedScriptStates();
        }
    }
}
