# Pre-renders Benchmark cue WAVs into Assets/Sounds (no live TTS at runtime).
# Beeps: sine tones with fade envelopes. Voice: SAPI offline render (if available).
$ErrorActionPreference = 'Stop'
$out = Join-Path (Join-Path (Join-Path $PSScriptRoot '..') 'Assets') 'Sounds'
New-Item -ItemType Directory -Path $out -Force | Out-Null

$rate = 22050

function Write-Tone([string]$path, [double[]]$freqs, [int[]]$ms, [double]$gain = 0.5) {
    $samples = New-Object System.Collections.Generic.List[int16]
    for ($s = 0; $s -lt $freqs.Count; $s++) {
        $n = [int]($rate * $ms[$s] / 1000)
        for ($i = 0; $i -lt $n; $i++) {
            $t = $i / $rate
            $env = [Math]::Min(1.0, [Math]::Min($i, $n - 1 - $i) / ($rate * 0.008))
            $v = [Math]::Sin(2 * [Math]::PI * $freqs[$s] * $t) * $gain * $env
            $samples.Add([int16]($v * 32767))
        }
    }
    Write-Wav $path $samples.ToArray()
}

function Write-Wav([string]$path, [int16[]]$samples) {
    $fs = [IO.File]::Create($path)
    try {
        $bw = New-Object IO.BinaryWriter($fs)
        $dataLen = $samples.Length * 2
        $bw.Write([Text.Encoding]::ASCII.GetBytes('RIFF'))
        $bw.Write([int](36 + $dataLen))
        $bw.Write([Text.Encoding]::ASCII.GetBytes('WAVE'))
        $bw.Write([Text.Encoding]::ASCII.GetBytes('fmt '))
        $bw.Write([int]16); $bw.Write([int16]1); $bw.Write([int16]1)
        $bw.Write([int]$rate); $bw.Write([int]($rate * 2))
        $bw.Write([int16]2); $bw.Write([int16]16)
        $bw.Write([Text.Encoding]::ASCII.GetBytes('data'))
        $bw.Write([int]$dataLen)
        foreach ($s in $samples) { $bw.Write($s) }
        $bw.Flush()
    } finally { $fs.Close() }
    Write-Host "wrote $path"
}

Write-Tone (Join-Path $out 'tick.wav') @(1000.0) @(80)
Write-Tone (Join-Path $out 'start.wav') @(660.0, 880.0) @(120, 140)
Write-Tone (Join-Path $out 'tenleft.wav') @(1200.0) @(80)
Write-Tone (Join-Path $out 'stop.wav') @(880.0, 660.0) @(120, 140)
Write-Tone (Join-Path $out 'saved.wav') @(1500.0) @(60)
Write-Tone (Join-Path $out 'failed.wav') @(220.0, 220.0) @(150, 150) 0.6

# Voice cues via offline SAPI render (no runtime TTS).
try {
    Add-Type -AssemblyName System.Speech
    $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
    $synth.Rate = 0
    $synth.SetOutputToWaveFile((Join-Path $out 'voice_started.wav'))
    $synth.Speak('Recording started')
    $synth.SetOutputToWaveFile((Join-Path $out 'voice_stopped.wav'))
    $synth.Speak('Recording stopped')
    $synth.Dispose()
    Write-Host 'wrote voice_started.wav voice_stopped.wav'
} catch {
    Write-Host "SAPI voice render unavailable: $($_.Exception.Message) (Voice mode will fall back to beeps)"
}
