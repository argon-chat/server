# Fills a local stand with two of everything, so the whole cosmetics surface can be exercised.
#
# Not a migration and deliberately not one. A migration seeds what every database should have; this
# seeds what a laptop needs to click through — placeholder art, everything free, everything
# published. Running it against anything but the local stand would put temporary layouts in a
# catalogue people pay from.
#
# Re-running it is harmless: every row is upserted by id and every object is written to the same key.

param(
    [string]$Postgres = "argon-postgres",
    [string]$Database = "argon",
    [string]$User     = "postgres",
    [string]$S3       = "http://localhost:9321/argon-dev",
    [string]$Assets   = "../client/packages/assets"
)

$ErrorActionPreference = "Stop"

# A file id with no row in "Files" still resolves: CdnRedirectFeature falls back to a flat key equal
# to the id, which is exactly what an upload straight into the bucket produces.
$ids = @{
    "background-rain"      = "dddddddd-0000-4000-8000-000000000001"
    "background-blackhole" = "dddddddd-0000-4000-8000-000000000002"
    "badge-owner"          = "dddddddd-0001-4000-8000-000000000001"
    "badge-staff"          = "dddddddd-0001-4000-8000-000000000002"
    "decoration-neon"      = "dddddddd-0002-4000-8000-000000000001"
    "decoration-sparks"    = "dddddddd-0002-4000-8000-000000000002"
    "decoration-orbit"      = "dddddddd-0003-4000-8000-000000000003"
    "orbit-raven"           = "dddddddd-0004-4000-8000-000000000001"
    "orbit-spiders"         = "dddddddd-0004-4000-8000-000000000002"
    "frame-gold"            = "ffffffff-0001-4000-8000-000000000001"
    "frame-circuit"         = "ffffffff-0001-4000-8000-000000000002"
    "frame-thorns-band"     = "ffffffff-0001-4000-8000-000000000011"
    "frame-thorns-skull"    = "ffffffff-0001-4000-8000-000000000012"
    "frame-thorns-tuft"     = "ffffffff-0001-4000-8000-000000000013"
    "effect-snow"           = "ffffffff-0002-4000-8000-000000000001"
    "effect-embers"         = "ffffffff-0002-4000-8000-000000000002"
    "scene-sakura-sheet"    = "ffffffff-0003-4000-8000-000000000001"
    "scene-sakura-tree"     = "ffffffff-0003-4000-8000-000000000002"
    "scene-sakura-still"    = "ffffffff-0003-4000-8000-000000000003"
    "scene-sakura-carpet"   = "ffffffff-0003-4000-8000-000000000004"
    "scene-sakura-glow"     = "ffffffff-0003-4000-8000-000000000005"
}

function Send-Object {
    param([string]$Key, [string]$Path, [string]$ContentType)

    Write-Host "  -> $Key  ($ContentType)"
    Invoke-WebRequest -Method Put -Uri "$S3/$Key" -InFile $Path -ContentType $ContentType -UseBasicParsing | Out-Null
}

function Send-Svg {
    param([string]$Key, [string]$Markup)

    $file = New-TemporaryFile

    try {
        [System.IO.File]::WriteAllText($file.FullName, $Markup, [System.Text.UTF8Encoding]::new($false))
        Send-Object -Key $Key -Path $file.FullName -ContentType "image/svg+xml"
    } finally {
        Remove-Item $file -Force
    }
}

$root = Split-Path -Parent $PSScriptRoot
$assetRoot = Join-Path $root $Assets

Write-Host "Uploading placeholder art to $S3"

Send-Object -Key $ids["background-rain"]      -Path (Join-Path $assetRoot "backgrounds/rain.webm")      -ContentType "video/webm"
Send-Object -Key $ids["background-blackhole"] -Path (Join-Path $assetRoot "backgrounds/blackhole.webm") -ContentType "video/webm"

# Drawn here rather than kept as files: they are stand-ins a designer replaces, and a stand-in that
# lives in the repository is one somebody eventually ships.
Send-Svg -Key $ids["badge-owner"] -Markup @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
  <path d="M12 2 3 7v6c0 5 3.8 8.4 9 9 5.2-.6 9-4 9-9V7z" fill="#f59e0b"/>
  <path d="m12 7 1.6 3.4 3.6.5-2.6 2.6.6 3.7-3.2-1.8-3.2 1.8.6-3.7L6.8 11l3.6-.5z" fill="#fff"/>
</svg>
'@

Send-Svg -Key $ids["badge-staff"] -Markup @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
  <circle cx="12" cy="12" r="10" fill="#3b82f6"/>
  <path d="m7 12.4 3.3 3.4L17 9" fill="none" stroke="#fff" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"/>
</svg>
'@

Send-Svg -Key $ids["decoration-neon"] -Markup @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
  <defs>
    <linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#06b6d4"/>
      <stop offset="1" stop-color="#8b5cf6"/>
    </linearGradient>
  </defs>
  <circle cx="50" cy="50" r="45" fill="none" stroke="url(#g)" stroke-width="6"/>
  <circle cx="50" cy="50" r="45" fill="none" stroke="#06b6d4" stroke-width="2" opacity="0.5"/>
</svg>
'@

Send-Svg -Key $ids["decoration-sparks"] -Markup @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
  <circle cx="50" cy="50" r="44" fill="none" stroke="#f59e0b" stroke-width="3" stroke-dasharray="6 5"/>
  <g fill="#fde68a">
    <path d="m50 0 3 8 8 3-8 3-3 8-3-8-8-3 8-3z"/>
    <path d="m95 42 2.2 6 6 2.2-6 2.2-2.2 6-2.2-6-6-2.2 6-2.2z"/>
    <path d="m8 62 2.2 6 6 2.2-6 2.2L8 80l-2.2-6L0 71.8l5.8-2.2z"/>
  </g>
</svg>
'@

# preserveAspectRatio="none" is what lets a frame stretch to a card of any width and still land its
# corners on the card's corners. The stroke is drawn inside the box so nothing is clipped.
Send-Svg -Key $ids["frame-gold"] -Markup @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 320 180" preserveAspectRatio="none" width="320" height="180">
  <defs>
    <linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#8a6116"/>
      <stop offset="0.45" stop-color="#f6c453"/>
      <stop offset="0.6" stop-color="#fff3b0"/>
      <stop offset="1" stop-color="#8a6116"/>
    </linearGradient>
  </defs>
  <rect x="1.5" y="1.5" width="317" height="177" rx="13" fill="none" stroke="url(#g)" stroke-width="3"/>
  <rect x="5" y="5" width="310" height="170" rx="10" fill="none" stroke="#fff3b0" stroke-width="0.6" opacity="0.5"/>
</svg>
'@

Send-Svg -Key $ids["frame-circuit"] -Markup @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 320 180" preserveAspectRatio="none" width="320" height="180">
  <rect x="1.5" y="1.5" width="317" height="177" rx="13" fill="none" stroke="#22d3ee" stroke-width="2.4" opacity="0.9"/>
  <g fill="none" stroke="#22d3ee" stroke-width="1.6" opacity="0.75">
    <path d="M18 1.5 v10 h26 v-10"/>
    <path d="M276 178.5 v-10 h26 v10"/>
    <path d="M1.5 58 h12 v26 h-12"/>
    <path d="M318.5 96 h-12 v26 h12"/>
  </g>
  <g fill="#a855f7">
    <circle cx="31" cy="11.5" r="2.6"/>
    <circle cx="289" cy="168.5" r="2.6"/>
    <circle cx="13.5" cy="71" r="2.6"/>
    <circle cx="306.5" cy="109" r="2.6"/>
  </g>
</svg>
'@

