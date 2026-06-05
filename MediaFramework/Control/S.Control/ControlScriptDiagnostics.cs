using Mond;
using Mond.Debugger;

namespace S.Control;

/// <summary>Raised by the script host/runtime for control-script faults (resolve, compile, invoke).</summary>
public sealed class ControlScriptException : Exception
{
    public ControlScriptException(string message)
        : base(message)
    {
    }
}

/// <summary>Which stage of a script's lifecycle produced a diagnostic.</summary>
public enum ControlScriptDiagnosticStage
{
    Compile,
    Runtime,
}

/// <summary>
/// Enforces a per-invocation Mond instruction budget by breaking once the limit is hit, so a runaway
/// script surfaces as a timeout instead of hanging the control runtime.
/// </summary>
internal sealed class InstructionLimitDebugger : MondDebugger
{
    private readonly int _instructionLimit;
    private int _count;

    public InstructionLimitDebugger(int instructionLimit)
    {
        _instructionLimit = instructionLimit;
    }

    protected override bool ShouldBreak(MondProgram program, int address) =>
        ++_count >= _instructionLimit;

    protected override MondDebugAction OnBreak(MondDebugContext context, int address) =>
        throw new MondRuntimeException("Script execution timed out.");
}
