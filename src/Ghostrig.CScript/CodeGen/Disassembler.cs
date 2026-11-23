using System.Globalization;
using System.Text;
using Ghostrig.CScript.Runtime;

namespace Ghostrig.CScript.CodeGen;

/// <summary>Bytecode listings with operands resolved to names (Doc 7 §13.15 <c>Compiler.Disassemble</c>).</summary>
public static class Disassembler
{
    public static string Disassemble(CodeUnit unit, ProgramImage image)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(image);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"; {unit.Name} ({unit.Flavor}, args {unit.ArgumentCount}, locals {unit.LocalCount}) {unit.SourcePath}\n");
        var lastLine = -1;
        for (var ip = 0; ip < unit.Code.Length; ip++)
        {
            var line = unit.LineAt(ip);
            if (line != lastLine && line > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"      ; line {line}\n");
                lastLine = line;
            }

            var instruction = unit.Code[ip];
            var text = string.Create(CultureInfo.InvariantCulture, $"{ip,4}  {instruction.Op,-15} {Operand(instruction, unit, image)}");
            sb.Append(text.TrimEnd()).Append('\n');
        }

        foreach (var handler in unit.Handlers)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"      ; try [{handler.Start}, {handler.End}) -> {handler.Handler} catch {handler.Test?.Display ?? "all"} into local {handler.ErrorLocal}\n");
        }

        return sb.ToString();
    }

    private static string Operand(Instruction instruction, CodeUnit unit, ProgramImage image) => instruction.Op switch
    {
        OpCode.PushConst => Constant(unit.Constants[instruction.A]),
        OpCode.NewArray => $"default {Constant(unit.Constants[instruction.A])}",
        OpCode.Call => $"{image.Callables[instruction.A].Name} argc={instruction.B}",
        OpCode.CallVirtual => $"slot {instruction.A} argc={instruction.B}",
        OpCode.CallFunction => $"argc={instruction.B}",
        OpCode.MakeClosure => $"{image.Callables[instruction.A].Name} captures={instruction.B}",
        OpCode.MakeMethodRef => instruction.B == 1 ? $"slot {instruction.A}" : image.Callables[instruction.A].Name,
        OpCode.NewObject or OpCode.ToEnum => image.Classes[instruction.A].Name,
        OpCode.CastCheck or OpCode.IsType => image.TypeTests[instruction.A].Display,
        OpCode.FormatValue => instruction.A < 0 ? string.Empty : Constant(unit.Constants[instruction.A]),
        OpCode.ThrowRuntime => $"{Constant(unit.Constants[instruction.A])} {Constant(unit.Constants[instruction.B])}",
        OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.JumpIfNull or OpCode.JumpIfNotNull => $"-> {instruction.A}",
        OpCode.LoadLocal or OpCode.StoreLocal or OpCode.MakeCell or OpCode.LoadCell or OpCode.StoreCell or OpCode.LoadCellRef
            or OpCode.Rethrow => $"local {instruction.A}",
        OpCode.LoadCaptured or OpCode.StoreCaptured or OpCode.LoadCapturedRef => $"capture {instruction.A}",
        OpCode.LoadGlobal or OpCode.StoreGlobal => $"global {instruction.A}",
        OpCode.LoadField or OpCode.StoreField => $"field {instruction.A}",
        OpCode.NewArrayInit or OpCode.NewTuple => $"count {instruction.A}",
        OpCode.LoadTupleItem => $"item {instruction.A}",
        OpCode.Return => instruction.A == 1 ? "value" : string.Empty,
        _ => string.Empty,
    };

    private static string Constant(Value value) => value.Object is string s ? $"\"{ValueText.Escape(s)}\"" : value.ToString();
}
