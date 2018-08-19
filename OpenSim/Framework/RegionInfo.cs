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
using System.Net;
using System.Net.Sockets;
using System.Xml;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using ProtoBuf;

namespace OpenSim.Framework
{
    public class SimpleRegionInfo : IEquatable<SimpleRegionInfo>
    {
        protected uint m_httpPort;
        public bool m_allow_alternate_ports;
        protected string m_externalHostName;
        protected IPEndPoint m_internalEndPoint;
        
        protected uint? m_regionLocX;
        protected uint? m_regionLocY;
        
        protected uint m_remotingPort;
        
        public UUID RegionID = UUID.Zero;

        public string RemotingAddress;

        private string m_outsideIP;


        public SimpleRegionInfo()
        {
        }

        public SimpleRegionInfo(SimpleRegionInfoSnapshot snap)
        {
            m_httpPort = snap.HttpPort;
            m_allow_alternate_ports = snap.AllowAlternatePorts;
            m_externalHostName = snap.ExternalHostName;
            m_internalEndPoint = new IPEndPoint(IPAddress.Parse(snap.IPEndPointAddress), snap.IPEndPointPort);
            m_regionLocX = snap.RegionLocX;
            m_regionLocY = snap.RegionLocY;
            m_remotingPort = snap.RemotingPort;
            RegionID = new UUID(snap.RegionID);
            RemotingAddress = snap.RemotingAddress;
            m_outsideIP = snap.OutsideIP;
        }

        public SimpleRegionInfo(uint regionLocX, uint regionLocY, IPEndPoint internalEndPoint, string externalUri)
        {
            m_regionLocX = regionLocX;
            m_regionLocY = regionLocY;

            m_internalEndPoint = internalEndPoint;
            m_externalHostName = externalUri;
        }

        public SimpleRegionInfo(uint regionLocX, uint regionLocY, string externalUri, uint port)
        {
            m_regionLocX = regionLocX;
            m_regionLocY = regionLocY;

            m_externalHostName = externalUri;

            m_internalEndPoint = new IPEndPoint(IPAddress.Parse("0.0.0.0"), (int) port);
        }

        public SimpleRegionInfo(RegionInfo ConvertFrom)
        {
            m_regionLocX = ConvertFrom.RegionLocX;
            m_regionLocY = ConvertFrom.RegionLocY;
            m_internalEndPoint = ConvertFrom.InternalEndPoint;
            m_externalHostName = ConvertFrom.ExternalHostName;
            m_remotingPort = ConvertFrom.RemotingPort;
            m_httpPort = ConvertFrom.HttpPort;
            m_allow_alternate_ports = ConvertFrom.m_allow_alternate_ports;
            RemotingAddress = ConvertFrom.RemotingAddress;
            RegionID = UUID.Zero;
        }

        /// <summary>
        /// The port by which http communication occurs with the region (most noticeably, CAPS communication)
        /// </summary>
        public uint HttpPort
        {
            get { return m_httpPort; }
            set { m_httpPort = value; }
        }

        /// <summary>
        /// Returns a server URI consisting of the external hostname and the http server port
        /// </summary>
        public string InsecurePublicHTTPServerURI
        {
            get { return "http://" + ExternalHostName + ":" + HttpPort; }
        }

        /// <summary>
        /// Unused
        /// </summary>
        public uint RemotingPort
        {
            get { return m_remotingPort; }
            set { m_remotingPort = value; }
        }

        /// <value>
        /// This accessor can throw all the exceptions that Dns.GetHostAddresses can throw.
        ///
        /// XXX Isn't this really doing too much to be a simple getter, rather than an explict method?
        /// </value>
        public IPEndPoint ExternalEndPoint
        {
            get
            {
                //if we have an outside ip address specified, use that
                if (m_outsideIP != null)
                    return new IPEndPoint(IPAddress.Parse(m_outsideIP), m_internalEndPoint.Port);

                IPAddress ia = null;
                // If it is already an IP, don't resolve it - just return directly
                if (IPAddress.TryParse(m_externalHostName, out ia))
                    return new IPEndPoint(ia, m_internalEndPoint.Port);

                // Reset for next check
                ia = null;

                foreach (IPAddress Adr in Dns.GetHostAddresses(m_externalHostName))
                {
                    if (ia == null)
                        ia = Adr;

                    if (Adr.AddressFamily == AddressFamily.InterNetwork)
                    {
                        ia = Adr;
                        break;
                    }
                }

                return new IPEndPoint(ia, m_internalEndPoint.Port);
            }
        }

        /// <summary>
        ///  Gets or sets the Internet-accessible domain name or IP of the region.
        /// </summary>
        /// <value>The name of the external host.</value>
        public string ExternalHostName
        {
            get { return m_externalHostName; }
            set { m_externalHostName = value; }
        }

