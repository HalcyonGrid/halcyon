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
using OpenSim.Data;
using OpenSim.Framework;
using OpenMetaverse;

namespace OpenSim.Data
{
    public class CheckedInventoryStorage : ICheckedInventoryStorage
    {
        private IInventoryStorage _storage;

        public CheckedInventoryStorage(IInventoryStorage storage)
        {
            _storage = storage;
        }

        public List<InventoryFolderBase> GetInventorySkeleton(UUID userId)
        {
            return _storage.GetInventorySkeleton(userId);
        }

        public InventoryFolderBase GetFolder(UUID userId, UUID folderId)
        {
            InventoryFolderBase folder = _storage.GetFolder(folderId);

            if (folder != null && folder.Owner != userId)
            {
                throw new InventorySecurityException(String.Format("Not retrieving folder {0} for user {1}. User does not own the folder", folderId, userId));
            }

            return folder;
        }

        public InventoryFolderBase GetFolderAttributes(UUID userId, UUID folderId)
        {
            InventoryFolderBase folder = _storage.GetFolderAttributes(folderId);

            if (folder != null && folder.Owner != userId)
            {
                throw new InventorySecurityException(String.Format("Not retrieving folder {0} attributes for user {1}. User does not own the folder", folderId, userId));
            }

            return folder;
        }

        public void CreateFolder(UUID userId, InventoryFolderBase folder)
        {
            if (folder.Owner != userId)
            {
                throw new InventorySecurityException(String.Format("Not creating folder {0} for user {1}. User does not own the folder", folder.ID, userId));
            }

            _storage.CreateFolder(folder);
        }

        public void SaveFolder(UUID userId, InventoryFolderBase folder)
        {
            if (folder.Owner != userId)
            {
                throw new InventorySecurityException(String.Format("Not saving folder {0} for user {1}. User does not own the folder", folder.ID, userId));
            }

            _storage.SaveFolder(folder);
        }

        public void MoveFolder(UUID userId, UUID folderId, UUID parentId)
        {
            //retrieve both folders and verify the owners match
            InventoryFolderBase subjectFolder = _storage.GetFolderAttributes(folderId);
            InventoryFolderBase newParentFolder = _storage.GetFolderAttributes(parentId);

            if (subjectFolder.Owner != userId || newParentFolder.Owner != userId)
            {
                throw new InventorySecurityException(String.Format("Not moving folder {0} for user {1}. User does not own the folder", subjectFolder.ID, userId));
            }

            _storage.MoveFolder(subjectFolder, parentId);
        }

        public UUID SendFolderToTrash(UUID userId, UUID folderId, UUID trashFolderHint)
        {
            //verify this user owns the folder
            InventoryFolderBase subjectFolder = _storage.GetFolderAttributes(folderId);

            if (subjectFolder.Owner != userId)
            {
                throw new InventorySecurityException(String.Format("Not trashing folder {0} for user {1}. User does not own the folder", subjectFolder.ID, userId));
            }

            return _storage.SendFolderToTrash(subjectFolder, trashFolderHint);
        }

        public InventoryFolderBase FindFolderForType(UUID owner, AssetType type)
        {
            return _storage.FindFolderForType(owner, type);
        }

        // Searches the parentage tree for an ancestor folder with a matching type (e.g. Trash)
        public InventoryFolderBase FindTopLevelFolderFor(UUID owner, UUID folderID)
        {
            return _storage.FindTopLevelFolderFor(owner, folderID);
        }

        public void PurgeFolderContents(UUID userId, UUID folderId)
        {
            //verify this user owns the folder
            InventoryFolderBase subjectFolder = _storage.GetFolderAttributes(folderId);

            if (subjectFolder.Owner != userId)
            {
                throw new InventorySecurityException(String.Format("Not purging folder contents in folder {0} for user {1}. User does not own the folder", subjectFolder.ID, userId));
            }

            _storage.PurgeFolderContents(subjectFolder);
        }

        public void PurgeFolder(UUID userId, UUID folderId)
        {
            //verify this user owns the folder
            InventoryFolderBase subjectFolder = _storage.GetFolderAttributes(folderId);

            if (subjectFolder != null && subjectFolder.Owner != userId)
            {
                throw new InventorySecurityException(String.Format("Not purging folder {0} for user {1}. User does not own the folder", subjectFolder.ID, userId));
            }

            _storage.PurgeFolder(subjectFolder);
        }

