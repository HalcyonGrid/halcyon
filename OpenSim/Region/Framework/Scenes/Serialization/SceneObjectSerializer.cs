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
using System.IO;
using System.Reflection;
using System.Xml;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.Framework.Scenes.Serialization
{
    /// <summary>
    /// Serialize and deserialize scene objects.
    /// </summary>
    /// This should really be in OpenSim.Framework.Serialization but this would mean circular dependency problems
    /// right now - hopefully this isn't forever.
    public class SceneObjectSerializer
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        
        /// <summary>
        /// Deserialize a scene object from the original xml format
        /// </summary>
        /// <param name="serialization"></param>
        /// <returns></returns>
        public static SceneObjectGroup FromOriginalXmlFormat(string serialization)
        {
            return FromOriginalXmlFormat(UUID.Zero, serialization);
        }

        /// <summary>
        /// Deserialize a scene object from the original xml format
        /// </summary>
        /// <param name="serialization"></param>
        /// <returns></returns>
        public static SceneObjectPart RootPartInOriginalXmlFormat(UUID fromUserInventoryItemID, string xmlData)
        {
            //m_log.DebugFormat("[SOG]: Starting deserialization of SOG");
            //int time = System.Environment.TickCount;

            SceneObjectPart part = new SceneObjectPart();

            // libomv.types changes UUID to Guid
            xmlData = xmlData.Replace("<UUID>", "<Guid>");
            xmlData = xmlData.Replace("</UUID>", "</Guid>");

            // Handle Nested <UUID><UUID> property
            xmlData = xmlData.Replace("<Guid><Guid>", "<UUID><Guid>");
            xmlData = xmlData.Replace("</Guid></Guid>", "</Guid></UUID>");

            try
            {
                StringReader sr;
                XmlTextReader reader;
                XmlNodeList parts;
                XmlDocument doc;

                doc = new XmlDocument();
                doc.LoadXml(xmlData);
                parts = doc.GetElementsByTagName("RootPart");

                if (parts.Count < 1)
                    return null;

                sr = new StringReader(parts[0].InnerXml);
                reader = new XmlTextReader(sr);
                part = SceneObjectPart.FromXml(fromUserInventoryItemID, reader);
                reader.Close();
                sr.Close();

                return part;
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[SERIALIZER]: Deserialization of root part xml failed with {0}.", e);
            }
            return null;
        }

        /// <summary>
        /// Deserialize a scene object from the original xml format
        /// </summary>
        /// <param name="serialization"></param>
        /// <returns></returns>
        public static SceneObjectGroup FromOriginalXmlFormat(UUID fromUserInventoryItemID, string xmlData)
        {
            //m_log.DebugFormat("[SOG]: Starting deserialization of SOG");
            //int time = System.Environment.TickCount;

            SceneObjectGroup sceneObject = new SceneObjectGroup();            

            // libomv.types changes UUID to Guid
            xmlData = xmlData.Replace("<UUID>", "<Guid>");
            xmlData = xmlData.Replace("</UUID>", "</Guid>");

            // Handle Nested <UUID><UUID> property
            xmlData = xmlData.Replace("<Guid><Guid>", "<UUID><Guid>");
            xmlData = xmlData.Replace("</Guid></Guid>", "</Guid></UUID>");

            try
            {
                StringReader  sr;
                XmlTextReader reader;
                XmlNodeList   parts;
                XmlDocument   doc;
                int           linkNum;

                doc = new XmlDocument();
                doc.LoadXml(xmlData);
                parts = doc.GetElementsByTagName("RootPart");

                if (parts.Count == 0)
                {
                    throw new Exception("Invalid Xml format - no root part");
                }
                else
                {
                    sr = new StringReader(parts[0].InnerXml);
                    reader = new XmlTextReader(sr);
                    sceneObject.SetRootPart(SceneObjectPart.FromXml(fromUserInventoryItemID, reader));
                    reader.Close();
                    sr.Close();
                }

                parts = doc.GetElementsByTagName("Part");

                for (int i = 0; i < parts.Count; i++)
                {
                    sr = new StringReader(parts[i].InnerXml);
                    reader = new XmlTextReader(sr);
                    SceneObjectPart part = SceneObjectPart.FromXml(reader);
                    linkNum = part.LinkNum;
                    sceneObject.AddPart(part);
                    part.LinkNum = linkNum;
                    part.TrimPermissions();
                    part.StoreUndoState();
                    reader.Close();
                    sr.Close();
                }

                // Script state may, or may not, exist. Not having any, is NOT
                // ever a problem.
                sceneObject.LoadScriptState(doc);

                //m_log.DebugFormat("[SERIALIZER]: Finished deserialization of SOG {0}, {1}ms", Name, System.Environment.TickCount - time);
                return sceneObject;
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[SERIALIZER]: Deserialization of xml1 failed with {0}.", e);
            }
            return null;
        }      

        /// <summary>
        /// Serialize a scene object to the original xml format
        /// </summary>
        /// <param name="sceneObject"></param>
        /// <returns></returns>        
        public static string ToOriginalXmlFormat(SceneObjectGroup sceneObject, StopScriptReason stopScriptReason)
        {
            using (StringWriter sw = new StringWriter())
            {
                using (XmlTextWriter writer = new XmlTextWriter(sw))
                {
                    ToOriginalXmlFormat(sceneObject, writer, stopScriptReason);
                }

                return sw.ToString();
            }
        }

        /// <summary>
        /// Serialize a scene object to the 'xml2' format.
        /// </summary>
        /// <param name="sceneObject"></param>
        /// <returns></returns>               
        public static string ToOriginalXmlFormat(SceneObjectGroup sceneObject, bool stopScripts)
        {
            StopScriptReason reason = stopScripts ? StopScriptReason.Derez : StopScriptReason.None;
            return ToOriginalXmlFormat(sceneObject, reason);
        }

        /// <summary>
        /// Serialize a scene object to the original xml format
        /// </summary>
        /// <param name="sceneObject"></param>
        /// <returns></returns>            
        public static void ToOriginalXmlFormat(SceneObjectGroup sceneObject, XmlTextWriter writer, StopScriptReason stopScriptReason)
        {
            //m_log.DebugFormat("[SERIALIZER]: Starting serialization of {0}", Name);
            //int time = System.Environment.TickCount;

            writer.WriteStartElement(String.Empty, "SceneObjectGroup", String.Empty);
            writer.WriteStartElement(String.Empty, "RootPart", String.Empty);
            sceneObject.RootPart.ToXml(writer);
            writer.WriteEndElement();
            writer.WriteStartElement(String.Empty, "OtherParts", String.Empty);

            foreach (SceneObjectPart part in sceneObject.GetParts())
            {
                if (part.UUID != sceneObject.RootPart.UUID)
                {
                    writer.WriteStartElement(String.Empty, "Part", String.Empty);
                    part.ToXml(writer);
                    writer.WriteEndElement();
                }
            }

            writer.WriteEndElement(); // OtherParts
            // sceneObject.SaveScriptedState(writer, stopScriptReason);
            writer.WriteEndElement(); // SceneObjectGroup

            //m_log.DebugFormat("[SERIALIZER]: Finished serialization of SOG {0}, {1}ms", Name, System.Environment.TickCount - time);
        }
        
        public static SceneObjectGroup FromXml2Format(string xmlData)
        {
            //m_log.DebugFormat("[SOG]: Starting deserialization of SOG");
            //int time = System.Environment.TickCount;

            SceneObjectGroup sceneObject = new SceneObjectGroup();
            
            // libomv.types changes UUID to Guid
            xmlData = xmlData.Replace("<UUID>", "<Guid>");
            xmlData = xmlData.Replace("</UUID>", "</Guid>");

            // Handle Nested <UUID><UUID> property
            xmlData = xmlData.Replace("<Guid><Guid>", "<UUID><Guid>");
            xmlData = xmlData.Replace("</Guid></Guid>", "</Guid></UUID>");

            try
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xmlData);

                XmlNodeList parts = doc.GetElementsByTagName("SceneObjectPart");

                // Process the root part first
                if (parts.Count > 0)
                {
                    StringReader      sr = new StringReader(parts[0].OuterXml);
                    XmlTextReader reader = new XmlTextReader(sr);
                    sceneObject.SetRootPart(SceneObjectPart.FromXml(reader));
                    reader.Close();
                    sr.Close();
                }

                // Then deal with the rest
                for (int i = 1; i < parts.Count; i++)
                {
                    StringReader      sr = new StringReader(parts[i].OuterXml);
                    XmlTextReader reader = new XmlTextReader(sr);
                    SceneObjectPart part = SceneObjectPart.FromXml(reader);

                    int originalLinkNum = part.LinkNum;
                    sceneObject.AddPart(part);
                    // SceneObjectGroup.AddPart() tries to be smart and automatically set the LinkNum.
                    // We override that here
                    if (originalLinkNum != 0)
                        part.LinkNum = originalLinkNum;

                    part.StoreUndoState();
                    reader.Close();
                    sr.Close();
                }

                // Script state may, or may not, exist. Not having any, is NOT
                // ever a problem.

                sceneObject.LoadScriptState(doc);
                //m_log.DebugFormat("[SERIALIZER]: Finished deserialization of SOG {0}, {1}ms", Name, System.Environment.TickCount - time);
                return sceneObject;
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[SERIALIZER]: Deserialization of xml2 failed with {0}.", e);
            }
            return null;
        }         

        /// <summary>
        /// Serialize a scene object to the 'xml2' format.
        /// </summary>
        /// <param name="sceneObject"></param>
        /// <returns></returns>               
        public static string ToXml2Format(SceneObjectGroup sceneObject, bool stopScripts)
        {
            StopScriptReason reason = stopScripts ? StopScriptReason.Derez : StopScriptReason.None;
            return ToXml2Format(sceneObject, reason, true);
        }

        public static string ToXml2Format(SceneObjectGroup sceneObject, StopScriptReason stopScriptReason, bool saveScriptState)
        {
            using (StringWriter sw = new StringWriter())
            {
                using (XmlTextWriter writer = new XmlTextWriter(sw))
                {
                    ToXml2Format(sceneObject, writer, stopScriptReason);
                }

                return sw.ToString();
            }
        }

        public static void ToXml2Format(SceneObjectGroup sceneObject, XmlTextWriter writer, StopScriptReason stopScriptReason)
        {
            ToXml2Format(sceneObject, writer, stopScriptReason, true);
        }

        /// <summary>
        /// Serialize a scene object to the 'xml2' format.
        /// </summary>
        /// <param name="sceneObject"></param>
        /// <returns></returns>          
        public static void ToXml2Format(SceneObjectGroup sceneObject, XmlTextWriter writer, StopScriptReason stopScriptReason, bool saveScriptState)
        {
            //m_log.DebugFormat("[SERIALIZER]: Starting serialization of SOG {0} to XML2", Name);
            //int time = System.Environment.TickCount;

            writer.WriteStartElement(String.Empty, "SceneObjectGroup", String.Empty);
            sceneObject.RootPart.ToXml(writer);
            writer.WriteStartElement(String.Empty, "OtherParts", String.Empty);
            
            foreach (SceneObjectPart part in sceneObject.GetParts())
            {
                if (part.UUID != sceneObject.RootPart.UUID)
                {
                    part.ToXml(writer);
                }
            }

            writer.WriteEndElement(); // End of OtherParts
            if (saveScriptState)
            {
                sceneObject.SaveScriptedState(writer, stopScriptReason);
            }
            writer.WriteEndElement(); // End of SceneObjectGroup

            //m_log.DebugFormat("[SERIALIZER]: Finished serialization of SOG {0} to XML2, {1}ms", Name, System.Environment.TickCount - time);
        }   
    }
}
