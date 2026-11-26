using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.Hosting;

public static class FlowLedgerDataProtectionExtensions
{
    /// <summary>Stable application discriminator so every replica sharing a key directory can read each other's payloads.</summary>
    public const string ApplicationName = "FlowLedger";

    /// <summary>
    /// Persists the ASP.NET Data Protection key ring (auth cookies, antiforgery tokens, protected payloads) so it
    /// survives container recreation and can be shared by replicas.
    /// <list type="bullet">
    /// <item><c>DataProtection:KeysPath</c> - directory on a mounted volume; back it up with the rest of the recovery set.</item>
    /// <item><c>DataProtection:CertificatePath</c> (+ <c>CertificatePassword</c>) - optional PFX that encrypts the keys at rest
    /// (on Linux the framework otherwise writes them unencrypted, so restrict the volume's permissions).</item>
    /// <item><c>DataProtection:Required=true</c> - outside Development, refuse to start when no KeysPath is set. Off by
    /// default so existing deployments keep starting (with a warning in the log from the framework's own ephemeral-key
    /// notice); turn it on once the volume is mounted.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddFlowLedgerDataProtection(
        this IServiceCollection services, IConfiguration configuration, string environmentName)
    {
        var keysPath = configuration["DataProtection:KeysPath"];
        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);

        if (string.IsNullOrWhiteSpace(keysPath))
        {
            if (!string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase)
                && configuration.GetValue("DataProtection:Required", false))
            {
                throw new InvalidOperationException(
                    "DataProtection:Required is true but DataProtection:KeysPath is not set. Mount a persistent volume and set " +
                    "DataProtection__KeysPath (see docs/self-hosting/production.md), or unset DataProtection__Required.");
            }

            return services;
        }

        Directory.CreateDirectory(keysPath);
        builder.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

        if (configuration["DataProtection:CertificatePath"] is { Length: > 0 } certPath)
        {
            var cert = X509CertificateLoader.LoadPkcs12FromFile(certPath, configuration["DataProtection:CertificatePassword"]);
            builder.ProtectKeysWithCertificate(cert);
        }

        return services;
    }
}
