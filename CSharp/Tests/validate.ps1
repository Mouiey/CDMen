$ErrorActionPreference = 'Stop'

$modRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$runConfigPath = Join-Path $modRoot 'CSharp\RunConfig.xml'
$bookPath = Join-Path $modRoot 'content\items\book.xml'
$particlesPath = Join-Path $modRoot 'content\Particles\Particles.xml'
$pluginPath = Join-Path $modRoot 'CSharp\Client\EmitterFollowPlugin.cs'
$layerRuntimePath = Join-Path $modRoot 'CSharp\Client\EmitterItemLayerRuntime.cs'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-AttributeValue {
    param([System.Xml.XmlNode]$Node, [string]$Name)
    foreach ($attribute in $Node.Attributes) {
        if ($attribute.LocalName.Equals($Name, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $attribute.Value
        }
    }
    return $null
}

[xml]$runConfig = Get-Content -LiteralPath $runConfigPath -Raw -Encoding UTF8
Assert-True ($runConfig.RunConfig.Server -eq 'None') 'CSharp must remain disabled on the server.'
Assert-True ($runConfig.RunConfig.Client -eq 'Standard') 'CSharp client mode must be Standard.'

[xml]$book = Get-Content -LiteralPath $bookPath -Raw -Encoding UTF8
[xml]$particleDocument = Get-Content -LiteralPath $particlesPath -Raw -Encoding UTF8

$expected = [ordered]@{
    'magiclab_user_project_1yd8xfu_p_mtosgl3b7f' = @{ Depth = 0.6; Blend = 'AlphaBlend' }
    'magiclab_user_project_1yd8xfu_p_mtosgl3b7h' = @{ Depth = 0.6; Blend = 'Additive' }
    'magiclab_user_project_1yd8xfu_p_mtosgl3b7j' = @{ Depth = 0.45; Blend = 'AlphaBlend' }
    'magiclab_user_project_1yd8xfu_p_mtosgl3b7l' = @{ Depth = 0.45; Blend = 'Additive' }
    'magiclab_user_project_1yd8xfu_p_mtoskhx67s' = @{ Depth = 0.6; Blend = 'AlphaBlend' }
    'magiclab_user_project_1yd8xfu_p_mtoskhx67t' = @{ Depth = 0.6; Blend = 'Additive' }
    'magiclab_user_project_1yd8xfu_p_mtoskhx67u' = @{ Depth = 0.45; Blend = 'AlphaBlend' }
    'magiclab_user_project_1yd8xfu_p_mtoskhx67v' = @{ Depth = 0.45; Blend = 'Additive' }
}

$emitters = @($book.SelectNodes('//ParticleEmitter'))
foreach ($identifier in $expected.Keys) {
    $matches = @($emitters | Where-Object { (Get-AttributeValue $_ 'particle') -eq $identifier })
    Assert-True ($matches.Count -eq 1) "Expected exactly one emitter for '$identifier'."
    Assert-True ((Get-AttributeValue $matches[0] 'itemlayerdepth') -ieq 'true') `
        "Emitter '$identifier' must enable itemlayerdepth."
    Assert-True ((Get-AttributeValue $matches[0] 'draworder') -ieq 'Default') `
        "Emitter '$identifier' must use DrawOrder=Default."

    $prefab = $particleDocument.Particles.SelectSingleNode($identifier)
    Assert-True ($null -ne $prefab) "Particle prefab '$identifier' is missing."
    Assert-True ((Get-AttributeValue $prefab 'DrawTarget') -ieq 'Both') `
        "Particle '$identifier' must use DrawTarget=Both."
    Assert-True ((Get-AttributeValue $prefab 'DrawOrder') -ieq 'Default') `
        "Particle '$identifier' must use DrawOrder=Default."
    Assert-True ((Get-AttributeValue $prefab 'BlendState') -ieq $expected[$identifier].Blend) `
        "Particle '$identifier' has the wrong blend mode."

    $sprites = @($prefab.SelectNodes('./Sprite|./AnimatedSprite'))
    Assert-True ($sprites.Count -gt 0) "Particle '$identifier' must contain a Sprite."
    foreach ($sprite in $sprites) {
        $actualDepth = [float]::Parse(
            (Get-AttributeValue $sprite 'depth'),
            [Globalization.CultureInfo]::InvariantCulture)
        Assert-True ([Math]::Abs($actualDepth - $expected[$identifier].Depth) -lt 0.0001) `
            "Particle '$identifier' has unexpected Sprite depth '$actualDepth'."

        $texture = Get-AttributeValue $sprite 'texture'
        if ($texture -like '%ModDir%/*') {
            $relativeTexture = $texture.Substring('%ModDir%/'.Length).Replace('/', '\')
            Assert-True (Test-Path -LiteralPath (Join-Path $modRoot $relativeTexture)) `
                "Particle texture '$texture' is missing."
        }
    }
}

$pluginSource = Get-Content -LiteralPath $pluginPath -Raw -Encoding UTF8
$layerSource = Get-Content -LiteralPath $layerRuntimePath -Raw -Encoding UTF8
foreach ($requiredToken in @(
    'GameScreenDrawMap',
    'SubmarineDrawBack',
    'SubmarineDrawFront',
    'ParticleManagerDraw',
    'ParticleDraw',
    'particlesInCreationOrder',
    'spriteIndex'
)) {
    Assert-True ($pluginSource.Contains($requiredToken)) "Missing compatibility token '$requiredToken'."
}
Assert-True ($layerSource.Contains('CharacterLayerBoundary = 0.5f')) `
    'The item-layer boundary must remain 0.5.'
Assert-True ($layerSource.Contains('ParticlePrefab.DrawTargetType.Both')) `
    'DrawTarget=Both fallback guard is missing.'
Assert-True (-not (($pluginSource + $layerSource) -match 'HttpClient|WebRequest|ClientEvent|ServerEvent')) `
    'The client-only extension must not add network calls.'

Write-Host 'Validation passed: client boundary, draw hooks, 8 emitters, depths, blends, and textures.'
