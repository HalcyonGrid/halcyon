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
using OpenSim.Framework;

namespace OpenSim.Data
{
    /// <summary>
    /// A static class containing methods for obtaining handles to database
    /// storage objects.
    /// </summary>
    public static class DataPluginFactory
    {
        private static readonly log4net.ILog m_log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Based on <typeparam name="T" />, returns the appropriate
        /// PluginInitializerBase instance in <paramref name="init" /> and
        /// extension point path in <paramref name="path" />.
        /// </summary>
        /// <param name="connect">
        /// The DB connection string used when creating a new
        /// PluginInitializerBase, returned in <paramref name="init" />.
        /// </param>
        /// <param name="init">
        /// A reference to a PluginInitializerBase object in which the proper
        /// initializer will be returned.
        /// </param>
        /// <param name="path">
        /// A string in which the proper extension point path will be returned.
        /// </param>
        /// <typeparam name="T">
        /// The type of data plugin requested.
        /// </typeparam>
        /// <exception cref="NotImplementedException">
        /// Thrown if <typeparamref name="T" /> is not one of the expected data
        /// interfaces.
        /// </exception>
        private static void PluginLoaderParamFactory<T>(string connect, out PluginInitializerBase init, out string path) where T : IPlugin
        {
            Type type = typeof(T);

            if (type == typeof(IInventoryDataPlugin))
            {
                init = new InventoryDataInitializer(connect);
                path = "/OpenSim/InventoryData";
            }
            else if (type == typeof(IUserDataPlugin))
            {
                init = new UserDataInitializer(connect);
                path = "/OpenSim/UserData";
            }
            else if (type == typeof(IGridDataPlugin))
            {
                init = new GridDataInitializer(connect);
                path = "/OpenSim/GridData";
            }
            else if (type == typeof(ILogDataPlugin))
            {
                init = new LogDataInitializer(connect);
                path = "/OpenSim/LogData";
            }
            else if (type == typeof(IAssetDataPlugin))
            {
                init = new AssetDataInitializer(connect);
                path = "/OpenSim/AssetData";
            }
            else
            {
                // We don't support this data plugin.
                throw new NotImplementedException(String.Format("The type '{0}' is not a valid data plugin.", type));
            }
        }

        /// <summary>
        /// Returns a list of new <typeparamref name="T" /> data plugins.
        /// Plugins will be requested in the order they were added.
        /// </summary>
        /// <param name="provider">
        /// The filename of the inventory server plugin DLL.
        /// </param>
        /// <param name="connect">
        /// The connection string for the storage backend.
        /// </param>
        /// <typeparam name="T">
        /// The type of data plugin requested.
        /// </typeparam>
        /// <returns>
        /// A list of all loaded plugins matching <typeparamref name="T" />.
        /// </returns>
        public static List<T> LoadDataPlugins<T>(string provider, string connect) where T : IPlugin
        {
            PluginInitializerBase pluginInitializer;
            string extensionPointPath;

            PluginLoaderParamFactory<T>(connect, out pluginInitializer, out extensionPointPath);

            PluginLoader<T> loader = new PluginLoader<T>(pluginInitializer);

            // loader will try to load all providers (MySQL, MSSQL, etc)
            // unless it is constrainted to the correct "Provider" entry in the addin.xml
            loader.Add(extensionPointPath, new PluginProviderFilter(provider));
            loader.Load();

            if (loader.Plugins.Count <= 0)
            {
                m_log.Warn($"No plugins found for extension path {extensionPointPath}");
            }

            return loader.Plugins;
        }

        /// <summary>
        /// Returns a new <typeparamref name="T" /> data plugin instance if
        /// only one was loaded, otherwise returns null (<c>default(T)</c>).
        /// </summary>
        /// <param name="provider">
        /// The filename of the inventory server plugin DLL.
        /// </param>
        /// <param name="connect">
        /// The connection string for the storage backend.
        /// </param>
        /// <typeparam name="T">
        /// The type of data plugin requested.
        /// </typeparam>
        /// <returns>
        /// A list of all loaded plugins matching <typeparamref name="T" />.
        /// </returns>
        public static T LoadDataPlugin<T>(string provider, string connect) where T : IPlugin
        {
            List<T> plugins = LoadDataPlugins<T>(provider, connect);
            return (plugins.Count == 1) ? plugins[0] : default(T);
        }
    }
}
