﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Explicitly import a few things from existing Managed dir
// Eventually this should go away -- move these to Parser or some common location
//using HttpContentReadStream = System.Net.Http.Managed.HttpContentReadStream;


// TODO:
// Figure out content read end stuff (used to be PutConnectionInPool) -- use Task CompletedTask
// Get rid of ReadChar*; deal with bytes only

namespace System.Net.Http.Parser
{
    // TODO: Move?
    public enum HttpElementType
    {
        None = 0,   // Needed?

        // Response line
        Status,
        ReasonPhrase,

        // Request line
        Method,
        Path,

        // Request and response line
        Version,

        // Headers
        HeaderName,
        HeaderValue
    }

    public interface IHttpParserHandler
    {
        void OnHttpElement(HttpElementType elementType, ArraySegment<byte> bytes, bool complete);
    }

    // CONSIDER: Make HttpParser generic on T, where T is IHttpParserHandler
    // May be a perf win
    // Also enables avoiding boxing for structs, if we care about this

    // TODO: This operates on chars currently, but there's no reason not to operate directly on bytes.

    public sealed class HttpParser
    {
        private BufferedStream _bufferedStream;
        private IHttpParserHandler _handler;
        private HttpElementType _currentElement;
        private int _elementStartOffset;

        private void SetCurrentElement(HttpElementType elementType)
        {
            Debug.Assert(elementType != HttpElementType.None);
            Debug.Assert(_currentElement == HttpElementType.None);

            _currentElement = elementType;

            // Last byte read is considered the start of the element
            _elementStartOffset = _bufferedStream.ReadOffset - 1;
            Debug.Assert(_elementStartOffset >= 0);
        }

        private void EmitElement()
        {
            Debug.Assert(_currentElement != HttpElementType.None);
            Debug.Assert(_elementStartOffset < _bufferedStream.ReadOffset);

            // The last read byte is not part of the element to emit
            int elementEndOffset = _bufferedStream.ReadOffset - 1;
            Debug.Assert(elementEndOffset >= _elementStartOffset);

            _handler.OnHttpElement(_currentElement, new ArraySegment<byte>(_bufferedStream.ReadBuffer, _elementStartOffset, elementEndOffset - _elementStartOffset), true);

            _currentElement = HttpElementType.None;
        }

        private char GetNextByte()
        {
            byte b = _bufferedStream.ReadBuffer[_bufferedStream.ReadOffset++];
            if ((b & 0x80) != 0)
            {
                // Non-ASCII character received
                throw new HttpRequestException("Invalid character read from stream");
            }

            return (char)b;
        }

        private async SlimTask<char> ReadCharSlowAsync(CancellationToken cancellationToken)
        {
            if (_currentElement != HttpElementType.None)
            {
                _handler.OnHttpElement(_currentElement, new ArraySegment<byte>(_bufferedStream.ReadBuffer, _elementStartOffset, _bufferedStream.ReadLength - _elementStartOffset), false);
            }

            await _bufferedStream.FillAsync(cancellationToken);

            if (_bufferedStream.ReadLength == 0)
            {
                // End of stream
                throw new IOException("unexpected end of stream");
            }

            return GetNextByte();
        }

        private SlimTask<char> ReadCharAsync(CancellationToken cancellationToken)
        {
            if (_bufferedStream.HasBufferedReadBytes)
            {
                return new SlimTask<char>(GetNextByte());
            }

            return ReadCharSlowAsync(cancellationToken);
        }


