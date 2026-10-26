namespace AdoLens.Tests;

public class SettingsWindowTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"adolens-window-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void The_settings_window_builds_with_every_tab()
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "adolens.json");
        var s = new Settings { DefaultConnection = "contoso", Mode = "dev" };
        s.Connections["contoso"] = new Connection { Organization = "contoso", Project = "Demo", CodeProject = "ERP" };
        s.Save(path);

        Exception? failed = null;
        var tabs = new List<string>();
        var thread = new Thread(() =>
        {
            try
            {
                using var window = new SettingsWindow(path, "0.0.0");
                window.CreateControl();
                var shots = Environment.GetEnvironmentVariable("ADOLENS_SHOTS");
                if (shots is { Length: > 0 })
                {
                    window.StartPosition = FormStartPosition.Manual;
                    window.Location = new Point(-3000, -3000);
                    window.Show();
                }
                var control = window.Controls.OfType<TabControl>().Single();
                foreach (TabPage page in control.TabPages)
                {
                    tabs.Add(page.Text);
                    // ADOLENS_SHOTS=<folder>: a picture of each tab, to look at the layout.
                    if (shots is { Length: > 0 })
                    {
                        control.SelectedTab = page;
                        Application.DoEvents();
                        using var bitmap = new Bitmap(window.Width, window.Height);
                        window.DrawToBitmap(bitmap, new Rectangle(0, 0, window.Width, window.Height));
                        bitmap.Save(Path.Combine(shots, $"tab{tabs.Count}.png"));
                    }
                }
            }
            catch (Exception ex) { failed = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failed);
        Assert.Equal(4, tabs.Count);
    }

    [Fact]
    public void Environment_rows_become_the_map_in_their_order()
    {
        var map = SettingsWindow.MapOf([("ERP", "dev", "DEV (sbx01)"), ("", "Deploy/Stage 1", "UAT; SCM"), ("ERP", "staging", ""), (null, "master", "PROD")]);
        Assert.Equal(["ERP:dev", "Deploy/Stage 1", "master"], map.Keys);
        Assert.Equal("UAT, SCM", map["Deploy/Stage 1"]);
    }
}
