using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ControlHub.Services.Persistence;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static void Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "recipe-persistence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _ = new Application();
        // Construct the view without loading a window or attaching any hardware.
        var home = new HomePage();
        var settingsStore = new HomePageSettingsStore(Path.Combine(directory, "home.json"));
        typeof(HomePage).GetField("_homeSettingsStore", PrivateInstance)!.SetValue(home, settingsStore);
        var defaults = new HomePageSettings
        {
            SM7110LowerLimit = 1e9,
            SM7110MaximumTestSeconds = 1
        };
        home.ApplyRecipeSettings(defaults);
        var input = (TextBox)home.FindName("VisionPickupCountTextBox");
        Require(input.Text == "20", "Legacy recipes use the default pickup count.");
        var store = new ProductRecipeStore(Path.Combine(directory, "recipes"));
        var first = store.Create("配方A");
        first.Production = home.CaptureRecipeSettings();
        store.Save(first);
        var second = store.Create("配方B");
        second.Production = ProductRecipeStore.Clone(first.Production);
        second.Production.VisionPickupCount = 40;
        store.Save(second);

        foreach (var value in new[] { 2, 10, 30, 100 })
        {
            input.Text = value.ToString(CultureInfo.CurrentCulture);
            first.Production = home.CaptureRecipeSettings();
            store.Save(first);
            home.ApplyRecipeSettings(store.LoadAll().Single(recipe => recipe.Id == second.Id).Production);
            Require(input.Text == "40", "Switching to B restores its own count.");
            // Reload through a new store to verify disk persistence, not a cached recipe object.
            var saved = new ProductRecipeStore(Path.Combine(directory, "recipes")).LoadAll()
                .Single(recipe => recipe.Id == first.Id);
            home.ApplyRecipeSettings(saved.Production);
            Require(input.Text == value.ToString(CultureInfo.CurrentCulture), "Switching back restores the saved count.");
            Require(settingsStore.Load().VisionPickupCount == value, "Applied count persists in current settings.");
        }
        Console.WriteLine("PASS: create, edit, save, switch A/B/A and reload from disk, including 2 and 100.");

        foreach (var invalid in new[] { "3", "21", "", "0", "102", "-2", "2.5", "abc", "999999999999" })
        {
            input.Text = invalid;
            var rejected = false;
            try { _ = home.CaptureRecipeSettings(); }
            catch (ArgumentException exception)
            {
                rejected = exception.Message.Contains("抓取颗数") && exception.Message.Contains("偶数");
            }
            Require(rejected, $"Invalid input '{invalid}' must fail explicitly instead of saving the old count.");
            Require(settingsStore.Load().VisionPickupCount == 100, "Invalid input preserves the last valid count.");
        }
        Console.WriteLine("PASS: odd, empty, out-of-range, fractional, nonnumeric and overflowing inputs cannot silently save.");

        // Exercise both UI operations: errors must reach the recipe status, without touching the recipe file.
        var parameters = new ParameterSettingsPage();
        typeof(ParameterSettingsPage).GetField("_homePage", PrivateInstance)!.SetValue(parameters, home);
        typeof(ParameterSettingsPage).GetField("_recipeStore", PrivateInstance)!.SetValue(parameters, store);
        var list = (ListBox)parameters.FindName("RecipeListBox");
        list.ItemsSource = store.LoadAll();
        list.SelectedItem = list.Items.Cast<ProductRecipe>().Single(recipe => recipe.Id == first.Id);
        var before = store.Serialize(store.LoadAll().Single(recipe => recipe.Id == first.Id));
        input.Text = "21";
        foreach (var handler in new[] { "SaveCurrentRecipe_Click", "CreateRecipe_Click" })
        {
            typeof(ParameterSettingsPage).GetMethod(handler, PrivateInstance)!
                .Invoke(parameters, [parameters, new RoutedEventArgs()]);
            var status = ((TextBlock)parameters.FindName("RecipeStatusText")).Text;
            Require(status.Contains("抓取颗数") && status.Contains("偶数"), "The recipe UI must explain the rejected input.");
            Require(store.LoadAll().Count == 2 && store.Serialize(store.LoadAll().Single(recipe => recipe.Id == first.Id)) == before,
                "Rejected saves must not create or overwrite recipes.");
        }
        Console.WriteLine("PASS: New and Save Current report invalid pickup count and preserve recipe files. No hardware opened.");
        CheckAllParameterControls(home, defaults, store);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
