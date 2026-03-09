# find_interfaces.py - Run this in IDA Pro
# Output: /home/wyattw/Documents/ida_interfaces_output.txt

import idc
import idautils

output_file = "/mnt/extra_ssd/Github/SBOX-DEV/Research/CResourceCompiler/ida_interfaces_output.txt"

def safe_get_qword(ea):
    """Safely get a qword, return 0 if invalid"""
    if ea == 0 or ea is None:
        return 0
    try:
        return idc.get_qword(ea)
    except:
        return 0

def safe_get_strlit(ea):
    """Safely get a string, return None if invalid"""
    if ea == 0 or ea is None:
        return None
    try:
        result = idc.get_strlit_contents(ea, -1, idc.STRTYPE_C)
        if result:
            return result.decode() if isinstance(result, bytes) else result
    except:
        pass
    return None

with open(output_file, "w") as f:
    f.write("=" * 60 + "\n")
    f.write("ResourceCompiler.dll Interface Analysis\n")
    f.write("=" * 60 + "\n\n")

    # 1. Get interface list head
    list_head = safe_get_qword(0x180D69C28)
    f.write(f"Interface list head: {hex(list_head)}\n\n")

    # 2. Walk the interface chain
    f.write("--- Registered Interfaces ---\n")
    current = list_head
    count = 0
    visited = set()

    while current != 0 and count < 100 and current not in visited:
        visited.add(current)

        iface_ptr = safe_get_qword(current)
        name_ptr = safe_get_qword(current + 8)
        next_ptr = safe_get_qword(current + 0x10)

        name = safe_get_strlit(name_ptr)
        if name:
            f.write(f"{count}: '{name}'\n")
            f.write(f"   VTable: {hex(iface_ptr)}\n")
            f.write(f"   Entry: {hex(current)}\n")
            f.write(f"   Next: {hex(next_ptr)}\n\n")
        else:
            f.write(f"{count}: (no name) Entry={hex(current)}\n\n")

        current = next_ptr
        count += 1

    f.write(f"Total interfaces: {count}\n\n")

    # 3. Search for compiler-related strings
    f.write("--- Compiler-Related Strings ---\n")
    for string_ea in idautils.Strings():
        s = str(string_ea)
        sl = s.lower()
        if "compiler" in sl and len(s) < 80:
            f.write(f"{hex(string_ea.ea)}: {s}\n")

    f.write("\n")

    # 4. Search for V* interface patterns
    f.write("--- V* Interface Patterns ---\n")
    for string_ea in idautils.Strings():
        s = str(string_ea)
        if s.startswith("V") and len(s) > 3 and len(s) < 40:
            if any(c.isdigit() for c in s[-4:]):
                f.write(f"{hex(string_ea.ea)}: {s}\n")

    f.write("\n")

    # 5. Search for shader-related strings
    f.write("--- Shader/VFX Strings ---\n")
    for string_ea in idautils.Strings():
        s = str(string_ea)
        sl = s.lower()
        if ("shader" in sl or "vfx" in sl) and len(s) < 80:
            f.write(f"{hex(string_ea.ea)}: {s}\n")

print(f"Output written to: {output_file}")
