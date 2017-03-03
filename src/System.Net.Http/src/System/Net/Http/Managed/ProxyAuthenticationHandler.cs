// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http.Managed
{
    internal sealed class ProxyAuthenticationHandler : HttpMessageHandler
    {
        HttpMessageHandler _innerHandler;
        IWebProxy _proxy;

        public ProxyAuthenticationHandler(IWebProxy proxy, HttpMessageHandler innerHandler)
        {
            if (innerHandler == null)
            {
                throw new ArgumentNullException(nameof(innerHandler));
            }

            if (proxy == null)
            {
                throw new ArgumentNullException(nameof(proxy));
            }

            _proxy = proxy;
            _innerHandler = innerHandler;
        }

        protected internal override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await _innerHandler.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
            {
                Uri proxyUri = ManagedHttpClientHandler.GetProxyUri(_proxy, request.RequestUri);
                if (proxyUri != null)
                {
                    foreach (AuthenticationHeaderValue h in response.Headers.ProxyAuthenticate)
                    {
                        // We only support Basic auth, ignore others
                        if (h.Scheme == "Basic")
                        {
                            NetworkCredential credential = _proxy.Credentials.GetCredential(proxyUri, "Basic");
                            if (credential == null)
                            {
                                break;
                            }

                            request.Headers.ProxyAuthorization = new AuthenticationHeaderValue("Basic",
                                BasicAuthenticationHelper.GetBasicTokenForCredential(credential));

                            response = await _innerHandler.SendAsync(request, cancellationToken);
                            break;
                        }
                    }
                }
            }

            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _innerHandler.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
