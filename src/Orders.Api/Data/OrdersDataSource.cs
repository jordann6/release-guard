using Amazon;
using Amazon.RDS.Util;
using Npgsql;

namespace Orders.Api.Data;

internal static class OrdersDataSource
{
    public static NpgsqlDataSource Create(DatabaseOptions options)
    {
        var builder = new NpgsqlDataSourceBuilder(options.ConnectionString);

        // Kerberos is never used. Without this, Npgsql probes for GSSAPI encryption and the
        // chiseled image, which has no libgssapi, logs a load failure on every connection.
        builder.ConnectionStringBuilder.GssEncryptionMode = GssEncryptionMode.Disable;

        if (options.UseIamAuthentication)
        {
            if (string.IsNullOrWhiteSpace(options.Region))
            {
                throw new InvalidOperationException("Database:Region is required when IAM authentication is enabled.");
            }

            var csb = new NpgsqlConnectionStringBuilder(options.ConnectionString);
            if (!string.IsNullOrEmpty(csb.Password))
            {
                throw new InvalidOperationException("A static password must not be configured when IAM authentication is enabled.");
            }

            if (csb.SslMode is SslMode.Disable or SslMode.Allow or SslMode.Prefer)
            {
                throw new InvalidOperationException("IAM authentication requires SSL Mode=Require or stricter.");
            }

            var region = RegionEndpoint.GetBySystemName(options.Region);

            // The token is signed with the pod's IRSA credentials, so the application
            // holds no database secret at all (ADR-0004).
            builder.UsePeriodicPasswordProvider(
                async (settings, _) => await RDSAuthTokenGenerator
                    .GenerateAuthTokenAsync(region, settings.Host, settings.Port, settings.Username)
                    .ConfigureAwait(false),
                options.IamTokenRefreshInterval,
                failureRefreshInterval: TimeSpan.FromSeconds(5));
        }

        return builder.Build();
    }
}
