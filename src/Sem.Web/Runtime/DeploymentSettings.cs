using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
namespace Sem.Web.Runtime;
public sealed class DeploymentSettings
{
    public string PublicUrl { get; set; } = "https://sem-test.raub.jp/";
    public string DataDirectory { get; set; } = "/data";
    public string[] TrustedProxies { get; set; } = [];
    public string BootstrapSecret { get; set; } = "";
    public Uri Validate()
    {
        if (!Uri.TryCreate(PublicUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo)) throw new InvalidOperationException("SEM PublicUrl must be an HTTPS origin with a root path.");
        if (!Path.IsPathFullyQualified(DataDirectory)) throw new InvalidOperationException("SEM DataDirectory must be an absolute path.");
        foreach (var proxy in TrustedProxies) if (!IPAddress.TryParse(proxy, out _)) throw new InvalidOperationException("TrustedProxies must contain explicit IP addresses.");
        return uri;
    }
}
public static class DeploymentConfiguration
{
    public static DeploymentSettings AddSemDeployment(this WebApplicationBuilder builder)
    {
        var settings = builder.Configuration.GetSection("Sem").Get<DeploymentSettings>() ?? new();
        var origin = settings.Validate();
        Directory.CreateDirectory(settings.DataDirectory);
        var keys = Path.Combine(settings.DataDirectory, "keys");
        Directory.CreateDirectory(keys);
        builder.Services.AddSingleton(settings);
        builder.Services.AddDataProtection().SetApplicationName("SEM.ExternalTest").PersistKeysToFileSystem(new DirectoryInfo(keys));
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 1;
            o.KnownIPProxies.Clear();
            o.KnownIPNetworks.Clear();
            foreach (var address in settings.TrustedProxies) o.KnownIPProxies.Add(IPAddress.Parse(address));
        });
        builder.Services.Configure<IdentityPasskeyOptions>(o =>
        {
            o.ServerDomain = origin.Host;
            o.UserVerificationRequirement = "required";
        });
        builder.Services.ConfigureApplicationCookie(o =>
        {
            o.Cookie.Name = "__Host-SEM-Auth";
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.Path = "/";
        });
        return settings;
    }
    public static async Task InitializeSemStorageAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync();
    }
}
