# Generates speech WAV fixtures with the Windows built-in TTS voices.
# Used as fake microphone input for automated browser tests
# (Chrome --use-file-for-fake-audio-capture) and as AI pipeline test clips.
#
#   powershell -ExecutionPolicy Bypass -File tests\fixtures\generate_speech.ps1
param([string]$OutDir = (Join-Path $PSScriptRoot "audio"))

Add-Type -AssemblyName System.Speech
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-SpeechWav([string]$Voice, [string]$Text, [string]$Path) {
    $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
    $synth.SelectVoice($Voice)
    $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(48000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
    $synth.SetOutputToWaveFile($Path, $format)
    $synth.Speak($Text)
    $synth.Dispose()
    Write-Host "wrote $Path"
}

$voices = (New-Object System.Speech.Synthesis.SpeechSynthesizer).GetInstalledVoices() | ForEach-Object { $_.VoiceInfo.Name }
$male = $voices | Where-Object { $_ -like "*David*" } | Select-Object -First 1
$female = $voices | Where-Object { $_ -like "*Zira*" } | Select-Object -First 1
if (-not $male) { $male = $voices[0] }
if (-not $female) { $female = $voices[-1] }

# Two utterances separated by 3 s pauses: produces separate clips when looped as fake mic input.
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$synth.SelectVoice($male)
$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(48000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
$pausePath = Join-Path $OutDir "speech_with_pauses.wav"
$synth.SetOutputToWaveFile($pausePath, $format)
$pb = New-Object System.Speech.Synthesis.PromptBuilder
$pb.AppendBreak([TimeSpan]::FromSeconds(1.5))
$pb.AppendText("This is an audio recording test.")
$pb.AppendBreak([TimeSpan]::FromSeconds(3))
$pb.AppendText("The second sentence should become a separate clip.")
$pb.AppendBreak([TimeSpan]::FromSeconds(3))
$synth.Speak($pb)
$synth.Dispose()
Write-Host "wrote $pausePath"

New-SpeechWav $male "The quick brown fox jumps over the lazy dog. I am answering question number four of the exam." (Join-Path $OutDir "speech_single.wav")
New-SpeechWav $female "Can you help me with this question? I think the answer is the second option." (Join-Path $OutDir "speech_female.wav")