        public IPEndPoint InternalEndPoint
        {
            get { return m_internalEndPoint; }
            set { m_internalEndPoint = value; }
        }

        public uint RegionLocX
        {
            get { return m_regionLocX.Value; }
            set { m_regionLocX = value; }
        }

        public uint RegionLocY
        {
            get { return m_regionLocY.Value; }
            set { m_regionLocY = value; }
        }

        public ulong RegionHandle
        {
            get { return Util.RegionHandleFromLocation(RegionLocX, RegionLocY); }
        }

        public string OutsideIP
        {
            get { return m_outsideIP; }
            set { m_outsideIP = value; }
        }

        public bool Equals(SimpleRegionInfo other)
        {
            return this.RegionHandle == other.RegionHandle;
        }

        public override bool Equals(object obj)
        {
            SimpleRegionInfo other = obj as SimpleRegionInfo;
            if (other == null) return false;

            return this.Equals(other);
        }

        public override int GetHashCode()
        {
            return this.RegionHandle.GetHashCode();
        }

        public SimpleRegionInfoSnapshot ToSnapshot()
        {
            if (m_internalEndPoint == null)
            {
                m_internalEndPoint = new IPEndPoint(0, 0);
            }

            return new SimpleRegionInfoSnapshot
            {
                AllowAlternatePorts = m_allow_alternate_ports,
                ExternalHostName = m_externalHostName,
                HttpPort = m_httpPort,
                IPEndPointAddress = m_internalEndPoint.Address.ToString(),
                IPEndPointPort = m_internalEndPoint.Port,
                OutsideIP = m_outsideIP,
                RegionID = RegionID.Guid,
                RegionLocX = m_regionLocX,
                RegionLocY = m_regionLocY,
                RemotingAddress = RemotingAddress,
                RemotingPort = m_remotingPort
            };
        }
    }

    public enum ProductRulesUse { UnknownUse, FullUse, OceanUse, ScenicUse, PlusUse };
    public enum ProductRulesWho { Default, NoDeeding, OnlyEO }; // Default==normal, same as full region

    public enum ProductAccessUse { Anyone=0, PlusOnly=1 };  // when more than one, these should have bitmask values (1,2,4,8)

    public class RegionInfo : SimpleRegionInfo
    {
        public const int DEFAULT_REGION_PRIM_LIMIT = 45000;
        public const int SCENIC_REGION_PRIM_LIMIT = 5000;

        // private static readonly log4net.ILog m_log
        //     = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public bool commFailTF = false;
        public ConfigurationMember configMember;
        public string DataStore = String.Empty;
        public string RegionFile = String.Empty;
        public bool isSandbox = false;
        private EstateSettings m_estateSettings;
        private RegionSettings m_regionSettings;
        //private IConfigSource m_configSource = null;

        public UUID MasterAvatarAssignedUUID = UUID.Zero;
        public string MasterAvatarFirstName = String.Empty;
        public string MasterAvatarLastName = String.Empty;
        public string MasterAvatarSandboxPassword = String.Empty;
        public UUID originRegionID = UUID.Zero;
        public string proxyUrl = String.Empty;
        public int ProxyOffset = 0;
        public string RegionName = String.Empty;
        public string regionSecret = UUID.Random().ToString();
        
        public string osSecret;

        private int m_physPrimMax = 0;
        private bool m_clampPrimSize = false;
        private int m_objectCapacity = 0;

        private ProductRulesUse m_product = ProductRulesUse.UnknownUse;
        private ProductAccessUse m_productAccess = ProductAccessUse.Anyone;
        private int m_primLimit = 0;            // 0 = default below, read from region file
        private int m_primLimitDefault = DEFAULT_REGION_PRIM_LIMIT; // value saved from region file
        private int m_primsTotal = 0;           // actual # prims that exist in region
        private ProductRulesWho m_rezzers = ProductRulesWho.Default;
        private bool m_partnerRez = false;
        private ProductRulesWho m_setHome = ProductRulesWho.Default;
        private ProductRulesWho m_ownersAllowed = ProductRulesWho.Default;
        private bool m_groupTagsAllowed = true;
        private bool m_salesAllowed = true;
        private bool m_enforcePrimLimits = true;
        private int m_maxAutoReturn = 0;           // seconds, 0 = no limits on autoreturn

#if USE_REGION_ENVIRONMENT_DATA
        private RegionEnvironmentData m_environment = new RegionEnvironmentData();
#endif

        // Apparently, we're applying the same estatesettings regardless of whether it's local or remote.

        // MT: Yes. Estates can't span trust boundaries. Therefore, it can be
        // assumed that all instances belonging to one estate are able to
        // access the same database server. Since estate settings are lodaed
        // from there, that should be sufficient for full remote administration

