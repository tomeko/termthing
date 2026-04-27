using Renci.SshNet;
using System.IO;

namespace TermThing.Ssh;

/// <summary>
/// Builds a <see cref="ConnectionInfo"/> from discrete SSH parameters, applying the
/// same key-exchange / cipher tuning that was previously inlined in
/// <c>SshSessionLauncher.BuildConnectionInfo</c>.
/// </summary>
internal static class SshConnectionInfoFactory
{
    /// <summary>
    /// Creates a <see cref="ConnectionInfo"/> for the given host/port/user/auth details.
    /// </summary>
    /// <param name="host">Hostname or IP to connect to (may be 127.0.0.1 for a local forward).</param>
    /// <param name="port">TCP port.</param>
    /// <param name="username">SSH username.</param>
    /// <param name="keyFilePath">Optional path to a PEM private key file.</param>
    /// <param name="keyPassphrase">Optional passphrase for the private key.</param>
    /// <param name="password">Optional password (added as <see cref="PasswordAuthenticationMethod"/>).</param>
    public static ConnectionInfo Build(
        string  host,
        int     port,
        string  username,
        string? keyFilePath      = null,
        string? keyPassphrase    = null,
        string? password         = null)
    {
        var authMethods = new List<AuthenticationMethod>();

        if (!string.IsNullOrWhiteSpace(keyFilePath))
        {
            if (!File.Exists(keyFilePath))
                throw new FileNotFoundException("Private key file not found.", keyFilePath);

            PrivateKeyFile keyFile = string.IsNullOrEmpty(keyPassphrase)
                ? new PrivateKeyFile(keyFilePath)
                : new PrivateKeyFile(keyFilePath, keyPassphrase);
            authMethods.Add(new PrivateKeyAuthenticationMethod(username, keyFile));
        }

        if (!string.IsNullOrEmpty(password))
            authMethods.Add(new PasswordAuthenticationMethod(username, password));

        if (authMethods.Count == 0)
            authMethods.Add(new NoneAuthenticationMethod(username));

        var info = new ConnectionInfo(host, port, username, [.. authMethods]);
        TuneAlgorithms(info);
        return info;
    }

    /// <summary>
    /// Remove / deprioritise slow key-exchange algorithms so that connect latency
    /// is dominated by the network round-trip rather than managed-code crypto.
    /// </summary>
    internal static void TuneAlgorithms(ConnectionInfo info)
    {
        // diffie-hellman-group18-sha512 uses 8192-bit DH in managed .NET code —
        // can add 2-3 s on its own.  PQC algorithms are also expensive; move them
        // to the end so a server that supports curve25519 picks it immediately.
        info.KeyExchangeAlgorithms.Remove("diffie-hellman-group18-sha512");
        foreach (var pqc in new[]
        {
            "mlkem768x25519-sha256",
            "sntrup761x25519-sha512",
            "sntrup761x25519-sha512@openssh.com",
        })
        {
            if (info.KeyExchangeAlgorithms.TryGetValue(pqc, out var factory))
            {
                info.KeyExchangeAlgorithms.Remove(pqc);
                info.KeyExchangeAlgorithms.Add(pqc, factory);
            }
        }
    }
}
