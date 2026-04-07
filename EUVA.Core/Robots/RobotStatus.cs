// SPDX-License-Identifier: GPL-3.0-or-later

namespace EUVA.Core.Robots;

public enum RobotStatus
{
    Initializing,

    Ready,

    Working,

    AwaitingAdminResponse,

    Done,

    Verified,

    Faulted,
}
