﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client.ApiConfig.Parameters;
using Microsoft.Identity.Client.AppConfig;
using Microsoft.Identity.Client.Cache.Items;
using Microsoft.Identity.Client.Core;
using Microsoft.Identity.Client.Http;
using Microsoft.Identity.Client.OAuth2;
using Microsoft.Identity.Client.PlatformsCommon.Interfaces;
using Microsoft.Identity.Client.Utils;
using Microsoft.Identity.Client.ManagedIdentity;
using System.Linq;

namespace Microsoft.Identity.Client.Internal.Requests
{
    internal class SlcManagedIdentityAuthRequest : ManagedIdentityAuthRequest
    {
        private readonly Uri _credentialEndpoint;

        private SlcManagedIdentityAuthRequest(
            IServiceBundle serviceBundle,
            AuthenticationRequestParameters authenticationRequestParameters,
            AcquireTokenForManagedIdentityParameters managedIdentityParameters,
            Uri credentialEndpoint)
            : base(serviceBundle, authenticationRequestParameters, managedIdentityParameters)
        {
            _credentialEndpoint = credentialEndpoint;
        }

        public static SlcManagedIdentityAuthRequest TryCreate(
            IServiceBundle serviceBundle,
            AuthenticationRequestParameters authenticationRequestParameters,
            AcquireTokenForManagedIdentityParameters managedIdentityParameters)
        {
            return UseSlcManagedIdentity(
                authenticationRequestParameters.RequestContext,
                out Uri credentialEndpointUri) ?
                    new SlcManagedIdentityAuthRequest(
                    serviceBundle,
                    authenticationRequestParameters,
                    managedIdentityParameters, credentialEndpointUri) : null;
        }

        protected override async Task<AuthenticationResult> GetAccessTokenAsync(
            IKeyMaterialManager keyMaterial,
            CancellationToken cancellationToken,
            ILoggerAdapter logger)
        {
            await ResolveAuthorityAsync().ConfigureAwait(false);

            //calls sent to app token provider
            AuthenticationResult authResult = null;
            MsalAccessTokenCacheItem cachedAccessTokenItem = null;

            //allow only one call to the provider 
            logger.Verbose(() => "[SlcManagedIdentityAuthRequest] Entering acquire token for managed identity credential request semaphore.");
            await s_semaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);
            logger.Verbose(() => "[SlcManagedIdentityAuthRequest] Entered acquire token for managed identity credential request semaphore.");

            try
            {
                // Bypass cache and send request to token endpoint, when 
                // 1. Force refresh is requested, or
                // 2. Claims are passed, or 
                // 3. If the AT needs to be refreshed pro-actively 
                if (_managedIdentityParameters.ForceRefresh ||
                    !string.IsNullOrEmpty(AuthenticationRequestParameters.Claims) ||
                    AuthenticationRequestParameters.RequestContext.ApiEvent.CacheInfo == CacheRefreshReason.ProactivelyRefreshed)
                {
                    authResult = await GetAccessTokenFromTokenEndpointAsync(keyMaterial, cancellationToken, logger).ConfigureAwait(false);
                }
                else
                {
                    cachedAccessTokenItem = await GetCachedAccessTokenAsync().ConfigureAwait(false);

                    if (cachedAccessTokenItem == null)
                    {
                        authResult = await GetAccessTokenFromTokenEndpointAsync(keyMaterial, cancellationToken, logger).ConfigureAwait(false);
                    }
                    else
                    {
                        logger.Verbose(() => "[SlcManagedIdentityAuthRequest] Getting Access token from cache ...");
                        authResult = CreateAuthenticationResultFromCache(cachedAccessTokenItem);
                    }
                }

                return authResult;
            }
            finally
            {
                s_semaphoreSlim.Release();
                logger.Verbose(() => "[SlcManagedIdentityAuthRequest] Released acquire token for managed identity credential request semaphore.");
            }
        }

