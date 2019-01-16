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
using OpenMetaverse;

namespace OpenSim.Region.Framework.Interfaces
{
    /// <summary>
    /// Interface to region archive functionality
    /// </summary>
    public interface IRegionArchiverModule
    {
        /// <summary>
        /// Archive the region to the given path
        /// </summary>
        /// 
        /// This method occurs asynchronously.  If you want notification of when it has completed then subscribe to
        /// the EventManager.OnOarFileSaved event.
        /// 
        /// <param name="savePath"></param>
        /// <param name="storeAssets">Whether or not to store binary assets with the archive</param>
        void ArchiveRegion(string savePath, bool storeAssets);

        /// <summary>
        /// Archive the region to the given path
        /// </summary>
        /// 
        /// This method occurs asynchronously.  If you want notification of when it has completed then subscribe to
        /// the EventManager.OnOarFileSaved event.
        /// 
        /// <param name="savePath"></param>
        void ArchiveRegion(string savePath);
        
        /// <summary>
        /// Archive the region to the given path
        /// </summary>
        /// 
        /// This method occurs asynchronously.  If you want notification of when it has completed then subscribe to
        /// the EventManager.OnOarFileSaved event.
        /// 
        /// <param name="savePath"></param>  
        /// <param name="requestId">If supplied, this request Id is later returned in the saved event</param>
        void ArchiveRegion(string savePath, Guid requestId, bool storeAssets);        

        /// <summary>
        /// Archive the region to a stream.
        /// </summary>
        /// 
        /// This method occurs asynchronously.  If you want notification of when it has completed then subscribe to
        /// the EventManager.OnOarFileSaved event.
        /// 
        /// <param name="saveStream"></param>
        /// <param name="requestId">If supplied, this request Id is later returned in the saved event</param>
        void ArchiveRegion(Stream saveStream, Guid requestId);

        /// <summary>
        /// Saves a portable OAR with assets from users not listed stripped
        /// </summary>
        /// <param name="savePath"></param>
        /// <param name="requestId"></param>
        /// <param name="creatorIds"></param>
        void ArchiveRegion(string savePath, Guid requestId, IEnumerable<UUID> creatorIds);

        /// <summary>
        /// Dearchive the given region archive.  This replaces the existing scene.
        /// </summary>
        /// 
        /// If you want notification of when it has completed then subscribe to the EventManager.OnOarFileLoaded event.
        /// 
        /// <param name="loadPath"></param>
        void DearchiveRegion(string loadPath, bool allowUserReassignment, bool skipErrorGroups, string optionsTable);
        
        /// <summary>
        /// Dearchive the given region archive.  This replaces the existing scene.
        /// </summary>
        /// 
        /// If you want notification of when it has completed then subscribe to the EventManager.OnOarFileLoaded event.
        /// 
        /// <param name="loadPath"></param>
        /// <param name="merge">
        /// If true, the loaded region merges with the existing one rather than replacing it.  Any terrain or region
        /// settings in the archive will be ignored.
        /// </param>
        /// <param name="requestId">If supplied, this request Id is later returned in the saved event</param>
        void DearchiveRegion(string loadPath, bool merge, Guid requestId, bool allowUserReassignment, bool skipErrorGroups, string optionsTable);
        
        /// <summary>
        /// Dearchive a region from a stream.  This replaces the existing scene. 
        /// </summary>
        /// 
        /// If you want notification of when it has completed then subscribe to the EventManager.OnOarFileLoaded event.
        /// 
        /// <param name="loadStream"></param>
        void DearchiveRegion(Stream loadStream, bool allowUserReassignment, bool skipErrorGroups, string optionsTable);
        
        /// <summary>
        /// Dearchive a region from a stream.  This replaces the existing scene.
        /// </summary>
        /// 
        /// If you want notification of when it has completed then subscribe to the EventManager.OnOarFileLoaded event.
        /// 
        /// <param name="loadStream"></param>
        /// <param name="merge">
        /// If true, the loaded region merges with the existing one rather than replacing it.  Any terrain or region
        /// settings in the archive will be ignored.
        /// </param>    
        /// <param name="requestId">If supplied, this request Id is later returned in the saved event</param>
        void DearchiveRegion(Stream loadStream, bool merge, Guid requestId, bool allowUserReassignment, bool skipErrorGroups, string optionsTable);

        /// <summary>
        /// Scans the objects in an archive to try to find creator IDs for asset (even if the assets are not present).
        /// </summary>
        /// 
        /// <param name="loadPath">name of the OAR file</param>
        void ScanArchiveForAssetCreatorIDs(string loadPath);

        void SetDebug(int level);
    }
}
