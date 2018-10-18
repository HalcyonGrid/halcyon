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
using NUnit.Framework;
using OpenSim.Framework;
using OpenMetaverse;

namespace Halcyon.Data.Inventory.MySQL
{
    [TestFixture]
    class UnitTests
    {
        private MySqlInventoryStorage _storage;

        [SetUp]
        public void Setup()
        {
            _storage = new MySqlInventoryStorage("Data Source=localhost;User=root;Password=secret");
        }

        [TestCase]
        public void TestBasicCreateFolder()
        {
            UUID userId = UUID.Random();

            InventoryFolderBase folder = new InventoryFolderBase();
            folder.ID = UUID.Random();
            folder.Name = "Test Root 1";
            folder.Level = InventoryFolderBase.FolderLevel.Root;
            folder.Owner = userId;
            folder.ParentID = UUID.Zero;
            folder.Type = 1;

            _storage.CreateFolder(folder);

            List<InventoryFolderBase> skel = _storage.GetInventorySkeleton(userId);
            Assert.IsTrue(skel.Count == 1);
            Assert.AreEqual(skel[0].ID, folder.ID);

            InventoryFolderBase folderCopy = _storage.GetFolderAttributes(folder.ID);
            AssertFolderEqual(folder, folderCopy, true);
            Assert.AreEqual(1, folderCopy.Version);

            _storage.SaveFolder(folderCopy);

            InventoryFolderBase folderCopy2 = _storage.GetFolderAttributes(folder.ID);
            AssertFolderEqual(folder, folderCopy2, true);
            Assert.AreEqual(2, folderCopy2.Version);
        }

        [TestCase]
        public void TestSkeletonDataMatchesFolderProps()
        {
            UUID userId = UUID.Random();

            InventoryFolderBase folder = new InventoryFolderBase();
            folder.ID = UUID.Random();
            folder.Name = "TestFolder";
            folder.Level = InventoryFolderBase.FolderLevel.Root;
            folder.Owner = userId;
            folder.ParentID = UUID.Random();
            folder.Type = -1;

            _storage.CreateFolder(folder);

            List<InventoryFolderBase> skel = _storage.GetInventorySkeleton(userId);
            Assert.IsTrue(skel.Count == 1);
            AssertFolderEqual(skel[0], folder, true);

            InventoryFolderBase folderCopy = _storage.GetFolderAttributes(folder.ID);
            AssertFolderEqual(folder, folderCopy, true);
            AssertFolderEqual(skel[0], folderCopy, true);
            Assert.AreEqual(1, folderCopy.Version);
        }

        [TestCase]
        public void TestUpdateFolderKeepsIndexUpdated()
        {
            UUID userId = UUID.Random();

            InventoryFolderBase folder = new InventoryFolderBase();
            folder.ID = UUID.Random();
            folder.Name = "Test Folder zzzzzz";
            folder.Level = InventoryFolderBase.FolderLevel.Root;
            folder.Owner = userId;
            folder.ParentID = UUID.Random();
            folder.Type = -1;

            _storage.CreateFolder(folder);

            folder.Name = "Updated TestFolderZZZ";

            _storage.SaveFolder(folder);

            List<InventoryFolderBase> skel = _storage.GetInventorySkeleton(userId);
            Assert.AreEqual(1, skel.Count);
            Assert.AreEqual(2, skel[0].Version);

            AssertFolderEqual(folder, skel[0], true);

            UUID newParent = UUID.Random();
            _storage.MoveFolder(folder, newParent);

            InventoryFolderBase folderCopy = _storage.GetFolderAttributes(folder.ID);
            skel = _storage.GetInventorySkeleton(userId);

            Assert.AreEqual(1, skel.Count);
            Assert.AreEqual(3, skel[0].Version);
            Assert.AreEqual(newParent, folderCopy.ParentID);
            AssertFolderEqual(folderCopy, skel[0], true);

        }

        private void AssertFolderEqual(InventoryFolderBase f1, InventoryFolderBase f2, bool checkParent)
        {
            Assert.AreEqual(f1.ID, f2.ID);
            Assert.AreEqual(f1.Level, f2.Level);
            Assert.AreEqual(f1.Name, f2.Name);
            Assert.AreEqual(f1.Owner, f2.Owner);

            if (checkParent)
            {
                Assert.AreEqual(f1.ParentID, f2.ParentID);
            }

            Assert.AreEqual(f1.Type, f2.Type);
        }

        [TestCase]
        public void TestMoveFolderToNewParent()
        {
            UUID userId = UUID.Random();

            InventoryFolderBase folder = new InventoryFolderBase();
            folder.ID = UUID.Random();
            folder.Name = "Test Root 1";
            folder.Level = InventoryFolderBase.FolderLevel.Root;
            folder.Owner = userId;
            folder.ParentID = UUID.Zero;
            folder.Type = 1;

            _storage.CreateFolder(folder);

            InventoryFolderBase firstLeaf = new InventoryFolderBase();
            firstLeaf.ID = UUID.Random();
            firstLeaf.Name = "Leaf 1";
            firstLeaf.Level = InventoryFolderBase.FolderLevel.Leaf;
            firstLeaf.Owner = userId;
            firstLeaf.ParentID = folder.ID;
            firstLeaf.Type = 1;

            InventoryFolderBase secondLeaf = new InventoryFolderBase();
            secondLeaf.ID = UUID.Random();
            secondLeaf.Name = "Leaf 2";
            secondLeaf.Level = InventoryFolderBase.FolderLevel.Leaf;
            secondLeaf.Owner = userId;
            secondLeaf.ParentID = folder.ID;
            secondLeaf.Type = 1;

            _storage.CreateFolder(firstLeaf);
            _storage.CreateFolder(secondLeaf);

            InventoryFolderBase secondLeafCopy = _storage.GetFolderAttributes(secondLeaf.ID);
            AssertFolderEqual(secondLeaf, secondLeafCopy, true);
            Assert.AreEqual(1, secondLeafCopy.Version);

            _storage.MoveFolder(secondLeaf, firstLeaf.ID);

            InventoryFolderBase secondLeafWithNewParent = _storage.GetFolderAttributes(secondLeaf.ID);
            AssertFolderEqual(secondLeaf, secondLeafWithNewParent, false);
            Assert.AreEqual(firstLeaf.ID, secondLeafWithNewParent.ParentID);
            Assert.AreEqual(2, secondLeafWithNewParent.Version);

            InventoryFolderBase firstLeafWithSecondLeafChild = _storage.GetFolder(firstLeaf.ID);
            AssertFolderEqual(firstLeafWithSecondLeafChild, firstLeaf, true);
            Assert.AreEqual(1, firstLeafWithSecondLeafChild.SubFolders.Count);
            Assert.AreEqual(secondLeaf.Name, firstLeafWithSecondLeafChild.SubFolders[0].Name);
            Assert.AreEqual(secondLeaf.ID, firstLeafWithSecondLeafChild.SubFolders[0].ID);
            Assert.AreEqual(2, firstLeafWithSecondLeafChild.Version);
        }