        /// <summary>
        /// Load the region information from a file.
        /// </summary>
        /// <param name="description">Description used for logging.</param>
        /// <param name="filename">Full path of the file to load from. Can be relative or absolute.</param>
        /// <param name="skipConsoleConfig">If set to <c>true</c> skip interactive queries for the region parameters and use defaults instead.</param>
        /// <param name="configSource">Unused at this time.</param>
        public RegionInfo(string description, string filename, bool skipConsoleConfig, IConfigSource configSource)
        {
            //m_configSource = configSource;
            configMember =
                new ConfigurationMember(filename, description, loadConfigurationOptions, handleIncomingConfiguration, !skipConsoleConfig);
            configMember.performConfigurationRetrieve();
            RegionFile = filename;
        }

        /// <summary>
        /// Load the region information from preparsed XML.
        /// </summary>
        /// <param name="description">Description used for logging.</param>
        /// <param name="xmlNode">Xml node containing the region configuration entry.</param>
        /// <param name="skipConsoleConfig">If set to <c>true</c> skip interactive queries for the region parameters and use defaults instead.</param>
        /// <param name="configSource">Unused at this time.</param>
        public RegionInfo(string description, XmlNode xmlNode, bool skipConsoleConfig, IConfigSource configSource)
        {
            //m_configSource = configSource;
            configMember =
                new ConfigurationMember(xmlNode, description, loadConfigurationOptions, handleIncomingConfiguration, !skipConsoleConfig);
            configMember.performConfigurationRetrieve();
        }

        public RegionInfo(uint regionLocX, uint regionLocY, IPEndPoint internalEndPoint, string externalUri) :
            base(regionLocX, regionLocY, internalEndPoint, externalUri)
        {
        }

        public RegionInfo()
        {
        }

        public RegionInfo(SimpleRegionInfo ConvertFrom)
        {
            m_regionLocX = ConvertFrom.RegionLocX;
            m_regionLocY = ConvertFrom.RegionLocY;
            m_internalEndPoint = ConvertFrom.InternalEndPoint;
            m_externalHostName = ConvertFrom.ExternalHostName;
            m_remotingPort = ConvertFrom.RemotingPort;
            m_allow_alternate_ports = ConvertFrom.m_allow_alternate_ports;
            RemotingAddress = ConvertFrom.RemotingAddress;
            RegionID = UUID.Zero;
        }

        public EstateSettings EstateSettings
        {
            get
            {
                if (m_estateSettings == null)
                {
                    m_estateSettings = new EstateSettings();
                }

                return m_estateSettings;
            }

            set { m_estateSettings = value; }
        }

        public RegionSettings RegionSettings
        {
            get
            {
                if (m_regionSettings == null)
                {
                    m_regionSettings = new RegionSettings();
                }

                return m_regionSettings;
            }

            set { m_regionSettings = value; }
        }

#if USE_REGION_ENVIRONMENT_DATA
        public RegionEnvironmentData EnvironmentSettings
        {
            get
            {
                if (m_environment == null)
                {
                    m_environment = new RegionEnvironmentData();
                }

                return m_environment;
            }

            set { m_environment = value; }
        }
#endif

        public void ApplyProductRules()
        {
            ProductRulesUse use = m_product;
            if (use == ProductRulesUse.UnknownUse)
                use = ProductRulesUse.FullUse;

            switch (use)
            {
                case ProductRulesUse.FullUse:
                default:
                    m_primLimit = m_primLimitDefault;   // 0 = default, read from region file
                    m_rezzers = ProductRulesWho.Default;   // owners define restrictions
                    m_partnerRez = false;
                    m_setHome = ProductRulesWho.Default;
                    m_ownersAllowed = ProductRulesWho.Default;
                    m_groupTagsAllowed = true;
                    m_salesAllowed = true;
                    m_enforcePrimLimits = true;
                    m_maxAutoReturn = 0;    // no limits on auto-return
                    break;
                case ProductRulesUse.ScenicUse:
                    m_primLimit = SCENIC_REGION_PRIM_LIMIT;
                    m_rezzers = ProductRulesWho.OnlyEO;
                    m_partnerRez = true;
                    m_setHome = ProductRulesWho.OnlyEO;
                    m_ownersAllowed = ProductRulesWho.OnlyEO;
                    m_groupTagsAllowed = false;
                    m_salesAllowed = false;
                    m_enforcePrimLimits = true;
                    m_maxAutoReturn = 60;    // minutes
                    break;
                case ProductRulesUse.OceanUse:   // same as Full regions for now
                    m_primLimit = m_primLimitDefault;   // 0 = default, read from region file
                    m_rezzers = ProductRulesWho.Default;   // owners define restrictions
                    m_partnerRez = false;
                    m_setHome = ProductRulesWho.Default;
                    m_ownersAllowed = ProductRulesWho.OnlyEO;
                    m_groupTagsAllowed = true;
                    m_salesAllowed = false;
                    m_enforcePrimLimits = true;
                    m_maxAutoReturn = 0;    // no limits on auto-return
                    break;
                case ProductRulesUse.PlusUse:
                    m_primLimit = m_primLimitDefault;   // 0 = default, read from region file
                    m_rezzers = ProductRulesWho.Default;   // owners define restrictions
                    m_partnerRez = true;
                    m_setHome = ProductRulesWho.Default;
                    m_ownersAllowed = ProductRulesWho.NoDeeding;
                    m_groupTagsAllowed = true;
                    m_salesAllowed = false;
                    m_enforcePrimLimits = true;
                    m_maxAutoReturn = 4*60;    // 4-hour auto-return
                    break;
            };
        }

