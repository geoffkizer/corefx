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
        private static readonly Dictionary<HeaderInfo, HttpHeaderParser> s_parserStore = CreateParserStore();
        private static readonly HashSet<HeaderInfo> s_invalidHeaders = CreateInvalidHeaders();

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
                        HeaderInfo.KnownHeaders.Accept, this);
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
                        HeaderInfo.KnownHeaders.AcceptCharset, this);
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
                        HeaderInfo.KnownHeaders.AcceptEncoding, this);
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
                        HeaderInfo.KnownHeaders.AcceptLanguage, this);
                }
                return _acceptLanguage;
            }
        }

        public AuthenticationHeaderValue Authorization
        {
            get { return (AuthenticationHeaderValue)GetParsedValues(HeaderInfo.KnownHeaders.Authorization); }
            set { SetOrRemoveParsedValue(HeaderInfo.KnownHeaders.Authorization, value); }
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
            get { return (string)GetParsedValues(HeaderInfo.KnownHeaders.From); }
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
                SetOrRemoveParsedValue(HeaderInfo.KnownHeaders.From, value);
            }
        }

        public string Host
        {
            get { return (string)GetParsedValues(HeaderInfo.KnownHeaders.Host); }
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
                SetOrRemoveParsedValue(HeaderInfo.KnownHeaders.Host, value);
            }
        }

        public HttpHeaderValueCollection<EntityTagHeaderValue> IfMatch
        {
            get
            {
                if (_ifMatch == null)
                {
                    _ifMatch = new HttpHeaderValueCollection<EntityTagHeaderValue>(
                        HeaderInfo.KnownHeaders.IfMatch, this);
                }
                return _ifMatch;
            }
        }

        public DateTimeOffset? IfModifiedSince
        {
            get { return HeaderUtilities.GetDateTimeOffsetValue(HeaderInfo.KnownHeaders.IfModifiedSince, this); }
            set { SetOrRemoveParsedValue(HeaderInfo.KnownHeaders.IfModifiedSince, value); }
        }

        public HttpHeaderValueCollection<EntityTagHeaderValue> IfNoneMatch
        {
            get
            {
                if (_ifNoneMatch == null)
                {
                    _ifNoneMatch = new HttpHeaderValueCollection<EntityTagHeaderValue>(
                        HeaderInfo.KnownHeaders.IfNoneMatch, this);
                }
                return _ifNoneMatch;
            }
        }

        public RangeConditionHeaderValue IfRange
        {
            get { return (RangeConditionHeaderValue)GetParsedValues(HeaderInfo.KnownHeaders.IfRange); }
            set { SetOrRemoveParsedValue(HeaderInfo.KnownHeaders.IfRange, value); }
        }

        public DateTimeOffset? IfUnmodifiedSince
        {
            get { return HeaderUtilities.GetDateTimeOffsetValue(HeaderInfo.KnownHeaders.IfUnmodifiedSince, this); }
            set { SetOrRemoveParsedValue(HeaderInfo.KnownHeaders.IfUnmodifiedSince, value); }
        }

        public int? MaxForwards
        {
            get
            {
                object storedValue = GetParsedValues(HeaderInfo.KnownHeaders.MaxForwards);
                if (storedValue != null)
                {
                    return (int)storedValue;
                }
                return null;
            }
            set { SetOrRemoveParsedValue(HeaderInfo.KnownHeaders.MaxForwards, value); }
        }


        public AuthenticationHeaderValue ProxyAuthorization
        {
            get { return (AuthenticationHeaderValue)GetParsedValues(HeaderInfo.KnownHeaders.ProxyAuthorization); }
            set { SetOrRemoveParsedValue(HeaderInfo.KnownHeaders.ProxyAuthorization, value); }
        }

        public RangeHeaderValue Range
        {
            get { return (RangeHeaderValue)GetParsedValues(HeaderInfo.KnownHeaders.Range); }
            set { SetOrRemoveParsedValue(HeaderInfo.KnownHeaders.Range, value); }
        }

        public Uri Referrer
        {
            get { return (Uri)GetParsedValues(HeaderInfo.KnownHeaders.Referer); }
            set { SetOrRemoveParsedValue(HeaderInfo.KnownHeaders.Referer, value); }
        }

        public HttpHeaderValueCollection<TransferCodingWithQualityHeaderValue> TE
        {
            get
            {
                if (_te == null)
                {
                    _te = new HttpHeaderValueCollection<TransferCodingWithQualityHeaderValue>(
                        HeaderInfo.KnownHeaders.TE, this);
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
                    _userAgent = new HttpHeaderValueCollection<ProductInfoHeaderValue>(HeaderInfo.KnownHeaders.UserAgent,
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
                        HeaderInfo.KnownHeaders.Expect, this, HeaderUtilities.ExpectContinue);
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
            : base(HeaderInfo.HttpHeaderType.General | HeaderInfo.HttpHeaderType.Request | HeaderInfo.HttpHeaderType.Custom)
        {
            _generalHeaders = new HttpGeneralHeaders(this);

            base.SetConfiguration(s_parserStore, s_invalidHeaders);
        }

        private static Dictionary<HeaderInfo, HttpHeaderParser> CreateParserStore()
        {
            var parserStore = new Dictionary<HeaderInfo, HttpHeaderParser>();

            parserStore.Add(HeaderInfo.KnownHeaders.Accept, MediaTypeHeaderParser.MultipleValuesParser);
            parserStore.Add(HeaderInfo.KnownHeaders.AcceptCharset, GenericHeaderParser.MultipleValueStringWithQualityParser);
            parserStore.Add(HeaderInfo.KnownHeaders.AcceptEncoding, GenericHeaderParser.MultipleValueStringWithQualityParser);
            parserStore.Add(HeaderInfo.KnownHeaders.AcceptLanguage, GenericHeaderParser.MultipleValueStringWithQualityParser);
            parserStore.Add(HeaderInfo.KnownHeaders.Authorization, GenericHeaderParser.SingleValueAuthenticationParser);
            parserStore.Add(HeaderInfo.KnownHeaders.Expect, GenericHeaderParser.MultipleValueNameValueWithParametersParser);
            parserStore.Add(HeaderInfo.KnownHeaders.From, GenericHeaderParser.MailAddressParser);
            parserStore.Add(HeaderInfo.KnownHeaders.Host, GenericHeaderParser.HostParser);
            parserStore.Add(HeaderInfo.KnownHeaders.IfMatch, GenericHeaderParser.MultipleValueEntityTagParser);
            parserStore.Add(HeaderInfo.KnownHeaders.IfModifiedSince, DateHeaderParser.Parser);
            parserStore.Add(HeaderInfo.KnownHeaders.IfNoneMatch, GenericHeaderParser.MultipleValueEntityTagParser);
            parserStore.Add(HeaderInfo.KnownHeaders.IfRange, GenericHeaderParser.RangeConditionParser);
            parserStore.Add(HeaderInfo.KnownHeaders.IfUnmodifiedSince, DateHeaderParser.Parser);
            parserStore.Add(HeaderInfo.KnownHeaders.MaxForwards, Int32NumberHeaderParser.Parser);
            parserStore.Add(HeaderInfo.KnownHeaders.ProxyAuthorization, GenericHeaderParser.SingleValueAuthenticationParser);
            parserStore.Add(HeaderInfo.KnownHeaders.Range, GenericHeaderParser.RangeParser);
            parserStore.Add(HeaderInfo.KnownHeaders.Referer, UriHeaderParser.RelativeOrAbsoluteUriParser);
            parserStore.Add(HeaderInfo.KnownHeaders.TE, TransferCodingHeaderParser.MultipleValueWithQualityParser);
            parserStore.Add(HeaderInfo.KnownHeaders.UserAgent, ProductInfoHeaderParser.MultipleValueParser);

            HttpGeneralHeaders.AddParsers(parserStore);

            return parserStore;
        }

        private static HashSet<HeaderInfo> CreateInvalidHeaders()
        {
            var invalidHeaders = new HashSet<HeaderInfo>();
            HttpContentHeaders.AddKnownHeaders(invalidHeaders);
            return invalidHeaders;

            // Note: Reserved response header names are allowed as custom request header names.  Reserved response
            // headers have no defined meaning or format when used on a request.  This enables a server to accept
            // any headers sent from the client as either content headers or request headers.
        }

        internal static void AddKnownHeaders(HashSet<HeaderInfo> headerSet)
        {
            Debug.Assert(headerSet != null);

            headerSet.Add(HeaderInfo.KnownHeaders.Accept);
            headerSet.Add(HeaderInfo.KnownHeaders.AcceptCharset);
            headerSet.Add(HeaderInfo.KnownHeaders.AcceptEncoding);
            headerSet.Add(HeaderInfo.KnownHeaders.AcceptLanguage);
            headerSet.Add(HeaderInfo.KnownHeaders.Authorization);
            headerSet.Add(HeaderInfo.KnownHeaders.Expect);
            headerSet.Add(HeaderInfo.KnownHeaders.From);
            headerSet.Add(HeaderInfo.KnownHeaders.Host);
            headerSet.Add(HeaderInfo.KnownHeaders.IfMatch);
            headerSet.Add(HeaderInfo.KnownHeaders.IfModifiedSince);
            headerSet.Add(HeaderInfo.KnownHeaders.IfNoneMatch);
            headerSet.Add(HeaderInfo.KnownHeaders.IfRange);
            headerSet.Add(HeaderInfo.KnownHeaders.IfUnmodifiedSince);
            headerSet.Add(HeaderInfo.KnownHeaders.MaxForwards);
            headerSet.Add(HeaderInfo.KnownHeaders.ProxyAuthorization);
            headerSet.Add(HeaderInfo.KnownHeaders.Range);
            headerSet.Add(HeaderInfo.KnownHeaders.Referer);
            headerSet.Add(HeaderInfo.KnownHeaders.TE);
            headerSet.Add(HeaderInfo.KnownHeaders.UserAgent);
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
