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
using System.Reflection;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.World.WorldMap
{
    public class MapSearchModule : IRegionModule
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        Scene m_scene = null; // only need one for communication with GridService
        List<Scene> m_scenes = new List<Scene>();

        #region IRegionModule Members
        public void Initialize(Scene scene, IConfigSource source)
        {
            if (m_scene == null)
            {
                m_scene = scene;
            }

            m_scenes.Add(scene);
            scene.EventManager.OnNewClient += OnNewClient;
        }

        public void PostInitialize()
        {
        }

        public void Close()
        {
            m_scene = null;
            m_scenes.Clear();
        }

        public string Name
        {
            get { return "MapSearchModule"; }
        }

        public bool IsSharedModule
        {
            get { return true; }
        }

        #endregion

        private void OnNewClient(IClientAPI client)
        {
            client.OnMapNameRequest += OnMapNameRequest;
        }

        private void OnMapNameRequest(IClientAPI remoteClient, string mapName, uint flags)
        {
            if (mapName.Length < 3)
            {
                remoteClient.SendAlertMessage("Use a search string with at least 3 characters");
                return;
            }
            
            // try to fetch from GridServer
            List<RegionInfo> regionInfos = m_scene.SceneGridService.RequestNamedRegions(mapName, 20);
            if (regionInfos == null)
            {
                m_log.Warn("[MAPSEARCHMODULE]: RequestNamedRegions returned null. Old gridserver?");
                // service wasn't available; maybe still an old GridServer. Try the old API, though it will return only one region
                regionInfos = new List<RegionInfo>();
                RegionInfo info = m_scene.SceneGridService.RequestClosestRegion(mapName);
                if (info != null) regionInfos.Add(info);
            }

            List<MapBlockData> blocks = new List<MapBlockData>();

            MapBlockData data;
            if (regionInfos.Count > 0)
            {
                foreach (RegionInfo info in regionInfos)
                {
                    data = new MapBlockData();
                    data.Agents = 0;
                    data.Access = info.AccessLevel;
                    data.MapImageId = info.RegionSettings.TerrainImageID;
                    data.Name = info.RegionName;
                    data.RegionFlags = 0; // TODO not used?
                    data.WaterHeight = 0; // not used
                    data.X = (ushort)info.RegionLocX;
                    data.Y = (ushort)info.RegionLocY;
                    blocks.Add(data);
                }
            }

            // final block, closing the search result
            data = new MapBlockData();
            data.Agents = 0;
            data.Access = 255;
            data.MapImageId = UUID.Zero;
            data.Name = mapName;
            data.RegionFlags = 0;
            data.WaterHeight = 0; // not used
            data.X = 0;
            data.Y = 0;
            blocks.Add(data);

            remoteClient.SendMapBlock(blocks, flags);
        }

        private bool IsHypergridOn()
        {
            return false;
        }

        private Scene GetClientScene(IClientAPI client)
        {
            foreach (Scene s in m_scenes)
            {
                if (client.Scene.RegionInfo.RegionHandle == s.RegionInfo.RegionHandle)
                    return s;
            }
            return m_scene;
        }
    }
}
