#!/bin/bash
# rebuild.sh
# Kills any running sbox instances, runs a full Docker build, then runs post-deploy.
#
# Usage:
#   ./linux/rebuild.sh           # full compile (engine + shaders + content)
#   ./linux/rebuild.sh engine    # engine only
#   ./linux/rebuild.sh shaders   # shaders only
#   ./linux/rebuild.sh content   # content only

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

COMMAND="${1:-compile}"

echo "=========================================="
echo "s&box Rebuild Helper"
echo "=========================================="
echo ""

# --- Step 1: Kill any running sbox instances ---
echo "[1/3] Stopping any running sbox instances..."
pkill -f "sbox-engine" 2>/dev/null || true
pkill -f "dotnet.*Sandbox" 2>/dev/null || true
pkill -f "run\.sh" 2>/dev/null || true
sleep 2
echo "  [OK] Processes cleared"

# --- Step 2: Docker build ---
echo ""
echo "[2/3] Running Docker build ($COMMAND)..."
cd "$REPO_ROOT"
./sbox-public-linux-docker/sbox-install.sh "$COMMAND" ./

# --- Step 3: Post-deploy (only on full compile or engine build) ---
if [[ "$COMMAND" == "compile" || "$COMMAND" == "all" || "$COMMAND" == "engine" ]]; then
	echo ""
	echo "[3/3] Running post-deploy..."
	./linux/post-docker-deploy.sh
else
	echo ""
	echo "[3/3] Skipping post-deploy (not needed for $COMMAND)"
fi

echo ""
echo "=========================================="
echo "Rebuild complete. Run the game with:"
echo "  cd linux && ./run.sh"
echo "=========================================="
