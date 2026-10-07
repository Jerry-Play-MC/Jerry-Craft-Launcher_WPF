using System.Runtime.InteropServices;

namespace MyPreload;

/// <summary>
/// 轻量 x64 内联钩子：在函数入口写入 14 字节绝对间接跳转，同时把原始前导指令
/// 搬运到 trampoline 并接回原函数，从而保留"调用原始实现"的能力。
/// </summary>
internal static unsafe class InlineHook
{
    private const uint MemCommit = 0x1000;
    private const uint MemRelease = 0x8000;
    private const uint PageExecuteReadWrite = 0x40;

    private const int JumpSize = 14;
    private const int MinPatchLength = JumpSize;
    private const int MaxDecodeBytes = 32;

    public static bool TryCreate(nint target, nint detour, out nint trampoline)
    {
        trampoline = default;
        if (target == 0 || detour == 0)
            return false;

        byte* trunk = BuildTrampoline((byte*)target, out int patchLength);
        if (trunk is null)
            return false;

        if (!PatchJump(target, detour))
        {
            NativeMethods.VirtualFree((nint)trunk, 0, MemRelease);
            return false;
        }

        trampoline = (nint)trunk;
        return true;
    }

    private static byte* BuildTrampoline(byte* target, out int patchLength)
    {
        patchLength = 0;

        while (patchLength < MinPatchLength)
        {
            if (!X64Decoder.TryDecode(target + patchLength, MaxDecodeBytes - patchLength, out int length, out _))
                return null;
            if (length <= 0 || patchLength + length > MaxDecodeBytes)
                return null;
            patchLength += length;
        }

        byte* trunk = (byte*)NativeMethods.VirtualAlloc(nint.Zero, (nuint)(patchLength + JumpSize),
            MemCommit, PageExecuteReadWrite);
        if (trunk is null)
            return null;

        for (int i = 0; i < patchLength; i++)
            trunk[i] = target[i];

        RelocateRipRelative(trunk, target, patchLength);
        PatchJump((nint)(trunk + patchLength), (nint)(target + patchLength));
        return trunk;
    }

    private static void RelocateRipRelative(byte* trunk, byte* target, int length)
    {
        int cursor = 0;
        while (cursor < length)
        {
            if (!X64Decoder.TryDecode(target + cursor, length - cursor, out int instrLength, out int ripOffset))
                break;
            if (ripOffset >= 0)
            {
                long displacement = *(int*)(target + cursor + ripOffset) + ((long)target - (long)trunk);
                *(int*)(trunk + cursor + ripOffset) = (int)displacement;
            }
            cursor += instrLength;
        }
    }

    private static bool PatchJump(nint location, nint destination)
    {
        if (!NativeMethods.VirtualProtect(location, JumpSize, PageExecuteReadWrite, out uint oldProtect))
            return false;

        byte* code = (byte*)location;
        code[0] = 0xFF;
        code[1] = 0x25;
        *(int*)(code + 2) = 0;
        *(long*)(code + 6) = destination;

        NativeMethods.VirtualProtect(location, JumpSize, oldProtect, out _);
        return true;
    }
}

