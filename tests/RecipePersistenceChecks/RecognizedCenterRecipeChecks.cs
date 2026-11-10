using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ControlHub.Services.Persistence;
using ControlHub.Views.Controls;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private static void CheckRecognizedCenterRecipeNames(string directory)
    {
        const string original = "找芯片流程_011_测试";
        var store = new ProductRecipeStore(Path.Combine(directory, "recognized-center-recipes"));
        var first = store.Create("中心流程A");
        var second = store.Create("中心流程B");
        var old = JsonNode.Parse(store.Serialize(first))!;
        old["VisionProcedureNames"]!.AsObject().Remove("RecognizedCenter");
        Require(store.DeserializeForExistingRecipe(old.ToJsonString(), first.Id).VisionProcedureNames.RecognizedCenter == original,
            "Existing recipe without the new field must retain the old fixed procedure name");
        old["VisionProcedures"] = new JsonArray("找料A", "示教A", "标定A", "下标定A", "三点A", "旋转A", "纠偏A");
        var migrated = store.DeserializeForExistingRecipe(old.ToJsonString(), first.Id);
        Require(migrated.VisionProcedureNames.Inspection == "找料A" &&
                migrated.VisionProcedureNames.RecognizedCenter == original,
            "Legacy seven-name recipe migration must preserve the center procedure default");

        first.VisionProcedureNames.RecognizedCenter = "产品 A 中心识别";
        second.VisionProcedureNames.RecognizedCenter = "产品 B 中心识别";
        store.Save(first);
        store.Save(second);
        var parameters = new ParameterSettingsPage();
        typeof(ParameterSettingsPage).GetField("_recipeStore", PrivateInstance)!.SetValue(parameters, store);
        var host = new VisionMasterProcessHost();
        var refresh = typeof(ParameterSettingsPage).GetMethod("RefreshRecipeList", PrivateInstance)!;
        foreach (var recipe in new[] { first, second, first })
        {
            refresh.Invoke(parameters, [recipe.Id]);
            var input = (TextBox)parameters.FindName("RecognizedCenterProcedureNameTextBox");
            Require(input.IsEnabled && input.Text == recipe.VisionProcedureNames.RecognizedCenter,
                "A/B/A selection must restore each recipe's independent center procedure name");
            var saved = new ProductRecipeStore(Path.Combine(directory, "recognized-center-recipes"))
                .LoadAll().Single(item => item.Id == recipe.Id);
            host.SetProcedureNames(saved.VisionProcedureNames);
            var startInfo = new ProcessStartInfo();
            typeof(VisionMasterProcessHost).GetMethod("AddProcedureNameArguments", PrivateInstance)!.Invoke(host, [startInfo]);
            var index = startInfo.ArgumentList.IndexOf("--recognized-center-procedure");
            Require(index >= 0 && startInfo.ArgumentList[index + 1] == recipe.VisionProcedureNames.RecognizedCenter,
                "Applied recipe must pass its full center procedure name, including spaces, to the actual host startup arguments");
        }
        var editor = (TextBox)parameters.FindName("RecognizedCenterProcedureNameTextBox");
        editor.Text = "  产品A修改中心  ";
        typeof(ParameterSettingsPage).GetMethod("SaveVisionProcedureNames_Click", PrivateInstance)!
            .Invoke(parameters, [parameters, new RoutedEventArgs()]);
        Require(store.LoadAll().Single(item => item.Id == first.Id).VisionProcedureNames.RecognizedCenter == "产品A修改中心" &&
                host.ProcedureNames.RecognizedCenter == first.VisionProcedureNames.RecognizedCenter,
            "Save Names must persist the trimmed name without changing active runtime until application");
        foreach (var invalid in new[] { "", "  ", new string('x', 101) })
        {
            editor.Text = invalid;
            typeof(ParameterSettingsPage).GetMethod("SaveVisionProcedureNames_Click", PrivateInstance)!
                .Invoke(parameters, [parameters, new RoutedEventArgs()]);
            Require(store.LoadAll().Single(item => item.Id == first.Id).VisionProcedureNames.RecognizedCenter == "产品A修改中心",
                "Blank and overlong center procedure drafts must preserve the saved recipe");
        }
        refresh.Invoke(parameters, [first.Id]);
        foreach (var width in new[] { 1680, 1280 })
        {
            parameters.Measure(new Size(width, 854));
            parameters.Arrange(new Rect(0, 0, width, 854));
            parameters.UpdateLayout();
            var input = (TextBox)parameters.FindName("RecognizedCenterProcedureNameTextBox");
            var position = input.TransformToAncestor(parameters).Transform(new Point());
            Require(input.ActualHeight >= 26 && position.Y + input.ActualHeight < 420 &&
                    position.X + input.ActualWidth <= width,
                "Eighth procedure name must remain visible and editable in the recipe panel");
            var preview = new RenderTargetBitmap(width, 854, 96, 96, PixelFormats.Pbgra32);
            preview.Render(parameters);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(preview));
            var previewDir = Path.Combine(Environment.CurrentDirectory, ".codex-tmp", "recognized-center-recipe");
            Directory.CreateDirectory(previewDir);
            using var output = File.Create(Path.Combine(previewDir, $"parameters-{width}.png"));
            encoder.Save(output);
        }
        Console.WriteLine("PASS: center procedure name per recipe, A/B/A reload, legacy defaults, Save Names, invalid drafts, host startup arguments, eight-row layout. No hardware accessed.");
    }
}
