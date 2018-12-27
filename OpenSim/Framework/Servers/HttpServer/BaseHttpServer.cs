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
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Xml;
using System.Security.Cryptography.X509Certificates;
using System.Security.Authentication;
using log4net;
using Nwc.XmlRpc;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using Amib.Threading;
using System.IO.Compression;

namespace OpenSim.Framework.Servers.HttpServer
{
    public class BaseHttpServer : IHttpServer
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Gets or sets the debug level.
        /// </summary>
        /// <value>
        /// See MainServer.DebugLevel.
        /// </value>
        public int DebugLevel { get; set; }

        private volatile uint RequestNumber = 0;

        protected HttpListener m_httpListener;
        protected Dictionary<string, XmlRpcMethod> m_rpcHandlers = new Dictionary<string, XmlRpcMethod>();
        protected Dictionary<string, bool> m_rpcHandlersKeepAlive = new Dictionary<string, bool>();
        protected Dictionary<string, LLSDMethod> m_llsdHandlers = new Dictionary<string, LLSDMethod>();
        protected Dictionary<string, IRequestHandler> m_streamHandlers = new Dictionary<string, IRequestHandler>();
        protected Dictionary<string, GenericHTTPMethod> m_HTTPHandlers = new Dictionary<string, GenericHTTPMethod>();

        // The Main Thread Pool
        protected const int c_ThreadIdleTimeout = 1000;
        protected const int c_ThreadPoolMaxSize = 64;
        protected const int c_ThreadPoolMinSize = 2;
        protected SmartThreadPool m_Threads = new SmartThreadPool(c_ThreadIdleTimeout, c_ThreadPoolMaxSize, c_ThreadPoolMinSize);

        // SSL Support
        protected X509Certificate2 m_cert;
        protected SslProtocols m_sslProtocol = SslProtocols.None;
        protected bool m_isSecure;
        protected uint m_port;
        protected string m_hostName;

        protected IPAddress m_listenIPAddress = IPAddress.Any;

        public uint Port
        {
            get { return m_port; }
        }

        public bool Secure
        {
            get { return m_isSecure; }
        }

        public IPAddress ListenIPAddress
        {
            get { return m_listenIPAddress; }
            set { m_listenIPAddress = value; }
        }

        public string HostName
        {
            get { return m_hostName; }
            set { m_hostName = value; }
        }

        public string Protocol
        {
            get
            {
                string protocol = "http://";
                if (Secure)
                    protocol = "https://";
                return protocol;
            }
        }

        public string FullHostName
        {
            get
            {
                string protocol = "http://";
                if (Secure)
                    protocol = "https://";
                return protocol + m_hostName;
            }
        }

        /// <summary>
        /// A well-formed URI for the host region server (namely "http://ExternalHostName:Port)
        /// </summary>
        public string ServerURI
        {
            get
            {
                string protocol = "http://";
                if (Secure)
                    protocol = "https://";
                return protocol + m_hostName + ":" + m_port.ToString();
            }
        }

        public bool IsRunning
        {
            get { return m_httpListener.IsListening; }
        }

        public BaseHttpServer(uint port, IPAddress ipaddr)
        {
            m_Threads.Name = "HttpServer";
            m_port = port;
            m_isSecure = false;

            if (ipaddr == null)
                m_listenIPAddress = IPAddress.Any;
            else
                m_listenIPAddress = ipaddr;
        }

        private  X509Certificate2 GetCertificateFromStore(string certName)
        {
            // Get the certificate store for the current user.
            X509Store store = new X509Store(StoreLocation.CurrentUser);
            try
            {
                store.Open(OpenFlags.ReadOnly);

                // Place all certificates in an X509Certificate2Collection object.
                X509Certificate2Collection certCollection = store.Certificates;
                // If using a certificate with a trusted root you do not need to FindByTimeValid, instead:
                // currentCerts.Find(X509FindType.FindBySubjectDistinguishedName, certName, true);
                X509Certificate2Collection currentCerts = certCollection.Find(X509FindType.FindByTimeValid, DateTime.Now, false);
                X509Certificate2Collection signingCert = currentCerts.Find(X509FindType.FindBySubjectDistinguishedName, certName, false);
                if (signingCert.Count == 0)
                    return null;
                // Return the first certificate in the collection, has the right name and is current.
                return signingCert[0];
            }
            finally
            {
                store.Close();
            }
        }

        /// <summary>
        /// Set the SSL params based on lookup of the cert in the local store using
        /// the common name provided.
        /// </summary>
        /// <param name="commonName"></param>
        /// <param name="protocol"></param>
        public void SetSecureParams(string commonName, SslProtocols protocol)
        {
            m_isSecure = true; 
            m_cert = GetCertificateFromStore(commonName);
            m_sslProtocol = protocol;
        }

        /// <summary>
        /// Set the SSL params based on a filepath and password to access and unlock a certificate.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="password"></param>
        /// <param name="protocol"></param>
        public void SetSecureParams(string path, string password, SslProtocols protocol)
        {
            m_isSecure = true;
            m_cert = new X509Certificate2(path, password);
            m_sslProtocol = protocol;
        }

        /// <summary>
        /// Add a stream handler to the http server.  If the handler already exists, then nothing happens.
        /// </summary>
        /// <param name="handler"></param>
        public void AddStreamHandler(IRequestHandler handler)
        {
            string httpMethod = handler.HttpMethod;
            string path = handler.Path;
            string handlerKey = GetHandlerKey(httpMethod, path);

            lock (m_streamHandlers)
            {
                if (!m_streamHandlers.ContainsKey(handlerKey))
                {
                    // m_log.DebugFormat("[BASE HTTP SERVER]: Adding handler key {0}", handlerKey);
                    m_streamHandlers.Add(handlerKey, handler);
                }
            }
        }

        public List<string> GetStreamHandlerKeys()
        {
            lock (m_streamHandlers)
                return new List<string>(m_streamHandlers.Keys);
        }

        private static string GetHandlerKey(string httpMethod, string path)
        {
            return httpMethod + ":" + path;
        }

        public bool AddXmlRPCHandler(string method, XmlRpcMethod handler)
        {
            return AddXmlRPCHandler(method, handler, true);
        }

