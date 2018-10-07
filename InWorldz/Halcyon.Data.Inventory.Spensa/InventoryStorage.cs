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
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

using log4net;
using Cassandra;
using Cassandra.Mapping;

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Data;

namespace Halcyon.Data.Inventory.Spensa
{
    public class InventoryStorage : IInventoryStorage
    {
        /// <summary>
        /// What kind of mutations we want to apply to a folder
        /// </summary>
        private enum FolderMutationSelector
        {
            /// <summary>
            /// Mutate all the folder properties
            /// </summary>
            All,

            /// <summary>
            /// Mutate all folder properties except for the folder's parent
            /// </summary>
            AllButParent,

            /// <summary>
            /// Mutate only the folder's parent
            /// </summary>
            ParentOnly
        }

        private const string KEYSPACE = "inventory";

        private const string FOLDERS = "Folders";
        private const string SUBFOLDERS = "Subfolders";
        private const string FOLDERVERSIONS = "FolderVersions";
        private const string ITEMS = "Items";
        private const string USERACTIVEGESTURES = "UserActiveGestures";

        private const ConsistencyLevel DEFAULT_CONSISTENCY_LEVEL = ConsistencyLevel.Quorum;

        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private string _clusterName;
        private string _storageUsername;
        private string _storagePassword;
        private Cluster _cluster = null;
        ISession _session = null;

        public InventoryStorage(string clusterName, string storageUsername, string storagePassword)
        {
            _clusterName = clusterName;
            _storageUsername = storageUsername;
            _storagePassword = storagePassword;

            _cluster = Cluster.Builder()
                .AddContactPoints(_clusterName)
                .WithCredentials(_storageUsername, _storagePassword)
                .Build();

            _session = _cluster.Connect(KEYSPACE);
        }

        #region IInventoryStorage Members

        public class FolderRow
        {
            public Guid ownerid;
            public Guid folderid;
            public Guid parentid;
            public string name;
            public short type;
            public InventoryFolderBase.FolderLevel level;
        }

        public enum SubfoldersMode { SingleFolder, Recursive };

        private void AddRowsToIndex(List<InventoryFolderBase> index, IEnumerable<FolderRow> folderRows)
        {
            foreach (var folderRow in folderRows)
            {
                ushort version = 1;
                var folderId = new UUID(folderRow.folderid);
                var newFolder = new InventoryFolderBase(folderId, folderRow.name, new UUID(folderRow.ownerid), folderRow.type, new UUID(folderRow.parentid), version);
                newFolder.Level = folderRow.level;
                index.Add(newFolder);
            }
        }

        private void AddFolderIndexByParent(List<InventoryFolderBase> index, UUID ownerId, UUID parentId)
        {
            try
            {
                IMapper mapper = new Mapper(_session);
                IEnumerable<FolderRow> folderRows = mapper.Fetch<FolderRow>(
                        $"SELECT * FROM {KEYSPACE}.{SUBFOLDERS} WHERE ownerID = ? AND parentID=?", ownerId.Guid, parentId.Guid);
                AddRowsToIndex(index, folderRows);
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Unable to retrieve folder skeleton: {0}", e);
                throw new InventoryStorageException(e.Message, e);
            }
        }

        private List<InventoryFolderBase> GetFolderIndexByParent(UUID ownerId, UUID parentId)
        {
            try
            {
                List<InventoryFolderBase> index = new List<InventoryFolderBase>();
                AddFolderIndexByParent(index, ownerId, parentId);
                return index;
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Unable to retrieve folder index: {0}", e);
                throw new InventoryStorageException(e.Message, e);
            }
        }

        public InventoryFolderBase GetRootFolder(UUID ownerId)
        {
            List<InventoryFolderBase> index = GetFolderIndexByParent(ownerId, UUID.Zero);
            if (index.Count > 1)
                throw new InventoryStorageException($"User {ownerId} has {index.Count} root folders!");
            if (index.Count < 1)
                throw new InventoryStorageException($"User {ownerId} root folder not found!");

            return index.First();
        }

        void AddSubfoldersToIndex(List<InventoryFolderBase> index, UUID ownerId, UUID parentId, SubfoldersMode mode)
        {
            List<InventoryFolderBase> folderIndex = GetFolderIndexByParent(ownerId, parentId);
            foreach (var folder in folderIndex)
            {
                if (mode == SubfoldersMode.Recursive)
                {
                    index.Add(folder);
                    AddSubfoldersToIndex(index, ownerId, folder.ID, mode);
                }
            }
        }

        /// <summary>
        /// Returns a copy of all user inventory folders with subfolders and items excluded
        /// </summary>
        /// <returns>A list of all folders that belong to this user</returns>
        public List<InventoryFolderBase> GetInventorySkeleton(UUID ownerId)
        {
            List<InventoryFolderBase> index = new List<InventoryFolderBase>();
            InventoryFolderBase rootFolder = GetRootFolder(ownerId);

            // Add the parent folder first so that it is known when the viewer encounters the subfolders.
            index.Add(rootFolder);

            // Fetch subfolders recursively.
            AddSubfoldersToIndex(index, ownerId, rootFolder.ID, SubfoldersMode.Recursive);

            return index;
        }

        // This function fetches and fills the Subfolders member of the folder parameter.
        public InventoryFolderBase GetSubfolders(InventoryFolderBase folder)
        {
            if (folder.ID == UUID.Zero)
                throw new InventorySecurityException("Not returning contents for folder with ID UUID.Zero");

            try
            {
                List<InventoryFolderBase> index = new List<InventoryFolderBase>();
                AddSubfoldersToIndex(index, folder.Owner, folder.ID, SubfoldersMode.SingleFolder);
                foreach (var subfolder in index)
                {
                    folder.SubFolders.Add(new InventorySubFolderBase
                        { ID = subfolder.ID, Name = subfolder.Name, Owner = subfolder.Owner, Type = subfolder.Type });
                }
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Unable to retrieve folder subfolders {0}: {1}", folder.ID, e);
                throw new InventoryStorageException(e.Message, e);
            }

            return folder;
        }

