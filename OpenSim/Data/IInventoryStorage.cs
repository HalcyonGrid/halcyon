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

using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data
{
    /// <summary>
    /// Inventory storage interface
    /// </summary>
    public interface IInventoryStorage
    {
        /// <summary>
        /// Returns a copy of all user inventory folders with subfolders and items excluded
        /// </summary>
        /// <returns>A list of all folders that belong to this user</returns>
        List<InventoryFolderBase> GetInventorySkeleton(UUID userId);

        /// <summary>
        /// Returns a full copy of the requested folder including items and sub folder ids
        /// </summary>
        /// <param name="folderId">The ID of the folder to retrieve</param>
        /// <returns>The folder that was found</returns>
        InventoryFolderBase GetFolder(UUID folderId);

        /// <summary>
        /// Returns a copy of the requested folder's properties. Excludes items and subfolder ids.
        /// </summary>
        /// <param name="folderId">The ID of the folder to retrieve</param>
        /// <returns>The folder that was found</returns>
        InventoryFolderBase GetFolderAttributes(UUID folderId);

        /// <summary>
        /// Creates a new folder and sets its parent correctly as well as other properties
        /// </summary>
        /// <param name="folder"></param>
        void CreateFolder(InventoryFolderBase folder);

        /// <summary>
        /// Stores changes made to the base properties of the folder. Can not be used to reassign a new
        /// parent
        /// </summary>
        /// <param name="folder">The folder to save</param>
        void SaveFolder(InventoryFolderBase folder);

        /// <summary>
        /// Moves the specified folder to the new parent
        /// </summary>
        /// <param name="folder">The folder to move</param>
        /// <param name="parentId">The destination folder to move the folder into</param>
        void MoveFolder(InventoryFolderBase folder, UUID parentId);

        /// <summary>
        /// Moves a folder and its subitems into the trash folder
        /// </summary>
        /// <param name="folder">The folder to remove</param>
        /// <param name="trashFolderHint">The UUID of the trash folder if known, or a Zero UUID</param>
        /// <returns>The UUID of the trash folder for caching</returns>
        UUID SendFolderToTrash(InventoryFolderBase folder, UUID trashFolderHint);

        /// <summary>
        /// Finds the best root folder to hold the given type
        /// </summary>
        /// <param name="type"></param>
        /// <returns>The best folder to put an object</returns>
        InventoryFolderBase FindFolderForType(UUID owner, AssetType type);

        /// <summary>
        /// Searches the parentage tree for an ancestor folder with a matching type (e.g. Trash)
        /// </summary>
        /// <param name="type"></param>
        /// <returns>The top-level parent folder</returns>
        InventoryFolderBase FindTopLevelFolderFor(UUID owner, UUID folderID);

        /// <summary>
        /// Purges all subfolders and items from the specified folder
        /// </summary>
        /// <param name="folder">The folder to purge</param>
        void PurgeFolderContents(InventoryFolderBase folder);

        /// <summary>
        /// Purges all subfolders and items from the specified folder and then removes the folder
        /// </summary>
        /// <param name="folder">The folder to purge</param>
        void PurgeFolder(InventoryFolderBase folder);

        /// <summary>
        /// Removes (deletes) a folder that is KNOWN to be empty.
        /// Caller must ensure it is already empty: no items or subfolders.
        /// </summary>
        /// <param name="folder">The folder to purge</param>
        void PurgeEmptyFolder(InventoryFolderBase folder);

        /// <summary>
        /// Purges all subfolders and items from the specified folders and then removes the folders
        /// </summary>
        /// <param name="folder">The folder to purge</param>
        void PurgeFolders(IEnumerable<InventoryFolderBase> folders);

        /// <summary>
        /// Returns an item fetched by the given id
        /// </summary>
        /// <param name="itemId">The item id</param>
        /// <param name="parentFolderHint">An optional hint to the parent folder that contains the item. If unknown pass UUID.Zero</param>
        /// <returns>The item that matches the id, or null if none found</returns>
        InventoryItemBase GetItem(UUID itemId, UUID parentFolderHint);

        /// <summary>
        /// Retrieves the given items by ID
        /// </summary>
        /// <param name="itemIds">A list of item ids to retrieve</param>
        /// <param name="throwOnNotFound">Whether or not to throw an exception if one or more items cant be found</param>
        /// <returns>The items that were found</returns>
        /// <remarks>THIS IS A CONVENIENCE FUNCTION. IT IS NOT GUARANTEED TO BE NETWORK EFFICIENT</remarks>
        List<InventoryItemBase> GetItems(IEnumerable<UUID> itemIds, bool throwOnNotFound);

        /// <summary>
        /// Creates a new item in the given folder
        /// </summary>
        /// <param name="item">The item to create</param>
        void CreateItem(InventoryItemBase item);

        /// <summary>
        /// Saves changes that have been made to an item
        /// </summary>
        /// <param name="item">The item to store</param>
        void SaveItem(InventoryItemBase item);
        
        /// <summary>
        /// Moves the given item to the given folder
        /// </summary>
        /// <param name="item">The item to move</param>
        /// <param name="parentFolder">The parent folder to move the item into</param>
        void MoveItem(InventoryItemBase item, InventoryFolderBase parentFolder);

        /// <summary>
        /// Sends the given item to the trash folder
        /// </summary>
        /// <param name="item">The item to send to the trash</param>
        /// <param name="trashFolderHint">The UUID of the trash folder if known, or a Zero UUID</param>
        UUID SendItemToTrash(InventoryItemBase item, UUID trashFolderHint);

        /// <summary>
        /// Purges a single item from the inventory
        /// </summary>
        /// <param name="item">The item to purge</param>
        void PurgeItem(InventoryItemBase item);

        /// <summary>
        /// Purges multiple items from the inventory
        /// </summary>
        /// <param name="item">The item to purge</param>
        void PurgeItems(IEnumerable<InventoryItemBase> items);

        /// <summary>
        /// Marks the given gesture items active
        /// </summary>
        /// <param name="userId">The user that owns the gestures</param>
        /// <param name="itemIds">The items that are to be activated</param>
        void ActivateGestures(UUID userId, IEnumerable<UUID> itemIds);

        /// <summary>
        /// Marks the given gesture items inactive
        /// </summary>
        /// <param name="userId">The user that owns the gestures</param>
        /// <param name="itemIds">The items that are to be activated</param>
        void DeactivateGestures(UUID userId, IEnumerable<UUID> itemIds);

        /// <summary>
        /// Returns the inventory items corresponding to the active gestures the
        /// user currently has set
        /// </summary>
        /// <param name="userId">The ID for the user we want to search</param>
        /// <returns>A list or empty list of gestures</returns>
        List<InventoryItemBase> GetActiveGestureItems(UUID userId);
    }
}
