namespace BrimDeck.Core;

// The power tooltip as a table: its column names, its rows (a label and a cell per column), whether the first row is a sum (the whole
// machine ≈ the parts added up), and notes under it.
public sealed record PowerTableRow(string Label, IReadOnlyList<string> Cells);
public sealed record PowerTable(IReadOnlyList<string> Columns, IReadOnlyList<PowerTableRow> Rows, bool Sum, IReadOnlyList<string> Notes);

// The words of the power and battery readings: the power tooltip's table (Table) and the battery tooltip's lines of the form "item  value"
// (two spaces between), in plain words and no implementation names; the few words the battery's line shows (Brief) and the power line's
// mean (Mean). The same readings, at their own length, are the screen reader's name (Spoken). Everything is localized here.
public static class PowerText
{
    public const string Gap = "  ";
    private static string Line(string label, string value) => label + Gap + value;

    public static string Watts(double watts) => PowerFormat.Watts(watts) + " W";
    public static string Percent(double percent) => MonitorFormat.Percent(percent / 100) + "%";

    // "17.0 W (estimated)" / "23.4 W (measured)".
    private static string Quality(PowerSample power) => power.Estimated ? "estimated" : "measured";
    private static string Bracket(string text) => " (" + text + ")";

    // Where the whole machine's figure comes from, the tooltip's last line: "（）", or the parts the estimate adds up and what
    // it leaves out: "CPU + GPU ，、、".
    private static string Source(PowerSample power) => power.Source switch
    {
        PowerSource.SystemMeter => "system meter" + Bracket("measured"),
        PowerSource.Battery => "battery discharge" + Bracket("measured"),
        _ => string.Join(" + ", power.Parts.Select(part => part.Key switch { "cpu" => "CPU", "gpu" => "GPU", _ => "memory" }).Distinct())
            + " estimate; display, drives, fans and others not included"
    };
    // The power tooltip with no reading: a dash and the reason.
    public static string NoPower(string reason) => Line("System power", "—") + "\n" + Line("Reason", reason);

    // The power tooltip as a table, a column for the whole machine and one each for the CPU and the GPU where this machine has them:
    //                     CPU       GPU
    //          42.2 W  ≈  34.3 W  +  7.9 W
    //   5   75.2 W     52.0 W     23.1 W
    //   5   46.2 W     38.0 W     8.2 W
    // "≈" and "+" join the current row only where the whole machine is the estimate made of these parts. Notes under it: what could not
    // be read, then where the whole machine's figure comes from. Null without a reading (NoPower then).
    public static PowerTable? Table(Reading<PowerSample> power, PowerSummary? whole, PowerTrack cpu, PowerTrack gpu)
    {
        if (!power.TryGet(out var sample)) return null;
        double? cpuNow = PowerResolver.PartWatts(sample, "cpu"), gpuNow = PowerResolver.PartWatts(sample, "gpu");
        bool hasCpu = cpuNow is not null || cpu.Summary is not null, hasGpu = gpuNow is not null || gpu.Summary is not null || sample.Missing.Count > 0;
        string Cell(double? watts) => watts is { } value ? Watts(value) : "—";
        List<string> Cells(double? all, double? processor, double? graphics)
        {
            var cells = new List<string> { Cell(all) };
            if (hasCpu) cells.Add(Cell(processor));
            if (hasGpu) cells.Add(Cell(graphics));
            return cells;
        }
        var columns = new List<string> { ViewName(PowerView.Whole) };
        if (hasCpu) columns.Add(ViewName(PowerView.Cpu));
        if (hasGpu) columns.Add(ViewName(PowerView.Gpu));
        var rows = new List<PowerTableRow> { new("Now", Cells(sample.Watts, cpuNow, gpuNow)) };
        if (whole is { Count: > 0 })
        {
            string span = PowerFormat.Span(whole.Seconds);
            rows.Add(new("Peak, " + span, Cells(whole.Peak, cpu.Summary?.Peak, gpu.Summary?.Peak)));
            rows.Add(new("Mean, " + span, Cells(whole.Average, cpu.Summary?.Average, gpu.Summary?.Average)));
        }
        // The whole machine is these parts added up only when it is the estimate and every part it adds is a column.
        bool sum = sample.Source == PowerSource.Components && columns.Count > 1 && sample.Parts.All(part => part.Key is "cpu" or "gpu");
        var notes = new List<string>();
        if (sample.Missing.Count > 0) notes.Add(Line("Not read", string.Join(", ", sample.Missing)));
        notes.Add(Line("Source", Source(sample)));
        return new PowerTable(columns, rows, sum, notes);
    }

