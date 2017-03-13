// Licensed to the .NET Foundation under one or more agreements.
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
        private static readonly HttpResponseMessage s_response;

        static PlaintextHandler()
        {
            s_response = new HttpResponseMessage(HttpStatusCode.OK);
            s_response.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("Hello, World!"));
            s_response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

            // This is a hack to force header generation/parsing/whatever up front
            foreach (var h in s_response.Content.Headers)
            {
            }
        }

        public PlaintextHandler()
        {
        }

        protected internal override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri.LocalPath == "/plaintext")
            {
                // TODO
                //                response.Headers.Date = 

                return Task.FromResult(s_response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