        /// <summary>
        /// Returns a copy of the requested folder's properties. Excludes items and subfolder ids.
        /// </summary>
        /// <param name="folderId">The ID of the folder to retrieve</param>
        /// <returns>The folder that was found</returns>
        public InventoryFolderBase GetFolderAttributes(UUID folderId)
        {
            try
            {
                IMapper mapper = new Mapper(_session);
                FolderRow folderRow = mapper.Single<FolderRow>(
                        $"SELECT * FROM {KEYSPACE}.{FOLDERS} WHERE folderid=?", folderId.Guid);
                _log.Info($"GetFolderAttributes: returning '{folderRow.name} ({folderRow.type}) [{folderRow.folderid}]");
                ushort version = 1;
                return new InventoryFolderBase(folderId, folderRow.name, new UUID(folderRow.ownerid), folderRow.type, new UUID(folderRow.parentid), version);
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Unable to retrieve folder skeleton: {0}", e);
                throw new InventoryStorageException(e.Message, e);
            }
        }

        /// <summary>
        /// Returns a full copy of the requested folder including items and sub folder ids
        /// </summary>
        /// <param name="folderId">The ID of the folder to retrieve</param>
        /// <returns>The folder that was found</returns>
        public InventoryFolderBase GetFolder(UUID folderId)
        {
            InventoryFolderBase folder;
            if (folderId == UUID.Zero) throw new InventorySecurityException("Not returning folder with ID UUID.Zero");

            try
            {
                folder = GetFolderAttributes(folderId);
                GetSubfolders(folder);
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Unable to retrieve folder {0}: {1}", folderId, e);
                throw new InventoryStorageException(e.Message, e);
            }

            return folder;
        }

        private async void _doAsync2(BoundStatement q1, BoundStatement q2)
        {
            Task t1 = _session.ExecuteAsync(q1);
            Task t2 = _session.ExecuteAsync(q2);
            await Task.WhenAll(t1, t2);
        }

        private async void _doAsync4(BoundStatement q1, BoundStatement q2, BoundStatement q3, BoundStatement q4)
        {
            Task t1 = _session.ExecuteAsync(q1);
            Task t2 = _session.ExecuteAsync(q2);
            Task t3 = _session.ExecuteAsync(q3);
            Task t4 = _session.ExecuteAsync(q4);
            await Task.WhenAll(t1, t2, t3, t4);
            _log.Info("Async create complete.");
        }

        /// <summary>
        /// Creates a new folder and sets its parent correctly as well as other properties
        /// </summary>
        /// <param name="folder"></param>
        public void CreateFolder(InventoryFolderBase folder)
        {
            long timeStamp = Util.UnixTimeSinceEpochInMicroseconds();

            if (folder.ID == UUID.Zero) throw new InventorySecurityException("Not creating folder with ID UUID.Zero");

            _log.Info($"CreateFolder: creating '{folder.Name}' ({folder.Type}) [{folder.ID}]");

            var pq1 = _session.Prepare($"INSERT INTO {KEYSPACE}.{FOLDERS} (OwnerID, FolderID, ParentID, Level, Name, Type) VALUES(?,?,?,?,?,?) IF NOT EXISTS");
            var q1 = pq1.Bind(folder.Owner.Guid, folder.ID.Guid, folder.ParentID.Guid, (int)folder.Level, folder.Name, (int)folder.Type);

            var pq2 = _session.Prepare($"INSERT INTO {KEYSPACE}.{SUBFOLDERS} (OwnerID, FolderID, ParentID, Level, Name, Type) VALUES(?,?,?,?,?,?)");
            var q2 = pq2.Bind(folder.Owner.Guid, folder.ID.Guid, folder.ParentID.Guid, (int)folder.Level, folder.Name, (int)folder.Type);

            var pq3 = _session.Prepare($"UPDATE {KEYSPACE}.{FOLDERVERSIONS} SET Version = Version + 1 WHERE FolderID =?");
            var q3 = pq3.Bind(folder.ID.Guid);

            var pq4 = _session.Prepare($"UPDATE {KEYSPACE}.{FOLDERVERSIONS} SET Version = Version + 1 WHERE FolderID =?");
            var q4 = pq4.Bind(folder.ParentID.Guid);

            try
            {
                _doAsync4(q1, q2, q3, q4);
                _log.InfoFormat("User {0} inserted folder {1} [{2}] under [{3}]", folder.Owner, folder.Name, folder.ID, folder.ParentID);
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: User {0} unable to create folder {1}: {2}", folder.Owner, folder.ID, e);
                throw new InventoryStorageException(e.Message, e);
            }
        }

        private static void CheckBasicFolderIntegrity(InventoryFolderBase folder)
        {
            if (folder.ID == UUID.Zero)
            {
                throw new UnrecoverableInventoryStorageException("Not creating zero UUID folder");
            }

            if (folder.ParentID == folder.ID)
            {
                throw new UnrecoverableInventoryStorageException("Not creating a folder with a parent set to itself");
            }

            if (folder.ParentID == UUID.Zero && folder.Level != InventoryFolderBase.FolderLevel.Root)
            {
                throw new UnrecoverableInventoryStorageException("Not storing a folder with parent set to ZERO that is not FolderLevel.Root");
            }
        }

