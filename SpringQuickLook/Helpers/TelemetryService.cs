// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of SpringQuickLook program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SpringQuickLook.Controls;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;

namespace SpringQuickLook.Helpers;

/// <summary>
/// Usage telemetry: batches preview events and uploads them to Seq as CLEF (NDJSON).
/// Mirrors the Snow Shot telemetry integration spec: best-effort recording that never
/// blocks or breaks the caller, drop-on-failure uploads with exponential backoff,
/// and a single-flight uploader whose request rate is a property of the clock.
/// </summary>
internal sealed class TelemetryService
{
    // The internal Seq collector (Raw Events Ingestion API, CLEF NDJSON).
    // Deliberately hard-coded: the endpoint is provisioned per deployment and this
    // is the single sanctioned exception to the "never hard-code endpoints" rule.
    private const string Endpoint = "https://desktop.cqczy.cn/seq/api/events/raw?clef";

    private const string AppName = "SpringQuickLook";

    private const int FirstFlushDelayMs = 20 * 1000;   // let the startup peak settle first
    private const int FlushIntervalMs = 60 * 1000;     // regular batch window
    private const int TransferTimeoutMs = 10 * 1000;   // per-request transfer timeout
    private const int ShutdownTimeoutMs = 500;         // final delivery budget on exit (spec §1.4)
    private const int Capacity = 512;                  // buffer cap; the newest is dropped
    private const int MaxSamplesPerRequest = 100;

    private static TelemetryService _instance;

    public static TelemetryService GetInstance()
    {
        return _instance ??= new TelemetryService();
    }

    /// <summary>
    /// Samples queued for the next batch. Strings are materialized only at encode
    /// time; recording sites stay allocation-free beyond one tiny struct.
    /// </summary>
    private readonly ConcurrentQueue<TelemetrySample> _queue = new();

    // Reported as-is by requirement: the corporate deployment uses account names
    // (employee IDs) as the personnel dimension for aggregated statistics.
    private readonly string _userId = Environment.UserName;

    private Timer _timer;
    private bool _started;
    private int _inFlight;    // single-flight: at most one request in flight
    private int _failures;
    private long _nextAttemptTicks;

    private readonly struct TelemetrySample
    {
        public TelemetrySample(long epochMs, string eventName, string fileName)
        {
            EpochMs = epochMs;
            EventName = eventName;
            FileName = fileName;
        }

        public long EpochMs { get; }
        public string EventName { get; }
        public string FileName { get; }   // null for session events; the field is omitted in CLEF
    }

    public void Start()
    {
        if (_started)
            return;

        _started = true;

        Record("session.start", null);

        // Self-chaining schedule: first flush after 20 s, then every 60 s.
        _timer = new Timer(_ => Flush(), null, FirstFlushDelayMs, FlushIntervalMs);
    }

    /// <summary>Records a file preview event. Never throws, never blocks the caller.</summary>
    public void RecordPreview(string path)
    {
        Record("preview.opened", path);
    }

    public void Stop()
    {
        if (!_started)
            return;

        // Shutdown sequence per spec §7.2: stop the timer, record the shutdown
        // event, then deliver the remaining buffer within the budget.
        _started = false;

        _timer?.Dispose();
        _timer = null;

        Record("session.clean_shutdown", null);

        try
        {
            var body = BuildBody(out var count);

            if (count > 0)
                TryPost(body, ShutdownTimeoutMs);
        }
        catch
        {
            // Ignore
        }
    }

    private void Record(string eventName, string path)
    {
        try
        {
            // No gate on _started here: samples recorded before Start() are queued
            // and delivered by the first flush, so startup-period events are
            // counted too (spec §6.0, "probe installed at construction").
            var sample = new TelemetrySample(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                eventName,
                path == null ? null : Path.GetFileName(path));

            // Buffer cap: drop the newest so every sent batch stays time-ordered
            // and internally consistent (spec §7.1).
            if (_queue.Count >= Capacity)
                return;

            _queue.Enqueue(sample);
        }
        catch
        {
            // Telemetry must never propagate into the caller's path
        }
    }

    private void Flush()
    {
        // single-flight: the request frequency is a property of the clock, not of
        // user behavior; a busy window's samples are left for the next batch.
        if (Interlocked.Exchange(ref _inFlight, 1) == 1)
            return;

        try
        {
            // Backoff window: the timer keeps ticking but nothing is sent while the
            // buffer keeps accumulating (spec §3.4).
            if (DateTime.UtcNow.Ticks < Volatile.Read(ref _nextAttemptTicks))
                return;

            var body = BuildBody(out var count);

            if (count == 0)
                return;

            if (TryPost(body, TransferTimeoutMs))
                _failures = 0;   // success resets the backoff ladder
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    private string BuildBody(out int count)
    {
        // "Date" is the local-timezone day (daily grouping key) while "@t" is UTC;
        // the two may fall on different days. Do not derive one from the other.
        var localDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);

        var sb = new StringBuilder();
        count = 0;

        while (count < MaxSamplesPerRequest && _queue.TryDequeue(out var sample))
        {
            var obj = new JObject
            {
                // UTC ISO8601 with millisecond precision, derived at encode time
                ["@t"] = DateTimeOffset.FromUnixTimeMilliseconds(sample.EpochMs)
                                     .UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                ["@mt"] = $"{AppName} usage {{EventName}}",
                ["@l"] = "Information",
                ["AppName"] = AppName,
                ["UserId"] = _userId,
                ["Date"] = localDate,
                ["EventName"] = sample.EventName,
                ["Count"] = 1,
            };

            if (!string.IsNullOrEmpty(appVersion))
                obj["AppVersion"] = appVersion;

            // Empty values omit the field itself, never "" or null (spec §3.1)
            if (sample.FileName != null)
                obj["FileName"] = sample.FileName;

            // NDJSON: one CLEF object per line joined by "\n", no trailing newline
            if (count > 0)
                sb.Append('\n');

            sb.Append(JsonConvert.SerializeObject(obj, Formatting.None));

            count++;
        }

        return sb.ToString();
    }

    private bool TryPost(string body, int timeoutMs)
    {
        try
        {
            var web = new WebClientEx(timeoutMs)
            {
                Proxy = WebRequest.DefaultWebProxy,
                Credentials = CredentialCache.DefaultCredentials
            };
            web.Headers.Add(HttpRequestHeader.ContentType, "application/vnd.serilog.clef");
            web.Headers.Add(HttpRequestHeader.Accept, "application/json");

            web.UploadData(Endpoint, "POST", Encoding.UTF8.GetBytes(body));

            return true;
        }
        catch
        {
            // The batch was already dequeued: dropped, never replayed. Undercounting
            // only flattens usage trends, while replaying an ambiguous failure would
            // double-count (spec §3.4). Back off exponentially instead.
            var delayMs = Math.Min(60_000d * Math.Pow(2, Math.Min(_failures, 5)), 1_800_000d);
            Volatile.Write(ref _nextAttemptTicks,
                DateTime.UtcNow.Ticks + (long)(delayMs * TimeSpan.TicksPerMillisecond));
            _failures++;

            return false;
        }
    }
}
