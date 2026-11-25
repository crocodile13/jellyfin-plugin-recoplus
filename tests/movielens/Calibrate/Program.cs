using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using RecoPlus.Engine;
using RecoPlus.Tests;

// Calibrage du plugin sur un jeu exporté par le banc MovieLens : rapport JSON (méthodes, gain hors échantillon, IC95,
// réglages proposés qui diffèrent des défauts). Usage : Calibrate <jeu.json> [auto|coordinate|choice|neural] [sortie.json]
if (args.Length < 1)
{
    Console.Error.WriteLine("Calibrate <dataset.json> [auto|coordinate|choice|neural] [out.json]");
    return 2;
}

var ds = DatasetJson.Load(JsonDocument.Parse(File.ReadAllText(args[0])).RootElement);
var method = (args.Length > 1 ? args[1] : "auto") switch
{
    "coordinate" => CalibrationMethod.Coordinate,
    "choice" => CalibrationMethod.ChoiceModel,
    "neural" => CalibrationMethod.Neural,
    _ => CalibrationMethod.Auto,
};
var defaults = new EngineSettings();
var sw = Stopwatch.StartNew();
int last = -10;
var progress = new Progress<double>(p =>
{
    if ((int)p >= last + 10)
    {
        last = (int)p;
        Console.Error.WriteLine($"{p:0}% ({sw.Elapsed.TotalMinutes:0.0} min)");
    }
});
var r = Calibrator.Calibrate(ds, defaults, progress, method: method);

static Dictionary<string, object?> Diff(EngineSettings a, EngineSettings b)
{
    var d = new Dictionary<string, object?>(StringComparer.Ordinal);
    foreach (var p in typeof(EngineSettings).GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
    {
        var x = p.GetValue(a);
        var y = p.GetValue(b);
        if (!Equals(x, y) && (x is double or int or bool or Enum) && (y is double or int or bool or Enum))
        {
            d[p.Name] = new Dictionary<string, object?> { ["default"] = x is Enum ? x.ToString() : x, ["proposed"] = y is Enum ? y.ToString() : y };
        }
    }

    return d;
}

var report = new Dictionary<string, object?>
{
    ["accounts"] = r.Current.Accounts,
    ["cases"] = r.Current.Events,
    ["minutes"] = Math.Round(sw.Elapsed.TotalMinutes, 1),
    ["current_gain"] = r.Current.Gain,
    ["proposed_gain"] = r.Proposed.Gain,
    ["cv_delta"] = r.CvDelta,
    ["ci"] = new[] { r.DeltaCi.Low, r.DeltaCi.High },
    ["recommended"] = r.Recommended,
    ["method"] = r.Method.ToString(),
    ["verdict"] = r.VerdictIn("en"),
    ["proposed"] = Diff(defaults, r.ProposedSettings),
    ["methods"] = r.Methods.Select(m => new Dictionary<string, object?>
    {
        ["name"] = m.Name,
        ["cv_delta"] = m.CvDelta,
        ["ci"] = new[] { m.Ci.Low, m.Ci.High },
        ["sufficiency"] = m.Sufficiency,
        ["proposed"] = Diff(defaults, m.Settings),
    }).ToList(),
};
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);
if (args.Length > 2)
{
    File.WriteAllText(args[2], json);
}

return 0;