        [TestCase]
        public void TestFolderChangeUpdateParent()
        {
            UUID userId = UUID.Random();

            InventoryFolderBase folder = new InventoryFolderBase();
            folder.ID = UUID.Random();
            folder.Name = "Test Root 1";
            folder.Level = InventoryFolderBase.FolderLevel.Root;
            folder.Owner = userId;
            folder.ParentID = UUID.Zero;
            folder.Type = 1;

            _storage.CreateFolder(folder);

            InventoryFolderBase firstLeaf = new InventoryFolderBase();
            firstLeaf.ID = UUID.Random();
            firstLeaf.Name = "Leaf 1";
            firstLeaf.Level = InventoryFolderBase.FolderLevel.Leaf;
            firstLeaf.Owner = userId;
            firstLeaf.ParentID = folder.ID;
            firstLeaf.Type = 1;

            _storage.CreateFolder(firstLeaf);

            firstLeaf.Name = "Leaf 1, Updated";
            _storage.SaveFolder(firstLeaf);

            InventoryFolderBase folderWithUpdatedChild = _storage.GetFolder(folder.ID);
            Assert.AreEqual(1, folderWithUpdatedChild.SubFolders.Count);
            Assert.AreEqual(firstLeaf.Name, folderWithUpdatedChild.SubFolders[0].Name);
            Assert.AreEqual(firstLeaf.ID, folderWithUpdatedChild.SubFolders[0].ID);
        }

        [TestCase]
        public void TestSendFolderToTrash()
        {
            UUID userId = UUID.Random();

            InventoryFolderBase folder = new InventoryFolderBase();
            folder.ID = UUID.Random();
            folder.Name = "Trash Folder";
            folder.Level = InventoryFolderBase.FolderLevel.TopLevel;
            folder.Owner = userId;
            folder.ParentID = UUID.Zero;
            folder.Type = (short)OpenMetaverse.FolderType.Trash;

            _storage.CreateFolder(folder);

            InventoryFolderBase trashFolderRetrieved = _storage.GetFolder(folder.ID);
            Assert.AreEqual((short)OpenMetaverse.FolderType.Trash, trashFolderRetrieved.Type);

            InventoryFolderBase firstLeaf = new InventoryFolderBase();
            firstLeaf.ID = UUID.Random();
            firstLeaf.Name = "Leaf 1";
            firstLeaf.Level = InventoryFolderBase.FolderLevel.Leaf;
            firstLeaf.Owner = userId;
            firstLeaf.ParentID = UUID.Zero;
            firstLeaf.Type = 1;

            _storage.CreateFolder(firstLeaf);

            _storage.SendFolderToTrash(firstLeaf, UUID.Zero);

            InventoryFolderBase leafFolderAfterTrashed = _storage.GetFolder(firstLeaf.ID);
            Assert.AreEqual(folder.ID, leafFolderAfterTrashed.ParentID);

            InventoryFolderBase trashFolderAfterLeafTrashed = _storage.GetFolder(folder.ID);
            Assert.AreEqual(1, trashFolderAfterLeafTrashed.SubFolders.Count);
            Assert.AreEqual(leafFolderAfterTrashed.ID, trashFolderAfterLeafTrashed.SubFolders[0].ID);
            Assert.AreEqual(leafFolderAfterTrashed.Name, trashFolderAfterLeafTrashed.SubFolders[0].Name);
        }

        [TestCase]
        public void TestBasicCreateItem()
        {
            UUID userId = UUID.Random();

            InventoryFolderBase folder = new InventoryFolderBase();
            folder.ID = UUID.Random();
            folder.Name = "Root Folder";
            folder.Level = InventoryFolderBase.FolderLevel.Root;
            folder.Owner = userId;
            folder.ParentID = UUID.Zero;
            folder.Type = (short)OpenMetaverse.FolderType.Root;

            _storage.CreateFolder(folder);

            UUID assetId = UUID.Random();
            UUID itemId = UUID.Random();
            InventoryItemBase item = new InventoryItemBase
            {
                AssetID = assetId,
                AssetType = (int)OpenMetaverse.AssetType.Texture,
                BasePermissions = 0xFF,
                CreationDate = Util.UnixTimeSinceEpoch(),
                CreatorId = userId.ToString(),
                CurrentPermissions = 0xFFF,
                Description = "A good item, of goodness",
                EveryOnePermissions = 0xFFFF,
                Flags = 0x12,
                Folder = folder.ID,
                GroupID = UUID.Zero,
                GroupOwned = false,
                GroupPermissions = 0x123,
                ID = itemId,
                InvType = (int)OpenMetaverse.InventoryType.Texture,
                Name = "Item of Goodness",
                NextPermissions = 0xF,
                Owner = userId,
                SalePrice = 100,
                SaleType = 1
            };

            _storage.CreateItem(item);

            InventoryItemBase itemCopy = _storage.GetItem(itemId, UUID.Zero);
            AssertItemEqual(itemCopy, item);

            InventoryFolderBase folderWithItem = _storage.GetFolder(folder.ID);
            Assert.AreEqual(1, folderWithItem.Items.Count);
            Assert.AreEqual(2, folderWithItem.Version);

            AssertItemEqual(itemCopy, folderWithItem.Items[0]);
        }

