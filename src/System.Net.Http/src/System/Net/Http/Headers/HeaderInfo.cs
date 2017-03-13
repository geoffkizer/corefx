// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;

namespace System.Net.Http.Headers
{
    internal abstract class HeaderInfo : IEquatable<HeaderInfo>
    {
        public abstract bool Equals(HeaderInfo other);
        public abstract override int GetHashCode();
        public abstract string Name { get; }
        public abstract HttpHeaderParser Parser { get; }
        public abstract HttpHeaderType HeaderType { get; }
        public abstract byte[] RawBytes { get; }

        public override bool Equals(object obj) => (obj is HeaderInfo headerInfo ? Equals(headerInfo) : false);

        public static bool operator ==(HeaderInfo left, HeaderInfo right)
        {
            return ((object)left == null ? (object)right == null : left.Equals(right));
        }

        public static bool operator !=(HeaderInfo left, HeaderInfo right)
        {
            return !(left == right);
        }

        public static HeaderInfo Get(string name)
        {
            Debug.Assert(!string.IsNullOrEmpty(name));
            Debug.Assert(HttpRuleParser.GetTokenLength(name, 0) == name.Length);

            return KnownHeaders.TryGetKnownHeader(new KnownHeaders.StringCharSpan(name)) ?? new CustomHeaderInfo(name);
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
            public override HttpHeaderParser Parser => null;
            public override HttpHeaderType HeaderType => HttpHeaderType.Custom;
            public override byte[] RawBytes => null;

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
            private HttpHeaderType _headerType;
            private HttpHeaderParser _parser;
            private byte[] _rawBytes;

            public KnownHeaderInfo(string name, HttpHeaderType headerType, HttpHeaderParser parser)
            {
                Debug.Assert(!string.IsNullOrEmpty(name));
                Debug.Assert(HttpRuleParser.GetTokenLength(name, 0) == name.Length);
                Debug.Assert(headerType != HttpHeaderType.Custom);
                Debug.Assert(parser != null);

                _name = name;
                _headerType = headerType;
                _parser = parser;

                _rawBytes = System.Text.Encoding.UTF8.GetBytes(name);

                _hashcode = StringComparer.OrdinalIgnoreCase.GetHashCode(_name);
            }

            public KnownHeaderInfo(string name)
            {
                Debug.Assert(!string.IsNullOrEmpty(name));
                Debug.Assert(HttpRuleParser.GetTokenLength(name, 0) == name.Length);

                _name = name;
                _headerType = HttpHeaderType.Custom;
                _parser = null;

                _hashcode = StringComparer.OrdinalIgnoreCase.GetHashCode(_name);
            }

            public override string Name => _name;
            public override HttpHeaderParser Parser => _parser;
            public override HttpHeaderType HeaderType => _headerType;
            public override byte[] RawBytes => _rawBytes;

            public override bool Equals(HeaderInfo other)
            {
                // Reference equality
                return (object)this == (object)other;
            }

            public override int GetHashCode()
            {
                return _hashcode;
            }
        }

        // Note, a given known header can only ever be a single type
        [Flags]
        public enum HttpHeaderType
        {
            General = 0b1,
            Request = 0b10,
            Response = 0b100,
            Content = 0b1000,
            Custom = 0b10000,

            // Mask
            All = 0b11111
        }

        internal static class KnownHeaders
        {
            // If you add a new entry here, you need to add it to TryGetKnownHeader below as well.

