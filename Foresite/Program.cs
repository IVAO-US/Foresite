using Foresite.Components;
using Foresite.Services;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

const string OIDC_SCHEME = "ivao";

try
{
	var builder = WebApplication.CreateBuilder(args);

	// Add services to the container.
	builder.Services.AddAuthentication(OIDC_SCHEME)
		.AddOpenIdConnect(OIDC_SCHEME, oidcOptions =>
		{
			oidcOptions.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
			oidcOptions.Scope.Add(OpenIdConnectScope.OpenIdProfile);
			oidcOptions.Authority = "https://api.ivao.aero/";
			oidcOptions.ClientId = "0202bfa6-1a63-47a7-a0fd-91a6395c756a";
			oidcOptions.ResponseType = OpenIdConnectResponseType.Code;
			oidcOptions.MapInboundClaims = false;
			oidcOptions.TokenValidationParameters.NameClaimType = "nickname";
			oidcOptions.TokenValidationParameters.RoleClaimType = "roles";
		})
		.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme);

	// ConfigureCookieOidcRefresh attaches a cookie OnValidatePrincipal callback to get
	// a new access token when the current one expires, and reissue a cookie with the
	// new access token saved inside. If the refresh fails, the user will be signed
	// out. OIDC connect options are set for saving tokens and the offline access
	// scope.
	builder.Services.ConfigureCookieOidcRefresh(CookieAuthenticationDefaults.AuthenticationScheme, OIDC_SCHEME);

	builder.Services.AddAuthorization();

	builder.Services.AddCascadingAuthenticationState();

	builder.Services.AddRazorComponents()
		.AddInteractiveServerComponents();

	builder.Services.AddHttpContextAccessor();
	builder.Services.AddHttpClient();
	builder.Services.AddHttpContextAccessor();
	builder.Services.AddSingleton<CifpService>();
	builder.Services.AddSingleton<WhazzupService>();

	builder.Services.AddScoped<IvaoApiService>();

	var app = builder.Build();

	// Force load of CIFPs & Whazzup.
	_ = app.Services.GetRequiredService<CifpService>().Cifp;
	_ = app.Services.GetRequiredService<WhazzupService>();

	// GitHub build pipeline to generate assets.
	if (app.Environment.IsEnvironment("Deploy"))
		Environment.Exit(0);

	// Configure the HTTP request pipeline.
	if (!app.Environment.IsDevelopment())
	{
		app.UseExceptionHandler("/Error", createScopeForErrors: true);
		// The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
		app.UseHsts();
	}

	app.UseHttpsRedirection();
	app.MapStaticAssets();
	app.UseAntiforgery();

	app.MapRazorComponents<App>()
		.AddInteractiveServerRenderMode();

	app.MapGroup("/auth").MapLoginAndLogout();

	app.MapGet("/api/livetrack/procedures/{airport}", (string airport, CifpService cifp) => cifp.Cifp.Procedures.SelectMany(kvp => kvp.Value).Where(p => p.Airport == airport));

	app.Run();
}
catch (Exception ex)
{
	// Log it so we can at least try to read it in a way that makes some semblance of sense.
	Console.Error.WriteLine(ex.Message);
	Console.Error.WriteLine(ex.StackTrace);

#if DEBUG
	File.WriteAllText("foresite.err", $"ERROR {DateTime.UtcNow:R}: {ex.Message}\n{ex.StackTrace}");
#else
	File.WriteAllText(Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".", "foresite.err"), $"ERROR {DateTime.UtcNow:R}: {ex.Message}\n{ex.StackTrace}");
#endif

	throw;
}