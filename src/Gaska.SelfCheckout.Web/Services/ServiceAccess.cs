using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Gaska.SelfCheckout.Web.Services;

/// <summary>One-time, short-lived access tokens for the service panel (issued after the PIN is entered on the kiosk screen).</summary>
public static class ServiceAccess
{
    private static readonly ConcurrentDictionary<string, DateTime> Tokens = new();

    public static string Issue()
    {
        foreach (var (key, expires) in Tokens)
            if (expires < DateTime.UtcNow)
                Tokens.TryRemove(key, out _);

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Tokens[token] = DateTime.UtcNow.AddSeconds(60);
        return token;
    }

    public static bool Consume(string? token) =>
        token is not null && Tokens.TryRemove(token, out var expires) && expires >= DateTime.UtcNow;
}
