namespace CodexUsage.Core.Models;

public enum UsageBudgetLevel { Normal, Warning, Tight, Critical }

public static class UsageBudget
{
    public static UsageBudgetLevel? ForReset(QuotaWindow window, DateTimeOffset now)
    {
        if (!double.IsFinite(window.UsedPercent) || window.ResetsAt is not { } reset || reset <= now) return null;
        var used = Math.Clamp(window.UsedPercent, 0, 100);
        if (window.WindowDurationMinutes != 10_080)
            return used < 50 ? UsageBudgetLevel.Normal : used < 80 ? UsageBudgetLevel.Warning : UsageBudgetLevel.Critical;
        if (used < 30) return UsageBudgetLevel.Normal;
        var perDay = (100 - used) / Math.Max((reset - now).TotalDays, 0.04);
        return perDay >= 12 ? UsageBudgetLevel.Normal : perDay >= 5 ? UsageBudgetLevel.Warning
            : perDay >= 2 ? UsageBudgetLevel.Tight : UsageBudgetLevel.Critical;
    }
}
