# TapRoom

TapRoom is a Windows desktop application for capturing and analysing web traffic using [mitmproxy](https://mitmproxy.org/). It is intended for security investigations, including the analysis of suspicious websites and phishing URLs.

## Features

- HTTP and HTTPS traffic capture with Microsoft Edge, Firefox and Firefox Portable.
- Isolated browser profiles and multiple concurrent captures.
- Automatic capture termination and IOC extraction when the browser closes.
- Extraction of domains, URLs, IP addresses, POST requests and file hashes.
- HTTP redirect analysis, request timelines and traffic statistics.
- Optional automatic browser screenshots based on visual changes.
- Configurable browser noise filtering and defanged IOC exports in CSV, TSV, JSON and TXT formats.
- Investigation management with optional case references, notes, search and archiving.
- Review of saved captures using mitmweb.

## Requirements

- Windows 10 (version 2004 or later) or Windows 11, x64.
- .NET 10 SDK to build from source.
- Microsoft Edge, Firefox or Firefox Portable.
- mitmproxy tools, which can be installed using TapRoom's **Setup / Verify** option.

## Build and run

From the project directory:

```powershell
dotnet build -c Release
dotnet run -c Release
```

To publish a self-contained Windows x64 executable:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## Usage

1. Open TapRoom and run **Setup / Verify** to configure mitmproxy and its CA certificate.
2. Enter a target URL and select a browser. A case reference is optional.
3. Select any required capture options, including screenshots.
4. Click **Start Capture** or press Enter in the URL field.
5. Browse the target website and stop the capture when finished.
6. Select the investigation to review IOCs, redirects, files, HTTP activity and screenshots, or open the capture in mitmweb.

Use **Settings** to configure storage, browser preferences, capture options and noise filters.

## Security

Use TapRoom in a dedicated investigation VM when analysing potentially malicious websites. Browser profiles are not security sandboxes. Captures and screenshots may contain sensitive information and should be stored and shared securely. Setup installs a trusted mitmproxy CA certificate to enable HTTPS interception.
