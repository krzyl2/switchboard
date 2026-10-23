namespace Switchboard.Infrastructure.Mqtt;

/// <summary>Broker address and login for one connection attempt. Never printed with its password.</summary>
public sealed class MqttCredentials(string host, int port, bool useTls, string? username, string? password)
{
    public string Host { get; } = host;

    public int Port { get; } = port;

    public bool UseTls { get; } = useTls;

    public string? Username { get; } = username;

    public string? Password { get; } = password;

    public override string ToString() => $"{Host}:{Port}{(UseTls ? " (TLS)" : string.Empty)}";
}

public interface IMqttCredentialSource
{
    /// <summary>Why no broker can be reached at all (standalone without a host), or null.</summary>
    string? ConfigurationProblem { get; }

    /// <summary>Fetched fresh for every connection attempt so re-provisioned credentials are picked up.</summary>
    Task<MqttCredentials> GetAsync(CancellationToken cancellationToken);
}

public sealed class MqttCredentialsUnavailableException(string message) : Exception(message);