        public bool AddXmlRPCHandler(string method, XmlRpcMethod handler, bool keepAlive)
        {
            lock (m_rpcHandlers)
            {
                m_rpcHandlers[method] = handler;
                m_rpcHandlersKeepAlive[method] = keepAlive; // default
            }

            return true;
        }

        public XmlRpcMethod GetXmlRPCHandler(string method)
        {
            lock (m_rpcHandlers)
            {
                if (m_rpcHandlers.ContainsKey(method))
                {
                    return m_rpcHandlers[method];
                }
                else
                {
                    return null;
                }
            }
        }

        public List<string> GetXmlRpcHandlerKeys()
        {
            lock (m_rpcHandlers)
                return new List<string>(m_rpcHandlers.Keys);
        }

        public bool AddHTTPHandler(string methodName, GenericHTTPMethod handler)
        {
            //m_log.DebugFormat("[BASE HTTP SERVER]: Registering {0}", methodName);

            lock (m_HTTPHandlers)
            {
                if (!m_HTTPHandlers.ContainsKey(methodName))
                {
                    m_HTTPHandlers.Add(methodName, handler);
                    return true;
                }
            }

            //must already have a handler for that path so return false
            return false;
        }

        public List<string> GetHTTPHandlerKeys()
        {
            lock (m_HTTPHandlers)
                return new List<string>(m_HTTPHandlers.Keys);
        }
        public bool AddLLSDHandler(string path, LLSDMethod handler)
        {
            lock (m_llsdHandlers)
            {
                if (!m_llsdHandlers.ContainsKey(path))
                {
                    m_llsdHandlers.Add(path, handler);
                    return true;
                }
            }
            return false;
        }

        public List<string> GetLLSDHandlerKeys()
        {
            lock (m_llsdHandlers)
                return new List<string>(m_llsdHandlers.Keys);
        }

        private void OnRequest(IAsyncResult result)
        {
            if (m_httpListener == null)
                return;

            try
            {
                HttpListenerContext context = m_httpListener.EndGetContext(result);

                // *** Immediately set up the next context
                if (m_httpListener.IsListening)
                    m_httpListener.BeginGetContext(new AsyncCallback(OnRequest), m_httpListener);

                m_Threads.QueueWorkItem(HandleRequest, context);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[BASE HTTP SERVER]: OnRequest() failed with {0}{1}", e.Message, e.StackTrace);
            }
        }