        [TestCase]
        public void TestSaveItem()
        {
            UUID userId = UUID.Random();

            InventoryFolderBase folder = new InventoryFolderBase();
            folder.ID = UUID.Random();
            folder.Name = "Root Folder";
            folder.Level = InventoryFolderBase.FolderLevel.Root;
            folder.Owner = userId;
            folder.ParentID = UUID.Zero;
            folder.Type = (short)OpenMetaverse.FolderType.Root;

            _storage.CreateFolder(folder);

            UUID assetId = UUID.Random();
            UUID itemId = UUID.Random();
            InventoryItemBase item = new InventoryItemBase
            {
                AssetID = assetId,
                AssetType = (int)OpenMetaverse.AssetType.Texture,
                BasePermissions = 0xFF,
                CreationDate = Util.UnixTimeSinceEpoch(),
                CreatorId = userId.ToString(),
                CurrentPermissions = 0xFFF,
                Description = "A good item, of goodness",
                EveryOnePermissions = 0xFFFF,
                Flags = 0x12,
                Folder = folder.ID,
                GroupID = UUID.Zero,
                GroupOwned = false,
                GroupPermissions = 0x123,
                ID = itemId,
                InvType = (int)OpenMetaverse.InventoryType.Texture,
                Name = "Item of Goodness",
                NextPermissions = 0xF,
                Owner = userId,
                SalePrice = 100,
                SaleType = 1
            };

            _storage.CreateItem(item);

            InventoryItemBase itemCopy = _storage.GetItem(itemId, UUID.Zero);
            AssertItemEqual(itemCopy, item);

            item.AssetID = assetId;
            item.AssetType = (int)OpenMetaverse.AssetType.Sound;
            item.BasePermissions = 0xAA;
            item.CreationDate = Util.UnixTimeSinceEpoch();
            item.CreatorId = userId.ToString();
            item.CurrentPermissions = 0xAAA;
            item.Description = "A good itemddddd";
            item.EveryOnePermissions = 0xAAAA;
            item.Flags = 0x24;
            item.Folder = folder.ID;
            item.GroupID = UUID.Random();
            item.GroupOwned = true;
            item.GroupPermissions = 0x456;
            item.ID = itemId;
            item.InvType = (int)OpenMetaverse.InventoryType.Sound;
            item.Name = "Itemjkhdsahjkadshkjasds";
            item.NextPermissions = 0xA;
            item.Owner = userId;
            item.SalePrice = 10;
            item.SaleType = 8;

            _storage.SaveItem(item);

            itemCopy = _storage.GetItem(itemId, UUID.Zero);
            AssertItemEqual(itemCopy, item);

            InventoryFolderBase folderCopy = _storage.GetFolderAttributes(folder.ID);
            Assert.AreEqual(3, folderCopy.Version);
        }

        [TestCase]
        public void TestSaveItemInt32Extremes()
        {
            UUID userId = UUID.Random();

            InventoryFolderBase folder = new InventoryFolderBase();
            folder.ID = UUID.Random();
            folder.Name = "Root Folder";
            folder.Level = InventoryFolderBase.FolderLevel.Root;
            folder.Owner = userId;
            folder.ParentID = UUID.Zero;
            folder.Type = (short)OpenMetaverse.FolderType.Root;

            _storage.CreateFolder(folder);

            UUID assetId = UUID.Random();
            UUID itemId = UUID.Random();
            InventoryItemBase item = new InventoryItemBase
            {
                AssetID = assetId,
                AssetType = (int)OpenMetaverse.AssetType.Texture,
                BasePermissions = UInt32.MaxValue,
                CreationDate = Util.UnixTimeSinceEpoch(),
                CreatorId = userId.ToString(),
                CurrentPermissions = unchecked((uint)-1),
                Description = "A good item, of goodness",
                EveryOnePermissions = Int32.MaxValue + (uint)1,
                Flags = unchecked((uint)Int32.MinValue),
                Folder = folder.ID,
                GroupID = UUID.Zero,
                GroupOwned = false,
                GroupPermissions = 0x123,
                ID = itemId,
                InvType = (int)OpenMetaverse.InventoryType.Texture,
                Name = "Item of Goodness",
                NextPermissions = 0xF,
                Owner = userId,
                SalePrice = 100,
                SaleType = 1
            };

            _storage.CreateItem(item);

            InventoryItemBase itemCopy = _storage.GetItem(itemId, UUID.Zero);
            AssertItemEqual(itemCopy, item);

            
        }

        [Test]
        [Repeat(10)]
        public void TestBasicMoveItem()
        {
            UUID userId = UUID.Random();

            InventoryFolderBase folder1 = new InventoryFolderBase();
            folder1.ID = UUID.Random();
            folder1.Name = "Folder1";
            folder1.Level = InventoryFolderBase.FolderLevel.Root;
            folder1.Owner = userId;
            folder1.ParentID = UUID.Zero;
            folder1.Type = (short)OpenMetaverse.FolderType.Root;

            _storage.CreateFolder(folder1);

            InventoryFolderBase folder2 = new InventoryFolderBase();
            folder2.ID = UUID.Random();
            folder2.Name = "Folder1";
            folder2.Level = InventoryFolderBase.FolderLevel.Root;
            folder2.Owner = userId;
            folder2.ParentID = UUID.Zero;
            folder2.Type = (short)OpenMetaverse.FolderType.Root;

            _storage.CreateFolder(folder2);


            UUID assetId = UUID.Random();
            UUID itemId = UUID.Random();
            InventoryItemBase item = new InventoryItemBase
            {
                AssetID = assetId,
                AssetType = (int)OpenMetaverse.AssetType.Texture,
                BasePermissions = UInt32.MaxValue,
                CreationDate = Util.UnixTimeSinceEpoch(),
                CreatorId = userId.ToString(),
                CurrentPermissions = unchecked((uint)-1),
                Description = "A good item, of goodness",
                EveryOnePermissions = Int32.MaxValue + (uint)1,
                Flags = unchecked((uint)Int32.MinValue),
                Folder = folder1.ID,
                GroupID = UUID.Zero,
                GroupOwned = false,
                GroupPermissions = 0x123,
                ID = itemId,
                InvType = (int)OpenMetaverse.InventoryType.Texture,
                Name = "Item of Goodness",
                NextPermissions = 0xF,
                Owner = userId,
                SalePrice = 100,
                SaleType = 1
            };

            _storage.CreateItem(item);

            ///
            /// NOTE: The race mentioned here should be fixed. I replaced the UnixTimeSinceEpochInMicroseconds
            /// Implementation to fix it. I am leaving the comments here as historical information.
            /// The sleep is no longer needed
            ///
            //I'm seeing a race here, that could be explained by
            //the timestamps in the ItemParents CF being the same between
            //the create and the move call. The index is left in a state
            //still pointing at the old folder

            //we sleep to mitigate this problem since we should never see a create
            //and a move executed in the same tick in a real inventory situation
            //this highlights the need for all clocks on the network to be synchronized
            //System.Threading.Thread.Sleep(30);

            _storage.MoveItem(item, folder2);

            InventoryItemBase itemCopy = _storage.GetItem(itemId, UUID.Zero);

            AssertItemEqual(item, itemCopy);

            Assert.AreEqual(folder2.ID, item.Folder);

            InventoryFolderBase containingFolder = _storage.GetFolder(folder2.ID);
            Assert.AreEqual(2, containingFolder.Version);
            Assert.AreEqual(1, containingFolder.Items.Count);
            AssertItemEqual(item, containingFolder.Items[0]);
            

            InventoryFolderBase oldFolder = _storage.GetFolder(folder1.ID);
            Assert.AreEqual(0, oldFolder.Items.Count);
            Assert.AreEqual(3, oldFolder.Version);

        }