        /*
        public void PurgeFolders(UUID userId, IEnumerable<UUID> folderIds)
        {
            //this is a very uncommon operation, and normamly will only be executed
            //when a user selects individual items from their trash to purge
            //therefore, we will grab each folder individually
            List<InventoryFolderBase> folders = new List<InventoryFolderBase>();
            foreach (UUID id in folderIds)
            {
                InventoryFolderBase folder = _storage.GetFolderAttributes(id);

                if (folder != null)
                {
                    if (folder.Owner != userId)
                    {
                        throw new InventorySecurityException(String.Format("Not purging folder {0} for user {1}. User does not own the folder", folder.ID, userId));
                    }

                    folders.Add(folder);
                }
            }

            _storage.PurgeFolders(folders);
        }
*/
        public InventoryItemBase GetItem(UUID userId, UUID itemId, UUID parentFolderHint)
        {
            //verify this user owns the item
            InventoryItemBase subjectItem = _storage.GetItem(itemId, parentFolderHint);

            if (subjectItem != null && subjectItem.Owner != userId)
            {
                throw new InventorySecurityException(String.Format("Not retrieving item {0} for user {1}. User does not own the item", subjectItem.ID, userId));
            }

            return subjectItem;
        }

        public List<InventoryItemBase> GetItems(UUID userId, List<UUID> itemIds, bool throwOnNotFound)
        {
            //verify this user owns the item
            List<InventoryItemBase> subjectItems = _storage.GetItems(itemIds, throwOnNotFound);

            foreach (InventoryItemBase subjectItem in subjectItems)
            {
                if (subjectItem.Owner != userId)
                {
                    throw new InventorySecurityException(String.Format("Not retrieving item {0} for user {1}. User does not own the item", subjectItem.ID, userId));
                }
            }

            return subjectItems;
        }

        public void CreateItem(UUID userId, InventoryItemBase item)
        {
            if (item.Owner != userId)
            {
                throw new InventorySecurityException(String.Format("Not creating item {0} for user {1}. User does not own the item", item.ID, userId));
            }

            _storage.CreateItem(item);
        }

        public void SaveItem(UUID userId, InventoryItemBase item)
        {
            if (item.Owner != userId)
            {
                throw new InventorySecurityException(String.Format("Not saving item {0} for user {1}. User does not own the item", item.ID, userId));
            }

            _storage.SaveItem(item);
        }

        public void MoveItem(UUID userId, UUID itemId, UUID parentFolderId)
        {
            InventoryItemBase item = _storage.GetItem(itemId, UUID.Zero);
            InventoryFolderBase folder = _storage.GetFolderAttributes(parentFolderId);

            if (item == null)
            {
                throw new InventoryObjectMissingException(String.Format("Not moving null item for user {0}. The item could not be found", userId));
            }

            if (folder == null)
            {
                throw new InventoryObjectMissingException(String.Format("Not moving item {0} for user {1}. The destination folder could not be found", item.ID, userId));
            }

            if (item.Owner != userId || item.Owner != folder.Owner)
            {
                throw new InventorySecurityException(String.Format("Not moving item {0} for user {1}. User does not own the item or destination folder", item.ID, userId));
            }

            _storage.MoveItem(item, folder);
        }

        public void ModifyAndMoveItem(UUID userId, InventoryItemBase item, UUID parentFolderId)
        {
            InventoryFolderBase folder = _storage.GetFolderAttributes(parentFolderId);

            if (item.Owner != userId || item.Owner != folder.Owner)
            {
                throw new InventorySecurityException(String.Format("Not moving item {0} for user {1}. User does not own the item or destination folder", item.ID, userId));
            }

            _storage.MoveItem(item, folder);
        }

        public UUID SendItemToTrash(UUID userId, UUID itemId, UUID trashFolderHint)
        {
            InventoryItemBase item = _storage.GetItem(itemId, UUID.Zero);

            if (item.Owner != userId)
            {
                throw new InventorySecurityException(String.Format("Not trashing item {0} for user {1}. User does not own the item", item.ID, userId));
            }

            return _storage.SendItemToTrash(item, trashFolderHint);
        }

        public void PurgeItem(UUID userId, UUID itemId)
        {
            InventoryItemBase item = _storage.GetItem(itemId, UUID.Zero);

            if (item.Owner != userId)
            {
                throw new InventorySecurityException(String.Format("Not purging item {0} for user {1}. User does not own the item", item.ID, userId));
            }

            _storage.PurgeItem(item);
        }

        /*
        public void PurgeItems(UUID userId, IEnumerable<UUID> itemIds)
        {
            List<InventoryItemBase> items = _storage.GetItems(itemIds, false);

            foreach (InventoryItemBase item in items)
            {
                if (item.Owner != userId)
                {
                    throw new InventorySecurityException(String.Format("Not purging item {0} for user {1}. User does not own the item", item.ID, userId));
                }
            }

            _storage.PurgeItems(items);
        }
        */

        public void ActivateGestures(UUID userId, IEnumerable<UUID> itemIds)
        {
            _storage.ActivateGestures(userId, itemIds);
        }

        public void DeactivateGestures(UUID userId, IEnumerable<UUID> itemIds)
        {
            _storage.DeactivateGestures(userId, itemIds);
        }

        public List<InventoryItemBase> GetActiveGestureItems(UUID userId)
        {
            return _storage.GetActiveGestureItems(userId);
        }

    }
}