        /// <summary>
        /// This methods is the start of incoming HTTP request handling.
        /// </summary>
        /// <param name="request"></param>
        /// <param name="response"></param>
        public void HandleRequest(HttpListenerContext context)
        {
            OSHttpRequest request = new OSHttpRequest(context.Request);
            OSHttpResponse response = new OSHttpResponse(context.Response);

            request.SeqNo = RequestNumber++;
            request.StartTime = Environment.TickCount;

            string requestMethod = request.HttpMethod;
            string uriString = request.RawUrl;

            IRequestHandler requestHandler = null;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US", true);

            // If they ask for it we'll use it.
            response.KeepAlive = request.KeepAlive;
            response.SendChunked = false;

            string path = request.RawUrl;
            string handlerKey = GetHandlerKey(request.HttpMethod, path);
            byte[] buffer = null;

            try
            {

                if (String.IsNullOrEmpty(request.HttpMethod)) // Can't handle empty requests, not wasting a thread
                {
                    buffer = SendHTML500(response);
                }
                else if (TryGetStreamHandler(handlerKey, out requestHandler))
                {
                    if (DebugLevel >= 3)
                        LogIncomingToStreamHandler(request, requestHandler);

                    response.ContentType = requestHandler.ContentType; // Lets do this defaulting before in case handler has varying content type.

                    if (requestHandler is IAsyncRequestHandler)
                    {
                        //  Call the request handler. The Response is sent Async from the handler
                        IAsyncRequestHandler asyncHandler = requestHandler as IAsyncRequestHandler;
                        asyncHandler.Handle(this, path, request, response);
                        return;
                    }
                    else if (requestHandler is IStreamedRequestHandler)
                    {
                        IStreamedRequestHandler streamedRequestHandler = requestHandler as IStreamedRequestHandler;

                        buffer = streamedRequestHandler.Handle(path, request.InputStream, request, response);
                    }
                    else if (requestHandler is IGenericHTTPHandler)
                    {
                        IGenericHTTPHandler HTTPRequestHandler = requestHandler as IGenericHTTPHandler;
                        Stream requestStream = request.InputStream;

                        Encoding encoding = Encoding.UTF8;
                        StreamReader reader = new StreamReader(requestStream, encoding);

                        string requestBody = reader.ReadToEnd();

                        reader.Close();
                        //requestStream.Close();

                        Hashtable keysvals = new Hashtable();
                        Hashtable headervals = new Hashtable();
                        //string host = String.Empty;

                        foreach (string queryname in request.QueryString.AllKeys)
                            keysvals.Add(queryname, request.QueryString[queryname]);

                        foreach (string headername in request.Headers.AllKeys)
                            headervals[headername] = request.Headers[headername];

                        headervals["remote_addr"] = request.RemoteIPEndPoint.ToString();

                        keysvals.Add("requestbody", requestBody);
                        keysvals.Add("headers", headervals);

                        if (keysvals.Contains("method"))
                        {
                            //m_log.Warn("[HTTP]: Contains Method");
                            //string method = (string)keysvals["method"];
                            //m_log.Warn("[HTTP]: " + requestBody);

                        }

                        buffer = DoHTTPGruntWork(HTTPRequestHandler.Handle(path, keysvals), request, response);
                    }
                    else
                    {
                        IStreamHandler streamHandler = (IStreamHandler)requestHandler;

                        using (MemoryStream memoryStream = new MemoryStream())
                        {
                            streamHandler.Handle(path, request.InputStream, memoryStream, request, response);
                            memoryStream.Flush();
                            buffer = memoryStream.ToArray();
                        }
                    }
                }
                else
                {
                    switch (request.ContentType)
                    {
                        case null:
                        case "text/html":

                            if (DebugLevel >= 3)
                                m_log.DebugFormat(
                                    "[BASE HTTP SERVER]: HTTP IN {0} :{1} {2} content type handler {3} {4} from {5}",
                                    request.SeqNo, Port, request.ContentType, request.HttpMethod, request.Url.PathAndQuery, request.RemoteIPEndPoint);

                            buffer = HandleHTTPRequest(request, response);
                            break;

                        case "application/llsd+xml":
                        case "application/xml+llsd":
                        case "application/llsd+json":

                            if (DebugLevel >= 3)
                                m_log.DebugFormat(
                                    "[BASE HTTP SERVER]: HTTP IN {0} :{1} {2} content type handler {3} {4} from {5}",
                                    request.SeqNo, Port, request.ContentType, request.HttpMethod, request.Url.PathAndQuery, request.RemoteIPEndPoint);

                            buffer = HandleLLSDRequests(request, response);
                            break;

                        case "text/xml":
                        case "application/xml":
                        case "application/json":
                        default:
                            if (DoWeHaveALLSDHandler(request.RawUrl))
                            {
                                if (DebugLevel >= 3)
                                    LogIncomingToContentTypeHandler(request);

                                buffer = HandleLLSDRequests(request, response);
                            }
                            else if (DoWeHaveAHTTPHandler(request.RawUrl))
                            {
                                if (DebugLevel >= 3)
                                    LogIncomingToContentTypeHandler(request);

                                buffer = HandleHTTPRequest(request, response);
                            }
                            else
                            {
                                if (DebugLevel >= 3)
                                    LogIncomingToXmlRpcHandler(request);

                                // generic login request.
                                buffer = HandleXmlRpcRequests(request, response);
                            }

                            break;
                    }
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[BASE HTTP SERVER] Caught exception while handling request: {0} {1}", path, e);
                buffer = SendHTML500(response);
            }

            SendResponse(request, response, requestHandler, buffer);
        }

        public void SendResponse(OSHttpRequest request, OSHttpResponse response, IRequestHandler requestHandler, byte[] buffer)
        {
            try
            {
                request.InputStream.Close();

                // Do not include the time taken to actually send the response to the caller in the measurement
                // time.  This is to avoid logging when it's the client that is slow to process rather than the
                // server
                request.EndTime = Environment.TickCount;
                // Every month or so this will wrap and give bad numbers, not really a problem
                // since its just for reporting
                int tickdiff = request.EndTime - request.StartTime;

                // Dont log EQG messages.  They always run long due to the way the queue works
                if ((tickdiff > 3000) && (request.Url.AbsolutePath.StartsWith("/CAPS/EQG") == false))
                {
                    m_log.InfoFormat(
                        "[BASE HTTP SERVER]: Slow handling of {0} {1} {2} {3} {4} from {5} took {6}ms",
                        request.SeqNo,
                        request.HttpMethod,
                        request.Url.AbsolutePath,
                        requestHandler != null ? requestHandler.Name : String.Empty,
                        requestHandler != null ? requestHandler.Description : String.Empty,
                        request.RemoteIPEndPoint,
                        tickdiff);
                }
                else if (DebugLevel >= 4)
                {
                    m_log.DebugFormat(
                        "[BASE HTTP SERVER]: HTTP IN {0} :{1} took {2}ms",
                        request.SeqNo,
                        Port,
                        tickdiff);
                }

                if (buffer != null)
                {          
                    //find the accept encoding key
                    string acceptEncodingKey = null;
                    foreach (string key in request.Headers.AllKeys)
                    {
                        if (key.ToLower() == "accept-encoding")
                        {
                            acceptEncodingKey = request.Headers[key];
                        }
                    }

                    // GZip compress the response if the client says they can handle that
                    if (acceptEncodingKey != null &&
                        request.Headers["accept-encoding"].Contains("gzip"))
                    {
                        using (MemoryStream ms = new MemoryStream())
                        {
                            using (GZipStream gs = new GZipStream(ms, CompressionMode.Compress))
                            {
                                gs.Write(buffer, 0, buffer.Length);
                            }

                            buffer = ms.ToArray();
                        }

                        response.AddHeader("Content-Encoding", "gzip");
                    }

                    if (!response.SendChunked)
                        response.ContentLength64 = buffer.LongLength;

                    response.OutputStream.BeginWrite(buffer, 0, buffer.Length, ResponseWriteFinished, Tuple.Create(request, response, requestHandler));
                }
            }
            catch (Exception e)
            {
                //fill out request end time to get an actual count in case the exception is thrown in response.Write
                request.EndTime = Environment.TickCount;
                int tickdiff = request.EndTime - request.StartTime;

                m_log.ErrorFormat(
                    "[BASE HTTP SERVER]: HandleRequest() threw {0} while processing {1} {2} {3} {4} {5} took {6}ms",
                    e,
                    request.SeqNo,
                    request.HttpMethod,
                    request.Url.AbsolutePath,
                    requestHandler != null ? requestHandler.Name : String.Empty,
                    requestHandler != null ? requestHandler.Description : String.Empty,
                    tickdiff);
                // note that request.RemoteIPEndPoint is often disposed when we reach here (e.g. remote end has crashed)
            }
        }

        private void ResponseWriteFinished(IAsyncResult ar)
        {
            var parms = (Tuple<OSHttpRequest, OSHttpResponse, IRequestHandler>)ar.AsyncState;
            OSHttpRequest request = parms.Item1;
            OSHttpResponse response = parms.Item2;
            IRequestHandler requestHandler = parms.Item3;

            try
            {
                response.OutputStream.EndWrite(ar);
                parms.Item2.OutputStream.Flush(); //should be a NOP
                parms.Item2.OutputStream.Close();
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[BASE HTTP SERVER]: ResponseWriteFinished() threw {0} while processing {1} {2} {3} {4} {5}",
                    e,
                    request.SeqNo,
                    request.HttpMethod,
                    request.Url.AbsolutePath,
                    requestHandler != null ? requestHandler.Name : String.Empty,
                    requestHandler != null ? requestHandler.Description : String.Empty);

                try
                {
                    parms.Item2.Response.Abort();
                }
                catch 
                { }
            }
        }

        private void LogIncomingToStreamHandler(OSHttpRequest request, IRequestHandler requestHandler)
        {
            m_log.DebugFormat(
                "[BASE HTTP SERVER]: HTTP IN {0} :{1} stream handler {2} {3} {4} {5} from {6}",
                request.SeqNo,
                Port,
                request.HttpMethod,
                request.Url.PathAndQuery,
                requestHandler.Name,
                requestHandler.Description,
                request.RemoteIPEndPoint);

            if (DebugLevel >= 5)
                LogIncomingInDetail(request);
        }

