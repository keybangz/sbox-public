#!/usr/bin/env python3
"""
ELF Symbol Visibility Patcher for libengine2.so (Program Header Version)

Fixes OpenSSL symbol collision by hiding OpenSSL symbols in libengine2.so
so they don't override system libcrypto.so.3 symbols.

Uses PT_DYNAMIC program header to find the dynamic symbol table, which is
the same mechanism the runtime dynamic linker uses. This ensures we're
patching the actual symbol table used at runtime, not a section header copy.

The crash:
  OPENSSL_LH_retrieve (libengine2.so)   ← wrong! should be from libcrypto.so.3
  libcrypto.so.3 internal calls
  CRYPTO_get_ex_new_index (libcrypto.so.3)
  CryptoNative_EnsureOpenSslInitialized (libSystem.Security.Cryptography.Native.OpenSsl.so)

The fix:
  Set STV_HIDDEN visibility on OpenSSL symbols in libengine2.so's dynamic symbol table.
"""

import struct
import sys
import shutil
import os

# ELF64 constants
ELFMAG = b'\x7fELF'
ELFCLASS64 = 2
ELFDATA2LSB = 1  # Little-endian
EV_CURRENT = 1
ET_EXEC = 2
ET_DYN = 3

# Program header types
PT_NULL = 0
PT_LOAD = 1
PT_DYNAMIC = 2

# Dynamic table entry tags
DT_NULL = 0
DT_NEEDED = 1
DT_STRTAB = 5
DT_SYMTAB = 6
DT_STRSZ = 10
DT_SYMENT = 11

# Section header types (used only for getting dynsym size)
SHT_DYNSYM = 11

# Symbol visibility
STV_DEFAULT = 0
STV_INTERNAL = 1
STV_HIDDEN = 2
STV_PROTECTED = 3

# ELF64 header format (little-endian)
# e_ident[16], e_type (uint16), e_machine (uint16), e_version (uint32),
# e_entry (uint64), e_phoff (uint64), e_shoff (uint64), e_flags (uint32),
# e_ehsize (uint16), e_phentsize (uint16), e_phnum (uint16),
# e_shentsize (uint16), e_shnum (uint16), e_shstrndx (uint16)
ELF64_HDR_FMT = '<16sHHIQQQIHHHHHH'
ELF64_HDR_SIZE = 64

# ELF64 program header format
# p_type (uint32), p_flags (uint32), p_offset (uint64), p_vaddr (uint64),
# p_paddr (uint64), p_filesz (uint64), p_memsz (uint64), p_align (uint64)
ELF64_PHDR_FMT = '<IIQQQQQQ'
ELF64_PHDR_SIZE = 56

# ELF64 section header format (used only for getting dynsym size)
# sh_name (uint32), sh_type (uint32), sh_flags (uint64), sh_addr (uint64),
# sh_offset (uint64), sh_size (uint64), sh_link (uint32), sh_info (uint32),
# sh_addralign (uint64), sh_entsize (uint64)
ELF64_SHDR_FMT = '<IIQQQQIIQQ'
ELF64_SHDR_SIZE = 64

# ELF64 dynamic entry format
# d_tag (int64), d_val (uint64)
ELF64_DYN_FMT = '<qQ'
ELF64_DYN_SIZE = 16

# ELF64 symbol entry format
# st_name (uint32), st_info (uint8), st_other (uint8), st_shndx (uint16),
# st_value (uint64), st_size (uint64)
ELF64_SYM_FMT = '<IBBHQQ'
ELF64_SYM_SIZE = 24

# Symbols to hide - exact matches
EXACT = {
    'OPENSSL_init_crypto',
    'OPENSSL_init_ssl',
    'OPENSSL_LH_retrieve',
    'OPENSSL_LH_insert',
    'OPENSSL_LH_delete',
    'EVP_KEYMGMT_is_a',
}

# Prefix matches - any symbol starting with these
PREFIXES = (
    'SSL_', 'TLS_', 'DTLS_',
    'OPENSSL_', 'OPENSSL_LH',
    'EVP_', 'EVP_KEYMGMT',
    'CRYPTO_', 'CRYPTO_THREAD',
    'BIO_', 'BIO_s_',
    'X509_', 'X509V3_',
    'PEM_', 'RSA_', 'EC_', 'ECDH_', 'ECDSA_',
    'DH_', 'DSA_', 'HMAC_',
    'SHA1_', 'SHA224_', 'SHA256_', 'SHA384_', 'SHA512_', 'SHA3_',
    'MD4_', 'MD5_', 'MDC2_',
    'AES_', 'DES_', 'RC4_', 'RC2_', 'BF_', 'CAST_',
    'RAND_', 'ERR_', 'ERR_get',
    'ASN1_', 'OBJ_', 'OBJ_nid',
    'PKCS', 'd2i_', 'i2d_',
    'sk_', 'BN_', 'BUF_',
    'lh_', 'NCONF_', 'CONF_',
    'ENGINE_', 'UI_', 'OCSP_',
    'TS_', 'CMS_', 'SMIME_',
    'COMP_', 'STORE_',
)


