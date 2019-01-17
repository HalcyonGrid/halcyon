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
 *     * Neither the name of the OpenSimulator Project nor the
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
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Threading;
using log4net;
using Nini.Config;
using Nwc.XmlRpc;
using BclExtras;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using Amib.Threading;
using System.Drawing;
using System.Runtime.InteropServices;

#if !_MONO_CLI_FLAG_
using System.DirectoryServices.AccountManagement;
#endif


namespace OpenSim.Framework
{
    /// <summary>
    /// The method used by Util.FireAndForget for asynchronously firing events
    /// </summary>
    public enum FireAndForgetMethod
    {
        UnsafeQueueUserWorkItem,
        QueueUserWorkItem,
        BeginInvoke,
        SmartThreadPool,
        Thread,
    }

    /// <summary>
    /// Miscellaneous utility functions
    /// </summary>
    public class Util
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static uint nextXferID = 5000;
        private static Random randomClass = new Random();
        // Get a list of invalid file characters (OS dependent)
        private static string regexInvalidFileChars = "[" + new String(Path.GetInvalidFileNameChars()) + "]";
        private static string regexInvalidPathChars = "[" + new String(Path.GetInvalidPathChars()) + "]";
        private static object XferLock = new object();

        public static bool IsWindows = System.Environment.OSVersion.Platform == System.PlatformID.Win32NT;

        /// <summary>
        /// These pools will be created for different purposes so that one does not affect the other
        /// </summary>
        public enum PoolSelection
        {
            /// <summary>
            /// The default thread pool. Should not be used for long operations
            /// </summary>
            Default,

            /// <summary>
            /// Pool to be used for long operations (call outs to services, db queries)
            /// </summary>
            LongIO
        }

        /// <summary>Thread pool used for Util.FireAndForget if
        /// FireAndForgetMethod.SmartThreadPool is used</summary>
        private static SmartThreadPool[] m_ThreadPool = new SmartThreadPool[Enum.GetValues(typeof(PoolSelection)).Length];

        

        // Unix-epoch starts at January 1st 1970, 00:00:00 UTC. And all our times in the server are (or at least should be) in UTC.
        private static readonly DateTime unixEpoch =
            DateTime.ParseExact("1970-01-01 00:00:00 +0", "yyyy-MM-dd hh:mm:ss z", DateTimeFormatInfo.InvariantInfo).ToUniversalTime();

        public static readonly Regex UUIDPattern
            = new Regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");

        public static FireAndForgetMethod FireAndForgetMethod = FireAndForgetMethod.SmartThreadPool;

        /// <summary>
        /// Linear interpolates B<->C using percent A
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <param name="c"></param>
        /// <returns></returns>
        public static double lerp(double a, double b, double c)
        {
            return (b * a) + (c * (1 - a));
        }

        /// <summary>
        /// Bilinear Interpolate, see Lerp but for 2D using 'percents' X & Y.
        /// Layout:
        ///     A B
        ///     C D
        /// A<->C = Y
        /// C<->D = X
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <param name="c"></param>
        /// <param name="d"></param>
        /// <returns></returns>
        public static double lerp2D(double x, double y, double a, double b, double c, double d)
        {
            return lerp(y, lerp(x, a, b), lerp(x, c, d));
        }


        public static Encoding UTF8 = Encoding.UTF8;

        /// <value>
        /// Well known UUID for the blank texture used in the Linden SL viewer version 1.20 (and hopefully onwards) 
        /// </value>
        public static UUID BLANK_TEXTURE_UUID = new UUID("5748decc-f629-461c-9a36-a35a221fe21f");

        [DllImport("kernel32.dll")]
        static extern UInt64 GetTickCount64();

        public static UInt64 GetLongTickCount()
        {
            if (IsWindows)
            {
                return GetTickCount64();
            }
            else
            {
                // TODO: Fill in implementation for linux/cross platform GetTickCount64 implementation
                return (UInt64)Environment.TickCount;
            }
        }


        /// <summary
        /// Authenticate a username/password pair against the user we are running under.
        /// </summary>
        /// <remarks>checks that the username is the same as the current System.Environment.UserName,
        /// And Validates the password against the password for that account</remarks>
        /// <returns>true if the authentication succeeded, false otherwise.</returns>
        /// <param name='username'>string</param>
        /// <param name='password'>string</param>
        public static bool AuthenticateAsSystemUser(string username, string password)
        {
            #if _MONO_CLI_FLAG_
                // TODO: find a way to check the user info cross platform.  In the mean time better security by NOT allowing remote admin.
                return false;
            #else
                // Is the username the same as the logged in user and do they have the password correct?
                var pc = new PrincipalContext(ContextType.Machine);
                var isValid =
                    (username.Equals(Environment.UserName) &&
                    pc.ValidateCredentials(username, password));

                return (isValid);
            #endif
        }


        #region Vector Equations

        /// <summary>
        /// Get the distance between two 3d vectors
        /// </summary>
        /// <param name="a">A 3d vector</param>
        /// <param name="b">A 3d vector</param>
        /// <returns>The distance between the two vectors</returns>
        public static double GetDistanceTo(Vector3 a, Vector3 b)
        {
            return Vector3.Distance(a, b);
        }