        [Test]
        public void TestItemPurge()
        {
            UUID userId = UUID.Random();

            InventoryFolderBase folder1 = new InventoryFolderBase();
            folder1.ID = UUID.Random();
            folder1.Name = "Folder1";
            folder1.Level = InventoryFolderBase.FolderLevel.Root;
            folder1.Owner = userId;
            folder1.ParentID = UUID.Zero;
            folder1.Type = (short)OpenMetaverse.FolderType.Root;

            _storage.CreateFolder(folder1);

            Amib.Threading.SmartThreadPool pool = new Amib.Threading.SmartThreadPool(1000, 20);
            pool.Start();
            for (int i = 0; i < 1000; i++)
            {
                pool.QueueWorkItem(() =>
                    {
                        UUID assetId = UUID.Random();
                        UUID itemId = UUID.Random();
                        InventoryItemBase item = new InventoryItemBase
                        {
                            AssetID = assetId,
                            AssetType = (int)OpenMetaverse.AssetType.Texture,
                            BasePermissions = UInt32.MaxValue,
                            CreationDate = Util.UnixTimeSinceEpoch(),
                            CreatorId = userId.ToString(),
                            CurrentPermissions = unchecked((uint)-1),
                            Description = "A good item, of goodness",
                            EveryOnePermissions = Int32.MaxValue + (uint)1,
                            Flags = unchecked((uint)Int32.MinValue),
                            Folder = folder1.ID,
                            GroupID = UUID.Zero,
                            GroupOwned = false,
                            GroupPermissions = 0x123,
                            ID = itemId,
                            InvType = (int)OpenMetaverse.InventoryType.Texture,
                            Name = "Item of Goodness",
                            NextPermissions = 0xF,
                            Owner = userId,
                            SalePrice = 100,
                            SaleType = 1
                        };

                        _storage.PurgeItem(item);
                        _storage.CreateItem(item);
                        _storage.PurgeItem(item);
                        _storage.CreateItem(item);
                        _storage.PurgeItem(item);

                        var ex =
                            Assert.Throws<InventoryObjectMissingException>(delegate()
                            {
                                InventoryItemBase itemCopy = _storage.GetItem(itemId, UUID.Zero);
                            });

                        Assert.AreEqual("Item was not found in the index", ex.ErrorDetails);
                    });
            }

            pool.WaitForIdle();

            InventoryFolderBase newFolder = _storage.GetFolder(folder1.ID);
            Assert.AreEqual(0, newFolder.Items.Count);
            Assert.AreEqual(5001, newFolder.Version);
        }

        [Test]
        public void TestPurgeCreateConsistent()
        {
            Amib.Threading.SmartThreadPool pool = new Amib.Threading.SmartThreadPool(1000, 20);
            pool.Start();

            var userId = UUID.Parse("01EAE367-3A88-48B2-A226-AB3234EE506B");

            InventoryFolderBase folder1 = new InventoryFolderBase();
            folder1.ID = UUID.Parse("F1EAE367-3A88-48B2-A226-AB3234EE506B");
            folder1.Name = "Folder1";
            folder1.Level = InventoryFolderBase.FolderLevel.Root;
            folder1.Owner = userId;
            folder1.ParentID = UUID.Zero;
            folder1.Type = (short)OpenMetaverse.FolderType.Root;

            try
            {
                _storage.GetFolderAttributes(folder1.ID);
            }
            catch (InventoryObjectMissingException e)
            {
                _storage.CreateFolder(folder1);
            }

            for (int i = 0; i < 100; i++)
            {
                UUID assetId = UUID.Random();
                UUID itemId = UUID.Random();
                InventoryItemBase item = new InventoryItemBase
                {
                    AssetID = assetId,
                    AssetType = (int)OpenMetaverse.AssetType.Texture,
                    BasePermissions = UInt32.MaxValue,
                    CreationDate = Util.UnixTimeSinceEpoch(),
                    CreatorId = userId.ToString(),
                    CurrentPermissions = unchecked((uint)-1),
                    Description = "A good item, of goodness",
                    EveryOnePermissions = Int32.MaxValue + (uint)1,
                    Flags = unchecked((uint)Int32.MinValue),
                    Folder = folder1.ID,
                    GroupID = UUID.Zero,
                    GroupOwned = false,
                    GroupPermissions = 0x123,
                    ID = itemId,
                    InvType = (int)OpenMetaverse.InventoryType.Texture,
                    Name = "Item of Goodness",
                    NextPermissions = 0xF,
                    Owner = userId,
                    SalePrice = 100,
                    SaleType = 1
                };
                pool.QueueWorkItem(() =>
                {
                    _storage.CreateItem(item);
                });

                pool.QueueWorkItem(() =>
                {
                    _storage.PurgeItem(item);
                });
                pool.QueueWorkItem(() =>
                {
                    _storage.CreateItem(item);
                });

                pool.QueueWorkItem(() =>
                {
                    _storage.PurgeItem(item);
                });

                pool.WaitForIdle();

                InventoryFolderBase newFolder = _storage.GetFolder(folder1.ID);
                //either the item should completely exist or not
                if (newFolder.Items.Exists((InventoryItemBase litem) => { return litem.ID == itemId; }))
                {
                    Assert.DoesNotThrow(delegate()
                    {
                        InventoryItemBase itemCopy = _storage.GetItem(itemId, UUID.Zero);
                    });

                    //cleanup
                    _storage.PurgeItem(item);
                }
                else
                {
                    var ex =
                        Assert.Throws<InventoryObjectMissingException>(delegate()
                        {
                            InventoryItemBase itemCopy = _storage.GetItem(itemId, UUID.Zero);
                        });

                    Assert.AreEqual("Item was not found in the index", ex.ErrorDetails);
                }
            }

            InventoryFolderBase finalFolder = _storage.GetFolder(folder1.ID);
            Assert.AreEqual(0, finalFolder.Items.Count);
        }