        /// <summary>
        /// Stores changes made to the base properties of the folder. Can not be used to reassign a new
        /// parent
        /// </summary>
        /// <param name="folder">The folder to save</param>
        public void SaveFolder(InventoryFolderBase folder)
        {
            long timeStamp = Util.UnixTimeSinceEpochInMicroseconds();

            try
            {
                InventoryFolderBase update = GetFolderAttributes(folder.ID);
                update.Name = String.Copy(folder.Name);
                update.Type = folder.Type;
                var pq1 = _session.Prepare($"UPDATE INTO {KEYSPACE}.{FOLDERS} SET Name=?, Type=? WHERE FolderID=?");
                var q1 = pq1.Bind(update.Name, (int)update.Type, update.ID.Guid);
                var pq2 = _session.Prepare($"UPDATE {KEYSPACE}.{FOLDERVERSIONS} SET Version = Version + 1 WHERE FolderID =?");
                var q2 = pq2.Bind(folder.ID.Guid);

                _doAsync2(q1, q2);
                _log.InfoFormat("User {0} updated folder {1} [{2}]", folder.Owner, folder.Name, folder.ID);
                return;
            }
            catch (UnrecoverableInventoryStorageException e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Unrecoverable error caught while saving folder {0} for {1}: {2}",
                    folder.ID, folder.Owner, e);

                throw;
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Exception caught while saving folder {0} for {1}: {2}",
                    folder.ID, folder.Owner, e);
            }
        }



        private void MoveFolderInternal(InventoryFolderBase folder, UUID parentId)
        {
            if (parentId == folder.ID)
            {
                throw new UnrecoverableInventoryStorageException(String.Format("The parent for folder {0} can not be set to itself", folder.ID));
            }

            var pq1 = _session.Prepare($"UPDATE {KEYSPACE}.{FOLDERS} SET ParentID=? WHERE FolderID=?");
            var q1 = pq1.Bind(parentId.Guid, folder.ID.Guid);
            var pq2 = _session.Prepare($"UPDATE {KEYSPACE}.{SUBFOLDERS} SET ParentID=? WHERE FolderID=?");
            var q2 = pq2.Bind(parentId.Guid, folder.ID.Guid);
            var pq3 = _session.Prepare($"UPDATE {KEYSPACE}.{FOLDERVERSIONS} SET Version = Version + 1 FolderID =?");
            var q3 = pq3.Bind(folder.ID.Guid);
            _doAsync2(q1, q2);
            folder.ParentID = parentId;
        }
        
        /// <summary>
        /// Moves the specified folder to the new parent
        /// </summary>
        /// <param name="folder">The folder to move</param>
        /// <param name="parentId">The destination folder to move the folder into</param>
        public void MoveFolder(InventoryFolderBase folder, UUID parentId)
        {
            try
            {
                //don't do anything with a folder that wants to set its new parent
                //to the same folder as its current parent, this can cause corruption
                if (folder.ParentID == parentId)
                {
                    _log.WarnFormat("[Halcyon.Data.Inventory.Spensa]: Refusing to move folder {0} to new parent {1} for {2}. The source and destination are the same",
                        folder.ID, parentId, folder.Owner);
                    return;
                }

                //don't do anything with a folder that wants to set its new parent to UUID.Zero
                if (parentId == UUID.Zero)
                {
                    _log.WarnFormat("[Halcyon.Data.Inventory.Spensa]: Refusing to move folder {0} to new parent {1} for {2}. New parent has ID UUID.Zero",
                        folder.ID, parentId, folder.Owner);
                    return;
                }

                MoveFolderInternal(folder, parentId);
            }
            catch (UnrecoverableInventoryStorageException e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Exception caught while moving folder {0} for {1}: {2}",
                    folder.ID, folder.Owner, e);
                throw;
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Exception caught while moving folder {0} for {1}: {2}",
                    folder.ID, folder.Owner, e);
            }
        }

        /// <summary>
        /// Finds the best root folder to hold the given type
        /// </summary>
        /// <param name="type"></param>
        /// <returns>The best folder to put an object</returns>
        public InventoryFolderBase FindFolderForType(UUID owner, AssetType type)
        {
            InventoryFolderBase rootFolder = this.GetRootFolder(owner);
            if (((short)type == (short)FolderType.Root) || ((short)type == (short)FolderType.OldRoot))
                return rootFolder;

            List<InventoryFolderBase> folderIndex = this.GetFolderIndexByParent(owner, rootFolder.ID);
            foreach (InventoryFolderBase folder in folderIndex)
            {
                if (folder.Level == InventoryFolderBase.FolderLevel.TopLevel ||
                    folder.Level == InventoryFolderBase.FolderLevel.Root)
                {
                    if ((short)type == folder.Type)
                        return folder;
                    if (((short)type == (short)FolderType.Root) && (folder.Type == (short)FolderType.OldRoot)) // old AssetType.RootFolder == 9
                        return folder; // consider 9 to be FolderType.Root too
                }
            }

            throw new InventoryStorageException(String.Format("Unable to find a suitable folder for type {0} and user {1}", type, owner));
        }

        /// <summary>
        /// Searches the parentage tree for an ancestor folder with a matching type (e.g. Trash)
        /// </summary>
        /// <param name="type"></param>
        /// <returns>The top-level parent folder</returns>
        public InventoryFolderBase FindTopLevelFolderFor(UUID owner, UUID folderID)
        {
            InventoryFolderBase rootFolder = this.GetRootFolder(owner);
            InventoryFolderBase folder = this.GetFolderAttributes(rootFolder.ID);

            do
            {
                if (folder.ParentID == rootFolder.ID)
                    return folder;
                if (folder.ParentID == UUID.Zero)
                    return null;
                folder = this.GetFolderAttributes(folder.ParentID);
            } while (folder != null);

            // No top-level/root folder found for this folder.
            return null;
        }

        private UUID SendFolderToTrashInternal(InventoryFolderBase folder, UUID trashFolderHint)
        {
            if (trashFolderHint != UUID.Zero)
            {
                this.MoveFolderInternal(folder, trashFolderHint);
                return trashFolderHint;
            }
            else
            {
                InventoryFolderBase trashFolder;

                try
                {
                    trashFolder = this.FindFolderForType(folder.Owner, (AssetType)FolderType.Trash);
                }
                catch (Exception e)
                {
                    throw new InventoryStorageException(String.Format("Trash folder could not be found for user {0}: {1}", folder.Owner, e), e);
                }

                this.MoveFolderInternal(folder, trashFolder.ID);
                return trashFolder.ID;
            }
        }

        public UUID SendFolderToTrash(InventoryFolderBase folder, UUID trashFolderHint)
        {
            try
            {
                return SendFolderToTrashInternal(folder, trashFolderHint);
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Exception caught while sending folder {0} to trash for {1}: {2}",
                    folder.ID, folder.Owner, e);
                return UUID.Zero;
            }
        }

        /*
        private void PurgeFolderContentsInternal(InventoryFolderBase folder)
        {
            //block all deletion requests for a folder with a 0 id
            if (folder.ID == UUID.Zero)
            {
                throw new UnrecoverableInventoryStorageException("Refusing to allow the purge of the inventory ZERO root folder");
            }

            //to purge a folder, we have to find all subfolders and items inside a folder
            //for each of the sub folders folders they choose, we need to recurse into all
            //sub-sub folders and grab out the items and folders. Once we have all of them
            //to the last leaf level we do simple removes on all the items and folders
            List<UUID> allFolders = new List<UUID>();
            List<UUID> allItems = new List<UUID>();

            C5.HashSet<UUID> rootItems = new C5.HashSet<UUID>();
            C5.HashSet<UUID> rootFolders = new C5.HashSet<UUID>();

            StringBuilder debugFolderList = new StringBuilder();
            this.RecursiveCollectSubfoldersAndItems(folder.ID, folder.Owner, allFolders, allItems, rootItems, rootFolders, true, null, debugFolderList);

            this.DebugFolderPurge("PurgeFolderContentsInternal", folder, debugFolderList);

            List<UUID> allItemIds = new List<UUID>();
            List<UUID> rootItemIds = new List<UUID>();

            foreach (UUID iid in allItems)
            {
                allItemIds.Add(iid);

                if (rootItems.Contains(iid))
                {
                    rootItemIds.Add(iid);
                }
            }

            //we have all the contents, so delete the actual folders and their versions...
            //this will wipe out the folders and in turn all items in subfolders
            //but does not take care of the items in the root

            //remove the individual items from the root folder

            //remove the individual folder references from the root folder

            //delete the ItemParents folder references for the removed items...

            //increment the version of the purged folder

        }
        */

        public void PurgeFolderContents(InventoryFolderBase folder)
        {
            long timeStamp = Util.UnixTimeSinceEpochInMicroseconds();

            try
            {
                return;
                // PurgeFolderContentsInternal(folder, timeStamp);
            }
            catch (UnrecoverableInventoryStorageException e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Unrecoverable error while purging contents in folder {0} for {1}: {2}",
                    folder.ID, folder.Owner, e);
                throw;
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Exception caught while sending folder {0} to trash for {1}: {2}",
                    folder.ID, folder.Owner, e);
                throw new InventoryStorageException("Could not purge folder contents for " + folder.ID.ToString() + ": " + e.Message, e);
            }
        }

        private void DebugFolderPurge(string method, InventoryFolderBase folder, StringBuilder debugFolderList)
        {
            _log.DebugFormat("[Halcyon.Data.Inventory.Spensa]: About to purge from {0} {1}\n Objects:\n{2}",
                folder.Name, folder.ID, debugFolderList.ToString());
        }

        public void PurgeFolder(InventoryFolderBase folder)
        {
            long timeStamp = Util.UnixTimeSinceEpochInMicroseconds();

            try
            {
                return;
                // PurgeFolderInternal(folder, timeStamp);
            }
            catch (UnrecoverableInventoryStorageException e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Unrecoverable error while purging folder {0} for {1}: {2}",
                    folder.ID, folder.Owner, e);
                throw;
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Exception caught while purging folder {0} for {1}: {2}",
                    folder.ID, folder.Owner, e);

                throw new InventoryStorageException("Could not purge folder " + folder.ID.ToString() + ": " + e.Message, e);
            }
        }

        private void PurgeFolderInternal(InventoryFolderBase folder, long timeStamp)
        {
            //block all deletion requests for a folder with a 0 id
            if (folder.ID == UUID.Zero)
            {
                throw new UnrecoverableInventoryStorageException("Refusing to allow the deletion of the inventory ZERO root folder");
            }

            //////////////////TODO////////////////
            return;
            /*
            //to purge a folder, we have to find all subfolders and items inside a folder
            //for each of the sub folders folders they choose, we need to recurse into all
            //sub-sub folders and grab out the items and folders. Once we have all of them
            //to the last leaf level we do simple removes on all the items and folders
            List<UUID> allFolders = new List<UUID>();
            List<UUID> allItems = new List<UUID>();
            C5.HashSet<UUID> rootItems = new C5.HashSet<UUID>();
            C5.HashSet<UUID> rootFolders = new C5.HashSet<UUID>();

            StringBuilder debugFolderList = new StringBuilder();
            this.RecursiveCollectSubfoldersAndItems(folder.ID, folder.Owner, allFolders, allItems, rootItems, rootFolders, true, null, debugFolderList);

            this.DebugFolderPurge("PurgeFolderInternal", folder, debugFolderList);

            List<byte[]> allFolderIdBytes = new List<byte[]>();
            foreach (UUID fid in allFolders)
            {
                allFolderIdBytes.Add(ByteEncoderHelper.GuidEncoder.ToByteArray(fid.Guid));
            }

            List<byte[]> allItemIdBytes = new List<byte[]>();
            List<byte[]> rootItemIdBytes = new List<byte[]>();
            foreach (UUID iid in allItems)
            {
                byte[] itemIdBytes = ByteEncoderHelper.GuidEncoder.ToByteArray(iid.Guid);

                allItemIdBytes.Add(itemIdBytes);

                if (rootItems.Contains(iid))
                {
                    rootItemIdBytes.Add(itemIdBytes);
                }
            }

            //we have all the contents, so delete the actual folders and their versions...
            //this will wipe out the folders and in turn all items in subfolders
            byte[] ownerIdBytes = ByteEncoderHelper.GuidEncoder.ToByteArray(folder.Owner.Guid);
            this.GetFolderDeletionMutations(ownerIdBytes, allFolderIdBytes, timeStamp, muts);
            //then we delete this actual folder
            this.GetSingleFolderDeletionMutations(ownerIdBytes, folderIdBytes, timeStamp, muts);
            //and remove the subfolder reference from this folders parent
            this.GetSubfolderEntryDeletionMutations(folderIdBytes, parentFolderIdBytes, timeStamp, muts);

            //delete the ItemParents folder references for the removed items...
            foreach (byte[] itemId in allItemIdBytes)
            {
                this.GetItemParentDeletionMutations(itemId, timeStamp, muts);
            }


            //increment the version of the parent of the purged folder
            if (folder.ParentID != UUID.Zero)
            {
                this.GetFolderVersionIncrementMutations(muts, parentFolderIdBytes);
            }

            ICluster cluster = AquilesHelper.RetrieveCluster(_clusterName);
            cluster.Execute(new ExecutionBlock(delegate(Apache.Cassandra.Cassandra.Client client)
            {
                client.batch_mutate(muts, DEFAULT_CONSISTENCY_LEVEL);

                return null;

            }), KEYSPACE);
            */
        }

        // This is an optimized PurgeFolderInternal that does not refetch the tree
        // but assumes the caller knows that the ID specified has no items or subfolders.
        private void PurgeEmptyFolderInternal(UUID ownerID, long timeStamp, UUID folderID, UUID parentID)
        {
            //block all deletion requests for a folder with a 0 id
            if (folderID == UUID.Zero)
            {
                throw new UnrecoverableInventoryStorageException("Refusing to allow the deletion of the inventory ZERO root folder");
            }

            //////////////////TODO////////////////
            return;
            /*
            Dictionary<byte[], Dictionary<string, List<Mutation>>> muts = new Dictionary<byte[], Dictionary<string, List<Mutation>>>();

            byte[] folderIdBytes = ByteEncoderHelper.GuidEncoder.ToByteArray(folderID.Guid);
            byte[] parentFolderIdBytes = ByteEncoderHelper.GuidEncoder.ToByteArray(parentID.Guid);

            //we have all the contents, so delete the actual folders and their versions...
            //this will wipe out the folders and in turn all items in subfolders
            byte[] ownerIdBytes = ByteEncoderHelper.GuidEncoder.ToByteArray(ownerID.Guid);
            //delete this actual folder
            this.GetSingleFolderDeletionMutations(ownerIdBytes, folderIdBytes, timeStamp, muts);
            //and remove the subfolder reference from this folders parent
            this.GetSubfolderEntryDeletionMutations(folderIdBytes, parentFolderIdBytes, timeStamp, muts);

            //increment the version of the parent of the purged folder
            if (parentID != UUID.Zero)
            {
                this.GetFolderVersionIncrementMutations(muts, parentFolderIdBytes);
            }

            ICluster cluster = AquilesHelper.RetrieveCluster(_clusterName);
            cluster.Execute(new ExecutionBlock(delegate(Apache.Cassandra.Cassandra.Client client)
            {
                client.batch_mutate(muts, DEFAULT_CONSISTENCY_LEVEL);

                return null;

            }), KEYSPACE);
            */
        }

        // This is an optimized PurgeFolderInternal that does not refetch the tree
        // but assumes the caller knows that the ID specified has no items or subfolders.
        public void PurgeEmptyFolder(InventoryFolderBase folder)
        {
            long timeStamp = Util.UnixTimeSinceEpochInMicroseconds();

            try
            {
                return;

                if ((folder.Items.Count != 0) || (folder.SubFolders.Count != 0))
                    throw new UnrecoverableInventoryStorageException("Refusing to PurgeEmptyFolder for folder that is not empty");

                PurgeEmptyFolderInternal(folder.Owner, timeStamp, folder.ID, folder.ParentID);
            }
            catch (UnrecoverableInventoryStorageException e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Unrecoverable error while purging empty folder {0} for {1}: {2}",
                    folder.ID, folder.Owner, e);
                throw;
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Exception caught while purging empty folder {0} for {1}: {2}",
                    folder.ID, folder.Owner, e);
                throw new InventoryStorageException("Could not purge empty folder " + folder.ID.ToString() + ": " + e.Message, e);
            }
        }

        public void PurgeFolders(IEnumerable<InventoryFolderBase> folders)
        {
            long timeStamp = Util.UnixTimeSinceEpochInMicroseconds();

            try
            {
                return;
                PurgeFoldersInternal(folders, timeStamp);
            }
            catch (UnrecoverableInventoryStorageException e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Unrecoverable error while purging folders: {0}", e);
                throw;
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Exception caught while purging folders: {0}", e);

                throw new InventoryStorageException("Could not purge folders: " + e.Message, e);
            }
        }

        private void PurgeFoldersInternal(IEnumerable<InventoryFolderBase> folders, long timeStamp)
        {
            return;
            foreach (InventoryFolderBase folder in folders)
            {
                this.PurgeFolderInternal(folder, timeStamp);
            }
        }

        /*
        private void RecursiveCollectSubfoldersAndItems(UUID id, UUID ownerId, List<UUID> allFolders, List<UUID> allItems, C5.HashSet<UUID> rootItems, C5.HashSet<UUID> rootFolders, bool isRoot,
            Dictionary<Guid, InventoryFolderBase> index, StringBuilder debugFolderList)
        {
            if (index == null)
            {
                index = GetFolderIndex(ownerId);
            }

            InventoryFolderBase folder;
            try
            {
                folder = this.GetFolder(id);
            }
            catch (InventoryObjectMissingException)
            {
                //missing a folder is not a fatal exception, it could indicate a corrupted or temporarily
                //inconsistent inventory state. this should not stop the remainder of the collection
                _log.WarnFormat("[Halcyon.Data.Inventory.Spensa]: Found missing folder with subFolder index remaining in parent. Inventory may need subfolder index maintenance.");
                return;
            }
            catch (InventoryStorageException e)
            {
                if (e.InnerException != null && e.InnerException is KeyNotFoundException)
                {
                    //not a fatal exception, it could indicate a corrupted or temporarily
                    //inconsistent inventory state. this should not stop the remainder of the collection
                    _log.WarnFormat("[Halcyon.Data.Inventory.Spensa]: Found corrupt folder with subFolder index remaining in parent. User inventory needs subfolder index maintenance.");
                    return;
                }
                else
                {
                    throw;
                }
            }

            foreach (InventoryItemBase item in folder.Items)
            {
                allItems.Add(item.ID);

                if (isRoot)
                {
                    rootItems.Add(item.ID);
                }

                debugFolderList.AppendLine("I " + item.ID.ToString() + " " + item.Name);
            }

            foreach (InventoryNodeBase subFolder in folder.SubFolders)
            {
                if (subFolder.Owner != ownerId)
                {
                    throw new UnrecoverableInventoryStorageException(
                        String.Format("Changed owner found during recursive folder collection. Folder: {0}, Expected Owner: {1}, Found Owner: {2}",
                        subFolder.ID, ownerId, subFolder.Owner)); ;
                }


                if (SubfolderIsConsistent(subFolder.ID, folder.ID, index))
                {
                    debugFolderList.AppendLine("F " + subFolder.ID.ToString() + " " + subFolder.Name);

                    allFolders.Add(subFolder.ID);

                    if (isRoot)
                    {
                        rootFolders.Add(subFolder.ID);
                    }

                    this.RecursiveCollectSubfoldersAndItems(subFolder.ID, ownerId, allFolders, allItems, rootItems, rootFolders, false, index, debugFolderList);
                }
                else
                {
                    _log.WarnFormat("[Halcyon.Data.Inventory.Spensa]: Not recursing into folder {0} with parent {1}. Index is inconsistent", 
                        subFolder.ID, folder.ID);
                }
            }
        }
        */

        /// <summary>
        /// Makes sure that both the index and subfolder index agree that the given subfolder
        /// id belongs to the given parent
        /// </summary>
        /// <param name="subfolderId"></param>
        /// <param name="subfolderIndexParentId"></param>
        /// <returns></returns>
        private bool SubfolderIsConsistent(UUID subfolderId, UUID subfolderIndexParentId, Dictionary<Guid, InventoryFolderBase> index)
        {
            InventoryFolderBase indexFolder;
            if (index.TryGetValue(subfolderId.Guid, out indexFolder))
            {
                if (indexFolder.ParentID == subfolderIndexParentId)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Retrieves a set of items in the same folder. This should be efficient compared to
        /// retrieving each item separately regardless of parent. This will be mostly used
        /// for gestures which are usually all in the same folder anyways
        /// </summary>
        /// <param name="folderId"></param>
        /// <param name="itemIds"></param>
        /// <returns></returns>
        private List<InventoryItemBase> GetItemsInSameFolder(UUID folderId, IEnumerable<UUID> itemIds, bool throwOnItemMissing)
        {
            List<InventoryItemBase> retItems = new List<InventoryItemBase>();
            /*
            foreach (ColumnOrSuperColumn superCol in itemCols)
            {
                if (throwOnItemMissing && itemCols.Count != pred.Column_names.Count)
                {
                    throw new InventoryObjectMissingException("One or more items requested could not be found");
                }

                Guid itemId = ByteEncoderHelper.GuidEncoder.FromByteArray(superCol.Super_column.Name);
                InventoryItemBase item = this.DecodeInventoryItem(superCol.Super_column.Columns, itemId, folderId.Guid);
                retItems.Add(item);
            }
            */
            return retItems;
        }

        private InventoryItemBase DecodeInventoryItem(Row itemCols, Guid itemId, Guid folderId)
        {
            Dictionary<string, Object> itemPropsMap = new Dictionary<string, Object>(); // this.IndexColumnsByUTF8Name(itemCols);

            return DecodeInventoryItemFromIndexedCols(itemId, folderId, itemPropsMap);
        }

        private static InventoryItemBase DecodeInventoryItemFromIndexedCols(Guid itemId, Guid folderId, Dictionary<string, Object> itemPropsMap)
        {
            InventoryItemBase retItem = new InventoryItemBase
            {
                /*
                AssetID = new UUID(ByteEncoderHelper.GuidEncoder.FromByteArray(itemPropsMap["asset_id"].Value)),
                AssetType = ByteEncoderHelper.LittleEndianInt32Encoder.FromByteArray(itemPropsMap["asset_type"].Value),
                BasePermissions = (uint)ByteEncoderHelper.LittleEndianInt32Encoder.FromByteArray(itemPropsMap["base_permissions"].Value),
                CreationDate = ByteEncoderHelper.LittleEndianInt32Encoder.FromByteArray(itemPropsMap["creation_date"].Value),
                CreatorId = new UUID(ByteEncoderHelper.GuidEncoder.FromByteArray(itemPropsMap["creator_id"].Value)).ToString(),
                CurrentPermissions = unchecked((uint)ByteEncoderHelper.LittleEndianInt32Encoder.FromByteArray(itemPropsMap["current_permissions"].Value)),
                Description = ByteEncoderHelper.UTF8Encoder.FromByteArray(itemPropsMap["description"].Value),
                EveryOnePermissions = unchecked((uint)ByteEncoderHelper.LittleEndianInt32Encoder.FromByteArray(itemPropsMap["everyone_permissions"].Value)),
                Flags = unchecked((uint)ByteEncoderHelper.LittleEndianInt32Encoder.FromByteArray(itemPropsMap["flags"].Value)),
                Folder = new UUID(folderId),
                GroupID = new UUID(ByteEncoderHelper.GuidEncoder.FromByteArray(itemPropsMap["group_id"].Value)),
                GroupOwned = itemPropsMap["group_owned"].Value[0] == 0 ? false : true,
                GroupPermissions = unchecked((uint)ByteEncoderHelper.LittleEndianInt32Encoder.FromByteArray(itemPropsMap["group_permissions"].Value)),
                ID = new UUID(itemId),
                InvType = ByteEncoderHelper.LittleEndianInt32Encoder.FromByteArray(itemPropsMap["inventory_type"].Value),
                Name = ByteEncoderHelper.UTF8Encoder.FromByteArray(itemPropsMap["name"].Value),
                NextPermissions = unchecked((uint)ByteEncoderHelper.LittleEndianInt32Encoder.FromByteArray(itemPropsMap["next_permissions"].Value)),
                Owner = new UUID(ByteEncoderHelper.GuidEncoder.FromByteArray(itemPropsMap["owner_id"].Value)),
                SalePrice = ByteEncoderHelper.LittleEndianInt32Encoder.FromByteArray(itemPropsMap["sale_price"].Value),
                SaleType = itemPropsMap["sale_type"].Value[0]
                */
            };

            return retItem;
        }

        public InventoryItemBase GetItem(UUID itemId, UUID parentFolderHint)
        {
            //Retrieving an item requires a lookup of the parent folder followed by 
            //a retrieval of the item. This was a consious decision made since the 
            //inventory item data currently takes up the most space and a
            //duplication of this data to prevent the index lookup 
            //would be expensive in terms of space required

            try
            {
                Guid parentId;
                
                if (parentFolderHint != UUID.Zero)
                {
                    parentId = parentFolderHint.Guid;
                }
                else
                {
                    parentId = FindItemParentFolderId(itemId);
                }

                if (parentId == Guid.Empty)
                {
                    throw new InventoryObjectMissingException(String.Format("Item with ID {0} could not be found", itemId), "Item was not found in the index");
                }

                //try to retrieve the item. note that even though we have an index there is a chance we will
                //not have the item data due to a race condition between index mutation and item mutation

                /*
                object itemDataObj =
                    _cluster.Execute(new ExecutionBlock(delegate(Apache.Cassandra.Cassandra.Client client)
                    {
                        return client.get_slice(folderIdBytes, columnParent, pred, DEFAULT_CONSISTENCY_LEVEL);

                    }), KEYSPACE);

                List<ColumnOrSuperColumn> itemCols = (List<ColumnOrSuperColumn>)itemDataObj;

                if (itemCols.Count == 0)
                {
                    throw new InventoryObjectMissingException(String.Format("Item with ID {0} could not be found", itemId), "Item was not found in its folder");
                }
                */

                //////////////////TODO////////////////
                Row row = new Row();
                InventoryItemBase item = this.DecodeInventoryItem(row, itemId.Guid, parentId);

                return item;
            }
            catch (InventoryStorageException)
            {
                throw;
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Unable to retrieve item {0}: {1}", itemId, e);
                throw new InventoryStorageException(e.Message, e);
            }
        }

        public List<InventoryItemBase> GetItems(IEnumerable<UUID> itemIds, bool throwOnNotFound)
        {
            // Dictionary<UUID, List<UUID>> folderItemMapping = this.FindItemParentFolderIds(itemIds);

            List<InventoryItemBase> foundItems = new List<InventoryItemBase>();
            return foundItems;
        }

        public Guid FindItemParentFolderId(UUID itemId)
        {
            /*
            byte[] itemIdArray = ByteEncoderHelper.GuidEncoder.ToByteArray(itemId.Guid);

            ICluster cluster = AquilesHelper.RetrieveCluster(_clusterName);

            object val = 
                cluster.Execute(new ExecutionBlock(delegate(Apache.Cassandra.Cassandra.Client client)
                {
                    return client.get_slice(itemIdArray, columnParent, pred, DEFAULT_CONSISTENCY_LEVEL);

                }), KEYSPACE);

            List<ColumnOrSuperColumn> indexCols = (List<ColumnOrSuperColumn>)val;

            //no index means the item doesnt exist
            if (indexCols.Count == 0)
            {
                return Guid.Empty;
            }

            var indexedColsByName = this.IndexColumnsByUTF8Name(indexCols);

            return ByteEncoderHelper.GuidEncoder.FromByteArray(indexedColsByName["parent"].Value);
            */
            return Guid.Empty;
        }

        /*
        /// <summary>
        /// Returns a dictionary of parents with a list of items Dictionary[FolderID, List[Items]] 
        /// </summary>
        /// <param name="itemIds"></param>
        /// <returns></returns>
        public Dictionary<UUID, List<UUID>> FindItemParentFolderIds(IEnumerable<UUID> itemIds)
        {
            ColumnParent columnParent = new ColumnParent();
            columnParent.Column_family = ITEMPARENTS_CF;

            SlicePredicate pred = new SlicePredicate();
            pred.Column_names = new List<byte[]>();
            pred.Column_names.Add(ByteEncoderHelper.UTF8Encoder.ToByteArray("parent"));

            List<byte[]> allItemIdBytes = new List<byte[]>();
            foreach (UUID id in itemIds)
            {
                allItemIdBytes.Add(ByteEncoderHelper.GuidEncoder.ToByteArray(id.Guid));
            }

            ICluster cluster = AquilesHelper.RetrieveCluster(_clusterName);

            object val =
                cluster.Execute(new ExecutionBlock(delegate(Apache.Cassandra.Cassandra.Client client)
                {
                    return client.multiget_slice(allItemIdBytes, columnParent, pred, DEFAULT_CONSISTENCY_LEVEL);

                }), KEYSPACE);

            Dictionary<byte[], List<ColumnOrSuperColumn>> itemParentCols = (Dictionary<byte[], List<ColumnOrSuperColumn>>)val;
            Dictionary<UUID, List<UUID>> retParents = new Dictionary<UUID, List<UUID>>();

            foreach (KeyValuePair<byte[], List<ColumnOrSuperColumn>> kvp in itemParentCols)
            {
                if (kvp.Value.Count == 1)
                {
                    Column col = kvp.Value[0].Column;

                    UUID parentId = new UUID(ByteEncoderHelper.GuidEncoder.FromByteArray(col.Value));

                    if (!retParents.ContainsKey(parentId))
                    {
                        retParents.Add(parentId, new List<UUID>());
                    }

                    retParents[parentId].Add(new UUID(ByteEncoderHelper.GuidEncoder.FromByteArray(kvp.Key)));
                }
            }

            return retParents;
        }
        */

        public void CreateItem(InventoryItemBase item)
        {
            long timeStamp = Util.UnixTimeSinceEpochInMicroseconds();

            CheckAndFixItemParentFolder(item);

            try
            {
                //////////////////TODO////////////////
                // CreateItemInternal(item, timeStamp);
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Exception caught while creating item {0} for {1}: {2}",
                    item.ID, item.Owner, e);
                throw new InventoryStorageException("Could not create item " + item.ID.ToString()+ e.Message, e);
            }
        }

        private void CheckAndFixItemParentFolder(InventoryItemBase item)
        {
            if (item.Folder == UUID.Zero)
            {
                _log.WarnFormat("[Halcyon.Data.Inventory.Spensa]: Repairing parent folder ID for item {0} for {1}: Folder set to UUID.Zero", item.ID, item.Owner);
                item.Folder = this.FindFolderForType(item.Owner, (AssetType)FolderType.Root).ID;
            }
        }

        public void SaveItem(InventoryItemBase item)
        {
            long timeStamp = Util.UnixTimeSinceEpochInMicroseconds();

            CheckAndFixItemParentFolder(item);

            try
            {
                //////////////////TODO////////////////
                // SaveItemInternal(item, timeStamp);
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Exception caught while saving item {0} for {1}: {2}",
                    item.ID, item.Owner, e);

                throw new InventoryStorageException("Could not save item " + item.ID.ToString() + ": " + e.Message, e);
            }
        }

        public void MoveItem(InventoryItemBase item, InventoryFolderBase parentFolder)
        {
            long timeStamp = Util.UnixTimeSinceEpochInMicroseconds();

            if (parentFolder.ID == UUID.Zero)
            {
                throw new InventoryStorageException("Not moving item to new folder. Destination folder has ID UUID.Zero");
            }

            try
            {
                //dont do anything with an item that wants to set its new parent 
                //to its current parent. this can cause corruption
                if (item.Folder == parentFolder.ID)
                {
                    _log.WarnFormat("[Halcyon.Data.Inventory.Spensa]: Refusing to move item {0} to new folder {1} for {2}. The source and destination folder are the same",
                        item.ID, parentFolder.ID, item.Owner);
                    return;
                }

                //////////////////TODO////////////////
                // MoveItemInternal(item, parentFolder.ID, timeStamp);
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Exception caught while moving item {0} to folder {1}: {2}",
                    item.ID, parentFolder.ID, e);
                throw new InventoryStorageException("Could not move item " + item.ID.ToString() + " " + e.Message, e);
            }
        }

        public UUID SendItemToTrash(InventoryItemBase item, UUID trashFolderHint)
        {
            long timeStamp = Util.UnixTimeSinceEpochInMicroseconds();

            try
            {
                //////////////////TODO////////////////
                return UUID.Zero;   // SendItemToTrashInternal(item, trashFolderHint, timeStamp);
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Exception caught while sending item {0} to trash for {1}: {2}",
                    item.ID, item.Owner, e);
                throw new InventoryStorageException("Could not send item " + item.ID.ToString() + " to trash: " + e.Message, e);
            }
        }

        public void PurgeItem(InventoryItemBase item)
        {
            long timeStamp = Util.UnixTimeSinceEpochInMicroseconds();

            try
            {
                string invType;
                if (item.AssetType == (int)AssetType.Link)
                    invType = "link";
                else
                if (item.AssetType == (int)AssetType.LinkFolder)
                    invType = "folder link";
                else
                    invType = "type "+item.AssetType.ToString();

                _log.WarnFormat("[Halcyon.Data.Inventory.Spensa]: Purge of {0} id={1} asset={2} '{3}' for user={4}", invType, item.ID, item.AssetID, item.Name, item.Owner);
                //////////////////TODO////////////////
                // PurgeItemInternal(item, timeStamp);
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Exception caught while purging item {0}: {1}",
                    item.ID, e);
                throw new InventoryStorageException("Could not purge item " + item.ID.ToString() + ": " + e.Message, e);
            }
        }

        public void PurgeItems(IEnumerable<InventoryItemBase> items)
        {
            long timeStamp = Util.UnixTimeSinceEpochInMicroseconds();

            try
            {
                //////////////////TODO////////////////
                // PurgeItemsInternal(items, timeStamp);
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Exception caught while purging items: {0}", e);
                throw new InventoryStorageException("Could not purge items: " + e.Message, e);
            }
        }

        public void ActivateGestures(UUID userId, IEnumerable<UUID> itemIds)
        {
            long timeStamp = Util.UnixTimeSinceEpochInMicroseconds();

            //failed gesture de/activation is not really fatal nor do we want to retry
            //so we don't bother to run it through the delayed mutation manager
            try
            {
                // ActivateGesturesInternal(userId, itemIds, timeStamp);
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Unable to activate gestures for {0}: {1}",
                    userId, e);

                throw new InventoryStorageException(String.Format("Unable to activate gestures for {0}: {1}", userId, e.Message), e);
            }
        }

        public void DeactivateGestures(UUID userId, IEnumerable<UUID> itemIds)
        {
            long timeStamp = Util.UnixTimeSinceEpochInMicroseconds();

            //failed gesture de/activation is not really fatal nor do we want to retry
            //so we don't bother to run it through the delayed mutation manager
            try
            {
                // DeactivateGesturesInternal(userId, itemIds, timeStamp);
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Halcyon.Data.Inventory.Spensa]: Unable to deactivate gestures for {0}: {1}",
                    userId, e);

                throw new InventoryStorageException(String.Format("Unable to deactivate gestures for {0}: {1}", userId, e));
            }
        }

        public List<InventoryItemBase> GetActiveGestureItems(UUID userId)
        {
            List<UUID> gestureItemIds = this.GetActiveGestureItemIds(userId);

            //////////////////TODO////////////////
            return new List<InventoryItemBase>(); // this.GetItems(gestureItemIds, false);
        }

        private List<UUID> GetActiveGestureItemIds(UUID userId)
        {
            try
            {
                List<UUID> ret = new List<UUID>();

                return ret;
            }
            catch (Exception e)
            {
                throw new InventoryStorageException(e.Message, e);
            }
        }

        private void RemoveFromIndex(Guid userId, Guid folderId, long timeStamp)
        {
        }

        #endregion
        
    }
}
