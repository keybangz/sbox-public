# find_real_vtable.py - Find the actual vtable initialization
import idc
import idautils

output_file = "/mnt/extra_ssd/Github/SBOX-DEV/Research/CResourceCompiler/ida_real_vtable.txt"

def safe_get_qword(ea):
    if ea == 0 or ea is None:
        return 0
    try:
        return idc.get_qword(ea)
    except:
        return 0

with open(output_file, "w") as f:
    f.write("=" * 60 + "\n")
    f.write("Finding IResourceCompilerSystem VTable\n")
    f.write("=" * 60 + "\n\n")

    # 1. Search for GenerateResourceFile function
    f.write("--- Searching for GenerateResourceFile ---\n")
    for string_ea in idautils.Strings():
        s = str(string_ea)
        if "GenerateResourceFile" in s or "GenerateResourceBytes" in s:
            f.write(f"{hex(string_ea.ea)}: {s}\n")

    # 2. Search for CResourceCompilerSystem methods
    f.write("\n--- CResourceCompilerSystem Methods ---\n")

    # Look for functions that might belong to CResourceCompilerSystem
    # by searching for xrefs to the RTTI
    rtti = 0x180d34178  # .?AVCResourceCompilerSystem@@

    # The vtable usually comes BEFORE the RTTI reference
    # VTable structure: [RTTI ptr][func1][func2]...

    # Find where this RTTI is referenced
    f.write("XRefs to CResourceCompilerSystem RTTI:\n")
    for xref in idautils.XrefsTo(rtti):
        xref_addr = xref.frm
        f.write(f"  {hex(xref_addr)}\n")

        # The vtable typically starts 8 bytes before the RTTI reference
        vtable_candidate = xref_addr - 8
        f.write(f"    Possible vtable at: {hex(vtable_candidate)}\n")

        # Read first few entries
        for i in range(15):
            val = safe_get_qword(vtable_candidate + i * 8)
            if val > 0x180000000 and val < 0x182000000:
                name = idc.get_name(val) or ""
                # Check if it's code
                flags = idc.get_full_flags(val)
                is_code = idc.is_code(flags)
                f.write(f"      [{i}] {hex(val)} {'(code)' if is_code else '(data)'} {name}\n")

    # 3. Search for IResourceCompilerSystem vtable
    f.write("\n--- IResourceCompilerSystem VTable ---\n")
    rtti_interface = 0x180d34218  # .?AVIResourceCompilerSystem@@

    for xref in idautils.XrefsTo(rtti_interface):
        xref_addr = xref.frm
        f.write(f"  XRef from: {hex(xref_addr)}\n")

        vtable_candidate = xref_addr - 8
        f.write(f"    Possible vtable at: {hex(vtable_candidate)}\n")

        for i in range(15):
            val = safe_get_qword(vtable_candidate + i * 8)
            if val > 0x180000000 and val < 0x182000000:
                name = idc.get_name(val) or ""
                flags = idc.get_full_flags(val)
                is_code = idc.is_code(flags)
                f.write(f"      [{i}] {hex(val)} {'(code)' if is_code else '(data)'} {name}\n")

    # 4. Look for "Connect" and "Disconnect" methods (common in Source 2 interfaces)
    f.write("\n--- Looking for interface methods ---\n")
    for name_ea in idautils.Names():
        name = name_ea[1]
        if "ResourceCompiler" in name and len(name) < 60:
            f.write(f"{hex(name_ea[0])}: {name}\n")

print(f"Output written to: {output_file}")
