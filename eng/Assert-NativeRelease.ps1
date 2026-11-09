param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'

# Read the PE import table without loading the DLL: developer runtimes can mask bad bundles.
$reader = [IO.BinaryReader]::new([IO.File]::OpenRead((Resolve-Path -LiteralPath $Path)))
try {
    $reader.BaseStream.Position = 0x3c
    $peOffset = $reader.ReadInt32()
    $reader.BaseStream.Position = $peOffset
    if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw "$Path is not Windows x64." }
    $sectionCount = $reader.ReadUInt16()
    $reader.BaseStream.Position = $peOffset + 20
    $optionalSize = $reader.ReadUInt16()
    $optionalStart = $peOffset + 24
    $reader.BaseStream.Position = $optionalStart
    if ($reader.ReadUInt16() -ne 0x20b) { throw "$Path is not PE32+." }
    $reader.BaseStream.Position = $optionalStart + 120
    $importRva = $reader.ReadUInt32()
    $importSize = $reader.ReadUInt32()
    $reader.BaseStream.Position = $optionalStart + $optionalSize
    $sections = @(for ($index = 0; $index -lt $sectionCount; $index++) {
        $start = $reader.BaseStream.Position
        $reader.BaseStream.Position = $start + 8
        $virtualSize = $reader.ReadUInt32()
        $virtualAddress = $reader.ReadUInt32()
        $rawSize = $reader.ReadUInt32()
        $rawOffset = $reader.ReadUInt32()
        [pscustomobject]@{ Rva=$virtualAddress; Size=[Math]::Max($virtualSize,$rawSize); Offset=$rawOffset }
        $reader.BaseStream.Position = $start + 40
    })
    function Get-FileOffset([uint32]$Rva) {
        foreach ($section in $sections) {
            if ($Rva -ge $section.Rva -and $Rva -lt $section.Rva + $section.Size) {
                return [long]($Rva - $section.Rva + $section.Offset)
            }
        }
        throw "Unmapped native import address in $Path."
    }
    if ($importSize -lt 20) { throw "$Path has no import table." }
    $importOffset = Get-FileOffset $importRva
    for ($offset = 0; $offset + 20 -le $importSize; $offset += 20) {
        $reader.BaseStream.Position = $importOffset + $offset + 12
        $nameRva = $reader.ReadUInt32()
        if ($nameRva -eq 0) { break }
        $reader.BaseStream.Position = Get-FileOffset $nameRva
        $name = ''
        while (($character = $reader.ReadByte()) -ne 0) { $name += [char]$character }
        if ($name -match '^(msvcp\d+|vcruntime\d+(_\d+)?|vcomp\d+|ucrtbase)d\.dll$') {
            throw "$Path imports $name, a Visual C++ Debug runtime that must not ship."
        }
    }
} finally { $reader.Dispose() }
Write-Host "Verified distributable x64 native dependency: $Path"
