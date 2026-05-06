# check_factory.py
import idc

output_file = "/mnt/extra_ssd/Github/SBOX-DEV/Research/CResourceCompiler/ida_factory_check.txt"

with open(output_file, "w") as f:
    # Check what's at the "factory" address
    addr = 0x180d696c0

    # Is it code or data?
    flags = idc.get_full_flags(addr)
    is_code = idc.is_code(flags)

    f.write(f"Address: {hex(addr)}\n")
    f.write(f"Flags: {hex(flags)}\n")
    f.write(f"Is code: {is_code}\n")
    f.write(f"Name: {idc.get_name(addr)}\n\n")

    # Get bytes at this address
    f.write("Bytes at address:\n")
    for i in range(16):
        byte = idc.get_wide_byte(addr + i)
        f.write(f"{byte:02x} ")
    f.write("\n\n")

    # If it's code, disassemble
    if is_code or idc.print_insn_mnem(addr):
        f.write("Disassembly:\n")
        ea = addr
        for i in range(10):
            disasm = idc.generate_disasm_line(ea, 0)
            f.write(f"{hex(ea)}: {disasm}\n")
            ea = idc.next_head(ea)
    else:
        # It's data - check what it points to
        f.write("Data values:\n")
        for i in range(10):
            val = idc.get_qword(addr + i * 8)
            name = idc.get_name(val) or ""
            f.write(f"  +{i*8}: {hex(val)} {name}\n")

    # Also look at IResourceCompilerSystem in the C# code
    f.write("\n" + "=" * 60 + "\n")
    f.write("Looking for related C++ classes...\n")

    # Search for IResourceCompilerSystem vtable pattern
    f.write("\nSearching for IResourceCompilerSystem references...\n")

    # Look at the RTTI type info
    rtti_addrs = [
        0x180d34178,  # CResourceCompilerSystem
        0x180d34218,  # IResourceCompilerSystem
    ]

    for rtti in rtti_addrs:
        name = idc.get_strlit_contents(rtti, -1, idc.STRTYPE_C)
        f.write(f"RTTI at {hex(rtti)}: {name}\n")

        for xref in idautils.XrefsTo(rtti):
            f.write(f"  Xref from: {hex(xref.frm)}\n")

import idautils
print(f"Output written to: {output_file}")
