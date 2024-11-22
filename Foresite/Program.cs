using Foresite.Components;
using Foresite.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<WhazzupService>();
builder.Services.AddSingleton<CifpService>();

var app = builder.Build();

// Force load of CIFPs & Whazzup.
_ = app.Services.GetRequiredService<CifpService>().Cifp;
_ = app.Services.GetRequiredService<WhazzupService>();

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
