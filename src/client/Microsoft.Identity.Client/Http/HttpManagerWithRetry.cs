﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client.Core;

namespace Microsoft.Identity.Client.Http
{
    /// <remarks>
    /// We invoke this class from different threads and they all use the same HttpClient.
    /// To prevent race conditions, make sure you do not get / set anything on HttpClient itself,
    /// instead rely on HttpRequest objects which are thread specific.
    ///
    /// In particular, do not change any properties on HttpClient such as BaseAddress, buffer sizes and Timeout. You should
    /// also not access DefaultRequestHeaders because the getters are not thread safe (use HttpRequestMessage.Headers instead).
    /// </remarks>
    internal class HttpManagerWithRetry : HttpManager
    {

        public HttpManagerWithRetry(IMsalHttpClientFactory httpClientFactory) :
            base(httpClientFactory) { }

        /// <inheritdoc/>
        public override async Task<HttpResponse> SendPostAsync(
            Uri endpoint,
            IDictionary<string, string> headers,
            HttpContent body,
            ILoggerAdapter logger,
            CancellationToken cancellationToken = default)
        {
            return await SendRequestAsync(endpoint, headers, body, HttpMethod.Post, logger, retry: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public override async Task<HttpResponse> SendGetAsync(
            Uri endpoint,
            IDictionary<string, string> headers,
            ILoggerAdapter logger,
            bool retry = true,
            CancellationToken cancellationToken = default)
        {
            return await SendRequestAsync(endpoint, headers, null, HttpMethod.Get, logger, retry: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public override async Task<HttpResponse> SendGetForceResponseAsync(
            Uri endpoint,
            IDictionary<string, string> headers,
            ILoggerAdapter logger,
            bool retry = true,
            CancellationToken cancellationToken = default)
        {
            return await SendRequestAsync(endpoint, headers, null, HttpMethod.Get, logger, retry: true, doNotThrow: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public override async Task<HttpResponse> SendPostForceResponseAsync(
            Uri uri,
            IDictionary<string, string> headers,
            IDictionary<string, string> bodyParameters,
            ILoggerAdapter logger,
            CancellationToken cancellationToken = default)
        {
            HttpContent body = bodyParameters == null ? null : new FormUrlEncodedContent(bodyParameters);
            return await SendRequestAsync(uri, headers, body, HttpMethod.Post, logger, retry: true, doNotThrow: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public override async Task<HttpResponse> SendPostForceResponseAsync(
            Uri uri,
            IDictionary<string, string> headers,
            IDictionary<string, string> bodyParameters,
            X509Certificate2 bindingCertificate,
            ILoggerAdapter logger,
            CancellationToken cancellationToken = default)
        {
            HttpContent body = bodyParameters == null ? null : new FormUrlEncodedContent(bodyParameters);
            return await SendRequestAsync(uri, headers, body, HttpMethod.Post, logger, bindingCertificate, doNotThrow: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public override async Task<HttpResponse> SendPostForceResponseAsync(
            Uri uri,
            IDictionary<string, string> headers,
            StringContent body,
            ILoggerAdapter logger,
            CancellationToken cancellationToken = default)
        {
            return await SendRequestAsync(uri, headers, body, HttpMethod.Post, logger, retry: true, doNotThrow: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public override async Task<HttpResponse> SendPostForceResponseAsync(
            Uri uri,
            IDictionary<string, string> headers,
            StringContent body,
            X509Certificate2 bindingCert,
            ILoggerAdapter logger,
            CancellationToken cancellationToken = default)
        {
            return await SendRequestAsync(uri, headers, body, HttpMethod.Post, logger, bindingCert, doNotThrow: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        protected override HttpClient GetHttpClient(X509Certificate2 x509Certificate2)
        {
            if (x509Certificate2 is null)
            {
                return _httpClientFactory.GetHttpClient();
            }

            if (_httpClientFactory is IMsalMtlsHttpClientFactory msalMtlsHttpClientFactory)
            {
                return msalMtlsHttpClientFactory.GetHttpClient(x509Certificate2);
            }

            throw new MsalClientException("http_client_factory", "You customized HttpClient factory but you are using APIs which require provisioning of a client certificate in HttpClient. Implement IMsalMtlsHttpClientFactory");
        }

        protected override async Task<HttpResponse> SendRequestAsync(
            Uri endpoint,
            IDictionary<string, string> headers,
            HttpContent body,
            HttpMethod method,
            ILoggerAdapter logger,
            X509Certificate2 bindingCertificate = null,
            bool doNotThrow = false,
            bool retry = true,
            CancellationToken cancellationToken = default)
        {
            Exception timeoutException = null;
            bool isRetriableStatusCode = false;
            HttpResponse response = null;
            bool isRetriable;

            try
            {
                HttpContent clonedBody = body;
                if (body != null)
                {
                    // Since HttpContent would be disposed by underlying client.SendAsync(),
                    // we duplicate it so that we will have a copy in case we would need to retry
                    clonedBody = await CloneHttpContentAsync(body).ConfigureAwait(false);
                }

                using (logger.LogBlockDuration("[HttpManager] ExecuteAsync"))
                {
                    response = await ExecuteAsync(endpoint, headers, clonedBody, method, bindingCertificate, logger, cancellationToken).ConfigureAwait(false);
                }

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return response;
                }

                logger.Info(() => string.Format(CultureInfo.InvariantCulture,
                    MsalErrorMessage.HttpRequestUnsuccessful,
                    (int)response.StatusCode, response.StatusCode));

                isRetriableStatusCode = IsRetryableStatusCode((int)response.StatusCode);
                isRetriable = isRetriableStatusCode && !HasRetryAfterHeader(response);
            }
            catch (TaskCanceledException exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    logger.Info("The HTTP request was cancelled. ");
                    throw;
                }

                logger.Error("The HTTP request failed. " + exception.Message);
                isRetriable = true;
                timeoutException = exception;
            }

            if (isRetriable && retry)
            {
                logger.Info("Retrying one more time..");
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                return await SendRequestAsync(
                    endpoint,
                    headers,
                    body,
                    method,
                    logger,
                    bindingCertificate,
                    doNotThrow,
                    retry: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            logger.Warning("Request retry failed.");
            if (timeoutException != null)
            {
                throw new MsalServiceException(
                    MsalError.RequestTimeout,
                    "Request to the endpoint timed out.",
                    timeoutException);
            }

            if (doNotThrow)
            {
                return response;
            }

            // package 500 errors in a "service not available" exception
            if (isRetriableStatusCode)
            {
                throw MsalServiceExceptionFactory.FromHttpResponse(
                    MsalError.ServiceNotAvailable,
                    "Service is unavailable to process the request",
                    response);
            }

            return response;
        }

        private static bool HasRetryAfterHeader(HttpResponse response)
        {
            var retryAfter = response?.Headers?.RetryAfter;
            return retryAfter != null &&
                (retryAfter.Delta.HasValue || retryAfter.Date.HasValue);
        }
    }
}