// SPDX-License-Identifier: GPL-3.0-or-later

namespace EUVA.Core.Robots;

public enum RobotRole
{
    YaraScanner = 0,

    HexSignatureMatcher = 1,

    BinaryPatternAnalyzer = 2,

    ApiChainTracer = 3,

    MetadataExtractor = 4,

    IrLifterAgent = 5,

    ControlFlowAnalyzer = 6,

    DataFlowAnalyzer = 7,

    TypeInferenceAgent = 8,

    CallingConventionAgent = 9,

    StringExtractor = 10,

    EntropyAnalyzer = 11,

    ImportTracer = 12,

    ExportTracer = 13,

    SsaTransformer = 14,

    LoopDetectionAgent = 15,

    SwitchDetectionAgent = 16,

    StructReconstructor = 17,

    VTableDetectionAgent = 18,

    IdiomRecognizer = 19,

    DeadCodeAgent = 20,

    ConstantPropagationAgent = 21,

    ExpressionSimplifier = 22,

    SemanticGuesser = 23,

    FingerprintAgent = 24,

    PseudocodeEmitter = 25,

    NamingAgent = 26,

    XrefAnalyzer = 27,

    WeightChainValidator = 28,

    VerificationRelay = 29,
}
