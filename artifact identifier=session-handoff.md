<artifact identifier="session-handoff-2026-03-30" type="application/vnd.ant.myst" title="2026-03-30 Session Handoff - UTC Migration & Entity Config">
# 2026-03-30 17:50 CST - UTC Migration + Entity Configuration Fix + Geofence Reception Design Session Handoff

## Session Overview
**Duration:** ~3 hours  
**Primary Goals:**
1. ✅ Implement UTC timezone best practices across entire application
2. ✅ Fix Entity Framework configuration errors preventing navigation
3. ⏳ Design geofence-based campus detection for Reception Kiosk (IN PROGRESS)

---

## Critical Fixes Completed

### 1. UTC Timezone Migration (COMPLETE)

**Problem Discovered:**
- SQL Server reported `ServerLocalTime = ServerUTC` (both showing same timestamp)
- Azure SQL stores UTC, but C# `DateTime.Now` was storing CST times
- Mixed timezone storage causing date filtering issues

**Solution Implemented:**
Changed 4 files to use `DateTime.UtcNow` for all timestamp storage:

1. **ClockInOut.razor** (line 806)
   - Changed: `ScanDateTime = DateTime.Now` → `DateTime.UtcNow`

2. **MobileCheckin.razor** (lines 518, 664)
   - Changed: `ScanDateTime = DateTime.Now` → `DateTime.UtcNow` (2 occurrences)

3. **TimePunchService.cs** (line 162)
   - Changed: `var now = DateTime.Now` → `DateTime.UtcNow`

4. **Home.razor** (restored UTC-aware version)
   - Storage: `DateTime.UtcNow` in database
   - Display: `ToCst()` helper converts UTC → CST for user display
   - Filtering: Calculates CST "today" and filters in memory

**Database Cleanup:**
All time-tracking data cleared to ensure clean UTC start:
```sql
-- Executed successfully
DELETE FROM TC_PunchCorrections;
DELETE FROM TC_CorrectionRequests;
DELETE FROM TC_PayPeriodSummary;
DELETE FROM TC_DailyTimecards;
UPDATE TC_TimePunches SET PairedPunchId = NULL WHERE PairedPunchId IS NOT NULL;
DELETE FROM TC_TimePunches;
DELETE FROM Attendance_Transactions;
-- Result: All 6 tables show 0 records
```

**Key Learning:**
Self-referencing foreign key (`TC_TimePunches.PairedPunchId`) must be set to NULL before DELETE due to FK constraint.

---

### 2. Entity Framework Configuration Fix (COMPLETE)

**Problem:**
Browser console error when clicking home page cards:
```
System.InvalidOperationException: The entity type 'TcBellPeriod' requires 
a primary key to be defined.
```

**Root Cause:**
Migration 002 added 4 new entities to DbContext, but their configurations were missing from `ConfigureTimeclockTables()` method:
- `TcBellPeriod`
- `TcBellSchedule`
- `TcPunchCorrection`
- `TcStaffHoursWindow`

**Solution:**
Added missing entity configurations to `TimeClockDbContext.cs` (lines 374-408):
```csharp
modelBuilder.Entity<TcPunchCorrection>(entity =>
{
    entity.ToTable("TC_PunchCorrections");
    entity.HasKey(e => e.CorrectionId);
    entity.Property(e => e.CorrectionType).HasMaxLength(20).IsRequired();
    entity.Property(e => e.Reason).HasMaxLength(500);
    entity.HasOne(e => e.OriginalPunch).WithMany()
        .HasForeignKey(e => e.OriginalPunchId)
        .OnDelete(DeleteBehavior.Restrict);
});

modelBuilder.Entity<TcBellSchedule>(entity =>
{
    entity.ToTable("TC_BellSchedule");
    entity.HasKey(e => e.BellScheduleId);  // Not ScheduleId!
    entity.Property(e => e.ScheduleName).HasMaxLength(100).IsRequired();
    entity.Property(e => e.SessionType).HasMaxLength(10).IsRequired();
    entity.HasOne(e => e.Campus).WithMany()
        .HasForeignKey(e => e.CampusId)
        .OnDelete(DeleteBehavior.Restrict);
});

modelBuilder.Entity<TcBellPeriod>(entity =>
{
    entity.ToTable("TC_BellPeriod");
    entity.HasKey(e => e.PeriodId);
    entity.Property(e => e.PeriodName).HasMaxLength(50).IsRequired();
    entity.Property(e => e.PeriodType).HasMaxLength(20).IsRequired();
    entity.HasOne(e => e.Schedule).WithMany(s => s.Periods)
        .HasForeignKey(e => e.BellScheduleId)
        .OnDelete(DeleteBehavior.Cascade);
});

modelBuilder.Entity<TcStaffHoursWindow>(entity =>
{
    entity.ToTable("TC_StaffHoursWindows");
    entity.HasKey(e => e.WindowId);
    entity.Property(e => e.SessionType).HasMaxLength(10).IsRequired();
    entity.HasOne(e => e.Campus).WithMany()
        .HasForeignKey(e => e.CampusId)
        .OnDelete(DeleteBehavior.Restrict);
});
```

