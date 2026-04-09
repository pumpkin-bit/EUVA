// SPDX-License-Identifier: GPL-3.0-or-later
// Please note: This plugin is a template. If something isn't working for you, isn't searching correctly, or isn't working correctly, please check whether your binary is compatible with this plugin.
// If not, you can write your own plugins! :3

using System;
using System.Linq;
using EUVA.Core.Scripting;

public class AdvancedApiTestPass : IDecompilerPass
{
    public PassStage Stage => PassStage.PreLifting;

    public void Execute(DecompilerContext context)
    {
        string currentSectionName = "Unknown";
        if (context.ExecutableSections != null)
        {
            foreach (var sec in context.ExecutableSections)
            {
                if (context.FunctionAddress >= sec.Start && context.FunctionAddress < sec.End)
                {
                    currentSectionName = sec.Name;
                    break;
                }
            }
        }
        
        context.Log?.Invoke($"[Test] Function 0x{context.FunctionAddress:X} located in section: {currentSectionName}", "#F5E0DC");

        if (context.ReadMemoryOffset != null)
        {
            try
            {
                byte[] originalBytes = context.ReadMemoryOffset(context.FunctionAddress, 64);
                
                context.OverrideFunctionBytes = originalBytes;
                
                context.Log?.Invoke($"[Test] Function buffer has been intercepted! The engine is working with your memory.", "#94E2D5"); 
                
                if (context.UserComments != null)
                {
                    context.UserComments[context.FunctionAddress] = $"[Section: {currentSectionName}] Code rendered from OverrideFunctionBytes";
                }
            }
            catch (Exception ex)
            {
                context.Log?.Invoke($"[Test] Error working with memory: {ex.Message}", "#F38BA8");
            }
        }
    }
}

return new AdvancedApiTestPass();