            public static HeaderInfo Accept = new KnownHeaderInfo("Accept", HttpHeaderType.Request, MediaTypeHeaderParser.MultipleValuesParser);
            public static HeaderInfo AcceptCharset = new KnownHeaderInfo("Accept-Charset", HttpHeaderType.Request, GenericHeaderParser.MultipleValueStringWithQualityParser);
            public static HeaderInfo AcceptEncoding = new KnownHeaderInfo("Accept-Encoding", HttpHeaderType.Request, GenericHeaderParser.MultipleValueStringWithQualityParser);
            public static HeaderInfo AcceptLanguage = new KnownHeaderInfo("Accept-Language", HttpHeaderType.Request, GenericHeaderParser.MultipleValueStringWithQualityParser);
            public static HeaderInfo AcceptPatch = new KnownHeaderInfo("Accept-Patch");
            public static HeaderInfo AcceptRanges = new KnownHeaderInfo("Accept-Ranges", HttpHeaderType.Response, GenericHeaderParser.TokenListParser);
            public static HeaderInfo AccessControlAllowCredentials = new KnownHeaderInfo("Access-Control-Allow-Credentials");
            public static HeaderInfo AccessControlAllowHeaders = new KnownHeaderInfo("Access-Control-Allow-Headers");
            public static HeaderInfo AccessControlAllowMethods = new KnownHeaderInfo("Access-Control-Allow-Methods");
            public static HeaderInfo AccessControlAllowOrigin = new KnownHeaderInfo("Access-Control-Allow-Origin");
            public static HeaderInfo AccessControlExposeHeaders = new KnownHeaderInfo("Access-Control-Expose-Headers");
            public static HeaderInfo AccessControlMaxAge = new KnownHeaderInfo("Access-Control-Max-Age");
            public static HeaderInfo Age = new KnownHeaderInfo("Age", HttpHeaderType.Response, TimeSpanHeaderParser.Parser);
            public static HeaderInfo Allow = new KnownHeaderInfo("Allow", HttpHeaderType.Content, GenericHeaderParser.TokenListParser);
            public static HeaderInfo AltSvc = new KnownHeaderInfo("Alt-Svc");
            public static HeaderInfo Authorization = new KnownHeaderInfo("Authorization", HttpHeaderType.Request, GenericHeaderParser.SingleValueAuthenticationParser);
            public static HeaderInfo CacheControl = new KnownHeaderInfo("Cache-Control", HttpHeaderType.General, CacheControlHeaderParser.Parser);
            public static HeaderInfo Connection = new KnownHeaderInfo("Connection", HttpHeaderType.General, GenericHeaderParser.TokenListParser);
            public static HeaderInfo ContentDisposition = new KnownHeaderInfo("Content-Disposition", HttpHeaderType.Content, GenericHeaderParser.ContentDispositionParser);
            public static HeaderInfo ContentEncoding = new KnownHeaderInfo("Content-Encoding", HttpHeaderType.Content, GenericHeaderParser.TokenListParser);
            public static HeaderInfo ContentLanguage = new KnownHeaderInfo("Content-Language", HttpHeaderType.Content, GenericHeaderParser.TokenListParser);
            public static HeaderInfo ContentLength = new KnownHeaderInfo("Content-Length", HttpHeaderType.Content, Int64NumberHeaderParser.Parser);
            public static HeaderInfo ContentLocation = new KnownHeaderInfo("Content-Location", HttpHeaderType.Content, UriHeaderParser.RelativeOrAbsoluteUriParser);
            public static HeaderInfo ContentMD5 = new KnownHeaderInfo("Content-MD5", HttpHeaderType.Content, ByteArrayHeaderParser.Parser);
            public static HeaderInfo ContentRange = new KnownHeaderInfo("Content-Range", HttpHeaderType.Content, GenericHeaderParser.ContentRangeParser);
            public static HeaderInfo ContentSecurityPolicy = new KnownHeaderInfo("Content-Security-Policy");
            public static HeaderInfo ContentType = new KnownHeaderInfo("Content-Type", HttpHeaderType.Content, MediaTypeHeaderParser.SingleValueParser);
            public static HeaderInfo Cookie = new KnownHeaderInfo("Cookie");
            public static HeaderInfo Cookie2 = new KnownHeaderInfo("Cookie2");
            public static HeaderInfo Date = new KnownHeaderInfo("Date", HttpHeaderType.General, DateHeaderParser.Parser);
            public static HeaderInfo ETag = new KnownHeaderInfo("ETag", HttpHeaderType.Response, GenericHeaderParser.SingleValueEntityTagParser);
            public static HeaderInfo Expect = new KnownHeaderInfo("Expect", HttpHeaderType.Request, GenericHeaderParser.MultipleValueNameValueWithParametersParser);
            public static HeaderInfo Expires = new KnownHeaderInfo("Expires", HttpHeaderType.Content, DateHeaderParser.Parser);
            public static HeaderInfo From = new KnownHeaderInfo("From", HttpHeaderType.Request, GenericHeaderParser.MailAddressParser);
            public static HeaderInfo Host = new KnownHeaderInfo("Host", HttpHeaderType.Request, GenericHeaderParser.HostParser);
            public static HeaderInfo IfMatch = new KnownHeaderInfo("If-Match", HttpHeaderType.Request, GenericHeaderParser.MultipleValueEntityTagParser);
            public static HeaderInfo IfModifiedSince = new KnownHeaderInfo("If-Modified-Since", HttpHeaderType.Request, DateHeaderParser.Parser);
            public static HeaderInfo IfNoneMatch = new KnownHeaderInfo("If-None-Match", HttpHeaderType.Request, GenericHeaderParser.MultipleValueEntityTagParser);
            public static HeaderInfo IfRange = new KnownHeaderInfo("If-Range", HttpHeaderType.Request, GenericHeaderParser.RangeConditionParser);
            public static HeaderInfo IfUnmodifiedSince = new KnownHeaderInfo("If-Unmodified-Since", HttpHeaderType.Request, DateHeaderParser.Parser);
            public static HeaderInfo KeepAlive = new KnownHeaderInfo("Keep-Alive");
            public static HeaderInfo LastModified = new KnownHeaderInfo("Last-Modified", HttpHeaderType.Content, DateHeaderParser.Parser);
            public static HeaderInfo Link = new KnownHeaderInfo("Link");
            public static HeaderInfo Location = new KnownHeaderInfo("Location", HttpHeaderType.Response, UriHeaderParser.RelativeOrAbsoluteUriParser);
            public static HeaderInfo MaxForwards = new KnownHeaderInfo("Max-Forwards", HttpHeaderType.Request, Int32NumberHeaderParser.Parser);
            public static HeaderInfo Origin = new KnownHeaderInfo("Origin");
            public static HeaderInfo P3P = new KnownHeaderInfo("P3P");
            public static HeaderInfo Pragma = new KnownHeaderInfo("Pragma", HttpHeaderType.General, GenericHeaderParser.MultipleValueNameValueParser);
            public static HeaderInfo ProxyAuthenticate = new KnownHeaderInfo("Proxy-Authenticate", HttpHeaderType.Response, GenericHeaderParser.MultipleValueAuthenticationParser);
            public static HeaderInfo ProxyAuthorization = new KnownHeaderInfo("Proxy-Authorization", HttpHeaderType.Request, GenericHeaderParser.SingleValueAuthenticationParser);
            public static HeaderInfo ProxyConnection = new KnownHeaderInfo("Proxy-Connection");
            public static HeaderInfo PublicKeyPins = new KnownHeaderInfo("Public-Key-Pins");
            public static HeaderInfo Range = new KnownHeaderInfo("Range", HttpHeaderType.Request, GenericHeaderParser.RangeParser);
            public static HeaderInfo Referer = new KnownHeaderInfo("Referer", HttpHeaderType.Request, UriHeaderParser.RelativeOrAbsoluteUriParser); // NB: The spelling-mistake "Referer" for "Referrer" must be matched.
            public static HeaderInfo RetryAfter = new KnownHeaderInfo("Retry-After", HttpHeaderType.Response, GenericHeaderParser.RetryConditionParser);
            public static HeaderInfo SecWebSocketAccept = new KnownHeaderInfo("Sec-WebSocket-Accept");
            public static HeaderInfo SecWebSocketExtensions = new KnownHeaderInfo("Sec-WebSocket-Extensions");
            public static HeaderInfo SecWebSocketKey = new KnownHeaderInfo("Sec-WebSocket-Key");
            public static HeaderInfo SecWebSocketProtocol = new KnownHeaderInfo("Sec-WebSocket-Protocol");
            public static HeaderInfo SecWebSocketVersion = new KnownHeaderInfo("Sec-WebSocket-Version");
            public static HeaderInfo Server = new KnownHeaderInfo("Server", HttpHeaderType.Response, ProductInfoHeaderParser.MultipleValueParser);
            public static HeaderInfo SetCookie = new KnownHeaderInfo("Set-Cookie");
            public static HeaderInfo SetCookie2 = new KnownHeaderInfo("Set-Cookie2");
            public static HeaderInfo StrictTransportSecurity = new KnownHeaderInfo("Strict-Transport-Security");
            public static HeaderInfo TE = new KnownHeaderInfo("TE", HttpHeaderType.Request, TransferCodingHeaderParser.MultipleValueWithQualityParser);
            public static HeaderInfo TSV = new KnownHeaderInfo("TSV");
            public static HeaderInfo Trailer = new KnownHeaderInfo("Trailer", HttpHeaderType.General, GenericHeaderParser.TokenListParser);
            public static HeaderInfo TransferEncoding = new KnownHeaderInfo("Transfer-Encoding", HttpHeaderType.General, TransferCodingHeaderParser.MultipleValueParser);
            public static HeaderInfo Upgrade = new KnownHeaderInfo("Upgrade", HttpHeaderType.General, GenericHeaderParser.MultipleValueProductParser);
            public static HeaderInfo UpgradeInsecureRequests = new KnownHeaderInfo("Upgrade-Insecure-Requests");
            public static HeaderInfo UserAgent = new KnownHeaderInfo("User-Agent", HttpHeaderType.Request, ProductInfoHeaderParser.MultipleValueParser);
            public static HeaderInfo Vary = new KnownHeaderInfo("Vary", HttpHeaderType.Response, GenericHeaderParser.TokenListParser);
            public static HeaderInfo Via = new KnownHeaderInfo("Via", HttpHeaderType.General, GenericHeaderParser.MultipleValueViaParser);
            public static HeaderInfo WWWAuthenticate = new KnownHeaderInfo("WWW-Authenticate", HttpHeaderType.Response, GenericHeaderParser.MultipleValueAuthenticationParser);
            public static HeaderInfo Warning = new KnownHeaderInfo("Warning", HttpHeaderType.General, GenericHeaderParser.MultipleValueWarningParser);
            public static HeaderInfo XAspNetVersion = new KnownHeaderInfo("X-AspNet-Version");
            public static HeaderInfo XContentDuration = new KnownHeaderInfo("X-Content-Duration");
            public static HeaderInfo XContentTypeOptions = new KnownHeaderInfo("X-Content-Type-Options");
            public static HeaderInfo XFrameOptions = new KnownHeaderInfo("X-Frame-Options");
            public static HeaderInfo XMSEdgeRef = new KnownHeaderInfo("X-MSEdge-Ref");
            public static HeaderInfo XPoweredBy = new KnownHeaderInfo("X-Powered-By");
            public static HeaderInfo XRequestID = new KnownHeaderInfo("X-Request-ID");
            public static HeaderInfo XUACompatible = new KnownHeaderInfo("X-UA-Compatible");

