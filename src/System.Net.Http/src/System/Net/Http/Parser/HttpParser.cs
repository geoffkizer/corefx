// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http.Headers;
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

    // TODO: I've used static methods for now,
    // but ideally I would put all parser state on a class and be able to reuse this.  Consider.

    // TODO: This operates on chars currently, but there's no reason not to operate directly on bytes.

    public static class HttpParser
    {
        // TODO: SlimTask?

        private static async Task ParseResponseHeaderAsync(BufferedStream bufferedStream, IHttpParserHandler handler, CancellationToken cancellationToken)
        {
            HttpElementType currentElement = HttpElementType.None;
            int elementStartOffset;

            void SetCurrentElement(HttpElementType elementType)
            {
                Debug.Assert(elementType != HttpElementType.None);
                Debug.Assert(currentElement == HttpElementType.None);

                currentElement = elementType;

                // Last byte read is considered the start of the element
                elementStartOffset = bufferedStream.ReadOffset - 1;
                Debug.Assert(elementStartOffset >= 0);
            }

            void EmitElement()
            {
                Debug.Assert(currentElement != HttpElementType.None);
                Debug.Assert(elementStartOffset < bufferedStream.ReadOffset);

                // The last read byte is not part of the element to emit
                int elementEndOffset = bufferedStream.ReadOffset - 1;
                Debug.Assert(elementEndOffset >= elementStartOffset);

                handler.OnHttpElement(currentElement, new ArraySegment<byte>(bufferedStream.ReadBuffer, elementStartOffset, elementEndOffset - elementStartOffset), true);

                currentElement = HttpElementType.None;
            }

            char GetNextByte()
            {
                byte b = bufferedStream.ReadBuffer[bufferedStream.ReadOffset++];
                if ((b & 0x80) != 0)
                {
                    // Non-ASCII character received
                    throw new HttpRequestException("Invalid character read from stream");
                }

                return (char)b;
            }

            async SlimTask<char> ReadCharSlowAsync()
            {
                await bufferedStream.FillAsync(cancellationToken);

                if (bufferedStream.ReadLength == 0)
                {
                    // End of stream
                    throw new IOException("unexpected end of stream");
                }

                return GetNextByte();
            }

            SlimTask<char> ReadCharAsync()
            {
                if (bufferedStream.HasBufferedReadBytes)
                {
                    return new SlimTask<char>(GetNextByte());
                }

                return ReadCharSlowAsync();
            }

            char c;

            // Read version
            // Must contain at least one character
            c = await ReadCharAsync();
            if (c == ' ' || c == '\r' || c == '\n')
            {
                throw new HttpRequestException("could not parse response line");
            }

            // This will include the byte we just read
            SetCurrentElement(HttpElementType.Version);

            do
            {
                await ReadCharAsync();
                if (c == '\r' || c == '\n')
                {
                    throw new HttpRequestException("could not parse response line");
                }
            } while (c != ' ');

            EmitElement();

            // Read status code
            // Must contain exactly three chars character
            c = await ReadCharAsync();
            if (c == ' ' || c == '\r' || c == '\n')
            {
                throw new HttpRequestException("could not parse response line");
            }

            // This will include the byte we just read
            SetCurrentElement(HttpElementType.Status);

            c = await ReadCharAsync();
            if (c == ' ' || c == '\r' || c == '\n')
            {
                throw new HttpRequestException("could not parse response line");
            }

            c = await ReadCharAsync();
            if (c == ' ' || c == '\r' || c == '\n')
            {
                throw new HttpRequestException("could not parse response line");
            }

            // Read space separator
            c = await ReadCharAsync();
            if (c != ' ')
            {
                throw new HttpRequestException("could not parse response line");
            }

            EmitElement();

            // Read reason phrase, if present
            c = await ReadCharAsync();
            if (c != '\r')
            {
                SetCurrentElement(HttpElementType.ReasonPhrase);

                do
                {
                    c = await ReadCharAsync();
                } while (c != '\r');

                EmitElement();
            }

            c = await ReadCharAsync();
            if (c != '\n')
            {
                throw new HttpRequestException("could not parse response line");
            }

            // Parse headers
            while (true)
            {
                c = await ReadCharAsync();
                if (c == '\r')
                {
                    if (await ReadCharAsync() != '\n')
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
                    c = await ReadCharAsync();
                } while (c != ':' && c != ' ');

                EmitElement();

                // Eat trailing spaces on header name
                // The RFC doesn't technically allow this, but we support it for compatibility
                if (c == ' ')
                {
                    do
                    {
                        c = await ReadCharAsync();
                    } while (c == ' ');

                    if (c != ':')
                    {
                        throw new HttpRequestException("invalid header name");
                    }
                }

                // Get header value
                c = await ReadCharAsync();
                while (c == ' ')
                {
                    c = await ReadCharAsync();
                }

                SetCurrentElement(HttpElementType.HeaderValue);

                while (c != '\r')
                {
                    c = await ReadCharAsync();
                }

                EmitElement();

                if (await ReadCharAsync() != '\n')
                {
                    throw new HttpRequestException("Saw CR without LF while parsing headers");
                }
            }

            // Leave read offset at the beginning of the response body (if any)
        }

        // Intentionally lower case for comparison
        private static readonly byte[] s_contentLengthUtf8 = Encoding.UTF8.GetBytes("content-length");
        private static readonly byte[] s_transferEncodingUtf8 = Encoding.UTF8.GetBytes("transfer-encoding");
        private static readonly byte[] s_chunkedUtf8 = Encoding.UTF8.GetBytes("chunked");

        // This helper seems generally useful, so think about making it public.
        private static bool CompareUtf8CaseInsenstive(byte[] compareTo, ref int offset, ArraySegment<byte> partialBytes, bool complete)
        {
            // Note these can be equal, and partialBytes.Count can be 0 (when complete == true);
            Debug.Assert(offset <= compareTo.Length);
            Debug.Assert(partialBytes.Count > 0 || complete);

            int end = offset + partialBytes.Count;

            if (complete)
            {
                if (end != compareTo.Length)
                {
                    return false;
                }
            }
            else
            {
                // If not complete, it's ok for end to be less than the full length
                if (end > compareTo.Length)
                {
                    return false;
                }
            }

            for (int i = 0; i < partialBytes.Count; i++)
            {
                if (compareTo[offset + i] != partialBytes.Array[partialBytes.Offset + i].ToLowerUtf8())
                {
                    return false;
                }
            }

            offset += partialBytes.Count;
            return true;
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

            private State _state = State.LookingForHeader;
            private int _compareOffset = 0;

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

                        _compareOffset = 0;
                        if (_contentLength == -1 &&
                            CompareUtf8CaseInsenstive(s_contentLengthUtf8, ref _compareOffset, bytes, complete))
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
                                 CompareUtf8CaseInsenstive(s_transferEncodingUtf8, ref _compareOffset, bytes, complete))
                        {
                            if (complete)
                            {
                                _compareOffset = 0;
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

                        if (CompareUtf8CaseInsenstive(s_contentLengthUtf8, ref _compareOffset, bytes, complete))
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

                        if (CompareUtf8CaseInsenstive(s_transferEncodingUtf8, ref _compareOffset, bytes, complete))
                        {
                            if (complete)
                            {
                                _compareOffset = 0;
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

                        if (CompareUtf8CaseInsenstive(s_chunkedUtf8, ref _compareOffset, bytes, complete))
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

                        // TODO

                        if (complete)
                        {
                            _state = State.LookingForHeader;
                        }

                        break;
                }
            }
        }

        public static async Task<Stream> ParseResponseAndGetBodyAsync(BufferedStream bufferedStream, IHttpParserHandler handler, CancellationToken cancellationToken, bool noContent = false)
        {
            // TODO: SimpleParserHandler needs to accept an inner handler
            // TODO: Reuse SimpleParserHandler instance
            var simpleHandler = new SimpleParserHandler();

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

            // TODO:
            // (2) completion Task
            // (3) Drain logic?
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
    }
}
