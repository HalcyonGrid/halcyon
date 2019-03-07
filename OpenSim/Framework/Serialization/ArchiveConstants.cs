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
using OpenMetaverse;

namespace OpenSim.Framework.Serialization
{
    /// <summary>
    /// Constants for the archiving module
    /// </summary>
    public class ArchiveConstants
    {
        /// <value>
        /// The location of the archive control file
        /// </value>
        public const string CONTROL_FILE_PATH = "archive.xml";

        /// <value>
        /// The location of the user list file.
        /// This file identifies all owner and creator IDs referenced in a region.
        /// </value>
        public const string USERLIST_FILE_PATH = "userlist.txt";

        /// <value>
        /// Path for the assets held in an archive
        /// </value>
        public const string ASSETS_PATH = "assets/";

        /// <value>
        /// Path for the inventory data
        /// </value>
        public const string INVENTORY_PATH = "inventory/";

        /// <value>
        /// Path for the prims file
        /// </value>
        public const string OBJECTS_PATH = "objects/";

        /// <value>
        /// Path for terrains.  Technically these may be assets, but I think it's quite nice to split them out.
        /// </value>
        public const string TERRAINS_PATH = "terrains/";

        /// <value>
        /// Path for region settings.
        /// </value>
        public const string SETTINGS_PATH = "settings/";
        
        /// <value>
        /// Path for user profiles
        /// </value>
        public const string USERS_PATH = "userprofiles/";

        /// <value>
        /// The character the separates the uuid from extension information in an archived asset filename
        /// </value>
        public const string ASSET_EXTENSION_SEPARATOR = "_";

        /// <value>
        /// Used to separate components in an inventory node name
        /// </value>
        public const string INVENTORY_NODE_NAME_COMPONENT_SEPARATOR = "__";

        /// <value>
        /// Extensions used for asset types in the archive
        /// </value>
        public static readonly IDictionary<sbyte, string> ASSET_TYPE_TO_EXTENSION = new Dictionary<sbyte, string>();
        public static readonly IDictionary<string, sbyte> EXTENSION_TO_ASSET_TYPE = new Dictionary<string, sbyte>();

