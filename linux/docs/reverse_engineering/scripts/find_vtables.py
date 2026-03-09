# find_vtable.py - Find ResourceCompilerSystem001 vtable
import idc
import idautils

output_file = "/mnt/extra_ssd/Github/SBOX-DEV/Research/CResourceCompiler/ida_vtable_output.txt"

def safe_get_qword(ea):
    if ea == 0 or ea is None:
        return 0
    try:
        return idc.get_qword(ea)
    except:
        return 0

with open(output_file, "w") as f:
    f.write("=" * 60 + "\n")
    f.write("ResourceCompilerSystem001 VTable Analysis\n")
    f.write("=" * 60 + "\n\n")

    # 1. Find the string "ResourceCompilerSystem001"
    iface_string_addr = 0x180b3c578
    f.write(f"Interface name at: {hex(iface_string_addr)}\n")
    name = idc.get_strlit_contents(iface_string_addr, -1, idc.STRTYPE_C)
    f.write(f"Name: {name}\n\n")

    # 2. Find cross-references to this string
    f.write("--- Cross-references to interface name ---\n")
    for xref in idautils.XrefsTo(iface_string_addr):
        f.write(f"Xref from: {hex(xref.frm)} (type={xref.type})\n")

        # Look for nearby vtable or interface registration
        # Usually there's a structure like:
        # { vtable_ptr, name_ptr, next_ptr }

        # Check what's at the address before the name reference
        struct_addr = xref.frm - 8  # Often the vtable is 8 bytes before
        vtable_candidate = safe_get_qword(struct_addr)
        f.write(f"  Possible struct at: {hex(struct_addr)}\n")
        f.write(f"  VTable candidate: {hex(vtable_candidate)}\n\n")

    # 3. Search for CResourceCompilerSystem vtable
    f.write("\n--- Searching for CResourceCompilerSystem vtable ---\n")

    # The vtable usually has the class name referenced nearby
    # Search for the RTTI typeinfo name reference
    rtti_name = 0x180d34178  # .?AVCResourceCompilerSystem@@

    for xref in idautils.XrefsTo(rtti_name):
        f.write(f"RTTI name xref from: {hex(xref.frm)}\n")

    # 4. Look for vtable by searching for typical vtable patterns
    # VTables usually start with a pointer to RTTI, then virtual functions
    f.write("\n--- Potential VTable addresses ---\n")

    # Look at addresses that reference the RTTI type
    typeinfo_addr = 0x180d34178
    for xref in idautils.XrefsTo(typeinfo_addr):
        vtable_addr = xref.frm
        f.write(f"\nPossible vtable at: {hex(vtable_addr)}\n")

        # Try to read first few function pointers
        for i in range(20):
            func_ptr = safe_get_qword(vtable_addr + i * 8)
            if func_ptr > 0x180000000 and func_ptr < 0x182000000:
                # Try to get function name
                func_name = idc.get_name(func_ptr)
                f.write(f"  [{i}] {hex(func_ptr)} - {func_name}\n")

print(f"Output written to: {output_file}")
