﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client.ApiConfig.Parameters;
using Microsoft.Identity.Client.AppConfig;
using Microsoft.Identity.Client.Cache.Items;
using Microsoft.Identity.Client.Core;
using Microsoft.Identity.Client.Credential;
using Microsoft.Identity.Client.OAuth2;
using Microsoft.Identity.Client.PlatformsCommon.Interfaces;
using Microsoft.Identity.Client.Utils;

namespace Microsoft.Identity.Client.Internal.Requests
{
    internal class CredentialBasedMsiAuthRequest : RequestBase
    {
        private readonly AcquireTokenForManagedIdentityParameters _managedIdentityParameters;
        private static readonly SemaphoreSlim s_semaphoreSlim = new(1, 1);
        private readonly Uri _credentialEndpoint;
        private readonly IKeyMaterialManager _keyMaterialManager;

        private CredentialBasedMsiAuthRequest(
            IServiceBundle serviceBundle,
            AuthenticationRequestParameters authenticationRequestParameters,
            AcquireTokenForManagedIdentityParameters managedIdentityParameters,
            Uri credentialEndpoint)
            : base(serviceBundle, authenticationRequestParameters, managedIdentityParameters)
        {
            _managedIdentityParameters = managedIdentityParameters;
            _credentialEndpoint = credentialEndpoint;
            _keyMaterialManager = serviceBundle.Config.KeyMaterialManagerForTest ?? serviceBundle.PlatformProxy.GetKeyMaterialManager();
        }

        public static CredentialBasedMsiAuthRequest TryCreate(
            IServiceBundle serviceBundle,
            AuthenticationRequestParameters authenticationRequestParameters,
            AcquireTokenForManagedIdentityParameters managedIdentityParameters)
        {
            return IsCredentialKeyAvailable(authenticationRequestParameters.RequestContext, out Uri credentialEndpointUri) ?
                    new CredentialBasedMsiAuthRequest(
                    serviceBundle,
                    authenticationRequestParameters,
                    managedIdentityParameters, credentialEndpointUri) :
                    null;
        }

        protected override async Task<AuthenticationResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            if (AuthenticationRequestParameters.Scope == null || AuthenticationRequestParameters.Scope.Count == 0)
            {
                throw new MsalClientException(
                    MsalError.ScopesRequired,
                    MsalErrorMessage.ScopesRequired);
            }

            ILoggerAdapter logger = AuthenticationRequestParameters.RequestContext.Logger;

            AuthenticationResult authResult = null;

            //skip checking cache for force refresh or when claims are present
            if (_managedIdentityParameters.ForceRefresh || !string.IsNullOrEmpty(_managedIdentityParameters.Claims))
            {
                AuthenticationRequestParameters.RequestContext.ApiEvent.CacheInfo = CacheRefreshReason.ForceRefreshOrClaims;
                logger.Info("[CredentialBasedMsiAuthRequest] Skipped looking for an Access Token in the cache because ForceRefresh was set.");
                authResult = await GetAccessTokenAsync(cancellationToken, logger).ConfigureAwait(false);
                return authResult;
            }

            //check cache for AT
            MsalAccessTokenCacheItem cachedAccessTokenItem = await GetCachedAccessTokenAsync().ConfigureAwait(false);

            if (cachedAccessTokenItem != null)
            {
                //return the token in the cache and check if it needs to be proactively refreshed
                authResult = CreateAuthenticationResultFromCache(cachedAccessTokenItem);

                try
                {
                    var proactivelyRefresh = SilentRequestHelper.NeedsRefresh(cachedAccessTokenItem);

                    // may fire a request to get a new token in the background when AT needs to be refreshed
                    if (proactivelyRefresh)
                    {
                        AuthenticationRequestParameters.RequestContext.ApiEvent.CacheInfo = CacheRefreshReason.ProactivelyRefreshed;

                        SilentRequestHelper.ProcessFetchInBackground(
                        cachedAccessTokenItem,
                        () => GetAccessTokenAsync(cancellationToken, logger), logger);
                    }
                }
                catch (MsalServiceException e)
                {
                    return await HandleTokenRefreshErrorAsync(e, cachedAccessTokenItem).ConfigureAwait(false);
                }
            }
            else
            {
                //  No AT in the cache 
                if (AuthenticationRequestParameters.RequestContext.ApiEvent.CacheInfo != CacheRefreshReason.Expired)
                {
                    AuthenticationRequestParameters.RequestContext.ApiEvent.CacheInfo = CacheRefreshReason.NoCachedAccessToken;
                }

                authResult = await GetAccessTokenAsync(cancellationToken, logger).ConfigureAwait(false);
            }

