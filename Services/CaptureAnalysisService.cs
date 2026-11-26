using System.Text.Json;
using TapRoom.Models;

namespace TapRoom.Services;

// Offline analysis only. The embedded script is used solely for reading saved flows.
// No response body is extracted to an executable file and no request is replayed.
internal static class CaptureAnalysisService
{
    internal const string ResultFile = "analysis.json";
    internal const string ErrorFile = "analysis-error.txt";
    internal static string Prepare(string stage)
    {
        var script = Path.Combine(stage, "taproom-analysis.py");
        File.WriteAllText(script, AddonSource, new System.Text.UTF8Encoding(false));
        return script;
    }

    internal static CaptureAnalysis? Load(string directory)
    {
        var file = Path.Combine(directory, ResultFile);
        try
        {
            if (!File.Exists(file))
                return new CaptureAnalysis { Warning = File.Exists(Path.Combine(directory, ErrorFile))
                    ? File.ReadAllText(Path.Combine(directory, ErrorFile)) : "Advanced analysis is unavailable. Reparse the original capture." };
            using var stream = File.OpenRead(file);
            var result = JsonSerializer.Deserialize<CaptureAnalysis>(stream);
            if (result is null || result.SchemaVersion != 1 || result.Requests is null || result.Files is null || result.Redirects is null)
                throw new InvalidDataException("Unsupported or invalid advanced analysis file.");
            return result;
        }
        catch (Exception ex)
        {
            return new CaptureAnalysis { Warning = "Cannot load advanced analysis: " + ex.Message };
        }
    }