        private async Task<AuthenticationResult> GetAccessTokenFromTokenEndpointAsync(
            IKeyMaterialManager keyMaterial,
            CancellationToken cancellationToken,
            ILoggerAdapter logger)
        {
            string message;
            Exception exception = null;

            try
            {
                logger.Verbose(() => "[SlcManagedIdentityAuthRequest] Getting token from the managed identity endpoint.");

                SlcCredentialResponse credentialResponse =
                    await GetCredentialAssertionAsync(keyMaterial, logger, cancellationToken).ConfigureAwait(false);

                var baseUri = new Uri(credentialResponse.RegionalTokenUrl);
                var tokenUrl = new Uri(baseUri, $"{credentialResponse.TenantId}/oauth2/v2.0/token");

                logger.Verbose(() => $"[SlcManagedIdentityAuthRequest] Token endpoint : { tokenUrl }.");

                OAuth2Client client = CreateClientRequest(
                    keyMaterial,
                    AuthenticationRequestParameters.RequestContext.ServiceBundle.HttpManager,
                    credentialResponse);

                MsalTokenResponse msalTokenResponse = await client
                        .GetTokenAsync(tokenUrl,
                        AuthenticationRequestParameters.RequestContext,
                        true,
                        AuthenticationRequestParameters.OnBeforeTokenRequestHandler).ConfigureAwait(false);

                msalTokenResponse.Scope = AuthenticationRequestParameters.Scope.AsSingleString();

                logger.Info("[SlcManagedIdentityAuthRequest] Successful response received.");

                return await CacheTokenResponseAndCreateAuthenticationResultAsync(msalTokenResponse)
                    .ConfigureAwait(false);
            }
            catch (MsalClientException ex)
            {
                logger.Verbose(() => $"[SlcManagedIdentityAuthRequest] Caught an exception. {ex.Message}");
                throw;
            }
            catch (HttpRequestException ex)
            {
                exception = MsalServiceExceptionFactory.CreateManagedIdentityException(
                MsalError.ManagedIdentityUnreachableNetwork,
                ex.Message,
                ex,
                ManagedIdentitySource.SlcCredential,
                null);

                logger.Verbose(() => $"[SlcManagedIdentityAuthRequest] Caught an exception. {ex.Message}");

                throw exception;
            }
            catch (MsalServiceException ex)
            {
                logger.Verbose(() => $"[SlcManagedIdentityAuthRequest] Caught an exception. {ex.Message}. Error Code : {ex.ErrorCode} Status Code : {ex.StatusCode}");

                exception = MsalServiceExceptionFactory.CreateManagedIdentityException(
                    ex.ErrorCode,
                    ex.Message,
                    ex,
                    ManagedIdentitySource.SlcCredential,
                    ex.StatusCode);

                throw exception;
            }
            catch (Exception e) when (e is not MsalServiceException)
            {
                logger.Error($"[SlcManagedIdentityAuthRequest] Exception: {e.Message}");
                exception = e;
                message = MsalErrorMessage.CredentialEndpointNoResponseReceived;
            }

            MsalException msalException = MsalServiceExceptionFactory.CreateManagedIdentityException(
                MsalError.CredentialRequestFailed,
                message,
                exception,
                ManagedIdentitySource.SlcCredential,
                null);

            throw msalException;
        }

        private async Task<SlcCredentialResponse> GetCredentialAssertionAsync(
            IKeyMaterialManager keyMaterial,
            ILoggerAdapter logger,
            CancellationToken cancellationToken
            )
        {
            var msiCredentialService = new ManagedIdentityCredentialService(
                _credentialEndpoint,
                keyMaterial.BindingCertificate,
                AuthenticationRequestParameters.RequestContext,
                cancellationToken);

            SlcCredentialResponse credentialResponse = await msiCredentialService.GetCredentialAsync().ConfigureAwait(false);

            logger.Verbose(() => "[SlcManagedIdentityAuthRequest] A credential was successfully fetched.");

            return credentialResponse;
        }

        /// <summary>
        /// Creates an OAuth2 client request for fetching the managed identity credential.
        /// </summary>
        /// <param name="keyMaterial"></param>
        /// <param name="httpManager"></param>
        /// <param name="credentialResponse"></param>
        /// <returns></returns>
        private OAuth2Client CreateClientRequest(
            IKeyMaterialManager keyMaterial,
            IHttpManager httpManager,
            SlcCredentialResponse credentialResponse)
        {
            // Initialize an OAuth2 client with logger, HTTP manager, and binding certificate.
            var client = new OAuth2Client(
                AuthenticationRequestParameters.RequestContext.Logger,
                httpManager,
                keyMaterial.BindingCertificate);

            // Convert overridden scopes to a single string.
            string scopes = GetOverriddenScopes(AuthenticationRequestParameters.Scope).AsSingleString();

            //credential flows must have a scope value with /.default suffixed to the resource identifier (application ID URI)
            scopes += "/.default";

            // Add required parameters for client credentials grant request.
            client.AddBodyParameter(OAuth2Parameter.GrantType, OAuth2GrantType.ClientCredentials);
            client.AddBodyParameter(OAuth2Parameter.Scope, scopes);
            client.AddBodyParameter(OAuth2Parameter.ClientId, credentialResponse.ClientId);
            client.AddBodyParameter(OAuth2Parameter.ClientAssertion, credentialResponse.Credential);
            client.AddBodyParameter(OAuth2Parameter.ClientAssertionType, OAuth2AssertionType.JwtBearer);

            // Add optional claims and client capabilities parameter if provided.
            if (!string.IsNullOrWhiteSpace(AuthenticationRequestParameters.ClaimsAndClientCapabilities))
            {
                client.AddBodyParameter(OAuth2Parameter.Claims, AuthenticationRequestParameters.ClaimsAndClientCapabilities);
            }

            // Return the configured OAuth2 client.
            return client;
        }

