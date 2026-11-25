using System.Reflection.Metadata;

namespace ZTX16.Toolchain;

/// <summary>One decoded IL instruction.</summary>
/// <param name="Offset">IL offset of the instruction.</param>
/// <param name="OpCode">The IL opcode.</param>
/// <param name="Operand">Integer/float bits, local index, metadata token or branch target (absolute offset).</param>
/// <param name="SwitchTargets">Absolute targets of a <c>switch</c>; empty otherwise.</param>
internal readonly record struct IlInstruction(int Offset, ILOpCode OpCode, long Operand, int[] SwitchTargets);

/// <summary>Decodes an IL method body into instructions with absolute branch targets.</summary>
internal static class IlDecoder
{
    public static List<IlInstruction> Decode(MethodBodyBlock body)
    {
        var result = new List<IlInstruction>();
        BlobReader reader = body.GetILReader();

        while (reader.RemainingBytes > 0)
        {
            int offset = reader.Offset;
            int first = reader.ReadByte();
            var op = first == 0xFE ? (ILOpCode)(0xFE00 | reader.ReadByte()) : (ILOpCode)first;

            if (op == ILOpCode.Switch)
            {
                int count = reader.ReadInt32();
                int[] relative = new int[count];
                for (int i = 0; i < count; i++)
                {
                    relative[i] = reader.ReadInt32();
                }

                int next = reader.Offset;
                result.Add(new IlInstruction(offset, op, 0, relative.Select(r => next + r).ToArray()));
                continue;
            }

            long operand;
            if (op.IsBranch())
            {
                int size = op.GetBranchOperandSize();
                int relative = size == 1 ? reader.ReadSByte() : reader.ReadInt32();
                operand = reader.Offset + relative;
            }
            else
            {
                operand = OperandSize(op) switch
                {
                    0 => 0,
                    1 => op == ILOpCode.Ldc_i4_s ? reader.ReadSByte() : reader.ReadByte(),
                    2 => reader.ReadUInt16(),
                    4 => op == ILOpCode.Ldc_r4 ? BitConverter.SingleToInt32Bits(reader.ReadSingle()) : reader.ReadInt32(),
                    _ => op == ILOpCode.Ldc_r8 ? BitConverter.DoubleToInt64Bits(reader.ReadDouble()) : reader.ReadInt64(),
                };
            }

            result.Add(new IlInstruction(offset, op, operand, []));
        }

        return result;
    }

    private static int OperandSize(ILOpCode op) => op switch
    {
        ILOpCode.Ldarg_s or ILOpCode.Ldarga_s or ILOpCode.Starg_s
            or ILOpCode.Ldloc_s or ILOpCode.Ldloca_s or ILOpCode.Stloc_s
            or ILOpCode.Ldc_i4_s or ILOpCode.Unaligned => 1,

        ILOpCode.Ldarg or ILOpCode.Ldarga or ILOpCode.Starg
            or ILOpCode.Ldloc or ILOpCode.Ldloca or ILOpCode.Stloc => 2,

        ILOpCode.Ldc_i4 or ILOpCode.Ldc_r4
            or ILOpCode.Jmp or ILOpCode.Call or ILOpCode.Calli or ILOpCode.Callvirt
            or ILOpCode.Cpobj or ILOpCode.Ldobj or ILOpCode.Ldstr or ILOpCode.Newobj
            or ILOpCode.Castclass or ILOpCode.Isinst or ILOpCode.Unbox or ILOpCode.Unbox_any
            or ILOpCode.Ldfld or ILOpCode.Ldflda or ILOpCode.Stfld
            or ILOpCode.Ldsfld or ILOpCode.Ldsflda or ILOpCode.Stsfld
            or ILOpCode.Stobj or ILOpCode.Box or ILOpCode.Newarr
            or ILOpCode.Ldelema or ILOpCode.Ldelem or ILOpCode.Stelem
            or ILOpCode.Refanyval or ILOpCode.Mkrefany or ILOpCode.Ldtoken
            or ILOpCode.Ldftn or ILOpCode.Ldvirtftn or ILOpCode.Initobj
            or ILOpCode.Constrained or ILOpCode.Sizeof => 4,

        ILOpCode.Ldc_i8 or ILOpCode.Ldc_r8 => 8,

        _ => 0,
    };
}