        private void LogIncomingToContentTypeHandler(OSHttpRequest request)
        {
            m_log.DebugFormat(
                "[BASE HTTP SERVER]: HTTP IN {0} :{1} {2} content type handler {3} {4} from {5}",
                request.SeqNo,
                Port,
                request.ContentType,
                request.HttpMethod,
                request.Url.PathAndQuery,
                request.RemoteIPEndPoint);

            if (DebugLevel >= 5)
                LogIncomingInDetail(request);
        }

        private void LogIncomingToXmlRpcHandler(OSHttpRequest request)
        {
            m_log.DebugFormat(
                "[BASE HTTP SERVER]: HTTP IN {0} :{1} assumed generic XMLRPC request {2} {3} from {4}",
                request.SeqNo,
                Port,
                request.HttpMethod,
                request.Url.PathAndQuery,
                request.RemoteIPEndPoint);

            if (DebugLevel >= 5)
                LogIncomingInDetail(request);
        }

        private void LogIncomingInDetail(OSHttpRequest request)
        {
            using (StreamReader reader = new StreamReader(Util.Copy(request.InputStream), Encoding.UTF8))
            {
                string output;

                if (DebugLevel == 5)
                {
                    const int sampleLength = 80;
                    char[] sampleChars = new char[sampleLength];
                    reader.Read(sampleChars, 0, sampleLength);
                    output = new string(sampleChars);
                }
                else
                {
                    output = reader.ReadToEnd();
                }

                m_log.DebugFormat("[BASE HTTP SERVER]: {0}...", output.Replace("\n", @"\n"));
            }
        }

        private bool TryGetStreamHandler(string handlerKey, out IRequestHandler streamHandler)
        {
            string bestMatch = null;

            lock (m_streamHandlers)
            {
                foreach (string pattern in m_streamHandlers.Keys)
                {
                    if (handlerKey.StartsWith(pattern))
                    {
                        if (String.IsNullOrEmpty(bestMatch) || pattern.Length > bestMatch.Length)
                        {
                            bestMatch = pattern;
                        }
                    }
                }

                if (String.IsNullOrEmpty(bestMatch))
                {
                    streamHandler = null;
                    return false;
                }
                else
                {
                    streamHandler = m_streamHandlers[bestMatch];
                    return true;
                }
            }
        }


        private bool TryGetHTTPHandler(string handlerKey, out GenericHTTPMethod HTTPHandler)
        {
//            m_log.DebugFormat("[BASE HTTP HANDLER]: Looking for HTTP handler for {0}", handlerKey);

            string bestMatch = null;

            lock (m_HTTPHandlers)
            {
                foreach (string pattern in m_HTTPHandlers.Keys)
                {
                    if (handlerKey.StartsWith(pattern))
                    {
                        if (String.IsNullOrEmpty(bestMatch) || pattern.Length > bestMatch.Length)
                        {
                            bestMatch = pattern;
                        }
                    }
                }

                if (String.IsNullOrEmpty(bestMatch))
                {
                    HTTPHandler = null;
                    return false;
                }
                else
                {
                    HTTPHandler = m_HTTPHandlers[bestMatch];
                    return true;
                }
            }
        }

        /// <summary>
        /// Try all the registered xmlrpc handlers when an xmlrpc request is received.
        /// Sends back an XMLRPC unknown request response if no handler is registered for the requested method.
        /// </summary>
        /// <param name="request"></param>
        /// <param name="response"></param>
        private byte[] HandleXmlRpcRequests(OSHttpRequest request, OSHttpResponse response)
        {
            Stream requestStream = request.InputStream;

            Encoding encoding = Encoding.UTF8;
            StreamReader reader = new StreamReader(requestStream, encoding);

            string requestBody = reader.ReadToEnd();
            reader.Close();
            requestStream.Close();
            string responseString = String.Empty;
            XmlRpcRequest xmlRprcRequest = null;
            string methodName = null;

            try
            {
                xmlRprcRequest = (XmlRpcRequest) (new XmlRpcRequestDeserializer()).Deserialize(requestBody);
                methodName = xmlRprcRequest.MethodName;
            }
            catch (XmlException e)
            {
                if (DebugLevel >= 2)
                {
                    m_log.Warn(
                        string.Format(
                            "[BASE HTTP SERVER]: Got XMLRPC request with invalid XML from {0}.  XML was '{1}'.  Sending 404 response.  Exception ",
                            request.RemoteIPEndPoint, requestBody),
                        e);
                }
                else if (DebugLevel >= 1)
                {
                    m_log.WarnFormat(
                        "[BASE HTTP SERVER]: Got XMLRPC request with invalid XML from {0}, length {1}.  Sending 404 response.",
                        request.RemoteIPEndPoint, requestBody.Length);
                }
            }

            if ((xmlRprcRequest == null) || (methodName == null))
            {
                //HandleLLSDRequests(request, response);
                response.ContentType = "text/plain";
                response.StatusCode = 404;
                response.StatusDescription = "Not Found";
                response.ProtocolVersion = new System.Version("1.0");
                responseString = "Not found";
                response.KeepAlive = false;

                if (DebugLevel >= 1)
                {
                    m_log.WarnFormat(
                        "[BASE HTTP SERVER]: Handler not found for http request {0} {1}",
                        request.HttpMethod, request.Url.PathAndQuery);
                }
            }
            else
            {
                xmlRprcRequest.Params.Add(request.RemoteIPEndPoint); // Param[1]
                XmlRpcResponse xmlRpcResponse;

                XmlRpcMethod method;
                bool methodWasFound;
                bool keepAlive = false;
                lock (m_rpcHandlers)
                {
                    methodWasFound = m_rpcHandlers.TryGetValue(methodName, out method);
                    if (methodWasFound)
                        keepAlive = m_rpcHandlersKeepAlive[methodName];
                }

                if (methodWasFound)
                {
                    xmlRprcRequest.Params.Add(request.Url); // Param[2]

                    string xff = "X-Forwarded-For";
                    string xfflower = xff.ToLower();
                    foreach (string s in request.Headers.AllKeys)
                    {
                        if (s != null && s.Equals(xfflower))
                        {
                            xff = xfflower;
                            break;
                        }
                    }
                    xmlRprcRequest.Params.Add(request.Headers.Get(xff)); // Param[3]

                    try
                    {
                        xmlRpcResponse = method(xmlRprcRequest, request.RemoteIPEndPoint);
                    }
                    catch(Exception e)
                    {
                        string errorMessage
                            = String.Format(
                                "Requested method [{0}] from {1} threw exception: {2} {3}",
                                methodName, request.RemoteIPEndPoint.Address, e.Message, e.StackTrace);

                        m_log.ErrorFormat("[BASE HTTP SERVER]: {0}", errorMessage);

                        // if the registered XmlRpc method threw an exception, we pass a fault-code along
                        xmlRpcResponse = new XmlRpcResponse();

                        // Code probably set in accordance with http://xmlrpc-epi.sourceforge.net/specs/rfc.fault_codes.php
                        xmlRpcResponse.SetFault(-32603, errorMessage);
                    }

                    // if the method wasn't found, we can't determine KeepAlive state anyway, so lets do it only here
                    response.KeepAlive = keepAlive;
                }
                else
                {
                    xmlRpcResponse = new XmlRpcResponse();

                    // Code set in accordance with http://xmlrpc-epi.sourceforge.net/specs/rfc.fault_codes.php
                    xmlRpcResponse.SetFault(
                        XmlRpcErrorCodes.SERVER_ERROR_METHOD,
                        String.Format("Requested method [{0}] not found", methodName));
                }

                response.ContentType = "text/xml";
                responseString = XmlRpcResponseSerializer.Singleton.Serialize(xmlRpcResponse);
            }

            byte[] buffer = Encoding.UTF8.GetBytes(responseString);

            response.ContentLength64 = buffer.LongLength;
            response.ContentEncoding = Encoding.UTF8;
            response.SendChunked = false;

            return buffer;
        }

