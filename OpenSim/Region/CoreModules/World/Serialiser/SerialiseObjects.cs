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

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;

namespace OpenSim.Region.CoreModules.World.Serialiser
{
    internal class SerialiseObjects : IFileSerialiser
    {
        #region IFileSerialiser Members

        public string WriteToFile(Scene scene, string dir)
        {
            string targetFileName = dir + "objects.xml";

            SaveSerialisedToFile(targetFileName, scene);

            return "objects.xml";
        }

        #endregion

        public void SaveSerialisedToFile(string fileName, Scene scene)
        {
            string xmlstream = GetObjectXml(scene);

            MemoryStream stream = ReformatXmlString(xmlstream);

            stream.Seek(0, SeekOrigin.Begin);
            CreateXmlFile(stream, fileName);

            stream.Seek(0, SeekOrigin.Begin);
            CreateCompressedXmlFile(stream, fileName);
        }

        private static MemoryStream ReformatXmlString(string xmlstream)
        {
            MemoryStream stream = new MemoryStream();
            XmlTextWriter formatter = new XmlTextWriter(stream, Encoding.UTF8);
            XmlDocument doc = new XmlDocument();

            doc.LoadXml(xmlstream);
            formatter.Formatting = Formatting.Indented;
            doc.WriteContentTo(formatter);
            formatter.Flush();
            return stream;
        }

        private static string GetObjectXml(Scene scene)
        {
            string xmlstream = "<scene>";

            List<EntityBase> EntityList = scene.GetEntities();
            List<string> EntityXml = new List<string>();

            foreach (EntityBase ent in EntityList)
            {
                if (ent is SceneObjectGroup)
                {
                    EntityXml.Add(SceneObjectSerializer.ToXml2Format((SceneObjectGroup)ent, false));
                }
            }
            EntityXml.Sort();

            foreach (string xml in EntityXml)
                xmlstream += xml;

            xmlstream += "</scene>";
            return xmlstream;
        }

        private static void CreateXmlFile(MemoryStream xmlStream, string fileName)
        {
            FileStream objectsFile = new FileStream(fileName, FileMode.Create);

            xmlStream.WriteTo(objectsFile);
            objectsFile.Flush();
            objectsFile.Close();
        }

        private static void CreateCompressedXmlFile(MemoryStream xmlStream, string fileName)
        {
            #region GZip Compressed Version

            FileStream objectsFileCompressed = new FileStream(fileName + ".gzs", FileMode.Create);
            MemoryStream gzipMSStream = new MemoryStream();
            GZipStream gzipStream = new GZipStream(gzipMSStream, CompressionMode.Compress);
            xmlStream.WriteTo(gzipStream);
            gzipMSStream.WriteTo(objectsFileCompressed);
            objectsFileCompressed.Flush();
            objectsFileCompressed.Close();

            #endregion
        }
    }
}
