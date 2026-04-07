// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Threading;
using System.Threading.Tasks;

namespace EUVA.Core.Robots;

public abstract class RobotBase
{
    public Guid Id { get; }

    public RobotRole Role { get; }

    private volatile RobotStatus _status = RobotStatus.Initializing;

    public RobotStatus Status => _status;


    public event EventHandler<RobotStatusChangedEventArgs>? StatusChanged;

    protected RobotBase(RobotRole role)
    {
        Id   = Guid.NewGuid();
        Role = role;
    }

    protected void EnforceRole(RobotRole expected)
    {
        if (Role != expected)
            throw new InvalidOperationException(
                $"Robot {Id} (role={Role}) attempted to perform work for role={expected}. ");
    }

    protected void SetStatus(RobotStatus newStatus)
    {
        var previous = _status;
        _status = newStatus;
        StatusChanged?.Invoke(this, new RobotStatusChangedEventArgs(Id, Role, previous, newStatus));
    }

    public abstract Task OnHello(CancellationToken ct = default);

    public abstract Task<RobotResult> ExecuteAsync(string linearOutput, CancellationToken ct = default);

    public override string ToString() =>
        $"[Robot id={Id:N} role={Role} status={Status}]";
}
