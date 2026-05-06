#!/bin/bash
# Build and package picoNET.agent as a .NET global tool
# Usage: ./build.sh [install]

set -e

cd "$(dirname "$0")"

echo "Building..."
dotnet build -c Release

# Clean temp dir
rm -rf /tmp/piconet-pack
mkdir -p /tmp/piconet-pack/tools/net10.0/any

# Copy all output files to tools directory
cp bin/Release/net10.0/* /tmp/piconet-pack/tools/net10.0/any/ 2>/dev/null || true
cp -r bin/Release/net10.0/de /tmp/piconet-pack/tools/net10.0/any/ 2>/dev/null || true
cp -r bin/Release/net10.0/fr /tmp/piconet-pack/tools/net10.0/any/ 2>/dev/null || true
cp -r bin/Release/net10.0/sv /tmp/piconet-pack/tools/net10.0/any/ 2>/dev/null || true

# Create DotnetToolSettings.xml (required for .NET global tools)
cat > /tmp/piconet-pack/tools/net10.0/any/DotnetToolSettings.xml << 'SETTINGS'
<?xml version="1.0" encoding="utf-8"?>
<DotNetCliTool Version="1">
  <Commands>
    <Command Name="pico" EntryPoint="piconet-agent.dll" Runner="dotnet" />
  </Commands>
</DotNetCliTool>
SETTINGS

# Copy readme
cp ../README.md /tmp/piconet-pack/

# Create proper nuspec
cat > /tmp/piconet-pack/piconet.agent.nuspec << 'NUSPEC'
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd">
  <metadata>
    <id>piconet.agent</id>
    <version>1.0.0</version>
    <authors>picoNET</authors>
    <license type="expression">MIT</license>
    <readme>README.md</readme>
    <description>A lightweight AI agent console tool supporting local models via OpenAI-compatible APIs.</description>
    <tags>ai agent llm local-model openai-compatible console-tool</tags>
    <packageTypes>
      <packageType name="DotnetTool" />
    </packageTypes>
    <repository type="git" url="https://github.com/piconet/picoNET" />
  </metadata>
</package>
NUSPEC

# Create the package using zip (nupkg is just a zip with specific structure)
cd /tmp/piconet-pack
rm -f piconet.agent.1.0.0.nupkg

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
  <Relationship Type="http://schemas.microsoft.com/packaging/2010/07/manifest" Target="/piconet.agent.nuspec" Id="R1" />
</Relationships>
RELS

zip -r9 piconet.agent.1.0.0.nupkg piconet.agent.nuspec README.md '[Content_Types].xml' _rels tools/
rm -rf _rels
mkdir -p /home/lars/source/picoNET/picoNET.agent/nupkg
mv piconet.agent.1.0.0.nupkg /home/lars/source/picoNET/picoNET.agent/nupkg/

echo ""
echo "Package created: nupkg/piconet.agent.1.0.0.nupkg"
echo ""

if [ "$1" = "install" ]; then
    echo "Installing as global tool..."
    dotnet tool uninstall --global piconet.agent 2>/dev/null || true
    dotnet tool install --add-source /home/lars/source/picoNET/picoNET.agent/nupkg --global piconet.agent
    echo ""
    echo "Installed! Run 'pico' to start chatting."
fi

# Cleanup
rm -rf /tmp/piconet-pack
