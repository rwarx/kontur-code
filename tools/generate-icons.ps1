# Extracts the Linear (stroke) variant of each Iconsax icon and emits Icons.xaml
# entries on the same 24-grid the drawings were designed for.
$ErrorActionPreference = "Stop"
$src = "C:\Temp\opencode\iconsets\iconsax-react-0.0.8\package\dist\esm"

$map = [ordered]@{
    "Chat"         = "MessageText"
    "Canvas"       = "Element3"
    "Graph"        = "Hierarchy"
    "Files"        = "Folder2"
    "Code"         = "Code1"
    "Models"       = "Layer"
    "Tasks"        = "TaskSquare"
    "Memory"       = "Data"
    "Settings"     = "Setting2"
    "Search"       = "SearchNormal"
    "Command"      = "Command"
    "History"      = "RecoveryConvert"
    "Contrast"     = "Moon"
    "Folder"       = "Folder2"
    "File"         = "Document"
    "Link"         = "Link2"
    "Package"      = "Box"
    "Filter"       = "Filter"
    "Plus"         = "Add"
    "Paperclip"    = "Paperclip2"
    "ChevronUp"    = "ArrowUp2"
    "ChevronDown"  = "ArrowDown2"
    "ChevronLeft"  = "ArrowLeft2"
    "ChevronRight" = "ArrowRight2"
    "CollapsePanel" = "SidebarLeft"
    "ExpandPanel"  = "SidebarRight"
    "Refresh"      = "Refresh"
    "Undo"         = "ArrowRotateLeft"
    "Redo"         = "ArrowRotateRight"
    "ZoomIn"       = "SearchZoomIn"
    "ZoomOut"      = "SearchZoomOut"
    "Fit"          = "Maximize4"
    "Focus"        = "Gps"
    "Pointer"      = "Pointer"
    "Hand"         = "Mouse"
    "AutoLayout"   = "Bezier"
    "Pin"          = "Bookmark2"
    "Trash"        = "Trash"
    "Edit"         = "Edit2"
    "Copy"         = "Copy"
    "Export"       = "Export"
    "Save"         = "Save2"
    "Open"         = "ExportSquare"
    "Play"         = "Play"
    "Stop"         = "Stop"
    "Send"         = "Send"
    "More"         = "More"
    "Sparkle"      = "MagicStar"
    "Warning"      = "Danger"
    "Error"        = "CloseCircle"
    "Check"        = "TickSquare"
    "Info"         = "InfoCircle"
    "Eye"          = "Eye"
    "Clock"        = "Clock"
    "Bot"          = "CpuCharge"
    "User"         = "User"
    "Note"         = "Note"
    "Sun"          = "Sun1"
    "Moon"         = "Moon"
    "EyeOff"       = "EyeSlash"
}

$lines = New-Object System.Collections.Generic.List[string]
$missing = @()

foreach ($kind in $map.Keys) {
    $file = Join-Path $src ("{0}.js" -f $map[$kind])
    if (-not (Test-Path $file)) { $missing += "$kind -> $($map[$kind]) (file missing)"; continue }

    $text = Get-Content $file -Raw

    if ($text -match "var Linear = function Linear\(_ref\d*\) \{([\s\S]*?)\r?\n\};") {
        $block = $Matches[1]
        $ds = [regex]::Matches($block, 'd:\s*"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
        if ($ds.Count -eq 0) { $missing += "$kind -> $($map[$kind]) (no paths in Linear)"; continue }

        # Multiple subpaths concatenate into one geometry; every Iconsax path is
        # authored in the same 24-box, absolute-first.
        $combined = ($ds -join " ")
        $lines.Add('    <StreamGeometry x:Key="Icon.' + $kind + '" po:Freeze="True">' + $combined + '</StreamGeometry>') | Out-Null
    } else {
        $missing += "$kind -> $($map[$kind]) (Linear not found)"
    }
}

$header = @'
<!--
    Kontur Code design system - icon drawings.

    The set is Iconsax (Linear variant, by Vuesax - free license), drawn on one 24x24
    grid with 1.5px strokes and round caps and joins: the same shared grid the
    morphicons library builds its rotations on, which is why pairs of these icons can
    turn into each other. Geometry resources are frozen StreamGeometries; KonturIcon
    resolves "Icon.<Kind>" from this dictionary, MorphIcon folds one kind into another.

    Conventions:
      - The stroke weight lives with the consumer: 2.25 design units at this grid render
        as 1.5px at the 16px box, which is the weight the interface is tuned to.
      - Logo and Node are the two house drawings that predate the set and stay custom.
      - Close (the bare X) and the checkbox tick are house additions in the set's style;
        Iconsax only ships them boxed.
-->
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:po="http://schemas.microsoft.com/winfx/2006/xaml/presentation/options">

    <!-- Identity. The Kontur mark: a stem and a broken chevron (house drawing, 16-grid). -->
    <StreamGeometry x:Key="Icon.Logo" po:Freeze="True">M4.4 2.2 V13.8 M12.9 2.2 L5.3 8 L12.9 13.8</StreamGeometry>

    <!-- Node: the abstract graph node, four dots and the cross between them (house). -->
    <StreamGeometry x:Key="Icon.Node" po:Freeze="True">M7 5 A2 2 0 1 0 7.01 5 M17 5 A2 2 0 1 0 17.01 5 M7 19 A2 2 0 1 0 7.01 19 M17 19 A2 2 0 1 0 17.01 19 M8.5 6.5 L15.5 17.5 M15.5 6.5 L8.5 17.5</StreamGeometry>

    <!-- Close: the bare X in the set's style; Iconsax only ships it boxed. -->
    <StreamGeometry x:Key="Icon.Close" po:Freeze="True">M18 6 L6 18 M6 6 L18 18</StreamGeometry>

'@

$footer = @'
</ResourceDictionary>
'@

$output = $header + ($lines -join "`r`n") + "`r`n" + $footer
Set-Content -Path "src\AIClient.App\Resources\Design\Icons.xaml" -Value $output -NoNewline -Encoding UTF8

Write-Host "Wrote $($lines.Count) icons."
if ($missing.Count -gt 0) { Write-Host "MISSING:"; $missing | ForEach-Object { Write-Host "  $_" } }
