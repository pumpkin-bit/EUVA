// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EUVA.Core.Robots;

public sealed class ProcessAdmin
{
    private readonly RobotNetwork _network;
    private readonly List<RobotBase> _robots;

    public ProcessAdmin()
    {
        _network = new RobotNetwork();
        _robots  = new List<RobotBase>(30);
    }

    public void InitializeFleet()
    {
        var roles = Enum.GetValues<RobotRole>();
        
        foreach (var role in roles)
        {
            var robot = new DecompilerRobot(role, _network);
            _network.Register(robot);
            _robots.Add(robot);
        }

        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"[ADMIN]  Fleet initialized. Total robots: {_robots.Count}");
        Console.ForegroundColor = prev;
    }

    public async Task<List<RobotAnnotation>> RunPipelineAsync(string linearOutput, CancellationToken ct = default)
    {
        
        await InvokeHelloPhaseAsync(ct).ConfigureAwait(false);

        var results = await KickAllSimultaneouslyAsync(linearOutput, ct).ConfigureAwait(false);

       
        var allAnnotations = new List<RobotAnnotation>();
        foreach (var res in results)
        {
            if (res.HasFindings)
            {
                allAnnotations.AddRange(res.Annotations);
            }
        }

        return allAnnotations;
    }

    private async Task InvokeHelloPhaseAsync(CancellationToken ct)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("[ADMIN] Initiating Hello Phase. Waiting for all robots to report...");
        Console.ForegroundColor = prev;

        var helloTasks = _robots.Select(r => r.OnHello(ct));
        await Task.WhenAll(helloTasks).ConfigureAwait(false);

        if (!_network.AllRobotsReady)
        {
            throw new InvalidOperationException(
                "Hello phase completed but network reports not all robots are ready.");
        }

        prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("[ADMIN] Hello phase complete. All robots are ready.");
        Console.ForegroundColor = prev;
    }

    private async Task<RobotResult[]> KickAllSimultaneouslyAsync(string linearOutput, CancellationToken ct)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("[ADMIN] Kicking all 30 robots simultaneously now!");
        Console.ForegroundColor = prev;

        var executeTasks = _robots.Select(r => r.ExecuteAsync(linearOutput, ct));
        
        var results = await Task.WhenAll(executeTasks).ConfigureAwait(false);

        prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[ADMIN] All {results.Length} robots have finished their work.");
        Console.ForegroundColor = prev;

        return results;
    }
}
