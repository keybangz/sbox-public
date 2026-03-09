#!/bin/bash
# Script to check for orphaned dotnet processes
# Run this AFTER closing the game to see what's left behind

echo "=========================================="
echo "Checking for dotnet-related processes..."
echo "=========================================="

echo ""
echo "=== All dotnet processes ==="
pgrep -af "dotnet" 2>/dev/null || echo "  (none found)"

echo ""
echo "=== VBCSCompiler processes ==="
pgrep -af "VBCSCompiler" 2>/dev/null || echo "  (none found)"

echo ""
echo "=== MSBuild processes ==="
pgrep -af "MSBuild" 2>/dev/null || echo "  (none found)"

echo ""
echo "=== Roslyn processes ==="
pgrep -af "roslyn" 2>/dev/null || echo "  (none found)"

echo ""
echo "=== sbox-related processes ==="
pgrep -af "sbox" 2>/dev/null || echo "  (none found)"

echo ""
echo "=== All processes with 'dotnet' in their command line ==="
ps aux | grep -i dotnet | grep -v grep || echo "  (none found)"

echo ""
echo "=== Process tree for any dotnet processes ==="
for pid in $(pgrep -f "dotnet" 2>/dev/null); do
    echo "Process tree for PID $pid:"
    pstree -p "$pid" 2>/dev/null || echo "  (could not get tree)"
    echo ""
done

echo "=========================================="
echo "Done checking processes"
echo "=========================================="