            return authResult;
        }

        private async Task<AuthenticationResult> GetAccessTokenAsync(
            CancellationToken cancellationToken,
            ILoggerAdapter logger)
        {
            await ResolveAuthorityAsync().ConfigureAwait(false);

            //calls sent to app token provider
            AuthenticationResult authResult = null;
            MsalAccessTokenCacheItem cachedAccessTokenItem = null;

            try
            {
                //allow only one call to the provider 
                logger.Verbose(() => "[CredentialBasedMsiAuthRequest] Entering token acquire for managed identity credential request semaphore.");
                await s_semaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);
                logger.Verbose(() => "[CredentialBasedMsiAuthRequest] Entered token acquire for managed identity credential request semaphore.");

                // Bypass cache and send request to token endpoint, when 
                // 1. Force refresh is requested, or
                // 2. Claims are passed, or 
                // 3. If the AT needs to be refreshed pro-actively 
                if (AuthenticationRequestParameters.RequestContext.ApiEvent.CacheInfo == CacheRefreshReason.ProactivelyRefreshed ||
                    AuthenticationRequestParameters.RequestContext.ApiEvent.CacheInfo == CacheRefreshReason.ForceRefreshOrClaims)
                {
                    authResult = await GetAccessTokenFromTokenEndpointAsync(cancellationToken, logger).ConfigureAwait(false);
                }
                else
                {
                    cachedAccessTokenItem = await GetCachedAccessTokenAsync().ConfigureAwait(false);

                    if (cachedAccessTokenItem == null)
                    {
                        authResult = await GetAccessTokenFromTokenEndpointAsync(cancellationToken, logger).ConfigureAwait(false);
                    }
                    else
                    {
                        logger.Verbose(() => "[CredentialBasedMsiAuthRequest] Getting Access token from cache ...");
                        authResult = CreateAuthenticationResultFromCache(cachedAccessTokenItem);
                    }
                }

                return authResult;
            }
            catch (MsalManagedIdentityException ex)
            {
                logger.Verbose(() => $"[CredentialBasedMsiAuthRequest] Caught an exception. {ex.Message}");
                throw new MsalManagedIdentityException(ex.Source, ex.Message, ManagedIdentity.ManagedIdentitySource.Credential);
            }
            finally
            {
                s_semaphoreSlim.Release();
                logger.Verbose(() => "[CredentialBasedMsiAuthRequest] Released token acquire for managed identity credential request semaphore.");
            }
        }

        private async Task<AuthenticationResult> GetAccessTokenFromTokenEndpointAsync(
            CancellationToken cancellationToken, 
            ILoggerAdapter logger)
        {
            CredentialResponse credentialResponse = await GetCredentialAssertionAsync(logger, cancellationToken)
                .ConfigureAwait(false);

            var tenantAuthority = AuthorityInfo.FromAadAuthority(
                credentialResponse.RegionalTokenUrl, 
                tenant: credentialResponse.TenantId, 
                validateAuthority: false);
            
            MsalTokenResponse msalTokenResponse = await SendTokenRequestAsync(
                tenantAuthority.CanonicalAuthority.ToString() + "oauth2/v2.0/token", 
                GetBodyParameters(credentialResponse), cancellationToken).ConfigureAwait(false);
            
            return await CacheTokenResponseAndCreateAuthenticationResultAsync(msalTokenResponse)
                .ConfigureAwait(false);
        }

        private async Task<CredentialResponse> GetCredentialAssertionAsync(
            ILoggerAdapter logger,
            CancellationToken cancellationToken
            )
        {
            var credentialResponseCache = new ManagedIdentityCredentialResponseCache(
                _credentialEndpoint,
                AuthenticationRequestParameters.AppConfig.ClientId,
                _keyMaterialManager.BindingCertificate,
                _managedIdentityParameters,
                AuthenticationRequestParameters.RequestContext,
                cancellationToken);

            CredentialResponse credentialResponse = await credentialResponseCache.GetOrFetchCredentialAsync().ConfigureAwait(false);

            return credentialResponse;
        }

        private async Task<MsalAccessTokenCacheItem> GetCachedAccessTokenAsync()
        {
            MsalAccessTokenCacheItem cachedAccessTokenItem = await CacheManager.FindAccessTokenAsync().ConfigureAwait(false);

            if (cachedAccessTokenItem != null && !_managedIdentityParameters.ForceRefresh)
            {
                AuthenticationRequestParameters.RequestContext.ApiEvent.IsAccessTokenCacheHit = true;
                Metrics.IncrementTotalAccessTokensFromCache();
                return cachedAccessTokenItem;
            }

            return null;
        }

        private AuthenticationResult CreateAuthenticationResultFromCache(MsalAccessTokenCacheItem cachedAccessTokenItem)
        {
            AuthenticationResult authResult = new AuthenticationResult(
                                                            cachedAccessTokenItem,
                                                            null,
                                                            AuthenticationRequestParameters.AuthenticationScheme,
                                                            AuthenticationRequestParameters.RequestContext.CorrelationId,
                                                            TokenSource.Cache,
                                                            AuthenticationRequestParameters.RequestContext.ApiEvent,
                                                            account: null,
                                                            spaAuthCode: null,
                                                            additionalResponseParameters: null);
            return authResult;
        }

        protected override SortedSet<string> GetOverriddenScopes(ISet<string> inputScopes)
        {
            // Client credentials should not add the reserved scopes
            // "openid", "profile" and "offline_access" 
            // because AT is on behalf of an app (no profile, no IDToken, no RT)
            return new SortedSet<string>(inputScopes);
        }

        private Dictionary<string, string> GetBodyParameters(CredentialResponse credentialResponse)
        {
            var dict = new Dictionary<string, string>
            {
                [OAuth2Parameter.GrantType] = OAuth2GrantType.ClientCredentials,
                [OAuth2Parameter.Scope] = AuthenticationRequestParameters.Scope.AsSingleString(),
                [OAuth2Parameter.ClientId] = credentialResponse.ClientId,
                [OAuth2Parameter.ClientAssertion] = credentialResponse.Credential,
                [OAuth2Parameter.Claims] = AuthenticationRequestParameters.Claims,
                [OAuth2Parameter.ClientAssertionType] = OAuth2AssertionType.JwtBearer,
            };
            return dict;
        }

        protected override KeyValuePair<string, string>? GetCcsHeader(IDictionary<string, string> additionalBodyParameters)
        {
            return null;
        }

        private static bool IsCredentialKeyAvailable(
                RequestContext requestContext, out Uri credentialEndpointUri)
        {
            credentialEndpointUri = null;

            if (!requestContext.ServiceBundle.Config.IsManagedIdentityTokenRequestInfoAvailable)
            {
                requestContext.Logger.Verbose(() => "[Managed Identity] Credential based managed identity is unavailable.");
                return false;
            }

            string credentialUri = Constants.CredentialEndpoint;

            switch (requestContext.ServiceBundle.Config.ManagedIdentityId.IdType)
            {
                case ManagedIdentityIdType.ClientId:
                    requestContext.Logger.Info("[Managed Identity] Adding user assigned client id to the request.");
                    credentialUri += $"&{Constants.ManagedIdentityClientId}={requestContext.ServiceBundle.Config.ManagedIdentityId.UserAssignedId}";
                    break;

                case ManagedIdentityIdType.ResourceId:
                    requestContext.Logger.Info("[Managed Identity] Adding user assigned resource id to the request.");
                    credentialUri += $"&{Constants.ManagedIdentityResourceId}={requestContext.ServiceBundle.Config.ManagedIdentityId.UserAssignedId}";
                    break;

                case ManagedIdentityIdType.ObjectId:
                    requestContext.Logger.Info("[Managed Identity] Adding user assigned object id to the request.");
                    credentialUri += $"&{Constants.ManagedIdentityObjectId}={requestContext.ServiceBundle.Config.ManagedIdentityId.UserAssignedId}";
                    break;
            }

            credentialEndpointUri = new(credentialUri);

            requestContext.Logger.Info($"[Managed Identity] Creating Credential based managed identity.");
            return true;
        }
    }
}