// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EUVA.Core.Robots;

public sealed class RobotVerifier
{
    private readonly byte[] _secretKey;
    private int _verifiedCount = 0;
    private readonly TaskCompletionSource _releaseBarrier = new();

    public RobotVerifier()
    {
        _secretKey = new byte[32];
        RandomNumberGenerator.Fill(_secretKey);
    }

    public async Task<byte[]> RequestVerificationKeyAsync(Guid robotId, RobotRole role, int count, string summary)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine($"[VERIFIER] Request from {role}. Validating annotations ({count})...");
        Console.ForegroundColor = prev;
        await Task.Delay(10); 

        byte[] key = GenerateMac(robotId, count, summary);

        int currentCount = Interlocked.Increment(ref _verifiedCount);

        var waitColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"[VERIFIER] {role} signature verified. Waiting at barrier ({currentCount}/7)...");
        Console.ForegroundColor = waitColor;

        if (currentCount == 7)
        {
            var releaseColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[VERIFIER] 7 robots verified. Releasing barrier! Proceed to apply.");
            Console.ForegroundColor = releaseColor;
            
            _releaseBarrier.TrySetResult();
        }

        await _releaseBarrier.Task;

        return key;
    }

    public bool ValidateKey(Guid robotId, int count, string summary, byte[]? providedKey)
    {
        if (providedKey == null || providedKey.Length == 0) return false;
        byte[] expected = GenerateMac(robotId, count, summary);
        return CryptographicOperations.FixedTimeEquals(expected, providedKey);
    }

    private byte[] GenerateMac(Guid robotId, int count, string summary)
    {
        using var hmac = new HMACSHA256(_secretKey);
        string payload = $"{robotId}_{count}_{summary}";
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
    }
}
