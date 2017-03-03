// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Threading;

namespace System.Net.Http.Managed
{
    internal class CookieHandler : MessageProcessingHandler
    {
        private CookieContainer _cookieContainer;

        public CookieHandler(CookieContainer cookieContainer, HttpMessageHandler innerHandler)
            : base(innerHandler)
        {
            if (cookieContainer == null)
            {
                throw new ArgumentNullException(nameof(cookieContainer));
            }

            _cookieContainer = cookieContainer;
        }

        protected override HttpRequestMessage ProcessRequest(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string cookieHeader = _cookieContainer.GetCookieHeader(request.RequestUri);
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                request.Headers.Add("Cookie", cookieHeader);
            }

            return request;
        }

        protected override HttpResponseMessage ProcessResponse(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            // Handle Set-Cookie
            IEnumerable<string> setCookies;
            if (response.Headers.TryGetValues("Set-Cookie", out setCookies))
            {
                foreach (string setCookie in setCookies)
                {
                    _cookieContainer.SetCookies(response.RequestMessage.RequestUri, setCookie);
                }
            }

            return response;
        }
    }
}