def should_hide_symbol(name: str) -> bool:
    """Check if a symbol name should be hidden."""
    if name in EXACT:
        return True
    for prefix in PREFIXES:
        if name.startswith(prefix):
            return True
    return False


def parse_elf64_header(data: bytes) -> dict:
    """Parse ELF64 header and return relevant fields."""
    if len(data) < ELF64_HDR_SIZE:
        raise ValueError("File too small for ELF header")
    
    if data[:4] != ELFMAG:
        raise ValueError("Not an ELF file")
    
    if data[4] != ELFCLASS64:
        raise ValueError("Not a 64-bit ELF file")
    
    if data[5] != ELFDATA2LSB:
        raise ValueError("Not a little-endian ELF file")
    
    hdr = struct.unpack(ELF64_HDR_FMT, data[:ELF64_HDR_SIZE])
    return {
        'e_type': hdr[1],
        'e_machine': hdr[2],
        'e_version': hdr[3],
        'e_entry': hdr[4],
        'e_phoff': hdr[5],
        'e_shoff': hdr[6],
        'e_flags': hdr[7],
        'e_ehsize': hdr[8],
        'e_phentsize': hdr[9],
        'e_phnum': hdr[10],
        'e_shentsize': hdr[11],
        'e_shnum': hdr[12],
        'e_shstrndx': hdr[13],
    }


def parse_program_header(data: bytes, offset: int) -> dict:
    """Parse a single ELF64 program header."""
    phdr_data = data[offset:offset + ELF64_PHDR_SIZE]
    if len(phdr_data) < ELF64_PHDR_SIZE:
        raise ValueError(f"Program header at offset {offset} is truncated")
    
    phdr = struct.unpack(ELF64_PHDR_FMT, phdr_data)
    return {
        'p_type': phdr[0],
        'p_flags': phdr[1],
        'p_offset': phdr[2],
        'p_vaddr': phdr[3],
        'p_paddr': phdr[4],
        'p_filesz': phdr[5],
        'p_memsz': phdr[6],
        'p_align': phdr[7],
    }


def parse_dynamic_entry(data: bytes, offset: int) -> dict:
    """Parse a single ELF64 dynamic entry."""
    dyn_data = data[offset:offset + ELF64_DYN_SIZE]
    if len(dyn_data) < ELF64_DYN_SIZE:
        raise ValueError(f"Dynamic entry at offset {offset} is truncated")
    
    dyn = struct.unpack(ELF64_DYN_FMT, dyn_data)
    return {
        'd_tag': dyn[0],
        'd_val': dyn[1],
    }


def parse_section_header(data: bytes, offset: int) -> dict:
    """Parse a single ELF64 section header."""
    shdr_data = data[offset:offset + ELF64_SHDR_SIZE]
    if len(shdr_data) < ELF64_SHDR_SIZE:
        raise ValueError(f"Section header at offset {offset} is truncated")
    
    shdr = struct.unpack(ELF64_SHDR_FMT, shdr_data)
    return {
        'sh_name': shdr[0],
        'sh_type': shdr[1],
        'sh_flags': shdr[2],
        'sh_addr': shdr[3],
        'sh_offset': shdr[4],
        'sh_size': shdr[5],
        'sh_link': shdr[6],
        'sh_info': shdr[7],
        'sh_addralign': shdr[8],
        'sh_entsize': shdr[9],
    }


def parse_symbol_entry(data: bytes, offset: int) -> dict:
    """Parse a single ELF64 symbol entry."""
    sym_data = data[offset:offset + ELF64_SYM_SIZE]
    if len(sym_data) < ELF64_SYM_SIZE:
        raise ValueError(f"Symbol entry at offset {offset} is truncated")
    
    sym = struct.unpack(ELF64_SYM_FMT, sym_data)
    return {
        'st_name': sym[0],
        'st_info': sym[1],
        'st_other': sym[2],
        'st_shndx': sym[3],
        'st_value': sym[4],
        'st_size': sym[5],
        'offset': offset,  # Keep track of offset for writing back
    }


