using Foresite.Components;
using Foresite.Services;

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Add services to the container.
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    builder.Services.AddHttpContextAccessor();
    builder.Services.AddHttpClient();
    builder.Services.AddSingleton<WhazzupService>();
    builder.Services.AddSingleton<CifpService>();

    var app = builder.Build();

    // Force load of CIFPs.
    _ = app.Services.GetRequiredService<CifpService>().Cifp;

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