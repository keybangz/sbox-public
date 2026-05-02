#!/bin/bash
# ns_launch.sh — runs inside a user+mount namespace to inject libs into /usr/share/dotnet/
# Called by run.sh via: unshare --user --mount --map-root-user bash ns_launch.sh <args>
# Env vars set by run.sh: NS_TMPDIR, SCRIPT_DIR

NS_TMPDIR="${NS_TMPDIR:-}"
SCRIPT_DIR="${SCRIPT_DIR:-}"
OVERLAY_WORK="${OVERLAY_WORK:-}"
OVERLAY_MERGED="${OVERLAY_MERGED:-}"

echo "[ns] Inside namespace (uid=$(id -u), euid=$(id -u))"

# Try overlayfs first: merges our libs on top of the real /usr/share/dotnet/
mount -t overlay overlay \
    -o "lowerdir=/usr/share/dotnet,upperdir=${NS_TMPDIR},workdir=${OVERLAY_WORK}" \
    "${OVERLAY_MERGED}" 2>/tmp/sbox_ns_mount.log

if mountpoint -q "${OVERLAY_MERGED}" 2>/dev/null; then
    mount --bind "${OVERLAY_MERGED}" /usr/share/dotnet 2>>/tmp/sbox_ns_mount.log
    if mountpoint -q /usr/share/dotnet 2>/dev/null; then
        echo "[ns] overlay+bind-mount succeeded on /usr/share/dotnet"
    else
        echo "[ns] overlay mount succeeded but bind-back failed: $(cat /tmp/sbox_ns_mount.log)"
    fi
else
    echo "[ns] overlay failed: $(cat /tmp/sbox_ns_mount.log)"
    echo "[ns] trying direct bind-mount of tmpdir over /usr/share/dotnet..."
    mount --bind "${NS_TMPDIR}" /usr/share/dotnet 2>/tmp/sbox_ns_mount2.log
    if mountpoint -q /usr/share/dotnet 2>/dev/null; then
        echo "[ns] direct bind-mount succeeded (dotnet runtime files hidden, only our libs visible)"
    else
        echo "[ns] direct bind-mount also failed: $(cat /tmp/sbox_ns_mount2.log)"
        echo "[ns] WARNING: /usr/share/dotnet/ injection failed — engine may not find render libs"
    fi
fi

# Verify
ls /usr/share/dotnet/librendersystemvulkan.so 2>/dev/null && echo "[ns] verified: librendersystemvulkan.so visible at /usr/share/dotnet/" || echo "[ns] librendersystemvulkan.so NOT visible at /usr/share/dotnet/"

# Re-exec run.sh inside this namespace
exec bash "${SCRIPT_DIR}/run.sh" "$@"