        public ProductRulesUse Product
        {
            get
            {
                if (m_product == ProductRulesUse.UnknownUse)
                    return ProductRulesUse.FullUse;
                return m_product;
            }

            set
            {
                m_product = value;
                ApplyProductRules();
            }
        }

        public ProductAccessUse ProductAccess
        {
            get { return m_productAccess; }
        }

        public bool ProductAccessAllowed(ProductAccessUse which)
        {
            return ((uint)this.ProductAccess & (uint)ProductAccessUse.PlusOnly) != 0;
        }

        public bool IsPlusUser(string customType)
        {
            bool isPlusUser;
            switch (customType.ToLower())
            {
                // test for lowercase strings
                case "plus":
                case "cornerstone":
                case "cornerstone plus":
                case "inworldz co-founder":
                case "inworldz grid monkey":
                case "inworldz employee":
                case "founding member":
                case "retired founding member":
                    isPlusUser = true;
                    break;
                default:
                    isPlusUser = false;
                    break;
            }

            return isPlusUser;
        }

        public bool UserHasProductAccess(UserProfileData profile)
        {
            if (ProductAccess == ProductAccessUse.Anyone)
                return true;

            // Region has access restricted to certain user types, i.e. Plus
            if (ProductAccessAllowed(ProductAccessUse.PlusOnly))
            {
                if (profile != null)
                {
                    if (IsPlusUser(profile.CustomType))
                        return true;
                }
            }

            // no more access options to check
            return false;
        }

        public int PrimTotal
        {
            get
            {
                return m_primsTotal;
            }
            set
            {
                m_primsTotal = value;
            }
        }

        public int PrimLimit
        {
            get
            {
                if (m_primLimit == 0) 
                    m_primLimit = PrimLimitDefault;
                return m_primLimit;
            }
        }

        public int PrimLimitDefault
        {
            get
            {
                if (m_primLimitDefault == 0)
                    m_primLimitDefault = DEFAULT_REGION_PRIM_LIMIT;
                return m_primLimitDefault;
            }
            set
            {
                if (value != 0)
                    m_primLimitDefault = value;
                else
                    m_primLimitDefault = DEFAULT_REGION_PRIM_LIMIT;
            }
        }

        public int MaxAutoReturn
        {
            get
            {
                return m_maxAutoReturn; // seconds, 0 = no limits on autoreturn
            }
        }

        public ProductRulesWho AllowRezzers
        {
            get
            {
                return m_rezzers;
            }
        }

        public bool AllowPartnerRez
        {
            get
            {
                return m_partnerRez;
            }
        }

        public ProductRulesWho AllowSetHome
        {
            get
            {
                return m_setHome;
            }
        }

        public bool AllowGroupTags
        {
            get
            {
                return m_groupTagsAllowed;
            }
        }

        public bool AllowSales
        {
            get
            {
                return m_salesAllowed;
            }
        }

        public ProductRulesWho AllowOwners
        {
            get
            {
                return m_ownersAllowed;
            }
        }

        public bool AllowDeeding
        {
            get
            {
                switch (m_ownersAllowed)
                {
                    case ProductRulesWho.NoDeeding:
                    case ProductRulesWho.OnlyEO:
                        return false;
                    case ProductRulesWho.Default:
                    default:
                        return true;
                }
            }
        }

        public bool EnforcePrimLimits
        {
            get { return m_enforcePrimLimits; }
        }

        public int NonphysPrimMax
        {
            get { return PrimLimit; }
        }

        public int PhysPrimMax
        {
            get {
                if (m_physPrimMax == 0)
                    return PrimLimit;
                if (PrimLimit == 0)
                    return m_physPrimMax;
                return Math.Min(m_physPrimMax, PrimLimit);
            }
        }

        public bool ClampPrimSize
        {
            get { return m_clampPrimSize; }
        }

        public int ObjectCapacity
        {
            get { return m_objectCapacity; }
        }

        public byte AccessLevel
        {
            get { return (byte)Util.ConvertMaturityToAccessLevel((uint)RegionSettings.Maturity); }
        }

        public void SetEndPoint(string ipaddr, int port)
        {
            IPAddress tmpIP = IPAddress.Parse(ipaddr);
            IPEndPoint tmpEPE = new IPEndPoint(tmpIP, port);
            m_internalEndPoint = tmpEPE;
        }

