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
// Figure out content read end stuff (used to be PutConnectionInPool)
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

    internal sealed class HttpParser : IDisposable
    {
        private const int BufferSize = 4096;

        private readonly Stream _stream;
        private readonly TransportContext _transportContext;
        private readonly bool _usingProxy;

        private IHttpParserHandler _handler;

//        private readonly StringBuilder _sb;

        private readonly byte[] _readBuffer;
        private int _readOffset;
        private int _readLength;

        private HttpElementType _currentElement;
        private int _elementStartOffset;

        private bool _disposed;

        private sealed class ContentLengthReadStream : HttpContentReadStream
        {
            private long _contentBytesRemaining;

            public ContentLengthReadStream(HttpParser parser, long contentLength)
                : base(parser)
            {
                if (contentLength == 0)
                {
                    _parser = null;
                    _contentBytesRemaining = 0;
//                    parser.PutparserInPool();
                }
                else
                {
                    _contentBytesRemaining = contentLength;
                }
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (buffer == null)
                {
                    throw new ArgumentNullException(nameof(buffer));
                }

                if (offset < 0 || offset > buffer.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(offset));
                }

                if (count < 0 || count > buffer.Length - offset)
                {
                    throw new ArgumentOutOfRangeException(nameof(count));
                }

                if (_parser == null)
                {
                    // Response body fully consumed
                    return 0;
                }

                Debug.Assert(_contentBytesRemaining > 0);

                count = (int)Math.Min(count, _contentBytesRemaining);

                int bytesRead = await _parser.ReadAsync(buffer, offset, count, cancellationToken);

                if (bytesRead == 0)
                {
                    // Unexpected end of response stream
                    throw new IOException("Unexpected end of content stream");
                }

                Debug.Assert(bytesRead <= _contentBytesRemaining);
                _contentBytesRemaining -= bytesRead;

                if (_contentBytesRemaining == 0)
                {
                    // End of response body
//                    _parser.PutparserInPool();
                    _parser = null;
                }

                return bytesRead;
            }

            public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            {
                if (destination == null)
                {
                    throw new ArgumentNullException(nameof(destination));
                }

                if (_parser == null)
                {
                    // Response body fully consumed
                    return;
                }

                await _parser.CopyChunkToAsync(destination, _contentBytesRemaining, cancellationToken);

                _contentBytesRemaining = 0;
//                _parser.PutparserInPool();
                _parser = null;
            }
        }

        private sealed class ChunkedEncodingReadStream : HttpContentReadStream
        {
            private int _chunkBytesRemaining;

            public ChunkedEncodingReadStream(HttpParser parser)
                : base(parser)
            {
                _chunkBytesRemaining = 0;
            }

            private async Task<bool> TryGetNextChunk(CancellationToken cancellationToken)
            {
                Debug.Assert(_chunkBytesRemaining == 0);

                // Start of chunk, read chunk size
                int chunkSize = 0;
                char c = await _parser.ReadCharAsync(cancellationToken);
                while (true)
                {
                    // Get hex digit
                    if (c >= '0' && c <= '9')
                    {
                        chunkSize = chunkSize * 16 + (c - '0');
                    }
                    else if (c >= 'a' && c <= 'f')
                    {
                        chunkSize = chunkSize * 16 + (c - 'a' + 10);
                    }
                    else if (c >= 'A' && c <= 'F')
                    {
                        chunkSize = chunkSize * 16 + (c - 'A' + 10);
                    }
                    else
                    {
                        throw new IOException("Invalid chunk size in response stream");
                    }

                    c = await _parser.ReadCharAsync(cancellationToken);
                    if (c == '\r')
                    {
                        if (await _parser.ReadCharAsync(cancellationToken) != '\n')
                        {
                            throw new IOException("Saw CR without LF while parsing chunk size");
                        }

                        break;
                    }
                }

                _chunkBytesRemaining = chunkSize;
                if (chunkSize == 0)
                {
                    // Indicates end of response body

                    // We expect final CRLF after this
                    if (await _parser.ReadCharAsync(cancellationToken) != '\r' ||
                        await _parser.ReadCharAsync(cancellationToken) != '\n')
                    {
                        throw new IOException("missing final CRLF for chunked encoding");
                    }

//                    _parser.PutparserInPool();
                    _parser = null;
                    return false;
                }

                return true;
            }

            private async Task ConsumeChunkBytes(int bytesConsumed, CancellationToken cancellationToken)
            {
                Debug.Assert(bytesConsumed <= _chunkBytesRemaining);
                _chunkBytesRemaining -= bytesConsumed;

                if (_chunkBytesRemaining == 0)
                {
                    // Parse CRLF at end of chunk
                    if (await _parser.ReadCharAsync(cancellationToken) != '\r' ||
                        await _parser.ReadCharAsync(cancellationToken) != '\n')
                    {
                        throw new IOException("missing CRLF for end of chunk");
                    }
                }
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (buffer == null)
                {
                    throw new ArgumentNullException(nameof(buffer));
                }

                if (offset < 0 || offset > buffer.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(offset));
                }

                if (count < 0 || count > buffer.Length - offset)
                {
                    throw new ArgumentOutOfRangeException(nameof(count));
                }

                if (_parser == null)
                {
                    // Response body fully consumed
                    return 0;
                }

                if (_chunkBytesRemaining == 0)
                {
                    if (!await TryGetNextChunk(cancellationToken))
                    {
                        // End of response body
                        return 0;
                    }
                }

                count = Math.Min(count, _chunkBytesRemaining);

                int bytesRead = await _parser.ReadAsync(buffer, offset, count, cancellationToken);

                if (bytesRead == 0)
                {
                    // Unexpected end of response stream
                    throw new IOException("Unexpected end of content stream while processing chunked response body");
                }

                await ConsumeChunkBytes(bytesRead, cancellationToken);

                return bytesRead;
            }

            public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            {
                if (destination == null)
                {
                    throw new ArgumentNullException(nameof(destination));
                }

                if (_parser == null)
                {
                    // Response body fully consumed
                    return;
                }

                if (_chunkBytesRemaining > 0)
                {
                    await _parser.CopyChunkToAsync(destination, _chunkBytesRemaining, cancellationToken);
                    await ConsumeChunkBytes(_chunkBytesRemaining, cancellationToken);
                }

                while (await TryGetNextChunk(cancellationToken))
                {
                    await _parser.CopyChunkToAsync(destination, _chunkBytesRemaining, cancellationToken);
                    await ConsumeChunkBytes(_chunkBytesRemaining, cancellationToken);
                }
            }
        }

        private sealed class ConnectionCloseReadStream : HttpContentReadStream
        {
            public ConnectionCloseReadStream(HttpParser parser)
                : base(parser)
            {
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (buffer == null)
                {
                    throw new ArgumentNullException(nameof(buffer));
                }

                if (offset < 0 || offset > buffer.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(offset));
                }

                if (count < 0 || count > buffer.Length - offset)
                {
                    throw new ArgumentOutOfRangeException(nameof(count));
                }

                if (_parser == null)
                {
                    // Response body fully consumed
                    return 0;
                }

                int bytesRead = await _parser.ReadAsync(buffer, offset, count, cancellationToken);

                if (bytesRead == 0)
                {
                    // We cannot reuse this parser, so close it.
                    _parser.Dispose();
                    _parser = null;
                    return 0;
                }

                return bytesRead;
            }

            public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            {
                if (destination == null)
                {
                    throw new ArgumentNullException(nameof(destination));
                }

                if (_parser == null)
                {
                    // Response body fully consumed
                    return;
                }

                await _parser.CopyToAsync(destination, cancellationToken);

                // We cannot reuse this parser, so close it.
                _parser.Dispose();
                _parser = null;
            }
        }

        public HttpParser(
            IHttpParserHandler handler,
            Stream stream, 
            TransportContext transportContext, 
            bool usingProxy)
        {
            _handler = handler;

            _stream = stream;
            _transportContext = transportContext;
            _usingProxy = usingProxy;

            _currentElement = HttpElementType.None;

            _readBuffer = new byte[BufferSize];
            _readLength = 0;
            _readOffset = 0;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;

                _stream.Dispose();
            }
        }

        private void SetCurrentElement(HttpElementType elementType)
        {
            Debug.Assert(elementType != HttpElementType.None);
            Debug.Assert(_currentElement == HttpElementType.None);

            _currentElement = elementType;

            // Last byte read is considered the start of the element
            _elementStartOffset = _readOffset - 1;
            Debug.Assert(_elementStartOffset >= 0);
        }

        private void EmitElement()
        {
            Debug.Assert(_currentElement != HttpElementType.None);
            Debug.Assert(_elementStartOffset < _readOffset);

            // The last read byte is not part of the element to emit
            int elementEndOffset = _readOffset - 1;
            Debug.Assert(elementEndOffset >= _elementStartOffset);

            _handler.OnHttpElement(_currentElement, new ArraySegment<byte>(_readBuffer, _elementStartOffset, elementEndOffset - _elementStartOffset), true);

            _currentElement = HttpElementType.None;
        }

        private async Task ParseResponseHeaderAsync(CancellationToken cancellationToken)
        {
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
                await ReadCharAsync(cancellationToken);
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

        public async Task<Stream> ParseResponseAndGetBodyAsync(CancellationToken cancellationToken, bool noContent = false)
        {
            // TODO: Reuse SimpleParserHandler instance

            // TODO: ParseResponseHeaderAsync doesn't take an IHttpParserHandler.  Should it?
            // Probably; that's the model from Kestrel
            return null;
#if false
            // Instantiate response stream
            HttpContentReadStream responseStream;

            if (request.Method == HttpMethod.Head ||
                status == 204 ||
                status == 304)
            {
                // There is implicitly no response body
                // TODO: I don't understand why there's any content here at all --
                // i.e. why not just set response.Content = null?
                // This is legal for request bodies (e.g. GET).
                // However, setting response.Content = null causes a bunch of tests to fail.
                responseStream = new ContentLengthReadStream(this, 0);
            }
            else
            { 
                if (responseContent.Headers.ContentLength != null)
                {
                    responseStream = new ContentLengthReadStream(this, responseContent.Headers.ContentLength.Value);
                }
                else if (response.Headers.TransferEncodingChunked == true)
                {
                    responseStream = new ChunkedEncodingReadStream(this);
                }
                else
                {
                    responseStream = new ConnectionCloseReadStream(this);
                }
            }

            responseContent.SetStream(responseStream);
            response.Content = responseContent;
            return response;
#endif
        }

#if false
        private async SlimTask<HttpRequestMessage> ParseRequestAsync(CancellationToken cancellationToken)
        {
            HttpRequestMessage request = new HttpRequestMessage();

            // Read method
            char c = await ReadCharAsync(cancellationToken);
            if (c == ' ')
            {
                throw new HttpRequestException("could not read request method");
            }

            do
            {
                _sb.Append(c);
                c = await ReadCharAsync(cancellationToken);
            } while (c != ' ');

            request.Method = new HttpMethod(_sb.ToString());
            _sb.Clear();

            // Read Uri
            c = await ReadCharAsync(cancellationToken);
            if (c == ' ')
            {
                throw new HttpRequestException("could not read request uri");
            }

            do
            {
                _sb.Append(c);
                c = await ReadCharAsync(cancellationToken);
            } while (c != ' ');

            string uri = _sb.ToString();
            _sb.Clear();

            // Read Http version
            if (await ReadCharAsync(cancellationToken) != 'H' ||
                await ReadCharAsync(cancellationToken) != 'T' ||
                await ReadCharAsync(cancellationToken) != 'T' ||
                await ReadCharAsync(cancellationToken) != 'P' ||
                await ReadCharAsync(cancellationToken) != '/' ||
                await ReadCharAsync(cancellationToken) != '1' ||
                await ReadCharAsync(cancellationToken) != '.' ||
                await ReadCharAsync(cancellationToken) != '1')
            {
                throw new HttpRequestException("could not read response HTTP version");
            }

            if (await ReadCharAsync(cancellationToken) != '\r' ||
                await ReadCharAsync(cancellationToken) != '\n')
            {
                throw new HttpRequestException("expected CRLF");
            }

            var requestContent = new HttpparserContent(CancellationToken.None);

            string hostHeader = null;

            // Parse headers
            // TODO: Share with response parsing path
            c = await ReadCharAsync(cancellationToken);
            while (true)
            {
                if (c == '\r')
                {
                    if (await ReadCharAsync(cancellationToken) != '\n')
                        throw new HttpRequestException("Saw CR without LF while parsing headers");

                    break;
                }

                // Get header name
                while (c != ':')
                {
                    _sb.Append(c);
                    c = await ReadCharAsync(cancellationToken);
                }

                //                string headerName = _sb.ToString();

                // TODO: validate header name
                HeaderInfo headerInfo = HeaderInfo.Get(_sb);

                _sb.Clear();

                // Get header value
                c = await ReadCharAsync(cancellationToken);
                while (c == ' ')
                {
                    c = await ReadCharAsync(cancellationToken);
                }

                while (c != '\r')
                {
                    _sb.Append(c);
                    c = await ReadCharAsync(cancellationToken);
                }

                if (await ReadCharAsync(cancellationToken) != '\n')
                {
                    throw new HttpRequestException("Saw CR without LF while parsing headers");
                }

                string headerValue = _sb.ToString();
                _sb.Clear();

                // TryAddWithoutValidation will fail if the header name has trailing whitespace.
                // So, trim it here.
                // TODO: Not clear to me from the RFC that this is really correct; RFC seems to indicate this should be an error.
//                headerName = headerName.TrimEnd();

                if (headerInfo.HeaderType == HeaderInfo.HttpHeaderType.Content)
                {
                    requestContent.Headers.TryAddWithoutValidation(headerInfo, headerValue);
                }
                else
                {
                    request.Headers.TryAddWithoutValidation(headerInfo, headerValue);
                }

                // Capture Host header so we can use to construct the request Uri below
                if (headerInfo == HeaderInfo.KnownHeaders.Host)
                {
                    hostHeader = headerValue;
                }

                c = await ReadCharAsync(cancellationToken);
            }

            // Validate Host header and construct Uri
            // TODO: this isn't quite right; it's expecting just a host name without port
            // Do validation in a different way
#if false
            if (Uri.CheckHostName(hostHeader) == UriHostNameType.Unknown)
            {
                throw new HttpRequestException("invalid Host header");
            }
#endif

            // TODO: https
            request.RequestUri = new Uri("http://" + hostHeader + uri);

            // Instantiate requestStream
            HttpContentReadStream requestStream;

            // TODO: Other cases where implicit no request body?
            if (request.Method == HttpMethod.Get || 
                request.Method == HttpMethod.Head)
            {
                // Implicitly no request body
                requestStream = new ContentLengthReadStream(this, 0);
            }
            else if (request.Headers.TransferEncodingChunked == true)
            {
                requestStream = new ChunkedEncodingReadStream(this);
            }
            else if (request.Content.Headers.ContentLength != null)
            {
                requestStream = new ContentLengthReadStream(this, request.Content.Headers.ContentLength.Value);
            }
            else
            {
                throw new HttpRequestException("invalid request body");
            }

            requestContent.SetStream(requestStream);
            request.Content = requestContent;
            return request;
        }
#endif

        private async SlimTask FillAsync(CancellationToken cancellationToken)
        {
            Debug.Assert(_readOffset == _readLength);

            // Emit partial element, if any
            if (_currentElement != HttpElementType.None)
            {
                _handler.OnHttpElement(_currentElement, new ArraySegment<byte>(_readBuffer, _elementStartOffset, _readLength - _elementStartOffset), false);
            }

            _readOffset = 0;
            _readLength = await _stream.ReadAsync(_readBuffer, 0, BufferSize, cancellationToken);

//            Console.WriteLine("Read bytes:");
//            Console.WriteLine(System.Text.Encoding.UTF8.GetString(_readBuffer, 0, _readLength));
        }

        private async SlimTask<char> ReadCharSlowAsync(CancellationToken cancellationToken)
        {
            await FillAsync(cancellationToken);

            if (_readLength == 0)
            {
                // End of stream
                throw new IOException("unexpected end of stream");
            }

            byte b = _readBuffer[_readOffset++];
            if ((b & 0x80) != 0)
            {
                throw new HttpRequestException("Invalid character read from stream");
            }

            return (char)b;
        }

        private SlimTask<char> ReadCharAsync(CancellationToken cancellationToken)
        {
            if (_readOffset < _readLength)
            {
                byte b = _readBuffer[_readOffset++];
                if ((b & 0x80) != 0)
                {
                    throw new HttpRequestException("Invalid character read from stream");
                }

                return new SlimTask<char>((char)b);
            }

            return ReadCharSlowAsync(cancellationToken);
        }

        private void ReadFromBuffer(byte[] buffer, int offset, int count)
        {
            Debug.Assert(count <= _readLength - _readOffset);

            Buffer.BlockCopy(_readBuffer, _readOffset, buffer, offset, count);
            _readOffset += count;
        }

        private async SlimTask<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // This is called when reading the response body

            int remaining = _readLength - _readOffset;
            if (remaining > 0)
            {
                // We have data in the read buffer.  Return it to the caller.
                count = Math.Min(count, remaining);
                ReadFromBuffer(buffer, offset, count);
                return count;
            }

            // No data in read buffer. 
            if (count < BufferSize / 2)
            {
                // Caller requested a small read size (less than half the read buffer size).
                // Read into the buffer, so that we read as much as possible, hopefully.
                await FillAsync(cancellationToken);

                count = Math.Min(count, _readLength);
                ReadFromBuffer(buffer, offset, count);
                return count;
            }

            // Large read size, and no buffered data.
            // Do an unbuffered read directly against the underlying stream.
            count = await _stream.ReadAsync(buffer, offset, count, cancellationToken);
            return count;
        }

        private async SlimTask CopyFromBuffer(Stream destination, int count, CancellationToken cancellationToken)
        {
            Debug.Assert(count <= _readLength - _readOffset);

            await destination.WriteAsync(_readBuffer, _readOffset, count, cancellationToken);
            _readOffset += count;
        }

        private async SlimTask CopyToAsync(Stream destination, CancellationToken cancellationToken)
        {
            Debug.Assert(destination != null);

            int remaining = _readLength - _readOffset;
            if (remaining > 0)
            {
                await CopyFromBuffer(destination, remaining, cancellationToken);
            }

            while (true)
            {
                await FillAsync(cancellationToken);
                if (_readLength == 0)
                {
                    // End of stream
                    break;
                }

                await CopyFromBuffer(destination, _readLength, cancellationToken);
            }
        }

        // Copy *exactly* [length] bytes into destination; throws on end of stream.
        private async SlimTask CopyChunkToAsync(Stream destination, long length, CancellationToken cancellationToken)
        {
            Debug.Assert(destination != null);
            Debug.Assert(length > 0);

            int remaining = _readLength - _readOffset;
            if (remaining > 0)
            {
                remaining = (int)Math.Min(remaining, length);
                await CopyFromBuffer(destination, remaining, cancellationToken);

                length -= remaining;
                if (length == 0)
                {
                    return;
                }
            }

            while (true)
            {
                await FillAsync(cancellationToken);
                if (_readLength == 0)
                {
                    throw new HttpRequestException("unexpected end of stream");
                }

                remaining = (int)Math.Min(_readLength, length);
                await CopyFromBuffer(destination, remaining, cancellationToken);

                length -= remaining;
                if (length == 0)
                {
                    return;
                }
            }
        }
        internal bool HasBufferedReadBytes => (_readOffset < _readLength);
    }


    internal abstract class HttpContentReadStream : Stream
    {
        protected HttpParser _parser;

        public HttpContentReadStream(HttpParser parser)
        {
            _parser = parser;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length
        {
            get { throw new NotSupportedException(); }
        }

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count, CancellationToken.None).Result;
        }

        public override void CopyTo(Stream destination, int bufferSize)
        {
            CopyToAsync(destination, bufferSize, CancellationToken.None).Wait();
        }

        public abstract override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);
        public abstract override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (_parser != null)
            {
                // We haven't finished reading the body, so close the parser.
                _parser.Dispose();
                _parser = null;
            }

            base.Dispose(disposing);
        }
    }

    internal static class Utf8Helpers
    {
        internal static byte ToLowerUtf8(this byte b)
        {
            return (b >= 'A' && b <= 'Z') ? (byte)(b - 'A' + 'a') : b;
        }
    }
}
