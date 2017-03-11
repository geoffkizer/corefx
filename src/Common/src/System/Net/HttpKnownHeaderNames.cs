// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.Http.Headers;

namespace System.Net
{
    internal static partial class HttpKnownHeaderNames
    {
        // When adding a new constant, add it to HttpKnownHeaderNames.TryGetHeaderName.cs as well.

        public static readonly HeaderKey Accept = new HeaderKey("Accept");
        public static readonly HeaderKey AcceptCharset = new HeaderKey("Accept-Charset");
        public static readonly HeaderKey AcceptEncoding = new HeaderKey("Accept-Encoding");
        public static readonly HeaderKey AcceptLanguage = new HeaderKey("Accept-Language");
        public static readonly HeaderKey AcceptPatch = new HeaderKey("Accept-Patch");
        public static readonly HeaderKey AcceptRanges = new HeaderKey("Accept-Ranges");
        public static readonly HeaderKey AccessControlAllowCredentials = new HeaderKey("Access-Control-Allow-Credentials");
        public static readonly HeaderKey AccessControlAllowHeaders = new HeaderKey("Access-Control-Allow-Headers");
        public static readonly HeaderKey AccessControlAllowMethods = new HeaderKey("Access-Control-Allow-Methods");
        public static readonly HeaderKey AccessControlAllowOrigin = new HeaderKey("Access-Control-Allow-Origin");
        public static readonly HeaderKey AccessControlExposeHeaders = new HeaderKey("Access-Control-Expose-Headers");
        public static readonly HeaderKey AccessControlMaxAge = new HeaderKey("Access-Control-Max-Age");
        public static readonly HeaderKey Age = new HeaderKey("Age");
        public static readonly HeaderKey Allow = new HeaderKey("Allow");
        public static readonly HeaderKey AltSvc = new HeaderKey("Alt-Svc");
        public static readonly HeaderKey Authorization = new HeaderKey("Authorization");
        public static readonly HeaderKey CacheControl = new HeaderKey("Cache-Control");
        public static readonly HeaderKey Connection = new HeaderKey("Connection");
        public static readonly HeaderKey ContentDisposition = new HeaderKey("Content-Disposition");
        public static readonly HeaderKey ContentEncoding = new HeaderKey("Content-Encoding");
        public static readonly HeaderKey ContentLanguage = new HeaderKey("Content-Language");
        public static readonly HeaderKey ContentLength = new HeaderKey("Content-Length");
        public static readonly HeaderKey ContentLocation = new HeaderKey("Content-Location");
        public static readonly HeaderKey ContentMD5 = new HeaderKey("Content-MD5");
        public static readonly HeaderKey ContentRange = new HeaderKey("Content-Range");
        public static readonly HeaderKey ContentSecurityPolicy = new HeaderKey("Content-Security-Policy");
        public static readonly HeaderKey ContentType = new HeaderKey("Content-Type");
        public static readonly HeaderKey Cookie = new HeaderKey("Cookie");
        public static readonly HeaderKey Cookie2 = new HeaderKey("Cookie2");
        public static readonly HeaderKey Date = new HeaderKey("Date");
        public static readonly HeaderKey ETag = new HeaderKey("ETag");
        public static readonly HeaderKey Expect = new HeaderKey("Expect");
        public static readonly HeaderKey Expires = new HeaderKey("Expires");
        public static readonly HeaderKey From = new HeaderKey("From");
        public static readonly HeaderKey Host = new HeaderKey("Host");
        public static readonly HeaderKey IfMatch = new HeaderKey("If-Match");
        public static readonly HeaderKey IfModifiedSince = new HeaderKey("If-Modified-Since");
        public static readonly HeaderKey IfNoneMatch = new HeaderKey("If-None-Match");
        public static readonly HeaderKey IfRange = new HeaderKey("If-Range");
        public static readonly HeaderKey IfUnmodifiedSince = new HeaderKey("If-Unmodified-Since");
        public static readonly HeaderKey KeepAlive = new HeaderKey("Keep-Alive");
        public static readonly HeaderKey LastModified = new HeaderKey("Last-Modified");
        public static readonly HeaderKey Link = new HeaderKey("Link");
        public static readonly HeaderKey Location = new HeaderKey("Location");
        public static readonly HeaderKey MaxForwards = new HeaderKey("Max-Forwards");
        public static readonly HeaderKey Origin = new HeaderKey("Origin");
        public static readonly HeaderKey P3P = new HeaderKey("P3P");
        public static readonly HeaderKey Pragma = new HeaderKey("Pragma");
        public static readonly HeaderKey ProxyAuthenticate = new HeaderKey("Proxy-Authenticate");
        public static readonly HeaderKey ProxyAuthorization = new HeaderKey("Proxy-Authorization");
        public static readonly HeaderKey ProxyConnection = new HeaderKey("Proxy-Connection");
        public static readonly HeaderKey PublicKeyPins = new HeaderKey("Public-Key-Pins");
        public static readonly HeaderKey Range = new HeaderKey("Range");
        public static readonly HeaderKey Referer = new HeaderKey("Referer"); // NB: The spelling-mistake "Referer" for "Referrer" must be matched.
        public static readonly HeaderKey RetryAfter = new HeaderKey("Retry-After");
        public static readonly HeaderKey SecWebSocketAccept = new HeaderKey("Sec-WebSocket-Accept");
        public static readonly HeaderKey SecWebSocketExtensions = new HeaderKey("Sec-WebSocket-Extensions");
        public static readonly HeaderKey SecWebSocketKey = new HeaderKey("Sec-WebSocket-Key");
        public static readonly HeaderKey SecWebSocketProtocol = new HeaderKey("Sec-WebSocket-Protocol");
        public static readonly HeaderKey SecWebSocketVersion = new HeaderKey("Sec-WebSocket-Version");
        public static readonly HeaderKey Server = new HeaderKey("Server");
        public static readonly HeaderKey SetCookie = new HeaderKey("Set-Cookie");
        public static readonly HeaderKey SetCookie2 = new HeaderKey("Set-Cookie2");
        public static readonly HeaderKey StrictTransportSecurity = new HeaderKey("Strict-Transport-Security");
        public static readonly HeaderKey TE = new HeaderKey("TE");
        public static readonly HeaderKey TSV = new HeaderKey("TSV");
        public static readonly HeaderKey Trailer = new HeaderKey("Trailer");
        public static readonly HeaderKey TransferEncoding = new HeaderKey("Transfer-Encoding");
        public static readonly HeaderKey Upgrade = new HeaderKey("Upgrade");
        public static readonly HeaderKey UpgradeInsecureRequests = new HeaderKey("Upgrade-Insecure-Requests");
        public static readonly HeaderKey UserAgent = new HeaderKey("User-Agent");
        public static readonly HeaderKey Vary = new HeaderKey("Vary");
        public static readonly HeaderKey Via = new HeaderKey("Via");
        public static readonly HeaderKey WWWAuthenticate = new HeaderKey("WWW-Authenticate");
        public static readonly HeaderKey Warning = new HeaderKey("Warning");
        public static readonly HeaderKey XAspNetVersion = new HeaderKey("X-AspNet-Version");
        public static readonly HeaderKey XContentDuration = new HeaderKey("X-Content-Duration");
        public static readonly HeaderKey XContentTypeOptions = new HeaderKey("X-Content-Type-Options");
        public static readonly HeaderKey XFrameOptions = new HeaderKey("X-Frame-Options");
        public static readonly HeaderKey XMSEdgeRef = new HeaderKey("X-MSEdge-Ref");
        public static readonly HeaderKey XPoweredBy = new HeaderKey("X-Powered-By");
        public static readonly HeaderKey XRequestID = new HeaderKey("X-Request-ID");
        public static readonly HeaderKey XUACompatible = new HeaderKey("X-UA-Compatible");
    }
}
