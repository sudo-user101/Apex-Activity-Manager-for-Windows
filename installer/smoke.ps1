# Opens Apex on each page (and the tray panel's tabs, and Free Up Memory),
# takes a screenshot, and fails if it crashed. Then runs Apex's self-test.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
New-Item -ItemType Directory -Force shots | Out-Null
Remove-Item "$env:TEMP\apex-crash.txt" -ErrorAction SilentlyContinue
$exe = Resolve-Path "publish\x64\Apex.exe"

function Shot($name, $arguments) {
  $p = Start-Process $exe -ArgumentList $arguments -PassThru
  Start-Sleep -Seconds 14
  if ($p.HasExited) {
    Write-Host "Apex exited on $name with code $($p.ExitCode)"
    if (Test-Path "$env:TEMP\apex-crash.txt") { Get-Content "$env:TEMP\apex-crash.txt" }
    exit 1
  }
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
  $bmp.Save("shots\$name.png", [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Stop-Process -Id $p.Id -Force
  Start-Sleep -Seconds 1
}

$names = "processes", "performance", "assistant", "gaming", "history", "startup", "users", "services"
for ($i = 0; $i -lt $names.Count; $i++) { Shot "$i-$($names[$i])" @("--page=$i", "--maximize") }

# Something to show in the clipboard tab.
Set-Clipboard -Value "Copied by the build's smoke test"
Shot "tray-0-monitor" @("--page=1", "--tray=0")
Shot "tray-1-energy" @("--page=1", "--tray=1")
Shot "tray-2-clipboard" @("--page=1", "--tray=2")
Shot "freeup" @("--page=1", "--maximize", "--dialog=freeup")
Shot "network" @("--page=1", "--maximize", "--perf=net")
Shot "disk" @("--page=1", "--maximize", "--perf=disk")
Shot "storage" @("--page=1", "--maximize", "--dialog=storage")
Shot "memory" @("--page=1", "--maximize", "--perf=memory")
Shot "memory-compression" @("--page=1", "--maximize", "--perf=memory", "--dialog=compression")
Shot "analyze" @("--page=1", "--maximize", "--dialog=analyze")
Shot "assistant-lumen" @("--page=2", "--maximize", "--assistant=2")
Shot "settings-general" @("--page=1", "--settings=general")
Shot "settings-utilities" @("--page=1", "--settings=utilities")
Shot "settings-tweaks" @("--page=1", "--settings=tweaks")
Shot "settings-alerts" @("--page=1", "--settings=alerts")

if (Test-Path "$env:TEMP\apex-crash.txt") { Get-Content "$env:TEMP\apex-crash.txt"; exit 1 }
Write-Host "All pages opened without crashing."

# Page update timings (smoothness).
Remove-Item "$env:TEMP\apex-perftest.txt" -ErrorAction SilentlyContinue
$pt = Start-Process $exe -ArgumentList "--perftest", "--maximize" -PassThru
$pt.WaitForExit(180000) | Out-Null
if (Test-Path "$env:TEMP\apex-perftest.txt") { Get-Content "$env:TEMP\apex-perftest.txt" } else { Write-Host "No perf report." }

# Self-test: End task by identity, Efficiency mode, memory trim, clipboard history.
Remove-Item "$env:TEMP\apex-selftest.txt" -ErrorAction SilentlyContinue
$t = Start-Process $exe -ArgumentList "--selftest" -PassThru -Wait
if (Test-Path "$env:TEMP\apex-selftest.txt") { Get-Content "$env:TEMP\apex-selftest.txt" } else { Write-Host "No self-test report." ; exit 1 }
if ($t.ExitCode -ne 0) { Write-Host "Self-test failed ($($t.ExitCode))."; exit 1 }
Write-Host "Self-test passed."
