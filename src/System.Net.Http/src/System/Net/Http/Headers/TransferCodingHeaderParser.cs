// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;

namespace System.Net.Http.Headers
{
    internal class TransferCodingHeaderParser : BaseHeaderParser
    {
        bool _withQuality;      // Indicates that we should create TransferCodingWithQuality objects

        internal static readonly TransferCodingHeaderParser SingleValueParser =
            new TransferCodingHeaderParser(false, false);
        internal static readonly TransferCodingHeaderParser MultipleValueParser =
            new TransferCodingHeaderParser(true, false);
        internal static readonly TransferCodingHeaderParser SingleValueWithQualityParser =
            new TransferCodingHeaderParser(false, true);
        internal static readonly TransferCodingHeaderParser MultipleValueWithQualityParser =
            new TransferCodingHeaderParser(true, true);

        private TransferCodingHeaderParser(bool supportsMultipleValues, bool withQuality)
            : base(supportsMultipleValues)
        {
            _withQuality = withQuality;
        }

        protected override int GetParsedValueLength(string value, int startIndex, object storeValue,
            out object parsedValue)
        {
            TransferCodingHeaderValue temp = null;
            int resultLength = TransferCodingHeaderValue.GetTransferCodingLength(value, startIndex,
                _withQuality, out temp);

            parsedValue = temp;
            return resultLength;
        }
    }
}