        private byte[] HandleLLSDRequests(OSHttpRequest request, OSHttpResponse response)
        {
            //m_log.Warn("[BASE HTTP SERVER]: We've figured out it's a LLSD Request");
            Stream requestStream = request.InputStream;

            Encoding encoding = Encoding.UTF8;
            StreamReader reader = new StreamReader(requestStream, encoding);

            string requestBody = reader.ReadToEnd();
            reader.Close();
            requestStream.Close();

            //m_log.DebugFormat("[OGP]: {0}:{1}", request.RawUrl, requestBody);
            // If they ask for it we'll use it.
            response.KeepAlive = request.KeepAlive;

            OSD llsdRequest = null;
            OSD llsdResponse = null;

            bool LegacyLLSDLoginLibOMV = (requestBody.Contains("passwd") && requestBody.Contains("mac") && requestBody.Contains("viewer_digest"));

            if (String.IsNullOrEmpty(requestBody))
            // Get Request
            {
                requestBody = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><llsd><map><key>request</key><string>get</string></map></llsd>";
            }
            try
            {
                llsdRequest = OSDParser.Deserialize(requestBody);
            }
            catch (Exception ex)
            {
                m_log.Warn("[BASE HTTP SERVER]: Error - " + ex.Message);
            }

            if (llsdRequest != null)// && m_defaultLlsdHandler != null)
            {
                LLSDMethod llsdhandler = null;

                if (TryGetLLSDHandler(request.RawUrl, out llsdhandler) && !LegacyLLSDLoginLibOMV)
                {
                    // we found a registered llsd handler to service this request
                    llsdResponse = llsdhandler(request.RawUrl, llsdRequest, request.RemoteIPEndPoint);
                }
                else
                {
                    // we didn't find a registered llsd handler to service this request
                    // Oops, no handler for this..   give em the failed message
                    llsdResponse = GenerateNoLLSDHandlerResponse();
                }
            }
            else
            {
                llsdResponse = GenerateNoLLSDHandlerResponse();
            }

            byte[] buffer = new byte[0];

            if (llsdResponse.ToString() == "shutdown404!")
            {
                response.ContentType = "text/plain";
                response.StatusCode = 404;
                response.StatusDescription = "Not Found";
                response.ProtocolVersion = new System.Version("1.0");
                buffer = Encoding.UTF8.GetBytes("Not found");
            }
            else
            {
                // Select an appropriate response format
                buffer = BuildLLSDResponse(request, response, llsdResponse);
            }

            response.SendChunked = false;
            response.ContentLength64 = buffer.Length;
            response.ContentEncoding = Encoding.UTF8;

            return buffer;
        }

        private byte[] BuildLLSDResponse(OSHttpRequest request, OSHttpResponse response, OSD llsdResponse)
        {
            if (request.AcceptTypes != null && request.AcceptTypes.Length > 0)
            {
                foreach (string strAccept in request.AcceptTypes)
                {
                    switch (strAccept)
                    {
                        case "application/llsd+xml":
                        case "application/xml":
                        case "text/xml":
                            response.ContentType = strAccept;
                            return OSDParser.SerializeLLSDXmlBytes(llsdResponse);
                        case "application/llsd+json":
                        case "application/json":
                            response.ContentType = strAccept;
                            return Encoding.UTF8.GetBytes(OSDParser.SerializeJsonString(llsdResponse));
                    }
                }
            }

            if (!String.IsNullOrEmpty(request.ContentType))
            {
                switch (request.ContentType)
                {
                    case "application/llsd+xml":
                    case "application/xml":
                    case "text/xml":
                        response.ContentType = request.ContentType;
                        return OSDParser.SerializeLLSDXmlBytes(llsdResponse);
                    case "application/llsd+json":
                    case "application/json":
                        response.ContentType = request.ContentType;
                        return Encoding.UTF8.GetBytes(OSDParser.SerializeJsonString(llsdResponse));
                }
            }

            // response.ContentType = "application/llsd+json";
            // return Util.UTF8.GetBytes(OSDParser.SerializeJsonString(llsdResponse));
            response.ContentType = "application/llsd+xml";
            return OSDParser.SerializeLLSDXmlBytes(llsdResponse);
        }