        private async SlimTask ParseResponseHeaderAsync(BufferedStream bufferedStream, IHttpParserHandler handler, CancellationToken cancellationToken)
        {
            _bufferedStream = bufferedStream;
            _handler = handler;
            _currentElement = HttpElementType.None;
            _elementStartOffset = 0;

            char c;

            // Read version
            // Must contain at least one character
            c = await ReadCharAsync(cancellationToken);
            if (c == ' ' || c == '\r' || c == '\n')
            {
                throw new HttpRequestException("could not parse response line");
            }

            // This will include the byte we just read
            SetCurrentElement(HttpElementType.Version);

            do
            {
                c = await ReadCharAsync(cancellationToken);
                if (c == '\r' || c == '\n')
                {
                    throw new HttpRequestException("could not parse response line");
                }
            } while (c != ' ');

            EmitElement();

            // Read status code
            // Must contain exactly three chars character
            c = await ReadCharAsync(cancellationToken);
            if (c == ' ' || c == '\r' || c == '\n')
            {
                throw new HttpRequestException("could not parse response line");
            }

            // This will include the byte we just read
            SetCurrentElement(HttpElementType.Status);

            c = await ReadCharAsync(cancellationToken);
            if (c == ' ' || c == '\r' || c == '\n')
            {
                throw new HttpRequestException("could not parse response line");
            }

            c = await ReadCharAsync(cancellationToken);
            if (c == ' ' || c == '\r' || c == '\n')
            {
                throw new HttpRequestException("could not parse response line");
            }

            // Read space separator
            c = await ReadCharAsync(cancellationToken);
            if (c != ' ')
            {
                throw new HttpRequestException("could not parse response line");
            }

            EmitElement();

            // Read reason phrase, if present
            c = await ReadCharAsync(cancellationToken);
            if (c != '\r')
            {
                SetCurrentElement(HttpElementType.ReasonPhrase);

                do
                {
                    c = await ReadCharAsync(cancellationToken);
                } while (c != '\r');

                EmitElement();
            }

            c = await ReadCharAsync(cancellationToken);
            if (c != '\n')
            {
                throw new HttpRequestException("could not parse response line");
            }

            // Parse headers
            while (true)
            {
                c = await ReadCharAsync(cancellationToken);
                if (c == '\r')
                {
                    if (await ReadCharAsync(cancellationToken) != '\n')
                    {
                        throw new HttpRequestException("Saw CR without LF while parsing headers");
                    }

                    break;
                }

                if (c == ' ')
                {
                    throw new HttpRequestException("invalid header name");
                }

                SetCurrentElement(HttpElementType.HeaderName);

                // Get header name
                do
                {
                    c = await ReadCharAsync(cancellationToken);
                } while (c != ':' && c != ' ');

                EmitElement();

                // Eat trailing spaces on header name
                // The RFC doesn't technically allow this, but we support it for compatibility
                if (c == ' ')
                {
                    do
                    {
                        c = await ReadCharAsync(cancellationToken);
                    } while (c == ' ');

                    if (c != ':')
                    {
                        throw new HttpRequestException("invalid header name");
                    }
                }

                // Get header value
                c = await ReadCharAsync(cancellationToken);
                while (c == ' ')
                {
                    c = await ReadCharAsync(cancellationToken);
                }

                SetCurrentElement(HttpElementType.HeaderValue);

                while (c != '\r')
                {
                    c = await ReadCharAsync(cancellationToken);
                }

                EmitElement();

                if (await ReadCharAsync(cancellationToken) != '\n')
                {
                    throw new HttpRequestException("Saw CR without LF while parsing headers");
                }
            }

            // Leave read offset at the beginning of the response body (if any)
        }

        // Intentionally lower case for comparison
        private static readonly byte[] s_contentLengthUtf8 = Encoding.UTF8.GetBytes("Content-Length");
        private static readonly byte[] s_transferEncodingUtf8 = Encoding.UTF8.GetBytes("Transfer-Encoding");
        private static readonly byte[] s_chunkedUtf8 = Encoding.UTF8.GetBytes("chunked");

        public struct Utf8StringMatcher
        {
            byte[] _matchString;
            IEqualityComparer<byte> _comparer;
            int _offset;

            public Utf8StringMatcher(byte[] matchString, IEqualityComparer<byte> comparer)
            {
                _matchString = matchString;
                _comparer = comparer;
                _offset = 0;
            }

            public bool Match(ArraySegment<byte> partialString, bool complete)
            {
                // Note these can be equal, and partialBytes.Count can be 0 (when complete == true);
                Debug.Assert(_offset <= _matchString.Length);
                Debug.Assert(partialString.Count > 0 || complete);

                int end = _offset + partialString.Count;

                if (end > _matchString.Length ||
                    (complete && end < _matchString.Length))
                {
                    return false;
                }

                for (int i = 0; i < partialString.Count; i++)
                {
                    if (!_comparer.Equals(_matchString[_offset + i], partialString.Array[partialString.Offset + i]))
                    {
                        return false;
                    }
                }

                _offset += partialString.Count;
                return true;
            }