        [TestCase]
        public void TestItemMove()
        {
            UUID userId = UUID.Random();

            InventoryFolderBase folder1 = new InventoryFolderBase();
            folder1.ID = UUID.Random();
            folder1.Name = "Folder1";
            folder1.Level = InventoryFolderBase.FolderLevel.Root;
            folder1.Owner = userId;
            folder1.ParentID = UUID.Zero;
            folder1.Type = (short)OpenMetaverse.FolderType.Root;

            _storage.CreateFolder(folder1);


            InventoryFolderBase folder2 = new InventoryFolderBase();
            folder2.ID = UUID.Random();
            folder2.Name = "Folder2";
            folder2.Level = InventoryFolderBase.FolderLevel.Root;
            folder2.Owner = userId;
            folder2.ParentID = UUID.Zero;
            folder2.Type = (short)OpenMetaverse.FolderType.Root;

            _storage.CreateFolder(folder1);
            _storage.CreateFolder(folder2);

            UUID assetId = UUID.Random();
            UUID itemId = UUID.Random();
            InventoryItemBase item = new InventoryItemBase
            {
                AssetID = assetId,
                AssetType = (int)OpenMetaverse.AssetType.Texture,
                BasePermissions = UInt32.MaxValue,
                CreationDate = Util.UnixTimeSinceEpoch(),
                CreatorId = userId.ToString(),
                CurrentPermissions = unchecked((uint)-1),
                Description = "A good item, of goodness",
                EveryOnePermissions = Int32.MaxValue + (uint)1,
                Flags = unchecked((uint)Int32.MinValue),
                Folder = folder1.ID,
                GroupID = UUID.Zero,
                GroupOwned = false,
                GroupPermissions = 0x123,
                ID = itemId,
                InvType = (int)OpenMetaverse.InventoryType.Texture,
                Name = "Item of Goodness",
                NextPermissions = 0xF,
                Owner = userId,
                SalePrice = 100,
                SaleType = 1
            };

            _storage.CreateItem(item);
            _storage.MoveItem(item, folder2);


            InventoryFolderBase newFolder1 = _storage.GetFolder(folder1.ID);
            InventoryFolderBase newFolder2 = _storage.GetFolder(folder2.ID);

            AssertFolderEqual(folder1, newFolder1, true);
            AssertFolderEqual(folder2, newFolder2, true);

            Assert.AreEqual(0, newFolder1.Items.Count);
            Assert.AreEqual(1, newFolder2.Items.Count);

            InventoryItemBase newItem = _storage.GetItem(item.ID, UUID.Zero);
            item.Folder = newFolder2.ID;

            AssertItemEqual(item, newItem);

        }

        [TestCase]
        public void TestSendItemToTrash()
        {
            UUID userId = UUID.Random();

            InventoryFolderBase rootFolder = new InventoryFolderBase();
            rootFolder.ID = UUID.Random();
            rootFolder.Name = "Root Folder";
            rootFolder.Level = InventoryFolderBase.FolderLevel.Root;
            rootFolder.Owner = userId;
            rootFolder.ParentID = UUID.Zero;
            rootFolder.Type = (short)OpenMetaverse.FolderType.Root;

            _storage.CreateFolder(rootFolder);

            InventoryFolderBase trashFolder = new InventoryFolderBase();
            trashFolder.ID = UUID.Random();
            trashFolder.Name = "Trash Folder";
            trashFolder.Level = InventoryFolderBase.FolderLevel.TopLevel;
            trashFolder.Owner = userId;
            trashFolder.ParentID = rootFolder.ID;
            trashFolder.Type = (short)OpenMetaverse.FolderType.Trash;

            _storage.CreateFolder(trashFolder);

            InventoryItemBase item = new InventoryItemBase
            {
                AssetID = UUID.Random(),
                AssetType = (int)OpenMetaverse.AssetType.Texture,
                BasePermissions = UInt32.MaxValue,
                CreationDate = Util.UnixTimeSinceEpoch(),
                CreatorId = userId.ToString(),
                CurrentPermissions = unchecked((uint)-1),
                Description = "A good item, of goodness",
                EveryOnePermissions = Int32.MaxValue + (uint)1,
                Flags = unchecked((uint)Int32.MinValue),
                Folder = rootFolder.ID,
                GroupID = UUID.Zero,
                GroupOwned = false,
                GroupPermissions = 0x123,
                ID = UUID.Random(),
                InvType = (int)OpenMetaverse.InventoryType.Texture,
                Name = "Item of Goodness",
                NextPermissions = 0xF,
                Owner = userId,
                SalePrice = 100,
                SaleType = 1
            };

            _storage.SendItemToTrash(item, UUID.Zero);

            InventoryItemBase trashedItem = _storage.GetItem(item.ID, UUID.Zero);
            Assert.AreEqual(trashedItem.Folder, trashFolder.ID);

            AssertItemEqual(item, trashedItem);


        }

