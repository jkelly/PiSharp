#!/usr/bin/env bash
# Installs the exact .NET SDK pinned in global.json into a local directory (default: .dotnet)
# so contributors can build and test PiSharp without modifying system-wide SDK installations
# or relaxing global.json's deterministic offline contract.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
INSTALL_DIR="${1:-$REPO_ROOT/.dotnet}"
SDK_VERSION="$(node -e "console.log(JSON.parse(require('fs').readFileSync(process.argv[1],'utf8')).sdk.version)" "$REPO_ROOT/global.json")"

if [[ -z "$SDK_VERSION" ]]; then
  echo "global.json does not declare sdk.version" >&2
  exit 1
fi

mkdir -p "$INSTALL_DIR"
INSTALL_DIR="$(cd "$INSTALL_DIR" && pwd)"
if [[ -x "$INSTALL_DIR/dotnet" ]] && "$INSTALL_DIR/dotnet" --list-sdks 2>/dev/null | grep -q "^${SDK_VERSION} "; then
  echo ".NET SDK $SDK_VERSION is already installed in $INSTALL_DIR."
  echo "Activate in your shell:"
  echo "  export DOTNET_ROOT=\"$INSTALL_DIR\" && export PATH=\"\$DOTNET_ROOT:\$PATH\""
  exit 0
fi

echo "Downloading official Microsoft dotnet-install.sh for .NET SDK $SDK_VERSION..."
curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$INSTALL_DIR/dotnet-install.sh"
chmod +x "$INSTALL_DIR/dotnet-install.sh"
"$INSTALL_DIR/dotnet-install.sh" --version "$SDK_VERSION" --install-dir "$INSTALL_DIR" --no-path

echo ""
echo "Installed .NET SDK $SDK_VERSION to $INSTALL_DIR."
echo "Activate in your shell:"
echo "  export DOTNET_ROOT=\"$INSTALL_DIR\" && export PATH=\"\$DOTNET_ROOT:\$PATH\""
