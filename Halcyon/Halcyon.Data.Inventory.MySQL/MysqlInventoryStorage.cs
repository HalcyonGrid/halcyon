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
using log4net;
using OpenSim.Data;
using OpenMetaverse;
using OpenSim.Framework;

namespace Halcyon.Data.Inventory.MySQL
{
    /// <summary>
    /// IInventorystorage adapter to the old mysql inventory system
    /// </summary>
    public class MySqlInventoryStorage : IInventoryStorage
    {
        private static readonly ILog m_log
            = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private MysqlStorageImpl _impl;

        public MySqlInventoryStorage(string connStr)
        {
            _impl = new MysqlStorageImpl(connStr);
        }

        public List<InventoryFolderBase> GetInventorySkeleton(UUID userId)
        {
            List<InventoryFolderBase> retFolders = new List<InventoryFolderBase>();

            InventoryFolderBase rootFolder = _impl.getUserRootFolder(userId);
            if (rootFolder != null)
            {
                retFolders.Add(rootFolder);
                retFolders.AddRange(_impl.getFolderHierarchy(rootFolder.ID));
            }
            else
            {
                // Handle a null here but throw the same exception Cassandra does.
                throw new InventoryStorageException("[MySqlInventoryStorage]: Unable to retrieve folder skeleton: root folder");
            }

            return retFolders;
        }

        public InventoryFolderBase GetFolder(UUID folderId)
        {
            InventoryFolderBase folder = _impl.getInventoryFolder(folderId);

            if (folder == null)
            {
                throw new InventoryObjectMissingException(String.Format("Unable to find folder {0}", folderId));
            }

            //now we need the descendents
            List<InventoryItemBase> items = _impl.getInventoryInFolder(folderId);
            List<InventoryFolderBase> folders = _impl.getInventoryFolders(folderId);

            folder.Items.AddRange(items);

            foreach (InventoryFolderBase subFolder in folders)
            {
                InventorySubFolderBase sub= new InventorySubFolderBase
                {
                    ID = subFolder.ID,
                    Name = subFolder.Name,
                    Owner = subFolder.Owner,
                    Type = subFolder.Type
                };
                folder.SubFolders.Add(sub);
            }

            return folder;
        }

        public InventoryFolderBase GetFolderAttributes(UUID folderId)
        {
            return _impl.getInventoryFolder(folderId);
        }

        public void CreateFolder(InventoryFolderBase folder)
        {
            _impl.addInventoryFolder(folder);
        }

        public void SaveFolder(InventoryFolderBase folder)
        {
            folder.Version++;
            _impl.updateInventoryFolder(folder);
        }

        public void MoveFolder(InventoryFolderBase folder, UUID parentId)
        {
            // Don't do anything with a folder that wants to set its new parent to the same folder as its current parent.
            if (folder.ParentID == parentId)
            {
                m_log.WarnFormat("[Inventory]: Refusing to move folder {0} to new parent {1} for {2}. The source and destination are the same", folder.ID, parentId, folder.Owner);
                return;
            }

            // Don't do anything with a folder that wants to set its new parent to UUID.Zero
            if (parentId == UUID.Zero)
            {
                m_log.WarnFormat("[Inventory]: Refusing to move folder {0} to new parent {1} for {2}. New parent has ID UUID.Zero", folder.ID, parentId, folder.Owner);
                return;
            }

            _impl.moveInventoryFolder(folder, parentId);
        }

        public UUID SendFolderToTrash(InventoryFolderBase folder, UUID trashFolderHint)
        {
            InventoryFolderBase trashFolder = _impl.findUserFolderForType(folder.Owner, (int)FolderType.Trash);
            this.MoveFolder(folder, trashFolder.ID);

            return trashFolder.ID;
        }

