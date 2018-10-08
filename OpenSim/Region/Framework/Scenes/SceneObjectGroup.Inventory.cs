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
using System.Reflection;
using OpenMetaverse;
using log4net;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using System.Collections.Generic;
using System.Xml;

namespace OpenSim.Region.Framework.Scenes
{
    public partial class SceneObjectGroup : EntityBase
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Force all task inventories of prims in the scene object to persist
        /// </summary>
        public void ForceInventoryPersistence()
        {
            m_childParts.ForEachPart((SceneObjectPart part) => {
                part.Inventory.ForceInventoryPersistence();
            });
        }

        /// <summary>
        /// Start the scripts contained in all the prims in this group.
        /// </summary>
        public void CreateScriptInstances(int? startParam, ScriptStartFlags startFlags,
                string engine, int stateSource, ICompilationListener listener)
        {
            // Don't start scripts if they're turned off in the region!
            if (!m_scene.RegionInfo.RegionSettings.DisableScripts)
            {
                m_childParts.ForEachPart((SceneObjectPart part) => {
                    part.Inventory.CreateScriptInstances(startParam, startFlags, engine, stateSource, listener);
                });
            }
        }

        /// <summary>
        /// Stop the scripts contained in all the prims in this group
        /// </summary>
        public void RemoveScriptInstances()
        {
            m_childParts.ForEachPart((SceneObjectPart part) => {
                part.Inventory.RemoveScriptInstances();
            });
        }

        /// <summary>
        /// Resets all the scripts and item attributes in all prims in this group (when itemId == UUID.Zero).
        /// If itemId is specified, only that specified script is reset.
        /// </summary>
        public void ResetItems(bool isNewInstance, bool isScriptReset, UUID itemId)
        {
            m_childParts.ForEachPart((SceneObjectPart part) => {
                part.Inventory.ResetItems(isNewInstance, isScriptReset, itemId);
            });
        }

        /// <summary>
        /// Return serialized inventory metadata for the given constituent prim
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="xferManager"></param>
        public void RequestInventoryFile(IClientAPI client, uint localID, IXfer xferManager)
        {
            SceneObjectPart part = GetChildPart(localID);
            if (part == null)
            {
                m_log.ErrorFormat(
                    "[PRIM INVENTORY]: " +
                    "Couldn't find part {0} in object group {1}, {2} to request inventory data",
                    localID, Name, UUID);
                return;
            }

/*
            if (GetPartInventoryFileName(client, localID, xferManager))
            SceneObjectPart part = GetChildPart(localID);
            if (part != null)
            {
                return part.Inventory.AllocateInventoryFile(remoteClient, localID, xferManager);
            }
            else
            {
                m_log.ErrorFormat(
                    "[PRIM INVENTORY]: " +
                    "Couldn't find part {0} in object group {1}, {2} to retreive prim inventory",
                    localID, Name, UUID);
            }
            return false;
*/
            part.Inventory.RequestInventoryFile(client, xferManager);
        }

        /// <summary>
        /// Add an inventory item to a prim in this group.
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="localID"></param>
        /// <param name="item"></param>
        /// <param name="copyItemID">The item UUID that should be used by the new item.</param>
        /// <returns></returns>
        public bool AddInventoryItem(IClientAPI remoteClient, uint localID,
                                     InventoryItemBase item, UUID copyItemID)
        {
            UUID newItemId = (copyItemID != UUID.Zero) ? copyItemID : item.ID;

            SceneObjectPart part = GetChildPart(localID);
            if (part != null)
            {
                TaskInventoryItem taskItem = new TaskInventoryItem();

                taskItem.ItemID = newItemId;
                taskItem.AssetID = item.AssetID;
                taskItem.Name = item.Name;
                taskItem.Description = item.Description;
                taskItem.OwnerID = part.OwnerID; // Transfer ownership
                taskItem.CreatorID = item.CreatorIdAsUuid;
                taskItem.Type = item.AssetType;
                taskItem.InvType = item.InvType;

                if (remoteClient != null &&
                        remoteClient.AgentId != part.OwnerID &&
                        m_scene.Permissions.PropagatePermissions())
                {
                    taskItem.BasePermissions = item.BasePermissions &
                            item.NextPermissions;
                    taskItem.CurrentPermissions = item.CurrentPermissions &
                            item.NextPermissions;
                    taskItem.EveryonePermissions = item.EveryonePermissions &
                            item.NextPermissions;
                    taskItem.GroupPermissions = item.GroupPermissions &
                            item.NextPermissions;
                    taskItem.NextPermissions = item.NextPermissions;
                } else {
                    taskItem.BasePermissions = item.BasePermissions;
                    taskItem.CurrentPermissions = item.CurrentPermissions;
                    taskItem.EveryonePermissions = item.EveryonePermissions;
                    taskItem.GroupPermissions = item.GroupPermissions;
                    taskItem.NextPermissions = item.NextPermissions;
                }

                taskItem.Flags = item.Flags;
                // TODO: These are pending addition of those fields to TaskInventoryItem
//                taskItem.SalePrice = item.SalePrice;
//                taskItem.SaleType = item.SaleType;
                taskItem.CreationDate = (uint)item.CreationDate;

                bool addFromAllowedDrop = false;
                if (remoteClient!=null) 
                {
                    addFromAllowedDrop = remoteClient.AgentId != part.OwnerID;
                }

                part.Inventory.AddInventoryItem(taskItem, addFromAllowedDrop, true);

                return true;
            }
            else
            {
                m_log.ErrorFormat(
                    "[PRIM INVENTORY]: " +
                    "Couldn't find prim local ID {0} in group {1}, {2} to add inventory item ID {3}",
                    localID, Name, UUID, newItemId);
            }

            return false;
        }

