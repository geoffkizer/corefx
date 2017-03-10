﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http.Headers;

// TODO: This is temporary

namespace System.Net.Http.Managed
{
    public sealed class PlaintextHandler : HttpMessageHandler
    {
        private static readonly byte[] _helloWorldPayload = Encoding.UTF8.GetBytes("Hello, World!");
        private static readonly MediaTypeHeaderValue _contentTypeHeader = new MediaTypeHeaderValue("text/plain");

        public PlaintextHandler()
        {
        }

        protected internal override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri.LocalPath == "/plaintext")
            {
                HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Content = new ByteArrayContent(_helloWorldPayload);
                response.Content.Headers.ContentType = _contentTypeHeader;
                // TODO
//                response.Headers.Date = 

                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