        /// <summary>
        /// Checks if we have an Exact path in the LLSD handlers for the path provided
        /// </summary>
        /// <param name="path">URI of the request</param>
        /// <returns>true if we have one, false if not</returns>
        private bool DoWeHaveALLSDHandler(string path)
        {
            string[] pathbase = path.Split('/');
            string searchquery = "/";

            if (pathbase.Length < 1)
                return false;

            for (int i = 1; i < pathbase.Length; i++)
            {
                searchquery += pathbase[i];
                if (pathbase.Length - 1 != i)
                    searchquery += "/";
            }

            string bestMatch = null;

            lock (m_llsdHandlers)
            {
                foreach (string pattern in m_llsdHandlers.Keys)
                {
                    if (searchquery.StartsWith(pattern) && searchquery.Length >= pattern.Length)
                        bestMatch = pattern;
                }
            }

            // extra kicker to remove the default XMLRPC login case..  just in case..
            if (path != "/" && bestMatch == "/" && searchquery != "/")
                return false;

            if (path == "/")
                return false;

            if (String.IsNullOrEmpty(bestMatch))
            {
                return false;
            }
            else
            {
                return true;
            }
        }

        /// <summary>
        /// Checks if we have an Exact path in the HTTP handlers for the path provided
        /// </summary>
        /// <param name="path">URI of the request</param>
        /// <returns>true if we have one, false if not</returns>
        private bool DoWeHaveAHTTPHandler(string path)
        {
            string[] pathbase = path.Split('/');
            string searchquery = "/";

            if (pathbase.Length < 1)
                return false;

            for (int i = 1; i < pathbase.Length; i++)
            {
                searchquery += pathbase[i];
                if (pathbase.Length - 1 != i)
                    searchquery += "/";
            }

            string bestMatch = null;

            //m_log.DebugFormat("[BASE HTTP HANDLER]: Checking if we have an HTTP handler for {0}", searchquery);

            lock (m_HTTPHandlers)
            {
                foreach (string pattern in m_HTTPHandlers.Keys)
                {
                    if (searchquery.StartsWith(pattern) && searchquery.Length >= pattern.Length)
                    {
                        bestMatch = pattern;
                    }
                }

                // extra kicker to remove the default XMLRPC login case..  just in case..
                if (path == "/")
                    return false;

                if (String.IsNullOrEmpty(bestMatch))
                {
                    return false;
                }
                else
                {
                    return true;
                }
            }
        }

        private bool TryGetLLSDHandler(string path, out LLSDMethod llsdHandler)
        {
            llsdHandler = null;
            // Pull out the first part of the path
            // splitting the path by '/' means we'll get the following return..
            // {0}/{1}/{2}
            // where {0} isn't something we really control 100%

            string[] pathbase = path.Split('/');
            string searchquery = "/";

            if (pathbase.Length < 1)
                return false;

            for (int i = 1; i < pathbase.Length; i++)
            {
                searchquery += pathbase[i];
                if (pathbase.Length - 1 != i)
                    searchquery += "/";
            }

            // while the matching algorithm below doesn't require it, we're expecting a query in the form
            //
            //   [] = optional
            //   /resource/UUID/action[/action]
            //
            // now try to get the closest match to the reigstered path
            // at least for OGP, registered path would probably only consist of the /resource/

            string bestMatch = null;

            lock (m_llsdHandlers)
            {
                foreach (string pattern in m_llsdHandlers.Keys)
                {
                    if (searchquery.ToLower().StartsWith(pattern.ToLower()))
                    {
                        if (String.IsNullOrEmpty(bestMatch) || searchquery.Length > bestMatch.Length)
                        {
                            // You have to specifically register for '/' and to get it, you must specificaly request it
                            //
                            if (pattern == "/" && searchquery == "/" || pattern != "/")
                                bestMatch = pattern;
                        }
                    }
                }
    
                if (String.IsNullOrEmpty(bestMatch))
                {
                    llsdHandler = null;
                    return false;
                }
                else
                {
                    llsdHandler = m_llsdHandlers[bestMatch];
                    return true;
                }
            }
        }

        private OSDMap GenerateNoLLSDHandlerResponse()
        {
            OSDMap map = new OSDMap();
            map["reason"] = OSD.FromString("LLSDRequest");
            map["message"] = OSD.FromString("No handler registered for LLSD Requests");
            map["login"] = OSD.FromString("false");
            return map;
        }

        public byte[] HandleHTTPRequest(OSHttpRequest request, OSHttpResponse response)
        {
//            m_log.DebugFormat(
//                "[BASE HTTP SERVER]: HandleHTTPRequest for request to {0}, method {1}",
//                request.RawUrl, request.HttpMethod);

            switch (request.HttpMethod)
            {
                case "OPTIONS":
                    response.StatusCode = (int)OSHttpStatusCode.SuccessOk;
                    return null;

                default:
                    return HandleContentVerbs(request, response);
            }
        }