**Build Status:** ✅ SUCCESSFUL (Release configuration)

**Important Notes:**
- `TcBellSchedule.BellScheduleId` is the primary key (not `ScheduleId`)
- `TcMasterSchedule.ScheduleId` is different entity (remains unchanged)
- Configuration errors fixed through multiple PowerShell regex replacements

---

## Pending Work: Geofence-Based Reception Kiosk

### Current State
**Problem:** Reception Kiosk uses manual campus selection:
- Home page links: `/reception/@_selectedCampusCode`
- ReceptionDashboard shows modal: "Select Your Campus"
- Users manually choose Stop Six or McCart

### Requirement
**New Behavior:**
1. User clicks "Reception Kiosk" card → GPS location requested
2. System detects nearest campus via geofencing
3. Auto-navigate to `/reception/{detected-campus}`
4. Users can **view** all campuses (tabs)
5. Users can only **add corrections** to their current (detected) campus

### Available Infrastructure
**GeofenceService.cs** already exists with:
- `GetNearestCampusAsync(lat, lon)` - Finds closest campus
- `ValidateLocationAsync(campusId, lat, lon)` - Checks if within radius
- `GetAllCampusesAsync()` - Returns all campuses
- `CalculateDistanceMeters()` - Haversine distance calculation

**Campus Data Structure:**
```csharp
Campus {
    CampusId,
    CampusCode,  // "stopsix" or "mccart"
    CampusName,
    Latitude,
    Longitude,
    GeofenceRadiusMeters,
    CampusWifiSSID
}
```

### Implementation Plan (NOT STARTED)

**STEP 1: Modify Home.razor Reception Kiosk Card**

Current (line 106-110):
```html
<a class="home-card" href="/reception/@_selectedCampusCode">
    <span class="card-icon">🖥️</span>
    <span class="card-label">Reception Kiosk</span>
    <span class="card-desc">Full live scanner view</span>
</a>
```

Change to:
```html
<button class="home-card" @onclick="NavigateToReception" disabled="@_navigatingReception">
    <span class="card-icon">🖥️</span>
    <span class="card-label">Reception Kiosk</span>
    <span class="card-desc">@(_navigatingReception ? "Detecting campus..." : "Full live scanner view")</span>
</button>
```

Add to @code section:
```csharp
private bool _navigatingReception = false;

private async Task NavigateToReception()
{
    _navigatingReception = true;
    try
    {
        var position = await JS.InvokeAsync<GeolocationPosition>("getGeolocation");
        
        // Inject GeofenceService (add to constructor/DI)
        var nearest = await GeofenceService.GetNearestCampusAsync(
            position.Latitude, 
            position.Longitude
        );
        
        if (nearest != null)
        {
            Nav.NavigateTo($"/reception/{nearest.CampusCode}");
        }
        else
        {
            // Fallback: use last selected campus or show error
            Nav.NavigateTo("/reception");
        }
    }
    catch (Exception ex)
    {
        Logger.LogError(ex, "Failed to detect campus location");
        // Fallback to manual selection
        Nav.NavigateTo("/reception");
    }
    finally
    {
        _navigatingReception = false;
    }
}

// JS Interop model
private class GeolocationPosition
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}
```

**STEP 2: Add JavaScript Geolocation Helper**

Create or update `wwwroot/js/site.js`:
```javascript
window.getGeolocation = function() {
    return new Promise((resolve, reject) => {
        if (!navigator.geolocation) {
            reject(new Error('Geolocation not supported'));
            return;
        }
        
        navigator.geolocation.getCurrentPosition(
            position => resolve({
                latitude: position.coords.latitude,
                longitude: position.coords.longitude
            }),
            error => reject(error),
            { enableHighAccuracy: true, timeout: 10000, maximumAge: 0 }
        );
    });
};
```

**STEP 3: Update ReceptionDashboard.razor**

Remove manual campus selector overlay (lines 21-36).

