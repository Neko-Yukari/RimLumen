namespace RimLumen.Adapter.RimUnity.Tests;

internal static class NoticeTests
{
    internal static void MapHdrNoticeRule()
    {
        var rule = new global::RimLumen.Adapter.RimUnity.MapHdrNoticeRule();
        (string Mode, bool Notify)[] steps =
        [
            ("None", false),
            ("Experiment", true),
            ("Experiment", false),
            ("Legacy", true),
            ("Legacy", false),
            ("None", false),
            ("Legacy", true),
        ];
        for (int i = 0; i < steps.Length; i++)
            Check.Equal(steps[i].Notify, rule.ShouldNotify(steps[i].Mode), $"step {i} ({steps[i].Mode})");
    }
}