        // Check if CredentialKeyType is set to a valid value for Managed Identity.
        private static bool UseSlcManagedIdentity(
            RequestContext requestContext,
            out Uri credentialEndpointUri)
        {
            credentialEndpointUri = null;
            
            CryptoKeyType credentialKeyType = requestContext.ServiceBundle.Config.ManagedIdentityCredentialKeyType;
            bool isClaimsRequested = requestContext.ServiceBundle.Config.ClientCapabilities?.Any() == true;

            // CredentialKeyType will be Undefined, if no keys are provisioned.
            if (credentialKeyType == CryptoKeyType.Undefined)
            {
                // If new CAE APIs are used when no keys are defined, throw an exception.
                if (isClaimsRequested)
                {
                    requestContext.Logger.Verbose(() => "[SlcManagedIdentityAuthRequest] Claims-based authentication is not " +
                    "supported with Managed Identity on the current Azure Resource.");

                    throw new MsalClientException(
                        MsalError.ClaimsNotSupportedOnMiResource,
                        MsalErrorMessage.ResourceDoesNotSupportClaims);
                }
                // Log the unavailability of credential based managed identity for a basic request.
                // Proceed to use Legacy MSI flow.
                requestContext.Logger.Verbose(() => "[SlcManagedIdentityAuthRequest] Credential based managed identity is unavailable without specific client capabilities.");
                return false;
            }

            if (credentialKeyType == CryptoKeyType.KeyGuardUser && !isClaimsRequested)
            {
                // MSAL will setup a user key in public preview and try to fetch a credential if claims are requested.
                // Even if MSAL was able to setup a user key and no claims where requested proceed to use Legacy MSI flow.
                requestContext.Logger.Verbose(() => "[SlcManagedIdentityAuthRequest] Credential based managed identity is unavailable without specific client capabilities.");
                return false;
            }

            // Initialize the credentialUri with the constant CredentialEndpoint and API version.
            string credentialUri = Constants.CredentialEndpoint;

            // Switch based on the type of Managed Identity ID provided.
            switch (requestContext.ServiceBundle.Config.ManagedIdentityId.IdType)
            {
                // If the ID is of type ClientId, add user assigned client id to the request.
                case ManagedIdentityIdType.ClientId:
                    requestContext.Logger.Info("[SlcManagedIdentityAuthRequest] Adding user assigned client id to the request.");
                    credentialUri += $"&{Constants.ManagedIdentityClientId}={requestContext.ServiceBundle.Config.ManagedIdentityId.UserAssignedId}";
                    break;

                // If the ID is of type ResourceId, add user assigned resource id to the request.
                case ManagedIdentityIdType.ResourceId:
                    requestContext.Logger.Info("[SlcManagedIdentityAuthRequest] Adding user assigned resource id to the request.");
                    credentialUri += $"&{Constants.ManagedIdentityResourceId}={requestContext.ServiceBundle.Config.ManagedIdentityId.UserAssignedId}";
                    break;

                // If the ID is of type ObjectId, add user assigned object id to the request.
                case ManagedIdentityIdType.ObjectId:
                    requestContext.Logger.Info("[SlcManagedIdentityAuthRequest] Adding user assigned object id to the request.");
                    credentialUri += $"&{Constants.ManagedIdentityObjectId}={requestContext.ServiceBundle.Config.ManagedIdentityId.UserAssignedId}";
                    break;
            }

            // Set the credentialEndpointUri with the constructed URI.
            credentialEndpointUri = new Uri(credentialUri);

            // Log information about creating Credential based managed identity.
            requestContext.Logger.Info($"[SlcManagedIdentityAuthRequest] Creating Credential based managed identity.");
            return true;
        }
    }
}