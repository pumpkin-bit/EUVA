// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Threading.Tasks;

namespace EUVA.Core.Robots;


public interface IRobotNetwork
{
    Task BroadcastHelloAsync(HelloPacket packet);
    Task ReceiveHelloAsync(HelloPacket packet);

    bool AllRobotsReady { get; }

    IProcessAdmin Admin { get; }

    Task<RobotDirectResponse> SendDirectCommandAsync(Guid targetId, RobotDirectCommand command);
}
