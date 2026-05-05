#!/bin/bash
# rebuild.sh
# Kills any running sbox instances, runs a full Docker build, then runs post-deploy.
#
# Usage:
#   ./linux/rebuild.sh                    # full compile, NO artifact download (default)
#   ./linux/rebuild.sh --pull-artifacts   # full compile WITH artifact download
#   ./linux/rebuild.sh engine             # engine only, no artifacts
#   ./linux/rebuild.sh engine --pull-artifacts
#   ./linux/rebuild.sh shaders
#   ./linux/rebuild.sh content

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

COMMAND="compile"
PULL_ARTIFACTS=0

# Parse args
for arg in "$@"; do
	case "$arg" in
		--pull-artifacts)
			PULL_ARTIFACTS=1
			;;
		compile|all|engine|shaders|content|shell)
			COMMAND="$arg"
			;;
		*)
			echo "Unknown argument: $arg"
			echo "Usage: $0 [compile|engine|shaders|content] [--pull-artifacts]"
			exit 1
			;;
	esac
done

echo "=========================================="
echo "s&box Rebuild Helper"
if [ "$PULL_ARTIFACTS" == "1" ]; then
	echo "  Artifact download: ENABLED"
else
	echo "  Artifact download: DISABLED (pass --pull-artifacts to enable)"
fi
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

if [ "$PULL_ARTIFACTS" == "1" ]; then
	SBOX_SKIP_ARTIFACTS=0 ./sbox-public-linux-docker/sbox-install.sh "$COMMAND" ./
else
	SBOX_SKIP_ARTIFACTS=1 ./sbox-public-linux-docker/sbox-install.sh "$COMMAND" ./
fi

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
