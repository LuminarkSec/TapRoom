using System.Diagnostics;
using TapRoom.Models;

namespace TapRoom.Services;

public sealed record BrowserLaunchResult(
    BrowserKind Browser,
    Process RootProcess,
    string ProfileDirectory,
    string ExecutablePath);