def vaddr_to_file_offset(phdrs: list, vaddr: int) -> int:
    """Convert a virtual address to a file offset using PT_LOAD segments."""
    for phdr in phdrs:
        if phdr['p_type'] == PT_LOAD:
            if phdr['p_vaddr'] <= vaddr < phdr['p_vaddr'] + phdr['p_filesz']:
                return phdr['p_offset'] + (vaddr - phdr['p_vaddr'])
    raise ValueError(f"Virtual address 0x{vaddr:x} not found in any PT_LOAD segment")


def get_dynsym_size_from_sections(data: bytes, hdr: dict) -> int:
    """Get the size of .dynsym from section headers."""
    for i in range(hdr['e_shnum']):
        shdr_offset = hdr['e_shoff'] + i * ELF64_SHDR_SIZE
        shdr = parse_section_header(data, shdr_offset)
        if shdr['sh_type'] == SHT_DYNSYM:
            return shdr['sh_size']
    raise ValueError("Could not find .dynsym section header to get size")


def get_string(data: bytes, strtab_offset: int, name_offset: int) -> str:
    """Get a null-terminated string from a string table."""
    start = strtab_offset + name_offset
    end = start
    while end < len(data) and data[end] != 0:
        end += 1
    return data[start:end].decode('utf-8', errors='replace')


def patch_engine2_openssl(lib_path: str) -> tuple:
    """
    Patch libengine2.so to hide OpenSSL symbols.
    
    Returns:
        tuple: (total_symbols_scanned, total_hidden, hidden_symbols_list)
    """
    # First, make a backup if it doesn't exist
    backup_path = lib_path + '.bak2'
    if not os.path.exists(backup_path):
        print(f"Creating backup: {backup_path}")
        shutil.copy2(lib_path, backup_path)
    else:
        print(f"Backup already exists: {backup_path}")
    
    # Open the file for read/write
    with open(lib_path, 'r+b') as f:
        data = bytearray(f.read())
        
        # Parse ELF header
        hdr = parse_elf64_header(data)
        print(f"ELF64 header parsed successfully")
        print(f"  Program header offset: {hdr['e_phoff']}")
        print(f"  Number of program headers: {hdr['e_phnum']}")
        print(f"  Section header offset: {hdr['e_shoff']}")
        print(f"  Number of sections: {hdr['e_shnum']}")
        
        # Step 1: Find PT_DYNAMIC program header
        dynamic_phdr = None
        phdrs = []  # Keep all program headers for address translation
        
        for i in range(hdr['e_phnum']):
            phdr_offset = hdr['e_phoff'] + i * ELF64_PHDR_SIZE
            phdr = parse_program_header(data, phdr_offset)
            phdrs.append(phdr)
            
            if phdr['p_type'] == PT_DYNAMIC:
                dynamic_phdr = phdr
                print(f"\nFound PT_DYNAMIC at program header index {i}")
                print(f"  File offset: {phdr['p_offset']}")
                print(f"  Virtual address: 0x{phdr['p_vaddr']:x}")
                print(f"  File size: {phdr['p_filesz']}")
        
        if not dynamic_phdr:
            raise ValueError("Could not find PT_DYNAMIC program header")
        
        # Step 2: Parse dynamic entries to find DT_SYMTAB, DT_STRTAB, etc.
        dynsym_vaddr = None
        dynstr_vaddr = None
        dynstr_size = None
        syment_size = None
        
        # Dynamic entries are at the file offset specified by PT_DYNAMIC
        # Note: If PT_DYNAMIC has p_filesz == 0, we need to calculate based on PT_LOAD
        dyn_file_offset = dynamic_phdr['p_offset']
        num_dyn_entries = dynamic_phdr['p_filesz'] // ELF64_DYN_SIZE
        
        print(f"\nParsing {num_dyn_entries} dynamic entries at file offset {dyn_file_offset}...")
        
        for i in range(num_dyn_entries):
            dyn_offset = dyn_file_offset + i * ELF64_DYN_SIZE
            dyn = parse_dynamic_entry(data, dyn_offset)
            
            if dyn['d_tag'] == DT_SYMTAB:
                dynsym_vaddr = dyn['d_val']
                print(f"  DT_SYMTAB: virtual address 0x{dynsym_vaddr:x}")
            elif dyn['d_tag'] == DT_STRTAB:
                dynstr_vaddr = dyn['d_val']
                print(f"  DT_STRTAB: virtual address 0x{dynstr_vaddr:x}")
            elif dyn['d_tag'] == DT_STRSZ:
                dynstr_size = dyn['d_val']
                print(f"  DT_STRSZ: {dynstr_size}")
            elif dyn['d_tag'] == DT_SYMENT:
                syment_size = dyn['d_val']
                print(f"  DT_SYMENT: {syment_size}")
            elif dyn['d_tag'] == DT_NULL:
                break
        
        if not dynsym_vaddr:
            raise ValueError("Could not find DT_SYMTAB in dynamic section")
        if not dynstr_vaddr:
            raise ValueError("Could not find DT_STRTAB in dynamic section")
        
        # Step 3: Convert virtual addresses to file offsets
        print(f"\nConverting virtual addresses to file offsets...")
        dynsym_file_offset = vaddr_to_file_offset(phdrs, dynsym_vaddr)
        dynstr_file_offset = vaddr_to_file_offset(phdrs, dynstr_vaddr)
        print(f"  .dynsym file offset: {dynsym_file_offset}")
        print(f"  .dynstr file offset: {dynstr_file_offset}")
        
        # Step 4: Get dynsym size from section headers
        # (DT_DYNAMIC doesn't have a direct entry for dynsym size)
        try:
            dynsym_size = get_dynsym_size_from_sections(data, hdr)
            print(f"  .dynsym size from section headers: {dynsym_size}")
        except ValueError as e:
            print(f"  Warning: {e}")
            # Fallback: estimate size as distance between dynsym and dynstr
            dynsym_size = dynstr_vaddr - dynsym_vaddr
            print(f"  Estimated .dynsym size: {dynsym_size}")
        
        # Verify symbol entry size
        if syment_size and syment_size != ELF64_SYM_SIZE:
            print(f"  Warning: DT_SYMENT is {syment_size}, expected {ELF64_SYM_SIZE}")
        
        # Step 5: Iterate through all symbols and hide matching ones
        num_symbols = dynsym_size // ELF64_SYM_SIZE
        print(f"\nScanning {num_symbols} dynamic symbols...")
        print(f"  Symbol table at file offset: {dynsym_file_offset}")
        print(f"  String table at file offset: {dynstr_file_offset}")
        
        hidden_count = 0
        already_hidden = 0
        hidden_symbols = []
        
        for i in range(num_symbols):
            sym_offset = dynsym_file_offset + i * ELF64_SYM_SIZE
            sym = parse_symbol_entry(data, sym_offset)
            
            # Get symbol name from string table
            if sym['st_name'] > 0:
                sym_name = get_string(data, dynstr_file_offset, sym['st_name'])
            else:
                sym_name = ""
            
            # Check if we should hide this symbol
            if sym_name and should_hide_symbol(sym_name):
                current_visibility = sym['st_other'] & 0x3
                
                if current_visibility != STV_HIDDEN:
                    # Set visibility to HIDDEN (bits 0-1 of st_other = 2)
                    old_other = sym['st_other']
                    new_other = (old_other & ~0x3) | STV_HIDDEN
                    
                    # Write the st_other byte directly
                    st_other_offset = sym_offset + 5  # st_other is at offset 5 within the symbol entry
                    data[st_other_offset] = new_other
                    
                    hidden_symbols.append(sym_name)
                    hidden_count += 1
                else:
                    already_hidden += 1
        
        # Write the modified data back to the file
        f.seek(0)
        f.write(data)
        f.truncate()
        
        total_scanned = num_symbols
        return total_scanned, hidden_count, hidden_symbols


