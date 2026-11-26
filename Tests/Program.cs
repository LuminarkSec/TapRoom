using System.IO;
using TapRoom.Models;
using TapRoom.Services;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "TapRoom-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TestIocs(root);
            TestProfileMatching();
            TestStates();
            TestShutdownControl(root);
            TestSettings(root);
            TestArchive(root);
            TestCaseDetails(root);
            TestCommandLine();
            TestDomainAndExports();
            TestAnalysis(root);
            TestSmartScreenNoise();
            TestEdgeDeliveryNoise();
            TestScreenshots(root);
            TestEdgePreferences(root);
            Console.WriteLine("PASS: Phase 1, Phase 2, Phase 3 and screenshot regression checks.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static void TestEdgePreferences(string root)
    {
        var defaults = new AppSettings();
        Require(defaults.EdgeHideCopilot && defaults.EdgeHideSidebar && defaults.EdgeSuppressFirstRun &&
            defaults.EdgeDisableStartupBoost && defaults.EdgeDisableBackgroundMode, "Edge defaults not enabled.");
        var launcher = new BrowserLauncher();
        var profile = Path.Combine(root, "edge profile");
        Directory.CreateDirectory(profile);
        var url = "https://example.com/path?a=1&b=2";
        var args = launcher.BuildEdgeArguments(url, 9000, profile);
        Require(args.Last() == url && args.Contains("--user-data-dir=" + profile) &&
            args.Contains("--proxy-server=http=127.0.0.1:9000;https=127.0.0.1:9000"), "Edge isolation/proxy arguments changed.");
        var features = args.Single(a => a.StartsWith("--disable-features=", StringComparison.Ordinal));
        Require(features.Contains("msEdgeStartupBoost") && features.Contains("msUndersideButton") && features.Contains("msEdgeSidebarV2"), "Edge feature flags missing.");
        launcher.SeedEdgePreferences(profile);
        var pref = Path.Combine(profile, "Default", "Preferences");
        using (var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(pref)))
            Require(!document.RootElement.GetProperty("browser").GetProperty("show_hub_apps_tower").GetBoolean(), "Edge sidebar preference not seeded.");
        File.WriteAllText(pref, "{\"existing\":true}");
        launcher.SeedEdgePreferences(profile);
        Require(File.ReadAllText(pref) == "{\"existing\":true}", "Existing Edge profile overwritten.");
        launcher.EdgeHideCopilot = launcher.EdgeHideSidebar = launcher.EdgeSuppressFirstRun = false;
        launcher.EdgeDisableStartupBoost = launcher.EdgeDisableBackgroundMode = false;
        args = launcher.BuildEdgeArguments(url, 9000, profile);
        Require(!args.Any(a => a.StartsWith("--disable-features=", StringComparison.Ordinal)) &&
            !args.Contains("--no-first-run") && !args.Contains("--disable-background-mode"), "Disabled options still passed to Edge.");
        var untouched = Path.Combine(root, "edge-unmodified");
        Directory.CreateDirectory(untouched);
        launcher.SeedEdgePreferences(untouched);
        Require(!Directory.EnumerateFileSystemEntries(untouched).Any(), "Opt-out created a preference file.");
        var settingsPath = Path.Combine(root, "edge-settings.json");
        var settings = defaults with { EdgeHideCopilot = false, EdgeHideSidebar = false, EdgeSuppressFirstRun = false,
            EdgeDisableStartupBoost = false, EdgeDisableBackgroundMode = false };
        SettingsStore.Save(settingsPath, settings);
        Require(SettingsStore.Load(settingsPath, out var warning) == settings && warning is null, "Edge settings failed to round-trip.");
    }

    private static void TestScreenshots(string root)
    {
        // RGB tolerance, fractional change, alpha exclusion, and cumulative
        // changes measured against the original saved baseline.
        byte[] original = new byte[12];
        byte[] small = new byte[12]; small[0] = ScreenshotDifference.Tolerance;
        Require(ScreenshotDifference.Percent(original, small) == 0, "Small colour change counted.");
        byte[] changed = (byte[])small.Clone(); changed[0]++;
        Require(ScreenshotDifference.Percent(original, changed) == 25, "Changed-pixel percentage is incorrect.");
        Require(ScreenshotDifference.Percent(small, changed) == 0, "Tolerance must apply to a baseline.");
        byte[] a = { 30, 40, 50, 0 }, b = { 30, 40, 50, 255 };
        Require(ScreenshotDifference.Sample(a, 1, 1, 4).SequenceEqual(ScreenshotDifference.Sample(b, 1, 1, 4)), "Alpha affected comparison.");
        var settings = new AppSettings();
        Require(!settings.ScreenshotEnabled && settings.ScreenshotIntervalSeconds == 2 && settings.ScreenshotChangePercent == 3 && settings.ScreenshotMaximum == 500, "Screenshot defaults changed.");
        bool invalid = false;
        try { (settings with { ScreenshotChangePercent = double.NaN }).Validate(); } catch (ArgumentException) { invalid = true; }
        Require(invalid, "Invalid screenshot threshold accepted.");
        invalid = false;
        try { (settings with { ScreenshotIntervalSeconds = 0 }).Validate(); } catch (ArgumentException) { invalid = true; }
        Require(invalid, "Invalid screenshot interval accepted.");
        var folder = Path.Combine(root, "screenshot-test");
        var records = ScreenshotStore.Load(folder);
        var image = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96,
            System.Windows.Media.PixelFormats.Bgr32, null, a, 4);
        image.Freeze();
        var record = new ScreenshotRecord("0001.png", DateTime.UtcNow, 0.5, null, 1, 1, "Synthetic window", "Initial");
        ScreenshotStore.Save(folder, records, record, image, System.Threading.CancellationToken.None);
        var reloaded = ScreenshotStore.Load(folder);
        Require(reloaded.Count == 1 && reloaded[0] == record, "Screenshot metadata did not round-trip.");
        Require(ScreenshotStore.ReadImage(folder, record.FileName).PixelWidth == 1, "PNG did not round-trip.");
        var next = record with { FileName = "0002.png", ChangePercent = 25, Reason = "Visual change" };
        ScreenshotStore.Save(folder, records, next, image, System.Threading.CancellationToken.None);
        Require(ScreenshotStore.Load(folder).Count == 2, "Atomic index replacement failed.");
        Require(!File.ReadAllText(Path.Combine(folder, "index.json")).Contains("hash", StringComparison.OrdinalIgnoreCase), "Screenshot hashing is not required.");
        Require(!ScreenshotStore.ValidName("../other.png") && !ScreenshotStore.ValidName(@"..\other.png"), "Unsafe screenshot filename accepted.");
        using var cancel = new System.Threading.CancellationTokenSource(); cancel.Cancel();
        try { ScreenshotStore.Save(folder, records, record with { FileName = "0003.png" }, image, cancel.Token); }
        catch (OperationCanceledException) { }
        Require(!File.Exists(Path.Combine(folder, "0003.png")) && ScreenshotStore.Load(folder).Count == 2, "Cancelled screenshot changed the index.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void TestIocs(string root)
    {
        var dir = Path.Combine(root, "parsed");
        Directory.CreateDirectory(dir);
        var har = Path.Combine(dir, "flows.har.json");
        File.WriteAllText(har, """
            {"log":{"entries":[
            {"request":{"method":"POST","url":"https://EXAMPLE.com/Payload?Key=A"}},
            {"request":{"method":"POST","url":"https://EXAMPLE.com/payload?Key=A"}},
            {"request":{"method":"POST","url":"https://EXAMPLE.com/payload?Key=a"}},
            {"request":{"method":"POST","url":"https://EXAMPLE.com/payload?Key=a"}},
            {"request":{"method":"GET","url":"https://example.com/"}}
            ]}}
            """);
        var parsed = CaptureManager.ParseHarFile(har, dir, Path.Combine(dir, "domains.txt"),
            Path.Combine(dir, "urls.txt"), Path.Combine(dir, "post-urls.txt"), Path.Combine(dir, "post-domains.txt"));
        Require(parsed.Domains.Count == 1 && parsed.PostDomains.Count == 1, "Domain deduplication failed.");
        Require(parsed.Urls.Count == 4 && parsed.PostUrls.Count == 3, "Case-sensitive URL extraction failed.");
        var reloaded = new CaptureManager().LoadParsedIocs(new CaptureSession { CaseDirectory = root });
        Require(reloaded is not null && reloaded.Urls.SequenceEqual(parsed.Urls) && reloaded.PostUrls.SequenceEqual(parsed.PostUrls),
            "Reload changed case-sensitive URLs.");
    }

    private static void TestProfileMatching()
    {
        var path = @"C:\TapRoom Profiles\case-1";
        Require(BrowserProfileMonitor.MatchesProfile(BrowserKind.Edge, new[] { "msedge.exe", "--user-data-dir=" + path }, path), "Edge exact profile not matched.");
        Require(!BrowserProfileMonitor.MatchesProfile(BrowserKind.Edge, new[] { "msedge.exe", "--user-data-dir=" + path + "0" }, path), "Prefix collision matched.");
        Require(!BrowserProfileMonitor.MatchesProfile(BrowserKind.Edge, new[] { "msedge.exe", "--type=renderer", "--user-data-dir=" + path }, path), "Renderer matched.");
        Require(BrowserProfileMonitor.MatchesProfile(BrowserKind.Firefox, new[] { "firefox.exe", "-profile", path }, path), "Firefox profile not matched.");
        Require(!BrowserProfileMonitor.MatchesProfile(BrowserKind.Firefox, new[] { "firefox.exe", "-contentproc", "-profile", path }, path), "Firefox helper matched.");
    }

    private static void TestShutdownControl(string root)
    {
        var a = MitmShutdownControl.Prepare(Path.Combine(root, "shutdown-a"));
        var b = MitmShutdownControl.Prepare(Path.Combine(root, "shutdown-b"));
        Require(File.ReadAllText(a) == MitmShutdownControl.AddonSource, "Embedded addon not written correctly.");
        Require(!File.Exists(MitmShutdownControl.ReadyPath(a)), "Ready flag must come from mitmdump.");
        File.WriteAllText(MitmShutdownControl.RequestPath(a), "stop");
        Require(!File.Exists(MitmShutdownControl.RequestPath(b)), "Shutdown request escaped its case directory.");
        var rejected = false;
        try { MitmShutdownControl.Prepare(Path.GetDirectoryName(a)!); }
        catch (IOException) { rejected = true; }
        Require(rejected, "Stale shutdown files must not be reused.");
    }

    private static void TestStates()
    {
        foreach (var state in Enum.GetValues<CaptureStatus>())
        {
            var session = new CaptureSession { Status = state };
            Require(session.CanAnalyse == (state is CaptureStatus.Stopped or CaptureStatus.ForceStopped or CaptureStatus.Failed), $"Analysis guard failed: {state}");
            Require(session.NeedsStop == (state is CaptureStatus.Starting or CaptureStatus.Running or CaptureStatus.Stopping or CaptureStatus.StopFailed), $"Stop guard failed: {state}");
        }
    }

    private static void TestSettings(string root)
    {
        var path = Path.Combine(root, "config", "settings.json");
        var settings = new AppSettings { StorageRoot = root, DefaultBrowser = BrowserKind.Firefox,
            AutoLaunchBrowser = false, StopOnBrowserClose = false, AutoParseAfterStop = true, FirstProxyPort = 9010, LastProxyPort = 9020 };
        SettingsStore.Save(path, settings);
        var restored = SettingsStore.Load(path, out var warning);
        Require(warning is null && restored == settings, "Settings did not round-trip.");
        SettingsStore.Save(path, settings with { AutoParseAfterStop = false });
        Require(File.Exists(path + ".bak"), "Previous settings backup not created.");
        var rejected = false;
        try { (settings with { LastProxyPort = 12000 }).Validate(); } catch (ArgumentException) { rejected = true; }
        Require(rejected, "Proxy range overlaps mitmweb range.");
        rejected = false;
        try { (settings with { FirstProxyPort = 9500, LastProxyPort = 9000 }).Validate(); } catch (ArgumentException) { rejected = true; }
        Require(rejected, "Reversed proxy range accepted.");
        File.WriteAllText(path, "not json");
        Require(SettingsStore.Load(path, out warning).DefaultBrowser == BrowserKind.Edge && warning is not null, "Corrupt settings not reported.");
        Require(File.ReadAllText(path) == "not json", "Loading corrupt settings must not overwrite them.");
    }

    private static void TestArchive(string root)
    {
        var storage = Path.Combine(root, "storage");
        var dir = Path.Combine(storage, "Cases", "Case1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "metadata.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "capture.mitm"), "test-capture");
        var destination = CaseArchive.Move(storage, dir);
        Require(!Directory.Exists(dir) && File.ReadAllText(Path.Combine(destination, "capture.mitm")) == "test-capture", "Case archive did not preserve capture.");
        var refused = false;
        try { CaseArchive.Move(storage, destination); } catch (IOException) { refused = true; }
        Require(refused && Directory.Exists(destination), "Archive must reject paths outside Cases.");
        var manager = new CaptureManager();
        Require(!manager.CanArchive(new CaptureSession { Status = CaptureStatus.Running }), "Active capture can be archived.");
        Require(!manager.CanArchive(new CaptureSession { Status = CaptureStatus.Interrupted }), "Interrupted capture can be archived.");
    }

    private static void TestCaseDetails(string root)
    {
        var dir = Path.Combine(root, "details");
        Directory.CreateDirectory(dir);
        var metadata = Path.Combine(dir, "metadata.json");
        var session = new CaptureSession { CaseName = "unchanged-folder", CaseDirectory = dir, MetadataFile = metadata,
            Status = CaptureStatus.Stopped, AutoParseAfterStop = false };
        new CaptureManager().UpdateCaseDetails(session, " INC-123 ", " Investigation notes ");
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(metadata));
        Require(json.RootElement.GetProperty("CaseReference").GetString() == "INC-123", "Case reference was not persisted.");
        Require(json.RootElement.GetProperty("Notes").GetString() == "Investigation notes", "Notes were not persisted.");
        Require(!json.RootElement.GetProperty("AutoParseAfterStop").GetBoolean() && session.CaseName == "unchanged-folder", "Editing details changed capture identity or settings.");
    }

    private static void TestCommandLine()
    {
        var options = TapRoom.StartupOptions.Parse(new[] { "--url", "https://example.com", "--browser", "firefox", "--no-auto-stop", "--auto-parse" });
        Require(options.Browser == BrowserKind.Firefox && !options.StopAndParseOnBrowserClose && options.AutoParseAfterStop, "Independent CLI defaults not applied.");
        var disabled = TapRoom.StartupOptions.Parse(new[] { "--auto-stop", "--no-auto-parse" });
        Require(disabled.StopAndParseOnBrowserClose && !disabled.AutoParseAfterStop, "Disabling parse also disabled stopping.");
    }

    private static void TestDomainAndExports()
    {
        var session = new CaptureSession { Url = "https://Login.Example.com:8443/path?secret=not-the-host" };
        Require(session.Domain == "login.example.com", "Domain must retain the subdomain but exclude path and port.");
        Require(session.CaseReference == "" && session.DisplayName == session.Domain, "An omitted reference must remain empty.");
        session.CaseReference = "INC-42";
        Require(session.DisplayName == "login.example.com · INC-42", "Domain must be displayed alongside the reference.");
        Require(IocExportService.Defang("URL", "https://Example.com/Path.EXE?a=X.Y") == "hxxps://Example[.]com/Path.EXE?a=X.Y", "Defanging changed the URL path/query.");
        Require(IocExportService.Defang("IP address", "192.0.2.10") == "192[.]0[.]2[.]10", "IPv4 defanging failed.");
        var rows = new[] { new IocEntry { Type = "URL", Value = "https://example.com/a" }, new IocEntry { Type = "Domain", Value = "=1+1" } };
        var csv = IocExportService.Build(rows, "csv", false, false);
        Require(csv.Contains("'=1+1", StringComparison.Ordinal), "Spreadsheet formula guard failed.");
        var json = IocExportService.Build(rows, "json", false, true);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Require(doc.RootElement[0].GetProperty("Value").GetString() == "hxxps://example[.]com/a", "Defanged JSON export failed.");
    }

    private static void TestAnalysis(string root)
    {
        var dir = Path.Combine(root, "advanced");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, CaptureAnalysisService.ResultFile), """
            {"SchemaVersion":1,"Requests":[{"PeerIp":"192.0.2.1"},{"PeerIp":"192.0.2.1"}],
             "Files":[{"SHA256":"test-hash","Name":"invoice.zip","Url":"https://example.com/invoice.zip"},
             {"SHA256":"test-hash","Name":"other.zip","Url":"https://other.example/other.zip"},
             {"SHA256":"test-hash","Name":"invoice.zip","Url":"https://example.com/invoice.zip"},{}],
             "Redirects":[{"DestinationUrl":"https://example.com/Path"},{"DestinationUrl":"https://example.com/path"}]}
            """);
        var report = CaptureAnalysisService.Load(dir);
        Require(report?.Available == true, "Advanced analysis failed to load.");
        var entries = new ParsedIocResult { Analysis = report }.ToEntries();
        Require(entries.Count(e => e.Type == "IP address") == 1, "IP deduplication failed.");
        Require(entries.Count(e => e.Type == "SHA-256") == 1, "Hash deduplication failed.");
        var hash = entries.Single(e => e.Type == "SHA-256");
        Require(hash.HashSources.Count == 2 && hash.SourceSummary.Contains("(+1 more)"), "Hash source attribution or deduplication failed.");
        Require(hash.SourceContext.Contains("invoice.zip") && hash.SourceContext.Contains("https://other.example/other.zip"), "Hash context lost a source.");
        var export = IocExportService.Build(new[] { hash }, "json", false, true);
        using var context = System.Text.Json.JsonDocument.Parse(export);
        Require(context.RootElement[0].GetProperty("Value").GetString() == "test-hash" &&
            context.RootElement[0].GetProperty("SourceContext").GetString()!.Contains("hxxps://other[.]example/other.zip"), "Defanged hash context export failed.");
        foreach (var format in new[] { "csv", "tsv", "txt" })
        {
            var text = IocExportService.Build(new[] { hash }, format, true, true);
            Require(text.Contains("hxxps://example[.]com/invoice.zip") && text.Contains("hxxps://other[.]example/other.zip"), $"Missing hash sources in {format} export.");
        }
        Require(entries.Count(e => e.Type == "Redirect URL") == 2, "Redirect URL case was not preserved.");
        File.WriteAllText(Path.Combine(dir, CaptureAnalysisService.ResultFile), "broken JSON");
        Require(CaptureAnalysisService.Load(dir)?.Available == false, "Corrupt analysis must be reported, not treated as empty traffic.");
    }

    private static void TestEdgeDeliveryNoise()
    {
        const string host = "msedge.b.tlu.dl.delivery.mp.microsoft.com";
        foreach (var type in new[] { "Domain", "POST Domain" })
        {
            Require(IocNoiseFilter.IsBrowserNoise(type, host, out var reason) && reason == "Known browser/vendor background domain", $"{type}: Edge delivery host not recognised.");
            Require(IocNoiseFilter.IsBrowserNoise(type, " " + host.ToUpperInvariant() + ". ", out reason) && reason == "Known browser/vendor background domain", $"{type}: normalised Edge delivery host not recognised.");
            IocNoiseFilter.IsBrowserNoise(type, host + ".example.invalid", out reason);
            Require(reason != "Known browser/vendor background domain", $"{type}: Edge delivery host rule matched a lookalike.");
        }
        foreach (var type in new[] { "URL", "POST URL", "Redirect URL" })
        {
            foreach (var url in new[] {
                "http://" + host + "/filestreamingservice/files/4c1d5cd5-b559-405a-9f2f-550437e50d95?P1=1795795314&P2=404&P3=2&P4=test%2f",
                "http://" + host + "/filestreamingservice/files/682f0409-daf0-4b68-9e13-25396fbb48a7?P1=1795735952&P2=404&P3=2&P4=test%3d",
                "https://" + host + "/different/path?anything=1",
                "https://" + host.ToUpperInvariant() + "/" })
                Require(IocNoiseFilter.IsBrowserNoise(type, url, out var reason) && reason == "Known browser/vendor background URL", $"{type}: Edge delivery URL not recognised.");
            foreach (var url in new[] { "https://" + host + ".example.invalid/filestreamingservice/files/test", "https://example.invalid/?host=" + host, "https://other.tlu.dl.delivery.mp.microsoft.com/filestreamingservice/files/test" })
            {
                IocNoiseFilter.IsBrowserNoise(type, url, out var reason);
                Require(reason != "Known browser/vendor background URL", $"{type}: Edge delivery rule matched an unrelated hostname.");
            }
        }
    }

    private static void TestSmartScreenNoise()
    {
        const string navigation = "https://nav-edge.smartscreen.microsoft.com/api/browser/edge/navigate/3";
        const string telemetry = "https://telem-edge.smartscreen.microsoft.com/api/browser/edge/telemetry/3";
        foreach (var type in new[] { "URL", "POST URL", "Redirect URL" })
        {
            Require(IocNoiseFilter.IsBrowserNoise(type, navigation, out var reason) && reason.Contains("SmartScreen"), "Edge navigation noise not recognised.");
            Require(IocNoiseFilter.IsBrowserNoise(type, telemetry + "?test=1", out reason) && reason.Contains("SmartScreen"), "Edge telemetry noise not recognised.");
        }
        foreach (var type in new[] { "Domain", "POST Domain" })
        {
            foreach (var host in new[] { "nav-edge.smartscreen.microsoft.com", "telem-edge.smartscreen.microsoft.com" })
            {
                Require(IocNoiseFilter.IsBrowserNoise(type, host, out var reason) && reason.Contains("SmartScreen"), $"{type}: SmartScreen host not recognised.");
                Require(IocNoiseFilter.IsBrowserNoise(type, " " + host.ToUpperInvariant() + ". ", out reason) && reason.Contains("SmartScreen"), $"{type}: normalised SmartScreen host not recognised.");
            }
            foreach (var host in new[] { "smartscreen.microsoft.com", "other.smartscreen.microsoft.com", "nav-edge.smartscreen.microsoft.com.example.invalid", "sub.telem-edge.smartscreen.microsoft.com" })
            {
                IocNoiseFilter.IsBrowserNoise(type, host, out var reason);
                Require(!reason.Contains("SmartScreen"), $"{type}: exact SmartScreen rule matched an unrelated domain.");
            }
        }
        // Existing user-defined rules may intentionally classify these as noise;
        // these checks specifically ensure the new built-in rule is not overbroad.
        foreach (var url in new[] { "https://example.invalid/api/browser/edge/navigate/3", navigation + "0", "https://nav-edge.smartscreen.microsoft.com/api/browser/edge/telemetry/3" })
        {
            IocNoiseFilter.IsBrowserNoise("URL", url, out var reason);
            Require(!reason.Contains("SmartScreen"), "SmartScreen rule matched an unrelated endpoint.");
        }
    }

}
