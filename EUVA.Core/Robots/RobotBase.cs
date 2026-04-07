// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace EUVA.Core.Robots;

public abstract class RobotBase
{
    public Guid       Id   { get; }
    public RobotRole  Role { get; }


    private readonly IRobotNetwork _network;

    private readonly ConcurrentDictionary<Guid, RobotRole> _peers = new();

    public RobotRole? GetPeerRole(Guid peerId) =>
        _peers.TryGetValue(peerId, out var role) ? role : null;

    public IReadOnlyDictionary<Guid, RobotRole> KnownPeers => _peers;


    private volatile RobotStatus _status = RobotStatus.Initializing;
    public RobotStatus Status => _status;

    public event EventHandler<RobotStatusChangedEventArgs>? StatusChanged;

   
    protected RobotBase(RobotRole role, IRobotNetwork network)
    {
        Id       = Guid.NewGuid();
        Role     = role;
        _network = network ?? throw new ArgumentNullException(nameof(network));
    }

    
    protected void EnforceRole(RobotRole expected)
    {
        if (Role != expected)
            throw new InvalidOperationException(
                $"Robot {Id} (role={Role}) attempted to perform work for role={expected}. " +
                $"Role boundaries are strict, a robot must not do another robot's job.");
    }

 
    protected void SetStatus(RobotStatus newStatus)
    {
        var previous = _status;
        _status = newStatus;

        // debug
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = newStatus switch
        {
            RobotStatus.Ready                 => ConsoleColor.Green,
            RobotStatus.Working               => ConsoleColor.Yellow,
            RobotStatus.Done                  => ConsoleColor.Blue,
            RobotStatus.Verified              => ConsoleColor.Magenta,
            RobotStatus.Faulted               => ConsoleColor.Red,
            RobotStatus.AwaitingAdminResponse => ConsoleColor.DarkYellow,
            _                                 => ConsoleColor.Gray,
        };
        Console.WriteLine($"[STATUS] {Role,-28} ? {previous} - {newStatus}");
        Console.ForegroundColor = prev;

        StatusChanged?.Invoke(this, new RobotStatusChangedEventArgs(Id, Role, previous, newStatus));
    }

    public async Task OnHello(CancellationToken ct = default)
    {
        if (_status != RobotStatus.Initializing)
            throw new InvalidOperationException(
                $"Robot {Id} (role={Role}) called OnHello() in invalid state '{_status}'. " +
                $"OnHello() must be called exactly once while status is Initializing.");

        ct.ThrowIfCancellationRequested();

        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[ROBOT]  {Role,-28} | OnHello() - broadcasting...");
        Console.ForegroundColor = prev;

        var packet = HelloPacket.Create(Id, Role);
        await _network.BroadcastHelloAsync(packet).ConfigureAwait(false);
        SetStatus(RobotStatus.Ready);
    }

    public virtual Task OnPeerHello(HelloPacket packet)
    {
        _peers[packet.SenderId] = packet.SenderRole;
        return Task.CompletedTask;
    }

    protected async Task SendDirectToPeer(Guid targetId)
    {
        if (_network is RobotNetwork net)
        {
            var packet = HelloPacket.Create(Id, Role);
            await net.SendDirectAsync(targetId, packet).ConfigureAwait(false);
        }
    }

    public abstract Task<RobotResult> ExecuteAsync(string linearOutput, CancellationToken ct = default);

    public override string ToString() =>
        $"[Robot id={Id:N} role={Role} status={Status} peers={_peers.Count}]";
}