        [TestCase]
        public void TestFolderPurgeContents()
        {
            UUID userId = UUID.Random();

            InventoryFolderBase rootFolder = new InventoryFolderBase();
            rootFolder.ID = UUID.Random();
            rootFolder.Name = "Root Folder";
            rootFolder.Level = InventoryFolderBase.FolderLevel.Root;
            rootFolder.Owner = userId;
            rootFolder.ParentID = UUID.Zero;
            rootFolder.Type = (short)OpenMetaverse.FolderType.Root;

            _storage.CreateFolder(rootFolder);

            InventoryFolderBase trashFolder = new InventoryFolderBase();
            trashFolder.ID = UUID.Random();
            trashFolder.Name = "Trash Folder";
            trashFolder.Level = InventoryFolderBase.FolderLevel.TopLevel;
            trashFolder.Owner = userId;
            trashFolder.ParentID = rootFolder.ID;
            trashFolder.Type = (short)OpenMetaverse.FolderType.Trash;

            _storage.CreateFolder(trashFolder);

            //generate 50 folders with 500 items in them
            List<InventoryFolderBase> folders = new List<InventoryFolderBase>();
            List<InventoryItemBase> items = new List<InventoryItemBase>();
            Random r = new Random();

            for (int i = 0; i < 50; i++)
            {
                InventoryFolderBase folder = new InventoryFolderBase();
                folder.ID = UUID.Random();
                folder.Name = "RandomFolder" + i.ToString();
                folder.Level = InventoryFolderBase.FolderLevel.Leaf;
                folder.Owner = userId;
                folder.Type = (short)OpenMetaverse.FolderType.Trash;

                int index = r.Next(-1, folders.Count - 1);
                if (index == -1)
                {
                    folder.ParentID = trashFolder.ID;
                }
                else
                {
                    folder.ParentID = folders[index].ID;
                }

                folders.Add(folder);
            }

            for (int i = 0; i < 200; i++)
            {
                InventoryItemBase item = new InventoryItemBase
                {
                    AssetID = UUID.Random(),
                    AssetType = (int)OpenMetaverse.AssetType.Texture,
                    BasePermissions = UInt32.MaxValue,
                    CreationDate = Util.UnixTimeSinceEpoch(),
                    CreatorId = userId.ToString(),
                    CurrentPermissions = unchecked((uint)-1),
                    Description = "A good item, of goodness",
                    EveryOnePermissions = Int32.MaxValue + (uint)1,
                    Flags = unchecked((uint)Int32.MinValue),
                    GroupID = UUID.Zero,
                    GroupOwned = false,
                    GroupPermissions = 0x123,
                    ID = UUID.Random(),
                    InvType = (int)OpenMetaverse.InventoryType.Texture,
                    Name = "RandomItem" + i.ToString(),
                    NextPermissions = 0xF,
                    Owner = userId,
                    SalePrice = 100,
                    SaleType = 1
                };

                int index = r.Next(-1, folders.Count - 1);

                if (index == -1)
                {
                    item.Folder = trashFolder.ID;
                }
                else
                {
                    item.Folder = folders[index].ID;
                }

                items.Add(item);
            }

            foreach (InventoryFolderBase folder in folders)
            {
                _storage.CreateFolder(folder);
            }

            foreach (InventoryItemBase item in items)
            {
                _storage.CreateItem(item);
            }

            //verify the trash folder is now full of stuff
            InventoryFolderBase fullTrash = _storage.GetFolder(trashFolder.ID);

            Assert.That(fullTrash.SubFolders.Count > 0);

            //make sure all folders are accounted for
            List<InventoryFolderBase> skel = _storage.GetInventorySkeleton(userId);
            Assert.AreEqual(52, skel.Count);

            //do some raw queries to verify we have good indexing happening
            foreach (InventoryItemBase item in items)
            {
                Guid parent = _storage.FindItemParentFolderId(item.ID);
                Assert.AreEqual(parent, item.Folder.Guid);
            }

            //purge the trash
            _storage.PurgeFolderContents(trashFolder);

            //verify the index is empty except for the trash and the root
            skel = _storage.GetInventorySkeleton(userId);
            Assert.AreEqual(2, skel.Count);

            //verify none of the items are accessable anymore
            foreach (InventoryItemBase item in items)
            {
                Assert.Throws<InventoryObjectMissingException>(delegate()
                {
                    _storage.GetItem(item.ID, UUID.Zero);
                });

                Assert.Throws<InventoryObjectMissingException>(delegate()
                {
                    _storage.GetItem(item.ID, item.Folder);
                });
            }

            //verify the root folder doesn't show any items or subfolders
            InventoryFolderBase emptyTrash = _storage.GetFolder(trashFolder.ID);

            Assert.AreEqual(0, emptyTrash.Items.Count);
            Assert.AreEqual(0, emptyTrash.SubFolders.Count);
        }

        [TestCase]
        public void TestFolderIndexPagination()
        {
            TestFolderIndexPagination(1);
            TestFolderIndexPagination(1023);
            TestFolderIndexPagination(1024);
            TestFolderIndexPagination(1025);
            TestFolderIndexPagination(2047);
            TestFolderIndexPagination(2048);
            TestFolderIndexPagination(2049);
        }