            public void Reset()
            {
                _offset = 0;
            }
        }

        // This state machine logic is super painful.
        // Consider how to make this simpler.
        // A couple options:
        // (1) Better building blocks for this, e.g. HeaderMatcher or StringMatcher etc
        // (2) Convert to pull and use await.

        // TODO: This should check for known status codes that implicitly have no body -- 204, 304

        // Used by ParseResponseAndGetBody below
        private sealed class SimpleParserHandler : IHttpParserHandler
        {
            IHttpParserHandler _innerHandler;

            public SimpleParserHandler(IHttpParserHandler innerHandler)
            {
                _innerHandler = innerHandler;

                _contentLengthMatcher = new Utf8StringMatcher(s_contentLengthUtf8, Utf8Helpers.CaseInsensitiveComparer);
                _transferEncodingMatcher = new Utf8StringMatcher(s_transferEncodingUtf8, Utf8Helpers.CaseInsensitiveComparer);
                _chunkedMatcher = new Utf8StringMatcher(s_chunkedUtf8, Utf8Helpers.CaseInsensitiveComparer);

                _state = State.LookingForHeader;
            }

            // This is why await is so useful.
            private enum State
            {
                LookingForHeader,
                MatchingContentLength,
                MatchingTransferEncoding,
                IgnoringHeader,
                ParsingContentLength,
                ParsingTransferEncoding
            }

            private State _state;
            private Utf8StringMatcher _contentLengthMatcher;
            private Utf8StringMatcher _transferEncodingMatcher;
            private Utf8StringMatcher _chunkedMatcher;

            private bool _chunkedEncoding = false;
            private long _contentLength = -1;

            public bool ChunkedEncoding => _chunkedEncoding;
            public long ContentLength => (_chunkedEncoding ? -1 : _contentLength);

            public bool NoBody
            {
                get
                {
                    // TODO: Check for known status codes that implicitly have no body -- 204, 304

                    return ContentLength == 0;
                }
            }

            public void OnHttpElement(HttpElementType elementType, ArraySegment<byte> bytes, bool complete)
            {
                switch (_state)
                {
                    case State.LookingForHeader:
                        if (elementType != HttpElementType.HeaderName)
                        {
                            break;
                        }

                        _contentLengthMatcher.Reset();
                        _transferEncodingMatcher.Reset();
                        if (_contentLength == -1 &&
                            _contentLengthMatcher.Match(bytes, complete))
                        {
                            if (complete)
                            {
                                _contentLength = 0;
                                _state = State.ParsingContentLength;
                            }
                            else
                            {
                                _state = State.MatchingContentLength;
                            }
                        }
                        else if (_chunkedEncoding == false &&
                                 _transferEncodingMatcher.Match(bytes, complete))
                        {
                            if (complete)
                            {
                                _chunkedMatcher.Reset();
                                _state = State.ParsingTransferEncoding;
                            }
                            else
                            {
                                _state = State.MatchingContentLength;
                            }
                        }
                        else
                        {
                            // Didn't partially or fully match either header
                            _state = complete ? State.LookingForHeader : State.IgnoringHeader;
                        }

                        break;

                    case State.IgnoringHeader:
                        // Look for end of header name
                        if (elementType == HttpElementType.HeaderName && complete)
                        {
                            _state = State.LookingForHeader;
                        }
                        break;

                    case State.MatchingContentLength:
                        if (elementType != HttpElementType.HeaderName)
                        {
                            // We expect a continuation of the HeaderName
                            throw new InvalidOperationException();
                        }

                        if (_contentLengthMatcher.Match(bytes, complete))
                        {
                            if (complete)
                            {
                                _contentLength = 0;
                                _state = State.ParsingContentLength;
                            }
                        }
                        else
                        {
                            _state = complete ? State.LookingForHeader : State.IgnoringHeader;
                        }

                        break;

                    case State.MatchingTransferEncoding:
                        if (elementType != HttpElementType.HeaderName)
                        {
                            // We expect a continuation of the HeaderName
                            throw new InvalidOperationException();
                        }

                        if (_transferEncodingMatcher.Match(bytes, complete))
                        {
                            if (complete)
                            {
                                _chunkedMatcher.Reset();
                                _state = State.ParsingTransferEncoding;
                            }
                        }
                        else
                        {
                            _state = complete ? State.LookingForHeader : State.IgnoringHeader;
                        }

                        break;

                    case State.ParsingTransferEncoding:
                        if (elementType != HttpElementType.HeaderValue)
                        {
                            // We expect a header value
                            throw new InvalidOperationException();
                        }

                        // TODO: If we see another transfer-encoding after chunked, this is an invalid request

                        if (_chunkedMatcher.Match(bytes, complete))
                        {
                            if (complete)
                            {
                                _chunkedEncoding = true;
                                _state = State.LookingForHeader;
                            }
                        }
                        else
                        {
                            _state = State.LookingForHeader;
                        }

                        break;

                    case State.ParsingContentLength:
                        if (elementType != HttpElementType.HeaderValue)
                        {
                            // We expect a header value
                            throw new InvalidOperationException();
                        }

                        if (complete)
                        {
                            _state = State.LookingForHeader;
                        }

                        // TODO
                        throw new NotImplementedException();
                }

                if (_innerHandler != null)
                {
                    _innerHandler.OnHttpElement(elementType, bytes, complete);
                }
            }
        }

