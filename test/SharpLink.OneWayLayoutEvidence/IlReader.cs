using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace SharpLink.OneWayLayoutEvidence;

internal sealed record IlInstruction(int Offset, OpCode Opcode, int? Token, int SwitchTargets);

internal static class IlReader
{
    private static readonly IReadOnlyDictionary<ushort, OpCode> SOpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(static field => field.FieldType == typeof(OpCode))
        .Select(static field => (OpCode)field.GetValue(null)!)
        .ToDictionary(static opcode => unchecked((ushort)opcode.Value));

    internal static IReadOnlyList<IlInstruction> Read(MethodBase method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        var instructions = new List<IlInstruction>();
        for (var cursor = 0; cursor < bytes.Length;)
        {
            var offset = cursor;
            ushort key = bytes[cursor++];
            if (key == 0xfe)
                key = (ushort)(0xfe00 | bytes[cursor++]);
            var opcode = SOpCodes[key];
            var operand = opcode.OperandType;
            int? token = operand is OperandType.InlineMethod or OperandType.InlineField or
                OperandType.InlineType or OperandType.InlineTok ? BitConverter.ToInt32(bytes, cursor) : null;
            var targets = operand == OperandType.InlineSwitch ? BitConverter.ToInt32(bytes, cursor) : 0;
            cursor += operand switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => checked(4 + targets * 4),
                _ => 4
            };
            if (cursor > bytes.Length)
                throw new InvalidOperationException($"Malformed IL at {method}:{offset}.");
            instructions.Add(new IlInstruction(offset, opcode, token, targets));
        }
        return instructions;
    }

    internal static object Describe(MethodBase method)
    {
        var instructions = Read(method);
        return new
        {
            bytes = method.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0,
            instructions = instructions.Count,
            allBranchFlowInstructions = instructions.Count(static instruction =>
                instruction.Opcode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch),
            conditionalBranches = instructions.Count(static instruction =>
                instruction.Opcode.FlowControl == FlowControl.Cond_Branch && instruction.Opcode != OpCodes.Switch),
            switches = instructions.Count(static instruction => instruction.Opcode == OpCodes.Switch),
            switchTargets = instructions.Sum(static instruction => instruction.SwitchTargets),
            exceptionClauses = method.GetMethodBody()?.ExceptionHandlingClauses.Count ?? 0,
            hasClientStreamsReads = ResolveCalls(method).Count(static call => call.Callee.Name == "get_HasClientStreams")
        };
    }

    internal static IEnumerable<(IlInstruction Instruction, MethodBase Callee)> ResolveCalls(MethodBase method)
    {
        foreach (var instruction in Read(method))
        {
            if (instruction.Opcode.OperandType != OperandType.InlineMethod || instruction.Token is not int token)
                continue;
            MethodBase? callee;
            try
            {
                callee = method.Module.ResolveMethod(token, method.DeclaringType?.GetGenericArguments(),
                    method.IsGenericMethod ? method.GetGenericArguments() : null);
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (callee is not null)
                yield return (instruction, callee);
        }
    }
}
