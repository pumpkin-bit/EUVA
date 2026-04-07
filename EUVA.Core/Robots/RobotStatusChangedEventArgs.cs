// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace EUVA.Core.Robots;

public sealed class RobotStatusChangedEventArgs : EventArgs
{
    public Guid RobotId { get; }

    public RobotRole Role { get; }

    public RobotStatus Previous { get; }

    public RobotStatus Current { get; }

    public DateTime OccurredAt { get; } = DateTime.UtcNow;

    public RobotStatusChangedEventArgs(Guid robotId, RobotRole role, RobotStatus previous, RobotStatus current)
    {
        RobotId  = robotId;
        Role     = role;
        Previous = previous;
        Current  = current;
    }

    public override string ToString() =>
        $"[StatusChanged robot={RobotId:N} role={Role} {Previous}→{Current} at={OccurredAt:HH:mm:ss.fff}]";
}