Add auto-detection on load:
```csharp
private int _detectedCampusId = 0;  // User's physical campus
private int _viewingCampusId = 0;   // Campus currently viewing

protected override async Task OnParametersSetAsync()
{
    // Detect user's physical location
    try
    {
        var position = await JS.InvokeAsync<GeolocationPosition>("getGeolocation");
        var nearest = await GeofenceService.GetNearestCampusAsync(
            position.Latitude, 
            position.Longitude
        );
        
        if (nearest != null)
        {
            _detectedCampusId = nearest.CampusId;
            
            // If no campus code in URL, use detected campus
            if (string.IsNullOrEmpty(CampusCode))
            {
                Nav.NavigateTo($"/reception/{nearest.CampusCode}", replace: true);
                return;
            }
        }
    }
    catch (Exception ex)
    {
        Logger.LogWarning(ex, "Could not detect campus location");
    }
    
    // Continue with existing URL-based campus selection
    await LoadCampusFromUrl();
}
```

**STEP 4: Restrict Corrections to Detected Campus**

In correction/edit methods, add validation:
```csharp
private async Task AddPunchCorrection(...)
{
    // Only allow corrections for user's physical campus
    if (_detectedCampusId == 0)
    {
        await JS.InvokeVoidAsync("alert", "Cannot determine your location. Please enable GPS.");
        return;
    }
    
    if (correction.CampusId != _detectedCampusId)
    {
        await JS.InvokeVoidAsync("alert", "You can only add corrections for your current campus.");
        return;
    }
    
    // Proceed with correction...
}
```

Add visual indicator showing detected vs. viewing campus:
```html
<div class="campus-status">
    <span class="status-label">Your Campus:</span>
    <span class="status-campus">@GetCampusName(_detectedCampusId)</span>
    
    @if (_viewingCampusId != _detectedCampusId)
    {
        <span class="status-warning">⚠️ Viewing different campus (corrections disabled)</span>
    }
</div>
```

---

## Build & Deployment Status

**Current Build:** ✅ Release build successful  
**Next Step:** 🚀 **PUBLISH TO AZURE** (critical - fixes not live yet)

After publish:
1. Test home page card navigation
2. Test mobile check-in with UTC timestamps
3. Test time clock with UTC timestamps
4. Verify times display correctly in CST
5. Verify database stores UTC

**Database Verification Query:**
```sql
-- After testing, verify UTC storage
SELECT TOP 5
    TransactionId,
    FirstName + ' ' + LastName AS Name,
    ScanDateTime AS StoredTime_UTC,
    DATEADD(HOUR, -6, ScanDateTime) AS DisplayTime_CST,
    ScanType
FROM Attendance_Transactions
ORDER BY ScanDateTime DESC;

SELECT TOP 5
    PunchId,
    EmployeeId,
    PunchDateTime AS StoredTime_UTC,
    DATEADD(HOUR, -6, PunchDateTime) AS DisplayTime_CST,
    PunchType
FROM TC_TimePunches
ORDER BY PunchDateTime DESC;
```

---

## Files Modified This Session

1. ✅ `ClockInOut.razor` - UTC timestamps
2. ✅ `MobileCheckin.razor` - UTC timestamps  
3. ✅ `TimePunchService.cs` - UTC timestamps
4. ✅ `Home.razor` - UTC display conversion
5. ✅ `TimeClockDbContext.cs` - Entity configurations

---

## Next Session Priorities

1. **IMMEDIATE:** Publish to Azure (fixes not live)
2. **GEOFENCE RECEPTION:**
   - Implement GPS detection in Home.razor
   - Add JavaScript geolocation helper
   - Update ReceptionDashboard auto-detection
   - Add campus restriction validation
3. **TESTING:**
   - Verify UTC storage in database
   - Test campus detection accuracy
   - Test correction restrictions

---

## Key Learnings

1. **UTC Best Practice:** Always store `DateTime.UtcNow`, convert to local timezone only for display
2. **Entity Framework:** All entities in DbContext require explicit `HasKey()` configuration
3. **SQL Foreign Keys:** Self-referencing FKs must be NULLed before DELETE
4. **Azure SQL:** `GETDATE()` returns UTC despite appearing as local time
5. **PowerShell Regex:** Multi-line replacements with `\n` insert literal text, not newlines - use line-by-line array manipulation instead

---

## Token Usage
**Session Start:** 190K tokens available  
**Session End:** ~80K tokens remaining (~58% used)  
**Handoff Trigger:** Approaching complex geofence implementation
</artifact>