# An effect covers rather than stretches, so it is drawn square and cropped — otherwise the flakes
# would be ovals on a wide card.
Send-Svg -Key $ids["effect-snow"] -Markup @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 200" width="200" height="200">
  <style>
    .f { fill: #ffffff; opacity: 0.85 }
    .c { animation: fall 9s linear infinite }
    .c2 { animation-duration: 13s; animation-delay: -4s }
    .c3 { animation-duration: 17s; animation-delay: -8s }
    @keyframes fall { from { transform: translateY(-40px) } to { transform: translateY(240px) } }
  </style>
  <g class="c">
    <circle class="f" cx="18" cy="10" r="1.7"/><circle class="f" cx="74" cy="46" r="1.2"/>
    <circle class="f" cx="132" cy="18" r="1.9"/><circle class="f" cx="186" cy="62" r="1.3"/>
  </g>
  <g class="c c2">
    <circle class="f" cx="46" cy="24" r="1.4"/><circle class="f" cx="104" cy="8" r="1.8"/>
    <circle class="f" cx="158" cy="38" r="1.1"/><circle class="f" cx="12" cy="70" r="1.6"/>
  </g>
  <g class="c c3">
    <circle class="f" cx="88" cy="30" r="1.3"/><circle class="f" cx="146" cy="70" r="1.7"/>
    <circle class="f" cx="30" cy="52" r="1.1"/><circle class="f" cx="172" cy="14" r="1.5"/>
  </g>
</svg>
'@

Send-Svg -Key $ids["effect-embers"] -Markup @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 200" width="200" height="200">
  <style>
    .e { fill: #fb923c }
    .r { animation: rise 7s ease-in infinite }
    .r2 { animation-duration: 11s; animation-delay: -3s }
    .r3 { animation-duration: 15s; animation-delay: -7s }
    @keyframes rise {
      from { transform: translateY(40px); opacity: 0 }
      25%  { opacity: 0.9 }
      to   { transform: translateY(-220px); opacity: 0 }
    }
  </style>
  <g class="r">
    <circle class="e" cx="24" cy="190" r="1.8"/><circle class="e" cx="96" cy="176" r="1.2"/>
    <circle class="e" cx="168" cy="194" r="1.6"/>
  </g>
  <g class="r r2">
    <circle class="e" cx="58" cy="186" r="1.4"/><circle class="e" cx="130" cy="198" r="1.9"/>
    <circle class="e" cx="188" cy="170" r="1.1"/>
  </g>
  <g class="r r3">
    <circle class="e" cx="12" cy="196" r="1.3"/><circle class="e" cx="80" cy="192" r="1.7"/>
    <circle class="e" cx="150" cy="180" r="1.2"/>
  </g>
</svg>
'@

# The animated decoration: a ring that turns. CSS animations inside an SVG run when it is loaded
# through an <img>, which is what the avatar draws it with — so this needs no new rendering code.
Send-Svg -Key $ids["decoration-orbit"] -Markup @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
  <style>
    .turn { transform-origin: 50px 50px; animation: turn 6s linear infinite }
    .turn-back { transform-origin: 50px 50px; animation: turn 9s linear infinite reverse }
    @keyframes turn { to { transform: rotate(360deg) } }
  </style>
  <circle class="turn" cx="50" cy="50" r="45" fill="none" stroke="#22d3ee" stroke-width="3.5"
          stroke-linecap="round" stroke-dasharray="26 18" opacity="0.95"/>
  <circle class="turn-back" cx="50" cy="50" r="38" fill="none" stroke="#a855f7" stroke-width="2"
          stroke-linecap="round" stroke-dasharray="6 14" opacity="0.8"/>
</svg>
'@


# ── The raven: one strip of drawings, and the only thing the orbit needs ─────────────────────────
#
# Drawn here for the same reason the badges are — it is a stand-in a designer replaces, and a
# stand-in that lives in the repository is one somebody eventually ships. Its palette is the thorn
# frame's, so the two can be worn together and read as one set.
#
# Written as a cycle rather than as eight hand-placed poses: the wings sweep through one beat, the
# far one a fraction behind the near one, and the body lifts on the downstroke. Eight frames in one
# row, 64 square each, facing right — the renderer mirrors it for the half of the circuit it spends
# travelling the other way.

# Invariant, because this writes numbers into an SVG and a comma for a decimal point on a Russian
# machine would produce a file that parses as nothing.
function Fmt([double]$value) {
    return $value.ToString("0.##", [System.Globalization.CultureInfo]::InvariantCulture)
}

function New-RavenWing {
    param(
        [double]$ShoulderX, [double]$ShoulderY, [double]$Angle, [double]$Length,
        [string]$Fill, [string]$Edge, [switch]$Thorns
    )

    $rad = [Math]::PI / 180

    $tipX = $ShoulderX + ($Length * [Math]::Cos($Angle * $rad))
    $tipY = $ShoulderY - ($Length * [Math]::Sin($Angle * $rad))

    # The leading edge bows forward and the trailing edge cuts back, which is the whole difference
    # between a wing and a triangle.
    $leadX = $ShoulderX + ($Length * 0.62 * [Math]::Cos(($Angle + 17) * $rad))
    $leadY = $ShoulderY - ($Length * 0.62 * [Math]::Sin(($Angle + 17) * $rad))
    $backX = $ShoulderX + ($Length * 0.46 * [Math]::Cos(($Angle - 44) * $rad))
    $backY = $ShoulderY - ($Length * 0.46 * [Math]::Sin(($Angle - 44) * $rad))

    # The trailing edge is a zigzag rather than a curve: three primaries with notches cut between
    # them, which is the difference between a wing and a leaf.
    $d = "M$(Fmt $ShoulderX) $(Fmt $ShoulderY) Q$(Fmt $leadX) $(Fmt $leadY) $(Fmt $tipX) $(Fmt $tipY)"

    foreach ($point in @(-14, 0.74), @(-24, 0.94), @(-36, 0.57), @(-48, 0.81), @(-62, 0.43), @(-74, 0.64), @(-88, 0.26)) {
        $angle = ($Angle + $point[0]) * $rad
        $reach = $Length * $point[1]

        $d += " L$(Fmt ($ShoulderX + ($reach * [Math]::Cos($angle)))) $(Fmt ($ShoulderY - ($reach * [Math]::Sin($angle))))"
    }

    $d += " Z"

    $wing = "<path d=""$d"" fill=""$Fill"" stroke=""$Edge"" stroke-width=""0.7"" stroke-linejoin=""round""/>"

    if (-not $Thorns) {
        return $wing
    }

    # Two spikes off the leading edge, turned with the wing. They are what ties the bird to the
    # thorn frame — worn together the two read as one set rather than as two rows that happen to be
    # on at once.
    $spikes = ""

    foreach ($along in 0.34, 0.56, 0.78) {
        $baseX = $ShoulderX + ($Length * $along * [Math]::Cos(($Angle + 9) * $rad))
        $baseY = $ShoulderY - ($Length * $along * [Math]::Sin(($Angle + 9) * $rad))
        $outX  = $baseX + (5.4 * [Math]::Cos(($Angle + 72) * $rad))
        $outY  = $baseY - (5.4 * [Math]::Sin(($Angle + 72) * $rad))
        $endX  = $baseX + (3.1 * [Math]::Cos(($Angle + 9) * $rad))
        $endY  = $baseY - (3.1 * [Math]::Sin(($Angle + 9) * $rad))

        $spikes += "<path d=""M$(Fmt $baseX) $(Fmt $baseY) L$(Fmt $outX) $(Fmt $outY) L$(Fmt $endX) $(Fmt $endY) Z"" fill=""$Edge""/>"
    }

    return $wing + $spikes
}

function New-RavenStrip {
    $body    = "#0d0913"
    $nearW   = "#150e20"
    $farW    = "#06040c"
    $rim     = "#7b5fa8"
    $beak    = "#8b8fa0"
    $eye     = "#e11d48"

    $frames = @()

    for ($index = 0; $index -lt 8; $index++) {
        $phase = $index / 8
        $flap  = [Math]::Sin($phase * [Math]::PI * 2)

        # It rises on the downstroke, which is the one thing that stops a flapping sprite reading as
        # a picture with a moving part.
        $cy = 34 - (2.4 * $flap)

        $near = 8 + (46 * $flap)
        $far  = 8 + (46 * [Math]::Sin(($phase + 0.09) * [Math]::PI * 2))

        $parts = @()

        $parts += New-RavenWing -ShoulderX 31 -ShoulderY ($cy - 2) -Angle $far -Length 23 -Fill $farW -Edge $farW

        # Tail, ragged rather than fanned — a bird that has been in a fight.
        $parts += "<path d=""M22 $(Fmt $cy) L3 $(Fmt ($cy - 7.5)) L10 $(Fmt ($cy - 1)) L2 $(Fmt ($cy + 2.5)) " +
                  "L10.5 $(Fmt ($cy + 3.5)) L4 $(Fmt ($cy + 9.5)) Z"" fill=""$farW""/>"

        $parts += "<ellipse cx=""29"" cy=""$(Fmt $cy)"" rx=""14.5"" ry=""7"" fill=""$body"" transform=""rotate(-10 29 $(Fmt $cy))""/>"
        $parts += "<circle cx=""43.5"" cy=""$(Fmt ($cy - 6.6))"" r=""6.4"" fill=""$body""/>"

        # A hooked beak rather than a cone, and a brow over the eye. Between them they are the whole
        # difference between a bird and a bird that means it.
        $parts += "<path d=""M47.6 $(Fmt ($cy - 9)) L62 $(Fmt ($cy - 5.2)) L57.5 $(Fmt ($cy - 3)) " +
                  "L58.5 $(Fmt ($cy - 1.2)) L54 $(Fmt ($cy - 3.4)) L47.6 $(Fmt ($cy - 3.2)) Z"" fill=""$beak""/>"
        $parts += "<path d=""M39.5 $(Fmt ($cy - 10.4)) L49 $(Fmt ($cy - 10.8)) L48 $(Fmt ($cy - 8.2)) Z"" fill=""$farW""/>"

        $parts += "<circle cx=""45.2"" cy=""$(Fmt ($cy - 7.6))"" r=""1.85"" fill=""$eye""/>"
        $parts += "<circle cx=""45.7"" cy=""$(Fmt ($cy - 8.1))"" r=""0.6"" fill=""#ffd9e2""/>"

        # The rim light: one stroke along the back, and the only cold thing on the bird.
        $parts += "<path d=""M17 $(Fmt ($cy - 3.6)) Q29 $(Fmt ($cy - 9.4)) 39 $(Fmt ($cy - 11.2))"" " +
                  "fill=""none"" stroke=""$rim"" stroke-width=""1.1"" stroke-linecap=""round"" opacity=""0.7""/>"

        $parts += New-RavenWing -ShoulderX 32 -ShoulderY ($cy - 3) -Angle $near -Length 27 -Fill $nearW -Edge $rim -Thorns

        $frames += "  <g transform=""translate($($index * 64) 0)"">" + ($parts -join "") + "</g>"
    }

    return "<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 512 64"" width=""512"" height=""64"">`n" +
           ($frames -join "`n") + "`n</svg>`n"
}

Send-Svg -Key $ids["orbit-raven"] -Markup (New-RavenStrip)


# ── The spiders: the same eight frames, worn eight times over ────────────────────────────────────
#
# Drawn from above rather than from the side, which is the only view that reads at 20 per cent of a
# face: a side-on spider is a smudge with hairs, and a top-down one is a round thing with legs.
#
# One strip and one row. What makes eight of them a swarm rather than one picture repeated is that
# every satellite in the row has its own ring, its own period and its own phase — no two are ever in
# the same place at the same point of their gait.
# Where one leg is at a point in its cycle: how far back it has swept, and whether it is off the
# ground.
#
# <b>This is the whole difference between crawling and flapping, and it is not a detail.</b> A leg
# driven by a sine wave sweeps forward exactly as slowly as it sweeps back, which is what a wing
# does — the first version of this read as eight wings beating, and no amount of colour fixed it. A
# leg that walks is two unequal halves: planted and sweeping steadily back while the body passes over
# it, then lifted and thrown forward in half the time. The eye reads the difference immediately even
# when it cannot say what it is looking at.
function New-SpiderGait {
    param([double]$Phase)

    # Five frames of the eight on the ground, three in the air. More than half planted is what makes
    # the feet look like they are pushing rather than paddling.
    $planted = 0.625
    $point = $Phase - [Math]::Floor($Phase)

    if ($point -lt $planted) {
        return @(($point / $planted), 0.0)
    }

    $swing = ($point - $planted) / (1 - $planted)

    return @((1 - $swing), [Math]::Sin($swing * [Math]::PI))
}

function New-SpiderLeg {
    param(
        [double]$HipX, [double]$HipY, [double]$Forward, [double]$Back,
        [double]$Length, [double]$Side, [double]$Sweep, [double]$Lift,
        [string]$Outline, [string]$Core
    )

    $rad = [Math]::PI / 180
    $angle = $Forward + (($Back - $Forward) * $Sweep)

    # Off the ground the leg draws in and bends harder, which is what a lifted leg looks like from
    # above — there is no up to show it with.
    $reach = $Length * (1 - (0.26 * $Lift))
    $bend = 26 + (22 * $Lift)

    $footX = $HipX + ($reach * [Math]::Cos($angle * $rad))
    $footY = $HipY + ($Side * $reach * [Math]::Sin($angle * $rad))
    $kneeX = $HipX + ($reach * 0.6 * [Math]::Cos(($angle + $bend) * $rad))
    $kneeY = $HipY + ($Side * $reach * 0.6 * [Math]::Sin(($angle + $bend) * $rad))

    $d = "M$(Fmt $HipX) $(Fmt $HipY) Q$(Fmt $kneeX) $(Fmt $kneeY) $(Fmt $footX) $(Fmt $footY)"

    # Drawn twice: a dark stroke under a light one. A white leg on a light avatar is no leg at all,
    # and a spider without legs is a snowflake — which is what the first one was taken for.
    return "<path d=""$d"" fill=""none"" stroke=""$Outline"" stroke-width=""4"" stroke-linecap=""round""/>" +
           "<path d=""$d"" fill=""none"" stroke=""$Core"" stroke-width=""2.1"" stroke-linecap=""round""/>"
}

function New-SpiderStrip {
    $coat    = "#f7f6fc"
    $shade   = "#cec9e2"
    $outline = "#6f6892"
    $eye     = "#1b1626"

    # Where each pair of legs is rooted, how far out it reaches, and the two ends of its sweep.
    $hips     = 29, 32.5, 35.5, 38.5
    $forwards = 88, 48, 6, -36
    $backs    = 50, 10, -34, -76
    $spans    = 16.5, 18, 17, 15

    $frames = @()

    for ($index = 0; $index -lt 8; $index++) {
        $phase = $index / 8
        $parts = @()

        foreach ($side in -1, 1) {
            for ($pair = 0; $pair -lt 4; $pair++) {
                # An alternating tetrapod: four legs down while four swing, and the two sides out of
                # step, so the body is always standing on a tripod at either end.
                $group = ($pair + [Math]::Max(0, $side)) % 2
                $gait = New-SpiderGait -Phase ($phase + ($group * 0.5))

                $parts += New-SpiderLeg -HipX $hips[$pair] -HipY (32 + ($side * 3.2)) `
                                        -Forward $forwards[$pair] -Back $backs[$pair] `
                                        -Length $spans[$pair] -Side $side `
                                        -Sweep $gait[0] -Lift $gait[1] `
                                        -Outline $outline -Core $coat
            }
        }

        # Sideways only, and barely. A body that rises and falls is a body hopping; a crawling one
        # keeps its height and rocks a little as the tripods hand over.
        $sway = 0.5 * [Math]::Sin($phase * [Math]::PI * 2)

        $parts += "<ellipse cx=""21"" cy=""$(Fmt (32 + $sway))"" rx=""12"" ry=""10.5"" fill=""$coat"" stroke=""$outline"" stroke-width=""1.3""/>"
        $parts += "<path d=""M15 $(Fmt (27 + $sway)) q6 -2 11 1 M14 $(Fmt (36 + $sway)) q6 2 12 -1"" fill=""none"" stroke=""$shade"" stroke-width=""1.6"" stroke-linecap=""round""/>"
        $parts += "<ellipse cx=""31"" cy=""$(Fmt (32 + ($sway * 0.4)))"" rx=""3"" ry=""3.4"" fill=""$coat"" stroke=""$outline"" stroke-width=""1""/>"
        $parts += "<ellipse cx=""38.5"" cy=""$(Fmt (32 - $sway))"" rx=""8"" ry=""7"" fill=""$coat"" stroke=""$outline"" stroke-width=""1.3""/>"

        # The fangs. Two dark points at the front are the single mark that says spider rather than
        # beetle, ladybird or, at this size, snow.
        $parts += "<path d=""M45 $(Fmt (29.4 - $sway)) l3.6 1.4 -3.2 1 z M45 $(Fmt (34.6 - $sway)) l3.6 -1.4 -3.2 -1 z"" fill=""$outline""/>"

        $parts += "<circle cx=""42.6"" cy=""$(Fmt (28.8 - $sway))"" r=""2.7"" fill=""$eye""/>"
        $parts += "<circle cx=""42.6"" cy=""$(Fmt (35.2 - $sway))"" r=""2.7"" fill=""$eye""/>"
        $parts += "<circle cx=""43.5"" cy=""$(Fmt (28 - $sway))"" r=""1"" fill=""#ffffff""/>"
        $parts += "<circle cx=""43.5"" cy=""$(Fmt (34.4 - $sway))"" r=""1"" fill=""#ffffff""/>"
        $parts += "<circle cx=""38.6"" cy=""$(Fmt (26.4 - $sway))"" r=""1.25"" fill=""$eye""/>"
        $parts += "<circle cx=""38.6"" cy=""$(Fmt (37.6 - $sway))"" r=""1.25"" fill=""$eye""/>"

        # Blown up about the middle of its cell, because the drawing is what gets scaled onto a face
        # and the empty margin around it is not. Left at its natural size the spider came out half
        # the width the row asked for, which reads as "too small" and is answered by fixing the art
        # rather than by writing a bigger number in every row that ever uses it.
        $frames += "  <g transform=""translate($($index * 64) 0) translate(32 32) scale(1.3) translate(-32 -32)"">" +
                   ($parts -join "") + "</g>"
    }

    return "<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 512 64"" width=""512"" height=""64"">`n" +
           ($frames -join "`n") + "`n</svg>`n"
}

Send-Svg -Key $ids["orbit-spiders"] -Markup (New-SpiderStrip)


# ── The thorn frame: three files and no client code at all ───────────────────────────────────────
#
# The point of this one is what it is not. There is no thorn component, no thorn branch in any
# renderer and no thorn anything in the client — the shape, the overhang and the movement are all
# numbers in the row below, and these three pictures. A second frame is a second row.

# The band, drawn as a nine-slice tile: the outer 40px ring of a 128 square is what the frame is cut
# from, and the 48px middle of each edge is the piece that repeats along it.
#
# One edge and one corner are drawn, and the other six pieces are the same two turned about the
# centre. Symmetry by construction rather than by four hand-written copies that drift apart — and it
# is why the vine meets itself at every seam: the corner ends where the edge begins because they are
# the same two coordinates rotated.
Send-Svg -Key $ids["frame-thorns-band"] -Markup @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128" width="128" height="128">
  <defs>
    <linearGradient id="vine" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#c8b2e0"/>
      <stop offset="0.55" stop-color="#7d63a0"/>
      <stop offset="1" stop-color="#2e2440"/>
    </linearGradient>
    <linearGradient id="spike" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#ddd0ee"/>
      <stop offset="0.7" stop-color="#5b4780"/>
      <stop offset="1" stop-color="#241c2e"/>
    </linearGradient>
    <g id="edge">
      <path d="M40 28 C 50 20 58 34 68 26 S 82 19 88 28" fill="none"
            stroke="url(#vine)" stroke-width="7" stroke-linecap="round"/>
      <path d="M45 27 L51 2 L58 25 Z" fill="url(#spike)"/>
      <path d="M61 28 L68 6 L75 26 Z" fill="url(#spike)"/>
      <path d="M77 25 L83 4 L88 27 Z" fill="url(#spike)"/>
      <path d="M54 30 L57 40 L60 30 Z" fill="#3a2d4e"/>
      <path d="M70 29 L73 38 L76 29 Z" fill="#3a2d4e"/>
    </g>
    <g id="corner">
      <path d="M28 40 C 28 33 33 28 40 28" fill="none"
            stroke="url(#vine)" stroke-width="7" stroke-linecap="round"/>
      <path d="M27 36 L4 26 L24 21 Z" fill="url(#spike)"/>
      <path d="M34 30 L20 4 L40 19 Z" fill="url(#spike)"/>
      <path d="M36 37 L46 34 L38 42 Z" fill="#3a2d4e"/>
      <circle cx="31" cy="31" r="4.2" fill="#8d76ad"/>
      <circle cx="31" cy="31" r="1.8" fill="#e6dcf5"/>
    </g>
  </defs>
  <use href="#edge"/>
  <use href="#edge" transform="rotate(90 64 64)"/>
  <use href="#edge" transform="rotate(180 64 64)"/>
  <use href="#edge" transform="rotate(270 64 64)"/>
  <use href="#corner"/>
  <use href="#corner" transform="rotate(90 64 64)"/>
  <use href="#corner" transform="rotate(180 64 64)"/>
  <use href="#corner" transform="rotate(270 64 64)"/>
</svg>
'@

# The piece that sits on top of the card and hangs above it. Nothing in it knows that: where it goes
# is an anchor and two offsets in the row.
Send-Svg -Key $ids["frame-thorns-skull"] -Markup @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 56 48" width="56" height="48">
  <ellipse cx="28" cy="26" rx="23" ry="20" fill="#6d5a80" opacity="0.28"/>
  <path d="M11 13 L2 2 L15 8 Z" fill="#2b2036"/>
  <path d="M45 13 L54 2 L41 8 Z" fill="#2b2036"/>
  <path d="M28 4 C 15 4 8 13 8 23 C 8 29 11 33 15 35 L15 40 C 15 43 17 44 20 44
           L36 44 C 39 44 41 43 41 40 L41 35 C 45 33 48 29 48 23 C 48 13 41 4 28 4 Z"
        fill="#ded7e6"/>
  <ellipse cx="20" cy="23" rx="5.2" ry="6.2" fill="#241c2e"/>
  <ellipse cx="36" cy="23" rx="5.2" ry="6.2" fill="#241c2e"/>
  <path d="M28 29 L24.5 35 L31.5 35 Z" fill="#241c2e"/>
  <path d="M18 38 H38" stroke="#241c2e" stroke-width="1.6"/>
  <path d="M24 38 V44 M28 38 V44 M32 38 V44" stroke="#241c2e" stroke-width="1.3"/>
</svg>
'@

# A tuft of thorns, hung under the card twice with different phases — which is the whole argument for
# a phase field. The same tuft at the same period on both sides is one picture being animated; a
# second and a half between them is two things in the same draught.
Send-Svg -Key $ids["frame-thorns-tuft"] -Markup @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 44" width="40" height="44">
  <defs>
    <linearGradient id="spike" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#c8b2e0"/>
      <stop offset="0.6" stop-color="#5b4780"/>
      <stop offset="1" stop-color="#201929"/>
    </linearGradient>
  </defs>
  <g fill="url(#spike)">
    <path d="M20 4 L3 30 L17 18 Z"/>
    <path d="M20 4 L22 42 L28 20 Z"/>
    <path d="M20 4 L37 27 L25 18 Z"/>
  </g>
  <circle cx="20" cy="6" r="5" fill="#8d76ad"/>
  <circle cx="20" cy="6" r="2.1" fill="#e6dcf5"/>
</svg>
'@






# ── The sakura: a tree that draws itself, and the whole card its scene takes ─────────────────────
#
# The one scene the stand seeds, and the reason `profile.scene` grew spill, retreat and replay. The
# tree is generated rather than drawn: a recursion from one seed, so the same tree comes out every
# run. Each piece of wood is a stroked curve that lengthens from its base to its tip on its own
# clock, starting when the wood it grows from has nearly reached it; blossom opens along the thin
# wood a breath after it arrives; the whole of it is done in about seven and a half seconds. The
# engine adds the rest — the breathing, the spill past the card's edge, the retreat from the words,
# the petals, the carpet and the glow — from the row below.
#
# The still is the same tree with no animation in it, and stands in for the tree when motion is
# off: an animated file cannot be paused, so the only way to hold one still is to draw another.

$script:rng = [System.Random]::new(20260918)

$script:rng = [System.Random]::new(20260918)

function Rnd([double]$low, [double]$high) {
    return $low + ($script:rng.NextDouble() * ($high - $low))
}

function Pick($items) {
    return $items[$script:rng.Next(0, $items.Count)]
}

$rad = [Math]::PI / 180

# ── The tree ─────────────────────────────────────────────────────────────────────────────────────
#
# Canvas 480 x 640: wider than a card and a little taller, so that a card of ordinary height sees
# the whole crown and a tall one sees the tree closer rather than stretched. The trunk stands at
# (240, 560); roots go down to the bottom edge; the crown fills the top two thirds from side to
# side, and past the sides, which is what a branch hanging over the edge of a card is cut from.
#
# Angles are compass-style in screen space: -90 is up, 0 is right, 90 is down.

$bark     = "#46323b"
$barkLite = "#7a5866"
$rootInk  = "#30202a"
$tones    = @("#ffe3ec", "#ffc9da", "#ffb0c9", "#f997ba")
$cloud    = @("#f9b9cf", "#f4a7c2", "#f2a0bd")
$centre   = "#d94b7f"
$bud      = "#e6608f"

$script:paths    = New-Object System.Collections.Generic.List[string]
$script:clouds   = New-Object System.Collections.Generic.List[string]
$script:flowers  = New-Object System.Collections.Generic.List[string]
$script:finished = 0.0
$script:bloomed  = 0.0

function Add-Segment {
    param(
        [double]$X, [double]$Y, [double]$Angle, [double]$Length, [double]$Width,
        [int]$Level, [double]$Start, [string]$Ink, [bool]$Lit, [double]$Speed
    )

    $endX = $X + ($Length * [Math]::Cos($Angle * $rad))
    $endY = $Y + ($Length * [Math]::Sin($Angle * $rad))

    # A control point off the chord, so no branch is a ruler. The bow is stronger on thin wood.
    $bowAmount = (Rnd -0.24 0.24) * $Length * (0.6 + 0.1 * $Level)
    $midX = ($X + $endX) / 2 + ($bowAmount * [Math]::Cos(($Angle + 90) * $rad))
    $midY = ($Y + $endY) / 2 + ($bowAmount * [Math]::Sin(($Angle + 90) * $rad))

    $duration = 0.26 + ($Length / $Speed)
    $d = "M$(Fmt $X) $(Fmt $Y) Q$(Fmt $midX) $(Fmt $midY) $(Fmt $endX) $(Fmt $endY)"

    $script:paths.Add("<path class=""b"" pathLength=""1"" d=""$d"" stroke=""$Ink"" stroke-width=""$(Fmt $Width)"" style=""--d:$(Fmt $Start)s;--t:$(Fmt $duration)s""/>")

    # One lit edge along the thick wood, which is the whole of its volume. Offset up-left, towards
    # the light every blossom tone was picked under.
    if ($Lit) {
        $dl = "M$(Fmt ($X - 0.22 * $Width)) $(Fmt ($Y - 0.16 * $Width)) Q$(Fmt ($midX - 0.22 * $Width)) $(Fmt ($midY - 0.16 * $Width)) $(Fmt ($endX - 0.22 * $Width)) $(Fmt ($endY - 0.16 * $Width))"
        $script:paths.Add("<path class=""b l"" pathLength=""1"" d=""$dl"" stroke=""$barkLite"" stroke-width=""$(Fmt ($Width * 0.26))"" style=""--d:$(Fmt $Start)s;--t:$(Fmt $duration)s""/>")
    }

    $end = $Start + $duration

    if ($end -gt $script:finished) { $script:finished = $end }

    return @{ X = $endX; Y = $endY; MidX = $midX; MidY = $midY; End = $end; Duration = $duration }
}

# A point along a quadratic curve, for putting blossom on the wood rather than beside it.
function Along([double]$t, [double]$x0, [double]$y0, [double]$cx, [double]$cy, [double]$x1, [double]$y1) {
    $u = 1 - $t

    # Each coordinate in its own variable: the comma binds tighter than the plus in PowerShell,
    # and inline the second term becomes an array the first is asked to add.
    $px = ($u * $u * $x0) + (2 * $u * $t * $cx) + ($t * $t * $x1)
    $py = ($u * $u * $y0) + (2 * $u * $t * $cy) + ($t * $t * $y1)

    return @($px, $py)
}

function Note-Bloom([double]$At) {
    if ($At + 0.62 -gt $script:bloomed) { $script:bloomed = $At + 0.62 }
}

function Add-Flower([double]$X, [double]$Y, [double]$Size, [double]$At) {
    $tone = $script:rng.Next(0, 4)
    $turn = $script:rng.Next(0, 3)
    $half = $Size / 2

    Note-Bloom $At
    $script:flowers.Add("<use href=""#f$tone$turn"" x=""$(Fmt ($X - $half))"" y=""$(Fmt ($Y - $half))"" width=""$(Fmt $Size)"" height=""$(Fmt $Size)"" class=""f"" style=""--d:$(Fmt $At)s""/>")
}

function Add-Bud([double]$X, [double]$Y, [double]$At) {
    Note-Bloom $At
    $script:flowers.Add("<circle cx=""$(Fmt $X)"" cy=""$(Fmt $Y)"" r=""$(Fmt (Rnd 1.7 2.6))"" fill=""$bud"" class=""f"" style=""--d:$(Fmt $At)s""/>")
}

# A soft mass of colour under the flowers. What makes a crown a cloud rather than a scatter of
# dots, and one shape each where the flowers over it are many. Filled with a radial gradient so it
# has no edge: a cloud with an outline is a balloon.
function Add-Cloud([double]$X, [double]$Y, [double]$Angle, [double]$Size, [double]$At) {
    $rx = $Size * (Rnd 0.85 1.15)
    $ry = $Size * (Rnd 0.7 0.95)

    # Sat a little above the wood rather than centred on it: blossom stands on a branch, and a
    # cloud centred on the branch line reads as the branch having been smeared.
    $cx = $X + (Rnd -6 6)
    $cy = $Y - (Rnd 2 9)

    Note-Bloom $At
    $script:clouds.Add("<ellipse cx=""$(Fmt $cx)"" cy=""$(Fmt $cy)"" rx=""$(Fmt $rx)"" ry=""$(Fmt $ry)"" fill=""url(#c$($script:rng.Next(0, 3)))"" transform=""rotate($(Fmt ($Angle + (Rnd -30 30))) $(Fmt $cx) $(Fmt $cy))"" class=""c"" style=""--d:$(Fmt $At)s""/>")
}

function Grow-Branch {
    param([double]$X, [double]$Y, [double]$Angle, [double]$Length, [double]$Width, [int]$Level, [double]$Start)

    $tip = Add-Segment -X $X -Y $Y -Angle $Angle -Length $Length -Width $Width -Level $Level -Start $Start -Ink $bark -Lit ($Level -le 2) -Speed 150

    $last = $Level -ge 5 -or $Width -lt 2

    # Blossom sits on the thin wood: along everything past the limbs, thicker towards the tips, and
    # a cluster at every tip. A soft cloud under each twig is what fills the crown; the flowers are
    # what the eye lands on. Counts are kept low and the flowers large — the crown has to read as
    # full at a fifth of this size on a phone, and a thousand shapes re-rasterised sixty times a
    # second is a phone on fire.
    # Clouds along everything past the limbs: big ones on the thick wood, where the crown's mass
    # is, small ones out on the twigs. The count of flowers is what is expensive; the clouds are
    # one shape each, and they are what the crown is made of from across a room.
    if ($Level -ge 2) {
        $big = $Level -le 3
        $point = Along 0.78 $X $Y $tip.MidX $tip.MidY $tip.X $tip.Y
        $size = if ($big) { Rnd 32 46 } else { Rnd 18 26 }

        Add-Cloud -X $point[0] -Y $point[1] -Angle $Angle -Size $size -At ($Start + ($tip.Duration * 0.7) + (Rnd 0.1 0.6))

        if ($Level -eq 2) {
            $point = Along 0.4 $X $Y $tip.MidX $tip.MidY $tip.X $tip.Y
            Add-Cloud -X $point[0] -Y $point[1] -Angle $Angle -Size (Rnd 24 34) -At ($Start + ($tip.Duration * 0.4) + (Rnd 0.1 0.6))
        }
    }

    if ($Level -ge 3) {
        $count = if ((Rnd 0 1) -lt 0.35) { 2 } else { 1 }

        for ($index = 0; $index -lt $count; $index++) {
            $t = Rnd 0.3 1.0
            $point = Along $t $X $Y $tip.MidX $tip.MidY $tip.X $tip.Y
            $side = Rnd -9 9
            $bloomAt = $Start + ($tip.Duration * $t) + (Rnd 0.35 1.5)

            Add-Flower -X ($point[0] + $side * [Math]::Cos(($Angle + 90) * $rad)) -Y ($point[1] + $side * [Math]::Sin(($Angle + 90) * $rad)) -Size (Rnd 12 17) -At $bloomAt
        }
    }

    if ($last) {
        $count = $script:rng.Next(1, 3)

        for ($index = 0; $index -lt $count; $index++) {
            $reach = Rnd 0 14
            $way = Rnd 0 360
            $bloomAt = $tip.End + (Rnd 0.25 1.4)

            Add-Flower -X ($tip.X + $reach * [Math]::Cos($way * $rad)) -Y ($tip.Y + $reach * [Math]::Sin($way * $rad)) -Size (Rnd 12 18) -At $bloomAt
        }

        Add-Bud -X ($tip.X + (Rnd -10 10)) -Y ($tip.Y + (Rnd -10 10)) -At ($tip.End + (Rnd 0.1 0.8))

        return
    }

    # Two or three children, spread about the parent's heading and drooping as they thin: sakura
    # branches reach out and then hang, which is what makes a crown wide rather than a broom. The
    # count falls with the level, or the tree is a broom of a different kind: a thousand twigs.
    $children = if ($Level -le 1) { 3 } elseif ($Level -le 3) { if ((Rnd 0 1) -lt 0.4) { 3 } else { 2 } } else { if ((Rnd 0 1) -lt 0.5) { 2 } else { 1 } }
    $spread = 28 + (6 * $Level)

    for ($child = 0; $child -lt $children; $child++) {
        $slot = ($child + 0.5) / $children
        $turn = (($slot - 0.5) * 2 * $spread) + (Rnd -14 14)
        $angle = $Angle + $turn

        # Droop: heading is pulled towards the horizontal on the side it is already on, more so at
        # the tips. A branch going straight up keeps going up; one leaning out leans out further.
        $lean = [Math]::Cos($angle * $rad)
        $angle = $angle + ((0.12 + 0.06 * $Level) * 40 * $lean)

        # Nothing grows downwards past the horizontal on thick wood.
        if ($Level -le 2 -and $angle -gt -14 -and $angle -lt 90)  { $angle = -14 }
        if ($Level -le 2 -and $angle -lt -166 -and $angle -gt -270) { $angle = -166 }

        $nextLength = $Length * (Rnd 0.6 0.8)
        $nextWidth = $Width * (Rnd 0.58 0.7)

        # Children set off just before the parent has quite arrived, which is how wood actually
        # branches: the fork is there before the tip is.
        $nextStart = $Start + ($tip.Duration * (Rnd 0.72 0.92))

        Grow-Branch -X $tip.X -Y $tip.Y -Angle $angle -Length $nextLength -Width $nextWidth -Level ($Level + 1) -Start $nextStart
    }
}

function Grow-Root {
    param([double]$X, [double]$Y, [double]$Angle, [double]$Length, [double]$Width, [int]$Level, [double]$Start)

    $tip = Add-Segment -X $X -Y $Y -Angle $Angle -Length $Length -Width $Width -Level $Level -Start $Start -Ink $rootInk -Lit $false -Speed 130

    if ($Level -ge 3 -or $Width -lt 1.6) { return }

    $children = $script:rng.Next(2, 4)

    for ($child = 0; $child -lt $children; $child++) {
        $angle = $Angle + (Rnd -44 44)

        # Roots go down and out, never up: anything heading above the horizontal is bent back.
        if ($angle -lt 8) { $angle = 8 + (Rnd 0 20) }
        if ($angle -gt 172) { $angle = 172 - (Rnd 0 20) }

        Grow-Root -X $tip.X -Y $tip.Y -Angle $angle -Length ($Length * (Rnd 0.5 0.75)) -Width ($Width * (Rnd 0.5 0.65)) -Level ($Level + 1) -Start ($Start + $tip.Duration * (Rnd 0.7 0.95))
    }
}

function New-Symbols {
    $symbols = New-Object System.Collections.Generic.List[string]

    # One path of five petals and one centre: two shapes a flower, however many flowers there are.
    # Three turns of each tone, because a five-petalled thing repeats every 72 degrees and three
    # steps of 24 are as different as it gets.
    for ($tone = 0; $tone -lt 4; $tone++) {
        for ($turn = 0; $turn -lt 3; $turn++) {
            $d = ""

            for ($petal = 0; $petal -lt 5; $petal++) {
                $a = ($petal * 72 + $turn * 24) * $rad
                $cos = [Math]::Cos($a)
                $sin = [Math]::Sin($a)

                # The petal drawn pointing up, then turned about the centre by hand — one path, no
                # transforms, so a symbol is two elements and not six.
                $points = @(
                    @(10, 10), @(7.3, 8.7), @(5.5, 5.5), @(7.3, 3.3), @(8.3, 2.1), @(9.4, 2.4), @(10, 3.5),
                    @(10.6, 2.4), @(11.7, 2.1), @(12.7, 3.3), @(14.5, 5.5), @(12.7, 8.7), @(10, 10)
                )
                $turned = @()

                foreach ($p in $points) {
                    $dx = $p[0] - 10
                    $dy = $p[1] - 10
                    $turned += "$(Fmt (10 + $dx * $cos - $dy * $sin)) $(Fmt (10 + $dx * $sin + $dy * $cos))"
                }

                $d += "M$($turned[0]) C$($turned[1]) $($turned[2]) $($turned[3]) C$($turned[4]) $($turned[5]) $($turned[6]) C$($turned[7]) $($turned[8]) $($turned[9]) C$($turned[10]) $($turned[11]) $($turned[12]) Z"
            }

            $symbols.Add("<symbol id=""f$tone$turn"" viewBox=""0 0 20 20""><path d=""$d"" fill=""$($tones[$tone])"" stroke=""#c9557f"" stroke-opacity=""0.3"" stroke-width=""0.45""/><circle cx=""10"" cy=""10"" r=""1.8"" fill=""$centre""/></symbol>")
        }
    }

    # The clouds' fills: a colour that is solid at the middle and gone at the rim.
    for ($index = 0; $index -lt 3; $index++) {
        $symbols.Add("<radialGradient id=""c$index""><stop offset=""0"" stop-color=""$($cloud[$index])"" stop-opacity=""0.72""/><stop offset=""0.55"" stop-color=""$($cloud[$index])"" stop-opacity=""0.45""/><stop offset=""1"" stop-color=""$($cloud[$index])"" stop-opacity=""0""/></radialGradient>")
    }

    return $symbols -join ""
}

function New-Tree([bool]$Still) {
    $script:rng = [System.Random]::new(20260918)
    $script:paths.Clear()
    $script:clouds.Clear()
    $script:flowers.Clear()
    $script:finished = 0.0
    $script:bloomed = 0.0

    $baseX = 240.0
    $baseY = 560.0

    # Roots first: five of them, down and out from the base, on the ground before anything rises.
    foreach ($angle in 28, 62, 92, 120, 152) {
        Grow-Root -X ($baseX + (Rnd -6 6)) -Y ($baseY - 8) -Angle ($angle + (Rnd -8 8)) -Length (Rnd 58 92) -Width (Rnd 8 12) -Level 1 -Start (Rnd 0 0.35)
    }

    # The trunk: two pieces, the lower one wide where the roots meet it, with a lean that changes
    # its mind on the way up.
    $trunkTop = @{ X = $baseX; Y = $baseY + 4; End = 0.55 }
    $heading = -90 + (Rnd -5 5)
    $widths = 30, 22

    for ($piece = 0; $piece -lt 2; $piece++) {
        $heading = $heading + (Rnd -9 9)
        $trunkTop = Add-Segment -X $trunkTop.X -Y $trunkTop.Y -Angle $heading -Length (Rnd 62 76) -Width $widths[$piece] -Level 0 -Start ($trunkTop.End - 0.12) -Ink $bark -Lit $true -Speed 150
    }

    # The crown: five limbs off the top of the trunk, the outer ones reaching wide — past the sides
    # of the picture, which is what a branch hanging over a card's edge is cut from.
    $limbs = @(
        @{ Angle = -164; Length = 168; Width = 14 },
        @{ Angle = -134; Length = 140; Width = 12.5 },
        @{ Angle = -98;  Length = 128; Width = 12 },
        @{ Angle = -62;  Length = 138; Width = 12.5 },
        @{ Angle = -22;  Length = 164; Width = 14 }
    )

    foreach ($limb in $limbs) {
        Grow-Branch -X $trunkTop.X -Y $trunkTop.Y -Angle ($limb.Angle + (Rnd -7 7)) -Length ($limb.Length + (Rnd -12 12)) -Width $limb.Width -Level 1 -Start ($trunkTop.End - 0.14)
    }

    # Two low limbs off the trunk itself, so the crown does not sit on a stalk.
    Grow-Branch -X ($baseX + 6) -Y ($baseY - 62) -Angle (-6 + (Rnd -6 6)) -Length 104 -Width 9 -Level 2 -Start 1.1
    Grow-Branch -X ($baseX - 8) -Y ($baseY - 96) -Angle (-176 + (Rnd -6 6)) -Length 92 -Width 8.5 -Level 2 -Start 1.35

    $style = if ($Still) {
        ".b{fill:none;stroke-linecap:round;stroke-linejoin:round}.l{opacity:.55}"
    } else {
        ".b{fill:none;stroke-linecap:round;stroke-linejoin:round;stroke-dasharray:1 2;stroke-dashoffset:1.02;animation:draw var(--t) cubic-bezier(.3,.5,.45,1) var(--d) both}" +
        ".l{opacity:.55}" +
        ".c{transform-box:fill-box;transform-origin:center;transform:scale(0);animation:pop .9s cubic-bezier(.2,.8,.4,1) var(--d) both}" +
        ".f{transform-box:fill-box;transform-origin:center;transform:scale(0);animation:pop .62s cubic-bezier(.3,1.5,.55,1) var(--d) both}" +
        "@keyframes draw{to{stroke-dashoffset:0}}@keyframes pop{from{transform:scale(0)}to{transform:scale(1)}}"
    }

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 480 640"" width=""480"" height=""640"">")
    $lines.Add("<style>$style</style>")
    $lines.Add("<defs>" + (New-Symbols) + "</defs>")
    $lines.Add("<g>" + ($script:paths -join "") + "</g>")
    $lines.Add("<g>" + ($script:clouds -join "") + "</g>")
    $lines.Add("<g>" + ($script:flowers -join "") + "</g>")
    $lines.Add("</svg>")

    return ($lines -join "`n") + "`n"
}

Send-Svg -Key $ids["scene-sakura-tree"] -Markup (New-Tree $false)
Send-Svg -Key $ids["scene-sakura-still"] -Markup (New-Tree $true)

Write-Host ("  sakura: {0} wood, {1} clouds, {2} blossom; wood done at {3}s, bloom done at {4}s" -f $script:paths.Count, $script:clouds.Count, $script:flowers.Count, (Fmt $script:finished), (Fmt $script:bloomed))

# ── The petals: four in a row, 64 square each, cut from one sheet ────────────────────────────────

function New-Petal([string]$Fill, [string]$Deep, [int]$Cell) {
    $x = $Cell * 64
    return "<g transform=""translate($x 0)""><path d=""M32 56 C14 44 8 24 20 12 C25 7 30 9 32 15 C34 9 39 7 44 12 C56 24 50 44 32 56 Z"" fill=""$Fill"" stroke=""$Deep"" stroke-width=""1.2"" stroke-opacity=""0.35""/><path d=""M32 52 C24 40 22 28 27 18"" fill=""none"" stroke=""$Deep"" stroke-width=""1.4"" stroke-opacity=""0.35"" stroke-linecap=""round""/></g>"
}

$sheet = "<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 256 64"" width=""256"" height=""64"">" +
    (New-Petal "#ffd6e3" "#d2618a" 0) + (New-Petal "#ffbfd4" "#c9557f" 1) + (New-Petal "#ffa6c4" "#c04d78" 2) + (New-Petal "#f78fb3" "#b3436c" 3) +
    "</svg>`n"

Send-Svg -Key $ids["scene-sakura-sheet"] -Markup $sheet

# ── The carpet: fallen petals along the ground, as a nine-slice strip ────────────────────────────
#
# 96 wide: a 24px piece at either end and a 48px middle that repeats along the card. The petals
# thin out towards the top of the strip so the carpet has a soft upper edge rather than a ruled one.

$script:rng = [System.Random]::new(7)
$lying = ""

for ($index = 0; $index -lt 40; $index++) {
    $x = Rnd 2 94
    $y = 30 - [Math]::Pow((Rnd 0 1), 1.8) * 22
    $size = Rnd 2.6 4.6
    $tone = Pick @("#ffd6e3", "#ffbfd4", "#ffa6c4", "#f78fb3")
    $lying += "<ellipse cx=""$(Fmt $x)"" cy=""$(Fmt $y)"" rx=""$(Fmt $size)"" ry=""$(Fmt ($size * 0.6))"" fill=""$tone"" transform=""rotate($(Fmt (Rnd 0 180)) $(Fmt $x) $(Fmt $y))"" opacity=""$(Fmt (Rnd 0.65 1))""/>"
}

$carpet = "<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 96 32"" width=""96"" height=""32"">$lying</svg>`n"
Send-Svg -Key $ids["scene-sakura-carpet"] -Markup $carpet

# ── The glow: a soft light the bloom brings with it ───────────────────────────────────────────────

$glow = "<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 320 320"" width=""320"" height=""320""><defs><radialGradient id=""g"" cx=""0.5"" cy=""0.42"" r=""0.55""><stop offset=""0"" stop-color=""#ffd9e6"" stop-opacity=""0.95""/><stop offset=""0.45"" stop-color=""#ffb8cf"" stop-opacity=""0.5""/><stop offset=""1"" stop-color=""#ffb8cf"" stop-opacity=""0""/></radialGradient></defs><rect width=""320"" height=""320"" fill=""url(#g)""/></svg>`n"
Send-Svg -Key $ids["scene-sakura-glow"] -Markup $glow

# ── end of the sakura ──


# Everything free and published: a stand is for trying the mechanism, not the till.
#
# Interpolating here-string, deliberately — the asset ids below are read out of $ids. The cost is
# that a backtick is an escape character all the way down, including inside the SQL comments: a
# `repeat` written the way one would write it in prose becomes a carriage return and the word
# "epeat", which cuts the statement in half and produces `syntax error at or near "epeat"` from a
# line that looks nothing like the one you wrote. Quote identifiers in these comments, do not
# backtick them.
$sql = @"
UPDATE "Cosmetics" SET
    "AssetFileIds" = '{"Primary": "$($ids["background-rain"])"}'::jsonb,
    "IsPublished" = true, "AcquisitionMode" = 32, "UpdatedAt" = now()
WHERE "KindKey" = 'profile.background' AND "Slug" = 'rain';

UPDATE "Cosmetics" SET
    "AssetFileIds" = '{"Primary": "$($ids["background-blackhole"])"}'::jsonb,
    "IsPublished" = true, "AcquisitionMode" = 32, "UpdatedAt" = now()
WHERE "KindKey" = 'profile.background' AND "Slug" = 'blackhole';

UPDATE "Cosmetics" SET
    "AssetFileIds" = '{"Primary": "$($ids["badge-owner"])"}'::jsonb,
    "IsPublished" = true, "AcquisitionMode" = 32, "UpdatedAt" = now()
WHERE "KindKey" = 'profile.badge' AND "Slug" = 'owner';

UPDATE "Cosmetics" SET
    "AssetFileIds" = '{"Primary": "$($ids["badge-staff"])"}'::jsonb,
    "IsPublished" = true, "AcquisitionMode" = 32, "UpdatedAt" = now()
WHERE "KindKey" = 'profile.badge' AND "Slug" = 'staff';

-- The activity card is gone as a kind: it repeated the activity line the profile already shows and
-- had nothing for its wearer to fill in. Its equips go first, or the catalogue rows cannot.
DELETE FROM "CosmeticEquips" WHERE "KindKey" = 'widget.activity';
DELETE FROM "Cosmetics" WHERE "KindKey" = 'widget.activity';

INSERT INTO "Cosmetics" (
    "Id", "KindKey", "Slug", "NameKey", "DescriptionKey", "Rarity", "SortOrder", "Version",
    "Payload", "AssetFileIds", "AssetSource", "IsEnabled", "IsPublished", "AcquisitionMode",
    "LegacyId", "CreatedAt", "UpdatedAt", "IsDeleted")
VALUES
    ('dddddddd-0003-4000-8000-000000000001', 'avatar.decoration', 'neon-ring', 'cosmetic_decoration_neon_ring', NULL, 'rare', 1, 1,
     '{"insetPct":12,"beneath":false}', '{"Primary": "$($ids["decoration-neon"])"}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),
    ('dddddddd-0003-4000-8000-000000000002', 'avatar.decoration', 'sparks', 'cosmetic_decoration_sparks', NULL, 'legendary', 2, 1,
     '{"insetPct":14,"beneath":false}', '{"Primary": "$($ids["decoration-sparks"])"}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),
    ('dddddddd-0005-4000-8000-000000000001', 'widget.note', 'note', 'cosmetic_widget_note', 'cosmetic_widget_note_desc', NULL, 1, 1,
     '{}', '{}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),
    ('dddddddd-0005-4000-8000-000000000002', 'widget.tags', 'tags', 'cosmetic_widget_tags', 'cosmetic_widget_tags_desc', NULL, 2, 1,
     '{}', '{}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),

    -- The one card that is not words, so a board can be judged as a layout rather than as a wall of
    -- text. One to a board: several pictures is an album, and an album is its own thing.
    ('dddddddd-0005-4000-8000-000000000003', 'widget.picture', 'picture', 'cosmetic_widget_picture', 'cosmetic_widget_picture_desc', NULL, 3, 1,
     '{}', '{}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),

    -- A decoration that moves. The animation is CSS inside the SVG, which runs when the file is
    -- loaded through an <img> — so an animated ornament costs a file and no rendering code.
    ('dddddddd-0003-4000-8000-000000000003', 'avatar.decoration', 'orbit', 'cosmetic_decoration_orbit', NULL, 'legendary', 3, 1,
     '{"insetPct":16,"beneath":false}', '{"Primary": "$($ids["decoration-orbit"])"}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),

    -- The orbit, and the one thing on this whole list that no layer can do: the cat goes behind the
    -- head. It is the same picture drawn twice — once cut to the shape of the face and shown only
    -- for the half of the lap it spends at the back — and it costs no rendering code either, the
    -- same as everything else here. The ring is where its plane lies rather than an ellipse on the
    -- screen: an ellipse would draw this exact picture and could not say which half of it is nearer.
    --
    -- The numbers are what make it read as a bird on a sphere rather than a sticker on a circle.
    -- radiusPct just past the edge of the face (50) and a middling tiltDeg put its near half across
    -- the picture and its far half behind the head, high enough that a wing still shows over the
    -- top. yawSwingDeg rocks the ring, which is what turns one circle into a sphere. It is drawn
    -- most of the size of the face on purpose: a small one reads as a mark rather than as a thing.
    ('dddddddd-0006-4000-8000-000000000001', 'avatar.orbit', 'thorn-raven', 'cosmetic_orbit_thorn_raven', NULL, 'legendary', 1, 1,
     '{"minSizePx":0,"satellites":[
        {"type":"satellite","slot":"Primary",
         "sprite":{"frames":8,"columns":8,"fps":12,"still":2},
         "radiusPct":85,"tiltDeg":71,"yawDeg":0,"yawSwingDeg":34,"yawSwingMs":13000,
         "periodMs":5600,"phaseMs":0,"stillDeg":90,
         "widthPct":60,"heightPct":60,"leanDeg":0,"faceTravel":true,
         "nearScale":1.18,"farScale":0.76,"dim":0.38,"haze":0.18,"blurPct":0.8,"occlude":true,
         "bobPct":2.5,"bobPeriodMs":620}
      ]}',
     '{"Primary": "$($ids["orbit-raven"])"}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),

    -- The other shape the same kind takes, and the reason the cap is eight rather than four: one
    -- strip of drawings worn eight times, each on its own ring at its own angle, its own period and
    -- its own phase. Nothing is shared but the picture, so no two are ever in the same place at the
    -- same point of their gait — which is the whole difference between a swarm and one animation
    -- repeated. The periods are deliberately awkward numbers: round ones come back into step.
    --
    -- stillDeg is spread round the circle as well. Eight of them at the same rest pose would stack
    -- in one heap the moment anybody turns motion off.
    --
    -- Every ring leans between 45 and 135 degrees on purpose: near zero is the raven's own route
    -- across the face, and two cosmetics that travel the same line read as one idea twice. These go
    -- up and down the face and across it on the diagonal instead.
    --
    -- leanDeg matches each ring's own lean and the turn is a spin rather than a mirror, because
    -- these are drawn from above. A mirror would leave a spider pointing down a vertical ring still
    -- pointing down, crawling backwards for half of every lap; and leanDeg is also the axis the turn
    -- is measured along, so setting it to the ring puts the turn where the spider goes behind the
    -- head rather than in the middle of the face.
    ('dddddddd-0006-4000-8000-000000000002', 'avatar.orbit', 'snow-spiders', 'cosmetic_orbit_snow_spiders', NULL, 'rare', 2, 1,
     '{"minSizePx":0,"satellites":[
        {"type":"satellite","slot":"Primary","sprite":{"frames":8,"columns":8,"fps":8,"still":0},
         "radiusPct":76,"tiltDeg":78,"yawDeg":90,"yawSwingDeg":18,"yawSwingMs":11000,
         "periodMs":9000,"phaseMs":0,"stillDeg":90,"widthPct":42,"heightPct":42,
         "faceTravel":true,"turn":"spin","leanDeg":90,
         "nearScale":1.12,"farScale":0.82,"dim":0.3,"haze":0.14,"blurPct":0.4,"occlude":true,"bobPct":0,"bobPeriodMs":600},
        {"type":"satellite","slot":"Primary","sprite":{"frames":8,"columns":8,"fps":8,"still":0},
         "radiusPct":73,"tiltDeg":73,"yawDeg":52,"yawSwingDeg":14,"yawSwingMs":14000,
         "periodMs":12000,"phaseMs":3400,"reverse":true,"stillDeg":200,"widthPct":36,"heightPct":36,
         "faceTravel":true,"turn":"spin","leanDeg":52,
         "nearScale":1.12,"farScale":0.82,"dim":0.3,"haze":0.14,"blurPct":0.4,"occlude":true,"bobPct":0,"bobPeriodMs":600},
        {"type":"satellite","slot":"Primary","sprite":{"frames":8,"columns":8,"fps":8,"still":0},
         "radiusPct":75,"tiltDeg":80,"yawDeg":128,"yawSwingDeg":16,"yawSwingMs":9500,
         "periodMs":10500,"phaseMs":6100,"stillDeg":320,"widthPct":39,"heightPct":39,
         "faceTravel":true,"turn":"spin","leanDeg":128,
         "nearScale":1.12,"farScale":0.82,"dim":0.3,"haze":0.14,"blurPct":0.4,"occlude":true,"bobPct":0,"bobPeriodMs":600}
      ]}',
     '{"Primary": "$($ids["orbit-spiders"])"}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),

    -- Over the card rather than under it, which is the whole difference between these and a
    -- background: a frame's corners are the card's corners, and an effect is weather in front of it.
    -- The two frames that are only a coloured edge. That shape is still worth having and still costs
    -- no file; it is now one part a frame may contain rather than the only thing a frame may be, so
    -- the same border is written as a list of one.
    ('ffffffff-0001-4000-8000-000000000001', 'profile.frame', 'gilded', 'cosmetic_frame_gilded', NULL, 'legendary', 1, 1,
     '{"parts":[{"type":"ring","thickness":3,"colors":[-7708394,-605101,-3152,-7708394],"angle":135,"glow":0}]}',
     '{}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),
    ('ffffffff-0001-4000-8000-000000000002', 'profile.frame', 'circuit', 'cosmetic_frame_circuit', NULL, 'rare', 2, 1,
     '{"parts":[{"type":"ring","thickness":2,"colors":[-14494738,-5745161],"angle":45,"glow":0.8}]}',
     '{}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),

    -- The frame with a shape. Four parts: a band cut from a nine-slice tile that hangs 12px outside
    -- the card on every side and lies 18px over it, a skull sitting on the top edge with a third of
    -- itself above the card, and the same tuft of thorns hung under the card twice — a second and a
    -- half out of step, which is what stops two copies of one picture reading as one picture.
    --
    -- Nothing in the client knows any of that. Change "width" to [30,0,30,0] and the frame is a band
    -- along the top and bottom only; drop the second part and the skull is gone. Both are edits to
    -- this row, and neither is a release.
    ('ffffffff-0001-4000-8000-000000000003', 'profile.frame', 'thorns', 'cosmetic_frame_thorns', 'cosmetic_frame_thorns_desc', 'legendary', 3, 1,
     '{"parts":[
        {"type":"surround","slot":"Primary","slice":[40,40,40,40],"width":[30,30,30,30],
         "outset":[26,26,26,26],"repeat":"round","inset":[0,0,8,0],
         "motion":{"kind":"glimmer","amount":0.4,"periodMs":5200,"phaseMs":0}},
        {"type":"prop","slot":"Secondary","anchor":"top","w":72,"h":62,"dy":18,
         "motion":{"kind":"bob","amount":3,"periodMs":3600,"phaseMs":0}},
        {"type":"prop","slot":"Tertiary","anchor":"bottom","w":52,"h":56,"dx":-108,"dy":-12,
         "motion":{"kind":"sway","amount":5,"periodMs":4400,"phaseMs":0}},
        {"type":"prop","slot":"Tertiary","anchor":"bottom","w":52,"h":56,"dx":108,"dy":-12,
         "motion":{"kind":"sway","amount":5,"periodMs":4400,"phaseMs":1700}}
      ]}',
     '{"Primary": "$($ids["frame-thorns-band"])", "Secondary": "$($ids["frame-thorns-skull"])", "Tertiary": "$($ids["frame-thorns-tuft"])"}'::jsonb,
     0, true, true, 32, NULL, now(), now(), false),
    ('ffffffff-0002-4000-8000-000000000001', 'profile.effect', 'snowfall', 'cosmetic_effect_snowfall', NULL, 'rare', 1, 1,
     '{"fit":"cover","opacity":0.75}', '{"Primary": "$($ids["effect-snow"])"}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),
    ('ffffffff-0002-4000-8000-000000000002', 'profile.effect', 'embers', 'cosmetic_effect_embers', NULL, 'legendary', 2, 1,
     '{"fit":"cover","opacity":0.8}', '{"Primary": "$($ids["effect-embers"])"}'::jsonb, 0, true, true, 32, NULL, now(), now(), false),
    -- The scene. A tree that draws itself over the whole card, blooms, sheds and steps back to the
    -- edges — and the one row here that leaves the card: spillPct lets it hang past every edge by
    -- twelve per cent of the width, replay starts the file over each time the card opens, and
    -- retreat clears it out from under the words at 9.6s, keeping the crown above the glass, a strip
    -- at each side and the roots at the foot. The loop emitters' delayMs is their onset plus their
    -- longest copy's crossing, because a looping copy starts part-way through its cycle by a
    -- negative delay: the near petals first show at 7.6s and the far ones at 8.6s. minWidthPx is
    -- under the 187 the picker's stage tile is authored at and over its 38px cells.
    ('ffffffff-0003-4000-8000-000000000010', 'profile.scene', 'sakura', 'cosmetic_scene_sakura', 'cosmetic_scene_sakura_desc', 'legendary', 1, 1,
     '{"reach":"choice","defaultReach":"card","minWidthPx":160,"sheet":{"w":256,"h":64},"actors":[
  {"type":"wash","slot":"Quaternary","source":"file","depth":"deep","fit":"cover","periodMs":12000,"repeat":"once",
   "opacity":[{"at":0,"v":0},{"at":0.42,"v":0},{"at":0.75,"v":0.85},{"at":1,"v":0.7}]},
  {"type":"wash","slot":"Secondary","source":"file","depth":"over","fit":"cover","spillPct":12,"replay":true,"occlude":["avatar"],
   "periodMs":8000,"repeat":"once",
   "grow":{"fromPct":100,"toPct":100,"durationMs":8000,"repeat":"once","fromScale":1,"softness":0,"from":"bottom","sway":{"deg":1.1,"ms":7400}},
   "retreat":{"atMs":9600,"durationMs":2800,"edgePct":9,"footPct":7,"softPct":6,"remain":0},
   "shadow":{"dxPct":0.8,"dyPct":1.2,"blurPct":1.4,"alpha":0.32}},
  {"type":"emitter","slot":"Primary","source":"atlas","atlas":{"x":0,"y":0,"w":64,"h":64},"depth":"deep","edge":"top",
   "count":16,"seed":1207,"sizePct":2.4,"sizeVarPct":0.9,"durationMs":9800,"durationVarMs":3200,
   "driftPct":22,"swayPct":3.2,"swayPeriodMs":2300,"swayVarMs":900,"spinDeg":220,"flutterDeg":55,"bobPct":1.2,"depthSpreadPct":60,
   "delayMs":26000,"opacity":[{"at":0,"v":0},{"at":0.1,"v":0.75},{"at":0.85,"v":0.75},{"at":1,"v":0}]},
  {"type":"emitter","slot":"Primary","source":"atlas","atlas":{"x":64,"y":0,"w":64,"h":64},"depth":"front","edge":"top",
   "count":26,"seed":4471,"sizePct":3.6,"sizeVarPct":1.3,"durationMs":7600,"durationVarMs":2400,
   "driftPct":28,"swayPct":4.5,"swayPeriodMs":1900,"swayVarMs":800,"spinDeg":300,"flutterDeg":64,"bobPct":1.8,"depthSpreadPct":70,
   "delayMs":22000,"opacity":[{"at":0,"v":0},{"at":0.08,"v":1},{"at":0.88,"v":1},{"at":1,"v":0}]},
  {"type":"emitter","slot":"Primary","source":"atlas","atlas":{"x":128,"y":0,"w":64,"h":64},"depth":"front","edge":"top",
   "count":30,"seed":907,"sizePct":3.4,"sizeVarPct":1.2,"durationMs":5200,"durationVarMs":1800,
   "driftPct":34,"swayPct":5,"swayPeriodMs":1700,"swayVarMs":700,"spinDeg":340,"flutterDeg":70,"bobPct":2,"depthSpreadPct":60,
   "delayMs":9600,"repeat":"once","opacity":[{"at":0,"v":0},{"at":0.06,"v":1},{"at":0.9,"v":1},{"at":1,"v":0}]},
  {"type":"band","slot":"Tertiary","source":"file","depth":"over","edge":"bottom","slice":[0,24,0,24],"thicknessPct":3.5,"cornerPct":3.5,"tile":"round","delayMs":12400,
   "opacity":[{"at":0,"v":0.9},{"at":1,"v":0.9}],
   "grow":{"fromPct":0,"toPct":100,"durationMs":7000,"repeat":"once","fromScale":1,"softness":1,"from":"edge"}}
]}',
     '{"Primary": "$($ids["scene-sakura-sheet"])", "Secondary": "$($ids["scene-sakura-tree"])", "Tertiary": "$($ids["scene-sakura-carpet"])", "Quaternary": "$($ids["scene-sakura-glow"])", "Poster": "$($ids["scene-sakura-still"])"}'::jsonb,
     0, true, true, 32, NULL, now(), now(), false)
ON CONFLICT ("Id") DO UPDATE SET
    "KindKey" = EXCLUDED."KindKey",
    "Slug" = EXCLUDED."Slug",
    "NameKey" = EXCLUDED."NameKey",
    "DescriptionKey" = EXCLUDED."DescriptionKey",
    "Payload" = EXCLUDED."Payload",

    -- The version is the only thing a client compares a cached profile against, so a payload
    -- re-seeded under the old number leaves every cache holding the old one and looking, from
    -- the inside, like a renderer that stopped working.
    "Version" = EXCLUDED."Version",
    "AssetFileIds" = EXCLUDED."AssetFileIds",
    "IsEnabled" = true,
    "IsPublished" = true,
    "AcquisitionMode" = EXCLUDED."AcquisitionMode",
    "UpdatedAt" = now();

-- Names, because a name is a row now.
--
-- Until 2026-09-18 a cosmetic's name was an i18n key compiled into the client, and everything here
-- got one for free by being listed in en.json. It is data now: cosmeticName() reads the wearer's
-- locale out of "CosmeticTranslations", falls back to en, and then prints the key verbatim — so a
-- row seeded after the migration that backfilled the old catalogue has no name in any language and
-- announces itself in the picker as cosmetic_orbit_snow_spiders. Nothing refuses to publish it and
-- the locale validator cannot see it, because there is no longer a key for it to look at.
--
-- Only the rows this script adds that the backfill did not cover. Everything else already has its
-- names and would be overwritten by a worse copy of them.
INSERT INTO "CosmeticTranslations" ("Id", "CosmeticItemId", "Locale", "Name", "Description", "CreatedAt", "UpdatedAt", "IsDeleted")
VALUES
    (gen_random_uuid(), 'dddddddd-0006-4000-8000-000000000002', 'en', 'Snow spiders', NULL, now(), now(), false),
    (gen_random_uuid(), 'dddddddd-0006-4000-8000-000000000002', 'ru', 'Снежные пауки', NULL, now(), now(), false),
    (gen_random_uuid(), 'ffffffff-0003-4000-8000-000000000010', 'en', 'Sakura', 'A cherry tree grows over your whole card, blooms, sheds its petals and steps back to the edges', now(), now(), false),
    (gen_random_uuid(), 'ffffffff-0003-4000-8000-000000000010', 'ru', 'Сакура', 'Вишня вырастает на всю карточку, цветёт, роняет лепестки и отступает к краям', now(), now(), false)
ON CONFLICT ("CosmeticItemId", "Locale") WHERE "IsDeleted" = false DO UPDATE SET
    "Name" = EXCLUDED."Name",
    "Description" = EXCLUDED."Description",
    "UpdatedAt" = now();

SELECT "KindKey", count(*) FILTER (WHERE "IsPublished") AS published, count(*) AS total
FROM "Cosmetics" GROUP BY 1 ORDER BY 1;

-- A zero here is a cosmetic that will print its own key at everybody who opens the picker.
SELECT c."KindKey", c."Slug", count(t."Locale") AS locales
FROM "Cosmetics" c
LEFT JOIN "CosmeticTranslations" t ON t."CosmeticItemId" = c."Id" AND t."IsDeleted" = false
GROUP BY 1, 2 HAVING count(t."Locale") = 0 ORDER BY 1, 2;
"@

Write-Host "Seeding rows in $Database"
$sql | docker exec -i $Postgres psql -U $User -d $Database