        private byte[] HandleContentVerbs(OSHttpRequest request, OSHttpResponse response)
        {
//            m_log.DebugFormat("[BASE HTTP SERVER]: HandleContentVerbs for request to {0}", request.RawUrl);

            // This is a test.  There's a workable alternative..  as this way sucks.
            // We'd like to put this into a text file parhaps that's easily editable.
            //
            // For this test to work, I used the following secondlife.exe parameters
            // "C:\Program Files\SecondLifeWindLight\SecondLifeWindLight.exe" -settings settings_windlight.xml -channel "Second Life WindLight"  -set SystemLanguage en-us -loginpage http://10.1.1.2:8002/?show_login_form=TRUE -loginuri http://10.1.1.2:8002 -user 10.1.1.2
            //
            // Even after all that, there's still an error, but it's a start.
            //
            // I depend on show_login_form being in the secondlife.exe parameters to figure out
            // to display the form, or process it.
            // a better way would be nifty.

            byte[] buffer;

            Stream requestStream = request.InputStream;

            Encoding encoding = Encoding.UTF8;
            StreamReader reader = new StreamReader(requestStream, encoding);

            string requestBody = reader.ReadToEnd();
            // avoid warning for now
            reader.ReadToEnd();
            reader.Close();
            requestStream.Close();

            Hashtable keysvals = new Hashtable();
            Hashtable headervals = new Hashtable();

            Hashtable requestVars = new Hashtable();

            string host = String.Empty;

            string[] querystringkeys = request.QueryString.AllKeys;
            string[] rHeaders = request.Headers.AllKeys;

            keysvals.Add("body", requestBody);
            keysvals.Add("uri", request.RawUrl);
            keysvals.Add("content-type", request.ContentType);
            keysvals.Add("http-method", request.HttpMethod);

            foreach (string queryname in querystringkeys)
            {
//                m_log.DebugFormat(
//                    "[BASE HTTP SERVER]: Got query paremeter {0}={1}", queryname, request.QueryString[queryname]);

                // HttpRequest.QueryString.AllKeys returns a one-item array, with a null only,
                // if passed something without an '=' in the query, such as URL/?abc or URL/?abc+def
                if (queryname != null)
                {
                    keysvals.Add(queryname, request.QueryString[queryname]);
                    requestVars.Add(queryname, keysvals[queryname]);
                }
            }

            foreach (string headername in rHeaders)
            {
//                m_log.Debug("[BASE HTTP SERVER]: " + headername + "=" + request.Headers[headername]);
                headervals[headername] = request.Headers[headername];
            }

            if (headervals.Contains("Host"))
            {
                host = (string)headervals["Host"];
            }

            headervals["remote_addr"] = request.RemoteIPEndPoint.ToString();

            keysvals.Add("headers", headervals);
            keysvals.Add("querystringkeys", querystringkeys);
            keysvals.Add("requestvars", requestVars);
//            keysvals.Add("form", request.Form);

            if (keysvals.Contains("method"))
            {
//                m_log.Debug("[BASE HTTP SERVER]: Contains Method");
                string method = (string) keysvals["method"];
//                m_log.Debug("[BASE HTTP SERVER]: " + requestBody);
                GenericHTTPMethod requestprocessor;
                bool foundHandler = TryGetHTTPHandler(method, out requestprocessor);
                if (foundHandler)
                {
                    Hashtable responsedata1 = requestprocessor(keysvals);
                    buffer = DoHTTPGruntWork(responsedata1, request, response);

                    //SendHTML500(response);
                }
                else
                {
//                    m_log.Warn("[BASE HTTP SERVER]: Handler Not Found");
                    buffer = SendHTML404(response);
                }
            }
            else
            {
                GenericHTTPMethod requestprocessor;
                bool foundHandler = TryGetHTTPHandlerPathBased(request.RawUrl, out requestprocessor);
                if (foundHandler)
                {
                    Hashtable responsedata2 = requestprocessor(keysvals);
                    buffer = DoHTTPGruntWork(responsedata2, request, response);

                    //SendHTML500(response);
                }
                else
                {
//                    m_log.Warn("[BASE HTTP SERVER]: Handler Not Found2");
                    buffer = SendHTML404(response);
                }
            }

            return buffer;
        }

        private bool TryGetHTTPHandlerPathBased(string path, out GenericHTTPMethod httpHandler)
        {
            httpHandler = null;
            // Pull out the first part of the path
            // splitting the path by '/' means we'll get the following return..
            // {0}/{1}/{2}
            // where {0} isn't something we really control 100%

            string[] pathbase = path.Split('/');
            string searchquery = "/";

            if (pathbase.Length < 1)
                return false;

            for (int i = 1; i < pathbase.Length; i++)
            {
                searchquery += pathbase[i];
                if (pathbase.Length - 1 != i)
                    searchquery += "/";
            }

            // while the matching algorithm below doesn't require it, we're expecting a query in the form
            //
            //   [] = optional
            //   /resource/UUID/action[/action]
            //
            // now try to get the closest match to the reigstered path
            // at least for OGP, registered path would probably only consist of the /resource/

            string bestMatch = null;

//            m_log.DebugFormat(
//                "[BASE HTTP HANDLER]: TryGetHTTPHandlerPathBased() looking for HTTP handler to match {0}", searchquery);

            lock (m_HTTPHandlers)
            {
                foreach (string pattern in m_HTTPHandlers.Keys)
                {
                    if (searchquery.ToLower().StartsWith(pattern.ToLower()))
                    {
                        if (String.IsNullOrEmpty(bestMatch) || searchquery.Length > bestMatch.Length)
                        {
                            // You have to specifically register for '/' and to get it, you must specifically request it
                            if (pattern == "/" && searchquery == "/" || pattern != "/")
                                bestMatch = pattern;
                        }
                    }
                }

                if (String.IsNullOrEmpty(bestMatch))
                {
                    httpHandler = null;
                    return false;
                }
                else
                {
                    if (bestMatch == "/" && searchquery != "/")
                        return false;

                    httpHandler =  m_HTTPHandlers[bestMatch];
                    return true;
                }
            }
        }

        public byte[] DoHTTPGruntWork(Hashtable responsedata, OSHttpRequest request, OSHttpResponse response)
        {
            //m_log.Info("[BASE HTTP SERVER]: Doing HTTP Grunt work with response");
            int responsecode = (int)responsedata["int_response_code"];
            string responseString = (string)responsedata["str_response_string"];
            string contentType = (string)responsedata["content_type"];
            byte[] responseBinary = responsedata.Contains("response_binary") ? (byte[])responsedata["response_binary"] : null;

            if (responsedata.ContainsKey("error_status_text"))
            {
                response.StatusDescription = (string)responsedata["error_status_text"];
            }
            if (responsedata.ContainsKey("http_protocol_version"))
            {
                response.ProtocolVersion = new System.Version((string)responsedata["http_protocol_version"]);
            }

            if (responsedata.ContainsKey("keepalive"))
            {
                bool keepalive = (bool)responsedata["keepalive"];
                response.KeepAlive = keepalive;
            }

            // Cross-Origin Resource Sharing with simple requests
            if (responsedata.ContainsKey("access_control_allow_origin"))
                response.AddHeader("Access-Control-Allow-Origin", (string)responsedata["access_control_allow_origin"]);

            // The client ignores anything but 200 here for web login, so ensure that this is 200 for that
            response.StatusCode = responsecode;
            if (responsecode == (int)OSHttpStatusCode.RedirectMovedPermanently)
            {
                response.RedirectLocation = (string)responsedata["str_redirect_location"];
                response.StatusCode = responsecode;
            }

            if (string.IsNullOrEmpty(contentType))
                contentType = "text/html";
            response.AddHeader("Content-Type", contentType);

            byte[] buffer;

            if (responseBinary != null)
            {
                buffer = responseBinary;
            }
            else
            {
                if (!(contentType.Contains("image")
                    || contentType.Contains("x-shockwave-flash")
                    || contentType.Contains("application/x-oar")
                    || contentType.Contains("application/vnd.ll.mesh")))
                {
                    // Text
                    buffer = Encoding.UTF8.GetBytes(responseString);
                }
                else
                {
                    // Binary!
                    buffer = Convert.FromBase64String(responseString);
                }
            }

            response.SendChunked = false;
            response.ContentLength64 = buffer.LongLength;
            response.ContentEncoding = System.Text.Encoding.UTF8;

            return buffer;
        }