        /// <summary>
        /// Returns true if the distance beween A and B is less than amount. Significantly faster than GetDistanceTo since it eliminates the Sqrt.
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <param name="amount"></param>
        /// <returns></returns>
        public static bool DistanceLessThan(Vector3 a, Vector3 b, double amount)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            float dz = a.Z - b.Z;
            return (dx * dx + dy * dy + dz * dz) < (amount * amount);
        }

        /// <summary>
        /// Get the magnitude of a 3d vector
        /// </summary>
        /// <param name="a">A 3d vector</param>
        /// <returns>The magnitude of the vector</returns>
        public static double GetMagnitude(Vector3 a)
        {
            return Math.Sqrt((a.X * a.X) + (a.Y * a.Y) + (a.Z * a.Z));
        }

        /// <summary>
        /// Get a normalized form of a 3d vector
        /// </summary>
        /// <param name="a">A 3d vector</param>
        /// <returns>A new vector which is normalized form of the vector</returns>
        /// <remarks>The vector paramater cannot be <0,0,0></remarks>
        public static Vector3 GetNormalizedVector(Vector3 a)
        {
            if (IsZeroVector(a))
                throw new ArgumentException("Vector paramater cannot be a zero vector.");

            float Mag = (float)GetMagnitude(a);
            return new Vector3(a.X / Mag, a.Y / Mag, a.Z / Mag);
        }

        /// <summary>
        /// Returns if a vector is a zero vector (has all zero components)
        /// </summary>
        /// <returns></returns>
        public static bool IsZeroVector(Vector3 v)
        {
            if (v.X == 0 && v.Y == 0 && v.Z == 0)
            {
                return true;
            }

            return false;
        }

        # endregion

        public static Quaternion Axes2Rot(Vector3 fwd, Vector3 left, Vector3 up)
        {
            float s;
            float tr = (float)(fwd.X + left.Y + up.Z + 1.0);

            if (tr >= 1.0)
            {
                s = (float)(0.5 / Math.Sqrt(tr));
                return new Quaternion(
                        (left.Z - up.Y) * s,
                        (up.X - fwd.Z) * s,
                        (fwd.Y - left.X) * s,
                        (float)0.25 / s);
            }
            else
            {
                float max = (left.Y > up.Z) ? left.Y : up.Z;

                if (max < fwd.X)
                {
                    s = (float)(Math.Sqrt(fwd.X - (left.Y + up.Z) + 1.0));
                    float x = (float)(s * 0.5);
                    s = (float)(0.5 / s);
                    return new Quaternion(
                            x,
                            (fwd.Y + left.X) * s,
                            (up.X + fwd.Z) * s,
                            (left.Z - up.Y) * s);
                }
                else if (max == left.Y)
                {
                    s = (float)(Math.Sqrt(left.Y - (up.Z + fwd.X) + 1.0));
                    float y = (float)(s * 0.5);
                    s = (float)(0.5 / s);
                    return new Quaternion(
                            (fwd.Y + left.X) * s,
                            y,
                            (left.Z + up.Y) * s,
                            (up.X - fwd.Z) * s);
                }
                else
                {
                    s = (float)(Math.Sqrt(up.Z - (fwd.X + left.Y) + 1.0));
                    float z = (float)(s * 0.5);
                    s = (float)(0.5 / s);
                    return new Quaternion(
                            (up.X + fwd.Z) * s,
                            (left.Z + up.Y) * s,
                            z,
                            (fwd.Y - left.X) * s);
                }
            }
        }

        public static Random RandomClass
        {
            get { return randomClass; }
        }

        public static string RandomString(uint length, string alphabet = "abcdefghijklmnopqrstuvwyxzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")
        {
            var chars = new char[length];
            for (var i = 0u; i < length; i++)
            {
                chars[i] = alphabet[RandomClass.Next(alphabet.Length)];
            }
            return new string(chars);
        }

        public static ulong UIntsToLong(uint X, uint Y)
        {
            return Utils.UIntsToLong(X, Y);
        }

        public static T Clamp<T>(T x, T min, T max)
            where T : IComparable<T>
        {
            return x.CompareTo(max) > 0 ? max :
                x.CompareTo(min) < 0 ? min :
                x;
        }

        public static uint GetNextXferID()
        {
            uint id = 0;
            lock (XferLock)
            {
                id = nextXferID;
                nextXferID++;
            }
            return id;
        }

        public static string GetFileName(string file)
        {
            // Return just the filename on UNIX platforms
            // TODO: this should be customisable with a prefix, but that's something to do later.
            if (Environment.OSVersion.Platform == PlatformID.Unix)
            {
                return file;
            }

            // Return %APPDATA%/OpenSim/file for 2K/XP/NT/2K3/VISTA
            // TODO: Switch this to System.Enviroment.SpecialFolders.ApplicationData
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                if (!Directory.Exists("%APPDATA%\\OpenSim\\"))
                {
                    Directory.CreateDirectory("%APPDATA%\\OpenSim");
                }

                return "%APPDATA%\\OpenSim\\" + file;
            }

            // Catch all - covers older windows versions
            // (but those probably wont work anyway)
            return file;
        }

        /// <summary>
        /// Debug utility function to convert OSD into formatted XML for debugging purposes.
        /// </summary>
        /// <param name="osd">
        /// A <see cref="OSD"/>
        /// </param>
        /// <returns>
        /// A <see cref="System.String"/>
        /// </returns>
        public static string GetFormattedXml(OSD osd)
        {
            return GetFormattedXml(OSDParser.SerializeLLSDXmlString(osd));
        }

        /// <summary>
        /// Debug utility function to convert unbroken strings of XML into something human readable for occasional debugging purposes.
        /// </summary>
        /// <remarks>
        /// Please don't delete me even if I appear currently unused!
        /// </remarks>
        /// <param name="rawXml"></param>
        /// <returns></returns>
        public static string GetFormattedXml(string rawXml)
        {
            XmlDocument xd = new XmlDocument();
            xd.LoadXml(rawXml);

            StringBuilder sb = new StringBuilder();
            StringWriter sw = new StringWriter(sb);

            XmlTextWriter xtw = new XmlTextWriter(sw);
            xtw.Formatting = Formatting.Indented;

            try
            {
                xd.WriteTo(xtw);
            }
            finally
            {
                xtw.Close();
            }

            return sb.ToString();
        }

        public static bool IsEnvironmentSupported(ref string reason)
        {
            // Must have .NET CLR 4 or more recent. Note this is NOT the .NET Framework version as that information is only available via the registry.
            if (Environment.Version.Major < 4)
            {
                reason = "Your .NET install is out of date. Please install .NET Framework 4.7 or newer.";
                return false;
            }

            // Pre Win7 / Server 2008 R2 are unsupported.
            if (Environment.OSVersion.Platform == PlatformID.Win32Windows &&
                Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                reason = "Windows editions before Windows 7 or Windows Server 2008 R2 will not run Halcyon.";
                return false;
            }

            // Windows 7 and Windows Server 2008 R2 is the oldest supported by MS for .NET Framework 4.7.1: https://docs.microsoft.com/en-us/dotnet/framework/get-started/system-requirements
            if (Environment.OSVersion.Platform == PlatformID.Win32NT &&
                Environment.OSVersion.Version.Major <= 6 &&
                Environment.OSVersion.Version.Minor <= 1)
            {
                reason = "Please update to Windows 7 Service Pack 1 or newer operating system.";
                return false;
            }

            return true;
        }

        public static int UnixTimeSinceEpoch()
        {
            return ToUnixTime(DateTime.UtcNow);
        }
        public static int LocalUnixTimeSinceEpoch()
        {
            DateTime now = DateTime.Now;
            DateTime localNow = DateTime.SpecifyKind(now, DateTimeKind.Utc);
            return ToUnixTime(localNow);
        }

        // returns seconds in the timezone difference, like UnixTime functions
        public static int LocalTimeOffset()
        {
            DateTime utcNow = DateTime.UtcNow;

            int utcOffset = (int)((utcNow - unixEpoch).TotalSeconds);
            int locOffset = (int)((utcNow.ToLocalTime() - unixEpoch).TotalSeconds);
            return locOffset - utcOffset;
        }

        private static long _lastTimestampRecorded;
        private static long _numMicrosecsAdded;
        private static object _mirosecTimeLock = new object();

        /// <summary>
        /// This method returns the current Unix time in microseconds. It also resolves conflicts dealing
        /// with the fact that the system timer may only be accurate to ~15 ms and adds a microsecond to 
        /// subsequent calls that have the same resultant time.
        /// </summary>
        /// <returns></returns>
        public static long UnixTimeSinceEpochInMicroseconds()
        {
            long microsecs = ToUnixTimeInMicroseconds(DateTime.UtcNow);

            lock (_mirosecTimeLock)
            {
                if (microsecs == _lastTimestampRecorded)
                {
                    ++_numMicrosecsAdded;
                    return _lastTimestampRecorded + _numMicrosecsAdded;
                }
                else
                {
                    _lastTimestampRecorded = microsecs;
                    _numMicrosecsAdded = 0;

                    return microsecs;
                }
            }
        }

        public static long ToUnixTimeInMicroseconds(DateTime stamp)
        {
            TimeSpan t = stamp.ToUniversalTime() - unixEpoch;
            //a tick is 100 nanoseconds
            return t.Ticks / 10L;
        }

        public static int ToUnixTime(DateTime stamp)
        {
            TimeSpan t = stamp.ToUniversalTime() - unixEpoch;
            return (int)t.TotalSeconds;
        }

        public static DateTime UnixToUTCDateTime(ulong seconds)
        {
            DateTime epoch = unixEpoch;
            DateTime utc = epoch.AddSeconds(seconds);
            return utc;
        }

        public static DateTime UnixToUTCDateTime(int seconds)
        {
            DateTime epoch = unixEpoch;
            DateTime utc = epoch.AddSeconds(seconds);
            return utc;
        }

        public static DateTime UnixToLocalDateTime(ulong seconds)
        {
            DateTime epoch = unixEpoch;
            DateTime utc = epoch.AddSeconds(seconds);
            return utc.ToLocalTime();
        }

        public static DateTime UnixToLocalDateTime(int seconds)
        {
            DateTime epoch = unixEpoch;
            DateTime utc = epoch.AddSeconds(seconds);
            return utc.ToLocalTime();
        }

        /// <summary>
        /// Return an md5 hash of the given string
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public static string Md5Hash(string data)
        {
            byte[] dataMd5 = ComputeMD5Hash(data);
            return Md5HashToHexString(dataMd5);
        }

        private static string Md5HashToHexString(byte[] dataMd5)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < dataMd5.Length; i++)
                sb.AppendFormat("{0:x2}", dataMd5[i]);
            return sb.ToString();
        }

        private static byte[] ComputeMD5Hash(string data)
        {
            using (MD5 md5 = MD5.Create())
            {
                return md5.ComputeHash(Encoding.Default.GetBytes(data));
            }
        }

        public static string Md5Hash(Stream data)
        {
            byte[] hash = ComputeMD5Hash(data);
            return Md5HashToHexString(hash);
        }

        private static byte[] ComputeMD5Hash(Stream data)
        {
            using (MD5 md5 = MD5.Create())
            {
                return md5.ComputeHash(data);
            }
        }

        /// <summary>
        /// Return an SHA1 hash of the given string
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public static string SHA1Hash(string data)
        {
            byte[] hash = ComputeSHA1Hash(data);
            return BitConverter.ToString(hash).Replace("-", String.Empty);
        }

        private static byte[] ComputeSHA1Hash(string src)
        {
            SHA1CryptoServiceProvider SHA1 = new SHA1CryptoServiceProvider();
            return SHA1.ComputeHash(Encoding.Default.GetBytes(src));
        }

        public static string SHA1Hash(byte[] data)
        {
            byte[] hash = ComputeSHA1Hash(data);
            return BitConverter.ToString(hash).Replace("-", String.Empty);
        }

        private static byte[] ComputeSHA1Hash(byte[] src)
        {
            SHA1CryptoServiceProvider SHA1 = new SHA1CryptoServiceProvider();
            return SHA1.ComputeHash(src);
        }

        public static int fast_distance2d(int x, int y)
        {
            x = Math.Abs(x);
            y = Math.Abs(y);

            int min = Math.Min(x, y);

            return (x + y - (min >> 1) - (min >> 2) + (min >> 4));
        }

        public static bool IsOutsideView(uint oldx, uint newx, uint oldy, uint newy)
        {
            // Eventually this will be a function of the draw distance / camera position too.
            return (((int)Math.Abs((int)(oldx - newx)) > 1) || ((int)Math.Abs((int)(oldy - newy)) > 1));
        }

        public static string FieldToString(byte[] bytes)
        {
            return FieldToString(bytes, String.Empty);
        }

        /// <summary>
        /// Convert a variable length field (byte array) to a string, with a
        /// field name prepended to each line of the output
        /// </summary>
        /// <remarks>If the byte array has unprintable characters in it, a
        /// hex dump will be put in the string instead</remarks>
        /// <param name="bytes">The byte array to convert to a string</param>
        /// <param name="fieldName">A field name to prepend to each line of output</param>
        /// <returns>An ASCII string or a string containing a hex dump, minus
        /// the null terminator</returns>
        public static string FieldToString(byte[] bytes, string fieldName)
        {
            // Check for a common case
            if (bytes.Length == 0) return String.Empty;

            StringBuilder output = new StringBuilder();
            bool printable = true;

            for (int i = 0; i < bytes.Length; ++i)
            {
                // Check if there are any unprintable characters in the array
                if ((bytes[i] < 0x20 || bytes[i] > 0x7E) && bytes[i] != 0x09
                    && bytes[i] != 0x0D && bytes[i] != 0x0A && bytes[i] != 0x00)
                {
                    printable = false;
                    break;
                }
            }

            if (printable)
            {
                if (!String.IsNullOrEmpty(fieldName))
                {
                    output.Append(fieldName);
                    output.Append(": ");
                }

                output.Append(CleanString(Util.UTF8.GetString(bytes, 0, bytes.Length - 1)));
            }
            else
            {
                for (int i = 0; i < bytes.Length; i += 16)
                {
                    if (i != 0)
                        output.Append(Environment.NewLine);
                    if (!String.IsNullOrEmpty(fieldName))
                    {
                        output.Append(fieldName);
                        output.Append(": ");
                    }

                    for (int j = 0; j < 16; j++)
                    {
                        if ((i + j) < bytes.Length)
                            output.Append(String.Format("{0:X2} ", bytes[i + j]));
                        else
                            output.Append("   ");
                    }

                    for (int j = 0; j < 16 && (i + j) < bytes.Length; j++)
                    {
                        if (bytes[i + j] >= 0x20 && bytes[i + j] < 0x7E)
                            output.Append((char)bytes[i + j]);
                        else
                            output.Append(".");
                    }
                }
            }

            return output.ToString();
        }

        /// <summary>
        /// Converts a URL to a IPAddress
        /// </summary>
        /// <param name="url">URL Standard Format</param>
        /// <returns>A resolved IP Address</returns>
        public static IPAddress GetHostFromURL(string url)
        {
            return GetHostFromDNS(url.Split(new char[] { '/', ':' })[3]);
        }

        /// <summary>
        /// Returns a IP address from a specified DNS, favouring IPv4 addresses.
        /// </summary>
        /// <param name="dnsAddress">DNS Hostname</param>
        /// <returns>An IP address, or null</returns>
        public static IPAddress GetHostFromDNS(string dnsAddress)
        {
            // Is it already a valid IP? No need to look it up.
            IPAddress ipa;
            if (IPAddress.TryParse(dnsAddress, out ipa))
                return ipa;

            IPAddress[] hosts = null;

            // Not an IP, lookup required
            try
            {
                hosts = Dns.GetHostEntry(dnsAddress).AddressList;
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[UTIL]: An error occurred while resolving host name {0}, {1}", dnsAddress, e);

                // Still going to throw the exception on for now, since this was what was happening in the first place
                throw;
            }

            foreach (IPAddress host in hosts)
            {
                if (host.AddressFamily == AddressFamily.InterNetwork)
                {
                    return host;
                }
            }

            if (hosts.Length > 0)
                return hosts[0];

            return null;
        }

        public static Uri GetURI(string protocol, string hostname, int port, string path)
        {
            return new UriBuilder(protocol, hostname, port, path).Uri;
        }

        /// <summary>
        /// Gets a list of all local system IP addresses
        /// </summary>
        /// <returns></returns>
        public static IPAddress[] GetLocalHosts()
        {
            return Dns.GetHostAddresses(Dns.GetHostName());
        }

        public static IPAddress GetLocalHost()
        {
            IPAddress[] iplist = GetLocalHosts();

            if (iplist.Length == 0) // No accessible external interfaces
            {
                IPAddress[] loopback = Dns.GetHostAddresses("localhost");
                IPAddress localhost = loopback[0];

                return localhost;
            }

            foreach (IPAddress host in iplist)
            {
                if (!IPAddress.IsLoopback(host) && host.AddressFamily == AddressFamily.InterNetwork)
                {
                    return host;
                }
            }

            if (iplist.Length > 0)
            {
                foreach (IPAddress host in iplist)
                {
                    if (host.AddressFamily == AddressFamily.InterNetwork)
                        return host;
                }
                // Well all else failed...
                return iplist[0];
            }

            return null;
        }


        public static bool IsLocal(IPAddress address)
        {
            if (address == null)
                throw new ArgumentNullException("address");

            byte[] addr = address.GetAddressBytes();

            return ((addr[0] == 10) ||
                    (addr[0] == 192 && addr[1] == 168) ||
                    (addr[0] == 172 && addr[1] >= 16 && addr[1] <= 31));
        }

        /// <summary>
        /// Get the local IP Address of this host
        /// </summary>
        /// <returns></returns>
        public static string RetrieveListenAddress()
        {
            IPAddress[] addresses = Dns.GetHostAddresses(Dns.GetHostName());
            string first = null;

            foreach (IPAddress addr in addresses)
            {
                if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && first == null)
                {
                    first = addr.ToString();
                }

                if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IsLocal(addr))
                {
                    return addr.ToString();
                }
            }

            //couldnt find a public IP return the first private (probably a standalone)
            if (addresses.Length > 0)
            {
                return first;
            }
            else
            {
                return "0.0.0.0";
            }
        }



        /// <summary>
        /// Removes all invalid path chars (OS dependent)
        /// </summary>
        /// <param name="path">path</param>
        /// <returns>safe path</returns>
        public static string safePath(string path)
        {
            return Regex.Replace(path, regexInvalidPathChars, String.Empty);
        }

        /// <summary>
        /// Removes all invalid filename chars (OS dependent)
        /// </summary>
        /// <param name="path">filename</param>
        /// <returns>safe filename</returns>
        public static string safeFileName(string filename)
        {
            return Regex.Replace(filename, regexInvalidFileChars, String.Empty);
            ;
        }

        //
        // directory locations
        //

        public static string homeDir()
        {
            string temp;
            //            string personal=(Environment.GetFolderPath(Environment.SpecialFolder.Personal));
            //            temp = Path.Combine(personal,".OpenSim");
            temp = ".";
            return temp;
        }

        public static string assetsDir()
        {
            return Path.Combine(configDir(), "assets");
        }

        public static string inventoryDir()
        {
            return Path.Combine(configDir(), "inventory");
        }

        public static string configDir()
        {
            return ".";
        }

        public static string dataDir()
        {
            return ".";
        }

        public static string logDir()
        {
            return ".";
        }

        // From: http://coercedcode.blogspot.com/2008/03/c-generate-unique-filenames-within.html
        public static string GetUniqueFilename(string FileName)
        {
            int count = 0;
            string Name;

            if (File.Exists(FileName))
            {
                FileInfo f = new FileInfo(FileName);

                if (!String.IsNullOrEmpty(f.Extension))
                {
                    Name = f.FullName.Substring(0, f.FullName.LastIndexOf('.'));
                }
                else
                {
                    Name = f.FullName;
                }

                while (File.Exists(FileName))
                {
                    count++;
                    FileName = Name + count + f.Extension;
                }
            }
            return FileName;
        }

        // Nini (config) related Methods
        public static IConfigSource ConvertDataRowToXMLConfig(DataRow row, string fileName)
        {
            if (!File.Exists(fileName))
            {
                //create new file
            }
            XmlConfigSource config = new XmlConfigSource(fileName);
            AddDataRowToConfig(config, row);
            config.Save();

            return config;
        }

        public static void AddDataRowToConfig(IConfigSource config, DataRow row)
        {
            config.Configs.Add((string)row[0]);
            for (int i = 0; i < row.Table.Columns.Count; i++)
            {
                config.Configs[(string)row[0]].Set(row.Table.Columns[i].ColumnName, row[i]);
            }
        }

        public static float Clip(float x, float min, float max)
        {
            return Math.Min(Math.Max(x, min), max);
        }

        public static int Clip(int x, int min, int max)
        {
            return Math.Min(Math.Max(x, min), max);
        }

        public static Vector3 Clip(Vector3 vec, float min, float max)
        {
            return new Vector3(Clip(vec.X, min, max), Clip(vec.Y, min, max),
                Clip(vec.Z, min, max));
        }

        /// <summary>
        /// Convert an UUID to a raw uuid string.  Right now this is a string without hyphens.
        /// </summary>
        /// <param name="UUID"></param>
        /// <returns></returns>
        public static String ToRawUuidString(UUID UUID)
        {
            return UUID.Guid.ToString("n");
        }

        public static string CleanString(string input)
        {
            if (String.IsNullOrEmpty(input))
                return String.Empty;

            int clip = input.Length;

            // Test for ++ string terminator
            int pos = input.IndexOf("\0");
            if (pos != -1 && pos < clip)
                clip = pos;

            // Test for CR
            pos = input.IndexOf("\r");
            if (pos != -1 && pos < clip)
                clip = pos;

            // Test for LF
            pos = input.IndexOf("\n");
            if (pos != -1 && pos < clip)
                clip = pos;

            // Truncate string before first end-of-line character found
            return input.Substring(0, clip);
        }

        /// <summary>
        /// returns the contents of /etc/issue on Unix Systems
        /// Use this for where it's absolutely necessary to implement platform specific stuff
        /// </summary>
        /// <returns></returns>
        public static string ReadEtcIssue()
        {
            try
            {
                StreamReader sr = new StreamReader("/etc/issue.net");
                string issue = sr.ReadToEnd();
                sr.Close();
                return issue;
            }
            catch (Exception)
            {
                return String.Empty;
            }
        }

        public static void SerializeToFile(string filename, Object obj)
        {
            IFormatter formatter = new BinaryFormatter();
            Stream stream = null;

            try
            {
                stream = new FileStream(
                    filename, FileMode.Create,
                    FileAccess.Write, FileShare.None);

                formatter.Serialize(stream, obj);
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
            }
            finally
            {
                if (stream != null)
                {
                    stream.Close();
                }
            }
        }

        public static Object DeserializeFromFile(string filename)
        {
            IFormatter formatter = new BinaryFormatter();
            Stream stream = null;
            Object ret = null;

            try
            {
                stream = new FileStream(
                    filename, FileMode.Open,
                    FileAccess.Read, FileShare.None);

                ret = formatter.Deserialize(stream);
            }
            catch (Exception e)
            {
                m_log.Error(e.ToString());
            }
            finally
            {
                if (stream != null)
                {
                    stream.Close();
                }
            }

            return ret;
        }

        /// <summary>
        /// Copy data from one stream to another, leaving the read position of both streams at the beginning.
        /// </summary>
        /// <param name='inputStream'>
        /// Input stream.  Must be seekable.
        /// </param>
        /// <exception cref='ArgumentException'>
        /// Thrown if the input stream is not seekable.
        /// </exception>
        public static Stream Copy(Stream inputStream)
        {
            if (!inputStream.CanSeek)
                throw new ArgumentException("Util.Copy(Stream inputStream) must receive an inputStream that can seek");

            const int readSize = 256;
            byte[] buffer = new byte[readSize];
            MemoryStream ms = new MemoryStream();
        
            int count = inputStream.Read(buffer, 0, readSize);

            while (count > 0)
            {
                ms.Write(buffer, 0, count);
                count = inputStream.Read(buffer, 0, readSize);
            }

            ms.Position = 0;
            inputStream.Position = 0;

            return ms;
        }

        public static XmlRpcResponse XmlRpcCommand(string url, string methodName, params object[] args)
        {
            return SendXmlRpcCommand(url, methodName, args);
        }

        public static XmlRpcResponse SendXmlRpcCommand(string url, string methodName, object[] args)
        {
            XmlRpcRequest client = new XmlRpcRequest(methodName, args);
            return client.Send(url, 6000);
        }

        /// <summary>
        /// Returns an error message that the user could not be found in the database
        /// </summary>
        /// <returns>XML string consisting of a error element containing individual error(s)</returns>
        public static XmlRpcResponse CreateUnknownUserErrorResponse()
        {
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable responseData = new Hashtable();
            responseData["error_type"] = "unknown_user";
            responseData["error_desc"] = "The user requested is not in the database";

            response.Value = responseData;
            return response;
        }

        /// <summary>
        /// Returns an error message that this endpoint is not permitted access
        /// </summary>
        /// <returns>XML string consisting of a error element containing individual error(s)</returns>
        public static XmlRpcResponse CreateTrustManagerAccessDeniedResponse()
        {
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable responseData = new Hashtable();
            responseData["error_type"] = "access_denied";
            responseData["error_desc"] = "Access is denied for this IP Endpoint";

            response.Value = responseData;
            return response;
        }

        /// <summary>
        /// Converts a byte array in big endian order into an ulong.
        /// </summary>
        /// <param name="bytes">
        /// The array of bytes
        /// </param>
        /// <returns>
        /// The extracted ulong
        /// </returns>
        public static ulong BytesToUInt64Big(byte[] bytes)
        {
            if (bytes.Length < 8) return 0;
            return ((ulong)bytes[0] << 56) | ((ulong)bytes[1] << 48) | ((ulong)bytes[2] << 40) | ((ulong)bytes[3] << 32) |
                ((ulong)bytes[4] << 24) | ((ulong)bytes[5] << 16) | ((ulong)bytes[6] << 8) | (ulong)bytes[7];
        }

        // used for RemoteParcelRequest (for "About Landmark")
        public static UUID BuildFakeParcelID(ulong regionHandle, uint x, uint y)
        {
            byte[] bytes =
            {
                (byte)regionHandle, (byte)(regionHandle >> 8), (byte)(regionHandle >> 16), (byte)(regionHandle >> 24),
                (byte)(regionHandle >> 32), (byte)(regionHandle >> 40), (byte)(regionHandle >> 48), (byte)(regionHandle << 56),
                (byte)x, (byte)(x >> 8), 0, 0,
                (byte)y, (byte)(y >> 8), 0, 0 };
            return new UUID(bytes, 0);
        }

        public static UUID BuildFakeParcelID(ulong regionHandle, uint x, uint y, uint z)
        {
            byte[] bytes =
            {
                (byte)regionHandle, (byte)(regionHandle >> 8), (byte)(regionHandle >> 16), (byte)(regionHandle >> 24),
                (byte)(regionHandle >> 32), (byte)(regionHandle >> 40), (byte)(regionHandle >> 48), (byte)(regionHandle << 56),
                (byte)x, (byte)(x >> 8), (byte)z, (byte)(z >> 8),
                (byte)y, (byte)(y >> 8), 0, 0 };
            return new UUID(bytes, 0);
        }

        public static void ParseFakeParcelID(UUID parcelID, out ulong regionHandle, out uint x, out uint y)
        {
            byte[] bytes = parcelID.GetBytes();
            regionHandle = Utils.BytesToUInt64(bytes);
            x = Utils.BytesToUInt(bytes, 8) & 0xffff;
            y = Utils.BytesToUInt(bytes, 12) & 0xffff;
        }

        public static void ParseFakeParcelID(UUID parcelID, out ulong regionHandle, out uint x, out uint y, out uint z)
        {
            byte[] bytes = parcelID.GetBytes();
            regionHandle = Utils.BytesToUInt64(bytes);
            x = Utils.BytesToUInt(bytes, 8) & 0xffff;
            z = (Utils.BytesToUInt(bytes, 8) & 0xffff0000) >> 16;
            y = Utils.BytesToUInt(bytes, 12) & 0xffff;
        }

        public static void FakeParcelIDToGlobalPosition(UUID parcelID, out uint x, out uint y)
        {
            ulong regionHandle;
            uint rx, ry;

            ParseFakeParcelID(parcelID, out regionHandle, out x, out y);
            Utils.LongToUInts(regionHandle, out rx, out ry);

            x += rx;
            y += ry;
        }

        /// <summary>
        /// Get operating system information if available.  Returns only the first 45 characters of information
        /// </summary>
        /// <returns>
        /// Operating system information.  Returns an empty string if none was available.
        /// </returns>
        public static string GetOperatingSystemInformation()
        {
            string os = String.Empty;

            if (Environment.OSVersion.Platform != PlatformID.Unix)
            {
                os = Environment.OSVersion.ToString();
            }
            else
            {
                os = ReadEtcIssue();
            }

            if (os.Length > 45)
            {
                os = os.Substring(0, 45);
            }

            return os;
        }

        public static string GetRuntimeInformation()
        {
            string ru = String.Empty;

            if (Environment.OSVersion.Platform == PlatformID.Unix)
                ru = "Unix/Mono";
            else
                if (Environment.OSVersion.Platform == PlatformID.MacOSX)
                    ru = "OSX/Mono";
                else
                {
                    if (Type.GetType("Mono.Runtime") != null)
                        ru = "Win/Mono";
                    else
                        ru = "Win/.NET";
                }

            return ru;
        }

        /// <summary>
        /// Is the given string a UUID?
        /// </summary>
        /// <param name="s"></param>
        /// <returns></returns>
        public static bool isUUID(string s)
        {
            return UUIDPattern.IsMatch(s);
        }

        public static string GetDisplayConnectionString(string connectionString)
        {
            int passPosition = 0;
            int passEndPosition = 0;
            string displayConnectionString = null;

            // hide the password in the connection string
            passPosition = connectionString.IndexOf("password", StringComparison.OrdinalIgnoreCase);

            if (passPosition != -1)
            {
                passPosition = connectionString.IndexOf("=", passPosition);
                if (passPosition < connectionString.Length)
                    passPosition += 1;
                passEndPosition = connectionString.IndexOf(";", passPosition);

                displayConnectionString = connectionString.Substring(0, passPosition);
                displayConnectionString += "***";
                if (passEndPosition >= 0)
                    displayConnectionString += connectionString.Substring(passEndPosition, connectionString.Length - passEndPosition);
            }
            else
            {
                displayConnectionString = connectionString;
            }

            return displayConnectionString;
        }

        public static T ReadSettingsFromIniFile<T>(IConfig config, T settingsClass)
        {
            Type settingsType = settingsClass.GetType();

            FieldInfo[] fieldInfos = settingsType.GetFields();
            foreach (FieldInfo fieldInfo in fieldInfos)
            {
                if (!fieldInfo.IsStatic)
                {
                    if (fieldInfo.FieldType == typeof(System.String))
                    {
                        fieldInfo.SetValue(settingsClass, config.Get(fieldInfo.Name, (string)fieldInfo.GetValue(settingsClass)));
                    }
                    else if (fieldInfo.FieldType == typeof(System.Boolean))
                    {
                        fieldInfo.SetValue(settingsClass, config.GetBoolean(fieldInfo.Name, (bool)fieldInfo.GetValue(settingsClass)));
                    }
                    else if (fieldInfo.FieldType == typeof(System.Int32))
                    {
                        fieldInfo.SetValue(settingsClass, config.GetInt(fieldInfo.Name, (int)fieldInfo.GetValue(settingsClass)));
                    }
                    else if (fieldInfo.FieldType == typeof(System.Single))
                    {
                        fieldInfo.SetValue(settingsClass, config.GetFloat(fieldInfo.Name, (float)fieldInfo.GetValue(settingsClass)));
                    }
                    else if (fieldInfo.FieldType == typeof(System.UInt32))
                    {
                        fieldInfo.SetValue(settingsClass, Convert.ToUInt32(config.Get(fieldInfo.Name, ((uint)fieldInfo.GetValue(settingsClass)).ToString())));
                    }
                }
            }

            PropertyInfo[] propertyInfos = settingsType.GetProperties();
            foreach (PropertyInfo propInfo in propertyInfos)
            {
                if ((propInfo.CanRead) && (propInfo.CanWrite))
                {
                    if (propInfo.PropertyType == typeof(System.String))
                    {
                        propInfo.SetValue(settingsClass, config.Get(propInfo.Name, (string)propInfo.GetValue(settingsClass, null)), null);
                    }
                    else if (propInfo.PropertyType == typeof(System.Boolean))
                    {
                        propInfo.SetValue(settingsClass, config.GetBoolean(propInfo.Name, (bool)propInfo.GetValue(settingsClass, null)), null);
                    }
                    else if (propInfo.PropertyType == typeof(System.Int32))
                    {
                        propInfo.SetValue(settingsClass, config.GetInt(propInfo.Name, (int)propInfo.GetValue(settingsClass, null)), null);
                    }
                    else if (propInfo.PropertyType == typeof(System.Single))
                    {
                        propInfo.SetValue(settingsClass, config.GetFloat(propInfo.Name, (float)propInfo.GetValue(settingsClass, null)), null);
                    }
                    if (propInfo.PropertyType == typeof(System.UInt32))
                    {
                        propInfo.SetValue(settingsClass, Convert.ToUInt32(config.Get(propInfo.Name, ((uint)propInfo.GetValue(settingsClass, null)).ToString())), null);
                    }
                }
            }

            return settingsClass;
        }

        public static string Base64ToString(string str)
        {
            UTF8Encoding encoder = new UTF8Encoding();
            Decoder utf8Decode = encoder.GetDecoder();

            byte[] todecode_byte = Convert.FromBase64String(str);
            int charCount = utf8Decode.GetCharCount(todecode_byte, 0, todecode_byte.Length);
            char[] decoded_char = new char[charCount];
            utf8Decode.GetChars(todecode_byte, 0, todecode_byte.Length, decoded_char, 0);
            string result = new String(decoded_char);
            return result;
        }

        public static string StringToBase64(string str)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(str));
        }

        public static Guid GetHashGuid(string data, string salt)
        {
            byte[] hash = ComputeMD5Hash(data + salt);

            //string s = BitConverter.ToString(hash);

            Guid guid = new Guid(hash);

            return guid;
        }

        public static byte ConvertMaturityToAccessLevel(uint maturity)
        {
            byte retVal = 0;
            switch (maturity)
            {
                case 0: //PG
                    retVal = 13;
                    break;
                case 1: //Mature
                    retVal = 21;
                    break;
                case 2: // Adult
                    retVal = 42;
                    break;
            }

            return retVal;

        }

        /// <summary>
        /// Produces an OSDMap from its string representation on a stream
        /// </summary>
        /// <param name="data">The stream</param>
        /// <param name="length">The size of the data on the stream</param>
        /// <returns>The OSDMap or an exception</returns>
        public static OSDMap GetOSDMap(Stream stream, int length)
        {
            byte[] data = new byte[length];
            stream.Read(data, 0, length);
            string strdata = Util.UTF8.GetString(data);
            OSDMap args = null;
            OSD buffer;
            buffer = OSDParser.DeserializeJson(strdata);
            if (buffer.Type == OSDType.Map)
            {
                args = (OSDMap)buffer;
                return args;
            }
            return null;
        }

        public static OSDMap GetOSDMap(string data)
        {
            OSDMap args = null;
            try
            {
                OSD buffer;
                // We should pay attention to the content-type, but let's assume we know it's Json
                buffer = OSDParser.DeserializeJson(data);
                if (buffer.Type == OSDType.Map)
                {
                    args = (OSDMap)buffer;
                    return args;
                }
                else
                {
                    // uh?
                    m_log.Debug(("[UTILS]: Got OSD of unexpected type " + buffer.Type.ToString()));
                    return null;
                }
            }
            catch (Exception ex)
            {
                m_log.Debug("[UTILS]: exception on GetOSDMap " + ex.Message);
                return null;
            }
        }

        public static string[] Glob(string path)
        {
            string vol = String.Empty;

            if (Path.VolumeSeparatorChar != Path.DirectorySeparatorChar)
            {
                string[] vcomps = path.Split(new char[] { Path.VolumeSeparatorChar }, 2, StringSplitOptions.RemoveEmptyEntries);

                if (vcomps.Length > 1)
                {
                    path = vcomps[1];
                    vol = vcomps[0];
                }
            }

            string[] comps = path.Split(new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            // Glob

            path = vol;
            if (!String.IsNullOrEmpty(vol))
                path += new String(new char[] { Path.VolumeSeparatorChar, Path.DirectorySeparatorChar });
            else
                path = new String(new char[] { Path.DirectorySeparatorChar });

            List<string> paths = new List<string>();
            List<string> found = new List<string>();
            paths.Add(path);

            int compIndex = -1;
            foreach (string c in comps)
            {
                compIndex++;

                List<string> addpaths = new List<string>();
                foreach (string p in paths)
                {
                    string[] dirs = Directory.GetDirectories(p, c);

                    if (dirs.Length != 0)
                    {
                        foreach (string dir in dirs)
                            addpaths.Add(Path.Combine(path, dir));
                    }

                    // Only add files if that is the last path component
                    if (compIndex == comps.Length - 1)
                    {
                        string[] files = Directory.GetFiles(p, c);
                        foreach (string f in files)
                            found.Add(f);
                    }
                }
                paths = addpaths;
            }

            return found.ToArray();
        }

        public static string ServerURI(string uri)
        {
            if (String.IsNullOrEmpty(uri))
                return String.Empty;

            // Get rid of eventual slashes at the end
            uri = uri.TrimEnd('/');

            IPAddress ipaddr1 = null;
            string port1 = String.Empty;
            try
            {
                ipaddr1 = Util.GetHostFromURL(uri);
            }
            catch { }

            try
            {
                port1 = uri.Split(new char[] { ':' })[2];
            }
            catch { }

            // We tried our best to convert the domain names to IP addresses
            return (ipaddr1 != null) ? "http://" + ipaddr1.ToString() + ":" + port1 : uri;
        }

        public static string XmlRpcRequestPrefix(string methodName)
        {
            string prefix = "/xmlrpc";
            if (!String.IsNullOrEmpty(methodName))
                prefix += ("/" + methodName);
            return (prefix);
        }

        public static string XmlRpcRequestURI(string url, string methodName)
        {
            return (ServerURI(url) + XmlRpcRequestPrefix(methodName));
        }


        /// <summary>
        /// Convert a string to a byte format suitable for transport in an LLUDP packet.  The output is truncated to 256 bytes if necessary.
        /// </summary>
        /// <param name="str">
        /// If null or empty, then an bytes[0] is returned.
        /// Using "\0" will return a conversion of the null character to a byte.  This is not the same as bytes[0]
        /// </param>
        /// <param name="args">
        /// Arguments to substitute into the string via the {} mechanism.
        /// </param>
        /// <returns></returns>
        public static byte[] StringToBytes256(string str, params object[] args)
        {
            return StringToBytes256(string.Format(str, args));
        }

        /// <summary>
        /// Convert a string to a byte format suitable for transport in an LLUDP packet.  The output is truncated to 256 bytes if necessary.
        /// </summary>
        /// <param name="str">
        /// If null or empty, then an bytes[0] is returned.
        /// Using "\0" will return a conversion of the null character to a byte.  This is not the same as bytes[0]
        /// </param>
        /// <returns></returns>
        public static byte[] StringToBytes256(string str)
        {
            if (String.IsNullOrEmpty(str)) { return Utils.EmptyBytes; }
            if (str.Length > 254) str = str.Remove(254);
            if (!str.EndsWith("\0")) { str += "\0"; }
            
            // Because this is UTF-8 encoding and not ASCII, it's possible we
            // might have gotten an oversized array even after the string trim
            byte[] data = UTF8.GetBytes(str);
            if (data.Length > 256)
            {
                Array.Resize<byte>(ref data, 256);
                data[255] = 0;
            }

            return data;
        }

        /// <summary>
        /// Convert a string to a byte format suitable for transport in an LLUDP packet.  The output is truncated to 1024 bytes if necessary.
        /// </summary>
        /// <param name="str">
        /// If null or empty, then an bytes[0] is returned.
        /// Using "\0" will return a conversion of the null character to a byte.  This is not the same as bytes[0]
        /// </param>
        /// <param name="args">
        /// Arguments to substitute into the string via the {} mechanism.
        /// </param>
        /// <returns></returns>
        public static byte[] StringToBytes1024(string str, params object[] args)
        {
            return StringToBytes1024(string.Format(str, args));
        }

        /// <summary>
        /// Convert a string to a byte format suitable for transport in an LLUDP packet.  The output is truncated to 1024 bytes if necessary.
        /// </summary>
        /// <param name="str">
        /// If null or empty, then an bytes[0] is returned.
        /// Using "\0" will return a conversion of the null character to a byte.  This is not the same as bytes[0]
        /// </param>
        /// <returns></returns>
        public static byte[] StringToBytes1024(string str)
        {
            if (String.IsNullOrEmpty(str)) { return Utils.EmptyBytes; }
            if (str.Length > 1023) str = str.Remove(1023);
            if (!str.EndsWith("\0")) { str += "\0"; }

            // Because this is UTF-8 encoding and not ASCII, it's possible we
            // might have gotten an oversized array even after the string trim
            byte[] data = UTF8.GetBytes(str);
            if (data.Length > 1024)
            {
                Array.Resize<byte>(ref data, 1024);
                data[1023] = 0;
            }

            return data;
        }

        #region FireAndForget Threading Pattern

        /// <summary>
        /// Created to work around a limitation in Mono with nested delegates
        /// </summary>
        private class FireAndForgetWrapper
        {
            public void FireAndForget(System.Threading.WaitCallback callback)
            {
                callback.BeginInvoke(null, EndFireAndForget, callback);
            }

            public void FireAndForget(System.Threading.WaitCallback callback, object obj)
            {
                callback.BeginInvoke(obj, EndFireAndForget, callback);
            }

            private static void EndFireAndForget(IAsyncResult ar)
            {
                System.Threading.WaitCallback callback = (System.Threading.WaitCallback)ar.AsyncState;

                try { callback.EndInvoke(ar); }
                catch (Exception ex) { m_log.Error("[UTIL]: Asynchronous method threw an exception: " + ex.Message, ex); }

                ar.AsyncWaitHandle.Close();
            }
        }

        public static void FireAndForget(System.Threading.WaitCallback callback)
        {
            FireAndForget(callback, null);
        }

        public static void InitThreadPool(PoolSelection which, int maxThreads)
        {
            if (maxThreads < 2)
                throw new ArgumentOutOfRangeException("maxThreads", "maxThreads must be greater than 2");
            if (m_ThreadPool[(int)which] != null)
                throw new InvalidOperationException("SmartThreadPool is already initialized");

            m_ThreadPool[(int)which] = new SmartThreadPool(5 * 60 * 1000, maxThreads, 2);
        }

        public static int FireAndForgetCount()
        {
            const int MAX_SYSTEM_THREADS = 200;

            switch (FireAndForgetMethod)
            {
                case FireAndForgetMethod.UnsafeQueueUserWorkItem:
                case FireAndForgetMethod.QueueUserWorkItem:
                case FireAndForgetMethod.BeginInvoke:
                    int workerThreads, iocpThreads;
                    ThreadPool.GetAvailableThreads(out workerThreads, out iocpThreads);
                    return workerThreads;
                case FireAndForgetMethod.SmartThreadPool:
                    return m_ThreadPool[0].MaxThreads - m_ThreadPool[0].InUseThreads;
                case FireAndForgetMethod.Thread:
                    return MAX_SYSTEM_THREADS - System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
                default:
                    throw new NotImplementedException();
            }
        }

        public static void FireAndForget(PoolSelection whichPool, System.Threading.WaitCallback callback, object obj)
        {
            WorkItemCallback stpCallback = SmartThreadPoolCallback;
            m_ThreadPool[(int)whichPool].QueueWorkItem(stpCallback, new object[] { callback, obj });
        }

        public static void FireAndForget(PoolSelection whichPool, System.Threading.WaitCallback callback)
        {
            FireAndForget(whichPool, callback, null);
        }

        public static void FireAndForget(System.Threading.WaitCallback callback, object obj)
        {
            switch (FireAndForgetMethod)
            {
                case FireAndForgetMethod.UnsafeQueueUserWorkItem:
                    ThreadPool.UnsafeQueueUserWorkItem(callback, obj);
                    break;
                case FireAndForgetMethod.QueueUserWorkItem:
                    ThreadPool.QueueUserWorkItem(callback, obj);
                    break;
                case FireAndForgetMethod.BeginInvoke:
                    FireAndForgetWrapper wrapper = Singleton.GetInstance<FireAndForgetWrapper>();
                    wrapper.FireAndForget(callback, obj);
                    break;
                case FireAndForgetMethod.SmartThreadPool:
                    FireAndForget(PoolSelection.Default, callback, obj);
                    break;
                case FireAndForgetMethod.Thread:
                    Thread thread = new Thread(delegate(object o) { callback(o); });
                    thread.Start(obj);
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        private static object SmartThreadPoolCallback(object o)
        {
            object[] array = (object[])o;
            WaitCallback callback = (WaitCallback)array[0];
            object obj = array[1];

            try
            {
                callback(obj);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[FIREANDFORGET] Call threw an exception {0}", e);
            }
            return null;    // not used, effectively a void function, but must match WorkItemCallback
        }

        #endregion FireAndForget Threading Pattern

        /// <summary>
        /// Environment.TickCount is an int but it counts all 32 bits so it goes positive
        /// and negative every 24.9 days. This trims down TickCount so it doesn't wrap
        /// for the callers. 
        /// This trims it to a 12 day interval so don't let your frame time get too long.
        /// </summary>
        /// <returns></returns>
        public static Int32 EnvironmentTickCount()
        {
            return Environment.TickCount & EnvironmentTickCountMask;
        }
        const Int32 EnvironmentTickCountMask = 0x3fffffff;

        /// <summary>
        /// Environment.TickCount is an int but it counts all 32 bits so it goes positive
        /// and negative every 24.9 days. Subtracts the passed value (previously fetched by
        /// 'EnvironmentTickCount()') and accounts for any wrapping.
        /// </summary>
        /// <param name="newValue"></param>
        /// <param name="prevValue"></param>
        /// <returns>subtraction of passed prevValue from current Environment.TickCount</returns>
        public static Int32 EnvironmentTickCountSubtract(Int32 newValue, Int32 prevValue)
        {
            Int32 diff = newValue - prevValue;
            return (diff >= 0) ? diff : (diff + EnvironmentTickCountMask + 1);
        }

        /// <summary>
        /// Environment.TickCount is an int but it counts all 32 bits so it goes positive
        /// and negative every 24.9 days. Subtracts the passed value (previously fetched by
        /// 'EnvironmentTickCount()') and accounts for any wrapping.
        /// </summary>
        /// <returns>subtraction of passed prevValue from current Environment.TickCount</returns>
        public static Int32 EnvironmentTickCountSubtract(Int32 prevValue)
        {
            return EnvironmentTickCountSubtract(EnvironmentTickCount(), prevValue);
        }

        // Returns value of Tick Count A - TickCount B accounting for wrapping of TickCount
        // Assumes both tcA and tcB came from previous calls to Util.EnvironmentTickCount().
        // A positive return value indicates A occured later than B
        public static Int32 EnvironmentTickCountCompare(Int32 tcA, Int32 tcB)
        {
            // A, B and TC are all between 0 and 0x3fffffff
            int tc = EnvironmentTickCount();

            if (tc - tcA >= 0)
                tcA += EnvironmentTickCountMask + 1;

            if (tc - tcB >= 0)
                tcB += EnvironmentTickCountMask + 1;

            return tcA - tcB;
        }

        /// <summary>
        /// Prints the call stack at any given point. Useful for debugging.
        /// </summary>
        public static void PrintCallStack()
        {
            StackTrace stackTrace = new StackTrace();           // get call stack
            StackFrame[] stackFrames = stackTrace.GetFrames();  // get method calls (frames)

            // write call stack method names
            foreach (StackFrame stackFrame in stackFrames)
            {
                m_log.Debug(stackFrame.GetMethod().DeclaringType + "." + stackFrame.GetMethod().Name); // write method name
            }
        }


        public enum ColorFormat
        {
            NamedColor,
            ARGBColor
        }

        public static string SerializeColor(Color color)
        {
            if (color.IsNamedColor)
                return string.Format("{0}:{1}",
                    ColorFormat.NamedColor, color.Name);
            else
                return string.Format("{0}:{1}:{2}:{3}:{4}",
                    ColorFormat.ARGBColor,
                    color.A, color.R, color.G, color.B);
        }

        public static Color DeserializeColor(string color)
        {
            byte a, r, g, b;

            string[] pieces = color.Split(new char[] { ':' });

            ColorFormat colorType = (ColorFormat)
                Enum.Parse(typeof(ColorFormat), pieces[0], true);

            switch (colorType)
            {
                case ColorFormat.NamedColor:
                    return Color.FromName(pieces[1]);

                case ColorFormat.ARGBColor:
                    a = byte.Parse(pieces[1]);
                    r = byte.Parse(pieces[2]);
                    g = byte.Parse(pieces[3]);
                    b = byte.Parse(pieces[4]);

                    return Color.FromArgb(a, r, g, b);
            }
            return Color.Empty;
        }

        public const double DefaultComparePrecision = 0.00001;

        public static bool AlmostEquals(double double1, double double2, double precision)
        {
            return (Math.Abs(double1 - double2) <= precision);
        }

        public static bool AlmostEquals(Vector3 v1, Vector3 v2, double precision)
        {
            return  Math.Abs(v1.X - v2.X) <= precision &&
                    Math.Abs(v1.Y - v2.Y) <= precision &&
                    Math.Abs(v1.Z - v2.Z) <= precision;
        }

        public static bool AlmostEquals(Quaternion q1, Quaternion q2, double precision)
        {
            return  Math.Abs(q1.X - q2.X) <= precision &&
                    Math.Abs(q1.Y - q2.Y) <= precision &&
                    Math.Abs(q1.Z - q2.Z) <= precision &&
                    Math.Abs(q1.W - q2.W) <= precision;
        }

        public static bool NotEquals(double double1, double double2, double precision)
        {
            return !AlmostEquals(double1, double2, precision);
        }

        public static bool NotEquals(Vector3 v1, Vector3 v2, double precision)
        {
            return !AlmostEquals(v1, v2, precision);
        }

        public static bool NotEquals(Quaternion q1, Quaternion q2, double precision)
        {
            return !AlmostEquals(q1, q2, precision);
        }

        public static string TruncateString(string s, int maxLength)
        {
            if (Encoding.UTF8.GetByteCount(s) <= maxLength)
                return s;
            var cs = s.ToCharArray();
            int length = 0;
            int i = 0;
            while (i < cs.Length)
            {
                int charSize = 1;
                if (i < (cs.Length - 1) && char.IsSurrogate(cs[i]))
                    charSize = 2;
                int byteSize = Encoding.UTF8.GetByteCount(cs, i, charSize);
                if ((byteSize + length) <= maxLength)
                {
                    i = i + charSize;
                    length += byteSize;
                }
                else
                    break;
            }
            return s.Substring(0, i);
        }

        /// <summary>
        /// The set of characters that are unreserved in RFC 2396 but are NOT unreserved in RFC 3986.
        /// </summary>
        private static readonly string[] UriRfc3986CharsToEscape = new[] { "!", "*", "'", "(", ")" };

        /// <summary>
        /// Escapes a string according to the URI data string rules given in RFC 3986.
        /// </summary>
        /// <param name="value">The value to escape.</param>
        /// <returns>The escaped value.</returns>
        /// <remarks>
        /// The <see cref="Uri.EscapeDataString"/> method is <i>supposed</i> to take on
        /// RFC 3986 behavior if certain elements are present in a .config file.  Even if this
        /// actually worked (which in my experiments it <i>doesn't</i>), we can't rely on every
        /// host actually having this configuration element present.
        /// </remarks>
        public static string EscapeUriDataStringRfc3986(string value)
        {
            // Start with RFC 2396 escaping by calling the .NET method to do the work.
            // This MAY sometimes exhibit RFC 3986 behavior (according to the documentation).
            // If it does, the escaping we do that follows it will be a no-op since the
            // characters we search for to replace can't possibly exist in the string.
            StringBuilder escaped = new StringBuilder(Uri.EscapeDataString(value));

            // Upgrade the escaping to RFC 3986, if necessary.
            for (int i = 0; i < UriRfc3986CharsToEscape.Length; i++)
            {
                escaped.Replace(UriRfc3986CharsToEscape[i], Uri.HexEscape(UriRfc3986CharsToEscape[i][0]));
            }

            // Return the fully-RFC3986-escaped string.
            return escaped.ToString();
        }

        public static bool AnyAre<BaseType, DownCast>(IEnumerable<BaseType> objects)
        {
            foreach (BaseType obj in objects)
            {
                if (obj is DownCast)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool AllAre<BaseType, DownCast>(IEnumerable<BaseType> objects)
        {
            foreach (BaseType obj in objects)
            {
                if (!(obj is DownCast))
                {
                    return false;
                }
            }

            return true;
        }

        public static void ThrowIfNull(object obj, string param)
        {
            if (obj == null)
            {
                throw new ArgumentNullException(param);
            }
        }

        public static void DebugOut(string p)
        {
            m_log.Debug(p);
        }

        public static List<ArraySegment<byte>> SplitArray(byte[] barr, byte value)
        {
            List<ArraySegment<byte>> ret = new List<ArraySegment<byte>>();
            int lastOffset = 0;

            for (int i = 0; i < barr.Length; i++)
            {
                if (barr[i] == value)
                {
                    ret.Add(new ArraySegment<byte>(barr, lastOffset, i - lastOffset));
                    lastOffset = i+1;
                }
            }

            if (lastOffset < barr.Length)
            {
                ret.Add(new ArraySegment<byte>(barr, lastOffset, barr.Length - lastOffset));
            }

            return ret;
        }

        public static List<ArraySegment<byte>> SplitArraySegment(ArraySegment<byte> barr, byte value)
        {
            List<ArraySegment<byte>> ret = new List<ArraySegment<byte>>();
            int lastOffset = barr.Offset;

            for (int i = barr.Offset; i < barr.Offset + barr.Count; i++)
            {
                if (barr.Array[i] == value)
                {
                    ret.Add(new ArraySegment<byte>(barr.Array, lastOffset, i - lastOffset));
                    lastOffset = i+1;
                }
            }

            if (lastOffset < barr.Offset + barr.Count)
            {
                ret.Add(new ArraySegment<byte>(barr.Array, lastOffset, barr.Offset + barr.Count - lastOffset));
            }

            return ret;
        }

        public static string ArraySegmentToString(ArraySegment<byte> barr, Encoding enc)
        {
            return enc.GetString(barr.Array, barr.Offset, barr.Count);
        }

        public static bool IsValidRegionXYZ(float X, float Y, float Z)
        {
            if (X < Constants.OUTSIDE_REGION_NEGATIVE_EDGE || X >= Constants.OUTSIDE_REGION || 
                Y < Constants.OUTSIDE_REGION_NEGATIVE_EDGE || Y >= Constants.OUTSIDE_REGION ||
                Z < Constants.REGION_MINIMUM_Z             || Z > Constants.REGION_MAXIMUM_Z)
                return false;

            return true;
        }
        public static bool IsValidRegionXYZ(Vector3 pos)
        {
            return IsValidRegionXYZ(pos.X, pos.Y, pos.Z);
        }
        public static bool IsValidRegionXY(float X, float Y)
        {
            return IsValidRegionXYZ(X, Y, Constants.REGION_VALID_Z);
        }
        public static bool IsValidRegionXY(Vector3 pos)
        {
            return IsValidRegionXYZ(pos.X, pos.Y, Constants.REGION_VALID_Z);
        }

        public static void ForceValidRegionXYZ(ref float X, ref float Y, ref float Z)
        {
            X = Utils.Clamp(X, Constants.OUTSIDE_REGION_NEGATIVE_EDGE, Constants.OUTSIDE_REGION_POSITIVE_EDGE);
            Y = Utils.Clamp(Y, Constants.OUTSIDE_REGION_NEGATIVE_EDGE, Constants.OUTSIDE_REGION_POSITIVE_EDGE);
            Z = Utils.Clamp(Z, Constants.REGION_MINIMUM_Z, Constants.REGION_MAXIMUM_Z);
        }
        public static void ForceValidRegionXYZ(ref Vector3 pos)
        {
            ForceValidRegionXYZ(ref pos.X, ref pos.Y, ref pos.Z);
        }
        public static void ForceValidRegionXY(ref float X, ref float Y)
        {
            float Z = 0.0f;
            ForceValidRegionXYZ(ref X, ref Y, ref Z);
        }
        public static void ForceValidRegionXY(ref Vector3 pos)
        {
            float Z = 0.0f;
            ForceValidRegionXYZ(ref pos.X, ref pos.Y, ref Z);
        }

        public static Vector3 GetValidRegionXYZ(Vector3 pos)
        {
            Vector3 newPos = new Vector3(pos);
            ForceValidRegionXYZ(ref newPos);
            return newPos;
        }

        public static Vector3 GetValidRegionXY(Vector3 pos)
        {
            Vector3 newPos = new Vector3(pos);
            ForceValidRegionXY(ref newPos);
            return newPos;
        }

        /// <summary>
        /// Formats the region name and the location given into a concatenated form.  The most common usage is to create a URL path for mapping.
        /// </summary>
        /// <returns>The short code.</returns>
        /// <param name="regionName">Region name.</param>
        /// <param name="positionInRegion">Location in the region.</param>
        /// <param name="separator">Separator, defaults to "/" and is placed between the region name, and each of the X, Y, and Z parts.</param>
        public static string LocationShortCode(string regionName, Vector3 positionInRegion, string separator = "/")
        {
            int x = (int)positionInRegion.X;
            int y = (int)positionInRegion.Y;
            int z = (int)positionInRegion.Z;

            string region;

            if (String.IsNullOrEmpty(regionName))
            {
                return x + separator + y + separator + z;
            }

            try
            {
                region = Util.EscapeUriDataStringRfc3986(regionName);
            }
            catch (Exception)
            {
                region = regionName;
            }
            return region + separator + x + separator + y + separator + z;
        }

        /// <summary>
        /// The prefix to the URL links.  Set by the GridInfoService, and is also known to the viewer as "slurl_base".
        /// </summary>
        public static string LocationURLPrefix = "http://places.inworldz.com/"; // use InWorldz default until override works throughout all code modules
        /// <summary>
        /// Builds a teleport URL.
        /// </summary>
        /// <returns>The formatted URL.</returns>
        /// <param name="regionName">Region name.</param>
        /// <param name="positionInRegion">Location in the region.</param>
        public static string LocationURL(string regionName, Vector3 positionInRegion)
        {
            string separator = String.Empty;

            if (!LocationURLPrefix.EndsWith("/")) // Fix it without side effects.
            {
                separator = "/";
            }

            return LocationURLPrefix + separator + LocationShortCode(regionName, positionInRegion);
        }

        public static Vector3 EmergencyPosition()
        {
            return new Vector3(Constants.REGION_VALID_X, Constants.REGION_VALID_Y, Constants.REGION_VALID_Z);
        }

        /// <summary>
        /// Safely tries to convert various text forms of booleans or numbers to a boolean result.
        /// </summary>
        /// <param name="text">the text form of the value to convert</param>
        /// <returns>the boolean representation of it</returns>
        public static bool String2Bool(string text)
        {
            bool result;

            // Try to convert from booleans first ("False", "True")
            try
            {
                result = Convert.ToBoolean(text);
                return result;
            }
            catch (Exception)
            {
                // If that fails, it was either a number like "1" or invalid.
            }

            // Now try to convert from integers like "0" or "1"
            try
            {
                int number = Convert.ToInt16(text);
                result = (number != 0);
            }
            catch (Exception)
            {
                result = false;
            }

            return result;
        }

        /// <summary>
        /// Encapsulates a retry mechanism that will retry the given action retryCount times
        /// after catching an exception
        /// </summary>
        /// <param name="retryCount">Number of times to re-call a function while an exception is thrown</param>
        /// <param name="passthroughExceptions">List of exceptions that will cause an instant failure and rethrow without retrying</param>
        /// <param name="f">The function to execute</param>
        public static void Retry(int retryCount, IEnumerable<Type> passthroughExceptions, Action f)
        {
            for (int i = 0; i < retryCount + 1; i++)
            {
                bool threw = false;
                try
                {
                    f();
                }
                catch (Exception e)
                {
                    threw = true;

                    if (i == retryCount)
                    {
                        //cant retry anymore. rethrow
                        throw;
                    }

                    if (passthroughExceptions != null)
                    {
                        foreach (Type t in passthroughExceptions)
                        {
                            if (e.GetType().Equals(t))
                            {
                                //passthrough, rethrow
                                throw;
                            }
                        }
                    }
                }

                //no exception thrown, no need to retry
                if (!threw) break;
            }
        }

        /// <summary>
        /// Encapsulates a retry mechanism that will retry the given action retryCount times
        /// after catching an exception
        /// </summary>
        /// <param name="retryCount">Number of times to re-call a function while an exception is thrown</param>
        /// <param name="passthroughExceptions">List of exceptions that will cause an instant failure and rethrow without retrying</param>
        /// <param name="f">The function to execute</param>
        public static RetType Retry<RetType>(int retryCount, IEnumerable<Type> passthroughExceptions, Func<RetType> f)
        {
            for (int i = 0; i < retryCount + 1; i++)
            {
                try
                {
                    RetType ret = f();
                    return ret;
                }
                catch (Exception e)
                {
                    if (i == retryCount)
                    {
                        //cant retry anymore. rethrow
                        throw;
                    }

                    if (passthroughExceptions != null)
                    {
                        foreach (Type t in passthroughExceptions)
                        {
                            if (e.GetType().Equals(t))
                            {
                                //passthrough, rethrow
                                throw;
                            }
                        }
                    }
                }
            }

            return default(RetType); //should never get here
        }

        /// <summary>
        /// Returns the region handle given a region location
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        public static ulong RegionHandleFromLocation(uint x, uint y)
        {
            return Util.UIntsToLong(x * (uint)Constants.RegionSize, y * (uint)Constants.RegionSize);
        }

        public static void RegionHandleToLocation(ulong regionHandle, out uint regionX, out uint regionY)
        {
            Utils.LongToUInts(regionHandle, out regionX, out regionY);
            regionX /= Constants.RegionSize;
            regionY /= Constants.RegionSize;
        }

        public static string RegionHandleToLocationString(ulong regionHandle)
        {
            uint regionX;
            uint regionY;
            RegionHandleToLocation(regionHandle, out regionX, out regionY);
            return "("+regionX.ToString()+","+regionY.ToString()+")";
        }

        /// <summary>
        /// Generates the proper HTTP Authorization header given a key
        /// </summary>
        /// <param name="gridSendKey"></param>
        /// <returns></returns>
        public static string GenerateHttpAuthorization(string gridSendKey)
        {
            return "Basic " + Util.StringToBase64(gridSendKey + ":" + gridSendKey);
        }

        /// <summary>
        /// Checks HTTP headers for the proper authorization key
        /// </summary>
        /// <param name="expectedSendKey"></param>
        /// <param name="headers"></param>
        /// <returns></returns>
        public static bool CheckHttpAuthorization(string expectedSendKey, System.Collections.Specialized.NameValueCollection headers)
        {
            foreach (string headername in headers.AllKeys)
            {
                if (headername.ToLower() == "authorization")
                {
                    if ((string)headers[headername] == GenerateHttpAuthorization(expectedSendKey))
                    {
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Checks HTTP headers for the proper authorization key
        /// </summary>
        /// <param name="expectedSendKey"></param>
        /// <param name="headers"></param>
        /// <returns></returns>
        public static bool CheckHttpAuthorization(string expectedSendKey, Hashtable headers)
        {
            foreach (string headername in headers.Keys)
            {
                if (headername.ToLower() == "authorization")
                {
                    if ((string)headers[headername] == GenerateHttpAuthorization(expectedSendKey))
                    {
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            return false;
        }


        /// <summary>
        /// Returns x/y min and max for a draw distance based region area around the given region location
        /// </summary>
        /// <param name="drawDistance">The draw distance we're checking</param>
        /// <param name="regionLocX">The X location of the current region</param>
        /// <param name="regionLocY">The Y location of the current region</param>
        /// <param name="xmin">Outputs the X minimum for the DD rectangle</param>
        /// <param name="xmax">Outputs the X maximum for the DD rectangle</param>
        /// <param name="ymin">Outputs the Y minimum for the DD rectangle</param>
        /// <param name="ymax">Outputs the Y maximum for the DD rectangle</param>
        public static void GetDrawDistanceBasedRegionRectangle(uint drawDistance, uint maxRange, uint regionLocX, uint regionLocY, 
            out uint xmin, out uint xmax, out uint ymin, out uint ymax)
        {
            uint ddRegionWidth = GetRegionUnitsFromDD(drawDistance);

            xmin = ddRegionWidth > regionLocX ? 0U : regionLocX - ddRegionWidth;
            xmax = regionLocX + ddRegionWidth;
            ymin = ddRegionWidth > regionLocY ? 0U : regionLocY - ddRegionWidth;
            ymax = regionLocY + ddRegionWidth;

            if (maxRange > 0)   // apply it
            {
                if ((regionLocX - xmin) > maxRange)
                    xmin = regionLocX - maxRange;
                if ((regionLocY - ymin) > maxRange)
                    ymin = regionLocY - maxRange;
                if ((xmax - regionLocX) > maxRange)
                    xmax = regionLocX + maxRange;
                if ((ymax - regionLocY) > maxRange)
                    ymax = regionLocY + maxRange;
            }
        }

        /// <summary>
        /// Returns the number of regions we can see into around the current one given a DD
        /// </summary>
        /// <param name="drawDistance"></param>
        /// <returns></returns>
        public static uint GetRegionUnitsFromDD(uint drawDistance)
        {
            return (uint)Math.Ceiling((float)drawDistance / (float)Constants.RegionSize);
        }

        /// <summary>
        /// Returns whether or not the given location is within the specified DD rectangle
        /// </summary>
        /// <param name="locX">X coord of the location to test</param>
        /// <param name="locY">Y coord of the location to test</param>
        /// <param name="xmin">Rectangle X minimum</param>
        /// <param name="xmax">Rectangle X maximum</param>
        /// <param name="ymin">Rectangle Y minimum</param>
        /// <param name="ymax">Rectangle Y maximum</param>
        /// <returns>whether or not the given location is within the specified DD rectangle</returns>
        public static bool IsWithinDDRectangle(uint locX, uint locY, uint xmin, uint xmax, uint ymin, uint ymax)
        {
            if (locX >= xmin && locX <= xmax && locY >= ymin && locY <= ymax)
            {
                return true;
            }

            return false;
        }

        public static void DetermineRegionCoordinatesFromOffset(uint currentRegionX, uint currentRegionY, Vector3 offset,
            out uint regionX, out uint regionY)
        {
            int signedX = (int)currentRegionX;
            int signedY = (int)currentRegionY;

            regionX = (uint)(signedX + (int)Math.Floor((float)offset.X / (float)Constants.RegionSize));
            regionY = (uint)(signedY + (int)Math.Floor((float)offset.Y / (float)Constants.RegionSize));
        }

        public class SlowTimeReporter
        {
            // To always report the time:
            //    Util.SlowTimeReporter slowCheck = new Util.SlowTimeReporter("[FRIEND]: GetUserFriendList2 took");
            //    try
            //    {
            //        // do something that might take a while...
            //    }
            //    finally
            //    {
            //        slowCheck.Complete(true);
            //    }
// or
            // To only report over a threshold timespan:
            //    Util.SlowTimeReporter slowCheck = new Util.SlowTimeReporter("[FRIEND]: GetUserFriendList2 took", TimeSpan.FromMilliseconds(500));
            //    try
            //    {
            //        // do something that might take a while...
            //    }
            //    finally
            //    {
            //        slowCheck.Complete();
            //    }
            private string m_context = "[UTIL]: Slow operation";
            private TimeSpan m_threshold;
            private DateTime m_started;
            
            /// <summary>
            /// This class reports long processing times between its creation (or Start) and Complete() call.
            /// </summary>
            /// <param name="context">The text to report. Should not be null or empty so that the source of slowness is known.</param>
            /// <param name="silentThreshold">A TimeSpan for how many milliseconds an operation can take before it gets reported as slow.</param>
            public SlowTimeReporter(string context, TimeSpan silentThreshold)
            {
                if (context != null) m_context = context;
                m_threshold = silentThreshold;
                Start();
            }
            /// <summary>
            /// This variant provides a default of 250ms for the time threshold.
            /// </summary>
            /// <param name="context">The text to report. Should not be null or empty so that the source of slowness is known.</param>
            public SlowTimeReporter(string context)
            {
                if (context != null) m_context = context;
                m_threshold = TimeSpan.FromMilliseconds(250);
                Start();
            }

            public void Start()
            {
                m_started = DateTime.Now;
            }

            public TimeSpan Elapsed()
            {
                return DateTime.Now - m_started;
            }

            public void Complete(bool force = false)
            {
                TimeSpan elapsed = Elapsed();
                if ((!force) && (elapsed <= m_threshold))
                    return; // was fast enough

                m_log.Info(m_context + ": " + elapsed.ToString());
            }
        }

        public static void ReportIfSlow(string prefix, int millis, System.Action action)
        {
            Util.SlowTimeReporter slowCheck = new Util.SlowTimeReporter(prefix, new TimeSpan(0, 0, 0, 0, millis));
            try
            {
                action();
            }
            finally
            {
                slowCheck.Complete();
            }
        }
    }
}
