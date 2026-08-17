namespace Argon.Features.Clustering;

using System.Reflection.Emit;

/// <summary>
/// Decodes a method body far enough to enumerate its call sites. Only the operand <i>sizes</i>
/// matter for correctness of the walk, so the full opcode table is built once from
/// <see cref="OpCodes"/> rather than hand-maintained.
/// </summary>
internal static class IlCallWalker
{
    private static readonly OpCode?[] single = new OpCode?[0x100];
    private static readonly OpCode?[] wideTable = new OpCode?[0x100];

    static IlCallWalker()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode op)
                continue;

            var value = unchecked((ushort)op.Value);
            if (op.Size == 1)
                single[value & 0xFF] = op;
            else
                wideTable[value & 0xFF] = op;
        }
    }

    /// <summary>
    /// Yields the metadata tokens of every call-shaped instruction in <paramref name="il"/>.
    /// <c>ldftn</c> and <c>ldvirtftn</c> are included because a method reached only through a
    /// delegate is still reachable code.
    /// </summary>
    public static IEnumerable<int> CallTokens(byte[] il)
    {
        var offset = 0;

        while (offset < il.Length)
        {
            OpCode op;

            var first = il[offset];
            if (first == 0xFE)
            {
                if (offset + 1 >= il.Length)
                    yield break;
                if (wideTable[il[offset + 1]] is not { } wide)
                    yield break;               // unknown prefix: operand size unknown, stop.
                op      = wide;
                offset += 2;
            }
            else
            {
                if (single[first] is not { } narrow)
                    yield break;
                op      = narrow;
                offset += 1;
            }

            var operand = OperandSize(op, il, offset);
            if (operand < 0 || offset + operand > il.Length)
                yield break;

            if (IsCall(op) && operand == 4)
                yield return BitConverter.ToInt32(il, offset);

            offset += operand;
        }
    }

    private static bool IsCall(OpCode op)
        => op == OpCodes.Call
        || op == OpCodes.Callvirt
        || op == OpCodes.Newobj
        || op == OpCodes.Ldftn
        || op == OpCodes.Ldvirtftn
        || op == OpCodes.Jmp;

    private static int OperandSize(OpCode op, byte[] il, int offset)
        => op.OperandType switch
        {
            OperandType.InlineNone                                          => 0,
            OperandType.ShortInlineBrTarget                                 => 1,
            OperandType.ShortInlineI                                        => 1,
            OperandType.ShortInlineVar                                      => 1,
            OperandType.InlineVar                                           => 2,
            OperandType.InlineBrTarget or OperandType.InlineField           => 4,
            OperandType.InlineI or OperandType.InlineMethod                 => 4,
            OperandType.InlineSig or OperandType.InlineString               => 4,
            OperandType.InlineTok or OperandType.InlineType                 => 4,
            OperandType.ShortInlineR                                        => 4,
            OperandType.InlineI8 or OperandType.InlineR                     => 8,
            OperandType.InlineSwitch                                        => SwitchSize(il, offset),
            _                                                               => -1
        };

    private static int SwitchSize(byte[] il, int offset)
    {
        if (offset + 4 > il.Length)
            return -1;
        var count = BitConverter.ToUInt32(il, offset);
        var total = 4L + count * 4L;
        return total > int.MaxValue ? -1 : (int)total;
    }
}
