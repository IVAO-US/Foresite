using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

using System.Globalization;

using System.IdentityModel.Tokens.Jwt;

using System.Security.Claims;

namespace Foresite.Services;

internal static class LoginLogoutEndpointRouteBuilderExtensions
{
	internal static IEndpointConventionBuilder MapLoginAndLogout(this IEndpointRouteBuilder endpoints)
	{
		var group = endpoints.MapGroup("");

		group.MapGet("/login", (string? returnUrl) => TypedResults.Challenge(GetAuthProperties(returnUrl)))
			.AllowAnonymous();

		// Sign out of the Cookie and OIDC handlers. If you do not sign out with the OIDC handler,
		// the user will automatically be signed back in the next time they visit a page that requires authentication
		// without being able to choose another account.
		group.MapPost("/logout", ([FromForm] string? returnUrl) => TypedResults.SignOut(GetAuthProperties(returnUrl),
			[CookieAuthenticationDefaults.AuthenticationScheme, "ivao"]));

		return group;
	}

	private static AuthenticationProperties GetAuthProperties(string? returnUrl)
	{
		// TODO: Use HttpContext.Request.PathBase instead.
		const string pathBase = "/";

		// Prevent open redirects.
		if (string.IsNullOrEmpty(returnUrl))
		{
			returnUrl = pathBase;
		}
		else if (!Uri.IsWellFormedUriString(returnUrl, UriKind.Relative))
		{
			returnUrl = new Uri(returnUrl, UriKind.Absolute).PathAndQuery;
		}
		else if (returnUrl[0] != '/')
		{
			returnUrl = $"{pathBase}{returnUrl}";
		}

		return new AuthenticationProperties { RedirectUri = returnUrl };
	}
}

internal static partial class CookieOidcServiceCollectionExtensions
{
	public static IServiceCollection ConfigureCookieOidcRefresh(this IServiceCollection services, string cookieScheme, string oidcScheme)
	{
		services.AddSingleton<CookieOidcRefresher>();
		services.AddOptions<CookieAuthenticationOptions>(cookieScheme).Configure<CookieOidcRefresher>((cookieOptions, refresher) =>
		{
			cookieOptions.Events.OnValidatePrincipal = context => refresher.ValidateOrRefreshCookieAsync(context, oidcScheme);
		});
		services.AddOptions<OpenIdConnectOptions>(oidcScheme).Configure(oidcOptions =>
		{
			// Store the refresh_token.
			oidcOptions.SaveTokens = true;
		});
		return services;
	}
}

// https://github.com/dotnet/aspnetcore/issues/8175
internal sealed class CookieOidcRefresher(IOptionsMonitor<OpenIdConnectOptions> oidcOptionsMonitor)
{
	private readonly OpenIdConnectProtocolValidator oidcTokenValidator = new() {
		// We no longer have the original nonce cookie which is deleted at the end of the authorization code flow having served its purpose.
		// Even if we had the nonce, it's likely expired. It's not intended for refresh requests. Otherwise, we'd use oidcOptions.ProtocolValidator.
		RequireNonce = false,
	};

	public async Task ValidateOrRefreshCookieAsync(CookieValidatePrincipalContext validateContext, string oidcScheme)
	{
		var accessTokenExpirationText = validateContext.Properties.GetTokenValue("expires_at");
		if (!DateTimeOffset.TryParse(accessTokenExpirationText, out var accessTokenExpiration))
		{
			return;
		}

		var oidcOptions = oidcOptionsMonitor.Get(oidcScheme);
		var now = oidcOptions.TimeProvider!.GetUtcNow();
		if (now + TimeSpan.FromMinutes(5) < accessTokenExpiration)
		{
			return;
		}

		var oidcConfiguration = await oidcOptions.ConfigurationManager!.GetConfigurationAsync(validateContext.HttpContext.RequestAborted);
		var tokenEndpoint = oidcConfiguration.TokenEndpoint ?? throw new InvalidOperationException("Cannot refresh cookie. TokenEndpoint missing!");

		using var refreshResponse = await oidcOptions.Backchannel.PostAsync(tokenEndpoint,
			new FormUrlEncodedContent(new Dictionary<string, string?>() {
				["grant_type"] = "refresh_token",
				["client_id"] = oidcOptions.ClientId,
				["client_secret"] = oidcOptions.ClientSecret,
				["scope"] = string.Join(" ", oidcOptions.Scope),
				["refresh_token"] = validateContext.Properties.GetTokenValue("refresh_token"),
			}));

		if (!refreshResponse.IsSuccessStatusCode)
		{
			validateContext.RejectPrincipal();
			return;
		}

		var refreshJson = await refreshResponse.Content.ReadAsStringAsync();
		var message = new OpenIdConnectMessage(refreshJson);

		var validationParameters = oidcOptions.TokenValidationParameters.Clone();
		if (oidcOptions.ConfigurationManager is BaseConfigurationManager baseConfigurationManager)
		{
			validationParameters.ConfigurationManager = baseConfigurationManager;
		}
		else
		{
			validationParameters.ValidIssuer = oidcConfiguration.Issuer;
			validationParameters.IssuerSigningKeys = oidcConfiguration.SigningKeys;
		}

		var validationResult = await oidcOptions.TokenHandler.ValidateTokenAsync(message.IdToken, validationParameters);

		if (!validationResult.IsValid)
		{
			validateContext.RejectPrincipal();
			return;
		}

		var validatedIdToken = JwtSecurityTokenConverter.Convert(validationResult.SecurityToken as JsonWebToken);
		validatedIdToken.Payload["nonce"] = null;
		oidcTokenValidator.ValidateTokenResponse(new() {
			ProtocolMessage = message,
			ClientId = oidcOptions.ClientId,
			ValidatedIdToken = validatedIdToken,
		});

		validateContext.ShouldRenew = true;
		validateContext.ReplacePrincipal(new ClaimsPrincipal(validationResult.ClaimsIdentity));

		var expiresIn = int.Parse(message.ExpiresIn, NumberStyles.Integer, CultureInfo.InvariantCulture);
		var expiresAt = now + TimeSpan.FromSeconds(expiresIn);
		validateContext.Properties.StoreTokens([
			new() { Name = "access_token", Value = message.AccessToken },
			new() { Name = "id_token", Value = message.IdToken },
			new() { Name = "refresh_token", Value = message.RefreshToken },
			new() { Name = "token_type", Value = message.TokenType },
			new() { Name = "expires_at", Value = expiresAt.ToString("o", CultureInfo.InvariantCulture) },
		]);
	}
}