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
    public sealed class HttpRequestHeaders : HttpHeaders
    {
        private static readonly Dictionary<HeaderKey, HttpHeaderParser> s_parserStore = CreateParserStore();
        private static readonly HashSet<HeaderKey> s_invalidHeaders = CreateInvalidHeaders();

        private HttpGeneralHeaders _generalHeaders;
        private HttpHeaderValueCollection<MediaTypeWithQualityHeaderValue> _accept;
        private HttpHeaderValueCollection<NameValueWithParametersHeaderValue> _expect;
        private bool _expectContinueSet;
        private HttpHeaderValueCollection<EntityTagHeaderValue> _ifMatch;
        private HttpHeaderValueCollection<EntityTagHeaderValue> _ifNoneMatch;
        private HttpHeaderValueCollection<TransferCodingWithQualityHeaderValue> _te;
        private HttpHeaderValueCollection<ProductInfoHeaderValue> _userAgent;
        private HttpHeaderValueCollection<StringWithQualityHeaderValue> _acceptCharset;
        private HttpHeaderValueCollection<StringWithQualityHeaderValue> _acceptEncoding;
        private HttpHeaderValueCollection<StringWithQualityHeaderValue> _acceptLanguage;

        #region Request Headers

        public HttpHeaderValueCollection<MediaTypeWithQualityHeaderValue> Accept
        {
            get
            {
                if (_accept == null)
                {
                    _accept = new HttpHeaderValueCollection<MediaTypeWithQualityHeaderValue>(
                        HttpKnownHeaderKeys.Accept, this);
                }
                return _accept;
            }
        }

        [SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly", MessageId = "Charset",
            Justification = "The HTTP header name is 'Accept-Charset'.")]
        public HttpHeaderValueCollection<StringWithQualityHeaderValue> AcceptCharset
        {
            get
            {
                if (_acceptCharset == null)
                {
                    _acceptCharset = new HttpHeaderValueCollection<StringWithQualityHeaderValue>(
                        HttpKnownHeaderKeys.AcceptCharset, this);
                }
                return _acceptCharset;
            }
        }

        public HttpHeaderValueCollection<StringWithQualityHeaderValue> AcceptEncoding
        {
            get
            {
                if (_acceptEncoding == null)
                {
                    _acceptEncoding = new HttpHeaderValueCollection<StringWithQualityHeaderValue>(
                        HttpKnownHeaderKeys.AcceptEncoding, this);
                }
                return _acceptEncoding;
            }
        }

        public HttpHeaderValueCollection<StringWithQualityHeaderValue> AcceptLanguage
        {
            get
            {
                if (_acceptLanguage == null)
                {
                    _acceptLanguage = new HttpHeaderValueCollection<StringWithQualityHeaderValue>(
                        HttpKnownHeaderKeys.AcceptLanguage, this);
                }
                return _acceptLanguage;
            }
        }

        public AuthenticationHeaderValue Authorization
        {
            get { return (AuthenticationHeaderValue)GetParsedValues(HttpKnownHeaderKeys.Authorization); }
            set { SetOrRemoveParsedValue(HttpKnownHeaderKeys.Authorization, value); }
        }

        public HttpHeaderValueCollection<NameValueWithParametersHeaderValue> Expect
        {
            get { return ExpectCore; }
        }

        public bool? ExpectContinue
        {
            get
            {
                if (ExpectCore.IsSpecialValueSet)
                {
                    return true;
                }
                if (_expectContinueSet)
                {
                    return false;
                }
                return null;
            }
            set
            {
                if (value == true)
                {
                    _expectContinueSet = true;
                    ExpectCore.SetSpecialValue();
                }
                else
                {
                    _expectContinueSet = value != null;
                    ExpectCore.RemoveSpecialValue();
                }
            }
        }

        public string From
        {
            get { return (string)GetParsedValues(HttpKnownHeaderKeys.From); }
            set
            {
                // Null and empty string are equivalent. In this case it means, remove the From header value (if any).
                if (value == string.Empty)
                {
                    value = null;
                }

                if ((value != null) && !HeaderUtilities.IsValidEmailAddress(value))
                {
                    throw new FormatException(SR.net_http_headers_invalid_from_header);
                }
                SetOrRemoveParsedValue(HttpKnownHeaderKeys.From, value);
            }
        }

        public string Host
        {
            get { return (string)GetParsedValues(HttpKnownHeaderKeys.Host); }
            set
            {
                // Null and empty string are equivalent. In this case it means, remove the Host header value (if any).
                if (value == string.Empty)
                {
                    value = null;
                }

                string host = null;
                if ((value != null) && (HttpRuleParser.GetHostLength(value, 0, false, out host) != value.Length))
                {
                    throw new FormatException(SR.net_http_headers_invalid_host_header);
                }
                SetOrRemoveParsedValue(HttpKnownHeaderKeys.Host, value);
            }
        }

        public HttpHeaderValueCollection<EntityTagHeaderValue> IfMatch
        {
            get
            {
                if (_ifMatch == null)
                {
                    _ifMatch = new HttpHeaderValueCollection<EntityTagHeaderValue>(
                        HttpKnownHeaderKeys.IfMatch, this);
                }
                return _ifMatch;
            }
        }

        public DateTimeOffset? IfModifiedSince
        {
            get { return HeaderUtilities.GetDateTimeOffsetValue(HttpKnownHeaderKeys.IfModifiedSince, this); }
            set { SetOrRemoveParsedValue(HttpKnownHeaderKeys.IfModifiedSince, value); }
        }

        public HttpHeaderValueCollection<EntityTagHeaderValue> IfNoneMatch
        {
            get
            {
                if (_ifNoneMatch == null)
                {
                    _ifNoneMatch = new HttpHeaderValueCollection<EntityTagHeaderValue>(
                        HttpKnownHeaderKeys.IfNoneMatch, this);
                }
                return _ifNoneMatch;
            }
        }

        public RangeConditionHeaderValue IfRange
        {
            get { return (RangeConditionHeaderValue)GetParsedValues(HttpKnownHeaderKeys.IfRange); }
            set { SetOrRemoveParsedValue(HttpKnownHeaderKeys.IfRange, value); }
        }

        public DateTimeOffset? IfUnmodifiedSince
        {
            get { return HeaderUtilities.GetDateTimeOffsetValue(HttpKnownHeaderKeys.IfUnmodifiedSince, this); }
            set { SetOrRemoveParsedValue(HttpKnownHeaderKeys.IfUnmodifiedSince, value); }
        }

        public int? MaxForwards
        {
            get
            {
                object storedValue = GetParsedValues(HttpKnownHeaderKeys.MaxForwards);
                if (storedValue != null)
                {
                    return (int)storedValue;
                }
                return null;
            }
            set { SetOrRemoveParsedValue(HttpKnownHeaderKeys.MaxForwards, value); }
        }


        public AuthenticationHeaderValue ProxyAuthorization
        {
            get { return (AuthenticationHeaderValue)GetParsedValues(HttpKnownHeaderKeys.ProxyAuthorization); }
            set { SetOrRemoveParsedValue(HttpKnownHeaderKeys.ProxyAuthorization, value); }
        }

        public RangeHeaderValue Range
        {
            get { return (RangeHeaderValue)GetParsedValues(HttpKnownHeaderKeys.Range); }
            set { SetOrRemoveParsedValue(HttpKnownHeaderKeys.Range, value); }
        }

        public Uri Referrer
        {
            get { return (Uri)GetParsedValues(HttpKnownHeaderKeys.Referer); }
            set { SetOrRemoveParsedValue(HttpKnownHeaderKeys.Referer, value); }
        }

        public HttpHeaderValueCollection<TransferCodingWithQualityHeaderValue> TE
        {
            get
            {
                if (_te == null)
                {
                    _te = new HttpHeaderValueCollection<TransferCodingWithQualityHeaderValue>(
                        HttpKnownHeaderKeys.TE, this);
                }
                return _te;
            }
        }

        public HttpHeaderValueCollection<ProductInfoHeaderValue> UserAgent
        {
            get
            {
                if (_userAgent == null)
                {
                    _userAgent = new HttpHeaderValueCollection<ProductInfoHeaderValue>(HttpKnownHeaderKeys.UserAgent,
                        this);
                }
                return _userAgent;
            }
        }

        private HttpHeaderValueCollection<NameValueWithParametersHeaderValue> ExpectCore
        {
            get
            {
                if (_expect == null)
                {
                    _expect = new HttpHeaderValueCollection<NameValueWithParametersHeaderValue>(
                        HttpKnownHeaderKeys.Expect, this, HeaderUtilities.ExpectContinue);
                }
                return _expect;
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

        internal HttpRequestHeaders()
        {
            _generalHeaders = new HttpGeneralHeaders(this);

            base.SetConfiguration(s_parserStore, s_invalidHeaders);
        }

        private static Dictionary<HeaderKey, HttpHeaderParser> CreateParserStore()
        {
            var parserStore = new Dictionary<HeaderKey, HttpHeaderParser>();

            parserStore.Add(HttpKnownHeaderKeys.Accept, MediaTypeHeaderParser.MultipleValuesParser);
            parserStore.Add(HttpKnownHeaderKeys.AcceptCharset, GenericHeaderParser.MultipleValueStringWithQualityParser);
            parserStore.Add(HttpKnownHeaderKeys.AcceptEncoding, GenericHeaderParser.MultipleValueStringWithQualityParser);
            parserStore.Add(HttpKnownHeaderKeys.AcceptLanguage, GenericHeaderParser.MultipleValueStringWithQualityParser);
            parserStore.Add(HttpKnownHeaderKeys.Authorization, GenericHeaderParser.SingleValueAuthenticationParser);
            parserStore.Add(HttpKnownHeaderKeys.Expect, GenericHeaderParser.MultipleValueNameValueWithParametersParser);
            parserStore.Add(HttpKnownHeaderKeys.From, GenericHeaderParser.MailAddressParser);
            parserStore.Add(HttpKnownHeaderKeys.Host, GenericHeaderParser.HostParser);
            parserStore.Add(HttpKnownHeaderKeys.IfMatch, GenericHeaderParser.MultipleValueEntityTagParser);
            parserStore.Add(HttpKnownHeaderKeys.IfModifiedSince, DateHeaderParser.Parser);
            parserStore.Add(HttpKnownHeaderKeys.IfNoneMatch, GenericHeaderParser.MultipleValueEntityTagParser);
            parserStore.Add(HttpKnownHeaderKeys.IfRange, GenericHeaderParser.RangeConditionParser);
            parserStore.Add(HttpKnownHeaderKeys.IfUnmodifiedSince, DateHeaderParser.Parser);
            parserStore.Add(HttpKnownHeaderKeys.MaxForwards, Int32NumberHeaderParser.Parser);
            parserStore.Add(HttpKnownHeaderKeys.ProxyAuthorization, GenericHeaderParser.SingleValueAuthenticationParser);
            parserStore.Add(HttpKnownHeaderKeys.Range, GenericHeaderParser.RangeParser);
            parserStore.Add(HttpKnownHeaderKeys.Referer, UriHeaderParser.RelativeOrAbsoluteUriParser);
            parserStore.Add(HttpKnownHeaderKeys.TE, TransferCodingHeaderParser.MultipleValueWithQualityParser);
            parserStore.Add(HttpKnownHeaderKeys.UserAgent, ProductInfoHeaderParser.MultipleValueParser);

            HttpGeneralHeaders.AddParsers(parserStore);

            return parserStore;
        }

        private static HashSet<HeaderKey> CreateInvalidHeaders()
        {
            var invalidHeaders = new HashSet<HeaderKey>();
            HttpContentHeaders.AddKnownHeaders(invalidHeaders);
            return invalidHeaders;

            // Note: Reserved response header names are allowed as custom request header names.  Reserved response
            // headers have no defined meaning or format when used on a request.  This enables a server to accept
            // any headers sent from the client as either content headers or request headers.
        }

        internal static void AddKnownHeaders(HashSet<HeaderKey> headerSet)
        {
            Debug.Assert(headerSet != null);

            headerSet.Add(HttpKnownHeaderKeys.Accept);
            headerSet.Add(HttpKnownHeaderKeys.AcceptCharset);
            headerSet.Add(HttpKnownHeaderKeys.AcceptEncoding);
            headerSet.Add(HttpKnownHeaderKeys.AcceptLanguage);
            headerSet.Add(HttpKnownHeaderKeys.Authorization);
            headerSet.Add(HttpKnownHeaderKeys.Expect);
            headerSet.Add(HttpKnownHeaderKeys.From);
            headerSet.Add(HttpKnownHeaderKeys.Host);
            headerSet.Add(HttpKnownHeaderKeys.IfMatch);
            headerSet.Add(HttpKnownHeaderKeys.IfModifiedSince);
            headerSet.Add(HttpKnownHeaderKeys.IfNoneMatch);
            headerSet.Add(HttpKnownHeaderKeys.IfRange);
            headerSet.Add(HttpKnownHeaderKeys.IfUnmodifiedSince);
            headerSet.Add(HttpKnownHeaderKeys.MaxForwards);
            headerSet.Add(HttpKnownHeaderKeys.ProxyAuthorization);
            headerSet.Add(HttpKnownHeaderKeys.Range);
            headerSet.Add(HttpKnownHeaderKeys.Referer);
            headerSet.Add(HttpKnownHeaderKeys.TE);
            headerSet.Add(HttpKnownHeaderKeys.UserAgent);
        }

        internal override void AddHeaders(HttpHeaders sourceHeaders)
        {
            base.AddHeaders(sourceHeaders);
            HttpRequestHeaders sourceRequestHeaders = sourceHeaders as HttpRequestHeaders;
            Debug.Assert(sourceRequestHeaders != null);

            // Copy special values but do not overwrite.
            _generalHeaders.AddSpecialsFrom(sourceRequestHeaders._generalHeaders);

            bool? expectContinue = ExpectContinue;
            if (!expectContinue.HasValue)
            {
                ExpectContinue = sourceRequestHeaders.ExpectContinue;
            }
        }
    }
}