    internal const string AddonSource = """
        import hashlib
        import ipaddress
        import json
        import logging
        import bisect
        from datetime import datetime, timezone
        from email.message import Message
        from pathlib import Path
        from urllib.parse import urljoin, urlsplit, unquote, urldefrag
        from mitmproxy import ctx, http, io

        logger = logging.getLogger("taproom.analysis")
        ROOT = Path(__file__).resolve().parent
        FILE_EXTENSIONS = {'.exe','.dll','.msi','.scr','.bat','.cmd','.ps1','.js','.vbs','.jar','.apk','.iso','.img','.zip','.7z','.rar','.tar','.gz','.pdf','.doc','.docx','.docm','.xls','.xlsx','.xlsm','.ppt','.pptx','.rtf','.lnk','.dmg','.pkg','.deb','.rpm','.csv'}
        FILE_MIMES = {'application/octet-stream','application/zip','application/x-zip-compressed','application/x-7z-compressed','application/x-rar-compressed','application/vnd.rar','application/pdf','application/x-msdownload','application/x-dosexec','application/java-archive','application/x-msi','application/gzip','application/x-tar','application/vnd.android.package-archive','application/msword','application/vnd.ms-excel','application/vnd.ms-powerpoint','application/rtf','text/csv'}

        def utc(value):
            if value is None:
                return ''
            try:
                return datetime.fromtimestamp(value, timezone.utc).isoformat()
            except (ValueError, OverflowError, OSError):
                return ''

        def url_key(url):
            # Match exact observed URL (without fragment); never claim causal proof.
            return urldefrag(url)[0]

        def inspect_file(flow, row):
            response = flow.response
            if response is None or flow.request.method.upper() == 'HEAD':
                return None
            mime = row['ContentType'].split(';', 1)[0].strip().lower()
            disposition = response.headers.get('Content-Disposition', '')
            name = ''
            if disposition:
                try:
                    msg = Message()
                    msg['Content-Disposition'] = disposition
                    name = msg.get_filename() or ''
                except (ValueError, TypeError):
                    pass
            if not name:
                name = unquote(urlsplit(row['Url']).path.rsplit('/', 1)[-1])
            # Filename is display metadata, never a filesystem path.
            name = name.replace('\\', '/').rsplit('/', 1)[-1]
            name = ''.join(ch for ch in name if ch.isprintable())[:512]
            attachment = disposition.lower().split(';', 1)[0].strip() == 'attachment'
            extension = '.' + name.rsplit('.', 1)[-1].lower() if '.' in name else ''
            if not (attachment or mime in FILE_MIMES or extension in FILE_EXTENSIONS or mime.startswith('application/vnd.openxmlformats-officedocument.') or mime.startswith('application/vnd.ms-')):
                return None
            # A failed HTTP response to a .zip URL is not a successful download.
            if not 200 <= response.status_code < 300:
                return None
            result = {'Utc': row['Utc'], 'Url': row['Url'], 'Name': name or '(unnamed response)',
                      'ContentType': row['ContentType'], 'StatusCode': response.status_code,
                      'DecodedBytes': None, 'SHA256': '', 'SHA1': '', 'MD5': '', 'HashStatus': ''}
            if response.status_code == 206 or response.headers.get('Content-Range'):
                result['HashStatus'] = 'Partial/range response; file hash withheld'
                return result
            if flow.error or response.timestamp_end is None or response.raw_content is None:
                result['HashStatus'] = 'Missing or incomplete captured body; hash withheld'
                return result
            declared = response.headers.get('Content-Length')
            if declared is not None:
                try:
                    if int(declared) != len(response.raw_content):
                        result['HashStatus'] = 'Content-Length mismatch; hash withheld'
                        return result
                except ValueError:
                    result['HashStatus'] = 'Invalid Content-Length; hash withheld'
                    return result
            try:
                body = response.get_content(strict=True)
            except (ValueError, TypeError, OSError) as exc:
                result['HashStatus'] = 'Cannot decode captured body; hash withheld'
                return result
            if body is None or len(body) == 0:
                result['HashStatus'] = 'No retained non-empty body; hash withheld'
                return result
            result.update(DecodedBytes=len(body), SHA256=hashlib.sha256(body).hexdigest(),
                          SHA1=hashlib.sha1(body, usedforsecurity=False).hexdigest(),
                          MD5=hashlib.md5(body, usedforsecurity=False).hexdigest(),
                          HashStatus='Hash of retained decoded response body; not independent proof of file completeness')
            return result

        def analyse(path):
            requests, files, redirects = [], [], []
            with open(path, 'rb') as source:
                for flow in io.FlowReader(source).stream():
                    if not isinstance(flow, http.HTTPFlow):
                        continue
                    req, response = flow.request, flow.response
                    if req is None:
                        continue
                    url = req.pretty_url
                    # Keep HTTP(S) data only. Non-HTTP protocol flows are not part of this report.
                    try:
                        parts = urlsplit(url)
                        if parts.scheme.lower() not in ('http','https') or not parts.hostname:
                            continue
                    except ValueError:
                        continue
                    peer = flow.server_conn.peername
                    ip = ''
                    if peer:
                        try:
                            ip = str(ipaddress.ip_address(peer[0]))
                        except ValueError:
                            pass
                    status = response.status_code if response is not None else None
                    content_type = response.headers.get('Content-Type', '') if response is not None else ''
                    row = {'Utc': utc(req.timestamp_start), 'Method': req.method, 'Url': url,
                           'Domain': parts.hostname.lower(), 'StatusCode': status, 'PeerIp': ip,
                           'ContentType': content_type,
                           'ResponseBytes': len(response.raw_content) if response is not None and response.raw_content is not None else None,
                           'Error': flow.error.msg if flow.error else '', 'Event': 'Request'}
                    if flow.error:
                        row['Event'] = 'Request error'
                    elif response is None:
                        row['Event'] = 'No response recorded'
                    elif req.method.upper() == 'POST':
                        row['Event'] = 'POST request'
                    if response is not None and status in (301,302,303,307,308):
                        location = response.headers.get('Location', '')
                        if location:
                            try:
                                target = urljoin(url, location)
                                dest = urlsplit(target)
                                if dest.scheme.lower() in ('http','https') and dest.hostname:
                                    redirects.append({'Utc': row['Utc'], 'SourceUrl': url, 'DestinationUrl': target,
                                                      'StatusCode': status, 'ResponseUtc': utc(response.timestamp_end or response.timestamp_start),
                                                      'LaterRequestUtc': ''})
                                    row['Event'] = 'HTTP redirect'
                            except ValueError:
                                pass
                    file_row = inspect_file(flow, row)
                    if file_row is not None:
                        files.append(file_row)
                        row['Event'] = 'File candidate'
                    requests.append(row)
            requests.sort(key=lambda row: (not row['Utc'], row['Utc']))
            files.sort(key=lambda row: (not row['Utc'], row['Utc']))
            redirects.sort(key=lambda row: (not row['Utc'], row['Utc']))
            times_by_url = {}
            for row in requests:
                if row['Utc']:
                    times_by_url.setdefault(url_key(row['Url']), []).append(row['Utc'])
            for redirect in redirects:
                values = times_by_url.get(url_key(redirect['DestinationUrl']), [])
                after = redirect['ResponseUtc'] or redirect['Utc']
                if after:
                    index = bisect.bisect_left(values, after)
                    if index < len(values):
                        redirect['LaterRequestUtc'] = values[index]
            return {'SchemaVersion': 1, 'Warning': '', 'Requests': requests, 'Files': files, 'Redirects': redirects}

        class TapRoomAnalysis:
            def done(self):
                tmp = ROOT / 'analysis.json.tmp'
                try:
                    report = analyse(ctx.options.rfile)
                    with tmp.open('w', encoding='utf-8') as output:
                        json.dump(report, output, ensure_ascii=True, allow_nan=False)
                    tmp.replace(ROOT / 'analysis.json')
                except Exception as exc:
                    logger.exception('TapRoom advanced analysis failed')
                    (ROOT / 'analysis-error.txt').write_text('Advanced analysis failed: ' + str(exc), encoding='utf-8')
                finally:
                    if tmp.exists():
                        tmp.unlink()

        addons = [TapRoomAnalysis()]
        """;
}
