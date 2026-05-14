#!/bin/bash
# Build and package sif.agent as a .NET global tool
# Usage: ./build.sh [install]

set -e

cd "$(dirname "$0")"
ROOT_DIR="$(cd .. && pwd)"
EXTENSION_SRC="$ROOT_DIR/sif.vscode"
EXTENSION_ID="sif.sif-vscode"
EXTENSION_VERSION="0.1.0"
EXTENSION_INSTALL_DIR="${VSCODE_EXTENSIONS:-$HOME/.vscode/extensions}/${EXTENSION_ID}-${EXTENSION_VERSION}"

echo "Building..."
dotnet build -c Release

# Clean temp dir
rm -rf /tmp/sif-pack
mkdir -p /tmp/sif-pack/tools/net10.0/any

# Copy all output files to tools directory
cp -r bin/Release/net10.0/* /tmp/sif-pack/tools/net10.0/any/ 2>/dev/null || true

# Explicitly copy BuildHost directories (required by Roslyn MSBuild workspace for diagnostics/find-symbols)
# These are subdirectories that can get missed by glob expansion in some cp implementations
for dir in bin/Release/net10.0/BuildHost-*; do
    [ -d "$dir" ] && cp -a "$dir" /tmp/sif-pack/tools/net10.0/any/
done

# Create DotnetToolSettings.xml (required for .NET global tools)
cat > /tmp/sif-pack/tools/net10.0/any/DotnetToolSettings.xml << 'SETTINGS'
<?xml version="1.0" encoding="utf-8"?>
<DotNetCliTool Version="1">
  <Commands>
    <Command Name="sif" EntryPoint="sif-agent.dll" Runner="dotnet" />
  </Commands>
</DotNetCliTool>
SETTINGS

# Copy readme
cp "$ROOT_DIR/README.md" /tmp/sif-pack/

# Create proper nuspec
cat > /tmp/sif-pack/sif.agent.nuspec << 'NUSPEC'
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd">
  <metadata>
    <id>sif.agent</id>
    <version>1.0.0</version>
    <authors>sif</authors>
    <license type="expression">MIT</license>
    <readme>README.md</readme>
    <description>A lightweight AI agent console tool supporting local models via OpenAI-compatible APIs.</description>
    <tags>ai agent llm local-model openai-compatible console-tool</tags>
    <packageTypes>
      <packageType name="DotnetTool" />
    </packageTypes>
    <repository type="git" url="https://github.com/sif/sif" />
  </metadata>
</package>
NUSPEC

# Create the package using zip (nupkg is just a zip with specific structure)
cd /tmp/sif-pack
rm -f sif.agent.1.0.0.nupkg
PACKAGE_VERSION=1.0.0
PACKAGE_ID=sif.agent
PACKAGE_DIR="$ROOT_DIR/sif.agent/nupkg"

# Create proper Open Packaging Convention structure
mkdir -p _rels
cat > '[Content_Types].xml' << 'TYPES'
<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
  <Default Extension="psmdcp" ContentType="application/vnd.openxmlformats-package.core-properties+xml" />
  <Default Extension="xml" ContentType="application/octet" />
  <Default Extension="dll" ContentType="application/octet" />
  <Default Extension="json" ContentType="application/octet" />
  <Default Extension="pdb" ContentType="application/octet" />
  <Default Extension="md" ContentType="application/octet" />
  <Default Extension="nuspec" ContentType="application/octet" />
</Types>
TYPES

cat > _rels/.rels << 'RELS'
<?xml version="1.0" encoding="utf-8"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Type="http://schemas.microsoft.com/packaging/2010/07/manifest" Target="/sif.agent.nuspec" Id="R1" />
</Relationships>
RELS

zip -r9 "${PACKAGE_ID}.${PACKAGE_VERSION}.nupkg" sif.agent.nuspec README.md '[Content_Types].xml' _rels tools/
rm -rf _rels
mkdir -p "$PACKAGE_DIR"
mv "${PACKAGE_ID}.${PACKAGE_VERSION}.nupkg" "$PACKAGE_DIR/"

echo ""
echo "Package created: nupkg/${PACKAGE_ID}.${PACKAGE_VERSION}.nupkg"
echo ""

if [ "$1" = "install" ]; then
    echo "Installing as global tool..."
    if dotnet tool list --global | grep -q "^$PACKAGE_ID[[:space:]]"; then
        dotnet tool uninstall --global "$PACKAGE_ID"
    fi
    rm -rf "$HOME/.nuget/packages/$PACKAGE_ID/$PACKAGE_VERSION"
    dotnet tool install --source "$PACKAGE_DIR" --version "$PACKAGE_VERSION" --no-http-cache --global "$PACKAGE_ID"
    echo ""
    echo "Installing VS Code extension..."
    if [ -d "$EXTENSION_SRC" ]; then
        rm -rf "$EXTENSION_INSTALL_DIR"
        mkdir -p "$EXTENSION_INSTALL_DIR"
        cp "$EXTENSION_SRC/package.json" "$EXTENSION_INSTALL_DIR/"
        cp "$EXTENSION_SRC/extension.js" "$EXTENSION_INSTALL_DIR/"
        cp "$EXTENSION_SRC/README.md" "$EXTENSION_INSTALL_DIR/"
        cp "$EXTENSION_SRC/.vscodeignore" "$EXTENSION_INSTALL_DIR/" 2>/dev/null || true
        echo "VS Code extension installed: $EXTENSION_INSTALL_DIR"
    else
        echo "Warning: VS Code extension source not found at $EXTENSION_SRC"
    fi
    echo ""
    echo "Installed! Run 'sif' to start chatting, or use 'sif: Start Chat With Editor Context' in VS Code."
fi

# Cleanup
rm -rf /tmp/sif-pack
