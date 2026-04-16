namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Payload emitted by SubPeriodPickerModal when the sub clicks "Save Period".
/// Consumed by MySubTimesheet.razor (Phase 2) and, later, the supervisor
/// add-period flow (Phase 3). Lives in the Services namespace so it can be
/// referenced from both the modal component and any page that hosts it.
/// </summary>
/// <param name="BellPeriodId">
/// Selected TcBellPeriod.PeriodId — the period the sub covered. Required.
/// </param>
/// <param name="MasterScheduleId">
/// Optional TcMasterSchedule.ScheduleId when a specific teacher slot was
/// picked. Null when no teacher match was selected (e.g., the sub covered an
/// unscheduled / walk-in period). When non-null, the service snapshots
/// teacher / course / room / content-area onto the entry.
/// </param>
/// <param name="SessionType">DAY or NIGHT — matches TcSubstitutePeriodEntry.SessionType.</param>
/// <param name="Notes">Optional free-text notes up to 500 chars.</param>
public record PeriodPickerResult(
    int BellPeriodId,
    int? MasterScheduleId,
    string SessionType,
    string? Notes);
