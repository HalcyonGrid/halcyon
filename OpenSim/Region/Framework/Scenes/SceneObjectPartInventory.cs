/*
 * Copyright (c) InWorldz Halcyon Developers
 * Copyright (c) Contributors, http://opensimulator.org/
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using OpenMetaverse;
using log4net;
using OpenSim.Framework;
using OpenSim.Framework.Communications.Cache;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes.Scripting;
using System.Text;

namespace OpenSim.Region.Framework.Scenes
{
    public class SceneObjectPartInventory : IEntityInventory
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private string m_inventoryFileName = String.Empty;
        private int m_inventoryFileNameSerial = 0;
        
        /// <value>
        /// The part to which the inventory belongs.
        /// </value>
        private SceneObjectPart m_part;

        /// <summary>
        /// Serial count for inventory file , used to tell if inventory has changed
        /// no need for this to be part of Database backup
        /// </summary>
        protected uint m_inventorySerial = 0;

        /// <summary>
        /// Holds in memory prim inventory
        /// </summary>
        protected TaskInventoryDictionary m_items = new TaskInventoryDictionary();

        /// <summary>
        /// Stores a list of deleted items
        /// </summary>
        private List<UUID> _removedItems = new List<UUID>();

        /// <summary>
        /// Tracks whether inventory has changed since the last persistent backup
        /// </summary>
        internal bool HasInventoryChanged;
        
        /// <value>
        /// Inventory serial number
        /// </value>
        protected internal uint Serial
        {
            get { return m_inventorySerial; }
            set { m_inventorySerial = value; }
        }

        /// <value>
        /// Raw inventory data
        /// </value>
        protected internal TaskInventoryDictionary Items
        {
            get { return m_items; }
            set
            {
                m_items = value;
                m_inventorySerial++;
            }
        }
        
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="part">
        /// A <see cref="SceneObjectPart"/>
        /// </param>
        public SceneObjectPartInventory(SceneObjectPart part)
        {
            m_part = part;
        }

        public SceneObjectPartInventory CloneForPartCopy(SceneObjectPart part, bool userExposed)
        {
            SceneObjectPartInventory newInventory = new SceneObjectPartInventory(part);

            //also copy the delete list
            newInventory._removedItems = new List<UUID>(_removedItems);

            // Duplicate the Contents now, skipping no-copy items.
            foreach (KeyValuePair<UUID, TaskInventoryItem> pair in Items)
            {
                TaskInventoryItem item = pair.Value;

                if (!userExposed)
                {
                    TaskInventoryItem dupeitem = (TaskInventoryItem)item.Clone();
                    newInventory.AddInventoryItem(dupeitem, false, userExposed);
                }
                else if ((item.CurrentPermissions & (uint)PermissionMask.Copy) == (uint)PermissionMask.Copy)
                {
                    TaskInventoryItem dupeitem = (TaskInventoryItem)item.Clone();
                    newInventory.AddInventoryItem(dupeitem, false, userExposed);
                }
            }

            return newInventory;
        }

        /// <summary>
        /// Force the task inventory of this prim to persist at the next update sweep
        /// </summary>
        public void ForceInventoryPersistence()
        {
            HasInventoryChanged = true;
        }

        /// <summary>
        /// Marks the inventory as saved
        /// </summary>
        public void MarkInventoryClean()
        {
            HasInventoryChanged = false;
        }

        /// <summary>
        /// Reset UUIDs for all the items in the prim's inventory.  This involves either generating
        /// new ones or setting existing UUIDs to the correct parent UUIDs.
        ///
        /// If this method is called and there are inventory items, then we regard the inventory as having changed.
        /// </summary>
        /// <param name="linkNum">Link number for the part</param>
        public void ResetInventoryIDs()
        {
            lock (Items)
            {
                if (0 == Items.Count)
                    return;

                HasInventoryChanged = true;

                if (m_part.ParentGroup != null)
                {
                    m_part.ParentGroup.HasGroupChanged = true;
                }

                IList<TaskInventoryItem> items = new List<TaskInventoryItem>(Items.Values);
                Items.Clear();

                foreach (TaskInventoryItem item in items)
                {
                    item.ResetIDs(m_part.UUID);
                    Items.Add(item.ItemID, item);
                }
            }
        }

        /// <summary>
        /// Change every item in this inventory to a new owner.
        /// </summary>
        /// <param name="ownerId"></param>
        public void ChangeInventoryOwner(UUID ownerId)
        {
            lock (Items)
            {
                if (0 == Items.Count)
                {
                    return;
                }

                HasInventoryChanged = true;
                m_part.ParentGroup.HasGroupChanged = true;
                IList<TaskInventoryItem> items = new List<TaskInventoryItem>(Items.Values);
                foreach (TaskInventoryItem item in items)
                {
                    if (ownerId != item.OwnerID)
                    {
                        item.LastOwnerID = item.OwnerID;
                        item.OwnerID = ownerId;
                    }
                }
            }
        }

        /// <summary>
        /// Change every item in this inventory to a new group.
        /// </summary>
        /// <param name="groupID"></param>        
        public void ChangeInventoryGroup(UUID groupID)
        {
            lock (Items)
            {
                if (0 == Items.Count)
                {
                    return;
                }

                HasInventoryChanged = true;
                m_part.ParentGroup.HasGroupChanged = true;
                IList<TaskInventoryItem> items = new List<TaskInventoryItem>(Items.Values);
                foreach (TaskInventoryItem item in items)
                {
                    if (groupID != item.GroupID)
                    {
                        item.GroupID = groupID;
                    }
                }
            }
        }

        /// <summary>
        /// Start all the scripts contained in this prim's inventory
        /// </summary>
        public void CreateScriptInstances(int? startParam, ScriptStartFlags startFlags, string engine, int stateSource, ICompilationListener listener)
        {
            lock (m_items)
            {
                foreach (TaskInventoryItem item in Items.Values)
                {
                    if ((int)InventoryType.LSL == item.InvType)
                    {
                        m_part.AddFlag(PrimFlags.Scripted);
                        CreateScriptInstance(item, startParam, startFlags, engine, stateSource, listener);
                    }
                }
            }
        }

        /// <summary>
        /// Stop all the scripts in this prim.
        /// </summary>
        public void RemoveScriptInstances()
        {
            //using a copy here to break a the chain of locks
            List<TaskInventoryItem> itemsToRemove;
            lock (Items)
            {
                itemsToRemove = new List<TaskInventoryItem>(Items.Values);
            }

            foreach (TaskInventoryItem item in itemsToRemove)
            {
                if ((int)InventoryType.LSL == item.InvType)
                {
                    RemoveScriptInstance(item.ItemID);
                    m_part.RemoveScriptEvents(item.ItemID);
                }
            }
            
        }

        /// <summary>
        /// Resets all the scripts and item attributes in this prim (when itemId == UUID.Zero).
        /// If itemId is specified, only that script is reset.
        /// </summary>
        public void ResetItems(bool isNewInstance, bool isScriptReset, UUID itemId)
        {
            // Nothing to do anymore... handled by LSLSystemAPI via TriggerScriptReset
        }

        /// <summary>
        /// Start or reload a script which is in this prim's inventory.
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public void CreateScriptInstance(TaskInventoryItem item, int? startParam, ScriptStartFlags startFlags, string engine, int stateSource,
            ICompilationListener compilationListener, bool isReload)
        {
            if (!m_part.ParentGroup.Scene.Permissions.CanRunScript(item.ItemID, m_part.ParentGroup.UUID, item.OwnerID))
                return;

            if (!m_part.ParentGroup.Scene.RegionInfo.RegionSettings.DisableScripts)
            {
                if (isReload)
                {
                    m_part.ParentGroup.Scene.EventManager.TriggerReloadScript(
                        m_part.LocalId, item.ItemID, String.Empty, startParam, startFlags, engine, stateSource, compilationListener);
                }
                else
                {
                    m_part.ParentGroup.Scene.EventManager.TriggerRezScript(
                        m_part.LocalId, item, String.Empty, startParam, startFlags, engine, stateSource, compilationListener);
                }

                m_part.ParentGroup.AddActiveScriptCount(1);
                m_part.ScheduleFullUpdate(PrimUpdateFlags.PrimFlags);
            }
        }

        /// <summary>
        /// Start a script which is in this prim's inventory.
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public void CreateScriptInstance(TaskInventoryItem item, int? startParam, ScriptStartFlags startFlags, string engine, int stateSource,
            ICompilationListener compilationListener)
        {
            this.CreateScriptInstance(item, startParam, startFlags, engine, stateSource, compilationListener, false);
        }

        /// <summary>
        /// Start a script which is in this prim's inventory.
        /// </summary>
        /// <param name="itemId">
        /// A <see cref="UUID"/>
        /// </param>
        public void CreateScriptInstance(UUID itemId, int? startParam, ScriptStartFlags startFlags, string engine, int stateSource, 
            ICompilationListener compilationListener)
        {
            this.CreateScriptInstance(itemId, startParam, startFlags, engine, stateSource, compilationListener, false);
        }

        /// <summary>
        /// Start/Restart a script which is in this prim's inventory.
        /// </summary>
        /// <param name="itemId">
        /// A <see cref="UUID"/>
        /// </param>
        public void CreateScriptInstance(UUID itemId, int? startParam, ScriptStartFlags startFlags, string engine, int stateSource,
            ICompilationListener compilationListener, bool isReload)
        {
            lock (m_items)
            {
                if (m_items.ContainsKey(itemId))
                {
                    CreateScriptInstance(m_items[itemId], startParam, startFlags, engine, stateSource, compilationListener, isReload);
                }
                else
                {
                    m_log.ErrorFormat(
                        "[PRIM INVENTORY]: " +
                        "Couldn't start script with ID {0} since it couldn't be found for prim {1}, {2}",
                        itemId, m_part.Name, m_part.UUID);
                }
            }
        }

        /// <summary>
        /// Stop a script which is in this prim's inventory.
        /// </summary>
        /// <param name="itemId"></param>
        public void RemoveScriptInstance(UUID itemId)
        {
            bool contains;
            lock (m_items)
            {
                contains = m_items.ContainsKey(itemId);
            }

            if (contains)
            {
                m_part.ParentGroup.Scene.EventManager.TriggerRemoveScript(m_part.LocalId, itemId, m_part, null, null, false, false);
                m_part.ParentGroup.AddActiveScriptCount(-1);
            }
            else
            {
                m_log.ErrorFormat(
                    "[PRIM INVENTORY]: " +
                    "Couldn't stop script with ID {0} since it couldn't be found for prim {1}, {2}",
                    itemId, m_part.Name, m_part.UUID);
            }
        }

        /// <summary>
        /// Check if the inventory holds an item with a given name.
        /// This method assumes that the task inventory is already locked.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        private bool InventoryContainsName(string name)
        {
            lock (m_items)
            {
                foreach (TaskInventoryItem item in Items.Values)
                {
                    if (item.Name == name)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// For a given item name, return that name if it is available.  Otherwise, return the next available
        /// similar name (which is currently the original name with the next available numeric suffix).
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        private string FindAvailableInventoryName(string name)
        {
            if (!InventoryContainsName(name))
                return name;

            int suffix=1;
            while (suffix < 256)
            {
                string tryName=String.Format("{0} {1}", name, suffix);
                if (!InventoryContainsName(tryName))
                    return tryName;
                suffix++;
            }
            return String.Empty;
        }

        /// <summary>
        /// Add an item to this prim's inventory.  If an item with the same name already exists, then an alternative
        /// name is chosen.
        /// </summary>
        /// <param name="item"></param>
        public void AddInventoryItem(TaskInventoryItem item, bool allowedDrop, bool fireEvents)
        {
            AddInventoryItem(item.Name, item, allowedDrop, fireEvents, null);
        }

        /// <summary>
        /// Add an item to this prim's inventory.  If an item with the same name already exists, it is replaced.
        /// </summary>
        /// <param name="item"></param>
        public void AddReplaceInventoryItem(TaskInventoryItem item, bool allowedDrop, bool fireEvents, ReplaceItemArgs replaceArgs)
        {
            List<TaskInventoryItem> il;

            lock (m_items)
            {
                il = new List<TaskInventoryItem>(m_items.Values);
            }

            foreach (TaskInventoryItem i in il)
            {
                if (i.Name == item.Name)
                {
                    ReplaceInventoryItem(i.ItemID, allowedDrop, fireEvents, replaceArgs);
                    return;        // found it, all done
                }
            }

            AddInventoryItem(item.Name, item, allowedDrop, fireEvents, replaceArgs);
        }

        /// <summary>
        /// Add an item to this prim's inventory.
        /// </summary>
        /// <param name="name">The name that the new item should have.</param>
        /// <param name="item">
        /// The item itself.  The name within this structure is ignored in favour of the name
        /// given in this method's arguments
        /// </param>
        /// <param name="allowedDrop">
        /// Item was only added to inventory because AllowedDrop is set
        /// </param>
        protected void AddInventoryItem(string name, TaskInventoryItem item, bool allowedDrop, bool fireEvents, ReplaceItemArgs replaceArgs)
        {
            name = FindAvailableInventoryName(name);
            if (String.IsNullOrEmpty(name))
                return;

            item.ParentID = m_part.UUID;
            item.ParentPartID = m_part.UUID;
            item.Name = name;

            lock (m_items)
            {
                m_items.Add(item.ItemID, item);
            }

            if (fireEvents)
            {
                if (allowedDrop)
                    m_part.TriggerScriptChangedEvent(Changed.ALLOWED_DROP);
                else
                    m_part.TriggerScriptChangedEvent(Changed.INVENTORY);
            }

            m_inventorySerial++;
            //m_inventorySerial += 2;
            HasInventoryChanged = true;

            //may be parentless briefly while a copy is performed
            if (m_part.ParentGroup != null)
            {
                m_part.ParentGroup.HasGroupChanged = true;
            }

            // If called from AddReplaceInventoryItem (above), the script may be running, depending on
            // llRemoteLoadScriptPin params, so tie up the loose ends of adding a new running script.
            HandleChangedScripts(item, replaceArgs);
        }

        /// <summary>
        /// Restore a whole collection of items to the prim's inventory at once.
        /// We assume that the items already have all their fields correctly filled out.
        /// The items are not flagged for persistence to the database, since they are being restored
        /// from persistence rather than being newly added.
        /// </summary>
        /// <param name="items"></param>
        public void RestoreInventoryItems(ICollection<TaskInventoryItem> items)
        {
            lock (m_items)
            {
                foreach (TaskInventoryItem item in items)
                {
                    m_items.Add(item.ItemID, item);
                    m_part.TriggerScriptChangedEvent(Changed.INVENTORY);
                }
            }

            m_inventorySerial++;
        }

        public void RestoreSingleInventoryItem(TaskInventoryItem item)
        {
            lock (m_items)
            {
                m_items.Add(item.ItemID, item);
                m_part.TriggerScriptChangedEvent(Changed.INVENTORY);
            }

            m_inventorySerial++;
        }

        /// <summary>
        /// Returns an existing inventory item.  Returns the original, so any changes will be live.
        /// </summary>
        /// <param name="itemID"></param>
        /// <returns>null if the item does not exist</returns>
        public TaskInventoryItem GetInventoryItem(UUID itemId)
        {
            TaskInventoryItem item;
            lock (m_items)
            {
                
                if (m_items.TryGetValue(itemId, out item))
                {
                    // Synchronize ScenePermBits.SLAM with InventoryItemFlags.ObjectSlamPerm
                    if (item.InvType == (int)InventoryType.Object)
                    {
                        if ((item.Flags & (uint)InventoryItemFlags.ObjectSlamPerm) != 0)
                            item.CurrentPermissions |= ScenePermBits.SLAM;
                        if ((item.CurrentPermissions & ScenePermBits.SLAM) != 0)
                            item.Flags |= (uint)InventoryItemFlags.ObjectSlamPerm;
                    }
                    else
                    {
                        item.Flags &= ~(uint)InventoryItemFlags.ObjectSlamPerm;
                        item.CurrentPermissions &= ~ScenePermBits.SLAM;
                    }
                }
            }

            return item;
        }

        /// <summary>
        /// Update an existing inventory item with a new asset (ID).
        /// </summary>
        /// <param name="ParentPartID">Part of the inventory interface, but ignored in this function. Should match this.UUID.</param>
        /// <param name="ItemID">The item to update. An item with the same id must already exist
        /// in this prim's inventory.</param>
        /// <param name="AssetID">The ID of the new asset to replace in the item above.</param>
        /// <returns>false if the item did not exist or a null ID was passed, true if the update occurred successfully</returns>
        public bool UpdateTaskInventoryItemAsset(UUID ParentPartID, UUID ItemID, UUID AssetID)
        {
            if (AssetID == UUID.Zero)
                return false;

            lock (m_items)
            {
                if (m_items.ContainsKey(ItemID))
                {
                    TaskInventoryItem item = m_items[ItemID];
                    if (item.AssetID == AssetID)
                    {
                        // the caller needs to let us do the update here, or we don't have the previous value.
                        m_log.ErrorFormat("[PRIM INVENTORY]: " +
                            "UpdateTaskInventoryItemAsset for item {0} already has asset updated {1}.",
                            item.ItemID, AssetID);
                    }
                    item.AssetID = AssetID;
                    m_inventorySerial++;
                    m_part.TriggerScriptChangedEvent(Changed.INVENTORY);
                    HasInventoryChanged = true;
                    m_part.ParentGroup.HasGroupChanged = true;
                    m_part.ScheduleFullUpdate(PrimUpdateFlags.PrimFlags);
                    return true;
                }
                else
                {
                    m_log.ErrorFormat(
                        "[PRIM INVENTORY]: " +
                        "Tried to retrieve item ID {0} from prim {1}, {2} but the item does not exist in this inventory",
                        ItemID, m_part.Name, m_part.UUID);
                }
            }

            return false;
        }

        /// <summary>
        /// Update an existing inventory item.
        /// </summary>
        /// <param name="item">The updated item.  An item with the same id must already exist
        /// in this prim's inventory.</param>
        /// <returns>false if the item did not exist, true if the update occurred successfully</returns>
        public bool UpdateTaskInventoryItemFromItem(TaskInventoryItem fromItem)
        {
            lock (m_items)
            {
                if (m_items.ContainsKey(fromItem.ItemID))
                {
                    TaskInventoryItem item = m_items[fromItem.ItemID];

                    bool slam = (item.CurrentPermissions & (uint)ScenePermBits.SLAM) != 0;

                    item.ParentID = m_part.UUID;
                    item.ParentPartID = m_part.UUID;

                    if (fromItem.AssetID != UUID.Zero)
                    {
                        item.AssetID = fromItem.AssetID;
                    }

                    // Check if next owner perms were changed
                    if (item.InvType == (int)InventoryType.Object)
                        slam = (item.NextPermissions != fromItem.NextPermissions);

                    // Now update specific fields from the specified item directly to the inventory item (while m_items is locked).
                    item.Name = fromItem.Name;
                    item.Description = fromItem.Description;
                    item.GroupID = fromItem.GroupID;
                    item.GroupPermissions = fromItem.GroupPermissions;          // already prefiltered/validated
                    item.NextPermissions = fromItem.NextPermissions;            // already prefiltered/validated
                    item.EveryonePermissions = fromItem.EveryonePermissions;    // already prefiltered/validated
                    item.CreationDate = fromItem.CreationDate;
                    item.InvType = fromItem.InvType;
                    item.Flags = fromItem.Flags;

                    if (slam)
                    {
                        item.CurrentPermissions |= (uint)ScenePermBits.SLAM;    // Slam!
                        item.Flags |= (uint)InventoryItemFlags.ObjectSlamPerm;  // Tell the viewer we are going to slam this
                    }

                    m_inventorySerial++;
                    m_part.TriggerScriptChangedEvent(Changed.INVENTORY);

                    HasInventoryChanged = true;
                    m_part.ParentGroup.HasGroupChanged = true;
                    m_part.ScheduleFullUpdate(PrimUpdateFlags.PrimFlags);
                    return true;
                }
                else
                {
                    m_log.ErrorFormat(
                        "[PRIM INVENTORY]: " +
                        "Tried to retrieve item ID {0} from prim {1}, {2} but the item does not exist in this inventory",
                        fromItem.ItemID, m_part.Name, m_part.UUID);
                }
            }

            return false;
        }

        /// <summary>
        /// Called indirectly from EventManager.TriggerRemoveScript() once the script engine has 
        /// removed the script and the task item can be safely deleted.
        /// If replaceArgs is null, it's a remove, otherwise it's a replace (remove plus add).
        /// </summary>
        private void _ReplaceItem(SceneObjectPart part, UUID itemID, bool allowedDrop, bool fireEvents, ReplaceItemArgs replaceArgs)
        {
            TaskInventoryItem newItem = null;
            bool scriptUpdate = false;

            // Avoid a race condition where the viewer might send what results in a second _Replace(itemID, null) 
            // for a delete which is a *duplicate*. This can happen if the user deletes the TaskInventoryItem 
            // a *second* time before the viewer updates the Contents to remove it. This change just handle 
            // the itemID no longer being present in the items list.
            TaskInventoryItem item;
            if (!m_items.TryGetValue(itemID, out item))
                return;

            InventoryType type = (InventoryType) item.InvType;
            if (type == InventoryType.LSL) // Script
                scriptUpdate = true;
            if ((replaceArgs != null) && (replaceArgs.ReplacementItem != null))
                newItem = replaceArgs.ReplacementItem;

            if (m_items.Remove(itemID))
                _removedItems.Add(itemID);
            m_inventorySerial++;
            m_part.TriggerScriptChangedEvent(Changed.INVENTORY);
            HasInventoryChanged = true;
            m_part.ParentGroup.HasGroupChanged = true;

            if (newItem != null)
            {
                // AddInventoryItem increments the serial and triggering the events
                AddInventoryItem(replaceArgs.ReplacementItem.Name, newItem, allowedDrop, fireEvents, null);
            }

            if (scriptUpdate)
            {
                HandleChangedScripts(newItem, replaceArgs);
            }
            m_part.ScheduleFullUpdate(PrimUpdateFlags.PrimFlags);
        }

        /// <summary>
        /// Scans the items to see how many have scripted controls.
        /// </summary>
        /// <returns>The total number of scripts holding controls.</returns>
        public int GetScriptedControlsCount()
        {
            int total = 0;
            lock (m_items)
            {
                foreach (TaskInventoryItem item in m_items.Values)
                {
                    if ((InventoryType)item.Type == InventoryType.LSL)
                    {
//                        m_log.WarnFormat("[CONTROLS]: Item {0} has control sets: {1}", item.ItemID, item.HasScriptedControls);
                        if (item.HasScriptedControls)
                            total++;
                    }
                }
            }
            return total;
        }

        private void HandleChangedScripts(TaskInventoryItem newItem, ReplaceItemArgs replaceArgs)
        {
            int scriptcount = 0;
            foreach (TaskInventoryItem item in m_items.Values)
            {
                if ((InventoryType)item.Type == InventoryType.LSL)
                {
                    scriptcount++;
                }
            }

            if (scriptcount > 0)
                m_part.AddFlag(PrimFlags.Scripted);
            else
                m_part.RemFlag(PrimFlags.Scripted);

            // Update for loss of other events (e.g. money, touch)
            m_part.DoAggregateScriptEvents();

            if (replaceArgs != null)
            {
                Scene scene = m_part.ParentGroup.Scene;
                if (replaceArgs.Running > 0)
                {
                    m_part.Inventory.CreateScriptInstance(newItem, replaceArgs.StartParam, ScriptStartFlags.None, scene.DefaultScriptEngine, 0, null);
                }

                ScenePresence avatar;

                if (scene.TryGetAvatar(newItem.OwnerID, out avatar))
                {
                    m_part.GetProperties(avatar.ControllingClient);
                }
            }
        }

        /// <summary>
        /// WARNING: This operation is asynchronous if the item is a script!
        /// Since removing a script from a prim's inventory is asynchronous, 
        /// replacing one requires the Add be performed in the Remove callback.
        /// This function is also used by RemoveInventoryItem (newItem==null).
        /// </summary>
        /// <param name="itemID">the item to replace or remove</param>
        /// <param name="replaceArgs">the new item to use to replace, or null on a Remove</param>
        /// <returns>Numeric asset type of the item removed.
        /// Returns -1 if the item did not exist in this prim's inventory.</returns>
        public int ReplaceInventoryItem(UUID itemID, bool allowedDrop, bool fireEvents, ReplaceItemArgs replaceArgs)
        {
            lock (m_items)
            {
                if (!m_items.ContainsKey(itemID))
                {
                    m_log.ErrorFormat(
                        "[PRIM INVENTORY]: " +
                        "Tried to remove item ID {0} from prim {1}, {2} but the item does not exist in this inventory",
                        itemID, m_part.Name, m_part.UUID);
                    return -1;
                }

                int type = m_items[itemID].InvType;
                if (type == 10) // Script
                {   
                    // _RemoveItem will be called asynchronously from the script engine thread
                    m_part.ParentGroup.Scene.EventManager.TriggerRemoveScript(m_part.LocalId, itemID, m_part, _ReplaceItem, replaceArgs, allowedDrop, fireEvents);
                    m_part.ParentGroup.AddActiveScriptCount(-1);
                }
                else
                {
                    // No scripts to wait for, remove the item right away.
                    _ReplaceItem(m_part, itemID, allowedDrop, fireEvents, replaceArgs);
                }
                return type;
            }
        }
        /// <summary>
        /// Remove an item from this prim's inventory
        /// WARNING: This operation is asynchronous if the item is a script!
        /// </summary>
        /// <param name="itemID"></param>
        /// <returns>Numeric asset type of the item removed.  Returns -1 if the item did not exist
        /// in this prim's inventory.</returns>
        public int RemoveInventoryItem(UUID itemID)
        {
            return ReplaceInventoryItem(itemID, false, false, null);
        }

        // This function initializes or updates the member variable m_inventoryFileName as needed.
        private void UpdateInventoryTempFileName(IXfer xferManager)
        {
            bool needNew = String.IsNullOrEmpty(m_inventoryFileName);   // if first instance

            // If the version has changed, we need a new file name even if we have one
            needNew |= (m_inventoryFileNameSerial < m_inventorySerial); // or new version

            if (needNew)
            {
                // if (!String.IsNullOrEmpty(m_inventoryFileName)) xferManager.RemoveNewFile(m_inventoryFileName); // replace existing with new update
                m_inventoryFileName = "inventory_" + UUID.Random().ToString() + ".tmp";
            }
        }

        /// <summary>
        /// Serialize all the metadata for the items in this prim's inventory ready for sending to the client
        /// </summary>
        /// <param name="xferManager"></param>
        public void RequestInventoryFile(IClientAPI client, IXfer xferManager)
        {
            byte[] fileData = new byte[0];

            if (m_inventorySerial <= 0) // no inventory to send
            {
                client.SendTaskInventory(m_part.UUID, 0, new byte[0]);
                return;   // client notified above
            }

            // Confusingly, the folder item has to be the object id, while the 'parent id' has to be zero.  This matches
            // what appears to happen in the Second Life protocol.  If this isn't the case. then various functionality
            // isn't available (such as drag from prim inventory to agent inventory)
            InventoryStringBuilder invString = new InventoryStringBuilder(m_part.UUID, UUID.Zero);

            lock (m_items)
            {
//                int items = 0;
                foreach (TaskInventoryItem item in m_items.Values)
                {
                    UUID ownerID = item.OwnerID;
                    uint everyoneMask = 0;
                    uint baseMask = item.BasePermissions;
                    uint ownerMask = item.CurrentPermissions;
                    uint groupMask = item.GroupPermissions;
                    string itemID;
                    string desc;
                    
                    // only the owner of the item can see the UUIDs of Contents and possibly private data
                    if (ownerID == client.AgentId)
                    {
                        itemID = item.AssetID.ToString();
                        desc = item.Description;
//                        m_log.DebugFormat("[ASSETS]: RequestInventoryFile returning item #{0} itemID {1} asset {2}", ++items, item.ItemID, item.AssetID);
                    }
                    else
                    {
                        itemID = UUID.Zero.ToString();
                        desc = "(not owner)";
                    }

                    invString.AddSectionEnd();

                    invString.AddItemStart();
                    invString.AddNameValueLine("item_id", item.ItemID.ToString());
                    invString.AddNameValueLine("parent_id", m_part.UUID.ToString());

                    invString.AddPermissionsStart();

                    invString.AddNameValueLine("base_mask", Utils.UIntToHexString(baseMask));
                    invString.AddNameValueLine("owner_mask", Utils.UIntToHexString(ownerMask));
                    invString.AddNameValueLine("group_mask", Utils.UIntToHexString(groupMask));
                    invString.AddNameValueLine("everyone_mask", Utils.UIntToHexString(everyoneMask));
                    invString.AddNameValueLine("next_owner_mask", Utils.UIntToHexString(item.NextPermissions));

                    invString.AddNameValueLine("creator_id", item.CreatorID.ToString());
                    invString.AddNameValueLine("owner_id", ownerID.ToString());

                    invString.AddNameValueLine("last_owner_id", item.LastOwnerID.ToString());

                    invString.AddNameValueLine("group_id", item.GroupID.ToString());
                    invString.AddSectionEnd();

                    invString.AddNameValueLine("asset_id", itemID);
                    invString.AddNameValueLine("type", TaskInventoryItem.Types[item.Type]);
                    invString.AddNameValueLine("inv_type", TaskInventoryItem.InvTypes[item.InvType]);
                    invString.AddNameValueLine("flags", Utils.UIntToHexString(item.Flags));

                    invString.AddSaleStart();
                    invString.AddNameValueLine("sale_type", "not");
                    invString.AddNameValueLine("sale_price", "0");
                    invString.AddSectionEnd();

                    invString.AddNameValueLine("name", item.Name + "|");
                    invString.AddNameValueLine("desc", desc + "|");

                    invString.AddNameValueLine("creation_date", item.CreationDate.ToString());
                    invString.AddSectionEnd();
                }
            }
            invString.AddEnd();

            fileData = Utils.StringToBytes(invString.BuildString.ToString());
            if (fileData.Length > 2)
            {
                // There is inventory to send. Allocate temp file.
                UpdateInventoryTempFileName(xferManager);
                // Add the data to the NewFiles list and await the viewer request for it.
                xferManager.AddNewFile(m_inventoryFileName, fileData);
                // Notify the viewer of the available file download.
                client.SendTaskInventory(m_part.UUID, (short)m_inventorySerial, Utils.StringToBytes(m_inventoryFileName));
            }
        }

        public bool InventoryNeedsBackup()
        {
            return HasInventoryChanged;
        }

        /// <summary>
        /// Returns the list of deleted item UUIDs for this inventory
        /// </summary>
        /// <returns>The list of deleted items</returns>
        public KeyValuePair<UUID, IEnumerable<UUID>> GetDeletedItemList()
        {
            lock (m_items)
            {
                KeyValuePair<UUID, IEnumerable<UUID>>
                    killList = new KeyValuePair<UUID, IEnumerable<UUID>>(
                        m_part.UUID, new List<UUID>(_removedItems));

                return killList;
            }
        }

        /// <summary>
        /// Clears the list of items that were removed since the last persistence
        /// </summary>
        public void ClearDeletedItemList()
        {
            _removedItems.Clear();
        }

        public KeyValuePair<UUID, IEnumerable<TaskInventoryItem>> CollectInventoryForBackup()
        {
            KeyValuePair<UUID, IEnumerable<TaskInventoryItem>> retItems;

            lock (Items)
            {
                retItems = new KeyValuePair<UUID,IEnumerable<TaskInventoryItem>>(
                    m_part.UUID, new List<TaskInventoryItem>(Items.Values));
            }

            return retItems;
        }

        public class InventoryStringBuilder
        {
            public StringBuilder BuildString = new StringBuilder(1024);

            public InventoryStringBuilder(UUID folderID, UUID parentID)
            {
                BuildString.Append("\tinv_object\t0\n\t{\n");
                AddNameValueLine("obj_id", folderID.ToString());
                AddNameValueLine("parent_id", parentID.ToString());
                AddNameValueLine("type", "category");
                AddNameValueLine("name", "Contents|");
            }

            public void AddEnd()
            {
                BuildString.Append("\n\t}");  // add \n before \t}
            }

            public void AddItemStart()
            {
                BuildString.Append("\tinv_item\t0\n\t{\n");
                AddSectionStart();
            }

            public void AddPermissionsStart()
            {
                BuildString.Append("\tpermissions 0\n");
                AddSectionStart();
            }

            public void AddSaleStart()
            {
                BuildString.Append("\tsale_info\t0\n");
                AddSectionStart();
            }

            protected void AddSectionStart()
            {
                BuildString.Append("\t{\n");
            }

            public void AddSectionEnd()
            {
                BuildString.Append("\t}\n");
            }

            public void AddLine(string addLine)
            {
                BuildString.Append(addLine);
            }

            public void AddNameValueLine(string name, string value)
            {
                BuildString.Append("\t\t");
                BuildString.Append(name);
                BuildString.Append("\t");
                BuildString.Append(value);
                BuildString.Append("\n");
            }

            public void Close()
            {
            }
        }

        private const uint PERM_MCT = (uint)PermissionMask.Copy | (uint)PermissionMask.Modify | (uint)PermissionMask.Transfer;
        public uint MaskEffectivePermissions()
        {
            uint mask = ScenePermBits.BASEMASK;

            lock (m_items)
            {
                foreach (TaskInventoryItem item in m_items.Values)
                {
                    if ((item.CurrentPermissions & (uint)PermissionMask.Copy) == 0)
                        mask &= ~(uint)PermissionMask.Copy;
                    if ((item.CurrentPermissions & (uint)PermissionMask.Transfer) == 0)
                        mask &= ~(uint)PermissionMask.Transfer;
                    if ((item.CurrentPermissions & (uint)PermissionMask.Modify) == 0)
                        mask &= ~(uint)PermissionMask.Modify;
                }
            }
            if ((mask & PERM_MCT) != PERM_MCT)
                mask &= ~(uint)PermissionMask.Export;
            return mask;
        }
        public uint MaskEffectiveNextPermissions()
        {
            uint mask = ScenePermBits.BASEMASK;

            lock (m_items)
            {
                foreach (TaskInventoryItem item in m_items.Values)
                {
                    if ((item.CurrentPermissions & item.NextPermissions & (uint)PermissionMask.Copy) == 0)
                        mask &= ~(uint)PermissionMask.Copy;
                    if ((item.CurrentPermissions & item.NextPermissions & (uint)PermissionMask.Transfer) == 0)
                        mask &= ~(uint)PermissionMask.Transfer;
                    if ((item.CurrentPermissions & item.NextPermissions & (uint)PermissionMask.Modify) == 0)
                        mask &= ~(uint)PermissionMask.Modify;
                }
            }
            if ((mask & PERM_MCT) != PERM_MCT)
                mask &= ~(uint)PermissionMask.Export;
            return mask;
        }

        public void ApplyNextOwnerPermissions()
        {
            lock (m_items)
            {
                foreach (TaskInventoryItem item in m_items.Values)
                {
                    item.CurrentPermissions &= (item.NextPermissions | ScenePermBits.SLAM);
                    item.BasePermissions &= item.NextPermissions;
                    item.EveryonePermissions &= item.NextPermissions;
                    item.PermsGranter = UUID.Zero;
                    item.PermsMask = 0;
                }
            }

            m_part.TriggerScriptChangedEvent(Changed.OWNER);
        }

        // called on rezzing for late last-minute fixes
        // returns true if the owner changed
        public bool Rationalize(UUID itemOwner)
        {
            bool ownerChanged = false;

            lock (m_items)
            {
                foreach (TaskInventoryItem item in m_items.Values)
                {
                    if ((item.OwnerID != itemOwner) || ((item.PermsMask != 0) && (item.PermsGranter != itemOwner)))
                    {
                        item.PermsGranter = UUID.Zero;
                        item.PermsMask = 0;
                        ownerChanged = true;
                    }
                }
            }
            return ownerChanged;
        }

        public void ApplyGodPermissions(uint perms)
        {
            lock (m_items)
            {
                foreach (TaskInventoryItem item in m_items.Values)
                {
                    item.CurrentPermissions = perms;
                    item.BasePermissions = perms;
                }
            }
            m_inventorySerial++;
            HasInventoryChanged = true;
        }

        public bool ContainsScripts()
        {
            lock (m_items)
            {
                foreach (TaskInventoryItem item in m_items.Values)
                {
                    if (item.InvType == (int)InventoryType.LSL)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public List<UUID> GetInventoryList()
        {
            List<UUID> ret = new List<UUID>();

            lock (m_items)
            {
                foreach (TaskInventoryItem item in m_items.Values)
                    ret.Add(item.ItemID);
            }

            return ret;
        }
        
        public Dictionary<UUID, string> GetScriptStates(StopScriptReason stopScriptReason)
        {
            Dictionary<UUID, string> ret = new Dictionary<UUID, string>(); 
            
            // Make sure we have a scene before we make this call.  GetScriptStates gets called
            // from ToXml in SOG which is static and therefore a scene may not exist.
            if (m_part.ParentGroup.Scene == null)
                return ret;

            IScriptModule[] engines = m_part.ParentGroup.Scene.RequestModuleInterfaces<IScriptModule>();
            if (engines.Length == 0)
                return ret;

            IScriptModule engine = engines[0];
            if (engine == null)   // happens under Phlox if disabled
                return ret;

            List<TaskInventoryItem> items;
            lock (m_items)
            {
                //we dont want to hold a lock here, as the script engine will be
                //holding locks and making us wait for the states during these requests
                //so we make a copy here
                items = new List<TaskInventoryItem>(m_items.Values);
            }

            foreach (TaskInventoryItem item in items)
            {
                if (item.InvType == (int)InventoryType.LSL)
                {
                    string n = engine.GetXMLState(item.ItemID, stopScriptReason);
                    if (!String.IsNullOrEmpty(n))
                    {
                        ret[item.ItemID] = n;
                    }
                }
            }
            return ret;
        }

        public Dictionary<UUID, byte[]> GetBinaryScriptStates(StopScriptReason stopScriptReason)
        {
            Dictionary<UUID, byte[]> ret = new Dictionary<UUID, byte[]>();

            // Make sure we have a scene before we make this call.  GetScriptStates gets called
            // from ToXml in SOG which is static and therefore a scene may not exist.
            if (m_part.ParentGroup == null || m_part.ParentGroup.Scene == null)
                return ret;

            IScriptModule[] engines = m_part.ParentGroup.Scene.RequestModuleInterfaces<IScriptModule>();
            if (engines.Length == 0)
                return ret;

            IScriptModule engine = engines[0];
            if (engine == null)     // happens under Phlox if disabled
                return ret;

            List<TaskInventoryItem> items;
            lock (m_items)
            {
                //we dont want to hold a lock here, as the script engine will be
                //holding locks and making us wait for the states during these requests
                //so we make a copy here
                items = new List<TaskInventoryItem>(m_items.Values);
            }

            foreach (TaskInventoryItem item in items)
            {
                if (item.InvType == (int)InventoryType.LSL)
                {
                    byte[] n = engine.GetBinaryStateSnapshot(item.ItemID, stopScriptReason);
                    if (n != null)
                    {
                        ret[item.ItemID] = n;
                    }
                }
            }

            return ret;
        }

        public Tuple<Dictionary<UUID, byte[]>, Dictionary<UUID, byte[]>> GetBinaryScriptStatesAndCompiledScripts(StopScriptReason stopScriptReason)
        {
            // Make sure we have a scene before we make this call.  GetScriptStates gets called
            // from ToXml in SOG which is static and therefore a scene may not exist.
            if (m_part.ParentGroup == null || m_part.ParentGroup.Scene == null)
                return new Tuple<Dictionary<UUID, byte[]>, Dictionary<UUID, byte[]>>(new Dictionary<UUID, byte[]>(), new Dictionary<UUID, byte[]>());

            IScriptModule[] engines = m_part.ParentGroup.Scene.RequestModuleInterfaces<IScriptModule>();
            if (engines.Length == 0)
                return new Tuple<Dictionary<UUID, byte[]>, Dictionary<UUID, byte[]>>(new Dictionary<UUID, byte[]>(), new Dictionary<UUID, byte[]>());

            IScriptModule engine = engines[0];
            if (engine == null)    // happens under Phlox if disabled
                return new Tuple<Dictionary<UUID, byte[]>, Dictionary<UUID, byte[]>>(new Dictionary<UUID, byte[]>(), new Dictionary<UUID, byte[]>());

            List<TaskInventoryItem> items;
            lock (m_items)
            {
                //we dont want to hold a lock here, as the script engine will be
                //holding locks and making us wait for the states during these requests
                //so we make a copy here
                items = new List<TaskInventoryItem>(m_items.Values);
            }

            var states = new Dictionary<UUID, byte[]>();
            List<UUID> scriptAssetIds = new List<UUID>();
            foreach (TaskInventoryItem item in items)
            {
                if (item.InvType == (int)InventoryType.LSL)
                {
                    scriptAssetIds.Add(item.AssetID);
                    byte[] n = engine.GetBinaryStateSnapshot(item.ItemID, stopScriptReason);
                    if (n != null)
                    {
                        states[item.ItemID] = n;
                    }
                }
            }

            Dictionary<UUID, byte[]> byteCode = engine.GetBytecodeForAssets(scriptAssetIds);

            return new Tuple<Dictionary<UUID, byte[]>, Dictionary<UUID, byte[]>>(states, byteCode);
        }

        public IList<TaskInventoryItem> GetScripts()
        {
            List<TaskInventoryItem> items = new List<TaskInventoryItem>();
            lock (m_items)
            {
                foreach (TaskInventoryItem item in m_items.Values)
                {
                    if (item.InvType == (int)InventoryType.LSL)
                    {
                        items.Add(item);
                    }
                }
            }

            return items;
        }

        
    }
}