def main():
    # Default path - relative to repo root
    default_path = 'game/bin/linuxsteamrt64/libengine2.so'
    
    # Allow override via command line
    if len(sys.argv) > 1:
        lib_path = sys.argv[1]
    else:
        lib_path = default_path
    
    # If relative path, resolve from current directory
    if not os.path.isabs(lib_path):
        # Try to find relative to script location
        script_dir = os.path.dirname(os.path.abspath(__file__))
        repo_root = os.path.dirname(script_dir)
        lib_path = os.path.join(repo_root, lib_path)
    
    print(f"ELF Symbol Visibility Patcher for libengine2.so (Program Header Version)")
    print(f"{'='*60}")
    print(f"Target: {lib_path}")
    print(f"{'='*60}\n")
    
    if not os.path.exists(lib_path):
        print(f"ERROR: File not found: {lib_path}")
        sys.exit(1)
    
    try:
        total_scanned, hidden_count, hidden_symbols = patch_engine2_openssl(lib_path)
        
        print(f"\n{'='*60}")
        print(f"PATCH SUMMARY")
        print(f"{'='*60}")
        print(f"Total symbols scanned: {total_scanned}")
        print(f"Total symbols hidden:  {hidden_count}")
        print(f"{'='*60}\n")
        
        if hidden_symbols:
            print("First 20 hidden symbol names:")
            for sym in hidden_symbols[:20]:
                print(f"  - {sym}")
            if len(hidden_symbols) > 20:
                print(f"  ... and {len(hidden_symbols) - 20} more")
        
        print(f"\nSuccess!")
        sys.exit(0)
    except Exception as e:
        print(f"\nERROR: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)


if __name__ == '__main__':
    main()
