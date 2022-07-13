using System.Diagnostics;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace NativeCIL.Backend.Amd64;

public class Amd64Architecture : Architecture
{
    private readonly string _asmPath, _binPath, _objPath;

    public Amd64Architecture(string input, string output) : base(input)
    {
        _asmPath = Path.ChangeExtension(output, "asm");
        _binPath = Path.ChangeExtension(output, "bin");
        _objPath = Path.ChangeExtension(output, "o");
        OutputPath = Path.ChangeExtension(output, "elf");
    }

    public override int PointerSize => 8;

    // Thanks https://os.phil-opp.com/multiboot-kernel/ :)
    public override void Initialize()
    {
        Builder.AppendLine("[bits 32]");
        Builder.AppendLine("[global _start]");

        Builder.AppendLine("KERNEL_STACK equ 0x00200000");

        Builder.AppendLine("dd 0xE85250D6"); // Magic
        Builder.AppendLine("dd 0"); // Architecture
        Builder.AppendLine("dd 16"); // Header length
        Builder.AppendLine("dd -(0xE85250D6+16)"); // Checksum
        // Required tag
        Builder.AppendLine("dw 0");
        Builder.AppendLine("dw 0");
        Builder.AppendLine("dd 8");

        Builder.AppendLine("_start:");
        Builder.AppendLine("mov esp,KERNEL_STACK");
        Builder.AppendLine("push 0");
        Builder.AppendLine("popf");
        Builder.AppendLine("push eax");
        Builder.AppendLine("push 0");
        Builder.AppendLine("push ebx");
        Builder.AppendLine("push 0");
        Builder.AppendLine("call EnterLongMode");

        Builder.AppendLine("align 4");
        Builder.AppendLine("IDT:");
        Builder.AppendLine(".Length dw 0");
        Builder.AppendLine(".Base dd 0");

        Builder.AppendLine("EnterLongMode:");
        Builder.AppendLine("mov edi,p4_table");
        Builder.AppendLine("push di");
        Builder.AppendLine("mov eax,p3_table");
        Builder.AppendLine("or eax,0b11");
        Builder.AppendLine("mov [p4_table],eax");
        Builder.AppendLine("mov eax,p2_table");
        Builder.AppendLine("or eax,0b11");
        Builder.AppendLine("mov [p3_table],eax");
        Builder.AppendLine("mov ecx,0");

        Builder.AppendLine(".Map_P2_Table:");
        Builder.AppendLine("mov eax,0x200000");
        Builder.AppendLine("mul ecx");
        Builder.AppendLine("or eax,0b10000011");
        Builder.AppendLine("mov [p2_table+ecx*8],eax");
        Builder.AppendLine("inc ecx");
        Builder.AppendLine("cmp ecx,512");
        Builder.AppendLine("jne .Map_P2_Table");

        Builder.AppendLine("pop di");
        Builder.AppendLine("mov al,0xFF");
        Builder.AppendLine("out 0xA1,al");
        Builder.AppendLine("out 0x21,al");
        Builder.AppendLine("cli");
        Builder.AppendLine("nop");
        Builder.AppendLine("nop");
        Builder.AppendLine("lidt [IDT]");
        Builder.AppendLine("mov eax,10100000b");
        Builder.AppendLine("mov cr4,eax");
        Builder.AppendLine("mov edx,edi");
        Builder.AppendLine("mov cr3,edx");
        Builder.AppendLine("mov ecx,0xC0000080");
        Builder.AppendLine("rdmsr");
        Builder.AppendLine("or eax,0x00000100");
        Builder.AppendLine("wrmsr");
        Builder.AppendLine("mov ebx,cr0");
        Builder.AppendLine("or ebx,0x80000001");
        Builder.AppendLine("mov cr0,ebx");
        Builder.AppendLine("lgdt [GDT.Pointer]");
        Builder.AppendLine("sti");
        Builder.AppendLine("jmp 0x0008:Main");

        Builder.AppendLine("GDT:");
        Builder.AppendLine(".Null:");
        Builder.AppendLine("dq 0x0000000000000000");
        Builder.AppendLine(".Code:");
        Builder.AppendLine("dq 0x00209A0000000000");
        Builder.AppendLine("dq 0x0000920000000000");
        Builder.AppendLine("align 4");
        Builder.AppendLine("dw 0");
        Builder.AppendLine(".Pointer:");
        Builder.AppendLine("dw $-GDT-1");
        Builder.AppendLine("dd GDT");

        Builder.AppendLine("align 4096");
        Builder.AppendLine("p4_table:");
        Builder.AppendLine("resb 4096");
        Builder.AppendLine("p3_table:");
        Builder.AppendLine("resb 4096");
        Builder.AppendLine("p2_table:");
        Builder.AppendLine("resb 4096");

        Builder.AppendLine("[bits 64]");
        Builder.AppendLine("Main:");
        Builder.AppendLine("mov ax,0x0010");
        Builder.AppendLine("mov ds,ax");
        Builder.AppendLine("mov es,ax");
        Builder.AppendLine("mov fs,ax");
        Builder.AppendLine("mov gs,ax");
        Builder.AppendLine("mov ss,ax");
        Builder.AppendLine("pop rsi");
        Builder.AppendLine("pop rdx");
        Builder.AppendLine("mov rbp,KERNEL_STACK-1024");
    }