internal static unsafe class X64Decoder
{
    public static bool TryDecode(byte* code, int maxBytes, out int length, out int ripDispOffset)
    {
        length = 0;
        ripDispOffset = -1;

        int pos = 0;
        bool operandSize66 = false;

        // 前缀
        while (pos < maxBytes)
        {
            byte prefix = code[pos];
            if (prefix == 0x66)
                operandSize66 = true;
            else if (prefix is 0x67 or 0xF0 or 0xF2 or 0xF3 or 0x2E or 0x36 or 0x3E or 0x26 or 0x64 or 0x65)
            {
            }
            else
                break;
            if (++pos > 14)
                return false;
        }

        if (pos >= maxBytes)
            return false;

        // REX
        bool rexW = false;
        byte op = code[pos];
        if ((op & 0xF0) == 0x40)
        {
            rexW = (op & 0x08) != 0;
            if (++pos >= maxBytes)
                return false;
            op = code[pos];
        }

        if (op is 0xC4 or 0xC5)
            return false;

        int instrStart = pos;
        int len;

        if (op == 0x0F)
        {
            if (++pos >= maxBytes)
                return false;
            byte op2 = code[pos];

            if (op2 is 0x05 or 0x06 or 0x07 or 0x08 or 0x09 or 0x0B or 0x0E or
                0x30 or 0x31 or 0x32 or 0x33 or 0x34 or 0x35 or 0x37 or 0x77 or
                0xA2 or 0xAA or 0xB9)
            {
                len = 2;
            }
            else if (op2 is >= 0x80 and <= 0x8F)
            {
                len = 6;
            }
            else
            {
                if (!DecodeModRM(code, pos + 1, maxBytes, out int afterModrm, out ripDispOffset))
                    return false;
                bool hasImm8 = op2 is 0x70 or 0x71 or 0x72 or 0x73 or 0xA4 or 0xAC or 0xBA or 0xC4 or 0xC5 or 0xC6;
                len = afterModrm - instrStart + (hasImm8 ? 1 : 0);
            }
        }
        else
        {
            switch (op)
            {
                // 无操作数、1 字节
                case 0x90 or 0x98 or 0x99 or 0x9B or 0x9C or 0x9D or 0x9E or 0x9F or
                     0xC3 or 0xC9 or 0xCB or 0xCC or 0xCE or 0xCF or
                     0xF4 or 0xF5 or 0xF8 or 0xF9 or 0xFA or 0xFB or 0xFC or 0xFD:
                    len = 1;
                    break;

                // PUSH/POP r64 (0x50-0x5F) 无操作数
                case >= 0x50 and <= 0x5F:
                    len = 1;
                    break;

                // 1 字节立即数 / rel8
                case 0x6A or 0xCD or 0xD4 or 0xD5 or 0xE0 or 0xE1 or 0xE2 or 0xE3 or
                     >= 0x70 and <= 0x7F or 0xEB:
                    len = 2;
                    break;

                // 4 字节立即数 / rel32
                case 0x68 or 0xE8 or 0xE9:
                    len = 5;
                    break;

                // RET imm16
                case 0xC2 or 0xCA:
                    len = 3;
                    break;

                // ENTER
                case 0xC8:
                    len = 4;
                    break;

                // MOV r8, imm8
                case >= 0xB0 and <= 0xB7:
                    len = 2;
                    break;

                // MOV r, imm
                case >= 0xB8 and <= 0xBF:
                    len = rexW ? 9 : (operandSize66 ? 3 : 5);
                    break;

                // MOV moffs
                case >= 0xA0 and <= 0xA3:
                    len = 9;
                    break;

                // 隐式操作数
                case >= 0xA4 and <= 0xA7 or >= 0xAC and <= 0xAF or >= 0xEC and <= 0xEF:
                    len = 1;
                    break;

                // TEST AL/AX/EAX/RAX, imm
                case 0xA8 or 0xA9:
                    len = operandSize66 ? 3 : (rexW ? 5 : 3);
                    break;

                // IN/OUT imm8
                case >= 0xE4 and <= 0xE7:
                    len = 2;
                    break;

                default:
                    {
                        if (!RequiresModRM(op))
                            return false;

                        if (!DecodeModRM(code, instrStart + 1, maxBytes, out int afterModrm, out ripDispOffset))
                            return false;

                        int imm = op switch
                        {
                            // === 1 字节立即数 ===
                            0x6A => 1,   // PUSH imm8
                            0x6B => 1,   // IMUL r, r/m, imm8
                            0x80 => 1,   // Group 1 Eb, Ib
                            0x82 => 1,   // Group 1 Eb, Ib (alias)
                            0x83 => 1,   // ★ Group 1 Ev, Ib  ← sub rsp, 20h 之类
                            0xC0 => 1,   // Shift Group 2 Eb, Ib
                            0xC1 => 1,   // Shift Group 2 Ev, Ib
                            0xC6 => 1,   // MOV Eb, Ib

                            // === 4 字节立即数 ===
                            0x81 => 4,   // Group 1 Ev, Iz
                            0x69 => 4,   // IMUL r, r/m, imm32
                            0xC7 => 4,   // MOV Ev, Iz
                            0xF7 => 4,   // Group 3 Ev, Iz

                            _ => 0,
                        };
                        len = afterModrm - instrStart + imm;
                        break;
                    }
            }
        }

        if (len <= 0 || instrStart + len > maxBytes)
            return false;

        length = instrStart + len;   // ★ 加上前缀字节
        return true;
    }

    private static bool RequiresModRM(byte op)
    {
        if (op <= 0x05) return true;
        if (op is >= 0x08 and <= 0x0D or >= 0x10 and <= 0x15 or >= 0x18 and <= 0x1D) return true;
        if (op is >= 0x20 and <= 0x25 or >= 0x28 and <= 0x2D or >= 0x30 and <= 0x35 or >= 0x38 and <= 0x3D) return true;
        if (op is 0x63 or 0x69 or 0x6B) return true;

        // ★ 新增：Group 1 (ADD/OR/ADC/SBB/AND/SUB/XOR/CMP Ev, Ib/Iz)
        if (op is 0x80 or 0x81 or 0x82 or 0x83) return true;

        if (op is >= 0x84 and <= 0x8F) return true;
        if (op is 0xC0 or 0xC1 or 0xC6 or 0xC7) return true;
        if (op is >= 0xD0 and <= 0xD3 or >= 0xD8 and <= 0xDF) return true;
        if (op is 0xF6 or 0xF7 or 0xFE or 0xFF) return true;
        return false;
    }

    private static bool DecodeModRM(byte* code, int modrmIndex, int maxBytes, out int afterEnd, out int ripDispOffset)
    {
        afterEnd = 0;
        ripDispOffset = -1;

        if (modrmIndex >= maxBytes)
            return false;

        byte modrm = code[modrmIndex];
        int mod = (modrm >> 6) & 3;
        int rm = modrm & 7;
        int len = 1;

        if (mod == 3)
        {
            afterEnd = modrmIndex + len;
            return true;
        }

        if (mod == 0 && rm == 5)
        {
            ripDispOffset = modrmIndex + len;
            len += 4;
        }
        else if (rm == 4)
        {
            if (modrmIndex + 1 >= maxBytes) return false;
            byte sib = code[modrmIndex + 1];
            len += 1;
            if (mod == 0 && (sib & 7) == 5)
                len += 4;
            else if (mod == 1)
                len += 1;
            else if (mod == 2)
                len += 4;
        }
        else if (mod == 1)
        {
            len += 1;
        }
        else if (mod == 2)
        {
            len += 4;
        }

        afterEnd = modrmIndex + len;
        return true;
    }
}