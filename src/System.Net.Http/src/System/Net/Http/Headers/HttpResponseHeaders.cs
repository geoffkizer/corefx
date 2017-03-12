// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace System.Net.Http.Headers
{
    [SuppressMessage("Microsoft.Naming", "CA1710:IdentifiersShouldHaveCorrectSuffix",
        Justification = "This is not a collection")]
    public sealed class HttpResponseHeaders : HttpHeaders
    {
        private static readonly Dictionary<HeaderKey, HttpHeaderParser> s_parserStore = CreateParserStore();
        private static readonly HashSet<HeaderKey> s_invalidHeaders = CreateInvalidHeaders();

        private HttpGeneralHeaders _generalHeaders;
        private HttpHeaderValueCollection<string> _acceptRanges;
        private HttpHeaderValueCollection<AuthenticationHeaderValue> _wwwAuthenticate;
        private HttpHeaderValueCollection<AuthenticationHeaderValue> _proxyAuthenticate;
        private HttpHeaderValueCollection<ProductInfoHeaderValue> _server;
        private HttpHeaderValueCollection<string> _vary;

        #region Response Headers

        public HttpHeaderValueCollection<string> AcceptRanges
        {
            get
            {
                if (_acceptRanges == null)
                {
                    _acceptRanges = new HttpHeaderValueCollection<string>(HttpKnownHeaderKeys.AcceptRanges,
                        this, HeaderUtilities.TokenValidator);
                }
                return _acceptRanges;
            }
        }

        public TimeSpan? Age
        {
            get { return HeaderUtilities.GetTimeSpanValue(HttpKnownHeaderKeys.Age, this); }
            set { SetOrRemoveParsedValue(HttpKnownHeaderKeys.Age, value); }
        }

        public EntityTagHeaderValue ETag
        {
            get { return (EntityTagHeaderValue)GetParsedValues(HttpKnownHeaderKeys.ETag); }
            set { SetOrRemoveParsedValue(HttpKnownHeaderKeys.ETag, value); }
        }

        public Uri Location
        {
            get { return (Uri)GetParsedValues(HttpKnownHeaderKeys.Location); }
            set { SetOrRemoveParsedValue(HttpKnownHeaderKeys.Location, value); }
        }

        public HttpHeaderValueCollection<AuthenticationHeaderValue> ProxyAuthenticate
        {
            get
            {
                if (_proxyAuthenticate == null)
                {
                    _proxyAuthenticate = new HttpHeaderValueCollection<AuthenticationHeaderValue>(
                        HttpKnownHeaderKeys.ProxyAuthenticate, this);
                }
                return _proxyAuthenticate;
            }
        }

        public RetryConditionHeaderValue RetryAfter
        {
            get { return (RetryConditionHeaderValue)GetParsedValues(HttpKnownHeaderKeys.RetryAfter); }
            set { SetOrRemoveParsedValue(HttpKnownHeaderKeys.RetryAfter, value); }
        }

        public HttpHeaderValueCollection<ProductInfoHeaderValue> Server
        {
            get
            {
                if (_server == null)
                {
                    _server = new HttpHeaderValueCollection<ProductInfoHeaderValue>(HttpKnownHeaderKeys.Server, this);
                }
                return _server;
            }
        }

        public HttpHeaderValueCollection<string> Vary
        {
            get
            {
                if (_vary == null)
                {
                    _vary = new HttpHeaderValueCollection<string>(HttpKnownHeaderKeys.Vary,
                        this, HeaderUtilities.TokenValidator);
                }
                return _vary;
            }
        }

        public HttpHeaderValueCollection<AuthenticationHeaderValue> WwwAuthenticate
        {
            get
            {
                if (_wwwAuthenticate == null)
                {
                    _wwwAuthenticate = new HttpHeaderValueCollection<AuthenticationHeaderValue>(
                        HttpKnownHeaderKeys.WWWAuthenticate, this);
                }
                return _wwwAuthenticate;
            }
        }

        #endregion

        #region General Headers

        public CacheControlHeaderValue CacheControl
        {
            get { return _generalHeaders.CacheControl; }
            set { _generalHeaders.CacheControl = value; }
        }

        public HttpHeaderValueCollection<string> Connection
        {
            get { return _generalHeaders.Connection; }
        }

        public bool? ConnectionClose
        {
            get { return _generalHeaders.ConnectionClose; }
            set { _generalHeaders.ConnectionClose = value; }
        }

        public DateTimeOffset? Date
        {
            get { return _generalHeaders.Date; }
            set { _generalHeaders.Date = value; }
        }

        public HttpHeaderValueCollection<NameValueHeaderValue> Pragma
        {
            get { return _generalHeaders.Pragma; }
        }

        public HttpHeaderValueCollection<string> Trailer
        {
            get { return _generalHeaders.Trailer; }
        }

        public HttpHeaderValueCollection<TransferCodingHeaderValue> TransferEncoding
        {
            get { return _generalHeaders.TransferEncoding; }
        }

        public bool? TransferEncodingChunked
        {
            get { return _generalHeaders.TransferEncodingChunked; }
            set { _generalHeaders.TransferEncodingChunked = value; }
        }

        public HttpHeaderValueCollection<ProductHeaderValue> Upgrade
        {
            get { return _generalHeaders.Upgrade; }
        }

        public HttpHeaderValueCollection<ViaHeaderValue> Via
        {
            get { return _generalHeaders.Via; }
        }

        public HttpHeaderValueCollection<WarningHeaderValue> Warning
        {
            get { return _generalHeaders.Warning; }
        }

        #endregion

        internal HttpResponseHeaders()
        {
            _generalHeaders = new HttpGeneralHeaders(this);

            base.SetConfiguration(s_parserStore, s_invalidHeaders);
        }

        private static Dictionary<HeaderKey, HttpHeaderParser> CreateParserStore()
        {
            var parserStore = new Dictionary<HeaderKey, HttpHeaderParser>();

            parserStore.Add(HttpKnownHeaderKeys.AcceptRanges, GenericHeaderParser.TokenListParser);
            parserStore.Add(HttpKnownHeaderKeys.Age, TimeSpanHeaderParser.Parser);
            parserStore.Add(HttpKnownHeaderKeys.ETag, GenericHeaderParser.SingleValueEntityTagParser);
            parserStore.Add(HttpKnownHeaderKeys.Location, UriHeaderParser.RelativeOrAbsoluteUriParser);
            parserStore.Add(HttpKnownHeaderKeys.ProxyAuthenticate, GenericHeaderParser.MultipleValueAuthenticationParser);
            parserStore.Add(HttpKnownHeaderKeys.RetryAfter, GenericHeaderParser.RetryConditionParser);
            parserStore.Add(HttpKnownHeaderKeys.Server, ProductInfoHeaderParser.MultipleValueParser);
            parserStore.Add(HttpKnownHeaderKeys.Vary, GenericHeaderParser.TokenListParser);
            parserStore.Add(HttpKnownHeaderKeys.WWWAuthenticate, GenericHeaderParser.MultipleValueAuthenticationParser);

            HttpGeneralHeaders.AddParsers(parserStore);

            return parserStore;
        }

        private static HashSet<HeaderKey> CreateInvalidHeaders()
        {
            var invalidHeaders = new HashSet<HeaderKey>();
            HttpContentHeaders.AddKnownHeaders(invalidHeaders);
            return invalidHeaders;

            // Note: Reserved request header names are allowed as custom response header names.  Reserved request
            // headers have no defined meaning or format when used on a response. This enables a client to accept
            // any headers sent from the server as either content headers or response headers.
        }

        internal static void AddKnownHeaders(HashSet<HeaderKey> headerSet)
        {
            Debug.Assert(headerSet != null);

            headerSet.Add(HttpKnownHeaderKeys.AcceptRanges);
            headerSet.Add(HttpKnownHeaderKeys.Age);
            headerSet.Add(HttpKnownHeaderKeys.ETag);
            headerSet.Add(HttpKnownHeaderKeys.Location);
            headerSet.Add(HttpKnownHeaderKeys.ProxyAuthenticate);
            headerSet.Add(HttpKnownHeaderKeys.RetryAfter);
            headerSet.Add(HttpKnownHeaderKeys.Server);
            headerSet.Add(HttpKnownHeaderKeys.Vary);
            headerSet.Add(HttpKnownHeaderKeys.WWWAuthenticate);
        }

        internal override void AddHeaders(HttpHeaders sourceHeaders)
        {
            base.AddHeaders(sourceHeaders);
            HttpResponseHeaders sourceResponseHeaders = sourceHeaders as HttpResponseHeaders;
            Debug.Assert(sourceResponseHeaders != null);

            // Copy special values, but do not overwrite
            _generalHeaders.AddSpecialsFrom(sourceResponseHeaders._generalHeaders);
        }
    }
}
