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
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenSim.Framework.Communications;
using OpenSim.Framework;
using OpenSim.Data;
using OpenMetaverse;

namespace Halcyon.Data.Inventory.MySQL
{
    public class MySqlInventoryPlugin : IInventoryStoragePlugin
    {
        private MySqlInventoryStorage _storage;
        private InventoryProviderSelector _storageSelector;

        #region IInventoryStoragePlugin Members

        public void Initialize(ConfigSettings settings)
        {
            _storage = new MySqlInventoryStorage(settings.InventorySource);

            // ProviderRegistry.Instance.RegisterInterface<IInventoryStoragePlugin>(this);
            _storageSelector = new InventoryProviderSelector(settings.CoreConnectionString, _storage);
            ProviderRegistry.Instance.RegisterInterface<IInventoryProviderSelector>(_storageSelector);
        }

        public IInventoryStorage GetStorage()
        {
            return _storage;
        }

        #endregion

        #region IPlugin Members

        public string Version
        {
            get { return "1.0.0"; }
        }

        public string Name
        {
            get { return "Halcyon.Data.Inventory.MySQL";  }
        }

        public void Initialize()
        {
            
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            
        }

        #endregion
    }
}
