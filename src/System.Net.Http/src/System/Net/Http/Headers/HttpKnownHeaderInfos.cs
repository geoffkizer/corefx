// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;

// TODO: Rename file

namespace System.Net.Http.Headers
{
    internal abstract class HeaderInfo : IEquatable<HeaderInfo>
    {
        public abstract bool Equals(HeaderInfo other);
        public abstract override int GetHashCode();
        public abstract string Name { get; }

        public override bool Equals(object obj) => (obj is HeaderInfo headerInfo ? Equals(headerInfo) : false);

        public static bool operator ==(HeaderInfo left, HeaderInfo right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(HeaderInfo left, HeaderInfo right)
        {
            return !left.Equals(right);
        }

        // TODO: Something like static "Get"
        public static HeaderInfo Get(string name)
        {
            Debug.Assert(!string.IsNullOrEmpty(name));
            Debug.Assert(HttpRuleParser.GetTokenLength(name, 0) == name.Length);

            // TODO
            return null;

        }

        private sealed class CustomHeaderInfo : HeaderInfo
        {
            private string _name;

            public CustomHeaderInfo(string name)
            {
                Debug.Assert(!string.IsNullOrEmpty(name));
                Debug.Assert(HttpRuleParser.GetTokenLength(name, 0) == name.Length);

                _name = name;
            }

            public override string Name => _name;

            public override bool Equals(HeaderInfo other) =>
                (other is CustomHeaderInfo customHeaderInfo ?
                 StringComparer.OrdinalIgnoreCase.Equals(_name, customHeaderInfo._name) :
                 false);

            public override int GetHashCode()
            {
                return _name.GetHashCode();
            }

        }

        private sealed class KnownHeaderInfo : HeaderInfo
        {
            private string _name;
            private int _hashcode;

            public KnownHeaderInfo(string name)
            {
                Debug.Assert(!string.IsNullOrEmpty(name));
                Debug.Assert(HttpRuleParser.GetTokenLength(name, 0) == name.Length);

                _name = name;
                _hashcode = StringComparer.OrdinalIgnoreCase.GetHashCode(_name);
            }

            public override string Name => _name;

            public override bool Equals(HeaderInfo other)
            {
                // Reference equality
                return this == other;
            }

            public override int GetHashCode()
            {
                return _hashcode;
            }
        }

        internal static class KnownHeaders
        {
            // If you add a new entry here, you need to add it to TryGetKnownHeader above as well.

            public static HeaderInfo Accept = new KnownHeaderInfo("Accept");
            public static HeaderInfo AcceptCharset = new KnownHeaderInfo("Accept-Charset");
            public static HeaderInfo AcceptEncoding = new KnownHeaderInfo("Accept-Encoding");
            public static HeaderInfo AcceptLanguage = new KnownHeaderInfo("Accept-Language");
            public static HeaderInfo AcceptPatch = new KnownHeaderInfo("Accept-Patch");
            public static HeaderInfo AcceptRanges = new KnownHeaderInfo("Accept-Ranges");
            public static HeaderInfo AccessControlAllowCredentials = new KnownHeaderInfo("Access-Control-Allow-Credentials");
            public static HeaderInfo AccessControlAllowHeaders = new KnownHeaderInfo("Access-Control-Allow-Headers");
            public static HeaderInfo AccessControlAllowMethods = new KnownHeaderInfo("Access-Control-Allow-Methods");
            public static HeaderInfo AccessControlAllowOrigin = new KnownHeaderInfo("Access-Control-Allow-Origin");
            public static HeaderInfo AccessControlExposeHeaders = new KnownHeaderInfo("Access-Control-Expose-Headers");
            public static HeaderInfo AccessControlMaxAge = new KnownHeaderInfo("Access-Control-Max-Age");
            public static HeaderInfo Age = new KnownHeaderInfo("Age");
            public static HeaderInfo Allow = new KnownHeaderInfo("Allow");
            public static HeaderInfo AltSvc = new KnownHeaderInfo("Alt-Svc");
            public static HeaderInfo Authorization = new KnownHeaderInfo("Authorization");
            public static HeaderInfo CacheControl = new KnownHeaderInfo("Cache-Control");
            public static HeaderInfo Connection = new KnownHeaderInfo("Connection");
            public static HeaderInfo ContentDisposition = new KnownHeaderInfo("Content-Disposition");
            public static HeaderInfo ContentEncoding = new KnownHeaderInfo("Content-Encoding");
            public static HeaderInfo ContentLanguage = new KnownHeaderInfo("Content-Language");
            public static HeaderInfo ContentLength = new KnownHeaderInfo("Content-Length");
            public static HeaderInfo ContentLocation = new KnownHeaderInfo("Content-Location");
            public static HeaderInfo ContentMD5 = new KnownHeaderInfo("Content-MD5");
            public static HeaderInfo ContentRange = new KnownHeaderInfo("Content-Range");
            public static HeaderInfo ContentSecurityPolicy = new KnownHeaderInfo("Content-Security-Policy");
            public static HeaderInfo ContentType = new KnownHeaderInfo("Content-Type");
            public static HeaderInfo Cookie = new KnownHeaderInfo("Cookie");
            public static HeaderInfo Cookie2 = new KnownHeaderInfo("Cookie2");
            public static HeaderInfo Date = new KnownHeaderInfo("Date");
            public static HeaderInfo ETag = new KnownHeaderInfo("ETag");
            public static HeaderInfo Expect = new KnownHeaderInfo("Expect");
            public static HeaderInfo Expires = new KnownHeaderInfo("Expires");
            public static HeaderInfo From = new KnownHeaderInfo("From");
            public static HeaderInfo Host = new KnownHeaderInfo("Host");
            public static HeaderInfo IfMatch = new KnownHeaderInfo("If-Match");
            public static HeaderInfo IfModifiedSince = new KnownHeaderInfo("If-Modified-Since");
            public static HeaderInfo IfNoneMatch = new KnownHeaderInfo("If-None-Match");
            public static HeaderInfo IfRange = new KnownHeaderInfo("If-Range");
            public static HeaderInfo IfUnmodifiedSince = new KnownHeaderInfo("If-Unmodified-Since");
            public static HeaderInfo KeepAlive = new KnownHeaderInfo("Keep-Alive");
            public static HeaderInfo LastModified = new KnownHeaderInfo("Last-Modified");
            public static HeaderInfo Link = new KnownHeaderInfo("Link");
            public static HeaderInfo Location = new KnownHeaderInfo("Location");
            public static HeaderInfo MaxForwards = new KnownHeaderInfo("Max-Forwards");
            public static HeaderInfo Origin = new KnownHeaderInfo("Origin");
            public static HeaderInfo P3P = new KnownHeaderInfo("P3P");
            public static HeaderInfo Pragma = new KnownHeaderInfo("Pragma");
            public static HeaderInfo ProxyAuthenticate = new KnownHeaderInfo("Proxy-Authenticate");
            public static HeaderInfo ProxyAuthorization = new KnownHeaderInfo("Proxy-Authorization");
            public static HeaderInfo ProxyConnection = new KnownHeaderInfo("Proxy-Connection");
            public static HeaderInfo PublicKeyPins = new KnownHeaderInfo("Public-Key-Pins");
            public static HeaderInfo Range = new KnownHeaderInfo("Range");
            public static HeaderInfo Referer = new KnownHeaderInfo("Referer"); // NB: The spelling-mistake "Referer" for "Referrer" must be matched.
            public static HeaderInfo RetryAfter = new KnownHeaderInfo("Retry-After");
            public static HeaderInfo SecWebSocketAccept = new KnownHeaderInfo("Sec-WebSocket-Accept");
            public static HeaderInfo SecWebSocketExtensions = new KnownHeaderInfo("Sec-WebSocket-Extensions");
            public static HeaderInfo SecWebSocketKey = new KnownHeaderInfo("Sec-WebSocket-Key");
            public static HeaderInfo SecWebSocketProtocol = new KnownHeaderInfo("Sec-WebSocket-Protocol");
            public static HeaderInfo SecWebSocketVersion = new KnownHeaderInfo("Sec-WebSocket-Version");
            public static HeaderInfo Server = new KnownHeaderInfo("Server");
            public static HeaderInfo SetCookie = new KnownHeaderInfo("Set-Cookie");
            public static HeaderInfo SetCookie2 = new KnownHeaderInfo("Set-Cookie2");
            public static HeaderInfo StrictTransportSecurity = new KnownHeaderInfo("Strict-Transport-Security");
            public static HeaderInfo TE = new KnownHeaderInfo("TE");
            public static HeaderInfo TSV = new KnownHeaderInfo("TSV");
            public static HeaderInfo Trailer = new KnownHeaderInfo("Trailer");
            public static HeaderInfo TransferEncoding = new KnownHeaderInfo("Transfer-Encoding");
            public static HeaderInfo Upgrade = new KnownHeaderInfo("Upgrade");
            public static HeaderInfo UpgradeInsecureRequests = new KnownHeaderInfo("Upgrade-Insecure-Requests");
            public static HeaderInfo UserAgent = new KnownHeaderInfo("User-Agent");
            public static HeaderInfo Vary = new KnownHeaderInfo("Vary");
            public static HeaderInfo Via = new KnownHeaderInfo("Via");
            public static HeaderInfo WWWAuthenticate = new KnownHeaderInfo("WWW-Authenticate");
            public static HeaderInfo Warning = new KnownHeaderInfo("Warning");
            public static HeaderInfo XAspNetVersion = new KnownHeaderInfo("X-AspNet-Version");
            public static HeaderInfo XContentDuration = new KnownHeaderInfo("X-Content-Duration");
            public static HeaderInfo XContentTypeOptions = new KnownHeaderInfo("X-Content-Type-Options");
            public static HeaderInfo XFrameOptions = new KnownHeaderInfo("X-Frame-Options");
            public static HeaderInfo XMSEdgeRef = new KnownHeaderInfo("X-MSEdge-Ref");
            public static HeaderInfo XPoweredBy = new KnownHeaderInfo("X-Powered-By");
            public static HeaderInfo XRequestID = new KnownHeaderInfo("X-Request-ID");
            public static HeaderInfo XUACompatible = new KnownHeaderInfo("X-UA-Compatible");
        }
    }
}
