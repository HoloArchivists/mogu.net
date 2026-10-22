using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Mogu.Core.Data;
using Mogu.Core.Options;
using Mogu.Web.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<MoguOptions>()
    .BindConfiguration(MoguOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<DiscordOptions>()
    .BindConfiguration(DiscordOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

var dataRoot = DataPaths.Resolve(builder.Environment, builder.Configuration);
Directory.CreateDirectory(dataRoot);

builder.Services.AddDbContext<MoguDbContext>(options =>
    options.UseSqlite(DataPaths.ResolveConnectionString(builder.Configuration.GetConnectionString("Mogu"), builder.Environment)));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DataPaths>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<IAuthorizationHandler, MinimumLevelHandler>();
builder.Services.AddMoguServices();

builder.Services.AddMoguAuthentication(builder.Configuration);
builder.Services.AddAuthorizationBuilder().AddMoguPolicies();

builder.Services.AddDataProtection()
    .SetApplicationName("Mogu")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataRoot, "keys")));

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // Mogu runs behind nginx on the same host; loopback proxies are trusted by default.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

builder.Services.AddProblemDetails();
builder.Services.AddOutputCache();
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Admin", Policies.Moderator);
});

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<MoguDbContext>().Database.MigrateAsync();
}

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/Error/{0}");

app.UseRouting();
app.UseAuthentication();
app.UseMiddleware<CurrentUserMiddleware>();
app.UseMiddleware<RequestGuardMiddleware>();
app.UseAuthorization();
app.UseOutputCache();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapMoguEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapDevelopmentLogin();
}

await app.RunAsync();

/// <summary>Entry point marker for <c>WebApplicationFactory</c> in tests.</summary>
public partial class Program;
