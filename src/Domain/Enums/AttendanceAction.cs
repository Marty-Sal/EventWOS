namespace EventWOS.Domain.Enums;

public enum AttendanceAction
{
    CheckIn       = 1,
    CheckOut      = 2,
    AdminOverride = 3   // Admin retroactively marked the crew as attended (post-event correction)
}
