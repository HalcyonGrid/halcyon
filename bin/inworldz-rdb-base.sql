-- MySQL dump 10.13  Distrib 5.6.24, for Win64 (x86_64)
--
-- Host: 10.0.2.140    Database: inworldz_rdb
-- ------------------------------------------------------
-- Server version	5.1.73-log

/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
/*!40101 SET @OLD_CHARACTER_SET_RESULTS=@@CHARACTER_SET_RESULTS */;
/*!40101 SET @OLD_COLLATION_CONNECTION=@@COLLATION_CONNECTION */;
/*!40101 SET NAMES utf8 */;
/*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */;
/*!40103 SET TIME_ZONE='+00:00' */;
/*!40014 SET @OLD_UNIQUE_CHECKS=@@UNIQUE_CHECKS, UNIQUE_CHECKS=0 */;
/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;
/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;
/*!40111 SET @OLD_SQL_NOTES=@@SQL_NOTES, SQL_NOTES=0 */;

--
-- Table structure for table `land`
--

DROP TABLE IF EXISTS `land`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8 */;
CREATE TABLE `land` (
  `UUID` varchar(255) NOT NULL,
  `RegionUUID` varchar(255) DEFAULT NULL,
  `LocalLandID` int(11) DEFAULT NULL,
  `Bitmap` longblob,
  `Name` varchar(255) DEFAULT NULL,
  `Description` varchar(254) DEFAULT NULL,
  `OwnerUUID` varchar(255) DEFAULT NULL,
  `IsGroupOwned` int(11) DEFAULT NULL,
  `Area` int(11) DEFAULT NULL,
  `AuctionID` int(11) DEFAULT NULL,
  `Category` int(11) DEFAULT NULL,
  `ClaimDate` int(11) DEFAULT NULL,
  `ClaimPrice` int(11) DEFAULT NULL,
  `GroupUUID` varchar(255) DEFAULT NULL,
  `SalePrice` int(11) DEFAULT NULL,
  `LandStatus` int(11) DEFAULT NULL,
  `LandFlags` int(11) unsigned DEFAULT NULL,
  `LandingType` int(11) DEFAULT NULL,
  `MediaAutoScale` int(11) DEFAULT NULL,
  `MediaTextureUUID` varchar(255) DEFAULT NULL,
  `MediaURL` varchar(255) DEFAULT NULL,
  `MusicURL` varchar(255) DEFAULT NULL,
  `PassHours` float DEFAULT NULL,
  `PassPrice` int(11) DEFAULT NULL,
  `SnapshotUUID` varchar(255) DEFAULT NULL,
  `UserLocationX` float DEFAULT NULL,
  `UserLocationY` float DEFAULT NULL,
  `UserLocationZ` float DEFAULT NULL,
  `UserLookAtX` float DEFAULT NULL,
  `UserLookAtY` float DEFAULT NULL,
  `UserLookAtZ` float DEFAULT NULL,
  `AuthbuyerID` varchar(36) NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
  `OtherCleanTime` int(11) NOT NULL DEFAULT '0',
  `Dwell` int(11) NOT NULL DEFAULT '0',
  PRIMARY KEY (`UUID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `landaccesslist`
--

DROP TABLE IF EXISTS `landaccesslist`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8 */;
CREATE TABLE `landaccesslist` (
  `LandUUID` varchar(255) DEFAULT NULL,
  `AccessUUID` varchar(255) DEFAULT NULL,
  `Flags` int(11) DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `migrations`
--

DROP TABLE IF EXISTS `migrations`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8 */;
CREATE TABLE `migrations` (
  `name` varchar(100) DEFAULT NULL,
  `version` int(11) DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `primitems`
--

DROP TABLE IF EXISTS `primitems`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8 */;
CREATE TABLE `primitems` (
  `invType` int(11) DEFAULT NULL,
  `assetType` int(11) DEFAULT NULL,
  `name` varchar(255) DEFAULT NULL,
  `description` varchar(255) DEFAULT NULL,
  `creationDate` bigint(20) DEFAULT NULL,
  `nextPermissions` int(11) DEFAULT NULL,
  `currentPermissions` int(11) DEFAULT NULL,
  `basePermissions` int(11) DEFAULT NULL,
  `everyonePermissions` int(11) DEFAULT NULL,
  `groupPermissions` int(11) DEFAULT NULL,
  `flags` int(11) NOT NULL DEFAULT '0',
  `itemID` char(36) NOT NULL DEFAULT '',
  `primID` char(36) DEFAULT NULL,
  `assetID` char(36) DEFAULT NULL,
  `parentFolderID` char(36) DEFAULT NULL,
  `creatorID` char(36) DEFAULT NULL,
  `ownerID` char(36) DEFAULT NULL,
  `groupID` char(36) DEFAULT NULL,
  `lastOwnerID` char(36) DEFAULT NULL,
  `canDebitOwner` tinyint(1) NOT NULL DEFAULT '0',
  PRIMARY KEY (`itemID`),
  KEY `primitems_primid` (`primID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `prims`
--

DROP TABLE IF EXISTS `prims`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8 */;
CREATE TABLE `prims` (
  `CreationDate` int(11) DEFAULT NULL,
  `Name` varchar(255) DEFAULT NULL,
  `Text` varchar(255) DEFAULT NULL,
  `Description` varchar(255) DEFAULT NULL,
  `SitName` varchar(255) DEFAULT NULL,
  `TouchName` varchar(255) DEFAULT NULL,
  `ObjectFlags` int(11) DEFAULT NULL,
  `OwnerMask` int(11) DEFAULT NULL,
  `NextOwnerMask` int(11) DEFAULT NULL,
  `GroupMask` int(11) DEFAULT NULL,
  `EveryoneMask` int(11) DEFAULT NULL,
  `BaseMask` int(11) DEFAULT NULL,
  `PositionX` double DEFAULT NULL,
  `PositionY` double DEFAULT NULL,
  `PositionZ` double DEFAULT NULL,
  `GroupPositionX` double DEFAULT NULL,
  `GroupPositionY` double DEFAULT NULL,
  `GroupPositionZ` double DEFAULT NULL,
  `VelocityX` double DEFAULT NULL,
  `VelocityY` double DEFAULT NULL,
  `VelocityZ` double DEFAULT NULL,
  `AngularVelocityX` double DEFAULT NULL,
  `AngularVelocityY` double DEFAULT NULL,
  `AngularVelocityZ` double DEFAULT NULL,
  `AccelerationX` double DEFAULT NULL,
  `AccelerationY` double DEFAULT NULL,
  `AccelerationZ` double DEFAULT NULL,
  `RotationX` double DEFAULT NULL,
  `RotationY` double DEFAULT NULL,
  `RotationZ` double DEFAULT NULL,
  `RotationW` double DEFAULT NULL,
  `SitTargetOffsetX` double DEFAULT NULL,
  `SitTargetOffsetY` double DEFAULT NULL,
  `SitTargetOffsetZ` double DEFAULT NULL,
  `SitTargetOrientW` double DEFAULT NULL,
  `SitTargetOrientX` double DEFAULT NULL,
  `SitTargetOrientY` double DEFAULT NULL,
  `SitTargetOrientZ` double DEFAULT NULL,
  `UUID` char(36) NOT NULL DEFAULT '',
  `RegionUUID` char(36) DEFAULT NULL,
  `CreatorID` char(36) DEFAULT NULL,
  `OwnerID` char(36) DEFAULT NULL,
  `GroupID` char(36) DEFAULT NULL,
  `LastOwnerID` char(36) DEFAULT NULL,
  `SceneGroupID` char(36) DEFAULT NULL,
  `PayPrice` int(11) NOT NULL DEFAULT '0',
  `PayButton1` int(11) NOT NULL DEFAULT '0',
  `PayButton2` int(11) NOT NULL DEFAULT '0',
  `PayButton3` int(11) NOT NULL DEFAULT '0',
  `PayButton4` int(11) NOT NULL DEFAULT '0',
  `LoopedSound` char(36) NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
  `LoopedSoundGain` double NOT NULL DEFAULT '0',
  `TextureAnimation` blob,
  `OmegaX` double NOT NULL DEFAULT '0',
  `OmegaY` double NOT NULL DEFAULT '0',
  `OmegaZ` double NOT NULL DEFAULT '0',
  `CameraEyeOffsetX` double NOT NULL DEFAULT '0',
  `CameraEyeOffsetY` double NOT NULL DEFAULT '0',
  `CameraEyeOffsetZ` double NOT NULL DEFAULT '0',
  `CameraAtOffsetX` double NOT NULL DEFAULT '0',
  `CameraAtOffsetY` double NOT NULL DEFAULT '0',
  `CameraAtOffsetZ` double NOT NULL DEFAULT '0',
  `ForceMouselook` tinyint(4) NOT NULL DEFAULT '0',
  `ScriptAccessPin` int(11) NOT NULL DEFAULT '0',
  `AllowedDrop` tinyint(4) NOT NULL DEFAULT '0',
  `DieAtEdge` tinyint(4) NOT NULL DEFAULT '0',
  `SalePrice` int(11) NOT NULL DEFAULT '10',
  `SaleType` tinyint(4) NOT NULL DEFAULT '0',
  `ColorR` int(11) NOT NULL DEFAULT '0',
  `ColorG` int(11) NOT NULL DEFAULT '0',
  `ColorB` int(11) NOT NULL DEFAULT '0',
  `ColorA` int(11) NOT NULL DEFAULT '0',
  `ParticleSystem` blob,
  `ClickAction` tinyint(4) NOT NULL DEFAULT '0',
  `Material` tinyint(4) NOT NULL DEFAULT '3',
  `CollisionSound` char(36) NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
  `CollisionSoundVolume` double NOT NULL DEFAULT '0',
  `LinkNumber` int(11) NOT NULL DEFAULT '0',
  `PassTouches` tinyint(1) NOT NULL DEFAULT '0',
  `ServerWeight` double DEFAULT '0',
  `StreamingCost` double DEFAULT '0',
  `KeyframeAnimation` blob,
  `ServerFlags` int(11) NOT NULL DEFAULT '0',
  PRIMARY KEY (`UUID`),
  KEY `prims_regionuuid` (`RegionUUID`),
  KEY `prims_scenegroupid` (`SceneGroupID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `primshapes`
--

DROP TABLE IF EXISTS `primshapes`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8 */;
CREATE TABLE `primshapes` (
  `Shape` int(11) DEFAULT NULL,
  `ScaleX` double NOT NULL DEFAULT '0',
  `ScaleY` double NOT NULL DEFAULT '0',
  `ScaleZ` double NOT NULL DEFAULT '0',
  `PCode` int(11) DEFAULT NULL,
  `PathBegin` int(11) DEFAULT NULL,
  `PathEnd` int(11) DEFAULT NULL,
  `PathScaleX` int(11) DEFAULT NULL,
  `PathScaleY` int(11) DEFAULT NULL,
  `PathShearX` int(11) DEFAULT NULL,
  `PathShearY` int(11) DEFAULT NULL,
  `PathSkew` int(11) DEFAULT NULL,
  `PathCurve` int(11) DEFAULT NULL,
  `PathRadiusOffset` int(11) DEFAULT NULL,
  `PathRevolutions` int(11) DEFAULT NULL,
  `PathTaperX` int(11) DEFAULT NULL,
  `PathTaperY` int(11) DEFAULT NULL,
  `PathTwist` int(11) DEFAULT NULL,
  `PathTwistBegin` int(11) DEFAULT NULL,
  `ProfileBegin` int(11) DEFAULT NULL,
  `ProfileEnd` int(11) DEFAULT NULL,
  `ProfileCurve` int(11) DEFAULT NULL,
  `ProfileHollow` int(11) DEFAULT NULL,
  `State` int(11) DEFAULT NULL,
  `Texture` longblob,
  `ExtraParams` longblob,
  `Media` longblob,
  `Materials` longblob,
  `UUID` char(36) NOT NULL DEFAULT '',
  `PhysicsData` blob,
  `PreferredPhysicsShape` tinyint(4) NOT NULL DEFAULT '0',
  `VertexCount` int(11) DEFAULT NULL,
  `HighLODBytes` int(11) DEFAULT NULL,
  `MidLODBytes` int(11) DEFAULT NULL,
  `LowLODBytes` int(11) DEFAULT NULL,
  `LowestLODBytes` int(11) DEFAULT NULL,
  PRIMARY KEY (`UUID`),
  KEY `IDX_ATTACHMENT` (`PCode`,`State`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `regionenvironment`
--

DROP TABLE IF EXISTS `regionenvironment`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8 */;
CREATE TABLE `regionenvironment` (
  `regionUUID` varchar(36) NOT NULL DEFAULT '000000-0000-0000-0000-000000000000',
  `llsd_text` text NOT NULL,
  PRIMARY KEY (`regionUUID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `regionsettings`
--

DROP TABLE IF EXISTS `regionsettings`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8 */;
CREATE TABLE `regionsettings` (
  `regionUUID` char(36) NOT NULL,
  `block_terraform` int(11) NOT NULL,
  `block_fly` int(11) NOT NULL,
  `allow_damage` int(11) NOT NULL,
  `restrict_pushing` int(11) NOT NULL,
  `allow_land_resell` int(11) NOT NULL,
  `allow_land_join_divide` int(11) NOT NULL,
  `block_show_in_search` int(11) NOT NULL,
  `agent_limit` int(11) NOT NULL,
  `object_bonus` double NOT NULL,
  `maturity` int(11) NOT NULL,
  `disable_scripts` int(11) NOT NULL,
  `disable_collisions` int(11) NOT NULL,
  `disable_physics` int(11) NOT NULL,
  `terrain_texture_1` char(36) NOT NULL,
  `terrain_texture_2` char(36) NOT NULL,
  `terrain_texture_3` char(36) NOT NULL,
  `terrain_texture_4` char(36) NOT NULL,
  `elevation_1_nw` double NOT NULL,
  `elevation_2_nw` double NOT NULL,
  `elevation_1_ne` double NOT NULL,
  `elevation_2_ne` double NOT NULL,
  `elevation_1_se` double NOT NULL,
  `elevation_2_se` double NOT NULL,
  `elevation_1_sw` double NOT NULL,
  `elevation_2_sw` double NOT NULL,
  `water_height` double NOT NULL,
  `terrain_raise_limit` double NOT NULL,
  `terrain_lower_limit` double NOT NULL,
  `use_estate_sun` int(11) NOT NULL,
  `fixed_sun` int(11) NOT NULL,
  `sun_position` double NOT NULL,
  `covenant` char(36) DEFAULT NULL,
  `Sandbox` tinyint(4) NOT NULL,
  `sunvectorx` double NOT NULL DEFAULT '0',
  `sunvectory` double NOT NULL DEFAULT '0',
  `sunvectorz` double NOT NULL DEFAULT '0',
  `covenantTimeStamp` int(11) unsigned NOT NULL DEFAULT '1262307600',
  PRIMARY KEY (`regionUUID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `terrain`
--

DROP TABLE IF EXISTS `terrain`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!40101 SET character_set_client = utf8 */;
CREATE TABLE `terrain` (
  `RegionUUID` varchar(255) NOT NULL DEFAULT '',
  `Revision` int(11) DEFAULT NULL,
  `Heightfield` longblob,
  PRIMARY KEY (`RegionUUID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8;
/*!40101 SET character_set_client = @saved_cs_client */;
/*!40103 SET TIME_ZONE=@OLD_TIME_ZONE */;

/*!40101 SET SQL_MODE=@OLD_SQL_MODE */;
/*!40014 SET FOREIGN_KEY_CHECKS=@OLD_FOREIGN_KEY_CHECKS */;
/*!40014 SET UNIQUE_CHECKS=@OLD_UNIQUE_CHECKS */;
/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40101 SET CHARACTER_SET_RESULTS=@OLD_CHARACTER_SET_RESULTS */;
/*!40101 SET COLLATION_CONNECTION=@OLD_COLLATION_CONNECTION */;
/*!40111 SET SQL_NOTES=@OLD_SQL_NOTES */;

-- Dump completed on 2015-09-28 12:14:00
