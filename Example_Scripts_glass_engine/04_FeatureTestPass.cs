// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Linq;
using EUVA.Core.Scripting;

public class UniversalFeatureTestPass : IDecompilerPass
{
    public PassStage Stage => PassStage.PreLifting;

    public void Execute(DecompilerContext context)
    {
        context.Log?.Invoke($"[Test] Start.. offset 0x{context.FunctionAddress:X}", "#A6E3A1");

        if (context.ReadMemoryOffset != null)
        {
            try
            {
                byte[] bytes = context.ReadMemoryOffset(context.FunctionAddress, 5);
                string hexBytes = BitConverter.ToString(bytes).Replace("-", " ");
                
                context.Log?.Invoke($"[Test] 5 hex bytes: {hexBytes}", "#89B4FA"); 
            }
            catch (Exception ex)
            {
                context.Log?.Invoke($"[Test] Error reading memory :< : {ex.Message}", "#F38BA8"); 
            }
        }

        if (context.UserComments != null)
        {
            context.UserComments[context.FunctionAddress] = "Glass Engine: Done!";
        }
    }
}

return new UniversalFeatureTestPass();
