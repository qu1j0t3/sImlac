﻿/*  
    This file is part of sImlac.

    sImlac is free software: you can redistribute it and/or modify
    it under the terms of the GNU Affero General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    sImlac is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU Affero General Public License for more details.

    You should have received a copy of the GNU Affero General Public License
    along with sImlac.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Text;

using imlac.IO;
using imlac.Debugger;

namespace imlac
{
    public enum ProcessorState
    {
        Halted,
        Running,
        BreakpointHalt
    }

    public enum ExecState
    {
        Fetch,
        Defer,
        Execute
    }

    public class Processor
    {
        public Processor(ImlacSystem system)
        {
            _system = system;
            _mem = _system.Memory;

            _iotDispatch = new IIOTDevice[0x200];   // 9 bits of IOT instructions

            Reset();
            InitializeCache();
        }

        public void Reset()
        {
            _pc = 0x0020;   // 40 oct, standard bootstrap address
            _ac = 0x0000;
            _link = 0;
            _currentInstruction = null;
            _currentIndirectAddress = 0x0000;
            _instructionState = ExecState.Fetch;
            _state = ProcessorState.Halted;
        }

        public void RegisterDeviceIOTs(IIOTDevice device)
        {
            int[] handledIOTs = device.GetHandledIOTs();

            foreach (int i in handledIOTs)
            {
                if (_iotDispatch[i] == null)
                {
                    _iotDispatch[i] = device;
                }
                else
                {
                    // We have an overlap here; can't have two devices sharing the same
                    // IOT.  It would be bad.
                    throw new InvalidOperationException(String.Format("IOT conflict on {0:x3} between {1} and {2}.", i, _iotDispatch[i], device));
                }
            }
        }

        public ProcessorState State
        {
            get { return _state; }
            set { _state = value; }
        }

        public ExecState InstructionState
        {
            get { return _instructionState; }
        }

        public ushort PC
        {
            get { return _pc; }
            set { _pc = value; }
        }

        public ushort AC
        {
            get { return _ac; }
            set { _ac = value; }
        }

        public ushort DS
        {
            get { return _ds; }
            set { _ds = value; }
        }

        public ushort BreakpointAddress
        {
            get { return _breakpointAddress; }
        }

        public string Disassemble(ushort address)
        {
            return GetCachedInstruction(address).Disassemble(address);
        }

        public void InitializeCache()
        {
            _instructionCache = new Instruction[Memory.Size];
        }

        public void InvalidateCache(ushort address)
        {
            _instructionCache[address & Memory.SizeMask] = null;
        }

        public void Clock()
        {
            if (_state == ProcessorState.Halted)
            {
                return;
            }

            //
            // Grab data switches from console if enabled
            //
            if (_system.Display.DataSwitchMappingEnabled)
            {
                _ds = _system.Display.DataSwitches;
            }

            switch (_instructionState)
            {
                case ExecState.Fetch:
                    _currentInstruction = GetCachedInstruction(_pc);

                    if (_currentInstruction.IsIndirect)
                    {
                        // Indirect instruction: the next state is Defer so we can do the indirect memory op before execution.
                        _instructionState = ExecState.Defer;
                    }
                    else if (_currentInstruction.IsOperateOrIOT)
                    {
                        //
                        // Operate or IOT instruction (no memory referenced): execute the instruction now, and return to the Fetch state.
                        //
                        Execute();
                        _instructionState = ExecState.Fetch;
                    }
                    else
                    {
                        // Processor order: Move to the Exec state
                        _instructionState = ExecState.Execute;
                    }
                    break;

                case ExecState.Defer:
                    //
                    // Get the indirect address for this instruction.
                    // 
                    ushort effectiveAddress = GetEffectiveAddress(_currentInstruction.Data);
                    _currentIndirectAddress = _mem.Fetch(effectiveAddress);

                    //
                    // If this is an auto-increment indirect index register (octal locations 10-17 on each 2k page)
                    // then we will increment the contents (and the indirect address).
                    //
                    if ((effectiveAddress & 0x7ff) >= 0x08 && (effectiveAddress & 0x7ff) < 0x10)
                    {
                        _currentIndirectAddress++;
                        _mem.Store(effectiveAddress, (ushort)(_currentIndirectAddress));
                    }
                    
                    _instructionState = ExecState.Execute;
                    break;

                case ExecState.Execute:
                    //
                    // Execute the instruction, return to Fetch state.
                    //
                    Execute();
                    _instructionState = ExecState.Fetch;
                    break;

            }
        }

        private Instruction GetCachedInstruction(ushort address)
        {
            // TODO: factor this masking logic out.
            if (_instructionCache[address & Memory.SizeMask] == null)
            {
                _instructionCache[address & Memory.SizeMask] = new Instruction(_mem.Fetch((ushort)(address & Memory.SizeMask)));
            }

            return _instructionCache[address & Memory.SizeMask];
        }

        private void Execute()
        {
            ushort q;
            uint res;
            switch (_currentInstruction.Opcode)
            {
                case Opcode.ADD:
                    q = DoFetchForCurrentInstruction();
                    res = (uint)(_ac + q);

                    _ac = (ushort)res;

                    // link bit is complemented if carry-out occurs
                    if ((res & 0x10000) != 0)
                    {
                        _link = (ushort)((_link ^ 1) & 0x1);
                    }
                    _pc++;
                    break;

                case Opcode.AND:
                    _ac = (ushort)(_ac & DoFetchForCurrentInstruction());
                    _pc++;
                    break;

                case Opcode.DAC:
                    DoStoreForCurrentInstruction(_ac);
                    _pc++;
                    break;

                case Opcode.IOR:
                    _ac = (ushort)(_ac | DoFetchForCurrentInstruction());
                    _pc++;
                    break;

                case Opcode.IOT:
                    DoIOT();
                    _pc++;
                    break;

                case Opcode.ISZ:
                    q = DoFetchForCurrentInstruction();
                    q++;
                    DoStoreForCurrentInstruction(q);

                    if (q == 0)
                    {
                        _pc++;  // skip next instruction
                    }
                   
                    _pc++;
                    break;

                case Opcode.JMP:
                    if (_currentInstruction.IsIndirect)
                    {
                        _pc = _currentIndirectAddress;
                    }
                    else
                    {
                        _pc = GetEffectiveAddress(_currentInstruction.Data);
                    }
                    break;

                case Opcode.JMS:
                    // Store next PC at location specified by instruction (Q),
                    // continue execution at Q+1
                    DoStoreForCurrentInstruction((ushort)(_pc + 1));

                    if (_currentInstruction.IsIndirect)
                    {
                        _pc = (ushort)(_currentIndirectAddress + 1);
                    }
                    else
                    {
                        _pc = (ushort)(GetEffectiveAddress(_currentInstruction.Data) + 1);
                    }
                    break;

                case Opcode.LAC:
                    _ac = DoFetchForCurrentInstruction();
                    _pc++;
                    break;

                case Opcode.LAW:
                    _ac = _currentInstruction.Data;
                    _pc++;
                    break;

                case Opcode.LWC:
                    _ac = (ushort)(-_currentInstruction.Data);
                    _pc++;
                    break;

                case Opcode.OPR:
                    // Execute the Operate Class 1 instruction based on the data bits.

                    // T1: Clear AC and / or Link
                    if ((_currentInstruction.Data & 0x0001) != 0)
                    {
                        _ac = 0x0000;
                    }

                    if ((_currentInstruction.Data & 0x0008) != 0)
                    {
                        _link = 0;
                    }

                    // T2: 1's Complement AC and / or Link
                    if ((_currentInstruction.Data & 0x0002) != 0)
                    {
                        _ac = (ushort)(~_ac);
                    }

                    if ((_currentInstruction.Data & 0x0010) != 0)
                    {
                        _link = (ushort)((~_link) & 0x1);
                    }

                    // T3: Increment AC and / or OR data switches with AC
                    if ((_currentInstruction.Data & 0x0004) != 0)
                    {
                        _ac++;

                        // If an overflow occurs, the link is complemented.
                        _link = (_ac == 0) ? (ushort)((~_link) & 0x1) : _link;
                    }

                    if ((_currentInstruction.Data & 0x0020) != 0)
                    {
                        _ac |= _ds;                       
                    }

                    if ((_currentInstruction.Data & 0x8000) == 0)
                    {
                        // Halt the CPU.
                        _state = ProcessorState.Halted;
                    }

                    _pc++;
                    break;

                case Opcode.RAL:
                    // TODO: this is pretty inefficient.
                    for (int i = 0; i < _currentInstruction.Data; i++)
                    {
                        ushort oldLink = _link;
                        _link = (ushort)((_ac & 0x8000) >> 15);
                        _ac = (ushort)((_ac << 1) | oldLink);
                    }

                    if (_currentInstruction.DisplayOn)
                    {
                        _system.DisplayProcessor.State = ProcessorState.Running;
                    }

                    _pc++;
                    break;

                case Opcode.RAR:
                    for (int i = 0; i < _currentInstruction.Data; i++)
                    {
                        ushort oldLink = _link;
                        _link = (ushort)((_ac & 0x0001));
                        _ac = (ushort)((_ac >> 1) | (oldLink << 15));
                    }

                    if (_currentInstruction.DisplayOn)
                    {
                        _system.DisplayProcessor.State = ProcessorState.Running;
                    }

                    _pc++;
                    break;

                case Opcode.SAL:
                    // The shift operators are arithmetic (& preserve sign).  Shifting left only shifts bits 2-15 left,
                    // bit 0 stays the same and bit 1 is lost (the link register is not involved in any way).
                    ushort bitZero = (ushort)(_ac & 0x8000);
                    for (int i = 0; i < _currentInstruction.Data; i++)
                    {
                        _ac = (ushort)(_ac << 1);
                    }
                    _ac = (ushort)(bitZero | (_ac & 0x7fff));

                    if (_currentInstruction.DisplayOn)
                    {
                        _system.DisplayProcessor.State = ProcessorState.Running;
                    }

                    _pc++;
                    break;

                case Opcode.SAM:
                    q = DoFetchForCurrentInstruction();
                    if (_ac == q)
                    {
                        _pc++;
                    }
                    _pc++;
                    break;

                case Opcode.SAR:
                    // The shift operators are arithmetic (& preserve sign).  Shifting right shifts bits 1-14 left,
                    // bit 0 is copied to bit 1, and bit 15 is lost (the link register is not involved in any way).
                    bitZero = (ushort)(_ac & 0x8000);
                    for (int i = 0; i < _currentInstruction.Data; i++)
                    {
                        _ac = (ushort)(_ac >> 1);
                        _ac = (ushort)(bitZero | _ac);
                    }

                    if (_currentInstruction.DisplayOn)
                    {
                        _system.DisplayProcessor.State = ProcessorState.Running;
                    }

                    _pc++;
                    break;

                case Opcode.SKP:
                    bool skip = false;

                    // AC == 0
                    if ((_currentInstruction.Data & 0x0001) != 0)
                    {
                        skip = (_ac == 0);
                    }

                    // AC > = 0
                    if ((_currentInstruction.Data & 0x0002) != 0)
                    {
                        skip = ((_ac & 0x8000) == 0);
                    }

                    // Link == 0
                    if ((_currentInstruction.Data & 0x0004) != 0)
                    {                        
                        skip = (_link == 0);
                    }

                    // Display on
                    if ((_currentInstruction.Data & 0x0008) != 0)
                    {
                        skip = (_system.DisplayProcessor.State == ProcessorState.Running);
                    }

                    // Keyboard data present
                    if ((_currentInstruction.Data & 0x0010) != 0)
                    {
                        skip = _system.Keyboard.KeyReady;
                    }

                    // TTY input present
                    if ((_currentInstruction.Data & 0x0020) != 0)
                    {
                        skip = _system.TTY.DataReady;
                    }

                    // TTY send complete
                    if ((_currentInstruction.Data & 0x0040) != 0)
                    {                        
                        skip = _system.TTY.DataSendReady;
                    }

                    // 40Hz display sync
                    if ((_currentInstruction.Data & 0x0080) != 0)
                    {
                        skip = _system.DisplayProcessor.FrameLatch;
                    }

                    // Paper Tape Reader data present
                    if ((_currentInstruction.Data & 0x0100) != 0)
                    {
                        skip = _system.PaperTapeReader.DataReady();
                    }

                    if (_currentInstruction.SkipNegate)
                    {
                        skip = !skip;
                    }

                    if (skip)
                    {
                        _pc++;
                    }

                    _pc++;
                    break;

                case Opcode.SUB:
                    q = DoFetchForCurrentInstruction();
                    res = (uint)(_ac - q);

                    _ac = (ushort)res;

                    // link bit is complemented if carry-in occurs
                    if ((res & 0x10000) != 0)
                    {
                        _link = (ushort)((_link ^ 1) & 0x1);
                    }
                    _pc++;
                    break;

                case Opcode.XAM:
                    q = DoFetchForCurrentInstruction();
                    ushort ac = _ac;
                    _ac = q;
                    DoStoreForCurrentInstruction(ac);
                    _pc++;                   
                    break;

                case Opcode.XOR:
                    _ac = (ushort)(_ac ^ DoFetchForCurrentInstruction());
                    _pc++;
                    break;

                default:
                    throw new NotImplementedException(String.Format("Unimplemented Opcode {0}", _currentInstruction.Opcode));
            }

            // If the next instruction has a breakpoint set we'll halt at this point, before executing it.
            if (BreakpointManager.TestBreakpoint(BreakpointType.Execution, _pc))
            {
                _state = ProcessorState.BreakpointHalt;
                _breakpointAddress = _pc;
            }
        }

        private void DoIOT()
        {
            //
            // Dispatch the IOT instruction to the correct device, if one is registered.
            // We use the Data field (the full 9 bits including the device and IOP code)
            // as the index.  See the comments in IIOTDevice for the reasoning here.
            //
            // We special case IOT 60 here -- this does not appear in any documentation but it does
            // show up in the 2nd-stage loader on a number of tapes as the first instruction.
            // Unsure what the purpose is (there are some hints that doing an IOT (any IOT) is needed to
            // trigger something) but ignoring it works fine for now.
            //
            if (_currentInstruction.Data == 0x30)
            {
                return;
            }

            IIOTDevice device = _iotDispatch[_currentInstruction.Data];
            if (device != null)
            {
                device.ExecuteIOT(_currentInstruction.Data);
            }
            else
            {
               Trace.Log(LogType.Processor, "Unimplemented IOT device {0}, IOT opcode {1}", Helpers.ToOctal((ushort)_currentInstruction.IOTDevice), Helpers.ToOctal(_currentInstruction.Data));
            }            
        }

        private ushort DoFetchForCurrentInstruction()
        {
            ushort effectiveAddress = _currentInstruction.IsIndirect ? _currentIndirectAddress : GetEffectiveAddress(_currentInstruction.Data);

            // If there's a read breakpoint on this address we will halt here.
            if (BreakpointManager.TestBreakpoint(BreakpointType.Read, effectiveAddress))
            {
                _state = ProcessorState.BreakpointHalt;
                _breakpointAddress = effectiveAddress;
            }

            return _mem.Fetch(effectiveAddress);
        }

        private ushort GetEffectiveAddress(ushort baseAddress)
        {            
            return (ushort)((_pc & (Memory.SizeMask & 0xf800)) | (baseAddress & 0x07ff));
        }

        private void DoStoreForCurrentInstruction(ushort word)
        {
            ushort effectiveAddress = _currentInstruction.IsIndirect ? _currentIndirectAddress : GetEffectiveAddress(_currentInstruction.Data);

            // If there's a write breakpoint on this address we will halt here.
            if (BreakpointManager.TestBreakpoint(BreakpointType.Write, effectiveAddress))
            {
                _state = ProcessorState.BreakpointHalt;
                _breakpointAddress = effectiveAddress;
            }

            _mem.Store(effectiveAddress, word);
        }

        private Instruction _currentInstruction;
        private ushort _currentIndirectAddress;
        private ExecState _instructionState;
        private ImlacSystem _system;
        private Memory _mem;
        private Instruction[] _instructionCache;
        private ProcessorState _state;

        //
        // registers
        //
        private ushort _pc;
        private ushort _link;
        private ushort _ac;

        //
        // Debug information -- the PC at which the last breakpoint occurred.
        //
        private ushort _breakpointAddress;


        //
        // Front panel data switch
        //
        private ushort _ds;

        //
        // IOT dispatch table
        //
        private IIOTDevice[] _iotDispatch;

        private enum Opcode
        {
            LAW,        // Load Accumulator With
            LWC,        // Load Accumulator With complement
            JMP,        // Jump
            DAC,        // Deposit Accumulator
            XAM,        // Exchange Accumulator with Memory
            ISZ,        // Increment and Skip on Zero
            JMS,        // Jump to Subroutine
            AND,        // Logical AND
            IOR,        // Inclusive OR
            XOR,        // Exclusive OR
            LAC,        // Load Accumulator w/memory
            ADD,        // Add to Accumulator
            SUB,        // Subtract from Accumulator
            SAM,        // Skip if Accumulator is same as memory

            OPR,        // Operate class 1 instruction
            IOT,        // IOT instruction

            RAL,        // Rotate left
            RAR,        // Rotate right
            SAL,        // Shift left
            SAR,        // Shift right

            SKP,        // generic skip op (encompasses all class 3 instructions which may be combined in a multitude of ways)
        }
        
        private class Instruction
        {
            public Instruction(ushort word)
            {
                Decode(word);
            }

            public bool IsIndirect
            {
                get { return _indirect; }
            }

            public bool IsOperateOrIOT
            {
                get { return _operateOrIOT; }
            }

            public bool DisplayOn
            {
                get { return _displayOn; }
            }

            public Opcode Opcode
            {
                get { return _opcode; }
            }

            public ushort Data
            {
                get { return _data; }
            }

            public bool SkipNegate
            {
                get { return _skipNegate; }
            }

            public int IOTDevice
            {
                get { return _iotDevice; }
            }

            public int IOP
            {
                get { return _iop; }
            }

            public string Disassemble(ushort address)
            {
                StringBuilder sb = new StringBuilder();

                if (IsIndirect &&
                    Opcode != Processor.Opcode.LAW &&
                    Opcode != Processor.Opcode.LWC)
                {
                    sb.Append("I ");
                }

                string effectiveAddress = Helpers.ToOctal(GetEffectiveAddress(address, Data));

                switch (Opcode)
                {
                    case Processor.Opcode.ADD:
                        sb.AppendFormat("ADD {0}", effectiveAddress);
                        break;

                    case Processor.Opcode.AND:
                        sb.AppendFormat("AND {0}", effectiveAddress);
                        break;

                    case Processor.Opcode.DAC:
                        sb.AppendFormat("DAC {0}", effectiveAddress);
                        break;                  

                    case Processor.Opcode.IOR:
                        sb.AppendFormat("IOR {0}", effectiveAddress);
                        break;

                    case Processor.Opcode.IOT:
                        sb.Append(DisassembleIOT());
                        break;

                    case Processor.Opcode.ISZ:
                        sb.AppendFormat("ISZ {0}", effectiveAddress);
                        break;

                    case Processor.Opcode.JMP:
                        sb.AppendFormat("JMP {0}", effectiveAddress);
                        break;

                    case Processor.Opcode.JMS:
                        sb.AppendFormat("JMS {0}", effectiveAddress);
                        break;

                    case Processor.Opcode.LAC:
                        sb.AppendFormat("LAC {0}", effectiveAddress);
                        break;

                    case Processor.Opcode.LAW:
                        sb.AppendFormat("LAW {0}", Helpers.ToOctal(Data));
                        break;

                    case Processor.Opcode.LWC:
                        sb.AppendFormat("LWC {0} !({1})", Helpers.ToOctal(Data), Helpers.ToOctal((ushort)(-Data)));
                        break;

                    case Processor.Opcode.OPR:
                        sb.Append(DisassembleOPR());
                        break;

                    case Processor.Opcode.RAL:
                        sb.AppendFormat("RAL {0},{1}", Data, DisplayOn ? "DON" : String.Empty);
                        break;

                    case Processor.Opcode.RAR:
                        sb.AppendFormat("RAR {0},{1}", Data, DisplayOn ? "DON" : String.Empty);
                        break;

                    case Processor.Opcode.SAL:
                        sb.AppendFormat("SAL {0},{1}", Data, DisplayOn ? "DON" : String.Empty);
                        break;

                    case Processor.Opcode.SAM:
                        sb.AppendFormat("SAM {0}", effectiveAddress);
                        break;

                    case Processor.Opcode.SAR:
                        sb.AppendFormat("SAR {0},{1}", Data, DisplayOn ? "DON" : String.Empty);
                        break;

                    case Processor.Opcode.SKP:
                        sb.Append(DisassembleSKP());
                        break;

                    case Processor.Opcode.SUB:
                        sb.AppendFormat("SUB {0}", effectiveAddress);
                        break;

                    case Processor.Opcode.XAM:
                        sb.AppendFormat("XAM {0}", effectiveAddress);
                        break;

                    case Processor.Opcode.XOR:
                        sb.AppendFormat("XOR {0}", effectiveAddress);
                        break;

                    default:
                        throw new InvalidOperationException(String.Format("Unhandled opcode {0}", Opcode));

                }

                return sb.ToString();
            }

            private string DisassembleIOT()
            {
                // todo: just have a lookup table for this.
                string iot;
                switch (Data)
                {
                    case 0x03:
                        iot = "DLA";
                        break;

                    case 0x09:
                        iot = "CTB";
                        break;

                    case 0x0a:
                        iot = "DOF";
                        break;

                    case 0x11:
                        iot = "KRB";
                        break;

                    case 0x12:
                        iot = "KCF";
                        break;

                    case 0x13:
                        iot = "KRC";
                        break;

                    case 0x19:
                        iot = "RRB";
                        break;

                    case 0x1a:
                        iot = "RCF";
                        break;

                    case 0x1b:
                        iot = "RRC";
                        break;

                    case 0x21:
                        iot = "TPR";
                        break;

                    case 0x22:
                        iot = "TCF";
                        break;

                    case 0x23:
                        iot = "TPC";
                        break;

                    case 0x29:
                        iot = "HRB";
                        break;

                    case 0x2a:
                        iot = "HOF";
                        break;

                    case 0x31:
                        iot = "HON";
                        break;

                    case 0x32:
                        iot = "STB";
                        break;

                    case 0x39:
                        iot = "SCF";
                        break;

                    case 0x3a:
                        iot = "IOS";
                        break;

                    case 0xb9:
                        iot = "PPC";
                        break;

                    case 0xbc:
                        iot = "PSF";
                        break;

                    case 0x71:
                        iot = "IOF";
                        break;

                    case 0x72:
                        iot = "ION";
                        break;

                    default:
                        iot = "IOT " + Helpers.ToOctal(Data);
                        break;
                }

                return iot;
            }

            private string DisassembleOPR()
            {
                string opr = String.Empty;

                string[] lowerCodes = { "NOP", "CLA", "CMA", "STA", "IAC", "COA", "CIA", "CMA, IAC", "CLA, CMA, IAC" };
                string[] upperCodes = { "", "CLL", "CML", "STL", "ODA", "CLL, CML", "CML, ODA", "CLL, CML", "CLL, CML, ODA" };

                // check for two specially named combinations of upper and lower bits:
                if (Data == 0x9)
                {
                    opr = "CAL";
                }
                else if (Data == 0x21)
                {
                    opr = "LDA";
                }
                else
                {
                    int lowIndex = Data & 0x7;
                    int highIndex = (Data & 0x38) >> 3;

                    if (highIndex != 0 && lowIndex != 0)
                    {
                        opr = String.Format("{0},{1}", lowerCodes[lowIndex], upperCodes[highIndex]);
                    }
                    else if (highIndex != 0)
                    {
                        opr = upperCodes[highIndex];
                    }
                    else if (lowIndex != 0)
                    {
                        opr = lowerCodes[lowIndex];
                    }
                }

                if ((Data & 0x8000) == 0)
                {
                    opr += ", HLT";
                }

                return opr;
            }

            private string DisassembleSKP()
            {
                string skp = String.Empty;

                // Data should be non-empty
                if (Data == 0)
                {
                    throw new InvalidOperationException("SKP instruction with no flags set.");
                }

                // these correspond to the bit set, from the lsb to the msb and can be combined.
                string[] codes = { "ASZ", "ASP", "LSZ", "DSF", "KSF", "RSF", "TSF", "SSF", "HSF" };
                string[] notCodes = { "ASN", "ASM", "LSN", "DSN", "KSN", "RSN", "TSN", "SSN", "HSN" };

                for (int i = 0; i < 9; i++)
                {
                    if ((Data & (0x01) << i) != 0)
                    {
                        if (!string.IsNullOrEmpty(skp))
                        {
                            skp += ",";
                        }

                        skp += SkipNegate ? notCodes[i] : codes[i];
                    }
                }

                return skp;
            }

            private ushort GetEffectiveAddress(ushort currentAddress, ushort baseAddress)
            {
                return (ushort)((currentAddress & 0x0800) | baseAddress);
            }

            private void Decode(ushort word)
            {
                // try to decode from most specified op type to least specific.
                if (!DecodeClass1(word))
                {
                    if (!DecodeClass2(word))
                    {
                        if (!DecodeClass3(word))
                        {
                            if (!DecodeIOT(word))
                            {
                                if (!DecodeOrder(word))
                                {
                                    throw new InvalidOperationException(String.Format("Unhandled instruction {0:x4}", word));
                                }
                            }
                        }
                    } 
                }
            }

            private bool DecodeClass1(ushort word)
            {
                // All class 1 instructions contain 0s in bits 1-9.  (Bit zero
                // encodes a Halt instruction if clear).
                if ((word & 0x7fc0) == 0x0000)
                {                    
                    _opcode = Opcode.OPR;
                    //
                    // Save the bits defining the T1, T2, and T3 operations
                    // (bits 10-15) and the HALT flag.
                    //
                    _data = (ushort)(word & 0x803f);
                    _operateOrIOT = true;
                    return true;
                }
                else
                {
                    // not a class 1 microinstruction
                    return false;
                }
            }

            private bool DecodeClass2(ushort word)
            {
                // All class 2 instructions contain 0000011 in bits 0-6
                if ((word & 0xfe00) == 0x0600)
                {
                    _data = (ushort)(word & 0x0003);

                    // If bit 7 is set, this is a Display On instruction.
                    if ((word & 0x0040) == 0x0040)
                    {
                        _displayOn = true;
                    }

                    if ((word & 0x0020) == 0x0000)
                    {
                        // Rotate
                        if ((word & 0x0010) == 0x0000)
                        {
                            _opcode = Opcode.RAL;
                        }
                        else
                        {
                            _opcode = Opcode.RAR;
                        }
                    }
                    else
                    {
                        // Shift
                        if ((word & 0x0010) == 0x0000)
                        {
                            _opcode = Opcode.SAL;
                        }
                        else
                        {
                            _opcode = Opcode.SAR;
                        }
                    }

                    _operateOrIOT = true;
                    return true;
                }
                else
                {
                    // not a class 2 microinstruction
                    return false;
                }
            }

            private bool DecodeClass3(ushort word)
            {
                // Operate class 3 instructions have 00010 in bits 1-6
                if ((word & 0x7e00) == 0x0400)
                {
                    _opcode = Opcode.SKP;
                    _skipNegate = (word & 0x8000) != 0x0000;

                    // Save the flag bits
                    _data = (ushort)(word & 0x01ff);

                    _operateOrIOT = true;
                    return true;
                }
                else
                {
                    return false;
                }
            }

            private bool DecodeIOT(ushort word)
            {
                // All IOT instructions contain 0000001 in bits 0-6
                if ((word & 0xfe00) == 0x0200)
                {
                    _opcode = Opcode.IOT;

                    _iotDevice = (word & 0x1f8) >> 3;
                    _iop = (ushort)(word & 0x0007);
                    _data = (ushort)(word & 0x1ff);
                    _operateOrIOT = true;
                    return true;
                }
                else
                {
                    return false;
                }
            }

            private bool DecodeOrder(ushort word)
            {
                int orderCode = (word & 0x7800) >> 9;
                _indirect = (word & 0x8000) != 0;
                _data = (ushort)(word & 0x07ff);

                switch (orderCode)
                {
                    case 0x04:
                        _opcode = _indirect ? Opcode.LWC : Opcode.LAW;
                        _indirect = false;  // This is never actually an indirect instruction.
                        break;

                    case 0x08:
                        _opcode = Opcode.JMP;
                        break;

                    case 0x10:
                        _opcode = Opcode.DAC;
                        break;

                    case 0x14:
                        _opcode = Opcode.XAM;
                        break;

                    case 0x18:
                        _opcode = Opcode.ISZ;
                        break;

                    case 0x1c:
                        _opcode = Opcode.JMS;
                        break;

                    case 0x24:
                        _opcode = Opcode.AND;
                        break;

                    case 0x28:
                        _opcode = Opcode.IOR;
                        break;

                    case 0x2c:
                        _opcode = Opcode.XOR;
                        break;

                    case 0x30:
                        _opcode = Opcode.LAC;
                        break;

                    case 0x34:
                        _opcode = Opcode.ADD;
                        break;

                    case 0x38:
                        _opcode = Opcode.SUB;
                        break;

                    case 0x3c:
                        _opcode = Opcode.SAM;
                        break;

                    default: 
                        return false;
                }

                return true;
            }

            private Opcode _opcode;
            private ushort _data;
            private bool _skipNegate;
            private bool _displayOn;

            private int _iotDevice;
            private int _iop;

            private bool _indirect;
            private bool _operateOrIOT;
        }
    }
}
