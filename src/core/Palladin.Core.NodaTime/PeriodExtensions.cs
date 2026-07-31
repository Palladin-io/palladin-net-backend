using NodaTime;

namespace Palladin.Core.NodaTime;

public static class PeriodExtensions
{
    public static long TotalMonths(this Period period)
    {
        return Math.Abs(period.Years * 12 + period.Months);
    }
}