        private void TestFolderIndexPagination(int FOLDER_COUNT)
        {
            UUID userId = UUID.Random();

            List<InventoryFolderBase> folders = new List<InventoryFolderBase>(FOLDER_COUNT);
            for (int i = 0; i < FOLDER_COUNT; i++)
            {
                InventoryFolderBase folder = new InventoryFolderBase();
                folder.ID = UUID.Random();
                folder.Name = "RandomFolder" + i.ToString();
                folder.Level = InventoryFolderBase.FolderLevel.TopLevel;
                folder.Owner = userId;
                folder.Type = (short)OpenMetaverse.AssetType.Unknown;
                folder.ParentID = UUID.Zero;

                _storage.CreateFolder(folder);

                folders.Add(folder);

                //add an item to the folder so that its version is 2
                InventoryItemBase item = new InventoryItemBase
                {
                    AssetID = UUID.Random(),
                    AssetType = (int)OpenMetaverse.AssetType.Texture,
                    BasePermissions = UInt32.MaxValue,
                    CreationDate = Util.UnixTimeSinceEpoch(),
                    CreatorId = userId.ToString(),
                    CurrentPermissions = unchecked((uint)-1),
                    Description = "A good item, of goodness",
                    EveryOnePermissions = Int32.MaxValue + (uint)1,
                    Flags = unchecked((uint)Int32.MinValue),
                    GroupID = UUID.Zero,
                    GroupOwned = false,
                    GroupPermissions = 0x123,
                    ID = UUID.Random(),
                    InvType = (int)OpenMetaverse.InventoryType.Texture,
                    Name = "RandomItem" + i.ToString(),
                    NextPermissions = 0xF,
                    Owner = userId,
                    SalePrice = 100,
                    SaleType = 1,
                    Folder = folder.ID
                };

                _storage.CreateItem(item);
            }

            List<InventoryFolderBase> skel = _storage.GetInventorySkeleton(userId);
            Assert.AreEqual(FOLDER_COUNT, skel.Count);

            Dictionary<UUID, InventoryFolderBase> indexOfFolders = new Dictionary<UUID, InventoryFolderBase>();
            foreach (InventoryFolderBase folder in skel)
            {
                indexOfFolders.Add(folder.ID, folder);
                Assert.AreEqual(2, folder.Version);
            }

            foreach (InventoryFolderBase folder in folders)
            {
                Assert.That(indexOfFolders.ContainsKey(folder.ID));

                AssertFolderEqual(folder, indexOfFolders[folder.ID], true);
            }
        }

        [TestCase]
        public void TestSinglePurgeFolder()
        {
            UUID userId = UUID.Random();

            InventoryFolderBase parentFolder = new InventoryFolderBase();
            parentFolder.ID = UUID.Random();
            parentFolder.Name = "RootFolder";
            parentFolder.Level = InventoryFolderBase.FolderLevel.Root;
            parentFolder.Owner = userId;
            parentFolder.Type = (short)OpenMetaverse.FolderType.Root;
            parentFolder.ParentID = UUID.Zero;

            InventoryFolderBase folder = new InventoryFolderBase();
            folder.ID = UUID.Random();
            folder.Name = "RandomFolder";
            folder.Level = InventoryFolderBase.FolderLevel.TopLevel;
            folder.Owner = userId;
            folder.Type = (short)OpenMetaverse.AssetType.Unknown;
            folder.ParentID = parentFolder.ID;

            _storage.CreateFolder(parentFolder);
            _storage.CreateFolder(folder);

            //System.Threading.Thread.Sleep(30);

            _storage.PurgeFolder(folder);

            List<InventoryFolderBase> skel = _storage.GetInventorySkeleton(userId);
            Assert.AreEqual(1, skel.Count);

            Assert.Throws<InventoryObjectMissingException>(delegate()
            {
                _storage.GetFolder(folder.ID);
            });

            InventoryFolderBase updatedParent = _storage.GetFolder(parentFolder.ID);
            Assert.AreEqual(0, updatedParent.SubFolders.Count);
        }

        [TestCase]
        public void TestMultiPurgeFolder()
        {
            const int FOLDER_COUNT = 20;

            UUID userId = UUID.Random();

            List<InventoryFolderBase> folders = new List<InventoryFolderBase>(FOLDER_COUNT);
            for (int i = 0; i < FOLDER_COUNT; i++)
            {
                InventoryFolderBase folder = new InventoryFolderBase();
                folder.ID = UUID.Random();
                folder.Name = "RandomFolder" + i.ToString();
                folder.Level = InventoryFolderBase.FolderLevel.TopLevel;
                folder.Owner = userId;
                folder.Type = (short)OpenMetaverse.AssetType.Unknown;
                folder.ParentID = UUID.Zero;

                _storage.CreateFolder(folder);

                folders.Add(folder);
            }

            System.Threading.Thread.Sleep(30);

            _storage.PurgeFolders(folders);

            List<InventoryFolderBase> skel = _storage.GetInventorySkeleton(userId);
            Assert.AreEqual(0, skel.Count);

            foreach (InventoryFolderBase folder in folders)
            {
                Assert.Throws<InventoryObjectMissingException>(delegate()
                {
                    _storage.GetFolder(folder.ID);
                });
            }
        }

        [TestCase]
        public void TestGestureTracking()
        {
            const int NUM_GESTURES = 100;

            List<InventoryItemBase> gestures = new List<InventoryItemBase>();
            Dictionary<UUID, InventoryItemBase> gestureIndex = new Dictionary<UUID, InventoryItemBase>();
            UUID userId = UUID.Random();

            InventoryFolderBase folder = new InventoryFolderBase();
            folder.ID = UUID.Random();
            folder.Name = "RandomFolder";
            folder.Level = InventoryFolderBase.FolderLevel.TopLevel;
            folder.Owner = userId;
            folder.Type = (short)OpenMetaverse.AssetType.Unknown;
            folder.ParentID = UUID.Zero;

            _storage.CreateFolder(folder);

            for (int i = 0; i < NUM_GESTURES; i++)
            {
                InventoryItemBase item = new InventoryItemBase
                {
                    AssetID = UUID.Random(),
                    AssetType = (int)OpenMetaverse.AssetType.Texture,
                    BasePermissions = UInt32.MaxValue,
                    CreationDate = Util.UnixTimeSinceEpoch(),
                    CreatorId = userId.ToString(),
                    CurrentPermissions = unchecked((uint)-1),
                    Description = "Gesture",
                    EveryOnePermissions = Int32.MaxValue + (uint)1,
                    Flags = unchecked((uint)Int32.MinValue),
                    GroupID = UUID.Zero,
                    GroupOwned = false,
                    GroupPermissions = 0x123,
                    ID = UUID.Random(),
                    InvType = (int)OpenMetaverse.InventoryType.Gesture,
                    Name = "RandomItem" + i.ToString(),
                    NextPermissions = 0xF,
                    Owner = userId,
                    Folder = folder.ID
                };

                gestures.Add(item);
                gestureIndex.Add(item.ID, item);
                _storage.CreateItem(item);
            }

            _storage.ActivateGestures(userId, (from item in gestures select item.ID));

            //make sure all the gestures show active
            List<InventoryItemBase> items = _storage.GetActiveGestureItems(userId);
            Assert.AreEqual(NUM_GESTURES, items.Count);

            foreach (InventoryItemBase item in items)
            {
                Assert.That(gestureIndex.ContainsKey(item.ID));
                AssertItemEqual(gestureIndex[item.ID], item);
                gestureIndex.Remove(item.ID);
            }

            Assert.AreEqual(0, gestureIndex.Count);

            _storage.DeactivateGestures(userId, (from item in gestures select item.ID));
            items = _storage.GetActiveGestureItems(userId);
            Assert.AreEqual(0, items.Count);
        }