        //not in use, should swap to nini though.
        public void LoadFromNiniSource(IConfigSource source)
        {
            LoadFromNiniSource(source, "RegionInfo");
        }

        //not in use, should swap to nini though.
        public void LoadFromNiniSource(IConfigSource source, string sectionName)
        {
            string errorMessage = String.Empty;
            RegionID = new UUID(source.Configs[sectionName].GetString("Region_ID", UUID.Random().ToString()));
            RegionName = source.Configs[sectionName].GetString("sim_name", "Halcyon Test");
            m_regionLocX = Convert.ToUInt32(source.Configs[sectionName].GetString("sim_location_x", "1000"));
            m_regionLocY = Convert.ToUInt32(source.Configs[sectionName].GetString("sim_location_y", "1000"));
            // this.DataStore = source.Configs[sectionName].GetString("datastore", "OpenSim.db");

            string ipAddress = source.Configs[sectionName].GetString("internal_ip_address", "0.0.0.0");
            IPAddress ipAddressResult;
            if (IPAddress.TryParse(ipAddress, out ipAddressResult))
            {
                m_internalEndPoint = new IPEndPoint(ipAddressResult, 0);
            }
            else
            {
                errorMessage = "needs an IP Address (IPAddress)";
            }
            m_internalEndPoint.Port =
                source.Configs[sectionName].GetInt("internal_ip_port", (int) ConfigSettings.DefaultRegionHttpPort);

            string externalHost = source.Configs[sectionName].GetString("external_host_name", "127.0.0.1");
            if (externalHost != "SYSTEMIP")
            {
                m_externalHostName = externalHost;
            }
            else
            {
                m_externalHostName = Util.GetLocalHost().ToString();
            }

            OutsideIP = source.Configs[sectionName].GetString("outside_ip", String.Empty);
            if (String.IsNullOrEmpty(OutsideIP)) OutsideIP = null;

            MasterAvatarFirstName = source.Configs[sectionName].GetString("master_avatar_first", "Test");
            MasterAvatarLastName = source.Configs[sectionName].GetString("master_avatar_last", "User");
            MasterAvatarSandboxPassword = source.Configs[sectionName].GetString("master_avatar_pass", "test");

            MasterAvatarSandboxPassword = source.Configs[sectionName].GetString("master_avatar_pass", "test");

            if (!String.IsNullOrEmpty(errorMessage))
            {
                // TODO: a error
            }
        }

        public bool ignoreIncomingConfiguration(string configuration_key, object configuration_result)
        {
            return true;
        }

        public void SaveRegionToFile(string description, string filename)
        {
            configMember = new ConfigurationMember(filename, description, loadConfigurationOptionsFromMe,
                                                   ignoreIncomingConfiguration, false);
            configMember.performConfigurationRetrieve();
            RegionFile = filename;
        }

