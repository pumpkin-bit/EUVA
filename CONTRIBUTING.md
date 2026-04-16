# Contributing to EUVA

EUVA is an open platform. We accept all PRs that bring value to the community. No gatekeeping, no bureaucracy. If it makes the tool better it gets merged.

## What counts as a contribution?

Everything. Seriously.

- Fixed a typo in docs? PR.
- Wrote a new regex rule for the nonlinear decompiler? PR.
- Built a whole new analysis module? PR.
- Added support for a new binary format? PR.
- Created a `.euv` patching script? PR.
- Improved the UI? PR.
- Found and fixed a bug? PR.
- Have you added new signatures to database? PR.

We don't care if it's 1 line or 1000 lines. If it helps someone in the community we want it.

## Ways to contribute examples

### 1. Decompiler Rules & Robots
The nonlinear decompiler is built on open regex rules and micro-algorithm robots. You can:
- Add new regex patterns to improve code readability
- Create new robots for `ProcessAdmin.cs` to handle specific code patterns
- No C# experience needed for regex rules just text patterns

### 2. Decompiler Scripts
Write C# scripts that hook into the decompiler pipeline. Your script becomes part of the engine. Drop a `.cs` file into the Scripts folder and it just works.
How to create scripts: [Glass Engine Scripting](docs/Decompiler.md#4-glass-engine-c-scripting-integration)

### 3. Plugins
EUVA has a plugin system via `IDetector` interface. Write a DLL, drop it in the Plugins folder the program picks it up via reflection. No core modifications needed.

### 4. Patching Scripts (.euv)
Share your `.euv` scripts via [EUVA-Library](https://github.com/pumpkin-bit/EUVA-Library). Create a Gist, submit a PR with a link and a short explanation.

### 5. MCP Server Tools
Extend the MCP server with new tools for AI integration. The server is plain Python easy to hack on.

### 6. Anything else
Documentation, tests, UI improvements, performance optimizations, new file format parsers all welcome.

## How to submit

1. Fork the repo.
2. Create a branch.
3. Make your changes.
4. Submit a PR with a clear description.

That's it. No templates, no committees, no week-long review cycles.

## Reporting Issues

- Check existing Issues first.
- If it's new open an Issue with steps to reproduce.

## Ethics

We are researchers. All contributions must be for educational and security research purposes. No piracy, no malware, no license bypasses.

## Contact

- Discord: fnafi_sus [link](https://discord.com/users/1193856523860975659)
- GitHub Issues for bugs and technical discussions

## License

By contributing, you agree that your work will be licensed under **GPL v3**.

---

EUVA is built by the community, for the community. Every PR matters.