﻿using Iced.Intel;
using MBBSEmu.Extensions;
using MBBSEmu.Logging;
using NLog;
using System;
using System.Linq;
namespace MBBSEmu.CPU
{
    public class CpuCore
    {
        protected static readonly Logger _logger = LogManager.GetCurrentClassLogger(typeof(CustomLogger));
        public delegate int InvokeExternalFunctionDelegate(int importedNameTableOrdinal, int functionOrdinal);

        private readonly InvokeExternalFunctionDelegate _invokeExternalFunctionDelegate;

        public readonly CpuRegisters Registers;
        public readonly CpuMemory Memory;

        private Instruction _currentInstruction;

        public CpuCore(InvokeExternalFunctionDelegate invokeExternalFunctionDelegate)
        {
            Registers = new CpuRegisters();
            Memory = new CpuMemory();
            _invokeExternalFunctionDelegate = invokeExternalFunctionDelegate;
            Registers.SP = CpuMemory.STACK_BASE;
        }

        public void Tick()
        {
            _currentInstruction = Memory.GetInstruction(Registers.CS, Registers.IP);

#if DEBUG
    _logger.Debug($"{_currentInstruction.ToString()}");
#endif

            switch (_currentInstruction.Mnemonic)
            {
                case Mnemonic.Add:
                    Op_Add();
                    break;
                case Mnemonic.Push:
                    Op_Push();
                    break;
                case Mnemonic.Pop:
                    Op_Pop();
                    break;
                case Mnemonic.Mov:
                    Op_Mov();
                    break;
                case Mnemonic.Call:
                    Op_Call();
                    break;
                case Mnemonic.Cmp:
                    Op_Cmp();
                    break;
                case Mnemonic.Je:
                    Op_Je();
                    return;
                default:
                    throw new ArgumentOutOfRangeException($"Unsupported OpCode: {_currentInstruction.Mnemonic}");
            }

            Registers.IP += (ushort)_currentInstruction.ByteLength;
        }

        private void Op_Je()
        {
            if (Registers.F.IsFlagSet((ushort) EnumFlags.ZF))
            {
                Registers.IP = _currentInstruction.Immediate16;
            }
            else
            {
                Registers.IP += (ushort)_currentInstruction.ByteLength;
            }
        }

        private void Op_Add()
        {
            switch (_currentInstruction.Op0Kind)
            {
                case OpKind.Register when _currentInstruction.Op1Kind == OpKind.Register:
                    Registers.SetValue(_currentInstruction.Op0Register,
                        Registers.GetValue(_currentInstruction.Op1Register));
                    break;
                
                case OpKind.Register when _currentInstruction.Op1Kind == OpKind.Immediate8:
                case OpKind.Register when _currentInstruction.Op1Kind == OpKind.Immediate8to16:
                case OpKind.Register when _currentInstruction.Op1Kind == OpKind.Immediate16:
                    Registers.SetValue(_currentInstruction.Op0Register, (ushort) (Registers.GetValue(_currentInstruction.Op0Register) + _currentInstruction.Immediate16));
                    break;
                default:
                    throw new ArgumentOutOfRangeException($"Unknown ADD: {_currentInstruction.Op0Kind}");
            }
        }

        private void Op_Cmp()
        {
            ushort value1, value2;
            switch (_currentInstruction.Op0Kind)
            {
                case OpKind.Register:
                    value1 = Registers.GetValue(_currentInstruction.Op0Register);
                    break;
                case OpKind.Immediate8:
                    value1 = _currentInstruction.Immediate8;
                    break;
                case OpKind.Memory when _currentInstruction.Op1Kind == OpKind.Immediate8:
                    value1 = Memory.GetByte(Registers.DS, (int) _currentInstruction.MemoryDisplacement);
                    break;
                default:
                    throw new ArgumentOutOfRangeException($"Unknown Op0 for CMP: {_currentInstruction.Op0Kind}");
            }

            switch (_currentInstruction.Op1Kind)
            {
                case OpKind.Register:
                    value2 = Registers.GetValue(_currentInstruction.Op1Register);
                    break;
                case OpKind.Immediate8:
                    value2 = _currentInstruction.Immediate8;
                    break;
                case OpKind.Immediate8to16:
                    value2 = (ushort) _currentInstruction.Immediate8to16;
                    break;
                default:
                    throw new ArgumentOutOfRangeException($"Unknown Op1 for CMP: {_currentInstruction.Op0Kind}");
            }

            //Set Appropriate Flags
            if (value1 == value2)
            {
                Registers.F = Registers.F.SetFlag((ushort) EnumFlags.ZF);
                Registers.F = Registers.F.ClearFlag((ushort)EnumFlags.CF);
            }
            else if (value1 < value2)
            {
                Registers.F = Registers.F.ClearFlag((ushort)EnumFlags.ZF);
                Registers.F = Registers.F.SetFlag((ushort)EnumFlags.CF);
            }
            else if (value1 > value2)
            {
                Registers.F = Registers.F.ClearFlag((ushort)EnumFlags.ZF);
                Registers.F = Registers.F.ClearFlag((ushort)EnumFlags.CF);
            }
        }

        private void Op_Pop()
        {
            switch (_currentInstruction.Op0Kind)
            {
                case OpKind.Register:
                    Registers.SetValue(_currentInstruction.Op0Register, Memory.Pop(Registers.SP));
                    break;
                default:
                    throw new ArgumentOutOfRangeException($"Unknown POP: {_currentInstruction.Op0Kind}");
            }

            Registers.SP += 2;
        }