            // TODO: This isn't quite right, as it only matches known headers with the standard capitalization (as listed above).
            // We need to handle cases where non-standard capitalization is used, e.g. "content-length".
            // These need to be treated as known headers, but have their capitalization preserved.
            // This probably means something like adding a "NonstandardKnownHeaderInfo".  Ugh.

            // Helper interface for making TryGetKnownHeader generic over strings and char arrays
            internal interface ICharSpan
            {
                int Length { get; }
                char CharAt(int index);
                bool IsEqualTo(string other);
            }

            internal struct StringCharSpan : ICharSpan
            {
                private string _string;

                public StringCharSpan(string s)
                {
                    _string = s;
                }

                public int Length => _string.Length;

                public char CharAt(int index) => _string[index];

                public bool IsEqualTo(string other) => StringComparer.Ordinal.Equals(_string, other);
            }

            // TODO: This method probably ought to live on HeaderInfo class, not here

            internal static HeaderInfo TryGetKnownHeader<T>(T key)
                where T : struct, ICharSpan     // Enforce struct for performance
            {
                // When adding a new constant, add it to HttpKnownHeaderNames.cs as well.

                // The lookup works as follows: first switch on the length of the passed-in key.
                //
                //  - If there is only one known header of that length, set potentialHeader to that known header
                //    and goto TryMatch to see if the key fully matches potentialHeader.
                //
                //  - If there are more than one known headers of that length, switch on a unique char from that
                //    set of same-length known headers. Typically this will be the first char, but some sets of
                //    same-length known headers do not have unique chars in the first position, so a char in a
                //    position further in the strings is used. If the char from the key matches one of the
                //    known headers, set potentialHeader to that known header and goto TryMatch to see if the key
                //    fully matches potentialHeader.
                //
                //  - Otherwise, there is no match, so set the out param to null and return false.
                //
                // Matching is case-sensitive: we only want to return a known header that exactly matches the key.

                HeaderInfo potentialHeader = null;

                int length = key.Length;
                switch (length)
                {
                    case 2:
                        potentialHeader = TE; goto TryMatch; // TE

                    case 3:
                        switch (key.CharAt(0))
                        {
                            case 'A': potentialHeader = Age; goto TryMatch; // [A]ge
                            case 'P': potentialHeader = P3P; goto TryMatch; // [P]3P
                            case 'T': potentialHeader = TSV; goto TryMatch; // [T]SV
                            case 'V': potentialHeader = Via; goto TryMatch; // [V]ia
                        }
                        break;

                    case 4:
                        switch (key.CharAt(0))
                        {
                            case 'D': potentialHeader = Date; goto TryMatch; // [D]ate
                            case 'E': potentialHeader = ETag; goto TryMatch; // [E]Tag
                            case 'F': potentialHeader = From; goto TryMatch; // [F]rom
                            case 'H': potentialHeader = Host; goto TryMatch; // [H]ost
                            case 'L': potentialHeader = Link; goto TryMatch; // [L]ink
                            case 'V': potentialHeader = Vary; goto TryMatch; // [V]ary
                        }
                        break;

                    case 5:
                        switch (key.CharAt(0))
                        {
                            case 'A': potentialHeader = Allow; goto TryMatch; // [A]llow
                            case 'R': potentialHeader = Range; goto TryMatch; // [R]ange
                        }
                        break;

                    case 6:
                        switch (key.CharAt(0))
                        {
                            case 'A': potentialHeader = Accept; goto TryMatch; // [A]ccept
                            case 'C': potentialHeader = Cookie; goto TryMatch; // [C]ookie
                            case 'E': potentialHeader = Expect; goto TryMatch; // [E]xpect
                            case 'O': potentialHeader = Origin; goto TryMatch; // [O]rigin
                            case 'P': potentialHeader = Pragma; goto TryMatch; // [P]ragma
                            case 'S': potentialHeader = Server; goto TryMatch; // [S]erver
                        }
                        break;

                    case 7:
                        switch (key.CharAt(0))
                        {
                            case 'A': potentialHeader = AltSvc; goto TryMatch;  // [A]lt-Svc
                            case 'C': potentialHeader = Cookie2; goto TryMatch; // [C]ookie2
                            case 'E': potentialHeader = Expires; goto TryMatch; // [E]xpires
                            case 'R': potentialHeader = Referer; goto TryMatch; // [R]eferer
                            case 'T': potentialHeader = Trailer; goto TryMatch; // [T]railer
                            case 'U': potentialHeader = Upgrade; goto TryMatch; // [U]pgrade
                            case 'W': potentialHeader = Warning; goto TryMatch; // [W]arning
                        }
                        break;

                    case 8:
                        switch (key.CharAt(3))
                        {
                            case 'M': potentialHeader = IfMatch; goto TryMatch;  // If-[M]atch
                            case 'R': potentialHeader = IfRange; goto TryMatch;  // If-[R]ange
                            case 'a': potentialHeader = Location; goto TryMatch; // Loc[a]tion
                        }
                        break;

                    case 10:
                        switch (key.CharAt(0))
                        {
                            case 'C': potentialHeader = Connection; goto TryMatch; // [C]onnection
                            case 'K': potentialHeader = KeepAlive; goto TryMatch;  // [K]eep-Alive
                            case 'S': potentialHeader = SetCookie; goto TryMatch;  // [S]et-Cookie
                            case 'U': potentialHeader = UserAgent; goto TryMatch;  // [U]ser-Agent
                        }
                        break;

                    case 11:
                        switch (key.CharAt(0))
                        {
                            case 'C': potentialHeader = ContentMD5; goto TryMatch; // [C]ontent-MD5
                            case 'R': potentialHeader = RetryAfter; goto TryMatch; // [R]etry-After
                            case 'S': potentialHeader = SetCookie2; goto TryMatch; // [S]et-Cookie2
                        }
                        break;

                    case 12:
                        switch (key.CharAt(2))
                        {
                            case 'c': potentialHeader = AcceptPatch; goto TryMatch; // Ac[c]ept-Patch
                            case 'n': potentialHeader = ContentType; goto TryMatch; // Co[n]tent-Type
                            case 'x': potentialHeader = MaxForwards; goto TryMatch; // Ma[x]-Forwards
                            case 'M': potentialHeader = XMSEdgeRef; goto TryMatch;  // X-[M]SEdge-Ref
                            case 'P': potentialHeader = XPoweredBy; goto TryMatch;  // X-[P]owered-By
                            case 'R': potentialHeader = XRequestID; goto TryMatch;  // X-[R]equest-ID
                        }
                        break;

                    case 13:
                        switch (key.CharAt(6))
                        {
                            case '-': potentialHeader = AcceptRanges; goto TryMatch;  // Accept[-]Ranges
                            case 'i': potentialHeader = Authorization; goto TryMatch; // Author[i]zation
                            case 'C': potentialHeader = CacheControl; goto TryMatch;  // Cache-[C]ontrol
                            case 't': potentialHeader = ContentRange; goto TryMatch;  // Conten[t]-Range
                            case 'e': potentialHeader = IfNoneMatch; goto TryMatch;   // If-Non[e]-Match
                            case 'o': potentialHeader = LastModified; goto TryMatch;  // Last-M[o]dified
                        }
                        break;

                    case 14:
                        switch (key.CharAt(0))
                        {
                            case 'A': potentialHeader = AcceptCharset; goto TryMatch; // [A]ccept-Charset
                            case 'C': potentialHeader = ContentLength; goto TryMatch; // [C]ontent-Length
                        }
                        break;

                    case 15:
                        switch (key.CharAt(7))
                        {
                            case '-': potentialHeader = XFrameOptions; goto TryMatch;  // X-Frame[-]Options
                            case 'm': potentialHeader = XUACompatible; goto TryMatch;  // X-UA-Co[m]patible
                            case 'E': potentialHeader = AcceptEncoding; goto TryMatch; // Accept-[E]ncoding
                            case 'K': potentialHeader = PublicKeyPins; goto TryMatch;  // Public-[K]ey-Pins
                            case 'L': potentialHeader = AcceptLanguage; goto TryMatch; // Accept-[L]anguage
                        }
                        break;

                    case 16:
                        switch (key.CharAt(11))
                        {
                            case 'o': potentialHeader = ContentEncoding; goto TryMatch; // Content-Enc[o]ding
                            case 'g': potentialHeader = ContentLanguage; goto TryMatch; // Content-Lan[g]uage
                            case 'a': potentialHeader = ContentLocation; goto TryMatch; // Content-Loc[a]tion
                            case 'c': potentialHeader = ProxyConnection; goto TryMatch; // Proxy-Conne[c]tion
                            case 'i': potentialHeader = WWWAuthenticate; goto TryMatch; // WWW-Authent[i]cate
                            case 'r': potentialHeader = XAspNetVersion; goto TryMatch;  // X-AspNet-Ve[r]sion
                        }
                        break;

                    case 17:
                        switch (key.CharAt(0))
                        {
                            case 'I': potentialHeader = IfModifiedSince; goto TryMatch;  // [I]f-Modified-Since
                            case 'S': potentialHeader = SecWebSocketKey; goto TryMatch;  // [S]ec-WebSocket-Key
                            case 'T': potentialHeader = TransferEncoding; goto TryMatch; // [T]ransfer-Encoding
                        }
                        break;

                    case 18:
                        switch (key.CharAt(0))
                        {
                            case 'P': potentialHeader = ProxyAuthenticate; goto TryMatch; // [P]roxy-Authenticate
                            case 'X': potentialHeader = XContentDuration; goto TryMatch;  // [X]-Content-Duration
                        }
                        break;

                    case 19:
                        switch (key.CharAt(0))
                        {
                            case 'C': potentialHeader = ContentDisposition; goto TryMatch; // [C]ontent-Disposition
                            case 'I': potentialHeader = IfUnmodifiedSince; goto TryMatch;  // [I]f-Unmodified-Since
                            case 'P': potentialHeader = ProxyAuthorization; goto TryMatch; // [P]roxy-Authorization
                        }
                        break;

                    case 20:
                        potentialHeader = SecWebSocketAccept; goto TryMatch; // Sec-WebSocket-Accept

                    case 21:
                        potentialHeader = SecWebSocketVersion; goto TryMatch; // Sec-WebSocket-Version

                    case 22:
                        switch (key.CharAt(0))
                        {
                            case 'A': potentialHeader = AccessControlMaxAge; goto TryMatch;  // [A]ccess-Control-Max-Age
                            case 'S': potentialHeader = SecWebSocketProtocol; goto TryMatch; // [S]ec-WebSocket-Protocol
                            case 'X': potentialHeader = XContentTypeOptions; goto TryMatch;  // [X]-Content-Type-Options
                        }
                        break;

                    case 23:
                        potentialHeader = ContentSecurityPolicy; goto TryMatch; // Content-Security-Policy

                    case 24:
                        potentialHeader = SecWebSocketExtensions; goto TryMatch; // Sec-WebSocket-Extensions

                    case 25:
                        switch (key.CharAt(0))
                        {
                            case 'S': potentialHeader = StrictTransportSecurity; goto TryMatch; // [S]trict-Transport-Security
                            case 'U': potentialHeader = UpgradeInsecureRequests; goto TryMatch; // [U]pgrade-Insecure-Requests
                        }
                        break;

                    case 27:
                        potentialHeader = AccessControlAllowOrigin; goto TryMatch; // Access-Control-Allow-Origin

                    case 28:
                        switch (key.CharAt(21))
                        {
                            case 'H': potentialHeader = AccessControlAllowHeaders; goto TryMatch; // Access-Control-Allow-[H]eaders
                            case 'M': potentialHeader = AccessControlAllowMethods; goto TryMatch; // Access-Control-Allow-[M]ethods
                        }
                        break;

                    case 29:
                        potentialHeader = AccessControlExposeHeaders; goto TryMatch; // Access-Control-Expose-Headers

                    case 32:
                        potentialHeader = AccessControlAllowCredentials; goto TryMatch; // Access-Control-Allow-Credentials
                }

                return null;

                TryMatch:
                Debug.Assert(potentialHeader != null);
                Debug.Assert(potentialHeader.Name.Length == length);

                return key.IsEqualTo(potentialHeader.Name) ? potentialHeader : null;
            }
        }
    }
}
