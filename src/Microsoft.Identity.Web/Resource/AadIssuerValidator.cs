﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using Microsoft.Identity.Web.InstanceDiscovery;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;

namespace Microsoft.Identity.Web.Resource
{
    /// <summary>
    /// Generic class that validates token issuer from the provided Azure AD authority.
    /// </summary>
    internal class AadIssuerValidator
    {
        // TODO: separate AadIssuerValidator creation logic from the validation logic in order to unit test it
        private static readonly IDictionary<string, AadIssuerValidator> s_issuerValidators = new ConcurrentDictionary<string, AadIssuerValidator>();

        private static readonly ConfigurationManager<IssuerMetadata> s_configManager = new ConfigurationManager<IssuerMetadata>(Constants.AzureADIssuerMetadataUrl, new IssuerConfigurationRetriever());

        /// <summary>
        /// A list of all Issuers across the various Azure AD instances.
        /// </summary>
        private readonly ISet<string> _issuerAliases;

        internal /* internal for test */ AadIssuerValidator(IEnumerable<string> aliases)
        {
            _issuerAliases = new HashSet<string>(aliases, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets an <see cref="AadIssuerValidator"/> for an authority.
        /// </summary>
        /// <param name="aadAuthority">The authority to create the validator for, e.g. https://login.microsoftonline.com/. </param>
        /// <returns>A <see cref="AadIssuerValidator"/> for the aadAuthority.</returns>
        /// <exception cref="ArgumentNullException">if <paramref name="aadAuthority"/> is null or empty.</exception>
        public static AadIssuerValidator GetIssuerValidator(string aadAuthority)
        {
            if (string.IsNullOrEmpty(aadAuthority))
            {
                throw new ArgumentNullException(nameof(aadAuthority));
            }

            Uri.TryCreate(aadAuthority, UriKind.Absolute, out Uri? authorityUri);
            string authorityHost = authorityUri?.Authority ?? new Uri(Constants.FallbackAuthority).Authority;

            if (s_issuerValidators.TryGetValue(authorityHost, out AadIssuerValidator? aadIssuerValidator))
            {
                return aadIssuerValidator;
            }

            // In the constructor, we hit the Azure AD issuer metadata endpoint and cache the aliases. The data is cached for 24 hrs.
            IssuerMetadata issuerMetadata = s_configManager.GetConfigurationAsync().ConfigureAwait(false).GetAwaiter().GetResult();

            // Add issuer aliases of the chosen authority to the cache
            IEnumerable<string> aliases = issuerMetadata.Metadata
                .Where(m => m.Aliases.Any(a => string.Equals(a, authorityHost, StringComparison.OrdinalIgnoreCase)))
                .SelectMany(m => m.Aliases)
                .Append(authorityHost) // For B2C scenarios, the alias will be the authority itself
                .Distinct();
            s_issuerValidators[authorityHost] = new AadIssuerValidator(aliases);

            return s_issuerValidators[authorityHost];
        }

        /// <summary>
        /// Validate the issuer for multi-tenant applications of various audience (Work and School account, or Work and School accounts +
        /// Personal accounts).
        /// </summary>
        /// <param name="actualIssuer">Issuer to validate (will be tenanted).</param>
        /// <param name="securityToken">Received Security Token.</param>
        /// <param name="validationParameters">Token Validation parameters.</param>
        /// <remarks>The issuer is considered as valid if it has the same HTTP scheme and authority as the
        /// authority from the configuration file, has a tenant ID, and optionally v2.0 (this web API
        /// accepts both V1 and V2 tokens).
        /// Authority aliasing is also taken into account.</remarks>
        /// <returns>The <c>issuer</c> if it's valid, or otherwise <c>SecurityTokenInvalidIssuerException</c> is thrown.</returns>
        /// <exception cref="ArgumentNullException"> if <paramref name="securityToken"/> is null.</exception>
        /// <exception cref="ArgumentNullException"> if <paramref name="validationParameters"/> is null.</exception>
        /// <exception cref="SecurityTokenInvalidIssuerException">if the issuer. </exception>
        public string Validate(string actualIssuer, SecurityToken securityToken, TokenValidationParameters validationParameters)
        {
            if (string.IsNullOrEmpty(actualIssuer))
            {
                throw new ArgumentNullException(nameof(actualIssuer));
            }

            if (securityToken == null)
            {
                throw new ArgumentNullException(nameof(securityToken));
            }

            if (validationParameters == null)
            {
                throw new ArgumentNullException(nameof(validationParameters));
            }

            string tenantId = GetTenantIdFromToken(securityToken);
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                throw new SecurityTokenInvalidIssuerException(IDWebErrorMessage.TenantIdClaimNotPresentInToken);
            }

            if (validationParameters.ValidIssuers != null)
            {
                foreach (var validIssuerTemplate in validationParameters.ValidIssuers)
                {
                    if (IsValidIssuer(validIssuerTemplate, tenantId, actualIssuer))
                    {
                        return actualIssuer;
                    }
                }
            }

            if (IsValidIssuer(validationParameters.ValidIssuer, tenantId, actualIssuer))
            {
                return actualIssuer;
            }

            // If a valid issuer is not found, throw
            // brentsch - todo, create a list of all the possible valid issuers in TokenValidationParameters
            throw new SecurityTokenInvalidIssuerException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    IDWebErrorMessage.IssuerDoesNotMatchValidIssuers,
                    actualIssuer));
        }

