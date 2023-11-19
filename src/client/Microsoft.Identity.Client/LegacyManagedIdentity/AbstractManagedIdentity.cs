﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client.Extensibility;
using Microsoft.Identity.Client.Http;
using Microsoft.Identity.Client.Utils;
using Microsoft.Identity.Client.Internal;
using Microsoft.Identity.Client.Core;
using System.Net;
using Microsoft.Identity.Client.ApiConfig.Parameters;
using System.Net.Sockets;
using System.Diagnostics;

#if TRA
using Microsoft.Identity.Client.Credential;
#endif

namespace Microsoft.Identity.Client.ManagedIdentity
{
    /// <summary>
    /// Original source of code: https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/identity/Azure.Identity/src/ManagedIdentitySource.cs
    /// </summary>
    internal abstract class AbstractManagedIdentity
    {
        protected readonly RequestContext _requestContext;
        internal const string TimeoutError = "[Managed Identity] Authentication unavailable. The request to the managed identity endpoint timed out.";
        internal readonly ManagedIdentitySource _sourceType;
#if TRA
        private readonly CredentialResponseCache _credentialResponseCache;
#endif
        protected AbstractManagedIdentity(RequestContext requestContext, ManagedIdentitySource sourceType)
        {
            _requestContext = requestContext;
            _sourceType = sourceType;
#if TRA
            _credentialResponseCache = CredentialResponseCache.GetCredentialInstance(_requestContext);
#endif
        }

        public virtual async Task<ManagedIdentityResponse> AuthenticateAsync(
            AcquireTokenForManagedIdentityParameters parameters,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _requestContext.Logger.Error(TimeoutError);
                cancellationToken.ThrowIfCancellationRequested();
            }

            HttpResponse response = null;

            // Convert the scopes to a resource string.
            string resource = parameters.Resource;

            ManagedIdentityRequest request = CreateRequest(resource);

            _requestContext.Logger.Info("[Managed Identity] sending request to managed identity endpoints.");

            try
            {
                if (request.Method == HttpMethod.Get)
                {
                    response = await _requestContext.ServiceBundle.HttpManager
                        .SendRequestAsync(
                            request.ComputeUri(),
                            request.Headers,
                            body: null,
                            HttpMethod.Get,
                            logger: _requestContext.Logger,
                            doNotThrow: true,
                            retry: true,
                            mtlsCertificate: null,
                            cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (_sourceType != ManagedIdentitySource.Credential)
                    {
                        Debug.WriteLine("_sourceType in AuthenticateAsync" + _sourceType);

                        response = await _requestContext.ServiceBundle.HttpManager
                            .SendRequestAsync(
                                request.ComputeUri(),
                                request.Headers,
                                body: new FormUrlEncodedContent(request.BodyParameters),
                                HttpMethod.Post,
                                logger: _requestContext.Logger,
                                doNotThrow: true,
                                retry: true,
                                mtlsCertificate: null,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
#if TRA
                        string credentialCacheKey = request.GetCredentialCacheKey();

                        response = await _credentialResponseCache.GetOrFetchCredentialAsync(
                                                        _requestContext.ServiceBundle.HttpManager,
                                                        request,
                                                        credentialCacheKey,
                                                        CancellationToken.None).ConfigureAwait(false);
#endif                    
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                throw new MsalManagedIdentityException(
                    MsalError.ManagedIdentityUnreachableNetwork, ex.Message, ex.InnerException, _sourceType);
            }
            catch (TaskCanceledException)
            {
                _requestContext.Logger.Error(TimeoutError);
                throw;
            }

            return await HandleResponseAsync(parameters, response, cancellationToken).ConfigureAwait(false);
        }

        protected virtual Task<ManagedIdentityResponse> HandleResponseAsync(
            AcquireTokenForManagedIdentityParameters parameters,
            HttpResponse response,
            CancellationToken cancellationToken)
        {
            string message;
            Exception exception = null;

            try
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    _requestContext.Logger.Info("[Managed Identity] Successful response received.");
                    return Task.FromResult(GetSuccessfulResponse(response));
                }

                message = GetMessageFromErrorResponse(response);
                _requestContext.Logger.Error($"[Managed Identity] request failed, HttpStatusCode: {response.StatusCode} Error message: {message}");
            }
            catch (Exception e) when (e is not MsalManagedIdentityException)
            {
                _requestContext.Logger.Error($"[Managed Identity] Exception: {e.Message} Http status code: {response?.StatusCode}");
                exception = e;
                message = MsalErrorMessage.ManagedIdentityUnexpectedResponse;
            }

            throw new MsalManagedIdentityException(MsalError.ManagedIdentityRequestFailed, message, exception, _sourceType, (int)response.StatusCode);
        }

        protected abstract ManagedIdentityRequest CreateRequest(string resource);

        protected ManagedIdentityResponse GetSuccessfulResponse(HttpResponse response)
        {
            ManagedIdentityResponse managedIdentityResponse = JsonHelper.DeserializeFromJson<ManagedIdentityResponse>(response.Body);

            if (managedIdentityResponse == null || managedIdentityResponse.AccessToken.IsNullOrEmpty()
                && (managedIdentityResponse.ExpiresOn.IsNullOrEmpty() || managedIdentityResponse.ExpiresIn.IsNullOrEmpty()))
            {
                _requestContext.Logger.Error("[Managed Identity] Response is either null or insufficient for authentication.");
                throw new MsalManagedIdentityException(
                    MsalError.ManagedIdentityRequestFailed,
                    MsalErrorMessage.ManagedIdentityInvalidResponse,
                    _sourceType);
            }

            return managedIdentityResponse;
        }

        internal string GetMessageFromErrorResponse(HttpResponse response)
        {
            var managedIdentityErrorResponse = JsonHelper.TryToDeserializeFromJson<ManagedIdentityErrorResponse>(response?.Body);
            string additionalErrorInfo = string.Empty;

            if (managedIdentityErrorResponse == null)
            {
                return MsalErrorMessage.ManagedIdentityNoResponseReceived;
            }

            if (!string.IsNullOrEmpty(managedIdentityErrorResponse.Message))
            {
                return $"[Managed Identity] Error Message: {managedIdentityErrorResponse.Message} " +
                    $"Managed Identity Correlation ID: {managedIdentityErrorResponse.CorrelationId} " +
                    $"Use this Correlation ID for further investigation.";
            }

            return $"[Managed Identity] Error Code: {managedIdentityErrorResponse.Error} " +
                $"Error Message: {managedIdentityErrorResponse.ErrorDescription} ";
        }
    }
}