        public void loadConfigurationOptionsFromMe()
        {
            configMember.addConfigurationOption("sim_UUID", ConfigurationOption.ConfigurationTypes.TYPE_UUID_NULL_FREE,
                                                "UUID of Region (Default is recommended, random UUID)",
                                                RegionID.ToString(), true);
            configMember.addConfigurationOption("sim_name", ConfigurationOption.ConfigurationTypes.TYPE_STRING_NOT_EMPTY,
                                                "Region Name", RegionName, true);
            configMember.addConfigurationOption("sim_location_x", ConfigurationOption.ConfigurationTypes.TYPE_UINT32,
                                                "Grid Location (X Axis)", m_regionLocX.ToString(), true);
            configMember.addConfigurationOption("sim_location_y", ConfigurationOption.ConfigurationTypes.TYPE_UINT32,
                                                "Grid Location (Y Axis)", m_regionLocY.ToString(), true);
            //m_configMember.addConfigurationOption("datastore", ConfigurationOption.ConfigurationTypes.TYPE_STRING_NOT_EMPTY, "Filename for local storage", "OpenSim.db", false);
            configMember.addConfigurationOption("internal_ip_address",
                                                ConfigurationOption.ConfigurationTypes.TYPE_IP_ADDRESS,
                                                "Internal IP Address for incoming UDP client connections",
                                                m_internalEndPoint.Address.ToString(),
                                                true);
            configMember.addConfigurationOption("internal_ip_port", ConfigurationOption.ConfigurationTypes.TYPE_INT32,
                                                "Internal IP Port for incoming UDP client connections",
                                                m_internalEndPoint.Port.ToString(), true);
            configMember.addConfigurationOption("allow_alternate_ports",
                                                ConfigurationOption.ConfigurationTypes.TYPE_BOOLEAN,
                                                "Allow sim to find alternate UDP ports when ports are in use?",
                                                m_allow_alternate_ports.ToString(), true);
            configMember.addConfigurationOption("external_host_name",
                                                ConfigurationOption.ConfigurationTypes.TYPE_STRING_NOT_EMPTY,
                                                "External Host Name", m_externalHostName, true);
            configMember.addConfigurationOption("master_avatar_uuid", ConfigurationOption.ConfigurationTypes.TYPE_UUID,
                                                "Master Avatar UUID", MasterAvatarAssignedUUID.ToString(), true);
            configMember.addConfigurationOption("master_avatar_first",
                                                ConfigurationOption.ConfigurationTypes.TYPE_STRING_NOT_EMPTY,
                                                "First Name of Master Avatar", MasterAvatarFirstName, true);
            configMember.addConfigurationOption("master_avatar_last",
                                                ConfigurationOption.ConfigurationTypes.TYPE_STRING_NOT_EMPTY,
                                                "Last Name of Master Avatar", MasterAvatarLastName, true);
            configMember.addConfigurationOption("master_avatar_pass", ConfigurationOption.ConfigurationTypes.TYPE_STRING,
                                                "(Sandbox Mode Only)Password for Master Avatar account",
                                                MasterAvatarSandboxPassword, true);

            configMember.addConfigurationOption("nonphysical_prim_max", ConfigurationOption.ConfigurationTypes.TYPE_INT32,
                                                "Maximum size for nonphysical prims", NonphysPrimMax.ToString(), true);
            
            configMember.addConfigurationOption("physical_prim_max", ConfigurationOption.ConfigurationTypes.TYPE_INT32,
                                                "Maximum size for physical prims", PhysPrimMax.ToString(), true);
            
            configMember.addConfigurationOption("clamp_prim_size", ConfigurationOption.ConfigurationTypes.TYPE_BOOLEAN,
                                                "Clamp prims to max size", m_clampPrimSize.ToString(), true);
            
            configMember.addConfigurationOption("object_capacity", ConfigurationOption.ConfigurationTypes.TYPE_INT32,
                                                "Max objects this sim will hold", m_objectCapacity.ToString(), true);

            configMember.addConfigurationOption("region_product", ConfigurationOption.ConfigurationTypes.TYPE_INT32,
                                                "Type of region, 1=full, 2=ocean/strait, 3=scenic, 4=plus", this.m_product.ToString(), true);

            configMember.addConfigurationOption("region_access", ConfigurationOption.ConfigurationTypes.TYPE_INT32,
                                                "Access restrictions, 0=anyone, 1=only Plus users", this.m_productAccess.ToString(), true);

            configMember.addConfigurationOption("outside_ip", ConfigurationOption.ConfigurationTypes.TYPE_STRING,
                                                "The ip address as seen by the outside world", this.OutsideIP, true);
        }