        private bool IsValidIssuer(string validIssuerTemplate, string tenantId, string actualIssuer)
        {
            if (string.IsNullOrEmpty(validIssuerTemplate))
            {
                return false;
            }

            try
            {
                Uri issuerFromTemplateUri = new Uri(validIssuerTemplate.Replace("{tenantid}", tenantId));
                Uri actualIssuerUri = new Uri(actualIssuer);

                // Template authority is in the aliases
                return _issuerAliases.Contains(issuerFromTemplateUri.Authority) &&
                       // "iss" authority is in the aliases
                       _issuerAliases.Contains(actualIssuerUri.Authority) &&
                      // Template authority ends in the tenant ID
                      IsValidTidInLocalPath(tenantId, issuerFromTemplateUri) &&
                      // "iss" ends in the tenant ID
                      IsValidTidInLocalPath(tenantId, actualIssuerUri);
            }
            catch
            {
                // if something faults, ignore
            }

            return false;
        }

        private static bool IsValidTidInLocalPath(string tenantId, Uri uri)
        {
            string trimmedLocalPath = uri.LocalPath.Trim('/');
            return trimmedLocalPath == tenantId || trimmedLocalPath == $"{tenantId}/v2.0" || (uri.Segments.Length > 2 && uri.Segments[2].TrimEnd('/') == tenantId);
        }

        /// <summary>Gets the tenant ID from a token.</summary>
        /// <param name="securityToken">A JWT token.</param>
        /// <returns>A string containing tenant ID, if found or <see cref="string.Empty"/>.</returns>
        /// <remarks>Only <see cref="JwtSecurityToken"/> and <see cref="JsonWebToken"/> are acceptable types.</remarks>
        private static string GetTenantIdFromToken(SecurityToken securityToken)
        {
            if (securityToken is JwtSecurityToken jwtSecurityToken)
            {
                if (jwtSecurityToken.Payload.TryGetValue(ClaimConstants.Tid, out object? tenantId))
                {
                    return (string)tenantId;
                }

                // Since B2C doesn't have "tid" as default, get it from issuer
                return GetTenantIdFromIss(jwtSecurityToken.Issuer);
            }

            if (securityToken is JsonWebToken jsonWebToken)
            {
                jsonWebToken.TryGetPayloadValue(ClaimConstants.Tid, out string? tid);
                if (tid != null)
                {
                    return tid;
                }

                // Since B2C doesn't have "tid" as default, get it from issuer
                return GetTenantIdFromIss(jsonWebToken.Issuer);
            }

            return string.Empty;
        }

        // The AAD "iss" claims contains the tenant ID in its value.
        // The URI can be
        // - {domain}/{tid}/v2.0
        // - {domain}/{tid}/v2.0/
        // - {domain}/{tfp}/{tid}/{userFlow}/v2.0/
        private static string GetTenantIdFromIss(string iss)
        {
            if (string.IsNullOrEmpty(iss))
            {
                return string.Empty;
            }

            var uri = new Uri(iss);

            if (uri.Segments.Length == 3)
            {
                return uri.Segments[1].TrimEnd('/');
            }

            if (uri.Segments.Length == 5 && uri.Segments[1].TrimEnd('/') == ClaimConstants.Tfp)
            {
                return uri.Segments[2].TrimEnd('/');
            }

            return string.Empty;
        }
    }
}
