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
        private static readonly Dictionary<HeaderKey, HttpHeaderParser> s_parserStore = CreateParserStore();
        private static readonly HashSet<HeaderKey> s_invalidHeaders = CreateInvalidHeaders();

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
                    _allow = new HttpHeaderValueCollection<string>(HttpKnownHeaderKeys.Allow,
                        this, HeaderUtilities.TokenValidator);
                }
                return _allow;
            }
        }

        public ContentDispositionHeaderValue ContentDisposition
        {
            get { return (ContentDispositionHeaderValue)GetParsedValues(HttpKnownHeaderKeys.ContentDisposition); }
            set { SetOrRemoveParsedValue(HttpKnownHeaderKeys.ContentDisposition, value); }
        }

        // Must be a collection (and not provide properties like "GZip", "Deflate", etc.) since the 
        // order matters!
        public ICollection<string> ContentEncoding
        {
            get
            {
                if (_contentEncoding == null)
                {
                    _contentEncoding = new HttpHeaderValueCollection<string>(HttpKnownHeaderKeys.ContentEncoding,
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
                    _contentLanguage = new HttpHeaderValueCollection<string>(HttpKnownHeaderKeys.ContentLanguage,
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
                object storedValue = GetParsedValues(HttpKnownHeaderKeys.ContentLength);

                // Only try to calculate the length if the user didn't set the value explicitly using the setter.
                if (!_contentLengthSet && (storedValue == null))
                {
                    // If we don't have a value for Content-Length in the store, try to let the content calculate
                    // it's length. If the content object is able to calculate the length, we'll store it in the
                    // store.
                    long? calculatedLength = _parent.GetComputedOrBufferLength();

                    if (calculatedLength != null)
                    {
                        SetParsedValue(HttpKnownHeaderKeys.ContentLength, (object)calculatedLength.Value);
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
                SetOrRemoveParsedValue(HttpKnownHeaderKeys.ContentLength, value); // box long value
                _contentLengthSet = true;
            }
        }

        public Uri ContentLocation
        {
            get { return (Uri)GetParsedValues(HttpKnownHeaderKeys.ContentLocation); }
            set { SetOrRemoveParsedValue(HttpKnownHeaderKeys.ContentLocation, value); }
        }

        [SuppressMessage("Microsoft.Performance", "CA1819:PropertiesShouldNotReturnArrays",
            Justification = "In this case the 'value' is the byte array. I.e. the array is treated as a value.")]
        public byte[] ContentMD5
        {
            get { return (byte[])GetParsedValues(HttpKnownHeaderKeys.ContentMD5); }
            set { SetOrRemoveParsedValue(HttpKnownHeaderKeys.ContentMD5, value); }
        }

        public ContentRangeHeaderValue ContentRange
        {
            get { return (ContentRangeHeaderValue)GetParsedValues(HttpKnownHeaderKeys.ContentRange); }
            set { SetOrRemoveParsedValue(HttpKnownHeaderKeys.ContentRange, value); }
        }

        public MediaTypeHeaderValue ContentType
        {
            get { return (MediaTypeHeaderValue)GetParsedValues(HttpKnownHeaderKeys.ContentType); }
            set { SetOrRemoveParsedValue(HttpKnownHeaderKeys.ContentType, value); }
        }

        public DateTimeOffset? Expires
        {
            get { return HeaderUtilities.GetDateTimeOffsetValue(HttpKnownHeaderKeys.Expires, this); }
            set { SetOrRemoveParsedValue(HttpKnownHeaderKeys.Expires, value); }
        }

        public DateTimeOffset? LastModified
        {
            get { return HeaderUtilities.GetDateTimeOffsetValue(HttpKnownHeaderKeys.LastModified, this); }
            set { SetOrRemoveParsedValue(HttpKnownHeaderKeys.LastModified, value); }
        }

        internal HttpContentHeaders(HttpContent parent)
        {
            _parent = parent;

            SetConfiguration(s_parserStore, s_invalidHeaders);
        }

        private static Dictionary<HeaderKey, HttpHeaderParser> CreateParserStore()
        {
            var parserStore = new Dictionary<HeaderKey, HttpHeaderParser>(11);

            parserStore.Add(HttpKnownHeaderKeys.Allow, GenericHeaderParser.TokenListParser);
            parserStore.Add(HttpKnownHeaderKeys.ContentDisposition, GenericHeaderParser.ContentDispositionParser);
            parserStore.Add(HttpKnownHeaderKeys.ContentEncoding, GenericHeaderParser.TokenListParser);
            parserStore.Add(HttpKnownHeaderKeys.ContentLanguage, GenericHeaderParser.TokenListParser);
            parserStore.Add(HttpKnownHeaderKeys.ContentLength, Int64NumberHeaderParser.Parser);
            parserStore.Add(HttpKnownHeaderKeys.ContentLocation, UriHeaderParser.RelativeOrAbsoluteUriParser);
            parserStore.Add(HttpKnownHeaderKeys.ContentMD5, ByteArrayHeaderParser.Parser);
            parserStore.Add(HttpKnownHeaderKeys.ContentRange, GenericHeaderParser.ContentRangeParser);
            parserStore.Add(HttpKnownHeaderKeys.ContentType, MediaTypeHeaderParser.SingleValueParser);
            parserStore.Add(HttpKnownHeaderKeys.Expires, DateHeaderParser.Parser);
            parserStore.Add(HttpKnownHeaderKeys.LastModified, DateHeaderParser.Parser);

            return parserStore;
        }

        private static HashSet<HeaderKey> CreateInvalidHeaders()
        {
            var invalidHeaders = new HashSet<HeaderKey>();

            HttpRequestHeaders.AddKnownHeaders(invalidHeaders);
            HttpResponseHeaders.AddKnownHeaders(invalidHeaders);
            HttpGeneralHeaders.AddKnownHeaders(invalidHeaders);

            return invalidHeaders;
        }

        internal static void AddKnownHeaders(HashSet<HeaderKey> headerSet)
        {
            Debug.Assert(headerSet != null);

            headerSet.Add(HttpKnownHeaderKeys.Allow);
            headerSet.Add(HttpKnownHeaderKeys.ContentDisposition);
            headerSet.Add(HttpKnownHeaderKeys.ContentEncoding);
            headerSet.Add(HttpKnownHeaderKeys.ContentLanguage);
            headerSet.Add(HttpKnownHeaderKeys.ContentLength);
            headerSet.Add(HttpKnownHeaderKeys.ContentLocation);
            headerSet.Add(HttpKnownHeaderKeys.ContentMD5);
            headerSet.Add(HttpKnownHeaderKeys.ContentRange);
            headerSet.Add(HttpKnownHeaderKeys.ContentType);
            headerSet.Add(HttpKnownHeaderKeys.Expires);
            headerSet.Add(HttpKnownHeaderKeys.LastModified);
        }
    }
}
