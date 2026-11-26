using System.Diagnostics;

namespace TapRoom.Services;

// A private, per-case control file lets mitmproxy stop its own event loop.
// The addon is embedded in the assembly, so single-file publication remains intact.
internal static class MitmShutdownControl
{
    internal static string ScriptPath(string logsDirectory) => Path.Combine(logsDirectory, "taproom-shutdown.py");
    internal static string ReadyPath(string script) => Path.ChangeExtension(script, ".ready");
    internal static string RequestPath(string script) => Path.ChangeExtension(script, ".request");
    internal static string AcceptedPath(string script) => Path.ChangeExtension(script, ".accepted");

    internal static string Prepare(string logsDirectory)
    {
        Directory.CreateDirectory(logsDirectory);
        var script = ScriptPath(logsDirectory);
        // Each capture gets a new case directory. Fail safely if stale control
        // files somehow exist instead of accidentally signalling a different engine.
        foreach (var path in new[] { script, ReadyPath(script), RequestPath(script), AcceptedPath(script) })
            if (File.Exists(path)) throw new IOException($"Shutdown control file already exists: {path}");
        using (var stream = new FileStream(script, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            writer.Write(AddonSource);
        return script;
    }

    internal static bool TryRequest(Process process, string script, TimeSpan timeout, out string detail)
    {
        try
        {
            if (!File.Exists(ReadyPath(script)))
            {
                detail = "Shutdown addon has not reported ready.";
                return false;
            }
            if (process.HasExited)
            {
                detail = "Engine had already exited before addon request.";
                return false;
            }
            File.WriteAllText(RequestPath(script), "stop");
            var exited = process.WaitForExit((int)timeout.TotalMilliseconds);
            var accepted = File.Exists(AcceptedPath(script));
            detail = $"Addon request sent; acknowledged={accepted}; process exited={exited}.";
            // Do not credit an unrelated exit to the addon without acknowledgement.
            return exited && accepted;
        }
        catch (Exception ex)
        {
            detail = $"Addon shutdown request failed: {ex.Message}";
            return false;
        }
    }

    // No external Python installation or extra Python packages are required.
    // mitmdump loads this addon in its own bundled Python interpreter.
    internal const string AddonSource = """
        import asyncio
        import logging
        from pathlib import Path
        from mitmproxy import ctx

        logger = logging.getLogger("taproom.shutdown")

        class TapRoomShutdown:
            def __init__(self):
                script = Path(__file__).resolve()
                self.ready = script.with_suffix(".ready")
                self.request = script.with_suffix(".request")
                self.accepted = script.with_suffix(".accepted")
                self.timer = None

            def running(self):
                self.loop = asyncio.get_running_loop()
                self.timer = self.loop.call_later(0.25, self.check_request)
                self.ready.touch()
                logger.info("TapRoom shutdown control ready")

            def check_request(self):
                self.timer = None
                if self.request.is_file():
                    try:
                        self.accepted.touch()
                    except OSError:
                        logger.exception("TapRoom could not acknowledge shutdown request")
                    else:
                        logger.info("TapRoom requested graceful shutdown")
                        ctx.master.shutdown()
                        return
                self.timer = self.loop.call_later(0.25, self.check_request)

            def done(self):
                if self.timer is not None:
                    self.timer.cancel()
                    self.timer = None

        addons = [TapRoomShutdown()]
        """;
}
