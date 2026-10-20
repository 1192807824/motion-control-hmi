using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ControlHub.Models;
using ControlHub.Services.Motion;
using ControlHub.Services.Persistence;
using ControlHub.Views.Pages;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static void Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "io-names-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var motionPath = Path.Combine(directory, "motion-settings.json");
        var namesPath = Path.Combine(directory, "io-point-names.json");
        var store = new MotionCardOptionsStore(motionPath);
        store.Save(new MotionCardOptions { IoPointNames = new() { ["DigitalOutput:24"] = "工位上方吸" } });
        var loaded = store.Load();
        Require(loaded.IoPointNames["DigitalOutput:24"] == "工位上方吸" && File.Exists(namesPath),
            "Migrate existing remarks to independent local file");

        // Simulate a pull/build/deployment replacing all repository defaults.
        store.Save(new MotionCardOptions());
        Require(new MotionCardOptionsStore(motionPath).Load().IoPointNames["DigitalOutput:24"] == "工位上方吸",
            "Replacing motion defaults must not overwrite migrated remarks");
        store.SaveIoPointNames(new Dictionary<string, string> { ["DigitalOutput:24"] = "现场确认的吸气" });
        Require(new MotionCardOptionsStore(motionPath).Load().IoPointNames["DigitalOutput:24"] == "现场确认的吸气",
            "Restart reloads edited remarks from disk");
        File.WriteAllText(namesPath, "invalid json");
        Require(store.Load().IoPointNames["DigitalOutput:24"] == "工位上方吸", "Recover valid name backup");
        store.SaveIoPointNames(new Dictionary<string, string>());
        store.Save(new MotionCardOptions { IoPointNames = new() { ["DigitalOutput:24"] = "旧名称" } });
        Require(store.Load().IoPointNames.Count == 0, "An intentionally empty local file must not resurrect legacy names");
        try
        {
            store.SaveIoPointNames(new Dictionary<string, string> { ["DigitalOutput:24"] = "" });
            throw new Exception("Invalid empty name accepted");
        }
        catch (InvalidDataException) { }
        Require(store.Load().IoPointNames.Count == 0, "Failed validation must preserve saved names");

        _ = new Application();
        var page = new MotionControlPage(); // Do not load or show the page: no hardware opens.
        var options = store.Load();
        Set(page, "_motionOptions", options);
        Set(page, "_motionOptionsStore", store);
        var point = new IoPoint { Channel = 24, Kind = IoPointKind.DigitalOutput, DefaultName = "Y24", Name = "Y24" };
        var editor = new TextBox { DataContext = point };
        editor.SetBinding(TextBox.TextProperty, new Binding(nameof(IoPoint.Name))
        {
            Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit
        });
        var before = File.ReadAllText(motionPath);
        Edit(page, editor, "现场吸气备注");
        Require(point.Name == "现场吸气备注" && store.Load().IoPointNames["DigitalOutput:24"] == point.Name,
            "The real name-edit handler saves independently");
        Require(File.ReadAllText(motionPath) == before, "Editing an IO name must not rewrite motion parameters");

        page.ApplyRecipeMotionSettings(new MotionRecipeSettings
        {
            IoPointNames = new() { ["DigitalOutput:24"] = "旧配方备注" }
        });
        Require(options.IoPointNames["DigitalOutput:24"] == "现场吸气备注" &&
                store.Load().IoPointNames["DigitalOutput:24"] == "现场吸气备注",
            "Applying an old recipe preserves live and persisted machine remarks");
        Require(page.CaptureRecipeMotionSettings().IoPointNames.Count == 0,
            "New recipes must not capture machine wiring names");

        // An invalid store destination must report failure and roll the editor back.
        var blockedPath = Path.Combine(directory, "blocked");
        File.WriteAllText(blockedPath, "not a directory");
        Set(page, "_motionOptionsStore", new MotionCardOptionsStore(Path.Combine(blockedPath, "motion-settings.json")));
        Edit(page, editor, "无法保存的备注");
        Require(point.Name == "现场吸气备注" && options.IoPointNames["DigitalOutput:24"] == "现场吸气备注",
            "Failed save must roll back both UI and memory");

        Set(page, "_motionOptionsStore", store);
        Edit(page, editor, "Y24");
        Require(store.Load().IoPointNames.Count == 0, "Resetting the last custom name persists an empty dictionary");
        File.WriteAllText(namesPath, "broken primary");
        File.WriteAllText(namesPath + ".bak", "broken backup");
        try { store.Load(); throw new Exception("Unreadable names silently reset"); }
        catch (InvalidDataException exception)
        {
            Require(exception.Message.Contains("I/O名称读取失败"), "Corrupt name files must produce an explicit error");
        }
        Console.WriteLine("PASS: legacy migration, code/default replacement, restart, backup recovery, explicit resets, UI save/rollback and recipe isolation. No hardware opened.");
    }

    private static void Edit(MotionControlPage page, TextBox editor, string text)
    {
        editor.Tag = ((IoPoint)editor.DataContext).Name;
        editor.IsReadOnly = false;
        editor.Text = text;
        typeof(MotionControlPage).GetMethod("IoName_LostFocus", Private)!
            .Invoke(page, [editor, new RoutedEventArgs()]);
    }

    private static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, Private)!.SetValue(target, value);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