        /// <summary>
        /// Returns an existing inventory item.  Returns the original, so any changes will be live.
        /// </summary>
        /// <param name="primID"></param>
        /// <param name="itemID"></param>
        /// <returns>null if the item does not exist</returns>
        public TaskInventoryItem GetInventoryItem(uint primID, UUID itemID)
        {
            SceneObjectPart part = GetChildPart(primID);
            if (part != null)
            {
                return part.Inventory.GetInventoryItem(itemID);
            }
            else
            {
                m_log.ErrorFormat(
                    "[PRIM INVENTORY]: " +
                    "Couldn't find prim local ID {0} in prim {1}, {2} to get inventory item ID {3}",
                    primID, part.Name, part.UUID, itemID);
            }

            return null;
        }

        /// <summary>
        /// Update an existing inventory item with a new asset (ID).
        /// </summary>
        /// <param name="ParentPartID">The UUID of the prim containing the task inventory item.</param>
        /// <param name="ItemID">The item to update. An item with the same id must already exist
        /// in the inventory of the prim specified by ParentPartID.</param>
        /// <param name="AssetID">The ID of the new asset to replace in the item above.</param>
        /// <returns>false if the item did not exist or a null ID was passed, true if the update occurred successfully</returns>
        public bool UpdateInventoryItemAsset(UUID ParentPartID, UUID ItemID, UUID AssetID)
        {
            SceneObjectPart part = GetChildPart(ParentPartID);
            if (part != null)
            {
                part.Inventory.UpdateTaskInventoryItemAsset(ParentPartID, ItemID, AssetID);

                return true;
            }
            else
            {
                m_log.ErrorFormat(
                    "[PRIM INVENTORY]: " +
                    "Couldn't find prim ID {0} to update item in {1}, {2}",
                    ParentPartID, this.UUID, ItemID);
            }

            return false;
        }

        /// <summary>
        /// Update an existing inventory item.
        /// </summary>
        /// <param name="item">The updated item.  An item with the same id must already exist
        /// in the inventory of the prim specified by item.ParentPartID.</param>
        /// <returns>false if the item did not exist, true if the update occurred successfully</returns>
        public bool UpdateInventoryItemFromItem(TaskInventoryItem item)
        {
            SceneObjectPart part = GetChildPart(item.ParentPartID);
            if (part != null)
            {
                part.Inventory.UpdateTaskInventoryItemFromItem(item);

                return true;
            }
            else
            {
                m_log.ErrorFormat(
                    "[PRIM INVENTORY]: " +
                    "Couldn't find prim ID {0} to update item {1}, {2}",
                    item.ParentPartID, item.Name, item.ItemID);
            }

            return false;
        }

        public int RemoveInventoryItem(uint localID, UUID itemID)
        {
            SceneObjectPart part = GetChildPart(localID);
            if (part != null)
            {
                int type = part.Inventory.RemoveInventoryItem(itemID);

                return type;
            }

            return -1;
        }

        private const uint PERM_MCT = (uint)PermissionMask.Copy | (uint)PermissionMask.Modify | (uint)PermissionMask.Transfer;
        public uint GetEffectivePermissions(bool includeContents)
        {
            uint perms = (uint)(PermissionMask.All | PermissionMask.Export);
            uint ownerMask = ScenePermBits.BASEMASK;

            m_childParts.ForEachPart((SceneObjectPart part) => {
                ownerMask &= part.OwnerMask;
                if (includeContents)
                    perms &= part.Inventory.MaskEffectivePermissions();
            });

            if ((ownerMask & (uint)PermissionMask.Modify) == 0)
                perms &= ~(uint)PermissionMask.Modify;
            if ((ownerMask & (uint)PermissionMask.Copy) == 0)
                perms &= ~(uint)PermissionMask.Copy;
            if ((ownerMask & (uint)PermissionMask.Transfer) == 0)
                perms &= ~(uint)PermissionMask.Transfer;
            if ((ownerMask & (uint)PermissionMask.Export) == 0)
                perms &= ~(uint)PermissionMask.Export;

            return perms;
        }

