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
    public sealed class HttpContentHeaders : HttpHeaders
    {
        private static readonly Dictionary<HeaderInfo, HttpHeaderParser> s_parserStore = CreateParserStore();
        private static readonly HashSet<HeaderInfo> s_invalidHeaders = CreateInvalidHeaders();

        private readonly HttpContent _parent;
        private bool _contentLengthSet;

        private HttpHeaderValueCollection<string> _allow;
        private HttpHeaderValueCollection<string> _contentEncoding;
        private HttpHeaderValueCollection<string> _contentLanguage;

        public ICollection<string> Allow
        {
            get
            {
                if (_allow == null)
                {
                    _allow = new HttpHeaderValueCollection<string>(HeaderInfo.KnownHeaders.Allow,
                        this, HeaderUtilities.TokenValidator);
                }
                return _allow;
            }
        }

        public ContentDispositionHeaderValue ContentDisposition
        {
            get { return (ContentDispositionHeaderValue)GetParsedValues(HeaderInfo.KnownHeaders.ContentDisposition); }
            set { SetOrRemoveParsedValue(HeaderInfo.KnownHeaders.ContentDisposition, value); }
        }

        // Must be a collection (and not provide properties like "GZip", "Deflate", etc.) since the 
        // order matters!
        public ICollection<string> ContentEncoding
        {
            get
            {
                if (_contentEncoding == null)
                {
                    _contentEncoding = new HttpHeaderValueCollection<string>(HeaderInfo.KnownHeaders.ContentEncoding,
                        this, HeaderUtilities.TokenValidator);
                }
                return _contentEncoding;
            }
        }

        public ICollection<string> ContentLanguage
        {
            get
            {
                if (_contentLanguage == null)
                {
                    _contentLanguage = new HttpHeaderValueCollection<string>(HeaderInfo.KnownHeaders.ContentLanguage,
                        this, HeaderUtilities.TokenValidator);
                }
                return _contentLanguage;
            }
        }

        public long? ContentLength
        {
            get
            {
                // 'Content-Length' can only hold one value. So either we get 'null' back or a boxed long value.
                object storedValue = GetParsedValues(HeaderInfo.KnownHeaders.ContentLength);

                // Only try to calculate the length if the user didn't set the value explicitly using the setter.
                if (!_contentLengthSet && (storedValue == null))
                {
                    // If we don't have a value for Content-Length in the store, try to let the content calculate
                    // it's length. If the content object is able to calculate the length, we'll store it in the
                    // store.
                    long? calculatedLength = _parent.GetComputedOrBufferLength();

                    if (calculatedLength != null)
                    {
                        SetParsedValue(HeaderInfo.KnownHeaders.ContentLength, (object)calculatedLength.Value);
                    }

                    return calculatedLength;
                }

                if (storedValue == null)
                {
                    return null;
                }
                else
                {
                    return (long)storedValue;
                }
            }
            set
            {
                SetOrRemoveParsedValue(HeaderInfo.KnownHeaders.ContentLength, value); // box long value
                _contentLengthSet = true;
            }
        }

        public Uri ContentLocation
        {
            get { return (Uri)GetParsedValues(HeaderInfo.KnownHeaders.ContentLocation); }
            set { SetOrRemoveParsedValue(HeaderInfo.KnownHeaders.ContentLocation, value); }
        }

        [SuppressMessage("Microsoft.Performance", "CA1819:PropertiesShouldNotReturnArrays",
            Justification = "In this case the 'value' is the byte array. I.e. the array is treated as a value.")]
        public byte[] ContentMD5
        {
            get { return (byte[])GetParsedValues(HeaderInfo.KnownHeaders.ContentMD5); }
            set { SetOrRemoveParsedValue(HeaderInfo.KnownHeaders.ContentMD5, value); }
        }

        public ContentRangeHeaderValue ContentRange
        {
            get { return (ContentRangeHeaderValue)GetParsedValues(HeaderInfo.KnownHeaders.ContentRange); }
            set { SetOrRemoveParsedValue(HeaderInfo.KnownHeaders.ContentRange, value); }
        }

        public MediaTypeHeaderValue ContentType
        {
            get { return (MediaTypeHeaderValue)GetParsedValues(HeaderInfo.KnownHeaders.ContentType); }
            set { SetOrRemoveParsedValue(HeaderInfo.KnownHeaders.ContentType, value); }
        }

        public DateTimeOffset? Expires
        {
            get { return HeaderUtilities.GetDateTimeOffsetValue(HeaderInfo.KnownHeaders.Expires, this); }
            set { SetOrRemoveParsedValue(HeaderInfo.KnownHeaders.Expires, value); }
        }

        public DateTimeOffset? LastModified
        {
            get { return HeaderUtilities.GetDateTimeOffsetValue(HeaderInfo.KnownHeaders.LastModified, this); }
            set { SetOrRemoveParsedValue(HeaderInfo.KnownHeaders.LastModified, value); }
        }

        internal HttpContentHeaders(HttpContent parent)
            : base(HeaderInfo.HttpHeaderType.Content | HeaderInfo.HttpHeaderType.Custom)
        {
            _parent = parent;

            SetConfiguration(s_parserStore, s_invalidHeaders);
        }

        private static Dictionary<HeaderInfo, HttpHeaderParser> CreateParserStore()
        {
            var parserStore = new Dictionary<HeaderInfo, HttpHeaderParser>(11);

            parserStore.Add(HeaderInfo.KnownHeaders.Allow, GenericHeaderParser.TokenListParser);
            parserStore.Add(HeaderInfo.KnownHeaders.ContentDisposition, GenericHeaderParser.ContentDispositionParser);
            parserStore.Add(HeaderInfo.KnownHeaders.ContentEncoding, GenericHeaderParser.TokenListParser);
            parserStore.Add(HeaderInfo.KnownHeaders.ContentLanguage, GenericHeaderParser.TokenListParser);
            parserStore.Add(HeaderInfo.KnownHeaders.ContentLength, Int64NumberHeaderParser.Parser);
            parserStore.Add(HeaderInfo.KnownHeaders.ContentLocation, UriHeaderParser.RelativeOrAbsoluteUriParser);
            parserStore.Add(HeaderInfo.KnownHeaders.ContentMD5, ByteArrayHeaderParser.Parser);
            parserStore.Add(HeaderInfo.KnownHeaders.ContentRange, GenericHeaderParser.ContentRangeParser);
            parserStore.Add(HeaderInfo.KnownHeaders.ContentType, MediaTypeHeaderParser.SingleValueParser);
            parserStore.Add(HeaderInfo.KnownHeaders.Expires, DateHeaderParser.Parser);
            parserStore.Add(HeaderInfo.KnownHeaders.LastModified, DateHeaderParser.Parser);

            return parserStore;
        }

        private static HashSet<HeaderInfo> CreateInvalidHeaders()
        {
            var invalidHeaders = new HashSet<HeaderInfo>();

            HttpRequestHeaders.AddKnownHeaders(invalidHeaders);
            HttpResponseHeaders.AddKnownHeaders(invalidHeaders);
            HttpGeneralHeaders.AddKnownHeaders(invalidHeaders);

            return invalidHeaders;
        }

        internal static void AddKnownHeaders(HashSet<HeaderInfo> headerSet)
        {
            Debug.Assert(headerSet != null);

            headerSet.Add(HeaderInfo.KnownHeaders.Allow);
            headerSet.Add(HeaderInfo.KnownHeaders.ContentDisposition);
            headerSet.Add(HeaderInfo.KnownHeaders.ContentEncoding);
            headerSet.Add(HeaderInfo.KnownHeaders.ContentLanguage);
            headerSet.Add(HeaderInfo.KnownHeaders.ContentLength);
            headerSet.Add(HeaderInfo.KnownHeaders.ContentLocation);
            headerSet.Add(HeaderInfo.KnownHeaders.ContentMD5);
            headerSet.Add(HeaderInfo.KnownHeaders.ContentRange);
            headerSet.Add(HeaderInfo.KnownHeaders.ContentType);
            headerSet.Add(HeaderInfo.KnownHeaders.Expires);
            headerSet.Add(HeaderInfo.KnownHeaders.LastModified);
        }
    }
}
