# analyze_vtable_final.py - Analyze CResourceCompilerSystem vtable
import idc

output_file = "/mnt/extra_ssd/Github/SBOX-DEV/Research/CResourceCompiler/resource_vtable.txt"

def safe_get_qword(ea):
    if ea == 0 or ea is None:
        return 0
    try:
        return idc.get_qword(ea)
    except:
        return 0

with open(output_file, "w") as f:
    f.write("=" * 60 + "\n")
    f.write("CResourceCompilerSystem VTable Analysis\n")
    f.write("=" * 60 + "\n\n")

    # VTable at 0x180b3d568 (??_7CResourceCompilerSystem@@6B@)
    vtable_addr = 0x180b3d568
    f.write(f"VTable address: {hex(vtable_addr)}\n\n")

    f.write("--- Virtual Functions ---\n")
    for i in range(30):
        func_ptr = safe_get_qword(vtable_addr + i * 8)

        if func_ptr > 0x180000000 and func_ptr < 0x182000000:
            func_name = idc.get_name(func_ptr) or "(no name)"
            f.write(f"[{i:2d}] {hex(func_ptr)} - {func_name}\n")
        else:
            f.write(f"[{i:2d}] {hex(func_ptr)} - (end or invalid)\n")
            if func_ptr == 0 or func_ptr == 0xffffffffffffffff:
                break

    # Also check the RTTI vtable
    f.write("\n" + "=" * 60 + "\n")
    f.write("RTTI Complete Object Locator VTable\n")
    f.write("=" * 60 + "\n\n")

    # ??_R4CResourceCompilerSystem@@6B@ at 0x180bef068
    rtti_vtable = 0x180bef068
    f.write(f"RTTI VTable at: {hex(rtti_vtable)}\n\n")

    for i in range(30):
        val = safe_get_qword(rtti_vtable + i * 8)
        if val > 0x180000000 and val < 0x182000000:
            name = idc.get_name(val) or ""
            f.write(f"[{i:2d}] {hex(val)} - {name}\n")
        else:
            f.write(f"[{i:2d}] {hex(val)}\n")

print(f"Output written to: {output_file}")
