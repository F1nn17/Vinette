using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Vinette.Api.Configuration;

/// <summary>Where sessions are kept (<c>Session:Store</c>).</summary>
public enum SessionStoreKind
{
    /// <summary>Process memory: one instance, sessions are lost on restart.</summary>
    InMemory,

    /// <summary>Redis: sessions are shared between instances and survive a restart.</summary>
    Redis,
}

/// <summary>Redis connection, used only when <c>Session:Store</c> is <see cref="SessionStoreKind.Redis"/>.</summary>
public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    /// <summary>StackExchange.Redis connection string, e.g. <c>redis:6379,password=...,ssl=true</c>. Contains secrets: never logged.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Prefix of every key Vinette writes, so one Redis can be shared with other applications.</summary>
    [Required(AllowEmptyStrings = false)]
    [StringLength(64)]
    public string KeyPrefix { get; set; } = "vinette:";
}

/// <summary>A connection string is mandatory as soon as sessions are stored in Redis.</summary>
public sealed class RedisOptionsValidator(IOptions<SessionLifetimeOptions> session) : IValidateOptions<RedisOptions>
{
    public ValidateOptionsResult Validate(string? name, RedisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return session.Value.Store == SessionStoreKind.Redis && string.IsNullOrWhiteSpace(options.ConnectionString)
            ? ValidateOptionsResult.Fail("Redis:ConnectionString is required when Session:Store is Redis.")
            : ValidateOptionsResult.Success;
    }
}
