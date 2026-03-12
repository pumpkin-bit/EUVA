# Contributing to EUVA

First of all, thank you for showing interest in the project! EUVA is an open-source hex editor licensed under GPL v3, and contributions are more than welcome.

## How can you help?

### 1. Developing Plugins
The easiest way to contribute is by creating plugins. EUVA features a plugin system that allows you to extend the editor's functionality without modifying the core engine.

You can write plugins in two ways: an external DLL that needs to be placed in the Plugins folder, and a DLL where the class implements the IDetector interface. The program will scan the .dll in the folder, find the interface implementation using reflection, and pick it up without editing the core.
Alternatively, you can contribute to the project's source code (the easiest way): drop it into the Sample folder and embed the line `_detectorManager.RegisterDetector(new MyCustomDetector());` in `MainWindow.xaml.cs`, `DetectorManager.cs`.

* If you have a plugin idea, feel free to submit a Pull Request adding it to the official list.

### 2. Developing Decompiler Scripts 
Create unique scripts for various types of binary files. The decompiler API is actively developing. You can contribute to decompilation methods or expand the current scripting API to solve researchers' problems. Your scripts can be accepted into the decompiler and become part of it. The Scripts folder is intended for decompilation scripts. Simply move your .cs script there and see the result.
How to create scripts is described in [1.8 Glass Engine C# Scripting Integration](docs/Decompiler.md#18-glass-engine-c-scripting-integration).

### 2. Pull Requests
Found a bug or have a feature improvement for the core? 
1. Fork the repository.
2. Create a new branch for your feature or bugfix.
3. Submit a Pull Request with a clear description of your changes.

### 3. Reporting Issues
If you found a bug or have a suggestion:
* Check the existing Issues to see if it has already been reported.
* If not, open a new Issue with steps to reproduce the bug.

## Contact:
If you need help with the plugin system, reach out on : fnafi_sus

If you have questions specifically about plugin architecture or want to discuss a new feature idea before writing code, feel free to reach out:
Discord: fnafi_sus [link](https://discord.com/users/1193856523860975659)

GitHub Issues: Please use Issues for bug reports and technical errors. This helps other developers see the solution. Note: I’m most active during development sessions. If it’s a quick architectural question, Discord is best. If it's a bug, please open an Issue.

## License
By contributing to this project, you agree that your contributions will be licensed under the **GPL v3 License**.

Happy coding.