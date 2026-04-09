// SPDX-License-Identifier: GPL-3.0-or-later

namespace EUVA.Core.Scripting;

public enum PassStage
{
    /// <summary>
    /// Executes before data are lifted to IR. :3
    /// Good for patching raw bytes, removing garbage instructions, and hooking before the engine parses them, and etc.
    /// </summary>
    PreLifting,

    /// <summary>
    /// Executes right after the control flow graph CFG has been lifted to intermediate representation IR,
    /// but before any Static Single Assignment SSA form construction or optimizations.
    /// Good for Architecture-specific lifting fixes, CFG modifications dead block removal.
    /// </summary>
    PreSsa,

    /// <summary>
    /// Executes after SSA form has been built and basic SSA-level optimizations Constant/Copy Propagation are run.
    /// Good for SSA-level custom optimizations fixing dominator tree issues, advanced CFG modifications.
    /// </summary>
    PostSsa,

    /// <summary>
    /// Executes before Type Inference takes place.
    /// Good for Forcing specific types onto variables before the engine tries to infer them automatically.
    /// </summary>
    PreTypeInference,

    /// <summary>
    /// Executes after Type Inference has settled.
    /// Good for Overriding incorrectly inferred types restructuring types based on custom heuristics.
    /// </summary>
    PostTypeInference,

    /// <summary>
    /// Executes after the AST has been built and structured into High-Level constructs If/While/For.
    /// Good for Custom AST folding rules simplifying complex bitwise math into cleaner expressions.
    /// </summary>
    PostStructuring,
}