        public void loadConfigurationOptions()
        {
            configMember.addConfigurationOption("sim_UUID", ConfigurationOption.ConfigurationTypes.TYPE_UUID,
                                                "UUID of Region (Default is recommended, random UUID)",
                                                UUID.Random().ToString(), true);
            configMember.addConfigurationOption("sim_name", ConfigurationOption.ConfigurationTypes.TYPE_STRING_NOT_EMPTY,
                                                "Region Name", "Halcyon Test", false);
            configMember.addConfigurationOption("sim_location_x", ConfigurationOption.ConfigurationTypes.TYPE_UINT32,
                                                "Grid Location (X Axis)", "1000", false);
            configMember.addConfigurationOption("sim_location_y", ConfigurationOption.ConfigurationTypes.TYPE_UINT32,
                                                "Grid Location (Y Axis)", "1000", false);
            //m_configMember.addConfigurationOption("datastore", ConfigurationOption.ConfigurationTypes.TYPE_STRING_NOT_EMPTY, "Filename for local storage", "OpenSim.db", false);
            configMember.addConfigurationOption("internal_ip_address",
                                                ConfigurationOption.ConfigurationTypes.TYPE_IP_ADDRESS,
                                                "Internal IP Address for incoming UDP client connections", "0.0.0.0",
                                                false);
            configMember.addConfigurationOption("internal_ip_port", ConfigurationOption.ConfigurationTypes.TYPE_INT32,
                                                "Internal IP Port for incoming UDP client connections",
                                                ConfigSettings.DefaultRegionHttpPort.ToString(), false);
            configMember.addConfigurationOption("allow_alternate_ports", ConfigurationOption.ConfigurationTypes.TYPE_BOOLEAN,
                                                "Allow sim to find alternate UDP ports when ports are in use?",
                                                "false", true);
            configMember.addConfigurationOption("external_host_name",
                                                ConfigurationOption.ConfigurationTypes.TYPE_STRING_NOT_EMPTY,
                                                "External Host Name", "127.0.0.1", false);
            configMember.addConfigurationOption("master_avatar_uuid", ConfigurationOption.ConfigurationTypes.TYPE_UUID,
                                                "Master Avatar UUID", UUID.Zero.ToString(), true);
            configMember.addConfigurationOption("master_avatar_first",
                                                ConfigurationOption.ConfigurationTypes.TYPE_STRING_NOT_EMPTY,
                                                "First Name of Master Avatar", "Test", false,
                                                (ConfigurationOption.ConfigurationOptionShouldBeAsked)
                                                shouldMasterAvatarDetailsBeAsked);
            configMember.addConfigurationOption("master_avatar_last",
                                                ConfigurationOption.ConfigurationTypes.TYPE_STRING_NOT_EMPTY,
                                                "Last Name of Master Avatar", "User", false,
                                                (ConfigurationOption.ConfigurationOptionShouldBeAsked)
                                                shouldMasterAvatarDetailsBeAsked);
            configMember.addConfigurationOption("master_avatar_pass", ConfigurationOption.ConfigurationTypes.TYPE_STRING,
                                                "(Sandbox Mode Only)Password for Master Avatar account", "test", false,
                                                (ConfigurationOption.ConfigurationOptionShouldBeAsked)
                                                shouldMasterAvatarDetailsBeAsked);
            
            configMember.addConfigurationOption("nonphysical_prim_max", ConfigurationOption.ConfigurationTypes.TYPE_INT32,
                                                "Maximum size for nonphysical prims", "0", true);
            
            configMember.addConfigurationOption("physical_prim_max", ConfigurationOption.ConfigurationTypes.TYPE_INT32,
                                                "Maximum size for physical prims", "0", true);
            
            configMember.addConfigurationOption("clamp_prim_size", ConfigurationOption.ConfigurationTypes.TYPE_BOOLEAN,
                                                "Clamp prims to max size", "false", true);
            
            configMember.addConfigurationOption("object_capacity", ConfigurationOption.ConfigurationTypes.TYPE_INT32,
                                                "Max objects this sim will hold", "0", true);

            configMember.addConfigurationOption("region_product", ConfigurationOption.ConfigurationTypes.TYPE_INT32,
                                                "Type of region, 1=full, 2=ocean/strait, 3=scenic, 4=plus", "0", true);

            configMember.addConfigurationOption("region_access", ConfigurationOption.ConfigurationTypes.TYPE_INT32,
                                                "Access restrictions, 0=anyone, 1=only Plus users", "0", true);

            configMember.addConfigurationOption("outside_ip", ConfigurationOption.ConfigurationTypes.TYPE_STRING,
                                                "The ip address as seen by the outside world", String.Empty, true);
        }

        public bool shouldMasterAvatarDetailsBeAsked(string configuration_key)
        {
            return MasterAvatarAssignedUUID == UUID.Zero;
        }

        public bool handleIncomingConfiguration(string configuration_key, object configuration_result)
        {
            switch (configuration_key)
            {
                case "sim_UUID":
                    RegionID = (UUID) configuration_result;
                    originRegionID = (UUID) configuration_result;
                    break;
                case "sim_name":
                    RegionName = (string) configuration_result;
                    break;
                case "sim_location_x":
                    m_regionLocX = (uint) configuration_result;
                    break;
                case "sim_location_y":
                    m_regionLocY = (uint) configuration_result;
                    break;
                case "datastore":
                    DataStore = (string) configuration_result;
                    break;
                case "internal_ip_address":
                    IPAddress address = (IPAddress) configuration_result;
                    m_internalEndPoint = new IPEndPoint(address, 0);
                    break;
                case "internal_ip_port":
                    m_internalEndPoint.Port = (int) configuration_result;
                    break;
                case "allow_alternate_ports":
                    m_allow_alternate_ports = (bool) configuration_result;
                    break;
                case "external_host_name":
                    if ((string) configuration_result != "SYSTEMIP")
                    {
                        m_externalHostName = (string) configuration_result;
                    }
                    else
                    {
                        m_externalHostName = Util.GetLocalHost().ToString();
                    }
                    break;
                case "master_avatar_uuid":
                    MasterAvatarAssignedUUID = (UUID) configuration_result;
                    break;
                case "master_avatar_first":
                    MasterAvatarFirstName = (string) configuration_result;
                    break;
                case "master_avatar_last":
                    MasterAvatarLastName = (string) configuration_result;
                    break;
                case "master_avatar_pass":
                    MasterAvatarSandboxPassword = (string)configuration_result;
                    break;
                case "nonphysical_prim_max":
                    PrimLimitDefault = (int)configuration_result;
                    break;
                case "physical_prim_max":
                    m_physPrimMax = (int)configuration_result;
                    break;
                case "clamp_prim_size":
                    m_clampPrimSize = (bool)configuration_result;
                    break;
                case "object_capacity":
                    m_objectCapacity = (int)configuration_result;
                    break;
                case "region_product":
                    switch ((int)configuration_result) 
                    {
                        case 1:
                            m_product = ProductRulesUse.FullUse;
                            break;
                        case 2:
                            m_product = ProductRulesUse.OceanUse;
                            break;
                        case 3:
                            m_product = ProductRulesUse.ScenicUse;
                            break;
                        case 4:
                            m_product = ProductRulesUse.PlusUse;
                            break;
                        case 0:
                        default:
                            m_product = ProductRulesUse.UnknownUse;
                            break;
                    }
                    ApplyProductRules();
                    break;
                case "region_access":
                    switch ((int)configuration_result)
                    {
                        case 1:
                            m_productAccess = ProductAccessUse.PlusOnly;
                            break;
                        case 0:
                        default:
                            m_productAccess = ProductAccessUse.Anyone;
                            break;
                    }
                    break;
                case "outside_ip":
                    OutsideIP = (string)configuration_result;
                    if (String.IsNullOrEmpty(OutsideIP)) OutsideIP = null;
                    break;
            }

            return true;
        }

