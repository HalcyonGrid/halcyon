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
using System.Collections.Generic;
using System.Reflection;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Data.SimpleDB;
using System.Data;

namespace Halcyon.Data.Inventory.MySQL
{
    /// <summary>
    /// A MySQL interface for the inventory server
    /// </summary>
    public class MysqlStorageImpl
    {
        private static readonly ILog m_log
            = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        ConnectionFactory _connFactory;

        private string _connectString;

        public MysqlStorageImpl(string connStr)
        {
            _connectString = connStr;
            _connFactory = new ConnectionFactory("MySQL", _connectString);
        }

        public InventoryFolderBase findUserFolderForType(UUID owner, int typeId)
        {
            InventoryFolderBase rootFolder = getUserRootFolder(owner);
            if (typeId == 8)    // by convention, this means root folder
                return rootFolder;

            string query = "SELECT * FROM inventoryfolders WHERE agentID = ?agentId AND type = ?type and parentFolderId = ?parent;";

            Dictionary<string, object> parms = new Dictionary<string, object>();
            parms.Add("?agentId", owner);
            parms.Add("?type", typeId);
            parms.Add("?parent", rootFolder.ID);

            try
            {
                using (ISimpleDB conn = _connFactory.GetConnection())
                {
                    using (IDataReader reader = conn.QueryAndUseReader(query, parms))
                    {
                        if (reader.Read())
                        {
                            // A null item (because something went wrong) breaks everything in the folder
                            InventoryFolderBase folder = readInventoryFolder(reader);
                            folder.Level = InventoryFolderBase.FolderLevel.TopLevel;
                            return folder;
                        }
                        else
                        {
                            // m_log.WarnFormat("[Inventory]: findUserFolderForType folder for type {0} not found.", typeId);
                            return null;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
                return null;
            }
        }

        /// <summary>
        /// Returns the folder info for the given folder, or null if one could not be found.
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="type"></param>
        /// <returns></returns>
        public InventoryFolderBase findFolder(UUID owner, UUID folderID)
        {
            string query = "SELECT * FROM inventoryfolders WHERE agentID = ?agentId AND folderId = ?folderId;";

            Dictionary<string, object> parms = new Dictionary<string, object>();
            parms.Add("?agentId", owner);
            parms.Add("?folderId", folderID);

            try
            {
                using (ISimpleDB conn = _connFactory.GetConnection())
                {
                    using (IDataReader reader = conn.QueryAndUseReader(query, parms))
                    {
                        if (reader.Read())
                        {
                            // A null item (because something went wrong) breaks everything in the folder
                            return readInventoryFolder(reader);
                        }
                        else
                        {
                            m_log.WarnFormat("[Inventory]: findUserTopLevelFolderFor: No top-level folders.");
                            return null;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
                return null;
            }
        }

        /// <summary>
        /// Works its way up the parentage and returns the most top-level folder for the given folder, or null if one could not be found.
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="type"></param>
        /// <returns></returns>
        public InventoryFolderBase findUserTopLevelFolderFor(UUID owner, UUID folderID)
        {
            InventoryFolderBase rootFolder = getUserRootFolder(owner);
            if (folderID == rootFolder.ID)
                return rootFolder;

            InventoryFolderBase folder = findFolder(owner, folderID);
            while (folder != null) {
                InventoryFolderBase parentFolder = findFolder(owner, folder.ParentID);
                if (parentFolder == null) return null;

                if (parentFolder.ID == rootFolder.ID)
                {
                    folder.Level = InventoryFolderBase.FolderLevel.TopLevel;
                    return folder;
                }
                // walk up the parentage chain
                folder = findFolder(owner, folder.ParentID);
            }
            return null;    // top-level folder not found
        }

        /// <summary>
        /// Returns a list of items in the given folders
        /// </summary>
        /// <param name="folders"></param>
        /// <returns></returns>
        public List<InventoryItemBase> getItemsInFolders(IEnumerable<InventoryFolderBase> folders)
        {
            string inList = String.Empty;
            foreach (InventoryFolderBase folder in folders)
            {
                if (!String.IsNullOrEmpty(inList)) inList += ",";
                inList += "'" + folder.ID.ToString() + "'";
            }
            if (String.IsNullOrEmpty(inList)) return new List<InventoryItemBase>();

            string query = "SELECT * FROM inventoryitems WHERE parentFolderID IN (" + inList + ");";
            try
            {
                using (ISimpleDB conn = _connFactory.GetConnection())
                {
                    using (IDataReader reader = conn.QueryAndUseReader(query))
                    {
                        List<InventoryItemBase> items = new List<InventoryItemBase>();
                        while (reader.Read())
                        {
                            // A null item (because something went wrong) breaks everything in the folder
                            InventoryItemBase item = readInventoryItem(reader);
                            if (item != null)
                            {
                                items.Add(item);
                            }
                        }
                        return items;
                    }
                }
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
                return new List<InventoryItemBase>();
            }
        }

        /// <summary>
        /// Returns a list of items in a specified folder
        /// </summary>
        /// <param name="folderID">The folder to search</param>
        /// <returns>A list containing inventory items</returns>
        public List<InventoryItemBase> getInventoryInFolder(UUID folderID)
        {
            try
            {
                using (ISimpleDB conn = _connFactory.GetConnection())
                {

                    string query = "SELECT * FROM inventoryitems WHERE parentFolderID = ?uuid";
                    Dictionary<string, object> parms = new Dictionary<string, object>();
                    parms.Add("?uuid", folderID.ToString());

                    List<InventoryItemBase> items = new List<InventoryItemBase>();
                    using (IDataReader reader = conn.QueryAndUseReader(query, parms))
                    {
                        while (reader.Read())
                        {
                            // A null item (because something went wrong) breaks everything in the folder
                            InventoryItemBase item = readInventoryItem(reader);
                            if (item != null)
                            {
                                items.Add(item);
                            }
                        }
                    }
                    return items;
                }
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
                return null;
            }
        }

        /// <summary>
        /// Returns a list of the root folders within a users inventory (folders that only have the root as their parent)
        /// </summary>
        /// <param name="user">The user whos inventory is to be searched</param>
        /// <returns>A list of folder objects</returns>
        public List<InventoryFolderBase> getUserRootFolders(UUID user, UUID root)
        {
            try
            {
                using (ISimpleDB conn = _connFactory.GetConnection())
                {

                    string query = "SELECT * FROM inventoryfolders WHERE parentFolderID = ?root AND agentID = ?uuid";
                    Dictionary<string, object> parms = new Dictionary<string, object>();
                    parms.Add("?uuid", user.ToString());
                    parms.Add("?root", root.ToString());

                    using (IDataReader reader = conn.QueryAndUseReader(query, parms))
                    {
                        List<InventoryFolderBase> items = new List<InventoryFolderBase>();
                        while (reader.Read())
                            items.Add(readInventoryFolder(reader));

                        return items;
                    }
                }
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
                return null;
            }
        }


        /// <summary>
        /// see <see cref="InventoryItemBase.getUserRootFolder"/>
        /// </summary>
        /// <param name="user">The user UUID</param>
        /// <returns></returns>
        public InventoryFolderBase getUserRootFolder(UUID user)
        {
            try
            {
                using (ISimpleDB conn = _connFactory.GetConnection())
                {

                    string query = "SELECT * FROM inventoryfolders WHERE parentFolderID = ?zero AND agentID = ?uuid";
                    Dictionary<string, object> parms = new Dictionary<string, object>();
                    parms.Add("?uuid", user.ToString());
                    parms.Add("?zero", UUID.Zero.ToString());

                    using (IDataReader reader = conn.QueryAndUseReader(query, parms))
                    {
                        List<InventoryFolderBase> items = new List<InventoryFolderBase>();
                        while (reader.Read())
                            items.Add(readInventoryFolder(reader));

                        InventoryFolderBase rootFolder = null;

                        // There should only ever be one root folder for a user.  However, if there's more
                        // than one we'll simply use the first one rather than failing.  It would be even
                        // nicer to print some message to this effect, but this feels like it's too low a
                        // to put such a message out, and it's too minor right now to spare the time to
                        // suitably refactor.
                        if (items.Count > 0)
                        {
                            rootFolder = items[0];
                        }
                        rootFolder.Level = InventoryFolderBase.FolderLevel.Root;
                        return rootFolder;
                    }
                }
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
                return null;
            }
        }

        /// <summary>
        /// Return a list of folders in a users inventory contained within the specified folder.
        /// This method is only used in tests - in normal operation the user always have one,
        /// and only one, root folder.
        /// </summary>
        /// <param name="parentID">The folder to search</param>
        /// <returns>A list of inventory folders</returns>
        public List<InventoryFolderBase> getInventoryFolders(UUID parentID)
        {
            try
            {
                using (ISimpleDB conn = _connFactory.GetConnection())
                {
                    string query = "SELECT * FROM inventoryfolders WHERE parentFolderID = ?uuid";
                    Dictionary<string, object> parms = new Dictionary<string, object>();
                    parms.Add("?uuid", parentID.ToString());

                    using (IDataReader reader = conn.QueryAndUseReader(query, parms))
                    {
                        List<InventoryFolderBase> items = new List<InventoryFolderBase>();
                        while (reader.Read())
                        {
                            InventoryFolderBase folder = readInventoryFolder(reader);
                            items.Add(folder);
                        }
                        return items;
                    }
                }
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
                return null;
            }
        }

        /// <summary>
        /// Reads a one item from an SQL result
        /// </summary>
        /// <param name="reader">The SQL Result</param>
        /// <returns>the item read</returns>
        private static InventoryItemBase readInventoryItem(IDataReader reader)
        {
            try
            {
                InventoryItemBase item = new InventoryItemBase();

                // TODO: this is to handle a case where NULLs creep in there, which we are not sure is indemic to the system, or legacy.  It would be nice to live fix these.
                if (reader["creatorID"] == null)
                {
                    item.CreatorId = UUID.Zero.ToString();
                }
                else
                {
                    item.CreatorId = (string)reader["creatorID"];
                }

                // Be a bit safer in parsing these because the
                // database doesn't enforce them to be not null, and
                // the inventory still works if these are weird in the
                // db
                UUID Owner = UUID.Zero;
                UUID GroupID = UUID.Zero;
                UUID.TryParse(Convert.ToString(reader["avatarID"]), out Owner);
                UUID.TryParse(Convert.ToString(reader["groupID"]), out GroupID);
                item.Owner = Owner;
                item.GroupID = GroupID;

                // Rest of the parsing.  If these UUID's fail, we're dead anyway                
                item.ID = new UUID(Convert.ToString(reader["inventoryID"]));
                item.AssetID = new UUID(Convert.ToString(reader["assetID"]));
                item.AssetType = (int)reader["assetType"];
                item.Folder = new UUID(Convert.ToString(reader["parentFolderID"]));
                item.Name = (string)reader["inventoryName"];
                item.Description = (string)reader["inventoryDescription"];
                item.NextPermissions = (uint)reader["inventoryNextPermissions"];
                item.CurrentPermissions = (uint)reader["inventoryCurrentPermissions"];
                item.InvType = (int)reader["invType"];
                item.BasePermissions = (uint)reader["inventoryBasePermissions"];
                item.EveryOnePermissions = (uint)reader["inventoryEveryOnePermissions"];
                item.GroupPermissions = (uint)reader["inventoryGroupPermissions"];
                item.SalePrice = (int)reader["salePrice"];
                item.SaleType = Convert.ToByte(reader["saleType"]);
                item.CreationDate = (int)reader["creationDate"];
                item.GroupOwned = Convert.ToBoolean(reader["groupOwned"]);
                item.Flags = (uint)reader["flags"];

                return item;
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
            }

            return null;
        }

        /// <summary>
        /// Returns a specified inventory item
        /// </summary>
        /// <param name="item">The item to return</param>
        /// <returns>An inventory item</returns>
        public InventoryItemBase getInventoryItem(UUID itemID)
        {
            try
            {
                using (ISimpleDB conn = _connFactory.GetConnection())
                {

                    string query = "SELECT * FROM inventoryitems WHERE inventoryID = ?uuid";
                    Dictionary<string, object> parms = new Dictionary<string, object>();
                    parms.Add("?uuid", itemID.ToString());

                    using (IDataReader reader = conn.QueryAndUseReader(query, parms))
                    {
                        InventoryItemBase item = null;
                        if (reader.Read())
                            item = readInventoryItem(reader);

                        return item;
                    }
                }
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
            }

            return null;
        }

        /// <summary>
        /// Reads a list of inventory folders returned by a query.
        /// </summary>
        /// <param name="reader">A MySQL Data Reader</param>
        /// <returns>A List containing inventory folders</returns>
        protected static InventoryFolderBase readInventoryFolder(IDataReader reader)
        {
            try
            {
                InventoryFolderBase folder = new InventoryFolderBase();
                folder.Owner = new UUID(Convert.ToString(reader["agentID"]));
                folder.ParentID = new UUID(Convert.ToString(reader["parentFolderID"]));
                folder.ID = new UUID(Convert.ToString(reader["folderID"]));
                folder.Name = (string)reader["folderName"];
                folder.Type = (short)reader["type"];
                folder.Version = (ushort)((int)reader["version"]);
                return folder;
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
            }
            return null;
        }

        /// <summary>
        /// Returns a specified inventory folder
        /// </summary>
        /// <param name="folder">The folder to return</param>
        /// <returns>A folder class</returns>
        public InventoryFolderBase getInventoryFolder(UUID folderID)
        {
            try
            {
                using (ISimpleDB conn = _connFactory.GetConnection())
                {

                    string query = "SELECT * FROM inventoryfolders WHERE folderID = ?uuid";
                    Dictionary<string, object> parms = new Dictionary<string, object>();
                    parms.Add("?uuid", folderID.ToString());

                    using (IDataReader reader = conn.QueryAndUseReader(query, parms))
                    {
                        if (reader.Read())
                        {
                            InventoryFolderBase folder = readInventoryFolder(reader);

                            return folder;
                        }
                        else
                        {
                            return null;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
                return null;
            }
        }

        /// <summary>
        /// Adds a specified item to the database
        /// </summary>
        /// <param name="item">The inventory item</param>
        public void addInventoryItem(InventoryItemBase item)
        {
            string query =
                "REPLACE INTO inventoryitems (inventoryID, assetID, assetType, parentFolderID, avatarID, inventoryName"
                    + ", inventoryDescription, inventoryNextPermissions, inventoryCurrentPermissions, invType"
                    + ", creatorID, inventoryBasePermissions, inventoryEveryOnePermissions, inventoryGroupPermissions, salePrice, saleType"
                    + ", creationDate, groupID, groupOwned, flags) VALUES ";
            query +=
                "(?inventoryID, ?assetID, ?assetType, ?parentFolderID, ?avatarID, ?inventoryName, ?inventoryDescription"
                    + ", ?inventoryNextPermissions, ?inventoryCurrentPermissions, ?invType, ?creatorID"
                    + ", ?inventoryBasePermissions, ?inventoryEveryOnePermissions, ?inventoryGroupPermissions, ?salePrice, ?saleType, ?creationDate"
                    + ", ?groupID, ?groupOwned, ?flags)";

            try
            {
                using (ISimpleDB conn = _connFactory.GetConnection())
                {
                    Dictionary<string, object> parms = new Dictionary<string, object>();
                    parms.Add("?inventoryID", item.ID.ToString());
                    parms.Add("?assetID", item.AssetID.ToString());
                    parms.Add("?assetType", item.AssetType.ToString());
                    parms.Add("?parentFolderID", item.Folder.ToString());
                    parms.Add("?avatarID", item.Owner.ToString());
                    parms.Add("?inventoryName", item.Name);
                    parms.Add("?inventoryDescription", item.Description);
                    parms.Add("?inventoryNextPermissions", item.NextPermissions.ToString());
                    parms.Add("?inventoryCurrentPermissions", item.CurrentPermissions.ToString());
                    parms.Add("?invType", item.InvType);
                    parms.Add("?creatorID", item.CreatorId);
                    parms.Add("?inventoryBasePermissions", item.BasePermissions);
                    parms.Add("?inventoryEveryOnePermissions", item.EveryOnePermissions);
                    parms.Add("?inventoryGroupPermissions", item.GroupPermissions);
                    parms.Add("?salePrice", item.SalePrice);
                    parms.Add("?saleType", item.SaleType);
                    parms.Add("?creationDate", item.CreationDate);
                    parms.Add("?groupID", item.GroupID);
                    parms.Add("?groupOwned", item.GroupOwned);
                    parms.Add("?flags", item.Flags);

                    conn.QueryNoResults(query, parms);

                    // Also increment the parent version number if not null.
                    this.IncrementSpecifiedFolderVersion(conn, item.Folder);
                }
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
            }
        }

        /// <summary>
        /// Updates the specified inventory item
        /// </summary>
        /// <param name="item">Inventory item to update</param>
        public void updateInventoryItem(InventoryItemBase item)
        {

            //addInventoryItem(item);

            /* 12/9/2009 - Ele's Edit - Rather than simply adding a whole new item, which seems kind of pointless to me, 
             let's actually try UPDATING the item as it should be. This is not fully functioning yet from the updating of items
             * within Scene.Inventory.cs MoveInventoryItem yet. Not sure the effect it will have on the rest of the updates either, as they
             * originally pointed back to addInventoryItem above.
            */

            string query =
                "UPDATE inventoryitems SET assetID=?assetID, assetType=?assetType, parentFolderID=?parentFolderID, "
                 + "avatarID=?avatarID, inventoryName=?inventoryName, inventoryDescription=?inventoryDescription, inventoryNextPermissions=?inventoryNextPermissions, "
                + "inventoryCurrentPermissions=?inventoryCurrentPermissions, invType=?invType, creatorID=?creatorID, inventoryBasePermissions=?inventoryBasePermissions, "
                + "inventoryEveryOnePermissions=?inventoryEveryOnePermissions, inventoryGroupPermissions=?inventoryGroupPermissions, salePrice=?salePrice, "
                + "saleType=?saleType, creationDate=?creationDate, groupID=?groupID, groupOwned=?groupOwned, flags=?flags "
                + "WHERE inventoryID=?inventoryID";

            try
            {
                using (ISimpleDB conn = _connFactory.GetConnection())
                {
                    Dictionary<string, object> parms = new Dictionary<string, object>();
                    parms.Add("?inventoryID", item.ID.ToString());
                    parms.Add("?assetID", item.AssetID.ToString());
                    parms.Add("?assetType", item.AssetType.ToString());
                    parms.Add("?parentFolderID", item.Folder.ToString());
                    parms.Add("?avatarID", item.Owner.ToString());
                    parms.Add("?inventoryName", item.Name);
                    parms.Add("?inventoryDescription", item.Description);
                    parms.Add("?inventoryNextPermissions", item.NextPermissions.ToString());
                    parms.Add("?inventoryCurrentPermissions", item.CurrentPermissions.ToString());
                    parms.Add("?invType", item.InvType);
                    parms.Add("?creatorID", item.CreatorId);
                    parms.Add("?inventoryBasePermissions", item.BasePermissions);
                    parms.Add("?inventoryEveryOnePermissions", item.EveryOnePermissions);
                    parms.Add("?inventoryGroupPermissions", item.GroupPermissions);
                    parms.Add("?salePrice", item.SalePrice);
                    parms.Add("?saleType", item.SaleType);
                    parms.Add("?creationDate", item.CreationDate);
                    parms.Add("?groupID", item.GroupID);
                    parms.Add("?groupOwned", item.GroupOwned);
                    parms.Add("?flags", item.Flags);

                    conn.QueryNoResults(query, parms);

                    // Also increment the parent version number if not null.
                    this.IncrementSpecifiedFolderVersion(conn, item.Folder);
                }
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
            }
        }

        /// <summary>
        /// Detele the specified inventory item
        /// </summary>
        /// <param name="item">The inventory item UUID to delete</param>
        public void deleteInventoryItem(InventoryItemBase item)
        {
            try
            {
                using (ISimpleDB conn = _connFactory.GetConnection())
                {
                    string query = "DELETE FROM inventoryitems WHERE inventoryID=?uuid";
                    Dictionary<string, object> parms = new Dictionary<string, object>();
                    parms.Add("?uuid", item.ID.ToString());

                    conn.QueryNoResults(query, parms);

                    // Also increment the parent version number if not null.
                    this.IncrementSpecifiedFolderVersion(conn, item.Folder);
                }
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
            }
        }

        public InventoryItemBase queryInventoryItem(UUID itemID)
        {
            return getInventoryItem(itemID);
        }

        public InventoryFolderBase queryInventoryFolder(UUID folderID)
        {
            return getInventoryFolder(folderID);
        }

        /// <summary>
        /// Creates a new inventory folder
        /// </summary>
        /// <param name="folder">Folder to create</param>
        public void addInventoryFolder(InventoryFolderBase folder)
        {
            if (folder.ID == UUID.Zero)
            {
                m_log.Error("[Inventory]: Not storing zero UUID folder for " + folder.Owner.ToString());
                return;
            }

            string query =
                "REPLACE INTO inventoryfolders (folderID, agentID, parentFolderID, folderName, type, version) VALUES ";
            query += "(?folderID, ?agentID, ?parentFolderID, ?folderName, ?type, ?version)";

            try
            {
                using (ISimpleDB conn = _connFactory.GetConnection())
                {
                    Dictionary<string, object> parms = new Dictionary<string, object>();
                    parms.Add("?folderID", folder.ID.ToString());
                    parms.Add("?agentID", folder.Owner.ToString());
                    parms.Add("?parentFolderID", folder.ParentID.ToString());
                    parms.Add("?folderName", folder.Name);
                    parms.Add("?type", (short)folder.Type);
                    parms.Add("?version", folder.Version);

                    conn.QueryNoResults(query, parms);

                    // Also increment the parent version number if not null.
                    this.IncrementSpecifiedFolderVersion(conn, folder.ParentID);
                }
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
            }
        }

        /// <summary>
        /// Increments the version of the passed folder, making sure the folder isn't Zero. Must be called from within a using{} block!
        /// </summary>
        /// <param name="conn">Database connection.</param>
        /// <param name="folderId">Folder UUID to increment</param>
        private void IncrementSpecifiedFolderVersion(ISimpleDB conn, UUID folderId)
        {
            if (folderId != UUID.Zero)
            {
                string query = "update inventoryfolders set version=version+1 where folderID = ?folderID";
                Dictionary<string, object> updParms = new Dictionary<string, object>();
                updParms.Add("?folderID", folderId.ToString());

                conn.QueryNoResults(query, updParms);
            }
        }

        /// <summary>
        /// Updates an inventory folder
        /// </summary>
        /// <param name="folder">Folder to update</param>
        public void updateInventoryFolder(InventoryFolderBase folder)
        {
            string query =
                "update inventoryfolders set folderName=?folderName where folderID=?folderID";

            try
            {
                using (ISimpleDB conn = _connFactory.GetConnection())
                {
                    Dictionary<string, object> parms = new Dictionary<string, object>();
                    parms.Add("?folderName", folder.Name);
                    parms.Add("?folderID", folder.ID.ToString());

                    conn.QueryNoResults(query, parms);

                    // Also increment the version number if not null.
                    this.IncrementSpecifiedFolderVersion(conn, folder.ID);
                }
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
            }

        }

        /// <summary>
        /// Move an inventory folder
        /// </summary>
        /// <param name="folder">Folder to move</param>
        /// <remarks>UPDATE inventoryfolders SET parentFolderID=?parentFolderID WHERE folderID=?folderID</remarks>
        public void moveInventoryFolder(InventoryFolderBase folder, UUID parentId)
        {
            string query = "UPDATE inventoryfolders SET parentFolderID=?parentFolderID WHERE folderID=?folderID";

            try
            {
                using (ISimpleDB conn = _connFactory.GetConnection())
                {
                    Dictionary<string, object> parms = new Dictionary<string, object>();
                    parms.Add("?folderID", folder.ID.ToString());
                    parms.Add("?parentFolderID", parentId.ToString());

                    conn.QueryNoResults(query, parms);

                    folder.ParentID = parentId; // Only change if the above succeeded.

                    // Increment both the old and the new parents - checking for null.
                    this.IncrementSpecifiedFolderVersion(conn, parentId);
                    this.IncrementSpecifiedFolderVersion(conn, folder.ParentID);
                }
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
            }

        }

        /// <summary>
        /// Append a list of all the child folders of a parent folder
        /// </summary>
        /// <param name="folders">list where folders will be appended</param>
        /// <param name="parentID">ID of parent</param>
        protected void getInventoryFolders(ref List<InventoryFolderBase> folders, UUID parentID)
        {
            List<InventoryFolderBase> subfolderList = getInventoryFolders(parentID);

            foreach (InventoryFolderBase f in subfolderList)
                folders.Add(f);
        }


        /// <summary>
        /// See IInventoryDataPlugin
        /// </summary>
        /// <param name="parentID"></param>
        /// <returns></returns>
        public List<InventoryFolderBase> getFolderHierarchy(UUID parentID)
        {
            /* Note: There are subtle changes between this implementation of getFolderHierarchy and the previous one
                 * - We will only need to hit the database twice instead of n times.
                 * - We assume the database is well-formed - no stranded/dangling folders, all folders in heirarchy owned
                 *   by the same person, each user only has 1 inventory heirarchy
                 * - The returned list is not ordered, instead of breadth-first ordered
               There are basically 2 usage cases for getFolderHeirarchy:
                 1) Getting the user's entire inventory heirarchy when they log in
                 2) Finding a subfolder heirarchy to delete when emptying the trash.
               This implementation will pull all inventory folders from the database, and then prune away any folder that
               is not part of the requested sub-heirarchy. The theory is that it is cheaper to make 1 request from the
               database than to make n requests. This pays off only if requested heirarchy is large.
               By making this choice, we are making the worst case better at the cost of making the best case worse.
               This way is generally better because we don't have to rebuild the connection/sql query per subfolder,
               even if we end up getting more data from the SQL server than we need.
                 - Francis
             */
            try
            {
                List<InventoryFolderBase> folders = new List<InventoryFolderBase>();
                Dictionary<UUID, List<InventoryFolderBase>> hashtable
                    = new Dictionary<UUID, List<InventoryFolderBase>>(); ;
                List<InventoryFolderBase> parentFolder = new List<InventoryFolderBase>();

                using (ISimpleDB conn = _connFactory.GetConnection())
                {
                    bool buildResultsFromHashTable = false;

                    /* Fetch the parent folder from the database to determine the agent ID, and if
                     * we're querying the root of the inventory folder tree */

                    string query = "SELECT * FROM inventoryfolders WHERE folderID = ?uuid";
                    Dictionary<string, object> parms = new Dictionary<string, object>();
                    parms.Add("?uuid", parentID.ToString());

                    IDataReader reader;
                    using (reader = conn.QueryAndUseReader(query, parms))
                    {
                        while (reader.Read())          // Should be at most 1 result
                            parentFolder.Add(readInventoryFolder(reader));
                    }

                    if (parentFolder.Count >= 1)   // No result means parent folder does not exist
                    {
                        if (parentFolder[0].ParentID == UUID.Zero) // We are querying the root folder
                        {
                            /* Get all of the agent's folders from the database, put them in a list and return it */
                            parms.Clear();
                            query = "SELECT * FROM inventoryfolders WHERE agentID = ?uuid";
                            parms.Add("?uuid", parentFolder[0].Owner.ToString());

                            using (reader = conn.QueryAndUseReader(query, parms))
                            {
                                while (reader.Read())
                                {
                                    InventoryFolderBase curFolder = readInventoryFolder(reader);
                                    if (curFolder.ID != parentID) // Do not need to add the root node of the tree to the list
                                        folders.Add(curFolder);
                                }
                            }
                        } // if we are querying the root folder
                        else // else we are querying a subtree of the inventory folder tree
                        {
                            /* Get all of the agent's folders from the database, put them all in a hash table
                             * indexed by their parent ID */
                            parms.Clear();
                            query = "SELECT * FROM inventoryfolders WHERE agentID = ?uuid";
                            parms.Add("?uuid", parentFolder[0].Owner.ToString());

                            using (reader = conn.QueryAndUseReader(query, parms))
                            {
                                while (reader.Read())
                                {
                                    InventoryFolderBase curFolder = readInventoryFolder(reader);
                                    if (hashtable.ContainsKey(curFolder.ParentID))      // Current folder already has a sibling
                                        hashtable[curFolder.ParentID].Add(curFolder);   // append to sibling list
                                    else // else current folder has no known (yet) siblings
                                    {
                                        List<InventoryFolderBase> siblingList = new List<InventoryFolderBase>();
                                        siblingList.Add(curFolder);
                                        // Current folder has no known (yet) siblings
                                        hashtable.Add(curFolder.ParentID, siblingList);
                                    }
                                } // while more items to read from the database
                            }

                            // Set flag so we know we need to build the results from the hash table after
                            // we unlock the database
                            buildResultsFromHashTable = true;

                        } // else we are querying a subtree of the inventory folder tree
                    } // if folder parentID exists

                    if (buildResultsFromHashTable)
                    {
                        /* We have all of the user's folders stored in a hash table indexed by their parent ID
                         * and we need to return the requested subtree. We will build the requested subtree
                         * by performing a breadth-first-search on the hash table */
                        if (hashtable.ContainsKey(parentID))
                            folders.AddRange(hashtable[parentID]);
                        for (int i = 0; i < folders.Count; i++) // **Note: folders.Count is *not* static
                            if (hashtable.ContainsKey(folders[i].ID))
                                folders.AddRange(hashtable[folders[i].ID]);
                    }
                } // lock (database)
                return folders;
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
                return null;
            }
        }

        /// <summary>
        /// Delete a folder from database. Must be called from within a using{} block for the database connection.
        /// </summary>
        /// Passing in the connection allows for consolidation of the DB connections, important as this method is often called from an inner loop.
        /// <param name="folderID">the folder UUID</param>
        /// <param name="conn">the database connection</param>
        private void deleteOneFolder(ISimpleDB conn, InventoryFolderBase folder)
        {
            string query = "DELETE FROM inventoryfolders WHERE folderID=?uuid";
            Dictionary<string, object> parms = new Dictionary<string, object>();
            parms.Add("?uuid", folder.ID.ToString());

            conn.QueryNoResults(query, parms);

            // As the callers of this function will increment the version, there's no need to do so here.
        }

        /// <summary>
        /// Delete all subfolders and items in a folder. Must be called from within a using{} block for the database connection.
        /// </summary>
        /// Passing in the connection allows for consolidation of the DB connections, important as this method is often called from an inner loop.
        /// <param name="folderID">the folder UUID</param>
        /// <param name="conn">the database connection</param>
        private void deleteFolderContents(ISimpleDB conn, UUID folderID)
        {
            // Get a flattened list of all subfolders.
            List<InventoryFolderBase> subFolders = getFolderHierarchy(folderID);

            // Delete all sub-folders
            foreach (InventoryFolderBase f in subFolders)
            {
                deleteFolderContents(conn, f.ID); // Recurse!
                deleteOneFolder(conn, f);
            }

            // Delete the actual items in this folder.
            string query = "DELETE FROM inventoryitems WHERE parentFolderID=?uuid";
            Dictionary<string, object> parms = new Dictionary<string, object>();
            parms.Add("?uuid", folderID.ToString());

            conn.QueryNoResults(query, parms);

            // As the callers of this function will increment the version, there's no need to do so here where this is most often ceing called from an inner loop!
        }

        /// <summary>
        /// Delete all subfolders and items in a folder.
        /// </summary>
        /// <param name="folderID">the folder UUID</param>
        public void deleteFolderContents(UUID folderID)
        {
            try
            {
                using (ISimpleDB conn = _connFactory.GetConnection())
                {
                    using (ITransaction transaction = conn.BeginTransaction()) // Use a transaction to guarantee that the following it atomic - it'd be bad to have a partial delete of the tree!
                    {
                        deleteFolderContents(conn, folderID);

                        // Increment the version of the purged folder.
                        this.IncrementSpecifiedFolderVersion(conn, folderID);

                        transaction.Commit();
                    }
                }
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
            }
        }

        /// <summary>
        /// Deletes an inventory folder
        /// </summary>
        /// <param name="folderId">Id of folder to delete</param>
        public void deleteInventoryFolder(InventoryFolderBase folder)
        {
            // Get a flattened list of all subfolders.
            List<InventoryFolderBase> subFolders = getFolderHierarchy(folder.ID);

            try
            {
                using (ISimpleDB conn = _connFactory.GetConnection())
                {

                    using (ITransaction transaction = conn.BeginTransaction()) // Use a transaction to guarantee that the following it atomic - it'd be bad to have a partial delete of the tree!
                    {
                        // Since the DB doesn't currently have foreign key constraints the order of delete ops doean't matter, 
                        //  however it's better practice to remove the contents and then remove the folder itself.

                        // Delete all sub-folders
                        foreach (InventoryFolderBase f in subFolders)
                        {
                            deleteFolderContents(conn, f.ID);
                            deleteOneFolder(conn, f);
                        }

                        // Delete the actual row
                        deleteFolderContents(conn, folder.ID);
                        deleteOneFolder(conn, folder);

                        // Increment the version of the parent of the purged folder.
                        this.IncrementSpecifiedFolderVersion(conn, folder.ParentID);

                        transaction.Commit();
                    }
                }
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
            }
        }

        public List<InventoryItemBase> fetchActiveGestures(UUID avatarID)
        {
            try
            {
                using (ISimpleDB conn = _connFactory.GetConnection())
                {
                    string query = "SELECT * FROM inventoryitems WHERE avatarId = ?uuid AND assetType = ?type and flags & 1";
                    Dictionary<string, object> parms = new Dictionary<string, object>();
                    parms.Add("?uuid", avatarID.ToString());
                    parms.Add("?type", (int)AssetType.Gesture);

                    using (IDataReader result = conn.QueryAndUseReader(query, parms))
                    {
                        List<InventoryItemBase> list = new List<InventoryItemBase>();
                        while (result.Read())
                        {
                            InventoryItemBase item = readInventoryItem(result);
                            if (item != null)
                                list.Add(item);
                        }

                        return list;
                    }
                }
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
                return new List<InventoryItemBase>();
            }
        }

        public List<InventoryItemBase> getAllItems(UUID avatarID)
        {
            try
            {
                using (ISimpleDB conn = _connFactory.GetConnection())
                {
                    string query = "SELECT * FROM inventoryitems WHERE avatarId = ?uuid";
                    Dictionary<string, object> parms = new Dictionary<string, object>();
                    parms.Add("?uuid", avatarID.ToString());

                    using (IDataReader result = conn.QueryAndUseReader(query, parms))
                    {
                        List<InventoryItemBase> list = new List<InventoryItemBase>();
                        while (result.Read())
                        {
                            InventoryItemBase item = readInventoryItem(result);
                            if (item != null)
                                list.Add(item);
                        }

                        return list;
                    }
                }
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
                return null;
            }
        }
    }
}
