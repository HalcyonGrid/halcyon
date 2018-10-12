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
using OpenSim.Data;
using OpenMetaverse;
using OpenSim.Framework;

namespace InWorldz.Data.Inventory.Cassandra
{
    public class CassandraMigrationProviderSelector : IInventoryProviderSelector
    {
        private bool _migrationActive;
        private bool _legacyOnly;   // disable Cassandra inventory?

        private InventoryStorage _cassandraStorage;
        private LegacyMysqlInventoryStorage _legacyStorage;

        private ICheckedInventoryStorage _checkedCassandraStorage;
        private ICheckedInventoryStorage _checkedLegacyStorage;

        private C5.HashSet<UUID> _migratedUsers = new C5.HashSet<UUID>();

        private MigrationStatusReader _migrationStatusChecker;



        public CassandraMigrationProviderSelector(bool migrationActive, bool legacyOnly,
            string coreConnString,
            InventoryStorage cassandraStorage, LegacyMysqlInventoryStorage legacyStorage)
        {
            _legacyOnly = legacyOnly;
            _migrationActive = migrationActive;
            _cassandraStorage = cassandraStorage;
            _legacyStorage = legacyStorage;

            _checkedLegacyStorage = new CheckedInventoryStorage(_legacyStorage);
            if (!legacyOnly)
            {
                _checkedCassandraStorage = new CheckedInventoryStorage(_cassandraStorage);
                _migrationStatusChecker = new MigrationStatusReader(coreConnString);
            }
        }

        public IInventoryStorage GetProvider(UUID userId)
        {
            // Are we sticking with MySQL only?
            if (_legacyOnly)
            {
                return _legacyStorage;
            }

            // Otherwise, Cassandra supported, and maybe both.
            if (!_migrationActive)
            {
                return _cassandraStorage;
            }

            lock (_migratedUsers)
            {
                if (_migratedUsers.Contains(userId))
                {
                    return _cassandraStorage;
                }
            }

            //nothing in our cache, we need to consult the database
            MigrationStatus status = _migrationStatusChecker.GetUserMigrationStatus(userId);
            if (status == MigrationStatus.Migrated)
            {
                lock (_migratedUsers)
                {
                    _migratedUsers.Add(userId);
                }

                return _cassandraStorage;
            }
            else if (status == MigrationStatus.InProgress)
            {
                throw new InventoryStorageException("Inventory can not be used while a migration is in progress");
            }
            else
            {
                return _legacyStorage;
            }
        }

        public ICheckedInventoryStorage GetCheckedProvider(UUID userId)
        {
            if (_legacyOnly)
            {
                return _checkedLegacyStorage;
            }

            if (!_migrationActive)
            {
                return _checkedCassandraStorage;
            }

            lock (_migratedUsers)
            {
                if (_migratedUsers.Contains(userId))
                {
                    return _checkedCassandraStorage;
                }
            }

            //nothing in our cache, we need to consult the database
            MigrationStatus status = _migrationStatusChecker.GetUserMigrationStatus(userId);
            if (status == MigrationStatus.Migrated)
            {
                lock (_migratedUsers)
                {
                    _migratedUsers.Add(userId);
                }

                return _checkedCassandraStorage;
            }
            else if (status == MigrationStatus.InProgress)
            {
                throw new InventoryStorageException("Inventory can not be used while a migration is in progress");
            }
            else
            {
                return _checkedLegacyStorage;
            }
        }

        public ICheckedInventoryStorage GetGroupsProvider()
        {
            return _checkedCassandraStorage;
        }
        public ICheckedInventoryStorage GetLegacyGroupsProvider()
        {
            return _checkedLegacyStorage;
        }
    }
}
