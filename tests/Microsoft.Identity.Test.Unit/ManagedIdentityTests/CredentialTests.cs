﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.AppConfig;
using Microsoft.Identity.Client.ManagedIdentity;
using Microsoft.Identity.Client.PlatformsCommon.Interfaces;
using Microsoft.Identity.Client.TelemetryCore.Internal.Events;
using Microsoft.Identity.Test.Common.Core.Helpers;
using Microsoft.Identity.Test.Common.Core.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.Identity.Test.Unit.ManagedIdentityTests
{
    /// <summary>
    /// Unit tests for the Credential component in the Managed Identity scenario.
    /// </summary>
    [TestClass]
    public class CredentialTests : TestBase
    {
        private const string AppService = "App Service";
        internal const string CredentialEndpoint = "http://169.254.169.254/metadata/identity/credential";
        internal const string MtlsEndpoint = "https://centraluseuap.mtlsauth.microsoft.com/" +
            "72f988bf-86f1-41af-91ab-2d7cd011db47/oauth2/v2.0/token";

        /// <summary>
        /// Tests the happy path for acquiring credentials with various CryptoKeyTypes.
        /// </summary>
        /// <param name="cryptoKeyType">The type of cryptographic key used.</param>
        [DataTestMethod]
        [DataRow(CryptoKeyType.Machine)]
        //[DataRow(CryptoKeyType.User)]
        //[DataRow(CryptoKeyType.InMemory)]
        //[DataRow(CryptoKeyType.Ephemeral)]
        //[DataRow(CryptoKeyType.KeyGuard)]
        public async Task CredentialHappyPathAsync(CryptoKeyType cryptoKeyType)
        {
            // Arrange
            using (MockHttpAndServiceBundle harness = CreateTestHarness())
            using (var httpManager = new MockHttpManager(isManagedIdentity: true))
            {
                ManagedIdentityApplicationBuilder miBuilder = ManagedIdentityApplicationBuilder
                    .Create(ManagedIdentityId.SystemAssigned)
                    .WithHttpManager(httpManager);

                KeyMaterialManagerMock keyManagerMock = new(CertHelper.GetOrCreateTestCert(), cryptoKeyType);
                miBuilder.Config.KeyMaterialManagerForTest = keyManagerMock;

                // Disabling shared cache options to avoid cross test pollution.
                miBuilder.Config.AccessorOptions = null;

                IManagedIdentityApplication mi = miBuilder.Build();

                httpManager.AddManagedIdentityCredentialMockHandler(
                    CredentialEndpoint,
                    sendHeaders: true,
                    MockHelpers.GetSuccessfulCredentialResponse());

                httpManager.AddManagedIdentityCredentialMockHandler(
                    MtlsEndpoint,
                    sendHeaders: false,
                    MockHelpers.GetSuccessfulMtlsResponse());

                // Act
                // We should get the auth result from the token provider
                AuthenticationResult result = await mi.AcquireTokenForManagedIdentity(ManagedIdentityTests.Resource)
                    .ExecuteAsync()
                    .ConfigureAwait(false);

                Assert.IsNotNull(result);
                Assert.IsNotNull(result.AccessToken);
                Assert.AreEqual(TokenSource.IdentityProvider, result.AuthenticationResultMetadata.TokenSource);

                // Act
                // We should get the auth result from the cache
                result = await mi.AcquireTokenForManagedIdentity(ManagedIdentityTests.Resource)
                    .ExecuteAsync()
                    .ConfigureAwait(false);

                Assert.IsNotNull(result);
                Assert.IsNotNull(result.AccessToken);
                Assert.AreEqual(TokenSource.Cache, result.AuthenticationResultMetadata.TokenSource);
            }
        }

        /// <summary>
        /// Tests the Force Refresh on MI.
        /// </summary>
        [TestMethod]
        public async Task CredentialForceRefreshAsync()
        {
            // Arrange
            using (MockHttpAndServiceBundle harness = CreateTestHarness())
            using (var httpManager = new MockHttpManager(isManagedIdentity: true))
            {
                ManagedIdentityApplicationBuilder miBuilder = ManagedIdentityApplicationBuilder
                    .Create(ManagedIdentityId.SystemAssigned)
                    .WithHttpManager(httpManager);

                KeyMaterialManagerMock keyManagerMock = new(CertHelper.GetOrCreateTestCert(), CryptoKeyType.Machine);
                miBuilder.Config.KeyMaterialManagerForTest = keyManagerMock;

                // Disabling shared cache options to avoid cross test pollution.
                miBuilder.Config.AccessorOptions = null;

                IManagedIdentityApplication mi = miBuilder.Build();

                httpManager.AddManagedIdentityCredentialMockHandler(
                    CredentialEndpoint,
                    sendHeaders: true,
                    MockHelpers.GetSuccessfulCredentialResponse());

                httpManager.AddManagedIdentityCredentialMockHandler(
                    MtlsEndpoint,
                    sendHeaders: false,
                    MockHelpers.GetSuccessfulMtlsResponse());

                // Act
                // We should get the auth result from the token provider
                AuthenticationResult result = await mi.AcquireTokenForManagedIdentity(ManagedIdentityTests.Resource)
                    .ExecuteAsync()
                    .ConfigureAwait(false);

                Assert.IsNotNull(result);
                Assert.IsNotNull(result.AccessToken);
                Assert.AreEqual(TokenSource.IdentityProvider, result.AuthenticationResultMetadata.TokenSource);

                // Act
                // We should get the auth result from the cache
                result = await mi.AcquireTokenForManagedIdentity(ManagedIdentityTests.Resource)
                    .ExecuteAsync()
                    .ConfigureAwait(false);

                Assert.IsNotNull(result);
                Assert.IsNotNull(result.AccessToken);
                Assert.AreEqual(TokenSource.Cache, result.AuthenticationResultMetadata.TokenSource);

                httpManager.AddManagedIdentityCredentialMockHandler(
                    CredentialEndpoint,
                    sendHeaders: true,
                    MockHelpers.GetSuccessfulCredentialResponse());

                httpManager.AddManagedIdentityCredentialMockHandler(
                    MtlsEndpoint,
                    sendHeaders: false,
                    MockHelpers.GetSuccessfulMtlsResponse());

                // We should get the auth result from the token provider when force refreshed
                result = await mi.AcquireTokenForManagedIdentity(ManagedIdentityTests.Resource)
                    .WithForceRefresh(true)
                    .ExecuteAsync()
                    .ConfigureAwait(false);

                Assert.IsNotNull(result);
                Assert.IsNotNull(result.AccessToken);
                Assert.AreEqual(TokenSource.IdentityProvider, result.AuthenticationResultMetadata.TokenSource);
            }
        }

        /// <summary>
        /// Tests the Claims on MI.
        /// </summary>
        [TestMethod]
        public async Task CredentialWithClaimsAsync()
        {
            // Arrange
            using (MockHttpAndServiceBundle harness = CreateTestHarness())
            using (var httpManager = new MockHttpManager(isManagedIdentity: true))
            {
                ManagedIdentityApplicationBuilder miBuilder = ManagedIdentityApplicationBuilder
                    .Create(ManagedIdentityId.SystemAssigned)
                    .WithExperimentalFeatures(true)
                    .WithClientCapabilities(new[] { "CP1" })
                    .WithHttpManager(httpManager);

                KeyMaterialManagerMock keyManagerMock = new(CertHelper.GetOrCreateTestCert(), CryptoKeyType.User);
                miBuilder.Config.KeyMaterialManagerForTest = keyManagerMock;

                // Disabling shared cache options to avoid cross test pollution.
                miBuilder.Config.AccessorOptions = null;

                IManagedIdentityApplication mi = miBuilder.Build();

                httpManager.AddManagedIdentityCredentialMockHandler(
                    CredentialEndpoint,
                    sendHeaders: true,
                    MockHelpers.GetSuccessfulCredentialResponse());

                httpManager.AddManagedIdentityCredentialMockHandler(
                    MtlsEndpoint,
                    sendHeaders: false,
                    MockHelpers.GetSuccessfulMtlsResponse());

                // Act
                // We should get the auth result from the token provider
                AuthenticationResult result = await mi.AcquireTokenForManagedIdentity(ManagedIdentityTests.Resource)
                    .ExecuteAsync()
                    .ConfigureAwait(false);

                Assert.IsNotNull(result);
                Assert.IsNotNull(result.AccessToken);
                Assert.AreEqual(TokenSource.IdentityProvider, result.AuthenticationResultMetadata.TokenSource);

                // Act
                // We should get the auth result from the cache
                result = await mi.AcquireTokenForManagedIdentity(ManagedIdentityTests.Resource)
                    .ExecuteAsync()
                    .ConfigureAwait(false);

                Assert.IsNotNull(result);
                Assert.IsNotNull(result.AccessToken);
                Assert.AreEqual(TokenSource.Cache, result.AuthenticationResultMetadata.TokenSource);

                // Arrange
                httpManager.AddManagedIdentityCredentialMockHandler(
                    CredentialEndpoint,
                    sendHeaders: true,
                    MockHelpers.GetSuccessfulCredentialResponse());

                httpManager.AddManagedIdentityCredentialMockHandler(
                    MtlsEndpoint,
                    sendHeaders: false,
                    MockHelpers.GetSuccessfulMtlsResponse());

                // Act
                // We should get the auth result from the token provider when claims are passed
                var builder = mi.AcquireTokenForManagedIdentity(ManagedIdentityTests.Resource)
                    .WithClaims(TestConstants.Claims);

                result = await builder.ExecuteAsync().ConfigureAwait(false);

                Assert.IsNotNull(result);
                Assert.IsNotNull(result.AccessToken);
                Assert.AreEqual(TokenSource.IdentityProvider, result.AuthenticationResultMetadata.TokenSource);
                //Assert.AreEqual(ApiEvent.ApiIds, builder.CommonParameters.ApiId);
            }
        }

        /// <summary>
        /// Tests the Invalid Credential endpoint.
        /// </summary>
        [TestMethod]
        public async Task InvalidCredentialEndpointAsync()
        {
            using (MockHttpAndServiceBundle harness = CreateTestHarness())
            using (var httpManager = new MockHttpManager(isManagedIdentity: true))
            {
                //Arrange
                ManagedIdentityApplicationBuilder miBuilder = ManagedIdentityApplicationBuilder.Create(ManagedIdentityId.SystemAssigned)
                    .WithHttpManager(httpManager);

                KeyMaterialManagerMock keyManagerMock = new(CertHelper.GetOrCreateTestCert(), CryptoKeyType.Ephemeral);
                miBuilder.Config.KeyMaterialManagerForTest = keyManagerMock;

                // Disabling shared cache options to avoid cross test pollution.
                miBuilder.Config.AccessorOptions = null;

                IManagedIdentityApplication mi = miBuilder.Build();

                MsalManagedIdentityException ex = await Assert.ThrowsExceptionAsync<MsalManagedIdentityException>(async () =>
                    await mi.AcquireTokenForManagedIdentity(ManagedIdentityTests.Resource)
                    .ExecuteAsync().ConfigureAwait(false)).ConfigureAwait(false);

                //Act
                Assert.IsNotNull(ex);
                Assert.AreEqual(ManagedIdentitySource.Credential, ex.ManagedIdentitySource);
                Assert.AreEqual(string.Format(CultureInfo.InvariantCulture, MsalErrorMessage.CredentialEndpointNoResponseReceived), ex.Message);
            }
        }
    }
}