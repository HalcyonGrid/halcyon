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

using OpenSim.Framework;
using OpenSim.Framework.Communications;
using OpenSim.Framework.Communications.Cache;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Data;

namespace OpenSim.Region.Communications.OGS1
{
    public class CommunicationsOGS1 : CommunicationsManager
    {
        public CommunicationsOGS1(
            NetworkServersInfo serversInfo, BaseHttpServer httpServer, 
            IAssetCache assetCache, LibraryRootFolder libraryRootFolder,
            ConfigSettings configSettings)
            : base(serversInfo, httpServer, assetCache, libraryRootFolder)
        {
            OGS1GridServices gridInterComms = new OGS1GridServices(serversInfo, httpServer);
            m_gridService = gridInterComms;

            // This plugin arrangement could eventually be configurable rather than hardcoded here.           
            OGS1UserServices userServices = new OGS1UserServices(this);
            //userServices.AddPlugin(new TemporaryUserProfilePlugin());
            userServices.AddPlugin(new OGS1UserDataPlugin(this, configSettings));
            
            m_userService = userServices;
            m_messageService = userServices;
            m_avatarService = (IAvatarService)m_userService;

            PluginLoader<IInventoryStoragePlugin> loader = new PluginLoader<IInventoryStoragePlugin>();
            loader.Add("/OpenSim/InventoryStorage", new PluginProviderFilter(configSettings.InventoryPlugin));
            loader.Load();

            loader.Plugin.Initialize(configSettings);
        }
    }
}
