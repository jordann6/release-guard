using System.ComponentModel.DataAnnotations;

namespace Orders.Api.Data;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// Host, port, database and username. In AWS it carries no password: with
    /// <see cref="UseIamAuthentication"/> the password is a short-lived RDS IAM token.
    /// </summary>
    [Required]
    public string ConnectionString { get; set; } = string.Empty;

    public bool UseIamAuthentication { get; set; }

    /// <summary>AWS region for IAM token signing. Required when IAM authentication is on.</summary>
    public string? Region { get; set; }

    /// <summary>RDS IAM tokens are valid for 15 minutes; refresh well before that.</summary>
    public TimeSpan IamTokenRefreshInterval { get; set; } = TimeSpan.FromMinutes(10);
}
