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
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.CoreModules.Framework.InterfaceCommander;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;

namespace OpenSim.Region.CoreModules.World.Serializer
{
    public class SerializerModule : IRegionModule, IRegionSerializerModule
    {
        private Commander m_commander = new Commander("export");
        private List<Scene> m_regions = new List<Scene>();
        private string m_savedir = "exports" + "/";
        private List<IFileSerializer> m_serializers = new List<IFileSerializer>();

        #region IRegionModule Members

        public void Initialize(Scene scene, IConfigSource source)
        {
            scene.RegisterModuleCommander(m_commander);
            scene.EventManager.OnPluginConsole += EventManager_OnPluginConsole;
            scene.RegisterModuleInterface<IRegionSerializerModule>(this);

            lock (m_regions)
            {
                m_regions.Add(scene);
            }
        }

        public void PostInitialize()
        {
            lock (m_serializers)
            {
                m_serializers.Add(new SerializeTerrain());
                m_serializers.Add(new SerializeObjects());
            }

            LoadCommanderCommands();
        }

        public void Close()
        {
            m_regions.Clear();
        }

        public string Name
        {
            get { return "ExportSerializationModule"; }
        }

        public bool IsSharedModule
        {
            get { return true; }
        }

        #endregion

        #region IRegionSerializer Members

        public void LoadPrimsFromXml(Scene scene, string fileName, bool newIDS, Vector3 loadOffset)
        {
            SceneXmlLoader.LoadPrimsFromXml(scene, fileName, newIDS, loadOffset);
        }

        public void SavePrimsToXml(Scene scene, string fileName)
        {
            SceneXmlLoader.SavePrimsToXml(scene, fileName);
        }

        public void LoadPrimsFromXml2(Scene scene, string fileName)
        {
            SceneXmlLoader.LoadPrimsFromXml2(scene, fileName);
        }

        public void LoadPrimsFromXml2(Scene scene, TextReader reader, bool startScripts)
        {
            SceneXmlLoader.LoadPrimsFromXml2(scene, reader, startScripts);
        }

        public void SavePrimsToXml2(Scene scene, string fileName)
        {
            SceneXmlLoader.SavePrimsToXml2(scene, fileName);
        }

        public void SavePrimsToXml2(Scene scene, TextWriter stream, Vector3 min, Vector3 max)
        {
            SceneXmlLoader.SavePrimsToXml2(scene, stream, min, max);
        }
        
        public void SaveNamedPrimsToXml2(Scene scene, string primName, string fileName)
        {
            SceneXmlLoader.SaveNamedPrimsToXml2(scene, primName, fileName);
        }

        public SceneObjectGroup DeserializeGroupFromXml2(string xmlString)
        {
            return SceneXmlLoader.DeserializeGroupFromXml2(xmlString);
        }

        public string SaveGroupToXml2(SceneObjectGroup grp)
        {
            return SceneXmlLoader.SaveGroupToXml2(grp);
        }

        public string SaveGroupToOriginalXml(SceneObjectGroup grp)
        {
            return SceneXmlLoader.SaveGroupToOriginalXml(grp);
        }
        
        public void SavePrimListToXml2(List<EntityBase> entityList, string fileName)
        {
            SceneXmlLoader.SavePrimListToXml2(entityList, fileName);
        }

        public void SavePrimListToXml2(List<EntityBase> entityList, TextWriter stream, Vector3 min, Vector3 max)
        {
            SceneXmlLoader.SavePrimListToXml2(entityList, stream, min, max);
        }

        public List<string> SerializeRegion(Scene scene, string saveDir)
        {
            List<string> results = new List<string>();

            if (!Directory.Exists(saveDir))
            {
                Directory.CreateDirectory(saveDir);
            }

            lock (m_serializers)
            {
                foreach (IFileSerializer serializer in m_serializers)
                {
                    results.Add(serializer.WriteToFile(scene, saveDir));
                }
            }

            TextWriter regionInfoWriter = new StreamWriter(saveDir + "README.TXT");
            regionInfoWriter.WriteLine("Region Name: " + scene.RegionInfo.RegionName);
            regionInfoWriter.WriteLine("Region ID: " + scene.RegionInfo.RegionID.ToString());
            regionInfoWriter.WriteLine("Backup Time: UTC " + DateTime.UtcNow.ToString());
            regionInfoWriter.WriteLine("Serialize Version: 0.1");
            regionInfoWriter.Close();

            TextWriter manifestWriter = new StreamWriter(saveDir + "region.manifest");
            foreach (string line in results)
            {
                manifestWriter.WriteLine(line);
            }
            manifestWriter.Close();

            return results;
        }

        #endregion

        private void EventManager_OnPluginConsole(string[] args)
        {
            if (args[0] == "export")
            {
                string[] tmpArgs = new string[args.Length - 2];
                int i = 0;
                for (i = 2; i < args.Length; i++)
                    tmpArgs[i - 2] = args[i];

                m_commander.ProcessConsoleCommand(args[1], tmpArgs);
            }
        }

        private void InterfaceSaveRegion(Object[] args)
        {
            foreach (Scene region in m_regions)
            {
                if (region.RegionInfo.RegionName == (string) args[0])
                {
                    // List<string> results = SerializeRegion(region, m_savedir + region.RegionInfo.RegionID.ToString() + "/");
                    SerializeRegion(region, m_savedir + region.RegionInfo.RegionID.ToString() + "/");
                }
            }
        }

        private void InterfaceSaveAllRegions(Object[] args)
        {
            foreach (Scene region in m_regions)
            {
                // List<string> results = SerializeRegion(region, m_savedir + region.RegionInfo.RegionID.ToString() + "/");
                SerializeRegion(region, m_savedir + region.RegionInfo.RegionID.ToString() + "/");
            }
        }

        private void LoadCommanderCommands()
        {
            Command serializeSceneCommand = new Command("save", CommandIntentions.COMMAND_NON_HAZARDOUS, InterfaceSaveRegion, "Saves the named region into the exports directory.");
            serializeSceneCommand.AddArgument("region-name", "The name of the region you wish to export", "String");

            Command serializeAllScenesCommand = new Command("save-all",CommandIntentions.COMMAND_NON_HAZARDOUS,  InterfaceSaveAllRegions, "Saves all regions into the exports directory.");

            m_commander.RegisterCommand("save", serializeSceneCommand);
            m_commander.RegisterCommand("save-all", serializeAllScenesCommand);
        }
    }
}
