$path = "C:\Users\PatrickHines\Documents\500GB\Github\NewHeights.TimeClock\src\NewHeights.TimeClock.Web\Components\Pages\Kiosk\ClockInOut.razor"
$lines = [System.Collections.ArrayList](Get-Content $path)

Write-Host "Applying ShouldPromptEarlyOut fix..." -ForegroundColor Cyan

# Fix 1: ShouldPromptEarlyOut - find and replace the entire method
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'private async Task<bool> ShouldPromptEarlyOut') {
        # Found the method signature at line $i
        # Find the closing brace
        $braceCount = 0
        $startIdx = $i
        for ($j = $i; $j < $lines.Count; $j++) {
            if ($lines[$j] -match '\{') { $braceCount++ }
            if ($lines[$j] -match '\}') { $braceCount-- }
            if ($braceCount -eq 0 -and $j -gt $i) {
                # Found the end at line $j
                $endIdx = $j
                Write-Host "  Found method: lines $($startIdx+1) to $($endIdx+1)"
                
                # Remove old method
                $lines.RemoveRange($startIdx, $endIdx - $startIdx + 1)
                
                # Insert new method
                $newMethod = @(
                    "    private async Task<bool> ShouldPromptEarlyOut(TimeClockDbContext context, int campusId)"
                    "    {"
                    "        // Kiosk mode: Skip early-out prompts to avoid UI issues after circuit reconnection"
                    "        // Early-out flagging should be handled server-side in TimePunchService (Option 4)"
                    "        return false;"
                    "    }"
                )
                $lines.InsertRange($startIdx, $newMethod)
                Write-Host "  ✓ ShouldPromptEarlyOut fixed" -ForegroundColor Green
                break
            }
        }
        break
    }
}

Write-Host "Adding diagnostic logging..." -ForegroundColor Cyan

# Fix 2: Add logging before/after RecordAttendanceTransaction in ProcessHourlyEmployee
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'var scanType = DetermineScanTypeFromPunch\(punchResult\.PunchType\);' -and 
        $i -gt 550 -and $i -lt 650) {  # Make sure we're in ProcessHourlyEmployee
        
        # Insert logging AFTER the scanType line
        $lines.Insert($i + 1, '            Logger.LogInformation("KIOSK: Recording attendance - ID:{Id} Type:{Type} ScanType:{ScanType}", employee.IdNumber, punchResult.PunchType, scanType);')
        $lines.Insert($i + 2, '            ')
        
        # Find the RecordAttendanceTransaction line (should be right after)
        for ($j = $i + 3; $j -lt [Math]::Min($i + 10, $lines.Count); $j++) {
            if ($lines[$j] -match 'await RecordAttendanceTransaction') {
                # This is the start of the call, find where it ends
                for ($k = $j; $k -lt [Math]::Min($j + 5, $lines.Count); $k++) {
                    if ($lines[$k] -match 'earlyOutReason\);') {
                        # Insert logging AFTER this line
                        $lines.Insert($k + 1, '            ')
                        $lines.Insert($k + 2, '            Logger.LogInformation("KIOSK: Attendance recorded successfully - ID:{Id} ScanType:{ScanType}", employee.IdNumber, scanType);')
                        Write-Host "  ✓ Logging added to ProcessHourlyEmployee" -ForegroundColor Green
                        goto DONE
                        break
                    }
                }
                break
            }
        }
        break
    }
}

:DONE
[System.IO.File]::WriteAllLines($path, $lines.ToArray(), (New-Object System.Text.UTF8Encoding $false))
Write-Host "`n✓ All fixes applied successfully!" -ForegroundColor Green