        public void ApplyNextOwnerPermissions()
        {
            m_childParts.ForEachPart((SceneObjectPart part) => {
                part.ApplyNextOwnerPermissions();
            });
        }


        public uint GetEffectiveNextPermissions(bool includeContents)
        {
            uint perms = (uint)(PermissionMask.All | PermissionMask.Export);
            uint nextOwnerMask = ScenePermBits.BASEMASK;
            
            m_childParts.ForEachPart((SceneObjectPart part) => {
                nextOwnerMask &= part.NextOwnerMask;
                if (includeContents)
                    perms &= part.Inventory.MaskEffectiveNextPermissions();
            });

            if ((nextOwnerMask & (uint)PermissionMask.Modify) == 0)
                perms &= ~(uint)PermissionMask.Modify;
            if ((nextOwnerMask & (uint)PermissionMask.Copy) == 0)
                perms &= ~(uint)PermissionMask.Copy;
            if ((nextOwnerMask & (uint)PermissionMask.Transfer) == 0)
                perms &= ~(uint)PermissionMask.Transfer;

            if ((perms & PERM_MCT) != PERM_MCT)
                perms &= ~(uint)PermissionMask.Export;

            return perms;
        }

        public string GetStateSnapshot(bool fromCrossing)
        {
            //m_log.Debug(" >>> GetStateSnapshot <<<");

            Dictionary<UUID, string> states = new Dictionary<UUID, string>();
            StopScriptReason stopScriptReason = fromCrossing ? StopScriptReason.Crossing : StopScriptReason.Derez;

            m_childParts.ForEachPart((SceneObjectPart part) => {
                foreach (KeyValuePair<UUID, string> s in part.Inventory.GetScriptStates(stopScriptReason))
                {
                    states[s.Key] = s.Value;
                }
            });

            if (states.Count < 1)
                return String.Empty;

            XmlDocument xmldoc = new XmlDocument();

            XmlNode xmlnode = xmldoc.CreateNode(XmlNodeType.XmlDeclaration,
                    String.Empty, String.Empty);

            xmldoc.AppendChild(xmlnode);
            XmlElement rootElement = xmldoc.CreateElement(String.Empty, "PhloxScriptData", String.Empty);
            
            xmldoc.AppendChild(rootElement);

            XmlElement wrapper = xmldoc.CreateElement(String.Empty, "PhloxSS",
                    String.Empty);
            
            rootElement.AppendChild(wrapper);

            foreach (KeyValuePair<UUID, string> state in states)
            {
                XmlElement stateData = xmldoc.CreateElement(String.Empty, "State", String.Empty);

                XmlAttribute stateID = xmldoc.CreateAttribute(String.Empty, "UUID", String.Empty);
                stateID.Value = state.Key.ToString();
                stateData.Attributes.Append(stateID);

                stateData.InnerText = state.Value;
                wrapper.AppendChild(stateData);
            }

            return xmldoc.InnerXml;
        }

        public void SetState(string objXMLData, UUID RegionID)
        {
            if (String.IsNullOrEmpty(objXMLData))
                return;

            XmlDocument doc = new XmlDocument();
            try
            {
                doc.LoadXml(objXMLData);
            }
            catch (Exception) // (System.Xml.XmlException)
            {
                // We will get here if the XML is invalid or in unit
                // tests. Really should determine which it is and either
                // fail silently or log it
                // Fail silently, for now.
                // TODO: Fix this
                //
                return;
            }

            XmlNodeList rootL = doc.GetElementsByTagName("PhloxScriptData");
            if (rootL.Count == 1)
            {
                XmlNode rootNode = rootL[0];
                if (rootNode != null)
                {
                    XmlNodeList partL = rootNode.ChildNodes;

                    foreach (XmlNode part in partL)
                    {
                        XmlNodeList nodeL = part.ChildNodes;

                        switch (part.Name)
                        {
                            case "PhloxSS":
                                foreach (XmlNode st in nodeL)
                                {
                                    string id = st.Attributes.GetNamedItem("UUID").Value;
                                    UUID uuid = new UUID(id);
                                    if (m_savedScriptState == null)
                                    {
                                        m_savedScriptState = new Dictionary<UUID, string>();
                                    }

                                    m_savedScriptState[uuid] = st.InnerText;
                                }
                                break;
                        }
                    }
                }
            }
        }
    }
}