        public byte[] SendHTML404(OSHttpResponse response)
        {
            response.StatusCode = 404;
            response.AddHeader("Content-type", "text/html");

            string responseString = GetHTTP404();
            byte[] buffer = Encoding.UTF8.GetBytes(responseString);

            response.SendChunked = false;
            response.ContentLength64 = buffer.Length;
            response.ContentEncoding = Encoding.UTF8;

            return buffer;
        }

        public byte[] SendHTML500(OSHttpResponse response)
        {
            response.StatusCode = (int)OSHttpStatusCode.ServerErrorInternalError;
            response.AddHeader("Content-type", "text/html");

            string responseString = GetHTTP500();
            byte[] buffer = Encoding.UTF8.GetBytes(responseString);

            response.SendChunked = false;
            response.ContentLength64 = buffer.Length;
            response.ContentEncoding = Encoding.UTF8;

            return buffer;
        }

        public void Start()
        {
            m_log.InfoFormat(
                "[BASE HTTP SERVER]: Starting {0} server on port {1}", Secure ? "HTTPS" : "HTTP", Port);

            // For unit tests execute the following lines, replacing YOUR_USER with your user's login name. Often that is Administrator
            // netsh http add urlacl url=http://+:9000/ user=YOUR_USER
            // netsh http add urlacl url=http://+:9021/ user=YOUR_USER
            // netsh http add urlacl url=http://+:9022/ user=YOUR_USER
            // netsh http add urlacl url=http://+:9023/ user=YOUR_USER
            // netsh http add urlacl url=http://+:9024/ user=YOUR_USER
            // netsh http add urlacl url=http://+:9025/ user=YOUR_USER
            // netsh http add urlacl url=http://+:9026/ user=YOUR_USER
            // netsh http add urlacl url=http://+:9027/ user=YOUR_USER
            // netsh http add urlacl url=http://+:9028/ user=YOUR_USER
            // netsh http add urlacl url=http://+:9029/ user=YOUR_USER

            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                m_httpListener = new HttpListener();
                m_httpListener.Prefixes.Add(Protocol + "+:" + Port.ToString() + "/");

                // Start Listening
                m_httpListener.Start();
                m_httpListener.BeginGetContext(new AsyncCallback(OnRequest), m_httpListener);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[BASE HTTP SERVER]: Error - {0}", e.Message);
                m_log.ErrorFormat("[BASE HTTP SERVER]: Tip: Do you have permission to listen on port {0} ?", m_port);
                m_log.ErrorFormat("[BASE HTTP SERVER]: Try:    netsh http add urlacl url={0}://+:{1}/ user={2}", 
                    Secure ? "https" : "http", m_port, Environment.UserName);

                // We want this exception to halt the entire server since in current configurations we aren't too
                // useful without inbound HTTP.
                throw;
            }
        }

        public void Stop()
        {
            if (m_httpListener != null)
            {
                m_httpListener.Close();
                m_httpListener = null;
            }
        }

        public void RemoveStreamHandler(string httpMethod, string path)
        {
            string handlerKey = GetHandlerKey(httpMethod, path);

            //m_log.DebugFormat("[BASE HTTP SERVER]: Removing handler key {0}", handlerKey);

            lock (m_streamHandlers)
                m_streamHandlers.Remove(handlerKey);
        }

        public void RemoveHTTPHandler(string httpMethod, string path)
        {
            lock (m_HTTPHandlers)
            {
                if (String.IsNullOrEmpty(httpMethod))
                {
                    m_HTTPHandlers.Remove(path);
                    return;
                }

                m_HTTPHandlers.Remove(GetHandlerKey(httpMethod, path));
            }
        }

        public void RemoveXmlRPCHandler(string method)
        {
            lock (m_rpcHandlers)
            {
                if (m_rpcHandlers.ContainsKey(method))
                {
                    m_rpcHandlers.Remove(method);
                }
            }
        }

        public bool RemoveLLSDHandler(string path, LLSDMethod handler)
        {
            lock (m_llsdHandlers)
            {
                LLSDMethod foundHandler;

                if (m_llsdHandlers.TryGetValue(path, out foundHandler) && foundHandler == handler)
                {
                    m_llsdHandlers.Remove(path);
                    return true;
                }
            }

            return false;
        }

        public string GetHTTP404()
        {
            string file = Path.Combine(".", "http_404.html");
            if (!File.Exists(file))
                return getDefaultHTTP404(FullHostName);

            StreamReader sr = File.OpenText(file);
            string result = sr.ReadToEnd();
            sr.Close();
            return result;
        }

        public string GetHTTP500()
        {
            string file = Path.Combine(".", "http_500.html");
            if (!File.Exists(file))
                return getDefaultHTTP500();

            StreamReader sr = File.OpenText(file);
            string result = sr.ReadToEnd();
            sr.Close();
            return result;
        }

        // Fallback HTTP responses in case the HTTP error response files don't exist
        private static string getDefaultHTTP404(string host)
        {
            return "<HTML><HEAD><TITLE>404 Page not found</TITLE><BODY><BR /><H1>Ooops!</H1><P>You have reached a Halcyon-based server.</P><P>To log in to this virtual world, you must connect with a viewer application or web-based viewer.</P></BODY></HTML>";
        }

        private static string getDefaultHTTP500()
        {
            return "<HTML><HEAD><TITLE>500 Internal Server Error</TITLE><BODY><BR /><H1>Ooops!</H1><P>The Halcyon-based server you requested does not support browser logins.</P></BODY></HTML>";
        }
    }

}