    public override void Compile()
    {
        foreach (var type in Module.Types)
        {
            // Initialize static fields
            foreach (var field in type.Fields)
            {
                if (!field.IsStatic)
                    continue;

                Builder.AppendLine(GetSafeName(field.Name) + ":");
                Builder.AppendLine("dq " + (field.HasConstant ? Convert.ToUInt64(field.Constant.Value) : 0));
            }

            // Compile methods
            foreach (var method in type.Methods)
            {
                if (method.IsConstructor)
                    continue;

                if (method.IsStaticConstructor)
                {
                    Builder.AppendLine("call " + GetSafeName(method.FullName));
                    continue;
                }

                var branches = GetAllBranches(method).ToList();
                Builder.AppendLine(GetSafeName(method.FullName) + ":");

                // TODO: Initialize locals (variables), so that they don't interfere with Ldarg/Call
                /*if (method.Body.InitLocals)
                    foreach (var local in method.Body.Variables)
                        PushVariable(local.Index, 0);*/

                foreach (var inst in method.Body.Instructions)
                {
                    foreach (var branch in branches)
                        if (((Instruction)branch.Operand).Offset == inst.Offset)
                        {
                            Builder.AppendLine(BrLabelName(inst, method, true) + ":");
                            break;
                        }

                    Builder.AppendLine(";" + inst.OpCode);
                    switch (inst.OpCode.Code)
                    {
                        case Code.Nop: break;
                        case Code.Ret: Builder.AppendLine("ret"); break;

                        case Code.Ldc_I4_0: Push(0); break;
                        case Code.Ldc_I4_1: Push(1); break;
                        case Code.Ldc_I4_2: Push(2); break;
                        case Code.Ldc_I4_3: Push(3); break;
                        case Code.Ldc_I4_4: Push(4); break;
                        case Code.Ldc_I4_5: Push(5); break;
                        case Code.Ldc_I4_6: Push(6); break;
                        case Code.Ldc_I4_7: Push(7); break;
                        case Code.Ldc_I4_8: Push(8); break;
                        case Code.Ldc_I4_M1: Push(-1); break;

                        case Code.Ldc_I4_S:
                        case Code.Ldc_I4: Push(inst.Operand); break;

                        case Code.Conv_I4:
                        case Code.Conv_I:
                            Pop("rax");
                            Builder.AppendLine("and rax,0xFFFFFFFF");
                            Push("rax");
                            break;

                        case Code.Conv_U1:
                        case Code.Conv_I1:
                            Pop("rax");
                            Builder.AppendLine("and rax,0xFF");
                            Push("rax");
                            break;

                        case Code.Stind_I1:
                            Pop("rax"); // Value
                            Pop("rbx"); // Address
                            Builder.AppendLine("mov [rbx],al");
                            break;

                        case Code.Add:
                            Pop("rax");
                            Pop("rbx");
                            Builder.AppendLine("add rbx,rax");
                            Push("rbx");
                            break;

                        case Code.Sub:
                            Pop("rax");
                            Pop("rbx");
                            Builder.AppendLine("sub rbx,rax");
                            Push("rbx");
                            break;

                        case Code.Or:
                            Pop("rax");
                            Pop("rbx");
                            Builder.AppendLine("or rbx,rax");
                            Push("rbx");
                            break;

                        case Code.Xor:
                            Pop("rax");
                            Pop("rbx");
                            Builder.AppendLine("xor rbx,rax");
                            Push("rbx");
                            break;

                        case Code.Ldloc_0:
                            PopIndex(0, "rax", "r8");
                            Push("rax");
                            break;
                        case Code.Ldloc_1:
                            PopIndex(1, "rax", "r8");
                            Push("rax");
                            break;
                        case Code.Ldloc_2:
                            PopIndex(2, "rax", "r8");
                            Push("rax");
                            break;
                        case Code.Ldloc_3:
                            PopIndex(3, "rax", "r8");
                            Push("rax");
                            break;

                        case Code.Ldloc_S:
                        case Code.Ldloc:
                            PopIndex(inst.Operand is Local o ? o.Index : Convert.ToInt32(inst.Operand), "rax", "r8");
                            Push("rax");
                            break;

                        case Code.Stloc_0:
                            Pop("rax");
                            PushIndex(0, "rax", "r8");
                            break;
                        case Code.Stloc_1:
                            Pop("rax");
                            PushIndex(1, "rax", "r8");
                            break;
                        case Code.Stloc_2:
                            Pop("rax");
                            PushIndex(2, "rax", "r8");
                            break;
                        case Code.Stloc_3:
                            Pop("rax");
                            PushIndex(3, "rax", "r8");
                            break;

                        case Code.Stloc_S:
                        case Code.Stloc:
                            Pop("rax");
                            PushIndex(inst.Operand is Local u ? u.Index : Convert.ToInt32(inst.Operand), "rax", "r8");
                            break;

                        case Code.Dup:
                            Peek("rax");
                            Push("rax");
                            break;

                        case Code.Br_S:
                        case Code.Br:
                            Builder.AppendLine("jmp " + BrLabelName(inst, method));
                            break;

                        case Code.Brtrue_S:
                        case Code.Brtrue:
                            Pop("rax");
                            Builder.AppendLine("cmp rax,0");
                            Builder.AppendLine("jnz " + BrLabelName(inst, method));
                            break;

                        case Code.Brfalse_S:
                        case Code.Brfalse:
                            Pop("rax");
                            Builder.AppendLine("cmp rax,0");
                            Builder.AppendLine("jz " + BrLabelName(inst, method));
                            break;

                        case Code.Clt:
                            Pop("rax");
                            Pop("rbx");
                            Builder.AppendLine("cmp rbx,rax");
                            Builder.AppendLine("setl bl");
                            Push("rbx");
                            break;

                        case Code.Ceq:
                            Pop("rax");
                            Pop("rbx");
                            Builder.AppendLine("cmp rbx,rax");
                            Builder.AppendLine("sete bl");
                            Push("rbx");
                            break;

                        case Code.Call:
                            var meth = (MethodDef)inst.Operand;
                            for (var i = meth.Parameters.Count; i > 0; i--)
                            {
                                Pop("rax");
                                PushIndex(i - 1, "rax", "rdx");
                            }
                            Builder.AppendLine("call " + GetSafeName(meth.FullName));
                            break;

                        case Code.Ldarg_S:
                        case Code.Ldarg:
                            PopIndex(Convert.ToInt32(inst.Operand), "rax", "rdx");
                            Push("rax");
                            break;

                        case Code.Ldarg_0:
                            PopIndex(0, "rax", "rdx");
                            Push("rax");
                            break;
                        case Code.Ldarg_1:
                            PopIndex(1, "rax", "rdx");
                            Push("rax");
                            break;
                        case Code.Ldarg_2:
                            PopIndex(2, "rax", "rdx");
                            Push("rax");
                            break;
                        case Code.Ldarg_3:
                            PopIndex(3, "rax", "rdx");
                            Push("rax");
                            break;

                        case Code.Ldsfld:
                            PopString(GetSafeName(((FieldDef)inst.Operand).Name), "rax");
                            Push("rax");
                            break;

                        case Code.Stsfld:
                            Pop("rax");
                            PushString(GetSafeName(((FieldDef)inst.Operand).Name), "rax");
                            break;

                        default:
                            Console.WriteLine("Unimplemented opcode: " + inst.OpCode);
                            break;
                    }
                }
            }
        }
    }

