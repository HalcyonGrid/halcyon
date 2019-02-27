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
using System.IO;
using System.Reflection;
using System.Xml;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using System.Collections.Generic;

namespace OpenSim.Region.Framework.Scenes.Serialization
{
    public class CoalescedSceneObjectSerializer
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Deserialize a scene object from the original xml format
        /// </summary>
        /// <param name="serialization"></param>
        /// <returns></returns>
        public static CoalescedObject FromXmlFormat(string serialization)
        {
            return FromXmlFormat(UUID.Zero, serialization);
        }

        /// <summary>
        /// Deserialize a scene object from the original xml format
        /// </summary>
        /// <param name="serialization"></param>
        /// <returns></returns>
        public static CoalescedObject FromXmlFormat(UUID fromUserInventoryItemID, string xmlData)
        {
            List<SceneObjectGroup> sceneObjects = new List<SceneObjectGroup>();
            Dictionary<UUID, ItemPermissionBlock> permBlock = new Dictionary<UUID, ItemPermissionBlock>();

            try
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xmlData);
                XmlNodeList parts = doc.GetElementsByTagName("ColObjectMember");

                for (int i = 0; i < parts.Count; i++)
                {
                    SceneObjectGroup group = SceneObjectSerializer.FromOriginalXmlFormat(fromUserInventoryItemID, parts[i].InnerXml);
                    sceneObjects.Add(group);
                }

                XmlNodeList permissions = doc.GetElementsByTagName("CSOPermission");
                for (int i = 0; i < permissions.Count; i++)
                {
                    StringReader sr = new StringReader(permissions[i].InnerXml);
                    XmlTextReader reader = new XmlTextReader(sr);
                    ItemPermissionBlock perm = ItemPermissionBlock.FromXml(reader);
                    permBlock.Add(perm.ItemId, perm);
                }

                return new CoalescedObject(sceneObjects, permBlock);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[SERIALIZER]: Deserialization of xml failed with {0}.  xml was {1}", e, xmlData);
            }

            //m_log.DebugFormat("[SERIALIZER]: Finished deserialization of SOG {0}, {1}ms", Name, System.Environment.TickCount - time);

            return null;
        }

        /// <summary>
        /// Deserialize the first prim in a possibly coalesced scene object from the original xml format
        /// </summary>
        /// <param name="fromUserInventoryItemID"></param>
        /// <param name="xmlData"></param>
        /// <param name="index">Which SOG to return in a coalesced item (typically index=0)</param>
        /// <returns></returns>
        public static SceneObjectPart RootPartXmlObject(UUID fromUserInventoryItemID, string xmlData, int index)
        {
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xmlData);
                XmlNodeList parts = doc.GetElementsByTagName("ColObjectMember");

                if (parts.Count < 1)
                    return null;
                return SceneObjectSerializer.RootPartInOriginalXmlFormat(fromUserInventoryItemID, parts[index].InnerXml);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[SERIALIZER]: Deserialization of root prim xml failed with {0}.  xml was {1}", e, xmlData);
            }

            //m_log.DebugFormat("[SERIALIZER]: Finished deserialization of root prim {0}, {1}ms", Name, System.Environment.TickCount - time);
            return null;
        }

        /// <summary>
        /// Serialize a scene object to the original xml format
        /// </summary>
        /// <param name="sceneObject"></param>
        /// <returns></returns>        
        public static string ToXmlFormat(IEnumerable<SceneObjectGroup> sceneObject, IEnumerable<ItemPermissionBlock> permissions, StopScriptReason stopScriptReason)
        {
            using (StringWriter sw = new StringWriter())
            {
                using (XmlTextWriter writer = new XmlTextWriter(sw))
                {
                    ToXmlFormat(sceneObject, permissions, writer, stopScriptReason);
                }

                return sw.ToString();
            }
        }

        /// <summary>
        /// Serialize a scene object to the original xml format
        /// </summary>
        /// <param name="sceneObject"></param>
        /// <returns></returns>            
        public static void ToXmlFormat(IEnumerable<SceneObjectGroup> sceneObjects, IEnumerable<ItemPermissionBlock> permissions,
            XmlTextWriter writer, StopScriptReason stopScriptReason)
        {
            writer.WriteStartElement(String.Empty, "CoalescedSceneObject", String.Empty);

            //permissions
            writer.WriteStartElement(String.Empty, "CSOPermissions", String.Empty);
            foreach (ItemPermissionBlock perm in permissions)
            {
                writer.WriteStartElement(String.Empty, "CSOPermission", String.Empty);
                perm.ToXml(writer);
                writer.WriteEndElement(); // ColObjectMember
            }
            writer.WriteEndElement();

            //scene objects
            writer.WriteStartElement(String.Empty, "ColObjects", String.Empty);
            foreach (SceneObjectGroup group in sceneObjects)
            {
                writer.WriteStartElement(String.Empty, "ColObjectMember", String.Empty);
                SceneObjectSerializer.ToOriginalXmlFormat(group, writer, stopScriptReason);
                writer.WriteEndElement(); // ColObjectMember
            }
            writer.WriteEndElement(); //ColObjects


            writer.WriteEndElement(); // CoalescedSceneObject
        }
    }
}