        public OSDMap PackRegionInfoData()
        {
            OSDMap args = new OSDMap();
            args["region_id"] = OSD.FromUUID(RegionID);
            if (!String.IsNullOrEmpty(RegionName))
                args["region_name"] = OSD.FromString(RegionName);
            args["external_host_name"] = OSD.FromString(ExternalHostName);
            args["http_port"] = OSD.FromString(HttpPort.ToString());
            args["region_xloc"] = OSD.FromString(RegionLocX.ToString());
            args["region_yloc"] = OSD.FromString(RegionLocY.ToString());
            args["internal_ep_address"] = OSD.FromString(InternalEndPoint.Address.ToString());
            args["internal_ep_port"] = OSD.FromString(InternalEndPoint.Port.ToString());
            if (!String.IsNullOrEmpty(RemotingAddress))
                args["remoting_address"] = OSD.FromString(RemotingAddress);
            args["remoting_port"] = OSD.FromString(RemotingPort.ToString());
            args["allow_alt_ports"] = OSD.FromBoolean(m_allow_alternate_ports);
            if (!String.IsNullOrEmpty(proxyUrl))
                args["proxy_url"] = OSD.FromString(proxyUrl);

            if (OutsideIP != null) args["outside_ip"] = OSD.FromString(OutsideIP);

            return args;
        }

        public void UnpackRegionInfoData(OSDMap args)
        {
            if (args.ContainsKey("region_id"))
                RegionID = args["region_id"].AsUUID();

            if (args.ContainsKey("region_name"))
                RegionName = args["region_name"].AsString();
            if (args.ContainsKey("external_host_name"))
                ExternalHostName = args["external_host_name"].AsString();
            if (args.ContainsKey("http_port"))
                UInt32.TryParse(args["http_port"].AsString(), out m_httpPort);

            if (args.ContainsKey("region_xloc"))
            {
                uint locx;
                UInt32.TryParse(args["region_xloc"].AsString(), out locx);
                RegionLocX = locx;
            }
            if (args.ContainsKey("region_yloc"))
            {
                uint locy;
                UInt32.TryParse(args["region_yloc"].AsString(), out locy);
                RegionLocY = locy;
            }
            IPAddress ip_addr = null;
            if (args.ContainsKey("internal_ep_address"))
            {
                IPAddress.TryParse(args["internal_ep_address"].AsString(), out ip_addr);
            }
            int port = 0;
            if (args.ContainsKey("internal_ep_port"))
            {
                Int32.TryParse(args["internal_ep_port"].AsString(), out port);
            }
            InternalEndPoint = new IPEndPoint(ip_addr, port);
            if (args.ContainsKey("remoting_address"))
                RemotingAddress = args["remoting_address"].AsString();
            if (args.ContainsKey("remoting_port"))
                UInt32.TryParse(args["remoting_port"].AsString(), out m_remotingPort);
            if (args.ContainsKey("allow_alt_ports"))
                m_allow_alternate_ports = args["allow_alt_ports"].AsBoolean();
            if (args.ContainsKey("proxy_url"))
                proxyUrl = args["proxy_url"].AsString();

            if (args.ContainsKey("outside_ip"))
                OutsideIP = args["outside_ip"].AsString();
        }

        public static RegionInfo Create(UUID regionID, string regionName, uint regX, uint regY, string externalHostName, uint httpPort, uint simPort, uint remotingPort,
            string outsideIp)
        {
            RegionInfo regionInfo;
            IPEndPoint neighbourInternalEndPoint = new IPEndPoint(Util.GetHostFromDNS(externalHostName), (int)simPort);
            regionInfo = new RegionInfo(regX, regY, neighbourInternalEndPoint, externalHostName);
            regionInfo.RemotingPort = remotingPort;
            regionInfo.RemotingAddress = externalHostName;
            regionInfo.HttpPort = httpPort;
            regionInfo.RegionID = regionID;
            regionInfo.RegionName = regionName;
            regionInfo.OutsideIP = outsideIp;

            return regionInfo;
        }

    }
}
