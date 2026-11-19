namespace SkySpaceAgency.AppUI
{
    /// <summary>The kinds of status pill: prograde for OK, coral for alerts, blue for information.</summary>
    public enum StatusKind { Ok, Alert, Info, Neutral }

    public static class StatusKinds
    {
        public const string PillClass = "ssa-pill";

        public static string ClassFor(StatusKind kind) => kind switch
        {
            StatusKind.Ok => PillClass + "--ok",
            StatusKind.Alert => PillClass + "--alert",
            StatusKind.Info => PillClass + "--info",
            _ => PillClass + "--neutral",
        };

        public static readonly string[] AllClasses =
            { ClassFor(StatusKind.Ok), ClassFor(StatusKind.Alert), ClassFor(StatusKind.Info), ClassFor(StatusKind.Neutral) };
    }
}
