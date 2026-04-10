// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

namespace EUVA.Core.Robots;

public sealed class RobotNetwork : IRobotNetwork
{
    private readonly ConcurrentDictionary<Guid, RobotBase> _registered = new();
    private readonly ConcurrentBag<Guid> _helloSent = new();

    public IProcessAdmin Admin { get; }

    public RobotNetwork(IProcessAdmin admin)
    {
        Admin = admin ?? throw new ArgumentNullException(nameof(admin));
    }

    public bool AllRobotsReady =>
        _registered.Count > 0 && _helloSent.Count >= _registered.Count;

    public async Task BroadcastHelloAsync(HelloPacket packet)
    {
        _helloSent.Add(packet.SenderId);

        // debug
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(
            $"[NET]    {packet.DisplayName,-28} * " +
            $"delivering to {_registered.Count - 1} peers " +
            $"({_helloSent.Count}/{_registered.Count} ready)");
        Console.ForegroundColor = prev;

        var deliveries = _registered.Values
            .Where(r => r.Id != packet.SenderId)
            .Select(r => r.OnPeerHello(packet));

        await Task.WhenAll(deliveries).ConfigureAwait(false);
    }

    public Task ReceiveHelloAsync(HelloPacket packet) => Task.CompletedTask;

    public void Register(RobotBase robot)
    {
        _registered[robot.Id] = robot;
    }

    public async Task SendDirectAsync(Guid targetId, HelloPacket packet)
    {
        if (_registered.TryGetValue(targetId, out var target))
        {
            // debug
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkMagenta;
            Console.WriteLine(
                $"[DIRECT] {packet.DisplayName,-28} > {target.Role}");
            Console.ForegroundColor = prev;

            await target.OnPeerHello(packet).ConfigureAwait(false);
        }
    }

    public async Task<RobotDirectResponse> SendDirectCommandAsync(Guid targetId, RobotDirectCommand command)
    {
        if (_registered.TryGetValue(targetId, out var target))
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkMagenta;
            Console.WriteLine($"[CMD] {command.SenderRole,-18} >>> {target.Role} :: Action: {command.Action}");
            Console.ForegroundColor = prev;

            return await target.OnDirectCommandReceivedAsync(command).ConfigureAwait(false);
        }

        return new RobotDirectResponse { Success = false };
    }

    public async Task BroadcastStatusAsync(Guid senderId, RobotStatus status)
    {
        var deliveries = _registered.Values
            .Where(r => r.Id != senderId)
            .Select(r => r.OnPeerStatusChangeAsync(senderId, status));

        await Task.WhenAll(deliveries).ConfigureAwait(false);
    }

    public int RegisteredCount => _registered.Count;
    public int ReadyCount      => _helloSent.Count;

    public override string ToString() =>
        $"[RobotNetwork registered={RegisteredCount} ready={ReadyCount} allReady={AllRobotsReady}]";
}