        public async SlimTask<HttpContentReadStream> ParseResponseAndGetBodyAsync(BufferedStream bufferedStream, IHttpParserHandler handler, CancellationToken cancellationToken, bool noContent = false)
        {
            // TODO: SimpleParserHandler needs to accept an inner handler
            // TODO: Reuse SimpleParserHandler instance
            var simpleHandler = new SimpleParserHandler(handler);

            await ParseResponseHeaderAsync(bufferedStream, simpleHandler, cancellationToken);

            if (noContent || simpleHandler.NoBody)
            {
                return EmptyReadStream.Instance;
            }
            else if (simpleHandler.ChunkedEncoding)
            {
                return new ChunkedEncodingReadStream(bufferedStream);
            }
            else if (simpleHandler.ContentLength != -1)
            {
                return new ContentLengthReadStream(bufferedStream, simpleHandler.ContentLength);
            }
            else
            {
                return new ConnectionCloseReadStream(bufferedStream);
            }
        }
    }

    internal static class Utf8Helpers
    {
        internal const byte UTF8_CR = (byte)'\r';
        internal const byte UTF8_LF = (byte)'\n';
        internal const byte UTF8_A = (byte)'A';
        internal const byte UTF8_a = (byte)'a';
        internal const byte UTF8_F = (byte)'F';
        internal const byte UTF8_f = (byte)'f';
        internal const byte UTF8_Z = (byte)'Z';
        internal const byte UTF8_z = (byte)'z';
        internal const byte UTF8_0 = (byte)'0';
        internal const byte UTF8_9 = (byte)'9';

        internal static byte ToLowerUtf8(this byte b)
        {
            return (b >= UTF8_A && b <= UTF8_Z) ? (byte)(b - UTF8_A + UTF8_a) : b;
        }

        internal static (bool ok, int value) TryGetHexDigitValue(this byte b)
        {
            if (b >= UTF8_0 && b <= UTF8_9)
            {
                return (true, b - UTF8_0);
            }
            else if (b >= UTF8_a && b <= UTF8_f)
            {
                return (true, b - UTF8_a + 10);
            }
            else if (b >= UTF8_A && b <= UTF8_F)
            {
                return (true, b - UTF8_A + 10);
            }
            else
            {
                return (false, 0);
            }
        }

        private sealed class _CaseSensitiveComparer : IEqualityComparer<byte>
        {
            public bool Equals(byte x, byte y)
            {
                return (x == y);
            }

            public int GetHashCode(byte b)
            {
                return b;
            }
        }

        private sealed class _CaseInsensitiveComparer : IEqualityComparer<byte>
        {
            public bool Equals(byte x, byte y)
            {
                return (x == y) || x.ToLowerUtf8() == y.ToLowerUtf8();
            }

            public int GetHashCode(byte b)
            {
                return b.ToLowerUtf8();
            }
        }

        public static readonly IEqualityComparer<byte> CaseSensitiveComparer = new _CaseSensitiveComparer();
        public static readonly IEqualityComparer<byte> CaseInsensitiveComparer = new _CaseInsensitiveComparer();
    }
}