    // What the power line shows, after its unit: "", "CPU", "GPU".
    public static string ViewName(PowerView view) => view switch { PowerView.Cpu => "CPU", PowerView.Gpu => "GPU", _ => "System" };

    // The power line's mean at its right end: " 46 W".
    public static string Mean(double watts) => "Mean " + PowerFormat.Compact(watts, 9999) + " W";

    // " 23.4 W", " 31.0 W", "，", "".
    public static string State(BatterySample battery) => battery.State switch
    {
        BatteryState.Charging => "Charging" + (battery.HasRate ? " " + Watts(battery.Watts) : ""),
        BatteryState.Full => "Full",
        BatteryState.PluggedIn => "Plugged in, not charging",
        _ => "Discharging" + (battery.HasRate ? " " + Watts(battery.Watts) : "")
    };

    // After the battery's charge on the page: the time, where there is one (" 1:42", " 0:40"); nothing otherwise, since the
    // charge and the battery's drawing (the plug while on mains) already say full, plugged in or on battery.
    public static string Brief(BatterySample battery) => battery.State switch
    {
        BatteryState.Charging when battery.TimeToFull is { } full => "Full in " + PowerFormat.Clock(full),
        BatteryState.Discharging when battery.TimeLeft is { } left => PowerFormat.Clock(left) + " left",
        _ => ""
    };

    // The battery tooltip: charge, state, time, power mode, saver, health, number of batteries.
    public static string Battery(Reading<BatterySample> battery)
    {
        if (!battery.TryGet(out var sample))
            return Line("Battery", "—") + "\n" + Line("Reason", battery.Reason);
        var lines = new List<string> { Line("Battery", Percent(sample.Percent) + (sample.RemainingWh is { } wh ? Bracket("" + wh.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " Wh stored") : "")) };
        lines.Add(Line("State", State(sample)));
        if (sample.TimeLeft is { } left) lines.Add(Line("Remaining", PowerFormat.Duration(left)));
        else if (sample.TimeToFull is { } full) lines.Add(Line("To full", PowerFormat.Duration(full)));
        if (BatteryModel.ModeName(sample.Mode) is { Length: > 0 } mode) lines.Add(Line("Power mode", mode));
        if (sample.BatterySaver) lines.Add(Line("Battery saver", "on"));
        if (sample.Health is { } health && sample.FullWh is { } fullWh && sample.DesignWh is { } design)
            lines.Add(Line("Health", MonitorFormat.Percent(health) + "%" + Bracket("full " + fullWh.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " Wh / " + "design " + design.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " Wh")));
        if (sample.Count > 1) lines.Add(Line("Batteries", sample.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return string.Join("\n", lines);
    }

    // The screen reader's name: the tooltips' first items in one line, "item value" and commas.
    public static string Spoken(Reading<PowerSample> power, Reading<BatterySample> battery, bool batteryShown)
    {
        string text = power.TryGet(out var sample)
            ? "System power " + Watts(sample.Watts) + ", " + Quality(sample)
            : "System power: no reading";
        if (batteryShown && battery.TryGet(out var cell))
            text += "; battery " + Percent(cell.Percent) + ", " + State(cell)
                + (BatteryModel.ModeName(cell.Mode) is { Length: > 0 } mode ? "; power mode " + mode : "");
        return text;
    }
}
