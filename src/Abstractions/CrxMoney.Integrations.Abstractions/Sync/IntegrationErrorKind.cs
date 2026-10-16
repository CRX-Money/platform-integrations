namespace CrxMoney.Integrations.Abstractions.Sync;

/// <summary>
/// Classifies why a sync failed, so the host can decide whether to retry, back off or ask the owner to act.
/// </summary>
public enum IntegrationErrorKind
{
    /// <summary>No error.</summary>
    None = 0,

    /// <summary>Credentials were rejected or have expired.</summary>
    Authentication = 1,

    /// <summary>The connection is misconfigured, for example a missing credential or setting.</summary>
    Configuration = 2,

    /// <summary>Credentials are valid but lack a required scope or permission.</summary>
    Permission = 3,

    /// <summary>The provider throttled the request.</summary>
    RateLimited = 4,

    /// <summary>The provider could not be reached.</summary>
    Network = 5,

    /// <summary>The provider returned an error or an unexpected response.</summary>
    Upstream = 6,

    /// <summary>Provider data failed validation and was not imported.</summary>
    Validation = 7,

    /// <summary>Any other failure.</summary>
    Unknown = 8
}
