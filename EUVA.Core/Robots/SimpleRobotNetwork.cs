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

    public int RegisteredCount => _registered.Count;
    public int ReadyCount      => _helloSent.Count;

    public override string ToString() =>
        $"[RobotNetwork registered={RegisteredCount} ready={ReadyCount} allReady={AllRobotsReady}]";
}
