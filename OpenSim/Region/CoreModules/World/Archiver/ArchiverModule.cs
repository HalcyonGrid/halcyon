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
using log4net;
using Nini.Config;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenMetaverse;
using System.Collections.Generic;

namespace OpenSim.Region.CoreModules.World.Archiver
{
    /// <summary>
    /// This module loads and saves OpenSimulator region archives
    /// </summary>
    public class ArchiverModule : IRegionModule, IRegionArchiverModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene m_scene;
        private IConfigSource m_config;

        public string Name { get { return "Region Archiver Module"; } }

        public bool IsSharedModule { get { return false; } }

        static int m_debug = 0; // singleton, affects all existing and subsequent instances

        public void Initialize(Scene scene, IConfigSource source)
        {
            m_scene = scene;
            m_config = source;
            m_scene.RegisterModuleInterface<IRegionArchiverModule>(this);
        }

        public void PostInitialize()
        {
        }

        public void Close()
        {
        }                

        public void SetDebug(int level)
        {
            m_debug = level;
        }

        public void ArchiveRegion(string savePath)
        {
            this.ArchiveRegion(savePath, Guid.Empty, true);
        }

        public void ArchiveRegion(string savePath, bool storeAssets)
        {
            this.ArchiveRegion(savePath, Guid.Empty, storeAssets);
        }
        
        public void ArchiveRegion(string savePath, Guid requestId, bool storeAssets)
        {
            m_log.InfoFormat(
                "[ARCHIVER]: Writing archive for region {0} to {1}", m_scene.RegionInfo.RegionName, savePath);
            
            new ArchiveWriteRequestPreparation(m_scene, savePath, requestId, storeAssets).ArchiveRegion();            
        }

        public void ArchiveRegion(string savePath, Guid requestId, IEnumerable<UUID> creatorIds)
        {
            m_log.InfoFormat(
                "[ARCHIVER]: Writing portable archive for region {0} to {1}", m_scene.RegionInfo.RegionName, savePath);

            new ArchiveWriteRequestPreparation(m_scene, savePath, requestId, true, creatorIds).ArchiveRegion();
        }
        
        public void ArchiveRegion(Stream saveStream)
        {
            ArchiveRegion(saveStream, Guid.Empty);
        }
        
        public void ArchiveRegion(Stream saveStream, Guid requestId)
        {
            new ArchiveWriteRequestPreparation(m_scene, saveStream, requestId).ArchiveRegion();
        }

        public void ScanArchiveForAssetCreatorIDs(string loadPath)
        {
            new ArchiveReadRequest(m_config, m_scene, loadPath, false, Guid.Empty, false, true, m_debug).ScanArchiveForAssetCreatorIDs();
        }

        public void DearchiveRegion(string loadPath, bool allowUserReassignment, bool skipErrorGroups, string optionsTable)
        {
            DearchiveRegion(loadPath, false, Guid.Empty, allowUserReassignment, skipErrorGroups, optionsTable);
        }

        public void DearchiveRegion(string loadPath, bool merge, Guid requestId, bool allowUserReassignment, bool skipErrorGroups, string optionsTable)
        {
            m_log.InfoFormat("[ARCHIVER]: Loading archive to region {0} from {1}", m_scene.RegionInfo.RegionName, loadPath);

            new ArchiveReadRequest(m_config, m_scene, loadPath, merge, requestId, allowUserReassignment, skipErrorGroups, m_debug).DearchiveRegion(optionsTable);
        }

        public void DearchiveRegion(Stream loadStream, bool allowUserReassignment, bool skipErrorGroups, string optionsTable)
        {
            DearchiveRegion(loadStream, false, Guid.Empty, allowUserReassignment, skipErrorGroups, optionsTable);
        }

        public void DearchiveRegion(Stream loadStream, bool merge, Guid requestId, bool allowUserReassignment, bool skipErrorGroups, string optionsTable)
        {
            new ArchiveReadRequest(m_config, m_scene, loadStream, merge, requestId, allowUserReassignment, skipErrorGroups, m_debug).DearchiveRegion(optionsTable);
        }        
    }
}
