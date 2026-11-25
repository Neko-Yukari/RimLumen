namespace RimLumen.Adapter.RimUnity.Tests;

internal static class NoticeTests
{
    internal static void MapHdrNoticeRule()
    {
        var rule = new global::RimLumen.Adapter.RimUnity.MapHdrNoticeRule();
        (string Mode, bool RimLumenOn, bool Notify)[] steps =
        [
            ("None", true, false),
            ("Experiment", false, false),
            ("Experiment", true, true),
            ("Experiment", true, false),
            ("Legacy", true, true),
            ("Legacy", true, false),
            ("Legacy", false, false),
            ("Legacy", true, true),
            ("None", true, false),
            ("Legacy", true, true),
        ];
        for (int i = 0; i < steps.Length; i++)
            Check.Equal(steps[i].Notify, rule.ShouldNotify(steps[i].Mode, steps[i].RimLumenOn), $"step {i} ({steps[i].Mode}, {steps[i].RimLumenOn})");
    }
}
