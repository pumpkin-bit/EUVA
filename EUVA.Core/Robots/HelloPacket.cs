// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace EUVA.Core.Robots;

public readonly struct HelloPacket : IEquatable<HelloPacket>
{
    public Guid SenderId { get; init; }

    public RobotRole SenderRole { get; init; }

    public DateTime SentAt { get; init; }

    public string DisplayName { get; init; }

    public static HelloPacket Create(Guid senderId, RobotRole role) => new()
    {
        SenderId    = senderId,
        SenderRole  = role,
        SentAt      = DateTime.UtcNow,
        DisplayName = role.ToString(),
    };

    public bool Equals(HelloPacket other) => SenderId == other.SenderId;
    public override bool Equals(object? obj) => obj is HelloPacket hp && Equals(hp);
    public override int GetHashCode() => SenderId.GetHashCode();

    public static bool operator ==(HelloPacket left, HelloPacket right) => left.Equals(right);
    public static bool operator !=(HelloPacket left, HelloPacket right) => !left.Equals(right);

    public override string ToString() =>
        $"[Hello from {DisplayName} id={SenderId:N} at={SentAt:HH:mm:ss.fff}]";
}
