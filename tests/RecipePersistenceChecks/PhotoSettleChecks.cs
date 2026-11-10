using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ControlHub.Services.Persistence;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private static void CheckPhotoSettleParameters(HomePage home, string directory)
    {
        Require(JsonSerializer.Deserialize<HomePageSettings>("{}")!.ProductionPhotoSettleMilliseconds == 200,
            "Old settings and recipes must default to the original 200ms wait");
        var settingsStore = new HomePageSettingsStore(Path.Combine(directory, "photo-wait.json"));
        typeof(HomePage).GetField("_homeSettingsStore", PrivateInstance)!.SetValue(home, settingsStore);
        home.ApplyRecipeSettings(new HomePageSettings { SM7110LowerLimit = 1e9, SM7110MaximumTestSeconds = 1 });
        var input = (TextBox)home.FindName("ProductionPhotoSettleTextBox");
        Require(input.Text == "200", "Parameter must load the compatible default");
        foreach (var milliseconds in new[] { 0, 350, 60000 })
        {
            input.Text = milliseconds.ToString();
            Require(settingsStore.Load().ProductionPhotoSettleMilliseconds == milliseconds &&
                    home.CaptureRecipeSettings().ProductionPhotoSettleMilliseconds == milliseconds,
                "Valid integer waits must auto-save and enter the recipe snapshot");
        }
        foreach (var invalid in new[] { "", "-1", "60001", "1.5", "abc" })
        {
            input.Text = invalid;
            try { home.CaptureRecipeSettings(); throw new Exception("Invalid photo wait saved"); }
            catch (ArgumentException exception) when (exception.Message.Contains("震动后拍照等待")) { }
            Require(settingsStore.Load().ProductionPhotoSettleMilliseconds == 60000,
                "Invalid drafts must not overwrite the last valid wait");
        }
        var recipeStore = new ProductRecipeStore(Path.Combine(directory, "photo-wait-recipes"));
        foreach (var milliseconds in new[] { 80, 450 })
        {
            input.Text = milliseconds.ToString();
            var recipe = recipeStore.Create($"拍照等待{milliseconds}");
            recipe.Production = home.CaptureRecipeSettings();
            recipeStore.Save(recipe);
        }
        var recipes = recipeStore.LoadAll().OrderBy(recipe => recipe.Name).ToArray();
        foreach (var recipe in new[] { recipes[0], recipes[1], recipes[0] })
        {
            home.ApplyRecipeSettings(recipe.Production);
            Require(input.Text == recipe.Production.ProductionPhotoSettleMilliseconds.ToString(),
                "A/B/A recipe application must restore independent photo waits");
        }
        typeof(HomePage).GetField("_startSequenceRunning", PrivateInstance)!.SetValue(home, true);
        typeof(HomePage).GetMethod("UpdateHomeCommandState", PrivateInstance)!.Invoke(home, null);
        Require(!input.IsEnabled, "Photo wait must be locked for the current production run");
        typeof(HomePage).GetField("_startSequenceRunning", PrivateInstance)!.SetValue(home, false);
        typeof(HomePage).GetMethod("UpdateHomeCommandState", PrivateInstance)!.Invoke(home, null);
        Require(input.IsEnabled, "Photo wait becomes editable after production ends");

        var parameters = new ParameterSettingsPage();
        parameters.AttachSettingsContent(home.DetachParameterSettingsPanel());
        foreach (var width in new[] { 1680, 1280 })
        {
            parameters.Measure(new Size(width, 854));
            parameters.Arrange(new Rect(0, 0, width, 854));
            parameters.UpdateLayout();
            var point = input.TransformToAncestor(parameters).Transform(new Point());
            Require(input.ActualHeight >= 25 && point.X >= 0 && point.Y >= 0 &&
                    point.X + input.ActualWidth <= width && point.Y + input.ActualHeight < 854,
                "Photo wait must be visible and editable on the parameter page");
            var bitmap = new RenderTargetBitmap(width, 854, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(parameters);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var previewDir = Path.Combine(Environment.CurrentDirectory, ".codex-tmp", "photo-wait");
            Directory.CreateDirectory(previewDir);
            using var output = File.Create(Path.Combine(previewDir, $"parameters-{width}.png"));
            encoder.Save(output);
        }
        Console.WriteLine("PASS: photo wait defaults to 200ms, auto-save, 0/60000 bounds, invalid drafts rejected, A/B/A recipes, production lock and parameter layout. No hardware accessed.");
    }
}