        public InventoryFolderBase FindFolderForType(UUID owner, AssetType type)
        {
            InventoryFolderBase folder = _impl.findUserFolderForType(owner, (int)type);

            if (folder != null)
            {
                return folder;
            }
            else if (type == (AssetType)FolderType.Root)
            {
                //this is a special case for the legacy inventory services. 
                //the root folder type asset type may have been incorrectly saved
                //as the old AssetType.RootFolder (which is 9),
                //rather than AssetType.Folder (which is 8) with FolderType.Root (which is also 8).
                folder = _impl.findUserFolderForType(owner, (int)FolderType.OldRoot);
                if (folder == null)
                {
                    //this is another special case for the legacy inventory services.
                    //the root folder type may be incorrectly set to folder instead of RootFolder
                    //we must find it a different way in this case
                    return _impl.getUserRootFolder(owner);
                }
            }

            return null;
        }

        // Cassandra version searches the parentage tree for an ancestor folder with a matching type (e.g. Trash)
        public InventoryFolderBase FindTopLevelFolderFor(UUID owner, UUID folderID)
        {
            return _impl.findUserTopLevelFolderFor(owner, folderID);
        }

        public void PurgeFolderContents(InventoryFolderBase folder)
        {
            _impl.deleteFolderContents(folder.ID);
        }

        public void PurgeFolder(InventoryFolderBase folder)
        {
            _impl.deleteInventoryFolder(folder);
        }

        public void PurgeEmptyFolder(InventoryFolderBase folder)
        {
            PurgeFolder(folder);    // no optimization for this in the legacy implementation
        }

        public void PurgeFolders(IEnumerable<InventoryFolderBase> folders)
        {
            foreach (InventoryFolderBase folder in folders)
            {
                _impl.deleteInventoryFolder(folder);
            }
        }

        public InventoryItemBase GetItem(UUID itemId, UUID parentFolderHint)
        {
            return _impl.getInventoryItem(itemId);
        }

        public List<InventoryItemBase> GetItems(IEnumerable<UUID> itemIds, bool throwOnNotFound)
        {
            m_log.ErrorFormat("[Inventory]: GetItems NOT IMPLEMENTED");
            throw new NotImplementedException();
        }

        public void CreateItem(InventoryItemBase item)
        {
            // TODO: Maybe. Cassandra does a CheckAndFixItemParentFolder(item) here.   Not sure if that should be replicated here too... ~Ricky 20151007

            _impl.addInventoryItem(item);
        }

        public void SaveItem(InventoryItemBase item)
        {
            // TODO: Maybe. Cassandra does a CheckAndFixItemParentFolder(item) here.   Not sure if that should be replicated here too... ~Ricky 20151007

            _impl.updateInventoryItem(item);
        }

        public void MoveItem(InventoryItemBase item, InventoryFolderBase parentFolder)
        {
            item.Folder = parentFolder.ID;
            _impl.updateInventoryItem(item);
        }

        public UUID SendItemToTrash(InventoryItemBase item, UUID trashFolderHint)
        {
            InventoryFolderBase trashFolder = _impl.findUserFolderForType(item.Owner, (int)FolderType.Trash);
            this.MoveItem(item, trashFolder);

            return trashFolder.ID;
        }

        public void PurgeItem(InventoryItemBase item)
        {
            _impl.deleteInventoryItem(item);
        }

        public void PurgeItems(IEnumerable<InventoryItemBase> items)
        {
            foreach (InventoryItemBase item in items)
            {
                _impl.deleteInventoryItem(item);
            }
        }

        public void ActivateGestures(UUID userId, IEnumerable<UUID> itemIds)
        {
            foreach (UUID id in itemIds)
            {
                InventoryItemBase item = _impl.getInventoryItem(id);
                if (item != null)
                {
                    item.Flags |= (uint)1;
                    _impl.updateInventoryItem(item);
                }
            }
        }

        public void DeactivateGestures(UUID userId, IEnumerable<UUID> itemIds)
        {
            foreach (UUID id in itemIds)
            {
                InventoryItemBase item = _impl.getInventoryItem(id);
                if (item != null)
                {
                    item.Flags &= ~(uint)1;
                    _impl.updateInventoryItem(item);
                }
            }
        }

        public List<InventoryItemBase> GetActiveGestureItems(UUID userId)
        {
            return _impl.fetchActiveGestures(userId);
        }
    }
}