        static ArchiveConstants()
        {
            ASSET_TYPE_TO_EXTENSION[(sbyte)AssetType.Animation]           = ASSET_EXTENSION_SEPARATOR + "animation.bvh";
            ASSET_TYPE_TO_EXTENSION[(sbyte)AssetType.Bodypart]            = ASSET_EXTENSION_SEPARATOR + "bodypart.txt";
            ASSET_TYPE_TO_EXTENSION[(sbyte)AssetType.CallingCard]         = ASSET_EXTENSION_SEPARATOR + "callingcard.txt";
            ASSET_TYPE_TO_EXTENSION[(sbyte)AssetType.Clothing]            = ASSET_EXTENSION_SEPARATOR + "clothing.txt";
            ASSET_TYPE_TO_EXTENSION[(sbyte)AssetType.Folder]              = ASSET_EXTENSION_SEPARATOR + "folder.txt";   // Not sure if we'll ever see this
            ASSET_TYPE_TO_EXTENSION[(sbyte)AssetType.Gesture]             = ASSET_EXTENSION_SEPARATOR + "gesture.txt";
            ASSET_TYPE_TO_EXTENSION[(sbyte)AssetType.ImageJPEG]           = ASSET_EXTENSION_SEPARATOR + "image.jpg";
            ASSET_TYPE_TO_EXTENSION[(sbyte)AssetType.ImageTGA]            = ASSET_EXTENSION_SEPARATOR + "image.tga";
            ASSET_TYPE_TO_EXTENSION[(sbyte)AssetType.Landmark]            = ASSET_EXTENSION_SEPARATOR + "landmark.txt";
            ASSET_TYPE_TO_EXTENSION[(sbyte)FolderType.LostAndFound]       = ASSET_EXTENSION_SEPARATOR + "lostandfoundfolder.txt";   // Not sure if we'll ever see this
            ASSET_TYPE_TO_EXTENSION[(sbyte)AssetType.LSLBytecode]         = ASSET_EXTENSION_SEPARATOR + "bytecode.lso";
            ASSET_TYPE_TO_EXTENSION[(sbyte)AssetType.LSLText]             = ASSET_EXTENSION_SEPARATOR + "script.lsl";
            ASSET_TYPE_TO_EXTENSION[(sbyte)AssetType.Mesh]                = ASSET_EXTENSION_SEPARATOR + "mesh.llmesh";
            ASSET_TYPE_TO_EXTENSION[(sbyte)AssetType.Notecard]            = ASSET_EXTENSION_SEPARATOR + "notecard.txt";
            ASSET_TYPE_TO_EXTENSION[(sbyte)AssetType.Object]              = ASSET_EXTENSION_SEPARATOR + "object.xml";
            ASSET_TYPE_TO_EXTENSION[(sbyte)FolderType.Root]               = ASSET_EXTENSION_SEPARATOR + "rootfolder.txt";   // Not sure if we'll ever see this
            ASSET_TYPE_TO_EXTENSION[(sbyte)AssetType.Simstate]            = ASSET_EXTENSION_SEPARATOR + "simstate.bin";   // Not sure if we'll ever see this
            ASSET_TYPE_TO_EXTENSION[(sbyte)FolderType.Snapshot]           = ASSET_EXTENSION_SEPARATOR + "snapshotfolder.txt";   // Not sure if we'll ever see this
            ASSET_TYPE_TO_EXTENSION[(sbyte)AssetType.Sound]               = ASSET_EXTENSION_SEPARATOR + "sound.ogg";
            ASSET_TYPE_TO_EXTENSION[(sbyte)AssetType.SoundWAV]            = ASSET_EXTENSION_SEPARATOR + "sound.wav";
            ASSET_TYPE_TO_EXTENSION[(sbyte)AssetType.Texture]             = ASSET_EXTENSION_SEPARATOR + "texture.jp2";
            ASSET_TYPE_TO_EXTENSION[(sbyte)AssetType.TextureTGA]          = ASSET_EXTENSION_SEPARATOR + "texture.tga";
            ASSET_TYPE_TO_EXTENSION[(sbyte)FolderType.Trash]              = ASSET_EXTENSION_SEPARATOR + "trashfolder.txt";   // Not sure if we'll ever see this

            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "animation.bvh"]            = (sbyte)AssetType.Animation;
            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "bodypart.txt"]             = (sbyte)AssetType.Bodypart;
            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "callingcard.txt"]          = (sbyte)AssetType.CallingCard;
            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "clothing.txt"]             = (sbyte)AssetType.Clothing;
            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "folder.txt"]               = (sbyte)AssetType.Folder;
            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "gesture.txt"]              = (sbyte)AssetType.Gesture;
            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "image.jpg"]                = (sbyte)AssetType.ImageJPEG;
            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "image.tga"]                = (sbyte)AssetType.ImageTGA;
            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "landmark.txt"]             = (sbyte)AssetType.Landmark;
            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "lostandfoundfolder.txt"]   = (sbyte)FolderType.LostAndFound;
            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "bytecode.lso"]             = (sbyte)AssetType.LSLBytecode;
            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "script.lsl"]               = (sbyte)AssetType.LSLText;
            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "mesh.llmesh"]              = (sbyte)AssetType.Mesh;
            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "notecard.txt"]             = (sbyte)AssetType.Notecard;
            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "object.xml"]               = (sbyte)AssetType.Object;
            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "rootfolder.txt"]           = (sbyte)FolderType.Root;
            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "simstate.bin"]             = (sbyte)AssetType.Simstate;
            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "snapshotfolder.txt"]       = (sbyte)FolderType.Snapshot;
            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "sound.ogg"]                = (sbyte)AssetType.Sound;
            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "sound.wav"]                = (sbyte)AssetType.SoundWAV;
            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "texture.jp2"]              = (sbyte)AssetType.Texture;
            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "texture.tga"]              = (sbyte)AssetType.TextureTGA;
            EXTENSION_TO_ASSET_TYPE[ASSET_EXTENSION_SEPARATOR + "trashfolder.txt"]          = (sbyte)FolderType.Trash;
        }
    }
}
