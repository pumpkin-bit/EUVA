// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;

namespace EUVA.Core.Robots;

public sealed class RobotResult
{
    public Guid RobotId { get; init; }

    public RobotRole Role { get; init; }

    public bool HasFindings { get; init; }

    public string Summary { get; init; } = string.Empty;

    public IReadOnlyList<RobotAnnotation> Annotations { get; init; } = [];

    public DateTime CompletedAt { get; init; } = DateTime.UtcNow;

    public double Confidence { get; init; } = 1.0;

    public override string ToString() =>
        $"[Result robot={RobotId:N} role={Role} findings={HasFindings} annotations={Annotations.Count} confidence={Confidence:P0}]";
}

public sealed class RobotAnnotation
{
    public RobotRole Category { get; init; }

    public string Location { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string? ReplacementCode { get; init; }

    public override string ToString() =>
        $"[Annotation category={Category} loc={Location} hasReplacement={ReplacementCode is not null}]";
}