        /// <summary>
        ///     Push Op Code
        /// </summary>
        private void Op_Push()
        {
            Registers.SP -= 2;
            switch (_currentInstruction.Op0Kind)
            {
                //PUSH r16
                case OpKind.Register:
                    Memory.Push(Registers.SP,
                        BitConverter.GetBytes((ushort) Registers.GetValue(_currentInstruction.Op0Register)));
                    break;
                //PUSH imm8 - PUSH imm16
                case OpKind.Immediate8:
                case OpKind.Immediate8to16:
                case OpKind.Immediate16:
                    Memory.Push(Registers.SP, BitConverter.GetBytes(_currentInstruction.Immediate16));
                    break;

                //PUSH r/m16
                case OpKind.Memory when _currentInstruction.MemorySegment == Register.DS:
                    Memory.Push(Registers.SP,
                        Memory.GetArray(Registers.GetValue(Register.DS),
                            (ushort) _currentInstruction.MemoryDisplacement, 2));
                    break;
                default:
                    throw new ArgumentOutOfRangeException($"Unknown PUSH: {_currentInstruction.Op0Kind}");
            }
        }

        /// <summary>
        ///     MOV Op Code
        /// </summary>
        private void Op_Mov()
        {
            switch (_currentInstruction.Op0Kind)
            {
                //MOV r16,imm16
                case OpKind.Register when _currentInstruction.Op1Kind == OpKind.Immediate16:
                {
                    //Check for a possible relocation
                    ushort destinationValue;

                    if (_currentInstruction.Immediate16 == ushort.MaxValue)
                    {
                        var relocationRecord = Memory._segments[Registers.CS].RelocationRecords
                            .FirstOrDefault(x => x.Offset == Registers.IP + 1);

                        //If we found a relocation record, set the new value, otherwise, it must have been a literal max
                        destinationValue = relocationRecord?.TargetTypeValueTuple.Item2 ?? ushort.MaxValue;
                    }
                    else
                    {
                        destinationValue = _currentInstruction.Immediate16;
                    }

                    Registers.SetValue(_currentInstruction.Op0Register, destinationValue);
                    return;
                }

                //MOV r16, r16
                case OpKind.Register when _currentInstruction.Op1Kind == OpKind.Register:
                {
                    Registers.SetValue(_currentInstruction.Op0Register,
                        Registers.GetValue(_currentInstruction.Op1Register));
                    return;
                }

                //MOV AX,moffs16*
                case OpKind.Register when _currentInstruction.Op1Kind == OpKind.Memory:
                {
                    Registers.SetValue(_currentInstruction.Op0Register, Memory.GetWord(Registers.DS, (int) _currentInstruction.MemoryDisplacement));
                    return;
                }

                //MOV r/m16,imm16
                /*
                 * The instruction in the question only uses a single constant offset so no effective address with registers.
                 * As such, it's DS unless overridden by a prefix.
                 */
                case OpKind.Memory when _currentInstruction.Op1Kind == OpKind.Immediate16:
                {
                    Memory.SetWord(Registers.DS, (int) _currentInstruction.MemoryDisplacement, _currentInstruction.Immediate16);
                    break;
                } 
                
                
                case OpKind.Memory when _currentInstruction.Op1Kind == OpKind.Register:
                {
                    switch (_currentInstruction.MemorySize)
                    {
                        //MOV moffs16*,AX
                        case MemorySize.UInt16:
                            Memory.SetWord(Registers.DS, (int) _currentInstruction.MemoryDisplacement,
                                (ushort) Registers.GetValue(_currentInstruction.Op1Register));
                            break;
                        //MOV moffs8*,AL
                        case MemorySize.UInt8:
                            Memory.SetByte(Registers.DS, (int) _currentInstruction.MemoryDisplacement,
                                (byte) Registers.GetValue(_currentInstruction.Op1Register));
                            break;
                        default:
                            throw new ArgumentOutOfRangeException("Unsupported Memory type for MOV operation");
                    }

                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException($"Unknown MOV: {_currentInstruction.Op0Kind}");
            }
        }

        private void Op_Call()
        {
            switch (_currentInstruction.Op0Kind)
            {
                case OpKind.FarBranch16 when _currentInstruction.Immediate16 == ushort.MaxValue:
                {

                    //We Handle this like a standard CALL function
                    //where, we set the BP to the current SP then 

                    //Set BP to the current stack pointer
                    

                    //We push CS:IP to the stack
                    //Push the Current IP to the stack
                    Registers.SP -= 2;
                    Memory.Push(Registers.SP, BitConverter.GetBytes((ushort)Registers.IP));
                    Registers.SP -= 2;
                    Memory.Push(Registers.SP, BitConverter.GetBytes((ushort)Registers.CS));
                    Registers.BP = Registers.SP;


                        //Check for a possible relocation
                        int destinationValue;

                    if (_currentInstruction.Immediate16 == ushort.MaxValue)
                    {
                        var relocationRecord = Memory._segments[Registers.CS].RelocationRecords
                            .FirstOrDefault(x => x.Offset == Registers.IP + 1);

                        if (relocationRecord == null)
                        {
                            destinationValue = ushort.MaxValue;
                        }
                        else
                        {
                            _invokeExternalFunctionDelegate(relocationRecord.TargetTypeValueTuple.Item2,
                                relocationRecord.TargetTypeValueTuple.Item3);

                            Registers.SetValue(Register.CS, Memory.Pop(Registers.SP));
                            Registers.SP += 2;
                            Registers.SetValue(Register.EIP, Memory.Pop(Registers.SP));
                            Registers.SP += 2;
                            return;
                        }
                    }
                    else
                    {
                        destinationValue = _currentInstruction.Immediate16;
                    }

                    //TODO -- Perform actual call

                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException($"Unknown CALL: {_currentInstruction.Op0Kind}");
            }

        }
    }
}