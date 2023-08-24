// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.AspNetCore.Identity.Data;

/// <summary>
/// The request type for the "/refresh" endpoint added by MapIdentityApi.
/// </summary>
public sealed class RefreshRequest
{
    /// <summary>
    /// The refresh token from the last "/login" or "/refresh" response.
    /// with an extended expiration.
    /// </summary>
    public required string RefreshToken { get; init; }
}