    public override void Assemble()
    {
        File.WriteAllText(_asmPath, Builder.ToString());
        Process.Start("nasm", $"-fbin {_asmPath} -o {_binPath}").WaitForExit();
    }

    public override void Link()
    {
        // TODO: Replace objcopy and lld with a C# linker
        Process.Start("objcopy", $"-Ibinary -Oelf64-x86-64 -Bi386 {_binPath} {_objPath}");
        Process.Start("ld.lld", $"-melf_x86_64 -Tlinker.ld -o{OutputPath} {_objPath}").WaitForExit();
    }

    public override void PushIndex(int index, object obj, string reg) => Builder.AppendLine($"mov qword [{reg}+{index * PointerSize}],{obj}");

    public override void PopIndex(int index, object obj, string reg) => Builder.AppendLine($"mov {obj},qword [{reg}+{index * PointerSize}]");

    public override void PushString(string str, object obj) => Builder.AppendLine($"mov [{str}],{obj}");

    public override void PopString(string str, object obj) => Builder.AppendLine($"mov {obj},[{str}]");

    public override void Peek(object obj) => Builder.AppendLine($"mov {obj},qword [rbp+{StackIndex}]");

    public override void Push(object obj)
    {
        StackIndex += PointerSize;
        var index = StackIndex;
        Builder.AppendLine($"mov qword [rbp+{index}],{obj}");
    }

    public override void Pop(object obj)
    {
        var index = StackIndex;
        StackIndex -= PointerSize;
        Builder.AppendLine($"mov {obj},qword [rbp+{index}]");
    }
}