        private void AssertItemEqual(InventoryItemBase i1, InventoryItemBase i2)
        {
            Assert.AreEqual(i1.AssetID, i2.AssetID);
            Assert.AreEqual(i1.AssetType, i2.AssetType);
            Assert.AreEqual(i1.BasePermissions, i2.BasePermissions);
            Assert.AreEqual(i1.ContainsMultipleItems, i2.ContainsMultipleItems);
            Assert.AreEqual(i1.CreationDate, i2.CreationDate);
            Assert.AreEqual(i1.CreatorId, i2.CreatorId);
            Assert.AreEqual(i1.CreatorIdAsUuid, i2.CreatorIdAsUuid);
            Assert.AreEqual(i1.CurrentPermissions, i2.CurrentPermissions);
            Assert.AreEqual(i1.Description, i2.Description);
            Assert.AreEqual(i1.EveryOnePermissions, i2.EveryOnePermissions);
            Assert.AreEqual(i1.Flags, i2.Flags);
            Assert.AreEqual(i1.Folder, i2.Folder);
            Assert.AreEqual(i1.GroupID, i2.GroupID);
            Assert.AreEqual(i1.GroupOwned, i2.GroupOwned);
            Assert.AreEqual(i1.GroupPermissions, i2.GroupPermissions);
            Assert.AreEqual(i1.ID, i2.ID);
            Assert.AreEqual(i1.InvType, i2.InvType);
            Assert.AreEqual(i1.Name, i2.Name);
            Assert.AreEqual(i1.NextPermissions, i2.NextPermissions);
            Assert.AreEqual(i1.Owner, i2.Owner);
            Assert.AreEqual(i1.SalePrice, i2.SalePrice);
            Assert.AreEqual(i1.SaleType, i2.SaleType);
        }

        [TestCase]
        public void TestInvalidSubfolderIndexCleanup()
        {
            UUID userId = UUID.Random();

            InventoryFolderBase parentFolder = new InventoryFolderBase();
            parentFolder.ID = UUID.Random();
            parentFolder.Name = "RootFolder";
            parentFolder.Level = InventoryFolderBase.FolderLevel.Root;
            parentFolder.Owner = userId;
            parentFolder.Type = (short)OpenMetaverse.FolderType.Root;
            parentFolder.ParentID = UUID.Zero;

            InventoryFolderBase folder = new InventoryFolderBase();
            folder.ID = UUID.Random();
            folder.Name = "RandomFolder";
            folder.Level = InventoryFolderBase.FolderLevel.TopLevel;
            folder.Owner = userId;
            folder.Type = (short)OpenMetaverse.AssetType.Unknown;
            folder.ParentID = parentFolder.ID;

            _storage.CreateFolder(parentFolder);
            _storage.CreateFolder(folder);

            InventoryFolderBase validChild = new InventoryFolderBase();
            validChild.ID = UUID.Random();
            validChild.Name = "ValidChild";
            validChild.Level = InventoryFolderBase.FolderLevel.Leaf;
            validChild.Owner = userId;
            validChild.Type = (short)OpenMetaverse.AssetType.Unknown;
            validChild.ParentID = folder.ID;

            _storage.CreateFolder(validChild);

            InventoryItemBase item = new InventoryItemBase
            {
                AssetID = UUID.Random(),
                AssetType = (int)OpenMetaverse.AssetType.Texture,
                BasePermissions = UInt32.MaxValue,
                CreationDate = Util.UnixTimeSinceEpoch(),
                CreatorId = userId.ToString(),
                CurrentPermissions = unchecked((uint)-1),
                Description = "Gesture",
                EveryOnePermissions = Int32.MaxValue + (uint)1,
                Flags = unchecked((uint)Int32.MinValue),
                GroupID = UUID.Zero,
                GroupOwned = false,
                GroupPermissions = 0x123,
                ID = UUID.Random(),
                InvType = (int)OpenMetaverse.InventoryType.Gesture,
                Name = "RandomItem",
                NextPermissions = 0xF,
                Owner = userId,
                Folder = validChild.ID
            };

            _storage.SaveItem(item);

            InventoryFolderBase invalidChild = new InventoryFolderBase();
            invalidChild.ID = UUID.Random();
            invalidChild.Name = "InvalidChild";
            invalidChild.Level = InventoryFolderBase.FolderLevel.Leaf;
            folder.Owner = userId;
            folder.Type = (short)OpenMetaverse.AssetType.Unknown;
            folder.ParentID = folder.ID;

            _storage.UpdateParentWithNewChild(invalidChild, folder.ID.Guid, Guid.Empty, Util.UnixTimeSinceEpochInMicroseconds());

            

            //reread the folder
            folder = _storage.GetFolder(folder.ID);

            //ensure that the dud link exists
            Assert.That(folder.SubFolders.Count == 2);

            foreach (var subfolder in folder.SubFolders)
            {
                Assert.IsTrue(subfolder.ID == invalidChild.ID || subfolder.ID == validChild.ID);
            }

            //Run a repair
            _storage.Maint_RepairSubfolderIndexes(userId);

            //verify we got rid of the dud link, but everything else is in tact
            folder = _storage.GetFolder(folder.ID);

            Assert.That(folder.SubFolders.Count == 1);
            Assert.AreEqual(folder.SubFolders.First().ID, validChild.ID);

            validChild = _storage.GetFolder(validChild.ID);

            Assert.AreEqual(validChild.Name, "ValidChild");
            Assert.That(validChild.Items.Count == 1);

            Assert.AreEqual(validChild.Items[0].Name, "RandomItem");
        }
    }   
}
