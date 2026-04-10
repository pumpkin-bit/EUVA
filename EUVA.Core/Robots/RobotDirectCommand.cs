// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace EUVA.Core.Robots;

public sealed class RobotDirectCommand
{
    public Guid SenderId { get; init; }
    public RobotRole SenderRole { get; init; }
    public string Action { get; init; } = string.Empty;
    public byte[]? Payload { get; init; }
}

public sealed class RobotDirectResponse
{
    public bool Success { get; init; }
    public byte[]? Data { get; init; }
}
