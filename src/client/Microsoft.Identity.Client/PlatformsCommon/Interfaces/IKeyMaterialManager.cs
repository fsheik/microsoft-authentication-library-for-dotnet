﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Microsoft.Identity.Client.PlatformsCommon.Interfaces
{
    internal interface IKeyMaterialManager
    {
        CryptoKeyType CryptoKeyType { get; }

        X509Certificate2 BindingCertificate { get; }

        X509Certificate2 GetOrCreateCertificateFromCryptoKeyInfo();

    }
}